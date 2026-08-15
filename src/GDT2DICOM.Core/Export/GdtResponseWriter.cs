using Gdt2Dicom.Core.Configuration;
using Gdt2Dicom.Core.Gdt;
using Gdt2Dicom.Core.Pipeline;
using Gdt2Dicom.Core.Runtime;
using Gdt2Dicom.Core.Worklist;
using Microsoft.Extensions.Logging;

namespace Gdt2Dicom.Core.Export;

/// <summary>Ein Dateiverweis, der dem PVS mitgeteilt wird.</summary>
public sealed record GdtAttachment(string Path, string Format, string Description);

/// <summary>
/// Ergebnis des Schreibens: entweder bereits im Ausgang oder noch im Rückstau.
/// </summary>
public sealed record GdtResponseOutcome(string TargetFileName, string? DeliveredPath, string? HoldBackReason)
{
    public bool Delivered => DeliveredPath is not null;

    public override string ToString() =>
        Delivered ? DeliveredPath! : $"{TargetFileName} (im Rückstau: {HoldBackReason})";
}

/// <summary>Schreibt den GDT-Rücksatz (Satzart 6310) für das PVS.</summary>
public sealed class GdtResponseWriter
{
    private readonly CounterStore _counters;
    private readonly GdtResponseSpool _spool;
    private readonly RuntimeStatus _status;
    private readonly ILogger _logger;

    public GdtResponseWriter(CounterStore counters, GdtResponseSpool spool, RuntimeStatus status, ILogger logger)
    {
        _counters = counters;
        _spool = spool;
        _status = status;
        _logger = logger;
    }

    public GdtResponseOutcome Write(AppConfig config, WorklistItem? item, PdfReportHeader header,
        IReadOnlyList<string> befundLines, IReadOnlyList<GdtAttachment> attachments)
    {
        var gdt = config.Gdt;
        var map = gdt.FieldMap;
        var record = new GdtRecord();

        // --- Kopf ---
        // Empfänger ist das PVS: bevorzugt die ID, unter der es den Auftrag gesendet hat.
        var receiverId = FirstNonEmpty(item?.GdtRequesterId, gdt.ReceiverId);
        var senderId = FirstNonEmpty(item?.GdtAddressedId, gdt.SenderId);

        record.Add(map.Satzidentifikation, gdt.ResponseSatzart);
        record.Add(map.Satzlaenge, "00000");
        record.Add(map.EmpfaengerId, receiverId);
        record.Add(map.SenderId, senderId);
        record.Add(map.GdtVersion, GdtSerializer.VersionCode(gdt.Version));
        record.Add(map.Zeichensatz, GdtSerializer.CharsetCode(gdt.Charset));

        // --- Patient ---
        var isGdt3 = GdtSerializer.IsGdt3(gdt.Version);
        if (isGdt3)
            record.Add(map.ObjektBeginn, map.ObjektNamePatient);

        record.AddIfSet(map.PatientId, header.PatientId);
        record.AddIfSet(map.PatientName, item?.PatientLastName ?? SplitLast(header.PatientName));
        record.AddIfSet(map.PatientVorname, item?.PatientFirstName ?? SplitFirst(header.PatientName));
        record.AddIfSet(map.PatientGeburtsdatum, GdtValues.DicomDateToGdt(item?.PatientBirthDate ?? header.BirthDate));
        record.AddIfSet(map.PatientGeschlecht, GdtValues.DicomSexToGdt(item?.PatientSex ?? header.Sex));

        if (isGdt3)
            record.Add(map.ObjektEnde, map.ObjektNamePatient);

        // --- Untersuchung ---
        if (isGdt3)
            record.Add(map.ObjektBeginn, map.ObjektNameUntersuchung);

        record.AddIfSet(map.DeviceIdent, FirstNonEmpty(item?.GdtDeviceIdent, gdt.DeviceIdent));
        record.AddIfSet(map.UntersuchungsDatum, GdtValues.DicomDateToGdt(header.StudyDate));
        record.AddIfSet(map.UntersuchungsUhrzeit, GdtValues.DicomTimeToGdt(header.StudyTime));
        record.AddIfSet(map.AnforderungsIdent, item?.GdtRequestIdent);
        record.AddIfSet(map.Anforderung, header.ProcedureDescription);

        // Felder, die das PVS im Auftrag mitgeschickt hat und zurückerwartet.
        if (item is not null)
        {
            foreach (var (fieldId, content) in item.EchoFields)
            {
                if (!record.Has(fieldId))
                    record.Add(fieldId, content);
            }
        }

        // --- Befundzeilen ---
        var maxLines = config.Export.MaxGdtBefundLines;
        var lineWidth = config.Export.GdtBefundLineWidth;
        var written = 0;

        foreach (var line in befundLines.SelectMany(l => GdtValues.WrapLines(l, lineWidth)))
        {
            if (maxLines > 0 && written >= maxLines)
            {
                record.Add(map.BefundZeile, "... (gekürzt, vollständiger Befund im PDF)");
                break;
            }
            record.Add(map.BefundZeile, line);
            written++;
        }

        if (isGdt3)
            record.Add(map.ObjektEnde, map.ObjektNameUntersuchung);

        // --- Anhänge ---
        var ordered = OrderAttachments(config, attachments);
        var maxAttachments = config.Export.MaxAttachmentsInGdt;
        var count = 0;

        foreach (var attachment in ordered)
        {
            if (maxAttachments > 0 && count >= maxAttachments)
                break;

            if (isGdt3)
                record.Add(map.ObjektBeginn, map.ObjektNameAnhang);

            record.Add(map.AnhangFormat, attachment.Format);
            record.Add(map.AnhangVerweis, FormatPath(config, attachment.Path));
            record.AddIfSet(map.AnhangBeschreibung, attachment.Description);

            if (isGdt3)
                record.Add(map.ObjektEnde, map.ObjektNameAnhang);

            count++;
        }

        // --- In den Rückstau, dann ggf. sofort ausliefern ---
        var fileName = BuildFileName(gdt, receiverId, senderId, header);
        var bytes = GdtSerializer.Serialize(record, gdt.Charset);

        var spooled = _spool.Enqueue(bytes, header.PatientId, header.PatientName, fileName);
        _status.CountGdtResponse();

        _logger.LogInformation("GDT-Rücksatz erzeugt: {File} ({Attachments} Anhänge, {Lines} Befundzeilen)",
            fileName, count, written);

        if (gdt.ResponseDelivery == ResponseDelivery.AufAbruf)
        {
            _logger.LogInformation(
                "Der Rücksatz für {Patient} wartet auf den Abruf durch das PVS.", header.PatientName);
            return new GdtResponseOutcome(fileName, null, "wartet auf den Abruf durch das PVS");
        }

        if (_spool.TryDeliver(spooled, gdt, out var delivered, out var reason))
        {
            _logger.LogInformation("GDT-Rücksatz ausgeliefert: {Path}", delivered);
            return new GdtResponseOutcome(fileName, delivered, null);
        }

        _logger.LogWarning(
            "Der Rücksatz für {Patient} bleibt vorerst im Rückstau: {Grund}", header.PatientName, reason);

        return new GdtResponseOutcome(fileName, null, reason);
    }

    private static IEnumerable<GdtAttachment> OrderAttachments(AppConfig config, IReadOnlyList<GdtAttachment> attachments)
    {
        if (!config.Export.PdfAttachmentFirst)
            return attachments;

        return attachments
            .OrderByDescending(a => a.Format.Equals("PDF", StringComparison.OrdinalIgnoreCase))
            .ThenBy(a => a.Path, StringComparer.OrdinalIgnoreCase);
    }

    private static string FormatPath(AppConfig config, string path) => config.Export.AttachmentPathMode switch
    {
        AttachmentPathMode.FileNameOnly => Path.GetFileName(path),
        AttachmentPathMode.RelativeToOutbox => MakeRelative(config.Gdt.OutboxDirectory, path),
        _ => path
    };

    private static string MakeRelative(string baseDirectory, string path)
    {
        try
        {
            return Path.GetRelativePath(baseDirectory, path);
        }
        catch
        {
            return path;
        }
    }

    private string BuildFileName(GdtConfig gdt, string receiverId, string senderId, PdfReportHeader header)
    {
        var counter = _counters.Next("gdt-out", Math.Max(1, gdt.OutboxCounterStart));

        var name = (string.IsNullOrWhiteSpace(gdt.OutboxFileNamePattern)
                ? "{receiver}_{sender}_{counter}.gdt"
                : gdt.OutboxFileNamePattern)
            .Replace("{receiver}", GdtValues.SafeFileToken(receiverId, "PVS"))
            .Replace("{sender}", GdtValues.SafeFileToken(senderId, "SONO"))
            .Replace("{patid}", GdtValues.SafeFileToken(header.PatientId, "0"))
            .Replace("{date}", DateTime.Now.ToString("yyyyMMdd"))
            .Replace("{time}", DateTime.Now.ToString("HHmmss"))
            .Replace("{counter}", counter.ToString("D5"));

        if (!Path.HasExtension(name))
            name += ".gdt";

        return name;
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? "";

    private static string SplitLast(string fullName) =>
        GdtValues.SplitDicomPersonName(fullName).LastName;

    private static string SplitFirst(string fullName) =>
        GdtValues.SplitDicomPersonName(fullName).FirstName;
}

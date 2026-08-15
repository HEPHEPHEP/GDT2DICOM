using Gdt2Dicom.Core.Configuration;
using Gdt2Dicom.Core.Dicom;
using Gdt2Dicom.Core.Gdt;
using Gdt2Dicom.Core.Runtime;
using Gdt2Dicom.Core.Worklist;
using Microsoft.Extensions.Logging;

namespace Gdt2Dicom.Core.Pipeline;

/// <summary>Übersetzt einen GDT-Auftrag aus dem PVS in einen Worklist-Eintrag.</summary>
public sealed class GdtRequestProcessor
{
    private readonly Func<AppConfig> _config;
    private readonly WorklistStore _worklist;
    private readonly CounterStore _counters;
    private readonly RuntimeStatus _status;
    private readonly ILogger _logger;

    public GdtRequestProcessor(Func<AppConfig> config, WorklistStore worklist, CounterStore counters,
        RuntimeStatus status, ILogger logger)
    {
        _config = config;
        _worklist = worklist;
        _counters = counters;
        _status = status;
        _logger = logger;
    }

    /// <summary>
    /// Verarbeitet eine GDT-Datei. Gibt den angelegten Eintrag zurück oder null, wenn die
    /// Satzart nicht als Auftrag konfiguriert ist.
    /// </summary>
    public WorklistItem? ProcessFile(string path)
    {
        var config = _config();
        var record = GdtSerializer.ReadFile(path, config.Gdt.Charset);
        return Process(record, path);
    }

    public WorklistItem? Process(GdtRecord record, string sourcePath)
    {
        var config = _config();
        var gdt = config.Gdt;
        var map = gdt.FieldMap;

        var satzart = record.Get(map.Satzidentifikation) ?? record.Get(GdtFk.Satzidentifikation) ?? "";
        var accepted = new List<string> { gdt.RequestSatzart };
        accepted.AddRange(gdt.AdditionalRequestSatzarten);

        if (!accepted.Any(s => string.Equals(s?.Trim(), satzart, StringComparison.OrdinalIgnoreCase)))
        {
            _logger.LogWarning("Satzart {Satzart} aus {File} ist nicht als Auftrag konfiguriert – übersprungen.",
                satzart, Path.GetFileName(sourcePath));
            return null;
        }

        var patientId = record.GetOrEmpty(map.PatientId).Trim();
        if (string.IsNullOrEmpty(patientId))
        {
            _logger.LogWarning("Auftrag {File} enthält keine Patientennummer (FK {Fk}) – wird trotzdem angelegt.",
                Path.GetFileName(sourcePath), map.PatientId);
        }

        var now = DateTime.Now;
        var scheduledDate = GdtValues.GdtDateToDicom(record.Get(map.UntersuchungsDatum));
        if (string.IsNullOrEmpty(scheduledDate))
            scheduledDate = now.ToString("yyyyMMdd");

        var scheduledTime = GdtValues.GdtTimeToDicom(record.Get(map.UntersuchungsUhrzeit));
        if (string.IsNullOrEmpty(scheduledTime))
            scheduledTime = now.ToString("HHmmss");

        var accession = BuildAccessionNumber(config, record, patientId, scheduledDate);
        var procedureDescription = FirstNonEmpty(
            record.GetJoined(map.Anforderung, " "),
            record.Get(map.AnforderungsIdent),
            config.Worklist.DefaultProcedureDescription);

        var item = new WorklistItem
        {
            PatientId = patientId,
            PatientLastName = GdtValues.Sanitize(record.Get(map.PatientName)),
            PatientFirstName = GdtValues.Sanitize(record.Get(map.PatientVorname)),
            PatientTitle = GdtValues.Sanitize(record.Get(map.PatientTitel)),
            PatientBirthDate = GdtValues.GdtDateToDicom(record.Get(map.PatientGeburtsdatum)),
            PatientSex = GdtValues.GdtSexToDicom(record.Get(map.PatientGeschlecht)),
            PatientSize = GdtValues.HeightCmToDicom(record.Get(map.PatientGroesseCm)),
            PatientWeight = GdtValues.WeightKgToDicom(record.Get(map.PatientGewichtKg)),
            PatientAddress = string.Join(", ",
                new[] { record.Get(map.PatientStrasse), record.Get(map.PatientPlzOrt) }
                    .Where(s => !string.IsNullOrWhiteSpace(s))),

            AccessionNumber = accession,
            StudyInstanceUid = UidHelper.Generate(config.Worklist.UidRoot),
            RequestedProcedureId = accession,
            RequestedProcedureDescription = Truncate(procedureDescription, 64),
            ScheduledProcedureStepId = accession,
            ScheduledProcedureStepDescription = Truncate(procedureDescription, 64),
            Modality = string.IsNullOrWhiteSpace(config.Worklist.Modality) ? "US" : config.Worklist.Modality.Trim().ToUpperInvariant(),
            ScheduledStationAeTitle = config.Worklist.ScheduledStationAeTitle.Trim(),
            ScheduledDate = scheduledDate,
            ScheduledTime = scheduledTime,
            ReferringPhysicianName = GdtValues.Sanitize(record.Get(map.UeberweiserName)),
            InstitutionName = config.Worklist.InstitutionName,

            SourceGdtFile = sourcePath,
            GdtRequesterId = record.GetOrEmpty(map.SenderId).Trim(),
            GdtAddressedId = record.GetOrEmpty(map.EmpfaengerId).Trim(),
            GdtDeviceIdent = record.GetOrEmpty(map.DeviceIdent).Trim(),
            GdtRequestIdent = record.GetOrEmpty(map.AnforderungsIdent).Trim()
        };

        // Felder, die das PVS mitgibt und im Rücksatz zurückerwartet, unverändert merken.
        foreach (var field in record.Fields)
        {
            if (field.FieldId.StartsWith("64") || field.FieldId == map.Auftragsnummer)
                item.EchoFields[field.FieldId] = field.Content;
        }

        _worklist.Add(item);
        _status.CountGdtRequest(true, $"Auftrag {accession} für {item.DisplayName} angelegt");
        _logger.LogInformation("Auftrag aus {File} übernommen: {Item}", Path.GetFileName(sourcePath), item);

        return item;
    }

    private string BuildAccessionNumber(AppConfig config, GdtRecord record, string patientId, string scheduledDate)
    {
        var map = config.Gdt.FieldMap;
        var prefix = config.Worklist.AccessionPrefix?.Trim() ?? "";

        string value = config.Worklist.AccessionNumberMode switch
        {
            AccessionNumberMode.FromGdtElseGenerated =>
                FirstNonEmpty(record.Get(map.Auftragsnummer), Generated()),
            AccessionNumberMode.PatientIdAndDate =>
                $"{patientId}{scheduledDate}",
            _ => Generated()
        };

        var result = prefix + value;

        // Accession Number ist DICOM-SH: maximal 16 Zeichen.
        return result.Length <= 16 ? result : result[^16..];

        string Generated() => _counters.Next("accession", 1).ToString("D8");
    }

    private static string FirstNonEmpty(params string?[] candidates) =>
        candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c))?.Trim() ?? "";

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}

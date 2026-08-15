using FellowOakDicom;
using Gdt2Dicom.Core.Configuration;
using Gdt2Dicom.Core.Export;
using Gdt2Dicom.Core.Gdt;
using Gdt2Dicom.Core.Runtime;
using Gdt2Dicom.Core.Worklist;
using Microsoft.Extensions.Logging;

namespace Gdt2Dicom.Core.Pipeline;

/// <summary>
/// Verarbeitet eine abgeschlossene Untersuchung: Bilder exportieren, Messwerte auslesen,
/// Befundblatt erzeugen, DICOM archivieren und den GDT-Rücksatz für das PVS schreiben.
/// </summary>
public sealed class StudyFinalizer
{
    private readonly Func<AppConfig> _config;
    private readonly WorklistStore _worklist;
    private readonly ImageExporter _images;
    private readonly SrTextExtractor _srExtractor;
    private readonly PdfReportBuilder _pdf;
    private readonly GdtResponseWriter _gdtWriter;
    private readonly RuntimeStatus _status;
    private readonly ILogger _logger;

    public StudyFinalizer(
        Func<AppConfig> config,
        WorklistStore worklist,
        ImageExporter images,
        SrTextExtractor srExtractor,
        PdfReportBuilder pdf,
        GdtResponseWriter gdtWriter,
        RuntimeStatus status,
        ILogger logger)
    {
        _config = config;
        _worklist = worklist;
        _images = images;
        _srExtractor = srExtractor;
        _pdf = pdf;
        _gdtWriter = gdtWriter;
        _status = status;
        _logger = logger;
    }

    public async Task FinalizeAsync(PendingStudy study)
    {
        var config = _config();
        var export = config.Export;

        var item = _worklist.Match(study.StudyInstanceUid, study.AccessionNumber, study.PatientId, study.MppsSopInstanceUid);
        if (item is null)
        {
            _logger.LogWarning(
                "Kein passender Auftrag zu Studie {Uid} (Accession {Accession}, Patient {PatientId}) – " +
                "Rücksatz wird nur aus den DICOM-Daten aufgebaut.",
                study.StudyInstanceUid, study.AccessionNumber, study.PatientId);
        }

        var files = LoadFiles(study);
        if (files.Count == 0)
        {
            _logger.LogWarning("Studie {Uid} enthält keine lesbaren Objekte – nichts zu exportieren.", study.StudyInstanceUid);
            _status.CountExportFailure($"Studie {study.StudyInstanceUid} ohne lesbare Objekte");
            return;
        }

        var header = BuildHeader(config, study, item);

        // {index} wird vom Bildexport selbst angehängt, damit die Nummerierung über alle
        // Objekte der Studie fortlaufend bleibt.
        var fileNameBase = BuildToken(export.ImageFileNamePattern.Replace("{index}", ""), header, index: 0);

        // --- Bilder ---
        var exportedImages = new List<ExportedImage>();
        if (export.ExportImages && export.ImageFormat != ImageOutputFormat.None)
        {
            foreach (var file in files.Where(f => f.Dataset.Contains(DicomTag.PixelData)))
            {
                exportedImages.AddRange(_images.Export(
                    file, export, export.ImageDirectory, fileNameBase, exportedImages.Count + 1));
            }
        }

        // --- Structured Reports ---
        var reportBlocks = new List<List<string>>();
        if (export.ExtractStructuredReport)
        {
            foreach (var file in files.Where(f => SrTextExtractor.IsStructuredReport(f.Dataset)))
                reportBlocks.Add(_srExtractor.Extract(file.Dataset));
        }

        var befundLines = reportBlocks.SelectMany(b => b.Append("")).ToList();
        if (befundLines.Count > 0 && string.IsNullOrWhiteSpace(befundLines[^1]))
            befundLines.RemoveAt(befundLines.Count - 1);

        // --- PDF ---
        string? pdfPath = null;
        if (export.CreatePdf)
        {
            var pdfName = BuildToken(export.PdfFileNamePattern, header, index: 0) + ".pdf";
            pdfPath = _pdf.Build(
                Path.Combine(export.PdfDirectory, pdfName),
                header,
                exportedImages.Select(i => i.Path).ToList(),
                befundLines,
                export);
        }

        // --- DICOM archivieren ---
        var archivedFiles = export.ArchiveDicom ? ArchiveDicom(study, header, export) : new List<string>();

        // --- GDT-Rücksatz ---
        GdtResponseOutcome? gdtOutcome = null;
        if (export.WriteGdtResponse)
        {
            var attachments = new List<GdtAttachment>();

            if (pdfPath is not null)
                attachments.Add(new GdtAttachment(pdfPath, "PDF", "Befundblatt"));

            var imageFormat = export.ImageFormat == ImageOutputFormat.Png ? "PNG" : "JPG";
            attachments.AddRange(exportedImages.Select((img, i) =>
                new GdtAttachment(img.Path, imageFormat, $"Bild {i + 1}")));

            gdtOutcome = _gdtWriter.Write(config, item, header, befundLines, attachments);
        }

        // --- Aufräumen und Status ---
        if (export.ArchiveDicom && archivedFiles.Count > 0)
            DeleteIncoming(study);

        if (item is not null)
        {
            _worklist.Update(item.Id, i => i.State = WorklistItemState.Exported);

            if (config.Worklist.RemoveAfterStudyExported)
                _worklist.Remove(item.Id);
        }

        _status.CountStudyExported(
            $"Untersuchung {header.PatientName}: {exportedImages.Count} Bilder, " +
            $"{(pdfPath is null ? "kein PDF" : "PDF")}, " +
            $"{(gdtOutcome is null ? "kein GDT" : gdtOutcome.Delivered ? gdtOutcome.TargetFileName : $"{gdtOutcome.TargetFileName} im Rückstau")}");

        _logger.LogInformation(
            "Untersuchung {Uid} abgeschlossen: {Images} Bilder, {Reports} Reports, PDF={Pdf}, GDT={Gdt}",
            study.StudyInstanceUid, exportedImages.Count, reportBlocks.Count,
            pdfPath ?? "-", gdtOutcome?.ToString() ?? "-");

        await Task.CompletedTask;
    }

    private List<DicomFile> LoadFiles(PendingStudy study)
    {
        var files = new List<DicomFile>();

        foreach (var path in study.Files)
        {
            try
            {
                if (File.Exists(path))
                    files.Add(DicomFile.Open(path));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DICOM-Datei {Path} konnte nicht geöffnet werden.", path);
            }
        }

        // Nach Serie und Instanznummer sortieren, damit die Bildreihenfolge der am Gerät entspricht.
        return files
            .OrderBy(f => f.Dataset.GetSingleValueOrDefault(DicomTag.SeriesNumber, 0))
            .ThenBy(f => f.Dataset.GetSingleValueOrDefault(DicomTag.InstanceNumber, 0))
            .ThenBy(f => f.Dataset.GetSingleValueOrDefault(DicomTag.AcquisitionTime, string.Empty))
            .ToList();
    }

    private static PdfReportHeader BuildHeader(AppConfig config, PendingStudy study, WorklistItem? item)
    {
        var (last, first) = GdtValues.SplitDicomPersonName(study.PatientName);

        var displayName = item is not null
            ? item.DisplayName
            : string.IsNullOrWhiteSpace(first) ? last : $"{last}, {first}";

        var sex = string.IsNullOrWhiteSpace(study.PatientSex) ? item?.PatientSex ?? "" : study.PatientSex;
        var birth = string.IsNullOrWhiteSpace(study.PatientBirthDate) ? item?.PatientBirthDate ?? "" : study.PatientBirthDate;

        return new PdfReportHeader(
            PatientName: displayName,
            PatientId: string.IsNullOrWhiteSpace(study.PatientId) ? item?.PatientId ?? "" : study.PatientId,
            BirthDate: birth,
            Sex: sex,
            StudyDate: string.IsNullOrWhiteSpace(study.StudyDate) ? DateTime.Now.ToString("yyyyMMdd") : study.StudyDate,
            StudyTime: string.IsNullOrWhiteSpace(study.StudyTime) ? DateTime.Now.ToString("HHmmss") : study.StudyTime,
            AccessionNumber: string.IsNullOrWhiteSpace(study.AccessionNumber) ? item?.AccessionNumber ?? "" : study.AccessionNumber,
            ProcedureDescription: item?.RequestedProcedureDescription ?? config.Worklist.DefaultProcedureDescription,
            Modality: string.IsNullOrWhiteSpace(study.Modality) ? config.Worklist.Modality : study.Modality,
            DeviceName: study.DeviceName);
    }

    private static string BuildToken(string pattern, PdfReportHeader header, int index)
    {
        var value = (string.IsNullOrWhiteSpace(pattern) ? "{patid}_{date}_{time}" : pattern)
            .Replace("{patid}", GdtValues.SafeFileToken(header.PatientId, "0"))
            .Replace("{name}", GdtValues.SafeFileToken(header.PatientName, "patient"))
            .Replace("{date}", string.IsNullOrWhiteSpace(header.StudyDate) ? DateTime.Now.ToString("yyyyMMdd") : header.StudyDate)
            .Replace("{time}", string.IsNullOrWhiteSpace(header.StudyTime) ? DateTime.Now.ToString("HHmmss") : header.StudyTime)
            .Replace("{accession}", GdtValues.SafeFileToken(header.AccessionNumber, "0"))
            .Replace("{index}", index.ToString("D3"));

        return GdtValues.SafeFileToken(value.Trim('_', '-', ' '), "export");
    }

    private List<string> ArchiveDicom(PendingStudy study, PdfReportHeader header, ExportConfig export)
    {
        var archived = new List<string>();

        try
        {
            var layout = (string.IsNullOrWhiteSpace(export.DicomArchiveLayout) ? @"{patid}\{date}_{accession}" : export.DicomArchiveLayout)
                .Replace("{patid}", GdtValues.SafeFileToken(header.PatientId, "0"))
                .Replace("{name}", GdtValues.SafeFileToken(header.PatientName, "patient"))
                .Replace("{date}", header.StudyDate)
                .Replace("{accession}", GdtValues.SafeFileToken(header.AccessionNumber, "0"))
                .Replace("{studyuid}", GdtValues.SafeFileToken(study.StudyInstanceUid, "study"));

            var targetDirectory = Path.Combine(export.DicomArchiveDirectory, layout);
            Directory.CreateDirectory(targetDirectory);

            foreach (var source in study.Files.Where(File.Exists))
            {
                var target = Path.Combine(targetDirectory, Path.GetFileName(source));
                File.Copy(source, target, overwrite: true);
                archived.Add(target);
            }

            _logger.LogInformation("{Count} DICOM-Objekte archiviert nach {Directory}", archived.Count, targetDirectory);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DICOM-Archivierung der Studie {Uid} fehlgeschlagen.", study.StudyInstanceUid);
        }

        return archived;
    }

    private void DeleteIncoming(PendingStudy study)
    {
        foreach (var path in study.Files)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Zwischendatei {Path} konnte nicht gelöscht werden.", path);
            }
        }
    }
}

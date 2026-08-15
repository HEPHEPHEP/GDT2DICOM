using Gdt2Dicom.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace Gdt2Dicom.Core.Runtime;

/// <summary>Eine im Archiv liegende Untersuchung: der Ordner, der die DICOM-Dateien enthält.</summary>
public sealed record ArchivedStudy(string Directory, DateTime NewestWriteUtc, long Bytes, int FileCount);

/// <summary>
/// Begrenzt das DICOM-Archiv nach Alter und Gesamtgröße.
/// </summary>
/// <remarks>
/// Gelöscht wird immer eine vollständige Untersuchung, nie einzelne Bilder daraus: eine Studie,
/// der die Hälfte der Aufnahmen fehlt, ist schlimmer als gar keine, weil der Verlust beim
/// Betrachten nicht auffällt.
///
/// Standardmäßig ist die Begrenzung ausgeschaltet. Die Dateien unterliegen der ärztlichen
/// Aufbewahrungspflicht; das Einschalten ist eine bewusste Entscheidung der Praxis.
/// </remarks>
public static class ArchiveCleanup
{
    public static int Run(ExportConfig config, ILogger logger)
    {
        if (!config.LimitDicomArchive || !config.ArchiveDicom)
            return 0;

        var root = config.DicomArchiveDirectory;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return 0;

        var studies = Collect(root, logger);
        if (studies.Count == 0)
            return 0;

        var doomed = new List<ArchivedStudy>();

        // 1. Altersgrenze
        if (config.DicomArchiveRetentionDays > 0)
        {
            var cutoff = DateTime.UtcNow.AddDays(-config.DicomArchiveRetentionDays);
            doomed.AddRange(studies.Where(s => s.NewestWriteUtc < cutoff));
        }

        // 2. Größengrenze: die ältesten verbliebenen Studien entfernen, bis es passt.
        if (config.DicomArchiveMaxSizeGb > 0)
        {
            var limit = (long)config.DicomArchiveMaxSizeGb * 1024 * 1024 * 1024;
            var remaining = studies.Except(doomed).OrderBy(s => s.NewestWriteUtc).ToList();
            var total = remaining.Sum(s => s.Bytes);

            foreach (var study in remaining)
            {
                if (total <= limit)
                    break;
                doomed.Add(study);
                total -= study.Bytes;
            }
        }

        if (doomed.Count == 0)
            return 0;

        var removed = 0;
        long freed = 0;

        foreach (var study in doomed.DistinctBy(s => s.Directory))
        {
            try
            {
                Directory.Delete(study.Directory, recursive: true);
                removed++;
                freed += study.Bytes;

                logger.LogInformation(
                    "Archivierte Untersuchung entfernt: {Directory} ({Files} Dateien, {Megabytes:0.0} MB, " +
                    "zuletzt geschrieben {Date:dd.MM.yyyy})",
                    study.Directory, study.FileCount, study.Bytes / 1024.0 / 1024.0,
                    study.NewestWriteUtc.ToLocalTime());
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Archivordner {Directory} konnte nicht entfernt werden.", study.Directory);
            }
        }

        PruneEmptyDirectories(root, logger);

        if (removed > 0)
        {
            logger.LogInformation("{Count} Untersuchungen aus dem DICOM-Archiv entfernt ({Gigabytes:0.00} GB frei).",
                removed, freed / 1024.0 / 1024.0 / 1024.0);
        }

        return removed;
    }

    /// <summary>
    /// Sucht alle Ordner, die unmittelbar DICOM-Dateien enthalten. Genau diese Ebene ist
    /// eine Untersuchung – unabhängig davon, wie tief das konfigurierte Ordnermuster schachtelt.
    /// </summary>
    public static List<ArchivedStudy> Collect(string root, ILogger logger)
    {
        var studies = new List<ArchivedStudy>();

        try
        {
            foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            {
                var files = new DirectoryInfo(directory).GetFiles("*.dcm", SearchOption.TopDirectoryOnly);
                if (files.Length == 0)
                    continue;

                studies.Add(new ArchivedStudy(
                    directory,
                    files.Max(f => f.LastWriteTimeUtc),
                    files.Sum(f => f.Length),
                    files.Length));
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "DICOM-Archiv {Root} konnte nicht durchsucht werden.", root);
        }

        return studies;
    }

    /// <summary>Entfernt zurückgebliebene leere Patientenordner. Die Wurzel bleibt bestehen.</summary>
    private static void PruneEmptyDirectories(string root, ILogger logger)
    {
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                         .OrderByDescending(d => d.Length))
            {
                if (!Directory.Exists(directory))
                    continue;
                if (Directory.EnumerateFileSystemEntries(directory).Any())
                    continue;

                Directory.Delete(directory);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Leere Archivordner konnten nicht vollständig aufgeräumt werden.");
        }
    }

    /// <summary>Gesamtgröße des Archivs – für die Anzeige in der Oberfläche.</summary>
    public static (int Studies, long Bytes) Measure(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return (0, 0);

        var studies = Collect(root, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        return (studies.Count, studies.Sum(s => s.Bytes));
    }
}

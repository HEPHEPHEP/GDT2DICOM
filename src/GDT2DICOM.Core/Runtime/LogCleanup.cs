using Gdt2Dicom.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace Gdt2Dicom.Core.Runtime;

/// <summary>
/// Löscht Logdateien nach Alter.
/// </summary>
/// <remarks>
/// Serilogs eigenes <c>retainedFileCountLimit</c> begrenzt nur die Anzahl der Dateien.
/// Läuft der Dienst nicht jeden Tag – Praxisrechner sind am Wochenende oft aus – entsprechen
/// 30 Dateien deutlich mehr als 30 Tage. Außerdem räumt Serilog nur beim Anlegen einer neuen
/// Datei auf, also frühestens am nächsten Betriebstag. Deshalb hier eine Prüfung nach
/// tatsächlichem Alter, die auch beim Start und danach zyklisch läuft.
/// </remarks>
public static class LogCleanup
{
    /// <summary>Muster der eigenen Logdateien. Fremde Dateien im Ordner bleiben unangetastet.</summary>
    public const string FilePattern = "gdt2dicom-*.log";

    /// <summary>Löscht abgelaufene Logdateien und gibt zurück, wie viele entfernt wurden.</summary>
    public static int Run(GeneralConfig config, ILogger logger)
    {
        if (!config.DeleteOldLogs)
            return 0;

        var days = config.LogRetentionDays;
        if (days <= 0)
        {
            logger.LogWarning(
                "Aufbewahrungsdauer für Logdateien ist {Days} Tage – das würde auch das laufende " +
                "Protokoll löschen. Es wird nichts entfernt.", days);
            return 0;
        }

        if (string.IsNullOrWhiteSpace(config.LogDirectory) || !Directory.Exists(config.LogDirectory))
            return 0;

        var cutoff = DateTime.UtcNow.AddDays(-days);
        var removed = 0;
        long freedBytes = 0;

        foreach (var file in EnumerateSafely(config.LogDirectory, logger))
        {
            // Nach Schreibzeitpunkt gehen, nicht nach Erstellungszeit: beim Kopieren eines
            // Logordners auf einen neuen Rechner bleibt die Schreibzeit erhalten.
            if (file.LastWriteTimeUtc >= cutoff)
                continue;

            try
            {
                var size = file.Length;
                file.Delete();
                removed++;
                freedBytes += size;
            }
            catch (IOException)
            {
                // Datei ist noch offen – beim nächsten Durchlauf erneut versuchen.
            }
            catch (UnauthorizedAccessException ex)
            {
                logger.LogWarning(ex, "Logdatei {File} konnte nicht gelöscht werden.", file.Name);
            }
        }

        if (removed > 0)
        {
            logger.LogInformation(
                "{Count} Logdateien älter als {Days} Tage gelöscht ({Megabytes:0.0} MB frei).",
                removed, days, freedBytes / 1024.0 / 1024.0);
        }

        return removed;
    }

    private static IEnumerable<FileInfo> EnumerateSafely(string directory, ILogger logger)
    {
        FileInfo[] files;
        try
        {
            files = new DirectoryInfo(directory).GetFiles(FilePattern, SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Logverzeichnis {Directory} konnte nicht gelesen werden.", directory);
            return Array.Empty<FileInfo>();
        }

        return files;
    }
}

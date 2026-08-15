using Gdt2Dicom.Core.Configuration;

namespace Gdt2Dicom.Core.Runtime;

/// <summary>Ergebnis der Prüfung eines konfigurierten Verzeichnisses.</summary>
public sealed record PathCheck(
    string Label,
    string Path,
    bool IsUnc,
    bool Exists,
    bool CanWrite,
    string? Error)
{
    public bool Ok => Exists && CanWrite && Error is null;
}

/// <summary>
/// Prüft, ob die konfigurierten Verzeichnisse erreichbar und beschreibbar sind.
/// </summary>
/// <remarks>
/// Diese Prüfung muss im Dienst laufen, nicht in der Oberfläche. Die Oberfläche läuft als
/// angemeldeter Benutzer und kommt an eine Netzwerkfreigabe meist problemlos heran – der
/// Dienst dagegen läuft standardmäßig als LocalSystem und hat im Netz überhaupt keine
/// Anmeldedaten. Eine Prüfung aus der Oberfläche würde also grünes Licht geben, während der
/// Dienst später an derselben Freigabe scheitert.
/// </remarks>
public static class PathProbe
{
    /// <summary>
    /// Obergrenze je Verzeichnis. Ein nicht erreichbarer Netzwerkpfad lässt Datei-Aufrufe
    /// sonst zwanzig Sekunden und länger hängen; bei acht Verzeichnissen wäre die Prüfung
    /// minutenlang blockiert.
    /// </summary>
    public static readonly TimeSpan PerPathTimeout = TimeSpan.FromSeconds(10);

    public static List<PathCheck> CheckAll(AppConfig config)
    {
        var checks = new List<PathCheck>
        {
            Check("GDT-Eingang", config.Gdt.InboxDirectory),
            Check("GDT-Ausgang", config.Gdt.OutboxDirectory),
            Check("GDT-Archiv", config.Gdt.InboxArchiveDirectory),
            Check("Datenverzeichnis", config.General.DataDirectory),
            Check("Logverzeichnis", config.General.LogDirectory),
            Check("DICOM-Eingang", config.Dicom.IncomingDirectory)
        };

        if (config.Export.ExportImages)
            checks.Add(Check("Bildverzeichnis", config.Export.ImageDirectory));
        if (config.Export.CreatePdf)
            checks.Add(Check("PDF-Verzeichnis", config.Export.PdfDirectory));
        if (config.Export.ArchiveDicom)
            checks.Add(Check("DICOM-Archiv", config.Export.DicomArchiveDirectory));

        return checks.Where(c => !string.IsNullOrWhiteSpace(c.Path)).ToList();
    }

    /// <summary>
    /// Prüft ein Verzeichnis: vorhanden, anlegbar, beschreibbar. Geschrieben wird eine winzige
    /// Datei, die sofort wieder verschwindet – nur so zeigt sich, ob die Freigabe wirklich
    /// Schreibrechte gewährt und nicht bloß lesbar ist.
    /// </summary>
    public static PathCheck Check(string label, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new PathCheck(label, "", false, false, false, "nicht konfiguriert");

        var isUnc = IsUncPath(path);

        // Der eigentliche Test läuft auf einem Hintergrund-Thread, damit ein hängender
        // Netzwerkpfad die Prüfung nicht festsetzt. Der Thread selbst bleibt zwar hängen,
        // läuft aber irgendwann von selbst aus.
        var task = Task.Run(() => Probe(label, path, isUnc));

        if (!task.Wait(PerPathTimeout))
        {
            return new PathCheck(label, path, isUnc, false, false,
                $"keine Antwort innerhalb von {PerPathTimeout.TotalSeconds:0} Sekunden – " +
                "Freigabe nicht erreichbar?");
        }

        return task.Result;
    }

    private static PathCheck Probe(string label, string path, bool isUnc)
    {
        try
        {
            var existed = Directory.Exists(path);
            if (!existed)
                Directory.CreateDirectory(path);

            var probe = Path.Combine(path, $".gdt2dicom-test-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "test");
            File.Delete(probe);

            return new PathCheck(label, path, isUnc, Exists: true, CanWrite: true,
                existed ? null : "war nicht vorhanden und wurde angelegt");
        }
        catch (UnauthorizedAccessException ex)
        {
            return new PathCheck(label, path, isUnc, Directory.Exists(path), false,
                $"kein Zugriff: {ex.Message}");
        }
        catch (IOException ex)
        {
            return new PathCheck(label, path, isUnc, Directory.Exists(path), false, ex.Message);
        }
        catch (Exception ex)
        {
            return new PathCheck(label, path, isUnc, false, false, ex.Message);
        }
    }

    /// <summary>Erkennt Netzwerkpfade der Form \\server\freigabe.</summary>
    public static bool IsUncPath(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal);

    /// <summary>
    /// Erkennt zugeordnete Laufwerksbuchstaben. Die sind pro Anmeldesitzung gültig und für
    /// einen Dienst grundsätzlich unbrauchbar – auch dann, wenn sie im Explorer sichtbar sind.
    /// </summary>
    public static bool IsMappedDrive(string path)
    {
        if (path.Length < 2 || path[1] != ':')
            return false;

        try
        {
            var root = path[..1] + @":\";
            var drive = new DriveInfo(root);
            return drive.DriveType == DriveType.Network;
        }
        catch
        {
            return false;
        }
    }
}

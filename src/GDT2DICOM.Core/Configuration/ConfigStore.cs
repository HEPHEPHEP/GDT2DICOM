using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gdt2Dicom.Core.Configuration;

/// <summary>Lädt und speichert die Konfiguration. Wird von Dienst und GUI gemeinsam genutzt.</summary>
public static class ConfigStore
{
    public static string RootDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GDT2DICOM");

    public static string ConfigFilePath { get; } = Path.Combine(RootDirectory, "config.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    private static readonly object FileLock = new();

    /// <summary>
    /// Legt das ProgramData-Verzeichnis an und gibt "Benutzer" Schreibrechte, damit die GUI
    /// ohne Adminrechte speichern kann, während der Dienst als LocalSystem läuft.
    /// </summary>
    /// <remarks>
    /// Die Zugriffsrechte werden bei jedem Aufruf gesetzt, nicht nur beim Anlegen des
    /// Ordners. Existiert er bereits – etwa aus einer früheren Fassung oder von einem Lauf
    /// ohne erhöhte Rechte – bekämen angemeldete Benutzer sonst nie Schreibrechte. Die
    /// Oberfläche könnte die Konfiguration dann nicht speichern, sobald der Dienst gestoppt
    /// ist, weil dessen Dateien unter LocalSystem entstanden sind.
    /// </remarks>
    public static void EnsureRootDirectory()
    {
        Directory.CreateDirectory(RootDirectory);

        try
        {
            var info = new DirectoryInfo(RootDirectory);
            var security = info.GetAccessControl();
            var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
            security.AddAccessRule(new FileSystemAccessRule(
                users,
                FileSystemRights.Modify,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            info.SetAccessControl(security);
        }
        catch (Exception)
        {
            // Ohne die nötigen Rechte nicht setzbar. Läuft der Dienst als LocalSystem,
            // holt er es beim nächsten Start nach; die Oberfläche arbeitet derweil über
            // den Steuerkanal weiter.
        }
    }

    public static AppConfig Load()
    {
        EnsureRootDirectory();

        lock (FileLock)
        {
            if (!File.Exists(ConfigFilePath))
            {
                var fresh = new AppConfig();
                SaveInternal(fresh);
                return fresh;
            }

            var json = File.ReadAllText(ConfigFilePath);
            var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
            return config ?? new AppConfig();
        }
    }

    /// <summary>Lädt die Konfiguration und gibt bei Fehlern die Standardwerte zurück.</summary>
    public static AppConfig LoadSafe(out string? error)
    {
        try
        {
            error = null;
            return Load();
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return new AppConfig();
        }
    }

    public static void Save(AppConfig config)
    {
        EnsureRootDirectory();
        lock (FileLock)
        {
            SaveInternal(config);
        }
    }

    private static void SaveInternal(AppConfig config)
    {
        var tempPath = ConfigFilePath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(config, JsonOptions));

        if (File.Exists(ConfigFilePath))
        {
            var backup = ConfigFilePath + ".bak";
            File.Replace(tempPath, ConfigFilePath, backup, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(tempPath, ConfigFilePath);
        }
    }

    /// <summary>Tiefe Kopie über die JSON-Serialisierung – für "Änderungen verwerfen" in der GUI.</summary>
    public static AppConfig Clone(AppConfig config) =>
        JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(config, JsonOptions), JsonOptions)!;

    public static string Serialize(AppConfig config) => JsonSerializer.Serialize(config, JsonOptions);

    public static AppConfig Deserialize(string json) =>
        JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();

    /// <summary>Legt alle in der Konfiguration referenzierten Verzeichnisse an, soweit möglich.</summary>
    public static List<string> EnsureConfiguredDirectories(AppConfig config)
    {
        var problems = new List<string>();

        void Try(string? path, string label)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;
            try
            {
                Directory.CreateDirectory(path);
            }
            catch (Exception ex)
            {
                problems.Add($"{label}: {path} – {ex.Message}");
            }
        }

        Try(config.General.DataDirectory, "Datenverzeichnis");
        Try(config.General.LogDirectory, "Logverzeichnis");
        Try(config.Gdt.InboxDirectory, "GDT-Eingang");
        Try(config.Gdt.OutboxDirectory, "GDT-Ausgang");
        Try(config.Gdt.InboxArchiveDirectory, "GDT-Archiv");
        Try(config.Dicom.IncomingDirectory, "DICOM-Eingang");
        if (config.Export.ExportImages) Try(config.Export.ImageDirectory, "Bildverzeichnis");
        if (config.Export.CreatePdf) Try(config.Export.PdfDirectory, "PDF-Verzeichnis");
        if (config.Export.ArchiveDicom) Try(config.Export.DicomArchiveDirectory, "DICOM-Archiv");

        return problems;
    }
}

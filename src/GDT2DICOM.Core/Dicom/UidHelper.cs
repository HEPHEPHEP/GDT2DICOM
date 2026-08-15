namespace Gdt2Dicom.Core.Dicom;

/// <summary>Erzeugt DICOM-UIDs unterhalb einer konfigurierbaren Wurzel.</summary>
public static class UidHelper
{
    private static int _counter;

    /// <summary>
    /// Baut eine UID aus Wurzel, Zeitstempel, Prozess-ID und Zähler. Das ist innerhalb einer
    /// Installation eindeutig; für den Praxisbetrieb sollte trotzdem eine eigene, registrierte
    /// OID als Wurzel eingetragen werden.
    /// </summary>
    public static string Generate(string root, string suffixHint = "")
    {
        var cleanRoot = string.IsNullOrWhiteSpace(root) ? "1.2.276.0.7230010.3.1.4.1" : root.Trim().TrimEnd('.');
        var counter = Interlocked.Increment(ref _counter) % 100000;
        var candidate = $"{cleanRoot}.{DateTime.UtcNow:yyyyMMddHHmmss}.{Environment.ProcessId % 100000}.{counter}";

        // DICOM erlaubt maximal 64 Zeichen.
        return candidate.Length <= 64 ? candidate : candidate[..64].TrimEnd('.');
    }

    public static bool IsValid(string? uid)
    {
        if (string.IsNullOrWhiteSpace(uid) || uid.Length > 64)
            return false;

        var parts = uid.Split('.');
        return parts.Length >= 2 && parts.All(p => p.Length > 0 && p.All(char.IsDigit));
    }
}

using System.Globalization;

namespace Gdt2Dicom.Core.Gdt;

/// <summary>Konvertierungen zwischen GDT-Wertformaten und DICOM-Wertformaten.</summary>
public static class GdtValues
{
    /// <summary>GDT-Datum "TTMMJJJJ" → DICOM-DA "JJJJMMTT". Leer, wenn nicht parsbar.</summary>
    public static string GdtDateToDicom(string? gdtDate)
    {
        var value = gdtDate?.Trim();
        if (string.IsNullOrEmpty(value))
            return "";

        if (value.Length == 8 && value.All(char.IsDigit))
            return value[4..8] + value[2..4] + value[0..2];

        // Manche PVS liefern TT.MM.JJJJ.
        if (DateTime.TryParseExact(value, new[] { "dd.MM.yyyy", "d.M.yyyy", "yyyy-MM-dd" },
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return parsed.ToString("yyyyMMdd");

        return "";
    }

    /// <summary>DICOM-DA "JJJJMMTT" → GDT-Datum "TTMMJJJJ".</summary>
    public static string DicomDateToGdt(string? dicomDate)
    {
        var value = dicomDate?.Trim().Replace(".", "");
        if (string.IsNullOrEmpty(value) || value.Length < 8 || !value[..8].All(char.IsDigit))
            return "";
        return value[6..8] + value[4..6] + value[0..4];
    }

    public static string DateToGdt(DateTime date) => date.ToString("ddMMyyyy");

    public static string TimeToGdt(DateTime time) => time.ToString("HHmmss");

    /// <summary>GDT-Uhrzeit "SSMMSS" → DICOM-TM. Beide Formate sind hier deckungsgleich.</summary>
    public static string GdtTimeToDicom(string? gdtTime)
    {
        var value = gdtTime?.Trim().Replace(":", "");
        if (string.IsNullOrEmpty(value))
            return "";
        return value.Length >= 6 ? value[..6] : value.PadRight(6, '0');
    }

    public static string DicomTimeToGdt(string? dicomTime)
    {
        var value = dicomTime?.Trim().Replace(":", "");
        if (string.IsNullOrEmpty(value))
            return "";
        var digits = new string(value.TakeWhile(c => char.IsDigit(c)).ToArray());
        return digits.Length >= 6 ? digits[..6] : digits.PadRight(6, '0');
    }

    /// <summary>GDT-Geschlecht (1 = männlich, 2 = weiblich, 3 = divers) → DICOM-CS.</summary>
    public static string GdtSexToDicom(string? gdtSex) => gdtSex?.Trim() switch
    {
        "1" => "M",
        "2" => "F",
        "3" or "4" => "O",
        "M" or "m" => "M",
        "W" or "w" or "F" or "f" => "F",
        _ => ""
    };

    public static string DicomSexToGdt(string? dicomSex) => dicomSex?.Trim().ToUpperInvariant() switch
    {
        "M" => "1",
        "F" => "2",
        "O" => "3",
        _ => ""
    };

    /// <summary>Baut einen DICOM-PN-Wert "Nachname^Vorname^^Titel".</summary>
    public static string BuildDicomPersonName(string? lastName, string? firstName, string? title = null)
    {
        var last = Sanitize(lastName);
        var first = Sanitize(firstName);
        var pre = Sanitize(title);

        if (string.IsNullOrEmpty(last) && string.IsNullOrEmpty(first))
            return "";

        return string.IsNullOrEmpty(pre) ? $"{last}^{first}" : $"{last}^{first}^^{pre}";
    }

    /// <summary>Zerlegt einen DICOM-PN-Wert in Nachname und Vorname.</summary>
    public static (string LastName, string FirstName) SplitDicomPersonName(string? personName)
    {
        if (string.IsNullOrWhiteSpace(personName))
            return ("", "");

        var parts = personName.Split('^');
        var last = parts.Length > 0 ? parts[0].Trim() : "";
        var first = parts.Length > 1 ? parts[1].Trim() : "";

        // Fallback für Geräte, die "Nachname, Vorname" oder "Vorname Nachname" liefern.
        if (parts.Length == 1 && last.Contains(','))
        {
            var comma = last.Split(',', 2);
            return (comma[0].Trim(), comma[1].Trim());
        }

        return (last, first);
    }

    /// <summary>Entfernt Zeichen, die in DICOM-PN nicht zulässig sind.</summary>
    public static string Sanitize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "" : value.Replace("^", " ").Replace("\\", " ").Replace("=", " ").Trim();

    /// <summary>Größe in cm → DICOM Patient Size in Metern.</summary>
    public static string HeightCmToDicom(string? cm)
    {
        if (double.TryParse(cm?.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var value) && value > 0)
            return (value / 100.0).ToString("0.###", CultureInfo.InvariantCulture);
        return "";
    }

    public static string WeightKgToDicom(string? kg)
    {
        if (double.TryParse(kg?.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var value) && value > 0)
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        return "";
    }

    /// <summary>Bricht Text auf die konfigurierte Zeilenbreite um, damit er in GDT-Befundzeilen passt.</summary>
    public static IEnumerable<string> WrapLines(string text, int width)
    {
        if (width <= 0)
            width = 60;

        foreach (var rawLine in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (line.Length <= width)
            {
                yield return line;
                continue;
            }

            var remaining = line;
            while (remaining.Length > width)
            {
                var breakAt = remaining.LastIndexOf(' ', Math.Min(width, remaining.Length - 1));
                if (breakAt <= 0)
                    breakAt = width;

                yield return remaining[..breakAt].TrimEnd();
                remaining = remaining[breakAt..].TrimStart();
            }

            if (remaining.Length > 0)
                yield return remaining;
        }
    }

    /// <summary>Macht einen Wert für die Verwendung in Dateinamen und Pfaden sicher.</summary>
    public static string SafeFileToken(string? value, string fallback = "unbekannt")
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Select(c => invalid.Contains(c) || c == ' ' ? '_' : c).ToArray()).Trim('_', '.');
        return string.IsNullOrEmpty(cleaned) ? fallback : cleaned;
    }
}

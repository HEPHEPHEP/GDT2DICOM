using System.Text;
using Gdt2Dicom.Core.Configuration;

namespace Gdt2Dicom.Core.Gdt;

/// <summary>
/// Liest und schreibt GDT-Dateien im Format <c>LLLFFFF&lt;Inhalt&gt;CRLF</c>.
/// LLL ist die Länge der gesamten Zeile in Bytes einschließlich Längenangabe,
/// Feldkennung und CRLF; damit ergibt sich LLL = Inhaltsbytes + 9.
/// </summary>
public static class GdtSerializer
{
    /// <summary>Overhead pro Zeile: 3 Zeichen Länge + 4 Zeichen Feldkennung + CR + LF.</summary>
    public const int LineOverhead = 9;

    /// <summary>Maximale Inhaltslänge in Bytes, damit LLL dreistellig bleibt.</summary>
    public const int MaxContentBytes = 999 - LineOverhead;

    private static bool _providerRegistered;

    private static void EnsureEncodingProvider()
    {
        if (_providerRegistered)
            return;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        _providerRegistered = true;
    }

    public static Encoding GetEncoding(GdtCharset charset)
    {
        EnsureEncodingProvider();
        return charset switch
        {
            GdtCharset.Ascii7 => Encoding.ASCII,
            GdtCharset.Cp437 => Encoding.GetEncoding(437),
            GdtCharset.Utf8 => new UTF8Encoding(false),
            _ => Encoding.Latin1
        };
    }

    public static string CharsetCode(GdtCharset charset) => charset switch
    {
        GdtCharset.Ascii7 => "1",
        GdtCharset.Cp437 => "2",
        GdtCharset.Iso8859_1 => "3",
        GdtCharset.Utf8 => "4",
        _ => "3"
    };

    public static GdtCharset ParseCharsetCode(string? code) => code?.Trim() switch
    {
        "1" => GdtCharset.Ascii7,
        "2" => GdtCharset.Cp437,
        "4" => GdtCharset.Utf8,
        _ => GdtCharset.Iso8859_1
    };

    public static string VersionCode(GdtVersion version) => version switch
    {
        GdtVersion.V30 => "03.00",
        GdtVersion.V31 => "03.10",
        _ => "02.10"
    };

    public static bool IsGdt3(GdtVersion version) => version is GdtVersion.V30 or GdtVersion.V31;

    // -----------------------------------------------------------------------
    // Lesen
    // -----------------------------------------------------------------------

    /// <summary>
    /// Liest eine GDT-Datei. Der Zeichensatz wird zunächst aus FK 9206 der Datei bestimmt;
    /// nur wenn das Feld fehlt, greift <paramref name="fallbackCharset"/>.
    /// </summary>
    public static GdtRecord ReadFile(string path, GdtCharset fallbackCharset = GdtCharset.Iso8859_1)
    {
        var bytes = File.ReadAllBytes(path);
        var record = Read(bytes, fallbackCharset);
        record.SourcePath = path;
        return record;
    }

    public static GdtRecord Read(byte[] bytes, GdtCharset fallbackCharset = GdtCharset.Iso8859_1)
    {
        // Erster Durchlauf mit Latin1: bytetreu, damit die Längenangaben stimmen und
        // FK 9206 sicher gefunden wird.
        var probe = ParseLines(bytes, Encoding.Latin1);
        var declared = probe.FirstOrDefault(f => f.FieldId == GdtFk.Zeichensatz)?.Content;

        var charset = declared is null ? fallbackCharset : ParseCharsetCode(declared);
        var encoding = GetEncoding(charset);

        var record = new GdtRecord();
        foreach (var field in ParseLines(bytes, encoding))
            record.Fields.Add(field);
        return record;
    }

    private static List<GdtField> ParseLines(byte[] bytes, Encoding encoding)
    {
        var fields = new List<GdtField>();
        var position = 0;

        while (position < bytes.Length)
        {
            // Führende Zeilenumbrüche / Leerbytes überspringen.
            while (position < bytes.Length && (bytes[position] == (byte)'\r' || bytes[position] == (byte)'\n' || bytes[position] == 0))
                position++;
            if (position >= bytes.Length)
                break;

            var lineEnd = IndexOfLineBreak(bytes, position);
            var rawLength = lineEnd - position;
            if (rawLength < 7)
            {
                // Zu kurz für Länge + Feldkennung – Rest der Zeile verwerfen.
                position = SkipLineBreak(bytes, lineEnd);
                continue;
            }

            var fieldId = encoding.GetString(bytes, position + 3, 4);
            var contentBytes = rawLength - 7;

            // Wenn die deklarierte Länge plausibel ist, hat sie Vorrang: so überleben
            // Inhalte, die selbst ein CR oder LF enthalten.
            var declaredLength = TryParseLength(bytes, position);
            if (declaredLength is int len && len >= LineOverhead && position + len - 2 <= bytes.Length)
            {
                var declaredContent = len - LineOverhead;
                if (declaredContent != contentBytes && position + 7 + declaredContent <= bytes.Length)
                {
                    contentBytes = declaredContent;
                    lineEnd = position + 7 + declaredContent;
                }
            }

            var content = encoding.GetString(bytes, position + 7, Math.Max(0, contentBytes)).TrimEnd('\r', '\n');
            fields.Add(new GdtField(fieldId, content));
            position = SkipLineBreak(bytes, lineEnd);
        }

        return fields;
    }

    private static int? TryParseLength(byte[] bytes, int position)
    {
        if (position + 3 > bytes.Length)
            return null;
        var value = 0;
        for (var i = 0; i < 3; i++)
        {
            var b = bytes[position + i];
            if (b < (byte)'0' || b > (byte)'9')
                return null;
            value = value * 10 + (b - (byte)'0');
        }
        return value;
    }

    private static int IndexOfLineBreak(byte[] bytes, int from)
    {
        for (var i = from; i < bytes.Length; i++)
            if (bytes[i] == (byte)'\r' || bytes[i] == (byte)'\n')
                return i;
        return bytes.Length;
    }

    private static int SkipLineBreak(byte[] bytes, int index)
    {
        if (index < bytes.Length && bytes[index] == (byte)'\r')
            index++;
        if (index < bytes.Length && bytes[index] == (byte)'\n')
            index++;
        return index;
    }

    // -----------------------------------------------------------------------
    // Schreiben
    // -----------------------------------------------------------------------

    /// <summary>
    /// Serialisiert einen Satz. Ein bereits vorhandenes Feld 8100 wird mit der korrekten
    /// Gesamtlänge neu gesetzt; fehlt es, wird es direkt hinter FK 8000 eingefügt.
    /// </summary>
    public static byte[] Serialize(GdtRecord record, GdtCharset charset)
    {
        var encoding = GetEncoding(charset);
        var fields = NormalizeForWrite(record, charset);

        // FK 8100 mit fünfstelliger, nullgepolsterter Zahl hat immer die Zeilenlänge 14,
        // dadurch ist die Gesamtlänge ohne Iteration bestimmbar.
        var total = fields.Sum(f => LineOverhead + encoding.GetByteCount(f.Content));

        var buffer = new MemoryStream();
        foreach (var field in fields)
        {
            var content = field.FieldId == GdtFk.Satzlaenge
                ? total.ToString("D5")
                : field.Content;

            WriteLine(buffer, encoding, field.FieldId, content);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Bringt die Felder in schreibbare Form: Umlaute für 7-Bit, Zeilen aufteilen, die zu
    /// lang sind, und sicherstellen, dass FK 8100 vorhanden ist.
    /// </summary>
    private static List<GdtField> NormalizeForWrite(GdtRecord record, GdtCharset charset)
    {
        var encoding = GetEncoding(charset);
        var result = new List<GdtField>();

        foreach (var field in record.Fields)
        {
            var content = field.Content.Replace("\r", " ").Replace("\n", " ");
            if (charset == GdtCharset.Ascii7)
                content = Transliterate(content);

            if (field.FieldId == GdtFk.Satzlaenge)
            {
                result.Add(new GdtField(field.FieldId, "00000"));
                continue;
            }

            foreach (var chunk in SplitToMaxBytes(content, encoding))
                result.Add(new GdtField(field.FieldId, chunk));
        }

        if (result.All(f => f.FieldId != GdtFk.Satzlaenge))
        {
            var insertAt = result.FindIndex(f => f.FieldId == GdtFk.Satzidentifikation);
            result.Insert(insertAt < 0 ? 0 : insertAt + 1, new GdtField(GdtFk.Satzlaenge, "00000"));
        }

        return result;
    }

    private static IEnumerable<string> SplitToMaxBytes(string content, Encoding encoding)
    {
        if (encoding.GetByteCount(content) <= MaxContentBytes)
        {
            yield return content;
            yield break;
        }

        var current = new StringBuilder();
        foreach (var ch in content)
        {
            if (encoding.GetByteCount(current.ToString() + ch) > MaxContentBytes)
            {
                yield return current.ToString();
                current.Clear();
            }
            current.Append(ch);
        }

        if (current.Length > 0)
            yield return current.ToString();
    }

    private static void WriteLine(Stream stream, Encoding encoding, string fieldId, string content)
    {
        var contentBytes = encoding.GetBytes(content);
        var length = LineOverhead + contentBytes.Length;

        var header = Encoding.ASCII.GetBytes(length.ToString("D3") + fieldId.PadRight(4).Substring(0, 4));
        stream.Write(header, 0, header.Length);
        stream.Write(contentBytes, 0, contentBytes.Length);
        stream.WriteByte((byte)'\r');
        stream.WriteByte((byte)'\n');
    }

    /// <summary>Ersetzt Umlaute und Sonderzeichen für den 7-Bit-Zeichensatz.</summary>
    public static string Transliterate(string input)
    {
        var sb = new StringBuilder(input.Length);
        foreach (var ch in input)
        {
            sb.Append(ch switch
            {
                'ä' => "ae",
                'ö' => "oe",
                'ü' => "ue",
                'Ä' => "Ae",
                'Ö' => "Oe",
                'Ü' => "Ue",
                'ß' => "ss",
                'é' or 'è' or 'ê' => "e",
                'á' or 'à' or 'â' => "a",
                'ó' or 'ò' or 'ô' => "o",
                'ú' or 'ù' or 'û' => "u",
                'í' or 'ì' or 'î' => "i",
                'ç' => "c",
                'ñ' => "n",
                _ => ch <= 127 ? ch.ToString() : "?"
            });
        }
        return sb.ToString();
    }

    public static void WriteFile(string path, GdtRecord record, GdtCharset charset)
    {
        var bytes = Serialize(record, charset);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        // Erst in eine temporäre Datei schreiben und dann umbenennen, damit das PVS
        // niemals einen halb geschriebenen Satz einliest.
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        if (File.Exists(path))
            File.Delete(path);
        File.Move(temp, path);
    }
}

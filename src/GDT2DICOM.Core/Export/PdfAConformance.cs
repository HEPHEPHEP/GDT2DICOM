using System.Text;
using Microsoft.Extensions.Logging;
using PdfSharp.Pdf;

namespace Gdt2Dicom.Core.Export;

/// <summary>Die Angaben, die im Info-Dictionary und im XMP übereinstimmend stehen müssen.</summary>
public sealed record PdfADocumentInfo(
    string Title,
    string Author,
    string Subject,
    string Creator,
    DateTime Created);

/// <summary>
/// Schaltet ein von PdfSharp erzeugtes Dokument auf PDF/A um.
/// </summary>
/// <remarks>
/// Die eigentliche Arbeit erledigt PdfSharp 6.2 selbst: <c>SetPdfA()</c> erzeugt beim
/// Speichern das XMP-Paket mit der Konformitätskennung und einen Output Intent mit dem
/// mitgelieferten sRGB-Profil. Eigene Metadaten- oder OutputIntent-Objekte darf man deshalb
/// nicht setzen – PdfSharp legt seine beim Speichern zusätzlich an und bricht mit einem
/// Schlüsselkonflikt ab.
///
/// Zwei Dinge bleiben zu tun:
/// 1. Info-Einträge setzen, bevor PdfSharp sie ins XMP spiegelt.
/// 2. Das Interpolate-Flag der Bilder auf false ziehen; PdfSharp setzt es auf true,
///    was PDF/A nicht erlaubt.
/// </remarks>
public static class PdfAConformance
{
    /// <summary>Muss vor <c>document.Save()</c> aufgerufen werden.</summary>
    public static void Apply(PdfDocument document, PdfADocumentInfo info)
    {
        document.SetPdfA();

        // PDF/A-3 setzt auf PDF 1.7 auf.
        document.Version = 17;

        ApplyDocumentInfo(document, info);
        DisableImageInterpolation(document);
    }

    /// <summary>
    /// Korrigiert die Konformitätskennung in der gespeicherten Datei von PDF/A-1a auf 3b.
    /// </summary>
    /// <remarks>
    /// PdfSharp schreibt fest <c>pdfaid:part 1</c> und <c>conformance A</c> – Level A verlangt
    /// aber ein getaggtes PDF mit vollständigem Strukturbaum, das PdfSharp nicht erzeugt. Die
    /// Datei würde also eine Konformität behaupten, die sie nicht erfüllt. Erfüllt sind
    /// dagegen sämtliche Regeln von PDF/A-3b, und das ist die Stufe, die für die ePA zählt.
    ///
    /// Der Eingriff ist bewusst eine Ersetzung gleicher Länge (1→3, A→B): dadurch verschieben
    /// sich keine Byte-Positionen, die Querverweistabelle und die Stream-Längen der Datei
    /// bleiben gültig. Wird eines der beiden Muster nicht genau einmal gefunden, bleibt die
    /// Datei unverändert – lieber eine Datei ohne Anspruch als eine mit falschem.
    /// </remarks>
    /// <returns>true, wenn die Kennung gesetzt werden konnte.</returns>
    public static bool DeclareA3b(string path, ILogger logger)
    {
        const string partFrom = "<pdfaid:part>1</pdfaid:part>";
        const string partTo = "<pdfaid:part>3</pdfaid:part>";
        const string levelFrom = "<pdfaid:conformance>A</pdfaid:conformance>";
        const string levelTo = "<pdfaid:conformance>B</pdfaid:conformance>";

        try
        {
            var bytes = File.ReadAllBytes(path);

            if (!TryReplaceUnique(bytes, partFrom, partTo, out var reason) ||
                !TryReplaceUnique(bytes, levelFrom, levelTo, out reason))
            {
                logger.LogWarning(
                    "Die PDF/A-Kennung konnte nicht auf 3b gesetzt werden ({Reason}). Die Datei {Path} " +
                    "trägt weiterhin die Kennung von PdfSharp. Vermutlich hat sich das Format der " +
                    "PdfSharp-Metadaten geändert.", reason, path);
                return false;
            }

            File.WriteAllBytes(path, bytes);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Die PDF/A-Kennung in {Path} konnte nicht gesetzt werden.", path);
            return false;
        }
    }

    /// <summary>Ersetzt ein Muster im Puffer, aber nur wenn es genau einmal vorkommt.</summary>
    private static bool TryReplaceUnique(byte[] buffer, string from, string to, out string reason)
    {
        var needle = Encoding.UTF8.GetBytes(from);
        var replacement = Encoding.UTF8.GetBytes(to);

        if (needle.Length != replacement.Length)
        {
            reason = "Ersetzung hätte eine andere Länge";
            return false;
        }

        var first = IndexOf(buffer, needle, 0);
        if (first < 0)
        {
            reason = $"\"{from}\" nicht gefunden";
            return false;
        }

        if (IndexOf(buffer, needle, first + 1) >= 0)
        {
            reason = $"\"{from}\" kommt mehrfach vor";
            return false;
        }

        Array.Copy(replacement, 0, buffer, first, replacement.Length);
        reason = "";
        return true;
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int start)
    {
        for (var i = start; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }
            if (match)
                return i;
        }
        return -1;
    }

    private static void ApplyDocumentInfo(PdfDocument document, PdfADocumentInfo info)
    {
        document.Info.Title = info.Title;
        document.Info.Author = info.Author;
        document.Info.Subject = info.Subject;
        document.Info.Creator = info.Creator;
        document.Info.CreationDate = info.Created;

        // Producer vergibt PdfSharp selbst und spiegelt ihn in sein XMP – hier nichts tun,
        // sonst weichen Info-Dictionary und Metadaten voneinander ab.
    }

    /// <summary>
    /// PDF/A erlaubt das Interpolate-Flag nur mit dem Wert false. PdfSharp setzt es beim
    /// Einbetten von Bildern auf true, deshalb wird es hier für alle Bild-XObjects korrigiert.
    /// </summary>
    private static void DisableImageInterpolation(PdfDocument document)
    {
        foreach (var obj in document.Internals.GetAllObjects())
        {
            if (obj is not PdfDictionary dictionary)
                continue;
            if (dictionary.Elements.GetName("/Subtype") != "/Image")
                continue;

            dictionary.Elements["/Interpolate"] = new PdfBoolean(false);
        }
    }
}

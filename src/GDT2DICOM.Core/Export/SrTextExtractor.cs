using System.Globalization;
using System.Text;
using FellowOakDicom;
using FellowOakDicom.StructuredReport;
using Microsoft.Extensions.Logging;

namespace Gdt2Dicom.Core.Export;

/// <summary>
/// Liest Messwerte aus DICOM Structured Reports (z. B. Geburtshilfe-Biometrie oder
/// Gefäßmessungen) als Klartextzeilen aus, damit sie im GDT-Befund landen können.
/// </summary>
public sealed class SrTextExtractor
{
    private readonly ILogger _logger;

    public SrTextExtractor(ILogger logger) => _logger = logger;

    public static bool IsStructuredReport(DicomDataset dataset)
    {
        var sopClass = dataset.GetSingleValueOrDefault(DicomTag.SOPClassUID, string.Empty);
        if (string.IsNullOrEmpty(sopClass))
            return false;

        var uid = DicomUID.Parse(sopClass);
        return uid.StorageCategory == DicomStorageCategory.StructuredReport
               || dataset.Contains(DicomTag.ContentSequence) && dataset.Contains(DicomTag.ValueType);
    }

    /// <summary>Gibt die Inhalte des Reports als eingerückte Textzeilen zurück.</summary>
    public List<string> Extract(DicomDataset dataset)
    {
        var lines = new List<string>();

        try
        {
            var report = new DicomStructuredReport(dataset);

            var title = SafeCodeMeaning(report);
            if (!string.IsNullOrWhiteSpace(title))
                lines.Add(title);

            AppendChildren(report, lines, depth: 0);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Structured Report konnte nicht ausgewertet werden, weiche auf Rohfelder aus.");
            lines.AddRange(ExtractFallback(dataset));
        }

        return lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
    }

    private void AppendChildren(DicomContentItem item, List<string> lines, int depth)
    {
        if (depth > 12)
            return;

        IEnumerable<DicomContentItem> children;
        try
        {
            children = item.Children();
        }
        catch
        {
            return;
        }

        foreach (var child in children)
        {
            var indent = new string(' ', depth * 2);
            var label = SafeCodeMeaning(child);
            var value = FormatValue(child);

            if (!string.IsNullOrWhiteSpace(label) || !string.IsNullOrWhiteSpace(value))
            {
                lines.Add(string.IsNullOrWhiteSpace(value)
                    ? $"{indent}{label}"
                    : $"{indent}{label}: {value}");
            }

            AppendChildren(child, lines, depth + 1);
        }
    }

    private static string SafeCodeMeaning(DicomContentItem item)
    {
        try
        {
            return item.Code?.Meaning ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static string FormatValue(DicomContentItem item)
    {
        try
        {
            switch (item.Type)
            {
                case DicomValueType.Numeric:
                {
                    var measurement = item.Get<DicomMeasuredValue>();
                    if (measurement is null)
                        return "";
                    var unit = measurement.Code?.Value ?? "";
                    return $"{measurement.Value.ToString("0.###", CultureInfo.CurrentCulture)} {unit}".Trim();
                }
                case DicomValueType.Text:
                    return item.Dataset.GetSingleValueOrDefault(DicomTag.TextValue, string.Empty);
                case DicomValueType.Code:
                {
                    var code = item.Get<DicomCodeItem>();
                    return code?.Meaning ?? "";
                }
                case DicomValueType.Date:
                    return item.Dataset.GetSingleValueOrDefault(DicomTag.Date, string.Empty);
                case DicomValueType.Time:
                    return item.Dataset.GetSingleValueOrDefault(DicomTag.Time, string.Empty);
                case DicomValueType.DateTime:
                    return item.Dataset.GetSingleValueOrDefault(DicomTag.DateTime, string.Empty);
                case DicomValueType.PersonName:
                    return item.Dataset.GetSingleValueOrDefault(DicomTag.PersonName, string.Empty);
                case DicomValueType.UIDReference:
                    return item.Dataset.GetSingleValueOrDefault(DicomTag.UID, string.Empty);
                default:
                    return "";
            }
        }
        catch
        {
            return "";
        }
    }

    /// <summary>Notausgang, wenn die SR-Struktur nicht sauber lesbar ist: rohe Textwerte einsammeln.</summary>
    private static IEnumerable<string> ExtractFallback(DicomDataset dataset)
    {
        var lines = new List<string>();
        Walk(dataset, 0);
        return lines;

        void Walk(DicomDataset current, int depth)
        {
            if (depth > 10)
                return;

            var indent = new string(' ', depth * 2);
            var name = "";
            if (current.TryGetSequence(DicomTag.ConceptNameCodeSequence, out var concept) && concept.Items.Count > 0)
                name = concept.Items[0].GetSingleValueOrDefault(DicomTag.CodeMeaning, string.Empty);

            var text = current.GetSingleValueOrDefault(DicomTag.TextValue, string.Empty);

            if (!string.IsNullOrWhiteSpace(text))
                lines.Add($"{indent}{name}{(string.IsNullOrWhiteSpace(name) ? "" : ": ")}{text}".TrimEnd());

            if (current.TryGetSequence(DicomTag.ContentSequence, out var sequence))
                foreach (var item in sequence.Items)
                    Walk(item, depth + 1);
        }
    }

    /// <summary>Fasst mehrere Reports zu einem Textblock zusammen.</summary>
    public static string Join(IEnumerable<IEnumerable<string>> reports)
    {
        var sb = new StringBuilder();
        foreach (var report in reports)
        {
            if (sb.Length > 0)
                sb.AppendLine();
            foreach (var line in report)
                sb.AppendLine(line);
        }
        return sb.ToString().TrimEnd();
    }
}

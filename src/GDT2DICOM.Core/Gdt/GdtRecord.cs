using System.Text;

namespace Gdt2Dicom.Core.Gdt;

/// <summary>Eine GDT-Zeile: vierstellige Feldkennung plus Inhalt.</summary>
public sealed record GdtField(string FieldId, string Content)
{
    public override string ToString() => $"{FieldId} {Content}";
}

/// <summary>Ein GDT-Satz, also der Inhalt einer GDT-Datei, als geordnete Feldliste.</summary>
public sealed class GdtRecord
{
    public List<GdtField> Fields { get; } = new();

    /// <summary>Ursprüngliche Datei, aus der der Satz gelesen wurde (nur informativ).</summary>
    public string? SourcePath { get; set; }

    public string Satzart => Get(GdtFk.Satzidentifikation) ?? "";

    public GdtRecord Add(string fieldId, string? content)
    {
        if (content is null)
            return this;
        Fields.Add(new GdtField(fieldId, content));
        return this;
    }

    /// <summary>Fügt das Feld nur hinzu, wenn Inhalt vorhanden ist.</summary>
    public GdtRecord AddIfSet(string fieldId, string? content)
    {
        if (!string.IsNullOrWhiteSpace(content))
            Fields.Add(new GdtField(fieldId, content));
        return this;
    }

    public string? Get(string fieldId) =>
        Fields.FirstOrDefault(f => f.FieldId == fieldId)?.Content;

    public string GetOrEmpty(string fieldId) => Get(fieldId) ?? "";

    public IEnumerable<string> GetAll(string fieldId) =>
        Fields.Where(f => f.FieldId == fieldId).Select(f => f.Content);

    /// <summary>Alle Werte eines Feldes zu einem Text zusammengefügt (für mehrzeilige Befunde).</summary>
    public string GetJoined(string fieldId, string separator = "\n") =>
        string.Join(separator, GetAll(fieldId));

    public bool Has(string fieldId) => Fields.Any(f => f.FieldId == fieldId);

    public void Remove(string fieldId) => Fields.RemoveAll(f => f.FieldId == fieldId);

    public override string ToString()
    {
        var sb = new StringBuilder();
        foreach (var f in Fields)
            sb.AppendLine($"{f.FieldId}  {f.Content}");
        return sb.ToString();
    }
}

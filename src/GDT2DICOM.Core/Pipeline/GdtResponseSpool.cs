using System.Text.Json;
using Gdt2Dicom.Core.Configuration;
using Gdt2Dicom.Core.Gdt;
using Microsoft.Extensions.Logging;

namespace Gdt2Dicom.Core.Pipeline;

/// <summary>Ergebnis eines Abrufversuchs durch das PVS.</summary>
public sealed record GdtFetchResult(
    bool Delivered,
    string? FileName = null,
    string? PatientName = null,
    string? PatientId = null,
    int Remaining = 0,
    string? Error = null);

/// <summary>Ein fertiger Rücksatz, der auf die Auslieferung ins Ausgangsverzeichnis wartet.</summary>
public sealed class SpooledResponse
{
    public string Id { get; set; } = "";
    public string PatientId { get; set; } = "";
    public string PatientName { get; set; } = "";
    public string TargetFileName { get; set; } = "";
    public DateTime CreatedUtc { get; set; }

    /// <summary>Pfad der zwischengespeicherten Datei. Nicht Teil der Metadaten auf der Platte.</summary>
    public string SpoolPath { get; set; } = "";
}

/// <summary>
/// Zwischenlager für fertige GDT-Rücksätze.
/// </summary>
/// <remarks>
/// Es gibt zwei Gründe, einen Rücksatz nicht sofort ins Ausgangsverzeichnis zu legen:
///
/// 1. Viele PVS lesen das Importverzeichnis ausschließlich unmittelbar nach dem Ende eines
///    aufgerufenen Fremdprogramms. Dann muss der Rücksatz genau dann dort erscheinen und
///    nicht vorher.
/// 2. Erwartet das PVS einen festen Dateinamen, würde eine zweite fertige Untersuchung die
///    erste überschreiben, bevor sie abgeholt wurde – ein Befund ginge verloren.
///
/// Deshalb entsteht jeder Rücksatz zuerst hier und wandert erst dann in den Ausgang, wenn
/// der Platz frei ist beziehungsweise das PVS ihn abruft.
/// </remarks>
public sealed class GdtResponseSpool
{
    private const string ContentExtension = ".gdt";
    private const string MetaExtension = ".json";

    private readonly string _directory;
    private readonly ILogger _logger;
    private readonly object _lock = new();

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public GdtResponseSpool(string dataDirectory, ILogger logger)
    {
        _logger = logger;
        _directory = Path.Combine(dataDirectory, "rueckstau");
        Directory.CreateDirectory(_directory);
    }

    public string Directory_ => _directory;

    /// <summary>Legt einen fertigen Rücksatz ins Zwischenlager.</summary>
    public SpooledResponse Enqueue(byte[] content, string patientId, string patientName, string targetFileName)
    {
        lock (_lock)
        {
            var id = $"{DateTime.UtcNow:yyyyMMddHHmmssfff}_{GdtValues.SafeFileToken(patientId, "0")}";
            var spoolPath = Path.Combine(_directory, id + ContentExtension);

            var entry = new SpooledResponse
            {
                Id = id,
                PatientId = patientId,
                PatientName = patientName,
                TargetFileName = targetFileName,
                CreatedUtc = DateTime.UtcNow,
                SpoolPath = spoolPath
            };

            File.WriteAllBytes(spoolPath, content);
            File.WriteAllText(Path.Combine(_directory, id + MetaExtension),
                JsonSerializer.Serialize(entry, JsonOptions));

            return entry;
        }
    }

    /// <summary>Alle wartenden Rücksätze, älteste zuerst.</summary>
    public IReadOnlyList<SpooledResponse> Pending()
    {
        lock (_lock)
        {
            var result = new List<SpooledResponse>();

            foreach (var meta in System.IO.Directory.EnumerateFiles(_directory, "*" + MetaExtension))
            {
                try
                {
                    var entry = JsonSerializer.Deserialize<SpooledResponse>(File.ReadAllText(meta));
                    if (entry is null)
                        continue;

                    entry.SpoolPath = Path.Combine(_directory, entry.Id + ContentExtension);
                    if (File.Exists(entry.SpoolPath))
                        result.Add(entry);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Eintrag im Rückstau {File} ist unlesbar.", meta);
                }
            }

            return result.OrderBy(r => r.CreatedUtc).ToList();
        }
    }

    /// <summary>
    /// Stellt einen wartenden Rücksatz in den Ausgang. Gibt false zurück, wenn der Platz
    /// noch belegt ist – dann bleibt der Eintrag liegen und wird später erneut versucht.
    /// </summary>
    public bool TryDeliver(SpooledResponse entry, GdtConfig gdt, out string? deliveredPath, out string reason)
    {
        deliveredPath = null;
        reason = "";

        lock (_lock)
        {
            try
            {
                System.IO.Directory.CreateDirectory(gdt.OutboxDirectory);
                var target = Path.Combine(gdt.OutboxDirectory, entry.TargetFileName);

                if (gdt.HoldBackWhileOutboxOccupied && File.Exists(target))
                {
                    reason = $"{entry.TargetFileName} liegt noch im Ausgang – vom PVS noch nicht abgeholt.";
                    return false;
                }

                // Über eine temporäre Datei, damit das PVS niemals einen halb geschriebenen
                // Rücksatz einliest.
                var temp = target + ".tmp";
                File.Copy(entry.SpoolPath, temp, overwrite: true);
                if (File.Exists(target))
                    File.Delete(target);
                File.Move(temp, target);

                Remove(entry);

                deliveredPath = target;
                return true;
            }
            catch (Exception ex)
            {
                reason = ex.Message;
                _logger.LogError(ex, "Rücksatz {Id} konnte nicht ausgeliefert werden.", entry.Id);
                return false;
            }
        }
    }

    /// <summary>Liefert alles aus, was gerade ausgeliefert werden kann. Für den Sofort-Modus.</summary>
    public int DeliverAll(GdtConfig gdt)
    {
        var delivered = 0;

        foreach (var entry in Pending())
        {
            if (!TryDeliver(entry, gdt, out var path, out var reason))
            {
                // Bei belegtem Platz hat es keinen Sinn, den nächsten zu versuchen: er trüge
                // denselben Namen. Bei fortlaufenden Namen greift die Bedingung ohnehin nicht.
                _logger.LogDebug("Rücksatz {Id} wartet weiter: {Grund}", entry.Id, reason);
                continue;
            }

            delivered++;
            _logger.LogInformation("Rücksatz für {Patient} ausgeliefert: {Pfad}", entry.PatientName, path);
        }

        return delivered;
    }

    /// <summary>
    /// Sucht den nächsten Rücksatz für einen Patienten. Ohne Patientennummer wird der
    /// älteste wartende genommen – in einer Einzelpraxis ist das der richtige.
    /// </summary>
    public SpooledResponse? Next(string? patientId)
    {
        var pending = Pending();

        if (string.IsNullOrWhiteSpace(patientId))
            return pending.FirstOrDefault();

        return pending.FirstOrDefault(p =>
            string.Equals(p.PatientId?.Trim(), patientId.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public void Remove(SpooledResponse entry)
    {
        foreach (var path in new[]
                 {
                     entry.SpoolPath,
                     Path.Combine(_directory, entry.Id + MetaExtension)
                 })
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Rückstau-Datei {Path} konnte nicht entfernt werden.", path);
            }
        }
    }

    public int Count => Pending().Count;
}

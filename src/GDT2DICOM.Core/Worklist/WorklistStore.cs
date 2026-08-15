using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Gdt2Dicom.Core.Worklist;

/// <summary>
/// Dateibasierter, threadsicherer Speicher für Worklist-Einträge. Bewusst ohne Datenbank:
/// eine Praxis hat pro Tag eine zweistellige Anzahl Einträge, und eine JSON-Datei überlebt
/// jeden Rechnerwechsel ohne Migrationsaufwand.
/// </summary>
public sealed class WorklistStore
{
    private readonly string _filePath;
    private readonly ILogger _logger;
    private readonly List<WorklistItem> _items = new();
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.SupportsRecursion);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public WorklistStore(string dataDirectory, ILogger logger)
    {
        _logger = logger;
        Directory.CreateDirectory(dataDirectory);
        _filePath = Path.Combine(dataDirectory, "worklist.json");
        Load();
    }

    public event Action? Changed;

    private void Load()
    {
        if (!File.Exists(_filePath))
            return;

        try
        {
            var items = JsonSerializer.Deserialize<List<WorklistItem>>(File.ReadAllText(_filePath), JsonOptions);
            if (items is not null)
                _items.AddRange(items);
            _logger.LogInformation("Worklist geladen: {Count} Einträge.", _items.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Worklist konnte nicht geladen werden, starte mit leerer Liste.");
        }
    }

    private void PersistUnsafe()
    {
        try
        {
            var temp = _filePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_items, JsonOptions));
            if (File.Exists(_filePath))
                File.Delete(_filePath);
            File.Move(temp, _filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Worklist konnte nicht gespeichert werden.");
        }
    }

    public IReadOnlyList<WorklistItem> GetAll()
    {
        _lock.EnterReadLock();
        try
        {
            return _items.ToList();
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>Einträge, die für eine C-FIND-Antwort in Frage kommen.</summary>
    public IReadOnlyList<WorklistItem> GetSchedulable()
    {
        _lock.EnterReadLock();
        try
        {
            return _items
                .Where(i => i.State is WorklistItemState.Scheduled or WorklistItemState.InProgress)
                .OrderBy(i => i.ScheduledDate)
                .ThenBy(i => i.ScheduledTime)
                .ToList();
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public void Add(WorklistItem item)
    {
        _lock.EnterWriteLock();
        try
        {
            _items.Add(item);
            PersistUnsafe();
        }
        finally
        {
            _lock.ExitWriteLock();
        }

        _logger.LogInformation("Worklist-Eintrag angelegt: {Item}", item);
        Changed?.Invoke();
    }

    /// <summary>Ändert einen Eintrag unter Sperre. Gibt false zurück, wenn er nicht mehr existiert.</summary>
    public bool Update(string id, Action<WorklistItem> mutate)
    {
        _lock.EnterWriteLock();
        try
        {
            var item = _items.FirstOrDefault(i => i.Id == id);
            if (item is null)
                return false;

            mutate(item);
            item.UpdatedUtc = DateTime.UtcNow;
            PersistUnsafe();
        }
        finally
        {
            _lock.ExitWriteLock();
        }

        Changed?.Invoke();
        return true;
    }

    public bool Remove(string id)
    {
        bool removed;
        _lock.EnterWriteLock();
        try
        {
            removed = _items.RemoveAll(i => i.Id == id) > 0;
            if (removed)
                PersistUnsafe();
        }
        finally
        {
            _lock.ExitWriteLock();
        }

        if (removed)
            Changed?.Invoke();
        return removed;
    }

    public WorklistItem? FindById(string id) => GetAll().FirstOrDefault(i => i.Id == id);

    public WorklistItem? FindByStudyUid(string studyInstanceUid) =>
        string.IsNullOrWhiteSpace(studyInstanceUid)
            ? null
            : GetAll().FirstOrDefault(i => i.StudyInstanceUid == studyInstanceUid);

    public WorklistItem? FindByAccession(string accessionNumber) =>
        string.IsNullOrWhiteSpace(accessionNumber)
            ? null
            : GetAll().FirstOrDefault(i => string.Equals(i.AccessionNumber, accessionNumber, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Sucht den passenden Auftrag zu einer empfangenen Studie. Reihenfolge: Study Instance UID,
    /// Accession Number, MPPS-UID, zuletzt Patienten-ID mit dem jüngsten offenen Auftrag.
    /// </summary>
    public WorklistItem? Match(string? studyInstanceUid, string? accessionNumber, string? patientId, string? mppsUid = null)
    {
        var all = GetAll();

        if (!string.IsNullOrWhiteSpace(studyInstanceUid))
        {
            var byStudy = all.FirstOrDefault(i => i.StudyInstanceUid == studyInstanceUid);
            if (byStudy is not null)
                return byStudy;
        }

        if (!string.IsNullOrWhiteSpace(accessionNumber))
        {
            var byAccession = all.FirstOrDefault(i =>
                string.Equals(i.AccessionNumber, accessionNumber, StringComparison.OrdinalIgnoreCase));
            if (byAccession is not null)
                return byAccession;
        }

        if (!string.IsNullOrWhiteSpace(mppsUid))
        {
            var byMpps = all.FirstOrDefault(i => i.MppsSopInstanceUid == mppsUid);
            if (byMpps is not null)
                return byMpps;
        }

        if (!string.IsNullOrWhiteSpace(patientId))
        {
            return all
                .Where(i => string.Equals(i.PatientId, patientId, StringComparison.OrdinalIgnoreCase)
                            && i.State != WorklistItemState.Exported)
                .OrderByDescending(i => i.CreatedUtc)
                .FirstOrDefault();
        }

        return null;
    }

    /// <summary>Entfernt abgelaufene Einträge. Gibt die Anzahl entfernter Einträge zurück.</summary>
    public int Purge(TimeSpan lifetime)
    {
        var cutoff = DateTime.UtcNow - lifetime;
        int removed;

        _lock.EnterWriteLock();
        try
        {
            removed = _items.RemoveAll(i => i.CreatedUtc < cutoff);
            if (removed > 0)
                PersistUnsafe();
        }
        finally
        {
            _lock.ExitWriteLock();
        }

        if (removed > 0)
        {
            _logger.LogInformation("{Count} abgelaufene Worklist-Einträge entfernt.", removed);
            Changed?.Invoke();
        }

        return removed;
    }

    public int Count => GetAll().Count;
}

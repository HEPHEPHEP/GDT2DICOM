using FellowOakDicom;
using Gdt2Dicom.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace Gdt2Dicom.Core.Pipeline;

/// <summary>Eine Untersuchung, deren Objekte gerade eintreffen.</summary>
public sealed class PendingStudy
{
    public required string StudyInstanceUid { get; init; }
    public List<string> Files { get; } = new();
    public DateTime FirstInstanceUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastInstanceUtc { get; set; } = DateTime.UtcNow;

    public string PatientId { get; set; } = "";
    public string PatientName { get; set; } = "";
    public string PatientBirthDate { get; set; } = "";
    public string PatientSex { get; set; } = "";
    public string AccessionNumber { get; set; } = "";
    public string StudyDate { get; set; } = "";
    public string StudyTime { get; set; } = "";
    public string Modality { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string CallingAeTitle { get; set; } = "";
    public string? MppsSopInstanceUid { get; set; }

    /// <summary>
    /// Das Gerät hat für diese Untersuchung MPPS „IN PROGRESS“ gemeldet und damit angekündigt,
    /// dass es das Ende selbst mitteilt. Solange das gilt, greift die Ruhezeit nicht – eine
    /// Messpause von mehreren Minuten darf eine laufende Untersuchung nicht zerreißen.
    /// </summary>
    public bool MppsInProgress { get; set; }

    public bool ForceFinalize { get; set; }

    public override string ToString() => $"{StudyInstanceUid} ({Files.Count} Objekte, {PatientName})";
}

/// <summary>
/// Sammelt eingehende DICOM-Objekte zu Untersuchungen. Ein Sonogerät sendet Bilder einzeln,
/// oft über mehrere Associations verteilt – deshalb wird eine Studie erst abgeschlossen,
/// wenn eine Weile nichts mehr kommt oder das Gerät per MPPS „fertig“ meldet.
/// </summary>
public sealed class StudyCollector : IAsyncDisposable
{
    private readonly Func<AppConfig> _config;
    private readonly Func<PendingStudy, Task> _finalize;
    private readonly ILogger _logger;

    private readonly Dictionary<string, PendingStudy> _studies = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    private readonly SemaphoreSlim _finalizeLock = new(1, 1);

    /// <summary>
    /// MPPS-Meldungen, die eintrafen, bevor das erste Bild da war. Sie hier zu behalten ist
    /// nötig, weil MPPS und Bilder über getrennte Associations laufen und die Reihenfolge
    /// nicht garantiert ist. Früher wurde eine solche Meldung stillschweigend verworfen; mit
    /// ausgeschalteter Ruhezeit bliebe die Untersuchung dann bis zur harten Obergrenze liegen.
    /// </summary>
    private readonly Dictionary<string, VorgemerktesMpps> _vorgemerkteMpps = new(StringComparer.OrdinalIgnoreCase);

    private sealed record VorgemerktesMpps(string? MppsUid, bool Abgeschlossen, DateTime EingegangenUtc);

    private CancellationTokenSource? _cts;
    private Task? _timerTask;

    public StudyCollector(Func<AppConfig> config, Func<PendingStudy, Task> finalize, ILogger logger)
    {
        _config = config;
        _finalize = finalize;
        _logger = logger;
    }

    public void Start()
    {
        Stop();
        _cts = new CancellationTokenSource();
        _timerTask = Task.Run(() => LoopAsync(_cts.Token));
    }

    public void Stop()
    {
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public int PendingCount
    {
        get
        {
            lock (_lock)
            {
                return _studies.Count;
            }
        }
    }

    public IReadOnlyList<PendingStudy> Snapshot()
    {
        lock (_lock)
        {
            return _studies.Values.ToList();
        }
    }

    public void AddInstance(DicomFile file, string storedPath, string callingAeTitle)
    {
        var ds = file.Dataset;
        var studyUid = ds.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, string.Empty);

        if (string.IsNullOrWhiteSpace(studyUid))
        {
            // Ohne Study Instance UID lässt sich nichts gruppieren – dann ist jedes Objekt
            // seine eigene "Studie", damit es wenigstens nicht verloren geht.
            studyUid = "ohne-uid-" + Guid.NewGuid().ToString("N");
        }

        lock (_lock)
        {
            if (!_studies.TryGetValue(studyUid, out var study))
            {
                study = new PendingStudy { StudyInstanceUid = studyUid };
                _studies[studyUid] = study;
                _logger.LogInformation("Neue Untersuchung wird gesammelt: {Uid}", studyUid);
            }

            study.Files.Add(storedPath);
            study.LastInstanceUtc = DateTime.UtcNow;
            study.CallingAeTitle = callingAeTitle;

            // Kopfdaten aus dem ersten Objekt übernehmen, das sie liefert.
            study.PatientId = Prefer(study.PatientId, ds.GetSingleValueOrDefault(DicomTag.PatientID, string.Empty));
            study.PatientName = Prefer(study.PatientName, ds.GetSingleValueOrDefault(DicomTag.PatientName, string.Empty));
            study.PatientBirthDate = Prefer(study.PatientBirthDate, ds.GetSingleValueOrDefault(DicomTag.PatientBirthDate, string.Empty));
            study.PatientSex = Prefer(study.PatientSex, ds.GetSingleValueOrDefault(DicomTag.PatientSex, string.Empty));
            study.AccessionNumber = Prefer(study.AccessionNumber, ds.GetSingleValueOrDefault(DicomTag.AccessionNumber, string.Empty));
            study.StudyDate = Prefer(study.StudyDate, ds.GetSingleValueOrDefault(DicomTag.StudyDate, string.Empty));
            study.StudyTime = Prefer(study.StudyTime, ds.GetSingleValueOrDefault(DicomTag.StudyTime, string.Empty));
            study.Modality = Prefer(study.Modality, ds.GetSingleValueOrDefault(DicomTag.Modality, string.Empty));
            study.DeviceName = Prefer(study.DeviceName, ds.GetSingleValueOrDefault(DicomTag.ManufacturerModelName, string.Empty));

            // Erst hier, weil die Accession Number oben aus dem Objekt kommt und eine
            // vorgemerkte Meldung womöglich nur darüber zuzuordnen ist.
            UebernehmeVorgemerktesUnsafe(study);
        }
    }

    /// <summary>
    /// Meldet, dass das Gerät die Untersuchung per MPPS begonnen hat. Ab da übernimmt das
    /// Gerät die Entscheidung, wann Schluss ist – die Ruhezeit greift nicht mehr.
    /// </summary>
    public void MarkInProgress(string? studyInstanceUid, string? accessionNumber, string? mppsUid) =>
        VermerkeMpps(studyInstanceUid, accessionNumber, mppsUid, abgeschlossen: false);

    /// <summary>Meldet, dass das Gerät die Untersuchung per MPPS abgeschlossen hat.</summary>
    public void MarkCompleted(string? studyInstanceUid, string? accessionNumber, string? mppsUid) =>
        VermerkeMpps(studyInstanceUid, accessionNumber, mppsUid, abgeschlossen: true);

    private void VermerkeMpps(string? studyInstanceUid, string? accessionNumber, string? mppsUid, bool abgeschlossen)
    {
        lock (_lock)
        {
            var study = FindUnsafe(studyInstanceUid, accessionNumber);

            if (study is null)
            {
                // Bilder noch nicht da – Meldung aufheben, bis die Untersuchung auftaucht.
                foreach (var schluessel in Schluessel(studyInstanceUid, accessionNumber))
                    _vorgemerkteMpps[schluessel] = new VorgemerktesMpps(mppsUid, abgeschlossen, DateTime.UtcNow);

                _logger.LogInformation(
                    "MPPS {Status} für eine Untersuchung, zu der noch kein Objekt vorliegt – vorgemerkt.",
                    abgeschlossen ? "COMPLETED" : "IN PROGRESS");
                return;
            }

            AnwendenUnsafe(study, mppsUid, abgeschlossen);
        }
    }

    private void AnwendenUnsafe(PendingStudy study, string? mppsUid, bool abgeschlossen)
    {
        if (!string.IsNullOrWhiteSpace(mppsUid))
            study.MppsSopInstanceUid = mppsUid;

        if (abgeschlossen)
        {
            study.ForceFinalize = true;
            _logger.LogInformation("Untersuchung {Uid} per MPPS als abgeschlossen gemeldet.", study.StudyInstanceUid);
        }
        else
        {
            study.MppsInProgress = true;
            _logger.LogInformation(
                "Untersuchung {Uid} läuft laut MPPS – die Ruhezeit greift bis zur Abschlussmeldung nicht.",
                study.StudyInstanceUid);
        }
    }

    private void UebernehmeVorgemerktesUnsafe(PendingStudy study)
    {
        foreach (var schluessel in Schluessel(study.StudyInstanceUid, study.AccessionNumber))
        {
            if (!_vorgemerkteMpps.TryGetValue(schluessel, out var vermerk))
                continue;

            _vorgemerkteMpps.Remove(schluessel);
            AnwendenUnsafe(study, vermerk.MppsUid, vermerk.Abgeschlossen);
        }
    }

    /// <summary>
    /// Study Instance UID und Accession Number als Schlüssel. Beides, weil eine MPPS-Meldung
    /// mal das eine und mal das andere zuverlässig mitbringt.
    /// </summary>
    private static IEnumerable<string> Schluessel(string? studyInstanceUid, string? accessionNumber)
    {
        if (!string.IsNullOrWhiteSpace(studyInstanceUid))
            yield return "S:" + studyInstanceUid;

        if (!string.IsNullOrWhiteSpace(accessionNumber))
            yield return "A:" + accessionNumber;
    }

    private PendingStudy? FindUnsafe(string? studyInstanceUid, string? accessionNumber)
    {
        if (!string.IsNullOrWhiteSpace(studyInstanceUid) && _studies.TryGetValue(studyInstanceUid, out var byUid))
            return byUid;

        if (!string.IsNullOrWhiteSpace(accessionNumber))
            return _studies.Values.FirstOrDefault(s =>
                string.Equals(s.AccessionNumber, accessionNumber, StringComparison.OrdinalIgnoreCase));

        return null;
    }

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), token);
                await CheckDueStudiesAsync();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fehler beim Prüfen offener Untersuchungen.");
            }
        }
    }

    private async Task CheckDueStudiesAsync()
    {
        var config = _config().Export;

        // 0 heißt aus. Der Wert wird sonst nach unten begrenzt, weil diese Schleife ohnehin
        // nur alle drei Sekunden läuft – eine kürzere Ruhezeit wäre eine leere Zusage.
        var idleAktiv = config.StudyIdleTimeoutSeconds > 0;
        var idle = TimeSpan.FromSeconds(Math.Max(3, config.StudyIdleTimeoutSeconds));
        var maxAge = TimeSpan.FromMinutes(Math.Max(1, config.StudyMaxAgeMinutes));
        var now = DateTime.UtcNow;

        List<PendingStudy> due;
        lock (_lock)
        {
            // Die Ruhezeit ruht, solange das Gerät die Untersuchung per MPPS als laufend
            // führt – dann kommt die Abschlussmeldung vom Gerät. Das gilt nur, wenn auf diese
            // Meldung überhaupt reagiert wird; sonst bliebe die Untersuchung sonst liegen,
            // bis die harte Obergrenze greift.
            due = _studies.Values
                .Where(s => (config.FinalizeOnMppsCompleted && s.ForceFinalize)
                            || (idleAktiv
                                && !(config.FinalizeOnMppsCompleted && s.MppsInProgress)
                                && now - s.LastInstanceUtc >= idle)
                            || now - s.FirstInstanceUtc >= maxAge)
                .ToList();

            foreach (var study in due)
                _studies.Remove(study.StudyInstanceUid);

            // Vermerke, zu denen nie eine Untersuchung kam, nicht ewig aufheben.
            foreach (var alt in _vorgemerkteMpps
                         .Where(v => now - v.Value.EingegangenUtc >= maxAge)
                         .Select(v => v.Key)
                         .ToList())
                _vorgemerkteMpps.Remove(alt);
        }

        foreach (var study in due)
        {
            await _finalizeLock.WaitAsync();
            try
            {
                _logger.LogInformation("Schließe Untersuchung ab: {Study}", study);
                await _finalize(study);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Untersuchung {Uid} konnte nicht abgeschlossen werden.", study.StudyInstanceUid);
            }
            finally
            {
                _finalizeLock.Release();
            }
        }
    }

    /// <summary>Schließt alle offenen Untersuchungen sofort ab – für einen sauberen Dienststopp.</summary>
    public async Task FlushAsync()
    {
        List<PendingStudy> all;
        lock (_lock)
        {
            all = _studies.Values.ToList();
            _studies.Clear();
        }

        foreach (var study in all)
        {
            try
            {
                await _finalize(study);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Untersuchung {Uid} konnte beim Herunterfahren nicht abgeschlossen werden.",
                    study.StudyInstanceUid);
            }
        }
    }

    private static string Prefer(string existing, string candidate) =>
        string.IsNullOrWhiteSpace(existing) ? candidate?.Trim() ?? "" : existing;

    public async ValueTask DisposeAsync()
    {
        Stop();

        if (_timerTask is not null)
        {
            try
            {
                await _timerTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch
            {
                // Beim Herunterfahren nicht weiter relevant.
            }
        }

        _cts?.Dispose();
        _finalizeLock.Dispose();
    }
}

using FellowOakDicom;
using Gdt2Dicom.Core.Configuration;
using Gdt2Dicom.Core.Dicom;
using Gdt2Dicom.Core.Export;
using Gdt2Dicom.Core.Pipeline;
using Gdt2Dicom.Core.Worklist;
using Microsoft.Extensions.Logging;

namespace Gdt2Dicom.Core.Runtime;

/// <summary>
/// Klammert alle Bausteine zusammen: DICOM-Server, GDT-Überwachung, Studien-Sammler und
/// Export. Wird sowohl vom Windows-Dienst als auch vom Konsolen-Testmodus verwendet.
/// </summary>
public sealed class MiddlewareHost : IDicomEventSink, IAsyncDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;

    private AppConfig _config;
    private readonly object _configLock = new();

    private WorklistStore _worklist = null!;
    private CounterStore _counters = null!;
    private GdtResponseSpool _spool = null!;
    private DicomServerHost _dicomServer = null!;
    private GdtInboxWatcher _gdtWatcher = null!;
    private StudyCollector _collector = null!;
    private StudyFinalizer _finalizer = null!;

    public RuntimeStatus Status { get; } = new();

    public MiddlewareHost(AppConfig config, ILoggerFactory loggerFactory)
    {
        _config = config;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger("Middleware");
    }

    public AppConfig Config
    {
        get
        {
            lock (_configLock)
            {
                return _config;
            }
        }
    }

    public WorklistStore Worklist => _worklist;

    public IReadOnlyList<PendingStudy> PendingStudies => _collector?.Snapshot() ?? Array.Empty<PendingStudy>();

    /// <summary>
    /// Verarbeitet eine Auftragsdatei auf Anforderung. Ist kein Pfad angegeben, wird die
    /// neueste passende Datei im Eingangsverzeichnis genommen – manche PVS rufen das
    /// Fremdprogramm ohne Argument auf und verlassen sich auf feste Verzeichnisse.
    /// </summary>
    public async Task<GdtProcessResult> ProcessGdtFileAsync(string? path, DateTime sinceUtc)
    {
        if (_gdtWatcher is null)
            return GdtProcessResult.Failed("Die Middleware ist noch nicht bereit.");

        var gdt = Config.Gdt;

        if (string.IsNullOrWhiteSpace(path))
        {
            var newest = Directory.Exists(gdt.InboxDirectory)
                ? new DirectoryInfo(gdt.InboxDirectory)
                    .GetFiles(gdt.InboxFilePattern)
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .FirstOrDefault()
                : null;

            if (newest is null)
            {
                // Kein Auftrag da – vielleicht hat ihn die Verzeichnisüberwachung gerade
                // weggearbeitet, während das PVS das Programm startete.
                var recent = _gdtWatcher.FindRecent(null, sinceUtc);
                if (recent is not null)
                    return recent;

                if (!Directory.Exists(gdt.InboxDirectory))
                    return GdtProcessResult.Failed($"Das Eingangsverzeichnis {gdt.InboxDirectory} existiert nicht.");

                var hinweis = gdt.EnableInboxWatcher
                    ? "\n\nMöglich ist auch, dass die Verzeichnisüberwachung den Auftrag bereits " +
                      "verarbeitet hat – dann steht der Patient schon in der Worklist. Wird die " +
                      "Schnittstelle ausschließlich über diesen Programmaufruf bedient, lässt sich " +
                      "die Überwachung unter PVS/GDT abschalten."
                    : "";

                return GdtProcessResult.Failed(
                    $"Im Eingangsverzeichnis {gdt.InboxDirectory} liegt keine Datei nach dem " +
                    $"Muster {gdt.InboxFilePattern}.{hinweis}");
            }

            path = newest.FullName;
        }
        else if (!File.Exists(path))
        {
            var recent = _gdtWatcher.FindRecent(path, sinceUtc);
            if (recent is not null)
                return recent;

            return GdtProcessResult.Failed($"Die Datei {path} existiert nicht.");
        }

        var result = await _gdtWatcher.ProcessNowAsync(path);

        // Die Überwachung hat die Datei gerade in Arbeit: kurz auf ihr Ergebnis warten,
        // statt dem PVS einen Fehler zu melden.
        if (result.AlreadyRunning)
            result = await AwaitConcurrentProcessingAsync(path, sinceUtc);

        return result;
    }

    /// <summary>Liefert im Sofort-Modus aus, was ausgeliefert werden kann.</summary>
    private void DeliverPendingResponses()
    {
        if (_spool is null || Config.Gdt.ResponseDelivery != ResponseDelivery.Sofort)
            return;

        if (_spool.Count > 0)
            _spool.DeliverAll(Config.Gdt);
    }

    /// <summary>
    /// Holt den nächsten bereitliegenden Rücksatz in den Ausgang – der zweite Aufruf, den
    /// das PVS nach der Untersuchung absetzt.
    /// </summary>
    public GdtFetchResult FetchResponse(string? patientId)
    {
        if (_spool is null)
            return new GdtFetchResult(false, Error: "Die Middleware ist noch nicht bereit.");

        var entry = _spool.Next(patientId);

        if (entry is null)
        {
            var wartend = _spool.Count;
            var hinweis = string.IsNullOrWhiteSpace(patientId)
                ? "Es liegt kein Rücksatz bereit."
                : $"Für Patient {patientId} liegt kein Rücksatz bereit.";

            if (wartend > 0)
                hinweis += $" Im Rückstau warten {wartend} Rücksätze für andere Patienten.";

            return new GdtFetchResult(false, Remaining: wartend, Error: hinweis);
        }

        if (!_spool.TryDeliver(entry, Config.Gdt, out var path, out var reason))
            return new GdtFetchResult(false, Remaining: _spool.Count, Error: reason);

        _logger.LogInformation("Rücksatz für {Patient} auf Abruf ausgeliefert: {Pfad}", entry.PatientName, path);

        return new GdtFetchResult(true, Path.GetFileName(path), entry.PatientName, entry.PatientId, _spool.Count);
    }

    /// <summary>Anzahl der Rücksätze, die auf Auslieferung warten.</summary>
    public int PendingResponseCount => _spool?.Count ?? 0;

    private async Task<GdtProcessResult> AwaitConcurrentProcessingAsync(string path, DateTime sinceUtc)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(200);

            var recent = _gdtWatcher.FindRecent(path, sinceUtc);
            if (recent is not null)
                return recent;
        }

        return GdtProcessResult.Failed(
            "Die Auftragsdatei wird bereits verarbeitet, das Ergebnis steht aber noch aus.");
    }

    // -----------------------------------------------------------------------
    // Lebenszyklus
    // -----------------------------------------------------------------------

    public void Start()
    {
        var config = Config;

        DicomServerHost.EnsureFoDicomSetup(_loggerFactory);

        var problems = ConfigStore.EnsureConfiguredDirectories(config);
        foreach (var problem in problems)
            _logger.LogWarning("Verzeichnis nicht verfügbar – {Problem}", problem);

        _worklist = new WorklistStore(config.General.DataDirectory, _loggerFactory.CreateLogger("Worklist"));
        _counters = new CounterStore(config.General.DataDirectory);

        var imageExporter = new ImageExporter(_loggerFactory.CreateLogger("Bildexport"));
        var srExtractor = new SrTextExtractor(_loggerFactory.CreateLogger("SR"));
        var pdfBuilder = new PdfReportBuilder(_loggerFactory.CreateLogger("PDF"));
        _spool = new GdtResponseSpool(config.General.DataDirectory, _loggerFactory.CreateLogger("Rückstau"));
        var gdtWriter = new GdtResponseWriter(_counters, _spool, Status, _loggerFactory.CreateLogger("GDT-Ausgang"));

        _finalizer = new StudyFinalizer(() => Config, _worklist, imageExporter, srExtractor, pdfBuilder,
            gdtWriter, Status, _loggerFactory.CreateLogger("Export"));

        _collector = new StudyCollector(() => Config, study => _finalizer.FinalizeAsync(study),
            _loggerFactory.CreateLogger("Studien"));

        var requestProcessor = new GdtRequestProcessor(() => Config, _worklist, _counters, Status,
            _loggerFactory.CreateLogger("GDT-Eingang"));

        _gdtWatcher = new GdtInboxWatcher(() => Config, requestProcessor, Status,
            _loggerFactory.CreateLogger("GDT-Eingang"));

        _dicomServer = new DicomServerHost(_loggerFactory.CreateLogger("DICOM"));

        _collector.Start();
        StartGdtWatcher();
        StartDicomServer();
        StartPurgeLoop();
        CleanUpLogs(force: true);

        _logger.LogInformation("Middleware gestartet.");
    }

    /// <summary>
    /// Startet die Verzeichnisüberwachung – oder eben nicht. Der Watcher wird trotzdem
    /// gebaut: Über ihn läuft auch die Verarbeitung auf Anforderung durch den Connector.
    /// </summary>
    private void StartGdtWatcher()
    {
        if (Config.Gdt.EnableInboxWatcher)
        {
            _gdtWatcher.Start();
            return;
        }

        _gdtWatcher.Stop();
        Status.GdtWatcherRunning = false;
        Status.GdtWatcherError = "";

        _logger.LogInformation(
            "Verzeichnisüberwachung ist abgeschaltet. Aufträge werden nur über den " +
            "Programmaufruf (GDT2DICOM.Aufruf.exe) verarbeitet.");
    }

    private void StartDicomServer()
    {
        try
        {
            _dicomServer.Start(new DicomServiceContext
            {
                Config = Config,
                Worklist = _worklist,
                Status = Status,
                Logger = _loggerFactory.CreateLogger("DICOM"),
                Events = this
            });

            Status.DicomServerRunning = true;
            Status.DicomServerError = "";
        }
        catch (Exception ex)
        {
            Status.DicomServerRunning = false;
            Status.DicomServerError = ex.Message;
            _logger.LogError(ex, "DICOM-Server konnte nicht gestartet werden (Port {Port} belegt oder gesperrt?).",
                Config.Dicom.Port);
        }
    }

    private CancellationTokenSource? _purgeCts;
    private DateTime _lastLogCleanupUtc = DateTime.MinValue;

    private void StartPurgeLoop()
    {
        _purgeCts = new CancellationTokenSource();
        var token = _purgeCts.Token;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    // Kurzer Takt wegen des Rückstaus: Ein zurückgehaltener Rücksatz soll
                    // zügig nachrutschen, sobald das PVS den vorigen abgeholt hat.
                    await Task.Delay(TimeSpan.FromSeconds(5), token);

                    var hours = Config.Worklist.ItemLifetimeHours;
                    if (hours > 0)
                        _worklist.Purge(TimeSpan.FromHours(hours));

                    CleanUpLogs();
                    DeliverPendingResponses();
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Fehler beim zyklischen Aufräumen.");
                }
            }
        }, token);
    }

    /// <summary>
    /// Räumt alte Logdateien weg – höchstens einmal alle sechs Stunden, damit die
    /// Fünf-Minuten-Schleife nicht ständig das Verzeichnis durchsucht.
    /// </summary>
    private void CleanUpLogs(bool force = false)
    {
        if (!force && DateTime.UtcNow - _lastLogCleanupUtc < TimeSpan.FromHours(6))
            return;

        _lastLogCleanupUtc = DateTime.UtcNow;

        var logger = _loggerFactory.CreateLogger("Protokoll");
        LogCleanup.Run(Config.General, logger);

        // Das Archiv wird im selben Takt geprüft; es durchsucht mehr Ordner und soll
        // deshalb erst recht nicht alle fünf Minuten laufen.
        ArchiveCleanup.Run(Config.Export, _loggerFactory.CreateLogger("Archiv"));
    }

    /// <summary>Übernimmt eine geänderte Konfiguration und startet die betroffenen Bausteine neu.</summary>
    public void Reload(AppConfig config)
    {
        _logger.LogInformation("Konfiguration wird neu geladen.");

        lock (_configLock)
        {
            _config = config;
        }

        ConfigStore.EnsureConfiguredDirectories(config);

        StartGdtWatcher();
        StartDicomServer();

        // Eine gerade verkürzte Aufbewahrungsdauer soll sofort greifen, nicht erst in sechs Stunden.
        CleanUpLogs(force: true);

        _logger.LogInformation("Konfiguration übernommen.");
    }

    public async Task StopAsync()
    {
        _logger.LogInformation("Middleware wird beendet.");

        try
        {
            _purgeCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _gdtWatcher?.Stop();
        _dicomServer?.Stop();
        Status.DicomServerRunning = false;

        if (_collector is not null)
        {
            _collector.Stop();
            // Angefangene Untersuchungen noch wegschreiben, statt sie zu verlieren.
            await _collector.FlushAsync();
        }
    }

    // -----------------------------------------------------------------------
    // IDicomEventSink
    // -----------------------------------------------------------------------

    public Task OnInstanceStoredAsync(DicomFile file, string storedPath, string callingAeTitle)
    {
        _collector.AddInstance(file, storedPath, callingAeTitle);
        return Task.CompletedTask;
    }

    public void OnMpps(MppsEvent mppsEvent)
    {
        var item = _worklist.Match(mppsEvent.StudyInstanceUid, mppsEvent.AccessionNumber, mppsEvent.PatientId, mppsEvent.SopInstanceUid);

        if (item is not null)
        {
            _worklist.Update(item.Id, i =>
            {
                i.MppsSopInstanceUid = mppsEvent.SopInstanceUid;
                switch (mppsEvent.Status)
                {
                    case MppsStatus.InProgress:
                        i.State = WorklistItemState.InProgress;
                        i.MppsStartedUtc = DateTime.UtcNow;
                        break;
                    case MppsStatus.Completed:
                        i.State = WorklistItemState.Completed;
                        i.MppsCompletedUtc = DateTime.UtcNow;
                        break;
                    case MppsStatus.Discontinued:
                        i.State = WorklistItemState.Discontinued;
                        i.MppsCompletedUtc = DateTime.UtcNow;
                        break;
                }
            });
        }

        // Das Gerät kündigt mit IN PROGRESS an, dass es das Ende selbst meldet. Ab da setzt
        // der Sammler die Ruhezeit aus – sonst zerfiele eine Untersuchung mit längeren
        // Messpausen in mehrere Rücksätze.
        if (mppsEvent.Status == MppsStatus.InProgress)
            _collector.MarkInProgress(mppsEvent.StudyInstanceUid, mppsEvent.AccessionNumber, mppsEvent.SopInstanceUid);

        if (mppsEvent.Status is MppsStatus.Completed or MppsStatus.Discontinued)
        {
            _collector.MarkCompleted(mppsEvent.StudyInstanceUid, mppsEvent.AccessionNumber, mppsEvent.SopInstanceUid);

            if (mppsEvent.Status == MppsStatus.Completed
                && item is not null
                && Config.Worklist.RemoveOnMppsCompleted
                && !Config.Worklist.RemoveAfterStudyExported)
            {
                _worklist.Remove(item.Id);
            }
        }
    }

    public async Task OnStorageCommitAsync(StorageCommitRequest request)
    {
        var config = Config;

        // Gegenstelle über den Calling AE identifizieren; nur zu bekannten Knoten wird gesendet.
        var target = config.Dicom.RemoteNodes.FirstOrDefault(n =>
            string.Equals(n.AeTitle, request.CallingAeTitle, StringComparison.OrdinalIgnoreCase));

        if (target is null)
        {
            _logger.LogWarning(
                "Storage Commitment von {CallingAe} kann nicht beantwortet werden: Es ist keine Gegenstelle mit " +
                "diesem AE-Titel konfiguriert. Bitte unter DICOM → Gegenstellen Host und Port eintragen.",
                request.CallingAeTitle);
            return;
        }

        // Als gesichert gilt, was tatsächlich im Eingang oder im Archiv liegt.
        var committed = new List<(DicomUID, DicomUID)>();
        var failed = new List<(DicomUID, DicomUID)>();

        foreach (var reference in request.References)
        {
            if (IsStored(config, reference.SopInstanceUid.UID))
                committed.Add((reference.SopClassUid, reference.SopInstanceUid));
            else
                failed.Add((reference.SopClassUid, reference.SopInstanceUid));
        }

        await DicomScu.SendStorageCommitResultAsync(
            target, config.Dicom.AeTitle, request.TransactionUid, committed, failed, _logger);
    }

    private static bool IsStored(AppConfig config, string sopInstanceUid)
    {
        var fileName = sopInstanceUid + ".dcm";

        var incoming = Path.Combine(config.Dicom.IncomingDirectory, fileName);
        if (File.Exists(incoming))
            return true;

        if (!config.Export.ArchiveDicom || !Directory.Exists(config.Export.DicomArchiveDirectory))
            return false;

        return Directory.EnumerateFiles(config.Export.DicomArchiveDirectory, fileName, SearchOption.AllDirectories).Any();
    }

    // -----------------------------------------------------------------------

    public async ValueTask DisposeAsync()
    {
        await StopAsync();

        if (_gdtWatcher is not null)
            await _gdtWatcher.DisposeAsync();
        if (_collector is not null)
            await _collector.DisposeAsync();

        _dicomServer?.Dispose();
        _purgeCts?.Dispose();
    }
}

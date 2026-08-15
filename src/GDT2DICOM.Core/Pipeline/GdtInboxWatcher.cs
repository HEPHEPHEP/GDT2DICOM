using Gdt2Dicom.Core.Configuration;
using Gdt2Dicom.Core.Runtime;
using Microsoft.Extensions.Logging;

namespace Gdt2Dicom.Core.Pipeline;

/// <summary>Ergebnis der Verarbeitung einer einzelnen Auftragsdatei.</summary>
public sealed record GdtProcessResult(
    bool Success,
    string? AccessionNumber = null,
    string? PatientName = null,
    string? PatientId = null,
    string? Error = null,
    bool AlreadyRunning = false)
{
    public static GdtProcessResult Failed(string error) => new(false, Error: error);

    public static GdtProcessResult Busy() =>
        new(false, Error: "Die Datei wird bereits verarbeitet.", AlreadyRunning: true);
}

/// <summary>
/// Überwacht das Ausgabeverzeichnis des PVS. FileSystemWatcher plus zyklisches Nachsehen –
/// auf Netzlaufwerken gehen Watcher-Ereignisse regelmäßig verloren, und ein verpasster
/// Auftrag ist in einer Praxis teurer als ein paar Verzeichnislistings.
/// </summary>
public sealed class GdtInboxWatcher : IAsyncDisposable
{
    private readonly Func<AppConfig> _config;
    private readonly GdtRequestProcessor _processor;
    private readonly RuntimeStatus _status;
    private readonly ILogger _logger;

    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _cts;
    private Task? _pollTask;
    private readonly SemaphoreSlim _processLock = new(1, 1);
    private readonly HashSet<string> _inFlight = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Kürzlich erfolgreich verarbeitete Aufträge.
    /// </summary>
    /// <remarks>
    /// Legt das PVS die Auftragsdatei ins überwachte Verzeichnis <em>und</em> ruft zusätzlich
    /// das Connector-Programm auf, ist die Datei beim Aufruf oft schon verarbeitet und
    /// gelöscht. Ohne dieses Gedächtnis würde der Connector dem PVS einen Fehler melden,
    /// obwohl der Auftrag angekommen ist.
    /// </remarks>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime WhenUtc, GdtProcessResult Result)>
        _recent = new(StringComparer.OrdinalIgnoreCase);

    private (DateTime WhenUtc, GdtProcessResult Result)? _lastSuccess;

    public GdtInboxWatcher(Func<AppConfig> config, GdtRequestProcessor processor, RuntimeStatus status, ILogger logger)
    {
        _config = config;
        _processor = processor;
        _status = status;
        _logger = logger;
    }

    public void Start()
    {
        Stop();

        var gdt = _config().Gdt;

        try
        {
            Directory.CreateDirectory(gdt.InboxDirectory);

            _cts = new CancellationTokenSource();

            _watcher = new FileSystemWatcher(gdt.InboxDirectory, gdt.InboxFilePattern)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true
            };
            _watcher.Created += (_, e) => QueueFile(e.FullPath);
            _watcher.Changed += (_, e) => QueueFile(e.FullPath);
            _watcher.Renamed += (_, e) => QueueFile(e.FullPath);
            _watcher.Error += (_, e) => _logger.LogWarning(e.GetException(), "FileSystemWatcher meldet einen Fehler.");

            _pollTask = Task.Run(() => PollLoopAsync(_cts.Token));

            _status.GdtWatcherRunning = true;
            _status.GdtWatcherError = "";
            _logger.LogInformation("GDT-Eingang wird überwacht: {Directory} ({Pattern})",
                gdt.InboxDirectory, gdt.InboxFilePattern);

            // Beim Start liegengebliebene Dateien sofort abarbeiten.
            _ = Task.Run(() => ScanOnceAsync(_cts.Token));
        }
        catch (Exception ex)
        {
            _status.GdtWatcherRunning = false;
            _status.GdtWatcherError = ex.Message;
            _logger.LogError(ex, "GDT-Eingang {Directory} konnte nicht überwacht werden.", gdt.InboxDirectory);
        }
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

        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }

        _status.GdtWatcherRunning = false;
    }

    private void QueueFile(string path)
    {
        lock (_inFlight)
        {
            if (!_inFlight.Add(path))
                return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await ProcessFileAsync(path);
            }
            finally
            {
                lock (_inFlight)
                {
                    _inFlight.Remove(path);
                }
            }
        });
    }

    private async Task PollLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var interval = Math.Max(2, _config().Gdt.PollIntervalSeconds);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(interval), token);
                await ScanOnceAsync(token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fehler beim zyklischen Prüfen des GDT-Eingangs.");
            }
        }
    }

    private async Task ScanOnceAsync(CancellationToken token)
    {
        var gdt = _config().Gdt;
        if (!Directory.Exists(gdt.InboxDirectory))
            return;

        foreach (var file in Directory.EnumerateFiles(gdt.InboxDirectory, gdt.InboxFilePattern))
        {
            if (token.IsCancellationRequested)
                return;
            QueueFile(file);
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Verarbeitet eine Auftragsdatei sofort und meldet das Ergebnis zurück. Wird vom
    /// Connector benutzt, wenn das PVS die Middleware per Programmaufruf anstößt.
    /// </summary>
    /// <remarks>
    /// Läuft über dieselbe Sperre wie die Verzeichnisüberwachung. Sonst würde eine Datei,
    /// die im Eingangsverzeichnis liegt und gleichzeitig per Aufruf gemeldet wird, zweimal
    /// verarbeitet – der Patient stünde doppelt in der Worklist.
    /// </remarks>
    public async Task<GdtProcessResult> ProcessNowAsync(string path)
    {
        lock (_inFlight)
        {
            if (!_inFlight.Add(path))
                return GdtProcessResult.Busy();
        }

        try
        {
            return await ProcessFileAsync(path);
        }
        finally
        {
            lock (_inFlight)
            {
                _inFlight.Remove(path);
            }
        }
    }

    private void Remember(string path, GdtProcessResult result)
    {
        var entry = (DateTime.UtcNow, result);

        try
        {
            _recent[Path.GetFullPath(path)] = entry;
        }
        catch (ArgumentException)
        {
            // Unbrauchbarer Pfad – dann bleibt nur der letzte Erfolg als Anhaltspunkt.
        }

        _lastSuccess = entry;

        // Alte Einträge wegräumen; die Liste dient nur der Überbrückung weniger Sekunden.
        var cutoff = DateTime.UtcNow.AddMinutes(-5);
        foreach (var key in _recent.Where(kv => kv.Value.WhenUtc < cutoff).Select(kv => kv.Key).ToList())
            _recent.TryRemove(key, out _);
    }

    /// <summary>
    /// Sucht einen bereits verarbeiteten Auftrag, der nicht älter als <paramref name="sinceUtc"/> ist.
    /// Damit lässt sich beantworten: „Die Datei ist weg – war das ich, oder ist sie nie angekommen?“
    /// </summary>
    public GdtProcessResult? FindRecent(string? path, DateTime sinceUtc)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            try
            {
                if (_recent.TryGetValue(Path.GetFullPath(path), out var byPath) && byPath.WhenUtc >= sinceUtc)
                    return byPath.Result;
            }
            catch (ArgumentException)
            {
            }

            return null;
        }

        return _lastSuccess is { } last && last.WhenUtc >= sinceUtc ? last.Result : null;
    }

    private async Task<GdtProcessResult> ProcessFileAsync(string path)
    {
        var gdt = _config().Gdt;

        // Warten, bis das PVS mit dem Schreiben fertig ist.
        if (!await WaitUntilReadableAsync(path, gdt.FileSettleMilliseconds))
            return GdtProcessResult.Failed($"{Path.GetFileName(path)} war nicht lesbar oder ist verschwunden.");

        await _processLock.WaitAsync();
        try
        {
            if (!File.Exists(path))
                return GdtProcessResult.Failed($"{Path.GetFileName(path)} existiert nicht mehr.");

            var item = _processor.ProcessFile(path);
            ArchiveOrDelete(path, gdt);

            if (item is null)
            {
                return GdtProcessResult.Failed(
                    $"{Path.GetFileName(path)} enthält keine als Auftrag konfigurierte Satzart.");
            }

            var success = new GdtProcessResult(true, item.AccessionNumber, item.DisplayName, item.PatientId);
            Remember(path, success);
            return success;
        }
        catch (Exception ex)
        {
            _status.CountGdtRequest(false, $"Fehler bei {Path.GetFileName(path)}: {ex.Message}");
            _logger.LogError(ex, "Auftragsdatei {File} konnte nicht verarbeitet werden.", path);
            MoveToErrorFolder(path, gdt);
            return GdtProcessResult.Failed(ex.Message);
        }
        finally
        {
            _processLock.Release();
        }
    }

    /// <summary>Prüft wiederholt, ob die Datei exklusiv geöffnet werden kann und ihre Größe stabil ist.</summary>
    private static async Task<bool> WaitUntilReadableAsync(string path, int settleMilliseconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        long lastSize = -1;

        while (DateTime.UtcNow < deadline)
        {
            if (!File.Exists(path))
                return false;

            try
            {
                var size = new FileInfo(path).Length;
                if (size > 0 && size == lastSize)
                {
                    using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
                    return true;
                }
                lastSize = size;
            }
            catch (IOException)
            {
                // Noch gesperrt – weiter warten.
            }

            await Task.Delay(Math.Max(100, settleMilliseconds));
        }

        return false;
    }

    private void ArchiveOrDelete(string path, GdtConfig gdt)
    {
        if (!string.IsNullOrWhiteSpace(gdt.InboxArchiveDirectory))
        {
            try
            {
                Directory.CreateDirectory(gdt.InboxArchiveDirectory);
                var target = Path.Combine(gdt.InboxArchiveDirectory,
                    $"{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Path.GetFileName(path)}");
                File.Copy(path, target, overwrite: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Auftragsdatei konnte nicht archiviert werden.");
            }
        }

        if (gdt.DeleteInboxFileAfterProcessing)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Auftragsdatei {File} konnte nicht gelöscht werden.", path);
            }
        }
    }

    private void MoveToErrorFolder(string path, GdtConfig gdt)
    {
        try
        {
            var errorDirectory = Path.Combine(
                string.IsNullOrWhiteSpace(gdt.InboxArchiveDirectory)
                    ? Path.GetDirectoryName(path)!
                    : gdt.InboxArchiveDirectory,
                "fehler");

            Directory.CreateDirectory(errorDirectory);
            var target = Path.Combine(errorDirectory, $"{DateTime.Now:yyyyMMdd_HHmmss}_{Path.GetFileName(path)}");
            File.Move(path, target, overwrite: true);
            _logger.LogWarning("Fehlerhafte Auftragsdatei nach {Target} verschoben.", target);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fehlerhafte Auftragsdatei konnte nicht verschoben werden.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        Stop();

        if (_pollTask is not null)
        {
            try
            {
                await _pollTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch
            {
                // Beim Herunterfahren nicht weiter relevant.
            }
        }

        _cts?.Dispose();
        _processLock.Dispose();
    }
}

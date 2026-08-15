using Gdt2Dicom.Core.Configuration;
using Gdt2Dicom.Core.Ipc;
using Gdt2Dicom.Core.Runtime;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Gdt2Dicom.Service;

/// <summary>Hält die Middleware und den Steuerkanal über die Lebensdauer des Dienstes am Laufen.</summary>
public sealed class MiddlewareWorker : BackgroundService
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<MiddlewareWorker> _logger;
    private readonly IHostApplicationLifetime _lifetime;

    private MiddlewareHost? _host;
    private IpcServer? _ipc;

    public MiddlewareWorker(ILoggerFactory loggerFactory, ILogger<MiddlewareWorker> logger,
        IHostApplicationLifetime lifetime)
    {
        _loggerFactory = loggerFactory;
        _logger = logger;
        _lifetime = lifetime;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var config = ConfigStore.LoadSafe(out var configError);
            if (configError is not null)
                _logger.LogError("Konfiguration fehlerhaft ({Error}) – es gelten die Standardwerte.", configError);

            _logger.LogInformation("GDT2DICOM startet. Konfiguration: {Path}", ConfigStore.ConfigFilePath);

            _host = new MiddlewareHost(config, _loggerFactory);
            _host.Start();

            _ipc = new IpcServer(_host, ApplyConfigAsync, _loggerFactory.CreateLogger("Steuerkanal"));
            _ipc.Start();

            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Regulärer Dienststopp.
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Unbehandelter Fehler – der Dienst wird beendet.");
            _lifetime.StopApplication();
        }
    }

    private Task ApplyConfigAsync(AppConfig config)
    {
        _host?.Reload(config);
        return Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("GDT2DICOM wird beendet.");

        if (_ipc is not null)
            await _ipc.DisposeAsync();

        if (_host is not null)
            await _host.DisposeAsync();

        await base.StopAsync(cancellationToken);
        LoggingSetup.Shutdown();
    }
}

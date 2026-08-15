using System.Text;
using FellowOakDicom;
using FellowOakDicom.Imaging;
using FellowOakDicom.Imaging.NativeCodec;
using FellowOakDicom.Network;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Gdt2Dicom.Core.Dicom;

/// <summary>Startet und stoppt den DICOM-Server und hält die fo-dicom-Grundeinrichtung zentral.</summary>
public sealed class DicomServerHost : IDisposable
{
    private readonly ILogger _logger;
    private IDicomServer? _server;

    private static readonly object SetupLock = new();
    private static bool _setupDone;

    public DicomServerHost(ILogger logger) => _logger = logger;

    /// <summary>
    /// Richtet fo-dicom ein: nativer Transcoder für komprimierte Transfersyntaxen und
    /// der WinForms-Bildmanager, damit Pixeldaten nach JPEG/PNG gerendert werden können.
    /// Darf nur einmal pro Prozess laufen.
    /// </summary>
    public static void EnsureFoDicomSetup(ILoggerFactory loggerFactory)
    {
        lock (SetupLock)
        {
            if (_setupDone)
                return;

            new DicomSetupBuilder()
                .RegisterServices(services => services
                    .AddFellowOakDicom()
                    .AddTranscoderManager<NativeTranscoderManager>()
                    .AddImageManager<WinFormsImageManager>()
                    // Nach AddFellowOakDicom registriert, damit fo-dicom in unser Log schreibt.
                    .AddSingleton(loggerFactory))
                .Build();

            _setupDone = true;
        }
    }

    public bool IsRunning => _server is not null;

    public int? Port => _server?.Port;

    public void Start(DicomServiceContext context)
    {
        Stop();

        var config = context.Config.Dicom;

        _server = DicomServerFactory.Create<Gdt2DicomService>(
            ipAddress: string.IsNullOrWhiteSpace(config.BindAddress) ? "0.0.0.0" : config.BindAddress,
            port: config.Port,
            userState: context,
            fallbackEncoding: Encoding.Latin1,
            logger: _logger);

        // DicomServiceOptions hängt am laufenden Server und wird beim Aufbau jeder
        // Association ausgewertet, lässt sich also nachträglich setzen.
        _server.Options.MaxPDULength = config.MaxPduLength;
        _server.Options.LogDimseDatasets = false;
        _server.Options.LogDataPDUs = false;
        _server.Options.RequestTimeout = TimeSpan.FromSeconds(Math.Max(10, config.AssociationTimeoutSeconds));

        _logger.LogInformation(
            "DICOM-Server läuft auf {Bind}:{Port} als AE {AeTitle} (Worklist={Mwl}, Storage={Storage}, MPPS={Mpps}, Commit={Commit}).",
            config.BindAddress, config.Port, config.AeTitle,
            config.EnableWorklist, config.EnableStorage, config.EnableMpps, config.EnableStorageCommit);
    }

    public void Stop()
    {
        if (_server is null)
            return;

        try
        {
            _server.Stop();
            _server.Dispose();
            _logger.LogInformation("DICOM-Server gestoppt.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fehler beim Stoppen des DICOM-Servers.");
        }
        finally
        {
            _server = null;
        }
    }

    public void Dispose() => Stop();
}

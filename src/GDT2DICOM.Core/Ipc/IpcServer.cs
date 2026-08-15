using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Gdt2Dicom.Core.Configuration;
using Gdt2Dicom.Core.Dicom;
using Gdt2Dicom.Core.Runtime;
using Gdt2Dicom.Core.Worklist;
using Microsoft.Extensions.Logging;

namespace Gdt2Dicom.Core.Ipc;

/// <summary>
/// Named-Pipe-Server im Dienst. Die GUI verbindet sich als angemeldeter Benutzer, während
/// der Dienst unter LocalSystem läuft – deshalb wird die Pipe explizit für die Gruppe
/// "Benutzer" freigegeben.
/// </summary>
public sealed class IpcServer : IAsyncDisposable
{
    public const string PipeName = "GDT2DICOM.Control";

    private readonly MiddlewareHost _host;
    private readonly Func<AppConfig, Task> _applyConfig;
    private readonly ILogger _logger;
    private CancellationTokenSource? _cts;
    private readonly List<Task> _workers = new();

    public IpcServer(MiddlewareHost host, Func<AppConfig, Task> applyConfig, ILogger logger)
    {
        _host = host;
        _applyConfig = applyConfig;
        _logger = logger;
    }

    public void Start(int concurrentListeners = 3)
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        for (var i = 0; i < concurrentListeners; i++)
            _workers.Add(Task.Run(() => ListenLoopAsync(token), token));

        _logger.LogInformation("Steuerkanal bereit: \\\\.\\pipe\\{Pipe}", PipeName);
    }

    private static PipeSecurity BuildPipeSecurity()
    {
        var security = new PipeSecurity();

        // Angemeldete Benutzer dürfen sich verbinden und lesen/schreiben, sonst kommt die
        // GUI ohne Adminrechte nicht an den Dienst heran. Bewusst ohne CreateNewInstance:
        // sonst könnte ein beliebiger Benutzer eine eigene Instanz dieser Pipe anlegen und
        // sich als Dienst ausgeben.
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        // Das eigene Konto braucht FullControl (inklusive CreateNewInstance), weil eine
        // explizite DACL die Vorgaberechte des Erzeugers ersetzt – ohne diese Regel könnte
        // der Dienst keine weitere Pipe-Instanz anlegen und nur einen Client bedienen.
        var owner = WindowsIdentity.GetCurrent().User;
        if (owner is not null)
        {
            security.AddAccessRule(new PipeAccessRule(owner, PipeAccessRights.FullControl, AccessControlType.Allow));
        }

        return security;
    }

    private async Task ListenLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = NamedPipeServerStreamAcl.Create(
                    PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    inBufferSize: 0,
                    outBufferSize: 0,
                    BuildPipeSecurity());

                await pipe.WaitForConnectionAsync(token);
                await HandleClientAsync(pipe, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Fehler im Steuerkanal.");
                await Task.Delay(500, CancellationToken.None);
            }
            finally
            {
                pipe?.Dispose();
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

        var line = await reader.ReadLineAsync(token);
        if (string.IsNullOrWhiteSpace(line))
            return;

        IpcResponse response;
        try
        {
            var request = IpcJson.Deserialize<IpcRequest>(line) ?? new IpcRequest();
            response = await DispatchAsync(request);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Steuerbefehl konnte nicht verarbeitet werden.");
            response = IpcResponse.Fail(ex.Message);
        }

        await writer.WriteLineAsync(IpcJson.Serialize(response));
        await writer.FlushAsync(token);

        try
        {
            pipe.WaitForPipeDrain();
        }
        catch (IOException)
        {
            // Client hat bereits aufgelegt.
        }
    }

    private async Task<IpcResponse> DispatchAsync(IpcRequest request) => request.Command switch
    {
        IpcCommands.Ping => IpcResponse.Ok("pong"),
        IpcCommands.GetStatus => IpcResponse.Ok(IpcJson.Serialize(BuildStatus())),
        IpcCommands.GetConfig => IpcResponse.Ok(ConfigStore.Serialize(_host.Config)),
        IpcCommands.SetConfig => await SetConfigAsync(request.Payload),
        IpcCommands.TailLog => IpcResponse.Ok(string.Join("\n",
            LogRingBuffer.Instance.Tail(int.TryParse(request.Payload, out var n) ? n : 300))),
        IpcCommands.ListWorklist => IpcResponse.Ok(IpcJson.Serialize(BuildWorklist())),
        IpcCommands.RemoveWorklistItem => RemoveWorklistItem(request.Payload),
        IpcCommands.ListPendingStudies => IpcResponse.Ok(IpcJson.Serialize(BuildPendingStudies())),
        IpcCommands.TestEcho => await TestEchoAsync(request.Payload),
        IpcCommands.ProcessGdt => await ProcessGdtAsync(request.Payload),
        IpcCommands.FetchGdtResponse => FetchGdtResponse(request.Payload),
        IpcCommands.CheckPaths => CheckPaths(),
        _ => IpcResponse.Fail($"Unbekannter Befehl: {request.Command}")
    };

    /// <summary>
    /// Prüft die Verzeichnisse im Kontext des Dienstes. Genau darauf kommt es an: Die
    /// Oberfläche läuft als angemeldeter Benutzer und erreicht Freigaben in der Regel, der
    /// Dienst als LocalSystem dagegen nicht.
    /// </summary>
    private IpcResponse CheckPaths()
    {
        var checks = PathProbe.CheckAll(_host.Config);

        var result = new PathCheckResultDto
        {
            ServiceAccount = System.Security.Principal.WindowsIdentity.GetCurrent().Name,
            Checks = checks.Select(c => new PathCheckDto
            {
                Label = c.Label,
                Path = c.Path,
                IsUnc = c.IsUnc,
                Exists = c.Exists,
                CanWrite = c.CanWrite,
                Error = c.Error ?? "",
                Ok = c.Ok
            }).ToList()
        };

        var fehler = checks.Count(c => !c.Ok);
        _logger.LogInformation("Verzeichnisprüfung als {Konto}: {Fehler} von {Gesamt} nicht erreichbar.",
            result.ServiceAccount, fehler, checks.Count);

        return IpcResponse.Ok(IpcJson.Serialize(result));
    }

    private IpcResponse FetchGdtResponse(string? patientId)
    {
        var result = _host.FetchResponse(patientId);

        _logger.LogInformation("Rücksatz abgerufen ({Patient}): {Ergebnis}",
            string.IsNullOrWhiteSpace(patientId) ? "ohne Patientenangabe" : patientId,
            result.Delivered ? $"{result.FileName} ausgeliefert" : result.Error);

        return IpcResponse.Ok(IpcJson.Serialize(new GdtFetchResultDto
        {
            Delivered = result.Delivered,
            FileName = result.FileName ?? "",
            PatientName = result.PatientName ?? "",
            PatientId = result.PatientId ?? "",
            Remaining = result.Remaining,
            Error = result.Error ?? ""
        }));
    }

    private async Task<IpcResponse> ProcessGdtAsync(string? payload)
    {
        var request = IpcJson.Deserialize<GdtProcessRequestDto>(payload)
                      ?? new GdtProcessRequestDto { SinceUtc = DateTime.UtcNow.AddSeconds(-15) };

        var path = string.IsNullOrWhiteSpace(request.Path) ? null : request.Path;
        var result = await _host.ProcessGdtFileAsync(path, request.SinceUtc);

        _logger.LogInformation(
            "Auftragsdatei auf Anforderung verarbeitet ({Path}): {Ergebnis}",
            path ?? "neueste im Eingang",
            result.Success ? $"Auftrag {result.AccessionNumber} für {result.PatientName}" : result.Error);

        // Auch ein fachlicher Fehlschlag ist eine gültige Antwort: der Aufrufer bekommt den
        // Grund im Ergebnis, nicht als Übertragungsfehler.
        return IpcResponse.Ok(IpcJson.Serialize(new GdtProcessResultDto
        {
            Success = result.Success,
            AccessionNumber = result.AccessionNumber ?? "",
            PatientName = result.PatientName ?? "",
            PatientId = result.PatientId ?? "",
            Error = result.Error ?? ""
        }));
    }

    private StatusDto BuildStatus()
    {
        var status = _host.Status;
        var config = _host.Config;

        return new StatusDto
        {
            StartedUtc = status.StartedUtc,
            DicomServerRunning = status.DicomServerRunning,
            DicomServerError = status.DicomServerError,
            DicomAeTitle = config.Dicom.AeTitle,
            DicomPort = config.Dicom.Port,
            GdtWatcherRunning = status.GdtWatcherRunning,
            GdtWatcherEnabled = config.Gdt.EnableInboxWatcher,
            GdtWatcherError = status.GdtWatcherError,
            GdtInboxDirectory = config.Gdt.InboxDirectory,
            GdtOutboxDirectory = config.Gdt.OutboxDirectory,
            GdtRequestsProcessed = status.GdtRequestsProcessed,
            GdtRequestsFailed = status.GdtRequestsFailed,
            WorklistQueries = status.WorklistQueries,
            InstancesReceived = status.InstancesReceived,
            StudiesExported = status.StudiesExported,
            GdtResponsesWritten = status.GdtResponsesWritten,
            ExportFailures = status.ExportFailures,
            AssociationsAccepted = status.AssociationsAccepted,
            AssociationsRejected = status.AssociationsRejected,
            WorklistCount = _host.Worklist?.Count ?? 0,
            PendingStudyCount = _host.PendingStudies.Count,
            PendingResponseCount = _host.PendingResponseCount,
            ResponseDeliveryOnDemand = config.Gdt.ResponseDelivery == ResponseDelivery.AufAbruf,
            LastGdtRequestUtc = status.LastGdtRequestUtc,
            LastWorklistQueryUtc = status.LastWorklistQueryUtc,
            LastInstanceUtc = status.LastInstanceUtc,
            LastExportUtc = status.LastExportUtc,
            LastActivity = status.LastActivity
        };
    }

    private List<WorklistItemDto> BuildWorklist() =>
        (_host.Worklist?.GetAll() ?? (IReadOnlyList<WorklistItem>)Array.Empty<WorklistItem>())
        .OrderByDescending(i => i.CreatedUtc)
        .Select(i => new WorklistItemDto
        {
            Id = i.Id,
            AccessionNumber = i.AccessionNumber,
            PatientId = i.PatientId,
            PatientName = i.DisplayName,
            BirthDate = i.PatientBirthDate,
            ScheduledDate = i.ScheduledDate,
            ScheduledTime = i.ScheduledTime,
            Procedure = i.RequestedProcedureDescription,
            State = i.State.ToString(),
            QueryCount = i.QueryCount,
            CreatedUtc = i.CreatedUtc
        })
        .ToList();

    private List<PendingStudyDto> BuildPendingStudies() =>
        _host.PendingStudies.Select(s => new PendingStudyDto
        {
            StudyInstanceUid = s.StudyInstanceUid,
            PatientName = s.PatientName,
            PatientId = s.PatientId,
            InstanceCount = s.Files.Count,
            LastInstanceUtc = s.LastInstanceUtc,
            CallingAeTitle = s.CallingAeTitle
        }).ToList();

    private IpcResponse RemoveWorklistItem(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return IpcResponse.Fail("Keine Eintrags-ID angegeben.");

        return _host.Worklist.Remove(id)
            ? IpcResponse.Ok()
            : IpcResponse.Fail("Eintrag nicht gefunden.");
    }

    private async Task<IpcResponse> SetConfigAsync(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return IpcResponse.Fail("Keine Konfiguration übergeben.");

        var config = ConfigStore.Deserialize(payload);
        ConfigStore.Save(config);
        await _applyConfig(config);

        _logger.LogInformation("Konfiguration über die Oberfläche geändert und übernommen.");
        return IpcResponse.Ok();
    }

    private static async Task<IpcResponse> TestEchoAsync(string? payload)
    {
        var dto = IpcJson.Deserialize<EchoRequestDto>(payload);
        if (dto is null || string.IsNullOrWhiteSpace(dto.Host))
            return IpcResponse.Fail("Kein Ziel angegeben.");

        var result = await DicomScu.EchoAsync(dto.Host, dto.Port, dto.CallingAe, dto.CalledAe);

        return IpcResponse.Ok(IpcJson.Serialize(new EchoResultDto
        {
            Success = result.Success,
            Message = result.Message,
            ElapsedMilliseconds = result.ElapsedMilliseconds
        }));
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        // Wartende Listener laufen erst nach einer Verbindung aus – kurz warten reicht.
        try
        {
            await Task.WhenAny(Task.WhenAll(_workers), Task.Delay(2000));
        }
        catch
        {
            // Beim Herunterfahren nicht weiter relevant.
        }

        _cts?.Dispose();
    }
}

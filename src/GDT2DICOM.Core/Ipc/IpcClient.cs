using System.IO.Pipes;
using System.Text;
using Gdt2Dicom.Core.Configuration;

namespace Gdt2Dicom.Core.Ipc;

/// <summary>Client-Seite des Steuerkanals. Wird von der GUI benutzt.</summary>
public sealed class IpcClient
{
    private readonly string _pipeName;
    private readonly int _timeoutMs;

    public IpcClient(string pipeName = IpcServer.PipeName, int timeoutMs = 3000)
    {
        _pipeName = pipeName;
        _timeoutMs = timeoutMs;
    }

    public async Task<IpcResponse> SendAsync(string command, string? payload = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(_timeoutMs, cancellationToken);

            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);

            await writer.WriteLineAsync(IpcJson.Serialize(new IpcRequest { Command = command, Payload = payload }));
            await writer.FlushAsync(cancellationToken);

            var line = await reader.ReadLineAsync(cancellationToken);
            return IpcJson.Deserialize<IpcResponse>(line) ?? IpcResponse.Fail("Leere Antwort vom Dienst.");
        }
        catch (TimeoutException)
        {
            return IpcResponse.Fail("Der Dienst antwortet nicht. Läuft GDT2DICOM?");
        }
        catch (Exception ex)
        {
            return IpcResponse.Fail(ex.Message);
        }
    }

    public async Task<bool> IsServiceReachableAsync(CancellationToken cancellationToken = default) =>
        (await SendAsync(IpcCommands.Ping, cancellationToken: cancellationToken)).Success;

    public async Task<StatusDto?> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(IpcCommands.GetStatus, cancellationToken: cancellationToken);
        return response.Success ? IpcJson.Deserialize<StatusDto>(response.Payload) : null;
    }

    /// <summary>
    /// Holt die Konfiguration vom Dienst. Ist der Dienst nicht erreichbar, wird die Datei
    /// direkt gelesen, damit sich die Middleware auch bei gestopptem Dienst einrichten lässt.
    /// </summary>
    public async Task<(AppConfig Config, bool FromService)> GetConfigAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(IpcCommands.GetConfig, cancellationToken: cancellationToken);

        if (response.Success && !string.IsNullOrWhiteSpace(response.Payload))
            return (ConfigStore.Deserialize(response.Payload), true);

        return (ConfigStore.LoadSafe(out _), false);
    }

    /// <summary>
    /// Speichert die Konfiguration. Läuft der Dienst, übernimmt er sie sofort; sonst wird
    /// nur die Datei geschrieben und beim nächsten Dienststart wirksam.
    /// </summary>
    public async Task<(bool Saved, bool AppliedLive, string? Error)> SetConfigAsync(
        AppConfig config, CancellationToken cancellationToken = default)
    {
        var json = ConfigStore.Serialize(config);
        var response = await SendAsync(IpcCommands.SetConfig, json, cancellationToken);

        if (response.Success)
            return (true, true, null);

        try
        {
            ConfigStore.Save(config);
            return (true, false, response.Error);
        }
        catch (Exception ex)
        {
            return (false, false, ex.Message);
        }
    }

    public async Task<IReadOnlyList<string>> TailLogAsync(int lines = 300, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(IpcCommands.TailLog, lines.ToString(), cancellationToken);
        if (!response.Success || string.IsNullOrEmpty(response.Payload))
            return Array.Empty<string>();

        return response.Payload.Split('\n');
    }

    public async Task<IReadOnlyList<WorklistItemDto>> GetWorklistAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(IpcCommands.ListWorklist, cancellationToken: cancellationToken);
        if (!response.Success)
            return Array.Empty<WorklistItemDto>();

        return IpcJson.Deserialize<List<WorklistItemDto>>(response.Payload) ?? new List<WorklistItemDto>();
    }

    public async Task<IReadOnlyList<PendingStudyDto>> GetPendingStudiesAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(IpcCommands.ListPendingStudies, cancellationToken: cancellationToken);
        if (!response.Success)
            return Array.Empty<PendingStudyDto>();

        return IpcJson.Deserialize<List<PendingStudyDto>>(response.Payload) ?? new List<PendingStudyDto>();
    }

    public async Task<bool> RemoveWorklistItemAsync(string id, CancellationToken cancellationToken = default) =>
        (await SendAsync(IpcCommands.RemoveWorklistItem, id, cancellationToken)).Success;

    /// <summary>
    /// Lässt den Dienst eine Auftragsdatei sofort verarbeiten. Ein leerer Pfad bedeutet
    /// „die neueste Datei im Eingangsverzeichnis“.
    /// </summary>
    public async Task<GdtProcessResultDto?> ProcessGdtAsync(string? path, DateTime sinceUtc,
        CancellationToken cancellationToken = default)
    {
        var payload = IpcJson.Serialize(new GdtProcessRequestDto
        {
            Path = path ?? "",
            SinceUtc = sinceUtc
        });

        var response = await SendAsync(IpcCommands.ProcessGdt, payload, cancellationToken);

        // null heißt: der Dienst war nicht erreichbar. Ein fachlicher Fehlschlag käme als
        // Ergebnis mit Success = false zurück.
        return response.Success ? IpcJson.Deserialize<GdtProcessResultDto>(response.Payload) : null;
    }

    /// <summary>Lässt den Dienst seine Verzeichnisse prüfen. null = Dienst nicht erreichbar.</summary>
    public async Task<PathCheckResultDto?> CheckPathsAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(IpcCommands.CheckPaths, cancellationToken: cancellationToken);
        return response.Success ? IpcJson.Deserialize<PathCheckResultDto>(response.Payload) : null;
    }

    /// <summary>Anzahl wartender Rücksätze. -1 bedeutet: Dienst nicht erreichbar.</summary>
    public async Task<int> FetchPendingCountAsync(CancellationToken cancellationToken = default)
    {
        var status = await GetStatusAsync(cancellationToken);
        return status?.PendingResponseCount ?? -1;
    }

    /// <summary>Fordert den bereitliegenden Rücksatz an. null = Dienst nicht erreichbar.</summary>
    public async Task<GdtFetchResultDto?> FetchGdtResponseAsync(string? patientId,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(IpcCommands.FetchGdtResponse, patientId ?? "", cancellationToken);
        return response.Success ? IpcJson.Deserialize<GdtFetchResultDto>(response.Payload) : null;
    }

    public async Task<EchoResultDto> EchoAsync(EchoRequestDto request, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(IpcCommands.TestEcho, IpcJson.Serialize(request), cancellationToken);

        if (!response.Success)
        {
            // Fällt der Dienst aus, direkt aus der GUI heraus testen.
            var direct = await Dicom.DicomScu.EchoAsync(request.Host, request.Port, request.CallingAe, request.CalledAe, cancellationToken);
            return new EchoResultDto
            {
                Success = direct.Success,
                Message = direct.Message + " (direkt aus der Oberfläche getestet, Dienst nicht erreichbar)",
                ElapsedMilliseconds = direct.ElapsedMilliseconds
            };
        }

        return IpcJson.Deserialize<EchoResultDto>(response.Payload)
               ?? new EchoResultDto { Success = false, Message = "Unlesbare Antwort." };
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gdt2Dicom.Core.Ipc;

public static class IpcCommands
{
    public const string Ping = "ping";
    public const string GetStatus = "status.get";
    public const string GetConfig = "config.get";
    public const string SetConfig = "config.set";
    public const string TailLog = "log.tail";
    public const string ListWorklist = "worklist.list";
    public const string RemoveWorklistItem = "worklist.remove";
    public const string ListPendingStudies = "studies.pending";
    public const string TestEcho = "dicom.echo";

    /// <summary>Eine GDT-Auftragsdatei sofort verarbeiten. Nutzlast: Dateipfad (darf leer sein).</summary>
    public const string ProcessGdt = "gdt.process";

    /// <summary>Bereitliegenden Rücksatz in den Ausgang stellen. Nutzlast: Patientennummer (darf leer sein).</summary>
    public const string FetchGdtResponse = "gdt.fetch";

    /// <summary>Alle konfigurierten Verzeichnisse im Dienstkontext auf Zugriff prüfen.</summary>
    public const string CheckPaths = "paths.check";
}

public sealed class IpcRequest
{
    public string Command { get; set; } = "";
    public string? Payload { get; set; }
}

public sealed class IpcResponse
{
    public bool Success { get; set; }
    public string? Payload { get; set; }
    public string? Error { get; set; }

    public static IpcResponse Ok(string? payload = null) => new() { Success = true, Payload = payload };
    public static IpcResponse Fail(string error) => new() { Success = false, Error = error };
}

/// <summary>Was die GUI im Statusbereich anzeigt.</summary>
public sealed class StatusDto
{
    public bool ServiceReachable { get; set; } = true;
    public DateTime StartedUtc { get; set; }
    public bool DicomServerRunning { get; set; }
    public string DicomServerError { get; set; } = "";
    public string DicomAeTitle { get; set; } = "";
    public int DicomPort { get; set; }
    public bool GdtWatcherRunning { get; set; }
    public bool GdtWatcherEnabled { get; set; } = true;
    public string GdtWatcherError { get; set; } = "";
    public string GdtInboxDirectory { get; set; } = "";
    public string GdtOutboxDirectory { get; set; } = "";

    public long GdtRequestsProcessed { get; set; }
    public long GdtRequestsFailed { get; set; }
    public long WorklistQueries { get; set; }
    public long InstancesReceived { get; set; }
    public long StudiesExported { get; set; }
    public long GdtResponsesWritten { get; set; }
    public long ExportFailures { get; set; }
    public long AssociationsAccepted { get; set; }
    public long AssociationsRejected { get; set; }

    public int WorklistCount { get; set; }
    public int PendingStudyCount { get; set; }

    /// <summary>Rücksätze, die auf die Auslieferung ins Ausgangsverzeichnis warten.</summary>
    public int PendingResponseCount { get; set; }
    public bool ResponseDeliveryOnDemand { get; set; }

    public DateTime? LastGdtRequestUtc { get; set; }
    public DateTime? LastWorklistQueryUtc { get; set; }
    public DateTime? LastInstanceUtc { get; set; }
    public DateTime? LastExportUtc { get; set; }
    public string LastActivity { get; set; } = "";
}

public sealed class WorklistItemDto
{
    public string Id { get; set; } = "";
    public string AccessionNumber { get; set; } = "";
    public string PatientId { get; set; } = "";
    public string PatientName { get; set; } = "";
    public string BirthDate { get; set; } = "";
    public string ScheduledDate { get; set; } = "";
    public string ScheduledTime { get; set; } = "";
    public string Procedure { get; set; } = "";
    public string State { get; set; } = "";
    public int QueryCount { get; set; }
    public DateTime CreatedUtc { get; set; }
}

public sealed class PendingStudyDto
{
    public string StudyInstanceUid { get; set; } = "";
    public string PatientName { get; set; } = "";
    public string PatientId { get; set; } = "";
    public int InstanceCount { get; set; }
    public DateTime LastInstanceUtc { get; set; }
    public string CallingAeTitle { get; set; } = "";
}

public sealed class GdtProcessRequestDto
{
    /// <summary>Dateipfad. Leer bedeutet „die neueste Datei im Eingangsverzeichnis“.</summary>
    public string Path { get; set; } = "";

    /// <summary>
    /// Ab wann ein bereits verarbeiteter Auftrag als der eigene gilt. Der Aufrufer setzt hier
    /// seinen Startzeitpunkt mit etwas Vorlauf ein, damit ein Auftrag, den die
    /// Verzeichnisüberwachung zwischen Dateiablage und Programmstart abgearbeitet hat, noch
    /// als Erfolg erkannt wird.
    /// </summary>
    public DateTime SinceUtc { get; set; }
}

public sealed class GdtProcessResultDto
{
    public bool Success { get; set; }
    public string AccessionNumber { get; set; } = "";
    public string PatientName { get; set; } = "";
    public string PatientId { get; set; } = "";
    public string Error { get; set; } = "";
}

public sealed class GdtFetchResultDto
{
    public bool Delivered { get; set; }
    public string FileName { get; set; } = "";
    public string PatientName { get; set; } = "";
    public string PatientId { get; set; } = "";
    public int Remaining { get; set; }
    public string Error { get; set; } = "";
}

public sealed class PathCheckDto
{
    public string Label { get; set; } = "";
    public string Path { get; set; } = "";
    public bool IsUnc { get; set; }
    public bool Exists { get; set; }
    public bool CanWrite { get; set; }
    public string Error { get; set; } = "";
    public bool Ok { get; set; }
}

public sealed class PathCheckResultDto
{
    /// <summary>Konto, unter dem der Dienst läuft – entscheidend bei Netzwerkfreigaben.</summary>
    public string ServiceAccount { get; set; } = "";
    public List<PathCheckDto> Checks { get; set; } = new();
}

public sealed class EchoRequestDto
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 104;
    public string CallingAe { get; set; } = "";
    public string CalledAe { get; set; } = "";
}

public sealed class EchoResultDto
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public long ElapsedMilliseconds { get; set; }
}

public static class IpcJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string? json) =>
        string.IsNullOrWhiteSpace(json) ? default : JsonSerializer.Deserialize<T>(json, Options);
}

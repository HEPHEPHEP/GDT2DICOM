namespace Gdt2Dicom.Core.Runtime;

/// <summary>Laufzeitzähler und Zustand, den die GUI über IPC abfragt.</summary>
public sealed class RuntimeStatus
{
    private long _gdtRequestsProcessed;
    private long _gdtRequestsFailed;
    private long _worklistQueries;
    private long _instancesReceived;
    private long _studiesExported;
    private long _gdtResponsesWritten;
    private long _exportFailures;
    private long _associationsAccepted;
    private long _associationsRejected;

    public DateTime StartedUtc { get; } = DateTime.UtcNow;

    public bool DicomServerRunning { get; set; }
    public string DicomServerError { get; set; } = "";
    public bool GdtWatcherRunning { get; set; }
    public string GdtWatcherError { get; set; } = "";

    public long GdtRequestsProcessed => Interlocked.Read(ref _gdtRequestsProcessed);
    public long GdtRequestsFailed => Interlocked.Read(ref _gdtRequestsFailed);
    public long WorklistQueries => Interlocked.Read(ref _worklistQueries);
    public long InstancesReceived => Interlocked.Read(ref _instancesReceived);
    public long StudiesExported => Interlocked.Read(ref _studiesExported);
    public long GdtResponsesWritten => Interlocked.Read(ref _gdtResponsesWritten);
    public long ExportFailures => Interlocked.Read(ref _exportFailures);
    public long AssociationsAccepted => Interlocked.Read(ref _associationsAccepted);
    public long AssociationsRejected => Interlocked.Read(ref _associationsRejected);

    public DateTime? LastGdtRequestUtc { get; private set; }
    public DateTime? LastWorklistQueryUtc { get; private set; }
    public DateTime? LastInstanceUtc { get; private set; }
    public DateTime? LastExportUtc { get; private set; }
    public string LastActivity { get; private set; } = "";

    public void CountGdtRequest(bool success, string description)
    {
        if (success)
            Interlocked.Increment(ref _gdtRequestsProcessed);
        else
            Interlocked.Increment(ref _gdtRequestsFailed);

        LastGdtRequestUtc = DateTime.UtcNow;
        LastActivity = description;
    }

    public void CountWorklistQuery(string callingAe, int matches)
    {
        Interlocked.Increment(ref _worklistQueries);
        LastWorklistQueryUtc = DateTime.UtcNow;
        LastActivity = $"Worklist-Abfrage von {callingAe}: {matches} Treffer";
    }

    public void CountInstance(string sopClassName)
    {
        Interlocked.Increment(ref _instancesReceived);
        LastInstanceUtc = DateTime.UtcNow;
        LastActivity = $"Objekt empfangen: {sopClassName}";
    }

    public void CountStudyExported(string description)
    {
        Interlocked.Increment(ref _studiesExported);
        LastExportUtc = DateTime.UtcNow;
        LastActivity = description;
    }

    public void CountGdtResponse() => Interlocked.Increment(ref _gdtResponsesWritten);

    public void CountExportFailure(string description)
    {
        Interlocked.Increment(ref _exportFailures);
        LastActivity = description;
    }

    public void CountAssociation(bool accepted)
    {
        if (accepted)
            Interlocked.Increment(ref _associationsAccepted);
        else
            Interlocked.Increment(ref _associationsRejected);
    }
}

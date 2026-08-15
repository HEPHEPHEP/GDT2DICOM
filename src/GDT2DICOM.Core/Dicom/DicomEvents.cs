using FellowOakDicom;
using Gdt2Dicom.Core.Configuration;
using Gdt2Dicom.Core.Runtime;
using Gdt2Dicom.Core.Worklist;
using Microsoft.Extensions.Logging;

namespace Gdt2Dicom.Core.Dicom;

/// <summary>Status eines MPPS-Vorgangs, wie ihn das Gerät meldet.</summary>
public enum MppsStatus { InProgress, Completed, Discontinued, Unknown }

public sealed record MppsEvent(
    string SopInstanceUid,
    MppsStatus Status,
    string? StudyInstanceUid,
    string? AccessionNumber,
    string? PatientId,
    DicomDataset Dataset);

public sealed record StorageCommitRequest(
    string TransactionUid,
    IReadOnlyList<(DicomUID SopClassUid, DicomUID SopInstanceUid)> References,
    string CallingAeTitle);

/// <summary>Rückkanal vom DICOM-Server in die Verarbeitungspipeline.</summary>
public interface IDicomEventSink
{
    Task OnInstanceStoredAsync(DicomFile file, string storedPath, string callingAeTitle);
    void OnMpps(MppsEvent mppsEvent);
    Task OnStorageCommitAsync(StorageCommitRequest request);
}

/// <summary>Alles, was der DICOM-Server zur Laufzeit braucht. Wird als UserState übergeben.</summary>
public sealed class DicomServiceContext
{
    public required AppConfig Config { get; init; }
    public required WorklistStore Worklist { get; init; }
    public required RuntimeStatus Status { get; init; }
    public required ILogger Logger { get; init; }
    public required IDicomEventSink Events { get; init; }
}

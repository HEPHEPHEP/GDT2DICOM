using System.Text;
using FellowOakDicom;
using FellowOakDicom.Network;
using Microsoft.Extensions.Logging;

namespace Gdt2Dicom.TestClient;

/// <summary>
/// Minimaler SCP, der nur den N-EVENT-REPORT des Storage Commitment entgegennimmt.
/// Ein echtes Sonogerät hält dafür ebenfalls einen Port offen.
/// </summary>
public sealed class CommitNotificationService :
    DicomService,
    IDicomServiceProvider,
    IDicomCEchoProvider,
    IDicomNServiceProvider
{
    public static volatile bool Received;
    public static int CommittedCount;
    public static int FailedCount;
    public static ushort EventTypeId;

    public CommitNotificationService(INetworkStream stream, Encoding fallbackEncoding, ILogger logger,
        DicomServiceDependencies dependencies)
        : base(stream, fallbackEncoding, logger, dependencies)
    {
    }

    public async Task OnReceiveAssociationRequestAsync(DicomAssociation association)
    {
        foreach (var pc in association.PresentationContexts)
        {
            if (pc.AbstractSyntax == DicomUID.StorageCommitmentPushModel || pc.AbstractSyntax == DicomUID.Verification)
                pc.AcceptTransferSyntaxes(DicomTransferSyntax.ExplicitVRLittleEndian, DicomTransferSyntax.ImplicitVRLittleEndian);
            else
                pc.SetResult(DicomPresentationContextResult.RejectAbstractSyntaxNotSupported);
        }

        await SendAssociationAcceptAsync(association);
    }

    public async Task OnReceiveAssociationReleaseRequestAsync() => await SendAssociationReleaseResponseAsync();

    public void OnReceiveAbort(DicomAbortSource source, DicomAbortReason reason) { }

    public void OnConnectionClosed(Exception? exception) { }

    public Task<DicomCEchoResponse> OnCEchoRequestAsync(DicomCEchoRequest request) =>
        Task.FromResult(new DicomCEchoResponse(request, DicomStatus.Success));

    public Task<DicomNEventReportResponse> OnNEventReportRequestAsync(DicomNEventReportRequest request)
    {
        EventTypeId = request.EventTypeID;

        var dataset = request.Dataset ?? new DicomDataset();
        CommittedCount = dataset.TryGetSequence(DicomTag.ReferencedSOPSequence, out var ok) ? ok.Items.Count : 0;
        FailedCount = dataset.TryGetSequence(DicomTag.FailedSOPSequence, out var failed) ? failed.Items.Count : 0;
        Received = true;

        return Task.FromResult(new DicomNEventReportResponse(request, DicomStatus.Success));
    }

    public Task<DicomNActionResponse> OnNActionRequestAsync(DicomNActionRequest request) =>
        Task.FromResult(new DicomNActionResponse(request, DicomStatus.NoSuchActionType));

    public Task<DicomNCreateResponse> OnNCreateRequestAsync(DicomNCreateRequest request) =>
        Task.FromResult(new DicomNCreateResponse(request, DicomStatus.SOPClassNotSupported));

    public Task<DicomNDeleteResponse> OnNDeleteRequestAsync(DicomNDeleteRequest request) =>
        Task.FromResult(new DicomNDeleteResponse(request, DicomStatus.SOPClassNotSupported));

    public Task<DicomNGetResponse> OnNGetRequestAsync(DicomNGetRequest request) =>
        Task.FromResult(new DicomNGetResponse(request, DicomStatus.SOPClassNotSupported));

    public Task<DicomNSetResponse> OnNSetRequestAsync(DicomNSetRequest request) =>
        Task.FromResult(new DicomNSetResponse(request, DicomStatus.SOPClassNotSupported));
}

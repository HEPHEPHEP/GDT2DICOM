using System.Text;
using FellowOakDicom;
using FellowOakDicom.Network;
using Gdt2Dicom.Core.Worklist;
using Microsoft.Extensions.Logging;

namespace Gdt2Dicom.Core.Dicom;

/// <summary>
/// Der DICOM-Server der Middleware. Bedient C-ECHO, Modality Worklist (C-FIND),
/// Storage (C-STORE), MPPS (N-CREATE/N-SET) und Storage Commitment (N-ACTION).
/// </summary>
public sealed class Gdt2DicomService :
    DicomService,
    IDicomServiceProvider,
    IDicomCEchoProvider,
    IDicomCFindProvider,
    IDicomCStoreProvider,
    IDicomNServiceProvider
{
    private static readonly DicomTransferSyntax[] QueryTransferSyntaxes =
    {
        DicomTransferSyntax.ExplicitVRLittleEndian,
        DicomTransferSyntax.ImplicitVRLittleEndian
    };

    private static readonly DicomTransferSyntax[] StorageTransferSyntaxes =
    {
        // Unkomprimiert zuerst, damit Bildexport ohne Codec funktioniert.
        DicomTransferSyntax.ExplicitVRLittleEndian,
        DicomTransferSyntax.ImplicitVRLittleEndian,
        DicomTransferSyntax.RLELossless,
        DicomTransferSyntax.JPEGLSLossless,
        DicomTransferSyntax.JPEGLSNearLossless,
        DicomTransferSyntax.JPEG2000Lossless,
        DicomTransferSyntax.JPEG2000Lossy,
        DicomTransferSyntax.JPEGProcess14SV1,
        DicomTransferSyntax.JPEGProcess1,
        DicomTransferSyntax.JPEGProcess2_4
    };

    private DicomServiceContext Context => (DicomServiceContext)UserState!;

    private string _callingAe = "";

    public Gdt2DicomService(INetworkStream stream, Encoding fallbackEncoding, ILogger logger,
        DicomServiceDependencies dependencies)
        : base(stream, fallbackEncoding, logger, dependencies)
    {
    }

    // -----------------------------------------------------------------------
    // Association
    // -----------------------------------------------------------------------

    public async Task OnReceiveAssociationRequestAsync(DicomAssociation association)
    {
        var context = Context;
        var config = context.Config.Dicom;
        _callingAe = association.CallingAE ?? "";

        context.Logger.LogInformation(
            "Association-Anfrage von {CallingAe} ({RemoteHost}) an {CalledAe}",
            association.CallingAE, association.RemoteHost, association.CalledAE);

        if (!config.AcceptAnyCalledAe &&
            !string.Equals(association.CalledAE, config.AeTitle, StringComparison.OrdinalIgnoreCase))
        {
            context.Logger.LogWarning("Abgelehnt: Called AE {CalledAe} entspricht nicht {Expected}.",
                association.CalledAE, config.AeTitle);
            context.Status.CountAssociation(accepted: false);
            await SendAssociationRejectAsync(DicomRejectResult.Permanent, DicomRejectSource.ServiceUser,
                DicomRejectReason.CalledAENotRecognized);
            return;
        }

        if (!config.AcceptAnyCallingAe &&
            !config.AllowedCallingAeTitles.Any(ae => string.Equals(ae, association.CallingAE, StringComparison.OrdinalIgnoreCase)))
        {
            context.Logger.LogWarning("Abgelehnt: Calling AE {CallingAe} steht nicht auf der Positivliste.", association.CallingAE);
            context.Status.CountAssociation(accepted: false);
            await SendAssociationRejectAsync(DicomRejectResult.Permanent, DicomRejectSource.ServiceUser,
                DicomRejectReason.CallingAENotRecognized);
            return;
        }

        var anyAccepted = false;

        foreach (var pc in association.PresentationContexts)
        {
            if (pc.AbstractSyntax == DicomUID.Verification)
            {
                pc.AcceptTransferSyntaxes(QueryTransferSyntaxes);
                anyAccepted = true;
            }
            else if (pc.AbstractSyntax == DicomUID.ModalityWorklistInformationModelFind && config.EnableWorklist)
            {
                pc.AcceptTransferSyntaxes(QueryTransferSyntaxes);
                anyAccepted = true;
            }
            else if (pc.AbstractSyntax == DicomUID.ModalityPerformedProcedureStep && config.EnableMpps)
            {
                pc.AcceptTransferSyntaxes(QueryTransferSyntaxes);
                anyAccepted = true;
            }
            else if (pc.AbstractSyntax == DicomUID.StorageCommitmentPushModel && config.EnableStorageCommit)
            {
                pc.AcceptTransferSyntaxes(QueryTransferSyntaxes);
                anyAccepted = true;
            }
            else if (pc.AbstractSyntax.StorageCategory != DicomStorageCategory.None && config.EnableStorage)
            {
                pc.AcceptTransferSyntaxes(StorageTransferSyntaxes);
                anyAccepted = true;
            }
            else
            {
                context.Logger.LogDebug("Presentation Context abgelehnt: {AbstractSyntax}", pc.AbstractSyntax);
                pc.SetResult(DicomPresentationContextResult.RejectAbstractSyntaxNotSupported);
            }
        }

        if (!anyAccepted)
        {
            context.Logger.LogWarning("Keiner der angebotenen Presentation Contexts wird unterstützt.");
            context.Status.CountAssociation(accepted: false);
            await SendAssociationRejectAsync(DicomRejectResult.Permanent, DicomRejectSource.ServiceUser,
                DicomRejectReason.NoReasonGiven);
            return;
        }

        context.Status.CountAssociation(accepted: true);
        await SendAssociationAcceptAsync(association);
    }

    public async Task OnReceiveAssociationReleaseRequestAsync()
    {
        await SendAssociationReleaseResponseAsync();
    }

    public void OnReceiveAbort(DicomAbortSource source, DicomAbortReason reason)
    {
        Context.Logger.LogWarning("Association abgebrochen von {Source}: {Reason}", source, reason);
    }

    public void OnConnectionClosed(Exception? exception)
    {
        if (exception is not null)
            Context.Logger.LogWarning(exception, "Verbindung zu {CallingAe} unerwartet beendet.", _callingAe);
    }

    // -----------------------------------------------------------------------
    // C-ECHO
    // -----------------------------------------------------------------------

    public Task<DicomCEchoResponse> OnCEchoRequestAsync(DicomCEchoRequest request)
    {
        Context.Logger.LogInformation("C-ECHO von {CallingAe} beantwortet.", _callingAe);
        return Task.FromResult(new DicomCEchoResponse(request, DicomStatus.Success));
    }

    // -----------------------------------------------------------------------
    // C-FIND – Modality Worklist
    // -----------------------------------------------------------------------

    public async IAsyncEnumerable<DicomCFindResponse> OnCFindRequestAsync(DicomCFindRequest request)
    {
        var context = Context;

        // Nicht über request.Level gehen: fo-dicom meldet für Worklist-Anfragen
        // "NotApplicable". Maßgeblich ist die SOP-Klasse der Anfrage.
        if (request.SOPClassUID != DicomUID.ModalityWorklistInformationModelFind)
        {
            context.Logger.LogWarning("C-FIND für nicht unterstützte SOP-Klasse {SopClass} abgelehnt.",
                request.SOPClassUID?.Name ?? "unbekannt");
            yield return new DicomCFindResponse(request, DicomStatus.QueryRetrieveIdentifierDoesNotMatchSOPClass);
            yield break;
        }

        var candidates = context.Worklist.GetSchedulable();
        var matches = WorklistMatcher.Filter(candidates, request.Dataset).ToList();

        context.Logger.LogInformation(
            "Worklist-Abfrage von {CallingAe}: {Matches} von {Total} Einträgen passen.",
            _callingAe, matches.Count, candidates.Count);
        context.Status.CountWorklistQuery(_callingAe, matches.Count);

        foreach (var item in matches)
        {
            context.Worklist.Update(item.Id, i =>
            {
                i.QueryCount++;
                i.LastQueriedUtc = DateTime.UtcNow;
            });

            yield return new DicomCFindResponse(request, DicomStatus.Pending)
            {
                Dataset = WorklistDataset.Build(item, request.Dataset)
            };
        }

        yield return new DicomCFindResponse(request, DicomStatus.Success);
        await Task.CompletedTask;
    }

    // -----------------------------------------------------------------------
    // C-STORE
    // -----------------------------------------------------------------------

    public async Task<DicomCStoreResponse> OnCStoreRequestAsync(DicomCStoreRequest request)
    {
        var context = Context;

        try
        {
            var sopInstanceUid = request.SOPInstanceUID?.UID ?? UidHelper.Generate(context.Config.Worklist.UidRoot);
            var directory = context.Config.Dicom.IncomingDirectory;
            Directory.CreateDirectory(directory);

            var path = Path.Combine(directory, sopInstanceUid + ".dcm");
            await request.File.SaveAsync(path);

            context.Status.CountInstance(request.SOPClassUID?.Name ?? "unbekannt");
            context.Logger.LogInformation("Objekt empfangen von {CallingAe}: {SopClass} → {Path}",
                _callingAe, request.SOPClassUID?.Name, Path.GetFileName(path));

            await context.Events.OnInstanceStoredAsync(request.File, path, _callingAe);

            return new DicomCStoreResponse(request, DicomStatus.Success);
        }
        catch (Exception ex)
        {
            context.Logger.LogError(ex, "C-STORE fehlgeschlagen.");
            return new DicomCStoreResponse(request, DicomStatus.ProcessingFailure);
        }
    }

    public Task OnCStoreRequestExceptionAsync(string tempFileName, Exception e)
    {
        Context.Logger.LogError(e, "Fehler beim Empfang nach {TempFile}.", tempFileName);
        return Task.CompletedTask;
    }

    // -----------------------------------------------------------------------
    // N-Services: MPPS und Storage Commitment
    // -----------------------------------------------------------------------

    public Task<DicomNCreateResponse> OnNCreateRequestAsync(DicomNCreateRequest request)
    {
        var context = Context;

        if (request.SOPClassUID != DicomUID.ModalityPerformedProcedureStep)
            return Task.FromResult(new DicomNCreateResponse(request, DicomStatus.SOPClassNotSupported));

        var dataset = request.Dataset ?? new DicomDataset();
        var mppsUid = request.SOPInstanceUID?.UID ?? UidHelper.Generate(context.Config.Worklist.UidRoot);

        var evt = BuildMppsEvent(mppsUid, dataset);
        context.Logger.LogInformation("MPPS N-CREATE von {CallingAe}: {Uid}, Status {Status}",
            _callingAe, mppsUid, evt.Status);
        context.Events.OnMpps(evt);

        return Task.FromResult(new DicomNCreateResponse(request, DicomStatus.Success));
    }

    public Task<DicomNSetResponse> OnNSetRequestAsync(DicomNSetRequest request)
    {
        var context = Context;

        if (request.SOPClassUID != DicomUID.ModalityPerformedProcedureStep)
            return Task.FromResult(new DicomNSetResponse(request, DicomStatus.SOPClassNotSupported));

        var dataset = request.Dataset ?? new DicomDataset();
        var mppsUid = request.SOPInstanceUID?.UID ?? "";

        var evt = BuildMppsEvent(mppsUid, dataset);
        context.Logger.LogInformation("MPPS N-SET von {CallingAe}: {Uid}, Status {Status}",
            _callingAe, mppsUid, evt.Status);
        context.Events.OnMpps(evt);

        return Task.FromResult(new DicomNSetResponse(request, DicomStatus.Success));
    }

    public async Task<DicomNActionResponse> OnNActionRequestAsync(DicomNActionRequest request)
    {
        var context = Context;

        if (request.SOPClassUID != DicomUID.StorageCommitmentPushModel || request.ActionTypeID != 1)
            return new DicomNActionResponse(request, DicomStatus.NoSuchActionType);

        var dataset = request.Dataset ?? new DicomDataset();
        var transactionUid = dataset.GetSingleValueOrDefault(DicomTag.TransactionUID, string.Empty);

        var references = new List<(DicomUID, DicomUID)>();
        if (dataset.TryGetSequence(DicomTag.ReferencedSOPSequence, out var sequence))
        {
            foreach (var item in sequence.Items)
            {
                var sopClass = item.GetSingleValueOrDefault(DicomTag.ReferencedSOPClassUID, string.Empty);
                var sopInstance = item.GetSingleValueOrDefault(DicomTag.ReferencedSOPInstanceUID, string.Empty);
                if (!string.IsNullOrEmpty(sopClass) && !string.IsNullOrEmpty(sopInstance))
                    references.Add((DicomUID.Parse(sopClass), DicomUID.Parse(sopInstance)));
            }
        }

        context.Logger.LogInformation(
            "Storage-Commitment-Anfrage von {CallingAe}: {Count} Objekte, Transaktion {Uid}",
            _callingAe, references.Count, transactionUid);

        // Der eigentliche N-EVENT-REPORT geht laut Standard über eine eigene Association
        // zurück an das Gerät. Das erledigt die Pipeline asynchron.
        _ = Task.Run(() => context.Events.OnStorageCommitAsync(
            new StorageCommitRequest(transactionUid, references, _callingAe)));

        return await Task.FromResult(new DicomNActionResponse(request, DicomStatus.Success));
    }

    public Task<DicomNDeleteResponse> OnNDeleteRequestAsync(DicomNDeleteRequest request) =>
        Task.FromResult(new DicomNDeleteResponse(request, DicomStatus.SOPClassNotSupported));

    public Task<DicomNEventReportResponse> OnNEventReportRequestAsync(DicomNEventReportRequest request) =>
        Task.FromResult(new DicomNEventReportResponse(request, DicomStatus.Success));

    public Task<DicomNGetResponse> OnNGetRequestAsync(DicomNGetRequest request) =>
        Task.FromResult(new DicomNGetResponse(request, DicomStatus.SOPClassNotSupported));

    // -----------------------------------------------------------------------

    private static MppsEvent BuildMppsEvent(string mppsUid, DicomDataset dataset)
    {
        var statusText = dataset.GetSingleValueOrDefault(DicomTag.PerformedProcedureStepStatus, string.Empty);
        var status = statusText.Trim().ToUpperInvariant() switch
        {
            "IN PROGRESS" => MppsStatus.InProgress,
            "COMPLETED" => MppsStatus.Completed,
            "DISCONTINUED" => MppsStatus.Discontinued,
            _ => MppsStatus.Unknown
        };

        // Study Instance UID und Accession stecken in der Scheduled Step Attributes Sequence.
        string? studyUid = null;
        string? accession = null;

        if (dataset.TryGetSequence(DicomTag.ScheduledStepAttributesSequence, out var scheduled) && scheduled.Items.Count > 0)
        {
            var item = scheduled.Items[0];
            studyUid = item.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, string.Empty);
            accession = item.GetSingleValueOrDefault(DicomTag.AccessionNumber, string.Empty);
        }

        studyUid = string.IsNullOrWhiteSpace(studyUid)
            ? dataset.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, string.Empty)
            : studyUid;

        var patientId = dataset.GetSingleValueOrDefault(DicomTag.PatientID, string.Empty);

        return new MppsEvent(mppsUid, status, studyUid, accession, patientId, dataset);
    }
}

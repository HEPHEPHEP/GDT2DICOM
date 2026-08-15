using System.Diagnostics;
using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using Gdt2Dicom.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace Gdt2Dicom.Core.Dicom;

public sealed record EchoResult(bool Success, string Message, long ElapsedMilliseconds);

/// <summary>Ausgehende DICOM-Verbindungen: Verbindungstest und Storage-Commitment-Rückmeldung.</summary>
public static class DicomScu
{
    public static async Task<EchoResult> EchoAsync(
        string host, int port, string callingAe, string calledAe, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var client = DicomClientFactory.Create(host, port, useTls: false, callingAe, calledAe);
            client.ClientOptions.AssociationRequestTimeoutInMs = 10000;
            client.ClientOptions.AssociationReleaseTimeoutInMs = 10000;

            DicomStatus? status = null;
            var request = new DicomCEchoRequest
            {
                OnResponseReceived = (_, response) => status = response.Status
            };

            await client.AddRequestAsync(request);
            await client.SendAsync(cancellationToken);

            stopwatch.Stop();

            if (status is null)
                return new EchoResult(false, "Keine Antwort erhalten.", stopwatch.ElapsedMilliseconds);

            return status == DicomStatus.Success
                ? new EchoResult(true, $"Erfolg – Antwort in {stopwatch.ElapsedMilliseconds} ms.", stopwatch.ElapsedMilliseconds)
                : new EchoResult(false, $"Gegenstelle antwortete mit Status {status}.", stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new EchoResult(false, ex.Message, stopwatch.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// Schickt das Ergebnis einer Storage-Commitment-Anfrage per N-EVENT-REPORT zurück.
    /// Laut Standard läuft das über eine neue Association zum anfragenden Gerät, deshalb
    /// muss dessen Host/Port in der Konfiguration hinterlegt sein.
    /// </summary>
    public static async Task<bool> SendStorageCommitResultAsync(
        RemoteNodeConfig target,
        string localAeTitle,
        string transactionUid,
        IReadOnlyList<(DicomUID SopClassUid, DicomUID SopInstanceUid)> committed,
        IReadOnlyList<(DicomUID SopClassUid, DicomUID SopInstanceUid)> failed,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var dataset = new DicomDataset
            {
                { DicomTag.TransactionUID, transactionUid }
            };

            if (committed.Count > 0)
            {
                var items = committed.Select(r => new DicomDataset
                {
                    { DicomTag.ReferencedSOPClassUID, r.SopClassUid },
                    { DicomTag.ReferencedSOPInstanceUID, r.SopInstanceUid }
                }).ToArray();
                dataset.Add(new DicomSequence(DicomTag.ReferencedSOPSequence, items));
            }

            if (failed.Count > 0)
            {
                var items = failed.Select(r => new DicomDataset
                {
                    { DicomTag.ReferencedSOPClassUID, r.SopClassUid },
                    { DicomTag.ReferencedSOPInstanceUID, r.SopInstanceUid },
                    { DicomTag.FailureReason, (ushort)0x0110 } // Processing failure
                }).ToArray();
                dataset.Add(new DicomSequence(DicomTag.FailedSOPSequence, items));
            }

            // Event Type 1 = alle Objekte gesichert, 2 = mindestens ein Fehlschlag.
            var eventTypeId = (ushort)(failed.Count == 0 ? 1 : 2);

            var request = new DicomNEventReportRequest(
                DicomUID.StorageCommitmentPushModel,
                DicomUID.StorageCommitmentPushModelInstance,
                eventTypeId)
            {
                Dataset = dataset
            };

            var client = DicomClientFactory.Create(target.Host, target.Port, useTls: false, localAeTitle, target.AeTitle);
            client.ClientOptions.AssociationRequestTimeoutInMs = 15000;

            await client.AddRequestAsync(request);
            await client.SendAsync(cancellationToken);

            logger.LogInformation(
                "Storage-Commitment-Ergebnis an {Target} gesendet: {Ok} bestätigt, {Failed} fehlgeschlagen.",
                target, committed.Count, failed.Count);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Storage-Commitment-Rückmeldung an {Target} fehlgeschlagen.", target);
            return false;
        }
    }
}

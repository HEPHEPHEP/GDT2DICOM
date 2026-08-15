using FellowOakDicom;
using Gdt2Dicom.Core.Gdt;
using Gdt2Dicom.Core.Worklist;

namespace Gdt2Dicom.Core.Dicom;

/// <summary>Baut aus einem Worklist-Eintrag das Antwort-Dataset einer C-FIND-Antwort.</summary>
public static class WorklistDataset
{
    /// <summary>
    /// Erzeugt die Antwort. Es werden immer alle üblichen Attribute geliefert, auch solche,
    /// die das Gerät nicht angefragt hat – das ist zulässig und erspart Ärger mit Geräten,
    /// die ihre Anfrage unvollständig stellen.
    /// </summary>
    public static DicomDataset Build(WorklistItem item, DicomDataset query)
    {
        var ds = new DicomDataset(DicomTransferSyntax.ImplicitVRLittleEndian)
        {
            { DicomTag.SpecificCharacterSet, "ISO_IR 100" }
        };

        // --- Patient ---
        ds.AddOrUpdate(DicomTag.PatientName, GdtValues.BuildDicomPersonName(item.PatientLastName, item.PatientFirstName, item.PatientTitle));
        ds.AddOrUpdate(DicomTag.PatientID, item.PatientId);
        ds.AddOrUpdate(DicomTag.PatientBirthDate, item.PatientBirthDate);
        ds.AddOrUpdate(DicomTag.PatientSex, item.PatientSex);
        ds.AddOrUpdate(DicomTag.PatientComments, string.Empty);

        if (!string.IsNullOrWhiteSpace(item.PatientSize))
            ds.AddOrUpdate(DicomTag.PatientSize, item.PatientSize);
        if (!string.IsNullOrWhiteSpace(item.PatientWeight))
            ds.AddOrUpdate(DicomTag.PatientWeight, item.PatientWeight);

        ds.AddOrUpdate(DicomTag.PregnancyStatus, (ushort)4); // 4 = unbekannt
        ds.AddOrUpdate(DicomTag.MedicalAlerts, string.Empty);
        ds.AddOrUpdate(DicomTag.Allergies, string.Empty);

        // --- Auftrag / Imaging Service Request ---
        ds.AddOrUpdate(DicomTag.AccessionNumber, item.AccessionNumber);
        ds.AddOrUpdate(DicomTag.StudyInstanceUID, item.StudyInstanceUid);
        ds.AddOrUpdate(DicomTag.ReferringPhysicianName, GdtValues.Sanitize(item.ReferringPhysicianName));
        ds.AddOrUpdate(DicomTag.RequestingPhysician, GdtValues.Sanitize(item.ReferringPhysicianName));
        ds.AddOrUpdate(DicomTag.RequestingService, string.Empty);
        ds.AddOrUpdate(DicomTag.RequestedProcedureID, item.RequestedProcedureId);
        ds.AddOrUpdate(DicomTag.RequestedProcedureDescription, item.RequestedProcedureDescription);
        ds.AddOrUpdate(DicomTag.RequestedProcedurePriority, "MEDIUM");
        ds.AddOrUpdate(DicomTag.InstitutionName, item.InstitutionName);
        ds.AddOrUpdate(DicomTag.AdmissionID, string.Empty);
        ds.AddOrUpdate(DicomTag.CurrentPatientLocation, string.Empty);
        ds.AddOrUpdate(DicomTag.StudyDate, item.ScheduledDate);
        ds.AddOrUpdate(DicomTag.StudyTime, item.ScheduledTime);

        // --- Scheduled Procedure Step Sequence (0040,0100) ---
        var step = new DicomDataset(DicomTransferSyntax.ImplicitVRLittleEndian);
        step.AddOrUpdate(DicomTag.Modality, item.Modality);
        step.AddOrUpdate(DicomTag.ScheduledStationAETitle, item.ScheduledStationAeTitle);
        step.AddOrUpdate(DicomTag.ScheduledProcedureStepStartDate, item.ScheduledDate);
        step.AddOrUpdate(DicomTag.ScheduledProcedureStepStartTime, item.ScheduledTime);
        step.AddOrUpdate(DicomTag.ScheduledPerformingPhysicianName, GdtValues.Sanitize(item.PerformingPhysicianName));
        step.AddOrUpdate(DicomTag.ScheduledProcedureStepDescription, item.ScheduledProcedureStepDescription);
        step.AddOrUpdate(DicomTag.ScheduledProcedureStepID, item.ScheduledProcedureStepId);
        step.AddOrUpdate(DicomTag.ScheduledProcedureStepStatus, item.State == WorklistItemState.InProgress ? "IN PROGRESS" : "SCHEDULED");
        step.AddOrUpdate(DicomTag.ScheduledStationName, string.Empty);
        step.AddOrUpdate(DicomTag.ScheduledProcedureStepLocation, string.Empty);
        step.AddOrUpdate(DicomTag.CommentsOnTheScheduledProcedureStep, string.Empty);
        step.AddOrUpdate(new DicomSequence(DicomTag.ScheduledProtocolCodeSequence));

        ds.AddOrUpdate(new DicomSequence(DicomTag.ScheduledProcedureStepSequence, step));
        ds.AddOrUpdate(new DicomSequence(DicomTag.ReferencedStudySequence));
        ds.AddOrUpdate(new DicomSequence(DicomTag.RequestedProcedureCodeSequence));

        // Attribute, die das Gerät angefragt hat, hier aber nicht belegt sind, als leeren Wert
        // nachtragen – manche Geräte werten eine Antwort sonst als unvollständig. Binäre VRs
        // werden übersprungen, die kommen in einer Worklist-Anfrage ohnehin nicht vor.
        foreach (var element in query)
        {
            if (element.Tag.Group == 0x0000 || element.Tag == DicomTag.SpecificCharacterSet)
                continue;
            if (ds.Contains(element.Tag) || element is DicomSequence)
                continue;
            if (!element.ValueRepresentation.IsString)
                continue;

            try
            {
                ds.AddOrUpdate(element.Tag, string.Empty);
            }
            catch (DicomDataException)
            {
                // Unbekannte private Tags ohne Wörterbucheintrag einfach auslassen.
            }
        }

        return ds;
    }
}

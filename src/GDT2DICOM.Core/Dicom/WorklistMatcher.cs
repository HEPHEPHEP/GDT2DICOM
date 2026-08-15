using FellowOakDicom;
using Gdt2Dicom.Core.Worklist;

namespace Gdt2Dicom.Core.Dicom;

/// <summary>
/// Wertet die Matching-Keys einer Modality-Worklist-C-FIND-Anfrage aus.
/// Unterstützt Single Value Matching, Wildcards (* und ?), Datums-/Zeitbereiche
/// und Universal Matching (leerer Wert = alles).
/// </summary>
public static class WorklistMatcher
{
    public static IEnumerable<WorklistItem> Filter(IEnumerable<WorklistItem> items, DicomDataset query)
    {
        var patientName = query.GetSingleValueOrDefault(DicomTag.PatientName, string.Empty);
        var patientId = query.GetSingleValueOrDefault(DicomTag.PatientID, string.Empty);
        var accession = query.GetSingleValueOrDefault(DicomTag.AccessionNumber, string.Empty);
        var birthDate = query.GetSingleValueOrDefault(DicomTag.PatientBirthDate, string.Empty);
        var studyUid = query.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, string.Empty);
        var requestedProcedureId = query.GetSingleValueOrDefault(DicomTag.RequestedProcedureID, string.Empty);

        // Die Keys der geplanten Prozedur stecken in der Sequenz (0040,0100).
        string modality = "", stationAe = "", startDate = "", startTime = "", performingPhysician = "", stepId = "";
        if (query.TryGetSequence(DicomTag.ScheduledProcedureStepSequence, out var sequence) && sequence.Items.Count > 0)
        {
            var step = sequence.Items[0];
            modality = step.GetSingleValueOrDefault(DicomTag.Modality, string.Empty);
            stationAe = step.GetSingleValueOrDefault(DicomTag.ScheduledStationAETitle, string.Empty);
            startDate = step.GetSingleValueOrDefault(DicomTag.ScheduledProcedureStepStartDate, string.Empty);
            startTime = step.GetSingleValueOrDefault(DicomTag.ScheduledProcedureStepStartTime, string.Empty);
            performingPhysician = step.GetSingleValueOrDefault(DicomTag.ScheduledPerformingPhysicianName, string.Empty);
            stepId = step.GetSingleValueOrDefault(DicomTag.ScheduledProcedureStepID, string.Empty);
        }

        foreach (var item in items)
        {
            if (!TextMatches(patientName, $"{item.PatientLastName}^{item.PatientFirstName}")) continue;
            if (!TextMatches(patientId, item.PatientId)) continue;
            if (!TextMatches(accession, item.AccessionNumber)) continue;
            if (!TextMatches(studyUid, item.StudyInstanceUid)) continue;
            if (!TextMatches(requestedProcedureId, item.RequestedProcedureId)) continue;
            if (!RangeMatches(birthDate, item.PatientBirthDate)) continue;

            if (!TextMatches(modality, item.Modality)) continue;
            if (!TextMatches(stepId, item.ScheduledProcedureStepId)) continue;
            if (!TextMatches(performingPhysician, item.PerformingPhysicianName)) continue;
            if (!RangeMatches(startDate, item.ScheduledDate)) continue;
            if (!RangeMatches(startTime, item.ScheduledTime)) continue;

            // Beim Station-AE nur filtern, wenn der Eintrag überhaupt einen gesetzt hat –
            // sonst bekäme ein Gerät, das streng nach seinem eigenen AE fragt, nie etwas.
            if (!string.IsNullOrWhiteSpace(stationAe)
                && !string.IsNullOrWhiteSpace(item.ScheduledStationAeTitle)
                && !TextMatches(stationAe, item.ScheduledStationAeTitle))
                continue;

            yield return item;
        }
    }

    /// <summary>Single Value bzw. Wildcard Matching. Ein leerer Key trifft immer.</summary>
    public static bool TextMatches(string? key, string? value)
    {
        if (string.IsNullOrWhiteSpace(key))
            return true;

        var pattern = key.Trim();
        var candidate = value ?? "";

        if (!pattern.Contains('*') && !pattern.Contains('?'))
            return string.Equals(pattern.TrimEnd('^'), candidate.TrimEnd('^'), StringComparison.OrdinalIgnoreCase);

        var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace("\\*", ".*")
            .Replace("\\?", ".") + "$";

        return System.Text.RegularExpressions.Regex.IsMatch(candidate, regex,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Range Matching für DA/TM: "20240101-20240131", "20240101-", "-20240131" oder Einzelwert.
    /// Ein leerer Wert im Eintrag gilt als Treffer, damit unvollständige Aufträge sichtbar bleiben.
    /// </summary>
    public static bool RangeMatches(string? key, string? value)
    {
        if (string.IsNullOrWhiteSpace(key))
            return true;
        if (string.IsNullOrWhiteSpace(value))
            return true;

        var pattern = key.Trim();
        var candidate = value.Trim();

        if (!pattern.Contains('-'))
            return TextMatches(pattern, candidate);

        var parts = pattern.Split('-', 2);
        var from = parts[0].Trim();
        var to = parts.Length > 1 ? parts[1].Trim() : "";

        if (!string.IsNullOrEmpty(from) && string.CompareOrdinal(candidate.PadRight(from.Length, '0'), from) < 0)
            return false;
        if (!string.IsNullOrEmpty(to) && string.CompareOrdinal(candidate.PadRight(to.Length, '0'), to) > 0)
            return false;

        return true;
    }
}

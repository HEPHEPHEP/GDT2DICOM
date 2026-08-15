namespace Gdt2Dicom.Core.Worklist;

public enum WorklistItemState
{
    /// <summary>Wartet darauf, vom Gerät abgerufen zu werden.</summary>
    Scheduled,
    /// <summary>Gerät hat die Untersuchung per MPPS gestartet.</summary>
    InProgress,
    /// <summary>Untersuchung abgeschlossen, Rücksatz noch offen.</summary>
    Completed,
    /// <summary>Vom Gerät abgebrochen.</summary>
    Discontinued,
    /// <summary>Rücksatz an das PVS wurde geschrieben.</summary>
    Exported
}

/// <summary>
/// Ein Auftrag aus dem PVS. Dient gleichzeitig als Datenquelle für die DICOM-Worklist
/// und als Kontext für den GDT-Rücksatz.
/// </summary>
public sealed class WorklistItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public WorklistItemState State { get; set; } = WorklistItemState.Scheduled;

    // --- Patient ---
    public string PatientId { get; set; } = "";
    public string PatientLastName { get; set; } = "";
    public string PatientFirstName { get; set; } = "";
    public string PatientTitle { get; set; } = "";
    /// <summary>DICOM-Format JJJJMMTT.</summary>
    public string PatientBirthDate { get; set; } = "";
    /// <summary>M, F oder O.</summary>
    public string PatientSex { get; set; } = "";
    /// <summary>Größe in Metern (DICOM Patient Size).</summary>
    public string PatientSize { get; set; } = "";
    /// <summary>Gewicht in kg.</summary>
    public string PatientWeight { get; set; } = "";
    public string PatientAddress { get; set; } = "";

    // --- Auftrag ---
    public string AccessionNumber { get; set; } = "";
    public string StudyInstanceUid { get; set; } = "";
    public string RequestedProcedureId { get; set; } = "";
    public string RequestedProcedureDescription { get; set; } = "";
    public string ScheduledProcedureStepId { get; set; } = "";
    public string ScheduledProcedureStepDescription { get; set; } = "";
    public string Modality { get; set; } = "US";
    public string ScheduledStationAeTitle { get; set; } = "";
    /// <summary>DICOM-Format JJJJMMTT.</summary>
    public string ScheduledDate { get; set; } = "";
    /// <summary>DICOM-Format HHMMSS.</summary>
    public string ScheduledTime { get; set; } = "";
    public string ReferringPhysicianName { get; set; } = "";
    public string PerformingPhysicianName { get; set; } = "";
    public string InstitutionName { get; set; } = "";

    // --- Herkunft aus GDT ---
    public string SourceGdtFile { get; set; } = "";
    /// <summary>FK 8316 des eingehenden Auftrags: die GDT-ID des PVS, an die zurückgeantwortet wird.</summary>
    public string GdtRequesterId { get; set; } = "";
    /// <summary>FK 8315 des eingehenden Auftrags: die GDT-ID, unter der die Middleware adressiert wurde.</summary>
    public string GdtAddressedId { get; set; } = "";
    /// <summary>FK 8402 – geräte-/verfahrensspezifisches Kennfeld aus dem Auftrag.</summary>
    public string GdtDeviceIdent { get; set; } = "";
    /// <summary>FK 8410 – Anforderungskennung aus dem Auftrag.</summary>
    public string GdtRequestIdent { get; set; } = "";

    /// <summary>Zusätzliche Felder des Auftrags, die im Rücksatz gespiegelt werden sollen.</summary>
    public Dictionary<string, string> EchoFields { get; set; } = new();

    // --- MPPS ---
    public string? MppsSopInstanceUid { get; set; }
    public DateTime? MppsStartedUtc { get; set; }
    public DateTime? MppsCompletedUtc { get; set; }

    /// <summary>Wie oft der Eintrag schon per C-FIND ausgeliefert wurde.</summary>
    public int QueryCount { get; set; }
    public DateTime? LastQueriedUtc { get; set; }

    public string DisplayName =>
        string.IsNullOrWhiteSpace(PatientFirstName) ? PatientLastName : $"{PatientLastName}, {PatientFirstName}";

    public override string ToString() => $"{AccessionNumber} {DisplayName} ({PatientId}) [{State}]";
}

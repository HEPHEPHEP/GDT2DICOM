using System.Collections.ObjectModel;
using System.ComponentModel;

namespace Gdt2Dicom.Core.Configuration;

/// <summary>Wurzel der Konfiguration. Wird als JSON unter %ProgramData%\GDT2DICOM\config.json abgelegt.</summary>
public sealed class AppConfig
{
    /// <summary>Schemaversion, damit spätere Migrationen möglich sind.</summary>
    public int SchemaVersion { get; set; } = 1;

    public GeneralConfig General { get; set; } = new();
    public GdtConfig Gdt { get; set; } = new();
    public DicomConfig Dicom { get; set; } = new();
    public WorklistConfig Worklist { get; set; } = new();
    public ExportConfig Export { get; set; } = new();
    public ConnectorConfig Connector { get; set; } = new();
}

/// <summary>
/// Verhalten von GDT2DICOM.Aufruf.exe – dem Programm, das ein PVS startet, wenn es die
/// GDT-Schnittstelle nicht über ein überwachtes Verzeichnis, sondern per Programmaufruf bedient.
/// </summary>
public sealed class ConnectorConfig
{
    [Description("Nach dem Anlegen des Auftrags auf den Rücksatz warten, statt sofort zurückzukehren. " +
                 "Nur einschalten, wenn das PVS den Rücksatz ausschließlich direkt nach dem Programmende liest.")]
    public bool WaitForResponse { get; set; } = false;

    [Description("Zeitlimit für das Warten auf den Rücksatz in Sekunden. 0 = kein Zeitlimit; " +
                 "dann endet das Warten nur, wenn der Rücksatz eintrifft oder der Anwender abbricht.")]
    public int WaitTimeoutSeconds { get; set; } = 0;

    [Description("Während des Wartens ein Fenster mit Abbruchmöglichkeit anzeigen. Ohne Zeitlimit " +
                 "wird es immer angezeigt – sonst gäbe es keine Möglichkeit, das Warten zu beenden.")]
    public bool ShowWaitWindow { get; set; } = true;

    [Description("Fehler als Meldungsfenster anzeigen. Ausschalten nur, wenn das PVS den Rückgabewert auswertet.")]
    public bool ShowErrorDialogs { get; set; } = true;

    [Description("Auch bei Erfolg eine Meldung anzeigen. Im Alltag störend, für die Einrichtung hilfreich.")]
    public bool ShowSuccessDialog { get; set; } = false;
}

public sealed class GeneralConfig
{
    [Description("Basisverzeichnis für Laufzeitdaten (Worklist-Store, Zwischenablage).")]
    public string DataDirectory { get; set; } = @"C:\ProgramData\GDT2DICOM\data";

    [Description("Verzeichnis für Logdateien.")]
    public string LogDirectory { get; set; } = @"C:\ProgramData\GDT2DICOM\logs";

    [Description("Verbose, Debug, Information, Warning, Error")]
    public string LogLevel { get; set; } = "Information";

    [Description("Logdateien, die älter als LogRetentionDays sind, automatisch löschen.")]
    public bool DeleteOldLogs { get; set; } = true;

    [Description("Aufbewahrungsdauer der Logdateien in Tagen. Wirkt nur, wenn DeleteOldLogs aktiv ist.")]
    public int LogRetentionDays { get; set; } = 30;
}

// ---------------------------------------------------------------------------
// GDT / PVS
// ---------------------------------------------------------------------------

public enum GdtVersion
{
    /// <summary>GDT 2.1 – Versionskennung "02.10".</summary>
    V21,
    /// <summary>GDT 3.0 – Versionskennung "03.00", Objektstruktur mit FK 8200/8201.</summary>
    V30,
    /// <summary>GDT 3.1 – Versionskennung "03.10", Objektstruktur mit FK 8200/8201.</summary>
    V31
}

public enum GdtCharset
{
    /// <summary>FK 9206 = 1, 7-Bit-ASCII (Umlaute werden transliteriert).</summary>
    Ascii7,
    /// <summary>FK 9206 = 2, IBM CP437 (DOS).</summary>
    Cp437,
    /// <summary>FK 9206 = 3, ISO 8859-1 (ANSI) – der Normalfall.</summary>
    Iso8859_1,
    /// <summary>FK 9206 = 4, UTF-8. Nur bei PVS verwenden, die das explizit unterstützen.</summary>
    Utf8
}

public enum ResponseDelivery
{
    /// <summary>
    /// Rücksatz sofort ins Ausgangsverzeichnis legen. Richtig, wenn das PVS das Verzeichnis
    /// ohnehin durchsucht – etwa beim Öffnen der Karteikarte.
    /// </summary>
    Sofort,

    /// <summary>
    /// Rücksatz zurückhalten, bis das PVS ihn per Programmaufruf abholt. Richtig, wenn das
    /// PVS das Importverzeichnis ausschließlich unmittelbar nach dem Ende eines aufgerufenen
    /// Programms einliest.
    /// </summary>
    AufAbruf
}

public enum AttachmentPathMode
{
    /// <summary>Vollständiger Pfad, z. B. C:\GDT\bilder\1234_1.jpg</summary>
    Absolute,
    /// <summary>Pfad relativ zum GDT-Ausgabeverzeichnis.</summary>
    RelativeToOutbox,
    /// <summary>Nur der Dateiname – wenn das PVS ein festes Bildverzeichnis kennt.</summary>
    FileNameOnly
}

public sealed class GdtConfig
{
    [Description("Verzeichnis, in das das PVS seine Auftragsdateien schreibt (PVS → Middleware).")]
    public string InboxDirectory { get; set; } = @"C:\GDT\in";

    [Description("Dateimuster für eingehende Aufträge.")]
    public string InboxFilePattern { get; set; } = "*.gdt";

    [Description("Das Eingangsverzeichnis laufend überwachen. Abschalten, wenn das PVS die " +
                 "Schnittstelle ausschließlich über den Programmaufruf bedient – dann bleibt " +
                 "die Auftragsdatei bis zum Aufruf liegen.")]
    public bool EnableInboxWatcher { get; set; } = true;

    [Description("Verzeichnis, aus dem das PVS die Rücksätze liest (Middleware → PVS).")]
    public string OutboxDirectory { get; set; } = @"C:\GDT\out";

    [Description("Dateinamensmuster für Rücksätze. Platzhalter: {receiver} {sender} {patid} {date} {time} {counter}")]
    public string OutboxFileNamePattern { get; set; } = "{receiver}_{sender}_{counter}.gdt";

    [Description("Startwert für {counter}; wird fortlaufend erhöht und persistiert.")]
    public int OutboxCounterStart { get; set; } = 1;

    [Description("Wann der fertige Rücksatz ins Ausgangsverzeichnis gelegt wird: Sofort oder AufAbruf.")]
    public ResponseDelivery ResponseDelivery { get; set; } = ResponseDelivery.Sofort;

    [Description("Einen neuen Rücksatz zurückhalten, solange im Ausgang noch eine Datei gleichen " +
                 "Namens liegt. Verhindert, dass ein noch nicht abgeholter Befund überschrieben wird.")]
    public bool HoldBackWhileOutboxOccupied { get; set; } = true;

    public GdtVersion Version { get; set; } = GdtVersion.V21;

    public GdtCharset Charset { get; set; } = GdtCharset.Iso8859_1;

    [Description("GDT-ID der Middleware (FK 8316 im Rücksatz).")]
    public string SenderId { get; set; } = "GDT2DICOM";

    [Description("GDT-ID des PVS (FK 8315 im Rücksatz).")]
    public string ReceiverId { get; set; } = "PVSSYS";

    [Description("Geräte-/verfahrensspezifisches Kennfeld (FK 8402), z. B. SONO01.")]
    public string DeviceIdent { get; set; } = "SONO01";

    [Description("Satzart, mit der das PVS eine Untersuchung anfordert.")]
    public string RequestSatzart { get; set; } = "6302";

    [Description("Weitere Satzarten, die ebenfalls als Auftrag akzeptiert werden (z. B. 6300 Stammdaten).")]
    public List<string> AdditionalRequestSatzarten { get; set; } = new() { "6300", "6311" };

    [Description("Satzart des Rücksatzes an das PVS.")]
    public string ResponseSatzart { get; set; } = "6310";

    [Description("Eingelesene Auftragsdatei nach der Verarbeitung löschen.")]
    public bool DeleteInboxFileAfterProcessing { get; set; } = true;

    [Description("Verzeichnis, in das verarbeitete Auftragsdateien kopiert werden. Leer = kein Archiv.")]
    public string InboxArchiveDirectory { get; set; } = @"C:\ProgramData\GDT2DICOM\data\gdt-archiv";

    [Description("Zusätzliches Polling-Intervall in Sekunden, falls der FileSystemWatcher Ereignisse verpasst (Netzlaufwerke).")]
    public int PollIntervalSeconds { get; set; } = 10;

    [Description("Wartezeit in Millisekunden, bis eine neu erkannte Datei als vollständig geschrieben gilt.")]
    public int FileSettleMilliseconds { get; set; } = 750;

    public GdtFieldMap FieldMap { get; set; } = new();
}

/// <summary>
/// Feldkennungen (FK). Die Vorgaben folgen der verbreiteten Konvention; einzelne PVS weichen ab,
/// deshalb ist jede Kennung in der GUI änderbar. Maßgeblich ist immer die GDT-Doku des PVS.
/// </summary>
public sealed class GdtFieldMap
{
    // Kopf
    public string Satzidentifikation { get; set; } = "8000";
    public string Satzlaenge { get; set; } = "8100";
    public string EmpfaengerId { get; set; } = "8315";
    public string SenderId { get; set; } = "8316";
    public string Zeichensatz { get; set; } = "9206";
    public string GdtVersion { get; set; } = "9218";
    public string DeviceIdent { get; set; } = "8402";

    // Patient
    public string PatientId { get; set; } = "3000";
    public string PatientName { get; set; } = "3101";
    public string PatientVorname { get; set; } = "3102";
    public string PatientGeburtsdatum { get; set; } = "3103";
    public string PatientGeschlecht { get; set; } = "3110";
    public string PatientTitel { get; set; } = "3104";
    public string PatientStrasse { get; set; } = "3107";
    public string PatientPlzOrt { get; set; } = "3106";
    public string PatientGroesseCm { get; set; } = "3622";
    public string PatientGewichtKg { get; set; } = "3623";

    // Untersuchung / Auftrag
    public string UntersuchungsDatum { get; set; } = "6200";
    public string UntersuchungsUhrzeit { get; set; } = "6201";
    public string Anforderung { get; set; } = "6205";
    public string AnforderungsIdent { get; set; } = "8410";
    public string Auftragsnummer { get; set; } = "8310";
    public string UeberweiserName { get; set; } = "6000";

    // Befund / Ergebnis
    public string BefundZeile { get; set; } = "6220";
    public string Kommentar { get; set; } = "6226";

    // Anhänge
    public string AnhangFormat { get; set; } = "6302";
    public string AnhangVerweis { get; set; } = "6303";
    public string AnhangBeschreibung { get; set; } = "6304";

    // GDT 3.x Objektklammern
    public string ObjektBeginn { get; set; } = "8200";
    public string ObjektEnde { get; set; } = "8201";
    public string ObjektNameAnhang { get; set; } = "Obj_Anhang";
    public string ObjektNamePatient { get; set; } = "Obj_Patient";
    public string ObjektNameUntersuchung { get; set; } = "Obj_Untersuchung";
}

// ---------------------------------------------------------------------------
// DICOM
// ---------------------------------------------------------------------------

public sealed class DicomConfig
{
    [Description("AE-Titel der Middleware. Muss am Sonogerät als Ziel eingetragen werden.")]
    public string AeTitle { get; set; } = "GDT2DICOM";

    [Description("TCP-Port für alle DICOM-Dienste.")]
    public int Port { get; set; } = 104;

    [Description("Netzwerkschnittstelle, an die gebunden wird. 0.0.0.0 = alle.")]
    public string BindAddress { get; set; } = "0.0.0.0";

    [Description("Maximale PDU-Länge in Bytes.")]
    public uint MaxPduLength { get; set; } = 262144;

    [Description("Verbindungen von beliebigen Calling-AE-Titeln annehmen.")]
    public bool AcceptAnyCallingAe { get; set; } = true;

    [Description("Erlaubte Calling-AE-Titel, wenn AcceptAnyCallingAe = false.")]
    public List<string> AllowedCallingAeTitles { get; set; } = new() { "SONO" };

    [Description("Auch Association-Requests mit abweichendem Called-AE annehmen.")]
    public bool AcceptAnyCalledAe { get; set; } = false;

    public bool EnableWorklist { get; set; } = true;
    public bool EnableStorage { get; set; } = true;
    public bool EnableMpps { get; set; } = true;
    public bool EnableStorageCommit { get; set; } = true;

    [Description("Verzeichnis, in dem eingehende DICOM-Objekte zwischengespeichert werden.")]
    public string IncomingDirectory { get; set; } = @"C:\ProgramData\GDT2DICOM\data\incoming";

    [Description("Timeout für inaktive Associations in Sekunden.")]
    public int AssociationTimeoutSeconds { get; set; } = 60;

    [Description("Gegenstellen für Storage-Commitment-Rückmeldungen (N-EVENT-REPORT) und C-ECHO-Tests.")]
    public ObservableCollection<RemoteNodeConfig> RemoteNodes { get; set; } = new()
    {
        new RemoteNodeConfig { Name = "Sonogerät", AeTitle = "SONO", Host = "192.168.1.50", Port = 104 }
    };
}

public sealed class RemoteNodeConfig
{
    public string Name { get; set; } = "";
    public string AeTitle { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 104;

    public override string ToString() => string.IsNullOrWhiteSpace(Name) ? $"{AeTitle}@{Host}:{Port}" : $"{Name} ({AeTitle}@{Host}:{Port})";
}

// ---------------------------------------------------------------------------
// Worklist
// ---------------------------------------------------------------------------

public enum AccessionNumberMode
{
    /// <summary>Auftragsnummer aus dem GDT-Satz übernehmen, sonst generieren.</summary>
    FromGdtElseGenerated,
    /// <summary>Immer fortlaufend generieren.</summary>
    AlwaysGenerated,
    /// <summary>Patienten-ID + Datum.</summary>
    PatientIdAndDate
}

public sealed class WorklistConfig
{
    [Description("Modality-Wert der Worklist-Einträge (US = Ultraschall).")]
    public string Modality { get; set; } = "US";

    [Description("Scheduled Station AE Title (0040,0001). Leer = wird nicht gesetzt.")]
    public string ScheduledStationAeTitle { get; set; } = "";

    [Description("Institution/Abteilung für Worklist-Einträge.")]
    public string InstitutionName { get; set; } = "";

    [Description("Vorgabe für die Beschreibung der geplanten Untersuchung, wenn der GDT-Satz nichts liefert.")]
    public string DefaultProcedureDescription { get; set; } = "Sonographie";

    public AccessionNumberMode AccessionNumberMode { get; set; } = AccessionNumberMode.FromGdtElseGenerated;

    [Description("Präfix für generierte Accession Numbers.")]
    public string AccessionPrefix { get; set; } = "";

    [Description("Einträge nach dieser Zeit automatisch entfernen.")]
    public int ItemLifetimeHours { get; set; } = 24;

    [Description("Eintrag entfernen, sobald das Gerät per MPPS COMPLETED meldet.")]
    public bool RemoveOnMppsCompleted { get; set; } = true;

    [Description("Eintrag entfernen, sobald die zugehörige Studie empfangen und exportiert wurde.")]
    public bool RemoveAfterStudyExported { get; set; } = true;

    [Description("DICOM-Root-UID für generierte Study Instance UIDs. Für den Produktivbetrieb eine eigene, registrierte OID eintragen.")]
    public string UidRoot { get; set; } = "1.2.276.0.7230010.3.1.4.1";
}

// ---------------------------------------------------------------------------
// Export / Rückrichtung
// ---------------------------------------------------------------------------

public enum ImageOutputFormat { None, Jpeg, Png }

public enum PdfFormat
{
    /// <summary>Gewöhnliches PDF. Kleiner, keine Anforderungen an Schriften und Farbraum.</summary>
    Standard,
    /// <summary>
    /// PDF/A-3b – Langzeitarchivformat. Von der gematik für Dokumente in der
    /// elektronischen Patientenakte gefordert.
    /// </summary>
    PdfA3b
}

public sealed class ExportConfig
{
    [Description("Eine Studie gilt als abgeschlossen, wenn so viele Sekunden lang kein weiteres Bild " +
                 "eintrifft. 0 = keine Ruhezeit. Meldet das Gerät die Untersuchung per MPPS als laufend, " +
                 "ruht die Ruhezeit ohnehin, bis das Gerät den Abschluss meldet.")]
    public int StudyIdleTimeoutSeconds { get; set; } = 60;

    [Description("Harte Obergrenze: Studie spätestens nach so vielen Minuten abschließen.")]
    public int StudyMaxAgeMinutes { get; set; } = 30;

    [Description("Studie sofort abschließen, wenn per MPPS COMPLETED gemeldet wird.")]
    public bool FinalizeOnMppsCompleted { get; set; } = true;

    // --- Einzelbilder ---
    public bool ExportImages { get; set; } = true;
    public ImageOutputFormat ImageFormat { get; set; } = ImageOutputFormat.Jpeg;
    public string ImageDirectory { get; set; } = @"C:\GDT\bilder";
    public int JpegQuality { get; set; } = 88;

    [Description("Bilder auf diese Breite herunterskalieren. 0 = Originalgröße.")]
    public int MaxImageWidth { get; set; } = 0;

    [Description("Bei Multiframe-Objekten (Cine-Loops) nur den ersten Frame exportieren.")]
    public bool FirstFrameOnlyForMultiframe { get; set; } = true;

    [Description("Dateinamensmuster. Platzhalter: {patid} {name} {date} {time} {accession} {index}")]
    public string ImageFileNamePattern { get; set; } = "{patid}_{date}_{time}_{index}";

    // --- PDF ---
    public bool CreatePdf { get; set; } = true;
    public string PdfDirectory { get; set; } = @"C:\GDT\pdf";
    public string PdfFileNamePattern { get; set; } = "{patid}_{date}_{time}";
    public bool PdfIncludeImages { get; set; } = true;
    public int PdfImagesPerPage { get; set; } = 4;
    public bool PdfIncludeSrText { get; set; } = true;

    [Description("Kopfzeile des Befundblatts, z. B. der Praxisname.")]
    public string PdfHeaderTitle { get; set; } = "Sonographie-Befund";
    public string PdfPracticeName { get; set; } = "";

    [Description("Standard oder PdfA3b. PDF/A-3b wird für die elektronische Patientenakte gefordert.")]
    public PdfFormat PdfFormat { get; set; } = PdfFormat.Standard;

    [Description("Verfasser, der in den PDF-Metadaten steht. Leer = Praxisname.")]
    public string PdfAuthor { get; set; } = "";

    // --- DICOM-Archiv ---
    public bool ArchiveDicom { get; set; } = true;
    public string DicomArchiveDirectory { get; set; } = @"C:\ProgramData\GDT2DICOM\data\dicom";

    [Description("Ordnerstruktur im Archiv. Platzhalter: {patid} {name} {date} {studyuid} {accession}")]
    public string DicomArchiveLayout { get; set; } = @"{patid}\{date}_{accession}";

    [Description("Das DICOM-Archiv automatisch begrenzen. Standardmäßig aus – die Dateien " +
                 "unterliegen der ärztlichen Aufbewahrungspflicht.")]
    public bool LimitDicomArchive { get; set; } = false;

    [Description("Studien entfernen, die älter als diese Anzahl Tage sind. 0 = keine Altersgrenze.")]
    public int DicomArchiveRetentionDays { get; set; } = 365;

    [Description("Älteste Studien entfernen, sobald das Archiv größer als dieser Wert ist (GB). 0 = keine Größengrenze.")]
    public int DicomArchiveMaxSizeGb { get; set; } = 50;

    // --- Structured Report ---
    public bool ExtractStructuredReport { get; set; } = true;

    [Description("Maximale Anzahl Befundzeilen, die in den GDT-Satz übernommen werden. 0 = unbegrenzt.")]
    public int MaxGdtBefundLines { get; set; } = 200;

    [Description("Maximale Zeilenlänge im GDT-Befundtext (Rest wird umgebrochen).")]
    public int GdtBefundLineWidth { get; set; } = 60;

    // --- GDT-Rücksatz ---
    public bool WriteGdtResponse { get; set; } = true;
    public AttachmentPathMode AttachmentPathMode { get; set; } = AttachmentPathMode.Absolute;

    [Description("Maximale Anzahl an Dateiverweisen im GDT-Rücksatz. 0 = unbegrenzt.")]
    public int MaxAttachmentsInGdt { get; set; } = 50;

    [Description("PDF zuerst als Anhang eintragen (viele PVS importieren nur den ersten Verweis).")]
    public bool PdfAttachmentFirst { get; set; } = true;
}

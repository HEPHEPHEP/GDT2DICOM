using FellowOakDicom;
using FellowOakDicom.Imaging;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using FellowOakDicom.StructuredReport;
using Gdt2Dicom.Core.Dicom;
using Gdt2Dicom.TestClient;

// Kleines Werkzeug, um die Middleware ohne Sonogerät zu prüfen: Verbindungstest,
// Worklist abfragen, Testbild senden. Simuliert die Rolle des Ultraschallgeräts.

if (args.Length == 0)
{
    PrintUsage();
    return 0;
}

try
{
    return args[0].ToLowerInvariant() switch
    {
        "echo" => await EchoAsync(args),
        "worklist" => await WorklistAsync(args),
        "store" => await StoreAsync(args),
        "makedicom" => MakeTestImage(args),
        "makesr" => MakeTestReport(args),
        "mpps" => await MppsAsync(args),
        "commit" => await CommitAsync(args),
        "musterbefund" => MakeSampleReport(args),
        _ => PrintUsage()
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Fehler: {ex.Message}");
    return 2;
}

static int PrintUsage()
{
    Console.WriteLine("""
        GDT2DICOM Testclient – simuliert ein Sonogerät.

          echo      <host> <port> <callingAE> <calledAE>
                    Verbindungstest (C-ECHO).

          worklist  <host> <port> <callingAE> <calledAE> [Modality]
                    Modality Worklist abfragen und Treffer anzeigen.

          store     <host> <port> <callingAE> <calledAE> <datei.dcm> [weitere.dcm ...]
                    DICOM-Objekte senden (C-STORE).

          makedicom <ziel.dcm> [PatientenID] [Nachname] [Vorname] [StudyInstanceUID] [AccessionNumber]
                    Erzeugt ein synthetisches Ultraschallbild zum Testen.

          makesr    <ziel.dcm> [PatientenID] [Nachname] [Vorname] [StudyInstanceUID] [AccessionNumber]
                    Erzeugt einen Structured Report mit Beispiel-Messwerten.

          mpps      <host> <port> <callingAE> <calledAE> <StudyUID> <Accession> <PatientenID>
                    Meldet Beginn und Ende einer Untersuchung (N-CREATE + N-SET).

          commit    <host> <port> <callingAE> <calledAE> <eigenerPort> <datei.dcm> [weitere.dcm ...]
                    Fordert Storage Commitment an und wartet auf die Rückmeldung
                    (N-EVENT-REPORT) auf dem eigenen Port.

          musterbefund <ziel.pdf> [pdfa] [bild.jpg ...]
                    Erzeugt ein Muster-Befundblatt mit den Einstellungen aus der
                    Konfiguration. Mit "pdfa" wird PDF/A-3b erzwungen – nützlich, um vor
                    dem Produktivbetrieb zu prüfen, ob das Farbprofil gefunden wird.
        """);
    return 1;
}

static async Task<int> EchoAsync(string[] args)
{
    if (args.Length < 5)
        return PrintUsage();

    var result = await DicomScu.EchoAsync(args[1], int.Parse(args[2]), args[3], args[4]);
    Console.WriteLine(result.Success ? $"OK: {result.Message}" : $"FEHLGESCHLAGEN: {result.Message}");
    return result.Success ? 0 : 1;
}

static async Task<int> WorklistAsync(string[] args)
{
    if (args.Length < 5)
        return PrintUsage();

    var modality = args.Length > 5 ? args[5] : "US";

    var request = DicomCFindRequest.CreateWorklistQuery(modality: modality);
    var found = new List<DicomDataset>();

    request.OnResponseReceived = (_, response) =>
    {
        if (response.Status == DicomStatus.Pending && response.Dataset is not null)
            found.Add(response.Dataset);
    };

    var client = DicomClientFactory.Create(args[1], int.Parse(args[2]), useTls: false, args[3], args[4]);
    await client.AddRequestAsync(request);
    await client.SendAsync();

    Console.WriteLine($"{found.Count} Worklist-Einträge:");
    Console.WriteLine();

    foreach (var ds in found)
    {
        var step = ds.GetSequence(DicomTag.ScheduledProcedureStepSequence).Items.FirstOrDefault()
                   ?? new DicomDataset();

        Console.WriteLine($"  Patient      : {ds.GetSingleValueOrDefault(DicomTag.PatientName, "")}");
        Console.WriteLine($"  Patienten-ID : {ds.GetSingleValueOrDefault(DicomTag.PatientID, "")}");
        Console.WriteLine($"  geboren      : {ds.GetSingleValueOrDefault(DicomTag.PatientBirthDate, "")}");
        Console.WriteLine($"  Geschlecht   : {ds.GetSingleValueOrDefault(DicomTag.PatientSex, "")}");
        Console.WriteLine($"  Accession    : {ds.GetSingleValueOrDefault(DicomTag.AccessionNumber, "")}");
        Console.WriteLine($"  Study UID    : {ds.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, "")}");
        Console.WriteLine($"  Untersuchung : {ds.GetSingleValueOrDefault(DicomTag.RequestedProcedureDescription, "")}");
        Console.WriteLine($"  geplant      : {step.GetSingleValueOrDefault(DicomTag.ScheduledProcedureStepStartDate, "")} " +
                          $"{step.GetSingleValueOrDefault(DicomTag.ScheduledProcedureStepStartTime, "")}");
        Console.WriteLine($"  Modality     : {step.GetSingleValueOrDefault(DicomTag.Modality, "")}");
        Console.WriteLine();
    }

    return found.Count > 0 ? 0 : 1;
}

static async Task<int> StoreAsync(string[] args)
{
    if (args.Length < 6)
        return PrintUsage();

    var client = DicomClientFactory.Create(args[1], int.Parse(args[2]), useTls: false, args[3], args[4]);
    var failures = 0;

    foreach (var path in args.Skip(5))
    {
        var request = new DicomCStoreRequest(path)
        {
            OnResponseReceived = (req, response) =>
            {
                Console.WriteLine($"  {Path.GetFileName(path)}: {response.Status}");
                if (response.Status != DicomStatus.Success)
                    Interlocked.Increment(ref failures);
            }
        };
        await client.AddRequestAsync(request);
    }

    await client.SendAsync();
    Console.WriteLine(failures == 0 ? "Alle Objekte übertragen." : $"{failures} Objekte fehlgeschlagen.");
    return failures == 0 ? 0 : 1;
}

static int MakeTestImage(string[] args)
{
    if (args.Length < 2)
        return PrintUsage();

    var target = args[1];
    var patientId = args.Length > 2 ? args[2] : "TEST0815";
    var lastName = args.Length > 3 ? args[3] : "Mustermann";
    var firstName = args.Length > 4 ? args[4] : "Erika";
    var studyUid = args.Length > 5 ? args[5] : DicomUID.Generate().UID;
    var accession = args.Length > 6 ? args[6] : "00000001";

    const int width = 512;
    const int height = 384;

    // Ein Fächer mit Rauschen – sieht einem Sonogramm ähnlich genug, um Export und
    // PDF-Erzeugung zu prüfen.
    var pixels = new byte[width * height];
    var random = new Random(42);

    for (var y = 0; y < height; y++)
    {
        for (var x = 0; x < width; x++)
        {
            var dx = x - width / 2.0;
            var dy = (double)y;
            var radius = Math.Sqrt(dx * dx + dy * dy);
            var angle = Math.Atan2(dy, dx);

            var inFan = radius < height * 0.95 && radius > height * 0.08
                        && angle > Math.PI * 0.28 && angle < Math.PI * 0.72;

            var value = inFan
                ? (byte)Math.Clamp(120 + 90 * Math.Sin(radius / 11.0) + random.Next(-35, 35), 0, 255)
                : (byte)0;

            pixels[y * width + x] = value;
        }
    }

    var now = DateTime.Now;
    var dataset = new DicomDataset
    {
        { DicomTag.SOPClassUID, DicomUID.UltrasoundImageStorage },
        { DicomTag.SOPInstanceUID, DicomUID.Generate() },
        { DicomTag.StudyInstanceUID, studyUid },
        { DicomTag.SeriesInstanceUID, DicomUID.Generate() },
        { DicomTag.SpecificCharacterSet, "ISO_IR 100" },
        { DicomTag.PatientName, $"{lastName}^{firstName}" },
        { DicomTag.PatientID, patientId },
        { DicomTag.PatientBirthDate, "19750317" },
        { DicomTag.PatientSex, "F" },
        { DicomTag.AccessionNumber, accession },
        { DicomTag.Modality, "US" },
        { DicomTag.StudyDate, now.ToString("yyyyMMdd") },
        { DicomTag.StudyTime, now.ToString("HHmmss") },
        { DicomTag.SeriesNumber, "1" },
        { DicomTag.InstanceNumber, "1" },
        { DicomTag.Manufacturer, "GDT2DICOM" },
        { DicomTag.ManufacturerModelName, "Testclient" },
        { DicomTag.StudyDescription, "Abdomen-Sonographie (Test)" },
        { DicomTag.PhotometricInterpretation, "MONOCHROME2" },
        { DicomTag.SamplesPerPixel, (ushort)1 },
        { DicomTag.Rows, (ushort)height },
        { DicomTag.Columns, (ushort)width },
        { DicomTag.BitsAllocated, (ushort)8 },
        { DicomTag.BitsStored, (ushort)8 },
        { DicomTag.HighBit, (ushort)7 },
        { DicomTag.PixelRepresentation, (ushort)0 },
        { DicomTag.NumberOfFrames, "1" }
    };

    var pixelData = DicomPixelData.Create(dataset, newPixelData: true);
    pixelData.AddFrame(new FellowOakDicom.IO.Buffer.MemoryByteBuffer(pixels));

    var file = new DicomFile(dataset);
    var directory = Path.GetDirectoryName(Path.GetFullPath(target));
    if (!string.IsNullOrEmpty(directory))
        Directory.CreateDirectory(directory);

    file.Save(target);

    Console.WriteLine($"Testbild geschrieben: {target}");
    Console.WriteLine($"  Study Instance UID : {studyUid}");
    Console.WriteLine($"  Accession Number   : {accession}");
    return 0;
}

static int MakeTestReport(string[] args)
{
    if (args.Length < 2)
        return PrintUsage();

    var target = args[1];
    var patientId = args.Length > 2 ? args[2] : "TEST0815";
    var lastName = args.Length > 3 ? args[3] : "Mustermann";
    var firstName = args.Length > 4 ? args[4] : "Erika";
    var studyUid = args.Length > 5 ? args[5] : DicomUID.Generate().UID;
    var accession = args.Length > 6 ? args[6] : "00000001";

    DicomContentItem Measurement(string code, string meaning, decimal value, string unit) =>
        new(new DicomCodeItem(code, "99GDT2DICOM", meaning, null),
            DicomRelationship.Contains,
            new DicomMeasuredValue(value, new DicomCodeItem(unit, "UCUM", unit, null)));

    var report = new FellowOakDicom.StructuredReport.DicomStructuredReport(
        new DicomCodeItem("125000", "DCM", "Ultraschall-Befund", null),
        Measurement("M1", "Leber Längsdurchmesser", 132.5m, "mm"),
        Measurement("M2", "Milz Länge", 98.0m, "mm"),
        Measurement("M3", "Aorta abdominalis Durchmesser", 18.4m, "mm"),
        new DicomContentItem(
            new DicomCodeItem("T1", "99GDT2DICOM", "Beurteilung", null),
            DicomRelationship.Contains,
            DicomValueType.Text,
            "Regelrechte Darstellung der Oberbauchorgane. Kein Nachweis freier Flüssigkeit."));

    var now = DateTime.Now;
    var dataset = report.Dataset;
    dataset.AddOrUpdate(DicomTag.SOPClassUID, DicomUID.ComprehensiveSRStorage);
    dataset.AddOrUpdate(DicomTag.SOPInstanceUID, DicomUID.Generate());
    dataset.AddOrUpdate(DicomTag.StudyInstanceUID, studyUid);
    dataset.AddOrUpdate(DicomTag.SeriesInstanceUID, DicomUID.Generate());
    dataset.AddOrUpdate(DicomTag.SpecificCharacterSet, "ISO_IR 100");
    dataset.AddOrUpdate(DicomTag.PatientName, $"{lastName}^{firstName}");
    dataset.AddOrUpdate(DicomTag.PatientID, patientId);
    dataset.AddOrUpdate(DicomTag.PatientBirthDate, "19750317");
    dataset.AddOrUpdate(DicomTag.PatientSex, "F");
    dataset.AddOrUpdate(DicomTag.AccessionNumber, accession);
    dataset.AddOrUpdate(DicomTag.Modality, "SR");
    dataset.AddOrUpdate(DicomTag.StudyDate, now.ToString("yyyyMMdd"));
    dataset.AddOrUpdate(DicomTag.StudyTime, now.ToString("HHmmss"));
    dataset.AddOrUpdate(DicomTag.SeriesNumber, "99");
    dataset.AddOrUpdate(DicomTag.InstanceNumber, "1");
    dataset.AddOrUpdate(DicomTag.CompletionFlag, "COMPLETE");
    dataset.AddOrUpdate(DicomTag.VerificationFlag, "UNVERIFIED");
    dataset.AddOrUpdate(DicomTag.ContentDate, now.ToString("yyyyMMdd"));
    dataset.AddOrUpdate(DicomTag.ContentTime, now.ToString("HHmmss"));

    var directory = Path.GetDirectoryName(Path.GetFullPath(target));
    if (!string.IsNullOrEmpty(directory))
        Directory.CreateDirectory(directory);

    new DicomFile(dataset).Save(target);

    Console.WriteLine($"Test-Report geschrieben: {target}");
    return 0;
}

static int MakeSampleReport(string[] args)
{
    if (args.Length < 2)
        return PrintUsage();

    var target = Path.GetFullPath(args[1]);
    var forcePdfA = args.Length > 2 && args[2].Equals("pdfa", StringComparison.OrdinalIgnoreCase);
    var images = args.Skip(forcePdfA ? 3 : 2).Where(File.Exists).ToList();

    var config = Gdt2Dicom.Core.Configuration.ConfigStore.LoadSafe(out _).Export;
    if (forcePdfA)
        config.PdfFormat = Gdt2Dicom.Core.Configuration.PdfFormat.PdfA3b;

    config.PdfIncludeImages = images.Count > 0;

    var logger = new ConsoleLogger();
    var builder = new Gdt2Dicom.Core.Export.PdfReportBuilder(logger);

    var header = new Gdt2Dicom.Core.Export.PdfReportHeader(
        PatientName: "Mustermann, Erika",
        PatientId: "TEST0815",
        BirthDate: "19750317",
        Sex: "F",
        StudyDate: DateTime.Now.ToString("yyyyMMdd"),
        StudyTime: DateTime.Now.ToString("HHmmss"),
        AccessionNumber: "A4711",
        ProcedureDescription: "Abdomen-Sonographie Oberbauch",
        Modality: "US",
        DeviceName: "Testclient");

    var lines = new[]
    {
        "Ultraschall-Befund",
        "Leber Längsdurchmesser: 132,5 mm",
        "Milz Länge: 98 mm",
        "Aorta abdominalis Durchmesser: 18,4 mm",
        "Beurteilung: Regelrechte Darstellung der Oberbauchorgane.",
        "Kein Nachweis freier Flüssigkeit. Gallenblase zartwandig, "
        + "keine Konkremente. Nieren beidseits normal groß."
    };

    var result = builder.Build(target, header, images, lines, config);

    if (result is null)
    {
        Console.Error.WriteLine("Das Befundblatt konnte nicht erstellt werden.");
        return 1;
    }

    Console.WriteLine($"Muster-Befundblatt: {result}");
    return 0;
}

static async Task<int> MppsAsync(string[] args)
{
    if (args.Length < 8)
        return PrintUsage();

    var (host, port, callingAe, calledAe) = (args[1], int.Parse(args[2]), args[3], args[4]);
    var (studyUid, accession, patientId) = (args[5], args[6], args[7]);
    var mppsUid = DicomUID.Generate();

    DicomDataset BuildDataset(string status)
    {
        var scheduled = new DicomDataset
        {
            { DicomTag.StudyInstanceUID, studyUid },
            { DicomTag.AccessionNumber, accession }
        };

        return new DicomDataset
        {
            { DicomTag.PerformedProcedureStepStatus, status },
            { DicomTag.PatientID, patientId },
            { DicomTag.Modality, "US" },
            { DicomTag.PerformedProcedureStepStartDate, DateTime.Now.ToString("yyyyMMdd") },
            { DicomTag.PerformedProcedureStepStartTime, DateTime.Now.ToString("HHmmss") },
            new DicomSequence(DicomTag.ScheduledStepAttributesSequence, scheduled)
        };
    }

    var ok = true;

    var create = new DicomNCreateRequest(DicomUID.ModalityPerformedProcedureStep, mppsUid)
    {
        Dataset = BuildDataset("IN PROGRESS"),
        OnResponseReceived = (_, response) =>
        {
            Console.WriteLine($"  N-CREATE (IN PROGRESS): {response.Status}");
            if (response.Status != DicomStatus.Success) ok = false;
        }
    };

    var client = DicomClientFactory.Create(host, port, useTls: false, callingAe, calledAe);
    await client.AddRequestAsync(create);
    await client.SendAsync();

    var set = new DicomNSetRequest(DicomUID.ModalityPerformedProcedureStep, mppsUid)
    {
        Dataset = BuildDataset("COMPLETED"),
        OnResponseReceived = (_, response) =>
        {
            Console.WriteLine($"  N-SET (COMPLETED): {response.Status}");
            if (response.Status != DicomStatus.Success) ok = false;
        }
    };

    var client2 = DicomClientFactory.Create(host, port, useTls: false, callingAe, calledAe);
    await client2.AddRequestAsync(set);
    await client2.SendAsync();

    Console.WriteLine($"  MPPS-Instanz: {mppsUid.UID}");
    return ok ? 0 : 1;
}

static async Task<int> CommitAsync(string[] args)
{
    if (args.Length < 7)
        return PrintUsage();

    var (host, port, callingAe, calledAe) = (args[1], int.Parse(args[2]), args[3], args[4]);
    var listenPort = int.Parse(args[5]);

    var references = args.Skip(6)
        .Select(path => DicomFile.Open(path))
        .Select(f => (
            SopClass: f.Dataset.GetSingleValue<DicomUID>(DicomTag.SOPClassUID),
            SopInstance: f.Dataset.GetSingleValue<DicomUID>(DicomTag.SOPInstanceUID)))
        .ToList();

    // Eigener kleiner SCP, damit die Middleware den N-EVENT-REPORT zustellen kann.
    using var listener = DicomServerFactory.Create<CommitNotificationService>(listenPort);
    Console.WriteLine($"  Warte auf Rückmeldung auf Port {listenPort} …");

    var transactionUid = DicomUID.Generate();
    var dataset = new DicomDataset { { DicomTag.TransactionUID, transactionUid.UID } };
    dataset.Add(new DicomSequence(DicomTag.ReferencedSOPSequence, references.Select(r => new DicomDataset
    {
        { DicomTag.ReferencedSOPClassUID, r.SopClass },
        { DicomTag.ReferencedSOPInstanceUID, r.SopInstance }
    }).ToArray()));

    var action = new DicomNActionRequest(
        DicomUID.StorageCommitmentPushModel,
        DicomUID.StorageCommitmentPushModelInstance,
        actionTypeId: 1)
    {
        Dataset = dataset,
        OnResponseReceived = (_, response) => Console.WriteLine($"  N-ACTION: {response.Status}")
    };

    var client = DicomClientFactory.Create(host, port, useTls: false, callingAe, calledAe);
    await client.AddRequestAsync(action);
    await client.SendAsync();

    // Die Middleware antwortet über eine neue Association – kurz darauf warten.
    for (var i = 0; i < 40 && !CommitNotificationService.Received; i++)
        await Task.Delay(500);

    if (!CommitNotificationService.Received)
    {
        Console.WriteLine("  Keine Rückmeldung erhalten.");
        return 1;
    }

    Console.WriteLine($"  Rückmeldung: {CommitNotificationService.CommittedCount} bestätigt, " +
                      $"{CommitNotificationService.FailedCount} fehlgeschlagen " +
                      $"(Event-Typ {CommitNotificationService.EventTypeId}).");

    return CommitNotificationService.FailedCount == 0 ? 0 : 1;
}

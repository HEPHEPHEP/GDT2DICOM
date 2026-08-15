using System.IO;
using System.Windows;
using Gdt2Dicom.Core.Configuration;
using Gdt2Dicom.Core.Gdt;
using Gdt2Dicom.Core.Ipc;
using Gdt2Dicom.Core.Pipeline;

namespace Gdt2Dicom.Connector;

/// <summary>
/// Das Programm, das ein PVS aufruft, wenn es die GDT-Schnittstelle nicht über ein
/// überwachtes Verzeichnis, sondern per Fremdprogramm-Aufruf bedient.
/// </summary>
public static class Program
{
    /// <summary>Rückgabewerte, die manche PVS auswerten.</summary>
    public const int ExitOk = 0;
    public const int ExitFehler = 1;
    public const int ExitDienstNichtErreichbar = 2;
    public const int ExitZeitlimit = 3;
    public const int ExitAbgebrochen = 4;
    public const int ExitKeinRuecksatz = 5;

    [STAThread]
    public static int Main(string[] args)
    {
        var parsed = CommandLine.Parse(args);

        if (parsed.Help)
        {
            ShowHelp();
            return ExitOk;
        }

        var config = ConfigStore.LoadSafe(out var configError);

        if (parsed.Diagnose)
            return RunDiagnose(parsed, config, configError);

        try
        {
            return Run(parsed, config);
        }
        catch (Exception ex)
        {
            Report(config, "GDT2DICOM", $"Unerwarteter Fehler:\n\n{ex.Message}", isError: true);
            return ExitFehler;
        }
    }

    /// <summary>
    /// Führt eine asynchrone Aufgabe aus und blockiert bis zum Ergebnis.
    /// </summary>
    /// <remarks>
    /// Bewusst kein <c>async</c> im gesamten Ablauf: Ohne Synchronisierungskontext läuft der
    /// Code nach einem <c>await</c> auf einem Thread-Pool-Thread weiter, und von dort lassen
    /// sich keine WPF-Fenster öffnen ("Beim aufrufenden Thread muss es sich um einen
    /// STA-Thread handeln"). So bleibt der gesamte Ablauf auf dem STA-Thread von Main,
    /// während nur die Wartezeit auf einem Hintergrund-Thread verbracht wird.
    /// </remarks>
    private static T Await<T>(Func<Task<T>> work) => Task.Run(work).GetAwaiter().GetResult();

    // -----------------------------------------------------------------------
    // Normalbetrieb
    // -----------------------------------------------------------------------

    private static int Run(ConnectorArguments args, AppConfig config)
    {
        var startedUtc = DateTime.UtcNow;
        var file = args.GdtFile;

        if (args.Abholen)
            return FetchResponse(args, config);

        if (!string.IsNullOrWhiteSpace(file) && !File.Exists(file))
        {
            Report(config, "Auftrag nicht gefunden",
                $"Die vom PVS übergebene Datei existiert nicht:\n\n{file}", isError: true);
            return ExitFehler;
        }

        // Vorlauf: Das PVS schreibt die Datei und startet erst danach dieses Programm. Die
        // Verzeichnisüberwachung kann sie in dieser Lücke bereits abgearbeitet haben – ein
        // Auftrag aus den letzten Sekunden gilt deshalb noch als der eigene.
        var client = new IpcClient();
        var result = Await(() => client.ProcessGdtAsync(file, startedUtc.AddSeconds(-20)));

        if (result is null)
            return HandleServiceUnavailable(config, file);

        if (!result.Success)
        {
            Report(config, "Auftrag nicht übernommen", result.Error, isError: true);
            return ExitFehler;
        }

        var wait = args.WaitOverride ?? config.Connector.WaitForResponse;
        if (!wait)
        {
            if (config.Connector.ShowSuccessDialog)
            {
                Report(config, "Auftrag übernommen",
                    $"{result.PatientName}\nAuftragsnummer {result.AccessionNumber}\n\n" +
                    "Der Patient steht jetzt in der Worklist des Ultraschallgeräts.",
                    isError: false);
            }
            return ExitOk;
        }

        return WaitForResponse(config, result, startedUtc);
    }

    /// <summary>
    /// Der zweite Aufruf: Es wird kein Auftrag angelegt, sondern der bereitliegende Rücksatz
    /// in den Ausgang gestellt. Das PVS liest ihn dann, wenn dieses Programm sich beendet.
    /// </summary>
    private static int FetchResponse(ConnectorArguments args, AppConfig config)
    {
        // Patientennummer bevorzugt aus der übergebenen GDT-Datei; viele PVS schicken auch
        // beim Abholen einen Satz mit, in dem der aktuelle Patient steht.
        var patientId = args.PatientId;

        if (string.IsNullOrWhiteSpace(patientId) && !string.IsNullOrWhiteSpace(args.GdtFile) && File.Exists(args.GdtFile))
        {
            try
            {
                var record = GdtSerializer.ReadFile(args.GdtFile, config.Gdt.Charset);
                patientId = record.Get(config.Gdt.FieldMap.PatientId)?.Trim();
            }
            catch (Exception ex)
            {
                Report(config, "Abholen", $"Die übergebene Datei ist nicht lesbar:\n\n{ex.Message}", isError: true);
                return ExitFehler;
            }
        }

        var result = Await(() => new IpcClient().FetchGdtResponseAsync(patientId));

        if (result is null)
        {
            Report(config, "Dienst nicht erreichbar",
                "Der GDT2DICOM-Dienst antwortet nicht, es kann kein Rücksatz abgeholt werden.\n\n" +
                "Bitte in der GDT2DICOM-Konfiguration prüfen, ob der Dienst gestartet ist.",
                isError: true);
            return ExitDienstNichtErreichbar;
        }

        if (!result.Delivered)
        {
            // Kein Rücksatz da ist der Normalfall, wenn die Untersuchung noch läuft –
            // deshalb bewusst ein eigener Rückgabewert und keine Fehlermeldung.
            if (config.Connector.ShowSuccessDialog)
                Report(config, "Kein Rücksatz", result.Error, isError: false);

            return ExitKeinRuecksatz;
        }

        if (config.Connector.ShowSuccessDialog)
        {
            Report(config, "Rücksatz bereitgestellt",
                $"{result.PatientName}\n{result.FileName}" +
                (result.Remaining > 0 ? $"\n\nEs warten noch {result.Remaining} weitere Rücksätze." : ""),
                isError: false);
        }

        return ExitOk;
    }

    /// <summary>
    /// Der Dienst antwortet nicht. Die Auftragsdatei darf trotzdem nicht verloren gehen:
    /// Sie wandert ins Eingangsverzeichnis, damit der Dienst sie beim nächsten Start findet.
    /// </summary>
    private static int HandleServiceUnavailable(AppConfig config, string? file)
    {
        var hint = "Der GDT2DICOM-Dienst antwortet nicht.";

        if (!string.IsNullOrWhiteSpace(file))
        {
            try
            {
                var inbox = config.Gdt.InboxDirectory;
                Directory.CreateDirectory(inbox);

                var alreadyThere = string.Equals(
                    Path.GetFullPath(Path.GetDirectoryName(file) ?? ""),
                    Path.GetFullPath(inbox),
                    StringComparison.OrdinalIgnoreCase);

                if (!alreadyThere)
                {
                    var target = Path.Combine(inbox, Path.GetFileName(file));
                    File.Copy(file, target, overwrite: true);
                }

                hint += "\n\nDer Auftrag wurde im Eingangsverzeichnis abgelegt und wird " +
                        "verarbeitet, sobald der Dienst wieder läuft.";
            }
            catch (Exception ex)
            {
                hint += $"\n\nDer Auftrag konnte auch nicht zwischengespeichert werden: {ex.Message}";
            }
        }

        hint += "\n\nBitte in der GDT2DICOM-Konfiguration prüfen, ob der Dienst gestartet ist.";

        Report(config, "Dienst nicht erreichbar", hint, isError: true);
        return ExitDienstNichtErreichbar;
    }

    private static int WaitForResponse(AppConfig config, GdtProcessResultDto result, DateTime startedUtc)
    {
        var sekunden = config.Connector.WaitTimeoutSeconds;
        var unbegrenzt = sekunden <= 0;
        var timeout = unbegrenzt ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(sekunden);

        using var cancellation = new CancellationTokenSource();

        string? responseFile = null;
        var cancelled = false;

        // Ohne Zeitlimit muss das Fenster erscheinen: sonst gäbe es keine Möglichkeit, das
        // Warten zu beenden, und das PVS bliebe unbegrenzt blockiert.
        var fensterZeigen = config.Connector.ShowWaitWindow || unbegrenzt;

        // Steht die Auslieferung auf „auf Abruf“, hält der Dienst den Rücksatz zurück. Beim
        // Warten wird er deshalb bei jedem Durchgang angefordert – sonst käme im Ausgang nie
        // etwas an und das Warten liefe ins Leere.
        var aufAbruf = config.Gdt.ResponseDelivery == ResponseDelivery.AufAbruf;
        var abrufClient = new IpcClient();

        Func<Task>? abrufen = aufAbruf
            ? () => abrufClient.FetchGdtResponseAsync(result.PatientId)
            : null;

        if (fensterZeigen)
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnLastWindowClose };
            var window = new WaitWindow(result.PatientName, unbegrenzt ? 0 : sekunden, cancellation);

            _ = Task.Run(async () =>
            {
                try
                {
                    responseFile = await GdtResponseWaiter.WaitAsync(
                        config, result.PatientId, startedUtc, timeout, cancellation.Token, abrufen);
                }
                catch (OperationCanceledException)
                {
                    // Vom Anwender abgebrochen.
                }
                finally
                {
                    try
                    {
                        window.CloseFromBackground();
                    }
                    catch
                    {
                        // Fenster war schon zu.
                    }
                }
            });

            app.Run(window);
            cancelled = window.Cancelled;
        }
        else
        {
            try
            {
                responseFile = Await(() => GdtResponseWaiter.WaitAsync(
                    config, result.PatientId, startedUtc, timeout, cancellation.Token, abrufen));
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
        }

        if (responseFile is not null)
            return ExitOk;

        if (cancelled)
            return ExitAbgebrochen;

        Report(config, "Kein Rücksatz",
            $"Innerhalb von {timeout.TotalMinutes:0} Minuten ist kein Rücksatz für " +
            $"{result.PatientName} eingetroffen.\n\n" +

            "Der Auftrag steht weiterhin in der Worklist. Sobald die Bilder eintreffen, " +
            "wird der Rücksatz geschrieben und beim nächsten Aufruf eingelesen.",
            isError: true);

        return ExitZeitlimit;
    }

    // -----------------------------------------------------------------------
    // Diagnose
    // -----------------------------------------------------------------------

    private static int RunDiagnose(ConnectorArguments args, AppConfig config, string? configError)
    {
        var output = new ConsoleOutput();

        output.Write("GDT2DICOM – Diagnose des PVS-Aufrufs");
        output.Write(new string('=', 60));
        output.Write();

        output.Write("Aufruf, so wie er angekommen ist:");
        output.Write($"  Programm  : {Environment.ProcessPath}");
        output.Write($"  Argumente : {(args.Raw.Count == 0 ? "(keine)" : string.Join(" | ", args.Raw))}");
        output.Write($"  Erkannte Datei: {args.GdtFile ?? "(keine – es würde die neueste Datei im Eingang genommen)"}");
        output.Write();

        output.Write("Konfiguration:");
        output.Write($"  Datei     : {ConfigStore.ConfigFilePath}");
        if (configError is not null)
            output.Write($"  FEHLER    : {configError} (es gelten Standardwerte)");
        output.Write($"  Eingang   : {config.Gdt.InboxDirectory}  {(Directory.Exists(config.Gdt.InboxDirectory) ? "[vorhanden]" : "[FEHLT]")}");
        output.Write($"  Ausgang   : {config.Gdt.OutboxDirectory}  {(Directory.Exists(config.Gdt.OutboxDirectory) ? "[vorhanden]" : "[FEHLT]")}");
        output.Write($"  Muster    : {config.Gdt.InboxFilePattern}");
        var wartetext = !config.Connector.WaitForResponse
            ? "nein, sofortige Rückkehr"
            : config.Connector.WaitTimeoutSeconds <= 0
                ? "ja, ohne Zeitlimit (Abbruch über das Wartefenster)"
                : $"ja, bis zu {config.Connector.WaitTimeoutSeconds} s";
        output.Write($"  Warten    : {wartetext}");
        output.Write();

        output.Write("Auftragsdatei:");
        DescribeFile(output, args.GdtFile, config);
        output.Write();

        output.Write("Dienst:");
        var reachable = Await(() => new IpcClient(timeoutMs: 2000).IsServiceReachableAsync());
        output.Write(reachable
            ? "  erreichbar – Aufträge werden sofort verarbeitet"
            : "  NICHT erreichbar – Aufträge landen nur im Eingangsverzeichnis");
        output.Write();

        output.Write("Im PVS einzutragen:");
        output.Write($"  Programm    : {Environment.ProcessPath}");
        output.Write("  Parameter   : <Pfad der Auftragsdatei>   (oder leer lassen)");
        output.Write($"  Exportpfad  : {config.Gdt.InboxDirectory}");
        output.Write($"  Importpfad  : {config.Gdt.OutboxDirectory}");
        output.Write();

        output.Write("Rücksätze, die auf Abholung warten:");
        var wartend = Await(() => new IpcClient(timeoutMs: 2000).FetchPendingCountAsync());
        output.Write(wartend < 0
            ? "  (nicht ermittelbar, Dienst nicht erreichbar)"
            : $"  {wartend}");
        output.Write($"  Auslieferung: {(config.Gdt.ResponseDelivery == ResponseDelivery.AufAbruf ? "auf Abruf durch das PVS" : "sofort")}");
        output.Write();

        output.Write("Rückgabewerte:");
        output.Write($"  {ExitOk} = Auftrag übernommen bzw. Rücksatz bereitgestellt");
        output.Write($"  {ExitFehler} = Fehler");
        output.Write($"  {ExitDienstNichtErreichbar} = Dienst nicht erreichbar, Auftrag zwischengespeichert");
        output.Write($"  {ExitZeitlimit} = Zeitlimit beim Warten auf den Rücksatz");
        output.Write($"  {ExitAbgebrochen} = vom Anwender abgebrochen");
        output.Write($"  {ExitKeinRuecksatz} = beim Abholen: es liegt noch kein Rücksatz bereit");

        output.Flush("GDT2DICOM – Diagnose");
        return reachable ? ExitOk : ExitDienstNichtErreichbar;
    }

    private static void DescribeFile(ConsoleOutput output, string? file, AppConfig config)
    {
        if (string.IsNullOrWhiteSpace(file))
        {
            output.Write("  (keine übergeben)");
            return;
        }

        if (!File.Exists(file))
        {
            output.Write($"  {file}");
            output.Write("  EXISTIERT NICHT – prüfen Sie den Exportpfad im PVS.");
            return;
        }

        try
        {
            var record = GdtSerializer.ReadFile(file, config.Gdt.Charset);
            var map = config.Gdt.FieldMap;
            var satzart = record.Get(map.Satzidentifikation) ?? "(fehlt)";

            var accepted = new List<string> { config.Gdt.RequestSatzart };
            accepted.AddRange(config.Gdt.AdditionalRequestSatzarten);
            var ok = accepted.Any(s => string.Equals(s?.Trim(), satzart, StringComparison.OrdinalIgnoreCase));

            output.Write($"  {file}  ({new FileInfo(file).Length} Bytes, {record.Fields.Count} Felder)");
            output.Write($"  Satzart       : {satzart}  {(ok ? "[akzeptiert]" : "[NICHT als Auftrag konfiguriert]")}");
            output.Write($"  Patienten-Nr. : {record.Get(map.PatientId) ?? "(fehlt)"}");
            output.Write($"  Name          : {record.Get(map.PatientName)}, {record.Get(map.PatientVorname)}");
            output.Write($"  Geburtsdatum  : {record.Get(map.PatientGeburtsdatum) ?? "(fehlt)"}");
            output.Write($"  Sender-ID     : {record.Get(map.SenderId) ?? "(fehlt)"}");
        }
        catch (Exception ex)
        {
            output.Write($"  {file}");
            output.Write($"  Nicht lesbar: {ex.Message}");
        }
    }

    private static void ShowHelp()
    {
        var output = new ConsoleOutput();
        output.Write("GDT2DICOM.Aufruf – wird vom PVS als Fremdprogramm gestartet.");
        output.Write();
        output.Write("  GDT2DICOM.Aufruf.exe <datei.gdt>       Auftrag übernehmen");
        output.Write("  GDT2DICOM.Aufruf.exe /GDT=<datei.gdt>  dasselbe mit Präfix");
        output.Write("  GDT2DICOM.Aufruf.exe                   neueste Datei im Eingang nehmen");
        output.Write();
        output.Write("  --diagnose    Aufruf und Einstellungen prüfen, ohne etwas zu verarbeiten");
        output.Write("  --warten      auf den Rücksatz warten (übersteuert die Konfiguration)");
        output.Write("  --nichtwarten sofort zurückkehren (übersteuert die Konfiguration)");
        output.Write();
        output.Write("Zweiter Aufruf nach der Untersuchung:");
        output.Write("  GDT2DICOM.Aufruf.exe --abholen <datei.gdt>   Rücksatz für den Patienten bereitstellen");
        output.Write("  GDT2DICOM.Aufruf.exe --abholen /patid=12345  dasselbe mit direkter Patientennummer");
        output.Write("  GDT2DICOM.Aufruf.exe --abholen               den ältesten wartenden Rücksatz");
        output.Flush("GDT2DICOM.Aufruf");
    }

    // -----------------------------------------------------------------------

    private static void Report(AppConfig config, string title, string? message, bool isError)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        if (isError && !config.Connector.ShowErrorDialogs)
            return;

        MessageBox.Show(message, $"GDT2DICOM – {title}", MessageBoxButton.OK,
            isError ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }
}

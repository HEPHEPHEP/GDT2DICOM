using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Threading;
using Gdt2Dicom.Core.Configuration;
using Gdt2Dicom.Core.Gdt;
using Gdt2Dicom.Core.Ipc;

namespace Gdt2Dicom.Gui;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private readonly IpcClient _client = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };

    private string _savedConfigJson = "";
    private bool _refreshInProgress;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;

        BuildFieldMapEditors();

        Loaded += async (_, _) =>
        {
            UpdateConnectorCommand();
            await LoadConfigAsync();
            FillAboutTab();
            await RefreshAsync();
            _timer.Tick += async (_, _) => await RefreshAsync();
            _timer.Start();
        };

        Closing += (_, e) =>
        {
            if (!HasPendingChanges())
                return;

            var answer = MessageBox.Show(
                "Es gibt ungespeicherte Änderungen. Trotzdem beenden?",
                "GDT2DICOM", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (answer == MessageBoxResult.No)
                e.Cancel = true;
        };
    }

    // -----------------------------------------------------------------------
    // Laden und Speichern
    // -----------------------------------------------------------------------

    private async Task LoadConfigAsync()
    {
        var (config, fromService) = await _client.GetConfigAsync();
        _vm.Config = config;
        _savedConfigJson = ConfigStore.Serialize(config);
        _vm.HasUnsavedChanges = false;

        _vm.StatusMessage = fromService
            ? "Konfiguration vom laufenden Dienst geladen."
            : $"Dienst nicht erreichbar – Konfiguration aus {ConfigStore.ConfigFilePath} geladen.";
    }

    private bool HasPendingChanges()
    {
        try
        {
            return ConfigStore.Serialize(_vm.Config) != _savedConfigJson;
        }
        catch
        {
            return false;
        }
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        // Offene Zelleneingaben im Gegenstellen-Gitter übernehmen.
        RemoteNodeGrid.CommitEdit(DataGridEditingUnit.Row, true);

        var problems = ConfigStore.EnsureConfiguredDirectories(_vm.Config);
        if (problems.Count > 0)
        {
            var answer = MessageBox.Show(
                "Folgende Verzeichnisse konnten nicht angelegt werden:\n\n" +
                string.Join("\n", problems) +
                "\n\nTrotzdem speichern?",
                "GDT2DICOM", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (answer == MessageBoxResult.No)
                return;
        }

        var (saved, applied, error) = await _client.SetConfigAsync(_vm.Config);

        if (!saved)
        {
            MessageBox.Show($"Speichern fehlgeschlagen:\n\n{error}", "GDT2DICOM",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _savedConfigJson = ConfigStore.Serialize(_vm.Config);
        _vm.HasUnsavedChanges = false;
        _vm.StatusMessage = applied
            ? "Gespeichert und vom Dienst übernommen."
            : "In die Konfigurationsdatei gespeichert. Der Dienst übernimmt sie beim nächsten Start.";
    }

    private async void OnDiscardClick(object sender, RoutedEventArgs e)
    {
        if (HasPendingChanges())
        {
            var answer = MessageBox.Show("Alle Änderungen seit dem letzten Speichern verwerfen?",
                "GDT2DICOM", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer == MessageBoxResult.No)
                return;
        }

        await LoadConfigAsync();
    }

    private async void OnReloadClick(object sender, RoutedEventArgs e)
    {
        await LoadConfigAsync();
        await RefreshAsync();
    }

    // -----------------------------------------------------------------------
    // Zyklische Aktualisierung
    // -----------------------------------------------------------------------

    private async Task RefreshAsync()
    {
        if (_refreshInProgress)
            return;

        _refreshInProgress = true;
        try
        {
            _vm.HasUnsavedChanges = HasPendingChanges();
            _vm.ServiceStateText = ServiceControl.DescribeStatus();

            var status = await _client.GetStatusAsync();
            _vm.Status = status;
            _vm.ConnectionText = status is null
                ? "keine Verbindung zum Dienst"
                : "verbunden";

            UpdateStatusTexts(status);

            // Bewusst über die benannten Reiter statt über Indizes: Ein neuer Reiter in der
            // Mitte würde die Nummerierung verschieben, und die Aktualisierung liefe
            // stillschweigend auf den falschen Inhalt.
            if (ReferenceEquals(Tabs.SelectedItem, LogTab))
                await RefreshLogAsync();

            if (ReferenceEquals(Tabs.SelectedItem, StatusTab))
                await RefreshPendingStudiesAsync();

            if (ReferenceEquals(Tabs.SelectedItem, WorklistTab))
                await RefreshWorklistAsync();

            // Die Supportangaben enthalten Laufzeitwerte des Dienstes. Beim Programmstart
            // liegen die noch nicht vor, deshalb hier mitlaufen lassen statt einmalig füllen.
            if (ReferenceEquals(Tabs.SelectedItem, AboutTab))
                SupportInfoBox.Text = BuildSupportInfo();
        }
        catch (Exception ex)
        {
            _vm.StatusMessage = $"Aktualisierung fehlgeschlagen: {ex.Message}";
        }
        finally
        {
            _refreshInProgress = false;
        }
    }

    private void UpdateStatusTexts(StatusDto? status)
    {
        if (status is null)
        {
            DicomStateText.Text = "–";
            DicomStateText.Foreground = (Brush)FindResource("MutedBrush");
            GdtStateText.Text = "–";
            GdtStateText.Foreground = (Brush)FindResource("MutedBrush");
            UptimeText.Text = "–";
            CounterPanel.Children.Clear();
            return;
        }

        DicomStateText.Text = status.DicomServerRunning
            ? $"läuft – {status.DicomAeTitle} auf Port {status.DicomPort}"
            : $"nicht aktiv{(string.IsNullOrEmpty(status.DicomServerError) ? "" : $" – {status.DicomServerError}")}";
        DicomStateText.Foreground = (Brush)FindResource(status.DicomServerRunning ? "OkBrush" : "ErrorBrush");

        if (status.GdtWatcherRunning)
        {
            GdtStateText.Text = $"läuft – {status.GdtInboxDirectory}";
            GdtStateText.Foreground = (Brush)FindResource("OkBrush");
        }
        else if (!status.GdtWatcherEnabled)
        {
            // Absichtlich aus: kein Fehler, sondern eine Einstellung.
            GdtStateText.Text = "abgeschaltet – Aufträge kommen nur über den Programmaufruf";
            GdtStateText.Foreground = (Brush)FindResource("MutedBrush");
        }
        else
        {
            GdtStateText.Text = $"nicht aktiv{(string.IsNullOrEmpty(status.GdtWatcherError) ? "" : $" – {status.GdtWatcherError}")}";
            GdtStateText.Foreground = (Brush)FindResource("ErrorBrush");
        }

        var uptime = DateTime.UtcNow - status.StartedUtc;
        UptimeText.Text = $"{status.StartedUtc.ToLocalTime():dd.MM.yyyy HH:mm} " +
                          $"({(int)uptime.TotalHours} h {uptime.Minutes} min)";

        RenderCounters(status);
    }

    private void RenderCounters(StatusDto status)
    {
        var counters = new (string Label, string Value)[]
        {
            ("Aufträge übernommen", status.GdtRequestsProcessed.ToString()),
            ("Aufträge fehlerhaft", status.GdtRequestsFailed.ToString()),
            ("Worklist-Abfragen", status.WorklistQueries.ToString()),
            ("Objekte empfangen", status.InstancesReceived.ToString()),
            ("Untersuchungen exportiert", status.StudiesExported.ToString()),
            ("Rücksätze geschrieben", status.GdtResponsesWritten.ToString()),
            ("Verbindungen angenommen", status.AssociationsAccepted.ToString()),
            ("Aufträge in der Worklist", status.WorklistCount.ToString()),
            (status.ResponseDeliveryOnDemand ? "Rücksätze zum Abholen" : "Rücksätze im Rückstau",
                status.PendingResponseCount.ToString())
        };

        CounterPanel.Children.Clear();
        foreach (var (label, value) in counters)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 4, 16, 4) };
            panel.Children.Add(new TextBlock
            {
                Text = value,
                FontSize = 20,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("AccentBrush")
            });
            panel.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 11,
                Foreground = (Brush)FindResource("MutedBrush")
            });
            CounterPanel.Children.Add(panel);
        }
    }

    private async Task RefreshLogAsync()
    {
        var lines = await _client.TailLogAsync(500);

        if (lines.Count == 0)
        {
            var fallback = ReadLogFileTail(500);
            _vm.LogText = fallback.Count > 0
                ? string.Join(Environment.NewLine, fallback)
                : "Keine Protokolleinträge. Läuft der Dienst?";
        }
        else
        {
            _vm.LogText = string.Join(Environment.NewLine, lines);
        }

        if (AutoScrollLog.IsChecked == true)
            LogBox.ScrollToEnd();
    }

    /// <summary>Liest das Log direkt aus der Datei, wenn der Dienst nicht antwortet.</summary>
    private List<string> ReadLogFileTail(int count)
    {
        try
        {
            var directory = _vm.Config.General.LogDirectory;
            if (!Directory.Exists(directory))
                return new List<string>();

            var newest = new DirectoryInfo(directory)
                .GetFiles("gdt2dicom-*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();

            if (newest is null)
                return new List<string>();

            using var stream = new FileStream(newest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);

            var lines = new Queue<string>(count);
            while (reader.ReadLine() is { } line)
            {
                lines.Enqueue(line);
                while (lines.Count > count)
                    lines.Dequeue();
            }

            return lines.ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    /// <summary>
    /// Gleicht die angezeigte Worklist mit dem Dienst ab.
    /// </summary>
    /// <remarks>
    /// Bewusst kein Leeren und Neubefüllen: Die Liste aktualisiert sich alle zwei Sekunden,
    /// und dabei würden Auswahl und Scrollposition jedes Mal verloren gehen – das Löschen
    /// eines Eintrags wäre kaum zu treffen. Deshalb werden vorhandene Zeilen an Ort und
    /// Stelle fortgeschrieben und nur echte Zu- und Abgänge bewegt.
    /// </remarks>
    private async Task RefreshWorklistAsync()
    {
        var items = await _client.GetWorklistAsync();

        // Auswahl merken – auch die mehrfache. Wer drei Einträge zum Löschen markiert hat,
        // soll sie nach der nächsten Aktualisierung noch markiert vorfinden.
        var selectedIds = WorklistGrid.SelectedItems
            .OfType<WorklistRow>()
            .Select(r => r.Id)
            .ToHashSet(StringComparer.Ordinal);

        var incoming = items.ToDictionary(i => i.Id, StringComparer.Ordinal);

        for (var i = _vm.Worklist.Count - 1; i >= 0; i--)
        {
            if (!incoming.ContainsKey(_vm.Worklist[i].Id))
                _vm.Worklist.RemoveAt(i);
        }

        var existing = _vm.Worklist.ToDictionary(i => i.Id, StringComparer.Ordinal);

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];

            // Vorhandene Zeilen nur fortschreiben: Die Zeile meldet die Änderung selbst,
            // das Gitter muss nicht neu gelesen werden und die Auswahl bleibt bestehen.
            if (existing.TryGetValue(item.Id, out var current))
            {
                current.Update(item);
                continue;
            }

            _vm.Worklist.Insert(Math.Min(i, _vm.Worklist.Count), WorklistRow.From(item));
        }

        RestoreWorklistSelection(selectedIds);

        WorklistUpdatedText.Text = $"Stand: {DateTime.Now:HH:mm:ss}" +
                                   (items.Count == 0 ? " – keine offenen Aufträge" : $" – {items.Count} Einträge");
    }

    private void RestoreWorklistSelection(IReadOnlySet<string> ids)
    {
        if (ids.Count == 0)
            return;

        foreach (var row in _vm.Worklist.Where(r => ids.Contains(r.Id)))
        {
            if (!WorklistGrid.SelectedItems.Contains(row))
                WorklistGrid.SelectedItems.Add(row);
        }
    }

    private async Task RefreshPendingStudiesAsync()
    {
        var studies = await _client.GetPendingStudiesAsync();

        _vm.PendingStudies.Clear();
        foreach (var study in studies)
            _vm.PendingStudies.Add(study);
    }

    // -----------------------------------------------------------------------
    // Dienststeuerung
    // -----------------------------------------------------------------------

    private void OnInstallServiceClick(object sender, RoutedEventArgs e) => RunServiceAction(ServiceControl.Install);
    private void OnUninstallServiceClick(object sender, RoutedEventArgs e) => RunServiceAction(ServiceControl.Uninstall);
    private void OnStartServiceClick(object sender, RoutedEventArgs e) => RunServiceAction(ServiceControl.Start);
    private void OnStopServiceClick(object sender, RoutedEventArgs e) => RunServiceAction(ServiceControl.Stop);
    private void OnRestartServiceClick(object sender, RoutedEventArgs e) => RunServiceAction(ServiceControl.Restart);

    private void RunServiceAction(Func<(bool Success, string Message)> action)
    {
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            var (success, message) = action();
            _vm.StatusMessage = message;

            if (!success)
                MessageBox.Show(message, "GDT2DICOM", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            Mouse.OverrideCursor = null;
            _vm.ServiceStateText = ServiceControl.DescribeStatus();
        }
    }

    // -----------------------------------------------------------------------
    // Werkzeuge
    // -----------------------------------------------------------------------

    private async void OnEchoTestClick(object sender, RoutedEventArgs e)
    {
        if (RemoteNodeGrid.SelectedItem is not RemoteNodeConfig node)
        {
            EchoResultText.Text = "Bitte zuerst eine Gegenstelle auswählen.";
            EchoResultText.Foreground = (Brush)FindResource("MutedBrush");
            return;
        }

        EchoResultText.Text = $"Teste {node.Host}:{node.Port} …";
        EchoResultText.Foreground = (Brush)FindResource("MutedBrush");

        var result = await _client.EchoAsync(new EchoRequestDto
        {
            Host = node.Host,
            Port = node.Port,
            CallingAe = _vm.Config.Dicom.AeTitle,
            CalledAe = node.AeTitle
        });

        EchoResultText.Text = result.Message;
        EchoResultText.Foreground = (Brush)FindResource(result.Success ? "OkBrush" : "ErrorBrush");
    }

    private void OnCreateTestGdtClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var gdt = _vm.Config.Gdt;
            Directory.CreateDirectory(gdt.InboxDirectory);

            var path = Path.Combine(gdt.InboxDirectory, $"test_{DateTime.Now:yyyyMMdd_HHmmss}.gdt");
            WriteTestOrder(path);

            _vm.StatusMessage = $"Testauftrag geschrieben: {path}";

            var hinweis = gdt.EnableInboxWatcher
                ? "Läuft der Dienst, greift die Verzeichnisüberwachung binnen einer Sekunde zu: " +
                  "Die Datei verschwindet dann wieder und der Patient steht in der Worklist."
                : "Die Verzeichnisüberwachung ist abgeschaltet – die Datei bleibt liegen, bis " +
                  "der Programmaufruf kommt.";

            MessageBox.Show($"Testauftrag wurde erzeugt:\n\n{path}\n\n{hinweis}",
                "GDT2DICOM", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Testauftrag konnte nicht erzeugt werden:\n\n{ex.Message}",
                "GDT2DICOM", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void OnRefreshWorklistClick(object sender, RoutedEventArgs e) => await RefreshWorklistAsync();

    private async void OnDeleteWorklistItemClick(object sender, RoutedEventArgs e)
    {
        // Momentaufnahme: Die Auswahl ändert sich, sobald Einträge verschwinden.
        var selected = WorklistGrid.SelectedItems.OfType<WorklistRow>().ToList();

        if (selected.Count == 0)
        {
            _vm.StatusMessage = "Kein Eintrag markiert. Mehrere lassen sich mit Strg oder Umschalt markieren.";
            return;
        }

        if (MessageBox.Show(BuildDeleteQuestion(selected), "GDT2DICOM",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        // Die zyklische Aktualisierung anhalten, damit sie nicht mitten in die Löschung fährt.
        _refreshInProgress = true;
        var fehlgeschlagen = 0;

        try
        {
            foreach (var row in selected)
            {
                if (!await _client.RemoveWorklistItemAsync(row.Id))
                    fehlgeschlagen++;
            }
        }
        finally
        {
            _refreshInProgress = false;
        }

        await RefreshWorklistAsync();

        if (fehlgeschlagen > 0)
        {
            MessageBox.Show(
                $"{fehlgeschlagen} von {selected.Count} Einträgen konnten nicht entfernt werden. " +
                "Möglicherweise hat der Dienst sie zwischenzeitlich selbst aufgeräumt.",
                "GDT2DICOM", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _vm.StatusMessage = selected.Count == 1
            ? $"Auftrag {selected[0].AccessionNumber} entfernt."
            : $"{selected.Count} Aufträge entfernt.";
    }

    private static string BuildDeleteQuestion(IReadOnlyList<WorklistRow> selected)
    {
        if (selected.Count == 1)
        {
            return $"Auftrag {selected[0].AccessionNumber} für {selected[0].PatientName} " +
                   "aus der Worklist entfernen?";
        }

        var liste = string.Join("\n", selected.Take(12)
            .Select(r => $"   {r.AccessionNumber}   {r.PatientName}"));

        if (selected.Count > 12)
            liste += $"\n   … und {selected.Count - 12} weitere";

        return $"{selected.Count} Aufträge aus der Worklist entfernen?\n\n{liste}";
    }

    /// <summary>Pfad zu GDT2DICOM.Aufruf.exe – liegt neben der Oberfläche.</summary>
    private static string? FindConnector()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "GDT2DICOM.Aufruf.exe"),
            Path.Combine(Directory.GetParent(AppContext.BaseDirectory)?.FullName ?? "", "GDT2DICOM.Aufruf.exe")
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private void UpdateConnectorCommand()
    {
        var connector = FindConnector();

        if (connector is null)
        {
            ConnectorCommandBox.Text = "GDT2DICOM.Aufruf.exe wurde nicht gefunden.";
            ConnectorStateText.Text = "Die Datei muss neben der Oberfläche liegen.";
            ConnectorStateText.Foreground = (Brush)FindResource("ErrorBrush");
            return;
        }

        // In Anführungszeichen, weil der Pfad üblicherweise Leerzeichen enthält.
        ConnectorCommandBox.Text = $"\"{connector}\"";
        ConnectorFetchCommandBox.Text = $"\"{connector}\" --abholen";
    }

    private void OnCopyConnectorCommandClick(object sender, RoutedEventArgs e) =>
        CopyToClipboard(ConnectorCommandBox.Text);

    private void OnCopyFetchCommandClick(object sender, RoutedEventArgs e) =>
        CopyToClipboard(ConnectorFetchCommandBox.Text);

    private void CopyToClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
            ConnectorStateText.Text = "In die Zwischenablage kopiert.";
            ConnectorStateText.Foreground = (Brush)FindResource("OkBrush");
        }
        catch (Exception ex)
        {
            ConnectorStateText.Text = $"Kopieren fehlgeschlagen: {ex.Message}";
            ConnectorStateText.Foreground = (Brush)FindResource("ErrorBrush");
        }
    }

    private void OnConnectorDiagnoseClick(object sender, RoutedEventArgs e)
    {
        var connector = FindConnector();
        if (connector is null)
        {
            MessageBox.Show("GDT2DICOM.Aufruf.exe wurde nicht gefunden.", "GDT2DICOM",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (HasPendingChanges())
        {
            var answer = MessageBox.Show(
                "Die Diagnose liest die gespeicherte Konfiguration. Ihre Änderungen sind noch nicht " +
                "gespeichert.\n\nTrotzdem starten?",
                "GDT2DICOM", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (answer != MessageBoxResult.Yes)
                return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = connector, Arguments = "--diagnose", UseShellExecute = true });
            ConnectorStateText.Text = "Diagnose gestartet.";
            ConnectorStateText.Foreground = (Brush)FindResource("MutedBrush");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Die Diagnose konnte nicht gestartet werden:\n\n{ex.Message}", "GDT2DICOM",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Stellt den PVS-Aufruf realistisch nach: Die Auftragsdatei entsteht außerhalb des
    /// überwachten Eingangs und wird dem Connector als Argument übergeben – genau so, wie
    /// ein PVS es tut. Damit gibt es kein Wettrennen mit der Verzeichnisüberwachung.
    /// </summary>
    private void OnConnectorTestOrderClick(object sender, RoutedEventArgs e)
    {
        var connector = FindConnector();
        if (connector is null)
        {
            MessageBox.Show("GDT2DICOM.Aufruf.exe wurde nicht gefunden.", "GDT2DICOM",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var directory = Path.Combine(Path.GetTempPath(), "GDT2DICOM-Test");
            Directory.CreateDirectory(directory);

            var path = Path.Combine(directory, $"testauftrag_{DateTime.Now:yyyyMMdd_HHmmss}.gdt");
            WriteTestOrder(path);

            Process.Start(new ProcessStartInfo
            {
                FileName = connector,
                Arguments = $"\"{path}\"",
                UseShellExecute = true
            });

            ConnectorStateText.Text = $"Testauftrag über den Aufruf gesendet: {Path.GetFileName(path)}. " +
                                      "Der Patient sollte gleich im Reiter Worklist stehen.";
            ConnectorStateText.Foreground = (Brush)FindResource("OkBrush");
        }
        catch (Exception ex)
        {
            ConnectorStateText.Text = $"Fehlgeschlagen: {ex.Message}";
            ConnectorStateText.Foreground = (Brush)FindResource("ErrorBrush");
        }
    }

    /// <summary>Schreibt einen vollständigen Beispielauftrag (Satzart 6302).</summary>
    private void WriteTestOrder(string path)
    {
        var gdt = _vm.Config.Gdt;
        var map = gdt.FieldMap;
        var now = DateTime.Now;

        var record = new GdtRecord()
            .Add(map.Satzidentifikation, gdt.RequestSatzart)
            .Add(map.Satzlaenge, "00000")
            .Add(map.EmpfaengerId, gdt.SenderId)
            .Add(map.SenderId, gdt.ReceiverId)
            .Add(map.GdtVersion, GdtSerializer.VersionCode(gdt.Version))
            .Add(map.Zeichensatz, GdtSerializer.CharsetCode(gdt.Charset))
            .Add(map.PatientId, "TEST0815")
            .Add(map.PatientName, "Mustermann")
            .Add(map.PatientVorname, "Erika")
            .Add(map.PatientGeburtsdatum, "17031975")
            .Add(map.PatientGeschlecht, "2")
            .Add(map.PatientGroesseCm, "168")
            .Add(map.PatientGewichtKg, "64")
            .Add(map.DeviceIdent, gdt.DeviceIdent)
            .Add(map.UntersuchungsDatum, GdtValues.DateToGdt(now))
            .Add(map.UntersuchungsUhrzeit, GdtValues.TimeToGdt(now))
            .Add(map.Anforderung, "Abdomen-Sonographie (Testauftrag)");

        GdtSerializer.WriteFile(path, record, gdt.Charset);
    }

    private async void OnCheckPathsClick(object sender, RoutedEventArgs e)
    {
        PathCheckSummary.Text = "Prüfung läuft …";
        PathCheckSummary.Foreground = (Brush)FindResource("MutedBrush");

        // Großzügig bemessen: Der Dienst prüft jedes Verzeichnis einzeln mit eigenem Zeitlimit,
        // bei mehreren nicht erreichbaren Freigaben summiert sich das.
        using var abbruch = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        PathCheckResultDto? result;
        try
        {
            result = await _client.CheckPathsAsync(abbruch.Token);
        }
        catch (OperationCanceledException)
        {
            result = null;
        }

        if (result is null)
        {
            PathCheckGrid.Visibility = Visibility.Collapsed;
            PathCheckSummary.Text = "Der Dienst ist nicht erreichbar oder antwortet nicht. Die Prüfung muss " +
                                    "im Dienst laufen – bitte den Dienst starten und erneut versuchen.";
            PathCheckSummary.Foreground = (Brush)FindResource("ErrorBrush");
            return;
        }

        PathCheckGrid.ItemsSource = result.Checks;
        PathCheckGrid.Visibility = Visibility.Visible;

        var fehler = result.Checks.Count(c => !c.Ok);
        var netz = result.Checks.Count(c => c.IsUnc);
        var alsSystem = result.ServiceAccount.EndsWith(@"\SYSTEM", StringComparison.OrdinalIgnoreCase)
                        || result.ServiceAccount.Equals("NT AUTHORITY\\SYSTEM", StringComparison.OrdinalIgnoreCase);

        var text = $"Dienstkonto: {result.ServiceAccount} — " +
                   (fehler == 0
                       ? $"alle {result.Checks.Count} Verzeichnisse erreichbar."
                       : $"{fehler} von {result.Checks.Count} nicht erreichbar.");

        // Der häufigste Stolperstein: Netzwerkfreigabe plus LocalSystem. Dieses Konto hat
        // im Netz keine Anmeldedaten und kommt an keine geschützte Freigabe heran.
        if (netz > 0 && alsSystem)
        {
            text += "\nAchtung: Es sind Netzwerkpfade konfiguriert, der Dienst läuft aber als " +
                    "lokales Systemkonto. Dieses Konto hat im Netz keine Anmeldedaten. Stellen Sie den " +
                    "Dienst in services.msc auf ein Benutzerkonto mit Zugriff auf die Freigabe um.";
        }

        PathCheckSummary.Text = text;
        PathCheckSummary.Foreground = (Brush)FindResource(
            fehler == 0 && !(netz > 0 && alsSystem) ? "OkBrush" : "ErrorBrush");
    }

    // -----------------------------------------------------------------------
    // Reiter „Über“
    // -----------------------------------------------------------------------

    private static string AppVersion
    {
        get
        {
            var v = typeof(MainWindow).Assembly.GetName().Version;
            return v is null ? "unbekannt" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    private void FillAboutTab()
    {
        VersionText.Text = $"Version {AppVersion}";
        SupportInfoBox.Text = BuildSupportInfo();
    }

    /// <summary>
    /// Angaben, die bei einer Supportanfrage regelmäßig gebraucht werden. Sie hier
    /// zusammenzustellen erspart die übliche Rückfragerunde nach Version und Umgebung.
    /// </summary>
    private string BuildSupportInfo()
    {
        var status = _vm.Status;

        var zeilen = new List<string>
        {
            $"GDT2DICOM        {AppVersion}",
            $"Dienst           {ServiceControl.DescribeStatus()}",
            $"Windows          {Environment.OSVersion.VersionString}",
            $".NET             {Environment.Version}",
            $"Rechner          {Environment.MachineName}",
            $"Konfiguration    {ConfigStore.ConfigFilePath}",
            $"Protokolle       {_vm.Config.General.LogDirectory}"
        };

        if (status is not null)
        {
            zeilen.Add($"DICOM            {status.DicomAeTitle} auf Port {status.DicomPort}, " +
                       $"{(status.DicomServerRunning ? "läuft" : "nicht aktiv")}");
            zeilen.Add($"GDT-Eingang      {status.GdtInboxDirectory}");
            zeilen.Add($"GDT-Ausgang      {status.GdtOutboxDirectory}");
            zeilen.Add($"Zähler           {status.GdtRequestsProcessed} Aufträge, " +
                       $"{status.WorklistQueries} Worklist-Abfragen, " +
                       $"{status.InstancesReceived} Objekte, " +
                       $"{status.StudiesExported} Untersuchungen exportiert");
        }
        else
        {
            zeilen.Add("Dienst           nicht erreichbar – Laufzeitangaben fehlen");
        }

        return string.Join(Environment.NewLine, zeilen);
    }

    private void OnCopySupportInfoClick(object sender, RoutedEventArgs e)
    {
        SupportInfoBox.Text = BuildSupportInfo();

        try
        {
            Clipboard.SetText(SupportInfoBox.Text);
            SupportCopyState.Text = "In die Zwischenablage kopiert – bitte der E-Mail beifügen.";
            SupportCopyState.Foreground = (Brush)FindResource("OkBrush");
        }
        catch (Exception ex)
        {
            SupportCopyState.Text = $"Kopieren fehlgeschlagen: {ex.Message}";
            SupportCopyState.Foreground = (Brush)FindResource("ErrorBrush");
        }
    }

    /// <summary>
    /// Zeigt den mitgelieferten Lizenztext. Die GPL verlangt in § 4, dass jede Kopie des
    /// Programms die Lizenz begleitet – deshalb liegt LICENSE.txt neben der Anwendung und
    /// wird nicht bloß im Netz verlinkt. Fehlt sie, bleibt der Verweis auf gnu.org.
    /// </summary>
    private void OnShowLicenseClick(object sender, RoutedEventArgs e) =>
        ZeigeLizenzdatei("LICENSE.txt", "https://www.gnu.org/licenses/gpl-3.0.txt");

    /// <summary>
    /// Zeigt die zusätzliche Erlaubnis nach GPL § 7. Sie begleitet das Paket aus demselben
    /// Grund wie der Lizenztext selbst: Ohne sie ist nicht erkennbar, warum dieses Paket
    /// trotz der MS-PL der DICOM-Bibliotheken weitergegeben werden darf.
    /// </summary>
    private void OnShowExceptionClick(object sender, RoutedEventArgs e) =>
        ZeigeLizenzdatei("LICENSE-EXCEPTION.txt",
                         "https://github.com/HEPHEPHEP/GDT2DICOM/blob/main/LICENSE-EXCEPTION.md");

    private void ZeigeLizenzdatei(string dateiname, string ersatzverweis)
    {
        var neben = Path.Combine(AppContext.BaseDirectory, dateiname);
        var ziel = File.Exists(neben) ? neben : ersatzverweis;

        try
        {
            Process.Start(new ProcessStartInfo(ziel) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Der Lizenztext konnte nicht geöffnet werden:\n\n{ziel}\n\n{ex.Message}",
                "GDT2DICOM", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnHyperlinkNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Der Verweis konnte nicht geöffnet werden:\n\n{e.Uri}\n\n{ex.Message}",
                "GDT2DICOM", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        e.Handled = true;
    }

    private void OnMeasureArchiveClick(object sender, RoutedEventArgs e)
    {
        var directory = _vm.Config.Export.DicomArchiveDirectory;

        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            ArchiveSizeText.Text = "Archivverzeichnis existiert noch nicht.";
            ArchiveSizeText.Foreground = (Brush)FindResource("MutedBrush");
            return;
        }

        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            var (studies, bytes) = Gdt2Dicom.Core.Runtime.ArchiveCleanup.Measure(directory);
            ArchiveSizeText.Text = studies == 0
                ? "Archiv ist leer."
                : $"{studies} Untersuchungen, {bytes / 1024.0 / 1024.0 / 1024.0:0.00} GB";
            ArchiveSizeText.Foreground = (Brush)FindResource("AccentBrush");
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    private async void OnRefreshLogClick(object sender, RoutedEventArgs e) => await RefreshLogAsync();

    private void OnOpenLogFolderClick(object sender, RoutedEventArgs e) =>
        OpenFolder(_vm.Config.General.LogDirectory);

    private void OnOpenInboxClick(object sender, RoutedEventArgs e) =>
        OpenFolder(_vm.Config.Gdt.InboxDirectory);

    private void OnOpenOutboxClick(object sender, RoutedEventArgs e) =>
        OpenFolder(_vm.Config.Gdt.OutboxDirectory);

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ordner konnte nicht geöffnet werden:\n\n{ex.Message}",
                "GDT2DICOM", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnBrowseFolderClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string target })
            return;

        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Verzeichnis auswählen",
            Multiselect = false
        };

        var current = GetConfigString(target);
        if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current))
            dialog.InitialDirectory = current;

        if (dialog.ShowDialog(this) == true)
            SetConfigString(target, dialog.FolderName);
    }

    private string? GetConfigString(string path)
    {
        var (owner, property) = ResolveConfigProperty(path);
        return owner is null ? null : property?.GetValue(owner) as string;
    }

    private void SetConfigString(string path, string value)
    {
        var (owner, property) = ResolveConfigProperty(path);
        if (owner is null || property is null)
            return;

        property.SetValue(owner, value);

        // Die Konfigurationsklassen melden keine Änderungen – deshalb die Bindungen neu setzen.
        _vm.Config = _vm.Config;
    }

    private (object? Owner, System.Reflection.PropertyInfo? Property) ResolveConfigProperty(string path)
    {
        var parts = path.Split('.');
        object? current = _vm.Config;

        for (var i = 0; i < parts.Length - 1; i++)
        {
            var next = current?.GetType().GetProperty(parts[i]);
            current = next?.GetValue(current);
            if (current is null)
                return (null, null);
        }

        return (current, current?.GetType().GetProperty(parts[^1]));
    }

    // -----------------------------------------------------------------------
    // Feldkennungs-Editor
    // -----------------------------------------------------------------------

    private void BuildFieldMapEditors()
    {
        AddFieldRows(FieldMapHeader,
            ("Satzidentifikation", nameof(GdtFieldMap.Satzidentifikation)),
            ("Satzlänge", nameof(GdtFieldMap.Satzlaenge)),
            ("Empfänger-ID", nameof(GdtFieldMap.EmpfaengerId)),
            ("Sender-ID", nameof(GdtFieldMap.SenderId)),
            ("Zeichensatz", nameof(GdtFieldMap.Zeichensatz)),
            ("GDT-Version", nameof(GdtFieldMap.GdtVersion)),
            ("Gerätekennfeld", nameof(GdtFieldMap.DeviceIdent)));

        AddFieldRows(FieldMapPatient,
            ("Patientennummer", nameof(GdtFieldMap.PatientId)),
            ("Name", nameof(GdtFieldMap.PatientName)),
            ("Vorname", nameof(GdtFieldMap.PatientVorname)),
            ("Geburtsdatum", nameof(GdtFieldMap.PatientGeburtsdatum)),
            ("Geschlecht", nameof(GdtFieldMap.PatientGeschlecht)),
            ("Titel", nameof(GdtFieldMap.PatientTitel)),
            ("Straße", nameof(GdtFieldMap.PatientStrasse)),
            ("PLZ / Ort", nameof(GdtFieldMap.PatientPlzOrt)),
            ("Größe (cm)", nameof(GdtFieldMap.PatientGroesseCm)),
            ("Gewicht (kg)", nameof(GdtFieldMap.PatientGewichtKg)));

        AddFieldRows(FieldMapExam,
            ("Untersuchungsdatum", nameof(GdtFieldMap.UntersuchungsDatum)),
            ("Untersuchungsuhrzeit", nameof(GdtFieldMap.UntersuchungsUhrzeit)),
            ("Anforderung (Text)", nameof(GdtFieldMap.Anforderung)),
            ("Anforderungskennung", nameof(GdtFieldMap.AnforderungsIdent)),
            ("Auftragsnummer", nameof(GdtFieldMap.Auftragsnummer)),
            ("Überweiser", nameof(GdtFieldMap.UeberweiserName)));

        AddFieldRows(FieldMapResult,
            ("Befundzeile", nameof(GdtFieldMap.BefundZeile)),
            ("Kommentar", nameof(GdtFieldMap.Kommentar)),
            ("Anhang: Format", nameof(GdtFieldMap.AnhangFormat)),
            ("Anhang: Dateiverweis", nameof(GdtFieldMap.AnhangVerweis)),
            ("Anhang: Beschreibung", nameof(GdtFieldMap.AnhangBeschreibung)));

        AddFieldRows(FieldMapObjects,
            ("Objekt-Beginn", nameof(GdtFieldMap.ObjektBeginn)),
            ("Objekt-Ende", nameof(GdtFieldMap.ObjektEnde)),
            ("Objektname Patient", nameof(GdtFieldMap.ObjektNamePatient)),
            ("Objektname Untersuchung", nameof(GdtFieldMap.ObjektNameUntersuchung)),
            ("Objektname Anhang", nameof(GdtFieldMap.ObjektNameAnhang)));
    }

    private static void AddFieldRows(Panel target, params (string Label, string Property)[] fields)
    {
        foreach (var (label, property) in fields)
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 16, 2), LastChildFill = true };

            row.Children.Add(new TextBlock
            {
                Text = label,
                Width = 170,
                VerticalAlignment = VerticalAlignment.Center
            });

            var box = new TextBox { MinWidth = 90 };
            box.SetBinding(TextBox.TextProperty, new Binding($"Config.Gdt.FieldMap.{property}")
            {
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            });

            row.Children.Add(box);
            target.Children.Add(row);
        }
    }
}

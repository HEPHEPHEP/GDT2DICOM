using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Gdt2Dicom.Connector;

/// <summary>
/// Kleines Fenster, das während des Wartens auf den Rücksatz angezeigt wird.
/// </summary>
/// <remarks>
/// Ohne Fenster sähe der Anwender nur ein PVS, das minutenlang nicht reagiert, und würde
/// irgendwann den Vorgang abwürgen. Deshalb: sichtbarer Zustand und ein Abbruchknopf.
/// Bewusst ohne XAML aufgebaut – das Programm startet bei jedem Klick im PVS neu, da zählt
/// jede eingesparte Ladezeit.
/// </remarks>
public sealed class WaitWindow : Window
{
    private readonly TextBlock _status;
    private readonly CancellationTokenSource _cancellation;

    public bool Cancelled { get; private set; }

    public WaitWindow(string patientName, int timeoutSeconds, CancellationTokenSource cancellation)
    {
        _cancellation = cancellation;

        Title = "GDT2DICOM";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        Background = Brushes.White;

        var layout = new StackPanel { Margin = new Thickness(20) };

        layout.Children.Add(new TextBlock
        {
            Text = "Untersuchung läuft",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1B, 0x5E, 0x8C)),
            Margin = new Thickness(0, 0, 0, 10)
        });

        layout.Children.Add(new TextBlock
        {
            Text = $"Der Auftrag für {patientName} steht am Gerät bereit. " +
                   "Dieses Fenster schließt sich automatisch, sobald die Bilder eingetroffen " +
                   "und an die Praxissoftware übergeben sind.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14)
        });

        _status = new TextBlock
        {
            Foreground = Brushes.Gray,
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 16)
        };
        layout.Children.Add(_status);

        var cancel = new Button
        {
            Content = "Abbrechen",
            Padding = new Thickness(14, 5, 14, 5),
            HorizontalAlignment = HorizontalAlignment.Right,
            MinWidth = 110
        };
        cancel.Click += (_, _) =>
        {
            Cancelled = true;
            _cancellation.Cancel();
            Close();
        };
        layout.Children.Add(cancel);

        Content = layout;

        StartCountdown(timeoutSeconds);
    }

    /// <summary>
    /// Zeigt die verbleibende Zeit an. Ohne Zeitlimit (0) läuft stattdessen die bereits
    /// verstrichene Zeit mit – der Anwender sieht so trotzdem, dass etwas passiert.
    /// </summary>
    private void StartCountdown(int timeoutSeconds)
    {
        var unbegrenzt = timeoutSeconds <= 0;
        var sekunden = timeoutSeconds;
        var verstrichen = 0;

        UpdateStatus(unbegrenzt, sekunden, verstrichen);

        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };

        timer.Tick += (_, _) =>
        {
            verstrichen++;
            sekunden--;
            UpdateStatus(unbegrenzt, sekunden, verstrichen);

            if (!unbegrenzt && sekunden <= 0)
                timer.Stop();
        };

        timer.Start();
        Closed += (_, _) => timer.Stop();
    }

    private void UpdateStatus(bool unbegrenzt, int verbleibend, int verstrichen)
    {
        if (unbegrenzt)
        {
            _status.Text = $"Läuft seit {TimeSpan.FromSeconds(verstrichen):mm\\:ss} – " +
                           "kein Zeitlimit gesetzt. Mit „Abbrechen“ beenden.";
            return;
        }

        _status.Text = verbleibend > 0
            ? $"Wartezeit noch {TimeSpan.FromSeconds(verbleibend):mm\\:ss} Minuten."
            : "Zeitlimit erreicht.";
    }

    /// <summary>Schließt das Fenster aus einem Hintergrund-Task heraus.</summary>
    public void CloseFromBackground()
    {
        Dispatcher.Invoke(Close);
    }
}

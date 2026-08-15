using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Gdt2Dicom.Connector;

/// <summary>
/// Ausgabe für den Diagnose-Modus.
/// </summary>
/// <remarks>
/// Das Programm ist eine Windows-Anwendung ohne eigene Konsole – sonst würde bei jedem Klick
/// im PVS ein schwarzes Fenster aufblitzen. Für die Diagnose ist eine Konsolenausgabe aber
/// genau das Richtige, deshalb wird die Konsole des aufrufenden Fensters mitbenutzt, wenn es
/// eine gibt. Sonst landet alles in einem Meldungsfenster.
/// </remarks>
public sealed class ConsoleOutput
{
    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    private readonly StringBuilder _buffer = new();
    private readonly bool _hasConsole;

    public ConsoleOutput()
    {
        _hasConsole = TryAttach();
    }

    private static bool TryAttach()
    {
        try
        {
            if (!AttachConsole(AttachParentProcess))
                return false;

            // Nach dem Anhängen zeigen die Standardströme noch ins Leere; sie müssen
            // neu geöffnet werden.
            var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            Console.SetOut(stdout);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Write(string line = "")
    {
        _buffer.AppendLine(line);
        if (_hasConsole)
            Console.WriteLine(line);
    }

    /// <summary>Zeigt die gesammelte Ausgabe als Fenster, wenn keine Konsole verfügbar war.</summary>
    public void Flush(string title)
    {
        if (_hasConsole)
        {
            Console.WriteLine();
            return;
        }

        System.Windows.MessageBox.Show(_buffer.ToString(), title,
            System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
    }
}

using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;

namespace Gdt2Dicom.Core.Runtime;

/// <summary>
/// Hält die letzten Logzeilen im Speicher, damit die GUI ein Live-Log anzeigen kann,
/// ohne die Logdatei zu sperren.
/// </summary>
public sealed class LogRingBuffer : ILogEventSink
{
    private readonly int _capacity;
    private readonly Queue<string> _lines;
    private readonly object _lock = new();
    private readonly MessageTemplateTextFormatter _formatter;

    public static LogRingBuffer Instance { get; } = new(2000);

    public LogRingBuffer(int capacity)
    {
        _capacity = capacity;
        _lines = new Queue<string>(capacity);
        _formatter = new MessageTemplateTextFormatter(
            "{Timestamp:HH:mm:ss} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}");
    }

    public void Emit(LogEvent logEvent)
    {
        using var writer = new StringWriter();
        _formatter.Format(logEvent, writer);
        var text = writer.ToString().TrimEnd();

        lock (_lock)
        {
            foreach (var line in text.Split('\n'))
            {
                _lines.Enqueue(line.TrimEnd('\r'));
                while (_lines.Count > _capacity)
                    _lines.Dequeue();
            }
        }
    }

    public IReadOnlyList<string> Tail(int count)
    {
        lock (_lock)
        {
            return _lines.Reverse().Take(Math.Max(1, count)).Reverse().ToList();
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _lines.Clear();
        }
    }
}

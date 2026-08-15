using Microsoft.Extensions.Logging;

namespace Gdt2Dicom.TestClient;

/// <summary>Schlanker Logger, damit Meldungen aus dem Core auf der Konsole sichtbar werden.</summary>
public sealed class ConsoleLogger : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Console.WriteLine($"  [{logLevel}] {formatter(state, exception)}");
        if (exception is not null)
            Console.WriteLine($"          {exception.GetType().Name}: {exception.Message}");
    }
}

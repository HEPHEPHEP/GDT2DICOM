using Gdt2Dicom.Core.Configuration;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace Gdt2Dicom.Core.Runtime;

/// <summary>Richtet Serilog ein: rollierende Tagesdatei plus Ringpuffer für die GUI.</summary>
public static class LoggingSetup
{
    public static LogEventLevel ParseLevel(string? level) => level?.Trim().ToLowerInvariant() switch
    {
        "verbose" or "trace" => LogEventLevel.Verbose,
        "debug" => LogEventLevel.Debug,
        "warning" or "warn" => LogEventLevel.Warning,
        "error" => LogEventLevel.Error,
        "fatal" => LogEventLevel.Fatal,
        _ => LogEventLevel.Information
    };

    public static ILoggerFactory Create(GeneralConfig config, bool alsoToConsole = false)
    {
        Directory.CreateDirectory(config.LogDirectory);

        var configuration = new LoggerConfiguration()
            .MinimumLevel.Is(ParseLevel(config.LogLevel))
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.Sink(LogRingBuffer.Instance)
            .WriteTo.File(
                Path.Combine(config.LogDirectory, "gdt2dicom-.log"),
                rollingInterval: RollingInterval.Day,
                // Zweite Sicherung neben LogCleanup: begrenzt die Anzahl, falls der Dienst
                // sehr lange durchläuft. null = Serilog räumt nicht selbst auf.
                // Die Bedingung muss dieselbe sein wie in LogCleanup – sonst würde Serilog
                // bei einer Aufbewahrung von 0 Tagen alles löschen, obwohl LogCleanup das
                // gerade als Fehleingabe ablehnt.
                retainedFileCountLimit: config.DeleteOldLogs && config.LogRetentionDays > 0
                    ? config.LogRetentionDays
                    : null,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}",
                shared: true);

        if (alsoToConsole)
        {
            configuration = configuration.WriteTo.Console(
                outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}");
        }

        Log.Logger = configuration.CreateLogger();

        return new SerilogLoggerFactory(Log.Logger, dispose: false);
    }

    public static void Shutdown() => Log.CloseAndFlush();
}

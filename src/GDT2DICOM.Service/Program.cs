using Gdt2Dicom.Core.Configuration;
using Gdt2Dicom.Core.Runtime;
using Gdt2Dicom.Service;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Serilog;

// Im Dienstkontext ist das Arbeitsverzeichnis C:\Windows\System32 – das würde relative
// Pfade und die Codec-DLLs von fo-dicom ins Leere laufen lassen.
Directory.SetCurrentDirectory(AppContext.BaseDirectory);

ConfigStore.EnsureRootDirectory();
var config = ConfigStore.LoadSafe(out _);

var runningAsService = WindowsServiceHelpers.IsWindowsService();
var loggerFactory = LoggingSetup.Create(config.General, alsoToConsole: !runningAsService);

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddSerilog(dispose: false);

builder.Services.AddSingleton(loggerFactory);
builder.Services.AddHostedService<MiddlewareWorker>();

builder.Services.AddWindowsService(options => options.ServiceName = "GDT2DICOM");

var host = builder.Build();

if (!runningAsService)
{
    Console.WriteLine("GDT2DICOM läuft im Konsolenmodus. Beenden mit Strg+C.");
    Console.WriteLine($"Konfiguration: {ConfigStore.ConfigFilePath}");
    Console.WriteLine($"Logs:          {config.General.LogDirectory}");
    Console.WriteLine();
}

await host.RunAsync();
LoggingSetup.Shutdown();

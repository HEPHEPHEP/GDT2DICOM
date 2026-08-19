using System.Diagnostics;
using System.IO;
using System.ServiceProcess;

namespace Gdt2Dicom.Gui;

/// <summary>Installiert, startet und stoppt den Windows-Dienst über sc.exe.</summary>
public static class ServiceControl
{
    public const string ServiceName = "GDT2DICOM";
    public const string DisplayName = "GDT2DICOM Middleware (PVS ↔ Sonographie)";

    /// <summary>Pfad zur Dienst-Exe. Liegt neben der GUI oder im Unterordner "Service".</summary>
    public static string? FindServiceExecutable()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "GDT2DICOM.Service.exe"),
            Path.Combine(AppContext.BaseDirectory, "Service", "GDT2DICOM.Service.exe"),
            Path.Combine(Directory.GetParent(AppContext.BaseDirectory)?.FullName ?? "", "Service", "GDT2DICOM.Service.exe")
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    public static bool IsInstalled() =>
        ServiceController.GetServices().Any(s => string.Equals(s.ServiceName, ServiceName, StringComparison.OrdinalIgnoreCase));

    public static ServiceControllerStatus? GetStatus()
    {
        try
        {
            using var controller = new ServiceController(ServiceName);
            return controller.Status;
        }
        catch
        {
            return null;
        }
    }

    public static string DescribeStatus()
    {
        if (!IsInstalled())
            return "nicht installiert";

        return GetStatus() switch
        {
            ServiceControllerStatus.Running => "läuft",
            ServiceControllerStatus.Stopped => "gestoppt",
            ServiceControllerStatus.StartPending => "startet",
            ServiceControllerStatus.StopPending => "wird beendet",
            ServiceControllerStatus.Paused => "angehalten",
            null => "unbekannt",
            _ => "unbekannt"
        };
    }

    public static (bool Success, string Message) Install()
    {
        var exe = FindServiceExecutable();
        if (exe is null)
            return (false, "GDT2DICOM.Service.exe wurde nicht gefunden. Liegt die Datei neben der Oberfläche?");

        // chcp 65001 muss stehen bleiben, solange in diesem Skript Umlaute vorkommen:
        // Die Datei wird als UTF-8 geschrieben, cmd.exe liest Batchdateien aber in der
        // Codepage der Konsole – auf einem deutschen Windows üblicherweise 850. Ohne die
        // Umschaltung käme bei sc.exe aus "Ultraschallgerät" ein "UltraschallgerÃ¤t" an.
        var script = $"""
            @echo off
            chcp 65001 > nul
            sc.exe create "{ServiceName}" binPath= "\"{exe}\"" start= auto DisplayName= "{DisplayName}"
            if errorlevel 1 goto :fehler
            sc.exe description "{ServiceName}" "Verbindet ein Praxisverwaltungssystem per GDT mit einem Ultraschallgerät per DICOM (Worklist, Storage, MPPS)."
            sc.exe failure "{ServiceName}" reset= 86400 actions= restart/5000/restart/15000/restart/60000
            sc.exe start "{ServiceName}"
            exit /b 0
            :fehler
            echo Der Dienst konnte nicht angelegt werden.
            pause
            exit /b 1
            """;

        return RunElevatedScript(script, "Dienst installieren");
    }

    public static (bool Success, string Message) Uninstall()
    {
        var script = $"""
            @echo off
            sc.exe stop "{ServiceName}"
            timeout /t 3 /nobreak > nul
            sc.exe delete "{ServiceName}"
            exit /b 0
            """;

        return RunElevatedScript(script, "Dienst entfernen");
    }

    public static (bool Success, string Message) Start() =>
        RunElevatedScript($"@echo off\r\nsc.exe start \"{ServiceName}\"\r\nexit /b 0", "Dienst starten");

    public static (bool Success, string Message) Stop() =>
        RunElevatedScript($"@echo off\r\nsc.exe stop \"{ServiceName}\"\r\nexit /b 0", "Dienst stoppen");

    public static (bool Success, string Message) Restart()
    {
        var script = $"""
            @echo off
            sc.exe stop "{ServiceName}"
            timeout /t 3 /nobreak > nul
            sc.exe start "{ServiceName}"
            exit /b 0
            """;

        return RunElevatedScript(script, "Dienst neu starten");
    }

    /// <summary>
    /// Führt ein Batch-Skript mit erhöhten Rechten aus. Der Umweg über eine Datei erspart
    /// das Quoting-Chaos, das sc.exe mit Leerzeichen im Pfad sonst verursacht.
    /// </summary>
    private static (bool Success, string Message) RunElevatedScript(string script, string title)
    {
        var path = Path.Combine(Path.GetTempPath(), $"gdt2dicom_{Guid.NewGuid():N}.cmd");

        try
        {
            // Ausdrücklich UTF-8 ohne BOM statt Encoding.Default: Der Wert ist unter .NET
            // zwar ohnehin UTF-8, aber das Skript verlässt sich darauf – zusammen mit dem
            // chcp 65001 in seiner ersten Zeile. Wer hier etwas ändert, muss beides ansehen.
            File.WriteAllText(path, script, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var process = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{path}\"",
                Verb = "runas",
                UseShellExecute = true,
                CreateNoWindow = false,
                WindowStyle = ProcessWindowStyle.Hidden
            });

            if (process is null)
                return (false, $"{title}: Der Vorgang wurde nicht gestartet.");

            process.WaitForExit(60000);
            return process.ExitCode == 0
                ? (true, $"{title}: erfolgreich.")
                : (false, $"{title}: Fehler (Rückgabewert {process.ExitCode}).");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return (false, $"{title}: Die Rechteerhöhung wurde abgelehnt.");
        }
        catch (Exception ex)
        {
            return (false, $"{title}: {ex.Message}");
        }
        finally
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // Temporäre Datei bleibt liegen – unkritisch.
            }
        }
    }
}

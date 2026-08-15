namespace Gdt2Dicom.Connector;

/// <summary>Das Ergebnis der Kommandozeilenauswertung.</summary>
public sealed record ConnectorArguments(
    string? GdtFile,
    bool Diagnose,
    bool Help,
    bool ForceWait,
    bool ForceNoWait,
    IReadOnlyList<string> Raw,
    bool Abholen = false,
    string? PatientId = null)
{
    /// <summary>Gibt an, ob das Warteverhalten auf der Kommandozeile übersteuert wurde.</summary>
    public bool? WaitOverride => ForceWait ? true : ForceNoWait ? false : null;
}

/// <summary>
/// Wertet die Kommandozeile aus, die das PVS absetzt.
/// </summary>
/// <remarks>
/// Es gibt keine einheitliche Konvention: die einen übergeben den Pfad blank, die anderen
/// mit einem Präfix wie <c>/GDT=</c>, wieder andere rufen ganz ohne Argument auf und
/// verlassen sich auf feste Verzeichnisse. Deshalb wird bewusst tolerant gelesen, statt auf
/// einer Form zu bestehen.
/// </remarks>
public static class CommandLine
{
    private static readonly string[] FileSwitches = { "gdt", "f", "file", "datei", "in", "input" };

    public static ConnectorArguments Parse(string[] args)
    {
        string? file = null;
        string? patientId = null;
        var diagnose = false;
        var help = false;
        var forceWait = false;
        var forceNoWait = false;
        var abholen = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.IsNullOrWhiteSpace(arg))
                continue;

            if (IsSwitch(arg, out var name, out var inlineValue))
            {
                switch (name)
                {
                    case "diagnose" or "test" or "pruefen" or "prüfen":
                        diagnose = true;
                        continue;
                    case "help" or "?" or "h" or "hilfe":
                        help = true;
                        continue;
                    case "warten" or "wait":
                        forceWait = true;
                        continue;
                    case "nichtwarten" or "nowait":
                        forceNoWait = true;
                        continue;
                    case "abholen" or "fetch" or "ruecksatz" or "rücksatz":
                        abholen = true;
                        continue;
                    case "patid" or "patient" or "patientennummer":
                        if (!string.IsNullOrEmpty(inlineValue))
                            patientId ??= Unquote(inlineValue);
                        else if (i + 1 < args.Length)
                            patientId ??= Unquote(args[++i]);
                        continue;
                }

                if (FileSwitches.Contains(name))
                {
                    // Entweder /GDT=C:\... oder /GDT C:\...
                    if (!string.IsNullOrEmpty(inlineValue))
                        file ??= Unquote(inlineValue);
                    else if (i + 1 < args.Length)
                        file ??= Unquote(args[++i]);
                    continue;
                }

                // Unbekannter Schalter: überlesen statt abbrechen. Manche PVS hängen
                // eigene Angaben an, die uns nichts angehen.
                continue;
            }

            file ??= Unquote(arg);
        }

        return new ConnectorArguments(file, diagnose, help, forceWait, forceNoWait, args, abholen, patientId);
    }

    private static bool IsSwitch(string arg, out string name, out string? value)
    {
        name = "";
        value = null;

        if (arg.Length < 2 || (arg[0] != '/' && arg[0] != '-'))
            return false;

        // Ein Pfad wie /home/... kommt unter Windows nicht vor, C:\... beginnt nie mit / oder -.
        var body = arg.TrimStart('/', '-');
        if (body.Length == 0)
            return false;

        var separator = body.IndexOfAny(new[] { '=', ':' });

        // Vorsicht bei "-:" – ein Doppelpunkt an Position 1 wäre ein Laufwerksbuchstabe.
        if (separator > 0)
        {
            name = body[..separator].ToLowerInvariant();
            value = body[(separator + 1)..];
        }
        else
        {
            name = body.ToLowerInvariant();
        }

        return true;
    }

    private static string Unquote(string value) => value.Trim().Trim('"');
}

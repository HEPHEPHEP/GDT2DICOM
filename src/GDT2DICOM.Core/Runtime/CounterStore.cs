namespace Gdt2Dicom.Core.Runtime;

/// <summary>Fortlaufende Zähler, die einen Dienstneustart überleben (Accession, Dateinamen).</summary>
public sealed class CounterStore
{
    private readonly string _path;
    private readonly object _lock = new();
    private readonly Dictionary<string, long> _values = new(StringComparer.OrdinalIgnoreCase);

    public CounterStore(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _path = Path.Combine(dataDirectory, "counters.txt");
        Load();
    }

    private void Load()
    {
        if (!File.Exists(_path))
            return;

        foreach (var line in File.ReadAllLines(_path))
        {
            var parts = line.Split('=', 2);
            if (parts.Length == 2 && long.TryParse(parts[1], out var value))
                _values[parts[0]] = value;
        }
    }

    private void PersistUnsafe()
    {
        try
        {
            File.WriteAllLines(_path, _values.Select(kv => $"{kv.Key}={kv.Value}"));
        }
        catch
        {
            // Ein verlorener Zählerstand ist unkritisch – im schlimmsten Fall beginnt er neu.
        }
    }

    public long Next(string name, long start = 1)
    {
        lock (_lock)
        {
            var value = _values.TryGetValue(name, out var current) ? current + 1 : start;
            _values[name] = value;
            PersistUnsafe();
            return value;
        }
    }

    public long Peek(string name, long start = 1)
    {
        lock (_lock)
        {
            return _values.TryGetValue(name, out var current) ? current : start - 1;
        }
    }
}

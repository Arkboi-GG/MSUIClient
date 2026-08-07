using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SnapshotParity;

internal static class JsonStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static readonly JsonSerializerOptions LineOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static T Read<T>(string path) where T : class =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path, Encoding.UTF8), Options)
        ?? throw new InvalidDataException($"could not parse {path}");

    public static void Write<T>(string path, T value)
    {
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, JsonSerializer.Serialize(value, Options) + "\n", new UTF8Encoding(false));
    }

    public static List<T> ReadLines<T>(string path) where T : class
    {
        if (!File.Exists(path)) return [];
        var values = new List<T>();
        int lineNumber = 0;
        foreach (string line in File.ReadLines(path, Encoding.UTF8))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#')) continue;
            values.Add(JsonSerializer.Deserialize<T>(line, LineOptions)
                ?? throw new InvalidDataException($"could not parse {path}:{lineNumber}"));
        }
        return values;
    }

    public static void WriteLines<T>(string path, IEnumerable<T> values)
    {
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        using var writer = new StreamWriter(full, false, new UTF8Encoding(false));
        foreach (T value in values) writer.WriteLine(JsonSerializer.Serialize(value, LineOptions));
    }

    public static void AppendLine<T>(string path, T value) where T : class
    {
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.AppendAllText(full, JsonSerializer.Serialize(value, LineOptions) + "\n",
            new UTF8Encoding(false));
    }

    public static void UpdateLine<T>(string path, Func<T, bool> predicate, Action<T> update)
        where T : class
    {
        string[] lines = File.ReadAllLines(path, Encoding.UTF8);
        int match = -1;
        T? value = null;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#')) continue;
            T parsed = JsonSerializer.Deserialize<T>(line, LineOptions)
                ?? throw new InvalidDataException($"could not parse {path}:{i + 1}");
            if (!predicate(parsed)) continue;
            if (match >= 0) throw new InvalidDataException("update matched more than one JSONL row");
            match = i;
            value = parsed;
        }
        if (match < 0 || value is null) throw new InvalidDataException("JSONL row was not found");
        update(value);
        lines[match] = JsonSerializer.Serialize(value, LineOptions);
        File.WriteAllLines(path, lines, new UTF8Encoding(false));
    }
}

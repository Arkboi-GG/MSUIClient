using System.Text.Json;

namespace MSUIClient.Engine;

/// <summary>Bounds-portrait knobs. Defaults preserve the pre-lab camera exactly.</summary>
public sealed record PortraitTuning
{
    public float HeadFraction { get; init; } = 0.92f;
    public float WindowFraction { get; init; } = 0.34f;
    public float WindowMin { get; init; } = 0.55f;
    public float WindowMax { get; init; } = 1.10f;
    public float FovyDegrees { get; init; } = 0.5f * 180f / MathF.PI;
    public float YawOffset { get; init; } = 0.42f;
    public float Pitch { get; init; } = 0.02f;
    public float NearFloor { get; init; } = 0.02f;
    public PortraitCameraSource? ForceSource { get; init; }

    public static readonly PortraitTuning Default = new();
}

/// <summary>
/// Tolerant, hand-editable portrait-overrides.json store at the repository root.
/// Missing or malformed data starts empty and never prevents the client from loading.
/// </summary>
public sealed class PortraitOverrideStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    private readonly string _path;
    private readonly Dictionary<string, PortraitTuning> _overrides;

    private PortraitOverrideStore(string path, Dictionary<string, PortraitTuning> overrides)
    {
        _path = path;
        _overrides = overrides;
    }

    public static PortraitOverrideStore Load(string repoRoot)
    {
        string path = Path.Combine(repoRoot, "portrait-overrides.json");
        var overrides = new Dictionary<string, PortraitTuning>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(path))
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, PortraitTuning>>(
                    File.ReadAllText(path), Options);
                if (parsed is not null)
                    foreach (var pair in parsed)
                        overrides[pair.Key] = pair.Value;
                Console.WriteLine($"[portrait] {overrides.Count} framing override(s) in {path}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[portrait] could not read {path} - starting empty ({ex.Message})");
            overrides.Clear();
        }
        return new PortraitOverrideStore(path, overrides);
    }

    public PortraitTuning? Find(string key) =>
        _overrides.TryGetValue(key, out PortraitTuning? tuning) ? tuning : null;

    public void Set(string key, PortraitTuning tuning)
    {
        _overrides[key] = tuning;
        Save();
    }

    public void Remove(string key)
    {
        _overrides.Remove(key);
        Save();
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(_overrides, Options));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[portrait] could not write {_path} - {ex.Message}");
        }
    }
}

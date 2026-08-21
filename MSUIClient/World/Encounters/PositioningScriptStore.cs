using System.Text;
using System.Text.Json;

namespace MSUIClient.World.Encounters;

/// <summary>Versioned, per-user file format for the reusable positioning library.</summary>
public sealed class PositioningScriptFileDto
{
    public int SchemaVersion { get; set; } = PositioningScriptStore.SchemaVersion;
    public Dictionary<string, PositioningScriptDto>? Scripts { get; set; }
}

/// <summary>
/// Durable positioning scripts keyed by their stable library id. The sibling of
/// <see cref="CombatPlanStore"/> for the other slot: a rotation says what a body
/// presses, a positioning script says where it stands. Both are libraries you
/// assign from and clone, not per-body copies.
///
/// Reads and writes are deliberately tolerant, exactly like the plan store: a
/// missing or malformed file never blocks the Lab from opening, and a failed
/// mutation reports false rather than throwing into the UI loop.
/// </summary>
public sealed class PositioningScriptStore
{
    public const int SchemaVersion = 1;

    private readonly Dictionary<string, PositioningScript> _scripts = new(StringComparer.Ordinal);
    private readonly List<string> _errors = [];

    public string FilePath { get; }
    public IReadOnlyDictionary<string, PositioningScript> Scripts => _scripts;
    public IReadOnlyList<string> Errors => _errors;

    public PositioningScriptStore(string filePath) => FilePath = filePath;

    public int Load()
    {
        _scripts.Clear();
        _errors.Clear();
        if (!File.Exists(FilePath)) return 0;

        try
        {
            PositioningScriptFileDto? dto = JsonSerializer.Deserialize<PositioningScriptFileDto>(
                File.ReadAllText(FilePath), EncounterLibrary.JsonOptions);
            if (dto is null)
            {
                _errors.Add("positioning-scripts.json: empty document");
                return 0;
            }
            if (dto.SchemaVersion != SchemaVersion)
            {
                _errors.Add($"positioning-scripts.json: schemaVersion {dto.SchemaVersion} " +
                            $"(this build reads {SchemaVersion})");
                return 0;
            }

            foreach ((string key, PositioningScriptDto value) in dto.Scripts ?? [])
            {
                if (string.IsNullOrWhiteSpace(key) || value is null) continue;
                PositioningScript? script = EncounterLibrary.PositioningScriptFromDto(value);
                if (script is not null) _scripts[key] = script with { Id = key };
            }
        }
        catch (Exception ex)
        {
            _scripts.Clear();
            _errors.Add($"positioning-scripts.json: {ex.Message}");
        }
        return _scripts.Count;
    }

    public PositioningScript? Find(string id) =>
        string.IsNullOrWhiteSpace(id) ? null : _scripts.GetValueOrDefault(id);

    /// <summary>Add or replace a script by its id and persist atomically. When the
    /// script has no id yet, one is minted from its name; the returned script carries
    /// the id that was actually stored so the caller can assign it to a body.</summary>
    public bool Upsert(PositioningScript script, out PositioningScript stored)
    {
        string id = string.IsNullOrWhiteSpace(script.Id) ? MintId(script.Name) : script.Id;
        stored = script with { Id = id };

        bool hadPrevious = _scripts.TryGetValue(id, out PositioningScript? previous);
        _scripts[id] = stored;
        if (Save()) return true;

        if (hadPrevious) _scripts[id] = previous!;
        else _scripts.Remove(id);
        return false;
    }

    public bool Remove(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        if (!_scripts.Remove(id, out PositioningScript? removed)) return true;
        if (Save()) return true;
        _scripts[id] = removed;
        return false;
    }

    /// <summary>A readable, unique id from a display name — a slug plus a numeric
    /// suffix only if the slug already exists. Human-diffable ids beat opaque guids
    /// in a file the owner may open.</summary>
    private string MintId(string name)
    {
        var slug = new StringBuilder();
        foreach (char c in name.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) slug.Append(c);
            else if ((c == ' ' || c == '-' || c == '_') &&
                     slug.Length > 0 && slug[^1] != '-') slug.Append('-');
        }
        string baseId = slug.Length > 0 ? slug.ToString().Trim('-') : "positioning";
        if (!_scripts.ContainsKey(baseId)) return baseId;
        for (int n = 2; ; n++)
        {
            string candidate = $"{baseId}-{n}";
            if (!_scripts.ContainsKey(candidate)) return candidate;
        }
    }

    private bool Save()
    {
        _errors.Clear();
        string? directory = Path.GetDirectoryName(FilePath);
        string tempPath = FilePath + ".tmp";
        try
        {
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var dto = new PositioningScriptFileDto
            {
                SchemaVersion = SchemaVersion,
                Scripts = _scripts.ToDictionary(
                    pair => pair.Key,
                    pair => EncounterLibrary.PositioningScriptToDto(pair.Value)!,
                    StringComparer.Ordinal),
            };
            File.WriteAllText(tempPath,
                JsonSerializer.Serialize(dto, EncounterLibrary.JsonOptions));
            File.Move(tempPath, FilePath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            _errors.Add($"positioning-scripts.json: {ex.Message}");
            try
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
            catch
            {
                // Best effort only; the original save failure remains the useful error.
            }
            return false;
        }
    }
}

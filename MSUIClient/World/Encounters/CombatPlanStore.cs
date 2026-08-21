using System.Text.Json;

namespace MSUIClient.World.Encounters;

/// <summary>Versioned, per-user file format for reusable character doctrine.</summary>
public sealed class CombatPlanFileDto
{
    public int SchemaVersion { get; set; } = CombatPlanStore.SchemaVersion;
    public Dictionary<string, CombatPlanDto>? CharacterPlans { get; set; }
}

/// <summary>
/// Durable combat plans keyed by stable character identity. Encounter Lab currently
/// supplies preset actor keys; real characters can later supply a GUID-backed key
/// without putting roster identity inside <see cref="CombatPlan"/> itself.
///
/// Reads and writes are deliberately tolerant: a missing or malformed preference file
/// never prevents the Lab from opening, and failed mutations report false rather than
/// throwing into the UI loop.
/// </summary>
public sealed class CombatPlanStore
{
    public const int SchemaVersion = 1;

    private readonly Dictionary<string, CombatPlan> _characterPlans =
        new(StringComparer.Ordinal);
    private readonly List<string> _errors = [];

    public string FilePath { get; }
    public IReadOnlyDictionary<string, CombatPlan> CharacterPlans => _characterPlans;
    public IReadOnlyList<string> Errors => _errors;

    public CombatPlanStore(string filePath) => FilePath = filePath;

    /// <summary>Reload the store. Missing is a valid empty store; malformed and future
    /// files are reported through <see cref="Errors"/> and also produce an empty store.</summary>
    public int Load()
    {
        _characterPlans.Clear();
        _errors.Clear();
        if (!File.Exists(FilePath)) return 0;

        try
        {
            CombatPlanFileDto? dto = JsonSerializer.Deserialize<CombatPlanFileDto>(
                File.ReadAllText(FilePath), EncounterLibrary.JsonOptions);
            if (dto is null)
            {
                _errors.Add("combat-plans.json: empty document");
                return 0;
            }
            if (dto.SchemaVersion != SchemaVersion)
            {
                _errors.Add($"combat-plans.json: schemaVersion {dto.SchemaVersion} " +
                            $"(this build reads {SchemaVersion})");
                return 0;
            }

            foreach ((string key, CombatPlanDto value) in dto.CharacterPlans ?? [])
            {
                if (string.IsNullOrWhiteSpace(key) || value is null) continue;
                CombatPlan? plan = EncounterLibrary.CombatPlanFromDto(value);
                // The dictionary key is the authoritative rotation id; stamp it onto
                // the model so a plan handed to the UI always knows its own library id.
                if (plan is not null) _characterPlans[key] = plan with { Id = key };
            }
        }
        catch (Exception ex)
        {
            _characterPlans.Clear();
            _errors.Add($"combat-plans.json: {ex.Message}");
        }
        return _characterPlans.Count;
    }

    public CombatPlan? Find(string characterKey) =>
        string.IsNullOrWhiteSpace(characterKey)
            ? null
            : _characterPlans.GetValueOrDefault(characterKey);

    /// <summary>Library-style save: treat the plan as a reusable rotation keyed by its
    /// own id. Mints a readable id from the name when the plan has none, and returns the
    /// stored plan (carrying that id) so the caller can assign it to a body by reference.
    /// This is the "assign one rotation to many bodies, clone for the next fight" path;
    /// the per-body <see cref="Upsert(string, CombatPlan)"/> overload remains for legacy
    /// callers.</summary>
    public bool UpsertLibrary(CombatPlan plan, out CombatPlan stored)
    {
        string id = string.IsNullOrWhiteSpace(plan.Id) ? MintId(plan.Name) : plan.Id;
        stored = plan with { Id = id };
        return Upsert(id, stored) && (stored = _characterPlans[id]) is not null;
    }

    /// <summary>A readable, unique rotation id from a display name — a slug, plus a
    /// numeric suffix only when the slug is taken. Mirrors the positioning store so both
    /// libraries produce human-diffable ids.</summary>
    private string MintId(string name)
    {
        var slug = new System.Text.StringBuilder();
        foreach (char c in name.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) slug.Append(c);
            else if ((c == ' ' || c == '-' || c == '_') &&
                     slug.Length > 0 && slug[^1] != '-') slug.Append('-');
        }
        string baseId = slug.Length > 0 ? slug.ToString().Trim('-') : "rotation";
        if (!_characterPlans.ContainsKey(baseId)) return baseId;
        for (int n = 2; ; n++)
        {
            string candidate = $"{baseId}-{n}";
            if (!_characterPlans.ContainsKey(candidate)) return candidate;
        }
    }

    /// <summary>Add or replace a plan and persist it atomically. The in-memory view is
    /// rolled back when persistence fails, so callers never mistake an unsaved edit for
    /// a durable profile.</summary>
    public bool Upsert(string characterKey, CombatPlan plan)
    {
        if (string.IsNullOrWhiteSpace(characterKey)) return false;

        bool hadPrevious = _characterPlans.TryGetValue(characterKey, out CombatPlan? previous);
        _characterPlans[characterKey] = plan;
        if (Save()) return true;

        if (hadPrevious) _characterPlans[characterKey] = previous!;
        else _characterPlans.Remove(characterKey);
        return false;
    }

    /// <summary>Remove and persist a character profile. Removing a missing key is a
    /// successful no-op and does not rewrite the file.</summary>
    public bool Remove(string characterKey)
    {
        if (string.IsNullOrWhiteSpace(characterKey)) return false;
        if (!_characterPlans.Remove(characterKey, out CombatPlan? removed)) return true;
        if (Save()) return true;
        _characterPlans[characterKey] = removed;
        return false;
    }

    private bool Save()
    {
        _errors.Clear();
        string? directory = Path.GetDirectoryName(FilePath);
        string tempPath = FilePath + ".tmp";
        try
        {
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var dto = new CombatPlanFileDto
            {
                SchemaVersion = SchemaVersion,
                CharacterPlans = _characterPlans.ToDictionary(
                    pair => pair.Key,
                    pair => EncounterLibrary.CombatPlanToDto(pair.Value)!,
                    StringComparer.Ordinal),
            };
            File.WriteAllText(tempPath,
                JsonSerializer.Serialize(dto, EncounterLibrary.JsonOptions));
            File.Move(tempPath, FilePath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            _errors.Add($"combat-plans.json: {ex.Message}");
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

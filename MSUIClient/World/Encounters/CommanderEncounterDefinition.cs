using System.Text.Json;
using System.Text.Json.Serialization;

namespace MSUIClient.World.Encounters;

public sealed record CommanderEncounterBounds(float[] Min, float[] Max);
public sealed record CommanderEncounterTeam(string Name, float[] Anchor);
public sealed record CommanderEncounterPhase(byte Id, string Name, int Priority, float HealthMin,
    float HealthMax, int Airborne, uint Aura, bool Alternate, bool Melee, bool Ranged, float ThreatRatio)
{
    public uint ElapsedMinMs { get; init; }
    public uint ElapsedMaxMs { get; init; }
    public uint PeriodMs { get; init; }
    public int MinLivingAdds { get; init; } = -1;
    public int MaxLivingAdds { get; init; } = -1;
    public bool EndWhenAddsDead { get; init; }
}
public sealed record CommanderEncounterRule(string Id, int Priority, string Action, string Trigger,
    uint[] Spells, byte Phase, uint Roles, float Radius, uint DurationMs, string Target, uint Spell,
    bool MissingAura, uint[] PointSpells, float[] Station, byte Toggle)
{
    public float TriggerRadius { get; init; }
    public int ReserveCasters { get; init; }
}
public sealed record CommanderCrowdControlPolicy(uint[] Entries, uint[] Spells, uint Roles, int MaxTargets, byte Phase);
public sealed record CommanderObjectHazardSource(uint Entry, uint CreationSpell);
public sealed record CommanderHazardPolicy(uint[] Entries, uint[] Spells, float Radius, uint DurationMs, uint Roles, int Priority, bool FollowSource, bool OnDeath) { public uint Aura { get; init; } public CommanderObjectHazardSource[] Objects { get; init; } = []; }
public sealed record CommanderDamagePolicy(uint[] Entries, uint[] Auras, uint Schools, bool Always) { public uint Roles { get; init; } = 62; public bool WhileObjectiveAlive { get; init; } }
public sealed record CommanderAddDistancePolicy(uint[] Entries, uint[] References, float Minimum, float Maximum, bool Flat) { public uint ReferenceAura { get; init; } }
public sealed record CommanderWavePolicy(uint Entry, uint Count, uint Aura, uint ActiveMs, uint RecoveryMs);
public sealed record CommanderInteractionPolicy(string Id, uint Entry, uint RequiredItem, float Range, bool AfterCompletion);
public sealed record CommanderMechanics
{
    public CommanderCrowdControlPolicy[] CrowdControl { get; init; } = [];
    public CommanderHazardPolicy[] Hazards { get; init; } = [];
    public CommanderDamagePolicy[] DamagePolicies { get; init; } = [];
    public CommanderAddDistancePolicy[] AddDistances { get; init; } = [];
    public CommanderWavePolicy[] Waves { get; init; } = [];
    public CommanderInteractionPolicy[] Interactions { get; init; } = [];
    public string Completion { get; init; } = "death";
    public uint CompletionStableMs { get; init; } = 1000;
    public uint ThreatSettleMs { get; init; }
    public bool InterruptOwnership { get; init; }
}
public sealed record CommanderEncounterAddRequirement(uint Entry, int Count);
public sealed record CommanderEncounterDefinition(int Schema, string Id, string Name, uint MapId,
    uint BossEntry, CommanderEncounterBounds Bounds, float[] TankAnchor, CommanderEncounterTeam[] Teams,
    int AddTanksPerTeam, int HealersPerTeam, int HealerCount, uint[] AddEntries,
    CommanderEncounterPhase[] Phases, CommanderEncounterRule[] Rules)
{
    public uint ImmuneSchools { get; init; }
    public uint[] Objectives { get; init; } = [];
    public string Coverage { get; init; } = "authored";
    public CommanderEncounterAddRequirement[] RequiredAdds { get; init; } = [];
    public string AddPolicy { get; init; } = "split";
    public CommanderMechanics Mechanics { get; init; } = new();
    public Dictionary<string,double> Tuning { get; init; } = new(StringComparer.Ordinal);
    [JsonIgnore] public uint[] ObjectiveEntries => Objectives.Length == 0 ? [BossEntry] : Objectives;
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
    };
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
    public static CommanderEncounterDefinition Parse(string json)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(json) > 32768) throw new InvalidDataException("Encounter definition exceeds 32 KiB.");
        var definition = JsonSerializer.Deserialize<CommanderEncounterDefinition>(json, JsonOptions)
            ?? throw new InvalidDataException("Empty encounter definition.");
        var errors = Engine.UI.CommanderEncounterLaw.Validate(definition);
        if (errors.Count > 0) throw new InvalidDataException(errors[0]);
        return definition with { Rules = definition.Rules.OrderByDescending(r => r.Priority).ToArray() };
    }
}

public static class CommanderEncounterCatalog
{
    private static readonly Lazy<CommanderEncounterDefinition> BuiltIn = new(() =>
    {
        using var stream = typeof(CommanderEncounterCatalog).Assembly.GetManifestResourceStream("MSUIClient.Encounters.onyxia.json")
            ?? throw new InvalidDataException("Missing bundled encounter definition.");
        using var reader = new StreamReader(stream);
        return CommanderEncounterDefinition.Parse(reader.ReadToEnd());
    });
    public static CommanderEncounterDefinition Default => BuiltIn.Value;
    public static IReadOnlyList<CommanderEncounterDefinition> Load(string directory)
    {
        var result = new SortedDictionary<string, CommanderEncounterDefinition>(StringComparer.Ordinal) { [Default.Id] = Default };
        if (Directory.Exists(directory)) foreach (string file in Directory.EnumerateFiles(directory, "*.json").Order())
        {
            var definition = CommanderEncounterDefinition.Parse(File.ReadAllText(file));
            result[definition.Id] = definition;
        }
        return result.Values.ToArray();
    }
}

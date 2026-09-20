using System.Numerics;
using System.Text.Json;

namespace MSUIClient.World.Encounters;

public enum CommanderRaidRole : byte { Unassigned, MainTank, AddTank, Healer, Melee, Ranged }
public enum CommanderRaidTeam : byte { West, East }

/// <summary>Room coordinates: X north, Y west. These anchors never rotate with the boss.</summary>
public sealed record CommanderRaidAssignment(ulong Guid, string Name, CommanderRaidRole Role,
    CommanderRaidTeam Team, Vector3 Ground, Vector3 Air, uint HealSpell = 0, uint DamageSpell = 0,
    bool Manual = false, ulong HealPrimary = 0, uint FocusEntry = 0,
    uint InterruptSpell = 0, uint DispelSpell = 0, uint TauntSpell = 0, uint DefensiveSpell = 0);

public sealed record CommanderRaidMember(ulong Guid, string Name, uint ClassId, bool Commandable,
    bool Alive, IReadOnlySet<uint> Spells, uint PreferredHeal = 0, uint PreferredDamage = 0)
{
    public uint Level { get; init; }
    public uint MaxHealth { get; init; }
    public uint Armor { get; init; }
    public bool FactsReady { get; init; } = true;
    public bool CanTank { get; init; }
    public uint InterruptSpell { get; init; }
    public uint DispelSpell { get; init; }
    public uint TauntSpell { get; init; }
    public uint DefensiveSpell { get; init; }
}

public sealed record CommanderRaidSpellFact(uint Id, string Name, uint Level, uint School,
    bool Passive, uint[] Effects, uint[] Auras, uint[] Targets, int[] BasePoints,
    uint RecoveryMs, int CastTimeMs, uint PowerType, uint ManaCost, bool AutoRepeat, float MaxRange = 30);

/// <summary>Versioned local preparation. Applying is a separate acknowledged server operation.</summary>
public sealed record CommanderRaidPlan(int Version, string Name, uint MapId, uint BossEntry,
    bool AvoidBreath, bool SpreadFireballs, bool MaintainFearWard,
    IReadOnlyList<CommanderRaidAssignment> Assignments)
{
    public const int CurrentVersion = 3;
    public ulong MainGuid { get; init; }
    public CommanderRaidRole MainRole { get; init; }
    public CommanderEncounterDefinition Encounter { get; init; } = CommanderEncounterCatalog.Default;
    public static CommanderRaidPlan Empty => ForEncounter(CommanderEncounterCatalog.Default);
    public static CommanderRaidPlan ForEncounter(CommanderEncounterDefinition encounter) =>
        new(3, encounter.Name, encounter.MapId, encounter.BossEntry, true, true, true, []) { Encounter = encounter };
}

public static class CommanderRaidPlanStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, IncludeFields = true };
    public static void Save(string path, CommanderRaidPlan plan)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(plan, Options));
        File.Move(temp, path, true);
    }
    public static CommanderRaidPlan Load(string path)
    {
        var plan = JsonSerializer.Deserialize<CommanderRaidPlan>(File.ReadAllText(path), Options);
        if (plan is null || plan.Version is not (1 or 2 or 3) || plan.Assignments is null || plan.Assignments.Count > 40 || plan.Assignments.Any(a => a is null)) throw new InvalidDataException("Unsupported or empty raid plan.");
        var errors = Engine.UI.CommanderEncounterLaw.Validate(plan.Encounter);
        if (errors.Count > 0) throw new InvalidDataException(errors[0]);
        if (plan.MapId != plan.Encounter.MapId || plan.BossEntry != plan.Encounter.BossEntry ||
            plan.Assignments.Any(a => a.Guid == 0 || a.Name is null || (int)a.Team >= plan.Encounter.Teams.Length ||
                !Enum.IsDefined(a.Role) || !Engine.UI.CommanderEncounterLaw.Inside(plan.Encounter, a.Ground) ||
                !Engine.UI.CommanderEncounterLaw.Inside(plan.Encounter, a.Air)))
            throw new InvalidDataException("Saved assignments do not fit their encounter definition.");
        return plan with { Version = CommanderRaidPlan.CurrentVersion };
    }
}

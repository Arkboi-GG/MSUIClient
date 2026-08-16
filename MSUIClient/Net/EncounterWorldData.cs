using System.Numerics;

namespace MSUIClient.Net;

// ─────────────────────────────────────────────────────────────────────────────
// The Encounter Lab's world-DB model: the tables that actually decide what a
// creature does in combat, in the shape the core reads them.
//
// Deliberately separate from DevWorldData (spawns/paths/formations). That file
// belongs to the NPC dev window; this one belongs to the Lab, and keeping them
// apart means neither feature's schema churn can break the other.
//
// UNITS TRAP — the two behaviour tables disagree and the core reconciles them
// at load time:
//   * creature_spells delays are stored in SECONDS and multiplied by
//     IN_MILLISECONDS in ObjectMgr::LoadCreatureSpells. Read raw, every
//     DB-driven creature would appear to cast 1000x too fast.
//   * creature_ai_events params are already MILLISECONDS (EventAI convention).
// Everything published from here is normalised to MILLISECONDS.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>One of the eight ability slots on a creature_spells row. Delays are
/// milliseconds (converted on parse — see the units trap above).</summary>
public sealed record CreatureSpellSlot(
    int Index,
    uint SpellId,
    int Probability,
    int CastTarget,             // enum ScriptTarget (ScriptCommands.h)
    uint TargetParam1,
    uint TargetParam2,
    uint CastFlags,
    int InitialMinMs,
    int InitialMaxMs,
    int RepeatMinMs,
    int RepeatMaxMs,
    uint ScriptId);

/// <summary>A whole creature_spells row: the creature's ability list.</summary>
public sealed record CreatureSpellList(
    uint Entry,
    string Name,
    IReadOnlyList<CreatureSpellSlot> Slots);

/// <summary>One creature_ai_events row. Params are milliseconds/percentages
/// depending on <see cref="EventType"/> — see EventAI_Type in the core.</summary>
public sealed record EventAiEvent(
    uint Id,
    uint CreatureId,
    uint ConditionId,
    int EventType,
    uint InversePhaseMask,
    int Chance,
    uint Flags,
    int Param1,
    int Param2,
    int Param3,
    int Param4,
    uint Action1Script,
    uint Action2Script,
    uint Action3Script,
    string Comment);

/// <summary>One creature_ai_scripts row — the generic script-command shape the
/// core shares across every *_scripts table.</summary>
public sealed record AiScriptCommand(
    uint Id,
    int Delay,
    int Command,                // enum ScriptCommands (ScriptCommands.h)
    uint Datalong,
    uint Datalong2,
    uint Datalong3,
    uint Datalong4,
    uint TargetParam1,
    uint TargetParam2,
    int TargetType,             // enum ScriptTarget
    int Dataint,
    int Dataint2,
    Vector3 Position,
    float Orientation,
    string Comment);

/// <summary>A spell_target_position row: the literal world coordinates a
/// TARGET_LOCATION_DATABASE spell lands on. Onyxia's breath lanes are these.</summary>
public sealed record SpellTargetPosition(
    uint SpellId, int Map, Vector3 Position, float Orientation);

/// <summary>A spell_cone row. NEGATIVE degrees mean a REAR arc — the sign is
/// meaningful, never take the absolute value on parse.</summary>
public sealed record SpellConeRow(uint SpellId, float Degrees);

/// <summary>The behaviour-binding subset of creature_template: which of the three
/// tiers this creature's combat logic actually lives in. Without it, a creature
/// picked in the world cannot be told "you are EventAI", "you have spell list N"
/// or "you are compiled C++ and therefore a hole".</summary>
public sealed record CreatureBehaviourBinding(
    uint Entry,
    string Name,
    string AiName,
    string ScriptName,
    uint SpellListId,
    uint MaxHealth);

/// <summary>
/// An immutable published snapshot of every behaviour table the Lab reads.
/// Published wholesale by <see cref="EncounterDataClient"/>; the game thread only
/// ever reads it.
/// </summary>
public sealed class EncounterWorldData
{
    public required DateTime FetchedUtc { get; init; }
    public required string Source { get; init; }                 // "csv" | "csv-cache" | "none"
    public string? Error { get; init; }

    public required IReadOnlyDictionary<uint, CreatureSpellList> SpellListsByEntry { get; init; }
    public required IReadOnlyDictionary<uint, IReadOnlyList<EventAiEvent>> EventsByCreature { get; init; }
    public required IReadOnlyDictionary<uint, IReadOnlyList<AiScriptCommand>> ScriptsById { get; init; }
    public required IReadOnlyDictionary<uint, SpellTargetPosition> TargetPositions { get; init; }
    public required IReadOnlyDictionary<uint, float> ConeDegrees { get; init; }
    public IReadOnlyDictionary<uint, CreatureBehaviourBinding> Bindings { get; init; } =
        new Dictionary<uint, CreatureBehaviourBinding>();

    public static EncounterWorldData Empty(string? error = null) => new()
    {
        FetchedUtc = DateTime.UtcNow,
        Source = "none",
        Error = error,
        SpellListsByEntry = new Dictionary<uint, CreatureSpellList>(),
        EventsByCreature = new Dictionary<uint, IReadOnlyList<EventAiEvent>>(),
        ScriptsById = new Dictionary<uint, IReadOnlyList<AiScriptCommand>>(),
        TargetPositions = new Dictionary<uint, SpellTargetPosition>(),
        ConeDegrees = new Dictionary<uint, float>(),
    };

    public CreatureBehaviourBinding? Binding(uint entry) => Bindings.GetValueOrDefault(entry);

    public IReadOnlyList<EventAiEvent> EventsFor(uint creatureId) =>
        EventsByCreature.TryGetValue(creatureId, out IReadOnlyList<EventAiEvent>? events) ? events : [];

    public IReadOnlyList<AiScriptCommand> Script(uint scriptId) =>
        ScriptsById.TryGetValue(scriptId, out IReadOnlyList<AiScriptCommand>? script) ? script : [];

    public CreatureSpellList? SpellList(uint spellListId) =>
        SpellListsByEntry.GetValueOrDefault(spellListId);

    /// <summary>One-line readout for the window's provenance panel.</summary>
    public string Describe() =>
        $"{SpellListsByEntry.Count} spell lists · {EventsByCreature.Count} EventAI creatures · " +
        $"{ScriptsById.Count} scripts · {TargetPositions.Count} DB target positions · " +
        $"{ConeDegrees.Count} cones · {Bindings.Count} bindings";
}

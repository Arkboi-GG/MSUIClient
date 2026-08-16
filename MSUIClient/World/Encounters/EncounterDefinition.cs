using System.Numerics;

namespace MSUIClient.World.Encounters;

// ─────────────────────────────────────────────────────────────────────────────
// The Encounter Lab's keystone: a DECLARATIVE description of what an NPC does
// in combat. One document, two consumers — the client simulates and draws it,
// and (later) a server-side interpreter can execute the same facts.
//
// The single binding rule: this format is DATA, never code. Everything here is
// statically analyzable, which is the only reason the client can answer "what
// would hit this spot, when, and why" without running the encounter. The moment
// a behaviour needs real branching logic it does NOT get an escape into a
// scripting language — it gets an ability marked UnknownUnmodeled and shows up
// in the coverage report as a hole. A visible hole beats a confident lie.
//
// Field names and the fidelity registry deliberately mirror
// docs/plans/DYNAMIC_COMBAT_RULES_AND_ENCOUNTER_INTELLIGENCE.md §11.3-11.4 so
// the two designs converge instead of forking.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// How much a single fact can be trusted. The registry is §11.3's, verbatim —
/// do not invent parallel vocabulary. Anything the simulator cannot honestly
/// reproduce must be <see cref="UnknownUnmodeled"/> rather than approximated
/// silently.
/// </summary>
public enum EncounterFidelity
{
    /// <summary>Straight out of a world-DB table. Reproducible exactly.</summary>
    ExactDb,
    /// <summary>Transcribed from a compiled C++ script by a reviewed manifest.
    /// Only as good as its review, and stale the moment the core commit moves —
    /// which is why definitions carry a core build hash.</summary>
    DeclaredCppManifest,
    /// <summary>Derived from spell data (DBC / spell_template): radii, cones,
    /// cast times, missile speed.</summary>
    DerivedDbc,
    /// <summary>An informed guess — scaffolded by tooling, not yet reviewed.</summary>
    Heuristic,
    /// <summary>Known to exist, deliberately not modeled. Draws as a hole.</summary>
    UnknownUnmodeled,
}

/// <summary>What makes an ability or a phase transition fire.</summary>
public enum EncounterTriggerKind
{
    /// <summary>Never fires on its own; driven by a phase step or another ability.</summary>
    Manual,
    /// <summary>Repeating timer with an initial and repeat window (the EventAI idiom).</summary>
    Timer,
    /// <summary>Caster health fraction drops below <see cref="EncounterTriggerSpec.Threshold"/>.</summary>
    HealthBelow,
    /// <summary>Caster health fraction rises above the threshold.</summary>
    HealthAbove,
    /// <summary>Caster mana fraction drops below the threshold.</summary>
    ManaBelow,
    /// <summary>Combat start.</summary>
    OnAggro,
    /// <summary>Fires once when the owning phase becomes active.</summary>
    OnPhaseEnter,
    /// <summary>Fires when the actor finishes a scripted move (the MovementInform seam).</summary>
    OnMovementDone,
    /// <summary>A hostile is within <see cref="EncounterTriggerSpec.Threshold"/> yards.</summary>
    TargetInRange,
    /// <summary>Caster died.</summary>
    OnDeath,
}

/// <summary>Where an ability's effect is anchored. This is the encounter-level
/// echo of a spell's implicit target: it decides the ORIGIN, while
/// <see cref="EncounterGeometrySpec"/> decides the SHAPE around it.</summary>
public enum EncounterTargetKind
{
    Self,
    CurrentVictim,
    RandomHostile,
    /// <summary>Random hostile excluding the current victim — the "random raider"
    /// pattern most casts-on-non-tank abilities use.</summary>
    RandomHostileNotVictim,
    NearestHostile,
    /// <summary>Every hostile in range; the geometry is evaluated per target.</summary>
    AllHostiles,
    /// <summary>Coordinates come from the world DB (spell_target_position) for the
    /// spell ids in <see cref="EncounterGeometrySpec.PointSpellIds"/>. This is how
    /// Onyxia's breath lanes are exact rather than guessed.</summary>
    DatabaseLocation,
    /// <summary>A literal authored point.</summary>
    FixedPoint,
}

/// <summary>The shape an ability puts on the ground. Deliberately small: every
/// kind here must be drawable AND hit-testable against a body capsule.</summary>
public enum FootprintKind
{
    /// <summary>No spatial footprint (a buff, a summon, a script action).</summary>
    None,
    /// <summary>Filled disc of <see cref="EncounterGeometrySpec.Radius"/>.</summary>
    Circle,
    /// <summary>Sector of <see cref="EncounterGeometrySpec.ConeDegrees"/> around the
    /// caster's facing. NEGATIVE degrees mean a REAR arc — the sign convention comes
    /// straight from the world DB's spell_cone table (Tail Sweep is -120).</summary>
    Cone,
    /// <summary>Swept capsule from origin to target, <see cref="EncounterGeometrySpec.Width"/>
    /// yards wide. Charges, cleave lines.</summary>
    Line,
    /// <summary>A chain of spheres along authored/DB points — the breath-lane shape.</summary>
    PointChain,
    /// <summary>A travelling impact: leaves the origin at
    /// <see cref="EncounterGeometrySpec.ProjectileSpeed"/> yd/s and lands as a circle.</summary>
    Projectile,
}

/// <summary>A step in an authored choreography. The vocabulary is a deliberate
/// subset of the core's own SCRIPT_COMMAND_* set, so a sequence authored here
/// stays translatable into DB script rows later instead of becoming a private
/// dialect.</summary>
public enum EncounterStepKind
{
    Wait,
    MoveTo,
    Cast,
    SetFlying,
    SetSpeed,
    Say,
    Summon,
    SetPhase,
    DespawnSummons,
    /// <summary>Explicitly marks a beat the format cannot express. Carries a note
    /// and forces the containing phase's fidelity down.</summary>
    Unmodeled,
}

/// <summary>Where a fact came from, in a form a human can go and check.</summary>
/// <param name="Kind">"db-table", "cpp", "dbc", "authored".</param>
/// <param name="Location">Table.column, file:symbol, or DBC field.</param>
public sealed record EncounterSourceRef(string Kind, string Location, string? Detail = null)
{
    public override string ToString() =>
        Detail is null ? $"{Kind}:{Location}" : $"{Kind}:{Location} ({Detail})";
}

/// <summary>Version pinning. VMaNGOS mixes DB content with compiled scripts and
/// per-patch data, so a definition that does not say which core commit and which
/// content patch it was written against is not reproducible.</summary>
public sealed record EncounterProvenance(
    string Source,
    string? CoreBuildHash = null,
    string? DbRevision = null,
    int? ContentPatch = null,
    DateTime? CapturedUtc = null);

/// <summary>Timer window in milliseconds. Min/max are a RANGE on purpose: the core
/// rolls urand() between them, so the simulator shows a band, never a fake exact
/// beat.</summary>
public readonly record struct EncounterTiming(
    int InitialMinMs, int InitialMaxMs, int RepeatMinMs, int RepeatMaxMs)
{
    public static readonly EncounterTiming Never = new(0, 0, 0, 0);
    public bool Repeats => RepeatMaxMs > 0;
    /// <summary>True when the window is a point, not a range — the only case where
    /// a single predicted timestamp is honest.</summary>
    public bool Deterministic => InitialMinMs == InitialMaxMs && RepeatMinMs == RepeatMaxMs;
}

public readonly record struct EncounterTriggerSpec(
    EncounterTriggerKind Kind,
    float Threshold = 0f,
    int Param1 = 0,
    int Param2 = 0)
{
    public static readonly EncounterTriggerSpec Manual = new(EncounterTriggerKind.Manual);
}

public readonly record struct EncounterTargetSpec(
    EncounterTargetKind Kind,
    Vector3 Point = default,
    uint Param1 = 0)
{
    public static readonly EncounterTargetSpec Victim = new(EncounterTargetKind.CurrentVictim);
    public static readonly EncounterTargetSpec Caster = new(EncounterTargetKind.Self);
}

/// <summary>
/// The shape of an ability's effect. Values are yards / degrees / milliseconds.
/// A definition may leave these at zero and let the geometry resolver fill them
/// from spell data — authored values always win, so a manifest can correct a
/// spell whose DBC radius does not match observed behaviour.
/// </summary>
public sealed record EncounterGeometrySpec(
    FootprintKind Kind,
    float Radius = 0f,
    float ConeDegrees = 0f,
    float Width = 0f,
    float ProjectileSpeed = 0f,
    int DurationMs = 0,
    IReadOnlyList<Vector3>? Points = null,
    IReadOnlyList<uint>? PointSpellIds = null)
{
    public static readonly EncounterGeometrySpec None = new(FootprintKind.None);
    /// <summary>Rear arcs are stored as a negative degree count (the spell_cone
    /// convention). Callers wanting an absolute half-angle should use this.</summary>
    public float AbsoluteConeDegrees => MathF.Abs(ConeDegrees);
    public bool IsRearCone => ConeDegrees < 0f;
}

/// <summary>One beat of authored choreography.</summary>
public sealed record EncounterStep(
    EncounterStepKind Kind,
    int DurationMs = 0,
    Vector3 Point = default,
    uint SpellId = 0,
    uint Entry = 0,
    int Count = 0,
    float Value = 0f,
    bool Flag = false,
    string? PhaseKey = null,
    string? Note = null);

/// <summary>
/// One thing an NPC does. This is the unit the timeline shows, the overlay draws,
/// and the position probe answers about.
/// </summary>
public sealed record EncounterAbility(
    string Key,
    string Name,
    uint SpellId,
    EncounterTriggerSpec Trigger,
    EncounterTiming Timing,
    EncounterTargetSpec Target,
    EncounterGeometrySpec Geometry,
    EncounterFidelity Fidelity,
    IReadOnlyList<string>? Phases = null,
    int ChancePercent = 100,
    int CastTimeMs = 0,
    IReadOnlyList<EncounterStep>? Steps = null,
    IReadOnlyList<EncounterSourceRef>? Sources = null,
    string? Note = null)
{
    /// <summary>Active in every phase when no phase list is given.</summary>
    public bool ActiveIn(string phaseKey) =>
        Phases is null || Phases.Count == 0 || Phases.Contains(phaseKey);

    public bool HasFootprint => Geometry.Kind != FootprintKind.None;
}

/// <summary>A transition out of a phase. Transitions own choreography because
/// that is exactly what EventAI cannot express today and what makes a boss a
/// boss — Onyxia's takeoff is six ordered beats, not one action.</summary>
public sealed record EncounterTransition(
    string ToPhase,
    EncounterTriggerSpec Trigger,
    EncounterFidelity Fidelity,
    IReadOnlyList<EncounterStep>? Steps = null,
    IReadOnlyList<EncounterSourceRef>? Sources = null,
    string? Note = null);

public sealed record EncounterPhase(
    string Key,
    string Name,
    IReadOnlyList<EncounterTransition>? Transitions = null,
    IReadOnlyList<EncounterStep>? OnEnter = null,
    bool CasterFlying = false,
    bool MeleeEnabled = true,
    string? Note = null);

/// <summary>A body in the scenario. The boss is one of these; so is every add and
/// every stand-here dummy the user drops.</summary>
public sealed record EncounterActorSpec(
    string Key,
    string Name,
    uint Entry,
    EncounterActorRole Role,
    Vector3 Position = default,
    float Facing = 0f,
    float BoundingRadius = 0.5f,
    float CombatReach = 1.5f,
    uint Level = 60,
    uint MaxHealth = 1000);

public enum EncounterActorRole
{
    Boss,
    Add,
    /// <summary>A player-side body. The sim never decides what these do — the user
    /// places them, which is the entire point of the position probe.</summary>
    Friendly,
}

/// <summary>Which behaviour sources were actually consulted. §11.3's rule: report
/// source flags, never a misleading completeness percentage.</summary>
[Flags]
public enum EncounterCoverage
{
    None = 0,
    Template = 1 << 0,
    CreatureSpells = 1 << 1,
    EventAi = 1 << 2,
    CreatureSpellScripts = 1 << 3,
    CppCreatureScript = 1 << 4,
    InstanceScript = 1 << 5,
    AddScripts = 1 << 6,
}

/// <summary>
/// The whole encounter. One NPC entry is not always one encounter, so this is
/// keyed independently and lists its member entries.
/// </summary>
public sealed record EncounterDefinition(
    string Key,
    string Name,
    uint PrimaryEntry,
    IReadOnlyList<EncounterPhase> Phases,
    IReadOnlyList<EncounterAbility> Abilities,
    EncounterProvenance Provenance,
    EncounterCoverage Coverage,
    IReadOnlyList<uint>? MemberEntries = null,
    IReadOnlyList<int>? MapIds = null,
    IReadOnlyList<EncounterActorSpec>? Actors = null,
    int SchemaVersion = EncounterSchema.Version,
    string? Note = null)
{
    public EncounterPhase? Phase(string key) => Phases.FirstOrDefault(p => p.Key == key);
    public EncounterPhase? FirstPhase => Phases.Count > 0 ? Phases[0] : null;

    /// <summary>Abilities live in this phase (or in every phase).</summary>
    public IEnumerable<EncounterAbility> AbilitiesIn(string phaseKey) =>
        Abilities.Where(a => a.ActiveIn(phaseKey));

    /// <summary>The worst fidelity anywhere in the document. An encounter is only
    /// as trustworthy as its weakest modeled beat, and the UI says so out loud.</summary>
    public EncounterFidelity WorstFidelity()
    {
        EncounterFidelity worst = EncounterFidelity.ExactDb;
        foreach (EncounterAbility ability in Abilities)
            if (ability.Fidelity > worst) worst = ability.Fidelity;
        foreach (EncounterPhase phase in Phases)
            foreach (EncounterTransition transition in phase.Transitions ?? [])
                if (transition.Fidelity > worst) worst = transition.Fidelity;
        return worst;
    }

    /// <summary>Everything deliberately left unmodeled, for the coverage readout.
    /// Scans ability steps as well as abilities themselves: a mostly-faithful
    /// ability with one unmodeled beat inside it (a summon wave that is really a
    /// state machine, say) is still a hole, and hiding it behind the ability's
    /// overall fidelity is exactly the confident lie this registry exists to
    /// prevent.</summary>
    public IEnumerable<string> Holes()
    {
        foreach (EncounterAbility ability in Abilities)
        {
            if (ability.Fidelity == EncounterFidelity.UnknownUnmodeled)
                yield return $"ability '{ability.Name}': {ability.Note ?? "not modeled"}";
            foreach (EncounterStep step in ability.Steps ?? [])
                if (step.Kind == EncounterStepKind.Unmodeled)
                    yield return $"ability '{ability.Name}' step: {step.Note ?? "not modeled"}";
        }
        foreach (EncounterPhase phase in Phases)
        {
            foreach (EncounterStep step in phase.OnEnter ?? [])
                if (step.Kind == EncounterStepKind.Unmodeled)
                    yield return $"phase '{phase.Name}' enter: {step.Note ?? "not modeled"}";
            foreach (EncounterTransition transition in phase.Transitions ?? [])
            {
                if (transition.Fidelity == EncounterFidelity.UnknownUnmodeled)
                    yield return $"transition '{phase.Key}'→'{transition.ToPhase}': " +
                                 (transition.Note ?? "not modeled");
                foreach (EncounterStep step in transition.Steps ?? [])
                    if (step.Kind == EncounterStepKind.Unmodeled)
                        yield return $"transition '{phase.Key}'→'{transition.ToPhase}': " +
                                     (step.Note ?? "not modeled");
            }
        }
    }
}

public static class EncounterSchema
{
    /// <summary>Bump when a shape change would make an older document parse wrong.
    /// Documents carry this so a stale authored file fails loudly, not subtly.</summary>
    public const int Version = 1;

    public static string Describe(EncounterFidelity fidelity) => fidelity switch
    {
        EncounterFidelity.ExactDb => "exact-db",
        EncounterFidelity.DeclaredCppManifest => "declared-cpp-manifest",
        EncounterFidelity.DerivedDbc => "derived-dbc",
        EncounterFidelity.Heuristic => "heuristic",
        _ => "unknown-unmodeled",
    };

    public static string Describe(EncounterCoverage coverage) =>
        coverage == EncounterCoverage.None ? "none" : coverage.ToString().Replace(", ", "+");
}

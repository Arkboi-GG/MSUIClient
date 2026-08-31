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
// Field names and the fidelity registry form the shared contract for encounter
// authoring, simulation, and coverage reporting.
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
    string? Note = null,
    /// <summary>For Summon steps: the summoned creature's display, so adds render
    /// as models rather than unexplained circles.</summary>
    uint DisplayId = 0);

/// <summary>What a friendly body is FOR. Jobs drive the playbook — what each role
/// does when a phase turns — and the melee-reach dps gate. Damage itself stays an
/// owner-set input; a job never invents numbers.</summary>
public enum RaidJob { None, Tank, Healer, Melee, Ranged }

/// <summary>Which side of the encounter a body works. A positioning script is
/// authored per role AND per side, so "left ranged" and "right ranged" are two
/// scripts a body picks between; Center is the stack-in-the-middle lane (tank
/// healer, the melee ball). The axis a Left/Right script mirrors across is the
/// boss's facing — see <see cref="RaidSideExtensions.Mirror"/>.</summary>
public enum RaidSide { None, Left, Center, Right }

public static class RaidSideExtensions
{
    /// <summary>The opposite side. Left⇄Right; Center and None are their own
    /// mirror (nothing to reflect). This is the ONLY place the mirror mapping
    /// lives, so "author left, mirror to right" and the geometric spot reflection
    /// agree by construction.</summary>
    public static RaidSide Mirror(this RaidSide side) => side switch
    {
        RaidSide.Left => RaidSide.Right,
        RaidSide.Right => RaidSide.Left,
        _ => side,
    };

    public static string Label(this RaidSide side) => side switch
    {
        RaidSide.Left => "left",
        RaidSide.Center => "center",
        RaidSide.Right => "right",
        _ => "unsided",
    };
}

/// <summary>When a movement order fires.</summary>
public enum MoveAnchor
{
    /// <summary>At an absolute sim time (the original verb).</summary>
    AtTime,
    /// <summary>When the body finishes the previous order in its list — the
    /// shift-click chain: "run A, then B", no times to author.</summary>
    AfterPrevious,
    /// <summary>The instant the fight enters <see cref="TimedMove.PhaseKey"/> —
    /// "when she lifts off, go here".</summary>
    OnPhaseEnter,
}

/// <summary>An order to a placed body. The default is the original verb — at TimeMs,
/// start running to Position — plus three refinements: an anchor (chains and
/// phase-entry orders), an arrival facing in radians (NaN keeps the facing the run
/// ended with; how a tank puts its back to the wall), and Teleport, the paused
/// what-if verb: "if he stood HERE at this exact moment", no travel time.</summary>
public readonly record struct TimedMove(
    int TimeMs,
    Vector3 Position,
    MoveAnchor Anchor = MoveAnchor.AtTime,
    string? PhaseKey = null,
    float ArrivalFacing = float.NaN,
    bool Teleport = false)
{
    public bool HasArrivalFacing => !float.IsNaN(ArrivalFacing);
}

/// <summary>What one job does when one phase turns: keep the owner's orders (Hold),
/// keep melee reach on the boss wherever she walks (ChaseBoss), or run to an
/// authored spot (MoveToSpot). Explicit timed orders always override — issuing one
/// takes the body off autopilot until the next phase turn re-applies its directive.</summary>
public enum RaidDirectiveKind { Hold, ChaseBoss, MoveToSpot }

public sealed record RaidPhaseDirective(
    string PhaseKey, RaidJob Job, RaidDirectiveKind Kind, Vector3 Spot = default);

/// <summary>What a creature does OUT of combat, from its spawn's DB row:
/// movement_type 0 = Stationary, 1 = Wander (wander_distance yards),
/// 2 = Waypoints (its creature_movement rows). Authored on the actor so the
/// pre-pull picture is game truth, not a guess — Onyxia's row (guid 47572)
/// says Stationary, and the Lab shows exactly that unless the owner explicitly
/// invents a roam as a what-if.</summary>
public enum IdleMovementKind { Stationary, Wander, Waypoints }

public readonly record struct IdleWaypoint(Vector3 Position, int WaitMs = 0);

public sealed record IdleMovementSpec(
    IdleMovementKind Kind,
    float WanderYards = 0f,
    IReadOnlyList<IdleWaypoint>? Points = null,
    string? Note = null);

/// <summary>A semantic subject inside a reusable combat plan. Only portable selectors
/// belong here. Named-character bindings are a separate roster/assignment concern, so
/// the same plan can move unchanged between dungeon and raid groups.</summary>
public enum CombatSubjectKind
{
    Self,
    RoleOrdinal,
    LowestHealthAlly,
}

public sealed record CombatSubject(
    CombatSubjectKind Kind,
    RaidJob Role = RaidJob.None,
    int Ordinal = 1)
{
    public static CombatSubject Self => new(CombatSubjectKind.Self);
    public static CombatSubject Tank(int ordinal = 1) =>
        new(CombatSubjectKind.RoleOrdinal, RaidJob.Tank, Math.Max(ordinal, 1));
    public static CombatSubject LowestHealth => new(CombatSubjectKind.LowestHealthAlly);
}

/// <summary>Strategic translation remains separate from spell choice so a combat rule can
/// never steal a waypoint, patrol, hold order, or directly controlled body.</summary>
public enum CombatMovementMode { Independent, HoldPosition, Follow }

public sealed record CombatMovementPlan(
    CombatMovementMode Mode = CombatMovementMode.Independent,
    CombatSubject? Anchor = null,
    float MinRangeYards = 8f,
    float MaxRangeYards = 18f,
    bool FacePrimaryEnemy = false);

/// <summary>Whether this character may create an engagement. None of these policies names
/// a boss: the current encounter can be a dungeon pull, raid encounter, or authored group.</summary>
public enum CombatEngagementMode { NeverInitiate, AssistAnchor, DefendGroup, Autonomous }

/// <summary>Portable hostile buckets exposed by the current Encounter Lab model.</summary>
public enum CombatEnemyKind { AnyAdd, CurrentEnemy, PrimaryEnemy }

public sealed record CombatEnemyPriority(CombatEnemyKind Kind, bool Enabled = true);

/// <summary>Ordered care assignment. The threshold prevents an always-applicable first row
/// from permanently hiding every fallback below it.</summary>
public sealed record CombatSupportPriority(
    CombatSubject Target,
    float OnlyWhenBelowHealthPercent = 100f,
    bool Enabled = true);

public enum CombatResponsibility
{
    Interrupt,
    DispelMagic,
    RemoveCurse,
    CleansePoison,
    CrowdControlAdds,
    Resurrect,
}

public enum CombatFallback { NoActionThisTick, AutoAttackCurrent, ClassDefaults }

/// <summary>One ordered rotation entry: a real 1.12 spell (by Spell.dbc id) the
/// character keeps using, priority-ordered against the other enabled entries.
/// The Lab executes these as COSMETIC casts — the puppet plays the true cast
/// visual on the real cast-time/cooldown cadence — while damage remains the
/// owner's DPS dial until the combat evaluator lands. Name is a display
/// fallback for when the spell catalog is unavailable.</summary>
public sealed record CombatAbilityIntent(
    uint SpellId,
    string Name = "",
    bool Enabled = true);

public sealed record CombatResourcePolicy(
    int ReservePercent = 20,
    int EmergencyHealthPercent = 25,
    bool SaveMajorCooldowns = true);

/// <summary>
/// One character's reusable combat doctrine. It deliberately contains no encounter key,
/// phase key, creature entry, "boss" selector, or party-size assumption. Encounter-local
/// choreography remains an overlay owned by the scenario/playbook; this object can move
/// unchanged between a five-player dungeon and a forty-player raid.
/// </summary>
public sealed record CombatPlan(
    string Name = "Custom plan",
    /// <summary>LEGACY spatial doctrine. Retained for JSON compatibility and for the
    /// sim's current follow model, but the authoring authority for "where I stand"
    /// is now the assigned <see cref="PositioningScript"/> (its <c>Movement</c>).
    /// A rotation is meant to be portable across every fight; spatial behaviour is
    /// not, so it belongs to the positioning slot.</summary>
    CombatMovementPlan? Movement = null,
    CombatEngagementMode Engagement = CombatEngagementMode.NeverInitiate,
    IReadOnlyList<CombatSupportPriority>? SupportPriorities = null,
    IReadOnlyList<CombatEnemyPriority>? EnemyPriorities = null,
    IReadOnlyList<CombatResponsibility>? Responsibilities = null,
    CombatResourcePolicy? Resources = null,
    CombatFallback Fallback = CombatFallback.ClassDefaults,
    /// <summary>The ordered ability intent from the precedence chain. Null keeps
    /// older plans loading unchanged (no rotation authored).</summary>
    IReadOnlyList<CombatAbilityIntent>? Rotation = null,
    /// <summary>1.12 class id (1 Warrior … 11 Druid) whose trained-at-60
    /// spellbook the rotation draws from. 0 = not chosen yet. Class identity
    /// rides the plan because the plan store is the per-character persistence.</summary>
    uint ClassId = 0,
    /// <summary>Stable library id. A rotation is a REUSABLE slot — authored once
    /// for a class/role, assigned to many bodies, cloned for the next fight — so it
    /// needs an identity independent of any one body's key. Empty on legacy plans;
    /// the store stamps one on first save.</summary>
    string Id = "");

/// <summary>Standing rules owned by one player-side body. The legacy facing bit remains
/// first and defaulted so existing positional constructors and encounter documents survive.
///
/// A body's behaviour is now TWO independent slots, each a reference into a library:
/// <see cref="RotationId"/> (what I press — portable across fights) and
/// <see cref="PositioningId"/> (where I stand — authored per role×side, per boss).
/// <see cref="Plan"/> is kept as the resolved/inline rotation the sim reads today; the
/// ids are the durable assignment, resolved back to library items on load.</summary>
public sealed record EncounterPlayerRules(
    /// <summary>Legacy spelling for face-primary-enemy. Retained for JSON compatibility.</summary>
    bool AlwaysFaceBoss = false,
    CombatPlan? Plan = null,
    /// <summary>Assigned rotation slot — the id of a <see cref="CombatPlan"/> in the
    /// rotation library. Null = no rotation assigned (or an inline-only legacy plan).</summary>
    string? RotationId = null,
    /// <summary>Assigned positioning slot — the id of a <see cref="PositioningScript"/>
    /// in the positioning library. Null = no positioning script assigned.</summary>
    string? PositioningId = null,
    /// <summary>Per-phase enemy-target overrides. Null/empty = the plan's default order
    /// (<see cref="CombatPlan.EnemyPriorities"/>) applies in every phase. This is
    /// ENCOUNTER-LOCAL: it names phase keys, so it lives on the body's scenario rules
    /// rather than the portable plan. Authored in the Game Plan tab, resolved against the
    /// sim's current phase.</summary>
    IReadOnlyList<PhaseTargetOverride>? PhaseTargets = null,
    /// <summary>Encounter ability keys this body actively stays out of. EXECUTED by the
    /// sim, not advisory: a telegraphed cast (cast time &gt; 0) triggers a run to the
    /// nearest safe point and a run back after impact; an instant cone becomes a standing
    /// constraint — the body continuously sidesteps out of the arc as the boss turns.
    /// Encounter-local (keys name this fight's abilities), authored in the Game Plan tab.
    /// Null/empty = the body stands its ground like it always did.</summary>
    IReadOnlyList<string>? AvoidAbilityKeys = null);

/// <summary>"In THIS phase, target this order instead of the plan default." The reason
/// per-phase targeting cannot live on <see cref="CombatPlan"/>: the plan is portable and
/// deliberately knows no phase keys, but "adds while she is untankable in the air" is a
/// fact about one fight. Kept as an ordered list of the same portable buckets the plan
/// uses, so it resolves through the identical selector.</summary>
public sealed record PhaseTargetOverride(
    string PhaseKey,
    IReadOnlyList<CombatEnemyPriority> Priorities);

/// <summary>At TimeMs, this body holds aggro. There is deliberately NO threat
/// model - the owner assigns aggro and swaps it, because "who is she facing"
/// is an input to the plan being tested, never a thing to guess at.</summary>
public readonly record struct TimedAggro(int TimeMs, string Key);

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
    uint MaxHealth = 1000,
    /// <summary>CreatureDisplayInfo id for the rendered puppet (0 = marks only).
    /// From creature_template display_id1 - the sim never reads it; it exists so
    /// a body can be a MODEL standing in the world, not a labelled circle.</summary>
    uint DisplayId = 0,
    float DisplayScale = 1f,
    /// <summary>Timed movement orders, ascending by time. The body runs to each
    /// in turn at run speed; the sim replays them identically every run, so a
    /// repositioning plan is testable against the same seeded fight.</summary>
    IReadOnlyList<TimedMove>? Moves = null,
    /// <summary>Owner-chosen damage per second. A body without a combat plan keeps
    /// the legacy primary-boss route; a planned body routes this same input to its
    /// resolved hostile intent. DPS is an INPUT to the plan, not a simulated outcome.</summary>
    float Dps = 0f,
    /// <summary>What the body is for. Feeds the playbook and the melee-reach dps
    /// gate; meaningless on the boss and on adds.</summary>
    RaidJob Job = RaidJob.None,
    /// <summary>Out-of-combat movement from the spawn's DB row. Null = the
    /// document does not say (derived encounters); the Lab may then invent a
    /// labeled what-if roam. Stationary is an ANSWER, not an absence.</summary>
    IdleMovementSpec? IdleMovement = null,
    /// <summary>Run speed in yd/s (creature_template speed_run × the 7 yd/s
    /// base). 0 = engine default 7. Onyxia: 1.28571 × 7 = 9.0.</summary>
    float RunSpeedYdPerSec = 0f,
    /// <summary>Walk speed in yd/s (speed_walk × the 2.5 yd/s base), used for
    /// idle movement. 0 = engine default 2.5.</summary>
    float WalkSpeedYdPerSec = 0f,
    /// <summary>creature_template.detection_range, yards. Display-only honesty
    /// beside the pull-ring slider (the real core adds a level-delta on top);
    /// 0 = unknown.</summary>
    float DetectionRangeYards = 0f,
    /// <summary>Per-player standing rules configured from the Player Setup modal.
    /// Null means all defaults and keeps older encounter documents compatible.</summary>
    EncounterPlayerRules? PlayerRules = null,
    /// <summary>Which side of the encounter this body works. Pairs with its assigned
    /// positioning script (which is itself role×side): the body's side selects which
    /// side's script applies and drives the mirror. Meaningless on the boss and adds.</summary>
    RaidSide Side = RaidSide.None,
    /// <summary>1.12 class id (1 Warrior … 11 Druid), 0 = unknown. Job says what the
    /// body is FOR; class says what it can CAST — the doctrine's class-gated rules
    /// (a Fear Ward chain wants priests, add-control wants mages) read this. Kept on
    /// the body, not the rotation, because a body has a class even with no plan.</summary>
    uint ClassId = 0);

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
                    yield return $"transition '{phase.Key}'->'{transition.ToPhase}': " +
                                 (transition.Note ?? "not modeled");
                foreach (EncounterStep step in transition.Steps ?? [])
                    if (step.Kind == EncounterStepKind.Unmodeled)
                        yield return $"transition '{phase.Key}'->'{transition.ToPhase}': " +
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

using System.Numerics;

namespace MSUIClient.World.Encounters;

// ─────────────────────────────────────────────────────────────────────────────
// The deterministic encounter simulator.
//
// Why this exists rather than "run the real thing and pause it": mangosd cannot
// rewind. Its world update is a real-time loop over mutable global state with no
// snapshot. Determinism and scrubbing are properties of an ARCHITECTURE, not of
// where the code runs — so the Lab reimplements the one shape that matters
// (a fixed-step UpdateAI over a small, snapshottable state) and gets pause,
// single-step, rewind, branching and what-if for free.
//
// The whole machine rests on two decisions:
//   1. FIXED STEP. Advance() takes no wall clock. Same inputs, same output.
//   2. SEEDED RNG. Every urand() the real script would roll becomes a draw from
//      a seeded stream, so a seed names a fight and you can replay it exactly.
//
// State is small enough to snapshot every single step (a boss is ~12 timers and
// a handful of actors), so rewind is a list index, not a re-simulation.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Deterministic xorshift128. Deliberately NOT System.Random: that class's
/// sequence is an implementation detail that has changed between .NET versions,
/// and a seed here is meant to name a reproducible fight forever.
/// </summary>
public sealed class SeededRng(uint seed)
{
    private uint _x = seed == 0 ? 0x9E3779B9u : seed;
    private uint _y = 0x243F6A88u, _z = 0xB7E15162u, _w = seed ^ 0xDEADBEEFu;

    public uint Next()
    {
        uint t = _x ^ (_x << 11);
        _x = _y; _y = _z; _z = _w;
        return _w = _w ^ (_w >> 19) ^ t ^ (t >> 8);
    }

    /// <summary>Inclusive on both ends, matching the core's urand(min, max).</summary>
    public int Range(int min, int max) =>
        max <= min ? min : min + (int)(Next() % (uint)(max - min + 1));

    public float Unit() => (Next() & 0xFFFFFF) / (float)0x1000000;
    public bool Chance(int percent) => percent >= 100 || (percent > 0 && Range(1, 100) <= percent);
    public SeededRng Clone() => new(0) { _x = _x, _y = _y, _z = _z, _w = _w };
}

public enum SimEventKind
{
    Aggro,
    PhaseEnter,
    CastStart,
    CastLand,
    Summon,
    Move,
    Say,
    Death,
    /// <summary>A placed body was inside a footprint when it landed. There is no
    /// damage model, so "hit" is the honest unit: who was standing in what, when.</summary>
    ActorHit,
    /// <summary>A beat the definition explicitly does not model. Shown in the
    /// timeline as a gap marker so the fight reads as incomplete, not as finished.</summary>
    Unmodeled,
}

/// <summary>One thing that happened at one instant. The timeline is a list of these;
/// the overlay draws the ones carrying a footprint.</summary>
public sealed record SimEvent(
    int TimeMs,
    SimEventKind Kind,
    string ActorKey,
    string Text,
    EncounterFidelity Fidelity = EncounterFidelity.ExactDb,
    string? AbilityKey = null,
    uint SpellId = 0,
    Footprint? Footprint = null,
    string? TargetKey = null,
    int LingerMs = 0)
{
    /// <summary>How long this event's footprint stays interesting on screen.</summary>
    public int VisibleUntilMs => TimeMs + Math.Max(LingerMs, 600);
}

public readonly record struct SimActorState(
    string Key, Vector3 Position, float Facing, int Health, bool Alive, bool Flying,
    int HitsTaken = 0,
    int ActiveOrderedMoveIndex = -1,
    bool[]? FiredOrderedMoves = null,
    string? CurrentFollowTargetKey = null,
    string? CurrentProtectTargetKey = null,
    string? CurrentEnemyTargetKey = null,
    Vector3? DodgeReturn = null,
    float DodgeReturnFacing = float.NaN);

/// <summary>A complete, restorable instant. Rewind is an index into a list of these.</summary>
public sealed record SimSnapshot(
    int Step,
    int TimeMs,
    string PhaseKey,
    IReadOnlyList<SimActorState> Actors,
    IReadOnlyList<SimEvent> Events);

public sealed class EncounterSimOptions
{
    /// <summary>Milliseconds per step. The core's own creature update lands around
    /// 100 ms; Onyxia's script comments say her timers move in 150 ms increments.
    /// Smaller is smoother and costs only memory.</summary>
    public int StepMs = 100;
    public uint Seed = 1;
    /// <summary>Fraction of max health the raid removes per second. There is no
    /// threat or damage model here — this is the dial that makes health-gated
    /// phases reachable, and it is labelled Heuristic wherever it is shown.</summary>
    public float RaidDpsFraction = 0.004f;
    /// <summary>When set, health is driven by the user instead of the dps dial.</summary>
    public float? PinnedHealthFraction;
    public int MaxDurationMs = 15 * 60 * 1000;

    /// <summary>Owner-assigned aggro timeline, ascending by time. The holder is
    /// who the boss faces and who "current victim" targeting resolves to. Empty
    /// falls back to nearest-friendly, the old tank stand-in.</summary>
    public IReadOnlyList<TimedAggro>? AggroPlan;

    /// <summary>The pull ring, in yards. With raid bodies placed, the boss is
    /// INERT until one walks inside this - authoring the pull is part of the
    /// plan. A scenario with no bodies engages at t=0 (the old behaviour).</summary>
    public float PullRangeYards = 30f;

    /// <summary>INVENT a pre-pull wander when the document's answer is "she
    /// stands" (or when it has no answer). An explicit what-if, never game
    /// truth: the boss actor's IdleMovement declaration — the DB row — always
    /// plays when it says Wander or Waypoints. Deterministic via its OWN seeded
    /// stream, so however long the roam runs, the post-pull ability rolls are
    /// untouched — same seed, same rotation, whenever the raid walks in.</summary>
    public bool InventPrePullRoam;

    /// <summary>Radius of the INVENTED wander, in yards. A document-declared
    /// wander uses its own exact wander_distance instead.</summary>
    public float RoamRadiusYards = 22f;

    /// <summary>Tank/melee body dps counts only while the body is within melee
    /// reach of a grounded boss. This is what makes an air phase honestly stall
    /// her health gates instead of melting her from the floor.</summary>
    public bool MeleeDpsNeedsReach = true;

    /// <summary>Damage each living add deals per second to its chosen victim while in
    /// melee reach. An owner dial in the same spirit as <see cref="RaidDpsFraction"/>:
    /// it makes adds CONSEQUENCES rather than scenery, without pretending to be a
    /// combat model. 0 turns add damage off (adds still chase and hold targets).</summary>
    public float AddDps = 15f;

    /// <summary>Healing per second a Healer-job body pours into its resolved protect
    /// target. This is what turns a plan's protect priorities from a read-out into
    /// throughput: the healer holds its charge above each row's threshold. Owner dial,
    /// labelled heuristic. 0 keeps healing observational (the old behaviour).</summary>
    public float HealerHps = 60f;

    /// <summary>Per (phase × job) standing orders, applied when a phase turns.
    /// The seam answer to "what does melee do when she lifts off" — a table the
    /// owner edits, never code. Explicit timed orders always override.
    ///
    /// LEGACY fallback: a body with an assigned <see cref="Positioning"/> script is
    /// driven by that instead. The playbook still governs bodies with no assigned
    /// positioning slot (and carries the default melee-chase seed).</summary>
    public IReadOnlyList<RaidPhaseDirective>? Playbook;

    /// <summary>Per-body positioning slot, keyed by actor key. The authored answer to
    /// "where does THIS body stand each phase" — role×side spots that override the
    /// job-wide playbook for the bodies that carry one. Absent keys fall through to
    /// <see cref="Playbook"/>.</summary>
    public IReadOnlyDictionary<string, PositioningScript>? Positioning;

    /// <summary>The raid-wide derived behaviour: formation stations, default dodging,
    /// spread-from-targeted, derived healing, bucket assignments. Null = no doctrine —
    /// every pre-doctrine behaviour unchanged, which is what existing scenarios and
    /// tests get unless they opt in.</summary>
    public RaidDoctrine? Doctrine;
}

/// <summary>A live actor inside the simulation. Mutable during a step, snapshotted after.</summary>
public sealed class SimActor(EncounterActorSpec spec)
{
    public readonly EncounterActorSpec Spec = spec;
    public string Key => Spec.Key;
    public Vector3 Position = spec.Position;
    public float Facing = spec.Facing;
    public int MaxHealth = (int)Math.Max(spec.MaxHealth, 1u);
    public int Health = (int)Math.Max(spec.MaxHealth, 1u);
    public bool Alive = true;
    public bool Flying;
    public Vector3? MoveTarget;
    /// <summary>yd/s. The template's real run speed when the spec carries one
    /// (speed_run × 7), the vanilla base otherwise. Choreography SetSpeed steps
    /// overwrite it mid-fight; Reset restores it.</summary>
    public float MoveSpeed = spec.RunSpeedYdPerSec > 0f ? spec.RunSpeedYdPerSec : 7f;
    public bool MoveIsFlight;
    /// <summary>The authored move currently being walked, or -1 for playbook/script/idle
    /// movement. Captured with the timeline so overlays can retire completed route legs
    /// correctly while scrubbing.</summary>
    public int ActiveOrderedMoveIndex = -1;
    /// <summary>Which authored move entries have dispatched. The array is copied into every
    /// snapshot; fired-but-inactive entries are complete, teleported, or superseded.</summary>
    public readonly bool[] FiredOrderedMoves = new bool[spec.Moves?.Count ?? 0];
    /// <summary>Facing to snap to when the current run arrives (an order's
    /// authored orientation — the tank's back to the wall). Null = keep the
    /// facing the run ended with.</summary>
    public float? PendingArrivalFacing;
    /// <summary>Playbook autopilot: keep melee reach on the boss wherever she
    /// walks. Set by a ChaseBoss directive; cleared by Hold, by MoveToSpot and
    /// by any explicit order the owner issues.</summary>
    public bool AutoChase;

    /// <summary>The reusable combat plan's resolved intent at this instant. Follow
    /// drives standing movement, enemy drives the body's owner-authored DPS input,
    /// and protect drives the healing dial. On ADD actors, CurrentEnemyTargetKey is
    /// the add's own chosen victim (the threat-lite selection). The Lab still does
    /// not invent spells, resources, responsibilities, engagement, or boss threat.</summary>
    public string? CurrentFollowTargetKey;
    public string? CurrentProtectTargetKey;
    public string? CurrentEnemyTargetKey;

    /// <summary>Where a hazard dodge should put the body back: the station (or the
    /// interrupted run's destination) it abandoned when it fled a telegraphed cast.
    /// Null = not dodging. The facing restores an interrupted arrival orientation
    /// (the tank's back to the wall) once the body walks home; NaN keeps whatever
    /// facing the return run ends with.</summary>
    public Vector3? DodgeReturn;
    public float DodgeReturnFacing = float.NaN;

    /// <summary>Footprint landings this body has been inside, cumulative. The
    /// positioning question the whole scenario exists to answer.</summary>
    public int HitsTaken;

    public float HealthFraction => MaxHealth <= 0 ? 0f : Health / (float)MaxHealth;
    public BodyCapsule Body => BodyCapsule.At(Position, Spec.BoundingRadius);
    public SimActorState Capture() => new(Key, Position, Facing, Health, Alive, Flying,
        HitsTaken, ActiveOrderedMoveIndex, FiredOrderedMoves.ToArray(),
        CurrentFollowTargetKey, CurrentProtectTargetKey, CurrentEnemyTargetKey,
        DodgeReturn, DodgeReturnFacing);

    public void Restore(in SimActorState state)
    {
        Position = state.Position; Facing = state.Facing;
        Health = state.Health; Alive = state.Alive; Flying = state.Flying;
        HitsTaken = state.HitsTaken;
        ActiveOrderedMoveIndex = state.ActiveOrderedMoveIndex;
        CurrentFollowTargetKey = state.CurrentFollowTargetKey;
        CurrentProtectTargetKey = state.CurrentProtectTargetKey;
        CurrentEnemyTargetKey = state.CurrentEnemyTargetKey;
        DodgeReturn = state.DodgeReturn;
        DodgeReturnFacing = state.DodgeReturnFacing;
        Array.Clear(FiredOrderedMoves);
        if (state.FiredOrderedMoves is { } fired)
            Array.Copy(fired, FiredOrderedMoves, Math.Min(fired.Length, FiredOrderedMoves.Length));

        // Restore the visible in-flight destination too. Runtime-authored boss/playbook moves
        // have index -1 and retain the simulator's existing snapshot limitations; ordered raid
        // routes are the state the Lab path overlay needs here.
        if (ActiveOrderedMoveIndex >= 0 && Spec.Moves is { } moves &&
            ActiveOrderedMoveIndex < moves.Count)
        {
            TimedMove move = moves[ActiveOrderedMoveIndex];
            MoveTarget = move.Position;
            PendingArrivalFacing = move.HasArrivalFacing ? move.ArrivalFacing : null;
        }
        else
        {
            MoveTarget = null;
            PendingArrivalFacing = null;
        }
    }
}

/// <summary>
/// The machine. Construct with a definition and a scenario, then call
/// <see cref="Advance"/> as many times as you like — it never reads a clock.
/// </summary>
public sealed class EncounterSim
{
    private sealed class PendingImpact
    {
        public int LandsAtMs;
        public EncounterAbility Ability = null!;
        public string CasterKey = "";
        public string? TargetKey;
        public Footprint Footprint = Footprint.Nothing;
    }

    private sealed class ScriptedSequence
    {
        public IReadOnlyList<EncounterStep> Steps = [];
        public int Index;
        public int WaitRemainingMs;
        public string? OnCompletePhase;
        public bool Done => Index >= Steps.Count;
    }

    private readonly EncounterDefinition _definition;
    private readonly IEncounterSpellFacts? _facts;
    private readonly List<SimActor> _actors = [];
    private readonly Dictionary<string, SimActor> _byKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _abilityTimers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _firedOnce = new(StringComparer.Ordinal);
    private readonly List<PendingImpact> _pending = [];
    private readonly List<SimSnapshot> _timeline = [];
    private readonly List<SimEvent> _allEvents = [];
    private readonly List<SimEvent> _stepEvents = [];
    private SeededRng _rng;
    private ScriptedSequence? _sequence;
    private int _summonSerial;

    // Ordered-move bookkeeping: which entries of each body's move list have fired.
    // Never snapshotted — the sim runs front-to-back exactly once per build, and
    // scrubbing is RestoreTo over the finished timeline, so dispatch state has no
    // second life to survive into.
    private readonly Dictionary<string, bool[]> _movesFired = new(StringComparer.Ordinal);

    // Doctrine-rule state, all forward-only like the ability timers (scrubbing
    // restores actor state; these belong to the single front-to-back run).
    /// <summary>"spellId:targetKey" → aura up until (ms). The maintain-aura law.</summary>
    private readonly Dictionary<string, int> _auraExpiry = new(StringComparer.Ordinal);
    /// <summary>"spellId:casterKey" → caster ready again at (ms).</summary>
    private readonly Dictionary<string, int> _auraCooldownReady = new(StringComparer.Ordinal);
    /// <summary>Adds slowed THIS step by an add-control channel, key → speed factor.</summary>
    private readonly Dictionary<string, float> _slowedAdds = new(StringComparer.Ordinal);
    /// <summary>Casters whose DPS is an AoE over covered adds THIS step.</summary>
    private readonly List<(SimActor Caster, List<SimActor> Covered)> _aoeCasters = [];
    /// <summary>Threat-lite: who holds her while no owner aggro entry governs.</summary>
    private string? _threatLiteHolder;

    // Pre-pull roam. Its OWN rng stream: however many draws the wander takes
    // before the pull, the fight stream's state at engage is identical — same
    // seed, same rotation, regardless of when the raid walks in.
    private SeededRng _roamRng = new(1);
    private Vector3? _roamTarget;
    private int _roamPauseUntilMs;
    private int _roamWaypointIndex;
    private Vector3 _bossSpawn;
    private const float DefaultWalkSpeed = 2.5f;   // yd/s; the 1.0 walk multiplier

    /// <summary>Edge-tracker so the chase emits one Move event per leg, not one
    /// per 100 ms step.</summary>
    private bool _bossChasing;

    public EncounterSimOptions Options { get; }
    public string PhaseKey { get; private set; }
    public int TimeMs { get; private set; }
    public int Step { get; private set; }
    public bool Finished { get; private set; }
    public IReadOnlyList<SimActor> Actors => _actors;
    public IReadOnlyList<SimSnapshot> Timeline => _timeline;
    public IReadOnlyList<SimEvent> Events => _allEvents;
    public EncounterDefinition Definition => _definition;
    public SimActor? Boss => _actors.FirstOrDefault(a => a.Spec.Role == EncounterActorRole.Boss);

    public EncounterSim(
        EncounterDefinition definition,
        IEnumerable<EncounterActorSpec> scenario,
        EncounterSimOptions? options = null,
        IEncounterSpellFacts? facts = null)
    {
        _definition = definition;
        _facts = facts;
        Options = options ?? new EncounterSimOptions();
        _rng = new SeededRng(Options.Seed);
        PhaseKey = definition.FirstPhase?.Key ?? "default";

        foreach (EncounterActorSpec spec in scenario)
        {
            var actor = new SimActor(spec);
            _actors.Add(actor);
            _byKey[spec.Key] = actor;
        }
        Reset();
    }

    /// <summary>Rewind to t=0 and re-arm every timer from the seed. Same seed ⇒ the
    /// same fight, every time.</summary>
    public void Reset()
    {
        TimeMs = 0; Step = 0; Finished = false;
        // Aggro at the pull: the latest plan entry stamped at or before zero.
        // A plan whose first swap is mid-fight starts on the fallback law.
        _aggroKey = Options.AggroPlan?.Where(a => a.TimeMs <= 0)
            .Select(a => a.Key).LastOrDefault();
        _rng = new SeededRng(Options.Seed);
        _timeline.Clear(); _allEvents.Clear(); _stepEvents.Clear();
        _pending.Clear(); _firedOnce.Clear(); _abilityTimers.Clear();
        _sequence = null; _summonSerial = 0;

        _movesFired.Clear();
        _auraExpiry.Clear();
        _auraCooldownReady.Clear();
        _slowedAdds.Clear();
        _aoeCasters.Clear();
        _threatLiteHolder = null;
        _roamRng = new SeededRng(Options.Seed * 2654435761u ^ 0x5EEDF00Du);
        _roamTarget = null;
        _roamPauseUntilMs = 2000;   // she stands a beat at spawn, then starts her round
        _roamWaypointIndex = 0;
        _bossChasing = false;

        foreach (SimActor actor in _actors)
        {
            actor.Position = actor.Spec.Position;
            actor.Facing = actor.Spec.Facing;
            actor.Health = actor.MaxHealth;
            actor.Alive = true;
            actor.Flying = false;
            actor.MoveTarget = null;
            actor.ActiveOrderedMoveIndex = -1;
            Array.Clear(actor.FiredOrderedMoves);
            actor.MoveSpeed = actor.Spec.RunSpeedYdPerSec > 0f ? actor.Spec.RunSpeedYdPerSec : 7f;
            actor.PendingArrivalFacing = null;
            actor.AutoChase = false;
            actor.CurrentFollowTargetKey = null;
            actor.CurrentProtectTargetKey = null;
            actor.CurrentEnemyTargetKey = null;
            actor.DodgeReturn = null;
            actor.DodgeReturnFacing = float.NaN;
            actor.HitsTaken = 0;
            if (actor.Spec.Moves is { Count: > 0 } moves)
                _movesFired[actor.Key] = actor.FiredOrderedMoves;
        }
        _bossSpawn = Boss?.Spec.Position ?? Vector3.Zero;

        PhaseKey = _definition.FirstPhase?.Key ?? "default";
        foreach (EncounterAbility ability in _definition.Abilities)
            _abilityTimers[ability.Key] = RollInitial(ability);

        // The pull is authored, never assumed: she idles her room until a raid
        // body crosses the ring. With no bodies placed there is nothing to pull
        // her and the room stays a living idle scene — the old fight-at-t=0
        // shortcut for empty scenarios made a fixtureless boss run her whole
        // script against nobody the instant a document loaded, and died for it.
        Engaged = false;
        EngagedAtMs = -1;
        ResolveCombatPlanIntents();
        Emit(SimEventKind.Aggro, BossKey,
            $"awaiting pull - {_definition.Name} engages inside {Options.PullRangeYards:0} yd");
        CaptureSnapshot();
    }

    /// <summary>True from the pull onward. Timers, phases, damage and facing all
    /// belong to the engaged fight; before it she is scenery with an aggro ring.</summary>
    public bool Engaged { get; private set; }

    /// <summary>Sim time of the pull, -1 while it has not happened.</summary>
    public int EngagedAtMs { get; private set; } = -1;

    private void EngageNow()
    {
        if (Engaged) return;
        Engaged = true;
        EngagedAtMs = TimeMs;
        _roamTarget = null;   // the round ends where the fight begins
        Emit(SimEventKind.Aggro, BossKey, $"{_definition.Name} engaged");
        EnterPhase(PhaseKey, announce: true);
    }

    private void CheckPull(SimActor boss)
    {
        foreach (SimActor actor in _actors)
        {
            if (!actor.Alive || actor.Spec.Role != EncounterActorRole.Friendly) continue;
            if (EncounterGeometryLaw.GroundDistance(boss.Position, actor.Position) >
                Options.PullRangeYards) continue;
            EngageNow();
            return;
        }
    }

    private string BossKey => Boss?.Key ?? _actors.FirstOrDefault()?.Key ?? "boss";

    // ── the step ─────────────────────────────────────────────────────────────

    /// <summary>Advance exactly one fixed step. Takes no wall clock by design.</summary>
    public void Advance()
    {
        if (Finished) return;
        int dt = Math.Max(Options.StepMs, 1);
        TimeMs += dt;
        Step++;
        _stepEvents.Clear();

        SimActor? boss = Boss;
        if (boss is { Alive: true } && !Engaged)
        {
            // Pre-pull: the raid may be walked in; she roams her room and does
            // nothing else until the ring is crossed. The ring rides her LIVE
            // position, and the pull moment is part of the recorded fight.
            DispatchOrderedMoves(dt);
            ResolveCombatPlanIntents();
            AdvanceCombatPlanMovement(dt);
            AdvanceMovement(dt);
            RoamPrePull(boss, dt);
            CheckPull(boss);
        }
        else if (boss is { Alive: true })
        {
            // Damage consumes one coherent set of targets for the whole fixed
            // step. Anything killed below reroutes at the resolution after the
            // encounter step, ready for the next damage tick.
            ResolveCombatPlanIntents();
            ComputeAddControl();
            UpdateThreatLite(boss);
            AdvanceMaintainedAuras();
            ApplyHealthModel(boss, dt);
            AdvanceAddCombat(dt);
            ApplyHealing(dt);
            DispatchAggroPlan(dt);
            DispatchOrderedMoves(dt);
            // Dodges fire BEFORE ordinary movement resolves so a body that spots a
            // telegraph starts running this very step, not one step late.
            AdvanceHazardDodging(dt);
            AdvanceCombatPlanMovement(dt);
            AdvanceMovement(dt);
            AdvanceAutoChase(boss, dt);
            FaceAggroHolder(boss);
            ChaseAggroHolder(boss, dt);
            // Derived stations AFTER she turns and walks: the formation flows with
            // her live facing, so dragging the boss re-flows the whole raid.
            AdvanceFormation(boss, dt);
            // The standing constraint AFTER the formation walk: sliding out of an
            // avoided arc must react to the facing she will actually cast with.
            AdvanceConeSidestep(boss, dt);
            // Spread LAST so its displacement survives this tick: while a targeted
            // cast is in flight, neighbours step off the target and the formation
            // pull pauses for them.
            AdvanceSpreadFromTargeted(dt);

            // A running choreography owns the actor: the real scripts return early
            // from UpdateAI while m_bTransition is set, so ability timers must not
            // tick underneath it. Reproducing that is the difference between a
            // believable transition and a boss that cleaves mid-takeoff.
            if (_sequence is not null) AdvanceSequence(dt);
            else
            {
                CheckTransitions(boss);
                if (_sequence is null) AdvanceAbilities(boss, dt);
            }

            if (boss.Health <= 0 && boss.Alive)
            {
                boss.Alive = false;
                Emit(SimEventKind.Death, boss.Key, $"{boss.Spec.Name} dies");
                Finished = true;
            }
        }

        // Re-resolve after the encounter's own step so a summon or death is
        // reflected in this very snapshot. Movement used the same deterministic
        // intent earlier in the step; this second pass has no side effects.
        ResolveCombatPlanIntents();

        // Player standing rules are the last word on pose. Movement and choreography above
        // may move either endpoint during this step; applying here makes "always face boss"
        // true even while crossing through her or running away to the back wall.
        if (boss is { Alive: true }) ApplyFriendlyFacingRules(boss);

        ResolvePendingImpacts();
        if (TimeMs >= Options.MaxDurationMs) Finished = true;
        CaptureSnapshot();
    }

    public void AdvanceTo(int targetMs)
    {
        while (!Finished && TimeMs < targetMs) Advance();
    }

    private void ApplyHealthModel(SimActor boss, int dt)
    {
        bool bossPinned = false;
        if (Options.PinnedHealthFraction is { } pinned)
        {
            boss.Health = (int)Math.Clamp(pinned * boss.MaxHealth, 0f, boss.MaxHealth);
            bossPinned = true;
        }

        // The global fraction dial remains boss pressure. Per-body DPS is still
        // the same owner-authored input, but a combat plan can route it to an add.
        // Aggregate before applying so actor/scenario insertion order cannot make
        // one character retarget midway through a fixed step after another lands
        // the killing blow.
        Dictionary<SimActor, float> damagePerSecond = [];
        float globalBossDps = Options.RaidDpsFraction * boss.MaxHealth;
        if (!bossPinned && globalBossDps > 0f) damagePerSecond[boss] = globalBossDps;

        foreach (SimActor actor in _actors)
        {
            if (!actor.Alive || actor.Spec.Role != EncounterActorRole.Friendly ||
                actor.Spec.Dps <= 0f) continue;

            // An add-control channel diverts this body's whole output into the pack,
            // split evenly — the Blizzard trade: per-target damage for area control.
            if (_aoeCasters.FirstOrDefault(entry => ReferenceEquals(entry.Caster, actor))
                    is { Caster: not null } channel)
            {
                float perAdd = actor.Spec.Dps / channel.Covered.Count;
                foreach (SimActor add in channel.Covered)
                    damagePerSecond[add] = damagePerSecond.GetValueOrDefault(add) + perAdd;
                continue;
            }

            SimActor? target = DamageTarget(actor, boss);
            if (target is null || (bossPinned && ReferenceEquals(target, boss))) continue;

            // Tank/melee damage happens at the chosen hostile's feet. A flying
            // or unreachable add gates that body exactly as a boss already did.
            if (Options.MeleeDpsNeedsReach &&
                actor.Spec.Job is RaidJob.Tank or RaidJob.Melee &&
                (target.Flying ||
                 EncounterGeometryLaw.GroundDistance(actor.Position, target.Position) >
                 MeleeReach(target, actor) + 3f))
                continue;

            damagePerSecond[target] = damagePerSecond.GetValueOrDefault(target) + actor.Spec.Dps;
        }

        foreach ((SimActor target, float perSecond) in damagePerSecond
                     .OrderBy(pair => pair.Key.Key, StringComparer.Ordinal))
        {
            int damage = (int)MathF.Ceiling(perSecond * (dt / 1000f));
            if (damage <= 0) continue;
            target.Health = Math.Max(0, target.Health - damage);

            // Boss death retains its established end-of-step handling. Adds have
            // no AI loop here, so retire them immediately and emit exactly one
            // deterministic event; all characters reroute on the next resolver.
            if (target.Spec.Role == EncounterActorRole.Add &&
                target.Health <= 0 && target.Alive)
            {
                target.Alive = false;
                Emit(SimEventKind.Death, target.Key, $"{target.Spec.Name} dies");
            }
        }
    }

    /// <summary>Characters without a plan retain the original boss-only damage
    /// law. Planned characters consume their resolved enemy/fallback intent; null
    /// is a real NoAction/invalid-current result, not permission to hit the boss.</summary>
    private SimActor? DamageTarget(SimActor actor, SimActor legacyBoss)
    {
        if (actor.Spec.PlayerRules?.Plan is null) return legacyBoss;
        return actor.CurrentEnemyTargetKey is { } key &&
               _byKey.TryGetValue(key, out SimActor? target) && IsHostile(target)
            ? target : null;
    }

    // ── aggro (owner-assigned, never inferred) ───────────────────────────────

    private string? _aggroKey;

    private void DispatchAggroPlan(int dt)
    {
        if (Options.AggroPlan is not { Count: > 0 } plan) return;
        foreach (TimedAggro entry in plan)
        {
            if (entry.TimeMs >= TimeMs - dt && entry.TimeMs < TimeMs &&
                _byKey.TryGetValue(entry.Key, out SimActor? holder))
            {
                _aggroKey = entry.Key;
                Emit(SimEventKind.Aggro, BossKey,
                    $"aggro -> {holder.Spec.Name}", targetKey: entry.Key);
            }
        }
    }

    /// <summary>The body the boss is on: the OWNER-assigned holder always wins; with
    /// threat-lite opted in, the derived holder next; the nearest friendly last (the
    /// pre-aggro-plan tank stand-in, kept as the final fallback).</summary>
    private SimActor? AggroTarget(SimActor boss) =>
        _aggroKey is { } key && _byKey.TryGetValue(key, out SimActor? held) && held.Alive
            ? held
            : Options.Doctrine is { BossThreatLite: true } && _threatLiteHolder is { } derived &&
              _byKey.TryGetValue(derived, out SimActor? holder) && holder.Alive
                ? holder
                : NearestFriendly(boss);

    /// <summary>The opt-in threat-lite law, resolved ONCE per step so holder changes
    /// emit exactly one event. While no owner aggro entry governs: the first tank (by
    /// job ordinal) inside melee reach holds her; until one arrives, whoever already
    /// holds her keeps her — the emergency stand-in with his shield out — seeded from
    /// the nearest body. Cleared on every phase turn: the P2→P3 landing race.</summary>
    private void UpdateThreatLite(SimActor boss)
    {
        if (Options.Doctrine is not { BossThreatLite: true }) return;
        if (_aggroKey is { } owner && _byKey.TryGetValue(owner, out SimActor? held) &&
            held.Alive) return;   // the owner's word governs; nothing to derive

        SimActor? tank = _actors
            .Where(a => a.Alive && a.Spec.Role == EncounterActorRole.Friendly &&
                a.Spec.Job == RaidJob.Tank &&
                EncounterGeometryLaw.GroundDistance(a.Position, boss.Position) <=
                MeleeReach(boss, a) + 1.5f)
            .OrderBy(JobOrdinal).ThenBy(a => a.Key, StringComparer.Ordinal)
            .FirstOrDefault();
        string? next = tank?.Key
            ?? (_threatLiteHolder is { } current &&
                _byKey.TryGetValue(current, out SimActor? standing) && standing.Alive
                    ? current
                    : NearestFriendly(boss)?.Key);
        if (string.Equals(next, _threatLiteHolder, StringComparison.Ordinal)) return;
        _threatLiteHolder = next;
        if (next is not null && _byKey.TryGetValue(next, out SimActor? holder2))
            Emit(SimEventKind.Aggro, boss.Key,
                $"aggro (threat-lite) -> {holder2.Spec.Name}",
                EncounterFidelity.Heuristic, targetKey: next);
    }

    /// <summary>
    /// She faces her victim, continuously, while she is grounded and no scripted
    /// choreography owns her. This is what turns repositioning the aggro holder
    /// into a live change of every cone on the floor: move the tank, and Flame
    /// Breath's wedge follows him.
    /// </summary>
    private void FaceAggroHolder(SimActor boss)
    {
        if (boss.Flying || _sequence is not null) return;
        if (AggroTarget(boss) is not { } victim) return;
        Vector3 delta = victim.Position - boss.Position;
        if (new System.Numerics.Vector2(delta.X, delta.Y).LengthSquared() < 1e-4f) return;
        boss.Facing = MathF.Atan2(delta.Y, delta.X);
    }

    /// <summary>
    /// Hand each body the movement order that has just come due. AtTime orders are
    /// "at T, start running there" - AdvanceMovement below walks it at run speed,
    /// so a late order visibly costs the travel time, which is the honest physics
    /// of "can I get out of the breath from this spot". AfterPrevious orders chain
    /// on the previous entry's arrival; OnPhaseEnter orders fire from EnterPhase.
    /// </summary>
    private void DispatchOrderedMoves(int dt)
    {
        foreach (SimActor actor in _actors)
        {
            if (!actor.Alive || actor.Spec.Moves is not { Count: > 0 } moves ||
                !_movesFired.TryGetValue(actor.Key, out bool[]? fired)) continue;
            for (int i = 0; i < moves.Count; i++)
            {
                if (fired[i]) continue;
                TimedMove move = moves[i];
                bool due = move.Anchor switch
                {
                    // Half-open [step start, step end): an order at exactly t=0
                    // fires in the first step, and never twice at a boundary.
                    MoveAnchor.AtTime => move.TimeMs >= TimeMs - dt && move.TimeMs < TimeMs,
                    // Chained leg: previous entry has fired and the body is idle
                    // again. A leading chain leg (i == 0) starts at once.
                    MoveAnchor.AfterPrevious => (i == 0 || fired[i - 1]) &&
                                                actor.MoveTarget is null,
                    // Phase-entry orders fire from EnterPhase, not from time.
                    _ => false,
                };
                if (!due) continue;
                FireMove(actor, fired, i);
            }
        }
    }

    /// <summary>Execute one order. Firing an order retires every unfired chain leg
    /// before it in the list — a fresh order outranks the stale remainder of an
    /// older route, which is what lets a mid-fight reposition cancel the plan it
    /// replaces instead of the plan resuming underneath it.</summary>
    private void FireMove(SimActor actor, bool[] fired, int index)
    {
        TimedMove move = actor.Spec.Moves![index];
        for (int j = 0; j < index; j++)
            if (!fired[j] && actor.Spec.Moves![j].Anchor == MoveAnchor.AfterPrevious)
                fired[j] = true;
        fired[index] = true;

        if (move.Teleport)
        {
            // The paused what-if verb: no travel, no travel time. The body IS
            // here at this instant; everything downstream reflows around it.
            actor.Position = move.Position;
            actor.MoveTarget = null;
            actor.ActiveOrderedMoveIndex = -1;
            actor.PendingArrivalFacing = null;
            if (move.HasArrivalFacing) actor.Facing = move.ArrivalFacing;
            Emit(SimEventKind.Move, actor.Key,
                $"{actor.Spec.Name} repositioned (what-if)", targetKey: actor.Key);
        }
        else
        {
            actor.MoveTarget = move.Position;
            actor.ActiveOrderedMoveIndex = index;
            actor.PendingArrivalFacing = move.HasArrivalFacing ? move.ArrivalFacing : null;
            Emit(SimEventKind.Move, actor.Key,
                $"{actor.Spec.Name} ordered to move", targetKey: actor.Key);
        }

        // An explicit order takes the body off autopilot until the next phase
        // turn re-applies its directive: the owner's word beats the playbook's —
        // and beats a dodge in progress, so a fresh order never gets "corrected"
        // by a walk back to the abandoned station.
        if (actor.Spec.Role == EncounterActorRole.Friendly)
        {
            actor.AutoChase = false;
            actor.DodgeReturn = null;
            actor.DodgeReturnFacing = float.NaN;
        }
    }

    /// <summary>Fire every OnPhaseEnter order bound to the phase that just began.
    /// Called from EnterPhase so "when she lifts off, go here" happens on the
    /// beat, not a step late.</summary>
    private void FirePhaseMoves(string phaseKey)
    {
        foreach (SimActor actor in _actors)
        {
            if (!actor.Alive || actor.Spec.Moves is not { Count: > 0 } moves ||
                !_movesFired.TryGetValue(actor.Key, out bool[]? fired)) continue;
            for (int i = 0; i < moves.Count; i++)
            {
                if (fired[i] || moves[i].Anchor != MoveAnchor.OnPhaseEnter) continue;
                if (!string.Equals(moves[i].PhaseKey, phaseKey, StringComparison.Ordinal))
                    continue;
                FireMove(actor, fired, i);
            }
        }
    }

    /// <summary>Post-pull, grounded, unscripted: she walks to melee reach of her
    /// victim and parks there. This is what lets a tank DRAG her — run to the wall
    /// with aggro and she follows, and every cone she casts comes with her.</summary>
    private void ChaseAggroHolder(SimActor boss, int dt)
    {
        if (boss.Flying || _sequence is not null || boss.MoveTarget is not null)
        { _bossChasing = false; return; }
        if (AggroTarget(boss) is not { } victim) { _bossChasing = false; return; }

        float reach = MeleeReach(boss, victim);
        float distance = EncounterGeometryLaw.GroundDistance(boss.Position, victim.Position);
        if (distance <= reach) { _bossChasing = false; return; }

        if (!_bossChasing)
        {
            _bossChasing = true;
            Emit(SimEventKind.Move, boss.Key,
                $"{boss.Spec.Name} moves to {victim.Spec.Name}", targetKey: victim.Key);
        }
        float step = MathF.Min(boss.MoveSpeed * (dt / 1000f), distance - reach);
        Vector3 delta = victim.Position - boss.Position;
        boss.Position += delta / MathF.Max(delta.Length(), 1e-4f) * step;
    }

    /// <summary>Playbook autopilot for friendlies: any body flagged ChaseBoss keeps
    /// melee reach on a grounded boss, approaching along its own bearing so a pack
    /// of melee fans around her instead of stacking on one point.</summary>
    private void AdvanceAutoChase(SimActor boss, int dt)
    {
        if (!boss.Alive) return;
        foreach (SimActor actor in _actors)
        {
            if (!actor.Alive || !actor.AutoChase ||
                actor.Spec.Role != EncounterActorRole.Friendly) continue;
            if (actor.MoveTarget is not null) continue;   // an explicit run owns the body
            if (boss.Flying) continue;                    // nothing to stand next to

            float reach = MeleeReach(boss, actor);
            Vector3 delta = boss.Position - actor.Position;
            float distance = EncounterGeometryLaw.GroundDistance(actor.Position, boss.Position);
            if (distance <= reach)
            {
                // In reach: square up on her and swing.
                if (new System.Numerics.Vector2(delta.X, delta.Y).LengthSquared() > 1e-4f)
                    actor.Facing = MathF.Atan2(delta.Y, delta.X);
                continue;
            }
            float step = MathF.Min(actor.MoveSpeed * (dt / 1000f), distance - reach);
            actor.Position += delta / MathF.Max(delta.Length(), 1e-4f) * step;
            actor.Facing = MathF.Atan2(delta.Y, delta.X);
        }
    }

    /// <summary>Where "standing at her feet" begins: her combat reach plus the
    /// body's radius, floored so a zero-reach template still leaves a gap.</summary>
    private static float MeleeReach(SimActor boss, SimActor body) =>
        MathF.Max(boss.Spec.CombatReach, 2f) + body.Spec.BoundingRadius;

    // ── adds, healing, and hazard responses ──────────────────────────────────
    // The three behaviours that turn the raid from scenery into participants:
    // adds pick victims and swing, protect priorities become healing throughput,
    // and avoidance intent EXECUTES — a telegraphed cast is dodged, an instant
    // cone becomes a standing keep-clear constraint. Boss threat remains owner-
    // assigned by design; the threat-lite law below governs ADDS only and is
    // labelled heuristic wherever it surfaces.

    /// <summary>Threat-lite victim selection for one add, deterministic and stateless:
    /// (1) the nearest Tank-job body currently fighting adds — an add-duty tank calls
    /// the pack, which is the whole reason "Adds only" exists as a phase target;
    /// (2) else the hardest-hitting body attacking THIS add; (3) else the nearest
    /// friendly. Ties break on the stable actor key.</summary>
    private SimActor? AddPickVictim(SimActor add)
    {
        SimActor? addTank = _actors
            .Where(a => a.Alive && a.Spec.Role == EncounterActorRole.Friendly &&
                a.Spec.Job == RaidJob.Tank &&
                a.CurrentEnemyTargetKey is { } key &&
                _byKey.TryGetValue(key, out SimActor? enemy) &&
                enemy.Alive && enemy.Spec.Role == EncounterActorRole.Add)
            .OrderBy(a => EncounterGeometryLaw.GroundDistance(add.Position, a.Position))
            .ThenBy(a => a.Key, StringComparer.Ordinal)
            .FirstOrDefault();
        if (addTank is not null) return addTank;

        SimActor? attacker = _actors
            .Where(a => a.Alive && a.Spec.Role == EncounterActorRole.Friendly &&
                string.Equals(a.CurrentEnemyTargetKey, add.Key, StringComparison.Ordinal))
            .OrderByDescending(a => a.Spec.Dps)
            .ThenBy(a => a.Key, StringComparer.Ordinal)
            .FirstOrDefault();
        return attacker ?? NearestFriendly(add);
    }

    /// <summary>The maintain-aura chains ("keep Fear Ward on tank 1"): while a rule's
    /// aura is down on its tank, the first off-cooldown caster of the class refreshes
    /// it — one cast, never a double-up, and an honest gap when the whole chain is on
    /// cooldown. The sim tracks presence and emits the casts; what the aura absorbs
    /// is server truth and stays out of the model.</summary>
    private void AdvanceMaintainedAuras()
    {
        if (Options.Doctrine?.MaintainAuras is not { Count: > 0 } rules) return;
        foreach (MaintainAuraRule rule in rules)
        {
            SimActor? target = _actors
                .Where(a => a.Alive && a.Spec.Role == EncounterActorRole.Friendly &&
                    a.Spec.Job == RaidJob.Tank)
                .OrderBy(a => a.Key, StringComparer.Ordinal)
                .Skip(Math.Max(rule.TargetTankOrdinal, 1) - 1)
                .FirstOrDefault();
            if (target is null) continue;

            string auraKey = $"{rule.SpellId}:{target.Key}";
            if (_auraExpiry.TryGetValue(auraKey, out int upUntil) && upUntil > TimeMs)
                continue;   // one is already up: the chain's whole point is not doubling

            SimActor? caster = _actors
                .Where(a => a.Alive && a.Spec.Role == EncounterActorRole.Friendly &&
                    a.Spec.ClassId == rule.CasterClassId &&
                    (!_auraCooldownReady.TryGetValue($"{rule.SpellId}:{a.Key}", out int ready) ||
                     ready <= TimeMs))
                .OrderBy(a => a.Key, StringComparer.Ordinal)
                .FirstOrDefault();
            if (caster is null) continue;   // every caster on cooldown: an honest gap

            _auraExpiry[auraKey] = TimeMs + Math.Max(rule.DurationMs, 1);
            _auraCooldownReady[$"{rule.SpellId}:{caster.Key}"] = TimeMs + Math.Max(rule.CooldownMs, 0);
            Emit(SimEventKind.CastLand, caster.Key,
                $"{caster.Spec.Name} keeps {rule.Name} on {target.Spec.Name}",
                EncounterFidelity.Heuristic, spellId: rule.SpellId, targetKey: target.Key);
        }
    }

    /// <summary>True while the rule's aura is up on that body — the Lab UI's read-out.</summary>
    public bool MaintainedAuraActive(uint spellId, string targetKey) =>
        _auraExpiry.TryGetValue($"{spellId}:{targetKey}", out int upUntil) && upUntil > TimeMs;

    /// <summary>The add-control channels ("mages Blizzard the pack"), computed fresh
    /// each step: every caster of the class picks the densest reachable add cluster
    /// worth the channel; covered adds move at the rule's slow factor this step and
    /// share the caster's owner-authored DPS as an AoE instead of its single-target
    /// route. No cluster worth it → the caster keeps its normal routing.</summary>
    private void ComputeAddControl()
    {
        _slowedAdds.Clear();
        _aoeCasters.Clear();
        if (Options.Doctrine?.AddControl is not { Count: > 0 } jobs) return;
        List<SimActor> adds = _actors
            .Where(a => a.Alive && a.Spec.Role == EncounterActorRole.Add).ToList();
        if (adds.Count == 0) return;

        foreach (AddControlJob job in jobs)
        {
            foreach (SimActor caster in _actors
                         .Where(a => a.Alive && a.Spec.Role == EncounterActorRole.Friendly &&
                             a.Spec.ClassId == job.CasterClassId)
                         .OrderBy(a => a.Key, StringComparer.Ordinal))
            {
                SimActor? anchor = adds
                    .Where(a => EncounterGeometryLaw.GroundDistance(caster.Position, a.Position) <=
                        job.CastRangeYards)
                    .Select(a => (Add: a, Packed: adds.Count(b =>
                        EncounterGeometryLaw.GroundDistance(a.Position, b.Position) <=
                        job.RadiusYards)))
                    .Where(entry => entry.Packed >= Math.Max(job.MinAdds, 1))
                    .OrderByDescending(entry => entry.Packed)
                    .ThenBy(entry => entry.Add.Key, StringComparer.Ordinal)
                    .Select(entry => entry.Add)
                    .FirstOrDefault();
                if (anchor is null) continue;

                List<SimActor> covered = adds.Where(a =>
                    EncounterGeometryLaw.GroundDistance(anchor.Position, a.Position) <=
                    job.RadiusYards).ToList();
                _aoeCasters.Add((caster, covered));
                float factor = Math.Clamp(job.SlowFactor, 0.05f, 1f);
                foreach (SimActor add in covered)
                    _slowedAdds[add.Key] = MathF.Min(
                        _slowedAdds.GetValueOrDefault(add.Key, 1f), factor);
            }
        }
    }

    /// <summary>Adds acquire, chase, and swing. Damage is the <see cref="EncounterSimOptions.AddDps"/>
    /// dial per add — a consequence knob, not a combat model — pooled per victim and
    /// applied in key order so insertion order can never decide who dies first.</summary>
    private void AdvanceAddCombat(int dt)
    {
        Dictionary<SimActor, float> incoming = [];
        foreach (SimActor add in _actors)
        {
            if (!add.Alive || add.Spec.Role != EncounterActorRole.Add) continue;

            SimActor? victim = AddPickVictim(add);
            if (!string.Equals(add.CurrentEnemyTargetKey, victim?.Key, StringComparison.Ordinal))
            {
                add.CurrentEnemyTargetKey = victim?.Key;
                if (victim is not null)
                    Emit(SimEventKind.Aggro, add.Key,
                        $"{add.Spec.Name} turns on {victim.Spec.Name}",
                        EncounterFidelity.Heuristic, targetKey: victim.Key);
            }
            if (victim is null) continue;

            float reach = MeleeReach(add, victim);
            float distance = EncounterGeometryLaw.GroundDistance(add.Position, victim.Position);
            Vector3 delta = victim.Position - add.Position;
            if (distance > reach)
            {
                // An add caught in an add-control channel wades, not runs.
                float speed = add.MoveSpeed * _slowedAdds.GetValueOrDefault(add.Key, 1f);
                float step = MathF.Min(speed * (dt / 1000f), distance - reach);
                add.Position += delta / MathF.Max(delta.Length(), 1e-4f) * step;
            }
            if (new Vector2(delta.X, delta.Y).LengthSquared() > 1e-4f)
                add.Facing = MathF.Atan2(delta.Y, delta.X);
            if (distance <= reach + 0.5f && Options.AddDps > 0f)
                incoming[victim] = incoming.GetValueOrDefault(victim) + Options.AddDps;
        }

        foreach ((SimActor victim, float perSecond) in incoming
                     .OrderBy(pair => pair.Key.Key, StringComparer.Ordinal))
        {
            int damage = (int)MathF.Ceiling(perSecond * (dt / 1000f));
            if (damage <= 0 || !victim.Alive) continue;
            victim.Health = Math.Max(0, victim.Health - damage);
            if (victim.Health > 0) continue;
            victim.Alive = false;
            victim.MoveTarget = null;
            victim.AutoChase = false;
            victim.DodgeReturn = null;
            victim.DodgeReturnFacing = float.NaN;
            Emit(SimEventKind.Death, victim.Key, $"{victim.Spec.Name} dies",
                EncounterFidelity.Heuristic);
        }
    }

    /// <summary>Protect priorities as throughput: each Healer-job body pours the
    /// <see cref="EncounterSimOptions.HealerHps"/> dial into its resolved protect target.
    /// The resolver only names a target while it is below its row's threshold, so the
    /// healer holds its charge above the line rather than topping the raid.</summary>
    private void ApplyHealing(int dt)
    {
        if (Options.HealerHps <= 0f) return;
        foreach (SimActor healer in _actors)
        {
            if (!healer.Alive || healer.Spec.Role != EncounterActorRole.Friendly ||
                healer.Spec.Job != RaidJob.Healer ||
                healer.CurrentProtectTargetKey is not { } key ||
                !_byKey.TryGetValue(key, out SimActor? target) || !target.Alive) continue;
            int amount = (int)MathF.Ceiling(Options.HealerHps * (dt / 1000f));
            target.Health = Math.Min(target.MaxHealth, target.Health + amount);
        }
    }

    /// <summary>In-flight avoided casts whose footprint covers this body right now.
    /// Projectiles are excluded on purpose: a tracked missile re-resolves onto its
    /// target at impact, so "outrunning" one would be a lie the overlay contradicts.</summary>
    /// <summary>What one body stays out of. A body carrying its own AvoidAbilityKeys
    /// uses that list; otherwise the doctrine default ("everything of this kind")
    /// applies. All=false with null keys means the body avoids nothing.</summary>
    private readonly record struct AvoidSet(bool All, IReadOnlyList<string>? Keys)
    {
        public bool Avoids(string abilityKey) =>
            All || (Keys is { } keys && keys.Contains(abilityKey, StringComparer.Ordinal));
        public bool Any => All || Keys is { Count: > 0 };
    }

    private AvoidSet DodgeSet(SimActor actor) =>
        actor.Spec.PlayerRules?.AvoidAbilityKeys is { Count: > 0 } keys
            ? new AvoidSet(false, keys)
            : new AvoidSet(Options.Doctrine is { DodgeTelegraphs: true }, null);

    private AvoidSet ConeAvoidSet(SimActor actor) =>
        actor.Spec.PlayerRules?.AvoidAbilityKeys is { Count: > 0 } keys
            ? new AvoidSet(false, keys)
            : new AvoidSet(Options.Doctrine is { KeepClearOfCones: true }, null);

    private bool CoveredByAvoidedTelegraph(in BodyCapsule body, in AvoidSet avoided)
    {
        foreach (PendingImpact impact in _pending)
        {
            if (impact.Ability.Geometry.Kind == FootprintKind.Projectile) continue;
            if (!avoided.Avoids(impact.Ability.Key)) continue;
            if (EncounterGeometryLaw.Test(impact.Footprint, body).Covered) return true;
        }
        return false;
    }

    /// <summary>Execute avoidance for telegraphed casts: a body standing inside an
    /// avoided in-flight footprint runs to the nearest safe point, then walks back to
    /// the station (or interrupted destination) it abandoned once the sky is clear.
    /// Explicit ordered routes are never hijacked — the owner's word wins, and the
    /// ActorHit record keeps the cost honest.</summary>
    private void AdvanceHazardDodging(int dt)
    {
        _ = dt;   // decisions are per-instant; AdvanceMovement spends the time
        foreach (SimActor actor in _actors)
        {
            if (!actor.Alive || actor.Spec.Role != EncounterActorRole.Friendly) continue;
            AvoidSet avoided = DodgeSet(actor);
            if (!avoided.Any) continue;
            if (actor.ActiveOrderedMoveIndex >= 0) continue;   // owner's route owns the body

            bool covered = CoveredByAvoidedTelegraph(actor.Body, avoided);
            if (covered && (actor.DodgeReturn is null || actor.MoveTarget is null))
            {
                if (FindSafePoint(actor, avoided) is not { } safe) continue;
                if (actor.DodgeReturn is null)
                {
                    // Stash the station once: re-dodges from a compromised safe spot
                    // still walk home to the ORIGINAL post, not to a previous refuge.
                    actor.DodgeReturn = actor.MoveTarget ?? actor.Position;
                    actor.DodgeReturnFacing = actor.PendingArrivalFacing ?? actor.Facing;
                }
                actor.MoveTarget = safe;
                actor.PendingArrivalFacing = null;
                string what = _pending.FirstOrDefault(impact =>
                        avoided.Avoids(impact.Ability.Key) &&
                        EncounterGeometryLaw.Test(impact.Footprint, actor.Body).Covered)
                    ?.Ability.Name ?? "the telegraph";
                Emit(SimEventKind.Move, actor.Key,
                    $"{actor.Spec.Name} dodges {what}", EncounterFidelity.Heuristic,
                    targetKey: actor.Key);
            }
            else if (!covered && actor.DodgeReturn is { } home && actor.MoveTarget is null)
            {
                // The impact resolved and the body is parked out of harm's way. Walk
                // home — unless home itself sits under a fresh avoided telegraph.
                if (CoveredByAvoidedTelegraph(
                        BodyCapsule.At(home, actor.Spec.BoundingRadius), avoided))
                    continue;
                actor.MoveTarget = home;
                actor.PendingArrivalFacing =
                    float.IsNaN(actor.DodgeReturnFacing) ? null : actor.DodgeReturnFacing;
                actor.DodgeReturn = null;
                actor.DodgeReturnFacing = float.NaN;
                Emit(SimEventKind.Move, actor.Key,
                    $"{actor.Spec.Name} returns to station", EncounterFidelity.Heuristic,
                    targetKey: actor.Key);
            }
        }
    }

    /// <summary>The nearest point clear of every avoided in-flight footprint, found by
    /// deterministic sampling: bearings fanning out from "directly away from the hazard",
    /// at growing radii. The candidate is tested with an INFLATED body so the refuge has
    /// real margin, not a toe on the line. Null when nowhere within ~38 yd is safe.</summary>
    private Vector3? FindSafePoint(SimActor actor, in AvoidSet avoided)
    {
        List<Footprint> threats = [];
        foreach (PendingImpact impact in _pending)
            if (impact.Ability.Geometry.Kind != FootprintKind.Projectile &&
                avoided.Avoids(impact.Ability.Key))
                threats.Add(impact.Footprint);
        if (threats.Count == 0) return null;

        // Seed bearing: away from the nearest hazard sample of the first covering threat.
        Footprint covering = threats.FirstOrDefault(t =>
            EncounterGeometryLaw.Test(t, actor.Body).Covered) ?? threats[0];
        Vector3 hazard = covering.Kind == FootprintKind.PointChain && covering.Points is { Count: > 0 } pts
            ? pts.OrderBy(p => EncounterGeometryLaw.GroundDistance(p, actor.Position)).First()
            : covering.Origin;
        Vector2 away = new(actor.Position.X - hazard.X, actor.Position.Y - hazard.Y);
        float awayBearing = away.LengthSquared() > 1e-6f
            ? MathF.Atan2(away.Y, away.X) : actor.Facing;

        ReadOnlySpan<float> offsets =
            [0f, 0.6f, -0.6f, 1.2f, -1.2f, 1.8f, -1.8f, 2.4f, -2.4f, MathF.PI];
        ReadOnlySpan<float> radii = [6f, 10f, 14f, 20f, 28f, 38f];
        foreach (float radius in radii)
        {
            foreach (float offset in offsets)
            {
                float bearing = awayBearing + offset;
                Vector3 candidate = actor.Position + new Vector3(
                    MathF.Cos(bearing) * radius, MathF.Sin(bearing) * radius, 0f);
                BodyCapsule inflated = BodyCapsule.At(
                    candidate, actor.Spec.BoundingRadius + 2f);
                bool safe = true;
                foreach (Footprint threat in threats)
                    if (EncounterGeometryLaw.Test(threat, inflated).Covered) { safe = false; break; }
                if (safe) return candidate;
            }
        }
        return null;
    }

    /// <summary>The standing constraint for INSTANT cones: a body avoiding one slides
    /// around the caster, out of the arc, as she turns — melee avoiding both Flame
    /// Breath and Tail Sweep settle at her flanks, which is the classic positioning
    /// emerging from data rather than being scripted. The aggro holder is exempt from
    /// victim-anchored cones: she turns to face him, so that race is unwinnable and
    /// aiming her is HIS job.</summary>
    private void AdvanceConeSidestep(SimActor boss, int dt)
    {
        if (!boss.Alive || boss.Flying || _sequence is not null) return;
        SimActor? victim = AggroTarget(boss);

        foreach (SimActor actor in _actors)
        {
            if (!actor.Alive || actor.Spec.Role != EncounterActorRole.Friendly) continue;
            AvoidSet avoided = ConeAvoidSet(actor);
            if (!avoided.Any) continue;
            if (actor.MoveTarget is not null || actor.DodgeReturn is not null ||
                actor.ActiveOrderedMoveIndex >= 0) continue;
            // A formation-driven body is already walking to a station computed
            // OUTSIDE these arcs; shoving it mid-walk would just fight the walk.
            if (FormationGoverns(actor)) continue;

            foreach (EncounterAbility ability in _definition.AbilitiesIn(PhaseKey))
            {
                if (ability.Geometry.Kind != FootprintKind.Cone || ability.CastTimeMs > 0)
                    continue;
                if (!avoided.Avoids(ability.Key)) continue;
                bool victimAnchored = ability.Target.Kind is EncounterTargetKind.CurrentVictim
                    or EncounterTargetKind.NearestHostile;
                // The aggro holder is exempt from every arc that points where she
                // faces — victim cones by definition, and frontal self-cones because
                // she is always facing him. Aiming her is his job.
                if (ReferenceEquals(actor, victim) &&
                    (victimAnchored || !ability.Geometry.IsRearCone)) continue;

                Footprint footprint = EncounterGeometryLaw.Resolve(
                    ability, boss.Position, boss.Facing,
                    victimAnchored ? victim?.Position : null, _facts);
                if (!EncounterGeometryLaw.Test(footprint, actor.Body).Covered) continue;

                // Slide tangentially around her, rotating away from the arc's centre
                // line — the shortest angular exit, walked at real run speed.
                Vector2 radial = new(actor.Position.X - boss.Position.X,
                    actor.Position.Y - boss.Position.Y);
                float distance = MathF.Max(radial.Length(), 1.5f);
                float bearing = MathF.Atan2(radial.Y, radial.X);
                float centre = footprint.IsRearCone
                    ? footprint.Facing + MathF.PI : footprint.Facing;
                float signedOffset = EncounterGeometryLaw.NormalizeAngle(bearing - centre);
                float sign = signedOffset >= 0f ? 1f : -1f;
                float angularStep = actor.MoveSpeed * (dt / 1000f) / distance;
                float newBearing = bearing + sign * angularStep;
                actor.Position = boss.Position + new Vector3(
                    MathF.Cos(newBearing) * distance, MathF.Sin(newBearing) * distance,
                    actor.Position.Z - boss.Position.Z);
                Vector2 face = new(boss.Position.X - actor.Position.X,
                    boss.Position.Y - actor.Position.Y);
                if (face.LengthSquared() > 1e-4f)
                    actor.Facing = MathF.Atan2(face.Y, face.X);
                break;   // one constraint per step: sequential ticks converge to a flank
            }
        }
    }

    // ── the derived formation ────────────────────────────────────────────────
    // "Where does everyone stand" as a computation: the encounter's instant cone
    // arcs say where not to be, combat reach says how close melee must be, role
    // picks the ring, and the macro group (RaidSide, auto-split when unsided)
    // picks the flank. Authored things always outrank it — a body with explicit
    // orders, an assigned positioning script, a playbook directive for the phase,
    // or a follow plan is never touched. Doctrine fills silence.

    /// <summary>True when the derived formation is what drives this body right now.</summary>
    private bool FormationGoverns(SimActor actor)
    {
        if (Options.Doctrine is not { DeriveFormation: true }) return false;
        if (!Engaged || !actor.Alive ||
            actor.Spec.Role != EncounterActorRole.Friendly) return false;
        // The owner ROUTED this body (even if the route has finished): its spots are
        // authored truth — the waypointed tank stays on his back wall.
        if (actor.Spec.Moves is { Count: > 0 }) return false;
        if (actor.MoveTarget is not null || actor.DodgeReturn is not null ||
            actor.ActiveOrderedMoveIndex >= 0 || actor.AutoChase) return false;
        if (AssignedPositioning(actor) is not null) return false;
        if (ResolvePhaseDirective(actor, PhaseKey) is not null) return false;
        if (actor.Spec.PlayerRules?.Plan?.Movement is { Mode: CombatMovementMode.Follow })
            return false;
        if (UnderSpreadPressure(actor)) return false;   // spacing outranks the station
        return true;
    }

    /// <summary>A body's macro-group sign: +1 = the boss's left (Group 1), -1 = her
    /// right (Group 2). Explicit Left/Right wins; everyone else is split evenly and
    /// deterministically within their formation bucket, by stable key order.</summary>
    private static int SideSign(RaidSide side, int unsidedOrdinal) => side switch
    {
        RaidSide.Left => 1,
        RaidSide.Right => -1,
        _ => unsidedOrdinal % 2 == 0 ? 1 : -1,
    };

    private enum FormationBucket { MainTank, Melee, Healer, Ranged }

    private static FormationBucket BucketOf(SimActor actor, SimActor? mainTank) =>
        ReferenceEquals(actor, mainTank) ? FormationBucket.MainTank
        : actor.Spec.Job switch
        {
            RaidJob.Tank or RaidJob.Melee => FormationBucket.Melee,
            RaidJob.Healer => FormationBucket.Healer,
            _ => FormationBucket.Ranged,
        };

    /// <summary>Walk every doctrine-governed body toward its computed station. Runs
    /// every tick against her LIVE position and facing, so dragging or turning the
    /// boss re-flows the raid — melee sliding around her flanks, ranged wheeling
    /// behind them — with no one authoring a single point.</summary>
    private void AdvanceFormation(SimActor boss, int dt)
    {
        if (Options.Doctrine is not { DeriveFormation: true } || !boss.Alive) return;

        List<SimActor> governed = _actors.Where(FormationGoverns)
            .OrderBy(a => a.Key, StringComparer.Ordinal).ToList();
        if (governed.Count == 0) return;

        SimActor? mainTank = _actors
            .Where(a => a.Alive && a.Spec.Role == EncounterActorRole.Friendly &&
                a.Spec.Job == RaidJob.Tank)
            .OrderBy(a => a.Key, StringComparer.Ordinal).FirstOrDefault();

        RaidFormationLaw.PhaseArcs arcs =
            RaidFormationLaw.ComputeArcs(_definition, PhaseKey, _facts);
        (float from, float to) = arcs.SafeBand();

        // Slots: per bucket, unsided bodies alternate sides; per side, bodies fan
        // by their position in stable key order.
        Dictionary<string, (int Side, int Index, int Count)> slots = new(StringComparer.Ordinal);
        foreach (IGrouping<FormationBucket, SimActor> bucket in
                 governed.GroupBy(a => BucketOf(a, mainTank)))
        {
            int unsided = 0;
            List<(SimActor Actor, int Side)> sided = [];
            foreach (SimActor actor in bucket)
                sided.Add((actor, actor.Spec.Side is RaidSide.Left or RaidSide.Right
                    ? SideSign(actor.Spec.Side, 0)
                    : SideSign(RaidSide.None, unsided++)));
            foreach (IGrouping<int, (SimActor Actor, int Side)> flank in
                     sided.GroupBy(entry => entry.Side))
            {
                int count = flank.Count(), index = 0;
                foreach ((SimActor actor, int side) in flank)
                    slots[actor.Key] = (side, index++, count);
            }
        }

        for (int i = 0; i < governed.Count; i++)
        {
            SimActor actor = governed[i];
            (int side, int index, int count) = slots[actor.Key];
            float reach = MeleeReach(boss, actor);
            Vector3 station;
            if (boss.Flying)
            {
                // Nothing to flank in the air: the raid spreads on a ring, which is
                // also what keeps a targeted cast from clipping neighbours.
                station = RaidFormationLaw.AirStation(
                    boss.Position, i, governed.Count, 25f, actor.Position.Z);
            }
            else
            {
                station = BucketOf(actor, mainTank) switch
                {
                    FormationBucket.MainTank => RaidFormationLaw.Station(
                        boss.Position, boss.Facing, 0f, 1, reach - 0.3f, actor.Position.Z),
                    FormationBucket.Melee => RaidFormationLaw.Station(
                        boss.Position, boss.Facing,
                        RaidFormationLaw.SlotAngle(from, to, index, count), side,
                        reach - 0.3f, actor.Position.Z),
                    FormationBucket.Healer => RaidFormationLaw.Station(
                        boss.Position, boss.Facing,
                        RaidFormationLaw.SlotAngle(from, to, index, count), side,
                        RaidFormationLaw.HealerRadius(arcs, reach), actor.Position.Z),
                    _ => RaidFormationLaw.Station(
                        boss.Position, boss.Facing,
                        RaidFormationLaw.SlotAngle(from, to, index, count), side,
                        RaidFormationLaw.RangedRadius(arcs, reach), actor.Position.Z),
                };
            }

            Vector3 delta = station - actor.Position;
            float distance = new Vector2(delta.X, delta.Y).Length();
            if (distance > 0.75f)
            {
                float step = MathF.Min(actor.MoveSpeed * (dt / 1000f), distance);
                actor.Position += delta / MathF.Max(delta.Length(), 1e-4f) * step;
                actor.Facing = MathF.Atan2(delta.Y, delta.X);
            }
            else
            {
                // Parked: square up on her (ApplyFriendlyFacingRules refines this for
                // bodies that carry the explicit face-boss flag).
                Vector2 face = new(boss.Position.X - actor.Position.X,
                    boss.Position.Y - actor.Position.Y);
                if (face.LengthSquared() > 1e-4f)
                    actor.Facing = MathF.Atan2(face.Y, face.X);
            }
        }
    }

    /// <summary>An in-flight single-target cast on a FRIENDLY body: the target holds
    /// (soaking one hit is the mechanic), everyone near it is under pressure to move.</summary>
    private bool UnderSpreadPressure(SimActor actor)
    {
        if (Options.Doctrine is not { SpreadFromTargetedCasts: true } doctrine) return false;
        foreach (PendingImpact impact in _pending)
        {
            if (impact.TargetKey is not { } key ||
                !_byKey.TryGetValue(key, out SimActor? target) ||
                !target.Alive || target.Spec.Role != EncounterActorRole.Friendly) continue;
            if (ReferenceEquals(actor, target)) return true;   // the soaker holds still
            if (EncounterGeometryLaw.GroundDistance(actor.Position, target.Position) <
                doctrine.SpreadYards + 1f) return true;
        }
        return false;
    }

    /// <summary>While a targeted cast travels, neighbours step directly away from
    /// the marked body so the impact lands on one raider instead of a clump — the
    /// P2 fireball law. The target itself never moves here: it is about to be
    /// clowned, and running just brings the clowning to friends.</summary>
    private void AdvanceSpreadFromTargeted(int dt)
    {
        if (Options.Doctrine is not { SpreadFromTargetedCasts: true } doctrine) return;
        foreach (PendingImpact impact in _pending)
        {
            if (impact.TargetKey is not { } key ||
                !_byKey.TryGetValue(key, out SimActor? target) ||
                !target.Alive || target.Spec.Role != EncounterActorRole.Friendly) continue;
            foreach (SimActor actor in _actors)
            {
                if (!actor.Alive || actor.Spec.Role != EncounterActorRole.Friendly ||
                    ReferenceEquals(actor, target)) continue;
                if (actor.MoveTarget is not null || actor.DodgeReturn is not null ||
                    actor.ActiveOrderedMoveIndex >= 0) continue;
                float distance = EncounterGeometryLaw.GroundDistance(
                    actor.Position, target.Position);
                if (distance >= doctrine.SpreadYards) continue;

                Vector2 away = new(actor.Position.X - target.Position.X,
                    actor.Position.Y - target.Position.Y);
                float bearing = away.LengthSquared() > 1e-6f
                    ? MathF.Atan2(away.Y, away.X) : actor.Facing + MathF.PI;
                float step = MathF.Min(actor.MoveSpeed * (dt / 1000f),
                    doctrine.SpreadYards - distance + 0.5f);
                actor.Position += new Vector3(
                    MathF.Cos(bearing) * step, MathF.Sin(bearing) * step, 0f);
                actor.Facing = bearing;
            }
        }
    }

    /// <summary>Derived healing assignments for healers with no authored protect
    /// rows: the first healers cover the tanks one-to-one, the rest watch their
    /// macro group's lowest health. Runs after plan resolution, so an authored
    /// SupportPriorities list always wins simply by already having resolved.</summary>
    private void ApplyDoctrineProtect()
    {
        if (Options.Doctrine is not { GroupHealing: true }) return;

        List<SimActor> tanks = _actors
            .Where(a => a.Alive && a.Spec.Role == EncounterActorRole.Friendly &&
                a.Spec.Job == RaidJob.Tank)
            .OrderBy(a => a.Key, StringComparer.Ordinal).ToList();
        int unsidedHealer = 0, healerIndex = 0;
        foreach (SimActor healer in _actors
                     .Where(a => a.Alive && a.Spec.Role == EncounterActorRole.Friendly &&
                         a.Spec.Job == RaidJob.Healer)
                     .OrderBy(a => a.Key, StringComparer.Ordinal))
        {
            int side = SideSign(healer.Spec.Side,
                healer.Spec.Side is RaidSide.Left or RaidSide.Right ? 0 : unsidedHealer++);
            if (healer.Spec.PlayerRules?.Plan?.SupportPriorities is { Count: > 0 })
            { healerIndex++; continue; }   // authored rows already resolved

            SimActor? charge;
            if (healerIndex < tanks.Count)
            {
                // Tank healer: watch your tank, top him before he is in danger.
                charge = tanks[healerIndex].HealthFraction < 0.95f
                    ? tanks[healerIndex] : null;
            }
            else
            {
                // Group healer: the lowest-health wounded body on YOUR flank.
                // Unsided bodies belong to everyone — with no split authored, every
                // group healer simply watches the raid's lowest health.
                charge = _actors
                    .Where(a => a.Alive && a.Spec.Role == EncounterActorRole.Friendly &&
                        a.HealthFraction < 0.9f)
                    .OrderBy(a => a.HealthFraction)
                    .ThenBy(a => a.Key, StringComparer.Ordinal)
                    .FirstOrDefault(a =>
                        a.Spec.Side is not (RaidSide.Left or RaidSide.Right) ||
                        SideSign(a.Spec.Side, 0) == side);
            }
            healer.CurrentProtectTargetKey = charge?.Key ?? healer.CurrentProtectTargetKey;
            healerIndex++;
        }
    }

    /// <summary>Pre-pull movement. The boss actor's IdleMovement declaration — the
    /// spawn's DB row — is the authority: Waypoints replays its creature_movement
    /// path, Wander uses its exact wander_distance, Stationary stands (an ANSWER,
    /// not an absence — Onyxia's row says exactly this). Only when the document
    /// says stationary/nothing may the owner invent a wander as a labeled what-if.
    /// Silent on the timeline — a roam is scenery, and the event list should read
    /// as "awaiting pull", not as forty walk lines.</summary>
    private void RoamPrePull(SimActor boss, int dt)
    {
        if (boss.MoveTarget is not null) return;
        IdleMovementSpec? declared = boss.Spec.IdleMovement;

        if (declared is { Kind: IdleMovementKind.Waypoints, Points.Count: > 0 })
        {
            WalkIdleWaypoints(boss, declared.Points, dt);
            return;
        }
        float radius =
            declared is { Kind: IdleMovementKind.Wander, WanderYards: > 0f } wander
                ? wander.WanderYards
                : Options.InventPrePullRoam ? Options.RoamRadiusYards : 0f;
        if (radius <= 0f) return;   // stationary — the DB's answer, or nothing invented

        if (_roamTarget is null)
        {
            if (TimeMs < _roamPauseUntilMs) return;
            float angle = _roamRng.Unit() * MathF.Tau;
            float distance = radius * (0.35f + 0.65f * _roamRng.Unit());
            _roamTarget = _bossSpawn +
                new Vector3(MathF.Cos(angle) * distance, MathF.Sin(angle) * distance, 0f);
        }
        if (WalkTowards(boss, _roamTarget.Value, WalkSpeed(boss), dt))
        {
            _roamTarget = null;
            _roamPauseUntilMs = TimeMs + _roamRng.Range(2500, 9000);
        }
    }

    /// <summary>Loop the declared patrol: walk each point, honour its wait, wrap.
    /// The replay a creature_movement dump deserves — exact points, exact pauses.</summary>
    private void WalkIdleWaypoints(SimActor boss, IReadOnlyList<IdleWaypoint> points, int dt)
    {
        if (TimeMs < _roamPauseUntilMs) return;
        IdleWaypoint waypoint = points[_roamWaypointIndex % points.Count];
        if (WalkTowards(boss, waypoint.Position, WalkSpeed(boss), dt))
        {
            _roamWaypointIndex = (_roamWaypointIndex + 1) % points.Count;
            _roamPauseUntilMs = TimeMs + Math.Max(waypoint.WaitMs, 0);
        }
    }

    /// <summary>One walking step toward a point; true on arrival.</summary>
    private static bool WalkTowards(SimActor actor, Vector3 target, float speed, int dt)
    {
        Vector3 delta = target - actor.Position;
        float distance = delta.Length();
        float step = speed * (dt / 1000f);
        if (distance <= step)
        {
            actor.Position = target;
            return true;
        }
        actor.Position += delta / distance * step;
        actor.Facing = MathF.Atan2(delta.Y, delta.X);
        return false;
    }

    private static float WalkSpeed(SimActor actor) =>
        actor.Spec.WalkSpeedYdPerSec > 0f ? actor.Spec.WalkSpeedYdPerSec : DefaultWalkSpeed;

    // ── reusable player combat plans ────────────────────────────────────────

    /// <summary>Resolve the plan's semantic selectors into observable actor keys.
    /// Support stays observational. Enemy intent routes only the body's existing
    /// Spec.Dps input; it does not invent spells, attacks, threat, or extra damage.</summary>
    private void ResolveCombatPlanIntents()
    {
        foreach (SimActor actor in _actors)
        {
            // Adds own their CurrentEnemyTargetKey (the threat-lite victim, managed by
            // AdvanceAddCombat) — plan resolution must not wipe it twice a step.
            if (actor.Spec.Role != EncounterActorRole.Friendly) continue;
            if (!actor.Alive || actor.Spec.PlayerRules?.Plan is not { } plan)
            {
                actor.CurrentFollowTargetKey = null;
                actor.CurrentProtectTargetKey = null;
                actor.CurrentEnemyTargetKey = null;
                continue;
            }

            CombatMovementPlan? movement = plan.Movement;
            SimActor? follow = movement is { Mode: CombatMovementMode.Follow, Anchor: { } anchor }
                ? ResolveCombatSubject(actor, anchor)
                : null;
            actor.CurrentFollowTargetKey = follow?.Key;
            actor.CurrentProtectTargetKey = ResolveProtectIntent(actor, plan.SupportPriorities)?.Key;
            actor.CurrentEnemyTargetKey = ResolveEnemyIntent(actor, plan)?.Key;
        }
        // Doctrine fills the silence: healers with no authored protect rows derive
        // tank/group charges. Runs after the loop so authored rows always win.
        ApplyDoctrineProtect();
    }

    /// <summary>Semantic ally selectors never depend on scenario insertion order.
    /// Role ordinals and equal-health ties are ordered by the stable actor key so
    /// the same document names the same body in every replay.</summary>
    private SimActor? ResolveCombatSubject(SimActor owner, CombatSubject subject)
    {
        switch (subject.Kind)
        {
            case CombatSubjectKind.Self:
                return owner.Alive ? owner : null;

            case CombatSubjectKind.RoleOrdinal:
                return _actors
                    .Where(candidate => candidate.Alive &&
                        candidate.Spec.Role == EncounterActorRole.Friendly &&
                        candidate.Spec.Job == subject.Role)
                    .OrderBy(candidate => candidate.Key, StringComparer.Ordinal)
                    .Skip(Math.Max(subject.Ordinal, 1) - 1)
                    .FirstOrDefault();

            case CombatSubjectKind.LowestHealthAlly:
                return _actors
                    .Where(candidate => candidate.Alive &&
                        candidate.Spec.Role == EncounterActorRole.Friendly)
                    .OrderBy(candidate => candidate.HealthFraction)
                    .ThenBy(candidate => candidate.Key, StringComparer.Ordinal)
                    .FirstOrDefault();

            default:
                return null;
        }
    }

    private SimActor? ResolveProtectIntent(
        SimActor owner,
        IReadOnlyList<CombatSupportPriority>? priorities)
    {
        if (priorities is null) return null;
        foreach (CombatSupportPriority priority in priorities)
        {
            if (!priority.Enabled) continue;
            SimActor? target = ResolveCombatSubject(owner, priority.Target);
            if (target is null) continue;
            float threshold = Math.Clamp(priority.OnlyWhenBelowHealthPercent, 0f, 100f);
            if (target.HealthFraction * 100f < threshold) return target;
        }
        return null;
    }

    private SimActor? ResolveEnemyIntent(SimActor owner, CombatPlan plan)
    {
        // A per-phase override, when the body has one for the CURRENT phase, replaces the
        // plan's default order — this is how "tank the boss in P1, the adds in P2" is said.
        IReadOnlyList<CombatEnemyPriority>? priorities =
            PhaseEnemyPriorities(owner) ?? plan.EnemyPriorities;
        if (priorities is { })
        {
            foreach (CombatEnemyPriority priority in priorities)
            {
                if (!priority.Enabled) continue;
                SimActor? target = priority.Kind switch
                {
                    CombatEnemyKind.AnyAdd => FirstAliveHostile(EncounterActorRole.Add),
                    CombatEnemyKind.CurrentEnemy => CurrentLegalEnemy(owner),
                    CombatEnemyKind.PrimaryEnemy => FirstAliveHostile(EncounterActorRole.Boss),
                    _ => null,
                };
                if (target is not null) return target;
            }
        }

        return plan.Fallback switch
        {
            CombatFallback.AutoAttackCurrent => CurrentLegalEnemy(owner),
            CombatFallback.ClassDefaults => FirstAliveHostile(EncounterActorRole.Boss),
            _ => null,
        };
    }

    /// <summary>The body's enemy-target order for the CURRENT phase: its own authored
    /// override first, else the doctrine's job-bucket assignment ("tanks 2+ on adds"),
    /// else null — the plan's default order.</summary>
    private IReadOnlyList<CombatEnemyPriority>? PhaseEnemyPriorities(SimActor owner)
    {
        if (owner.Spec.PlayerRules?.PhaseTargets is { } overrides)
            foreach (PhaseTargetOverride entry in overrides)
                if (string.Equals(entry.PhaseKey, PhaseKey, StringComparison.Ordinal) &&
                    entry.Priorities is { Count: > 0 })
                    return entry.Priorities;

        if (Options.Doctrine?.Assignments is { } assignments)
            foreach (PhaseJobAssignment assignment in assignments)
                if (string.Equals(assignment.PhaseKey, PhaseKey, StringComparison.Ordinal) &&
                    assignment.Job == owner.Spec.Job &&
                    JobOrdinal(owner) >= assignment.FromOrdinal)
                    return [new CombatEnemyPriority(assignment.Target)];
        return null;
    }

    /// <summary>1-based position within the body's job, by stable key order over ALL
    /// friendlies (dead included, so an ordinal never silently renumbers mid-fight).
    /// "Tank 2" is the same warrior at the pull and at the wipe.</summary>
    private int JobOrdinal(SimActor owner) => 1 + _actors.Count(a =>
        a.Spec.Role == EncounterActorRole.Friendly && a.Spec.Job == owner.Spec.Job &&
        string.CompareOrdinal(a.Key, owner.Key) < 0);

    private SimActor? CurrentLegalEnemy(SimActor owner) =>
        owner.CurrentEnemyTargetKey is { } currentKey &&
        _byKey.TryGetValue(currentKey, out SimActor? current) && IsHostile(current)
            ? current : null;

    private SimActor? FirstAliveHostile(EncounterActorRole role) => _actors
        .Where(candidate => candidate.Alive && candidate.Spec.Role == role)
        .OrderBy(candidate => candidate.Key, StringComparer.Ordinal)
        .FirstOrDefault();

    private static bool IsHostile(SimActor actor) =>
        actor.Alive && actor.Spec.Role is EncounterActorRole.Boss or EncounterActorRole.Add;

    /// <summary>Move a follower only when standing doctrine owns the body. An
    /// explicit run, an active chase, or any directive for this job in the current
    /// encounter phase is a higher-precedence order and remains untouched.</summary>
    private void AdvanceCombatPlanMovement(int dt)
    {
        if (dt <= 0) return;
        foreach (SimActor actor in _actors)
        {
            if (!actor.Alive || actor.Spec.Role != EncounterActorRole.Friendly ||
                actor.Spec.PlayerRules?.Plan?.Movement is not
                    { Mode: CombatMovementMode.Follow } movement ||
                actor.CurrentFollowTargetKey is not { } targetKey ||
                !_byKey.TryGetValue(targetKey, out SimActor? anchor) || !anchor.Alive ||
                ReferenceEquals(actor, anchor))
                continue;

            if (actor.MoveTarget is not null || actor.AutoChase || HasPhasePlaybookOrder(actor))
                continue;

            MaintainFollowRange(actor, anchor, movement, dt);
        }
    }

    private bool HasPhasePlaybookOrder(SimActor actor)
    {
        if (!Engaged) return false;
        // An assigned positioning slot is authoritative for its body: the playbook is
        // not consulted. A step exists only for Chase/Spot phases (Hold is the absence
        // of a step), and those already suppress follow via AutoChase/MoveTarget — so
        // this only needs to report whether the script speaks to the current phase.
        if (AssignedPositioning(actor) is { } script)
            return script.Step(PhaseKey) is not null;

        return Options.Playbook?.Any(directive =>
            directive.Job == actor.Spec.Job &&
            string.Equals(directive.PhaseKey, PhaseKey, StringComparison.Ordinal)) == true;
    }

    private static void MaintainFollowRange(
        SimActor actor, SimActor anchor, CombatMovementPlan movement, int dt)
    {
        float a = MathF.Max(movement.MinRangeYards, 0f);
        float b = MathF.Max(movement.MaxRangeYards, 0f);
        float minRange = MathF.Min(a, b);
        float maxRange = MathF.Max(a, b);

        Vector2 delta = new(anchor.Position.X - actor.Position.X,
            anchor.Position.Y - actor.Position.Y);
        float distance = delta.Length();
        float correction;
        Vector2 direction;
        if (distance > maxRange)
        {
            correction = distance - maxRange;
            direction = delta / MathF.Max(distance, 1e-5f);
        }
        else if (distance < minRange)
        {
            correction = minRange - distance;
            direction = distance > 1e-5f
                ? -delta / distance
                : StringComparer.Ordinal.Compare(actor.Key, anchor.Key) < 0
                    ? -Vector2.UnitX : Vector2.UnitX;
        }
        else return;

        float step = MathF.Min(actor.MoveSpeed * (dt / 1000f), correction);
        if (step <= 0f) return;
        actor.Position += new Vector3(direction.X * step, direction.Y * step, 0f);
        actor.Facing = MathF.Atan2(direction.Y, direction.X);
    }

    private void ApplyFriendlyFacingRules(SimActor boss)
    {
        foreach (SimActor actor in _actors)
        {
            EncounterPlayerRules? rules = actor.Spec.PlayerRules;
            bool facePrimary = rules?.Plan is { } plan
                ? plan.Movement?.FacePrimaryEnemy == true
                : rules?.AlwaysFaceBoss == true;
            if (!actor.Alive || actor.Spec.Role != EncounterActorRole.Friendly ||
                !facePrimary) continue;
            Vector2 delta = new(boss.Position.X - actor.Position.X,
                boss.Position.Y - actor.Position.Y);
            if (delta.LengthSquared() > 1e-5f)
                actor.Facing = MathF.Atan2(delta.Y, delta.X);
        }
    }

    private void AdvanceMovement(int dt)
    {
        foreach (SimActor actor in _actors)
        {
            if (actor.MoveTarget is not { } target || !actor.Alive) continue;
            Vector3 delta = target - actor.Position;
            float distance = delta.Length();
            float stepDistance = actor.MoveSpeed * (dt / 1000f);
            // A running body faces where it runs; the boss's facing stays owned
            // by her script (turns are choreography, not locomotion).
            if (actor.Spec.Role == EncounterActorRole.Friendly && distance > 1e-3f)
                actor.Facing = MathF.Atan2(delta.Y, delta.X);
            if (distance <= stepDistance || distance < 1e-3f)
            {
                actor.Position = target;
                actor.MoveTarget = null;
                actor.ActiveOrderedMoveIndex = -1;
                // The order's authored orientation: arrive, then PIVOT — the
                // tank puts its back to the wall the instant it stops.
                if (actor.PendingArrivalFacing is { } arrival)
                {
                    actor.Facing = arrival;
                    actor.PendingArrivalFacing = null;
                }
                // The MovementInform seam: arriving is a trigger the definition can
                // hang a transition or an ability on.
                FireMovementDone(actor);
            }
            else
            {
                actor.Position += delta / distance * stepDistance;
                actor.Facing = EncounterGeometryLaw.Facing(actor.Position, target);
            }
        }
    }

    private void FireMovementDone(SimActor actor)
    {
        if (actor.Spec.Role != EncounterActorRole.Boss) return;
        foreach (EncounterAbility ability in _definition.AbilitiesIn(PhaseKey))
            if (ability.Trigger.Kind == EncounterTriggerKind.OnMovementDone)
                _abilityTimers[ability.Key] = 0;
    }

    // ── phases ───────────────────────────────────────────────────────────────

    private void CheckTransitions(SimActor boss)
    {
        EncounterPhase? phase = _definition.Phase(PhaseKey);
        if (phase?.Transitions is not { Count: > 0 } transitions) return;

        foreach (EncounterTransition transition in transitions)
        {
            if (!TriggerFires(transition.Trigger, boss, $"transition:{PhaseKey}->{transition.ToPhase}"))
                continue;
            if (transition.Fidelity == EncounterFidelity.UnknownUnmodeled)
                Emit(SimEventKind.Unmodeled, boss.Key,
                    transition.Note ?? $"transition to {transition.ToPhase} is not modeled",
                    EncounterFidelity.UnknownUnmodeled);

            if (transition.Steps is { Count: > 0 } steps)
                _sequence = new ScriptedSequence { Steps = steps, OnCompletePhase = transition.ToPhase };
            else
                EnterPhase(transition.ToPhase, announce: true);
            return;
        }
    }

    private void EnterPhase(string key, bool announce)
    {
        PhaseKey = key;
        // A phase turn is the threat reset (Onyxia's landing wipes her table): the
        // race for her starts over, exactly what the threat-lite law needs cleared.
        _threatLiteHolder = null;
        EncounterPhase? phase = _definition.Phase(key);
        if (announce)
            Emit(SimEventKind.PhaseEnter, BossKey, $"phase: {phase?.Name ?? key}");
        if (Boss is { } boss && phase is not null) boss.Flying = phase.CasterFlying;

        // Re-arm every ability that belongs to the phase we just entered, so a
        // phase change reads as a fresh rotation rather than as leftovers firing.
        foreach (EncounterAbility ability in _definition.AbilitiesIn(key))
        {
            _firedOnce.Remove(ability.Key);
            if (ability.Trigger.Kind == EncounterTriggerKind.OnPhaseEnter) _abilityTimers[ability.Key] = 0;
            else if (ability.Trigger.Kind == EncounterTriggerKind.Timer)
                _abilityTimers[ability.Key] = RollInitial(ability);
        }

        if (phase?.OnEnter is { Count: > 0 } onEnter)
            _sequence = new ScriptedSequence { Steps = onEnter };

        // The raid answers the turn: standing orders per job, then any explicit
        // "on this phase, go here" orders. Only once the fight is real — the
        // initial Reset sets PhaseKey directly and never lands here.
        if (Engaged)
        {
            ApplyPlaybook(key);
            FirePhaseMoves(key);
        }
    }

    /// <summary>Apply the standing positioning order when a phase turns: each body's
    /// assigned positioning slot if it has one, otherwise the (phase × job) playbook.
    /// Spot directives fan bodies that resolve to the SAME point around it, so two
    /// group healers sharing one script — or six dps on one playbook spot — do not
    /// stand inside one another, while an individually-authored spot is left exact.</summary>
    private void ApplyPlaybook(string phaseKey)
    {
        Dictionary<(float, float), int> spotIndex = [];
        foreach (SimActor actor in _actors)
        {
            if (!actor.Alive || actor.Spec.Role != EncounterActorRole.Friendly) continue;
            if (ResolvePhaseDirective(actor, phaseKey) is not { } directive) continue;

            switch (directive.Kind)
            {
                case RaidDirectiveKind.Hold:
                    actor.AutoChase = false;
                    break;
                case RaidDirectiveKind.ChaseBoss:
                    actor.AutoChase = true;
                    break;
                case RaidDirectiveKind.MoveToSpot:
                    actor.AutoChase = false;
                    (float, float) cell = (MathF.Round(directive.Spot.X, 1),
                                           MathF.Round(directive.Spot.Y, 1));
                    int index = spotIndex.GetValueOrDefault(cell);
                    spotIndex[cell] = index + 1;
                    float angle = index * (MathF.Tau / 8f);
                    Vector3 offset = index == 0
                        ? Vector3.Zero
                        : new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0f) * 2.5f;
                    actor.MoveTarget = directive.Spot + offset;
                    actor.PendingArrivalFacing =
                        float.IsNaN(directive.Facing) ? null : directive.Facing;
                    Emit(SimEventKind.Move, actor.Key,
                        $"{actor.Spec.Name} falls back to the {phaseKey} spot",
                        targetKey: actor.Key);
                    break;
            }
        }
    }

    /// <summary>The phase directive that governs one body: its assigned positioning
    /// slot's step if it has one, else the job-wide playbook row. A body with a script
    /// but no step for this phase resolves to null — its movement doctrine (follow /
    /// hold) governs, which is exactly what an unlisted "Hold" phase means.</summary>
    private (RaidDirectiveKind Kind, Vector3 Spot, float Facing)? ResolvePhaseDirective(
        SimActor actor, string phaseKey)
    {
        if (AssignedPositioning(actor) is { } script)
            return script.Step(phaseKey) is { } step
                ? (step.Kind, step.Spot, step.ArrivalFacing)
                : null;

        foreach (RaidPhaseDirective candidate in Options.Playbook ?? [])
            if (candidate.Job == actor.Spec.Job &&
                string.Equals(candidate.PhaseKey, phaseKey, StringComparison.Ordinal))
                return (candidate.Kind, candidate.Spot, float.NaN);
        return null;
    }

    /// <summary>The positioning script assigned to a body, or null. Presence in the
    /// map is the assignment — a body with a script is governed by it, never the
    /// playbook, even for phases the script leaves on Hold.</summary>
    private PositioningScript? AssignedPositioning(SimActor actor) =>
        Options.Positioning?.GetValueOrDefault(actor.Key);

    // ── abilities ────────────────────────────────────────────────────────────

    private void AdvanceAbilities(SimActor boss, int dt)
    {
        foreach (EncounterAbility ability in _definition.AbilitiesIn(PhaseKey))
        {
            if (ability.Fidelity == EncounterFidelity.UnknownUnmodeled) continue;

            if (ability.Trigger.Kind == EncounterTriggerKind.Timer ||
                ability.Trigger.Kind == EncounterTriggerKind.OnPhaseEnter ||
                ability.Trigger.Kind == EncounterTriggerKind.OnMovementDone)
            {
                if (!_abilityTimers.TryGetValue(ability.Key, out int remaining)) continue;
                remaining -= dt;
                if (remaining > 0) { _abilityTimers[ability.Key] = remaining; continue; }
                _abilityTimers[ability.Key] = ability.Timing.Repeats
                    ? RollRepeat(ability)
                    : int.MaxValue / 4;
                TryCast(ability, boss);
            }
            else if (TriggerFires(ability.Trigger, boss, ability.Key))
            {
                TryCast(ability, boss);
            }
        }
    }

    private bool TriggerFires(in EncounterTriggerSpec trigger, SimActor boss, string onceKey)
    {
        switch (trigger.Kind)
        {
            case EncounterTriggerKind.HealthBelow:
                if (boss.HealthFraction >= trigger.Threshold) return false;
                break;
            case EncounterTriggerKind.HealthAbove:
                if (boss.HealthFraction <= trigger.Threshold) return false;
                break;
            case EncounterTriggerKind.ManaBelow:
                return false;   // no mana model; abilities gated on it stay silent
            case EncounterTriggerKind.TargetInRange:
                if (NearestFriendly(boss) is not { } near ||
                    EncounterGeometryLaw.GroundDistance(near.Position, boss.Position) > trigger.Threshold)
                    return false;
                break;
            case EncounterTriggerKind.OnDeath:
                if (boss.Alive) return false;
                break;
            case EncounterTriggerKind.OnAggro:
                break;
            default:
                return false;
        }
        // Threshold triggers are edges, not levels: fire once per phase entry.
        return _firedOnce.Add(onceKey);
    }

    private void TryCast(EncounterAbility ability, SimActor boss)
    {
        if (!_rng.Chance(ability.ChancePercent)) return;

        SimActor? target = SelectTarget(ability.Target, boss);
        Vector3? targetPosition = ability.Target.Kind switch
        {
            EncounterTargetKind.FixedPoint => ability.Target.Point,
            EncounterTargetKind.DatabaseLocation => null,   // the chain carries its own points
            EncounterTargetKind.Self => boss.Position,
            _ => target?.Position,
        };

        if (target is not null && ability.Geometry.Kind != FootprintKind.Cone)
            boss.Facing = EncounterGeometryLaw.Facing(boss.Position, target.Position);

        Footprint footprint = EncounterGeometryLaw.Resolve(
            ability, boss.Position, boss.Facing, targetPosition, _facts);

        float travel = targetPosition is { } destination
            ? EncounterGeometryLaw.GroundDistance(boss.Position, destination) : 0f;
        int delay = EncounterGeometryLaw.ImpactDelayMs(ability, travel, _facts);

        Emit(SimEventKind.CastStart, boss.Key,
            delay > 0 ? $"casts {ability.Name} ({delay} ms)" : $"casts {ability.Name}",
            ability.Fidelity, ability.Key, ability.SpellId,
            delay > 0 ? footprint : null, target?.Key);

        if (delay <= 0)
            LandCast(ability, boss.Key, target?.Key, footprint);
        else
            _pending.Add(new PendingImpact
            {
                LandsAtMs = TimeMs + delay,
                Ability = ability,
                CasterKey = boss.Key,
                TargetKey = target?.Key,
                Footprint = footprint,
            });

        if (ability.Steps is { Count: > 0 } steps)
            _sequence = new ScriptedSequence { Steps = steps };
    }

    private void ResolvePendingImpacts()
    {
        for (int i = _pending.Count - 1; i >= 0; i--)
        {
            PendingImpact impact = _pending[i];
            if (impact.LandsAtMs > TimeMs) continue;
            _pending.RemoveAt(i);

            // Re-resolve against the CURRENT world: a travelling effect lands where
            // the fight is now, not where it was at cast. This is the whole reason
            // the probe takes a trajectory rather than a point.
            Footprint footprint = impact.Footprint;
            if (impact.Ability.Geometry.Kind == FootprintKind.Projectile &&
                impact.TargetKey is { } key && _byKey.TryGetValue(key, out SimActor? target))
                footprint = footprint with { End = target.Position };

            LandCast(impact.Ability, impact.CasterKey, impact.TargetKey, footprint);
        }
    }

    private void LandCast(EncounterAbility ability, string casterKey, string? targetKey, Footprint footprint)
    {
        Emit(SimEventKind.CastLand, casterKey, $"{ability.Name} lands",
            ability.Fidelity, ability.Key, ability.SpellId, footprint, targetKey,
            Math.Max(ability.Geometry.DurationMs, 800));

        // Who was standing in it. Placed bodies steered the TARGETING above
        // (nearest = tank stand-in, random picks); without this test they were
        // never CONSEQUENCES - aimed at like players, hit like ghosts - and the
        // whole point of arranging a raid is to read what a position costs.
        foreach (SimActor actor in _actors)
        {
            if (!actor.Alive || actor.Spec.Role != EncounterActorRole.Friendly) continue;
            if (!EncounterGeometryLaw.Test(footprint, actor.Body).Covered) continue;
            actor.HitsTaken++;
            Emit(SimEventKind.ActorHit, actor.Key, $"{actor.Spec.Name} hit by {ability.Name}",
                ability.Fidelity, ability.Key, ability.SpellId, targetKey: actor.Key);
        }
    }

    private SimActor? SelectTarget(in EncounterTargetSpec spec, SimActor boss)
    {
        List<SimActor> friendlies = _actors
            .Where(a => a.Alive && a.Spec.Role == EncounterActorRole.Friendly).ToList();
        if (friendlies.Count == 0) return null;

        switch (spec.Kind)
        {
            case EncounterTargetKind.Self:
                return boss;
            case EncounterTargetKind.NearestHostile:
            case EncounterTargetKind.CurrentVictim:
                // The owner-assigned aggro holder; nearest-friendly only as the
                // fallback when no plan names one.
                return AggroTarget(boss);
            case EncounterTargetKind.RandomHostile:
                return friendlies[_rng.Range(0, friendlies.Count - 1)];
            case EncounterTargetKind.RandomHostileNotVictim:
            {
                SimActor? victim = AggroTarget(boss);
                List<SimActor> pool = friendlies.Where(a => a != victim).ToList();
                if (pool.Count == 0) pool = friendlies;
                return pool[_rng.Range(0, pool.Count - 1)];
            }
            case EncounterTargetKind.AllHostiles:
                return AggroTarget(boss);
            default:
                return null;
        }
    }

    private SimActor? NearestFriendly(SimActor from)
    {
        SimActor? best = null;
        float bestDistance = float.MaxValue;
        foreach (SimActor actor in _actors)
        {
            if (!actor.Alive || actor.Spec.Role != EncounterActorRole.Friendly) continue;
            float distance = EncounterGeometryLaw.GroundDistance(from.Position, actor.Position);
            if (distance >= bestDistance) continue;
            bestDistance = distance; best = actor;
        }
        return best;
    }

    // ── choreography ─────────────────────────────────────────────────────────

    private void AdvanceSequence(int dt)
    {
        ScriptedSequence sequence = _sequence!;
        if (sequence.WaitRemainingMs > 0)
        {
            sequence.WaitRemainingMs -= dt;
            if (sequence.WaitRemainingMs > 0) return;
        }

        while (!sequence.Done)
        {
            EncounterStep step = sequence.Steps[sequence.Index];
            SimActor? boss = Boss;
            if (boss is null) break;

            switch (step.Kind)
            {
                case EncounterStepKind.Wait:
                    sequence.Index++;
                    sequence.WaitRemainingMs = step.DurationMs;
                    if (step.DurationMs > 0) return;
                    continue;

                case EncounterStepKind.MoveTo:
                    boss.MoveTarget = step.Point;
                    boss.MoveIsFlight = step.Flag;
                    if (step.Value > 0f) boss.MoveSpeed = step.Value;
                    Emit(SimEventKind.Move, boss.Key,
                        $"moves to ({step.Point.X:0.#}, {step.Point.Y:0.#}, {step.Point.Z:0.#})");
                    sequence.Index++;
                    return;                       // movement resolves across later steps

                case EncounterStepKind.Cast:
                {
                    EncounterAbility? ability = _definition.Abilities
                        .FirstOrDefault(a => a.SpellId == step.SpellId)
                        ?? SyntheticAbility(step);
                    TryCast(ability, boss);
                    break;
                }

                case EncounterStepKind.SetFlying:
                    boss.Flying = step.Flag;
                    Emit(SimEventKind.Move, boss.Key, step.Flag ? "takes off" : "lands");
                    break;

                case EncounterStepKind.SetSpeed:
                    boss.MoveSpeed = MathF.Max(step.Value, 0.1f);
                    break;

                case EncounterStepKind.Say:
                    Emit(SimEventKind.Say, boss.Key, step.Note ?? "says something");
                    break;

                case EncounterStepKind.Summon:
                    SummonAdds(step);
                    break;

                case EncounterStepKind.DespawnSummons:
                    DespawnSummons();
                    break;

                case EncounterStepKind.SetPhase:
                    if (step.PhaseKey is { Length: > 0 } phaseKey)
                    {
                        sequence.Index++;
                        _sequence = null;
                        EnterPhase(phaseKey, announce: true);
                        return;
                    }
                    break;

                case EncounterStepKind.Unmodeled:
                    Emit(SimEventKind.Unmodeled, boss.Key,
                        step.Note ?? "unmodeled beat", EncounterFidelity.UnknownUnmodeled);
                    break;
            }
            sequence.Index++;
        }

        string? completePhase = sequence.OnCompletePhase;
        _sequence = null;
        if (completePhase is { Length: > 0 }) EnterPhase(completePhase, announce: true);
    }

    /// <summary>A Cast step naming a spell the definition never declared still has to
    /// draw something. It resolves from spell data alone and is labelled as such.</summary>
    private EncounterAbility SyntheticAbility(EncounterStep step) => new(
        Key: $"step-cast:{step.SpellId}",
        Name: _facts?.SpellName(step.SpellId) ?? $"spell {step.SpellId}",
        SpellId: step.SpellId,
        Trigger: EncounterTriggerSpec.Manual,
        Timing: EncounterTiming.Never,
        Target: EncounterTargetSpec.Victim,
        Geometry: new EncounterGeometrySpec(FootprintKind.Circle),
        Fidelity: EncounterFidelity.DerivedDbc);

    private void SummonAdds(EncounterStep step)
    {
        int count = Math.Max(step.Count, 1);
        for (int i = 0; i < count; i++)
        {
            string key = $"summon:{++_summonSerial}";
            var spec = new EncounterActorSpec(
                key, $"add {_summonSerial}", step.Entry, EncounterActorRole.Add,
                step.Point, 0f, 0.5f, 1.5f, 55, 500, DisplayId: step.DisplayId);
            var actor = new SimActor(spec);
            _actors.Add(actor);
            _byKey[key] = actor;
        }
        Emit(SimEventKind.Summon, BossKey, $"summons {count}x entry {step.Entry}");
    }

    /// <summary>Despawn is stronger than removing a renderable body from Actors:
    /// sticky CurrentEnemy resolution also consults the key index. Retire both
    /// views atomically so a vanished summon can never remain a legal target.</summary>
    private void DespawnSummons()
    {
        List<SimActor> despawned = _actors
            .Where(actor => actor.Spec.Role == EncounterActorRole.Add &&
                actor.Key.StartsWith("summon:", StringComparison.Ordinal))
            .ToList();
        foreach (SimActor actor in despawned)
        {
            actor.Alive = false;
            _byKey.Remove(actor.Key);
        }
        _actors.RemoveAll(despawned.Contains);
    }

    // ── timeline capture ─────────────────────────────────────────────────────

    private int RollInitial(EncounterAbility ability) =>
        ability.Trigger.Kind == EncounterTriggerKind.Timer
            ? _rng.Range(ability.Timing.InitialMinMs, Math.Max(ability.Timing.InitialMaxMs,
                ability.Timing.InitialMinMs))
            : int.MaxValue / 4;

    private int RollRepeat(EncounterAbility ability) =>
        _rng.Range(ability.Timing.RepeatMinMs,
            Math.Max(ability.Timing.RepeatMaxMs, ability.Timing.RepeatMinMs));

    private void Emit(SimEventKind kind, string actorKey, string text,
        EncounterFidelity fidelity = EncounterFidelity.ExactDb,
        string? abilityKey = null, uint spellId = 0, Footprint? footprint = null,
        string? targetKey = null, int lingerMs = 0)
    {
        var simEvent = new SimEvent(TimeMs, kind, actorKey, text, fidelity,
            abilityKey, spellId, footprint, targetKey, lingerMs);
        _stepEvents.Add(simEvent);
        _allEvents.Add(simEvent);
    }

    private void CaptureSnapshot() => _timeline.Add(new SimSnapshot(
        Step, TimeMs, PhaseKey,
        _actors.Select(a => a.Capture()).ToArray(),
        _stepEvents.ToArray()));

    /// <summary>Put the world back to a captured instant. Scrubbing the timeline is
    /// this call plus an index — no re-simulation, because the state is small.</summary>
    public void RestoreTo(int stepIndex)
    {
        if (_timeline.Count == 0) return;
        stepIndex = Math.Clamp(stepIndex, 0, _timeline.Count - 1);
        SimSnapshot snapshot = _timeline[stepIndex];
        foreach (SimActorState state in snapshot.Actors)
            if (_byKey.TryGetValue(state.Key, out SimActor? actor)) actor.Restore(state);
        TimeMs = snapshot.TimeMs;
        Step = snapshot.Step;
        PhaseKey = snapshot.PhaseKey;
    }

    /// <summary>Events whose footprint should be on screen at this instant.</summary>
    public IEnumerable<SimEvent> ActiveFootprints(int atMs) =>
        _allEvents.Where(e => e.Footprint is not null &&
                              e.Kind == SimEventKind.CastLand &&
                              e.TimeMs <= atMs && atMs <= e.VisibleUntilMs);
}

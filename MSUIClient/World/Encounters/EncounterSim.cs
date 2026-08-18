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
    bool[]? FiredOrderedMoves = null);

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

    /// <summary>Per (phase × job) standing orders, applied when a phase turns.
    /// The seam answer to "what does melee do when she lifts off" — a table the
    /// owner edits, never code. Explicit timed orders always override.</summary>
    public IReadOnlyList<RaidPhaseDirective>? Playbook;
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

    /// <summary>Footprint landings this body has been inside, cumulative. The
    /// positioning question the whole scenario exists to answer.</summary>
    public int HitsTaken;

    public float HealthFraction => MaxHealth <= 0 ? 0f : Health / (float)MaxHealth;
    public BodyCapsule Body => BodyCapsule.At(Position, Spec.BoundingRadius);
    public SimActorState Capture() => new(Key, Position, Facing, Health, Alive, Flying,
        HitsTaken, ActiveOrderedMoveIndex, FiredOrderedMoves.ToArray());

    public void Restore(in SimActorState state)
    {
        Position = state.Position; Facing = state.Facing;
        Health = state.Health; Alive = state.Alive; Flying = state.Flying;
        HitsTaken = state.HitsTaken;
        ActiveOrderedMoveIndex = state.ActiveOrderedMoveIndex;
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
            AdvanceMovement(dt);
            RoamPrePull(boss, dt);
            CheckPull(boss);
        }
        else if (boss is { Alive: true })
        {
            ApplyHealthModel(boss, dt);
            DispatchAggroPlan(dt);
            DispatchOrderedMoves(dt);
            AdvanceMovement(dt);
            AdvanceAutoChase(boss, dt);
            FaceAggroHolder(boss);
            ChaseAggroHolder(boss, dt);

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
        if (Options.PinnedHealthFraction is { } pinned)
        {
            boss.Health = (int)Math.Clamp(pinned * boss.MaxHealth, 0f, boss.MaxHealth);
            return;
        }

        // Per-body dps (owner-chosen, on each body) plus the fraction dial. The
        // sum is what makes the health-gated phases arrive when the raid's
        // numbers say they should, which is the point of simulating them.
        float perSecond = Options.RaidDpsFraction * boss.MaxHealth;
        foreach (SimActor actor in _actors)
        {
            if (!actor.Alive || actor.Spec.Role != EncounterActorRole.Friendly) continue;
            // Tank/melee damage happens at her feet: flying or out of reach means
            // that body contributes nothing, which is what makes an air phase
            // honestly stall the health gates.
            if (Options.MeleeDpsNeedsReach &&
                actor.Spec.Job is RaidJob.Tank or RaidJob.Melee &&
                (boss.Flying ||
                 EncounterGeometryLaw.GroundDistance(actor.Position, boss.Position) >
                 MeleeReach(boss, actor) + 3f))
                continue;
            perSecond += actor.Spec.Dps;
        }
        if (perSecond <= 0f) return;
        boss.Health = Math.Max(0, boss.Health - (int)MathF.Ceiling(perSecond * (dt / 1000f)));
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

    /// <summary>The body the boss is on: the assigned holder, else the nearest
    /// friendly (the pre-aggro-plan tank stand-in, kept as the fallback).</summary>
    private SimActor? AggroTarget(SimActor boss) =>
        _aggroKey is { } key && _byKey.TryGetValue(key, out SimActor? held) && held.Alive
            ? held : NearestFriendly(boss);

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
        // turn re-applies its directive: the owner's word beats the playbook's.
        if (actor.Spec.Role == EncounterActorRole.Friendly) actor.AutoChase = false;
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

    private void ApplyFriendlyFacingRules(SimActor boss)
    {
        foreach (SimActor actor in _actors)
        {
            if (!actor.Alive || actor.Spec.Role != EncounterActorRole.Friendly ||
                actor.Spec.PlayerRules?.AlwaysFaceBoss != true) continue;
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

    /// <summary>Apply the (phase × job) standing orders when a phase turns. Spot
    /// directives fan same-job bodies around the authored point so six dps do not
    /// stand inside one another.</summary>
    private void ApplyPlaybook(string phaseKey)
    {
        if (Options.Playbook is not { Count: > 0 } playbook) return;
        Dictionary<RaidJob, int> jobIndex = [];
        foreach (SimActor actor in _actors)
        {
            if (!actor.Alive || actor.Spec.Role != EncounterActorRole.Friendly) continue;
            RaidJob job = actor.Spec.Job;
            int index = jobIndex.GetValueOrDefault(job);
            jobIndex[job] = index + 1;

            RaidPhaseDirective? directive = null;
            foreach (RaidPhaseDirective candidate in playbook)
                if (candidate.Job == job &&
                    string.Equals(candidate.PhaseKey, phaseKey, StringComparison.Ordinal))
                { directive = candidate; break; }
            if (directive is null) continue;

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
                    float angle = index * (MathF.Tau / 8f);
                    Vector3 offset = index == 0
                        ? Vector3.Zero
                        : new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0f) * 2.5f;
                    actor.MoveTarget = directive.Spot + offset;
                    actor.PendingArrivalFacing = null;
                    Emit(SimEventKind.Move, actor.Key,
                        $"{actor.Spec.Name} falls back to the {phaseKey} spot",
                        targetKey: actor.Key);
                    break;
            }
        }
    }

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
                    _actors.RemoveAll(a => a.Spec.Role == EncounterActorRole.Add &&
                                           a.Key.StartsWith("summon:", StringComparison.Ordinal));
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

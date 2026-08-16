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
    string Key, Vector3 Position, float Facing, int Health, bool Alive, bool Flying);

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
    public float MoveSpeed = 7f;          // yd/s; vanilla base run speed
    public bool MoveIsFlight;

    public float HealthFraction => MaxHealth <= 0 ? 0f : Health / (float)MaxHealth;
    public BodyCapsule Body => BodyCapsule.At(Position, Spec.BoundingRadius);
    public SimActorState Capture() => new(Key, Position, Facing, Health, Alive, Flying);

    public void Restore(in SimActorState state)
    {
        Position = state.Position; Facing = state.Facing;
        Health = state.Health; Alive = state.Alive; Flying = state.Flying;
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
        _rng = new SeededRng(Options.Seed);
        _timeline.Clear(); _allEvents.Clear(); _stepEvents.Clear();
        _pending.Clear(); _firedOnce.Clear(); _abilityTimers.Clear();
        _sequence = null; _summonSerial = 0;

        foreach (SimActor actor in _actors)
        {
            actor.Position = actor.Spec.Position;
            actor.Facing = actor.Spec.Facing;
            actor.Health = actor.MaxHealth;
            actor.Alive = true;
            actor.Flying = false;
            actor.MoveTarget = null;
        }

        PhaseKey = _definition.FirstPhase?.Key ?? "default";
        foreach (EncounterAbility ability in _definition.Abilities)
            _abilityTimers[ability.Key] = RollInitial(ability);

        Emit(SimEventKind.Aggro, BossKey, $"{_definition.Name} engaged");
        EnterPhase(PhaseKey, announce: true);
        CaptureSnapshot();
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
        if (boss is { Alive: true })
        {
            ApplyHealthModel(boss, dt);
            AdvanceMovement(dt);

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
        if (Options.RaidDpsFraction <= 0f) return;
        float removed = Options.RaidDpsFraction * (dt / 1000f) * boss.MaxHealth;
        boss.Health = Math.Max(0, boss.Health - (int)MathF.Ceiling(removed));
    }

    private void AdvanceMovement(int dt)
    {
        foreach (SimActor actor in _actors)
        {
            if (actor.MoveTarget is not { } target || !actor.Alive) continue;
            Vector3 delta = target - actor.Position;
            float distance = delta.Length();
            float stepDistance = actor.MoveSpeed * (dt / 1000f);
            if (distance <= stepDistance || distance < 1e-3f)
            {
                actor.Position = target;
                actor.MoveTarget = null;
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
                // No threat table exists here. "Closest friendly" is the stand-in for
                // the tank, and it is labelled as an approximation everywhere it shows.
                return NearestFriendly(boss);
            case EncounterTargetKind.RandomHostile:
                return friendlies[_rng.Range(0, friendlies.Count - 1)];
            case EncounterTargetKind.RandomHostileNotVictim:
            {
                SimActor? victim = NearestFriendly(boss);
                List<SimActor> pool = friendlies.Where(a => a != victim).ToList();
                if (pool.Count == 0) pool = friendlies;
                return pool[_rng.Range(0, pool.Count - 1)];
            }
            case EncounterTargetKind.AllHostiles:
                return NearestFriendly(boss);
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
                step.Point, 0f, 0.5f, 1.5f, 55, 500);
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

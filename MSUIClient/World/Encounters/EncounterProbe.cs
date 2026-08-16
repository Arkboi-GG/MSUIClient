using System.Numerics;

namespace MSUIClient.World.Encounters;

// ─────────────────────────────────────────────────────────────────────────────
// The position probe: drop a body somewhere and get back what can hit it, when,
// and why — or why not.
//
// The "why not" half matters as much as the "why". A probe that only reports
// hits teaches nothing about where the safe line actually is; one that reports
// "outside the rear arc by 8 degrees" tells you to take one step left.
//
// A probe is a BODY, and optionally a TRAJECTORY. Bodies have width and height;
// fights move. Testing a dimensionless point at cast time would answer a
// question nobody asked.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Where the probed body is over time. A single stationary capsule is the
/// degenerate case with one waypoint.</summary>
public sealed class ProbeTrajectory
{
    private readonly List<(int TimeMs, Vector3 Position)> _waypoints = [];

    public float Radius { get; set; } = 0.5f;
    public float Height { get; set; } = BodyCapsule.DefaultHeight;
    public int Count => _waypoints.Count;
    public IReadOnlyList<(int TimeMs, Vector3 Position)> Waypoints => _waypoints;

    public static ProbeTrajectory Stationary(Vector3 position, float radius = 0.5f)
    {
        var trajectory = new ProbeTrajectory { Radius = radius };
        trajectory.Add(0, position);
        return trajectory;
    }

    public void Add(int timeMs, Vector3 position)
    {
        _waypoints.Add((timeMs, position));
        _waypoints.Sort((a, b) => a.TimeMs.CompareTo(b.TimeMs));
    }

    public void Clear() => _waypoints.Clear();

    public bool RemoveLast()
    {
        if (_waypoints.Count == 0) return false;
        _waypoints.RemoveAt(_waypoints.Count - 1);
        return true;
    }

    /// <summary>Position at an instant: constant before the first waypoint and after
    /// the last, linearly interpolated between. Walking is straight-line here — a
    /// pathfound walk would need a navmesh query the client does not have yet.</summary>
    public Vector3 PositionAt(int timeMs)
    {
        if (_waypoints.Count == 0) return Vector3.Zero;
        if (timeMs <= _waypoints[0].TimeMs) return _waypoints[0].Position;
        if (timeMs >= _waypoints[^1].TimeMs) return _waypoints[^1].Position;
        for (int i = 1; i < _waypoints.Count; i++)
        {
            if (_waypoints[i].TimeMs < timeMs) continue;
            var (t0, p0) = _waypoints[i - 1];
            var (t1, p1) = _waypoints[i];
            float span = Math.Max(t1 - t0, 1);
            return Vector3.Lerp(p0, p1, (timeMs - t0) / span);
        }
        return _waypoints[^1].Position;
    }

    public BodyCapsule BodyAt(int timeMs) => BodyCapsule.At(PositionAt(timeMs), Radius, Height);
}

/// <summary>One thing that reached (or nearly reached) the probed body.</summary>
public sealed record ProbeThreat(
    int TimeMs,
    string AbilityName,
    uint SpellId,
    string PhaseKey,
    FootprintHit Hit,
    EncounterFidelity Fidelity,
    Footprint Footprint,
    string CasterKey)
{
    public bool Covered => Hit.Covered;

    /// <summary>The one-line answer the probe panel prints.</summary>
    public string Describe() =>
        $"{TimeMs / 1000f,6:0.0}s  {AbilityName,-22} {(Covered ? "HIT " : "miss")}  {Hit.Explain()}";
}

/// <summary>Everything the probe learned about one spot, ready to print.</summary>
public sealed record ProbeReport(
    IReadOnlyList<ProbeThreat> Threats,
    int WindowStartMs,
    int WindowEndMs,
    bool Complete)
{
    public IEnumerable<ProbeThreat> Hits => Threats.Where(t => t.Covered);
    public IEnumerable<ProbeThreat> NearMisses =>
        Threats.Where(t => !t.Covered).OrderBy(t => t.Hit.ClearanceYards);

    public int HitCount => Threats.Count(t => t.Covered);
    public ProbeThreat? FirstHit => Threats.FirstOrDefault(t => t.Covered);

    /// <summary>The weakest fidelity among the things that actually hit. A spot that
    /// is only "safe" because a mechanic is unmodeled is not safe, and this is what
    /// makes the UI say so.</summary>
    public EncounterFidelity WorstHitFidelity()
    {
        EncounterFidelity worst = EncounterFidelity.ExactDb;
        foreach (ProbeThreat threat in Threats)
            if (threat.Covered && threat.Fidelity > worst) worst = threat.Fidelity;
        return worst;
    }

    public static readonly ProbeReport Empty = new([], 0, 0, true);
}

public static class EncounterProbeLaw
{
    /// <summary>How close a miss has to be before it is worth reporting.</summary>
    public const float NearMissYards = 8f;

    /// <summary>
    /// Test every landed effect in the simulated timeline against the probe body at
    /// the instant that effect lands. Near misses inside
    /// <see cref="NearMissYards"/> are kept so the panel can say how much room there
    /// was.
    /// </summary>
    public static ProbeReport Scan(
        EncounterSim sim, ProbeTrajectory trajectory, int fromMs = 0, int toMs = int.MaxValue)
    {
        if (trajectory.Count == 0) return ProbeReport.Empty;

        List<ProbeThreat> threats = [];
        string phase = sim.Definition.FirstPhase?.Key ?? "";
        var phaseAt = BuildPhaseIndex(sim);

        foreach (SimEvent simEvent in sim.Events)
        {
            if (simEvent.Kind == SimEventKind.PhaseEnter) phase = simEvent.Text;
            if (simEvent.Kind != SimEventKind.CastLand || simEvent.Footprint is not { } footprint)
                continue;
            if (simEvent.TimeMs < fromMs || simEvent.TimeMs > toMs) continue;

            // The body is tested WHERE IT IS WHEN THE EFFECT LANDS.
            BodyCapsule body = trajectory.BodyAt(simEvent.TimeMs);
            FootprintHit hit = EncounterGeometryLaw.Test(footprint, body);
            if (!hit.Covered && hit.ClearanceYards > NearMissYards) continue;

            threats.Add(new ProbeThreat(
                simEvent.TimeMs,
                AbilityNameOf(sim, simEvent),
                simEvent.SpellId,
                phaseAt.TryGetValue(simEvent.TimeMs, out string? at) ? at : phase,
                hit,
                simEvent.Fidelity,
                footprint,
                simEvent.ActorKey));
        }

        return new ProbeReport(threats, fromMs,
            toMs == int.MaxValue ? sim.TimeMs : toMs, sim.Finished);
    }

    /// <summary>Which abilities in the definition could EVER reach this spot, ignoring
    /// timing. Answers "is this position structurally safe" rather than "did anything
    /// happen to hit it in this run" — a seeded run only shows one roll of the dice.</summary>
    public static IReadOnlyList<string> StructuralThreats(
        EncounterDefinition definition, Vector3 bossPosition, BodyCapsule body,
        IEncounterSpellFacts? facts)
    {
        List<string> reaching = [];
        foreach (EncounterAbility ability in definition.Abilities)
        {
            if (!ability.HasFootprint) continue;
            Footprint footprint = EncounterGeometryLaw.Resolve(
                ability, bossPosition,
                EncounterGeometryLaw.Facing(bossPosition, body.Base),
                body.Base, facts);
            // Facing the probe is the worst case for a cone, which is the honest
            // question: "can this ever reach me here", not "does it right now".
            if (EncounterGeometryLaw.Test(footprint, body).Covered)
                reaching.Add(ability.Name);
        }
        return reaching;
    }

    private static Dictionary<int, string> BuildPhaseIndex(EncounterSim sim)
    {
        var index = new Dictionary<int, string>();
        string current = sim.Definition.FirstPhase?.Name ?? "";
        foreach (SimEvent simEvent in sim.Events)
        {
            if (simEvent.Kind == SimEventKind.PhaseEnter)
                current = simEvent.Text.Replace("phase: ", "");
            index[simEvent.TimeMs] = current;
        }
        return index;
    }

    private static string AbilityNameOf(EncounterSim sim, SimEvent simEvent)
    {
        if (simEvent.AbilityKey is { } key)
        {
            EncounterAbility? ability = sim.Definition.Abilities.FirstOrDefault(a => a.Key == key);
            if (ability is not null) return ability.Name;
        }
        return simEvent.Text.Replace(" lands", "");
    }
}

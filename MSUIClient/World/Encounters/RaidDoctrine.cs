using System.Numerics;

namespace MSUIClient.World.Encounters;

// ─────────────────────────────────────────────────────────────────────────────
// Raid doctrine: standard raid behaviour DERIVED from the encounter data instead
// of authored per body.
//
// The inversion this file exists for: the encounter definition already says
// where not to stand (cone arcs), how close melee must be (combat reach), and
// what is telegraphed (cast times, lanes). So "where does everyone stand" is a
// COMPUTATION — melee tucked between the front and rear arcs at reach, split
// left/right by macro group, ranged behind them, healers between — and the owner
// only decides the genuinely human things: the group split, special jobs, and
// the doctrine toggles below. Per-body authored positioning, playbook orders and
// explicit RTS clicks all outrank the derived formation; doctrine fills silence,
// it never argues with an order.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>"In PHASE, every JOB body from FromOrdinal up targets TARGET" — the
/// bucket assignment. Ordinals are 1-based positions within the job, in stable
/// key order, so "tanks 2 and up pick up adds" is (phase, Tank, 2, AnyAdd) while
/// tank 1 keeps the plan's boss order. Rides the same per-phase resolution as a
/// body's own override; the body's own override wins.</summary>
public sealed record PhaseJobAssignment(
    string PhaseKey, RaidJob Job, int FromOrdinal, CombatEnemyKind Target);

/// <summary>"Keep AURA on TANK, one caster at a time" — the Fear Ward law. Every
/// body of the caster class forms an implicit chain: whoever is off cooldown
/// refreshes the ward the moment it is down, and nobody double-casts while it is
/// up. The sim tracks presence/duration/cooldown and emits the casts; it does not
/// model what the aura absorbs (that is server truth — HasAura in the core).</summary>
public sealed record MaintainAuraRule(
    uint SpellId,
    string Name,
    /// <summary>1.12 class id of the casters (5 = priest for Fear Ward).</summary>
    uint CasterClassId,
    int DurationMs,
    int CooldownMs,
    /// <summary>Which tank carries the ward (1-based, stable key order).</summary>
    int TargetTankOrdinal = 1);

/// <summary>"This class slows and grinds the add pack" — the Blizzard law. Casters
/// of the class channel their owner-authored DPS as an AoE over every add inside
/// the radius around the densest reachable add, and adds inside it move at
/// SlowFactor speed. Executable consequence, not a spell model: the slow is what
/// buys the raid time, and that is the part movement can prove.</summary>
public sealed record AddControlJob(
    uint CasterClassId,
    float RadiusYards = 8f,
    float SlowFactor = 0.5f,
    /// <summary>Fewer live adds than this and the caster keeps its single-target
    /// routing — nobody blizzards one whelp.</summary>
    int MinAdds = 3,
    /// <summary>How far the caster can project the effect (vanilla Blizzard ~30yd).</summary>
    float CastRangeYards = 30f);

/// <summary>The raid-wide standing rules. One record for the whole scenario;
/// null on the sim options means "no doctrine" (every pre-doctrine behaviour
/// unchanged). Defaults are the sensible raid: derive stations, dodge what is
/// telegraphed, keep clear of instant arcs, spread off a targeted cast, heal
/// tanks first then your own macro group.</summary>
public sealed record RaidDoctrine(
    /// <summary>Drive unordered bodies to computed formation stations.</summary>
    bool DeriveFormation = true,
    /// <summary>Every body dodges telegraphed non-projectile casts by default.
    /// A body carrying its own AvoidAbilityKeys uses that list INSTEAD.</summary>
    bool DodgeTelegraphs = true,
    /// <summary>Every body slides out of instant cone arcs by default (the aggro
    /// holder stays exempt from arcs that point at him — aiming her is his job).</summary>
    bool KeepClearOfCones = true,
    /// <summary>When a single-target cast is in flight, everyone near the target
    /// steps away; the target holds so the effect lands on one body, not five.</summary>
    bool SpreadFromTargetedCasts = true,
    float SpreadYards = 8f,
    /// <summary>Healers with no authored protect rows derive them: the first
    /// healers cover the tanks one-to-one, the rest watch their macro group's
    /// lowest health. Authored SupportPriorities always win.</summary>
    bool GroupHealing = true,
    /// <summary>Per-phase job bucket assignments ("tanks 2+ on adds").</summary>
    IReadOnlyList<PhaseJobAssignment>? Assignments = null,
    /// <summary>Maintained-aura chains ("keep Fear Ward on tank 1, priests rotate").</summary>
    IReadOnlyList<MaintainAuraRule>? MaintainAuras = null,
    /// <summary>Class-gated add-control jobs ("mages Blizzard the pack").</summary>
    IReadOnlyList<AddControlJob>? AddControl = null,
    /// <summary>Opt-in boss threat-lite: when NO owner-assigned aggro entry governs,
    /// her victim derives — the nearest body holds her (the emergency stand-in with
    /// his shield out), and the first tank to reach melee range takes over. Cleared
    /// on every phase turn, which is exactly the P2→P3 landing race. Default OFF:
    /// owner-assigned aggro remains the design's resting state.</summary>
    bool BossThreatLite = false);

/// <summary>The pure geometry of the derived formation. Stateless and testable:
/// every function maps encounter facts to a station, no sim types involved.</summary>
public static class RaidFormationLaw
{
    /// <summary>Angular safety margin beyond a cone's own half-arc, radians (~12°).</summary>
    public const float ArcMarginRad = 0.21f;

    /// <summary>The standing threat picture of one phase: the widest instant front
    /// cone's half-angle, the widest rear cone's half-angle, and the longest cone
    /// range — everything the flank band and the ranged standoff derive from.</summary>
    public readonly record struct PhaseArcs(
        float FrontHalfRad, float RearHalfRad, float ConeRangeYards)
    {
        /// <summary>Where the safe flank band begins/ends, as an absolute angle off
        /// the boss's nose. Collapses to a beam at 90° when margins overlap.</summary>
        public (float From, float To) SafeBand()
        {
            float from = FrontHalfRad + ArcMarginRad;
            float to = MathF.PI - RearHalfRad - ArcMarginRad;
            return to > from ? (from, to) : (MathF.PI / 2f, MathF.PI / 2f);
        }
    }

    /// <summary>Scan the phase's INSTANT cones — the standing threats a body must
    /// not idle inside. Telegraphed casts are the dodge system's problem, not the
    /// formation's. Authored values win; spell data fills zeros; the vmangos 90°
    /// default covers the rest.</summary>
    public static PhaseArcs ComputeArcs(
        EncounterDefinition definition, string phaseKey, IEncounterSpellFacts? facts)
    {
        float front = 0f, rear = 0f, range = 0f;
        foreach (EncounterAbility ability in definition.AbilitiesIn(phaseKey))
        {
            if (ability.Geometry.Kind != FootprintKind.Cone || ability.CastTimeMs > 0)
                continue;
            if (ability.Trigger.Kind == EncounterTriggerKind.Manual) continue;
            float degrees = ability.Geometry.ConeDegrees;
            if (degrees == 0f && facts is not null &&
                facts.TryGetConeDegrees(ability.SpellId, out float dbDegrees))
                degrees = dbDegrees;
            if (degrees == 0f) degrees = 90f;
            float half = MathF.Abs(degrees) * .5f * (MathF.PI / 180f);
            if (degrees < 0f) rear = MathF.Max(rear, half);
            else front = MathF.Max(front, half);

            float radius = ability.Geometry.Radius;
            if (radius <= 0f && facts is not null &&
                facts.TryGetRadius(ability.SpellId, out float dbcRadius))
                radius = dbcRadius;
            range = MathF.Max(range, radius);
        }
        return new PhaseArcs(front, rear, range);
    }

    /// <summary>A slot's angle inside the safe band: bodies fan evenly, never on the
    /// band's edges. One body stands mid-band.</summary>
    public static float SlotAngle(float from, float to, int index, int count) =>
        from + (to - from) * ((index + 0.5f) / MathF.Max(count, 1));

    /// <summary>The station: boss-relative polar. sideSign +1 = the boss's LEFT
    /// (facing + 90°, Group 1), -1 = her right (Group 2). Z is the walker's own —
    /// the sim has no terrain to sample.</summary>
    public static Vector3 Station(
        Vector3 bossPosition, float bossFacing, float angleOffNose, int sideSign,
        float radius, float z)
    {
        float bearing = bossFacing + sideSign * angleOffNose;
        return new Vector3(
            bossPosition.X + MathF.Cos(bearing) * radius,
            bossPosition.Y + MathF.Sin(bearing) * radius,
            z);
    }

    /// <summary>The air-phase ring: nothing to flank when she is flying, so the raid
    /// spreads evenly on a circle — which is also what keeps a targeted cast from
    /// clipping neighbours.</summary>
    public static Vector3 AirStation(
        Vector3 bossPosition, int index, int count, float radius, float z)
    {
        float bearing = MathF.Tau * index / MathF.Max(count, 1);
        return new Vector3(
            bossPosition.X + MathF.Cos(bearing) * radius,
            bossPosition.Y + MathF.Sin(bearing) * radius,
            z);
    }

    /// <summary>Ranged standoff: past the longest instant cone with margin, floored
    /// at a sensible bow range, capped inside 1.12's 30-yd spell envelope.</summary>
    public static float RangedRadius(in PhaseArcs arcs, float meleeReach) =>
        Math.Clamp(MathF.Max(arcs.ConeRangeYards + 4f, meleeReach + 15f), 12f, 30f);

    /// <summary>Healers sit between the melee ball and the ranged line: close enough
    /// to reach both, behind the flank wedge's inner edge.</summary>
    public static float HealerRadius(in PhaseArcs arcs, float meleeReach) =>
        (meleeReach + RangedRadius(arcs, meleeReach)) * .5f;
}

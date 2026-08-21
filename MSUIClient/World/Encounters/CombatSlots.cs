using System.Numerics;

namespace MSUIClient.World.Encounters;

// ─────────────────────────────────────────────────────────────────────────────
// The POSITIONING slot — the second half of a body's behaviour, held apart from
// the rotation on purpose.
//
// A rotation (CombatPlan) is portable: the same holy-priest priority list works
// on every fight. Positioning is NOT — where you stand is boss-relative, phase
// by phase, and different on the left of the room than the right. So positioning
// is its own reusable library item, keyed by role AND side, and a body picks one
// rotation and one positioning independently. "Left ranged" and "right ranged"
// are two scripts; authoring one and mirroring it across the boss's facing gives
// you the other for free.
//
// Like everything else in the Lab this is DATA, not code: a phase step is a
// directive plus optional waypoints, never a branch into a scripting language.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>What one role does spatially during one phase. The directive is the
/// same vocabulary the playbook used — Hold the owner's ground, keep melee reach
/// on the boss, or run to an authored spot — now carrying its own optional
/// multi-leg path so "spread to the wall, then collapse in" is one step.</summary>
public sealed record PositioningPhaseStep(
    string PhaseKey,
    RaidDirectiveKind Kind,
    /// <summary>The MoveToSpot destination (boss-relative world coords). Ignored for
    /// Hold and ChaseBoss.</summary>
    Vector3 Spot = default,
    /// <summary>Arrival facing in radians; NaN keeps the facing the run ended with.</summary>
    float ArrivalFacing = float.NaN,
    /// <summary>Extra legs after the first Spot, for a phase that is really a short
    /// path rather than a single stand-here. Null = a single MoveToSpot (or none).</summary>
    IReadOnlyList<TimedMove>? Waypoints = null)
{
    public bool HasArrivalFacing => !float.IsNaN(ArrivalFacing);
}

/// <summary>
/// One reusable positioning script: everything spatial for ONE role on ONE side.
/// It owns the movement doctrine that used to live on the rotation (follow/hold/
/// range/face) plus the per-phase spots and paths. Its spots are boss-relative, so
/// it records which encounter it was authored against; cloning it for a new boss
/// is expected — the role×side STRUCTURE is what's reusable, the coordinates get
/// re-placed.
/// </summary>
public sealed record PositioningScript(
    /// <summary>Stable library id, independent of any body. The store stamps one on
    /// first save.</summary>
    string Id = "",
    string Name = "Custom positioning",
    /// <summary>Which role this positioning is written for. Advisory — a body of any
    /// role may be assigned it — but it drives the library's role filter.</summary>
    RaidJob Role = RaidJob.None,
    /// <summary>Left / Center / Right. The mirror axis for Left⇄Right is the boss's
    /// facing; see <see cref="RaidSideExtensions.Mirror"/>.</summary>
    RaidSide Side = RaidSide.None,
    /// <summary>The spatial doctrine, moved here from the rotation. Null = defaults
    /// (move independently, no follow anchor).</summary>
    CombatMovementPlan? Movement = null,
    /// <summary>Per-phase directives and paths. Empty = fall back to the movement
    /// doctrine every phase.</summary>
    IReadOnlyList<PositioningPhaseStep>? Phases = null,
    /// <summary>Encounter key the spots were placed against, for the "authored for a
    /// different boss" warning. Null on a doctrine-only script with no placed spots.</summary>
    string? EncounterKey = null,
    string? Note = null)
{
    public PositioningPhaseStep? Step(string phaseKey) =>
        Phases?.FirstOrDefault(step => step.PhaseKey == phaseKey);

    /// <summary>True once any phase places a real spot — i.e. the script carries
    /// boss-relative coordinates and is no longer a portable doctrine-only shell.</summary>
    public bool HasPlacedSpots =>
        Phases?.Any(step => step.Kind == RaidDirectiveKind.MoveToSpot ||
                            step.Waypoints is { Count: > 0 }) == true;
}

using System.Numerics;

namespace MSUIClient.World.Encounters;

// ─────────────────────────────────────────────────────────────────────────────
// Resolved geometry: turning a declarative EncounterGeometrySpec plus live actor
// positions into a concrete shape in world space, and answering the only
// question the position probe cares about — does this shape cover THIS body?
//
// A position is a body capsule, never a point. Whether something lands depends
// on the body's radius and height, on the caster's facing, and on where the body
// is at IMPACT rather than at cast — so every test here takes a capsule, and the
// simulator evaluates footprints at their impact moment.
//
// World space is WoW's: X north, Y west, Z up; orientation 0 faces +X and
// increases toward +Y, so forward = (cos o, sin o, 0). Verified against
// LocalMovementSender's jump-vector encoding.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>A body: feet at <paramref name="Base"/>, <paramref name="Radius"/> wide,
/// <paramref name="Height"/> tall. Radius comes from UNIT_BOUNDINGRADIUS on real
/// units and from the actor spec on synthetic ones.</summary>
public readonly record struct BodyCapsule(Vector3 Base, float Radius, float Height)
{
    public const float DefaultHeight = 2.2f;
    public static BodyCapsule At(Vector3 position, float radius = 0.5f, float height = DefaultHeight)
        => new(position, MathF.Max(radius, 0.01f), MathF.Max(height, 0.1f));

    public float Top => Base.Z + Height;
    public Vector2 Ground => new(Base.X, Base.Y);
}

/// <summary>A footprint in world space at a single moment. Produced by the
/// resolver, consumed by the renderer and by the hit test.</summary>
public sealed record Footprint(
    FootprintKind Kind,
    Vector3 Origin,
    float Radius = 0f,
    float Facing = 0f,
    float ConeDegrees = 0f,
    float Width = 0f,
    Vector3 End = default,
    IReadOnlyList<Vector3>? Points = null,
    float VerticalTolerance = Footprint.DefaultVerticalTolerance)
{
    /// <summary>How far above/below the footprint plane a body still counts as
    /// standing in it. Vanilla AoE is effectively cylindrical; this is the honest
    /// approximation and it is surfaced in the probe rather than hidden.</summary>
    public const float DefaultVerticalTolerance = 5f;

    public static readonly Footprint Nothing = new(FootprintKind.None, Vector3.Zero);
    public bool IsRearCone => ConeDegrees < 0f;
}

public enum FootprintVerdict
{
    Covered,
    OutOfRange,
    OutsideArc,
    WrongHeight,
    NoFootprint,
}

/// <summary>Why a body is or is not in a footprint, with the numbers that decide it.
/// The probe prints this verbatim — "why" is as much the product as "whether".</summary>
public readonly record struct FootprintHit(
    FootprintVerdict Verdict,
    float GroundDistance,
    float AngleDegrees,
    float ClearanceYards)
{
    public bool Covered => Verdict == FootprintVerdict.Covered;

    public string Explain() => Verdict switch
    {
        FootprintVerdict.Covered => $"covered ({GroundDistance:0.0} yd from origin)",
        FootprintVerdict.OutOfRange => $"{ClearanceYards:0.0} yd outside the radius",
        FootprintVerdict.OutsideArc => $"outside the arc by {AngleDegrees:0} deg",
        FootprintVerdict.WrongHeight => $"{ClearanceYards:0.0} yd above/below the effect plane",
        _ => "no footprint",
    };
}

/// <summary>The spell facts the geometry resolver needs. Implemented by the client's
/// data layer (Spell.dbc + the world DB's spell_template / spell_cone /
/// spell_target_position). An interface keeps this subsystem free of Net and
/// Formats coupling and makes the resolver testable with a stub.</summary>
public interface IEncounterSpellFacts
{
    bool TryGetRadius(uint spellId, out float radius);
    bool TryGetConeDegrees(uint spellId, out float degrees);
    bool TryGetSpeed(uint spellId, out float yardsPerSecond);
    bool TryGetCastTimeMs(uint spellId, out int castTimeMs);
    bool TryGetDatabasePosition(uint spellId, out Vector3 position);
    string? SpellName(uint spellId);
}

public static class EncounterGeometryLaw
{
    public static Vector3 Forward(float facing) => new(MathF.Cos(facing), MathF.Sin(facing), 0f);

    public static float Facing(Vector3 from, Vector3 to)
    {
        float dx = to.X - from.X, dy = to.Y - from.Y;
        return dx * dx + dy * dy < 1e-8f ? 0f : MathF.Atan2(dy, dx);
    }

    public static float GroundDistance(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X, dy = a.Y - b.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    // ── resolution: spec + actors → a concrete shape ─────────────────────────

    /// <summary>
    /// Build the world-space footprint for one ability instance. Authored values in
    /// the spec always win; zeros fall back to spell data, so a definition only has
    /// to state what the DBC gets wrong. Returns <see cref="Footprint.Nothing"/> for
    /// abilities with no spatial effect.
    /// </summary>
    public static Footprint Resolve(
        EncounterAbility ability,
        Vector3 casterPosition,
        float casterFacing,
        Vector3? targetPosition,
        IEncounterSpellFacts? facts)
    {
        EncounterGeometrySpec spec = ability.Geometry;
        if (spec.Kind == FootprintKind.None) return Footprint.Nothing;

        float radius = spec.Radius;
        if (radius <= 0f && facts is not null && facts.TryGetRadius(ability.SpellId, out float dbcRadius))
            radius = dbcRadius;

        Vector3 origin = spec.Kind == FootprintKind.Cone || ability.Target.Kind == EncounterTargetKind.Self
            ? casterPosition
            : targetPosition ?? casterPosition;

        switch (spec.Kind)
        {
            case FootprintKind.Circle:
                return new Footprint(FootprintKind.Circle, origin, MathF.Max(radius, 0.5f));

            case FootprintKind.Cone:
            {
                float degrees = spec.ConeDegrees;
                if (degrees == 0f && facts is not null &&
                    facts.TryGetConeDegrees(ability.SpellId, out float dbDegrees))
                    degrees = dbDegrees;
                if (degrees == 0f) degrees = 90f;   // vmangos's own default cone
                // A cone points at the victim when there is one: the core turns the
                // caster to face its target before a directed cast. A DEGENERATE target
                // (self-anchored casts pass the caster's own position) must fall back to
                // the caster's facing — Facing() returns 0 for a zero-length delta, which
                // silently aimed every self-cone at world +X instead of at her nose.
                float facing = targetPosition is { } target &&
                               GroundDistance(casterPosition, target) > 1e-3f
                    ? Facing(casterPosition, target)
                    : casterFacing;
                return new Footprint(FootprintKind.Cone, casterPosition,
                    MathF.Max(radius, 0.5f), facing, degrees);
            }

            case FootprintKind.Line:
            {
                Vector3 end = targetPosition ?? casterPosition + Forward(casterFacing) * MathF.Max(radius, 5f);
                float width = spec.Width > 0f ? spec.Width : 4f;
                return new Footprint(FootprintKind.Line, casterPosition,
                    MathF.Max(radius, width * .5f), Facing(casterPosition, end), 0f, width, end);
            }

            case FootprintKind.PointChain:
            {
                IReadOnlyList<Vector3> points = ResolveChainPoints(spec, facts);
                if (points.Count == 0) return Footprint.Nothing;
                return new Footprint(FootprintKind.PointChain, points[0],
                    MathF.Max(radius, 0.5f), 0f, 0f, 0f, points[^1], points);
            }

            case FootprintKind.Projectile:
            {
                Vector3 end = targetPosition ?? casterPosition;
                return new Footprint(FootprintKind.Projectile, casterPosition,
                    MathF.Max(radius, 0.5f), Facing(casterPosition, end), 0f, 0f, end);
            }

            default:
                return Footprint.Nothing;
        }
    }

    /// <summary>Breath-lane points: authored coordinates win, otherwise every listed
    /// spell's row in the world DB's spell_target_position. This is the path that
    /// makes Onyxia's lanes exact-db instead of hand-drawn.</summary>
    public static IReadOnlyList<Vector3> ResolveChainPoints(
        EncounterGeometrySpec spec, IEncounterSpellFacts? facts)
    {
        if (spec.Points is { Count: > 0 }) return spec.Points;
        if (spec.PointSpellIds is not { Count: > 0 } ids || facts is null) return [];
        List<Vector3> points = new(ids.Count);
        foreach (uint id in ids)
            if (facts.TryGetDatabasePosition(id, out Vector3 point)) points.Add(point);
        return points;
    }

    // ── the hit test ─────────────────────────────────────────────────────────

    /// <summary>Does this footprint cover this body? The whole probe rests on this.</summary>
    public static FootprintHit Test(in Footprint footprint, in BodyCapsule body) =>
        footprint.Kind switch
        {
            FootprintKind.Circle => TestCircle(footprint.Origin, footprint.Radius, footprint, body),
            FootprintKind.Projectile => TestCircle(footprint.End, footprint.Radius, footprint, body),
            FootprintKind.Cone => TestCone(footprint, body),
            FootprintKind.Line => TestLine(footprint, body),
            FootprintKind.PointChain => TestChain(footprint, body),
            _ => new FootprintHit(FootprintVerdict.NoFootprint, 0f, 0f, 0f),
        };

    private static FootprintHit TestCircle(
        Vector3 centre, float radius, in Footprint footprint, in BodyCapsule body)
    {
        float distance = GroundDistance(centre, body.Base);
        float reach = radius + body.Radius;
        if (distance > reach)
            return new FootprintHit(FootprintVerdict.OutOfRange, distance, 0f, distance - reach);
        if (!OverlapsVertically(centre.Z, footprint.VerticalTolerance, body, out float clearance))
            return new FootprintHit(FootprintVerdict.WrongHeight, distance, 0f, clearance);
        return new FootprintHit(FootprintVerdict.Covered, distance, 0f, reach - distance);
    }

    private static FootprintHit TestCone(in Footprint footprint, in BodyCapsule body)
    {
        float distance = GroundDistance(footprint.Origin, body.Base);
        float reach = footprint.Radius + body.Radius;
        if (distance > reach)
            return new FootprintHit(FootprintVerdict.OutOfRange, distance, 0f, distance - reach);
        if (!OverlapsVertically(footprint.Origin.Z, footprint.VerticalTolerance, body, out float clearance))
            return new FootprintHit(FootprintVerdict.WrongHeight, distance, 0f, clearance);

        // Standing inside the caster: every arc covers you.
        if (distance <= body.Radius)
            return new FootprintHit(FootprintVerdict.Covered, distance, 0f, reach - distance);

        // Rear arcs are stored negative (the spell_cone convention), so the arc's
        // centre line is the caster's back.
        float centreAngle = footprint.IsRearCone ? footprint.Facing + MathF.PI : footprint.Facing;
        float toBody = MathF.Atan2(body.Base.Y - footprint.Origin.Y, body.Base.X - footprint.Origin.X);
        float offset = MathF.Abs(NormalizeAngle(toBody - centreAngle));
        // A body is not a point: its width buys angular slack that grows as it closes.
        float slack = MathF.Asin(Math.Clamp(body.Radius / MathF.Max(distance, 1e-3f), 0f, 1f));
        float halfArc = MathF.Abs(footprint.ConeDegrees) * .5f * (MathF.PI / 180f);
        float excess = offset - slack - halfArc;
        return excess <= 0f
            ? new FootprintHit(FootprintVerdict.Covered, distance, offset * (180f / MathF.PI), -excess)
            : new FootprintHit(FootprintVerdict.OutsideArc, distance,
                excess * (180f / MathF.PI), 0f);
    }

    private static FootprintHit TestLine(in Footprint footprint, in BodyCapsule body)
    {
        (float distance, Vector3 closest) = ClosestOnSegment(footprint.Origin, footprint.End, body.Base);
        float reach = footprint.Width * .5f + body.Radius;
        if (distance > reach)
            return new FootprintHit(FootprintVerdict.OutOfRange, distance, 0f, distance - reach);
        if (!OverlapsVertically(closest.Z, footprint.VerticalTolerance, body, out float clearance))
            return new FootprintHit(FootprintVerdict.WrongHeight, distance, 0f, clearance);
        return new FootprintHit(FootprintVerdict.Covered, distance, 0f, reach - distance);
    }

    private static FootprintHit TestChain(in Footprint footprint, in BodyCapsule body)
    {
        FootprintHit best = new(FootprintVerdict.OutOfRange, float.MaxValue, 0f, float.MaxValue);
        foreach (Vector3 point in footprint.Points ?? [])
        {
            FootprintHit hit = TestCircle(point, footprint.Radius, footprint, body);
            if (hit.Covered) return hit;
            if (hit.ClearanceYards < best.ClearanceYards) best = hit;
        }
        return best.GroundDistance == float.MaxValue
            ? new FootprintHit(FootprintVerdict.NoFootprint, 0f, 0f, 0f)
            : best;
    }

    /// <summary>Does the body's vertical extent reach the effect plane's tolerance band?
    /// Onyxia hovering 20 yd up is exactly why this is not skipped.</summary>
    private static bool OverlapsVertically(
        float planeZ, float tolerance, in BodyCapsule body, out float clearance)
    {
        float low = planeZ - tolerance, high = planeZ + tolerance;
        if (body.Top < low) { clearance = low - body.Top; return false; }
        if (body.Base.Z > high) { clearance = body.Base.Z - high; return false; }
        clearance = 0f;
        return true;
    }

    private static (float Distance, Vector3 Closest) ClosestOnSegment(Vector3 a, Vector3 b, Vector3 p)
    {
        Vector2 ab = new(b.X - a.X, b.Y - a.Y);
        float lengthSq = ab.LengthSquared();
        float t = lengthSq < 1e-6f ? 0f
            : Math.Clamp(((p.X - a.X) * ab.X + (p.Y - a.Y) * ab.Y) / lengthSq, 0f, 1f);
        Vector3 closest = new(a.X + ab.X * t, a.Y + ab.Y * t, a.Z + (b.Z - a.Z) * t);
        return (GroundDistance(closest, p), closest);
    }

    public static float NormalizeAngle(float radians)
    {
        while (radians > MathF.PI) radians -= MathF.Tau;
        while (radians < -MathF.PI) radians += MathF.Tau;
        return radians;
    }

    /// <summary>Impact delay in ms: cast time plus missile travel. The probe needs
    /// this because "where the body is at impact" is the question, not "where it was
    /// when the cast began".</summary>
    public static int ImpactDelayMs(EncounterAbility ability, float travelYards, IEncounterSpellFacts? facts)
    {
        int cast = ability.CastTimeMs;
        if (cast <= 0 && facts is not null && facts.TryGetCastTimeMs(ability.SpellId, out int dbcCast))
            cast = dbcCast;
        float travelMs = 0f;
        if (ability.Geometry.Kind == FootprintKind.Projectile)
        {
            float speed = ability.Geometry.ProjectileSpeed;
            if (speed <= 0f && facts is not null && facts.TryGetSpeed(ability.SpellId, out float dbcSpeed))
                speed = dbcSpeed;
            if (speed > 0f) travelMs = travelYards / speed * 1000f;
        }
        return cast + (int)MathF.Round(travelMs);
    }
}

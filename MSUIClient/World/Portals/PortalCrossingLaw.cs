using System.Numerics;

namespace MSUIClient.World.Portals;

/// <summary>
/// Pure geometry shared by the runtime walk-through trigger and the enlarged
/// portal click target.  It owns no network or movement state: callers retain
/// the per-GUID arm/latch and the authoritative GameObject-use decision.
/// </summary>
public static class PortalCrossingLaw
{
    public readonly record struct Crossing(
        Vector3 FeetIntersection,
        float Fraction,
        int FromSide);

    /// <summary>
    /// Test a feet-position segment against a vertical portal rectangle.  The
    /// character is treated as a vertical capsule: its leading edge touching
    /// the plane is a crossing, its radius expands the horizontal edge, and its
    /// height overlaps the opening.  Testing the capsule edge instead of only
    /// its centre also works when world collision stops the centre just short
    /// of a cosmetic portal model's plane.
    /// </summary>
    public static bool TryCross(
        Vector3 previousFeet,
        Vector3 proposedFeet,
        in PortalFrame frame,
        float halfWidth,
        float halfHeight,
        float capsuleRadius,
        float capsuleHeight,
        out Crossing crossing)
    {
        crossing = default;
        if (!frame.TryNormalize(out PortalFrame portal) ||
            !Finite(previousFeet) || !Finite(proposedFeet) ||
            !float.IsFinite(halfWidth) || !float.IsFinite(halfHeight) ||
            !float.IsFinite(capsuleRadius) || !float.IsFinite(capsuleHeight) ||
            halfWidth <= 0f || halfHeight <= 0f ||
            capsuleRadius < 0f || capsuleHeight <= 0f)
            return false;

        float previousDistance = Vector3.Dot(previousFeet - portal.Center, portal.Normal);
        float proposedDistance = Vector3.Dot(proposedFeet - portal.Center, portal.Normal);
        if (MathF.Abs(previousDistance) <= 1e-5f) return false;

        int fromSide = previousDistance > 0f ? 1 : -1;
        float signedMotion = fromSide * (proposedDistance - previousDistance);
        if (signedMotion >= -1e-6f) return false; // parallel to or away from the film

        float previousClearance = fromSide * previousDistance - capsuleRadius;
        float proposedClearance = fromSide * proposedDistance - capsuleRadius;
        if (proposedClearance > 1e-5f) return false; // leading edge did not reach the plane

        // A portal can be discovered, or collision can be rebuilt, while the
        // capsule already overlaps the zero-depth film.  The first continuing
        // inward segment is the recoverable crossing in that case.
        float fraction = 0f;
        if (previousClearance > 0f)
        {
            float denominator = previousClearance - proposedClearance;
            if (denominator <= 1e-6f) return false;
            fraction = previousClearance / denominator;
            if (fraction < 0f || fraction > 1f) return false;
        }

        Vector3 intersection = Vector3.Lerp(previousFeet, proposedFeet, fraction);
        Vector3 relative = intersection - portal.Center;
        float lateral = Vector3.Dot(relative, portal.Right);
        if (MathF.Abs(lateral) > halfWidth + capsuleRadius) return false;

        float feet = Vector3.Dot(relative, portal.Up);
        float head = feet + capsuleHeight;
        if (head < -halfHeight || feet > halfHeight) return false;

        crossing = new Crossing(intersection, fraction, fromSide);
        return true;
    }

    /// <summary>
    /// Rearm a per-portal crossing latch from whichever side the player is now
    /// clearly on.  This deliberately replaces an opposite stale side: walking
    /// around an aperture changes sides without crossing its rectangle.
    /// </summary>
    public static int ResolveArmedSide(
        int armedSide,
        float signedDistance,
        float planeEpsilon,
        bool latchAvailable)
    {
        int retained = armedSide is -1 or 1 ? armedSide : 0;
        if (!latchAvailable || !float.IsFinite(signedDistance) ||
            !float.IsFinite(planeEpsilon) || planeEpsilon <= 0f ||
            MathF.Abs(signedDistance) < planeEpsilon)
            return retained;

        return signedDistance > 0f ? 1 : -1;
    }

    /// <summary>Two-sided ray hit against the visible rectangular aperture.</summary>
    public static bool TryRayHit(
        Vector3 origin,
        Vector3 direction,
        in PortalFrame frame,
        float halfWidth,
        float halfHeight,
        float limit,
        out float distance)
    {
        distance = float.PositiveInfinity;
        if (!frame.TryNormalize(out PortalFrame portal) ||
            !Finite(origin) || !Finite(direction) ||
            !float.IsFinite(halfWidth) || !float.IsFinite(halfHeight) ||
            !float.IsFinite(limit) || halfWidth <= 0f || halfHeight <= 0f || limit <= 0f)
            return false;

        float denominator = Vector3.Dot(direction, portal.Normal);
        if (MathF.Abs(denominator) <= 1e-6f) return false;

        float hit = Vector3.Dot(portal.Center - origin, portal.Normal) / denominator;
        if (hit < 0f || hit >= limit) return false;

        Vector3 relative = origin + direction * hit - portal.Center;
        if (MathF.Abs(Vector3.Dot(relative, portal.Right)) > halfWidth ||
            MathF.Abs(Vector3.Dot(relative, portal.Up)) > halfHeight)
            return false;

        distance = hit;
        return true;
    }

    private static bool Finite(in Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}

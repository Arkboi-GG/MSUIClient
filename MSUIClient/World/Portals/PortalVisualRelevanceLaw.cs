using System.Numerics;

namespace MSUIClient.World.Portals;

/// <summary>
/// Bounds the lifetime of the local procedural portal presentation independently
/// of the server's object-update range. Same-map teleports can leave source-map
/// entities in the store until a later out-of-range block arrives; an explicit
/// visual must not remain drawable at arbitrary distance during that interval.
/// </summary>
public static class PortalVisualRelevanceLaw
{
    // Enter conservatively, then retain through a wider exit radius so ordinary
    // movement around the boundary cannot repeatedly rebuild the aperture.
    public const float EnterRadius = 90f;
    public const float ExitRadius = 120f;

    // A one-frame parser/store gap should not blink a nearby portal. Genuine
    // despawns still disappear promptly, and map teardown clears immediately.
    public const double MissingEntityGraceSeconds = 0.50;

    public static bool IsRelevant(
        in Vector3 observer, in Vector3 portal, bool currentlyTracked)
    {
        if (!Finite(observer) || !Finite(portal)) return false;
        float radius = currentlyTracked ? ExitRadius : EnterRadius;
        return Vector3.DistanceSquared(observer, portal) <= radius * radius;
    }

    public static bool MissingEntityGraceExpired(double now, double lastSeenAt)
    {
        if (!double.IsFinite(now) || !double.IsFinite(lastSeenAt)) return true;
        return now - lastSeenAt >= MissingEntityGraceSeconds;
    }

    private static bool Finite(in Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}

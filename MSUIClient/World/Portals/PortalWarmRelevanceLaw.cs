using System.Numerics;

namespace MSUIClient.World.Portals;

/// <summary>
/// Bounds speculative destination warming independently of the nearer procedural
/// aperture presentation range. The wider exit radius prevents ordinary movement
/// around the warm boundary from repeatedly retiring and rebuilding a candidate.
/// </summary>
public static class PortalWarmRelevanceLaw
{
    public const float EnterRadius = 150f;
    public const float ExitRadius = 180f;

    public static bool IsRelevant(
        in Vector3 observer, in Vector3 portal, bool currentlyTracked)
    {
        if (!Finite(observer) || !Finite(portal)) return false;
        float radius = currentlyTracked ? ExitRadius : EnterRadius;
        return Vector3.DistanceSquared(observer, portal) <= radius * radius;
    }

    private static bool Finite(in Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}

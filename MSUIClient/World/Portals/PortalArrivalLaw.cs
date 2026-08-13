using System.Numerics;

namespace MSUIClient.World.Portals;

/// <summary>
/// The shared arrival-support invariant for a prepared preview and the main
/// world loader.  "There is some terrain/collision somewhere below" is not
/// enough: revealing a character tens of yards above or below it is precisely
/// the visible fall-through failure this gate exists to prevent.
/// </summary>
public static class PortalArrivalLaw
{
    // Server teleport positions are feet positions.  A small downward allowance
    // covers interpolation/steps; a still smaller upward allowance covers float
    // drift without accepting a ceiling or the terrain shell over an interior.
    public const float MaxSupportDrop = 3f;
    public const float MaxSupportRise = 1f;

    public static bool HasNearbySupport(
        in Vector3 arrival, float? surfaceZ,
        float maxDrop = MaxSupportDrop,
        float maxRise = MaxSupportRise)
    {
        if (!Finite(arrival) || surfaceZ is not float surface ||
            !float.IsFinite(surface) || !float.IsFinite(maxDrop) ||
            !float.IsFinite(maxRise) || maxDrop < 0f || maxRise < 0f)
            return false;

        float feetAboveSurface = arrival.Z - surface;
        return feetAboveSurface >= -maxRise && feetAboveSurface <= maxDrop;
    }

    private static bool Finite(in Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}

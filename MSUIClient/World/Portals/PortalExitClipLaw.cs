using System.Numerics;

namespace MSUIClient.World.Portals;

/// <summary>
/// Builds the destination doorway's one-way rendering cut. The transformed
/// preview camera lives behind the synthetic destination aperture; retaining
/// only the half-space in front of it prevents walls, terrain and props behind
/// the exit from leaking into the live oval.
/// </summary>
public static class PortalExitClipLaw
{
    public const float MinimumForwardBias = 0.05f;

    public static bool TryCreate(
        in PortalFrame destinationFrame,
        float forwardBias,
        out WorldClipPlane plane)
    {
        plane = default;
        if (!float.IsFinite(forwardBias) || forwardBias < 0f ||
            !destinationFrame.TryNormalize(out PortalFrame destination))
            return false;

        float bias = MathF.Max(MinimumForwardBias, forwardBias);
        Vector3 cutPoint = destination.Center + destination.Normal * bias;
        return WorldClipPlane.TryCreate(cutPoint, destination.Normal, out plane);
    }
}

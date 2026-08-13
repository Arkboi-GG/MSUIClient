using System.Numerics;

namespace MSUIClient.World.Portals;

/// <summary>
/// Pure two-sided camera mapping for a source aperture and the stock spell
/// landing pose supplied as its destination. The destination yaw is the
/// direction a player faces after arrival; it is not the outward normal of a
/// separately-authored exit aperture.
/// </summary>
public static class PortalPreviewCameraLaw
{
    public readonly record struct Mapping(
        PortalFrame ViewSource,
        PortalFrame Destination,
        int SourceSide)
    {
        public Vector3 TransformPoint(in Vector3 point) =>
            Destination.TransformPoint(point, ViewSource);

        public Vector3 TransformDirection(in Vector3 direction) =>
            Destination.TransformDirection(direction, ViewSource);
    }

    /// <summary>
    /// Resolve the source basis relative to the observing camera. From either
    /// side, the axis pointing into the film maps to destination forward. Both
    /// the normal and horizontal axis are reversed together when needed: that
    /// is an exact 180-degree turn around Up, rather than a reflection.
    /// </summary>
    public static bool TryCreate(
        in PortalFrame sourceFrame,
        in PortalFrame destinationFrame,
        in Vector3 sourceEye,
        in Vector3 sourceForward,
        out Mapping mapping)
    {
        mapping = default;
        if (!sourceFrame.TryNormalize(out PortalFrame source) ||
            !destinationFrame.TryNormalize(out PortalFrame destination) ||
            !Finite(sourceEye) || !Finite(sourceForward))
            return false;

        float signedEyeDistance = Vector3.Dot(sourceEye - source.Center, source.Normal);
        if (!float.IsFinite(signedEyeDistance)) return false;

        int side;
        if (MathF.Abs(signedEyeDistance) > 1e-4f)
        {
            side = signedEyeDistance > 0f ? 1 : -1;
        }
        else
        {
            // At the zero-depth film there is no positional side. Infer the
            // side the view is entering from; a tangent view uses a stable
            // front-side convention until position disambiguates it.
            float normalDirection = Vector3.Dot(sourceForward, source.Normal);
            if (!float.IsFinite(normalDirection)) return false;
            side = normalDirection > 1e-5f ? -1 : 1;
        }

        float turn = -side;
        PortalFrame viewSource = new(
            source.Center,
            source.Right * turn,
            source.Up,
            source.Normal * turn);
        mapping = new Mapping(viewSource, destination, side);
        return true;
    }

    private static bool Finite(in Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}

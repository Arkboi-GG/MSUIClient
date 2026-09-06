using System.Numerics;

namespace MSUIClient.Net;

/// <summary>Compose one wire transport-local rider pose into WoW world axes.</summary>
public static class TransportRiderLaw
{
    public readonly record struct WorldPose(Vector3 Position, float Orientation);

    public static WorldPose Compose(Vector3 transportPosition, float transportYaw,
        Vector3 localPosition, float localOrientation)
    {
        float sin = MathF.Sin(transportYaw);
        float cos = MathF.Cos(transportYaw);
        Vector3 rotated = new(
            localPosition.X * cos - localPosition.Y * sin,
            localPosition.X * sin + localPosition.Y * cos,
            localPosition.Z);
        return new WorldPose(transportPosition + rotated,
            NormalizeOrientation(localOrientation + transportYaw));
    }

    public static Vector3 ToLocal(Vector3 transportPosition, float transportYaw, Vector3 worldPosition)
    {
        Vector3 delta = worldPosition - transportPosition;
        float sin = MathF.Sin(-transportYaw), cos = MathF.Cos(-transportYaw);
        return new(delta.X * cos - delta.Y * sin, delta.X * sin + delta.Y * cos, delta.Z);
    }

    public static float NormalizeOrientation(float orientation)
    {
        float wrapped = orientation % (MathF.PI * 2f);
        return wrapped < 0f ? wrapped + MathF.PI * 2f : wrapped;
    }
}

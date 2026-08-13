using System.Numerics;

namespace MSUIClient.World;

/// <summary>
/// One normalized plane in absolute WoW world space. Points whose signed
/// distance is non-negative are retained. Renderers convert the equation to
/// camera-relative space before uploading it, matching their vertex paths.
/// </summary>
public readonly record struct WorldClipPlane(Vector3 Normal, float Offset)
{
    public bool IsValid =>
        Finite(Normal) && float.IsFinite(Offset) &&
        MathF.Abs(Normal.LengthSquared() - 1f) <= 0.001f;

    public float SignedDistance(in Vector3 worldPoint) =>
        Vector3.Dot(Normal, worldPoint) + Offset;

    /// <summary>
    /// Equation for a shader whose vertex positions have already had
    /// <paramref name="cameraOrigin"/> subtracted.
    /// </summary>
    public Vector4 RelativeEquation(in Vector3 cameraOrigin) => new(
        Normal,
        Offset + Vector3.Dot(Normal, cameraOrigin));

    public static bool TryCreate(
        in Vector3 point,
        in Vector3 normal,
        out WorldClipPlane plane)
    {
        plane = default;
        if (!Finite(point) || !Finite(normal)) return false;
        float lengthSquared = normal.LengthSquared();
        if (!float.IsFinite(lengthSquared) || lengthSquared <= 1e-12f) return false;

        Vector3 unit = normal / MathF.Sqrt(lengthSquared);
        float offset = -Vector3.Dot(unit, point);
        if (!float.IsFinite(offset)) return false;
        plane = new WorldClipPlane(unit, offset);
        return true;
    }

    private static bool Finite(in Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}

using System.Numerics;

namespace MSUIClient.World;

/// <summary>Pure build-5875 CWater0Ripple timing, depth and texgen laws.</summary>
public static class WaterFoamLaw
{
    public const float DefaultCollisionHeight = 2.0277777f;
    public const float WadeLineHeightFraction = 0.4f;
    public const float DepthGateHeightFraction = 2f;
    public const float RecordRiseFraction = 0.4f;
    public const float RecordFallFraction = 0.6f;
    public const float WakeDistanceYards = 0.625f;

    public static float CollisionGate(float collisionHeight)
    {
        float height = float.IsFinite(collisionHeight) && collisionHeight > 0f
            ? collisionHeight : DefaultCollisionHeight;
        return MathF.Max(DepthGateHeightFraction * height, 1f);
    }

    public static bool BeyondWadeLine(float? depth, float collisionHeight) =>
        depth is float d && float.IsFinite(d) &&
        d > WadeLineHeightFraction * (float.IsFinite(collisionHeight) && collisionHeight > 0f
            ? collisionHeight : DefaultCollisionHeight);

    public static bool Eligible(float? depth, float collisionHeight) =>
        depth is float d && float.IsFinite(d) && d > 0f && d < CollisionGate(collisionHeight);

    public static float RecordAlpha(float peak, float lifetime, float born, float now)
    {
        float age = (now - born) / MathF.Max(lifetime, 0.001f);
        if (age < 0f || age >= 1f) return 0f;
        return peak * (age <= RecordRiseFraction
            ? age / RecordRiseFraction
            : 1f - (age - RecordRiseFraction) / RecordFallFraction);
    }

    /// <summary>
    /// A foam record is alive for its authored lifetime, including its birth instant.
    /// RecordAlpha is deliberately zero at birth because the record fades in; zero alpha is
    /// therefore not a retirement condition.
    /// </summary>
    public static bool RecordAlive(float lifetime, float born, float now)
    {
        if (!float.IsFinite(lifetime) || !float.IsFinite(born) || !float.IsFinite(now) ||
            lifetime <= 0f)
            return false;
        float age = now - born;
        return age >= 0f && age < lifetime;
    }

    public static float RecordSize(float size0, float growth, float born, float now) =>
        size0 + growth * MathF.Max(0f, now - born);

    public static float WakeCooldown(float speed, float jitter) =>
        Math.Clamp(jitter, 0.9f, 1.1f) * WakeDistanceYards /
        Math.Clamp(speed, 0.1f, 20f);

    /// <summary>
    /// Reference texgen: U across travel, V against travel. The low-V wake apex lands ahead of
    /// the mover and its arms trail behind as the record expands.
    /// </summary>
    public static Vector2 TexGen(Vector2 center, float heading, float size, Vector2 point)
    {
        Vector2 d = point - center;
        (float sin, float cos) = MathF.SinCos(heading);
        float inv = 1f / (2f * MathF.Max(size, 0.001f));
        return new Vector2(
            (-sin * d.X + cos * d.Y) * inv + 0.5f,
            (-cos * d.X - sin * d.Y) * inv + 0.5f);
    }
}

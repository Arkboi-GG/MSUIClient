using System.Numerics;

namespace MSUIClient.World.Spells;

/// <summary>
/// Pure particle head/tail expansion shared by the production renderer and mounted-data checks.
/// A tail is authored in seconds, follows the particle's current velocity, and becomes a plain
/// camera billboard only when its screen projection is too short to define a stable width axis.
/// Particle quads are always two-sided. Projected tail axes can legitimately have the opposite
/// winding from head billboards, so enabling back-face culling makes tail-only emitters disappear.
/// </summary>
public static class SpellParticleTrailLaw
{
    // This is a renderer contract, not an asset flag. Benilla uses cull_mode: None for every
    // particle material. Keep production GL state and the motion validator wired to this value.
    public static bool CullBackFaces => false;
    public const float DegenerateProjectedLengthSquared = 7.7e-4f;

    public readonly record struct Quad(Vector3 Centre, Vector3 AxisRight, Vector3 AxisUp,
        Vector3 Tail, bool Streak);

    public static bool DrawsHead(byte headOrTail) => headOrTail != 1;
    public static bool DrawsTail(byte headOrTail) => headOrTail >= 1;

    public static Quad TailQuad(Vector3 centre, Vector3 velocity, float half,
        Vector3 cameraRight, Vector3 cameraUp, float authoredTailSeconds,
        float particleAge, bool clampToParticleAge)
    {
        float tailSeconds = float.IsFinite(authoredTailSeconds)
            ? Math.Max(0f, authoredTailSeconds) : 0f;
        if (clampToParticleAge)
            tailSeconds = MathF.Min(tailSeconds,
                float.IsFinite(particleAge) ? Math.Max(0f, particleAge) : 0f);

        Vector3 tail = -velocity * tailSeconds;
        float tr = Vector3.Dot(tail, cameraRight);
        float tu = Vector3.Dot(tail, cameraUp);
        float projectedLengthSquared = tr * tr + tu * tu;
        if (!float.IsFinite(projectedLengthSquared) ||
            projectedLengthSquared < DegenerateProjectedLengthSquared)
            return new Quad(centre, cameraRight * half, cameraUp * half, tail, Streak: false);

        Vector3 perpendicular = (cameraUp * tr - cameraRight * tu) *
            (half / MathF.Sqrt(projectedLengthSquared));
        return new Quad(centre + tail * .5f, perpendicular, tail * .5f, tail, Streak: true);
    }
}

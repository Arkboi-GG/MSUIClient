using System.Numerics;
using MSUIClient.Formats;

namespace MSUIClient.World.Spells;

/// <summary>
/// The live frame of one particle pool, EXACTLY as the emission kernel sees it this frame:
/// the evaluated emitter origin B_i(t), the linear birth frame (bone x effect root x caster
/// attachment), the effect root anchor and the ten scalars. Published by
/// <see cref="SpellParticleSystem.EmitterFrames"/> for the creator's gizmo layer, so the
/// drawing cannot disagree with the particles.
/// </summary>
public readonly record struct SpellEmitterFrame(
    string Path,
    int EmitterIndex,
    Vector3 Origin,
    Matrix4x4 LinearFrame,
    Vector3 RootAnchor,
    ParticleShape Shape,
    uint Flags,
    bool ModelSpace,
    float Speed,
    float SpeedVariation,
    float VerticalRange,
    float HorizontalRange,
    float Gravity,
    float Lifespan,
    float Rate,
    float AreaLength,
    float AreaWidth,
    float ZSource,
    int LiveParticles,
    string TexturePath);

/// <summary>
/// Pure geometry for the creator's spatial view (shared_docs/SPELL_CREATOR_IDE.md §2.3): the
/// reference grid around the acting body and one gizmo per live emitter. No GL, no GameLoop -
/// it appends <see cref="GizmoLine"/>s to a list the renderer draws.
///
/// THE ONE LAW HERE: every emitter shape and ray goes through the SAME kernel-to-world mapping
/// the emission kernel uses (Spawn: localPos = Swap(Rot90Z(posZ)); world = origin +
/// TransformNormal(local, frame)). Kernel space is the emitter's Z-up local frame: kernel X
/// spans the plane WIDTH, kernel Y the plane LENGTH, kernel Z is the mean emission direction.
/// The sign of speed is applied, because Cone of Cold's clouds travel at -27.8 and a gizmo that
/// ignored the sign would point exactly the wrong way.
/// </summary>
public static class SpellEmitterGizmoLaw
{
    public const float DefaultMinorYards = 0.25f;
    public const float SixthYardMinor = 1f / 6f;
    public const float MajorYards = 1f;
    public const float ExtentYards = 5f;
    public const float TriadYards = 0.5f;
    public const int RingSegments = 24;
    public const int ReachEdgeSamples = 6;
    public const int ArcSamples = 12;

    // World grid axes: the acting body's facing frame.
    public static readonly Vector4 ForwardAxis = new(1f, 0.30f, 0.30f, 1f);   // red
    public static readonly Vector4 LeftAxis = new(0.35f, 1f, 0.35f, 1f);      // green
    public static readonly Vector4 UpAxis = new(0.40f, 0.60f, 1f, 1f);        // blue
    public static readonly Vector4 MajorLine = new(0.80f, 0.80f, 0.80f, 0.45f);
    public static readonly Vector4 MinorLine = new(0.60f, 0.60f, 0.60f, 0.18f);
    public static readonly Vector4 EmitAxis = new(1f, 1f, 1f, 1f);

    private static Vector3 Swap(Vector3 v) => new(v.X, v.Z, -v.Y);
    private static Vector3 Rot90Z(Vector3 v) => new(-v.Y, v.X, v.Z);

    /// <summary>A kernel-space vector (Z-up emitter local) as a world vector through the live
    /// linear frame - the emission kernel's own mapping, scale included.</summary>
    public static Vector3 KernelToWorldVector(Vector3 kernel, Matrix4x4 frame)
        => Vector3.TransformNormal(Swap(Rot90Z(kernel)), frame);

    public static Vector3 KernelToWorldPoint(Vector3 kernel, Vector3 origin, Matrix4x4 frame)
        => origin + KernelToWorldVector(kernel, frame);

    /// <summary>The kernel direction a particle takes at zero spread: the emission axis.</summary>
    public static Vector3 MeanKernelDirection(in SpellEmitterFrame f)
    {
        // zSource steers births toward/away from (0, 0, zSource); from the origin that is straight
        // along the axis, away from the source point.
        if (f.ZSource != 0f) return new Vector3(0f, 0f, -MathF.Sign(f.ZSource));
        if (f.Shape == ParticleShape.Sphere)
            return (f.Flags & 0x100) != 0 ? Vector3.UnitZ : Vector3.UnitX;   // sphere_up, else radial
        return Vector3.UnitZ;
    }

    private static Vector3 PlaneDirection(float theta, float phi)
    {
        float st = MathF.Sin(theta), ct = MathF.Cos(theta);
        return new Vector3(st * MathF.Cos(phi), st * MathF.Sin(phi), ct);
    }

    private static Vector3 Shell(float lat, float lon)
    {
        float clat = MathF.Cos(lat);
        return new Vector3(clat * MathF.Cos(lon), clat * MathF.Sin(lon), MathF.Sin(lat));
    }

    /// <summary>Kernel birth point and direction for one (a, b) sample of the spread rectangle:
    /// plane emitters use (theta, phi) about the axis, sphere emitters the (lat, lon) shell.</summary>
    public static (Vector3 Birth, Vector3 Direction) Sample(in SpellEmitterFrame f, float a, float b)
    {
        if (f.Shape == ParticleShape.Sphere)
        {
            Vector3 shell = Shell(a, b);
            Vector3 birth = f.AreaLength * shell;
            Vector3 direction = f.ZSource != 0f
                ? SafeNormalize(birth - new Vector3(0f, 0f, f.ZSource), Vector3.UnitZ)
                : (f.Flags & 0x100) != 0 ? Vector3.UnitZ : shell;
            return (birth, direction);
        }
        Vector3 planeDirection = f.ZSource != 0f
            ? new Vector3(0f, 0f, -MathF.Sign(f.ZSource))
            : PlaneDirection(a, b);
        return (Vector3.Zero, planeDirection);
    }

    /// <summary>Where a particle born at <paramref name="kernelBirth"/> travelling along
    /// <paramref name="kernelDirection"/> is after <paramref name="t"/> seconds - the kernel's
    /// speed (signed) and gravity, drag ignored. Gravity acts on world -Z for anchored clouds and
    /// on model -Y (the emission axis, inverted) for model-space clouds, as in AdvanceParent.</summary>
    public static Vector3 Trajectory(in SpellEmitterFrame f, Vector3 kernelBirth, Vector3 kernelDirection, float t)
    {
        Vector3 local = Swap(Rot90Z(kernelBirth)) + Swap(Rot90Z(kernelDirection)) * (f.Speed * t);
        if (f.ModelSpace)
        {
            local.Y -= 0.5f * f.Gravity * t * t;
            return f.Origin + Vector3.TransformNormal(local, f.LinearFrame);
        }
        Vector3 world = Vector3.TransformNormal(local, f.LinearFrame);
        world.Z -= 0.5f * f.Gravity * t * t;
        return f.Origin + world;
    }

    // ── the reference grid ───────────────────────────────────────────────────

    /// <summary>Three planes through the acting body's feet in its facing frame: floor (forward x
    /// left), side (forward x up) and front (left x up). Vertical planes run from the feet UP only -
    /// five yards of grid below the floor helps nobody. The facing axes are drawn bold on top.</summary>
    public static void Grid(List<GizmoLine> lines, Vector3 feet, float yaw, float minor, float major,
        float extent, bool floor, bool side, bool front)
    {
        var forward = new Vector3(MathF.Cos(yaw), MathF.Sin(yaw), 0f);
        var left = new Vector3(-MathF.Sin(yaw), MathF.Cos(yaw), 0f);
        Vector3 up = Vector3.UnitZ;

        if (floor) Plane(lines, feet, forward, left, -extent, extent, -extent, extent, minor, major);
        if (side) Plane(lines, feet, forward, up, -extent, extent, 0f, extent, minor, major);
        if (front) Plane(lines, feet, left, up, -extent, extent, 0f, extent, minor, major);

        lines.Add(new GizmoLine(feet, feet + forward * extent, ForwardAxis));
        lines.Add(new GizmoLine(feet, feet + left * extent, LeftAxis));
        lines.Add(new GizmoLine(feet, feet + up * extent, UpAxis));
    }

    private static void Plane(List<GizmoLine> lines, Vector3 origin, Vector3 a, Vector3 b,
        float aMin, float aMax, float bMin, float bMax, float minor, float major)
    {
        if (minor <= 1e-3f) return;
        int bLo = (int)MathF.Ceiling(bMin / minor - 1e-3f), bHi = (int)MathF.Floor(bMax / minor + 1e-3f);
        for (int i = bLo; i <= bHi; i++)
        {
            float t = i * minor;
            lines.Add(new GizmoLine(origin + b * t + a * aMin, origin + b * t + a * aMax,
                IsMajor(t, major) ? MajorLine : MinorLine));
        }
        int aLo = (int)MathF.Ceiling(aMin / minor - 1e-3f), aHi = (int)MathF.Floor(aMax / minor + 1e-3f);
        for (int i = aLo; i <= aHi; i++)
        {
            float t = i * minor;
            lines.Add(new GizmoLine(origin + a * t + b * bMin, origin + a * t + b * bMax,
                IsMajor(t, major) ? MajorLine : MinorLine));
        }
    }

    public static readonly Vector4 LatticeMajor = new(0.85f, 0.85f, 0.85f, 0.30f);
    public static readonly Vector4 LatticeMinor = new(0.60f, 0.60f, 0.60f, 0.09f);

    /// <summary>
    /// The full cube lattice the owner asked for: minor cells across +-extent forward/left and
    /// 0..extent up, in the acting body's facing frame, with a brighter cage on the yard
    /// boundaries. "The character model is the priority": every line is clipped against a
    /// vertical cylinder around the body (<paramref name="pocketRadius"/>, <paramref name="pocketHeight"/>)
    /// so the model stands in a clear pocket instead of behind a fence of lines.
    /// </summary>
    public static void Lattice(List<GizmoLine> lines, Vector3 feet, float yaw, float minor, float major,
        float extent, float pocketRadius, float pocketHeight)
    {
        if (minor <= 1e-3f || extent <= 0f) return;
        var forward = new Vector3(MathF.Cos(yaw), MathF.Sin(yaw), 0f);
        var left = new Vector3(-MathF.Sin(yaw), MathF.Cos(yaw), 0f);
        Vector3 up = Vector3.UnitZ;
        int n = (int)MathF.Round(extent / minor);
        float reach = n * minor;

        // Along forward: one line per (left, up) cell corner.
        for (int iy = -n; iy <= n; iy++)
            for (int iz = 0; iz <= n; iz++)
            {
                float y = iy * minor, z = iz * minor;
                Vector3 o = feet + left * y + up * z;
                AddOutsidePocket(lines, o - forward * reach, o + forward * reach,
                    IsMajor(y, major) && IsMajor(z, major) ? LatticeMajor : LatticeMinor,
                    feet, pocketRadius, pocketHeight);
            }
        // Along left: one line per (forward, up) corner.
        for (int ix = -n; ix <= n; ix++)
            for (int iz = 0; iz <= n; iz++)
            {
                float x = ix * minor, z = iz * minor;
                Vector3 o = feet + forward * x + up * z;
                AddOutsidePocket(lines, o - left * reach, o + left * reach,
                    IsMajor(x, major) && IsMajor(z, major) ? LatticeMajor : LatticeMinor,
                    feet, pocketRadius, pocketHeight);
            }
        // Along up: one line per (forward, left) corner, from the floor up.
        for (int ix = -n; ix <= n; ix++)
            for (int iy = -n; iy <= n; iy++)
            {
                float x = ix * minor, y = iy * minor;
                Vector3 o = feet + forward * x + left * y;
                AddOutsidePocket(lines, o, o + up * reach,
                    IsMajor(x, major) && IsMajor(y, major) ? LatticeMajor : LatticeMinor,
                    feet, pocketRadius, pocketHeight);
            }

        lines.Add(new GizmoLine(feet, feet + forward * reach, ForwardAxis));
        lines.Add(new GizmoLine(feet, feet + left * reach, LeftAxis));
        lines.Add(new GizmoLine(feet, feet + up * reach, UpAxis));
    }

    /// <summary>Add the parts of segment a-b that lie OUTSIDE the vertical cylinder of
    /// <paramref name="radius"/> and <paramref name="height"/> standing on <paramref name="feet"/>.</summary>
    public static void AddOutsidePocket(List<GizmoLine> lines, Vector3 a, Vector3 b, Vector4 color,
        Vector3 feet, float radius, float height)
    {
        if (radius <= 0f || height <= 0f)
        {
            lines.Add(new GizmoLine(a, b, color));
            return;
        }
        // Parameter interval [t0, t1] of the segment inside the circle (XY).
        var p = new Vector2(a.X - feet.X, a.Y - feet.Y);
        var d = new Vector2(b.X - a.X, b.Y - a.Y);
        float qa = Vector2.Dot(d, d), qb = 2f * Vector2.Dot(p, d), qc = Vector2.Dot(p, p) - radius * radius;
        float t0, t1;
        if (qa < 1e-8f)
        {
            if (qc > 0f) { lines.Add(new GizmoLine(a, b, color)); return; }   // vertical, outside
            t0 = 0f; t1 = 1f;                                                  // vertical, inside
        }
        else
        {
            float disc = qb * qb - 4f * qa * qc;
            if (disc <= 0f) { lines.Add(new GizmoLine(a, b, color)); return; }
            float sq = MathF.Sqrt(disc);
            t0 = (-qb - sq) / (2f * qa);
            t1 = (-qb + sq) / (2f * qa);
            if (t1 <= 0f || t0 >= 1f) { lines.Add(new GizmoLine(a, b, color)); return; }
            t0 = MathF.Max(t0, 0f);
            t1 = MathF.Min(t1, 1f);
        }
        // Intersect with the height band.
        float zLo = feet.Z - 0.05f, zHi = feet.Z + height;
        float dz = b.Z - a.Z;
        float u0 = 0f, u1 = 1f;
        if (MathF.Abs(dz) < 1e-6f)
        {
            if (a.Z < zLo || a.Z > zHi) { lines.Add(new GizmoLine(a, b, color)); return; }
        }
        else
        {
            float ta = (zLo - a.Z) / dz, tb = (zHi - a.Z) / dz;
            u0 = MathF.Min(ta, tb);
            u1 = MathF.Max(ta, tb);
        }
        float s0 = MathF.Max(t0, u0), s1 = MathF.Min(t1, u1);
        if (s1 <= s0) { lines.Add(new GizmoLine(a, b, color)); return; }
        if (s0 > 0f) lines.Add(new GizmoLine(a, Vector3.Lerp(a, b, s0), color));
        if (s1 < 1f) lines.Add(new GizmoLine(Vector3.Lerp(a, b, s1), b, color));
    }

    private static bool IsMajor(float t, float major)
        => major > 1e-3f && MathF.Abs(t / major - MathF.Round(t / major)) < 1e-3f;

    // ── one emitter ──────────────────────────────────────────────────────────

    /// <summary>
    /// Origin cross, kernel triad (emit axis white, length axis red, width axis green), the birth
    /// shape, the reach (boundary rays of the spread at t = lifespan, their end-cap loop, and the
    /// mean ray's gravity arc) and the parentage line to the effect root. Bold when highlighted;
    /// an emitter whose rate is zero THIS instant (a gated burst between pours, Cleave's animated
    /// rate at its zero first key) draws dimmer still, so idle reads as idle.
    /// </summary>
    public static void Emitter(List<GizmoLine> lines, in SpellEmitterFrame f, Vector4 identity,
        bool highlighted, bool showReach)
    {
        float alpha = highlighted ? 1f : 0.45f;
        if (f.Rate <= 0f) alpha *= 0.6f;
        Vector4 color = identity with { W = alpha };
        Vector4 dim = identity with { W = alpha * 0.5f };
        Vector3 o = f.Origin;
        Matrix4x4 m = f.LinearFrame;

        // Origin: a small world-axis cross, larger when bold.
        float cross = highlighted ? 0.12f : 0.08f;
        lines.Add(new GizmoLine(o - Vector3.UnitX * cross, o + Vector3.UnitX * cross, color));
        lines.Add(new GizmoLine(o - Vector3.UnitY * cross, o + Vector3.UnitY * cross, color));
        lines.Add(new GizmoLine(o - Vector3.UnitZ * cross, o + Vector3.UnitZ * cross, color));

        // Kernel triad: width (X), length (Y), and the emission axis, in the live frame.
        float triad = highlighted ? TriadYards : TriadYards * 0.6f;
        lines.Add(new GizmoLine(o, KernelToWorldPoint(Vector3.UnitX * triad * 0.6f, o, m), LeftAxis with { W = alpha }));
        lines.Add(new GizmoLine(o, KernelToWorldPoint(Vector3.UnitY * triad * 0.6f, o, m), ForwardAxis with { W = alpha }));
        Vector3 emit = MeanKernelDirection(f);
        Vector3 emitTip = KernelToWorldPoint(emit * triad, o, m);
        lines.Add(new GizmoLine(o, emitTip, EmitAxis with { W = alpha }));
        // Arrowhead on the emission axis: two barbs back toward the origin.
        Vector3 back = o - emitTip;
        if (back.LengthSquared() > 1e-8f)
        {
            Vector3 dir = Vector3.Normalize(back);
            Vector3 side = Vector3.Cross(dir, MathF.Abs(dir.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX);
            side = SafeNormalize(side, Vector3.UnitX) * triad * 0.12f;
            Vector3 barbBase = emitTip + dir * triad * 0.25f;
            lines.Add(new GizmoLine(emitTip, barbBase + side, EmitAxis with { W = alpha }));
            lines.Add(new GizmoLine(emitTip, barbBase - side, EmitAxis with { W = alpha }));
        }

        // Birth shape.
        if (f.Shape == ParticleShape.Sphere)
        {
            Rings(lines, o, m, f.AreaLength, color);
            if (f.AreaWidth > f.AreaLength + 1e-3f) Rings(lines, o, m, f.AreaWidth, dim);
        }
        else if (f.Shape != ParticleShape.Spline)
        {
            float hw = f.AreaWidth * 0.5f, hl = f.AreaLength * 0.5f;
            if (hw > 1e-4f || hl > 1e-4f)
            {
                Vector3 c0 = KernelToWorldPoint(new Vector3(-hw, -hl, 0f), o, m);
                Vector3 c1 = KernelToWorldPoint(new Vector3(hw, -hl, 0f), o, m);
                Vector3 c2 = KernelToWorldPoint(new Vector3(hw, hl, 0f), o, m);
                Vector3 c3 = KernelToWorldPoint(new Vector3(-hw, hl, 0f), o, m);
                lines.Add(new GizmoLine(c0, c1, color));
                lines.Add(new GizmoLine(c1, c2, color));
                lines.Add(new GizmoLine(c2, c3, color));
                lines.Add(new GizmoLine(c3, c0, color));
            }
        }

        // Reach: where the first particles are after their lifetime.
        if (showReach && MathF.Abs(f.Speed) > 1e-4f && f.Lifespan > 1e-4f && f.Shape != ParticleShape.Spline)
            Reach(lines, f, color, dim, alpha);

        // Parentage: the effect root this emitter hangs from (the caster attachment).
        if (Vector3.DistanceSquared(o, f.RootAnchor) > 1e-6f)
            lines.Add(new GizmoLine(o, f.RootAnchor, dim));
    }

    private static void Rings(List<GizmoLine> lines, Vector3 o, Matrix4x4 m, float radius, Vector4 color)
    {
        if (radius <= 1e-4f) return;
        Ring(lines, o, m, radius, (c, s) => new Vector3(c, s, 0f), color);
        Ring(lines, o, m, radius, (c, s) => new Vector3(c, 0f, s), color);
        Ring(lines, o, m, radius, (c, s) => new Vector3(0f, c, s), color);
    }

    private static void Ring(List<GizmoLine> lines, Vector3 o, Matrix4x4 m, float radius,
        Func<float, float, Vector3> basis, Vector4 color)
    {
        Vector3 previous = default;
        for (int i = 0; i <= RingSegments; i++)
        {
            float angle = i * MathF.PI * 2f / RingSegments;
            Vector3 point = KernelToWorldPoint(basis(MathF.Cos(angle), MathF.Sin(angle)) * radius, o, m);
            if (i > 0) lines.Add(new GizmoLine(previous, point, color));
            previous = point;
        }
    }

    private static void Reach(List<GizmoLine> lines, SpellEmitterFrame f, Vector4 color, Vector4 dim, float alpha)
    {
        float v = f.VerticalRange, h = f.HorizontalRange, life = f.Lifespan;

        // Walk the boundary of the (a in +-v, b in +-h) spread rectangle: four edges, sampled.
        var tips = new List<Vector3>(ReachEdgeSamples * 4);
        var births = new List<Vector3>(ReachEdgeSamples * 4);
        void Edge(float a0, float b0, float a1, float b1)
        {
            for (int i = 0; i < ReachEdgeSamples; i++)
            {
                float t = i / (float)ReachEdgeSamples;
                (Vector3 birth, Vector3 direction) = Sample(f, a0 + (a1 - a0) * t, b0 + (b1 - b0) * t);
                births.Add(KernelToWorldPoint(birth, f.Origin, f.LinearFrame));
                tips.Add(Trajectory(f, birth, direction, life));
            }
        }
        Edge(v, -h, v, h);
        Edge(v, h, -v, h);
        Edge(-v, h, -v, -h);
        Edge(-v, -h, v, -h);

        bool spread = v > 1e-4f || h > 1e-4f;
        if (spread)
        {
            // End-cap loop across the boundary tips, and a ray to every corner and edge midpoint.
            for (int i = 0; i < tips.Count; i++)
                lines.Add(new GizmoLine(tips[i], tips[(i + 1) % tips.Count], dim));
            int stride = Math.Max(1, ReachEdgeSamples / 2);
            for (int i = 0; i < tips.Count; i += stride)
                lines.Add(new GizmoLine(births[i], tips[i], dim));
        }

        // The mean ray: the emission axis under gravity, drawn bold as an arc.
        (Vector3 meanBirth, Vector3 meanDirection) = Sample(f, 0f, 0f);
        Vector3 previous = KernelToWorldPoint(meanBirth, f.Origin, f.LinearFrame);
        for (int i = 1; i <= ArcSamples; i++)
        {
            Vector3 point = Trajectory(f, meanBirth, meanDirection, life * i / ArcSamples);
            lines.Add(new GizmoLine(previous, point, color));
            previous = point;
        }
        // Tip marker so "where it ends" is unmistakable even without the loop.
        float tick = 0.06f;
        lines.Add(new GizmoLine(previous - Vector3.UnitX * tick, previous + Vector3.UnitX * tick, EmitAxis with { W = alpha }));
        lines.Add(new GizmoLine(previous - Vector3.UnitY * tick, previous + Vector3.UnitY * tick, EmitAxis with { W = alpha }));
        lines.Add(new GizmoLine(previous - Vector3.UnitZ * tick, previous + Vector3.UnitZ * tick, EmitAxis with { W = alpha }));
    }

    private static Vector3 SafeNormalize(Vector3 v, Vector3 fallback)
        => v.LengthSquared() > 1e-12f ? Vector3.Normalize(v) : fallback;
}

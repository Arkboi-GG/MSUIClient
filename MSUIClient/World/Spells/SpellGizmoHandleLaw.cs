using System.Numerics;

namespace MSUIClient.World.Spells;

/// <summary>What a handle grabs.</summary>
public enum GizmoTargetKind { Emitter, Ribbon, Bone }

/// <summary>
/// The pieces of a manipulator. Translate: a centre square (drag in the screen plane), three
/// axis arrows and three plane squares. Emitter shape: the reach tip (speed), the birth
/// rectangle's edge marks (area length / width) or the sphere radius mark. Bone: three rings
/// (rotate about the bone's own axes) plus the translate set when the bone has a translation
/// track.
/// </summary>
public enum GizmoHandleKind
{
    Center, AxisX, AxisY, AxisZ, PlaneXY, PlaneXZ, PlaneYZ,
    Speed, AreaLength, AreaWidth, Radius,
    RingX, RingY, RingZ,
}

/// <summary>
/// One grabbable piece in absolute world space. <see cref="Anchor"/> is the origin drags are
/// measured from; <see cref="Axis"/> is the drag line (axis / speed / area handles), the plane
/// normal (plane and centre handles) or the ring normal; <see cref="Tangent"/> is the first
/// in-plane direction of a plane square, or the ring's zero-angle reference. <see cref="Pick"/>
/// is the screen-pick geometry (one point, a segment, or a ring polyline). <see cref="Scale"/>
/// converts a world distance along the axis back into the target's unit (yards per kernel unit,
/// or yards per unit of speed).
/// </summary>
public readonly record struct GizmoHandle(
    GizmoTargetKind Target, string ModelPath, int Index, GizmoHandleKind Kind,
    Vector3 Anchor, Vector3 Axis, Vector3 Tangent, Vector3 Point, Vector3[] Pick, float Scale)
{
    public bool IsTranslate => Kind is GizmoHandleKind.Center or GizmoHandleKind.AxisX or GizmoHandleKind.AxisY
        or GizmoHandleKind.AxisZ or GizmoHandleKind.PlaneXY or GizmoHandleKind.PlaneXZ or GizmoHandleKind.PlaneYZ;
    public bool IsRing => Kind is GizmoHandleKind.RingX or GizmoHandleKind.RingY or GizmoHandleKind.RingZ;
    public bool IsAxisDrag => Kind is GizmoHandleKind.AxisX or GizmoHandleKind.AxisY or GizmoHandleKind.AxisZ
        or GizmoHandleKind.Speed or GizmoHandleKind.AreaLength or GizmoHandleKind.AreaWidth or GizmoHandleKind.Radius;
    public bool IsPlaneDrag => Kind is GizmoHandleKind.Center or GizmoHandleKind.PlaneXY or GizmoHandleKind.PlaneXZ
        or GizmoHandleKind.PlaneYZ;
}

/// <summary>
/// Pure geometry and arithmetic for the creator's drag handles (shared_docs/SPELL_CREATOR_IDE.md
/// §2.10). No GL, no ImGui, no GameLoop: the caller supplies the mouse ray and a world-to-screen
/// projector, this builds handles, picks one, turns a ray into a parameter and turns a world
/// delta into the FILE's frame.
///
/// THE TWO FRAME FACTS EVERYTHING HERE RESTS ON (proven by the emitter lab against M2Reader):
///   1. The runtime's model space is the file's raw WoW space with (x, y, z) -> (x, z, -y):
///      M2Reader swaps every position, pivot, translation key and quaternion imaginary part on
///      parse. A world delta comes back through the live linear frame into MODEL space, and
///      <see cref="Unswap"/> takes it the last step into the bytes the patchers write.
///   2. The animator composes a bone as S * R * T(translation) * parent, so the bone's world
///      linear part is R_local * P (P = the parent's world linear part). Rotating the bone's
///      WORLD orientation by R_w is therefore R_new = boneWorldLinear * R_w * P^-1 - the local
///      rotation the file must hold for the picture to turn by exactly R_w about the axis the
///      user dragged.
/// </summary>
public static class SpellGizmoHandleLaw
{
    // Screen-space sizes; the caller converts through WorldPerPixel so a handle is the same
    // size on screen whatever the camera distance.
    public const float AxisPixels = 84f;
    public const float PlaneOffsetPixels = 30f;
    public const float PlaneSidePixels = 12f;
    public const float CenterPixels = 7f;
    public const float RingPixels = 48f;
    public const float MarkPixels = 8f;
    public const float PickPixels = 11f;
    public const int RingSamples = 36;

    public static readonly Vector4 HoverColour = new(1f, 0.92f, 0.35f, 1f);
    public static readonly Vector4 ActiveColour = new(1f, 1f, 1f, 1f);
    public static readonly Vector4 MarkColour = new(1f, 0.55f, 0.95f, 1f);

    // ── frame facts ──────────────────────────────────────────────────────────

    /// <summary>Raw file vector (WoW Z-up) to the runtime's model space (M2Reader).</summary>
    public static Vector3 Swap(Vector3 raw) => new(raw.X, raw.Z, -raw.Y);

    /// <summary>Runtime model-space vector back to the file's raw frame.</summary>
    public static Vector3 Unswap(Vector3 model) => new(model.X, -model.Z, model.Y);

    /// <summary>Raw file quaternion (x, y, z, w) to the runtime's (M2Reader: (x, z, -y, w)).</summary>
    public static Vector4 RawToModelQuaternion(Vector4 q) => new(q.X, q.Z, -q.Y, q.W);

    public static Vector4 ModelToRawQuaternion(Vector4 q) => new(q.X, -q.Z, q.Y, q.W);

    /// <summary>World yards per screen pixel at a given eye distance.</summary>
    public static float WorldPerPixel(float distance, float verticalFovRadians, float viewportHeight)
        => viewportHeight <= 1f
            ? 0.01f
            : 2f * MathF.Max(distance, 0.05f) * MathF.Tan(verticalFovRadians * 0.5f) / viewportHeight;

    /// <summary>The 3x3 part of a row-vector matrix, translation dropped.</summary>
    public static Matrix4x4 Linear(Matrix4x4 m)
    {
        m.M41 = 0f; m.M42 = 0f; m.M43 = 0f; m.M44 = 1f;
        m.M14 = 0f; m.M24 = 0f; m.M34 = 0f;
        return m;
    }

    public static Vector3 Row(in Matrix4x4 m, int row) => row switch
    {
        0 => new Vector3(m.M11, m.M12, m.M13),
        1 => new Vector3(m.M21, m.M22, m.M23),
        _ => new Vector3(m.M31, m.M32, m.M33),
    };

    /// <summary>Gram-Schmidt on the rows: the nearest pure rotation to a linear frame that may
    /// carry uniform scale and float drift.</summary>
    public static Matrix4x4 Orthonormalize(Matrix4x4 m)
    {
        Vector3 x = SafeNormalize(Row(m, 0), Vector3.UnitX);
        Vector3 y = Row(m, 1) - x * Vector3.Dot(Row(m, 1), x);
        y = SafeNormalize(y, Vector3.UnitY);
        Vector3 z = Vector3.Cross(x, y);
        return new Matrix4x4(x.X, x.Y, x.Z, 0f, y.X, y.Y, y.Z, 0f, z.X, z.Y, z.Z, 0f, 0f, 0f, 0f, 1f);
    }

    // ── building ─────────────────────────────────────────────────────────────

    /// <summary>The translate set at <paramref name="anchor"/> in the given axes (the lattice's
    /// forward / left / up so a drag follows the grid). <paramref name="unit"/> is yards per pixel.</summary>
    public static void Translate(List<GizmoHandle> handles, GizmoTargetKind target, string path, int index,
        Vector3 anchor, Vector3 forward, Vector3 left, Vector3 up, float unit)
    {
        float axisLength = AxisPixels * unit;
        handles.Add(new GizmoHandle(target, path, index, GizmoHandleKind.Center, anchor, up, forward, anchor,
            [anchor], 1f));
        Axis(GizmoHandleKind.AxisX, forward);
        Axis(GizmoHandleKind.AxisY, left);
        Axis(GizmoHandleKind.AxisZ, up);
        Plane(GizmoHandleKind.PlaneXY, forward, left, up);
        Plane(GizmoHandleKind.PlaneXZ, forward, up, left);
        Plane(GizmoHandleKind.PlaneYZ, left, up, forward);

        void Axis(GizmoHandleKind kind, Vector3 axis)
        {
            Vector3 tip = anchor + axis * axisLength;
            handles.Add(new GizmoHandle(target, path, index, kind, anchor, axis, Vector3.Zero, tip,
                [anchor + axis * axisLength * 0.3f, tip], 1f));
        }

        void Plane(GizmoHandleKind kind, Vector3 a, Vector3 b, Vector3 normal)
        {
            Vector3 centre = anchor + (a + b) * PlaneOffsetPixels * unit;
            handles.Add(new GizmoHandle(target, path, index, kind, anchor, normal, a, centre, [centre], 1f));
        }
    }

    /// <summary>The emitter's shape handles: the reach tip (speed) and the birth-shape marks
    /// (plane length/width edges, sphere radii).</summary>
    public static void EmitterShape(List<GizmoHandle> handles, SpellEmitterFrame f, string path, float unit,
        bool showReach)
    {
        Vector3 o = f.Origin;
        Matrix4x4 m = f.LinearFrame;
        if (showReach && MathF.Abs(f.Speed) > 1e-4f && f.Lifespan > 1e-4f && f.Shape != Formats.ParticleShape.Spline)
        {
            (Vector3 birth, Vector3 direction) = SpellEmitterGizmoLaw.Sample(f, 0f, 0f);
            Vector3 tip = SpellEmitterGizmoLaw.Trajectory(f, birth, direction, f.Lifespan);
            Vector3 worldDirection = SpellEmitterGizmoLaw.KernelToWorldVector(direction, m);
            float stretch = worldDirection.Length();   // yards per kernel unit along the emission axis
            if (stretch > 1e-6f)
            {
                Vector3 axis = worldDirection / stretch * MathF.Sign(f.Speed);
                Vector3 start = SpellEmitterGizmoLaw.KernelToWorldPoint(birth, o, m);
                handles.Add(new GizmoHandle(GizmoTargetKind.Emitter, path, f.EmitterIndex, GizmoHandleKind.Speed,
                    start, axis, Vector3.Zero, tip, [tip], stretch * f.Lifespan));
            }
        }

        if (f.Shape == Formats.ParticleShape.Sphere)
        {
            Mark(GizmoHandleKind.Radius, Vector3.UnitX, f.AreaLength);
            if (f.AreaWidth > 1e-4f) Mark(GizmoHandleKind.AreaWidth, Vector3.UnitY, f.AreaWidth);
        }
        else if (f.Shape != Formats.ParticleShape.Spline)
        {
            Mark(GizmoHandleKind.AreaLength, Vector3.UnitY, f.AreaLength * 0.5f);
            Mark(GizmoHandleKind.AreaWidth, Vector3.UnitX, f.AreaWidth * 0.5f);
        }

        void Mark(GizmoHandleKind kind, Vector3 kernelAxis, float kernelDistance)
        {
            Vector3 worldAxis = SpellEmitterGizmoLaw.KernelToWorldVector(kernelAxis, m);
            float stretch = worldAxis.Length();
            if (stretch < 1e-6f) return;
            Vector3 axis = worldAxis / stretch;
            // A zero-sized shape still gets a mark a few pixels out, so it can be grown.
            float distance = MathF.Max(kernelDistance * stretch, MarkPixels * 1.5f * unit);
            Vector3 point = o + axis * distance;
            handles.Add(new GizmoHandle(GizmoTargetKind.Emitter, path, f.EmitterIndex, kind, o, axis,
                Vector3.Zero, point, [point], stretch));
        }
    }

    /// <summary>Three rings about the bone's own world axes at its pivot, optionally the translate
    /// set (only bones with a translation track can move).</summary>
    public static void Bone(List<GizmoHandle> handles, string path, int bone, Vector3 pivot, Matrix4x4 worldLinear,
        float unit, bool translate, Vector3 forward, Vector3 left, Vector3 up)
    {
        Matrix4x4 r = Orthonormalize(worldLinear);
        Ring(GizmoHandleKind.RingX, Row(r, 0), Row(r, 1));
        Ring(GizmoHandleKind.RingY, Row(r, 1), Row(r, 2));
        Ring(GizmoHandleKind.RingZ, Row(r, 2), Row(r, 0));
        if (translate) Translate(handles, GizmoTargetKind.Bone, path, bone, pivot, forward, left, up, unit);

        void Ring(GizmoHandleKind kind, Vector3 normal, Vector3 reference)
        {
            float radius = RingPixels * unit;
            Vector3 side = Vector3.Cross(normal, reference);
            var points = new Vector3[RingSamples + 1];
            for (int i = 0; i <= RingSamples; i++)
            {
                float a = i * MathF.PI * 2f / RingSamples;
                points[i] = pivot + (reference * MathF.Cos(a) + side * MathF.Sin(a)) * radius;
            }
            handles.Add(new GizmoHandle(GizmoTargetKind.Bone, path, bone, kind, pivot, normal, reference,
                pivot + reference * radius, points, radius));
        }
    }

    // ── picking ──────────────────────────────────────────────────────────────

    /// <summary>The handle under the mouse, or -1. Point handles win ties against the long
    /// arrows and rings they sit on.</summary>
    public static int Pick(IReadOnlyList<GizmoHandle> handles, Vector2 mouse, Func<Vector3, Vector2?> project,
        float pickPixels = PickPixels)
    {
        int best = -1;
        float bestDistance = pickPixels;
        for (int i = 0; i < handles.Count; i++)
        {
            GizmoHandle h = handles[i];
            float d = ScreenDistance(h, mouse, project);
            if (h.Pick.Length == 1) d -= 3f;   // a point mark is easier to mean than the arrow beside it
            if (d < bestDistance)
            {
                bestDistance = d;
                best = i;
            }
        }
        return best;
    }

    public static float ScreenDistance(in GizmoHandle h, Vector2 mouse, Func<Vector3, Vector2?> project)
    {
        if (h.Pick.Length == 1)
            return project(h.Pick[0]) is { } p ? Vector2.Distance(p, mouse) : float.MaxValue;
        float best = float.MaxValue;
        Vector2? previous = null;
        foreach (Vector3 world in h.Pick)
        {
            Vector2? pixel = project(world);
            if (previous is { } a && pixel is { } b) best = MathF.Min(best, PointSegmentDistance(mouse, a, b));
            previous = pixel;
        }
        return best;
    }

    public static float PointSegmentDistance(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float len = ab.LengthSquared();
        if (len < 1e-6f) return Vector2.Distance(p, a);
        float t = Math.Clamp(Vector2.Dot(p - a, ab) / len, 0f, 1f);
        return Vector2.Distance(p, a + ab * t);
    }

    // ── drag arithmetic ──────────────────────────────────────────────────────

    /// <summary>The parameter along the line anchor + t * axis nearest to the mouse ray
    /// (closest points of two lines; the projection when they are parallel).</summary>
    public static float AxisParam(Vector3 rayOrigin, Vector3 rayDirection, Vector3 anchor, Vector3 axis)
    {
        Vector3 d = SafeNormalize(rayDirection, Vector3.UnitZ);
        Vector3 x = SafeNormalize(axis, Vector3.UnitZ);
        Vector3 w = rayOrigin - anchor;
        float b = Vector3.Dot(d, x);
        float dd = Vector3.Dot(d, w);
        float e = Vector3.Dot(x, w);
        float denominator = 1f - b * b;
        if (denominator < 1e-6f) return e;
        return (e - b * dd) / denominator;
    }

    /// <summary>Where the mouse ray meets a plane, or null when it looks away from it.</summary>
    public static Vector3? PlanePoint(Vector3 rayOrigin, Vector3 rayDirection, Vector3 planePoint, Vector3 normal)
    {
        float denominator = Vector3.Dot(rayDirection, normal);
        if (MathF.Abs(denominator) < 1e-5f) return null;
        float s = Vector3.Dot(planePoint - rayOrigin, normal) / denominator;
        if (s < 0f) return null;
        return rayOrigin + rayDirection * s;
    }

    /// <summary>The angle (radians, right-handed about <paramref name="normal"/>) of the mouse
    /// ray's hit on the ring plane, measured from <paramref name="reference"/>; null when the
    /// ray grazes the plane.</summary>
    public static float? RingAngle(Vector3 rayOrigin, Vector3 rayDirection, Vector3 centre, Vector3 normal,
        Vector3 reference)
    {
        if (MathF.Abs(Vector3.Dot(SafeNormalize(rayDirection, Vector3.UnitZ), normal)) < 0.08f) return null;
        if (PlanePoint(rayOrigin, rayDirection, centre, normal) is not { } hit) return null;
        Vector3 v = hit - centre;
        if (v.LengthSquared() < 1e-8f) return null;
        return MathF.Atan2(Vector3.Dot(Vector3.Cross(reference, v), normal), Vector3.Dot(reference, v));
    }

    public static float Snap(float value, float step) => step <= 0f ? value : MathF.Round(value / step) * step;

    /// <summary>A world delta into the FILE's raw frame through a live linear frame (model to
    /// world): inverse frame, then the M2Reader swap undone.</summary>
    public static Vector3 WorldDeltaToRaw(Vector3 worldDelta, Matrix4x4 linearFrame)
    {
        Matrix4x4 linear = Linear(linearFrame);
        if (!Matrix4x4.Invert(linear, out Matrix4x4 inverse)) return Unswap(worldDelta);
        return Unswap(Vector3.TransformNormal(worldDelta, inverse));
    }

    /// <summary>The bone's NEW local rotation (runtime model space, normalized quaternion) after
    /// its world orientation turns by <paramref name="radians"/> about <paramref name="worldAxis"/>:
    /// R_new = boneWorldLinear * R_w * P^-1.</summary>
    public static Vector4 RotateBoneWorld(Matrix4x4 boneWorldLinear, Matrix4x4 parentWorldLinear,
        Vector3 worldAxis, float radians)
    {
        Matrix4x4 rotation = Matrix4x4.CreateFromAxisAngle(SafeNormalize(worldAxis, Vector3.UnitZ), radians);
        Matrix4x4 parent = Linear(parentWorldLinear);
        if (!Matrix4x4.Invert(parent, out Matrix4x4 parentInverse)) parentInverse = Matrix4x4.Identity;
        Matrix4x4 local = Orthonormalize(Linear(boneWorldLinear) * rotation * parentInverse);
        Quaternion q = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(local));
        return new Vector4(q.X, q.Y, q.Z, q.W);
    }

    /// <summary>The bone's current local rotation as the animator sees it: boneWorld * P^-1.</summary>
    public static Vector4 LocalRotation(Matrix4x4 boneWorldLinear, Matrix4x4 parentWorldLinear)
        => RotateBoneWorld(boneWorldLinear, parentWorldLinear, Vector3.UnitZ, 0f);

    // ── drawing ──────────────────────────────────────────────────────────────

    public static string Describe(GizmoHandleKind kind) => kind switch
    {
        GizmoHandleKind.Center => "move (screen plane)",
        GizmoHandleKind.AxisX => "move forward/back",
        GizmoHandleKind.AxisY => "move left/right",
        GizmoHandleKind.AxisZ => "move up/down",
        GizmoHandleKind.PlaneXY => "move on the floor plane",
        GizmoHandleKind.PlaneXZ => "move on the side plane",
        GizmoHandleKind.PlaneYZ => "move on the front plane",
        GizmoHandleKind.Speed => "speed (drag the reach tip)",
        GizmoHandleKind.AreaLength => "birth area length",
        GizmoHandleKind.AreaWidth => "birth area width",
        GizmoHandleKind.Radius => "birth radius",
        GizmoHandleKind.RingX => "rotate about the bone's X",
        GizmoHandleKind.RingY => "rotate about the bone's Y",
        GizmoHandleKind.RingZ => "rotate about the bone's Z",
        _ => kind.ToString(),
    };

    public static Vector4 BaseColour(GizmoHandleKind kind) => kind switch
    {
        GizmoHandleKind.AxisX or GizmoHandleKind.RingX => SpellEmitterGizmoLaw.ForwardAxis,
        GizmoHandleKind.AxisY or GizmoHandleKind.RingY => SpellEmitterGizmoLaw.LeftAxis,
        GizmoHandleKind.AxisZ or GizmoHandleKind.RingZ => SpellEmitterGizmoLaw.UpAxis,
        GizmoHandleKind.PlaneXY => new Vector4(0.95f, 0.85f, 0.35f, 0.85f),
        GizmoHandleKind.PlaneXZ => new Vector4(0.95f, 0.5f, 0.75f, 0.85f),
        GizmoHandleKind.PlaneYZ => new Vector4(0.45f, 0.9f, 0.9f, 0.85f),
        GizmoHandleKind.Speed => SpellEmitterGizmoLaw.EmitAxis,
        GizmoHandleKind.AreaLength or GizmoHandleKind.AreaWidth or GizmoHandleKind.Radius => MarkColour,
        _ => new Vector4(1f, 1f, 1f, 0.9f),
    };

    /// <summary>Append one handle's lines. <paramref name="unit"/> is yards per pixel.</summary>
    public static void Draw(List<GizmoLine> lines, in GizmoHandle h, bool hovered, bool active, float unit,
        Vector3 cameraRight, Vector3 cameraUp)
    {
        Vector4 colour = active ? ActiveColour : hovered ? HoverColour : BaseColour(h.Kind);
        switch (h.Kind)
        {
            case GizmoHandleKind.Center:
                Square(h.Point, cameraRight, cameraUp, CenterPixels * unit, colour);
                break;
            case GizmoHandleKind.AxisX:
            case GizmoHandleKind.AxisY:
            case GizmoHandleKind.AxisZ:
            {
                lines.Add(new GizmoLine(h.Anchor, h.Point, colour));
                // Arrowhead: four barbs back toward the anchor.
                Vector3 back = -h.Axis * MarkPixels * 1.6f * unit;
                Vector3 side = SafeNormalize(Vector3.Cross(h.Axis, MathF.Abs(h.Axis.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX), Vector3.UnitX);
                Vector3 side2 = Vector3.Cross(h.Axis, side);
                float w = MarkPixels * 0.6f * unit;
                lines.Add(new GizmoLine(h.Point, h.Point + back + side * w, colour));
                lines.Add(new GizmoLine(h.Point, h.Point + back - side * w, colour));
                lines.Add(new GizmoLine(h.Point, h.Point + back + side2 * w, colour));
                lines.Add(new GizmoLine(h.Point, h.Point + back - side2 * w, colour));
                break;
            }
            case GizmoHandleKind.PlaneXY:
            case GizmoHandleKind.PlaneXZ:
            case GizmoHandleKind.PlaneYZ:
            {
                Vector3 b = Vector3.Cross(h.Axis, h.Tangent);
                Square(h.Point, h.Tangent, b, PlaneSidePixels * 0.5f * unit, colour);
                break;
            }
            case GizmoHandleKind.Speed:
            {
                Diamond(h.Point, cameraRight, cameraUp, MarkPixels * unit, colour);
                lines.Add(new GizmoLine(h.Anchor, h.Point, colour with { W = colour.W * 0.35f }));
                break;
            }
            case GizmoHandleKind.AreaLength:
            case GizmoHandleKind.AreaWidth:
            case GizmoHandleKind.Radius:
            {
                // A "T": a tick across the axis at the mark, and a stub along it.
                Vector3 side = SafeNormalize(Vector3.Cross(h.Axis, MathF.Abs(h.Axis.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX), Vector3.UnitX);
                float w = MarkPixels * unit;
                lines.Add(new GizmoLine(h.Point - side * w, h.Point + side * w, colour));
                lines.Add(new GizmoLine(h.Point, h.Point + h.Axis * w, colour));
                Diamond(h.Point, cameraRight, cameraUp, MarkPixels * 0.5f * unit, colour);
                break;
            }
            case GizmoHandleKind.RingX:
            case GizmoHandleKind.RingY:
            case GizmoHandleKind.RingZ:
            {
                for (int i = 1; i < h.Pick.Length; i++) lines.Add(new GizmoLine(h.Pick[i - 1], h.Pick[i], colour));
                if (hovered || active)
                    lines.Add(new GizmoLine(h.Anchor, h.Anchor + h.Axis * h.Scale * 1.3f, colour with { W = 0.6f }));
                break;
            }
        }

        void Square(Vector3 c, Vector3 a, Vector3 b, float half, Vector4 col)
        {
            Vector3 p0 = c - a * half - b * half, p1 = c + a * half - b * half;
            Vector3 p2 = c + a * half + b * half, p3 = c - a * half + b * half;
            lines.Add(new GizmoLine(p0, p1, col)); lines.Add(new GizmoLine(p1, p2, col));
            lines.Add(new GizmoLine(p2, p3, col)); lines.Add(new GizmoLine(p3, p0, col));
        }

        void Diamond(Vector3 c, Vector3 a, Vector3 b, float half, Vector4 col)
        {
            Vector3 p0 = c - a * half, p1 = c + b * half, p2 = c + a * half, p3 = c - b * half;
            lines.Add(new GizmoLine(p0, p1, col)); lines.Add(new GizmoLine(p1, p2, col));
            lines.Add(new GizmoLine(p2, p3, col)); lines.Add(new GizmoLine(p3, p0, col));
        }
    }

    public static Vector3 SafeNormalize(Vector3 v, Vector3 fallback)
        => v.LengthSquared() > 1e-12f ? Vector3.Normalize(v) : fallback;
}

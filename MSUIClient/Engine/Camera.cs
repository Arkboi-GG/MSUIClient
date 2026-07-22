using System.Numerics;

namespace MSUIClient.Engine;

/// <summary>
/// Third-person orbit camera.
///
/// IMPORTANT — THERE IS NO COORDINATE CONVERSION IN THIS CLIENT.
/// Everything works natively in WoW world space:
///     +X = north      +Y = west       +Z = up
///     orientation = radians CCW about +Z, measured from +X
///
/// The browser build needed a conversion module because three.js hardcodes
/// Y-up. OpenGL does not care: it only needs a consistent view and projection
/// matrix, and "up" is whatever we pass to LookAt. So the entire coords.ts
/// layer — and every bug it could have produced — simply does not exist here.
/// Positions read from ADT, vmaps, DBC and the network all mean the same thing
/// end to end, and the debug HUD can print raw values that match `.gps` in a
/// real client with no translation.
///
/// System.Numerics.Matrix4x4 builds row-vector matrices; GLSL expects
/// column-major. Upload with transpose = false and multiply as
/// `vec4(pos,1) * matrix` in the shader, or transpose here. We transpose here,
/// once, in <see cref="ViewProjection"/>, so shaders stay conventional.
/// </summary>
public sealed class Camera
{
    /// <summary>Point the camera orbits, in WoW space.</summary>
    public Vector3 Target;

    /// <summary>
    /// Radians CCW about +Z from +X. Matches WoW orientation exactly.
    ///
    /// This is the CHARACTER'S FACING, not the camera's. The controller reads it
    /// straight through as the character's orientation, and it is the value a
    /// movement packet wants in Phase 2. Where the camera actually sits is
    /// <see cref="ViewYaw"/>.
    /// </summary>
    public float Yaw;

    /// <summary>
    /// Camera-only yaw OFFSET from <see cref="Yaw"/>, in radians, kept in
    /// (-pi, pi].
    ///
    /// This is the whole left-button-versus-right-button distinction. Holding
    /// the LEFT button swings the camera around the character without turning
    /// him, so you can walk north and look at your own face - that motion goes
    /// here. Holding the RIGHT button turns the character, so that motion goes
    /// into <see cref="Yaw"/> instead.
    ///
    /// Keeping them separate is what makes both possible from one heading.
    /// Folding the offset back in (see <see cref="FoldOrbitIntoFacing"/>) turns
    /// the character to wherever you had swung the camera, without the camera
    /// moving a pixel - which is exactly what the real client does the instant
    /// you press the right button.
    ///
    /// Signed and wrapped rather than 0..2pi so easing it back to zero always
    /// takes the short way round.
    /// </summary>
    public float OrbitYaw;

    /// <summary>Where the camera actually sits and looks. Facing plus the orbit offset.</summary>
    public float ViewYaw => Yaw + OrbitYaw;

    /// <summary>
    /// Radians of camera ELEVATION ABOVE the target. Positive puts the camera
    /// up high looking down; negative puts it low looking up.
    ///
    /// The sign matters and is easy to get backwards. Screen Y grows DOWNWARD,
    /// so a mouse-look handler that wants standard (non-inverted) behaviour —
    /// mouse up means look up — must ADD the mouse delta, not subtract it:
    /// looking up means the camera drops below the target, which is a SMALLER
    /// pitch. See ClientWindow's MouseMove handler.
    /// </summary>
    public float Pitch = 0.35f;

    /// <summary>Zoom distance the user asked for, via the wheel.</summary>
    public float Distance = 9f;

    /// <summary>
    /// Distance actually used to place the camera. Camera collision writes this
    /// every frame so the camera can be pulled in toward the character without
    /// destroying the zoom level the user chose — let go of the wall and it
    /// eases back out to <see cref="Distance"/>.
    ///
    /// Kept separate rather than clamping Distance directly: overwriting the
    /// user's zoom means walking past a tree permanently zooms you in, which
    /// feels broken in a way that is hard to describe and easy to ship.
    /// </summary>
    public float EffectiveDistance = 9f;

    public float MinDistance = 1.5f;
    public float MaxDistance = 40f;

    public float FieldOfViewDegrees = 70f;
    public float NearPlane = 0.1f;
    public float FarPlane = 2000f;

    public float AspectRatio { get; set; } = 16f / 9f;

    /// <summary>Height above <see cref="Target"/> the camera actually looks at.</summary>
    public float EyeHeight = 2.2f;

    private const float PitchLimit = 1.45f;   // ~83 degrees, short of gimbal lock

    public Vector3 EyeTarget => Target + new Vector3(0, 0, EyeHeight);

    /// <summary>
    /// Unit vector from <see cref="EyeTarget"/> toward the camera, in WoW space.
    /// The camera-collision pass marches along this to find the first thing in
    /// the way.
    /// </summary>
    public Vector3 OrbitDirection
    {
        get
        {
            float cp = MathF.Cos(Pitch);
            float yaw = ViewYaw;
            // Behind the target: negate the facing direction.
            return new Vector3(
                -MathF.Cos(yaw) * cp,
                -MathF.Sin(yaw) * cp,
                MathF.Sin(Pitch));
        }
    }

    /// <summary>Camera position in WoW space, derived from yaw/pitch/distance.</summary>
    public Vector3 Position => EyeTarget + OrbitDirection * EffectiveDistance;

    /// <summary>Unit vector the camera looks along, in WoW space.</summary>
    public Vector3 Forward
    {
        get
        {
            float cp = MathF.Cos(Pitch);
            float yaw = ViewYaw;
            return Vector3.Normalize(new Vector3(
                MathF.Cos(yaw) * cp,
                MathF.Sin(yaw) * cp,
                -MathF.Sin(Pitch)));
        }
    }

    /// <summary>
    /// Horizontal facing, ignoring pitch — what movement input follows.
    /// Deliberately on <see cref="Yaw"/> and NOT ViewYaw: pressing W walks the
    /// character forward, not toward wherever the camera has been swung.
    /// </summary>
    public Vector3 FlatForward => new(MathF.Cos(Yaw), MathF.Sin(Yaw), 0);

    /// <summary>Right of <see cref="FlatForward"/>. In a Z-up left-of-west world this is (sin, -cos, 0).</summary>
    public Vector3 FlatRight => new(MathF.Sin(Yaw), -MathF.Cos(Yaw), 0);

    /// <summary>Turn the character, and with him the camera. Right-drag and the arrow keys.</summary>
    public void Rotate(float yawDelta, float pitchDelta)
    {
        Yaw += yawDelta;
        Pitch = Math.Clamp(Pitch + pitchDelta, -PitchLimit, PitchLimit);

        const float tau = MathF.PI * 2f;
        Yaw = ((Yaw % tau) + tau) % tau;
    }

    /// <summary>Swing the camera around the character without turning him. Left-drag.</summary>
    public void RotateView(float yawDelta)
    {
        if (yawDelta == 0f) return;
        OrbitYaw = Wrap(OrbitYaw + yawDelta);
    }

    /// <summary>
    /// Turn the character to wherever the camera has been swung, and drop the
    /// offset. The camera does not move: ViewYaw is unchanged because the same
    /// angle simply moved from one term to the other.
    ///
    /// This is what the real client does the moment you press the right button
    /// after looking at your own face - the character spins to put his back to
    /// you, from your point of view instantly and without the view shifting.
    /// </summary>
    public void FoldOrbitIntoFacing()
    {
        if (OrbitYaw == 0f) return;

        const float tau = MathF.PI * 2f;
        Yaw = (((Yaw + OrbitYaw) % tau) + tau) % tau;
        OrbitYaw = 0f;
    }

    /// <summary>
    /// Ease the camera back behind the character. Called while moving, because
    /// that is when the real client re-centres.
    /// </summary>
    public void EaseOrbitBehind(float dt, float seconds = 0.15f)
    {
        if (OrbitYaw == 0f) return;

        float blend = seconds <= 0f ? 1f : 1f - MathF.Exp(-dt / seconds);
        OrbitYaw -= OrbitYaw * blend;

        if (MathF.Abs(OrbitYaw) < 0.002f) OrbitYaw = 0f;
    }

    /// <summary>Wrap to (-pi, pi], so easing toward zero always takes the short way.</summary>
    private static float Wrap(float radians)
    {
        const float tau = MathF.PI * 2f;
        radians = ((radians % tau) + tau) % tau;
        return radians > MathF.PI ? radians - tau : radians;
    }

    public void Zoom(float delta)
    {
        Distance = Math.Clamp(Distance - delta, MinDistance, MaxDistance);
        // Zooming IN takes effect immediately; zooming out is left to the
        // collision pass, which eases outward only as far as it is safe.
        if (EffectiveDistance > Distance) EffectiveDistance = Distance;
    }

    public Matrix4x4 View
        => Matrix4x4.CreateLookAt(Position, EyeTarget, Vector3.UnitZ);

    public Matrix4x4 Projection
        => Matrix4x4.CreatePerspectiveFieldOfView(
            FieldOfViewDegrees * MathF.PI / 180f, AspectRatio, NearPlane, FarPlane);

    /// <summary>
    /// Combined view-projection, in System.Numerics' row-vector convention
    /// (v_clip = v * View * Projection).
    ///
    /// DO NOT transpose this for GLSL. System.Numerics stores Matrix4x4
    /// row-major in memory; glUniformMatrix4fv with transpose = false reads
    /// those same bytes as COLUMN-major, which is exactly the flip GLSL needs.
    /// Uploading the matrix as-is therefore hands the shader the transpose it
    /// wants, and `uViewProjection * vec4(pos, 1.0)` is correct.
    ///
    /// Transposing here as well double-flips it: every vertex lands in garbage
    /// clip space, nothing survives clipping, and the screen shows only the
    /// clear colour. That failure looks exactly like "geometry isn't rendering"
    /// and wastes a lot of time, so leave this alone.
    /// </summary>
    public Matrix4x4 ViewProjection => View * Projection;

    /// <summary>Six frustum planes in WoW space for tile culling. Normals point inward.</summary>
    public Vector4[] FrustumPlanes()
    {
        var m = View * Projection;
        var planes = new Vector4[6];

        planes[0] = new Vector4(m.M14 + m.M11, m.M24 + m.M21, m.M34 + m.M31, m.M44 + m.M41); // left
        planes[1] = new Vector4(m.M14 - m.M11, m.M24 - m.M21, m.M34 - m.M31, m.M44 - m.M41); // right
        planes[2] = new Vector4(m.M14 + m.M12, m.M24 + m.M22, m.M34 + m.M32, m.M44 + m.M42); // bottom
        planes[3] = new Vector4(m.M14 - m.M12, m.M24 - m.M22, m.M34 - m.M32, m.M44 - m.M42); // top
        planes[4] = new Vector4(m.M13, m.M23, m.M33, m.M43);                                  // near
        planes[5] = new Vector4(m.M14 - m.M13, m.M24 - m.M23, m.M34 - m.M33, m.M44 - m.M43); // far

        for (int i = 0; i < 6; i++)
        {
            var p = planes[i];
            float len = MathF.Sqrt(p.X * p.X + p.Y * p.Y + p.Z * p.Z);
            if (len > 0) planes[i] = p / len;
        }

        return planes;
    }

    public static bool BoxInFrustum(Vector4[] planes, Vector3 min, Vector3 max)
    {
        foreach (var p in planes)
        {
            // Positive vertex: the corner furthest along the plane normal.
            var v = new Vector3(
                p.X >= 0 ? max.X : min.X,
                p.Y >= 0 ? max.Y : min.Y,
                p.Z >= 0 ? max.Z : min.Z);

            if (p.X * v.X + p.Y * v.Y + p.Z * v.Z + p.W < 0) return false;
        }
        return true;
    }
}

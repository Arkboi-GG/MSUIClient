using System.Numerics;
using System.Text.Json.Serialization;

namespace MSUIClient.Engine;

/// <summary>
/// How the keyboard and mouse drive the Command View (free view) rig. Interface Options →
/// Command View picks one; the schemes differ ONLY in the free view — first-person play keeps
/// vanilla's A/D-turn, Q/E-strafe, right-drag free-look untouched.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CommandViewScheme
{
    /// <summary>W/S fly, A/D strafe, Q/E turn. Right-drag turns without tilting: the view
    /// angle is a knob (on screen and in Interface Options), not the mouse. The default.</summary>
    Strafe = 0,

    /// <summary>The original free view: A/D turn like a character, Q/E strafe, right-drag is a
    /// full free-look (yaw and pitch).</summary>
    Classic = 1,

    /// <summary>RTS / CRPG camera. Same keys as Strafe, but turning (Q/E, right-drag) orbits
    /// around the ground you are looking at instead of spinning the rig in place, the wheel
    /// raises and lowers the camera straight up and down, and the screen edges pan.</summary>
    Rts = 2,
}

public static class CommandViewLaw
{
    public static readonly IReadOnlyList<CommandViewScheme> DisplayOrder =
        [CommandViewScheme.Strafe, CommandViewScheme.Classic, CommandViewScheme.Rts];

    public const float MinPitchDegrees = 10f;
    public const float MaxPitchDegrees = 80f;
    public const float DefaultPitchDegrees = 50f;

    /// <summary>Furthest the RTS orbit pivot sits ahead of the rig: a nearly level view would
    /// otherwise put the pivot on the horizon and a small turn would fling the rig for miles.</summary>
    public const float MaxOrbitReachYards = 120f;

    public static CommandViewScheme Normalize(CommandViewScheme scheme) =>
        Enum.IsDefined(scheme) ? scheme : CommandViewScheme.Strafe;

    public static string Label(CommandViewScheme scheme) => scheme switch
    {
        CommandViewScheme.Classic => "Classic (A/D turn)",
        CommandViewScheme.Rts => "RTS / CRPG",
        _ => "Standard (A/D sidestep) - default",
    };

    public static string Description(CommandViewScheme scheme) => scheme switch
    {
        CommandViewScheme.Classic =>
            "The original free view. A and D turn like a character, Q and E\n" +
            "sidestep, and a right-drag is a full free-look: it tilts the view\n" +
            "as well as turning it. The wheel flies toward what you look at.",
        CommandViewScheme.Rts =>
            "Warcraft / Divinity style. W, A, S, D pan the camera over the ground;\n" +
            "Q and E are free for command hotkeys. A right-drag orbits around the\n" +
            "ground you are looking at, so the battlefield stays under you. The\n" +
            "wheel raises and lowers the camera straight up and down, the screen\n" +
            "edges pan, and the view angle is a knob rather than the mouse.",
        _ =>
            "W and S fly forward and back, A and D sidestep; Q and E are free for\n" +
            "command hotkeys. A right-drag turns the view but never tilts it: the\n" +
            "view angle is a knob (on screen, bottom right, and the slider below).",
    };

    /// <summary>In the free view, the Turn keys (A/D) strafe and the Strafe keys (Q/E) do
    /// nothing to the camera; they stay free for command hotkeys.</summary>
    public static bool TurnKeysStrafe(CommandViewScheme scheme) =>
        Normalize(scheme) != CommandViewScheme.Classic;

    /// <summary>Mouse look never tilts; the pitch is the view-angle knob.</summary>
    public static bool PitchLocked(CommandViewScheme scheme) =>
        Normalize(scheme) != CommandViewScheme.Classic;

    /// <summary>Turning orbits the rig around the ground it looks at.</summary>
    public static bool OrbitsFocus(CommandViewScheme scheme) =>
        Normalize(scheme) == CommandViewScheme.Rts;

    /// <summary>The wheel moves the rig straight up and down instead of along the view.</summary>
    public static bool WheelIsVertical(CommandViewScheme scheme) =>
        Normalize(scheme) == CommandViewScheme.Rts;

    /// <summary>Exponential easing time constant for the rig-to-camera glide (seconds).
    /// "A little" smoothing: the camera settles within ~0.4 s of the rig stopping.</summary>
    public const float SmoothingTau = 0.12f;

    /// <summary>Easing for the locked camera tracking its unit: a touch slower than the free
    /// glide so a running party reads as a steady pan, not a jitter.</summary>
    public const float FollowTau = 0.20f;

    /// <summary>A rig jump longer than this (map fly, focus across town) snaps instead of gliding.</summary>
    public const float SnapYards = 80f;

    /// <summary>A rig move this long while locked means the commander went somewhere else on
    /// purpose (commander-map fly); the lock lets go rather than dragging the unit's framing there.</summary>
    public const float FollowBreakYards = 40f;

    /// <summary>Frame-rate independent exponential glide of <paramref name="current"/> toward
    /// <paramref name="target"/>; snaps across teleports.</summary>
    public static Vector3 Smooth(Vector3 current, Vector3 target, float dt, float tau)
    {
        if (dt <= 0f || tau <= 0f) return target;
        Vector3 gap = target - current;
        if (gap.LengthSquared() > SnapYards * SnapYards) return target;
        float a = 1f - MathF.Exp(-dt / tau);
        return current + gap * a;
    }

    /// <summary>Rotate a framing offset about Z by the camera's yaw change, so a locked camera
    /// orbits its unit and the unit keeps its place on screen.</summary>
    public static Vector3 RotateOffset(Vector3 offset, float yawDelta)
    {
        if (yawDelta == 0f || !float.IsFinite(yawDelta)) return offset;
        float c = MathF.Cos(yawDelta), s = MathF.Sin(yawDelta);
        return new Vector3(offset.X * c - offset.Y * s, offset.X * s + offset.Y * c, offset.Z);
    }

    public static float ClampPitchDegrees(float degrees) =>
        float.IsFinite(degrees) ? Math.Clamp(degrees, MinPitchDegrees, MaxPitchDegrees) : DefaultPitchDegrees;

    public static float PitchRadians(float degrees) => ClampPitchDegrees(degrees) * MathF.PI / 180f;

    /// <summary>
    /// Where the rig should stand after its heading turned by <paramref name="yawDelta"/> so the
    /// ground point it was looking at stays put. <paramref name="altitude"/> is the rig's height
    /// over that ground; <paramref name="pitch"/> is the camera's downward angle in radians. The
    /// pivot sits ahead of the rig along the OLD heading at altitude / tan(pitch), capped so a
    /// level view cannot fling the rig; the rig then swings around it onto the new heading.
    /// </summary>
    public static Vector3 OrbitRig(Vector3 rig, float yawBefore, float yawDelta, float altitude,
        float pitch)
    {
        if (yawDelta == 0f || !float.IsFinite(yawDelta)) return rig;
        float tan = MathF.Tan(MathF.Max(0.05f, pitch));
        float reach = Math.Clamp(MathF.Max(0f, altitude) / tan, 0f, MaxOrbitReachYards);
        if (reach <= 1e-3f) return rig;
        var before = new Vector3(MathF.Cos(yawBefore), MathF.Sin(yawBefore), 0f);
        float yawAfter = yawBefore + yawDelta;
        var after = new Vector3(MathF.Cos(yawAfter), MathF.Sin(yawAfter), 0f);
        Vector3 pivot = rig + before * reach;
        return pivot - after * reach;
    }
}

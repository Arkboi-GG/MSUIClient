using System.Numerics;

namespace MSUIClient.Engine.UI;

public enum AutoFollowRefusal
{
    None,
    InvalidTarget,
    PlayerDead,
    Stunned,
    Busy,
}

/// <summary>
/// One tick of the reference auto-follow mode. <see cref="MovingLatch"/> is the hysteresis
/// state retained for the next tick; <see cref="Forward"/> is the synthetic MoveForward input
/// emitted on this tick. They differ only for the zero-horizontal-bearing guard.
/// </summary>
public readonly record struct AutoFollowMotion(
    float Yaw, bool MovingLatch, bool Forward, bool EndsFollow);

/// <summary>
/// Benilla's recovered 1.12 auto-follow laws. This owns the ordered start gate, motion
/// thresholds, bounded steering, status copy/fade and full-screen authored seat. Runtime code
/// only resolves live units and turns the result into ordinary movement input and draw calls.
/// </summary>
public static class AutoFollowUiLaw
{
    public const float TurnRate = MathF.PI;
    public const float TurnDeadzone = .001f;
    public const float SpeedNorm = 7f;
    public const float StopDistance = 3f;
    public const float ResumeFactor = 1.5f;
    public const float VerticalAlignCos = .9998477f; // cos(1 degree)
    public const double StatusFadeSeconds = 4.0;
    public const string StatusFontObject = "GameFontNormalHuge";

    public static AutoFollowRefusal StartRefusal(bool followeeIsPlayer,
        bool canAssistFollowee, bool playerDead, bool playerStunned, bool playerCasting)
    {
        if (!followeeIsPlayer || !canAssistFollowee) return AutoFollowRefusal.InvalidTarget;
        if (playerDead) return AutoFollowRefusal.PlayerDead;
        if (playerStunned) return AutoFollowRefusal.Stunned;
        if (playerCasting) return AutoFollowRefusal.Busy;
        return AutoFollowRefusal.None;
    }

    public static string? RefusalGlobalString(AutoFollowRefusal refusal) => refusal switch
    {
        AutoFollowRefusal.InvalidTarget => "ERR_INVALID_FOLLOW_TARGET",
        AutoFollowRefusal.PlayerDead => "ERR_PLAYER_DEAD",
        AutoFollowRefusal.Stunned => "ERR_GENERIC_STUNNED",
        AutoFollowRefusal.Busy => "ERR_TOOBUSYTOFOLLOW",
        _ => null,
    };

    public static float ArriveDistance(float speed) =>
        MathF.Max(0f, FiniteOrZero(speed)) / SpeedNorm * StopDistance;

    public static float ResumeDistance(float speed) =>
        StopDistance * ResumeFactor * MathF.Max(FiniteOrZero(speed) / SpeedNorm, 1f);

    public static bool ShouldMove(bool wasMoving, float distance, float speed)
    {
        float d = MathF.Max(0f, FiniteOrZero(distance));
        return wasMoving ? d > ArriveDistance(speed) : d >= ResumeDistance(speed);
    }

    public static bool VerticallyDegenerate(Vector3 delta)
    {
        if (!Finite(delta)) return true;
        float distance = delta.Length();
        return distance > float.Epsilon && MathF.Abs(delta.Z) / distance > VerticalAlignCos;
    }

    public static float WrapPi(float radians)
    {
        if (!float.IsFinite(radians)) return 0f;
        float tau = MathF.PI * 2f;
        float wrapped = (radians + MathF.PI) % tau;
        if (wrapped < 0f) wrapped += tau;
        return wrapped - MathF.PI;
    }

    public static float Steer(float yaw, float bearing, float dt)
    {
        float face = float.IsFinite(yaw) ? yaw : 0f;
        float remaining = WrapPi(bearing - face);
        if (MathF.Abs(remaining) <= TurnDeadzone) return face;
        float elapsed = float.IsFinite(dt) ? MathF.Max(0f, dt) : 0f;
        float budget = TurnRate * elapsed;
        return WrapPi(face + Math.Clamp(remaining, -budget, budget));
    }

    public static AutoFollowMotion Tick(Vector3 delta, float yaw, bool wasMoving,
        float speed, float dt)
    {
        if (!Finite(delta) || VerticallyDegenerate(delta))
            return new(WrapPi(yaw), false, false, EndsFollow: true);

        float horizontalDistance = new Vector2(delta.X, delta.Y).Length();
        // The reference suppresses this tick's synthetic input but retains the band latch when
        // there is no meaningful horizontal bearing. It does not end the follow.
        if (horizontalDistance < float.Epsilon)
            return new(WrapPi(yaw), wasMoving, false, EndsFollow: false);

        float bearing = MathF.Atan2(delta.Y, delta.X);
        float nextYaw = Steer(yaw, bearing, dt);
        bool moving = ShouldMove(wasMoving, horizontalDistance, speed);
        return new(nextYaw, moving, moving, EndsFollow: false);
    }

    public static string BeginText(string name) => $"Following {name}.";
    public static string EndText(string name) => $"You stop following {name}.";

    public static float StatusAlpha(bool active, double elapsedSinceEnd)
    {
        if (active) return 1f;
        if (!double.IsFinite(elapsedSinceEnd) || elapsedSinceEnd < 0) return 0f;
        return Math.Clamp(1f - (float)(elapsedSinceEnd / StatusFadeSeconds), 0f, 1f);
    }

    public static Vector2 StatusCenter(Vector2 displayPixels) => displayPixels * .5f;

    private static bool Finite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static float FiniteOrZero(float value) => float.IsFinite(value) ? value : 0f;
}

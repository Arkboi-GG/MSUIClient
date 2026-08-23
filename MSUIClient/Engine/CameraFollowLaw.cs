using System.Text.Json.Serialization;

namespace MSUIClient.Engine;

/// <summary>Vanilla 1.12's engine-side <c>cameraSmoothStyle</c> values.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CameraFollowStyle
{
    Never = 0,
    Smart = 1,
    Always = 2,
}

public enum CameraFollowState
{
    Fear,
    Turn,
    Strafe,
    Move,
    Track,
    Stop,
    Idle,
}

/// <summary>The build-5875 InputControl command bits consumed by the camera-follow classifier.</summary>
public static class CameraFollowCommand
{
    public const uint RightMouse = 0x1;
    public const uint LeftMouse = 0x2;
    public const uint Forward = 0x10;
    public const uint Backward = 0x20;
    public const uint StrafeLeft = 0x40;
    public const uint StrafeRight = 0x80;
    public const uint TurnLeft = 0x100;
    public const uint TurnRight = 0x200;
    public const uint Autorun = 0x1000;
    public const uint Track = 0x100000;
    public const uint Fear = 0x2000_0000;

    public const uint MoveBits = Forward | Backward | Autorun;
    public const uint StrafeBits = StrafeLeft | StrafeRight;
    public const uint TurnBits = TurnLeft | TurnRight;
}

public readonly record struct CameraFollowConfig(
    CameraFollowStyle Style,
    CameraFollowStyle TrackingStyle,
    float YawSpeedDegrees)
{
    public static CameraFollowConfig Default => new(
        CameraFollowStyle.Smart,
        CameraFollowStyle.Smart,
        CameraFollowLaw.DefaultYawSpeedDegrees);

    public CameraFollowConfig Normalized() => new(
        CameraFollowLaw.NormalizeStyle(Style),
        CameraFollowLaw.NormalizeStyle(TrackingStyle),
        Math.Clamp(YawSpeedDegrees,
            CameraFollowLaw.MinimumYawSpeedDegrees,
            CameraFollowLaw.MaximumYawSpeedDegrees));
}

public readonly record struct CameraFollowInput(
    CameraFollowConfig Config,
    float FaceYaw,
    uint Command);

/// <summary>
/// Pure build-5875 camera-return law. A command-word edge snapshots one cosine-smoothed return;
/// holding a command does not chase every frame, and entering either mouse-look mode cancels the
/// active return so the hand owns the camera.
/// </summary>
public static class CameraFollowLaw
{
    public const float DefaultYawSpeedDegrees = 180f;
    public const float MinimumYawSpeedDegrees = 90f;
    public const float MaximumYawSpeedDegrees = 270f;
    public const float MinimumDurationSeconds = .1f;
    public const float MaximumDurationSeconds = 2f;
    public const float Epsilon = .001f;

    /// <summary>The reference Options dropdown order, distinct from the engine enum order.</summary>
    public static IReadOnlyList<CameraFollowStyle> DisplayOrder { get; } =
    [CameraFollowStyle.Smart, CameraFollowStyle.Always, CameraFollowStyle.Never];

    public static CameraFollowStyle FromStoredValue(int value) => value switch
    {
        0 or 3 => CameraFollowStyle.Never,
        2 => CameraFollowStyle.Always,
        _ => CameraFollowStyle.Smart,
    };

    public static CameraFollowStyle NormalizeStyle(CameraFollowStyle value) =>
        value is CameraFollowStyle.Never or CameraFollowStyle.Smart or CameraFollowStyle.Always
            ? value
            : CameraFollowStyle.Smart;

    public static string Label(CameraFollowStyle value) => NormalizeStyle(value) switch
    {
        CameraFollowStyle.Never => "Never",
        CameraFollowStyle.Always => "Always",
        _ => "Smart",
    };

    public static string Description(CameraFollowStyle value) => NormalizeStyle(value) switch
    {
        CameraFollowStyle.Never =>
            "Leaves the camera exactly where you place it; keyboard turns still carry it with you.",
        CameraFollowStyle.Always =>
            "Returns the camera behind your character on every movement or camera-input edge.",
        _ =>
            "Returns behind your character while you move, but leaves a standing view where you place it.",
    };

    public static CameraFollowState State(uint command, bool stopping)
    {
        bool Held(uint bits) => (command & bits) != 0;
        if (Held(CameraFollowCommand.Fear)) return CameraFollowState.Fear;
        if (Held(CameraFollowCommand.TurnBits) || Held(CameraFollowCommand.RightMouse))
            return CameraFollowState.Turn;
        if (Held(CameraFollowCommand.StrafeBits) ||
            Held(CameraFollowCommand.RightMouse) && Held(CameraFollowCommand.TurnBits))
            return CameraFollowState.Strafe;
        if (Held(CameraFollowCommand.MoveBits) ||
            Held(CameraFollowCommand.RightMouse) && Held(CameraFollowCommand.LeftMouse))
            return CameraFollowState.Move;
        if (Held(CameraFollowCommand.Track)) return CameraFollowState.Track;
        return stopping ? CameraFollowState.Stop : CameraFollowState.Idle;
    }

    public static CameraFollowStyle Style(in CameraFollowInput input) =>
        (input.Command & (CameraFollowCommand.Track | CameraFollowCommand.Fear)) != 0
            ? input.Config.Normalized().TrackingStyle
            : input.Config.Normalized().Style;

    public static (float Delay, float Factor) Row(
        CameraFollowStyle style, CameraFollowState state) => NormalizeStyle(style) switch
    {
        CameraFollowStyle.Never => (0f, 0f),
        CameraFollowStyle.Smart => state switch
        {
            CameraFollowState.Idle or CameraFollowState.Stop => (0f, 0f),
            CameraFollowState.Track or CameraFollowState.Fear => (.4f, 10f),
            _ => (0f, 1f),
        },
        _ => (0f, 1f),
    };

    public static float Wrap(float radians)
    {
        const float tau = MathF.PI * 2f;
        radians = ((radians + MathF.PI) % tau + tau) % tau - MathF.PI;
        return radians;
    }
}

/// <summary>Stateful edge/transition half of <see cref="CameraFollowLaw"/>.</summary>
public sealed class CameraFollowController
{
    private sealed class Arm
    {
        public float From;
        public float To;
        public float Duration;
        public float Delay;
        public float Elapsed;
        public float ArmedDelay;
        public float ArmedFactor;
    }

    private uint? _lastCommand;
    private Arm? _arm;

    public bool Armed => _arm is not null;

    public void Reset()
    {
        _lastCommand = null;
        _arm = null;
    }

    /// <summary>Returns an absolute view yaw when the law owns this frame, otherwise null.</summary>
    public float? Advance(in CameraFollowInput rawInput, float currentViewYaw,
        float dt, bool lookHeld)
    {
        CameraFollowInput input = rawInput with { Config = rawInput.Config.Normalized() };
        uint word = input.Command;
        uint? previous = _lastCommand;
        _lastCommand = word;

        if (lookHeld)
        {
            _arm = null;
            return null;
        }

        if (previous is uint prior && prior != word)
        {
            bool stopping = ((prior & ~word) &
                (CameraFollowCommand.MoveBits |
                 CameraFollowCommand.StrafeBits |
                 CameraFollowCommand.TurnBits)) != 0;
            ArmTransition(input, currentViewYaw, stopping);
        }

        if (_arm is not Arm arm) return null;
        arm.Elapsed += MathF.Max(dt, 0f);
        float transitionTime = arm.Elapsed - arm.Delay;
        if (transitionTime < 0f) return null;

        float progress = transitionTime / arm.Duration;
        float offset;
        if (progress >= 1f)
        {
            offset = arm.To;
            _arm = null;
        }
        else
        {
            float eased = (1f - MathF.Cos(MathF.PI * progress)) * .5f;
            offset = arm.From + (arm.To - arm.From) * eased;
        }
        return CameraFollowLaw.Wrap(input.FaceYaw + offset);
    }

    private void ArmTransition(in CameraFollowInput input, float currentViewYaw, bool stopping)
    {
        CameraFollowState state = CameraFollowLaw.State(input.Command, stopping);
        (float delay, float factor) = CameraFollowLaw.Row(CameraFollowLaw.Style(input), state);
        if (factor == 0f)
        {
            _arm = null;
            return;
        }

        const float target = 0f;
        float from = CameraFollowLaw.Wrap(currentViewYaw - input.FaceYaw);
        float gap = MathF.Abs(target - from);
        if (gap < CameraFollowLaw.Epsilon) return;

        if (_arm is Arm current && current.To == target &&
            MathF.Abs(current.ArmedDelay - delay) < CameraFollowLaw.Epsilon &&
            MathF.Abs(current.ArmedFactor - factor) < CameraFollowLaw.Epsilon)
            return;

        float rate = MathF.Max(input.Config.YawSpeedDegrees * MathF.PI / 180f,
            CameraFollowLaw.Epsilon);
        _arm = new Arm
        {
            From = from,
            To = target,
            Duration = Math.Clamp(gap / rate * factor,
                CameraFollowLaw.MinimumDurationSeconds,
                CameraFollowLaw.MaximumDurationSeconds),
            Delay = delay,
            ArmedDelay = delay,
            ArmedFactor = factor,
        };
    }
}

using System.Numerics;

namespace MSUIClient.Net;

/// <summary>Short observer extrapolation anchored by each authoritative airborne movement packet.</summary>
public sealed class ObservedJump
{
    // Movement::gravity in the inspected 1.12 core. The wire's z-speed is down-positive.
    public const float Gravity = 19.291105f;
    private readonly Vector3 _position;
    private readonly JumpInfo _jump;
    private readonly float _fallSeconds;
    private readonly bool _slowFall;
    private long _receivedMs;

    public ObservedJump(MovementInfo movement, long nowMs)
    {
        _position = movement.Position;
        _jump = movement.Jump ?? default;
        _fallSeconds = movement.FallTime / 1000f;
        _slowFall = (movement.Flags & (uint)MovementFlags.FeatherFalling) != 0;
        _receivedMs = nowMs;
    }

    public Vector3 Sample(long nowMs)
    {
        // Ordinary heartbeats refresh at 500ms. A lost stream must not extrapolate forever.
        float dt = Math.Clamp((nowMs - _receivedMs) / 1000f, 0, 1);
        float terminal = _slowFall ? 7f : 60.148003f;
        float startDown = Math.Min(terminal, _jump.ZSpeed + Gravity * _fallSeconds);
        float accelerating = Math.Clamp((terminal - startDown) / Gravity, 0, dt);
        float fall = startDown * accelerating + .5f * Gravity * accelerating * accelerating +
            terminal * (dt - accelerating);
        return _position + new Vector3(_jump.CosAngle * _jump.XySpeed * dt,
            _jump.SinAngle * _jump.XySpeed * dt, -fall);
    }

    public void RebaseAfterPause(long pausedMs) => _receivedMs += Math.Max(0, pausedMs);
}

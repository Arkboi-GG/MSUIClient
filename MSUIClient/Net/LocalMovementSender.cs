using System.Numerics;
using MSUIClient.Player;

namespace MSUIClient.Net;

/// <summary>
/// Diffs the controlled avatar's movement into the build-5875 MSG_MOVE_*
/// transition stream. The server extrapolates from these flags, so every start
/// edge needs a matching stop and moving poses need periodic heartbeats.
/// </summary>
public sealed class LocalMovementSender
{
    private const double HeartbeatSeconds = 0.5;
    private const uint ForwardBackMask = (uint)(MovementFlags.Forward | MovementFlags.Backward);
    private const uint StrafeMask = (uint)(MovementFlags.StrafeLeft | MovementFlags.StrafeRight);
    private const uint TurnMask = (uint)(MovementFlags.TurnLeft | MovementFlags.TurnRight);

    private uint _previousFlags;
    private float _previousFacing;
    private double _lastHeartbeat;
    private bool _initialized;
    private readonly List<Op> _lastUpdateOpcodes = [];

    /// <summary>
    /// External park (possession pending, freecam): flush one MSG_MOVE_STOP,
    /// then send nothing until unparked — same server-invisible behaviour as
    /// free-fly. The controller keeps simulating locally.
    /// </summary>
    public bool Parked { get; set; }

    public long PacketsSent { get; private set; }
    public Op? LastOpcode { get; private set; }
    public uint LastFlags => _previousFlags;
    public IReadOnlyList<Op> LastUpdateOpcodes => _lastUpdateOpcodes;

    public void Reset(float facing)
    {
        _previousFlags = 0;
        _previousFacing = Normalize(facing);
        _lastHeartbeat = 0;
        _initialized = true;
        PacketsSent = 0;
        LastOpcode = null;
    }

    /// <summary>
    /// Flush moving flags before acknowledging a server root. The subsequent root ack carries
    /// MOVEFLAG_ROOT itself; the ordinary movement stream remains parked until input is ungated.
    /// </summary>
    public void ParkForRoot(NetworkClient net, CharacterController controller)
        => Park(net, controller.Position, Normalize(controller.Yaw));

    /// <summary>Live mover state used by addressed movement acknowledgements.</summary>
    public MovementInfo SnapshotForAck(CharacterController controller, float jumpLaunchSpeed,
        MovementFlags extraFlags = MovementFlags.None)
    {
        const MovementFlags controllerStateMask = MovementFlags.WaterWalking |
            MovementFlags.FeatherFalling | MovementFlags.Hover | MovementFlags.Swimming;
        MovementFlags flags = (MovementFlags)_previousFlags & ~controllerStateMask;
        flags |= controller.GrantedMovementFlags;
        if (controller.Swimming) flags |= MovementFlags.Swimming;
        flags |= extraFlags;
            var info = MovementInfo.Create(controller.Position, Normalize(controller.Yaw), flags);
            if (controller.Swimming) info.Pitch = controller.SwimPitch;
        info.FallTime = (uint)Math.Clamp(MathF.Round(controller.FallTimeMs), 0f, uint.MaxValue);
        if ((flags & MovementFlags.Falling) != 0)
        {
            Vector2 horizontal = new(controller.HorizontalVelocity.X, controller.HorizontalVelocity.Y);
            float speed = horizontal.Length();
            float cos = speed > 1e-4f ? horizontal.X / speed : MathF.Cos(controller.Yaw);
            float sin = speed > 1e-4f ? horizontal.Y / speed : MathF.Sin(controller.Yaw);
            info.Jump = new JumpInfo(-MathF.Abs(jumpLaunchSpeed), cos, sin, speed);
        }
        return info;
    }

    public void Update(
        NetworkClient net,
        CharacterController controller,
        in MovementInput input,
        float turn,
        bool jumped,
        bool landed,
        bool startedFalling,
        uint fallTimeMs,
        float jumpLaunchSpeed,
        double nowSeconds)
    {
        _lastUpdateOpcodes.Clear();
        float facing = Normalize(controller.Yaw);
        if (!_initialized) Reset(facing);

        if (controller.Flying || Parked)
        {
            Park(net, controller.Position, facing);
            return;
        }

        MovementFlags flags = MovementFlags.None;
        if (input.Forward > 0.01f) flags |= MovementFlags.Forward;
        else if (input.Forward < -0.01f) flags |= MovementFlags.Backward;
        if (input.Strafe < -0.01f) flags |= MovementFlags.StrafeLeft;
        else if (input.Strafe > 0.01f) flags |= MovementFlags.StrafeRight;
        if (turn > 0.01f) flags |= MovementFlags.TurnLeft;
        else if (turn < -0.01f) flags |= MovementFlags.TurnRight;
        if (input.Walking) flags |= MovementFlags.WalkMode;
        flags |= controller.GrantedMovementFlags;
        if (controller.Swimming) flags |= MovementFlags.Swimming;
        if (!controller.Grounded)
        {
            if (!controller.Swimming)
            {
                flags |= MovementFlags.Falling;
                if (controller.FallTimeMs >= 500f) flags |= MovementFlags.FallingFar;
            }
        }

        uint current = (uint)flags;
        uint added = current & ~_previousFlags;
        uint removed = _previousFlags & ~current;
        bool falling = (current & (uint)MovementFlags.Falling) != 0;
        bool sent = false;

        MovementInfo Snapshot()
        {
            var info = MovementInfo.Create(controller.Position, facing, flags);
            if (controller.Swimming) info.Pitch = controller.SwimPitch;
            info.FallTime = fallTimeMs;
            if (falling)
            {
                Vector2 horizontal = new(controller.HorizontalVelocity.X, controller.HorizontalVelocity.Y);
                float speed = horizontal.Length();
                float cos = speed > 1e-4f ? horizontal.X / speed : MathF.Cos(facing);
                float sin = speed > 1e-4f ? horizontal.Y / speed : MathF.Sin(facing);
                // The 5875 movement wire stores jump z-speed down-positive.
                info.Jump = new JumpInfo(-MathF.Abs(jumpLaunchSpeed), cos, sin, speed);
            }
            return info;
        }

        void Send(Op opcode)
        {
            net.SendMovement(opcode, Snapshot());
            PacketsSent++;
            LastOpcode = opcode;
            _lastUpdateOpcodes.Add(opcode);
            sent = true;
        }

        // Arc lifecycle is independent of directional axes. A walk-off has no
        // jump edge, so an immediate heartbeat introduces the falling state.
        if (jumped) Send(Op.MSG_MOVE_JUMP);
        else if (landed) Send(Op.MSG_MOVE_FALL_LAND);
        else if (startedFalling) Send(Op.MSG_MOVE_HEARTBEAT);

        if ((added & (uint)MovementFlags.Swimming) != 0) Send(Op.MSG_MOVE_START_SWIM);
        else if ((removed & (uint)MovementFlags.Swimming) != 0) Send(Op.MSG_MOVE_STOP_SWIM);

        // Vanilla defers ordinary forward/strafe transitions while airborne;
        // the current bits ride the next turn/facing/landing packet instead.
        if (!falling)
        {
            if ((added & (uint)MovementFlags.Forward) != 0) Send(Op.MSG_MOVE_START_FORWARD);
            else if ((added & (uint)MovementFlags.Backward) != 0) Send(Op.MSG_MOVE_START_BACKWARD);
            else if ((removed & ForwardBackMask) != 0 && (current & ForwardBackMask) == 0) Send(Op.MSG_MOVE_STOP);

            if ((added & (uint)MovementFlags.StrafeLeft) != 0) Send(Op.MSG_MOVE_START_STRAFE_LEFT);
            else if ((added & (uint)MovementFlags.StrafeRight) != 0) Send(Op.MSG_MOVE_START_STRAFE_RIGHT);
            else if ((removed & StrafeMask) != 0 && (current & StrafeMask) == 0) Send(Op.MSG_MOVE_STOP_STRAFE);
        }

        if ((added & (uint)MovementFlags.TurnLeft) != 0) Send(Op.MSG_MOVE_START_TURN_LEFT);
        else if ((added & (uint)MovementFlags.TurnRight) != 0) Send(Op.MSG_MOVE_START_TURN_RIGHT);
        else if ((removed & TurnMask) != 0 && (current & TurnMask) == 0) Send(Op.MSG_MOVE_STOP_TURN);

        // Mouse facing is independent. Keyboard turn flags already tell an
        // observer how to rotate, so those frames suppress SET_FACING.
        if ((current & TurnMask) == 0 && facing != _previousFacing)
            Send(Op.MSG_MOVE_SET_FACING);

        if (!sent && current != 0 && !falling && nowSeconds - _lastHeartbeat >= HeartbeatSeconds)
            Send(Op.MSG_MOVE_HEARTBEAT);

        if (sent) _lastHeartbeat = nowSeconds;
        _previousFacing = facing;
        _previousFlags = current;
    }

    /// <summary>Flush stale locomotion before local developer free-fly takes over.</summary>
    private void Park(NetworkClient net, Vector3 position, float facing)
    {
        if (_previousFlags != 0)
        {
            net.SendMovement(Op.MSG_MOVE_STOP, MovementInfo.Create(position, facing, MovementFlags.None));
            PacketsSent++;
            LastOpcode = Op.MSG_MOVE_STOP;
            _lastUpdateOpcodes.Add(Op.MSG_MOVE_STOP);
        }
        _previousFlags = 0;
        _previousFacing = facing;
    }

    private static float Normalize(float radians)
    {
        const float tau = MathF.PI * 2f;
        return ((radians % tau) + tau) % tau;
    }
}

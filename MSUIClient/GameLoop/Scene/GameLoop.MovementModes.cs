using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private const MovementFlags GrantedMovementModeMask =
        MovementFlags.Root | MovementFlags.WaterWalking |
        MovementFlags.FeatherFalling | MovementFlags.Hover;

    // vmangos rejects a root acknowledgement that combines MOVEFLAG_ROOT with
    // translation/fall state. Turning is deliberately not included: vanilla's
    // MOVEFLAG_MASK_MOVING permits a rooted mover to turn in place.
    private const MovementFlags RootIncompatibleFlags =
        MovementFlags.Forward | MovementFlags.Backward |
        MovementFlags.StrafeLeft | MovementFlags.StrafeRight |
        MovementFlags.Falling | MovementFlags.FallingFar |
        MovementFlags.SplineEnabled;

    private void ApplyMovementModeChange(NetworkClient net, Op opcode, byte[] body)
    {
        MovementModeChange change = MovementModePackets.Parse(opcode, body);
        bool addressedClientBody = change.Guid != 0 &&
            (change.Guid == ControlledGuid || change.Guid == net.PlayerGuid);
        if (!addressedClientBody)
        {
            Console.WriteLine($"[movement] ignored {opcode} for mover 0x{change.Guid:X16}");
            return;
        }

        MovementFlags streamedModeFlags = ApplyStreamedMovementMode(change);
        if (ControllerOwnsMovementPose(change.Guid))
        {
            switch (change.Kind)
            {
                case MovementModeKind.Root:
                    _movementRooted = change.Apply;
                    if (change.Apply) _movementSender.ParkForRoot(net, _controller!);
                    break;
                case MovementModeKind.WaterWalk:
                    _controller!.WaterWalking = change.Apply;
                    break;
                case MovementModeKind.FeatherFall:
                    _controller!.FeatherFalling = change.Apply;
                    break;
                case MovementModeKind.Hover:
                    _controller!.Hovering = change.Apply;
                    break;
            }
        }

        // The force packet is mandatory-acknowledged even while the addressed
        // body is streamed. In that state the controller is the Free View rig
        // (or is parked during a hand-off), so its pose must never go on wire.
        if (!TrySnapshotMovementAck(change.Guid, streamedModeFlags, out MovementInfo ack))
        {
            Console.WriteLine($"[movement] cannot ack {opcode} for 0x{change.Guid:X16}: " +
                              "addressed body pose is unavailable");
            return;
        }

        if (change.Kind == MovementModeKind.Root)
            net.MoveRootAck(change.Guid, change.Counter, change.Apply, ack);
        else
            net.MoveModeAck(change.Guid, change.Kind, change.Counter, change.Apply, ack);

        Console.WriteLine($"[movement] {change.Kind} {(change.Apply ? "applied" : "removed")} " +
                          $"counter={change.Counter} ackFlags=0x{ack.Flags:X8}");
    }

    private bool ControllerOwnsMovementPose(ulong guid) =>
        guid != 0 && guid == ControlledGuid && ControllerOwnsControlledBodyPose;

    /// <summary>
    /// Snapshot an addressed mover without confusing the observer rig for its
    /// body. The caller supplies the honest parked/streamed flag word; locally
    /// embodied play retains the richer controller snapshot.
    /// </summary>
    private bool TrySnapshotMovementAck(
        ulong guid, MovementFlags streamedFlags, out MovementInfo ack)
    {
        if (ControllerOwnsMovementPose(guid))
        {
            MovementFlags rootFlag = _movementRooted
                ? MovementFlags.Root
                : MovementFlags.None;
            ack = _movementSender.SnapshotForAck(
                _controller!, _config.Movement.JumpVelocity, rootFlag);
            return true;
        }

        if (TryGetWorldBodyPose(guid, out WorldBodyPose body))
        {
            ack = MovementInfo.Create(body.Position, body.Orientation, streamedFlags);
            return true;
        }

        ack = null!;
        return false;
    }

    /// <summary>
    /// Keep the streamed body's presentation state current even when the local
    /// controller is detached from it. Returns just the granted mode bits used
    /// by vanilla's parked movement-mode acknowledgement.
    /// </summary>
    private MovementFlags ApplyStreamedMovementMode(MovementModeChange change)
    {
        MovementFlags changedFlag = MovementModePackets.Flag(change.Kind);
        if (!_entities.TryGet(change.Guid, out WorldEntity entity))
            return change.Apply ? changedFlag : MovementFlags.None;

        MovementFlags flags = (MovementFlags)entity.MoveFlags;
        flags = change.Apply ? flags | changedFlag : flags & ~changedFlag;
        if (change.Kind == MovementModeKind.Root && change.Apply)
        {
            flags &= ~RootIncompatibleFlags;
            _entities.StopMovement(change.Guid);
        }
        entity.MoveFlags = (uint)flags;
        return flags & GrantedMovementModeMask;
    }

    /// <summary>
    /// A detached body can gain or lose server-granted modes while the Free
    /// View rig remains in the sky. Adopt that body's streamed state on the
    /// single controller-seating edge; if the entity is unavailable, clear the
    /// previous mover's grants rather than leaking them into the new body.
    /// </summary>
    private void AdoptControlledMovementModes()
    {
        MovementFlags flags = _entities.TryGet(ControlledGuid, out WorldEntity entity)
            ? (MovementFlags)entity.MoveFlags & GrantedMovementModeMask
            : MovementFlags.None;

        _movementRooted = (flags & MovementFlags.Root) != 0;
        if (_controller is null) return;
        _controller.WaterWalking = (flags & MovementFlags.WaterWalking) != 0;
        _controller.FeatherFalling = (flags & MovementFlags.FeatherFalling) != 0;
        _controller.Hovering = (flags & MovementFlags.Hover) != 0;
        if (!_controller.WaterWalking)
            _controller.ExternalWalkableSurfaceZ = null;
    }

    private void ResetMovementModes()
    {
        _movementRooted = false;
        if (_controller is null) return;
        _controller.WaterWalking = false;
        _controller.FeatherFalling = false;
        _controller.Hovering = false;
        _controller.ExternalWalkableSurfaceZ = null;
    }
}

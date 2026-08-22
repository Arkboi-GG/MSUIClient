using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private void ApplyMovementModeChange(NetworkClient net, Op opcode, byte[] body)
    {
        MovementModeChange change = MovementModePackets.Parse(opcode, body);
        if (_controller is null || change.Guid != ControlledGuid)
        {
            Console.WriteLine($"[movement] ignored {opcode} for mover 0x{change.Guid:X16}");
            return;
        }

        switch (change.Kind)
        {
            case MovementModeKind.Root:
                _movementRooted = change.Apply;
                if (change.Apply) _movementSender.ParkForRoot(net, _controller);
                break;
            case MovementModeKind.WaterWalk:
                _controller.WaterWalking = change.Apply;
                break;
            case MovementModeKind.FeatherFall:
                _controller.FeatherFalling = change.Apply;
                break;
            case MovementModeKind.Hover:
                _controller.Hovering = change.Apply;
                break;
        }

        MovementFlags rootFlag = _movementRooted ? MovementFlags.Root : MovementFlags.None;
        MovementInfo ack = _movementSender.SnapshotForAck(
            _controller, _config.Movement.JumpVelocity, rootFlag);
        if (change.Kind == MovementModeKind.Root)
            net.MoveRootAck(change.Guid, change.Counter, change.Apply, ack);
        else
            net.MoveModeAck(change.Guid, change.Kind, change.Counter, change.Apply, ack);

        Console.WriteLine($"[movement] {change.Kind} {(change.Apply ? "applied" : "removed")} " +
                          $"counter={change.Counter} ackFlags=0x{ack.Flags:X8}");
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

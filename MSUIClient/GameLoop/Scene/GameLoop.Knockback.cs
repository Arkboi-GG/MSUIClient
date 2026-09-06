using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private void ApplyKnockbackCommand(NetworkClient net, byte[] body)
    {
        KnockbackCommand command = KnockbackPackets.ParseCommand(body);
        if (command.Guid == 0 || (command.Guid != ControlledGuid && command.Guid != net.PlayerGuid))
            return;
        if (!TrySnapshotMovementAck(command.Guid, MovementFlags.Falling, out MovementInfo ack))
            return;
        ack.Flags |= (uint)MovementFlags.Falling;
        ack.FallTime = 0;
        ack.Jump = command.Jump; // echo all four values, never recompute from the camera/input
        if (ControllerOwnsMovementPose(command.Guid))
            _controller!.ApplyKnockback(command.Jump);
        else
            _entities.ApplyRemotePlayerMove(command.Guid, ack, MovementInfo.ClientUptimeMs());
        net.KnockbackAck(command.Guid, command.Counter, ack);
    }

    private void ApplyKnockbackRelay(byte[] body)
    {
        KnockbackRelay relay = KnockbackPackets.ParseRelay(body);
        if (ControllerOwnsMovementPose(relay.Guid)) return;
        relay.Movement.Flags |= (uint)MovementFlags.Falling;
        relay.Movement.Jump = relay.Jump;
        _entities.ApplyRemotePlayerMove(relay.Guid, relay.Movement, MovementInfo.ClientUptimeMs());
    }
}

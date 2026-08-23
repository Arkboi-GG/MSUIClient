using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private void ApplyForceSpeedChange(NetworkClient net, Op opcode, byte[] body)
    {
        ForceSpeedChange change = MovementSpeedPackets.ParseForceSpeedChange(opcode, body);
        ApplyEntitySpeed(change.Guid, change.Kind, change.Speed);

        if (_controller is null || change.Guid != ControlledGuid)
        {
            Console.WriteLine($"[movement] applied foreign {change.Kind} speed " +
                              $"{change.Speed:F3} to mover 0x{change.Guid:X16}; no ack");
            return;
        }

        ApplyControlledSpeed(change.Kind, change.Speed);
        MovementInfo ack = _movementSender.SnapshotForAck(
            _controller, _config.Movement.JumpVelocity);
        net.ForceSpeedChangeAck(change.Guid, change.Kind, change.Counter, ack, change.Speed);
        Console.WriteLine($"[movement] force {change.Kind} speed {change.Speed:F3} " +
                          $"counter={change.Counter} ackFlags=0x{ack.Flags:X8}");
    }

    private void ApplyEntitySpeed(ulong guid, MovementSpeedKind kind, float speed)
    {
        if (!_entities.TryGet(guid, out WorldEntity entity)) return;
        if (entity.Speeds is not { Length: >= 6 })
            entity.Speeds = new float[6];
        entity.Speeds[(int)kind] = speed;
    }

    private void ApplyObserverSpeedChange(NetworkClient net, Op opcode, byte[] body)
    {
        ObserverSpeedChange change = MovementSpeedPackets.ParseObserverSpeedChange(opcode, body);
        // MOVE_SET carries a fresh player pose as well as the speed. SPLINE_SET changes only the
        // speed set of a unit already following its server path. Neither observer form is acked.
        if (change.Movement is { } movement &&
            change.Guid != net.PlayerGuid && change.Guid != ControlledGuid)
            _entities.ApplyRemotePlayerMove(
                change.Guid, movement, MovementInfo.ClientUptimeMs());
        ApplyEntitySpeed(change.Guid, change.Kind, change.Speed);
    }

    private void ApplyControlledSpeed(MovementSpeedKind kind, float speed)
    {
        _controller?.ApplyServerSpeed(kind, speed);
        if (kind == MovementSpeedKind.TurnRate) _serverTurnRate = speed;
    }

    private void SyncControlledSpeeds(WorldEntity entity)
    {
        if (entity.Speeds is not { Length: >= 6 } speeds) return;
        for (int i = 0; i < 6; i++)
            if (float.IsFinite(speeds[i]) && speeds[i] >= 0f)
                ApplyControlledSpeed((MovementSpeedKind)i, speeds[i]);
    }

    private void ResetControlledSpeeds()
    {
        _serverTurnRate = null;
        _controller?.ResetServerSpeeds();
    }
}

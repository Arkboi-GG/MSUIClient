using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private void ApplyForceSpeedChange(NetworkClient net, Op opcode, byte[] body)
    {
        ForceSpeedChange change = MovementSpeedPackets.ParseForceSpeedChange(opcode, body);
        ApplyEntitySpeed(change.Guid, change.Kind, change.Speed);

        bool addressedClientBody = change.Guid != 0 &&
            (change.Guid == ControlledGuid || change.Guid == net.PlayerGuid);
        if (!addressedClientBody)
        {
            Console.WriteLine($"[movement] applied foreign {change.Kind} speed " +
                              $"{change.Speed:F3} to mover 0x{change.Guid:X16}; no ack");
            return;
        }

        if (ControllerOwnsMovementPose(change.Guid))
            ApplyControlledSpeed(change.Kind, change.Speed);

        // Benilla's detached/pre-control path acknowledges a forced speed with
        // the parked body pose and flags 0. That is also the only safe payload
        // in Free View: the camera controller is not the addressed mover.
        if (!TrySnapshotMovementAck(change.Guid, MovementFlags.None, out MovementInfo ack))
        {
            Console.WriteLine($"[movement] cannot ack force {change.Kind} for " +
                              $"0x{change.Guid:X16}: addressed body pose is unavailable");
            return;
        }

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
        if (change.Movement is { } movement && !ControllerOwnsMovementPose(change.Guid))
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
        // UPDATE_OBJECT continues to refresh the streamed body while Free View
        // is active. Its speed array belongs to that entity, not to the fly rig.
        if (entity.Guid != ControlledGuid || !ControllerOwnsControlledBodyPose) return;
        if (entity.Speeds is not { Length: >= 6 } speeds) return;
        for (int i = 0; i < 6; i++)
            if (float.IsFinite(speeds[i]) && speeds[i] >= 0f)
                ApplyControlledSpeed((MovementSpeedKind)i, speeds[i]);
    }

    /// <summary>
    /// A newly embodied controller must start with the seated body's speed
    /// table, including changes received while that body was streamed. Reset
    /// first so a sparse/unavailable table cannot inherit the previous mover's
    /// mount, aura, or turn-rate overrides.
    /// </summary>
    private void AdoptControlledSpeeds()
    {
        ResetControlledSpeeds();
        if (_entities.TryGet(ControlledGuid, out WorldEntity entity))
            SyncControlledSpeeds(entity);
    }

    private void ResetControlledSpeeds()
    {
        _serverTurnRate = null;
        _controller?.ResetServerSpeeds();
    }
}

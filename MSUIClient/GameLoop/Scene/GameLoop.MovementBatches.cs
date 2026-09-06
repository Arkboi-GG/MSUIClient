using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private bool ApplyAdditionalBatchedMovement(CompressedMovementRecord record)
    {
        if (SplineMovementModePackets.IsOpcode(record.Opcode))
        {
            _entities.ApplySplineMovementMode(SplineMovementModePackets.Parse(record.Opcode, record.Body));
            return true;
        }
        if (record.Opcode == Op.MSG_MOVE_KNOCK_BACK)
        {
            ApplyKnockbackRelay(record.Body);
            return true;
        }
        if (record.Opcode == Op.MSG_MOVE_TELEPORT)
        {
            MovementRelay teleport = record.Relay ?? MovementRelayPackets.Parse(record.Opcode, record.Body);
            if (ControllerOwnsMovementPose(teleport.Guid)) ApplyServerAuthoredSelfMove(teleport);
            else _entities.ApplyRemoteTeleport(teleport.Guid, teleport.Movement, MovementInfo.ClientUptimeMs());
            return true;
        }
        return false;
    }
}

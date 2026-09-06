namespace MSUIClient.Net;

public readonly record struct MovementRelay(ulong Guid, MovementInfo Movement);

/// <summary>
/// The bidirectional MSG_MOVE_* family is a bare MovementInfo outbound and a packed mover GUID plus
/// MovementInfo when realmd rebroadcasts another player's motion to observers.
/// </summary>
public static class MovementRelayPackets
{
    public static bool IsRelayOpcode(Op opcode) => opcode is
        Op.MSG_MOVE_START_FORWARD or Op.MSG_MOVE_START_BACKWARD or Op.MSG_MOVE_STOP or
        Op.MSG_MOVE_START_STRAFE_LEFT or Op.MSG_MOVE_START_STRAFE_RIGHT or
        Op.MSG_MOVE_STOP_STRAFE or Op.MSG_MOVE_JUMP or Op.MSG_MOVE_START_TURN_LEFT or
        Op.MSG_MOVE_START_TURN_RIGHT or Op.MSG_MOVE_STOP_TURN or
        Op.MSG_MOVE_START_PITCH_UP or Op.MSG_MOVE_START_PITCH_DOWN or
        Op.MSG_MOVE_STOP_PITCH or Op.MSG_MOVE_SET_RUN_MODE or Op.MSG_MOVE_SET_WALK_MODE or
        Op.MSG_MOVE_FALL_LAND or Op.MSG_MOVE_START_SWIM or Op.MSG_MOVE_STOP_SWIM or
        Op.MSG_MOVE_SET_FACING or Op.MSG_MOVE_SET_PITCH or Op.MSG_MOVE_HEARTBEAT or
        Op.MSG_MOVE_ROOT or Op.MSG_MOVE_UNROOT or Op.MSG_MOVE_HOVER or
        Op.MSG_MOVE_FEATHER_FALL or Op.MSG_MOVE_WATER_WALK or Op.MSG_MOVE_TELEPORT;

    public static MovementRelay Parse(Op opcode, byte[] body)
    {
        if (!IsRelayOpcode(opcode))
            throw new InvalidDataException($"{opcode} is not a relayed MSG_MOVE_* opcode");
        var reader = new PacketReader(body);
        ulong guid = reader.ReadPackedGuid();
        MovementInfo movement = MovementInfo.Read(reader);
        if (reader.Remaining != 0)
            throw new InvalidDataException($"{opcode} relay has {reader.Remaining} trailing byte(s)");
        return new MovementRelay(guid, movement);
    }
}

namespace MSUIClient.Net;

/// <summary>Build-5875 movement-speed axis; order matches the LIVING speed array.</summary>
public enum MovementSpeedKind
{
    Walk,
    Run,
    RunBack,
    Swim,
    SwimBack,
    TurnRate,
}

public readonly record struct ForceSpeedChange(
    ulong Guid, MovementSpeedKind Kind, uint Counter, float Speed);

public readonly record struct ObserverSpeedChange(
    ulong Guid, MovementSpeedKind Kind, float Speed, MovementInfo? Movement);

public static class MovementSpeedPackets
{
    public static bool IsForceSpeedOpcode(Op opcode) => TryKind(opcode, out _);

    public static ForceSpeedChange ParseForceSpeedChange(Op opcode, byte[] body)
    {
        if (!TryKind(opcode, out MovementSpeedKind kind))
            throw new InvalidDataException($"{opcode} is not a force-speed-change opcode");
        var reader = new PacketReader(body);
        ulong guid = reader.ReadPackedGuid();
        uint counter = reader.ReadU32();
        float speed = reader.ReadF32();
        if (reader.Remaining != 0)
            throw new InvalidDataException($"{opcode} has {reader.Remaining} trailing byte(s)");
        if (!float.IsFinite(speed) || speed < 0f)
            throw new InvalidDataException($"{opcode} carries invalid speed {speed}");
        return new(guid, kind, counter, speed);
    }

    public static ObserverSpeedChange ParseObserverSpeedChange(Op opcode, byte[] body)
    {
        bool spline = TrySplineKind(opcode, out MovementSpeedKind kind);
        bool moveSet = !spline && TryMoveSetKind(opcode, out kind);
        if (!spline && !moveSet)
            throw new InvalidDataException($"{opcode} is not an observer speed-change opcode");

        var reader = new PacketReader(body);
        ulong guid = reader.ReadPackedGuid();
        MovementInfo? movement = moveSet ? MovementInfo.Read(reader) : null;
        float speed = reader.ReadF32();
        if (reader.Remaining != 0)
            throw new InvalidDataException($"{opcode} has {reader.Remaining} trailing byte(s)");
        if (!float.IsFinite(speed) || speed < 0f)
            throw new InvalidDataException($"{opcode} carries invalid speed {speed}");
        return new(guid, kind, speed, movement);
    }

    public static Op AckOpcode(MovementSpeedKind kind) => kind switch
    {
        MovementSpeedKind.Walk => Op.CMSG_FORCE_WALK_SPEED_CHANGE_ACK,
        MovementSpeedKind.Run => Op.CMSG_FORCE_RUN_SPEED_CHANGE_ACK,
        MovementSpeedKind.RunBack => Op.CMSG_FORCE_RUN_BACK_SPEED_CHANGE_ACK,
        MovementSpeedKind.Swim => Op.CMSG_FORCE_SWIM_SPEED_CHANGE_ACK,
        MovementSpeedKind.SwimBack => Op.CMSG_FORCE_SWIM_BACK_SPEED_CHANGE_ACK,
        MovementSpeedKind.TurnRate => Op.CMSG_FORCE_TURN_RATE_CHANGE_ACK,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static bool TryKind(Op opcode, out MovementSpeedKind kind)
    {
        kind = opcode switch
        {
            Op.SMSG_FORCE_WALK_SPEED_CHANGE => MovementSpeedKind.Walk,
            Op.SMSG_FORCE_RUN_SPEED_CHANGE => MovementSpeedKind.Run,
            Op.SMSG_FORCE_RUN_BACK_SPEED_CHANGE => MovementSpeedKind.RunBack,
            Op.SMSG_FORCE_SWIM_SPEED_CHANGE => MovementSpeedKind.Swim,
            Op.SMSG_FORCE_SWIM_BACK_SPEED_CHANGE => MovementSpeedKind.SwimBack,
            Op.SMSG_FORCE_TURN_RATE_CHANGE => MovementSpeedKind.TurnRate,
            _ => default,
        };
        return opcode is Op.SMSG_FORCE_WALK_SPEED_CHANGE or Op.SMSG_FORCE_RUN_SPEED_CHANGE or
            Op.SMSG_FORCE_RUN_BACK_SPEED_CHANGE or Op.SMSG_FORCE_SWIM_SPEED_CHANGE or
            Op.SMSG_FORCE_SWIM_BACK_SPEED_CHANGE or Op.SMSG_FORCE_TURN_RATE_CHANGE;
    }


    private static bool TrySplineKind(Op opcode, out MovementSpeedKind kind)
    {
        kind = opcode switch
        {
            Op.SMSG_SPLINE_SET_WALK_SPEED => MovementSpeedKind.Walk,
            Op.SMSG_SPLINE_SET_RUN_SPEED => MovementSpeedKind.Run,
            Op.SMSG_SPLINE_SET_RUN_BACK_SPEED => MovementSpeedKind.RunBack,
            Op.SMSG_SPLINE_SET_SWIM_SPEED => MovementSpeedKind.Swim,
            Op.SMSG_SPLINE_SET_SWIM_BACK_SPEED => MovementSpeedKind.SwimBack,
            Op.SMSG_SPLINE_SET_TURN_RATE => MovementSpeedKind.TurnRate,
            _ => default,
        };
        return opcode is Op.SMSG_SPLINE_SET_WALK_SPEED or Op.SMSG_SPLINE_SET_RUN_SPEED or
            Op.SMSG_SPLINE_SET_RUN_BACK_SPEED or Op.SMSG_SPLINE_SET_SWIM_SPEED or
            Op.SMSG_SPLINE_SET_SWIM_BACK_SPEED or Op.SMSG_SPLINE_SET_TURN_RATE;
    }

    private static bool TryMoveSetKind(Op opcode, out MovementSpeedKind kind)
    {
        kind = opcode switch
        {
            Op.MSG_MOVE_SET_WALK_SPEED => MovementSpeedKind.Walk,
            Op.MSG_MOVE_SET_RUN_SPEED => MovementSpeedKind.Run,
            Op.MSG_MOVE_SET_RUN_BACK_SPEED => MovementSpeedKind.RunBack,
            Op.MSG_MOVE_SET_SWIM_SPEED => MovementSpeedKind.Swim,
            Op.MSG_MOVE_SET_SWIM_BACK_SPEED => MovementSpeedKind.SwimBack,
            Op.MSG_MOVE_SET_TURN_RATE => MovementSpeedKind.TurnRate,
            _ => default,
        };
        return opcode is Op.MSG_MOVE_SET_WALK_SPEED or Op.MSG_MOVE_SET_RUN_SPEED or
            Op.MSG_MOVE_SET_RUN_BACK_SPEED or Op.MSG_MOVE_SET_SWIM_SPEED or
            Op.MSG_MOVE_SET_SWIM_BACK_SPEED or Op.MSG_MOVE_SET_TURN_RATE;
    }
}

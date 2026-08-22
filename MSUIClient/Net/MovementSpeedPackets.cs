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
}

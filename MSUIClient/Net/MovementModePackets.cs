namespace MSUIClient.Net;

public enum MovementModeKind { Root, WaterWalk, FeatherFall, Hover }

public readonly record struct MovementModeChange(
    ulong Guid, MovementModeKind Kind, uint Counter, bool Apply);

public static class MovementModePackets
{
    public static MovementModeChange Parse(Op opcode, byte[] body)
    {
        (MovementModeKind kind, bool apply) = opcode switch
        {
            Op.SMSG_FORCE_MOVE_ROOT => (MovementModeKind.Root, true),
            Op.SMSG_FORCE_MOVE_UNROOT => (MovementModeKind.Root, false),
            Op.SMSG_MOVE_WATER_WALK => (MovementModeKind.WaterWalk, true),
            Op.SMSG_MOVE_LAND_WALK => (MovementModeKind.WaterWalk, false),
            Op.SMSG_MOVE_FEATHER_FALL => (MovementModeKind.FeatherFall, true),
            Op.SMSG_MOVE_NORMAL_FALL => (MovementModeKind.FeatherFall, false),
            Op.SMSG_MOVE_SET_HOVER => (MovementModeKind.Hover, true),
            Op.SMSG_MOVE_UNSET_HOVER => (MovementModeKind.Hover, false),
            _ => throw new InvalidDataException($"{opcode} is not a movement-mode opcode"),
        };
        var reader = new PacketReader(body);
        ulong guid = reader.ReadPackedGuid();
        uint counter = reader.ReadU32();
        if (reader.Remaining != 0)
            throw new InvalidDataException($"{opcode} has {reader.Remaining} trailing byte(s)");
        return new(guid, kind, counter, apply);
    }

    public static MovementFlags Flag(MovementModeKind kind) => kind switch
    {
        MovementModeKind.Root => MovementFlags.Root,
        MovementModeKind.WaterWalk => MovementFlags.WaterWalking,
        MovementModeKind.FeatherFall => MovementFlags.FeatherFalling,
        MovementModeKind.Hover => MovementFlags.Hover,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static Op AckOpcode(MovementModeKind kind, bool apply) => kind switch
    {
        MovementModeKind.Root => apply
            ? Op.CMSG_FORCE_MOVE_ROOT_ACK : Op.CMSG_FORCE_MOVE_UNROOT_ACK,
        MovementModeKind.WaterWalk => Op.CMSG_MOVE_WATER_WALK_ACK,
        MovementModeKind.FeatherFall => Op.CMSG_MOVE_FEATHER_FALL_ACK,
        MovementModeKind.Hover => Op.CMSG_MOVE_HOVER_ACK,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}

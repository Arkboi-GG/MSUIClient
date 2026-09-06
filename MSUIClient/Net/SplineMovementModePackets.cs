namespace MSUIClient.Net;

public readonly record struct SplineMovementModeChange(ulong Guid, MovementFlags Flag, bool Apply);

/// <summary>Server-driven 5875 mode edges carry only a packed GUID, with no counter or ACK.</summary>
public static class SplineMovementModePackets
{
    public static bool IsOpcode(Op opcode) => opcode is
        Op.SMSG_SPLINE_MOVE_ROOT or Op.SMSG_SPLINE_MOVE_UNROOT or
        Op.SMSG_SPLINE_MOVE_FEATHER_FALL or Op.SMSG_SPLINE_MOVE_NORMAL_FALL or
        Op.SMSG_SPLINE_MOVE_SET_HOVER or Op.SMSG_SPLINE_MOVE_UNSET_HOVER or
        Op.SMSG_SPLINE_MOVE_WATER_WALK or Op.SMSG_SPLINE_MOVE_LAND_WALK or
        Op.SMSG_SPLINE_MOVE_SET_WALK_MODE or Op.SMSG_SPLINE_MOVE_SET_RUN_MODE;

    public static SplineMovementModeChange Parse(Op opcode, byte[] body)
    {
        (MovementFlags flag, bool apply) = opcode switch
        {
            Op.SMSG_SPLINE_MOVE_ROOT => (MovementFlags.Root, true),
            Op.SMSG_SPLINE_MOVE_UNROOT => (MovementFlags.Root, false),
            Op.SMSG_SPLINE_MOVE_FEATHER_FALL => (MovementFlags.FeatherFalling, true),
            Op.SMSG_SPLINE_MOVE_NORMAL_FALL => (MovementFlags.FeatherFalling, false),
            Op.SMSG_SPLINE_MOVE_SET_HOVER => (MovementFlags.Hover, true),
            Op.SMSG_SPLINE_MOVE_UNSET_HOVER => (MovementFlags.Hover, false),
            Op.SMSG_SPLINE_MOVE_WATER_WALK => (MovementFlags.WaterWalking, true),
            Op.SMSG_SPLINE_MOVE_LAND_WALK => (MovementFlags.WaterWalking, false),
            Op.SMSG_SPLINE_MOVE_SET_WALK_MODE => (MovementFlags.WalkMode, true),
            Op.SMSG_SPLINE_MOVE_SET_RUN_MODE => (MovementFlags.WalkMode, false),
            _ => throw new InvalidDataException($"{opcode} is not a spline mode edge"),
        };
        var reader = new PacketReader(body);
        ulong guid = reader.ReadPackedGuid();
        if (reader.Remaining != 0) throw new InvalidDataException($"{opcode} trailing bytes");
        return new(guid, flag, apply);
    }
}

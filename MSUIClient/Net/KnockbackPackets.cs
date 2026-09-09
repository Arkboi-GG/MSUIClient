namespace MSUIClient.Net;

public readonly record struct KnockbackCommand(ulong Guid, uint Counter, JumpInfo Jump);
public readonly record struct KnockbackRelay(ulong Guid, MovementInfo Movement, JumpInfo Jump);

public static class KnockbackPackets
{
    private static JumpInfo ReadImpulse(PacketReader r)
    {
        float cos = r.ReadF32(), sin = r.ReadF32(), xy = r.ReadF32(), z = r.ReadF32();
        if (!float.IsFinite(cos) || !float.IsFinite(sin) || !float.IsFinite(xy) ||
            !float.IsFinite(z) || xy < 0)
            throw new InvalidDataException("invalid knockback impulse");
        return new JumpInfo(z, cos, sin, xy);
    }

    public static KnockbackCommand ParseCommand(byte[] body)
    {
        var r = new PacketReader(body);
        ulong guid = r.ReadPackedGuid();
        uint counter = r.ReadU32();
        JumpInfo jump = ReadImpulse(r);
        if (r.Remaining != 0) throw new InvalidDataException("knockback command trailing bytes");
        return new(guid, counter, jump);
    }

    public static KnockbackRelay ParseRelay(byte[] body)
    {
        var r = new PacketReader(body);
        ulong guid = r.ReadPackedGuid();
        MovementInfo movement = MovementInfo.Read(r);
        JumpInfo jump = ReadImpulse(r);
        if (r.Remaining != 0) throw new InvalidDataException("knockback relay trailing bytes");
        return new(guid, movement, jump);
    }

    public static byte[] BuildAck(ulong guid, uint counter, MovementInfo movement)
    {
        var w = new PacketWriter();
        w.WriteU64(guid); // ACK uses full GUID, command/relay use packed GUID
        w.WriteU32(counter);
        movement.Write(w);
        return w.ToArray();
    }
}

namespace MSUIClient.Net;

/// <summary>1.12 SMSG_AREA_SPIRIT_HEALER_TIME; the Core channel timer is time remaining.</summary>
public readonly record struct AreaSpiritHealerPacket(ulong Guide, uint RemainingMilliseconds)
{
    public static AreaSpiritHealerPacket Parse(byte[] body)
    {
        if (body.Length != 12) throw new InvalidDataException("Invalid area spirit healer timer length");
        var reader = new PacketReader(body);
        ulong guide = reader.ReadU64();
        if (guide == 0) throw new InvalidDataException("Missing area spirit healer GUID");
        return new(guide, reader.ReadU32());
    }
}

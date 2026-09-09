namespace MSUIClient.Net;

public readonly record struct ItemTimePacket(ulong ItemGuid, uint Seconds);

public static class ItemTimePackets
{
    public static ItemTimePacket Parse(byte[] body)
    {
        if (body.Length != 12) throw new InvalidDataException("bad SMSG_ITEM_TIME_UPDATE body");
        var reader = new PacketReader(body);
        return new(reader.ReadU64(), reader.ReadU32());
    }
}

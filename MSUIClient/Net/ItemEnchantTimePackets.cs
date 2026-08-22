namespace MSUIClient.Net;

public readonly record struct ItemEnchantTimePacket(
    ulong ItemGuid, uint Slot, uint Seconds, ulong PlayerGuid);

public static class ItemEnchantTimePackets
{
    public static ItemEnchantTimePacket Parse(byte[] body)
    {
        if (body.Length != 24)
            throw new InvalidDataException(
                $"SMSG_ITEM_ENCHANT_TIME_UPDATE expected 24 bytes, got {body.Length}");
        var reader = new PacketReader(body);
        return new(reader.ReadU64(), reader.ReadU32(), reader.ReadU32(), reader.ReadU64());
    }
}

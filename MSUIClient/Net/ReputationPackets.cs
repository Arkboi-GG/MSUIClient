namespace MSUIClient.Net;

public readonly record struct FactionAtWarChange(uint Index, bool AtWar);

public static class ReputationPackets
{
    public static FactionAtWarChange ParseAtWar(byte[] body)
    {
        var reader = new PacketReader(body);
        uint index = reader.ReadU32();
        byte flags = reader.ReadU8();
        if (index >= 64 || (flags & ~2) != 0 || reader.Remaining != 0)
            throw new InvalidDataException("invalid faction-at-war update");
        return new(index, (flags & 2) != 0);
    }
}

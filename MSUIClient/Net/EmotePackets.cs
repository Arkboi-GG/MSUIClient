namespace MSUIClient.Net;

public readonly record struct EmotePacket(uint EmoteId, ulong UnitGuid);

public static class EmotePackets
{
    /// <summary>SMSG_EMOTE is exactly u32 Emotes.dbc id followed by raw u64 unit guid.</summary>
    public static EmotePacket Parse(byte[] body)
    {
        if (body.Length != 12)
            throw new InvalidDataException($"SMSG_EMOTE body must be 12 bytes, got {body.Length}");
        var r = new PacketReader(body);
        return new(r.ReadU32(), r.ReadU64());
    }
}

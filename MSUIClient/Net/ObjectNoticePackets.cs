namespace MSUIClient.Net;

public static class ObjectNoticePackets
{
    public static ulong ParseGuid(byte[] body, Op opcode)
    {
        if (body.Length != 8) throw new InvalidDataException($"bad {opcode} GUID body");
        return new PacketReader(body).ReadU64();
    }
}

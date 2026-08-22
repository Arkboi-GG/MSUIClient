namespace MSUIClient.Net;

public static class MountResultPackets
{
    public static uint ParseCode(byte[] body, string opcode)
    {
        if (body.Length != 4)
            throw new InvalidDataException($"{opcode} expected 4 bytes, got {body.Length}");
        return new PacketReader(body).ReadU32();
    }
}

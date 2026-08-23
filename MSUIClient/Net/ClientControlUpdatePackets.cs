namespace MSUIClient.Net;

public readonly record struct ClientControlUpdatePacket(ulong Mover, bool AllowMove);

public static class ClientControlUpdatePackets
{
    /// <summary>SMSG_CLIENT_CONTROL_UPDATE: packed mover GUID followed by one allowMove byte.</summary>
    public static ClientControlUpdatePacket Parse(byte[] body)
    {
        var reader = new PacketReader(body);
        var packet = new ClientControlUpdatePacket(reader.ReadPackedGuid(), reader.ReadU8() != 0);
        if (reader.Remaining != 0)
            throw new InvalidDataException(
                $"SMSG_CLIENT_CONTROL_UPDATE has {reader.Remaining} trailing byte(s)");
        return packet;
    }
}

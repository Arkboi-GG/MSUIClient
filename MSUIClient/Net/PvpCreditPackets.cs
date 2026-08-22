namespace MSUIClient.Net;

public readonly record struct PvpCreditPacket(int Honor, ulong VictimGuid, uint VictimRank);

public static class PvpCreditPackets
{
    /// <summary>SMSG_PVP_CREDIT: i32 honor, full victim GUID, u32 victim rank.</summary>
    public static PvpCreditPacket Parse(byte[] body)
    {
        if (body.Length != 16)
            throw new InvalidDataException(
                $"SMSG_PVP_CREDIT expected 16 bytes, got {body.Length}");
        var reader = new PacketReader(body);
        return new(reader.ReadI32(), reader.ReadU64(), reader.ReadU32());
    }

    public static string FloatingText(PvpCreditPacket packet) => $"Honor: {packet.Honor}";
}

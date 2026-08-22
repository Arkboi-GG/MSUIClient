namespace MSUIClient.Net;

public readonly record struct AiReactionPacket(ulong UnitGuid, uint Reaction);

public static class AiReactionPackets
{
    public const uint Alert = 0;
    public const uint Hostile = 2;

    public static AiReactionPacket Parse(ReadOnlySpan<byte> body)
    {
        if (body.Length != 12)
            throw new InvalidDataException($"SMSG_AI_REACTION body must be 12 bytes, got {body.Length}");
        var reader = new PacketReader(body.ToArray());
        return new(reader.ReadU64(), reader.ReadU32());
    }

    public static bool Audible(uint reaction) => reaction is Alert or Hostile;
}

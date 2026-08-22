namespace MSUIClient.Net;

public static class MountSpecialPackets
{
    /// <summary>SMSG_MOUNTSPECIAL_ANIM is exactly one raw (not packed) u64 rider guid.</summary>
    public static ulong ParseGuid(byte[] body)
    {
        if (body.Length != 8)
            throw new InvalidDataException($"SMSG_MOUNTSPECIAL_ANIM body must be 8 bytes, got {body.Length}");
        return new PacketReader(body).ReadU64();
    }
}

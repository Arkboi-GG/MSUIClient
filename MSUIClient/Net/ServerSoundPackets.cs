namespace MSUIClient.Net;

public readonly record struct ServerSoundPacket(uint SoundId);
public readonly record struct ServerObjectSoundPacket(uint SoundId, ulong SourceGuid);

/// <summary>
/// Strict build-5875 decoders for the three server-scripted SoundEntries triggers.
/// The object-sound GUID is the full raw u64 written by
/// WorldObject::PlayDistanceSound, not a packed GUID.
/// </summary>
public static class ServerSoundPackets
{
    public static ServerSoundPacket ParseSound(byte[] body)
        => ParseSingleKit(body, "SMSG_PLAY_SOUND");

    public static ServerSoundPacket ParseMusic(byte[] body)
        => ParseSingleKit(body, "SMSG_PLAY_MUSIC");

    public static ServerObjectSoundPacket ParseObjectSound(byte[] body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (body.Length != 12)
            throw new InvalidDataException(
                $"SMSG_PLAY_OBJECT_SOUND body must be 12 bytes, got {body.Length}");
        var reader = new PacketReader(body);
        return new ServerObjectSoundPacket(reader.ReadU32(), reader.ReadU64());
    }

    private static ServerSoundPacket ParseSingleKit(byte[] body, string opcode)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (body.Length != 4)
            throw new InvalidDataException($"{opcode} body must be 4 bytes, got {body.Length}");
        return new ServerSoundPacket(new PacketReader(body).ReadU32());
    }
}

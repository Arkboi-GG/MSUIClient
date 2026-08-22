namespace MSUIClient.Net;

public readonly record struct WeatherPacket(
    uint WeatherType, float Grade, uint SoundId, bool Instant);

public static class WeatherPackets
{
    /// <summary>
    /// SMSG_WEATHER is exactly u32 type, f32 grade, u32 SoundEntries kit and
    /// u8 instant. The final byte is nonzero for an instant visual transition;
    /// grade never scales the ambience volume.
    /// </summary>
    public static WeatherPacket Parse(byte[] body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (body.Length != 13)
            throw new InvalidDataException(
                $"SMSG_WEATHER body must be 13 bytes, got {body.Length}");
        var reader = new PacketReader(body);
        return new WeatherPacket(reader.ReadU32(), reader.ReadF32(), reader.ReadU32(),
            reader.ReadU8() != 0);
    }
}

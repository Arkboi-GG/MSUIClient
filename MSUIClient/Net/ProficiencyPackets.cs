namespace MSUIClient.Net;

public readonly record struct ProficiencyPacket(byte ItemClass, uint SubclassMask);

public static class ProficiencyPackets
{
    /// <summary>
    /// SMSG_SET_PROFICIENCY: one item class followed by its complete allowed-subclass bitmask.
    /// The server sends these at login and again when training changes the character.
    /// </summary>
    public static ProficiencyPacket Parse(byte[] body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var reader = new PacketReader(body);
        var packet = new ProficiencyPacket(reader.ReadU8(), reader.ReadU32());
        if (reader.Remaining != 0)
            throw new InvalidDataException(
                $"SMSG_SET_PROFICIENCY has {reader.Remaining} trailing byte(s)");
        return packet;
    }
}

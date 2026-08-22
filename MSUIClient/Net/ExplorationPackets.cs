namespace MSUIClient.Net;

public readonly record struct ExplorationExperiencePacket(uint AreaId, uint Experience);

public static class ExplorationPackets
{
    /// <summary>SMSG_EXPLORATION_EXPERIENCE: AreaTable row id + awarded XP.</summary>
    public static ExplorationExperiencePacket Parse(byte[] body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var reader = new PacketReader(body);
        var packet = new ExplorationExperiencePacket(reader.ReadU32(), reader.ReadU32());
        if (reader.Remaining != 0)
            throw new InvalidDataException(
                $"SMSG_EXPLORATION_EXPERIENCE has {reader.Remaining} trailing byte(s)");
        return packet;
    }
}

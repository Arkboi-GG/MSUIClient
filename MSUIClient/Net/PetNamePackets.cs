namespace MSUIClient.Net;

public readonly record struct PetNameQueryResponse(uint PetNumber, string Name, uint Timestamp);

public static class PetNamePackets
{
    /// <summary>SMSG_PET_NAME_QUERY_RESPONSE: u32 pet number, cstring name, u32 cache timestamp.</summary>
    public static PetNameQueryResponse ParseResponse(byte[] body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var reader = new PacketReader(body);
        uint petNumber = reader.ReadU32();
        string name = reader.ReadCString();
        uint timestamp = reader.ReadU32();
        if (reader.Remaining != 0)
            throw new InvalidDataException(
                $"pet-name response has {reader.Remaining} trailing byte(s)");
        return new(petNumber, name, timestamp);
    }
}

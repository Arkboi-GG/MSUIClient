namespace MSUIClient.Net;

public readonly record struct PlayerTraits(byte Race, byte Class, byte Gender);

public readonly record struct PlayerNameQueryResponse(
    ulong Guid, string Name, string Realm, PlayerTraits Traits);

public static class PlayerNamePackets
{
    /// <summary>
    /// SMSG_NAME_QUERY_RESPONSE: full guid, name and realm C-strings, then
    /// race/gender/class as u32 values. The realm string is present in 1.12.1
    /// even though a single-realm server normally writes it empty.
    /// </summary>
    public static PlayerNameQueryResponse ParseResponse(byte[] body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var reader = new PacketReader(body);
        ulong guid = reader.ReadU64();
        string name = reader.ReadCString();
        string realm = reader.ReadCString();
        uint race = reader.ReadU32();
        uint gender = reader.ReadU32();
        uint @class = reader.ReadU32();
        if (reader.Remaining != 0)
            throw new InvalidDataException(
                $"player-name response has {reader.Remaining} trailing byte(s)");
        if (race > byte.MaxValue || gender > byte.MaxValue || @class > byte.MaxValue)
            throw new InvalidDataException(
                $"player-name response trait exceeds byte range: race={race}, gender={gender}, class={@class}");
        return new(guid, name, realm,
            new PlayerTraits((byte)race, (byte)@class, (byte)gender));
    }
}

namespace MSUIClient.Net;

/// <summary>
/// The UI-visible head of a successful build-5875 SMSG_CREATURE_QUERY_RESPONSE.
/// The cache is keyed by template entry, not by one streamed spawn GUID.
/// </summary>
public sealed record CreatureQueryInfo(
    string Name,
    string? Subname,
    uint TypeFlags,
    uint CreatureType,
    uint PetFamily,
    uint Rank,
    bool Civilian,
    bool RacialLeader);

/// <summary>
/// A decoded creature-query response. Info is null when the server returned the
/// entry-with-high-bit miss shape; that negative answer is still cacheable.
/// </summary>
public readonly record struct CreatureQueryResponse(
    uint Entry,
    CreatureQueryInfo? Info);

public static class CreatureQueryPacket
{
    /// <summary>
    /// Parse the exact 1.12.1 response body. A miss is only the high-bit entry.
    /// A hit carries five C strings, seven u32 tail fields, then two flag bytes.
    /// </summary>
    public static CreatureQueryResponse Parse(byte[] body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var r = new PacketReader(body);
        uint rawEntry = r.ReadU32();
        uint entry = rawEntry & 0x7fff_ffffu;
        if ((rawEntry & 0x8000_0000u) != 0)
        {
            if (r.Remaining != 0)
                throw new InvalidDataException(
                    $"creature-query miss has {r.Remaining} trailing byte(s)");
            return new(entry, null);
        }

        string name = r.ReadCString();
        r.ReadCString(); // name2, empty in build 5875
        r.ReadCString(); // name3
        r.ReadCString(); // name4
        string subname = r.ReadCString();
        uint typeFlags = r.ReadU32();
        uint creatureType = r.ReadU32();
        uint petFamily = r.ReadU32();
        uint rank = r.ReadU32();
        r.ReadU32(); // unknown
        r.ReadU32(); // pet spell-list id
        r.ReadU32(); // display id
        bool civilian = r.ReadU8() != 0;
        bool racialLeader = r.ReadU8() != 0;
        if (r.Remaining != 0)
            throw new InvalidDataException(
                $"creature-query hit has {r.Remaining} trailing byte(s)");

        return new(entry, new CreatureQueryInfo(name,
            subname.Length == 0 ? null : subname,
            typeFlags, creatureType, petFamily, rank, civilian, racialLeader));
    }
}

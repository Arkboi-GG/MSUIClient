namespace MSUIClient.Net;

/// <summary>Build-5875 friend-status wire and local delta law.</summary>
public static class SocialPackets
{
    public const int WhoMaxZones = 10;
    public const int WhoMaxSearchTerms = 4;
    public const byte FriendOnline = 0x02;
    public const byte FriendOffline = 0x03;
    public const byte FriendRemoved = 0x05;
    public const byte FriendAddedOnline = 0x06;
    public const byte FriendAddedOffline = 0x07;
    public const byte IgnoreAdded = 0x0f;
    public const byte IgnoreRemoved = 0x10;

    public readonly record struct FriendEntry(
        ulong Guid, byte Status, uint Area, uint Level, uint Class);
    public readonly record struct Online(byte Status, uint Area, uint Level, uint Class);
    public readonly record struct FriendStatus(byte Result, ulong Guid, Online? Presence);
    public sealed record WhoRequest(
        uint LevelMin,
        uint LevelMax,
        string PlayerName,
        string GuildName,
        uint RaceMask,
        uint ClassMask,
        IReadOnlyList<uint> Zones,
        IReadOnlyList<string> SearchTerms)
    {
        public static WhoRequest Default { get; } =
            new(0, 100, "", "", uint.MaxValue, uint.MaxValue, [], []);
    }

    public static FriendStatus ParseFriendStatus(byte[] body)
    {
        if (body.Length < 9)
            throw new InvalidDataException(
                $"SMSG_FRIEND_STATUS body is {body.Length} bytes, expected at least 9");
        var r = new PacketReader(body);
        byte result = r.ReadU8();
        ulong guid = r.ReadU64();
        Online? online = result is FriendOnline or FriendAddedOnline
            ? new(r.ReadU8(), r.ReadU32(), r.ReadU32(), r.ReadU32())
            : null;
        if (r.Remaining != 0)
            throw new InvalidDataException(
                $"SMSG_FRIEND_STATUS result {result} has {r.Remaining} trailing byte(s)");
        return new(result, guid, online);
    }

    public static void ApplyStatus(List<FriendEntry> friends, List<ulong> ignores,
        FriendStatus update)
    {
        int friendIndex = friends.FindIndex(f => f.Guid == update.Guid);
        switch (update.Result)
        {
            case FriendAddedOnline:
            case FriendAddedOffline:
                if (friendIndex < 0)
                {
                    Online? p = update.Presence;
                    friends.Add(new(update.Guid, p?.Status ?? 0, p?.Area ?? 0,
                        p?.Level ?? 0, p?.Class ?? 0));
                }
                break;
            case FriendRemoved:
                if (friendIndex >= 0) friends.RemoveAt(friendIndex);
                break;
            case FriendOnline when friendIndex >= 0 && update.Presence is Online p:
                friends[friendIndex] = new(update.Guid, p.Status, p.Area, p.Level, p.Class);
                break;
            case FriendOffline when friendIndex >= 0:
                friends[friendIndex] = new(update.Guid, 0, 0, 0, 0);
                break;
            case IgnoreAdded:
                if (!ignores.Contains(update.Guid)) ignores.Add(update.Guid);
                break;
            case IgnoreRemoved:
                ignores.Remove(update.Guid);
                break;
        }
    }

    public static WhoRequest ParseWhoFilter(string filter, Func<string, uint?>? zoneId = null)
    {
        uint levelMin = 0, levelMax = 100;
        string player = "", guild = "";
        uint raceMask = 0, classMask = 0;
        var zones = new List<uint>();
        var terms = new List<string>();
        foreach (string token in Tokenize(filter))
        {
            (char? tag, string value) = SplitTag(token);
            switch (tag)
            {
                case 'n': player = value; break;
                case 'g': guild = value; break;
                case 'z':
                    uint? id = zoneId?.Invoke(value);
                    if (id is uint zone) zones.Add(zone); else terms.Add(value);
                    break;
                case 'c':
                    if (NamedBit(value, ClassNames) is uint classBit) classMask |= classBit;
                    else terms.Add(value);
                    break;
                case 'r':
                    if (NamedBit(value, RaceNames) is uint raceBit) raceMask |= raceBit;
                    else terms.Add(value);
                    break;
                default:
                    if (LevelRange(value) is { } range)
                        (levelMin, levelMax) = range;
                    else if (value.Length > 0) terms.Add(value);
                    break;
            }
        }
        return new(levelMin, levelMax, player, guild,
            raceMask == 0 ? uint.MaxValue : raceMask,
            classMask == 0 ? uint.MaxValue : classMask, zones, terms);
    }

    public static byte[] BuildWhoBody(WhoRequest request)
    {
        var w = new PacketWriter(64);
        w.WriteU32(request.LevelMin);
        w.WriteU32(request.LevelMax);
        w.WriteCString(request.PlayerName);
        w.WriteCString(request.GuildName);
        w.WriteU32(request.RaceMask);
        w.WriteU32(request.ClassMask);
        IReadOnlyList<uint> zones = request.Zones.Take(WhoMaxZones).ToArray();
        w.WriteU32((uint)zones.Count);
        foreach (uint zone in zones) w.WriteU32(zone);
        IReadOnlyList<string> terms = request.SearchTerms.Take(WhoMaxSearchTerms).ToArray();
        w.WriteU32((uint)terms.Count);
        foreach (string term in terms) w.WriteCString(term);
        return w.ToArray();
    }

    private static readonly (byte Id, string Display, string Token)[] ClassNames =
    [
        (1, "Warrior", "WARRIOR"), (2, "Paladin", "PALADIN"),
        (3, "Hunter", "HUNTER"), (4, "Rogue", "ROGUE"),
        (5, "Priest", "PRIEST"), (7, "Shaman", "SHAMAN"),
        (8, "Mage", "MAGE"), (9, "Warlock", "WARLOCK"),
        (11, "Druid", "DRUID"),
    ];

    private static readonly (byte Id, string Display, string Token)[] RaceNames =
    [
        (1, "Human", "Human"), (2, "Orc", "Orc"), (3, "Dwarf", "Dwarf"),
        (4, "Night Elf", "NightElf"), (5, "Undead", "Scourge"),
        (6, "Tauren", "Tauren"), (7, "Gnome", "Gnome"), (8, "Troll", "Troll"),
    ];

    private static IEnumerable<string> Tokenize(string filter)
    {
        var current = new System.Text.StringBuilder();
        bool quoted = false;
        foreach (char ch in filter)
        {
            if (ch == '"') { quoted = !quoted; continue; }
            if (char.IsWhiteSpace(ch) && !quoted)
            {
                if (current.Length > 0) { yield return current.ToString(); current.Clear(); }
            }
            else current.Append(ch);
        }
        if (current.Length > 0) yield return current.ToString();
    }

    private static (char? Tag, string Value) SplitTag(string token) =>
        token.Length >= 2 && char.IsAsciiLetter(token[0]) && token[1] == '-'
            ? (char.ToLowerInvariant(token[0]), token[2..]) : (null, token);

    private static (uint Min, uint Max)? LevelRange(string value)
    {
        static uint Clamp(uint number) => Math.Clamp(number, 1u, 100u);
        int dash = value.IndexOf('-');
        if (dash >= 0)
        {
            if (!uint.TryParse(value[..dash], out uint lo) ||
                !uint.TryParse(value[(dash + 1)..], out uint hi)) return null;
            lo = Clamp(lo);
            return (lo, Clamp(Math.Max(hi, lo)));
        }
        return uint.TryParse(value, out uint exact)
            ? (Clamp(exact), Clamp(exact)) : null;
    }

    private static uint? NamedBit(string value,
        IEnumerable<(byte Id, string Display, string Token)> names)
    {
        foreach ((byte id, string display, string token) in names)
            if (value.Equals(display, StringComparison.OrdinalIgnoreCase) ||
                value.Equals(token, StringComparison.OrdinalIgnoreCase))
                return 1u << id;
        return null;
    }
}

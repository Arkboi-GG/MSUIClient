namespace MSUIClient.Formats;

/// <summary>
/// Build-5875 ChatChannels.dbc: ChannelID, Flags, FactionGroup, Name[8]+mask, Shortcut[8]+mask.
/// The auto-join walk composes the INITIAL rows (flag 0x1) against the player's zone: a
/// zone-dependent row (0x2) carries the zone name in its "%s", a city-only row (0x10) exists
/// only in a capital, and a city-named row (0x20) puts the shared city word in its "%s".
/// </summary>
public sealed class ChatChannelsCatalog
{
    public const string MpqPath = @"DBFilesClient\ChatChannels.dbc";
    public const uint FlagInitial = 0x1;
    public const uint FlagZoneDependent = 0x2;
    public const uint FlagCityOnly = 0x10;
    public const uint FlagCityName = 0x20;

    public readonly record struct Row(uint Id, uint Flags, uint FactionGroup, string Name, string Shortcut)
    {
        public bool IsInitial => (Flags & FlagInitial) != 0;
        public bool IsZoneDependent => (Flags & FlagZoneDependent) != 0;
        public bool IsCityOnly => (Flags & FlagCityOnly) != 0;
        public bool TakesCityName => (Flags & FlagCityName) != 0;
        /// <summary>The joinable name: the pattern's "%s" filled with the zone name, or the city word for a city-named row.</summary>
        public string JoinableName(string zoneName, string cityWord) =>
            Name.Replace("%s", TakesCityName ? cityWord : zoneName, StringComparison.Ordinal);
    }

    private readonly List<Row> _rows = [];
    public IReadOnlyList<Row> Rows => _rows;

    public static ChatChannelsCatalog? Load(MpqMount mpq)
    {
        byte[]? bytes = mpq.ReadFile(MpqPath);
        DbcFile? dbc = bytes is null ? null : DbcFile.Parse(bytes);
        if (dbc is null || dbc.FieldCount < 21) return null;
        var result = new ChatChannelsCatalog();
        for (int row = 0; row < dbc.RecordCount; row++)
            result._rows.Add(new Row(dbc.GetUInt(row, 0), dbc.GetUInt(row, 1), dbc.GetUInt(row, 2),
                dbc.GetString(row, 3), dbc.GetString(row, 12)));
        return result;
    }
}

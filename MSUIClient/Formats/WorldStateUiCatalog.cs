namespace MSUIClient.Formats;

public sealed record WorldStateUiRow(uint Id, uint Map, uint Area, string Icon, string Text, string Tooltip,
    int Faction, uint StateVariable, uint Type, string DynamicIcon, string DynamicTooltip,
    string ExtendedUi, uint ExtendedState1, uint ExtendedState2, uint ExtendedState3);

/// <summary>Mounted build-5875 WorldStateUI.dbc:39 fields, including eight-locale string blocks.</summary>
public sealed class WorldStateUiCatalog
{
    public const string Path = @"DBFilesClient\WorldStateUI.dbc";
    public IReadOnlyList<WorldStateUiRow> Rows { get; }
    private WorldStateUiCatalog(WorldStateUiRow[] rows) => Rows = Array.AsReadOnly(rows);
    public static WorldStateUiCatalog? Load(MpqMount mpq) => Parse(mpq.ReadFile(Path));
    public static WorldStateUiCatalog? Parse(byte[]? bytes)
    {
        DbcFile? dbc = bytes is null ? null : DbcFile.Parse(bytes);
        if (dbc is null || dbc.FieldCount != 39 || dbc.RecordSize != 156) return null;
        string Localized(int row, int field)
        {
            for (int locale = 0; locale < 8; locale++)
                if (dbc.GetString(row, field + locale) is { Length: > 0 } value) return value;
            return "";
        }
        var rows = new WorldStateUiRow[dbc.RecordCount];
        for (int row = 0; row < rows.Length; row++)
            rows[row] = new(dbc.GetUInt(row,0), dbc.GetUInt(row,1), dbc.GetUInt(row,2), dbc.GetString(row,3),
                Localized(row,4), Localized(row,13), dbc.GetInt(row,22), dbc.GetUInt(row,23), dbc.GetUInt(row,24),
                dbc.GetString(row,25), Localized(row,26), dbc.GetString(row,35),
                dbc.GetUInt(row,36), dbc.GetUInt(row,37), dbc.GetUInt(row,38));
        return new(rows);
    }
}

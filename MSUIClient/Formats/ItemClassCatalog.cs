namespace MSUIClient.Formats;

/// <summary>
/// Build-5875 ItemClass.dbc: ClassID, SubClassMapID, Flags, ClassName[8]+mask. The auction
/// house filter tree and the item tooltip class line read their words from here, never from
/// a hardcoded English table, so the install supplies the locale.
/// </summary>
public sealed class ItemClassCatalog
{
    public const string MpqPath = @"DBFilesClient\ItemClass.dbc";
    private readonly Dictionary<uint, string> _names = [];

    public string Name(uint itemClass) => _names.GetValueOrDefault(itemClass, "");

    public static ItemClassCatalog? Load(MpqMount mpq)
    {
        byte[]? bytes = mpq.ReadFile(MpqPath);
        DbcFile? dbc = bytes is null ? null : DbcFile.Parse(bytes);
        if (dbc is null || dbc.FieldCount < 4) return null;
        var result = new ItemClassCatalog();
        for (int row = 0; row < dbc.RecordCount; row++)
        {
            string name = dbc.GetString(row, 3);
            if (!string.IsNullOrWhiteSpace(name)) result._names[dbc.GetUInt(row, 0)] = name;
        }
        return result;
    }
}

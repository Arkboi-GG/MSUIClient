namespace MSUIClient.Formats;

public readonly record struct ItemRandomPropertyInfo(uint Id, string Suffix, uint[] Enchantments);

/// <summary>Build-5875 ItemRandomProperties.dbc: ID, internal name, five enchant slots, eight names and flags.</summary>
public sealed class ItemRandomPropertyCatalog
{
    public const string Path = @"DBFilesClient\ItemRandomProperties.dbc";
    private readonly Dictionary<uint, ItemRandomPropertyInfo> _rows = [];
    public bool TryGet(int id, out ItemRandomPropertyInfo row)
    {
        row = default;
        return id > 0 && _rows.TryGetValue((uint)id, out row);
    }

    public string ItemName(string name, int property) => TryGet(property, out var row) && row.Suffix.Length > 0
        ? name + " " + row.Suffix : name;

    public static ItemRandomPropertyCatalog? Load(MpqMount mpq) => mpq.ReadFile(Path) is { } bytes ? Parse(bytes) : null;

    public static ItemRandomPropertyCatalog? Parse(byte[] bytes)
    {
        DbcFile? dbc = DbcFile.Parse(bytes);
        if (dbc is null || dbc.FieldCount != 16 || dbc.RecordSize != 64) return null;
        var result = new ItemRandomPropertyCatalog();
        for (int row = 0; row < dbc.RecordCount; row++)
        {
            uint id = dbc.GetUInt(row, 0);
            if (id == 0) continue;
            string suffix = dbc.GetString(row, 7);
            if (suffix.Length == 0) suffix = dbc.GetString(row, 1);
            result._rows[id] = new(id, suffix, [dbc.GetUInt(row, 2), dbc.GetUInt(row, 3), dbc.GetUInt(row, 4)]);
        }
        return result;
    }
}

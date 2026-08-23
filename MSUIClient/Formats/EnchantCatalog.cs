namespace MSUIClient.Formats;

public readonly record struct EnchantInfo(uint Id, string Name, uint Flags, int VisualId)
{
    public bool BindsItem => (Flags & 0x1) != 0;
    public bool HidesTooltipName => (Flags & 0x2) != 0;
}

/// <summary>
/// Build-5875 SpellItemEnchantment.dbc rows used by the item-target confirmation gate.
/// Field 13 is the enUS name and field 23 bit 0 is the reference client's bind-warning flag.
/// </summary>
public sealed class EnchantCatalog
{
    public const string Path = @"DBFilesClient\SpellItemEnchantment.dbc";
    private readonly Dictionary<uint, EnchantInfo> _rows = [];

    public int Count => _rows.Count;
    public IEnumerable<EnchantInfo> Rows => _rows.Values;
    public bool TryGet(uint id, out EnchantInfo enchant) => _rows.TryGetValue(id, out enchant);
    public string Name(uint id) => _rows.TryGetValue(id, out EnchantInfo row) ? row.Name : "";
    public bool BindsItem(uint id) => _rows.TryGetValue(id, out EnchantInfo row) && row.BindsItem;
    public int? Visual(uint id) => _rows.TryGetValue(id, out EnchantInfo row) && row.VisualId != 0
        ? row.VisualId : null;

    public static EnchantCatalog FromRows(IEnumerable<EnchantInfo> rows)
    {
        var result = new EnchantCatalog();
        foreach (EnchantInfo row in rows) result._rows[row.Id] = row;
        return result;
    }

    public static EnchantCatalog? Load(MpqMount mpq)
    {
        byte[]? bytes = mpq.ReadFile(Path);
        DbcFile? dbc = bytes is null ? null : DbcFile.Parse(bytes);
        if (dbc is null || dbc.FieldCount != 24) return null;

        var result = new EnchantCatalog();
        for (int row = 0; row < dbc.RecordCount; row++)
        {
            uint id = dbc.GetUInt(row, 0);
            if (id == 0) continue;
            result._rows[id] = new EnchantInfo(id, dbc.GetString(row, 13), dbc.GetUInt(row, 23),
                unchecked((int)dbc.GetUInt(row, 22)));
        }
        return result;
    }

    public static EnchantCatalog? Load(string clientDataPath)
    {
        byte[]? bytes = AdtTerrainReader.ReadFileFromMpqs(clientDataPath, Path);
        DbcFile? dbc = bytes is null ? null : DbcFile.Parse(bytes);
        if (dbc is null || dbc.FieldCount != 24) return null;
        var result = new EnchantCatalog();
        for (int row = 0; row < dbc.RecordCount; row++)
        {
            uint id = dbc.GetUInt(row, 0);
            if (id == 0) continue;
            result._rows[id] = new EnchantInfo(id, dbc.GetString(row, 13), dbc.GetUInt(row, 23),
                unchecked((int)dbc.GetUInt(row, 22)));
        }
        return result;
    }
}

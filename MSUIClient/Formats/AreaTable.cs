namespace MSUIClient.Formats;

// AreaTable.dbc - the zone/area catalog. id -> (parent zone, display name). Used by the
// character-select roster to label each character's row with its zone ("Stormwind City").
//
// Layout VERIFIED against 1.12.1 build 5875 (benilla-formats/src/area_table.rs): 25 u32-wide
// columns; ID = col 0, parent ZoneID = col 2 (0 = this row IS a top-level zone), and the localized
// AreaName (enUS) = col 11. Only id / parent / name are read here.
public sealed class AreaTableCatalog
{
    public const string MpqPath = @"DBFilesClient\AreaTable.dbc";

    // id -> (parent area id, own display name). Parent 0 = top-level zone.
    private readonly Dictionary<uint, (uint Parent, string Name)> _rows = new();
    public int Count => _rows.Count;

    public static AreaTableCatalog? Parse(byte[] data)
    {
        var dbc = DbcFile.Parse(data);
        if (dbc is null || dbc.FieldCount < 12) return null;   // need through col 11 (AreaName)
        var t = new AreaTableCatalog();
        for (int r = 0; r < dbc.RecordCount; r++)
        {
            uint id = dbc.GetUInt(r, 0);
            uint parent = dbc.GetUInt(r, 2);
            string name = dbc.GetString(r, 11);
            t._rows[id] = (parent, name);
        }
        Console.WriteLine($"[dbc] AreaTable: {t.Count} area(s)");
        return t;
    }

    /// <summary>
    /// The label to show for an area id: the area's own name, or - if this row is an unnamed subzone -
    /// the nearest named parent zone. Empty when the id is unknown, 0, or unnamed all the way up (the
    /// roster then draws no location line, as 1.12 does for a 0/unknown zone).
    /// </summary>
    public string ZoneName(uint areaId)
    {
        uint id = areaId;
        for (int i = 0; i < 8 && id != 0; i++)   // depth-guarded; 1.12 parent chains are 1-2 deep
        {
            if (!_rows.TryGetValue(id, out var row)) break;
            if (!string.IsNullOrEmpty(row.Name)) return row.Name;
            id = row.Parent;
        }
        return "";
    }
}

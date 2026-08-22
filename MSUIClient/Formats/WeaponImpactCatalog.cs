namespace MSUIClient.Formats;

public readonly record struct WeaponImpactRow(uint[] Impact, uint[] Critical);

/// <summary>Build-5875 WeaponImpactSounds.dbc: (subclass, metal) to ten target slots.</summary>
public sealed class WeaponImpactCatalog
{
    public const string MpqPath = @"DBFilesClient\WeaponImpactSounds.dbc";
    private readonly Dictionary<(uint Subclass, bool Metal), WeaponImpactRow> _rows = [];
    public int Count => _rows.Count;

    public bool TryGet(uint subclass, bool metal, out WeaponImpactRow row) =>
        _rows.TryGetValue((subclass, metal), out row) ||
        _rows.TryGetValue((subclass, !metal), out row);

    public static WeaponImpactCatalog FromRows(
        params (uint Subclass, bool Metal, uint[] Impact, uint[] Critical)[] rows)
    {
        var result = new WeaponImpactCatalog();
        foreach (var row in rows)
            result._rows[(row.Subclass, row.Metal)] = new(row.Impact, row.Critical);
        return result;
    }

    public static WeaponImpactCatalog? Load(MpqMount mpq)
    {
        if (mpq.ReadFile(MpqPath) is not { } bytes || DbcFile.Parse(bytes) is not { } dbc ||
            dbc.FieldCount < 23 || dbc.RecordSize < 92) return null;
        var result = new WeaponImpactCatalog();
        for (int r = 0; r < dbc.RecordCount; r++)
        {
            var impact = new uint[10];
            var critical = new uint[10];
            for (int slot = 0; slot < 10; slot++)
            {
                impact[slot] = dbc.GetUInt(r, 3 + slot);
                critical[slot] = dbc.GetUInt(r, 13 + slot);
            }
            result._rows[(dbc.GetUInt(r, 1), dbc.GetUInt(r, 2) != 0)] =
                new(impact, critical);
        }
        return result;
    }
}

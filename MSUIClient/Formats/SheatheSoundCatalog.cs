namespace MSUIClient.Formats;

public readonly record struct SheatheSoundPair(uint Sheathe, uint Unsheathe);

/// <summary>
/// Build-5875 SheatheSoundLookups.dbc: (item class, subclass, material) to
/// stow/draw SoundEntries kits. Material is the load-bearing key; shield rows
/// carry material zero as a don't-care.
/// </summary>
public sealed class SheatheSoundCatalog
{
    public const string MpqPath = @"DBFilesClient\SheatheSoundLookups.dbc";
    private readonly Dictionary<(uint Class, uint Subclass, uint Material), SheatheSoundPair> _rows = [];

    public int Count => _rows.Count;

    public bool TryGet(uint itemClass, uint subclass, uint material, out SheatheSoundPair pair)
    {
        foreach (uint candidateSubclass in new[] { subclass, 0u })
        foreach (uint candidateMaterial in new[] { material, 1u, 2u, 0u })
            if (_rows.TryGetValue((itemClass, candidateSubclass, candidateMaterial), out pair))
                return true;
        pair = default;
        return false;
    }

    public static SheatheSoundCatalog? Load(MpqMount mpq)
    {
        DbcFile? dbc = mpq.ReadFile(MpqPath) is { } bytes ? DbcFile.Parse(bytes) : null;
        if (dbc is null || dbc.FieldCount < 7 || dbc.RecordSize < 28) return null;
        var result = new SheatheSoundCatalog();
        for (int row = 0; row < dbc.RecordCount; row++)
            result._rows[(dbc.GetUInt(row, 1), dbc.GetUInt(row, 2), dbc.GetUInt(row, 3))] =
                new SheatheSoundPair(dbc.GetUInt(row, 5), dbc.GetUInt(row, 6));
        return result;
    }

    public static SheatheSoundCatalog FromRows(params
        (uint Class, uint Subclass, uint Material, uint Sheathe, uint Unsheathe)[] rows)
    {
        var result = new SheatheSoundCatalog();
        foreach (var row in rows)
            result._rows[(row.Class, row.Subclass, row.Material)] =
                new SheatheSoundPair(row.Sheathe, row.Unsheathe);
        return result;
    }
}

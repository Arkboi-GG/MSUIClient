namespace MSUIClient.Formats;

public readonly record struct ShapeshiftFormInfo(uint Id, uint BonusActionBar,
    string Name, uint Flags, int CreatureType = 0)
{
    /// <summary>flags bit 0x2 blocks cancelling an active stance (warrior stances).</summary>
    public bool Cancelable => (Flags & 0x2) == 0;
}

/// <summary>The build-5875 form rows needed by StanceBar click semantics.</summary>
public sealed class ShapeshiftFormCatalog
{
    public const string MpqPath = @"DBFilesClient\SpellShapeshiftForm.dbc";
    private readonly Dictionary<uint, ShapeshiftFormInfo> _forms = new();

    public bool TryGet(uint id, out ShapeshiftFormInfo form) => _forms.TryGetValue(id, out form);

    public static ShapeshiftFormCatalog? Load(MpqMount mpq)
    {
        byte[]? bytes = mpq.ReadFile(MpqPath);
        DbcFile? dbc = bytes is null ? null : DbcFile.Parse(bytes);
        if (dbc is null || dbc.FieldCount < 14) return null;
        var result = new ShapeshiftFormCatalog();
        for (int row = 0; row < dbc.RecordCount; row++)
        {
            uint id = dbc.GetUInt(row, 0);
            if (id == 0) continue;
            result._forms[id] = new(id, dbc.GetUInt(row, 1),
                dbc.GetString(row, 2), dbc.GetUInt(row, 11),
                unchecked((int)dbc.GetUInt(row, 12)));
        }
        return result;
    }
}

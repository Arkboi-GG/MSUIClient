namespace MSUIClient.Formats;

/// <summary>
/// Build-5875 ItemSubClass.dbc header vocabulary. TradeSkillFrame groups recipes by the
/// created item's (class, subclass) and names each group verbose-first, display-name fallback.
/// </summary>
public sealed class ItemSubClassCatalog
{
    public const string MpqPath = @"DBFilesClient\ItemSubClass.dbc";
    private readonly Dictionary<(uint Class, uint Subclass), string> _names = new();
    private readonly Dictionary<(uint Class, uint Subclass), string> _displayNames = new();
    private readonly Dictionary<(uint Class, uint Subclass), ItemSubClassTooltipInfo> _tooltip = [];

    public string Name(uint itemClass, uint subclass) =>
        _names.GetValueOrDefault((itemClass, subclass), "");

    public string DisplayName(uint itemClass, uint subclass) =>
        _displayNames.GetValueOrDefault((itemClass, subclass), "");

    public ItemSubClassTooltipInfo TooltipInfo(uint itemClass, uint subclass) =>
        _tooltip.GetValueOrDefault((itemClass, subclass));

    public static ItemSubClassCatalog? Load(MpqMount mpq)
    {
        byte[]? bytes = mpq.ReadFile(MpqPath);
        DbcFile? dbc = bytes is null ? null : DbcFile.Parse(bytes);
        if (dbc is null || dbc.FieldCount < 28) return null;
        var result = new ItemSubClassCatalog();
        for (int row = 0; row < dbc.RecordCount; row++)
        {
            uint itemClass = dbc.GetUInt(row, 0), subclass = dbc.GetUInt(row, 1);
            string display = dbc.GetString(row, 10);
            string verbose = dbc.GetString(row, 19);
            string name = string.IsNullOrWhiteSpace(verbose) ? display : verbose;
            int prerequisite = dbc.GetInt(row, 2);
            int postrequisite = dbc.GetInt(row, 3);
            int alternative = prerequisite != -1 ? prerequisite : postrequisite;
            result._tooltip[(itemClass, subclass)] = new(
                alternative >= 0 ? (uint)alternative : null,
                (dbc.GetUInt(row, 5) & 1) != 0);
            if (!string.IsNullOrWhiteSpace(display))
                result._displayNames[(itemClass, subclass)] = display;
            if (!string.IsNullOrWhiteSpace(name))
                result._names[(itemClass, subclass)] = name;
        }
        return result;
    }
}

public readonly record struct ItemSubClassTooltipInfo(
    uint? ProficiencyAlternative,
    bool HidesDisplayName);

namespace MSUIClient.Formats;

public readonly record struct SkillLineInfo(uint Id, uint CategoryId, string Name,
    string Description, string IconPath);
public readonly record struct SkillCategoryInfo(uint Id, string Name, uint DisplayOrder);

/// <summary>Names, categories, descriptions and icons from the client's build-5875 DBCs.</summary>
public sealed class SkillLineCatalog
{
    public const string LinesPath = @"DBFilesClient\SkillLine.dbc";
    public const string CategoriesPath = @"DBFilesClient\SkillLineCategory.dbc";
    public const string IconsPath = @"DBFilesClient\SpellIcon.dbc";
    public const string AbilitiesPath = @"DBFilesClient\SkillLineAbility.dbc";
    private readonly Dictionary<uint, SkillLineInfo> _lines = new();
    private readonly Dictionary<uint, SkillCategoryInfo> _categories = new();
    private readonly Dictionary<uint, uint> _spellLines = new();

    public bool TryGet(uint id, out SkillLineInfo line) => _lines.TryGetValue(id, out line);
    public bool TryGetCategory(uint id, out SkillCategoryInfo category) => _categories.TryGetValue(id, out category);
    public uint SpellLine(uint spellId) => _spellLines.GetValueOrDefault(spellId);

    public static SkillLineCatalog? Load(MpqMount mpq)
    {
        DbcFile? lines = Parse(mpq, LinesPath), categories = Parse(mpq, CategoriesPath), icons = Parse(mpq, IconsPath),
            abilities = Parse(mpq, AbilitiesPath);
        if (lines is null || categories is null || lines.FieldCount < 22 || categories.FieldCount < 11)
            return null;
        var iconMap = new Dictionary<uint, string>();
        if (icons is { FieldCount: >= 2 })
            for (int row = 0; row < icons.RecordCount; row++)
                iconMap[icons.GetUInt(row, 0)] = icons.GetString(row, 1);

        var result = new SkillLineCatalog();
        for (int row = 0; row < categories.RecordCount; row++)
        {
            uint id = categories.GetUInt(row, 0);
            result._categories[id] = new SkillCategoryInfo(id, categories.GetString(row, 1),
                categories.GetUInt(row, 10));
        }
        for (int row = 0; row < lines.RecordCount; row++)
        {
            uint id = lines.GetUInt(row, 0), category = lines.GetUInt(row, 1);
            iconMap.TryGetValue(lines.GetUInt(row, 21), out string? icon);
            result._lines[id] = new SkillLineInfo(id, category, lines.GetString(row, 3),
                lines.GetString(row, 12), string.IsNullOrWhiteSpace(icon) ? "" : $@"Interface\Icons\{icon}");
        }
        if (abilities is { FieldCount: >= 15 })
            for (int row = 0; row < abilities.RecordCount; row++)
            {
                uint spell = abilities.GetUInt(row, 2);
                if (spell != 0) result._spellLines.TryAdd(spell, abilities.GetUInt(row, 1));
            }
        return result;
    }

    private static DbcFile? Parse(MpqMount mpq, string path)
    {
        byte[]? bytes = mpq.ReadFile(path);
        return bytes is null ? null : DbcFile.Parse(bytes);
    }
}

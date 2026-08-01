namespace MSUIClient.Formats;

public readonly record struct TalentTabInfo(uint Id, string Name, uint ClassMask, uint Page, string Background);
public readonly record struct TalentInfo(uint Id, uint TabId, uint Row, uint Column,
    uint[] RankSpells, uint DependsOn, uint DependsOnRank, uint RequiredSpell);

public sealed class TalentCatalog
{
    public const string TalentPath = @"DBFilesClient\Talent.dbc";
    public const string TabPath = @"DBFilesClient\TalentTab.dbc";
    private readonly Dictionary<uint, TalentTabInfo> _tabs = [];
    private readonly Dictionary<uint, TalentInfo> _talents = [];

    public int TalentCount => _talents.Count;
    public IReadOnlyCollection<TalentTabInfo> Tabs => _tabs.Values;
    public bool TryGet(uint id, out TalentInfo talent) => _talents.TryGetValue(id, out talent);
    public IEnumerable<TalentTabInfo> TabsForClass(byte classId) => classId is > 0 and < 32
        ? _tabs.Values.Where(x => (x.ClassMask & (1u << (classId - 1))) != 0).OrderBy(x => x.Page)
        : [];
    public IEnumerable<TalentInfo> TalentsForTab(uint tabId) => _talents.Values
        .Where(x => x.TabId == tabId).OrderBy(x => x.Row).ThenBy(x => x.Column);

    public static TalentCatalog? Load(MpqMount mpq)
    {
        DbcFile? talents = Parse(mpq, TalentPath), tabs = Parse(mpq, TabPath);
        if (talents is null || tabs is null || talents.FieldCount < 21 || tabs.FieldCount < 15) return null;
        var result = new TalentCatalog();
        for (int row = 0; row < tabs.RecordCount; row++)
        {
            uint id = tabs.GetUInt(row, 0); if (id == 0) continue;
            result._tabs[id] = new(id, tabs.GetString(row, 1), tabs.GetUInt(row, 12),
                tabs.GetUInt(row, 13), tabs.GetString(row, 14));
        }
        for (int row = 0; row < talents.RecordCount; row++)
        {
            uint id = talents.GetUInt(row, 0); if (id == 0) continue;
            uint[] ranks = Enumerable.Range(4, 5).Select(f => talents.GetUInt(row, f)).Where(x => x != 0).ToArray();
            result._talents[id] = new(id, talents.GetUInt(row, 1), talents.GetUInt(row, 2),
                talents.GetUInt(row, 3), ranks, talents.GetUInt(row, 13), talents.GetUInt(row, 16), talents.GetUInt(row, 20));
        }
        return result;
    }

    private static DbcFile? Parse(MpqMount mpq, string path)
    { byte[]? bytes = mpq.ReadFile(path); return bytes is null ? null : DbcFile.Parse(bytes); }
}

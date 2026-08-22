namespace MSUIClient.Formats;

public sealed record ItemSetInfo(
    uint Id,
    string Name,
    uint[] Members,
    (uint Threshold, uint SpellId)[] Bonuses,
    uint RequiredSkill,
    uint RequiredSkillRank);

/// <summary>
/// Vanilla build-5875 ItemSet.dbc. The tooltip uses the set name, seventeen member ids,
/// eight threshold spells, and the set-level skill requirement.
/// </summary>
public sealed class ItemSetCatalog
{
    public const string MpqPath = @"DBFilesClient\ItemSet.dbc";
    private readonly Dictionary<uint, ItemSetInfo> _sets = [];

    public int Count => _sets.Count;
    public bool TryGet(uint id, out ItemSetInfo set) => _sets.TryGetValue(id, out set!);

    public static ItemSetCatalog? Load(MpqMount mpq)
    {
        byte[]? bytes = mpq.ReadFile(MpqPath);
        DbcFile? dbc = bytes is null ? null : DbcFile.Parse(bytes);
        // id + localized name block + 17 items + 8 spells + 8 thresholds + 2 skill fields.
        if (dbc is null || dbc.FieldCount < 45 || dbc.RecordSize < 180) return null;

        var result = new ItemSetCatalog();
        for (int row = 0; row < dbc.RecordCount; row++)
        {
            uint id = dbc.GetUInt(row, 0);
            if (id == 0) continue;
            uint[] members = Enumerable.Range(10, 17)
                .Select(field => dbc.GetUInt(row, field))
                .Where(static member => member != 0)
                .ToArray();
            var bonuses = new List<(uint Threshold, uint SpellId)>(8);
            for (int i = 0; i < 8; i++)
            {
                uint spell = dbc.GetUInt(row, 27 + i);
                if (spell != 0)
                    bonuses.Add((dbc.GetUInt(row, 35 + i), spell));
            }
            result._sets[id] = new ItemSetInfo(id, dbc.GetString(row, 1), members,
                bonuses.ToArray(), dbc.GetUInt(row, 43), dbc.GetUInt(row, 44));
        }
        return result;
    }
}

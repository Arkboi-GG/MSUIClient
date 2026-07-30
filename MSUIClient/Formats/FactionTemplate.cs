namespace MSUIClient.Formats;

/// <summary>The three ranks returned by the vanilla FactionTemplate comparator.</summary>
public enum FactionReaction
{
    Hostile = 1,
    Neutral = 3,
    Friendly = 4,
}

public sealed class FactionTemplateRow
{
    public uint Id;
    public uint Faction;
    public uint GroupMask;
    public uint FriendGroupMask;
    public uint EnemyGroupMask;
    public uint[] Enemies = new uint[4];
    public uint[] Friends = new uint[4];

    /// <summary>Byte-equivalent base reaction: this template's reaction toward other.</summary>
    public FactionReaction ReactionToward(FactionTemplateRow other)
    {
        if ((EnemyGroupMask & other.GroupMask) != 0 || Contains(Enemies, other.Faction))
            return FactionReaction.Hostile;
        if ((FriendGroupMask & other.GroupMask) != 0 || Contains(Friends, other.Faction))
            return FactionReaction.Friendly;
        if ((other.FriendGroupMask & GroupMask) != 0 || Contains(other.Friends, Faction))
            return FactionReaction.Friendly;
        return FactionReaction.Neutral;
    }

    private static bool Contains(uint[] ids, uint faction)
    {
        foreach (uint id in ids)
        {
            if (id == 0) break;
            if (id == faction) return true;
        }
        return false;
    }
}

/// <summary>Vanilla build-5875 FactionTemplate.dbc, indexed by UNIT_FIELD_FACTIONTEMPLATE.</summary>
public sealed class FactionTemplateCatalog
{
    public const string MpqPath = @"DBFilesClient\FactionTemplate.dbc";
    private readonly Dictionary<uint, FactionTemplateRow> _rows = new();

    public int Count => _rows.Count;
    public bool TryGet(uint id, out FactionTemplateRow row) => _rows.TryGetValue(id, out row!);

    public static FactionTemplateCatalog? Parse(byte[] bytes)
    {
        DbcFile? dbc = DbcFile.Parse(bytes);
        if (dbc is null || dbc.FieldCount < 14 || dbc.RecordSize < 56) return null;

        var catalog = new FactionTemplateCatalog();
        for (int i = 0; i < dbc.RecordCount; i++)
        {
            var row = new FactionTemplateRow
            {
                Id = dbc.GetUInt(i, 0),
                Faction = dbc.GetUInt(i, 1),
                GroupMask = dbc.GetUInt(i, 3),
                FriendGroupMask = dbc.GetUInt(i, 4),
                EnemyGroupMask = dbc.GetUInt(i, 5),
            };
            for (int j = 0; j < 4; j++) row.Enemies[j] = dbc.GetUInt(i, 6 + j);
            for (int j = 0; j < 4; j++) row.Friends[j] = dbc.GetUInt(i, 10 + j);
            catalog._rows[row.Id] = row;
        }
        return catalog;
    }
}

namespace MSUIClient.Formats;

public sealed record FactionInfo(uint Id, int ReputationIndex, uint ParentFaction,
    string Name, uint[] RaceMasks, uint[] ClassMasks, int[] BaseValues)
{
    public int BaseStanding(byte race, byte playerClass)
    {
        uint raceMask = race is > 0 and <= 32 ? 1u << (race - 1) : 0;
        uint classMask = playerClass is > 0 and <= 32 ? 1u << (playerClass - 1) : 0;
        for (int i = 0; i < 4; i++)
            if ((RaceMasks[i] == 0 || (RaceMasks[i] & raceMask) != 0) &&
                (ClassMasks[i] == 0 || (ClassMasks[i] & classMask) != 0))
                return BaseValues[i];
        return 0;
    }
}

/// <summary>Vanilla build-5875 Faction.dbc, including the 64-entry reputation-list mapping.</summary>
public sealed class FactionCatalog
{
    public const string MpqPath = @"DBFilesClient\Faction.dbc";
    private readonly Dictionary<int, FactionInfo> _byReputationIndex = new();
    // Every faction by id (name + parent), including group headers (reputationIndex == -1)
    // that carry no standing of their own but head the reputation-pane categories.
    private readonly Dictionary<uint, string> _nameById = new();

    public bool TryGetByReputationIndex(int index, out FactionInfo row) =>
        _byReputationIndex.TryGetValue(index, out row!);

    /// <summary>The display name of any faction id, e.g. a ParentFaction group header.</summary>
    public bool TryGetName(uint factionId, out string name) =>
        _nameById.TryGetValue(factionId, out name!);

    public static FactionCatalog? Parse(byte[] bytes)
    {
        DbcFile? dbc = DbcFile.Parse(bytes);
        if (dbc is null || dbc.FieldCount < 28 || dbc.RecordSize < 112) return null;
        var result = new FactionCatalog();
        for (int row = 0; row < dbc.RecordCount; row++)
        {
            result._nameById[dbc.GetUInt(row, 0)] = dbc.GetString(row, 19);
            int reputationIndex = dbc.GetInt(row, 1);
            if (reputationIndex is < 0 or >= 64) continue;
            uint[] raceMasks = new uint[4], classMasks = new uint[4];
            int[] bases = new int[4];
            for (int i = 0; i < 4; i++)
            {
                raceMasks[i] = dbc.GetUInt(row, 2 + i);
                classMasks[i] = dbc.GetUInt(row, 6 + i);
                bases[i] = dbc.GetInt(row, 10 + i);
            }
            result._byReputationIndex[reputationIndex] = new FactionInfo(
                dbc.GetUInt(row, 0), reputationIndex, dbc.GetUInt(row, 18),
                dbc.GetString(row, 19), raceMasks, classMasks, bases);
        }
        return result;
    }
}

namespace MSUIClient.Formats;

/// <summary>
/// ChrRaces.dbc race id -> exploration-jingle SoundEntries kit. Build 5875
/// stores the kit in column 3; all playable races carry a nonzero row.
/// </summary>
public sealed class ExplorationSoundCatalog
{
    public const string MpqPath = @"DBFilesClient\ChrRaces.dbc";
    private readonly Dictionary<uint, uint> _byRace = [];

    public uint? Kit(byte race) =>
        _byRace.TryGetValue(race, out uint kit) ? kit : null;

    public static ExplorationSoundCatalog? Load(MpqMount mpq) =>
        mpq.ReadFile(MpqPath) is { } bytes ? Parse(bytes) : null;

    public static ExplorationSoundCatalog? Parse(byte[] bytes)
    {
        DbcFile? dbc = DbcFile.Parse(bytes);
        if (dbc is null || dbc.FieldCount < 4) return null;
        var result = new ExplorationSoundCatalog();
        for (int row = 0; row < dbc.RecordCount; row++)
        {
            uint race = dbc.GetUInt(row, 0);
            uint kit = dbc.GetUInt(row, 3);
            if (race != 0 && kit != 0) result._byRace[race] = kit;
        }
        return result;
    }
}

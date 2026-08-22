namespace MSUIClient.Formats;

/// <summary>
/// Build-5875 footstep chain: GroundEffectTexture -> TerrainType sound class ->
/// (creature footstep class, terrain sound class) -> dry/splash SoundEntries kits.
/// </summary>
public sealed class FootstepCatalog
{
    public const string GroundEffectsPath = @"DBFilesClient\GroundEffectTexture.dbc";
    public const string TerrainTypesPath = @"DBFilesClient\TerrainType.dbc";
    public const string LookupPath = @"DBFilesClient\FootstepTerrainLookup.dbc";

    private readonly Dictionary<uint, uint> _effectTerrain = [];
    private readonly Dictionary<uint, uint> _terrainSound = [];
    private readonly Dictionary<(uint FootstepClass, uint TerrainSound),
        (uint Dry, uint Splash)> _lookup = [];

    public int Count => _lookup.Count;
    public bool TryTerrainForEffect(int effectId, out uint terrainType)
    {
        terrainType = 0;
        return effectId >= 0 && _effectTerrain.TryGetValue((uint)effectId, out terrainType);
    }

    public bool TryResolveTerrain(uint footstepClass, uint terrainType,
        out (uint Dry, uint Splash) kits)
    {
        kits = default;
        return footstepClass != 0 && _terrainSound.TryGetValue(terrainType, out uint sound) &&
               _lookup.TryGetValue((footstepClass, sound), out kits);
    }

    public static FootstepCatalog? Load(MpqMount mpq)
    {
        DbcFile? effects = Parse(mpq, GroundEffectsPath);
        DbcFile? terrains = Parse(mpq, TerrainTypesPath);
        DbcFile? lookup = Parse(mpq, LookupPath);
        if (effects is null || terrains is null || lookup is null ||
            effects.FieldCount < 7 || effects.RecordSize < 28 ||
            terrains.FieldCount < 6 || terrains.RecordSize < 24 ||
            lookup.FieldCount < 5 || lookup.RecordSize < 20)
            return null;

        var result = new FootstepCatalog();
        for (int row = 0; row < effects.RecordCount; row++)
            result._effectTerrain[effects.GetUInt(row, 0)] = effects.GetUInt(row, 6);
        for (int row = 0; row < terrains.RecordCount; row++)
            result._terrainSound[terrains.GetUInt(row, 0)] = terrains.GetUInt(row, 4);
        for (int row = 0; row < lookup.RecordCount; row++)
            result._lookup[(lookup.GetUInt(row, 1), lookup.GetUInt(row, 2))] =
                (lookup.GetUInt(row, 3), lookup.GetUInt(row, 4));
        return result;
    }

    private static DbcFile? Parse(MpqMount mpq, string path) =>
        mpq.ReadFile(path) is { } bytes ? DbcFile.Parse(bytes) : null;
}

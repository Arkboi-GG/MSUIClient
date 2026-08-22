namespace MSUIClient.Formats;

public readonly record struct WorldMapAreaInfo(uint Id, uint MapId, uint AreaId,
    string Directory, float Left, float Right, float Top, float Bottom)
{
    public float X(float worldY) => Math.Clamp((worldY - Left) / (Right - Left), 0, 1);
    public float Y(float worldX) => Math.Clamp((worldX - Top) / (Bottom - Top), 0, 1);
}

public sealed class WorldMapAreaCatalog
{
    public const string Path = @"DBFilesClient\WorldMapArea.dbc";
    private readonly Dictionary<uint, WorldMapAreaInfo> _byArea = [];
    private readonly Dictionary<uint, WorldMapAreaInfo> _continents = [];
    private readonly List<WorldMapAreaInfo> _areaOrder = [];
    private readonly List<WorldMapAreaInfo> _continentOrder = [];
    public bool TryGetArea(uint areaId, out WorldMapAreaInfo area) => _byArea.TryGetValue(areaId, out area);
    public bool TryGetContinent(uint mapId, out WorldMapAreaInfo area) => _continents.TryGetValue(mapId, out area);

    /// <summary>Every zone-level entry (creator mode's map picker lists these).</summary>
    public IEnumerable<WorldMapAreaInfo> Areas => _areaOrder;
    public IEnumerable<WorldMapAreaInfo> Continents => _continentOrder;

    public static WorldMapAreaCatalog? Load(MpqMount mpq)
    {
        byte[]? bytes = mpq.ReadFile(Path);
        DbcFile? dbc = bytes is null ? null : DbcFile.Parse(bytes);
        if (dbc is null || dbc.FieldCount < 8) return null;
        var result = new WorldMapAreaCatalog();
        for (int row = 0; row < dbc.RecordCount; row++)
        {
            var info = new WorldMapAreaInfo(dbc.GetUInt(row, 0), dbc.GetUInt(row, 1),
                dbc.GetUInt(row, 2), dbc.GetString(row, 3), dbc.GetFloat(row, 4),
                dbc.GetFloat(row, 5), dbc.GetFloat(row, 6), dbc.GetFloat(row, 7));
            if (info.AreaId == 0)
            {
                result._continents[info.MapId] = info;
                result._continentOrder.Add(info);
            }
            else
            {
                result._byArea[info.AreaId] = info;
                result._areaOrder.Add(info);
            }
        }
        return result;
    }
}

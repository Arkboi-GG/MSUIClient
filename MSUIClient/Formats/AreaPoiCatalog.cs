using System.Numerics;

namespace MSUIClient.Formats;

/// <summary>One vanilla 1.12.1 AreaPOI.dbc landmark row.</summary>
public readonly record struct AreaPoiInfo(
    uint Id,
    uint Importance,
    uint Icon,
    uint FactionId,
    Vector3 Position,
    uint ContinentId,
    uint Flags,
    uint AreaId,
    string Name,
    string Description,
    uint WorldStateId);

public readonly record struct AreaPoiArrow(float Distance, AreaPoiInfo Poi);

public readonly record struct AreaPoiSelection(
    IReadOnlyList<AreaPoiInfo> Icons,
    IReadOnlyList<AreaPoiArrow> Arrows);

/// <summary>
/// Typed access to AreaPOI.dbc plus the byte-verified current Benilla minimap landmark rank law.
/// </summary>
public sealed class AreaPoiCatalog
{
    public const string Path = @"DBFilesClient\AreaPOI.dbc";
    public const int FieldCount = 29;
    public const int RecordSize = FieldCount * 4;
    public const float RankRadiusYards = 694.444f;
    public const float EdgeRatio = .8f;
    public const uint CandidateFlag = 0x1;
    public const uint InRangeIconFlag = 0x2;

    private readonly List<AreaPoiInfo> _rows = [];
    public IReadOnlyList<AreaPoiInfo> Rows => _rows;

    public static AreaPoiCatalog? Load(MpqMount mpq)
    {
        ArgumentNullException.ThrowIfNull(mpq);
        byte[]? bytes = mpq.ReadFile(Path);
        DbcFile? dbc = bytes is null ? null : DbcFile.Parse(bytes);
        if (dbc is null || dbc.FieldCount != FieldCount || dbc.RecordSize != RecordSize)
            return null;

        var result = new AreaPoiCatalog();
        for (int row = 0; row < dbc.RecordCount; row++)
            result._rows.Add(new(
                dbc.GetUInt(row, 0),
                dbc.GetUInt(row, 1),
                dbc.GetUInt(row, 2),
                dbc.GetUInt(row, 3),
                new Vector3(dbc.GetFloat(row, 4), dbc.GetFloat(row, 5),
                    dbc.GetFloat(row, 6)),
                dbc.GetUInt(row, 7),
                dbc.GetUInt(row, 8),
                dbc.GetUInt(row, 9),
                dbc.GetString(row, 10),
                dbc.GetString(row, 19),
                dbc.GetUInt(row, 28)));
        return result;
    }

    public AreaPoiSelection Select(uint mapId, Vector2 player, float radiusYards) =>
        Select(_rows, mapId, player, radiusYards);

    public static AreaPoiSelection Select(IEnumerable<AreaPoiInfo> rows, uint mapId,
        Vector2 player, float radiusYards)
    {
        var icons = new List<AreaPoiInfo>();
        var arrows = new List<AreaPoiArrow>();
        float edge = Math.Max(0f, radiusYards) * EdgeRatio;
        foreach (AreaPoiInfo poi in rows)
        {
            if (poi.ContinentId != mapId || (poi.Flags & CandidateFlag) == 0) continue;
            float distance = Vector2.Distance(player,
                new Vector2(poi.Position.X, poi.Position.Y));
            if (distance <= edge)
            {
                if ((poi.Flags & InRangeIconFlag) != 0) icons.Add(poi);
            }
            else if (distance <= RankRadiusYards)
            {
                arrows.Add(new(distance, poi));
            }
        }

        arrows.Sort((left, right) =>
        {
            int importance = unchecked((int)left.Poi.Importance)
                .CompareTo(unchecked((int)right.Poi.Importance));
            return importance != 0 ? importance : left.Distance.CompareTo(right.Distance);
        });
        if (arrows.Count > 3) arrows.RemoveRange(3, arrows.Count - 3);
        return new(icons, arrows);
    }

    /// <summary>AreaPOI Icon is an 8x8 atlas cell and the client rejects values >= 64.</summary>
    public static bool TryIconUv(uint icon, out Vector2 uvMin, out Vector2 uvMax)
    {
        if (icon >= 64)
        {
            uvMin = uvMax = default;
            return false;
        }
        float column = icon % 8;
        float row = icon / 8;
        uvMin = new(column / 8f, row / 8f);
        uvMax = new((column + 1f) / 8f, (row + 1f) / 8f);
        return true;
    }
}

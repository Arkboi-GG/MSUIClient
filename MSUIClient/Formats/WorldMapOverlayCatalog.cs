using System.Buffers.Binary;
using System.Numerics;

namespace MSUIClient.Formats;

/// <summary>
/// One row from vanilla 1.12.1 WorldMapOverlay.dbc.
///
/// These are the exploration-reveal pieces drawn over a zone's 1002x668 map.
/// They are not the continent hover silhouettes; those are the separate
/// Interface\WorldMap\{Directory}\{Directory}Highlight.blp assets represented by
/// <see cref="WorldMapHighlightCatalog"/> below.
/// </summary>
public sealed class WorldMapOverlayInfo
{
    public uint Id { get; }
    public uint WorldMapAreaId { get; }
    public uint AreaId0 { get; }
    public uint AreaId1 { get; }
    public uint AreaId2 { get; }
    public uint AreaId3 { get; }
    public uint MapPointX { get; }
    public uint MapPointY { get; }
    public string TextureName { get; }
    public int TextureWidth { get; }
    public int TextureHeight { get; }
    public int OffsetX { get; }
    public int OffsetY { get; }
    public int HitRectTop { get; }
    public int HitRectLeft { get; }
    public int HitRectBottom { get; }
    public int HitRectRight { get; }

    public WorldMapOverlayInfo(uint id, uint worldMapAreaId,
        uint areaId0, uint areaId1, uint areaId2, uint areaId3,
        uint mapPointX, uint mapPointY, string textureName,
        int textureWidth, int textureHeight, int offsetX, int offsetY,
        int hitRectTop, int hitRectLeft, int hitRectBottom, int hitRectRight)
    {
        Id = id;
        WorldMapAreaId = worldMapAreaId;
        AreaId0 = areaId0;
        AreaId1 = areaId1;
        AreaId2 = areaId2;
        AreaId3 = areaId3;
        MapPointX = mapPointX;
        MapPointY = mapPointY;
        TextureName = textureName;
        TextureWidth = textureWidth;
        TextureHeight = textureHeight;
        OffsetX = offsetX;
        OffsetY = offsetY;
        HitRectTop = hitRectTop;
        HitRectLeft = hitRectLeft;
        HitRectBottom = hitRectBottom;
        HitRectRight = hitRectRight;
    }

    public IEnumerable<uint> AreaIds
    {
        get
        {
            if (AreaId0 != 0) yield return AreaId0;
            if (AreaId1 != 0) yield return AreaId1;
            if (AreaId2 != 0) yield return AreaId2;
            if (AreaId3 != 0) yield return AreaId3;
        }
    }

    public bool CoversArea(uint areaId) => areaId != 0 &&
        (AreaId0 == areaId || AreaId1 == areaId || AreaId2 == areaId || AreaId3 == areaId);

    /// <summary>The authored sub-zone label/hit rectangle in the 1002x668 map space.</summary>
    public bool HitRectContains(float x, float y) =>
        x >= HitRectLeft && x <= HitRectRight && y >= HitRectTop && y <= HitRectBottom;

    /// <summary>
    /// Expand the row into the exact 1-based, row-major 256px texture sequence
    /// consumed by WorldMapFrame.lua.
    /// </summary>
    public IReadOnlyList<WorldMapOverlayChunk> BuildChunks(string mapDirectory)
        => WorldMapOverlayChunk.Build(this, mapDirectory);
}

/// <summary>
/// One BLP used by a WorldMapOverlay row. PixelWidth/PixelHeight are the authored
/// visible destination; FileWidth/FileHeight reproduce WorldMapFrame.lua's
/// power-of-two UV denominator. The names match FrameXML's textureFileWidth/
/// textureFileHeight variables: they are part of the UI contract and are not a
/// promise that a replacement BLP's header has those physical dimensions.
/// </summary>
public readonly record struct WorldMapOverlayChunk(
    uint OverlayId,
    int Index,
    int Column,
    int Row,
    string TexturePath,
    int OffsetX,
    int OffsetY,
    int PixelWidth,
    int PixelHeight,
    int FileWidth,
    int FileHeight)
{
    public const int TileSize = 256;

    public float UMax => PixelWidth / (float)FileWidth;
    public float VMax => PixelHeight / (float)FileHeight;

    public static IReadOnlyList<WorldMapOverlayChunk> Build(
        WorldMapOverlayInfo overlay, string mapDirectory)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        if (string.IsNullOrWhiteSpace(mapDirectory) ||
            overlay.TextureWidth <= 0 || overlay.TextureHeight <= 0 ||
            string.IsNullOrWhiteSpace(overlay.TextureName))
            return Array.Empty<WorldMapOverlayChunk>();

        int columns = (overlay.TextureWidth + TileSize - 1) / TileSize;
        int rows = (overlay.TextureHeight + TileSize - 1) / TileSize;
        var result = new WorldMapOverlayChunk[checked(columns * rows)];
        string textureBase = OverlayTextureBase(mapDirectory, overlay.TextureName);

        int at = 0;
        for (int row = 0; row < rows; row++)
        {
            int pixelHeight = Math.Min(TileSize, overlay.TextureHeight - row * TileSize);
            int fileHeight = BackingTextureSize(pixelHeight);
            for (int column = 0; column < columns; column++)
            {
                int pixelWidth = Math.Min(TileSize, overlay.TextureWidth - column * TileSize);
                int fileWidth = BackingTextureSize(pixelWidth);
                int index = row * columns + column + 1;
                result[at++] = new WorldMapOverlayChunk(
                    overlay.Id, index, column, row, $"{textureBase}{index}.blp",
                    overlay.OffsetX + column * TileSize,
                    overlay.OffsetY + row * TileSize,
                    pixelWidth, pixelHeight, fileWidth, fileHeight);
            }
        }
        return result;
    }

    private static string OverlayTextureBase(string mapDirectory, string textureName)
    {
        string name = textureName.Replace('/', '\\');
        if (name.EndsWith(".blp", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        if (name.Contains('\\')) return name;
        return $@"Interface\WorldMap\{mapDirectory}\{name}";
    }

    private static int BackingTextureSize(int visiblePixels)
    {
        int size = 16;
        while (size < visiblePixels) size *= 2;
        return size;
    }
}

/// <summary>Typed access to vanilla WorldMapOverlay.dbc.</summary>
public sealed class WorldMapOverlayCatalog
{
    public const string Path = @"DBFilesClient\WorldMapOverlay.dbc";
    public const int FieldCount = 17;
    public const int RecordSize = FieldCount * 4;

    private static readonly IReadOnlyList<WorldMapOverlayInfo> Empty =
        Array.Empty<WorldMapOverlayInfo>();

    private readonly List<WorldMapOverlayInfo> _all = [];
    private readonly Dictionary<uint, IReadOnlyList<WorldMapOverlayInfo>> _byMapArea = [];
    private readonly Dictionary<uint, IReadOnlyList<WorldMapOverlayInfo>> _byArea = [];

    public IReadOnlyList<WorldMapOverlayInfo> All => _all;

    public IReadOnlyList<WorldMapOverlayInfo> ForMapArea(uint worldMapAreaId) =>
        _byMapArea.TryGetValue(worldMapAreaId, out IReadOnlyList<WorldMapOverlayInfo>? rows)
            ? rows : Empty;

    public IReadOnlyList<WorldMapOverlayInfo> ForArea(uint areaId) =>
        _byArea.TryGetValue(areaId, out IReadOnlyList<WorldMapOverlayInfo>? rows)
            ? rows : Empty;

    /// <summary>
    /// Every reveal piece for a zone, independent of the character's exploration
    /// flags. Drawing these after the twelve base map tiles produces the intended
    /// no-fog/full-intel RTS drill map.
    /// </summary>
    public IReadOnlyList<WorldMapOverlayChunk> BuildFullRevealChunks(
        uint worldMapAreaId, string mapDirectory)
    {
        IReadOnlyList<WorldMapOverlayInfo> overlays = ForMapArea(worldMapAreaId);
        if (overlays.Count == 0) return Array.Empty<WorldMapOverlayChunk>();
        var chunks = new List<WorldMapOverlayChunk>();
        foreach (WorldMapOverlayInfo overlay in overlays)
            chunks.AddRange(overlay.BuildChunks(mapDirectory));
        return chunks;
    }

    public static WorldMapOverlayCatalog? Load(MpqMount mpq)
    {
        ArgumentNullException.ThrowIfNull(mpq);
        byte[]? bytes = mpq.ReadFile(Path);
        DbcFile? dbc = bytes is null ? null : DbcFile.Parse(bytes);
        if (dbc is null || dbc.FieldCount != FieldCount || dbc.RecordSize != RecordSize)
            return null;

        var result = new WorldMapOverlayCatalog();
        var byMapArea = new Dictionary<uint, List<WorldMapOverlayInfo>>();
        var byArea = new Dictionary<uint, List<WorldMapOverlayInfo>>();

        for (int row = 0; row < dbc.RecordCount; row++)
        {
            var info = new WorldMapOverlayInfo(
                dbc.GetUInt(row, 0), dbc.GetUInt(row, 1),
                dbc.GetUInt(row, 2), dbc.GetUInt(row, 3),
                dbc.GetUInt(row, 4), dbc.GetUInt(row, 5),
                dbc.GetUInt(row, 6), dbc.GetUInt(row, 7), dbc.GetString(row, 8),
                checked((int)dbc.GetUInt(row, 9)), checked((int)dbc.GetUInt(row, 10)),
                checked((int)dbc.GetUInt(row, 11)), checked((int)dbc.GetUInt(row, 12)),
                checked((int)dbc.GetUInt(row, 13)), checked((int)dbc.GetUInt(row, 14)),
                checked((int)dbc.GetUInt(row, 15)), checked((int)dbc.GetUInt(row, 16)));
            result._all.Add(info);
            Add(byMapArea, info.WorldMapAreaId, info);
            foreach (uint areaId in info.AreaIds) Add(byArea, areaId, info);
        }

        foreach ((uint key, List<WorldMapOverlayInfo> rows) in byMapArea)
            result._byMapArea[key] = rows;
        foreach ((uint key, List<WorldMapOverlayInfo> rows) in byArea)
            result._byArea[key] = rows;
        return result;
    }

    private static void Add(Dictionary<uint, List<WorldMapOverlayInfo>> index,
        uint key, WorldMapOverlayInfo info)
    {
        if (key == 0) return;
        if (!index.TryGetValue(key, out List<WorldMapOverlayInfo>? rows))
            index[key] = rows = [];
        rows.Add(info);
    }
}

/// <summary>A normalized rectangle in the authored continent-map coordinate space.</summary>
public readonly record struct WorldMapNormalizedRect(float Left, float Top, float Right, float Bottom)
{
    public float Width => Right - Left;
    public float Height => Bottom - Top;
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public bool Contains(Vector2 point) => !IsEmpty &&
        point.X >= Left && point.X <= Right && point.Y >= Top && point.Y <= Bottom;

    public Vector2 Project(Vector2 point) => new(
        Left + point.X * Width, Top + point.Y * Height);
}

/// <summary>
/// Placement for a zone's continent hover art. UpdateMapHighlight returns these
/// same normalized values to WorldMapFrame.lua: the zone's WorldMapArea bounds
/// projected through the containing continent row.
/// </summary>
public readonly record struct WorldMapHighlightInfo(
    uint WorldMapAreaId,
    uint AreaId,
    uint MapId,
    string Directory,
    string TexturePath,
    WorldMapNormalizedRect Bounds,
    int TexturePixelHeight,
    int TextureFileHeight)
{
    /// <summary>UpdateMapHighlight's horizontal crop is a constant 1.0.</summary>
    public float UMax => 1f;

    /// <summary>
    /// UpdateMapHighlight's vertical crop. The source is authored 128 pixels
    /// wide; its logical height is truncated, then padded to a power of two.
    /// </summary>
    public float VMax => TexturePixelHeight / (float)TextureFileHeight;

    public bool RectangleContains(Vector2 normalizedPoint) => Bounds.Contains(normalizedPoint);
}

/// <summary>
/// RGB-intensity mask for ADD-authored continent highlight art. Vanilla's files
/// have opaque alpha and a black background, so alpha alone cannot describe the
/// visible shape.
/// </summary>
public sealed class WorldMapHighlightMask
{
    private readonly byte[] _intensity;

    public WorldMapHighlightInfo Info { get; }
    public int Width { get; }
    public int Height { get; }
    public int VisibleLeft { get; }
    public int VisibleTop { get; }
    public int VisibleRight { get; }
    public int VisibleBottom { get; }
    public bool HasVisiblePixels => VisibleRight > VisibleLeft && VisibleBottom > VisibleTop;

    public WorldMapNormalizedRect VisibleBounds
    {
        get
        {
            if (!HasVisiblePixels) return default;
            int logicalHeight = Math.Min(Height, Math.Max(1, Info.TexturePixelHeight));
            return new WorldMapNormalizedRect(
                Info.Bounds.Left + Info.Bounds.Width * VisibleLeft / Width,
                Info.Bounds.Top + Info.Bounds.Height * VisibleTop / logicalHeight,
                Info.Bounds.Left + Info.Bounds.Width * VisibleRight / Width,
                Info.Bounds.Top + Info.Bounds.Height * VisibleBottom / logicalHeight);
        }
    }

    private WorldMapHighlightMask(WorldMapHighlightInfo info, int width, int height,
        byte[] intensity, int visibleLeft, int visibleTop, int visibleRight, int visibleBottom)
    {
        Info = info;
        Width = width;
        Height = height;
        _intensity = intensity;
        VisibleLeft = visibleLeft;
        VisibleTop = visibleTop;
        VisibleRight = visibleRight;
        VisibleBottom = visibleBottom;
    }

    /// <summary>
    /// Sample the visible additive shape in normalized continent coordinates.
    /// This is useful for diagnostics and label placement, but is deliberately
    /// not vanilla hover ownership; use <see cref="WorldMapZoneHitCatalog"/> for
    /// pointer hit-testing. A threshold of one includes every antialiased texel.
    /// </summary>
    public bool Contains(Vector2 normalizedPoint, byte threshold = 1)
    {
        if (!Info.Bounds.Contains(normalizedPoint) || !HasVisiblePixels) return false;
        float u = (normalizedPoint.X - Info.Bounds.Left) / Info.Bounds.Width;
        float v = (normalizedPoint.Y - Info.Bounds.Top) / Info.Bounds.Height;
        int x = Math.Clamp((int)(u * Width), 0, Width - 1);
        int logicalHeight = Math.Min(Height, Math.Max(1, Info.TexturePixelHeight));
        int y = Math.Clamp((int)(v * logicalHeight), 0, logicalHeight - 1);
        return _intensity[y * Width + x] >= threshold;
    }

    public static WorldMapHighlightMask? Load(MpqMount mpq, WorldMapHighlightInfo info)
    {
        ArgumentNullException.ThrowIfNull(mpq);
        byte[]? bytes = mpq.ReadFile(info.TexturePath);
        if (bytes is null) return null;
        byte[] bgra = BlpDecoder.GetPixels(bytes, 0, out int width, out int height);
        if (width <= 0 || height <= 0) return null;

        var intensity = new byte[checked(width * height)];
        int logicalHeight = Math.Min(height, Math.Max(1, info.TexturePixelHeight));
        int left = width, top = logicalHeight, right = 0, bottom = 0;
        for (int pixel = 0; pixel < intensity.Length; pixel++)
        {
            int source = pixel * 4;
            int rgb = Math.Max(bgra[source], Math.Max(bgra[source + 1], bgra[source + 2]));
            byte value = (byte)(rgb * bgra[source + 3] / 255);
            intensity[pixel] = value;
            if (value == 0 || pixel / width >= logicalHeight) continue;
            int x = pixel % width;
            int y = pixel / width;
            left = Math.Min(left, x);
            top = Math.Min(top, y);
            right = Math.Max(right, x + 1);
            bottom = Math.Max(bottom, y + 1);
        }
        return new WorldMapHighlightMask(info, width, height, intensity,
            left, top, right, bottom);
    }
}

/// <summary>
/// Geometry and lazy shape-mask access for the {Directory}Highlight asset family.
/// </summary>
public sealed class WorldMapHighlightCatalog
{
    private readonly Dictionary<uint, WorldMapHighlightInfo> _byArea = [];
    private readonly Dictionary<uint, WorldMapHighlightMask?> _masks = [];

    public IEnumerable<WorldMapHighlightInfo> All => _byArea.Values;

    public bool TryGetArea(uint areaId, out WorldMapHighlightInfo info) =>
        _byArea.TryGetValue(areaId, out info);

    public static WorldMapHighlightCatalog Build(WorldMapAreaCatalog areas)
    {
        ArgumentNullException.ThrowIfNull(areas);
        var result = new WorldMapHighlightCatalog();
        foreach (WorldMapAreaInfo zone in areas.Areas)
        {
            if (zone.AreaId == 0 || string.IsNullOrWhiteSpace(zone.Directory) ||
                !areas.TryGetContinent(zone.MapId, out WorldMapAreaInfo continent))
                continue;

            Vector2 first = new(continent.X(zone.Left), continent.Y(zone.Top));
            Vector2 second = new(continent.X(zone.Right), continent.Y(zone.Bottom));
            var bounds = new WorldMapNormalizedRect(
                Math.Min(first.X, second.X), Math.Min(first.Y, second.Y),
                Math.Max(first.X, second.X), Math.Max(first.Y, second.Y));
            if (bounds.IsEmpty) continue;

            float width = zone.Left - zone.Right;
            float height = zone.Top - zone.Bottom;
            if (width <= 0 || height <= 0) continue;
            int texturePixelHeight = (int)(height * 128f / width); // __ftol: toward zero
            if (texturePixelHeight <= 0) continue;
            int textureFileHeight = 1;
            while (textureFileHeight < texturePixelHeight) textureFileHeight <<= 1;

            string path = $@"Interface\WorldMap\{zone.Directory}\{zone.Directory}Highlight.blp";
            result._byArea[zone.AreaId] = new WorldMapHighlightInfo(
                zone.Id, zone.AreaId, zone.MapId, zone.Directory, path, bounds,
                texturePixelHeight, textureFileHeight);
        }
        return result;
    }

    public bool TryLoadMask(MpqMount mpq, uint areaId, out WorldMapHighlightMask mask)
    {
        mask = null!;
        if (!_byArea.TryGetValue(areaId, out WorldMapHighlightInfo info)) return false;
        if (!_masks.TryGetValue(areaId, out WorldMapHighlightMask? loaded))
        {
            try { loaded = WorldMapHighlightMask.Load(mpq, info); }
            catch { loaded = null; }
            _masks[areaId] = loaded;
        }
        if (loaded is null) return false;
        mask = loaded;
        return true;
    }
}

/// <summary>
/// One vanilla continent area bitmap: 128x128 little-endian AreaTable IDs from
/// Interface\WorldMap\{Continent}.zmp. The same bitmap is windowed through the
/// continent row at continent level and through the selected zone row at zone
/// level; there is no per-zone ZMP.
/// </summary>
public sealed class WorldMapZoneMap
{
    public const int Edge = 128;
    public const int CellCount = Edge * Edge;
    public const int ByteCount = CellCount * sizeof(uint);
    private const float WorldToGrid = 2.9296876e-5f;

    private readonly uint[] _rawAreaIds;
    private readonly uint[] _resolvedAreaIds;

    public uint MapId { get; }
    public string Directory { get; }
    public string Path => $@"Interface\WorldMap\{Directory}.zmp";

    internal WorldMapZoneMap(uint mapId, string directory,
        uint[] rawAreaIds, uint[] resolvedAreaIds)
    {
        MapId = mapId;
        Directory = directory;
        _rawAreaIds = rawAreaIds;
        _resolvedAreaIds = resolvedAreaIds;
    }

    public uint RawAreaIdAt(int index) =>
        (uint)index < (uint)_rawAreaIds.Length ? _rawAreaIds[index] : 0;

    public uint ResolvedAreaIdAt(int index) =>
        (uint)index < (uint)_resolvedAreaIds.Length ? _resolvedAreaIds[index] : 0;

    public bool TryRawAreaId(WorldMapAreaInfo mapWindow, Vector2 normalizedPoint,
        out uint areaId)
    {
        areaId = 0;
        if (!TryCellIndex(mapWindow, normalizedPoint, out int index)) return false;
        areaId = _rawAreaIds[index];
        return areaId != 0;
    }

    /// <summary>
    /// Resolve vanilla hover/click ownership after the load-time, one-hop
    /// AreaTable parent remap and same-continent WorldMapArea match.
    /// </summary>
    public bool TryResolvedAreaId(WorldMapAreaInfo mapWindow, Vector2 normalizedPoint,
        out uint areaId)
    {
        areaId = 0;
        if (!TryCellIndex(mapWindow, normalizedPoint, out int index)) return false;
        areaId = _resolvedAreaIds[index];
        return areaId != 0;
    }

    /// <summary>
    /// The exact 5875 cursor-to-ZMP law (0x4a6ec0). Casts deliberately truncate
    /// toward zero, matching the client's __ftol operations.
    /// </summary>
    public static bool TryCellIndex(WorldMapAreaInfo mapWindow, Vector2 normalizedPoint,
        out int index)
    {
        index = -1;
        float u = ClampLikeClient(normalizedPoint.X);
        float v = ClampLikeClient(normalizedPoint.Y);
        float spanU = mapWindow.Left - mapWindow.Right;
        float spanV = mapWindow.Top - mapWindow.Bottom;
        if (spanU == 0 || spanV == 0) return false;

        float worldY = (1f - u) * spanU + mapWindow.Right;
        float worldX = (1f - v) * spanV + mapWindow.Bottom;
        float f1 = 0.5f - worldY * WorldToGrid;
        float f2 = 0.5f - worldX * WorldToGrid;
        if (!(f1 >= 0 && f1 <= 1) || !(f2 >= 0 && f2 <= 1)) return false;

        int column = (int)(f1 * Edge);
        int row = (int)(f2 * -Edge);
        int candidate = column - row * Edge;
        if ((uint)candidate >= CellCount) return false;
        index = candidate;
        return true;
    }

    private static float ClampLikeClient(float value) =>
        value >= 1 ? 1 : value >= 0 ? value : 0;
}

/// <summary>
/// Loads and remaps every shipped continent ZMP. Raw cells are rolled up by
/// exactly one AreaTable.zone_id hop, then retained only when a child
/// WorldMapArea on that same continent has the resulting AreaTable ID.
/// </summary>
public sealed class WorldMapZoneHitCatalog
{
    public const string AreaTablePath = @"DBFilesClient\AreaTable.dbc";

    private readonly Dictionary<uint, WorldMapZoneMap> _byMap = [];
    public IEnumerable<WorldMapZoneMap> All => _byMap.Values;

    public bool TryGetMap(uint mapId, out WorldMapZoneMap zoneMap) =>
        _byMap.TryGetValue(mapId, out zoneMap!);

    public bool TryResolveArea(uint mapId, WorldMapAreaInfo mapWindow,
        Vector2 normalizedPoint, out uint areaId)
    {
        areaId = 0;
        return _byMap.TryGetValue(mapId, out WorldMapZoneMap? zoneMap) &&
               zoneMap.TryResolvedAreaId(mapWindow, normalizedPoint, out areaId);
    }

    public static WorldMapZoneHitCatalog? Load(MpqMount mpq, WorldMapAreaCatalog mapAreas)
    {
        ArgumentNullException.ThrowIfNull(mpq);
        ArgumentNullException.ThrowIfNull(mapAreas);

        byte[]? areaBytes = mpq.ReadFile(AreaTablePath);
        DbcFile? areaTable = areaBytes is null ? null : DbcFile.Parse(areaBytes);
        if (areaTable is null || areaTable.FieldCount < 3) return null;

        var directParents = new Dictionary<uint, uint>();
        for (int row = 0; row < areaTable.RecordCount; row++)
            directParents[areaTable.GetUInt(row, 0)] = areaTable.GetUInt(row, 2);

        var result = new WorldMapZoneHitCatalog();
        foreach (uint mapId in mapAreas.Areas.Select(area => area.MapId).Distinct())
        {
            if (!mapAreas.TryGetContinent(mapId, out WorldMapAreaInfo continent) ||
                string.IsNullOrWhiteSpace(continent.Directory))
                continue;

            string path = $@"Interface\WorldMap\{continent.Directory}.zmp";
            byte[]? bytes = mpq.ReadFile(path);
            if (bytes is null || bytes.Length != WorldMapZoneMap.ByteCount) continue;

            var children = mapAreas.Areas
                .Where(area => area.MapId == mapId && area.AreaId != 0)
                .Select(area => area.AreaId)
                .ToHashSet();
            var raw = new uint[WorldMapZoneMap.CellCount];
            var resolved = new uint[WorldMapZoneMap.CellCount];
            for (int cell = 0; cell < raw.Length; cell++)
            {
                int offset = cell * sizeof(uint);
                uint areaId = BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes.AsSpan(offset, sizeof(uint)));
                raw[cell] = areaId;
                if (directParents.TryGetValue(areaId, out uint parent) && parent != 0)
                    areaId = parent; // one hop, deliberately not ParentZoneId's recursive walk
                if (children.Contains(areaId)) resolved[cell] = areaId;
            }
            result._byMap[mapId] = new WorldMapZoneMap(
                mapId, continent.Directory, raw, resolved);
        }
        return result;
    }
}

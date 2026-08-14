using System.Buffers.Binary;
using System.Numerics;
using MSUIClient.Formats;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

// Pure chunk-law fixture: a 315x300 overlay is 2x2, with the final BLPs padded
// to 64 pixels and cropped by UV exactly as WorldMapFrame.lua specifies.
var fixture = new WorldMapOverlayInfo(1, 2, 3, 0, 0, 0, 0, 0, "TEST",
    315, 300, 20, 30, 0, 0, 0, 0);
IReadOnlyList<WorldMapOverlayChunk> fixtureChunks = fixture.BuildChunks("Elwynn");
Check(fixtureChunks.Count == 4, "315x300 fixture did not produce a 2x2 chunk grid");
Check(fixtureChunks[0].Index == 1 && fixtureChunks[0].PixelWidth == 256 &&
      fixtureChunks[0].PixelHeight == 256 && fixtureChunks[0].FileWidth == 256 &&
      fixtureChunks[0].FileHeight == 256, "full chunk geometry changed");
Check(fixtureChunks[1].Index == 2 && fixtureChunks[1].OffsetX == 276 &&
      fixtureChunks[1].PixelWidth == 59 && fixtureChunks[1].FileWidth == 64 &&
      Math.Abs(fixtureChunks[1].UMax - 59f / 64f) < 0.00001f,
    "right partial chunk geometry changed");
Check(fixtureChunks[2].Index == 3 && fixtureChunks[2].OffsetY == 286 &&
      fixtureChunks[2].PixelHeight == 44 && fixtureChunks[2].FileHeight == 64 &&
      Math.Abs(fixtureChunks[2].VMax - 44f / 64f) < 0.00001f,
    "bottom partial chunk geometry changed");
Check(fixtureChunks[3].TexturePath == @"Interface\WorldMap\Elwynn\TEST4.blp",
    "overlay texture naming/order changed");

string dataPath = args.Length > 0
    ? Path.GetFullPath(args[0])
    : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "GameData", "Data"));
Check(Directory.Exists(dataPath), $"client Data directory not found: {dataPath}");

using var mpq = new MpqMount(dataPath);
WorldMapAreaCatalog areas = WorldMapAreaCatalog.Load(mpq)
    ?? throw new InvalidOperationException("WorldMapArea.dbc did not load");
WorldMapOverlayCatalog overlays = WorldMapOverlayCatalog.Load(mpq)
    ?? throw new InvalidOperationException("WorldMapOverlay.dbc was not the vanilla 17-field schema");

Check(overlays.All.Count == 526, $"expected 526 vanilla overlays, got {overlays.All.Count}");
Check(overlays.ForMapArea(13).Count == 0 && overlays.ForMapArea(14).Count == 0,
    "exploration overlays unexpectedly appeared on a continent map");
var mapAreasById = areas.Areas.ToDictionary(area => area.Id);
int allChunks = 0;
int replacementSizedChunks = 0;
foreach (WorldMapOverlayInfo overlay in overlays.All)
{
    Check(mapAreasById.TryGetValue(overlay.WorldMapAreaId, out WorldMapAreaInfo owner),
        $"overlay {overlay.Id} refers to unknown WorldMapArea {overlay.WorldMapAreaId}");
    foreach (WorldMapOverlayChunk chunk in overlay.BuildChunks(owner.Directory))
    {
        allChunks++;
        byte[] asset = mpq.ReadFile(chunk.TexturePath)
            ?? throw new InvalidOperationException($"missing overlay asset: {chunk.TexturePath}");
        Check(asset.Length >= 20 && asset[0] == 'B' && asset[1] == 'L' &&
              asset[2] == 'P' && asset[3] == '2', $"not BLP2: {chunk.TexturePath}");
        int fileWidth = (int)BinaryPrimitives.ReadUInt32LittleEndian(asset.AsSpan(12, 4));
        int fileHeight = (int)BinaryPrimitives.ReadUInt32LittleEndian(asset.AsSpan(16, 4));
        Check(fileWidth > 0 && fileHeight > 0 &&
              BitOperations.IsPow2((uint)fileWidth) && BitOperations.IsPow2((uint)fileHeight),
            $"invalid BLP backing size {chunk.TexturePath}: {fileWidth}x{fileHeight}");
        if (fileWidth != chunk.FileWidth || fileHeight != chunk.FileHeight)
            replacementSizedChunks++;
    }
}
Check(allChunks > overlays.All.Count, "overlay catalog did not expand any multi-chunk rows");
Check(areas.TryGetArea(12, out WorldMapAreaInfo elwynn) && elwynn.Id == 30,
    "Elwynn WorldMapArea row changed");
Check(overlays.ForMapArea(elwynn.Id).Count == 12,
    $"expected 12 Elwynn reveal rows, got {overlays.ForMapArea(elwynn.Id).Count}");

IReadOnlyList<WorldMapOverlayChunk> elwynnChunks =
    overlays.BuildFullRevealChunks(elwynn.Id, elwynn.Directory);
Check(elwynnChunks.Count > overlays.ForMapArea(elwynn.Id).Count,
    "Elwynn full reveal did not expand multi-chunk overlays");
foreach (WorldMapOverlayChunk chunk in elwynnChunks)
{
    Check(chunk.PixelWidth is > 0 and <= 256 && chunk.PixelHeight is > 0 and <= 256,
        $"invalid visible chunk dimensions: {chunk.TexturePath}");
    Check(chunk.UMax is > 0 and <= 1 && chunk.VMax is > 0 and <= 1,
        $"invalid chunk UV: {chunk.TexturePath}");
    Check(mpq.ReadFile(chunk.TexturePath) is not null, $"missing overlay asset: {chunk.TexturePath}");
}

WorldMapHighlightCatalog highlights = WorldMapHighlightCatalog.Build(areas);
Check(highlights.TryGetArea(elwynn.AreaId, out WorldMapHighlightInfo highlight),
    "Elwynn highlight geometry was not built");
Check(highlight.TexturePath == @"Interface\WorldMap\Elwynn\ElwynnHighlight.blp",
    "Elwynn highlight path changed");
Check(highlight.Bounds.Width > 0 && highlight.Bounds.Height > 0,
    "Elwynn highlight placement is empty");
Check(highlight.UMax == 1f && highlight.TexturePixelHeight == 85 &&
      highlight.TextureFileHeight == 128 &&
      Math.Abs(highlight.VMax - 85f / 128f) < 0.00001f,
    "Elwynn UpdateMapHighlight crop changed");
Check(highlights.TryLoadMask(mpq, elwynn.AreaId, out WorldMapHighlightMask mask) &&
      mask.Width == 128 && mask.Height == 128 && mask.HasVisiblePixels,
    "Elwynn additive shape mask did not load");

bool foundVisible = false;
bool foundBlackInsideRect = false;
for (int y = 0; y < mask.Height; y++)
for (int x = 0; x < mask.Width; x++)
{
    Vector2 sample = highlight.Bounds.Project(new Vector2(
        (x + 0.5f) / mask.Width, (y + 0.5f) / mask.Height));
    if (mask.Contains(sample)) foundVisible = true;
    else foundBlackInsideRect = true;
}
Check(foundVisible && foundBlackInsideRect,
    "highlight mask did not distinguish the visible zone shape from black padding");
Check(highlight.RectangleContains(mask.VisibleBounds.Project(new Vector2(0.5f))),
    "visible highlight bounds escaped their WorldMapArea placement");

WorldMapZoneHitCatalog hits = WorldMapZoneHitCatalog.Load(mpq, areas)
    ?? throw new InvalidOperationException("AreaTable/ZMP hit catalog did not load");
if (!areas.TryGetContinent(0, out WorldMapAreaInfo azeroth) ||
    !hits.TryGetMap(0, out WorldMapZoneMap azerothGrid))
    throw new InvalidOperationException("Azeroth ZMP did not load");
Vector2 goldshire = new(azeroth.X(60f), azeroth.Y(-9450f));
Check(WorldMapZoneMap.TryCellIndex(azeroth, goldshire, out int goldshireCell) &&
      goldshireCell == 12735, $"Goldshire cursor law changed: {goldshireCell}");
Check(azerothGrid.RawAreaIdAt(goldshireCell) == 12 &&
      azerothGrid.ResolvedAreaIdAt(goldshireCell) == 12 &&
      azerothGrid.TryResolvedAreaId(azeroth, goldshire, out uint goldshireArea) &&
      goldshireArea == elwynn.AreaId,
    "Goldshire ZMP ownership did not resolve to Elwynn");
bool sawOneHopRollup = false;
for (int cell = 0; cell < WorldMapZoneMap.CellCount; cell++)
{
    uint raw = azerothGrid.RawAreaIdAt(cell);
    uint resolved = azerothGrid.ResolvedAreaIdAt(cell);
    if (raw != 0 && resolved != 0 && raw != resolved)
    {
        sawOneHopRollup = true;
        break;
    }
}
Check(sawOneHopRollup, "Azeroth ZMP never exercised the one-hop AreaTable remap");

Console.WriteLine($"PASS overlays={overlays.All.Count} chunks={allChunks} " +
                  $"replacementSized={replacementSizedChunks} " +
                  $"elwynnRows={overlays.ForMapArea(elwynn.Id).Count} " +
                  $"elwynnChunks={elwynnChunks.Count} highlight={mask.Width}x{mask.Height} " +
                  $"goldshireCell={goldshireCell}");

using System.Numerics;
using MSUIClient.Engine.UI;

int checks = 0;
void Check(bool condition, string message)
{
    checks++;
    if (!condition) throw new InvalidDataException(message);
}

void Near(float actual, float expected, string message)
{
    Check(MathF.Abs(actual - expected) <= 0.001f,
        $"{message}: expected {expected}, got {actual}");
}

Vector2 frame = CommanderMapUiLaw.FrameSize(CommanderMapUiLaw.AuthoredWidth);
Near(frame.X, 1002f, "authored frame width drifted");
Near(frame.Y, 668f, "authored frame height drifted");

(Vector2 firstMin, Vector2 firstMax) = CommanderMapUiLaw.TileBounds(
    Vector2.Zero, CommanderMapUiLaw.AuthoredWidth, 0, 0);
Check(firstMin == Vector2.Zero, "first atlas tile did not start at the frame origin");
Near(firstMax.X, 256f, "atlas tiles stopped being square (width)");
Near(firstMax.Y, 256f, "atlas tiles stopped being square (height)");

(Vector2 lastMin, Vector2 lastMax) = CommanderMapUiLaw.TileBounds(
    Vector2.Zero, CommanderMapUiLaw.AuthoredWidth, 2, 3);
Check(lastMin == new Vector2(768f, 512f), "last atlas tile origin drifted");
Check(lastMax == new Vector2(1024f, 768f), "padded atlas extent was squeezed into the frame");
Near(frame.X - lastMin.X, 234f, "right-edge clip tail drifted");
Near(frame.Y - lastMin.Y, 156f, "bottom-edge clip tail drifted");

Vector2 runtimeFrame = CommanderMapUiLaw.FrameSize(730f);
Near(runtimeFrame.X, 730f, "runtime frame width drifted");
Near(runtimeFrame.Y, 730f * 668f / 1002f, "runtime frame aspect drifted");
(Vector2 runtimeTileMin, Vector2 runtimeTileMax) = CommanderMapUiLaw.TileBounds(
    Vector2.Zero, 730f, 1, 1);
Near(runtimeTileMax.X - runtimeTileMin.X, runtimeTileMax.Y - runtimeTileMin.Y,
    "runtime atlas tile stopped being square");

Vector2 origin = new(37f, 91f);
Vector2 midpoint = CommanderMapUiLaw.Project(origin, frame, new Vector2(0.5f));
Check(midpoint == origin + frame * 0.5f, "normalized midpoint did not project to the frame midpoint");
Vector2 corner = CommanderMapUiLaw.Project(origin, frame, Vector2.One);
Check(corner == origin + frame, "normalized map corner did not reach the authored clip corner");
Vector2 roundTrip = CommanderMapUiLaw.Unproject(origin, frame, midpoint);
Near(roundTrip.X, 0.5f, "projection round-trip X drifted");
Near(roundTrip.Y, 0.5f, "projection round-trip Y drifted");
Check(CommanderMapUiLaw.Contains(origin, frame, corner), "authored corner was not clickable");
Check(!CommanderMapUiLaw.Contains(origin, frame, origin + new Vector2(500f, 700f)),
    "bottom atlas padding remained clickable");

// Dual-continent overview layout: both maps exist in every responsive mode,
// with deterministic EK-left/top and Kalimdor-right/bottom ordering.
CommanderMapUiLaw.DualViewportLayout wide = CommanderMapUiLaw.LayoutDualViewports(
    new Vector2(12f, 30f), new Vector2(1200f, 600f), gap: 20f,
    minimumSideBySideWidth: 420f);
Check(!wide.Stacked, "wide overview unexpectedly stacked its continents");
Near(wide.EasternKingdoms.Size.X, 590f, "wide EK cell width drifted");
Near(wide.Kalimdor.Min.X, 622f, "wide Kalimdor cell origin drifted");
Check(wide.EasternKingdoms.Max.X < wide.Kalimdor.Min.X,
    "wide continent cells overlap");

CommanderMapUiLaw.DualViewportLayout narrow = CommanderMapUiLaw.LayoutDualViewports(
    new Vector2(7f, 11f), new Vector2(800f, 1000f), gap: 20f,
    minimumSideBySideWidth: 420f);
Check(narrow.Stacked, "narrow overview did not stack its continents");
Near(narrow.EasternKingdoms.Size.Y, 490f, "stacked EK cell height drifted");
Near(narrow.Kalimdor.Min.Y, 521f, "stacked Kalimdor cell origin drifted");
Check(narrow.EasternKingdoms.Max.Y < narrow.Kalimdor.Min.Y,
    "stacked continent cells overlap");

var landCrop = new CommanderMapUiLaw.NormalizedRect(
    new Vector2(0.2f, 0.1f), new Vector2(0.8f, 0.9f));
CommanderMapUiLaw.ScreenRect ekViewport = CommanderMapUiLaw.FitViewportToCrop(
    wide.EasternKingdoms, landCrop);
Near(ekViewport.Aspect, landCrop.MapPixelAspect,
    "land-crop viewport distorted authored map pixels");
Check(ekViewport.Min.X >= wide.EasternKingdoms.Min.X &&
      ekViewport.Min.Y >= wide.EasternKingdoms.Min.Y &&
      ekViewport.Max.X <= wide.EasternKingdoms.Max.X &&
      ekViewport.Max.Y <= wide.EasternKingdoms.Max.Y,
    "land-crop viewport escaped its overview cell");

var edgeCrop = new CommanderMapUiLaw.NormalizedRect(
    new Vector2(0.02f, 0.04f), new Vector2(0.96f, 0.97f));
CommanderMapUiLaw.NormalizedRect padded = CommanderMapUiLaw.PadCrop(
    edgeCrop, new Vector2(0.08f, 0.07f));
Check(padded.Min == Vector2.Zero && padded.Max == Vector2.One,
    "land-crop padding did not clamp to the authored map");

Vector2 cropSource = new(0.41f, 0.67f);
Vector2 cropScreen = CommanderMapUiLaw.ProjectCrop(ekViewport, landCrop, cropSource);
Vector2 cropRoundTrip = CommanderMapUiLaw.UnprojectCrop(ekViewport, landCrop, cropScreen);
Near(cropRoundTrip.X, cropSource.X, "crop projection round-trip X drifted");
Near(cropRoundTrip.Y, cropSource.Y, "crop projection round-trip Y drifted");

var fullCrop = new CommanderMapUiLaw.NormalizedRect(Vector2.Zero, Vector2.One);

Check(CommanderMapUiLaw.ShowWorldPresence(areaMap: 0, bots: 2, players: 0) &&
      CommanderMapUiLaw.ShowWorldPresence(areaMap: 1, bots: 0, players: 1),
    "dual overview hid one continent's live presence");
Check(!CommanderMapUiLaw.ShowWorldPresence(areaMap: 33, bots: 4, players: 2) &&
      !CommanderMapUiLaw.ShowWorldPresence(areaMap: 1, bots: 0, players: 0),
    "dual overview presented an instance or an empty row as continent presence");
Check(CommanderMapUiLaw.ShowCameraMarker(viewportMap: 1, playerMap: 1) &&
      !CommanderMapUiLaw.ShowCameraMarker(viewportMap: 0, playerMap: 1),
    "camera marker was not confined to the character's continent");
Check(CommanderMapUiLaw.CanTravelCamera(playerMap: 0, destinationMap: 0) &&
      !CommanderMapUiLaw.CanTravelCamera(playerMap: 0, destinationMap: 1),
    "cross-continent camera travel law drifted");

// 1.12 WorldMapOverlay rows are exploration-detail graphics, not continent
// hover masks. No-fog expands every row unconditionally into 256px chunks and
// crops each power-of-two backing texture with UVs.
CommanderMapUiLaw.OverlayChunk[] banethil = CommanderMapUiLaw.NoFogOverlayChunks(
    offsetX: 382, offsetY: 281, width: 160, height: 210);
Check(banethil.Length == 1 && banethil[0].TextureIndex == 1,
    "single-chunk overlay count or suffix drifted");
Check(banethil[0].PixelMin == new Vector2(382f, 281f) &&
      banethil[0].PixelSize == new Vector2(160f, 210f),
    "single-chunk overlay destination drifted");
Check(banethil[0].TexturePixelSize == new Vector2(256f),
    "overlay backing texture stopped using power-of-two dimensions");
Near(banethil[0].UvMax.X, 160f / 256f, "overlay tail U drifted");
Near(banethil[0].UvMax.Y, 210f / 256f, "overlay tail V drifted");

CommanderMapUiLaw.OverlayChunk[] four = CommanderMapUiLaw.NoFogOverlayChunks(
    offsetX: 17, offsetY: 29, width: 300, height: 300);
Check(four.Length == 4 && four[0].TextureIndex == 1 && four[3].TextureIndex == 4,
    "multi-chunk overlay did not use row-major 1-based suffixes");
Check(four[1].PixelMin == new Vector2(273f, 29f) &&
      four[2].PixelMin == new Vector2(17f, 285f),
    "multi-chunk overlay row-major placement drifted");
Check(four[3].PixelSize == new Vector2(44f) &&
      four[3].TexturePixelSize == new Vector2(64f),
    "overlay tail did not select its next power-of-two backing");
Near(four[3].UvMax.X, 44f / 64f, "multi-chunk tail U drifted");
Near(four[3].UvMax.Y, 44f / 64f, "multi-chunk tail V drifted");

CommanderMapUiLaw.OverlayChunk[] tiny = CommanderMapUiLaw.NoFogOverlayChunks(
    offsetX: 0, offsetY: 0, width: 1, height: 1);
Check(tiny[0].TexturePixelSize == new Vector2(16f),
    "overlay backing fell below FrameXML's 16px minimum");
Near(tiny[0].UvMax.X, 1f / 16f, "minimum overlay backing UV drifted");
Check(CommanderMapUiLaw.OverlayChunkPath(
        @"Interface\WorldMap\Elwynn\BANETHILHOLLOW", 4) ==
      @"Interface\WorldMap\Elwynn\BANETHILHOLLOW4",
    "overlay texture suffix path drifted");

CommanderMapUiLaw.ScreenRect authoredViewport = new(Vector2.Zero, frame);
CommanderMapUiLaw.ScreenRect overlayProjected = CommanderMapUiLaw.ProjectOverlayChunk(
    banethil[0], authoredViewport, fullCrop);
Check(overlayProjected.Min == banethil[0].PixelMin &&
      overlayProjected.Size == banethil[0].PixelSize,
    "overlay projection drifted at authored 1:1 scale");

// Continent hover uses <Directory>Highlight, a separate stock asset. It is
// placed over the zone's WorldMapArea rectangle and UV-cropped to its authored
// content rather than stretched across its power-of-two padding.
var highlightBounds = new CommanderMapUiLaw.NormalizedRect(
    new Vector2(100f / CommanderMapUiLaw.AuthoredWidth,
        200f / CommanderMapUiLaw.AuthoredHeight),
    new Vector2(199f / CommanderMapUiLaw.AuthoredWidth,
        266f / CommanderMapUiLaw.AuthoredHeight));
CommanderMapUiLaw.ZoneHighlightPlacement highlight = CommanderMapUiLaw.PlaceZoneHighlight(
    "Elwynn", highlightBounds, authoredViewport, fullCrop);
Check(highlight.TexturePath == @"Interface\WorldMap\Elwynn\ElwynnHighlight.blp",
    "stock continent-highlight path drifted");
Near(highlight.Destination.Min.X, 100f, "highlight destination X drifted");
Near(highlight.Destination.Min.Y, 200f, "highlight destination Y drifted");
Near(highlight.Destination.Size.X, 99f, "highlight destination width drifted");
Near(highlight.Destination.Size.Y, 66f, "highlight destination height drifted");
Near(highlight.UvMax.X, 1f, "highlight U crop drifted");
Near(highlight.UvMax.Y, 85f / 128f, "highlight V crop drifted");

Check(!CommanderMapUiLaw.ShowHonor(mode: 1, modules: 0),
    "R1 foundation falsely presented an active Honor system");
Check(CommanderMapUiLaw.ShowHonor(mode: 1, modules: CommanderMapUiLaw.HonorModule),
    "active Honor module was hidden");
Check(!CommanderMapUiLaw.ShowHonor(mode: 0, modules: CommanderMapUiLaw.HonorModule),
    "vanilla mode presented RTS Honor");
Check(CommanderMapUiLaw.CampaignStatus(mode: 1, modules: 0, honor: 900) == "RTS CAMPAIGN",
    "R1 foundation campaign copy exposed inert Honor state");
string honorStatus = CommanderMapUiLaw.CampaignStatus(
    mode: 1, modules: CommanderMapUiLaw.HonorModule, honor: 900);
Check(honorStatus.Contains("Honor", StringComparison.Ordinal),
    "active Honor module did not label its pool");
int honorSeparator = honorStatus.IndexOf('\u00B7');
Check(honorSeparator >= 0 && honorSeparator == honorStatus.LastIndexOf('\u00B7') &&
      honorStatus.IndexOf('\u00C2') < 0,
    "Honor-enabled status must contain exactly one U+00B7 and no U+00C2");
Check(CommanderMapUiLaw.CampaignStatus(mode: 0, modules: 0, honor: 0).Length == 0,
    "vanilla mode emitted an RTS campaign status");

Check(CommanderMapUiLaw.ShowPresence(selectedMap: 0, areaMap: 0, bots: 0, players: 1),
    "selected-continent player presence was hidden");
Check(!CommanderMapUiLaw.ShowPresence(selectedMap: 0, areaMap: 1, bots: 4, players: 3),
    "off-continent population leaked into the selected panel");
Check(!CommanderMapUiLaw.ShowPresence(selectedMap: 0, areaMap: 0, bots: 0, players: 0),
    "zero population was presented as active presence");

string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
    "..", "..", "..", "..", ".."));
string renderer = File.ReadAllText(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
    "GameLoop.CommanderMap.cs")).Replace("\r\n", "\n", StringComparison.Ordinal);
Check(renderer.Contains("CommanderMapUiLaw.TileBounds", StringComparison.Ordinal),
    "Commander renderer bypassed the tested atlas layout law");
Check(!renderer.Contains("mapW / 4f", StringComparison.Ordinal) &&
      !renderer.Contains("mapH / 3f", StringComparison.Ordinal),
    "Commander renderer reintroduced squeezed atlas cells");
Check(renderer.Contains("_commanderMapHits?.TryResolveArea", StringComparison.Ordinal),
    "Commander overview stopped using exact ZMP area ownership");
Check(renderer.Contains("AdditiveHandle(highlight.TexturePath)", StringComparison.Ordinal),
    "Commander hover stopped using the stock additive zone highlight");
Check(renderer.Contains("DrawCommanderNoFogOverlays(dl, zone, mapMin, mapW, mapH);",
        StringComparison.Ordinal),
    "Commander zone drill stopped drawing every no-fog overlay");
Check(renderer.Contains("stageMax - bodyMin, gap, 420f);", StringComparison.Ordinal) &&
      !renderer.Contains("420f * s", StringComparison.Ordinal),
    "Commander responsive breakpoint stopped being a physical-pixel threshold");
Check(!renderer.Contains("DrawCommanderMapFrameLegacy", StringComparison.Ordinal) &&
      !renderer.Contains("_commanderZoneRects", StringComparison.Ordinal),
    "Commander rollback-only renderer state returned");

Console.WriteLine($"commander-map clinical check PASS ({checks} assertions)");

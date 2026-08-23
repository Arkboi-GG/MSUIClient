using System.Numerics;
using MSUIClient;
using MSUIClient.Engine.UI;

internal static class WorldMapClinicalChecks
{
    public static void Run()
    {
        WorldMapDropdownGeometry continent = WorldMapUiLaw.Continent(Vector2.Zero, 1f, 2);
        WorldMapDropdownGeometry zone = WorldMapUiLaw.Zone(Vector2.Zero, 1f, 40);
        WorldMapUiLaw.FrameLayout wide = WorldMapUiLaw.Frame(new Vector2(1920, 1080));
        Check(MathF.Abs(wide.Scale - 1.40625f) < .0001f &&
              Vector2.Distance(wide.LogicalOrigin, new Vector2(170.66667f, 0)) < .001f &&
              wide.LogicalSize == new Vector2(1024, 768) &&
              WorldMapUiLaw.FrameRect ==
                  new WorldMapUiLaw.LogicalRect(0, 0, 1024, 768) &&
              WorldMapUiLaw.MapRect ==
                  new WorldMapUiLaw.LogicalRect(11, 69, 1002, 668) &&
              WorldMapUiLaw.ShellTile(2, 3) ==
                  new WorldMapUiLaw.LogicalRect(768, 512, 256, 256) &&
              WorldMapUiLaw.ViewAction ==
                  new WorldMapUiLaw.LogicalRect(680, 34, 110, 22) &&
              WorldMapUiLaw.TitleCenter == new Vector2(512, 12) &&
              WorldMapUiLaw.TitleFont == "GameFontNormal" &&
              WorldMapUiLaw.HoverLabelFont == "WorldMapTextFont" &&
              WorldMapUiLaw.HoverLabel(new Vector2(11, 69), new Vector2(1002, 668), 1) ==
                  new Vector2(532, 115) &&
              WorldMapUiLaw.Close ==
                  new WorldMapUiLaw.LogicalRect(982, 4, 32, 32),
            "world-map fullscreen fit/letterbox positioning law drift");
        Check(continent.FrameMin == new Vector2(342, 35) &&
              continent.FrameSize == new Vector2(180, 32) &&
              continent.ListMin == new Vector2(350, 60) &&
              continent.ListSize == new Vector2(162, 62) &&
              zone.FrameMin == new Vector2(489, 35) &&
              zone.ListSize == new Vector2(162, 542),
            "world-map dropdown anchor, fixed width, border, or 32-row cap drift");
        Check(WorldMapUiLaw.RowMin(continent, 0, 1f) == new Vector2(367, 75) &&
              WorldMapUiLaw.RowMin(continent, 1, 1f) == new Vector2(367, 91) &&
              continent.Contains(new Vector2(400, 100), true) &&
              !continent.Contains(new Vector2(400, 100), false),
            "world-map dropdown row or pointer-ownership law drift");
        Check(WorldMapUiLaw.CapsuleSlices.Length == 3 &&
              WorldMapUiLaw.CapsuleSlices[0].Rect ==
                  new WorldMapUiLaw.LogicalRect(0, -17, 25, 64) &&
              WorldMapUiLaw.CapsuleSlices[1].Rect ==
                  new WorldMapUiLaw.LogicalRect(25, -17, 130, 64) &&
              WorldMapUiLaw.CapsuleSlices[2].Rect ==
                  new WorldMapUiLaw.LogicalRect(155, -17, 25, 64) &&
              WorldMapUiLaw.CapsuleButton ==
                  new WorldMapUiLaw.LogicalRect(140, 1, 24, 24) &&
              WorldMapUiLaw.DropdownRow ==
                  new WorldMapUiLaw.LogicalRect(0, 0, 130, 16) &&
              WorldMapUiLaw.MapPoint(new Vector2(11, 69), new Vector2(1002, 668),
                  .5f, .25f) == new Vector2(512, 236) &&
              WorldMapUiLaw.CorpseTooltipSeat(new Vector2(800, 300), new Vector2(16),
                  new Vector2(11, 69), new Vector2(1002, 668)) ==
                  new WorldMapUiLaw.TooltipSeat(new Vector2(800, 300), Vector2.One) &&
              WorldMapUiLaw.CorpseTooltipSeat(new Vector2(200, 300), new Vector2(16),
                  new Vector2(11, 69), new Vector2(1002, 668)) ==
                  new WorldMapUiLaw.TooltipSeat(new Vector2(216, 300), Vector2.UnitY),
            "world-map capsule slices, row furniture, or map projection drift");

        string root = ClientConfig.FindRepoRoot();
        string map = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.WorldMap.cs"));
        string catalog = SourceText.Read(Path.Combine(root, "MSUIClient", "Formats",
            "WorldMapAreaCatalog.cs"));
        Check(map.Contains("DrawWorldMapExploredOverlays", StringComparison.Ordinal) &&
              map.Contains("DrawWorldMapAreaHighlight", StringComparison.Ordinal) &&
              map.Contains("_worldMapHits?.TryResolveArea", StringComparison.Ordinal) &&
              map.Contains("DrawMinimapPlayerArrow(dl, player.Orientation", StringComparison.Ordinal),
            "world-map exploration, click/hover, or facing-arrow wiring drift");
        Check(map.Contains("DrawWorldMapDropdownCapsule", StringComparison.Ordinal) &&
              map.Contains("WorldMapUiLaw.Frame(display)", StringComparison.Ordinal) &&
              map.Contains("WorldMapUiLaw.Continent", StringComparison.Ordinal) &&
              map.Contains("WorldMapUiLaw.Zone", StringComparison.Ordinal) &&
              map.Contains("WorldMapUiLaw.CapsuleSlices", StringComparison.Ordinal) &&
              map.Contains("WorldMapUiLaw.MapPoint", StringComparison.Ordinal) &&
              map.Contains("WorldMapUiLaw.PixelRect", StringComparison.Ordinal) &&
              map.Contains("WorldMapUiLaw.CorpseTooltipSeat", StringComparison.Ordinal) &&
              map.Contains("world-map-corpse", StringComparison.Ordinal) &&
              map.Contains("GameText.DrawCentered(dl, WorldMapUiLaw.TitleFont",
                  StringComparison.Ordinal) &&
              map.Contains("GameText.DrawCentered(dl, WorldMapUiLaw.HoverLabelFont",
                  StringComparison.Ordinal) &&
              !map.Contains("DrawWorldMapOutlinedCenteredText", StringComparison.Ordinal) &&
              !map.Contains("ViewLabelCenter", StringComparison.Ordinal) &&
              !map.Contains("new Vector2", StringComparison.Ordinal) &&
              map.Contains("WowSkin.Dialog", StringComparison.Ordinal) &&
              map.Contains("UI-CheckBox-Check", StringComparison.Ordinal) &&
              catalog.Contains("_continentOrder.Add(info)", StringComparison.Ordinal) &&
              catalog.Contains("_areaOrder.Add(info)", StringComparison.Ordinal),
            "world-map dropdown law, authored art, checks, or stable catalog order drift");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}

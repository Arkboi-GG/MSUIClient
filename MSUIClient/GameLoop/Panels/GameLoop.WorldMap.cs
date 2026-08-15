using System.Numerics;
using ImGuiNET;
using Silk.NET.Input;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private bool _worldMapOpen;
    private bool _worldMapKeyWasDown;
    private int _worldMapZoom = 1;
    private uint _worldMapSelectedAreaId;
    private WorldMapAreaCatalog? _worldMapAreas;
    private bool _worldMapAreasLoaded;

    private void UpdateWorldMapInput(bool typing)
    {
        bool down = BindingDown(GameBinding.OpenWorldMap);
        if (down && !_worldMapKeyWasDown && !typing && _net is { IsInWorld: true })
        {
            // In the free view the same binding opens the RTS commander map; the
            // vanilla map stays exactly what it always was everywhere else.
            if (_freeView) ToggleCommanderMap();
            else ToggleWorldMap();
        }
        _worldMapKeyWasDown = down;
    }

    private void ToggleWorldMap()
    {
        _worldMapOpen = !_worldMapOpen;
        if (!_worldMapOpen) return;
        // Vanilla WorldMapFrame OnShow always starts at the character's current zone.
        _worldMapZoom = 1;
        _worldMapSelectedAreaId = 0;
    }

    private void DrawWorldMapFrame()
    {
        if (!_worldMapOpen || _gameplayArt is null || _net is null ||
            !_entities.TryGet(_net.PlayerGuid, out WorldEntity player)) return;
        Vector2 display = ImGui.GetIO().DisplaySize;
        float fitScale = MathF.Max(0.01f,
            MathF.Min(display.X / 1024f, display.Y / 768f));
        Vector2 logicalSize = new(1024, 768);
        Vector2 logicalOrigin = (display / fitScale - logicalSize) * .5f;
        if (!BeginVanillaWindow("##world-map", logicalOrigin, logicalSize,
                out ImDrawListPtr dl, out Vector2 origin, out float s, fitScale))
        { ImGui.End(); return; }
        // FULLSCREEN_DIALOG strata: submit on the foreground list so both the
        // letterbox and authored map cover every ordinary or developer window.
        dl = ImGui.GetForegroundDrawList();
        dl.AddRectFilled(Vector2.Zero, display, 0xff000000);
        string[] shellRows = ["Top", "Middle", "Bottom"];
        for (int row = 0; row < 3; row++)
        for (int col = 0; col < 4; col++)
            DrawArt(dl, $@"Interface\WorldMap\UI-WorldMap-{shellRows[row]}{col + 1}",
                origin + new Vector2(col * 256, row * 256) * s, new Vector2(256), s);

        EnsureWorldMapAreas();
        EnsureAreaTableForMinimap();
        EnsureWorldMapSupportingData();
        uint playerMap = _net.Player?.Map ?? 0;
        uint currentZoneId = _areas?.ParentZoneId(_minimapAreaId) ?? 0;
        if (currentZoneId == 0) currentZoneId = _net.Player?.Zone ?? 0;
        uint viewedAreaId = _worldMapSelectedAreaId != 0
            ? _worldMapSelectedAreaId : currentZoneId;
        WorldMapAreaInfo area = default;
        bool haveArea = _worldMapZoom > 0 &&
                        _worldMapAreas?.TryGetArea(viewedAreaId, out area) == true;
        bool haveMapArea = haveArea;
        if (!haveArea)
        {
            uint displayMap = playerMap;
            if (_worldMapAreas?.TryGetArea(currentZoneId,
                    out WorldMapAreaInfo currentZoneArea) == true)
                displayMap = currentZoneArea.MapId;
            haveMapArea = _worldMapAreas?.TryGetContinent(displayMap, out area) == true ||
                          _worldMapAreas?.TryGetContinent(playerMap, out area) == true ||
                          _worldMapAreas?.TryGetContinent(0, out area) == true;
        }
        string directory = !haveMapArea || string.IsNullOrWhiteSpace(area.Directory)
            ? (playerMap == 1 ? "Kalimdor" : "Azeroth") : area.Directory;
        Vector2 mapMin = origin + new Vector2(11, 69) * s;
        Vector2 mapSize = new Vector2(1002, 668) * s;
        dl.PushClipRect(mapMin, mapMin + mapSize, true);
        for (int row = 0; row < 3; row++)
        for (int col = 0; col < 4; col++)
        {
            int index = row * 4 + col + 1;
            uint texture = _gameplayArt.Handle(
                $@"Interface\WorldMap\{directory}\{directory}{index}.blp");
            if (texture == 0) continue;
            Vector2 min = mapMin + new Vector2(col * 256, row * 256) * s;
            dl.AddImage((nint)texture, min, min + new Vector2(256) * s);
        }
        dl.PopClipRect();

        if (haveArea)
            DrawWorldMapExploredOverlays(dl, area, player.Fields, mapMin, mapSize, s);

        ImGui.SetCursorScreenPos(mapMin);
        ImGui.InvisibleButton("##world-map-detail", mapSize);
        bool mapHovered = ImGui.IsItemHovered();
        WorldMapAreaInfo hoveredArea = default;
        bool haveHoveredArea = false;
        if (haveMapArea && !haveArea && mapHovered &&
            _worldMapHits?.TryResolveArea(area.MapId, area,
                CommanderMapUiLaw.Unproject(mapMin, mapSize, ImGui.GetMousePos()),
                out uint hoverAreaId) == true &&
            _worldMapAreas?.TryGetArea(hoverAreaId, out hoveredArea) == true)
        {
            haveHoveredArea = true;
            DrawWorldMapAreaHighlight(dl, hoveredArea, mapMin, mapSize);
            if (ImGui.IsItemClicked())
            {
                _worldMapSelectedAreaId = hoveredArea.AreaId;
                _worldMapZoom = 1;
            }
        }
        else if (haveArea && mapHovered && ImGui.IsItemClicked(ImGuiMouseButton.Right))
            _worldMapZoom = 0;

        Vector3 playerPosition = player.Position;
        if (haveMapArea && !string.IsNullOrWhiteSpace(area.Directory) && area.MapId == playerMap)
        {
            float fx = (playerPosition.Y - area.Left) / (area.Right - area.Left);
            float fy = (playerPosition.X - area.Top) / (area.Bottom - area.Top);
            if (fx is >= 0f and <= 1f && fy is >= 0f and <= 1f)
            {
                Vector2 marker = mapMin + new Vector2(fx * mapSize.X, fy * mapSize.Y);
                DrawMinimapPlayerArrow(dl, player.Orientation, marker, s);
            }
        }

        string viewLabel = haveArea
            ? (_areas?.ZoneName(viewedAreaId) ?? directory)
            : haveMapArea ? (area.MapId == 1 ? "Kalimdor" : "Eastern Kingdoms")
                : "Map unavailable";
        DrawCenteredText(dl, origin + new Vector2(512, 17) * s,
            "World Map", 14 * s, VanillaGold);
        DrawCenteredText(dl, origin + new Vector2(512, 48) * s,
            viewLabel, 12 * s, 0xffffffff);
        if (haveHoveredArea)
        {
            string hoverLabel = _areas?.ZoneName(hoveredArea.AreaId) ?? hoveredArea.Directory;
            DrawWorldMapOutlinedCenteredText(dl,
                mapMin + new Vector2(mapSize.X * 0.5f + 20f * s, 20f * s),
                hoverLabel, 16f * s, 0xfff2e8d4);
        }
        if (haveArea && VanillaButton(dl, "##world-map-zoomout", "Zoom Out",
                origin + new Vector2(680, 34) * s, new Vector2(110, 22), s))
            _worldMapZoom = 0;
        else if (!haveArea && VanillaButton(dl, "##world-map-zone", "Current Zone",
                     origin + new Vector2(680, 34) * s, new Vector2(110, 22), s))
        {
            _worldMapSelectedAreaId = 0;
            _worldMapZoom = 1;
        }
        Vector2 close = origin + new Vector2(982,4) * s;
        DrawImageButton(dl, "##world-map-close", close, new Vector2(32) * s,
            @"Interface\Buttons\UI-Panel-MinimizeButton-Up", @"Interface\Buttons\UI-Panel-MinimizeButton-Down",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Highlight");
        if (ImGui.IsItemClicked()) _worldMapOpen = false;
        ImGui.End();
    }

    private void DrawWorldMapAreaHighlight(ImDrawListPtr dl, WorldMapAreaInfo area,
        Vector2 mapMin, Vector2 mapSize)
    {
        if (_worldMapHighlights?.TryGetArea(area.AreaId,
                out WorldMapHighlightInfo highlight) != true) return;
        uint texture = _gameplayArt!.AdditiveHandle(highlight.TexturePath);
        if (texture == 0) return;
        Vector2 min = mapMin + new Vector2(
            highlight.Bounds.Left * mapSize.X, highlight.Bounds.Top * mapSize.Y);
        Vector2 max = mapMin + new Vector2(
            highlight.Bounds.Right * mapSize.X, highlight.Bounds.Bottom * mapSize.Y);
        dl.PushClipRect(mapMin, mapMin + mapSize, true);
        dl.AddImage((nint)texture, Vector2.Min(min, max), Vector2.Max(min, max),
            Vector2.Zero, new Vector2(highlight.UMax, highlight.VMax), 0xffffffff);
        dl.PopClipRect();
    }

    private void DrawWorldMapExploredOverlays(ImDrawListPtr dl, WorldMapAreaInfo area,
        ObjectFields fields, Vector2 mapMin, Vector2 mapSize, float s)
    {
        if (_worldMapOverlays is null || _areas is null) return;
        dl.PushClipRect(mapMin, mapMin + mapSize, true);
        foreach (WorldMapOverlayInfo overlay in _worldMapOverlays.ForMapArea(area.Id))
        {
            if (!overlay.IsExplored(areaId =>
                    _areas.ExplorationFlag(areaId) is uint flag &&
                    fields.PlayerHasExplored(flag))) continue;
            foreach (WorldMapOverlayChunk chunk in overlay.BuildChunks(area.Directory))
            {
                uint texture = _gameplayArt!.Handle(chunk.TexturePath);
                if (texture == 0) continue;
                Vector2 min = mapMin + new Vector2(chunk.OffsetX, chunk.OffsetY) * s;
                Vector2 max = min + new Vector2(chunk.PixelWidth, chunk.PixelHeight) * s;
                dl.AddImage((nint)texture, min, max, Vector2.Zero,
                    new Vector2(chunk.UMax, chunk.VMax), 0xffffffff);
            }
        }
        dl.PopClipRect();
    }

    private void EnsureWorldMapAreas()
    {
        if(_worldMapAreasLoaded)return;_worldMapAreasLoaded=true;
        try{if(_mpq is not null)_worldMapAreas=WorldMapAreaCatalog.Load(_mpq);}
        catch(Exception e){Console.WriteLine($"[world-map] WorldMapArea load failed: {e.Message}");}
    }
}

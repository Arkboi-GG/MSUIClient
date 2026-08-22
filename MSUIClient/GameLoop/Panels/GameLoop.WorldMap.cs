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
    private uint? _worldMapSelectedMapId;
    private bool _worldMapContinentMenuOpen;
    private bool _worldMapZoneMenuOpen;
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
        _worldMapSelectedMapId = null;
        _worldMapContinentMenuOpen = false;
        _worldMapZoneMenuOpen = false;
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
            uint displayMap = _worldMapSelectedMapId ?? playerMap;
            if (_worldMapSelectedMapId is null && _worldMapAreas?.TryGetArea(currentZoneId,
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
        WorldMapAreaInfo[] continentRows = WorldMapContinents();
        WorldMapAreaInfo[] zoneRows = WorldMapZones(area.MapId);
        WorldMapDropdownGeometry continentDrop = WorldMapUiLaw.Continent(
            origin, s, continentRows.Length);
        WorldMapDropdownGeometry zoneDrop = WorldMapUiLaw.Zone(origin, s, zoneRows.Length);
        bool dropdownOwnsPointer = continentDrop.Contains(ImGui.GetMousePos(),
                                       _worldMapContinentMenuOpen) ||
                                   zoneDrop.Contains(ImGui.GetMousePos(),
                                       _worldMapZoneMenuOpen);
        bool mapHovered = ImGui.IsItemHovered() && !dropdownOwnsPointer;
        if (mapHovered && (ImGui.IsMouseClicked(ImGuiMouseButton.Left) ||
                           ImGui.IsMouseClicked(ImGuiMouseButton.Right)))
            CloseWorldMapDropdowns();
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
                _worldMapSelectedMapId = hoveredArea.MapId;
                _worldMapZoom = 1;
                CloseWorldMapDropdowns();
            }
        }
        else if (haveArea && mapHovered && ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            _worldMapZoom = 0;
            _worldMapSelectedMapId = area.MapId;
            CloseWorldMapDropdowns();
        }

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
        DrawWorldMapCorpseMarker(dl, haveMapArea, area, mapMin, mapSize, s);

        string viewLabel = haveArea
            ? (_areas?.ZoneName(viewedAreaId) ?? directory)
            : haveMapArea ? (area.MapId == 1 ? "Kalimdor" : "Eastern Kingdoms")
                : "Map unavailable";
        DrawCenteredText(dl, origin + new Vector2(512, 17) * s,
            "World Map", 14 * s, VanillaGold);
        DrawCenteredText(dl, origin + new Vector2(512, 48) * s,
            viewLabel, 12 * s, 0xffffffff);
        DrawWorldMapDropdowns(dl, origin, s, area.MapId, viewedAreaId,
            continentRows, zoneRows, continentDrop, zoneDrop);
        if (haveHoveredArea)
        {
            string hoverLabel = _areas?.ZoneName(hoveredArea.AreaId) ?? hoveredArea.Directory;
            DrawWorldMapOutlinedCenteredText(dl,
                mapMin + new Vector2(mapSize.X * 0.5f + 20f * s, 20f * s),
                hoverLabel, 16f * s, 0xfff2e8d4);
        }
        if (haveArea && VanillaButton(dl, "##world-map-zoomout", "Zoom Out",
                origin + new Vector2(680, 34) * s, new Vector2(110, 22), s))
        {
            _worldMapZoom = 0;
            _worldMapSelectedMapId = area.MapId;
            CloseWorldMapDropdowns();
        }
        else if (!haveArea && VanillaButton(dl, "##world-map-zone", "Current Zone",
                     origin + new Vector2(680, 34) * s, new Vector2(110, 22), s))
        {
            _worldMapSelectedAreaId = 0;
            _worldMapSelectedMapId = null;
            _worldMapZoom = 1;
            CloseWorldMapDropdowns();
        }
        Vector2 close = origin + new Vector2(982,4) * s;
        DrawImageButton(dl, "##world-map-close", close, new Vector2(32) * s,
            @"Interface\Buttons\UI-Panel-MinimizeButton-Up", @"Interface\Buttons\UI-Panel-MinimizeButton-Down",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Highlight");
        if (ImGui.IsItemClicked())
        {
            _worldMapOpen = false;
            CloseWorldMapDropdowns();
        }
        ImGui.End();
    }

    private WorldMapAreaInfo[] WorldMapContinents() => _worldMapAreas?.Continents
        .Where(static continent => continent.MapId is 0 or 1)
        .ToArray() ?? [];

    private WorldMapAreaInfo[] WorldMapZones(uint mapId) => _worldMapAreas?.Areas
        .Where(zone => zone.MapId == mapId)
        .OrderBy(zone => _areas?.ZoneName(zone.AreaId) ?? zone.Directory,
            StringComparer.OrdinalIgnoreCase)
        .ToArray() ?? [];

    private static string WorldMapContinentName(uint mapId) => mapId switch
    {
        0 => "Eastern Kingdoms",
        1 => "Kalimdor",
        _ => "Continent",
    };

    private void CloseWorldMapDropdowns()
    {
        _worldMapContinentMenuOpen = false;
        _worldMapZoneMenuOpen = false;
    }

    private void DrawWorldMapDropdowns(ImDrawListPtr draw, Vector2 origin, float scale,
        uint viewedMapId, uint viewedAreaId, WorldMapAreaInfo[] continents,
        WorldMapAreaInfo[] zones, in WorldMapDropdownGeometry continentGeometry,
        in WorldMapDropdownGeometry zoneGeometry)
    {
        DrawWorldMapDropdownCapsule(draw, "continent", "Continent",
            WorldMapContinentName(viewedMapId), continentGeometry, scale,
            ref _worldMapContinentMenuOpen, ref _worldMapZoneMenuOpen);
        DrawWorldMapDropdownCapsule(draw, "zone", "Zone",
            _worldMapZoom > 0 ? _areas?.ZoneName(viewedAreaId) ?? "" : "",
            zoneGeometry, scale, ref _worldMapZoneMenuOpen,
            ref _worldMapContinentMenuOpen);

        if (_worldMapContinentMenuOpen)
            DrawWorldMapDropdownList(draw, "continent", continentGeometry, scale,
                continents.Select(row => (WorldMapContinentName(row.MapId),
                    row.MapId == viewedMapId, (Action)(() =>
                    {
                        _worldMapSelectedMapId = row.MapId;
                        _worldMapSelectedAreaId = 0;
                        _worldMapZoom = 0;
                        CloseWorldMapDropdowns();
                    }))).ToArray());
        if (_worldMapZoneMenuOpen)
            DrawWorldMapDropdownList(draw, "zone", zoneGeometry, scale,
                zones.Select(row => (_areas?.ZoneName(row.AreaId) ?? row.Directory,
                    row.AreaId == viewedAreaId && _worldMapZoom > 0, (Action)(() =>
                    {
                        _worldMapSelectedMapId = row.MapId;
                        _worldMapSelectedAreaId = row.AreaId;
                        _worldMapZoom = 1;
                        CloseWorldMapDropdowns();
                    }))).ToArray());
    }

    private void DrawWorldMapDropdownCapsule(ImDrawListPtr draw, string id, string label,
        string selected, in WorldMapDropdownGeometry geometry, float scale,
        ref bool open, ref bool otherOpen)
    {
        Vector2 artMin = geometry.FrameMin + new Vector2(0f, -17f) * scale;
        uint art = _gameplayArt?.Handle(WorldMapUiLaw.CapsuleTexture) ?? 0;
        if (art != 0)
        {
            draw.AddImage((nint)art, artMin, artMin + new Vector2(25, 64) * scale,
                new Vector2(0, 0), new Vector2(.1953125f, 1));
            draw.AddImage((nint)art, artMin + new Vector2(25, 0) * scale,
                artMin + new Vector2(155, 64) * scale,
                new Vector2(.1953125f, 0), new Vector2(.8046875f, 1));
            draw.AddImage((nint)art, artMin + new Vector2(155, 0) * scale,
                artMin + new Vector2(180, 64) * scale,
                new Vector2(.8046875f, 0), Vector2.One);
        }
        GameText.Draw(draw, "GameFontNormalSmall", label,
            geometry.FrameMin + new Vector2(20, -10) * scale, scale);
        GameText.DrawRightAligned(draw, "GameFontHighlightSmall", selected,
            geometry.FrameMin + new Vector2(137, 11) * scale, scale);

        Vector2 buttonMin = geometry.FrameMin + new Vector2(140, 1) * scale;
        uint button = _gameplayArt?.Handle(open
            ? @"Interface\ChatFrame\UI-ChatIcon-ScrollDown-Down"
            : @"Interface\ChatFrame\UI-ChatIcon-ScrollDown-Up") ?? 0;
        if (button != 0)
            draw.AddImage((nint)button, buttonMin, buttonMin + new Vector2(24) * scale);
        ImGui.SetCursorScreenPos(buttonMin);
        if (ImGui.InvisibleButton($"##world-map-{id}-dropdown", new Vector2(24) * scale))
        {
            open = !open;
            otherOpen = false;
            PlayUiSound("igMainMenuOptionCheckBoxOn", "ui.world-map");
        }
    }

    private void DrawWorldMapDropdownList(ImDrawListPtr draw, string id,
        in WorldMapDropdownGeometry geometry, float scale,
        (string Text, bool Checked, Action Select)[] rows)
    {
        if (_skin is null || rows.Length == 0) return;
        _skin.DrawBackdrop(draw, geometry.ListMin, geometry.ListMin + geometry.ListSize,
            WowSkin.Dialog);
        int count = Math.Min(rows.Length, WorldMapUiLaw.MaximumRows);
        for (int i = 0; i < count; i++)
        {
            Vector2 rowMin = WorldMapUiLaw.RowMin(geometry, i, scale);
            Vector2 rowSize = new Vector2(
                WorldMapUiLaw.DropWidth, WorldMapUiLaw.RowHeight) * scale;
            ImGui.SetCursorScreenPos(rowMin);
            bool clicked = ImGui.InvisibleButton($"##world-map-{id}-row-{i}", rowSize);
            if (ImGui.IsItemHovered())
            {
                uint highlight = _gameplayArt!.AdditiveHandle(
                    @"Interface\QuestFrame\UI-QuestTitleHighlight");
                if (highlight != 0) draw.AddImage((nint)highlight, rowMin, rowMin + rowSize);
            }
            if (rows[i].Checked)
            {
                uint check = _gameplayArt!.Handle(@"Interface\Buttons\UI-CheckBox-Check");
                if (check != 0)
                {
                    Vector2 checkMin = rowMin + new Vector2(0, -4) * scale;
                    draw.AddImage((nint)check, checkMin, checkMin + new Vector2(24) * scale);
                }
            }
            GameText.Draw(draw, "GameFontHighlightSmall", rows[i].Text,
                rowMin + new Vector2(27, 2) * scale, scale);
            if (clicked)
            {
                rows[i].Select();
                PlayUiSound("igMainMenuOptionCheckBoxOn", "ui.world-map");
            }
        }
    }

    private void DrawWorldMapCorpseMarker(ImDrawListPtr draw, bool haveMapArea,
        WorldMapAreaInfo area, Vector2 mapMin, Vector2 mapSize, float scale)
    {
        if (!haveMapArea || _corpseLocation is not { } corpse ||
            !DeathFrameUiLaw.TryWorldMapFraction(corpse.DisplayMap, area.MapId,
                corpse.Position, area.Left, area.Right, area.Top, area.Bottom,
                out Vector2 fraction)) return;
        uint texture = _gameplayArt?.Handle(DeathFrameUiLaw.CorpseMarkerPath) ?? 0;
        Vector2 size = new(DeathFrameUiLaw.WorldMapCorpseSize * scale);
        Vector2 center = mapMin + fraction * mapSize;
        Vector2 min = center - size * .5f;
        if (texture != 0)
            draw.AddImage((nint)texture, min, min + size,
                DeathFrameUiLaw.CorpseUvMin, DeathFrameUiLaw.CorpseUvMax);
        if (ImGui.IsMouseHoveringRect(min, min + size, false))
            ImGui.SetTooltip(DeathFrameUiLaw.CorpseTooltip);
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

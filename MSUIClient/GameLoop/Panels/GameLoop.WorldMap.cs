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

        // The dedicated Commander Map command (RTS Controls) ships UNBOUND: the free-view
        // overload above is how that map has always opened, and this is simply the seat for a
        // player who would rather not overload their World Map key.
        if (BindingPressedEdge(GameBinding.RtsCommanderMap, typing) &&
            _freeView && _net is { IsInWorld: true })
            ToggleCommanderMap();
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
        WorldMapUiLaw.FrameLayout frame = WorldMapUiLaw.Frame(display);
        if (!BeginVanillaWindow("##world-map", frame.LogicalOrigin, frame.LogicalSize,
                out ImDrawListPtr dl, out Vector2 origin, out float s, frame.Scale))
        { ImGui.End(); return; }
        // FULLSCREEN_DIALOG strata: submit on the foreground list so both the
        // letterbox and authored map cover every ordinary or developer window.
        dl = ImGui.GetForegroundDrawList();
        dl.AddRectFilled(Vector2.Zero, display, 0xff000000);
        string[] shellRows = ["Top", "Middle", "Bottom"];
        for (int row = 0; row < 3; row++)
        for (int col = 0; col < 4; col++)
        {
            WorldMapUiLaw.LogicalRect tile = WorldMapUiLaw.ShellTile(row, col);
            DrawArt(dl, $@"Interface\WorldMap\UI-WorldMap-{shellRows[row]}{col + 1}",
                tile.ScaledMin(origin, s), tile.Size, s);
        }

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
        Vector2 mapMin = WorldMapUiLaw.MapRect.ScaledMin(origin, s);
        Vector2 mapSize = WorldMapUiLaw.MapRect.ScaledSize(s);
        dl.PushClipRect(mapMin, mapMin + mapSize, true);
        for (int row = 0; row < 3; row++)
        for (int col = 0; col < 4; col++)
        {
            int index = row * 4 + col + 1;
            uint texture = _gameplayArt.Handle(
                $@"Interface\WorldMap\{directory}\{directory}{index}.blp");
            if (texture == 0) continue;
            WorldMapUiLaw.LogicalRect tile = WorldMapUiLaw.DetailTile(row, col);
            Vector2 min = tile.ScaledMin(mapMin, s);
            dl.AddImage((nint)texture, min, min + tile.ScaledSize(s));
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
                Vector2 marker = WorldMapUiLaw.MapPoint(mapMin, mapSize, fx, fy);
                DrawMinimapPlayerArrow(dl, player.Orientation, marker, s);
            }
        }
        DrawWorldMapGossipPoi(dl, haveMapArea, area, mapMin, mapSize, s);
        DrawWorldMapCorpseMarker(dl, haveMapArea, area, mapMin, mapSize, s);

        GameText.DrawCentered(dl, WorldMapUiLaw.TitleFont, "World Map",
            WorldMapUiLaw.At(origin, WorldMapUiLaw.TitleCenter, s), s);
        DrawWorldMapDropdowns(dl, origin, s, area.MapId, viewedAreaId,
            continentRows, zoneRows, continentDrop, zoneDrop);
        if (haveHoveredArea)
        {
            string hoverLabel = _areas?.ZoneName(hoveredArea.AreaId) ?? hoveredArea.Directory;
            GameText.DrawCentered(dl, WorldMapUiLaw.HoverLabelFont, hoverLabel,
                WorldMapUiLaw.HoverLabel(mapMin, mapSize, s), s);
        }
        if (haveArea && VanillaButton(dl, "##world-map-zoomout", "Zoom Out",
                WorldMapUiLaw.ViewAction.ScaledMin(origin, s),
                WorldMapUiLaw.ViewAction.Size, s))
        {
            _worldMapZoom = 0;
            _worldMapSelectedMapId = area.MapId;
            CloseWorldMapDropdowns();
        }
        else if (!haveArea && VanillaButton(dl, "##world-map-zone", "Current Zone",
                     WorldMapUiLaw.ViewAction.ScaledMin(origin, s),
                     WorldMapUiLaw.ViewAction.Size, s))
        {
            _worldMapSelectedAreaId = 0;
            _worldMapSelectedMapId = null;
            _worldMapZoom = 1;
            CloseWorldMapDropdowns();
        }
        Vector2 close = WorldMapUiLaw.Close.ScaledMin(origin, s);
        DrawImageButton(dl, "##world-map-close", close, WorldMapUiLaw.Close.ScaledSize(s),
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
        bool toggled = VanillaDropdownCapsule(draw, $"##world-map-{id}-dropdown",
            geometry.FrameMin, scale, WorldMapUiLaw.Capsule, selected);
        GameText.Draw(draw, "GameFontNormalSmall", label,
            WorldMapUiLaw.At(geometry.FrameMin, WorldMapUiLaw.CapsuleLabel, scale), scale);
        if (toggled)
        {
            open = !open;
            otherOpen = false;
            PlayUiSound(DropdownCapsuleUiLaw.ToggleSound, "ui.world-map");
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
            Vector2 rowSize = WorldMapUiLaw.DropdownRow.ScaledSize(scale);
            ImGui.SetCursorScreenPos(rowMin);
            bool clicked = ImGui.InvisibleButton($"##world-map-{id}-row-{i}", rowSize);
            if (rows[i].Checked || ImGui.IsItemHovered())
            {
                uint highlight = _gameplayArt!.AdditiveHandle(
                    DropdownCapsuleUiLaw.RowHighlight);
                if (highlight != 0) draw.AddImage((nint)highlight, rowMin, rowMin + rowSize);
            }
            if (rows[i].Checked)
            {
                uint check = _gameplayArt!.Handle(DropdownCapsuleUiLaw.RowCheck);
                if (check != 0)
                {
                    Vector2 checkMin = rowMin + DropdownCapsuleUiLaw.Check.Min * scale;
                    draw.AddImage((nint)check, checkMin,
                        checkMin + DropdownCapsuleUiLaw.Check.Size * scale);
                }
            }
            GameText.Draw(draw, DropdownCapsuleUiLaw.SelectionFont, rows[i].Text,
                rowMin + DropdownCapsuleUiLaw.RowTextOffset * scale, scale);
            if (clicked)
            {
                rows[i].Select();
                PlayUiSound(DropdownCapsuleUiLaw.RowSound, "ui.world-map");
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
        Vector2 size = Vector2.One * (DeathFrameUiLaw.WorldMapCorpseSize * scale);
        Vector2 center = mapMin + fraction * mapSize;
        Vector2 min = center - size * .5f;
        if (texture != 0)
            draw.AddImage((nint)texture, min, min + size,
                DeathFrameUiLaw.CorpseUvMin, DeathFrameUiLaw.CorpseUvMax);
        if (ImGui.IsMouseHoveringRect(min, min + size, false))
        {
            WorldMapUiLaw.TooltipSeat tooltipSeat =
                WorldMapUiLaw.CorpseTooltipSeat(min, size, mapMin, mapSize);
            OfferOwnerAnchoredSharedGameTooltip(new("world-map-corpse", 0),
                [new(DeathFrameUiLaw.CorpseTooltip, GameTooltipTextTone.Red)],
                tooltipSeat.Anchor, tooltipSeat.Pivot);
        }
    }

    private void DrawWorldMapGossipPoi(ImDrawListPtr draw, bool haveMapArea,
        WorldMapAreaInfo area, Vector2 mapMin, Vector2 mapSize, float scale)
    {
        if (!haveMapArea || _gossipPoi is not { } poi) return;
        Vector3 poiPosition = new(poi.Position.X, poi.Position.Y, 0f);
        if (!DeathFrameUiLaw.TryWorldMapFraction((int)_gossipPoiMapId, area.MapId,
                poiPosition, area.Left, area.Right, area.Top, area.Bottom,
                out Vector2 fraction)) return;

        Vector2 size = Vector2.One * (WorldMapUiLaw.PoiIconSize * scale);
        Vector2 center = WorldMapUiLaw.MapPoint(mapMin, mapSize, fraction.X, fraction.Y);
        Vector2 min = center - size * .5f;
        uint texture = _gameplayArt?.Handle(@"Interface\Minimap\POIIcons") ?? 0;
        if (texture != 0 && AreaPoiCatalog.TryIconUv(
                poi.Icon, out Vector2 uvMin, out Vector2 uvMax))
            draw.AddImage((nint)texture, min, min + size, uvMin, uvMax);
        else
            draw.AddCircleFilled(center, MathF.Max(3f, 4f * scale), 0xff2020ff);

        if (ImGui.IsMouseHoveringRect(min, min + size, false))
        {
            WorldMapUiLaw.TooltipSeat tooltipSeat =
                WorldMapUiLaw.CorpseTooltipSeat(min, size, mapMin, mapSize);
            OfferOwnerAnchoredSharedGameTooltip(new("world-map-gossip-poi", poi.Data),
                [new(poi.Name, GameTooltipTextTone.White)],
                tooltipSeat.Anchor, tooltipSeat.Pivot);
        }
    }

    private void DrawWorldMapAreaHighlight(ImDrawListPtr dl, WorldMapAreaInfo area,
        Vector2 mapMin, Vector2 mapSize)
    {
        if (_worldMapHighlights?.TryGetArea(area.AreaId,
                out WorldMapHighlightInfo highlight) != true) return;
        uint texture = _gameplayArt!.AdditiveHandle(highlight.TexturePath);
        if (texture == 0) return;
        Vector2 min = WorldMapUiLaw.MapPoint(mapMin, mapSize,
            highlight.Bounds.Left, highlight.Bounds.Top);
        Vector2 max = WorldMapUiLaw.MapPoint(mapMin, mapSize,
            highlight.Bounds.Right, highlight.Bounds.Bottom);
        dl.PushClipRect(mapMin, mapMin + mapSize, true);
        dl.AddImage((nint)texture, Vector2.Min(min, max), Vector2.Max(min, max),
            Vector2.Zero, WorldMapUiLaw.UvMaximum(highlight.UMax, highlight.VMax), 0xffffffff);
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
                WorldMapUiLaw.ScreenRect chunkRect = WorldMapUiLaw.PixelRect(mapMin,
                    chunk.OffsetX, chunk.OffsetY, chunk.PixelWidth, chunk.PixelHeight, s);
                Vector2 min = chunkRect.Min;
                Vector2 max = min + chunkRect.Size;
                dl.AddImage((nint)texture, min, max, Vector2.Zero,
                    WorldMapUiLaw.UvMaximum(chunk.UMax, chunk.VMax), 0xffffffff);
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

using System.Numerics;
using System.Text;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private Dictionary<string, string>? _minimapTileMap;
    private int _minimapZoom = 2;
    private bool _minimapVisible = true;
    private uint _minimapAreaId;
    private uint _minimapReportedZoneId;
    private string _minimapAreaMap = "";
    private string _minimapResourceSignature = "";
    private MinimapResourceTooltipRuntime? _minimapResourceTooltip;

    private readonly record struct MinimapResourceTooltipCandidate(ulong Guid, string Name);
    private readonly record struct MinimapResourceTooltipRuntime(
        GameTooltipOwnerToken Token,
        string Name);

    private void DrawMinimap()
    {
        if ((_net is null && !HudPreview) || _gameplayArt is null ||
            !_entities.TryGet(ControlledGuid, out WorldEntity player))
        {
            UpdateAndQueueMinimapResourceTooltip(null);
            return;
        }

        float s = GameplayUiScale();
        Vector2 logicalDisplay = ImGui.GetIO().DisplaySize / s;
        Vector2 root = new(logicalDisplay.X - 192f, 0f);
        Vector2 rootPx = root * s;
        ImDrawListPtr dl = ImGui.GetBackgroundDrawList();

        // ToggleMinimap hides the map body, not its toggle. Keeping this control alive is
        // what lets the stock 1.12 minimap be reopened after it has been collapsed.
        if (!_minimapVisible)
        {
            DrawMinimapTexture(dl, root, Vector2.Zero, new(192, 32),
                @"Interface\Minimap\UI-Minimap-Border", new(.25f, 0), new(1, .125f));
            DrawMinimapButton(dl, root + new Vector2(161, -3),
                @"Interface\Buttons\UI-Panel-MinimizeButton-Up", () => _minimapVisible = true);
            UpdateAndQueueMinimapResourceTooltip(null);
            return;
        }

        if (_uiParityArmed && _uiParityPanel == "minimap")
        {
            BeginUiParityFrame(rootPx, s);
            CollectUiParity("MinimapCluster", "Frame", rootPx, new Vector2(192) * s,
                parent: "", point: "TOPRIGHT", strata: "BACKGROUND");
        }

        // Painterly mode presents the map SQUARE. Vanilla's roundness is not a
        // clip - DrawMovingMinimap already crops to a rectangle - it is entirely
        // the UI-Minimap-Border art painted over the corners. So the square
        // variant simply declines to draw that art and frames the same rect,
        // which also lets the body grow into the corners the circle threw away.
        bool squareMap = PainterlyUi;
        Vector2 mapMin = (root + (squareMap ? new Vector2(12, 26) : new Vector2(35, 22))) * s;
        Vector2 mapMax = mapMin + new Vector2(squareMap ? 168 : 140) * s;
        // Local movement is client-authoritative in 1.12. The controller is the
        // continuously updated truth; the object-store entity is only the last
        // server snapshot and can remain unchanged for seconds.
        Vector3 playerPosition = _controller?.Position ?? player.Position;
        float playerOrientation = _controller?.Yaw ?? player.Orientation;
        MinimapProjection projection = MinimapProjection.FromWorld(playerPosition);
        DrawMovingMinimap(dl, mapMin, mapMax, projection);
        UpdateMinimapArea(projection);

        if (ImGui.IsMouseHoveringRect(mapMin, mapMax, false))
        {
            float wheel = ImGui.GetIO().MouseWheel;
            if (wheel > 0) _minimapZoom = Math.Max(0, _minimapZoom - 1);
            else if (wheel < 0) _minimapZoom = Math.Min(5, _minimapZoom + 1);
        }

        DrawMinimapPartyDots(dl, player, playerPosition, mapMin, mapMax, s);
        DrawMinimapPlayerArrow(dl, playerOrientation, (mapMin + mapMax) * .5f, s);
        MinimapResourceTooltipCandidate? resourceTooltip =
            DrawMinimapResourceDots(dl, player, playerPosition, mapMin, mapMax, s);
        UpdateAndQueueMinimapResourceTooltip(resourceTooltip);
        if (_uiParityArmed && _uiParityPanel == "minimap")
            CollectUiParity("Minimap", "Minimap", mapMin, new Vector2(140) * s,
                parent: "MinimapCluster", point: "CENTER", relativePoint: "TOP",
                offsetX: "9", offsetY: "-92", strata: "BACKGROUND");

        if (squareMap) DrawSquareMinimapFrame(dl, mapMin, mapMax, s);
        else DrawMinimapTexture(dl, root, new(0, 20), new(192),
            @"Interface\Minimap\UI-Minimap-Border", new(.25f, .125f), new(1f, .875f));
        if (!squareMap && _uiParityArmed && _uiParityPanel == "minimap")
        {
            CollectUiParity("MinimapBackdrop", "Frame", (root + new Vector2(0, 20)) * s,
                new Vector2(192) * s, parent: "Minimap", point: "CENTER",
                relativeTo: "MinimapCluster", offsetX: "0", offsetY: "-20", strata: "BACKGROUND");
            CollectUiParity("MinimapBorder", "Texture", (root + new Vector2(0, 20)) * s,
                new Vector2(192) * s, parent: "MinimapBackdrop",
                texture: @"Interface\Minimap\UI-Minimap-Border", layer: "ARTWORK",
                strata: "BACKGROUND", texCoords: "0.25|0.125|1.0|0.875");
        }

        // The stock zoom buttons sit on the CIRCLE's lower-right arc, which is
        // inside the square body once the corners come back. Tuck them under
        // the frame instead.
        Vector2 zoomIn = squareMap ? new Vector2(120, 196) : new Vector2(157, 113);
        Vector2 zoomOut = squareMap ? new Vector2(150, 196) : new Vector2(131, 141);
        DrawMinimapButton(dl, root + zoomIn, @"Interface\Minimap\UI-Minimap-ZoomInButton-Up",
            () => _minimapZoom = Math.Max(0, _minimapZoom - 1));
        DrawMinimapButton(dl, root + zoomOut, @"Interface\Minimap\UI-Minimap-ZoomOutButton-Up",
            () => _minimapZoom = Math.Min(5, _minimapZoom + 1));
        if (!squareMap && _uiParityArmed && _uiParityPanel == "minimap")
        {
            CollectUiParity("MinimapZoomIn", "Button", (root + new Vector2(157, 113)) * s,
                new Vector2(32) * s, parent: "MinimapBackdrop", point: "CENTER",
                offsetX: "77", offsetY: "-13", texture: @"Interface\Minimap\UI-Minimap-ZoomInButton-Up",
                strata: "BACKGROUND");
            CollectUiParity("MinimapZoomIn/NormalTexture", "NormalTexture", (root + new Vector2(157, 113)) * s,
                new Vector2(32) * s, parent: "MinimapZoomIn",
                texture: @"Interface\Minimap\UI-Minimap-ZoomInButton-Up", strata: "BACKGROUND");
            CollectUiParity("MinimapZoomOut", "Button", (root + new Vector2(131, 141)) * s,
                new Vector2(32) * s, parent: "MinimapBackdrop", point: "CENTER",
                offsetX: "51", offsetY: "-41", texture: @"Interface\Minimap\UI-Minimap-ZoomOutButton-Up",
                strata: "BACKGROUND");
            CollectUiParity("MinimapZoomOut/NormalTexture", "NormalTexture", (root + new Vector2(131, 141)) * s,
                new Vector2(32) * s, parent: "MinimapZoomOut",
                texture: @"Interface\Minimap\UI-Minimap-ZoomOutButton-Up", strata: "BACKGROUND");
        }

        // Square mode draws its own title bar (in DrawSquareMinimapFrame); the
        // stock top piece is curved art that reads as a mistake on a rectangle.
        if (!squareMap)
            DrawMinimapTexture(dl, root, Vector2.Zero, new(192, 32),
                @"Interface\Minimap\UI-Minimap-Border", new(.25f, 0), new(1, .125f));
        if (!squareMap && _uiParityArmed && _uiParityPanel == "minimap")
            CollectUiParity("MinimapBorderTop", "Texture", rootPx, new Vector2(192, 32) * s,
                parent: "MinimapCluster", point: "TOPRIGHT",
                texture: @"Interface\Minimap\UI-Minimap-Border", layer: "ARTWORK",
                strata: "BACKGROUND", texCoords: "0.25|0.0|1.0|0.125");

        DrawMinimapZoneText(dl, root, s);
        DrawMinimapTracking(dl, root, player, s);
        DrawMinimapMail(dl, root, s);

        Vector2 toggleMin = (root + new Vector2(161, -3)) * s;
        DrawMinimapButton(dl, root + new Vector2(161, -3),
            @"Interface\Buttons\UI-Panel-MinimizeButton-Up", () => _minimapVisible = false);
        if (_uiParityArmed && _uiParityPanel == "minimap")
        {
            CollectUiParity("MinimapToggleButton", "Button", toggleMin, new Vector2(32) * s,
                parent: "MinimapCluster", point: "CENTER", relativePoint: "TOPRIGHT",
                offsetX: "-15", offsetY: "-13", texture: @"Interface\Buttons\UI-Panel-MinimizeButton-Up",
                strata: "BACKGROUND");
            CollectUiParity("MinimapToggleButton/NormalTexture", "NormalTexture", toggleMin,
                new Vector2(32) * s, parent: "MinimapToggleButton",
                texture: @"Interface\Buttons\UI-Panel-MinimizeButton-Up", strata: "BACKGROUND");
            MarkUiParityFrameComplete();
        }
    }

    /// <summary>
    /// The square map frame: a painted border with a title strip above it,
    /// drawn from primitives rather than art so it needs no new BLPs. Layered
    /// dark-to-light outward, which is how the reference's stone frames read.
    /// </summary>
    private void DrawSquareMinimapFrame(ImDrawListPtr dl, Vector2 mapMin, Vector2 mapMax, float s)
    {
        float pad = MathF.Max(1f, 3f * s);
        var outerMin = new Vector2(mapMin.X - pad * 2f, mapMin.Y - pad * 2f);
        var outerMax = new Vector2(mapMax.X + pad * 2f, mapMax.Y + pad * 2f);

        // Title strip over the map's top edge, where the zone name already
        // lands - carved from the same stone as the frame, so the cluster reads
        // as one piece rather than a label sitting on a box.
        float rule = MathF.Max(1f, s);
        var titleMin = new Vector2(outerMin.X, outerMin.Y - 22f * s);
        var titleMax = new Vector2(outerMax.X, outerMin.Y);
        dl.AddRectFilledMultiColor(titleMin, titleMax,
            PainterlyStoneTop, PainterlyStoneTop, PainterlyStoneLow, PainterlyStoneLow);
        dl.AddRect(titleMin, titleMax, PainterlyFrameOuter, 0f, ImDrawFlags.None, rule);
        DrawBevel(dl, titleMin, titleMax, rule, PainterlyStoneTop, PainterlyFrameOuter);
        dl.AddLine(new Vector2(titleMin.X, titleMax.Y - rule),
                   new Vector2(titleMax.X, titleMax.Y - rule), PainterlyFrameRule, rule);

        // Frame proper: the shared carved panel, so map, portrait and skill bar
        // are literally the same stone, inlay and light direction.
        DrawSquarePanel(dl, mapMin, mapMax - mapMin, s);

        // Compass letter under the map, as on the reference's framed map.
        float fontSize = 11f * s;
        Vector2 extent = ImGui.CalcTextSize("N") * (fontSize / MathF.Max(1, ImGui.GetFontSize()));
        dl.AddText(ImGui.GetFont(), fontSize,
            new Vector2((outerMin.X + outerMax.X) * .5f - extent.X * .5f, outerMax.Y + 2f * s),
            UiGoldU32(), "N");
    }

    private void DrawMinimapZoneText(ImDrawListPtr dl, Vector2 root, float s)
    {
        Vector2 min = (root + new Vector2(29, 7)) * s;
        Vector2 size = new Vector2(128, 12) * s;
        EnsureAreaTableForMinimap();
        uint fallback = _net?.Player?.Zone ?? 0;
        string text = _areas?.AreaName(_minimapAreaId) is { Length: > 0 } subZone ? subZone :
            _areas?.ZoneName(fallback) is { Length: > 0 } zone ? zone : _config.Start.MapName;
        float fontSize = 12 * s;
        Vector2 extent = ImGui.CalcTextSize(text) * (fontSize / MathF.Max(1, ImGui.GetFontSize()));
        dl.AddText(ImGui.GetFont(), fontSize, min + new Vector2((size.X - extent.X) * .5f, 0),
            UiGoldU32(), text);
        if (_uiParityArmed && _uiParityPanel == "minimap")
        {
            CollectUiParity("MinimapZoneTextButton", "Button", min, size,
                parent: "MinimapCluster", point: "CENTER", offsetX: "-3", offsetY: "83",
                strata: "BACKGROUND");
            CollectUiParity("MinimapZoneText", "FontString", min, size,
                parent: "MinimapZoneTextButton", point: "TOP", font: "GameFontNormal",
                fontPath: @"Fonts\FRIZQT__.TTF", fontSize: "12", color: "#FFD100FF",
                layer: "BACKGROUND", strata: "BACKGROUND");
        }
    }

    private void DrawMinimapTracking(ImDrawListPtr dl, Vector2 root, WorldEntity player, float s)
    {
        AuraSnapshot? tracking = null;
        SpellInfo trackingSpell = default;
        foreach (AuraSnapshot aura in SnapshotAuras(player).Values.OrderBy(a => a.Slot))
            if (_spellCatalog?.TryGet(aura.SpellId, out SpellInfo spell) == true &&
                spell.AuraIds?.Any(type => type is 44 or 45 or 151) == true)
            { tracking = aura; trackingSpell = spell; }
        if (tracking is not AuraSnapshot active) return;
        uint icon = _gameplayArt!.Handle(trackingSpell.IconPath);
        if (icon == 0) return;
        Vector2 frame = root + new Vector2(20, 22);
        dl.AddImage((nint)icon, (frame + new Vector2(7, 6)) * s,
            (frame + new Vector2(33, 32)) * s);
        DrawMinimapTexture(dl, frame, Vector2.Zero, new(64),
            @"Interface\Minimap\MiniMap-TrackingBorder", Vector2.Zero, Vector2.One);
        Vector2 hitMin = (frame + new Vector2(7, 6)) * s;
        Vector2 hitMax = hitMin + new Vector2(33) * s;
        if (ImGui.IsMouseHoveringRect(hitMin, hitMax, false) &&
            ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            CancelPlayerAura(active, "MINIMAP_TRACKING_RIGHT_CLICK");
    }

    private void DrawMinimapMail(ImDrawListPtr dl, Vector2 root, float s)
    {
        if (!_hasNewMail) return;
        // MiniMapMailFrame: TOPRIGHT of Minimap, +21/-38; icon +7/-6 and tracking border.
        Vector2 frame = root + new Vector2(166, 60);
        uint icon = _gameplayArt?.Handle(@"Interface\Icons\INV_Letter_15") ?? 0;
        if (icon != 0)
            dl.AddImage((nint)icon, (frame + new Vector2(7, 6)) * s,
                (frame + new Vector2(25, 24)) * s);
        DrawMinimapTexture(dl, frame, Vector2.Zero, new(52),
            @"Interface\Minimap\MiniMap-TrackingBorder", Vector2.Zero, Vector2.One);
    }

    private void DrawMinimapPartyDots(ImDrawListPtr dl, WorldEntity player, Vector3 playerPosition,
        Vector2 mapMin, Vector2 mapMax, float s)
    {
        float halfTile = 0.10f + _minimapZoom * 0.025f;
        float pixelsPerYard = (mapMax.X - mapMin.X) / (2f * halfTile * 533.33333f);
        Vector2 center = (mapMin + mapMax) * .5f;
        foreach (PartyMember member in _partyMembers)
        {
            if (member.Guid == player.Guid || !_entities.TryGet(member.Guid, out WorldEntity unit)) continue;
            Vector2 dot = center + new Vector2(
                -(unit.Position.Y - playerPosition.Y),
                -(unit.Position.X - playerPosition.X)) * pixelsPerYard;
            if (Vector2.DistanceSquared(dot, center) > MathF.Pow(66f * s, 2)) continue;
            dl.AddCircleFilled(dot, MathF.Max(2f, 2.5f * s), 0xff00a5ff);
        }
    }

    private static void DrawMinimapPlayerArrow(ImDrawListPtr dl, float orientation, Vector2 center, float s)
    {
        // FrameXML supplies this as MinimapArrow.mdx. The world renderer does not render M2s
        // inside ImGui, so preserve its directional behavior with the same centered pointer.
        Vector2 forward = new(-MathF.Sin(orientation), -MathF.Cos(orientation));
        Vector2 right = new(-forward.Y, forward.X);
        Vector2 tip = center + forward * (8f * s);
        Vector2 tail = center - forward * (5f * s);
        dl.AddTriangleFilled(tip, tail + right * (4f * s), tail - right * (4f * s), 0xffffffff);
        dl.AddTriangle(tip, tail + right * (4f * s), tail - right * (4f * s), 0xff202020, 1.25f * s);
    }

    /// <summary>
    /// Vanilla's tracked-resource leg: PLAYER_TRACK_RESOURCES -> GAMEOBJECT_QUERY chest
    /// lockId -> Lock.dbc SKILL LockType -> ObjectIcons cell 0 (gold), 8 px.
    /// </summary>
    private MinimapResourceTooltipCandidate? DrawMinimapResourceDots(
        ImDrawListPtr dl, WorldEntity player, Vector3 playerPosition,
        Vector2 mapMin, Vector2 mapMax, float s)
    {
        uint mask = player.Fields.PlayerTrackResources;
        EnsureLockCatalog();
        if (mask == 0 || _locks is null)
        {
            ReportMinimapResourceSet(mask, []);
            return null;
        }
        float halfTile = 0.10f + _minimapZoom * 0.025f;
        float radiusYards = halfTile * 533.33333f;
        float pixelsPerYard = (mapMax.X - mapMin.X) / (radiusYards * 2f);
        Vector2 center = (mapMin + mapMax) * .5f;
        uint icons = _gameplayArt?.Handle(@"Interface\Minimap\ObjectIcons") ?? 0;
        var visible = new List<(WorldEntity Go, GameObjectTemplate Template, float Distance, Vector2 Dot)>();
        foreach (WorldEntity go in _entities.Entities.Values.Where(e => e.IsGameObject))
        {
            RequireGameObjectTemplate(go);
            if (!_gameObjectTemplates.TryGetValue(go.Entry, out GameObjectTemplate? template) ||
                !_locks.MatchesResourceMask(template.LockId, mask)) continue;
            float distance = Vector3.Distance(go.Position, playerPosition);
            if (distance > radiusYards) continue;
            Vector2 dot = center + new Vector2(
                -(go.Position.Y - playerPosition.Y),
                -(go.Position.X - playerPosition.X)) * pixelsPerYard;
            visible.Add((go, template, distance, dot));
        }
        MinimapResourceTooltipCandidate? hoveredTooltip = null;
        dl.PushClipRect(mapMin, mapMax, true);
        foreach (var row in visible)
        {
            Vector2 half = new(4f * s);
            if (icons != 0)
                dl.AddImage((nint)icons, row.Dot - half, row.Dot + half,
                    new Vector2(0f, 0f), new Vector2(.25f, .25f));
            else dl.AddCircleFilled(row.Dot, 3f * s, 0xff00d8ff);
            if (ImGui.IsMouseHoveringRect(row.Dot - half, row.Dot + half, false))
                hoveredTooltip = new(row.Go.Guid, row.Template.Name);
        }
        dl.PopClipRect();
        ReportMinimapResourceSet(mask, visible);
        return hoveredTooltip;
    }

    private static GameTooltipOwnerKey MinimapResourceGameTooltipOwner(ulong guid)
        => new("minimap-resource-dot", guid);

    private bool UpdateAndQueueMinimapResourceTooltip(
        MinimapResourceTooltipCandidate? hovered)
    {
        // The frame guard must precede Claim so an out-of-stratum minimap pass cannot change
        // ownership. A faded runtime is retained locally only to prepare this frame's callback;
        // callbacks themselves never cross frames or read live world/template state.
        if (!_sharedTooltipFrameOpen || _sharedTooltipFrameResolved) return false;

        if (hovered is MinimapResourceTooltipCandidate candidate)
        {
            GameTooltipOwnerToken token = ClaimSharedGameTooltip(
                MinimapResourceGameTooltipOwner(candidate.Guid));
            if (!ClearSharedGameTooltip(token))
                throw new InvalidOperationException(
                    "A freshly claimed minimap resource tooltip rejected its exact clear.");
            _minimapResourceTooltip = new(token, candidate.Name);
        }
        else if (_minimapResourceTooltip is MinimapResourceTooltipRuntime departing)
        {
            if (!SharedGameTooltipIsOwned(departing.Token))
            {
                _minimapResourceTooltip = null;
                return false;
            }
            BeginSharedGameTooltipFade(departing.Token, _sharedTooltipFrameTime,
                GameTooltipUiLaw.WorldFadeSeconds);
        }

        if (_minimapResourceTooltip is not MinimapResourceTooltipRuntime runtime)
            return false;
        if (!SharedGameTooltipIsOwned(runtime.Token))
        {
            _minimapResourceTooltip = null;
            return false;
        }

        GameTooltipLifecycleState lifecycle = SharedGameTooltipSnapshot().Lifecycle;
        if (!lifecycle.Visible || lifecycle.Alpha <= 0f)
        {
            _minimapResourceTooltip = null;
            return false;
        }

        string preparedName = runtime.Name;
        float preparedAlpha = lifecycle.Alpha;
        return QueueSharedGameTooltipRenderer(runtime.Token,
            SharedGameTooltipLeavePolicy.Fade(GameTooltipUiLaw.WorldFadeSeconds),
            () => DrawMinimapResourceTooltip(preparedName, preparedAlpha));
    }

    private static void DrawMinimapResourceTooltip(string preparedName, float preparedAlpha)
    {
        // Keep the established SetTooltip cursor seat at full alpha and during fade. The frozen
        // client retains its last pointer seat after departure, but there is no public ImGui
        // position capture for SetTooltip; inventing an offset would move the approved anchor.
        if (preparedAlpha >= 1f)
        {
            ImGui.SetTooltip(preparedName);
            return;
        }

        float alpha = Math.Clamp(preparedAlpha, 0f, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * alpha);
        try
        {
            ImGui.SetTooltip(preparedName);
        }
        finally
        {
            ImGui.PopStyleVar();
        }
    }

    private void ReportMinimapResourceSet(uint mask,
        IReadOnlyList<(WorldEntity Go, GameObjectTemplate Template, float Distance, Vector2 Dot)> visible)
    {
        string signature = $"{mask:X8}:" + string.Join(',', visible.OrderBy(x => x.Go.Guid)
            .Select(x => $"{x.Go.Guid:X16}/{x.Template.LockId}"));
        if (signature == _minimapResourceSignature) return;
        _minimapResourceSignature = signature;
        EmitInterface("gathering", "minimap-nodes", "RENDERED", _net?.PlayerGuid ?? 0,
            $"mask=0x{mask:X8};herbs={visible.Count(x => _locks?.ResourceLockType(x.Template.LockId) == 2)};" +
            $"minerals={visible.Count(x => _locks?.ResourceLockType(x.Template.LockId) == 3)};total={visible.Count};" +
            $"nodes={string.Join('|', visible.OrderBy(x => x.Distance).Select(x => $"{x.Go.Entry}:{SanitizeEvidence(x.Template.Name)}:{x.Distance:F2}"))}");
    }

    private void DrawMinimapTexture(ImDrawListPtr dl, Vector2 root, Vector2 offset, Vector2 size,
        string path, Vector2 uv0, Vector2 uv1)
    {
        uint handle = _gameplayArt?.Handle(path) ?? 0;
        if (handle == 0) return;
        float s = GameplayUiScale();
        Vector2 min = (root + offset) * s;
        dl.AddImage((nint)handle, min, min + size * s, uv0, uv1);
    }

    private void DrawMinimapButton(ImDrawListPtr dl, Vector2 logicalMin, string texture, Action click)
    {
        float s = GameplayUiScale();
        Vector2 min = logicalMin * s, max = min + new Vector2(32) * s;
        uint handle = _gameplayArt?.Handle(texture) ?? 0;
        if (handle != 0) dl.AddImage((nint)handle, min, max);
        if (ImGui.IsMouseHoveringRect(min, max, false) && ImGui.IsMouseClicked(ImGuiMouseButton.Left)) click();
    }

    private void DrawMovingMinimap(ImDrawListPtr dl, Vector2 mapMin, Vector2 mapMax,
        MinimapProjection player)
    {
        EnsureMinimapTileMap();
        float halfTiles = 0.10f + _minimapZoom * 0.025f;
        float pixelsPerTile = (mapMax.X - mapMin.X) / (2f * halfTiles);
        Vector2 center = (mapMin + mapMax) * .5f;
        string mapName = _adts?.MapName ?? _config.Start.MapName;

        // The old implementation cropped one BLP and clamped its UVs. At the
        // edge of that BLP the crop stopped moving. Vanilla instead presents a
        // continuous plane of neighboring minimap tiles beneath a fixed arrow.
        dl.PushClipRect(mapMin, mapMax, true);
        for (int rowOffset = -1; rowOffset <= 1; rowOffset++)
        for (int columnOffset = -1; columnOffset <= 1; columnOffset++)
        {
            int column = player.TileColumn + columnOffset;
            int row = player.TileRow + rowOffset;
            string logical = $@"{mapName}\map{column:00}_{row:00}.blp";
            if (_minimapTileMap?.TryGetValue(logical, out string? hashed) != true) continue;
            uint handle = _gameplayArt!.Handle(@"textures\Minimap\" + hashed);
            if (handle == 0) continue;
            Vector2 min = center + new Vector2(
                column - (player.TileColumn + player.TileU),
                row - (player.TileRow + player.TileV)) * pixelsPerTile;
            Vector2 max = min + new Vector2(pixelsPerTile);
            if (max.X <= mapMin.X || max.Y <= mapMin.Y || min.X >= mapMax.X || min.Y >= mapMax.Y) continue;
            dl.AddImage((nint)handle, min, max);
        }
        dl.PopClipRect();
    }

    private void UpdateMinimapArea(MinimapProjection projection)
    {
        EnsureAreaTableForMinimap();
        string mapName = _adts?.MapName ?? _config.Start.MapName;
        if (!string.Equals(mapName, _minimapAreaMap, StringComparison.OrdinalIgnoreCase))
        {
            _minimapAreaMap = mapName;
            _minimapAreaId = 0;
            _minimapReportedZoneId = 0;
        }
        uint areaId = 0;
        if (_adts?.TryPeek(projection.TileColumn, projection.TileRow, out var adt) == true)
            areaId = projection.AreaId(adt);
        if (areaId == 0) return;
        _minimapAreaId = areaId;

        uint zoneId = _areas?.ParentZoneId(areaId) ?? 0;
        if (zoneId == 0 || zoneId == _minimapReportedZoneId) return;
        _minimapReportedZoneId = zoneId;
        _net?.ZoneUpdate(zoneId);
        EmitInterface("minimap", "area", "UPDATED", _net?.PlayerGuid ?? 0,
            $"map={mapName};tile={projection.TileColumn}|{projection.TileRow};" +
            $"chunk={projection.ChunkX}|{projection.ChunkY};area={areaId};" +
            $"subZone={_areas?.AreaName(areaId)};zone={zoneId}");
        Console.WriteLine($"[minimap] area={areaId} '{_areas?.AreaName(areaId)}' zone={zoneId}");
    }

    private void EnsureAreaTableForMinimap()
    {
        if (_areasLoaded) return;
        _areasLoaded = true;
        try
        {
            byte[]? bytes = _mpq?.ReadFile(AreaTableCatalog.MpqPath);
            if (bytes is not null) _areas = AreaTableCatalog.Parse(bytes);
        }
        catch (Exception e) { Console.WriteLine($"[minimap] AreaTable load failed: {e.Message}"); }
    }

    private void EnsureMinimapTileMap()
    {
        if (_minimapTileMap is not null) return;
        _minimapTileMap = new(StringComparer.OrdinalIgnoreCase);
        byte[]? bytes = _mpq?.ReadFile(@"textures\Minimap\md5translate.trs");
        if (bytes is null) return;
        foreach (string line in Encoding.UTF8.GetString(bytes).Split('\n'))
        {
            string[] parts = line.Trim().Split('\t', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2) _minimapTileMap[parts[0]] = parts[1];
        }
    }
}

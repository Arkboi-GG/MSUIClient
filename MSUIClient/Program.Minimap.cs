using System.Numerics;
using System.Text;
using ImGuiNET;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private Dictionary<string, string>? _minimapTileMap;
    private int _minimapZoom = 2;
    private bool _minimapVisible = true;

    private void DrawMinimap()
    {
        if (!_minimapVisible || _net is null || _gameplayArt is null ||
            !_entities.TryGet(_net.PlayerGuid, out WorldEntity player)) return;

        float s = GameplayUiScale();
        Vector2 logicalDisplay = ImGui.GetIO().DisplaySize / s;
        Vector2 root = new(logicalDisplay.X - 192f, 0f);
        Vector2 rootPx = root * s;
        ImDrawListPtr dl = ImGui.GetBackgroundDrawList();

        if (_uiParityArmed && _uiParityPanel == "minimap")
        {
            BeginUiParityFrame(rootPx, s);
            CollectUiParity("MinimapCluster", "Frame", rootPx, new Vector2(192) * s,
                parent: "", point: "TOPRIGHT", strata: "BACKGROUND");
        }

        Vector2 mapMin = (root + new Vector2(35, 22)) * s;
        Vector2 mapMax = mapMin + new Vector2(140) * s;
        string tilePath = ResolveMinimapTile(player.Position, out Vector2 tileUv);
        uint tile = tilePath.Length == 0 ? 0 : _gameplayArt.Handle(tilePath);
        if (tile != 0)
        {
            float half = 0.10f + _minimapZoom * 0.025f;
            Vector2 uv0 = Vector2.Clamp(tileUv - new Vector2(half), Vector2.Zero, Vector2.One - new Vector2(2 * half));
            Vector2 uv1 = uv0 + new Vector2(2 * half);
            dl.AddImageRounded((nint)tile, mapMin, mapMax, uv0, uv1, 0xffffffff,
                70f * s, ImDrawFlags.RoundCornersAll);
        }
        if (_uiParityArmed && _uiParityPanel == "minimap")
            CollectUiParity("Minimap", "Minimap", mapMin, new Vector2(140) * s,
                parent: "MinimapCluster", point: "CENTER", relativePoint: "TOP",
                offsetX: "9", offsetY: "-92", strata: "BACKGROUND");

        DrawMinimapTexture(dl, root, new(0, 20), new(192),
            @"Interface\Minimap\UI-Minimap-Border", new(.25f, .125f), new(1f, .875f));
        if (_uiParityArmed && _uiParityPanel == "minimap")
        {
            CollectUiParity("MinimapBackdrop", "Frame", (root + new Vector2(0, 20)) * s,
                new Vector2(192) * s, parent: "Minimap", point: "CENTER",
                relativeTo: "MinimapCluster", offsetX: "0", offsetY: "-20", strata: "BACKGROUND");
            CollectUiParity("MinimapBorder", "Texture", (root + new Vector2(0, 20)) * s,
                new Vector2(192) * s, parent: "MinimapBackdrop",
                texture: @"Interface\Minimap\UI-Minimap-Border", layer: "ARTWORK",
                strata: "BACKGROUND", texCoords: "0.25|0.125|1.0|0.875");
        }

        DrawMinimapButton(dl, root + new Vector2(157, 113), @"Interface\Minimap\UI-Minimap-ZoomInButton-Up",
            () => _minimapZoom = Math.Max(0, _minimapZoom - 1));
        DrawMinimapButton(dl, root + new Vector2(131, 141), @"Interface\Minimap\UI-Minimap-ZoomOutButton-Up",
            () => _minimapZoom = Math.Min(5, _minimapZoom + 1));
        if (_uiParityArmed && _uiParityPanel == "minimap")
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

        DrawMinimapTexture(dl, root, Vector2.Zero, new(192, 32),
            @"Interface\Minimap\UI-Minimap-Border", new(.25f, 0), new(1, .125f));
        if (_uiParityArmed && _uiParityPanel == "minimap")
            CollectUiParity("MinimapBorderTop", "Texture", rootPx, new Vector2(192, 32) * s,
                parent: "MinimapCluster", point: "TOPRIGHT",
                texture: @"Interface\Minimap\UI-Minimap-Border", layer: "ARTWORK",
                strata: "BACKGROUND", texCoords: "0.25|0.0|1.0|0.125");

        DrawMinimapZoneText(dl, root, s);
        DrawMinimapTracking(dl, root, player, s);

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

    private void DrawMinimapZoneText(ImDrawListPtr dl, Vector2 root, float s)
    {
        Vector2 min = (root + new Vector2(29, 7)) * s;
        Vector2 size = new Vector2(128, 12) * s;
        uint areaId = _net?.Player?.Zone ?? 0;
        string text = _areas?.ZoneName(areaId) is { Length: > 0 } zone ? zone : _config.Start.MapName;
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
        foreach (var aura in player.Fields.Auras())
        {
            if (_spellCatalog is null || !_spellCatalog.TryGet(aura.SpellId, out SpellInfo spell) ||
                !(spell.Name.StartsWith("Find ", StringComparison.OrdinalIgnoreCase) ||
                  spell.Name.StartsWith("Track ", StringComparison.OrdinalIgnoreCase))) continue;
            uint icon = _gameplayArt!.Handle(spell.IconPath);
            if (icon == 0) return;
            Vector2 frame = root + new Vector2(20, 22);
            dl.AddImage((nint)icon, (frame + new Vector2(7, 6)) * s,
                (frame + new Vector2(33, 32)) * s);
            DrawMinimapTexture(dl, frame, Vector2.Zero, new(64),
                @"Interface\Minimap\MiniMap-TrackingBorder", Vector2.Zero, Vector2.One);
            break;
        }
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

    private string ResolveMinimapTile(Vector3 position, out Vector2 uv)
    {
        const float grid = 533.33333f;
        int col = (int)MathF.Floor(32f - position.Y / grid);
        int row = (int)MathF.Floor(32f - position.X / grid);
        float originX = (32f - row) * grid, originY = (32f - col) * grid;
        uv = new((originY - position.Y) / grid, (originX - position.X) / grid);
        EnsureMinimapTileMap();
        string logical = $@"{_config.Start.MapName}\map{col:00}_{row:00}.blp";
        return _minimapTileMap?.TryGetValue(logical, out string? hashed) == true
            ? @"textures\Minimap\" + hashed : "";
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

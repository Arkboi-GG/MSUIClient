using System.Globalization;
using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// World Builder: the editing camera, the ground grid and road surfaces (shared_docs/WORLD_BUILDER.md §4).
//
// Fly camera = the existing Free view (Ctrl+F); while a World Builder tool owns the left button its box-selection is
// suppressed, and tools reach ground far below the camera. The ground grid draws the terrain's real vertex cells
// (about 4.2 yd) and chunk borders (33.3 yd) around the pointer, highlights the picked cell and can snap points to
// it. Road surfaces paint a vanilla Tileset texture along the path in the editor with TerrainPaintLaw - the same rule
// the build must use - straight into the loaded tile's texture layers, then rebuild that tile. The original layers
// are kept per tile and put back whenever the road changes, the page closes or a tile re-streams.
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class GameLoop
{
    private bool _wbShowGrid = true, _wbSnapGrid;
    private const float WbPickReach = 900f;
    private bool _wbFlyOwned;                 // the World Builder opened the fly camera (its own view angle applies)
    private float _wbFlyPitch = 60f;

    /// <summary>While the World Builder's fly camera is up, its editing view angle replaces the Command View angle
    /// (players tune that one shallow for commanding units; editing wants to look down at the ground).</summary>
    private float? WbFlyPitchOverride =>
        _freeView && _wbFlyOwned && _creatorWorldRequested && _creatorPanel == CreatorPanel.World ? _wbFlyPitch : null;

    // vanilla ground textures
    private List<string>? _wbGroundTextures;
    private string[] _wbGroundFolders = Array.Empty<string>();
    private readonly byte[] _wbTexSearch = new byte[64];
    private int _wbTexFolder;

    // road paint preview
    private sealed class WbPaintSnapshot
    {
        public required AdtTerrainReader.AdtResult Adt;
        public required List<string> Textures;
        public required AdtTerrainReader.MclyLayer[][] Layers;
    }
    private readonly Dictionary<(int col, int row), WbPaintSnapshot> _wbPaintSnapshots = new();
    private string _wbPaintSig = "";
    private double _wbPaintAt;

    /// <summary>A World Builder action owns the left button (no Free view box-selection, no camera look).</summary>
    private bool WbOwnsLeftClick => _creatorWorldRequested && (_wbTool != WorldBuilderTool.None || WbNpcHasActiveAction);

    // ── camera + grid controls (workspace header) ────────────────────────────

    private void DrawWbViewControls()
    {
        if (CreatorButton(_freeView ? "Back to my character" : "Fly camera")) WbToggleFlyCamera();
        ObserveWorldBuilderUiItem("Fly camera");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(_freeView
                ? "Return the camera to your character (Ctrl+F)."
                : "Detach the camera and fly: W A S D move, Space up, Caps Lock down, Shift faster, right mouse turns,\n" +
                  "mouse wheel flies forward or back. The pointer stays free for the World Builder tools. (Ctrl+F)");
        ImGui.SameLine();
        ImGui.Checkbox("Grid", ref _wbShowGrid);
        ObserveWorldBuilderUiItem("Ground grid");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Show the terrain's cells and chunk borders around the pointer while a tool is active.");
        ImGui.SameLine();
        ImGui.Checkbox("Snap", ref _wbSnapGrid);
        ObserveWorldBuilderUiItem("Snap to grid");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Path points, buildings and objects land exactly on terrain grid corners.");
        if (_freeView && _wbFlyOwned)
        {
            ImGui.SetNextItemWidth(CreatorControlWidth * 0.5f);
            ImGui.SliderFloat("View angle", ref _wbFlyPitch, 20f, 85f, "%.0f deg");
        }
    }

    /// <summary>Enter or leave the fly camera. Entering lifts the camera well above trees and roofs and looks down.</summary>
    private void WbToggleFlyCamera()
    {
        if (_freeView) { ToggleFreeView(); _wbFlyOwned = false; return; }
        ToggleFreeView();
        if (!_freeView || _controller is null) return;
        _wbFlyOwned = true;
        var at = _controller.Position;
        float ground = _terrain?.SampleHeight(at.X, at.Y) ?? at.Z;
        _controller.Teleport(at.X, at.Y, MathF.Max(at.Z, ground + 40f));
    }

    /// <summary>Tools pick ground under the pointer even from a high fly camera: the normal pick first (floors, roofs),
    /// then the terrain alone out to <see cref="WbPickReach"/> yards.</summary>
    private bool WbPickGround(Vector2 pixel, bool terrainOnly, out Vector3 hit)
    {
        hit = default;
        if (!terrainOnly && TryPickGround(pixel, out hit)) return true;
        return _window.Camera.ScreenPointToRay(pixel, _window.FramebufferSize) is { } ray &&
               TryPickTerrainSurface(ray.Origin, ray.Direction, WbPickReach, out hit, out _);
    }

    /// <summary>The nearest terrain grid corner (outer vertex), on the ground.</summary>
    private Vector3 WbSnapToGrid(Vector3 p)
    {
        float u = WorldBuilderLaw.Unit, c = WorldBuilderLaw.Corner;
        float x = c - MathF.Round((c - p.X) / u) * u, y = c - MathF.Round((c - p.Y) / u) * u;
        return new Vector3(x, y, _terrain?.SampleHeight(x, y) ?? p.Z);
    }

    /// <summary>The terrain's own grid around the pointer: cell lines (faint), chunk borders (bright), the picked cell
    /// filled, and where it is (tile, chunk, cell).</summary>
    private void WbDrawGroundGrid()
    {
        if (!_wbShowGrid || _wbCursor is not { } at || _terrain is null || _creatorPanel != CreatorPanel.World) return;
        if (_wbTool == WorldBuilderTool.None && !_wbPathDragging) return;
        const int reach = 7;
        float u = WorldBuilderLaw.Unit, corner = WorldBuilderLaw.Corner;
        var draw = ImGui.GetBackgroundDrawList();
        var display = ImGui.GetIO().DisplaySize;
        var cam = _window.Camera;
        float gx = (corner - at.X) / u, gy = (corner - at.Y) / u;      // global vertex coordinates
        int i0 = (int)MathF.Floor(gx), j0 = (int)MathF.Floor(gy);
        Vector3 V(int i, int j)
        {
            float x = corner - i * u, y = corner - j * u;
            return new Vector3(x, y, (_terrain.SampleHeight(x, y) ?? at.Z) + 0.12f);
        }
        uint Colour(int index, int from, float baseAlpha)
        {
            float fade = 1f - MathF.Min(1f, MathF.Abs(index - from) / (reach + 1f));
            bool chunk = ((index % 8) + 8) % 8 == 0;
            uint a = (uint)(255 * baseAlpha * fade * (chunk ? 1f : 0.55f));
            return (a << 24) | (chunk ? 0x0040E0FFu : 0x00FFFFFFu);
        }
        for (int i = i0 - reach; i <= i0 + reach + 1; i++)
        {
            Vector2? prev = null;
            for (int j = j0 - reach; j <= j0 + reach + 1; j++)
            {
                Vector2? px = cam.TryProjectToScreen(V(i, j), display, out var s, out _) ? s : null;
                if (prev is { } p0 && px is { } p1) draw.AddLine(p0, p1, Colour(i, i0, 0.8f), ((i % 8) + 8) % 8 == 0 ? 2f : 1f);
                prev = px;
            }
        }
        for (int j = j0 - reach; j <= j0 + reach + 1; j++)
        {
            Vector2? prev = null;
            for (int i = i0 - reach; i <= i0 + reach + 1; i++)
            {
                Vector2? px = cam.TryProjectToScreen(V(i, j), display, out var s, out _) ? s : null;
                if (prev is { } p0 && px is { } p1) draw.AddLine(p0, p1, Colour(j, j0, 0.8f), ((j % 8) + 8) % 8 == 0 ? 2f : 1f);
                prev = px;
            }
        }
        if (cam.TryProjectToScreen(V(i0, j0), display, out var q0, out _) && cam.TryProjectToScreen(V(i0, j0 + 1), display, out var q1, out _) &&
            cam.TryProjectToScreen(V(i0 + 1, j0 + 1), display, out var q2, out _) && cam.TryProjectToScreen(V(i0 + 1, j0), display, out var q3, out _))
            draw.AddQuadFilled(q0, q1, q2, q3, 0x5040E0FFu);
        if (_wbSnapGrid && _wbTool is WorldBuilderTool.Path or WorldBuilderTool.Place or WorldBuilderTool.GameObject &&
            cam.TryWorldToScreen(at + new Vector3(0, 0, 0.12f), display, out var snapped))
            draw.AddCircle(snapped, 7f, 0xFFFFFFFFu, 12, 2f);

        var (col, row) = WorldBuilderLaw.TileOf(at.X, at.Y);
        int chunkX = Math.Clamp((((j0 % 128) + 128) % 128) / 8, 0, 15), chunkY = Math.Clamp((((i0 % 128) + 128) % 128) / 8, 0, 15);
        string where = Inv($"tile {col},{row}  chunk {chunkX},{chunkY}  cell {((j0 % 8) + 8) % 8},{((i0 % 8) + 8) % 8}  height {at.Z:F1}");
        var mouse = ImGui.GetIO().MousePos + new Vector2(16f, 40f * CreatorUiScale);
        draw.AddText(mouse + Vector2.One, 0xD0000000u, where);
        draw.AddText(mouse, 0xFFB0E8FFu, where);
    }

    // ── vanilla ground texture picker ────────────────────────────────────────

    private static readonly string[] WbTextureChips = { "road", "dirt", "path", "cobble", "stone", "rock", "grass", "sand", "mud", "snow" };

    private void WbEnsureGroundTextures()
    {
        if (_wbGroundTextures is not null || _mpq is null) return;
        _wbGroundTextures = _mpq.ListedFiles()
            .Where(f => f.StartsWith("Tileset\\", StringComparison.OrdinalIgnoreCase) && f.EndsWith(".blp", StringComparison.OrdinalIgnoreCase) &&
                        !f.EndsWith("_s.blp", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        _wbGroundFolders = new[] { "All zones" }.Concat(_wbGroundTextures
            .Select(f => f.Split('\\') is { Length: > 2 } parts ? parts[1] : "")
            .Where(f => f.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f, StringComparer.OrdinalIgnoreCase)).ToArray();
    }

    /// <summary>Thumbnail grid of the game's own ground textures (Tileset\Zone\*.blp). True when the choice changed.</summary>
    private bool DrawWbTexturePicker(ref string? selected)
    {
        WbEnsureGroundTextures();
        if (_wbGroundTextures is null) { ImGui.TextDisabled("Game textures are not loaded."); return false; }
        bool changed = false;
        ImGui.SetNextItemWidth(CreatorControlWidth * 0.6f);
        ImGui.Combo("Zone##wb-tex", ref _wbTexFolder, _wbGroundFolders, _wbGroundFolders.Length);
        ImGui.SetNextItemWidth(CreatorControlWidth);
        ImGui.InputText("Search textures##wb-tex", _wbTexSearch, (uint)_wbTexSearch.Length);
        ObserveWorldBuilderUiItem("Texture search");
        bool firstChip = true;
        foreach (var chip in WbTextureChips)
        {
            float width = ImGui.CalcTextSize(chip).X + ImGui.GetStyle().FramePadding.X * 2f + ImGui.GetStyle().ItemSpacing.X;
            if (!firstChip && ImGui.GetItemRectMax().X + width < ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X) ImGui.SameLine();
            if (ImGui.SmallButton(chip + "##texchip")) WbSetBuf(_wbTexSearch, chip);
            firstChip = false;
        }
        string folder = _wbTexFolder > 0 && _wbTexFolder < _wbGroundFolders.Length ? _wbGroundFolders[_wbTexFolder] : "";
        var words = WbText(_wbTexSearch).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var hits = _wbGroundTextures
            .Where(f => folder.Length == 0 || f.StartsWith("Tileset\\" + folder + "\\", StringComparison.OrdinalIgnoreCase))
            .Where(f => words.All(w => f.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .Take(120).ToList();
        float thumb = 64f * CreatorUiScale;
        if (ImGui.BeginChild("##wb-tex-grid", new Vector2(-1f, thumb * 2.6f + 8f), true))
        {
            float right = ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X;
            for (int i = 0; i < hits.Count; i++)
            {
                string path = hits[i];
                if (i > 0 && ImGui.GetItemRectMax().X + ImGui.GetStyle().ItemSpacing.X + thumb < right) ImGui.SameLine();
                uint handle = _gameplayArt?.Handle(path) ?? 0;
                if (handle != 0) ImGui.Image((nint)handle, new Vector2(thumb, thumb));
                else ImGui.Dummy(new Vector2(thumb, thumb));
                bool isSelected = string.Equals(path, selected, StringComparison.OrdinalIgnoreCase);
                if (isSelected) ImGui.GetWindowDrawList().AddRect(ImGui.GetItemRectMin() - Vector2.One * 2, ImGui.GetItemRectMax() + Vector2.One * 2, 0xFF33DDEEu, 0f, ImDrawFlags.None, 3f);
                string name = Path.GetFileNameWithoutExtension(path);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(path);
                if (ImGui.IsItemClicked()) { selected = path; changed = true; }
                ObserveWorldBuilderUiItem("Texture: " + name);
            }
        }
        ImGui.EndChild();   // always, whatever BeginChild returned
        ImGui.TextDisabled(hits.Count == 120 ? "First 120 shown; narrow the search." : $"{hits.Count} textures");
        return changed;
    }

    // ── road paint preview ───────────────────────────────────────────────────

    /// <summary>Paint the editor path's road surface into the loaded tiles (throttled while dragging); restore the
    /// original layers when it changes or the page closes. Called every World Builder frame.</summary>
    private void WbUpdateRoadPaintPreview()
    {
        int map = _config.Start.Map;
        // A re-streamed tile comes back as a fresh, unpainted instance: drop its snapshot and repaint.
        foreach (var (key, snap) in _wbPaintSnapshots.ToList())
            if (!_adts!.TryPeek(key.col, key.row, out var now) || !ReferenceEquals(now, snap.Adt))
            { _wbPaintSnapshots.Remove(key); _wbPaintSig = "\u0001"; }

        bool active = WbPathPageOpen && _wbRoadTexture is not null && _wbCarvePoints.Count >= 2 && _wbCarveMap == map &&
                      _adts is not null && float.IsFinite(_wbRoadWidth) && _wbRoadWidth > 0;
        string sig = active
            ? Inv($"{map}|{_wbRoadTexture}|{_wbRoadWidth}|{_wbRoadSoft}|{_wbRoadStrength}|") + string.Join(";", _wbCarvePoints.Select(p => Inv($"{p.X},{p.Y}")))
            : "";
        if (sig == _wbPaintSig) return;
        double t = ImGui.GetTime();
        if (sig.Length > 0 && t - _wbPaintAt < 0.25) return;   // a full tile rebuild per change: keep drags responsive
        _wbPaintAt = t;
        _wbPaintSig = sig;

        var rebuild = new HashSet<(int col, int row)>();
        foreach (var (key, snap) in _wbPaintSnapshots)
        {
            snap.Adt.Textures = new List<string>(snap.Textures);
            if (snap.Adt.Chunks is { } chunks)
                for (int i = 0; i < chunks.Length && i < snap.Layers.Length; i++)
                    if (chunks[i] is { } chunk) chunk.Layers = snap.Layers[i];
            rebuild.Add(key);
        }
        _wbPaintSnapshots.Clear();

        if (active)
        {
            var pts = _wbCarvePoints;
            string texture = _wbRoadTexture!;
            float reach = _wbRoadWidth / 2f + _wbRoadSoft;
            float minX = pts.Min(p => p.X) - reach, maxX = pts.Max(p => p.X) + reach;
            float minY = pts.Min(p => p.Y) - reach, maxY = pts.Max(p => p.Y) + reach;
            var (c0, r0) = WorldBuilderLaw.TileOf(maxX, maxY);
            var (c1, r1) = WorldBuilderLaw.TileOf(minX, minY);
            int painted = 0;
            for (int col = c0; col <= c1; col++)
                for (int row = r0; row <= r1; row++)
                {
                    if (!_adts!.TryPeek(col, row, out var adt) || adt?.Chunks is not { } chunks) continue;
                    var snap = new WbPaintSnapshot
                    {
                        Adt = adt, Textures = new List<string>(adt.Textures),
                        Layers = chunks.Select(c => c?.Layers ?? Array.Empty<AdtTerrainReader.MclyLayer>()).ToArray(),
                    };
                    int n = WbPaintRoadTile(adt, col, row, texture, pts, reach);
                    if (n == 0) continue;
                    painted += n;
                    _wbPaintSnapshots[(col, row)] = snap;
                    rebuild.Add((col, row));
                }
            if (WbScriptPath is not null) Console.WriteLine($"[wb] road paint preview: {painted} chunk(s) on {_wbPaintSnapshots.Count} tile(s), {texture}");
        }
        foreach (var key in rebuild)
            if (_terrain?.IsResident(key) == true && _adts!.TryPeek(key.col, key.row, out var a) && a is not null)
                _terrain.RebuildTile(key, a);
    }

    /// <summary>Paint one tile's chunks that the road reaches; returns how many chunks changed. New layer objects
    /// replace the old ones (never mutated), so the snapshot can put the originals back.</summary>
    private int WbPaintRoadTile(AdtTerrainReader.AdtResult adt, int col, int row, string texture, List<Vector3> pts, float reach)
    {
        int texIndex = adt.Textures.FindIndex(t => string.Equals(t, texture, StringComparison.OrdinalIgnoreCase));
        bool added = false;
        int changed = 0;
        foreach (var chunk in adt.Chunks!)
        {
            if (chunk is null || chunk.Layers.Length == 0) continue;
            var centre = TerrainPaintLaw.PixelWorld(col, row, chunk.IndexX, chunk.IndexY, 31, 31);
            if (WorldBuilderLaw.PathDistance(pts, centre).Distance > reach + TerrainPaintLaw.ChunkSize * 0.75f) continue;
            if (texIndex < 0) { texIndex = adt.Textures.Count; added = true; }
            var textures = chunk.Layers.Select(l => l.TextureIndex).ToList();
            var alphas = chunk.Layers.Select((l, i) => i == 0 ? null : l.AlphaMap).ToList();
            int cx = chunk.IndexX, cy = chunk.IndexY;
            bool paintedChunk = TerrainPaintLaw.PaintChunk(textures, alphas, texIndex, (r, c) =>
                TerrainPaintLaw.RoadAmount(WorldBuilderLaw.PathDistance(pts, TerrainPaintLaw.PixelWorld(col, row, cx, cy, r, c)).Distance,
                    _wbRoadWidth, _wbRoadSoft, _wbRoadStrength));
            if (!paintedChunk) continue;
            var old = chunk.Layers;
            chunk.Layers = textures.Select((tex, i) =>
            {
                var prior = old.FirstOrDefault(l => l.TextureIndex == tex);
                uint flags = (prior?.Flags ?? 0u) & ~0x300u;
                return new AdtTerrainReader.MclyLayer
                {
                    TextureIndex = tex,
                    Flags = i == 0 ? flags : flags | 0x100u,
                    OffsetInMcal = i == 0 ? 0u : (uint)((i - 1) * 2048),
                    EffectId = prior?.EffectId ?? 0,
                    AlphaMap = i == 0 ? null : alphas[i],
                };
            }).ToArray();
            changed++;
        }
        if (added && changed > 0) adt.Textures.Add(texture);
        return changed;
    }
}

using System.Numerics;
using System.Text;
using System.Text.Json;
using ImGuiNET;
using MSUIClient.Engine;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;
using MSUIClient.World;
using MSUIClient.World.Doodads;
using MSUIClient.World.Wmo;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// Creator Mode World Builder (shared_docs/WORLD_BUILDER.md §4).
//
// Terrain sculpt + WMO/M2 placement, authored against MangosSuperUI World Content
// Packs. The client never writes the server: every stroke/placement/move/delete is
// a POST to /WorldPacks/*, stored as an audited, undoable op, and reaches the game
// only through Publish (patch-7.MPQ + real extractor maps/vmaps/mmaps).
//
// PREVIEW LAW
//   Terrain: height = STOCK (read past patch-7) + server sum of enabled packs (+ the
//   selected pack) + the stroke in hand. Always from stock, so it is right whether or
//   not an up-to-date patch-7 is mounted.
//   Placements: an unpublished (or not-yet-downloaded) placement draws as a dynamic
//   ghost; a published one is already static in the mounted patch-7 ADT.
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class GameLoop
{
    private enum WorldBuilderTool { None, Sculpt, Place, Select, Path, GameObject, Pick }

    private const string WbPatchName = "patch-7.MPQ";
    private const ulong WbCursorGhostKey = 0xB0B0_FFFF_FFFF_FFFFUL;

    private readonly WorldPackClient _wbClient = new();
    private WorldBuilderTool _wbTool;
    private int _wbPackId;

    // server state
    private WorldPackClient.State? _wbState;
    private Task<WorldPackClient.State>? _wbStateTask;
    private int _wbStateMap = -1;
    private int _wbStateIncludePackId = -1, _wbStateRequestedPackId;
    private double _wbStateAt;
    private readonly Dictionary<(int col, int row), float[]> _wbServerSculpt = new();

    // ops in flight + messages
    private readonly List<(string Label, Task<WorldPackClient.Reply> Task)> _wbOps = new();
    private string _wbMessage = "";

    // sculpt
    private WorldBuilderLaw.BrushMode _wbBrush = WorldBuilderLaw.BrushMode.Raise;
    private float _wbRadius = 18f, _wbStrength = 6f, _wbHardness = 0.35f;
    private readonly Dictionary<(int col, int row), float[]> _wbStroke = new();
    private bool _wbStroking;
    private float _wbFlattenTarget;
    private float? _wbFlattenFixed;   // a picked level height (Flatten); null = the stroke's start height
    private Vector3? _wbCursor;
    private double _wbLastDab, _wbLastRebuild;
    private readonly HashSet<(int col, int row)> _wbDirtyTiles = new();
    private string _wbToolHint = "";
    private Task<WorldPackClient.Reply>? _wbSculptSaveTask;
    private readonly Dictionary<(int col, int row), float[]> _wbPendingSculpt = new();
    private bool _wbSculptAwaitingState;
    private int _wbStrokeMap, _wbStrokePackId;

    // stock terrain + what the preview currently owns
    private sealed class WbStockTile
    {
        public required Dictionary<(int x, int y), (float[] Heights, sbyte[]? Normals, float BaseZ)> Chunks;
    }
    private readonly Dictionary<(int col, int row), WbStockTile?> _wbStock = new();
    private readonly Dictionary<(int col, int row), AdtTerrainReader.AdtResult> _wbAppliedTo = new();
    private readonly HashSet<(int col, int row)> _wbPreviewTiles = new();
    private int? _wbStockBuild;   // last publish the pack-map preview bases were derived for

    // placement
    private List<string>? _wbCatalogue;
    private readonly byte[] _wbSearchBuf = new byte[96];
    private string _wbSearchLast = "\u0001";
    private List<string> _wbSearchHits = new();
    private string? _wbModel;
    private float _wbYaw, _wbPitch, _wbRoll, _wbScale = 1f, _wbZOffset;
    private int _wbDoodadSet;
    private readonly Dictionary<int, string> _wbGhosts = new();     // placement id → signature
    private bool _wbGhostsHidden;
    private string? _wbCursorGhostSig;
    private int _wbSelected;
    private bool _wbMoveArmed;

    // publish / download
    private bool _wbRestartOnPublish = true;
    private Task<WorldPackClient.BuildStatus>? _wbStatusTask;
    private WorldPackClient.BuildStatus? _wbStatus;
    private double _wbStatusAt;
    private Task<long>? _wbDownloadTask;
    private Task<WorldPackCollisionSync.Result>? _wbCollisionTask;   // vmaps/mmaps after the patch
    private int? _wbMountedBuild;
    private bool _wbMountedRead;
    private bool _wbReservedLeft;

    // new pack form
    private readonly byte[] _wbNewKey = new byte[64];
    private readonly byte[] _wbNewName = new byte[96];

    private static string WbText(byte[] buf) => Encoding.UTF8.GetString(buf).TrimEnd('\0').Trim();

    // ═══════════════════════════════════════════════════════════════ sections

    private void RegisterCreatorWorldSections()
    {
        CreatorSection("World", "wb-pack", "World Content Pack", true, DrawWbPackSection);
        CreatorSection("World", "wb-sculpt", "Sculpt terrain", true, DrawWbSculptSection);
        CreatorSection("World", "wb-place", "Place buildings & doodads", true, DrawWbPlaceSection);
        CreatorSection("World", "wb-list", "Placements near you", false, DrawWbListSection);
        CreatorSection("World", "wb-publish", "Publish & download", true, DrawWbPublishSection);
        RegisterCreatorVerifySection();
        RegisterCreatorContentSections();
    }

    private string WorldBuilderStatus()
    {
        string pack = _wbState?.Packs.FirstOrDefault(p => p.Id == _wbPackId) is { } p
            ? $"{p.PackKey}{(p.Enabled ? "" : " (disabled)")}" : "no pack";
        string tool = _wbTool == WorldBuilderTool.None ? "" : $"  |  {_wbTool}";
        return $"{pack}{tool}{(_wbOps.Count > 0 ? $"  |  {_wbOps.Count} saving" : "")}";
    }

    private void DrawWbPackSection()
    {
        ImGui.TextWrapped("A content pack holds your world edits. New packs start as drafts. Enable a pack when you want the next publish to include it for everyone on this server.");
        if (SuiWebAppUrl.Length == 0)
        {
            ImGui.TextWrapped("Set the MangosSuperUI address first (login screen > Web App). " +
                              "World Builder edits are stored and audited by the web app.");
            return;
        }
        if (_wbState is null)
        {
            ImGui.TextDisabled(_wbStateTask is null ? "not loaded" : "loading packs...");
            if (CreatorButton("Load")) WbRequestState();
            return;
        }

        var packs = _wbState.Packs;
        string current = packs.FirstOrDefault(p => p.Id == _wbPackId)?.Name ?? "(choose a pack)";
        ImGui.SetNextItemWidth(CreatorControlWidth);
        if (ImGui.BeginCombo("Pack", current))
        {
            foreach (var p in packs)
                if (ImGui.Selectable($"{p.Name}{(p.Enabled ? "" : "  (draft only)")}##pack{p.Id}", p.Id == _wbPackId))
                {
                    WbSetTool(WorldBuilderTool.None);
                    _wbPackId = p.Id;
                    WbRequestState();
                    WbRequestDocs();
                }
            ImGui.EndCombo();
        }

        if (packs.FirstOrDefault(p => p.Id == _wbPackId) is { } sel)
        {
            bool enabled = sel.Enabled;
            if (ImGui.Checkbox("Included in publish (everyone gets it after the next publish)", ref enabled))
                WbOp(enabled ? $"enable {sel.PackKey}" : $"disable {sel.PackKey}",
                    _wbClient.SetEnabledAsync(SuiWebAppUrl, sel.Id, enabled));
            ImGui.TextDisabled($"{sel.Placements} placed objects; {sel.UndoableOps} edits available to undo");
            WbUndoButton();
            ImGui.SameLine();
            if (CreatorButton("Refresh")) WbRequestState();
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Create a new pack");
        ImGui.SetNextItemWidth(CreatorControlWidth);
        ImGui.InputText("Name##wb-newname", _wbNewName, (uint)_wbNewName.Length);
        if (WbText(_wbNewName).Length == 0) ImGui.TextDisabled("For example: My town changes");
        string name = WbText(_wbNewName);
        string generatedKey = System.Text.RegularExpressions.Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (generatedKey.Length > 64) generatedKey = generatedKey[..64];
        if (ImGui.TreeNode("Advanced: pack identifier"))
        {
            ImGui.SetNextItemWidth(CreatorControlWidth);
            ImGui.InputText("Identifier##wb-newkey", _wbNewKey, (uint)_wbNewKey.Length);
            ImGui.TextDisabled("Leave empty to use: " + generatedKey);
            ImGui.TreePop();
        }
        string key = WbText(_wbNewKey).Length > 0 ? WbText(_wbNewKey) : generatedKey;
        bool duplicate = packs.Any(p => p.PackKey.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (duplicate) ImGui.TextWrapped("A pack already uses this name. Choose another name or identifier.");
        ImGui.BeginDisabled(key.Length == 0 || name.Length == 0 || duplicate || _wbOps.Count > 0);
        if (CreatorButton("Create pack"))
        {
            var task = _wbClient.CreatePackAsync(SuiWebAppUrl, key, name, "created in Creator Mode");
            WbOp($"create pack {name}", task, r => { if (r.Pack is { } p) { _wbPackId = p.Id; WbOpenPage("terrain"); } });
        }
        ImGui.EndDisabled();
    }

    private void DrawWbSculptSection()
    {
        var step = new Vector4(1f, .82f, .3f, 1f);
        bool armed = _wbTool == WorldBuilderTool.Sculpt;
        string problem = WbSculptProblem();
        ImGui.TextWrapped("Raise hills, dig valleys, smooth bumps or level ground. Each drag is saved as one stroke you can undo.");
        ImGui.TextColored(step, "1. Choose a brush");
        int mode = (int)_wbBrush;
        ImGui.RadioButton("Raise", ref mode, 0); ObserveWorldBuilderUiItem("Raise"); ImGui.SameLine();
        ImGui.RadioButton("Lower", ref mode, 1);
        ObserveWorldBuilderUiItem("Lower");
        ImGui.RadioButton("Smooth", ref mode, 2); ObserveWorldBuilderUiItem("Smooth"); ImGui.SameLine();
        ImGui.RadioButton("Flatten", ref mode, 3);
        ObserveWorldBuilderUiItem("Flatten");
        _wbBrush = (WorldBuilderLaw.BrushMode)mode;
        if (_wbBrush == WorldBuilderLaw.BrushMode.Flatten)
        {
            ImGui.TextDisabled(_wbFlattenFixed is { } fz ? Inv($"Levels to height {fz:F1} (picked).") : "Levels to the height where each stroke starts.");
            WbPickButton("Pick the level height", "the height to level the ground to", at => _wbFlattenFixed = at.Z);
            if (_wbFlattenFixed is not null) { ImGui.SameLine(); if (CreatorButton("Use stroke start")) _wbFlattenFixed = null; }
        }
        ImGui.TextColored(step, "2. Size and feel");
        ImGui.SetNextItemWidth(CreatorControlWidth);
        ImGui.SliderFloat("Size (yd)", ref _wbRadius, 3f, 120f, "%.0f");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Brush radius. [ and ] change it while sculpting; the ring under the pointer shows it.");
        ImGui.SetNextItemWidth(CreatorControlWidth);
        ImGui.SliderFloat("Strength", ref _wbStrength, 0.5f, 40f, "%.1f");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(_wbBrush <= WorldBuilderLaw.BrushMode.Lower ? "How fast the ground rises or sinks while you hold the mouse (yards per second)." : "How quickly the ground smooths or levels.");
        ImGui.SetNextItemWidth(CreatorControlWidth);
        ImGui.SliderFloat("Hard edge", ref _wbHardness, 0f, 0.95f, "%.2f");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("0 = soft, rounded edge; higher = the full effect reaches closer to the ring (a sharp step).");
        ImGui.TextColored(step, "3. Drag on the ground");
        if (problem.Length > 0)
        {
            ImGui.TextWrapped(problem);
            if (problem.Contains("Download", StringComparison.Ordinal) && CreatorButton("Open Publish (step 3: Download)")) WbOpenPage("publish");
        }
        ImGui.BeginDisabled(!armed && problem.Length > 0);
        if (CreatorButton(armed ? "Stop sculpting (Esc)" : "Start sculpting"))
            WbSetTool(armed ? WorldBuilderTool.None : WorldBuilderTool.Sculpt);
        ImGui.EndDisabled();
        ImGui.SameLine();
        WbUndoButton();
        ImGui.TextWrapped(armed ? "Hold the left mouse button on the ground and move. Release saves the stroke; Escape while holding discards it. Shift swaps Raise and Lower."
            : "Start sculpting, then drag on the ground.");
        if (_wbToolHint.Length > 0) ImGui.TextWrapped(_wbToolHint);
    }

    private void DrawWbPlaceSection()
    {
        ImGui.TextWrapped("Find a building or prop, select it, then place its preview in the world. Escape cancels placing.");
        bool armed = _wbTool == WorldBuilderTool.Place;
        ImGui.BeginDisabled(_wbModel is null);
        if (CreatorButton(armed ? "Stop placing (Esc)" : "Place selected model"))
            WbSetTool(armed ? WorldBuilderTool.None : WorldBuilderTool.Place);
        ImGui.EndDisabled();
        if (_wbModel is not null) ImGui.TextWrapped(Path.GetFileNameWithoutExtension(_wbModel.Replace('\\', '/')));
        if (armed) ImGui.TextWrapped("Left-click the ground to place. [ and ] rotate; Shift makes smaller turns. Right mouse moves the camera.");

        _wbCatalogue ??= WbBuildCatalogue();
        ImGui.SetNextItemWidth(CreatorControlWidth);
        ImGui.Combo("Show##wb-kind", ref _wbModelKind, WbModelKinds, WbModelKinds.Length);
        ObserveWorldBuilderUiItem("Model kind");
        ImGui.SetNextItemWidth(CreatorControlWidth);
        ImGui.InputText("Search models##wb-search", _wbSearchBuf, (uint)_wbSearchBuf.Length);
        ObserveWorldBuilderUiItem("Model search");
        bool firstChip = true;
        foreach (var chip in _wbModelKind == 1 ? WbBuildingChips : WbPropChips)
        {
            float width = ImGui.CalcTextSize(chip).X + ImGui.GetStyle().FramePadding.X * 2f + ImGui.GetStyle().ItemSpacing.X;
            if (!firstChip && ImGui.GetItemRectMax().X + width < ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X) ImGui.SameLine();
            if (ImGui.SmallButton(chip)) WbSetBuf(_wbSearchBuf, chip);
            firstChip = false;
        }
        string q = WbText(_wbSearchBuf);
        if (q + "|" + _wbModelKind != _wbSearchLast)
        {
            _wbSearchLast = q + "|" + _wbModelKind;
            var words = q.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            _wbSearchHits = _wbCatalogue
                .Where(p => _wbModelKind == 0 || (_wbModelKind == 1) != WbModelIsM2(p))
                .Where(p => words.All(w => p.Contains(w, StringComparison.OrdinalIgnoreCase))).Take(300).ToList();
        }
        if (ImGui.BeginListBox("##wb-models", new Vector2(-1f, 150f * CreatorUiScale)))
        {
            foreach (var path in _wbSearchHits)
            {
                if (ImGui.Selectable($"{Path.GetFileNameWithoutExtension(path.Replace('\\', '/'))}##{path}", path == _wbModel))
                { _wbModel = path; _wbCursorGhostSig = null; }
                ObserveWorldBuilderUiItem(Path.GetFileNameWithoutExtension(path.Replace('\\', '/')));
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(path);
            }
            ImGui.EndListBox();
        }
        ImGui.TextDisabled($"{_wbSearchHits.Count} results shown of {_wbCatalogue.Count} models");
        ImGui.SetNextItemWidth(CreatorControlWidth);
        ImGui.SliderFloat("Heading", ref _wbYaw, 0f, 360f, "%.0f deg");
        ImGui.SetNextItemWidth(CreatorControlWidth);
        ImGui.SliderFloat("Tilt X", ref _wbPitch, -45f, 45f, "%.0f deg");
        ImGui.SetNextItemWidth(CreatorControlWidth);
        ImGui.SliderFloat("Tilt Z", ref _wbRoll, -45f, 45f, "%.0f deg");
        ImGui.SetNextItemWidth(CreatorControlWidth);
        ImGui.SliderFloat("Height offset", ref _wbZOffset, -30f, 30f, "%.1f yd");
        if (WbModelIsM2(_wbModel))
        {
            ImGui.SetNextItemWidth(CreatorControlWidth);
            ImGui.SliderFloat("Scale (M2)", ref _wbScale, 0.2f, 6f, "%.2f");
            ImGui.SetNextItemWidth(CreatorControlWidth);
            if (ImGui.SliderFloat("Random size +/-", ref _wbScatterScale, 0f, 0.5f, "%.2f")) WbRollScatter();
        }
        else if (ImGui.TreeNode("Advanced: furnishing set"))
        {
            ImGui.TextWrapped("Some buildings come with alternative interior furnishings, numbered from 0.");
            ImGui.SetNextItemWidth(CreatorControlWidth * 0.5f);
            ImGui.InputInt("Furnishing set", ref _wbDoodadSet);
            _wbDoodadSet = Math.Clamp(_wbDoodadSet, 0, 32);
            ImGui.TreePop();
        }
        if (ImGui.Checkbox("Random heading for each placement", ref _wbScatterYaw)) WbRollScatter();
        ObserveWorldBuilderUiItem("Random heading");
        if (CreatorButton("Reset rotation / height / scale"))
        { _wbYaw = _wbPitch = _wbRoll = _wbZOffset = 0f; _wbScale = 1f; _wbDoodadSet = 0; _wbScatterYaw = false; _wbScatterScale = 0f; WbRollScatter(); }
        if (_wbToolHint.Length > 0) ImGui.TextWrapped(_wbToolHint);
    }

    private void DrawWbListSection()
    {
        if (_wbState is null || _wbStateMap != _config.Start.Map || _controller is null) { ImGui.TextDisabled("Loading objects on this map..."); return; }
        ImGui.TextWrapped("Select a placed building or prop to move, turn, resize or remove it. Changes are saved to its content pack.");
        ImGui.Checkbox("Show objects from other packs", ref _wbAllPlacementPacks);
        Vector3 me = _controller.Position;
        var near = _wbState.Placements
            .Where(p => !p.Deleted && (_wbAllPlacementPacks || p.PackId == _wbPackId))
            .Select(p => (p, d: Vector2.Distance(new Vector2(p.PosX, p.PosY), new Vector2(me.X, me.Y))))
            .Where(x => x.d < 600f)
            .OrderBy(x => x.d)
            .Take(80)
            .ToList();
        bool select = _wbTool == WorldBuilderTool.Select && !_wbSpawnArmed;
        if (CreatorButton(select ? "Stop selecting (Esc)" : "Select an object in the world"))
            WbSetTool(select ? WorldBuilderTool.None : WorldBuilderTool.Select);
        WbUndoButton();
        if (near.Count == 0) ImGui.TextWrapped("No pack objects within 600 yards. Place a model first, or move closer to an existing placement.");
        ImGui.BeginChild("##wb-placed-list", new Vector2(0, 160f * CreatorUiScale), true);
        foreach (var (p, d) in near)
        {
            string other = p.PackId == _wbPackId ? "" : "  - " + (_wbState.Packs.FirstOrDefault(x => x.Id == p.PackId)?.Name ?? "other pack");
            string label = $"{Path.GetFileNameWithoutExtension(p.ModelPath)}  {d:F0} yd{other}{(p.Published ? "" : "  (draft)")}##placed{p.Id}";
            if (ImGui.Selectable(label, p.Id == _wbSelected)) WbSelectPlacement(p.Id);
            ObserveWorldBuilderUiItem($"Object {p.Id}");
        }
        ImGui.EndChild();
        DrawWbPlacementSwitchPrompt();

        if (_wbState.Placements.FirstOrDefault(p => p.Id == _wbSelected && !p.Deleted) is { } sel)
        {
            ImGui.Separator();
            ImGui.TextWrapped(Path.GetFileNameWithoutExtension(sel.ModelPath.Replace('\\', '/')));
            if (sel.PackId != _wbPackId)
            {
                ImGui.TextWrapped("This object belongs to another pack. Select that pack above to edit it.");
                return;
            }
            if (_wbPlacementDraft?.Id != sel.Id)
            { _wbPlacementDraft = WbCopyPlacement(sel); _wbPlacementDraftBase = WbSignature(sel); }
            var draft = _wbPlacementDraft;
            bool stale = _wbPlacementDraftBase != WbSignature(sel);
            if (stale) ImGui.TextWrapped("This object changed since these fields were loaded. Reset fields to load its current position before saving.");
            if (WbSignature(draft) != WbSignature(sel)) ImGui.TextColored(new Vector4(1f, .82f, .3f, 1f), "Unsaved changes are previewed in the world.");
            float heading = draft.RotY;
            ImGui.SetNextItemWidth(CreatorControlWidth);
            if (ImGui.SliderFloat("Heading##sel", ref heading, 0f, 360f, "%.0f deg")) draft.RotY = heading;
            float z = draft.PosZ;
            ImGui.SetNextItemWidth(CreatorControlWidth);
            if (ImGui.InputFloat("Height##sel", ref z, .1f, 1f)) draft.PosZ = z;
            if (ImGui.TreeNode("Exact position##sel"))
            {
                float x = draft.PosX, y = draft.PosY;
                ImGui.SetNextItemWidth(CreatorControlWidth);
                if (ImGui.InputFloat("North / south##sel", ref x, .25f, 2f, "%.2f") && float.IsFinite(x)) draft.PosX = x;
                ImGui.SetNextItemWidth(CreatorControlWidth);
                if (ImGui.InputFloat("West / east##sel", ref y, .25f, 2f, "%.2f") && float.IsFinite(y)) draft.PosY = y;
                ImGui.TreePop();
            }
            if (CreatorButton("Put on the ground##sel") && _terrain?.SampleHeight(draft.PosX, draft.PosY) is { } ground) draft.PosZ = ground;
            float tiltX = draft.RotX, tiltZ = draft.RotZ;
            ImGui.SetNextItemWidth(CreatorControlWidth);
            if (ImGui.SliderFloat("Tilt X##sel", ref tiltX, -45f, 45f, "%.0f deg")) draft.RotX = tiltX;
            ImGui.SetNextItemWidth(CreatorControlWidth);
            if (ImGui.SliderFloat("Tilt Z##sel", ref tiltZ, -45f, 45f, "%.0f deg")) draft.RotZ = tiltZ;
            if (draft.Kind == "m2")
            {
                float scale = draft.Scale;
                ImGui.SetNextItemWidth(CreatorControlWidth);
                if (ImGui.SliderFloat("Scale##sel", ref scale, .2f, 6f)) draft.Scale = scale;
            }
            ImGui.BeginDisabled(_wbOps.Count > 0 || stale);
            if (CreatorButton("Save object changes"))
                WbOp($"edit object #{sel.Id}", _wbClient.MoveAsync(SuiWebAppUrl, WbCopyPlacement(draft)), _ => _wbPlacementDraft = null);
            ImGui.EndDisabled();
            ImGui.SameLine();
            if (CreatorButton("Reset fields")) { _wbPlacementDraft = WbCopyPlacement(sel); _wbPlacementDraftBase = WbSignature(sel); }
            if (CreatorButton(_wbMoveArmed ? "Click the ground..." : "Move (click new spot)")) { _wbMoveArmed = true; WbSetTool(WorldBuilderTool.Select); }
            ImGui.SameLine();
            if (CreatorButton("Go to")) _controller.Teleport(sel.PosX, sel.PosY, sel.PosZ + 30f);
            ImGui.BeginDisabled(_wbOps.Count > 0);
            if (CreatorButton("Duplicate"))
            {
                // A copy two yards north, selected and armed to move, so it can be dropped where it belongs.
                var copy = WbCopyPlacement(draft);
                copy.Id = 0; copy.Published = false; copy.PosX += 2f;
                if (_terrain?.SampleHeight(copy.PosX, copy.PosY) is { } g && _terrain.SampleHeight(draft.PosX, draft.PosY) is { } g0)
                    copy.PosZ = draft.PosZ + (g - g0);
                WbOp($"duplicate object #{sel.Id}", _wbClient.PlaceAsync(SuiWebAppUrl, _wbPackId, copy), r =>
                {
                    if (r.Result?.Placement is not { } p) return;
                    _wbSelected = p.Id; _wbPlacementDraft = null; _wbMoveArmed = true; WbSetTool(WorldBuilderTool.Select);
                });
            }
            ImGui.SameLine();
            if (WbConfirmButton("Remove object", $"Remove {Path.GetFileNameWithoutExtension(sel.ModelPath.Replace('\\', '/'))} from the world? Undo brings it back."))
                WbOp($"remove {Path.GetFileNameWithoutExtension(sel.ModelPath.Replace('\\', '/'))}", _wbClient.DeleteAsync(SuiWebAppUrl, sel.Id));
            ImGui.EndDisabled();
            if (sel.Kind == "wmo")
            {
                string why = WbSculptProblem();
                ImGui.BeginDisabled(why.Length > 0 || _wbOps.Count > 0 || WbSignature(draft) != WbSignature(sel));
                if (CreatorButton("Level the ground under it"))
                {
                    int n = WbPadPlacement(sel.Id, 2f, 8f);
                    _wbMessage = n > 0 ? "Ground levelled under the building (one terrain stroke; Undo reverts it)."
                        : n == 0 ? "The ground under it is already level." : "Its terrain is not loaded here yet; move closer and try again.";
                }
                ImGui.EndDisabled();
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip(why.Length > 0 ? why : WbSignature(draft) != WbSignature(sel) ? "Save or reset the field changes first."
                        : "Flattens the terrain to the building's floor under its footprint, blending back to the slope around it.");
            }
        }
    }

    private void DrawWbPublishSection()
    {
        if (SuiWebAppUrl.Length == 0) return;
        ImGui.TextWrapped("Save your drafts, then publish the enabled packs to apply them to the game server. Download the finished build to update this client.");
        WbReadMountedBuild();
        var last = _wbStatus?.LastBuild ?? _wbState?.LastBuild;
        var enabled = _wbState?.Packs.Where(p => p.Enabled).ToList() ?? new();
        ImGui.TextWrapped("Will publish: " + (enabled.Count == 0 ? "no packs (puts the original world back)" : string.Join(", ", enabled.Select(p => p.Name))));
        if (_wbState?.Packs.FirstOrDefault(p => p.Id == _wbPackId) is { Enabled: false } selected)
        {
            ImGui.TextColored(new Vector4(1f, .72f, .3f, 1f), "Your selected pack is draft only and will not be published.");
            if (CreatorButton("Include this pack in publish"))
                WbOp($"enable {selected.Name}", _wbClient.SetEnabledAsync(SuiWebAppUrl, selected.Id, true));
        }
        ImGui.Separator();
        ImGui.TextWrapped(last is null ? "Nothing has been published on this server yet."
            : _wbMountedBuild == last.BuildId ? $"Your game has the latest published world (publish #{last.BuildId})."
            : $"The server has publish #{last.BuildId}; your game {(_wbMountedBuild is { } b ? $"still has #{b}" : "has the original world")}. Use step 3 to update it.");
        ImGui.Checkbox("Restart game server to load the changes", ref _wbRestartOnPublish);
        bool running = _wbStatus?.Running == true;
        string busy = running ? "A publish is already running." : _wbOps.Count > 0 ? "Wait for your edits to finish saving."
            : _wbStroking ? "Finish the terrain stroke first." : _wbVerifyTask is not null ? "A check is running." : "";
        ImGui.BeginDisabled(busy.Length > 0);
        if (CreatorButton("1. Check drafts")) WbRequestPreflight();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Checks the saved drafts of the included packs for problems before anything is built (seconds).");
        string what = enabled.Count == 0 ? "No pack is included, so this puts the ORIGINAL world back for everyone."
            : "This builds and installs: " + string.Join(", ", enabled.Select(p => p.Name)) + ".";
        if (WbConfirmButton("2. Publish", what + (_wbRestartOnPublish ? " The game server restarts (players are disconnected for a moment)." : "") +
                (_wbVerify is { } rep && WbNum(rep["errors"]) > 0 ? $" The last check found {rep["errors"]} error(s)." : "") + " It can take several minutes. Publish now?"))
            WbOp("publish", _wbClient.PublishAsync(SuiWebAppUrl, _wbRestartOnPublish), _ => _wbStatusAt = 0);
        ImGui.EndDisabled();
        if (busy.Length > 0) ImGui.TextDisabled(busy);
        if (_wbVerifyTask is not null) ImGui.TextDisabled("Checking content...");
        if (_wbVerify is { } report)
        {
            ImGui.TextWrapped($"Last check: {report["errors"]} errors, {report["warnings"]} warnings.");
            if (CreatorButton("View check results")) { _wbAdvancedPage = 2; WbOpenPage("advanced"); }
        }
        if (last is not null && last.BuildId != _wbMountedBuild && _wbDownloadTask is null && !running)
        {
            if (CreatorButton($"3. Download publish #{last.BuildId}")) WbStartDownload();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Updates this game's world files; the world around you reloads.");
        }
        else if (last is not null && last.BuildId == _wbMountedBuild) ImGui.TextDisabled("This client has the latest published build.");
        if (_wbDownloadTask is not null || _wbCollisionTask is not null) ImGui.TextDisabled("Downloading world and collision data...");

        if (_wbStatus is { } st && (st.Running || st.Log.Count > 0))
        {
            ImGui.TextWrapped($"build #{st.BuildId}: {st.Status} - {st.Phase}" +
                               (st.Error is { } e ? $" - {e}" : ""));
            if (running) ImGui.TextWrapped("You can keep looking around while this builds. Larger terrain changes can take several minutes.");
            ImGui.Checkbox("Show build details", ref _wbShowHistory);
            if (_wbShowHistory)
            {
                if (ImGui.BeginChild("##wb-log", new Vector2(-1f, 140f * CreatorUiScale), true))
                {
                    foreach (var line in st.Log) ImGui.TextUnformatted(line);
                    if (st.Running) ImGui.SetScrollHereY(1f);
                }
                ImGui.EndChild();
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════ per-frame

    /// <summary>Called from DrawCreatorHud every creator frame (ImGui is live).</summary>
    private void UpdateWorldBuilder()
    {
        bool panelOpen = _creatorPanel == CreatorPanel.World;
        if (!panelOpen && _wbTool != WorldBuilderTool.None) WbSetTool(WorldBuilderTool.None);
        // Preview readiness must not depend on opening the Publish section.
        WbReadMountedBuild();
        PumpWbTasks();
        if (_wbState is null && _wbStateTask is null && panelOpen && SuiWebAppUrl.Length > 0) WbRequestState();
        if (_wbState is not null && (_config.Start.Map != _wbStateMap || _wbStateIncludePackId != _wbPackId) && _wbStateTask is null)
            WbRequestState();

        WbKeepPreviewApplied();
        WbSyncGhosts();
        PumpWbDocs();
        PumpWbVerify();
        PumpWbQuestCheck();
        WbSyncSpawnPreview();
        WbSyncGameObjectPreviews();
        WbSyncNpcCursorGhost();
        WbDrawSavedPatrols();
        WbDrawMarkers();
        WbDrawConfirmDialog();
        WbUpdatePathPreview();
        if (_adts is not null) WbUpdateRoadPaintPreview();
        WbDrawPathOverlay();
        WbDrawSelectionOverlay();
        var io = ImGui.GetIO();
        bool overUi = io.WantCaptureMouse || _settingsOpen || _worldLoading;
        if (panelOpen && !_settingsOpen && !_worldLoading && !io.WantTextInput &&
            io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.Z, false)) WbUndo();
        if (WbNpcPointerFrame(io, overUi)) return;
        if (_wbTool == WorldBuilderTool.None) { _wbCursor = null; WbSetCursorGhost(null); return; }
        Vector3 hit = default;
        // Sculpt and paths edit the height field; placement may land on floors and roofs. Both reach ground far below a fly camera.
        bool picked = !overUi && (_wbTool is WorldBuilderTool.Sculpt or WorldBuilderTool.Path
            ? WbTryPickSculptGround(io.MousePos, out hit)
            : WbPickGround(io.MousePos, false, out hit));
        if (picked && _wbSnapGrid && _wbTool is WorldBuilderTool.Path or WorldBuilderTool.Place or WorldBuilderTool.GameObject) hit = WbSnapToGrid(hit);
        _wbCursor = picked ? hit : null;
        _wbToolHint = overUi ? "Move the pointer over the world."
            : _wbCursor is null ? "Point at nearby ground; move closer if it is beyond reach." : "";

        if (!io.WantTextInput)
        {
            float step = io.KeyShift ? 1f : 15f;
            if (_wbTool == WorldBuilderTool.Sculpt)
            {
                if (ImGui.IsKeyPressed(ImGuiKey.LeftBracket)) _wbRadius = MathF.Max(3f, _wbRadius / 1.15f);
                if (ImGui.IsKeyPressed(ImGuiKey.RightBracket)) _wbRadius = MathF.Min(120f, _wbRadius * 1.15f);
            }
            else if (_wbTool == WorldBuilderTool.Place)
            {
                if (ImGui.IsKeyPressed(ImGuiKey.LeftBracket)) _wbYaw = (_wbYaw - step + 360f) % 360f;
                if (ImGui.IsKeyPressed(ImGuiKey.RightBracket)) _wbYaw = (_wbYaw + step) % 360f;
            }
            else if (_wbSpawnArmed && _wbSpawnMode == 0)
            {
                if (ImGui.IsKeyPressed(ImGuiKey.LeftBracket)) _wbSpawnYaw = (_wbSpawnYaw - step + 360f) % 360f;
                if (ImGui.IsKeyPressed(ImGuiKey.RightBracket)) _wbSpawnYaw = (_wbSpawnYaw + step) % 360f;
            }
            else if (_wbTool == WorldBuilderTool.GameObject)
            {
                if (ImGui.IsKeyPressed(ImGuiKey.LeftBracket)) _wbGoYaw = (_wbGoYaw - step + 360f) % 360f;
                if (ImGui.IsKeyPressed(ImGuiKey.RightBracket)) _wbGoYaw = (_wbGoYaw + step) % 360f;
            }
            else if (_wbTool == WorldBuilderTool.Path)
            {
                if (ImGui.IsKeyPressed(ImGuiKey.LeftBracket)) _wbCarveWidth = MathF.Max(2f, _wbCarveWidth - 1f);
                if (ImGui.IsKeyPressed(ImGuiKey.RightBracket)) _wbCarveWidth = MathF.Min(60f, _wbCarveWidth + 1f);
            }
        }

        switch (_wbTool)
        {
            case WorldBuilderTool.Sculpt: WbSculptFrame(io, overUi); break;
            case WorldBuilderTool.Place: WbPlaceFrame(io, overUi); break;
            case WorldBuilderTool.Select: WbSelectFrame(io, overUi); break;
            case WorldBuilderTool.Path: WbPathFrame(io, overUi); break;
            case WorldBuilderTool.GameObject: WbGameObjectFrame(io, overUi); break;
            case WorldBuilderTool.Pick: WbPickFrame(io, overUi); break;
        }
        WbDrawGroundGrid();
        WbDrawCursorOverlay();
    }

    private void WbSetTool(WorldBuilderTool tool)
    {
        if (_wbStroking) WbFinishStroke();
        _wbTool = tool;
        if (tool != WorldBuilderTool.Select) _wbMoveArmed = false;
        if (tool != WorldBuilderTool.Select) WbCancelNpcPlacement();
        bool reserve = tool != WorldBuilderTool.None;
        if (reserve && !_wbReservedLeft) { _window.LeftButtonReservedForWorldClicks = true; _wbReservedLeft = true; }
        else if (!reserve && _wbReservedLeft) { _window.LeftButtonReservedForWorldClicks = false; _wbReservedLeft = false; }
        if (tool != WorldBuilderTool.Place) WbSetCursorGhost(null);
        if (tool != WorldBuilderTool.GameObject) { WbSetGameObjectCursor(null); _wbGoMoveArmed = _wbGoPicking = false; }
        if (tool != WorldBuilderTool.Path) _wbPathDragging = false;
        if (tool != WorldBuilderTool.Pick) _wbPickDone = null;
    }

    /// <summary>Editing actions own Escape before gameplay menus. A stroke in hand is discarded, not saved.</summary>
    private bool ConsumeWorldBuilderEscape()
    {
        if (!_creatorWorldRequested ||
            (_wbTool == WorldBuilderTool.None && !_wbStroking && !WbNpcHasActiveAction)) return false;
        WbCancelStroke();
        WbCancelNpcPlacement();
        WbSetTool(WorldBuilderTool.None);
        _wbCursor = null;
        _wbToolHint = "";
        _wbMessage = "Editing action cancelled. Saved changes are kept.";
        return true;
    }

    private string WbSculptProblem()
    {
        if (SuiWebAppUrl.Length == 0) return "Connect to the web app before sculpting.";
        if (_wbPackId == 0) return "Choose or create a content pack first.";
        if (_wbState is null || _wbStateMap != _config.Start.Map || _wbStateIncludePackId != _wbPackId)
            return "Loading terrain editing data…";
        if (!_wbState.SurfaceSculptSupported) return "Update the web app before sculpting; it does not support final terrain edits yet.";
        if (_wbSculptSaveTask is not null || _wbSculptAwaitingState || _wbOps.Count > 0)
            return "Saving changes; wait before starting another stroke.";
        if (_wbCursor is { } at && WbStockFor(WorldBuilderLaw.TileOf(at.X, at.Y)) is null)
            return _wbState.LastBuild?.BuildId != _wbMountedBuild
                ? "Download the latest build before sculpting this terrain."
                : "Terrain data is not ready here. Wait for it to load or choose another spot.";
        return "";
    }

    private void WbCancelStroke()
    {
        _wbStroking = false;
        var changed = _wbStroke.Keys.ToArray();
        _wbStroke.Clear();
        foreach (var key in changed)
        {
            _wbDirtyTiles.Add(key);
            WbApplyTilePreview(key);
        }
    }

    /// <summary>World clicks are swallowed while a World Builder tool is armed (no stray targeting).</summary>
    private bool HandleWorldBuilderClick(WorldMouseClick click) =>
        _creatorWorldRequested && _wbTool != WorldBuilderTool.None;

    // ── sculpt ───────────────────────────────────────────────────────────────

    /// <summary>The terrain brush edits the height field, even below props or a roof.
    /// Placement keeps the ordinary nearest-surface pick so objects can still sit on floors.</summary>
    private bool WbTryPickSculptGround(Vector2 pixel, out Vector3 point)
    {
        point = default;
        return _window.Camera.ScreenPointToRay(pixel, _window.FramebufferSize) is { } ray &&
            TryPickTerrainSurface(ray.Origin, ray.Direction, WbPickReach, out point, out _);
    }

    private void WbSculptFrame(ImGuiIOPtr io, bool overUi)
    {
        double now = ImGui.GetTime();
        if (_wbStroking && (_wbStrokeMap != _config.Start.Map || _wbStrokePackId != _wbPackId))
        {
            if (_wbStrokeMap == _config.Start.Map) WbCancelStroke();
            else { _wbStroking = false; _wbStroke.Clear(); }
            _wbMessage = "Stroke cancelled because the map or pack changed.";
        }
        if (_wbStroking && !ImGui.IsMouseDown(ImGuiMouseButton.Left)) WbFinishStroke();
        string problem = WbSculptProblem();
        if (!_wbStroking && problem.Length > 0) _wbToolHint = problem;
        if (!_wbStroking && !overUi && _wbCursor is { } start && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            if (problem.Length > 0) { _wbMessage = problem; return; }
            _wbStroking = true;
            _wbStroke.Clear();
            _wbStrokeMap = _config.Start.Map;
            _wbStrokePackId = _wbPackId;
            _wbFlattenTarget = _wbFlattenFixed ?? start.Z;
            _wbLastDab = now - Math.Clamp(io.DeltaTime, 1f / 120f, 0.1f);
            _wbMessage = "Sculpting… Release to save, Escape to discard this stroke.";
        }
        if (_wbStroking && _wbCursor is { } at)
        {
            float dt = (float)Math.Clamp(now - _wbLastDab, 0.0, 0.1);
            _wbLastDab = now;
            var mode = _wbBrush;
            if (io.KeyShift && mode == WorldBuilderLaw.BrushMode.Raise) mode = WorldBuilderLaw.BrushMode.Lower;
            else if (io.KeyShift && mode == WorldBuilderLaw.BrushMode.Lower) mode = WorldBuilderLaw.BrushMode.Raise;
            float amount = mode <= WorldBuilderLaw.BrushMode.Lower ? _wbStrength * dt : Math.Clamp(_wbStrength * 0.15f * dt * 10f, 0f, 1f);
            WbDab(new Vector2(at.X, at.Y), mode, amount);
        }
        else _wbLastDab = now; // Moving across a panel must not accumulate a delayed dab.
        if (_wbDirtyTiles.Count > 0 && now - _wbLastRebuild > 0.12)
        {
            _wbLastRebuild = now;
            foreach (var key in _wbDirtyTiles.ToArray()) WbApplyTilePreview(key);
            _wbDirtyTiles.Clear();
        }
    }

    private void WbDab(Vector2 centre, WorldBuilderLaw.BrushMode mode, float amount)
    {
        if (_wbStateMap != _config.Start.Map || _wbStateIncludePackId != _wbPackId)
        {
            _wbToolHint = _wbMessage = "Wait for this pack's terrain editing data to load.";
            return;
        }
        if (_wbState?.SurfaceSculptSupported != true)
        {
            _wbToolHint = _wbMessage = "Update the web app before sculpting; it does not support final terrain edits yet.";
            return;
        }
        var verts = WorldBuilderLaw.BrushVertices(centre, _wbRadius, _wbHardness);
        var neighbours = mode == WorldBuilderLaw.BrushMode.Smooth
            ? verts.ToDictionary(v => (v.col, v.row, v.gridRow, v.gridCol),
                v => WorldBuilderLaw.NeighbourVertices(v.col, v.row, v.gridRow, v.gridCol))
            : null;
        var required = verts.Select(v => (v.col, v.row));
        if (neighbours is not null) required = required.Concat(neighbours.Values.SelectMany(n => n).Select(v => (v.col, v.row)));
        if (required.Distinct().Any(key => WbStockFor(key) is null))
        {
            _wbToolHint = _wbMessage = "The brush reaches terrain that is not loaded. Move closer or use a smaller radius.";
            return; // Never deform only one side of a shared tile edge.
        }
        var shared = new Dictionary<(int col, int row), float>();
        foreach (var (col, row, gr, gc, w) in verts)
        {
            if (WbStockFor((col, row)) is null) continue;
            if (!_wbStroke.TryGetValue((col, row), out var stroke))
                _wbStroke[(col, row)] = stroke = new float[WorldBuilderLaw.VertexCount];
            float h = WbHeight(col, row, gr, gc);
            float mean = 0f;
            if (neighbours is not null)
                mean = neighbours[(col, row, gr, gc)].Sum(v => WbHeight(v.col, v.row, v.gridRow, v.gridCol)) * 0.25f;
            var point = (col * 128 + gc, row * 128 + gr);
            if (!shared.TryGetValue(point, out float delta))
                shared[point] = delta = WorldBuilderLaw.Dab(mode, w, amount, h, mean, _wbFlattenTarget);
            stroke[gr * WorldBuilderLaw.Side + gc] += delta;
            _wbDirtyTiles.Add((col, row));
        }
    }

    /// <summary>Absolute outer-vertex height the brush sees: stock + server sum + stroke.</summary>
    private float WbHeight(int col, int row, int gr, int gc)
    {
        var stock = WbStockFor((col, row));
        if (stock is null) return 0f;
        int ix = Math.Min(gc / 8, 15), iy = Math.Min(gr / 8, 15);
        if (!stock.Chunks.TryGetValue((ix, iy), out var ch)) return 0f;
        int r = gr - iy * 8, c = gc - ix * 8;
        int g = gr * WorldBuilderLaw.Side + gc;
        return ch.BaseZ + ch.Heights[r * 17 + c]
               + (_wbServerSculpt.TryGetValue((col, row), out var s) ? s[g] : 0f)
               + (_wbStroke.TryGetValue((col, row), out var k) ? k[g] : 0f);
    }

    private void WbFinishStroke(string? label = null)
    {
        if (_wbState?.SurfaceSculptSupported != true)
        {
            WbCancelStroke();
            _wbMessage = "Stroke cancelled: the web app must support final terrain edits before saving.";
            return;
        }
        if (_wbStroking && (_wbStrokePackId != _wbPackId || _wbStrokeMap != _config.Start.Map))
        {
            if (_wbStrokeMap == _config.Start.Map) WbCancelStroke();
            else { _wbStroking = false; _wbStroke.Clear(); }
            _wbMessage = "Stroke cancelled because the map or pack changed.";
            return;
        }
        _wbStroking = false;
        if (_wbStroke.Count == 0) return;
        var tiles = new List<WorldPackClient.SculptTile>();
        foreach (var ((col, row), deltas) in _wbStroke)
        {
            var t = new WorldPackClient.SculptTile { Col = col, Row = row };
            for (int i = 0; i < deltas.Length; i++)
                if (WorldBuilderLaw.SavesDelta(deltas[i])) t.Deltas[i] = deltas[i];
            if (t.Deltas.Count > 0) tiles.Add(t);
        }
        var touched = _wbStroke.Keys.ToArray();
        _wbStroke.Clear();
        _wbPendingSculpt.Clear();
        // Mirror only the exact values sent, so tiny discarded deltas never linger in the preview.
        foreach (var t in tiles)
        {
            var key = (t.Col, t.Row);
            var deltas = new float[WorldBuilderLaw.VertexCount];
            foreach (var (i, d) in t.Deltas) deltas[i] = d;
            _wbPendingSculpt[key] = deltas;
            if (!_wbServerSculpt.TryGetValue(key, out var s))
                _wbServerSculpt[key] = s = new float[WorldBuilderLaw.VertexCount];
            WorldBuilderLaw.ApplyStroke(s, deltas);
            _wbPreviewTiles.Add(key);
        }
        foreach (var key in touched) { _wbDirtyTiles.Add(key); WbApplyTilePreview(key); }
        if (tiles.Count == 0) return;
        int n = tiles.Sum(t => t.Deltas.Count);
        _wbSculptSaveTask = _wbClient.SculptAsync(SuiWebAppUrl, _wbPackId, _config.Start.Map, label ?? $"{_wbBrush} r{_wbRadius:F0}", tiles);
        _wbSculptAwaitingState = true;
        _wbStateTask = null; // A snapshot started before this stroke cannot confirm its save.
        WbOp(label is null ? $"sculpt ({_wbBrush}, {n} vertices)" : $"{label} ({n} vertices)",
            _wbSculptSaveTask);
    }

    // ── terrain preview ──────────────────────────────────────────────────────

    private HashSet<(int, int)>? _wbPublishedStampCache;
    private uint _wbPublishedStampMap = uint.MaxValue;

    /// <summary>Published stamp ownership selects the sculpt preview's base terrain, never map artwork.</summary>
    private HashSet<(int, int)> WbPublishedStampedTiles(uint mapId)
    {
        if (_wbPublishedStampCache is not null && _wbPublishedStampMap == mapId) return _wbPublishedStampCache;
        var set = new HashSet<(int, int)>();
        try
        {
            if (_mpq?.ReadFile(@"WorldPacks\build.json") is { } bytes)
            {
                using var manifest = System.Text.Json.JsonDocument.Parse(bytes);
                if (manifest.RootElement.TryGetProperty("stamps", out var stamps))
                    foreach (var e in stamps.EnumerateArray())
                        if (e.GetString()?.Split(':', '_') is [var m, var c, var r] && uint.TryParse(m, out uint mm) && mm == mapId &&
                            int.TryParse(c, out int col) && int.TryParse(r, out int row))
                            set.Add((col, row));
            }
        }
        catch { }
        _wbPublishedStampMap = mapId;
        return _wbPublishedStampCache = set;
    }

    private WbStockTile? WbStockFor((int col, int row) key)
    {
        if (_wbStateMap != _config.Start.Map) return null;
        if (_wbStock.TryGetValue(key, out var cached) && cached is not null) return cached;
        WbStockTile? tile = null;
        if (_mpq is not null)
        {
            string path = $"World\\Maps\\{_config.Start.MapName}\\{_config.Start.MapName}_{key.col}_{key.row}.adt";
            // Every published tile may contain absolute path/coast shaping, even without a stamp.
            // Use that finished surface minus the published aggregate. Adding current totals then changes
            // only pending surface edits while the fixed legacy source contribution cancels exactly.
            bool packStamp = WbTileDoc(_config.Start.Map, key.col, key.row) is not null ||
                             WbPublishedStampedTiles((uint)_config.Start.Map).Contains(key);
            if (!packStamp && _wbDocs is null && _config.Start.Map < 800) return null;
            float[]? published = null;
            bool mountedIsLatest = _wbMountedBuild is { } mb && mb == _wbState?.LastBuild?.BuildId;
            if ((_wbState?.LastBuild is not null || _wbMountedBuild is not null) && !mountedIsLatest) return null;
            var bytes = mountedIsLatest ? _mpq.ReadFile(path) : packStamp ? null : _mpq.ReadFileExcluding(path, WbPatchName);
            if (bytes is not null && mountedIsLatest)
            {
                published = new float[WorldBuilderLaw.VertexCount];
                foreach (var t in _wbState?.PublishedSculpt.Where(t => t.Col == key.col && t.Row == key.row) ?? [])
                    foreach (var (i, dz) in t.Deltas)
                        if ((uint)i < (uint)published.Length) published[i] += dz;
            }
            var adt = bytes is null ? null : AdtTerrainReader.Parse(bytes, key.row, key.col);
            if (adt?.Chunks is { } chunks)
            {
                var map = new Dictionary<(int x, int y), (float[] Heights, sbyte[]? Normals, float BaseZ)>();
                foreach (var c in chunks)
                    if (c?.Heights is { } h)
                    {
                        var heights = (float[])h.Clone();
                        if (published is not null)
                        {
                            // Undo exactly what AdtDocument.ApplySculpt added: outer vertices their own delta,
                            // inner vertices the mean of their four corners.
                            for (int r = 0; r <= 8; r++)
                                for (int cc = 0; cc <= 8; cc++)
                                    heights[r * 17 + cc] -= published[(c.IndexY * 8 + r) * 129 + c.IndexX * 8 + cc];
                            for (int r = 0; r < 8; r++)
                                for (int cc = 0; cc < 8; cc++)
                                {
                                    int g = (c.IndexY * 8 + r) * 129 + c.IndexX * 8 + cc;
                                    heights[r * 17 + 9 + cc] -= (published[g] + published[g + 1] + published[g + 129] + published[g + 130]) * 0.25f;
                                }
                        }
                        map[(c.IndexX, c.IndexY)] = (heights, (sbyte[]?)c.Normals?.Clone(), c.BaseZ);
                    }
                tile = new WbStockTile { Chunks = map };
            }
        }
        // Missing docs, a pending download or archive mount is transient; do not poison the cache.
        if (tile is not null) _wbStock[key] = tile;
        return tile;
    }

    /// <summary>Rewrite the shared AdtCache instance of a tile to stock + deltas and rebuild its mesh.</summary>
    private void WbApplyTilePreview((int col, int row) key)
    {
        if (_adts is null || _terrain is null || _wbStateMap != _config.Start.Map) return;
        var stock = WbStockFor(key);
        var adt = _adts.Get(key.col, key.row);
        if (stock is null || adt?.Chunks is null) return;
        var total = new float[WorldBuilderLaw.VertexCount];
        bool any = false;
        if (_wbServerSculpt.TryGetValue(key, out var s)) { for (int i = 0; i < total.Length; i++) total[i] += s[i]; any = true; }
        if (_wbStroke.TryGetValue(key, out var k)) { for (int i = 0; i < total.Length; i++) total[i] += k[i]; any = true; }
        if (_wbPathPreview.TryGetValue(key, out var path)) { for (int i = 0; i < total.Length; i++) total[i] += path[i]; any = true; }

        // Absolute outer grid for normals.
        var outer = new float[WorldBuilderLaw.VertexCount];
        foreach (var c in adt.Chunks)
        {
            if (c?.Heights is null || !stock.Chunks.TryGetValue((c.IndexX, c.IndexY), out var st)) continue;
            for (int r = 0; r <= 8; r++)
                for (int cc = 0; cc <= 8; cc++)
                {
                    int g = (c.IndexY * 8 + r) * WorldBuilderLaw.Side + c.IndexX * 8 + cc;
                    outer[g] = c.BaseZ + st.Heights[r * 17 + cc] + total[g];
                }
        }

        foreach (var c in adt.Chunks)
        {
            if (c?.Heights is null || !stock.Chunks.TryGetValue((c.IndexX, c.IndexY), out var st)) continue;
            bool touched = false;
            for (int r = 0; r <= 8; r++)
                for (int cc = 0; cc <= 8; cc++)
                {
                    float d = total[(c.IndexY * 8 + r) * WorldBuilderLaw.Side + c.IndexX * 8 + cc];
                    c.Heights[r * 17 + cc] = st.Heights[r * 17 + cc] + d;
                    if (d != 0f) touched = true;
                }
            for (int r = 0; r < 8; r++)
                for (int cc = 0; cc < 8; cc++)
                {
                    float d = WorldBuilderLaw.InnerDelta(total, c.IndexY * 8 + r, c.IndexX * 8 + cc);
                    c.Heights[r * 17 + 9 + cc] = st.Heights[r * 17 + 9 + cc] + d;
                }
            if (c.Normals is null) continue;
            if (!touched && st.Normals is { } sn) { Array.Copy(sn, c.Normals, Math.Min(sn.Length, c.Normals.Length)); continue; }
            for (int r = 0; r <= 8; r++)
                for (int cc = 0; cc <= 8; cc++)
                {
                    int gr = c.IndexY * 8 + r, gc = c.IndexX * 8 + cc;
                    int r0 = Math.Max(gr - 1, 0), r1 = Math.Min(gr + 1, 128), c0 = Math.Max(gc - 1, 0), c1 = Math.Min(gc + 1, 128);
                    float dRow = (outer[r1 * 129 + gc] - outer[r0 * 129 + gc]) / ((r1 - r0) * WorldBuilderLaw.Unit);
                    float dCol = (outer[gr * 129 + c1] - outer[gr * 129 + c0]) / ((c1 - c0) * WorldBuilderLaw.Unit);
                    WbWriteNormal(c.Normals, r * 17 + cc, dRow, dCol);
                }
            for (int r = 0; r < 8; r++)
                for (int cc = 0; cc < 8; cc++)
                {
                    int gr = c.IndexY * 8 + r, gc = c.IndexX * 8 + cc;
                    float dRow = ((outer[(gr + 1) * 129 + gc] + outer[(gr + 1) * 129 + gc + 1]) - (outer[gr * 129 + gc] + outer[gr * 129 + gc + 1])) * 0.5f / WorldBuilderLaw.Unit;
                    float dCol = ((outer[gr * 129 + gc + 1] + outer[(gr + 1) * 129 + gc + 1]) - (outer[gr * 129 + gc] + outer[(gr + 1) * 129 + gc])) * 0.5f / WorldBuilderLaw.Unit;
                    WbWriteNormal(c.Normals, r * 17 + 9 + cc, dRow, dCol);
                }
        }
        if (any) _wbPreviewTiles.Add(key);
        _wbAppliedTo[key] = adt;
        _terrain.RebuildTile(key, adt);
    }

    private static void WbWriteNormal(sbyte[] normals, int vertex, float dRow, float dCol)
    {
        var (b0, b1, b2) = WorldBuilderLaw.EncodeNormal(dRow, dCol);
        if (vertex * 3 + 2 >= normals.Length) return;
        normals[vertex * 3] = b0; normals[vertex * 3 + 1] = b1; normals[vertex * 3 + 2] = b2;
    }

    /// <summary>A preview tile that streamed out and back in comes back as a fresh cache instance:
    /// re-apply. Also re-applies tiles whose server deltas changed on a state refresh.</summary>
    private void WbKeepPreviewApplied()
    {
        if (_adts is null || _terrain is null) return;
        foreach (var key in _wbPreviewTiles)
        {
            if (!_terrain.IsResident(key)) continue;
            if (!_adts.TryPeek(key.col, key.row, out var adt) || adt is null) continue;
            if (!_wbAppliedTo.TryGetValue(key, out var applied) || !ReferenceEquals(applied, adt))
                _wbDirtyTiles.Add(key);
        }
        if (!_wbStroking && _wbDirtyTiles.Count > 0)
        {
            foreach (var key in _wbDirtyTiles.ToArray()) WbApplyTilePreview(key);
            _wbDirtyTiles.Clear();
        }
    }

    // ── placement ────────────────────────────────────────────────────────────

    private static bool WbModelIsM2(string? path) =>
        path is not null && (path.EndsWith(".m2", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".mdx", StringComparison.OrdinalIgnoreCase));

    private List<string> WbBuildCatalogue()
    {
        if (_mpq is null) return new();
        var list = new List<string>();
        foreach (var name in _mpq.ListedFiles())
        {
            if (name.EndsWith(".wmo", StringComparison.OrdinalIgnoreCase))
            {
                // Skip group files (Name_000.wmo).
                string stem = Path.GetFileNameWithoutExtension(name);
                int us = stem.LastIndexOf('_');
                if (us > 0 && stem.Length - us == 4 && stem[(us + 1)..].All(char.IsDigit)) continue;
                list.Add(name);
            }
            else if (name.EndsWith(".m2", StringComparison.OrdinalIgnoreCase) &&
                     name.StartsWith("World\\", StringComparison.OrdinalIgnoreCase))
                list.Add(name);
        }
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    private void WbPlaceFrame(ImGuiIOPtr io, bool overUi)
    {
        if (_wbModel is null) { _wbToolHint = "Choose a building or object to place."; WbSetCursorGhost(null); return; }
        if (_wbCursor is not { } at) { WbSetCursorGhost(null); return; }
        var world = at + new Vector3(0, 0, _wbZOffset);
        var candidate = new WorldPackClient.Placement
        {
            MapId = _config.Start.Map,
            Kind = WbModelIsM2(_wbModel) ? "m2" : "wmo",
            ModelPath = _wbModel,
            PosX = world.X, PosY = world.Y, PosZ = world.Z,
            RotX = _wbPitch, RotY = (_wbYaw + _wbYawRoll) % 360f, RotZ = _wbRoll,
            Scale = WbModelIsM2(_wbModel) ? Math.Clamp(_wbScale * _wbScaleRoll, 0.05f, 10f) : 1f,
            DoodadSet = _wbDoodadSet,
        };
        WbSetCursorGhost(candidate);
        if (_wbCursorGhostSig is null) _wbToolHint = "Loading the model preview…";
        if (!overUi && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            if (_wbPackId == 0) { _wbMessage = "Choose or create a pack first."; return; }
            // Several placements may save at once (scattering props); other edits still wait for them.
            if (_wbOps.Count >= 4 || _wbSculptSaveTask is not null) { _wbMessage = "Saving the previous objects; click again in a moment."; return; }
            if (_wbCursorGhostSig is null) { _wbMessage = "Wait for the model preview before placing it."; return; }
            WbOp($"place {Path.GetFileName(_wbModel)}", _wbClient.PlaceAsync(SuiWebAppUrl, _wbPackId, candidate),
                r => { if (r.Result?.Placement is { } p) _wbSelected = p.Id; });
            WbRollScatter();
        }
    }

    private void WbSelectFrame(ImGuiIOPtr io, bool overUi)
    {
        if (overUi || _wbCursor is not { } at || _wbState is null) { WbSetCursorGhost(null); return; }
        if (_wbMoveArmed && !_wbState.Placements.Any(p => p.Id == _wbSelected && !p.Deleted && p.PackId == _wbPackId && p.MapId == _config.Start.Map))
        {
            // A just-saved copy (Duplicate) arrives with the next state refresh: wait for it instead of cancelling.
            if (_wbStateTask is not null || _wbOps.Count > 0) { _wbToolHint = "Loading the new object..."; return; }
            _wbMoveArmed = false;
            WbSetCursorGhost(null);
            _wbMessage = "Move cancelled: select an object owned by the current pack.";
            return;
        }
        if (_wbMoveArmed && _wbState.Placements.FirstOrDefault(p => p.Id == _wbSelected && !p.Deleted && p.PackId == _wbPackId && p.MapId == _config.Start.Map) is { } sel)
        {
            // Never mutate the saved placement before an accepted request; Escape/failure leaves it intact.
            var moved = WbCopyPlacement(sel);
            float lift = sel.PosZ - (_terrain?.SampleHeight(sel.PosX, sel.PosY) ?? sel.PosZ);   // keep its height above the ground
            moved.PosX = at.X; moved.PosY = at.Y; moved.PosZ = at.Z + (MathF.Abs(lift) < 30f ? lift : 0f);
            WbSetCursorGhost(moved);
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left) && (_wbOps.Count > 0 || _wbCursorGhostSig is null))
                _wbMessage = _wbOps.Count > 0 ? "Saving the previous change; click again in a moment." : "Loading the preview; click again in a moment.";
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left) && _wbOps.Count == 0 && _wbCursorGhostSig is not null)
            {
                _wbMoveArmed = false;
                WbSetCursorGhost(null);
                WbOp($"move #{sel.Id}", _wbClient.MoveAsync(SuiWebAppUrl, moved), _ => _wbPlacementDraft = null);
            }
            return;
        }
        WbSetCursorGhost(null);
        if (!ImGui.IsMouseClicked(ImGuiMouseButton.Left)) return;
        if (_wbOps.Count > 0) { _wbMessage = "Saving the previous change; click again in a moment."; return; }
        if (WbTrySpawnClick(at)) return;
        var nearest = _wbState.Placements.Where(p => !p.Deleted && p.PackId == _wbPackId)
            .OrderBy(p => Vector3.Distance(new Vector3(p.PosX, p.PosY, p.PosZ), at))
            .FirstOrDefault();
        if (nearest is not null && Vector3.Distance(new Vector3(nearest.PosX, nearest.PosY, nearest.PosZ), at) < 40f)
            WbSelectPlacement(nearest.Id);
        else _wbMessage = "No object from this pack nearby. Choose it in the object list.";
    }

    private static string WbSignature(WorldPackClient.Placement p) =>
        $"{p.ModelPath}|{p.PosX:F2}|{p.PosY:F2}|{p.PosZ:F2}|{p.RotX:F1}|{p.RotY:F1}|{p.RotZ:F1}|{p.Scale:F2}";

    private static WorldPackClient.Placement WbCopyPlacement(WorldPackClient.Placement source) => new()
    {
        Id = source.Id, PackId = source.PackId, MapId = source.MapId, Kind = source.Kind, ModelPath = source.ModelPath,
        PosX = source.PosX, PosY = source.PosY, PosZ = source.PosZ,
        RotX = source.RotX, RotY = source.RotY, RotZ = source.RotZ, Scale = source.Scale, DoodadSet = source.DoodadSet,
        Deleted = source.Deleted, Published = source.Published,
    };

    private static Matrix4x4 WbTransform(WorldPackClient.Placement p)
    {
        var pos = WorldBuilderLaw.WorldToPlacement(new Vector3(p.PosX, p.PosY, p.PosZ));
        var rot = new Vector3(p.RotX, p.RotY, p.RotZ);
        return p.Kind == "m2" ? DoodadRenderer.MddfTransform(pos, rot, p.Scale) : WmoRenderer.ModfTransform(pos, rot);
    }

    /// <summary>true when the ghost is (now) on screen.</summary>
    private bool WbAddGhost(ulong key, WorldPackClient.Placement p)
    {
        var t = WbTransform(p);
        if (p.Kind == "m2")
        {
            string m2 = Path.ChangeExtension(p.ModelPath, ".m2");
            return _doodads?.AddDynamic(key, m2, t) == DoodadRenderer.DynamicPlacement.Placed;
        }
        return _wmo?.AddDynamic(key, p.ModelPath, t) == WmoRenderer.DynamicPlacement.Placed;
    }

    private void WbRemoveGhost(ulong key)
    {
        _wmo?.RemoveDynamic(key);
        _doodads?.RemoveDynamic(key);
    }

    private void WbSetCursorGhost(WorldPackClient.Placement? p)
    {
        if (p is null)
        {
            WbRemoveGhost(WbCursorGhostKey);
            _wbCursorGhostSig = null;
            return;
        }
        string sig = WbSignature(p);
        if (sig == _wbCursorGhostSig) return;
        bool sameModel = _wbCursorGhostSig is not null && _wbCursorGhostSig.StartsWith(p.ModelPath + "|", StringComparison.Ordinal);
        if (sameModel)
        {
            bool moved = p.Kind == "m2"
                ? _doodads?.TryUpdateDynamicTransform(WbCursorGhostKey, WbTransform(p)) == true
                : _wmo?.TryUpdateDynamicTransform(WbCursorGhostKey, WbTransform(p)) == true;
            if (moved) { _wbCursorGhostSig = sig; return; }
        }
        WbRemoveGhost(WbCursorGhostKey);
        _wbCursorGhostSig = WbAddGhost(WbCursorGhostKey, p) ? sig : null;
    }

    /// <summary>Unpublished (or not-yet-downloaded) placements draw as dynamic models.</summary>
    private void WbSyncGhosts()
    {
        var want = new Dictionary<int, WorldPackClient.Placement>();
        if (_wbState is not null && _wbStateMap == _config.Start.Map && !_wbGhostsHidden)
        {
            var enabled = _wbState.Packs.Where(p => p.Enabled || p.Id == _wbPackId).Select(p => p.Id).ToHashSet();
            bool patchCurrent = _wbMountedBuild is { } b && _wbState.LastBuild?.BuildId == b;
            foreach (var p in _wbState.Placements)
                if (!p.Deleted && enabled.Contains(p.PackId) && !(p.Published && patchCurrent))
                    want[p.Id] = p;
        }
        foreach (var id in _wbGhosts.Keys.Where(id => !want.ContainsKey(id)).ToList())
        {
            WbRemoveGhost(WorldBuilderLaw.GhostKey(id));
            _wbGhosts.Remove(id);
        }
        foreach (var (id, p) in want)
        {
            string sig = WbSignature(p);
            if (_wbGhosts.TryGetValue(id, out var have) && have == sig) continue;
            if (WbAddGhost(WorldBuilderLaw.GhostKey(id), p)) _wbGhosts[id] = sig;
            else _wbGhosts.Remove(id);   // pending model: retried next frame
        }
    }

    private void WbDrawCursorOverlay()
    {
        var draw = ImGui.GetForegroundDrawList();
        var mouse = ImGui.GetIO().MousePos + new Vector2(16f, 18f);
        if (_wbCursor is not { } at || _terrain is null)
        {
            if (!ImGui.GetIO().WantCaptureMouse && _wbToolHint.Length > 0)
            {
                draw.AddText(mouse + Vector2.One, 0xE0000000, _wbToolHint);
                draw.AddText(mouse, 0xFF80D0FF, _wbToolHint);
            }
            return;
        }
        var display = ImGui.GetIO().DisplaySize;
        var cam = _window.Camera;
        uint colour = _wbTool is WorldBuilderTool.Sculpt or WorldBuilderTool.Path ? 0xE040D0FFu : 0xE0FFD040u;
        float radius = _wbTool == WorldBuilderTool.Sculpt ? _wbRadius : _wbTool == WorldBuilderTool.Path ? _wbCarveWidth / 2f
            : _wbSpawnArmed && _wbSpawnMode == 1 ? _wbPackRadius : _wbTool == WorldBuilderTool.GameObject ? 1f
            : _wbTool == WorldBuilderTool.Pick && _wbPickRadius > 0f ? _wbPickRadius : 2f;
        // The patrol route being clicked out: markers joined in order, closed back to the first (waypoints loop).
        if (_wbSpawnArmed && _wbSpawnMode == 2 && _wbPatrolPoints.Count > 0)
        {
            var route = _wbPatrolPoints.Select(p => cam.TryProjectToScreen(p + new Vector3(0, 0, 0.5f), display, out var s, out _) ? s : (Vector2?)null).ToList();
            for (int i = 0; i < route.Count; i++)
            {
                if (route[i] is not { } a0) continue;
                draw.AddCircleFilled(a0, 5f, i == 0 ? 0xE040FF40u : 0xE0FFD040u);
                if (route.Count > 1 && route[(i + 1) % route.Count] is { } b0) draw.AddLine(a0, b0, i + 1 == route.Count ? 0x80FFD040u : 0xE0FFD040u, 2f);
            }
        }
        Vector2? prev = null;
        for (int i = 0; i <= 48; i++)
        {
            float a = i / 48f * MathF.Tau;
            float x = at.X + MathF.Cos(a) * radius, y = at.Y + MathF.Sin(a) * radius;
            float z = _terrain.SampleHeight(x, y) ?? at.Z;
            Vector2? px = cam.TryProjectToScreen(new Vector3(x, y, z + 0.3f), display, out var pixel, out _) ? pixel : null;
            if (prev is { } p0 && px is { } p1) draw.AddLine(p0, p1, colour, 2f);
            prev = px;
        }
        string hint = _wbTool switch
        {
            WorldBuilderTool.Sculpt => $"{_wbBrush} · radius {_wbRadius:F0} yd · hold left mouse · Shift reverses · Esc cancels",
            WorldBuilderTool.Place => $"{Path.GetFileNameWithoutExtension(_wbModel ?? "pick a model")} · left click to place · [ ] rotate · Esc cancels",
            WorldBuilderTool.Path => _wbPathDragging ? "moving point · release to drop"
                : $"click: add point {_wbCarvePoints.Count + 1} · drag a point to move · Ctrl+click inserts · [ ] width · Esc stops",
            WorldBuilderTool.GameObject => WbGameObjectHint(),
            WorldBuilderTool.Pick => $"click: {_wbPickWhat} · Esc cancels",
            _ => _wbMoveArmed ? "click the new spot"
                : _wbSpawnArmed ? _wbSpawnMode switch
                {
                    1 => $"click: linked pack of {_wbPackCount}",
                    2 => $"click: patrol point {_wbPatrolPoints.Count + 1} (Save patrol in the panel)",
                    _ => "click: place the NPC here · [ ] turn it",
                }
                : "click near a placement to select it",
        };
        if (_wbToolHint.Length > 0) hint = _wbToolHint + "  (Esc cancels)";
        else if (_wbStroking) hint = "Sculpting… Release to save · Esc discards this stroke";
        draw.AddText(mouse + Vector2.One, 0xD0000000, hint);
        draw.AddText(mouse, colour, hint);
    }

    // ── web ──────────────────────────────────────────────────────────────────

    private void WbRequestState()
    {
        if (SuiWebAppUrl.Length == 0 || _wbStateTask is not null || _wbSculptSaveTask is not null) return;
        int map = _config.Start.Map;
        _wbStateRequestedPackId = _wbPackId;
        _wbStateTask = _wbClient.GetStateAsync(SuiWebAppUrl, map, _wbStateRequestedPackId);
    }

    private void WbOp(string label, Task<WorldPackClient.Reply> task, Action<WorldPackClient.Reply>? then = null)
    {
        _wbOps.Add((label, then is null ? task : task.ContinueWith(t =>
        {
            var r = t.GetAwaiter().GetResult();
            if (r.Success) _wbPendingThen.Enqueue(() => then(r));
            return r;
        })));
        _wbMessage = $"saving: {label}...";
    }

    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _wbPendingThen = new();

    private void WbUndo()
    {
        if (WbUndoProblem() is { Length: > 0 } problem) { _wbMessage = "Undo: " + problem; return; }
        string what = _wbLastOpLabel;
        WbOp("undo", _wbClient.UndoAsync(SuiWebAppUrl, _wbPackId), _ =>
        {
            _wbDocsTask = null; WbRequestDocs();
            _wbPendingUndoMessage = what.Length > 0 ? $"Undid: {what}" : "Undid the last edit.";
        });
    }

    private void WbResolveSculptSave()
    {
        if (_wbSculptSaveTask is not { IsCompleted: true } save) return;
        _wbSculptSaveTask = null;
        if (!save.IsCompletedSuccessfully || !save.Result.Success)
        {
            // Restore immediately even if the follow-up GET also fails. Only this rejected stroke is removed.
            foreach (var (key, deltas) in _wbPendingSculpt)
            {
                if (_wbServerSculpt.TryGetValue(key, out var current)) WorldBuilderLaw.ApplyStroke(current, deltas, undo: true);
                _wbDirtyTiles.Add(key);
            }
            _wbPendingSculpt.Clear();
            _wbSculptAwaitingState = false;
        }
    }

    private void PumpWbTasks()
    {
        WbResolveSculptSave();
        while (_wbPendingThen.TryDequeue(out var completed)) completed();

        bool anyDone = false;
        for (int i = _wbOps.Count - 1; i >= 0; i--)
        {
            var (label, task) = _wbOps[i];
            if (!task.IsCompleted) continue;
            _wbOps.RemoveAt(i);
            anyDone = true;
            if (!task.IsCompletedSuccessfully || !task.Result.Success)
            {
                _wbMessage = task.IsCanceled ? $"{label}: save cancelled; preview restored."
                    : task.IsFaulted ? $"{label}: {task.Exception?.GetBaseException().Message}" : $"{label}: {task.Result.Error}";
                // A script must never lose a rejected op silently (it would run on as if saved).
                if (WbScriptPath is not null) Console.WriteLine($"[wbscript] ERROR op rejected: {_wbMessage}");
            }
            else
            {
                var r = task.Result.Result;
                _wbMessage = r?.UndoneOpId is not null ? (_wbPendingUndoMessage ?? "Undid the last edit.")
                           : r is not null ? $"{label}: saved as op #{r.OpId}, audit #{r.AuditId}"
                           : $"{label}: ok";
            }
        }
        if (anyDone) { _wbStateTask = null; WbRequestState(); _wbLastOpStale = true; _wbPendingUndoMessage = null; }

        if (_wbStateTask is { IsCompleted: true } st)
        {
            _wbStateTask = null;
            if (!st.IsCompletedSuccessfully) _wbMessage = "web app: " + (st.Exception?.GetBaseException().Message ?? "refresh cancelled");
            else if (!st.Result.Success) _wbMessage = "web app: " + st.Result.Error;
            else if (_wbStateRequestedPackId != _wbPackId) WbRequestState();
            else WbAcceptState(st.Result);
        }

        // Build status: poll every 2 s while a build runs (and once after a Publish click).
        double now = ImGui.GetTime();
        if (_creatorPanel == CreatorPanel.World && SuiWebAppUrl.Length > 0 && _wbStatusTask is null &&
            (now - _wbStatusAt > (_wbStatus?.Running == true ? 2.0 : 15.0)))
        {
            _wbStatusAt = now;
            _wbStatusTask = _wbClient.GetStatusAsync(SuiWebAppUrl);
        }
        if (_wbStatusTask is { IsCompleted: true } bt)
        {
            _wbStatusTask = null;
            if (!bt.IsFaulted)
            {
                bool wasRunning = _wbStatus?.Running == true;
                _wbStatus = bt.Result;
                if (wasRunning && !_wbStatus.Running)
                {
                    _wbMessage = $"build #{_wbStatus.BuildId} {_wbStatus.Status}" + (_wbStatus.Error is { } e ? $": {e}" : "");
                    WbRequestState();
                }
            }
        }

        PumpWbCollisionSync();
        if (_wbDownloadTask is { IsCompleted: true } dl)
        {
            _wbDownloadTask = null;
            WbFinishDownload(dl);
        }
    }

    private void WbAcceptState(WorldPackClient.State s)
    {
        if (s.MapId != _config.Start.Map) return;
        if (_wbSculptSaveTask is not null) return; // This GET predates the save acknowledgement.
        _wbSculptAwaitingState = false;
        _wbPendingSculpt.Clear();
        bool mapChanged = s.MapId != _wbStateMap;
        _wbState = s;
        _wbStateMap = s.MapId;
        _wbStateIncludePackId = _wbStateRequestedPackId;
        if (_wbPackId == 0 && s.Packs.Count > 0) _wbPackId = s.Packs[0].Id;
        if (mapChanged) { _wbStock.Clear(); _wbAppliedTo.Clear(); _wbPreviewTiles.Clear(); _wbDirtyTiles.Clear(); }
        // A pack map's preview base is derived from the published sculpt: a new publish invalidates it.
        if (s.LastBuild?.BuildId != _wbStockBuild) { _wbStockBuild = s.LastBuild?.BuildId; _wbStock.Clear(); _wbAppliedTo.Clear(); }

        var previous = mapChanged ? new HashSet<(int, int)>() : _wbServerSculpt.Keys.ToHashSet();
        _wbServerSculpt.Clear();
        foreach (var t in s.Sculpt)
        {
            var grid = new float[WorldBuilderLaw.VertexCount];
            foreach (var (i, d) in t.Deltas) if ((uint)i < (uint)grid.Length) grid[i] = d;
            _wbServerSculpt[(t.Col, t.Row)] = grid;
        }
        // Every tile that has or had deltas (incl. published ones) is re-derived from stock.
        foreach (var key in previous.Concat(_wbServerSculpt.Keys).Concat(s.PublishedSculpt.Select(t => (t.Col, t.Row))))
        {
            _wbPreviewTiles.Add(key);
            _wbDirtyTiles.Add(key);
        }
        _wbAppliedTo.Clear();
    }

    private void WbReadMountedBuild()
    {
        if (_wbMountedRead || _mpq is null) return;
        _wbMountedRead = true;
        _wbMountedBuild = null;
        if (!_mpq.IsMounted(WbPatchName)) return;
        var hit = _mpq.ReadFileFromSupplier("WorldPacks\\build.json", WbPatchName);
        if (hit is null) return;
        try
        {
            using var doc = JsonDocument.Parse(hit.Value.Data);
            if (doc.RootElement.TryGetProperty("buildId", out var id)) _wbMountedBuild = id.GetInt32();
        }
        catch { }
    }

    /// <summary>A scripted QA client (MSUI_WORLDPACK_SYNC=off) shares GameData with the owner's client but talks to another
    /// web app: its downloads go to scratch and nothing it does touches the owner's patch-7, build record or collision.</summary>
    private static bool WbQaIsolated => Environment.GetEnvironmentVariable("MSUI_WORLDPACK_SYNC") == "off";

    private string WbPatchPath => WbQaIsolated
        ? Path.Combine(_config.RepoRoot, "scratch", "worldpack-qa", WbPatchName)
        : Path.Combine(_config.ClientDataPath, WbPatchName);

    private void WbStartDownload()
    {
        if (_mpq is null) return;
        string dest = WbPatchPath;
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        // Close our handle first so the file can be replaced on disk (Windows locks open files).
        _mpq.ReplaceArchive(WbPatchName, null);
        _wbDownloadTask = _wbClient.DownloadPatchAsync(SuiWebAppUrl, dest);
        _wbMessage = "downloading patch-7.MPQ...";
    }

    private void WbFinishDownload(Task<long> dl)
    {
        string dest = WbPatchPath;
        if (dl.IsFaulted)
        {
            _wbMessage = "download failed: " + dl.Exception?.GetBaseException().Message;
            string keep = File.Exists(dest) ? dest : Path.Combine(_config.ClientDataPath, WbPatchName);
            if (File.Exists(keep)) _mpq?.ReplaceArchive(WbPatchName, keep);   // keep the old one mounted
            _wbMountedRead = false;
            return;
        }
        bool ok = _mpq?.ReplaceArchive(WbPatchName, dest) == true;
        _wbMountedRead = false;
        _wbPublishedStampCache = null;   // the new build's manifest may stamp other tiles
        WbReadMountedBuild();
        _wbMessage = ok ? $"mounted {WbPatchName} ({dl.Result / 1024} KiB), build #{_wbMountedBuild} - reloading the world"
                        : $"{WbPatchName} downloaded but could not be mounted";
        if (ok) WbReloadWorld();
        // patch-7 is only what the client renders: MSUIClient's live collision reads the server-format
        // vmaps, so pull what the packs installed too (a player otherwise falls through pack buildings).
        // The same sidecar the startup sync reads: this client now holds build N.
        if (ok && !WbQaIsolated && _wbMountedBuild is { } mounted)
            WorldPackStartupSync.WriteLocal(_config.ClientDataPath, mounted,
                _wbState?.LastBuild is { } lb && lb.BuildId == mounted ? lb.Sha1 : null);
        if (ok && !WbQaIsolated && _wbCollisionTask is null)
            _wbCollisionTask = WorldPackCollisionSync.SyncAsync(_wbClient, SuiWebAppUrl, _wbMountedBuild,
                WbServerDataDir(_config.VmapPath, "vmaps"), WbServerDataDir(_config.MmapPath, "mmaps"));
    }

    /// <summary>A configured vmaps/mmaps folder (relative to the repo root), or GameData\{name}.</summary>
    private string WbServerDataDir(string? configured, string name)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.IsPathRooted(configured) ? configured : Path.Combine(_config.RepoRoot, configured);
        return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_config.ClientDataPath))!, name);
    }

    private void PumpWbCollisionSync()
    {
        if (_wbCollisionTask is not { IsCompleted: true } t) return;
        _wbCollisionTask = null;
        _wbMessage = t.IsFaulted ? "collision sync failed: " + t.Exception?.GetBaseException().Message : t.Result.Summary;
        Console.WriteLine($"[worldbuilder] {_wbMessage}");
    }

    /// <summary>Tear down and re-stream the current map at the current spot, so terrain, WMO/M2
    /// placements and collision all come from the freshly mounted archive.</summary>
    private void WbReloadWorld()
    {
        _worldMapReloadPending = true;
        if (_controller is null) return;
        foreach (var id in _wbGhosts.Keys.ToList()) WbRemoveGhost(WorldBuilderLaw.GhostKey(id));
        _wbGhosts.Clear();
        WbSetCursorGhost(null);
        _wbStock.Clear();
        _wbAppliedTo.Clear();
        // Map.dbc / WDTs may have changed with the archive (pack maps): re-read them.
        ResetInstanceData();
        EnsureInstanceData();
        var row = _maps?.Get(_config.Start.Map);
        var wdt = row is null ? null : _mapWdts?.GetValueOrDefault(_config.Start.Map);
        if (row is null || wdt is null) { _wbMessage += " (reload refused: no map row)"; return; }
        var at = _controller.Position;
        // TravelTo's SetMap is a no-op on the same map; the cached parses predate the mount.
        _adts?.Invalidate();
        TravelTo(row, wdt, at, _controller.Yaw, recordReturn: false);

        foreach (var key in _wbServerSculpt.Keys) { _wbPreviewTiles.Add(key); _wbDirtyTiles.Add(key); }
    }
}

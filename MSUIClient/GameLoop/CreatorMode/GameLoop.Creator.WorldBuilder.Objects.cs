using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;
using MSUIClient.World.Wmo;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// World Builder: chests and other server objects (shared_docs/WORLD_BUILDER.md §4, "Chests & objects").
//
// A gameobject is a SERVER spawn (lootable, clickable, respawning) - unlike Buildings & props, which are baked
// into the ADT. Each placement is one `dbrow:gameobject` doc in the reserved spawn range, posted through the
// audited /WorldPacks/Content op; a custom chest is a pack copy of a stock chest template (7,000,000+) with its
// own `gameobject_loot_template` rows. Stock templates come from the web app's read-only /GameObjects/Search.
// Saved spawns draw as model previews (offline, or until the live server shows the published object).
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class GameLoop
{
    private sealed record WbGoTemplate(uint Entry, int Type, uint DisplayId, string Name, float Size, bool Pack);

    private static readonly (string Label, int? Type)[] WbGoTypes =
    {
        ("All kinds", null), ("Chests, herbs and ore", 3), ("Decorations", 5), ("Chairs and benches", 7),
        ("Campfires and forges", 8), ("Usable objects", 10), ("Signs and books", 9), ("Doors", 0), ("Mailboxes", 19),
    };

    private const ulong WbGoCursorKey = 0xB2B2_FFFF_FFFF_FFFFUL;
    private static ulong WbGoPreviewKey(uint guid) => 0xB2B2_0000_0000_0000UL | guid;

    private readonly byte[] _wbGoSearch = new byte[64];
    private int _wbGoTypeIndex = 1;
    private string _wbGoSearchSig = "\u0001", _wbGoSearchWanted = "";
    private Task<string>? _wbGoSearchTask;
    private double _wbGoSearchAt;
    private List<WbGoTemplate> _wbGoHits = new();
    private string _wbGoSearchError = "";
    private WbGoTemplate? _wbGoPick;
    private float _wbGoYaw;
    private uint _wbChestEditEntry;            // custom chest open in the form (0 = a new chest)
    private Task<string>? _wbChestLooksTask;
    private List<WbGoTemplate> _wbChestLooks = new();
    private WbGoTemplate? _wbChestLook;
    private int _wbGoRespawn = 300;
    private bool _wbGoMoveArmed, _wbGoPicking;
    private uint _wbGoSelected;
    private JsonObject? _wbGoDraft;
    private string? _wbGoCursorSig;
    private readonly Dictionary<uint, (uint Display, float Size, string Name)> _wbGoKnown = new();
    private readonly Dictionary<ulong, string> _wbGoPreviews = new();
    private Task<string>? _wbGoLookupTask;
    private uint _wbGoLookupEntry;
    private readonly HashSet<uint> _wbGoLookupFailed = new();
    private uint WbReservedGoGuid, WbReservedGoEntry;
    private int _wbGoDocsRevision = -1, _wbGoSpawnCacheMap = -1;
    private List<JsonObject> _wbGoSpawnCache = new();

    // custom chest form
    private readonly byte[] _wbChestName = new byte[80];
    private readonly byte[] _wbChestItemSearch = new byte[64];
    private string _wbChestItemSearchLast = "\u0001";
    private List<CreatorItemTable.Item>? _wbChestItemHits;
    private readonly List<(uint Item, string Name, float Chance, int Min, int Max)> _wbChestLoot = new();
    private int _wbChestGoldMin, _wbChestGoldMax;
    private bool _wbChestConsumable = true;
    private bool _wbChestSaving;

    private void DrawWbGameObjectSection()
    {
        if (_wbDocs is null) { if (_wbDocsTask is null) WbRequestDocs(); ImGui.TextDisabled("Loading pack content..."); return; }
        EnsureGameObjectDisplays();
        ImGui.TextWrapped("Chests, herb and ore nodes, campfires, chairs, mailboxes: real server objects players can click, loot or use. They respawn and go live when you publish.");
        if (ImGui.BeginTabBar("##wb-go-tabs"))
        {
            bool place = ImGui.BeginTabItem("Place objects"); ObserveWorldBuilderUiItem("Place objects");
            if (place) { DrawWbGoPlace(); ImGui.EndTabItem(); }
            bool edit = ImGui.BeginTabItem("Edit placed objects##go"); ObserveWorldBuilderUiItem("Edit placed server objects");
            if (edit) { DrawWbGoEdit(); ImGui.EndTabItem(); }
            bool chest = ImGui.BeginTabItem("Make a custom chest"); ObserveWorldBuilderUiItem("Make a custom chest");
            if (chest) { DrawWbChestForm(); ImGui.EndTabItem(); }
            ImGui.EndTabBar();
        }
    }

    // ── place ────────────────────────────────────────────────────────────────

    private void DrawWbGoPlace()
    {
        bool armed = _wbTool == WorldBuilderTool.GameObject && !_wbGoMoveArmed && !_wbGoPicking;
        bool invisible = _wbGoPick is { } chosen && _gameObjectDisplays?.ModelPath(chosen.DisplayId) is null;
        ImGui.BeginDisabled(_wbGoPick is null || invisible);
        bool toggle = CreatorButton(armed ? "Stop placing (Esc)" : "Place selected object");
        ObserveWorldBuilderUiItem("Place selected object");
        if (toggle)
        {
            if (armed) WbSetTool(WorldBuilderTool.None);
            else { WbSetTool(WorldBuilderTool.GameObject); _wbGoMoveArmed = _wbGoPicking = false; }
        }
        ImGui.EndDisabled();
        if (_wbGoPick is { } pick) ImGui.TextWrapped($"{pick.Name}{(pick.Pack ? "  (yours)" : "")}");
        else ImGui.TextDisabled("Choose an object in the list below first.");
        if (invisible) ImGui.TextWrapped("This object has no model: players would not see it. Choose another.");
        if (armed) ImGui.TextWrapped("Left-click the ground to place; keep clicking to place more. [ and ] turn it.");

        ImGui.SetNextItemWidth(CreatorControlWidth);
        ImGui.Combo("Kind##wb-go", ref _wbGoTypeIndex, WbGoTypes.Select(t => t.Label).ToArray(), WbGoTypes.Length);
        ObserveWorldBuilderUiItem("Object kind");
        ImGui.SetNextItemWidth(CreatorControlWidth);
        ImGui.InputText("Search objects##wb-go", _wbGoSearch, (uint)_wbGoSearch.Length);
        ObserveWorldBuilderUiItem("Object search");
        ImGui.TextDisabled("Try chest, crate, copper, peacebloom, campfire...");
        WbPumpGoSearch();

        var packTemplates = WbGoPackTemplates().ToList();
        string q = WbText(_wbGoSearch);
        int? type = WbGoTypes[_wbGoTypeIndex].Type;
        var mine = packTemplates.Where(t => (type is null || t.Type == type) && (q.Length == 0 || t.Name.Contains(q, StringComparison.OrdinalIgnoreCase))).ToList();
        if (ImGui.BeginListBox("##wb-go-hits", new Vector2(-1f, 150f * CreatorUiScale)))
        {
            foreach (var t in mine.Concat(_wbGoHits.Where(h => mine.All(m => m.Entry != h.Entry))))
            {
                bool hasModel = _gameObjectDisplays?.ModelPath(t.DisplayId) is not null;
                string label = $"{t.Name}{(t.Pack ? "  (yours)" : "")}{(hasModel ? "" : "  (no model)")}##go{t.Entry}";
                if (ImGui.Selectable(label, _wbGoPick?.Entry == t.Entry)) { _wbGoPick = t; _wbGoCursorSig = null; }
                ObserveWorldBuilderUiItem("Object: " + t.Name);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(WbGoTypes.FirstOrDefault(k => k.Type == t.Type).Label ?? "object");
            }
            ImGui.EndListBox();
        }
        if (_wbGoSearchTask is not null) ImGui.TextDisabled("Searching...");
        else if (_wbGoSearchError.Length > 0) ImGui.TextWrapped(_wbGoSearchError);

        ImGui.SetNextItemWidth(CreatorControlWidth);
        ImGui.SliderFloat("Facing##wb-go", ref _wbGoYaw, 0f, 360f, "%.0f deg");
        ImGui.TextDisabled("The preview under the pointer shows the facing; [ and ] turn it while placing.");
        ImGui.SetNextItemWidth(CreatorControlWidth * 0.6f);
        ImGui.InputInt("Respawn (seconds)", ref _wbGoRespawn, 30, 300);
        _wbGoRespawn = Math.Clamp(_wbGoRespawn, 5, 604800);
        ImGui.TextWrapped("Placed objects are drafts until you publish. Ctrl+Z undoes the last one.");
    }

    private void WbPumpGoSearch()
    {
        string sig = WbText(_wbGoSearch) + "|" + _wbGoTypeIndex;
        if (sig != _wbGoSearchSig) { _wbGoSearchSig = sig; _wbGoSearchWanted = sig; _wbGoSearchAt = ImGui.GetTime(); }
        if (_wbGoSearchWanted.Length > 0 && _wbGoSearchTask is null && ImGui.GetTime() - _wbGoSearchAt > 0.35 && SuiWebAppUrl.Length > 0)
        {
            _wbGoSearchWanted = "";
            _wbGoSearchTask = _wbClient.GameObjectSearchAsync(SuiWebAppUrl, WbText(_wbGoSearch), WbGoTypes[_wbGoTypeIndex].Type);
        }
        if (_wbGoSearchTask is not { IsCompleted: true } t) return;
        _wbGoSearchTask = null;
        if (!t.IsCompletedSuccessfully) { _wbGoSearchError = "Object search failed: " + t.Exception?.GetBaseException().Message; return; }
        try
        {
            _wbGoSearchError = "";
            var hits = new List<WbGoTemplate>();
            foreach (var o in (JsonNode.Parse(t.Result)?["objects"] as JsonArray)?.OfType<JsonObject>() ?? [])
            {
                var tpl = new WbGoTemplate(WbNpcUInt(o["entry"]), (int)WbNpcFloat(o["type"]), WbNpcUInt(o["displayId"]),
                    o["name"]?.ToString() ?? "object", WbNpcFloat(o["size"], 1f), false);
                hits.Add(tpl);
                _wbGoKnown[tpl.Entry] = (tpl.DisplayId, tpl.Size, tpl.Name);
            }
            _wbGoHits = hits;
            if (hits.Count == 0) _wbGoSearchError = "No objects match. Try another word or kind.";
        }
        catch (Exception ex) { _wbGoSearchError = "Object search: " + ex.Message; }
    }

    private IEnumerable<WbGoTemplate> WbGoPackTemplates() =>
        WbNpcPackDocs("dbrow:gameobject_template", _wbPackId).Select(d => d["body"] as JsonObject).OfType<JsonObject>()
            .Select(b => new WbGoTemplate(WbNpcUInt(b["entry"]), (int)WbNpcFloat(b["type"]), WbNpcUInt(b["displayId"]),
                b["name"]?.ToString() ?? "object", WbNpcFloat(b["size"], 1f), true))
            .GroupBy(t => t.Entry).Select(g => g.First());

    // ── pointer ──────────────────────────────────────────────────────────────

    private void WbGameObjectFrame(ImGuiIOPtr io, bool overUi)
    {
        if (_wbCursor is not { } at || overUi) { WbSetGameObjectCursor(null); return; }
        bool click = ImGui.IsMouseClicked(ImGuiMouseButton.Left);
        if (_wbGoPicking)
        {
            WbSetGameObjectCursor(null);
            if (!click) return;
            var near = WbGoSpawnDocs(_wbPackId).Select(d => d["body"]!.AsObject())
                .Select(b => (b, d: Vector3.Distance(WbGoPosition(b), at))).Where(x => x.d < 8f).OrderBy(x => x.d).FirstOrDefault();
            if (near.b is null) { _wbMessage = "No object from this pack there. Click closer to it, or choose it in the list."; return; }
            _wbGoSelected = WbNpcUInt(near.b["guid"]);
            _wbGoDraft = null;
            _wbGoPicking = false;
            WbSetTool(WorldBuilderTool.None);
            return;
        }
        if (_wbGoMoveArmed)
        {
            if (WbGoSpawnDoc(_wbGoSelected) is not { } doc) { _wbGoMoveArmed = false; WbSetTool(WorldBuilderTool.None); return; }
            var moved = doc.DeepClone().AsObject();
            WbGoSetPosition(moved, at, WbNpcFloat(moved["orientation"]));
            uint entry = WbNpcUInt(moved["id"]);
            WbSetGameObjectCursor(_wbGoKnown.TryGetValue(entry, out var k) ? (k.Display, k.Size, at, WbNpcFloat(moved["orientation"])) : null);
            if (click && _wbOps.Count > 0) _wbMessage = "Saving the previous change; click again in a moment.";
            if (click && _wbOps.Count == 0)
            {
                _wbGoMoveArmed = false;
                WbSetTool(WorldBuilderTool.None);
                WbGoSaveRow(moved, $"move object {_wbGoSelected}");
            }
            return;
        }
        if (_wbGoPick is not { } pick) { WbSetGameObjectCursor(null); return; }
        float facing = WbGoFacing(at);
        WbSetGameObjectCursor((pick.DisplayId, pick.Size, at, facing));
        if (!click) return;
        if (_wbOps.Count >= 4) { _wbMessage = "Saving the previous objects; click again in a moment."; return; }
        var row = WbGoSpawnRow(pick.Entry, at, facing);
        _wbGoKnown[pick.Entry] = (pick.DisplayId, pick.Size, pick.Name);
        uint guid = WbNpcUInt(row["guid"]);
        int pack = _wbPackId;
        var items = new JsonArray { new JsonObject { ["kind"] = "dbrow:gameobject", ["body"] = row } };
        WbOp($"place {pick.Name}", _wbClient.ContentAsync(SuiWebAppUrl, pack, $"place {pick.Name} ({guid})", items.ToJsonString()),
            _ => { _wbGoSelected = guid; _wbGoDraft = null; _wbDocsTask = null; WbRequestDocs(); });
    }

    private string WbGameObjectHint() =>
        _wbGoPicking ? "click a placed object to select it"
        : _wbGoMoveArmed ? "click the new spot"
        : _wbGoPick is { } p ? $"{p.Name} · left click to place · [ ] turn · Esc stops" : "choose an object to place";

    private float WbGoFacing(Vector3 at)
    {
        float yaw = _wbGoYaw * MathF.PI / 180f;
        return WorldBuilderLaw.PositiveAngle(yaw);
    }

    /// <summary>The spawn row: the web app's own gameobject column set, a reserved guid, and a yaw quaternion
    /// (rotation2/3) matching the orientation.</summary>
    private JsonObject WbGoSpawnRow(uint entry, Vector3 at, float facing)
    {
        uint guid = Math.Max(WbNextId("dbrow:gameobject", "guid", WbSpawnBase), WbReservedGoGuid + 1);
        WbReservedGoGuid = guid;
        var row = new JsonObject
        {
            ["guid"] = guid, ["id"] = entry, ["map"] = _config.Start.Map,
            ["rotation0"] = 0, ["rotation1"] = 0,
            ["spawntimesecsmin"] = _wbGoRespawn, ["spawntimesecsmax"] = _wbGoRespawn,
            ["animprogress"] = 100, ["state"] = 1, ["spawn_flags"] = 0, ["patch_min"] = 0, ["patch_max"] = 10,
        };
        WbGoSetPosition(row, at, facing);
        return row;
    }

    private static void WbGoSetPosition(JsonObject row, Vector3 at, float facing)
    {
        facing = WorldBuilderLaw.PositiveAngle(facing);
        row["position_x"] = Math.Round(at.X, 2); row["position_y"] = Math.Round(at.Y, 2); row["position_z"] = Math.Round(at.Z, 2);
        row["orientation"] = Math.Round(facing, 4);
        row["rotation2"] = Math.Round(MathF.Sin(facing / 2f), 6);
        row["rotation3"] = Math.Round(MathF.Cos(facing / 2f), 6);
    }

    private static Vector3 WbGoPosition(JsonObject row) =>
        new(WbNpcFloat(row["position_x"]), WbNpcFloat(row["position_y"]), WbNpcFloat(row["position_z"]));

    private void WbGoSaveRow(JsonObject row, string label)
    {
        var items = new JsonArray { new JsonObject { ["kind"] = "dbrow:gameobject", ["body"] = row } };
        WbOp(label, _wbClient.ContentAsync(SuiWebAppUrl, _wbPackId, label, items.ToJsonString()),
            _ => { _wbGoDraft = null; _wbDocsTask = null; WbRequestDocs(); });
    }

    private IEnumerable<JsonObject> WbGoSpawnDocs(int? pack) =>
        WbNpcPackDocs("dbrow:gameobject", pack).Where(d => d["body"] is JsonObject b && (int?)b["map"] == _config.Start.Map);

    private JsonObject? WbGoSpawnDoc(uint guid) =>
        guid == 0 ? null : WbGoSpawnDocs(_wbPackId).Select(d => d["body"] as JsonObject).FirstOrDefault(b => WbNpcUInt(b?["guid"]) == guid);

    private string WbGoName(uint entry) =>
        _wbGoKnown.TryGetValue(entry, out var k) ? k.Name
        : WbGoPackTemplates().FirstOrDefault(t => t.Entry == entry)?.Name ?? $"object {entry}";

    // ── edit placed ──────────────────────────────────────────────────────────

    private void DrawWbGoEdit()
    {
        var spawns = WbGoSpawnDocs(_wbPackId).Select(d => d["body"]!.AsObject()).ToList();
        Vector3 me = _controller?.Position ?? Vector3.Zero;
        bool picking = _wbTool == WorldBuilderTool.GameObject && _wbGoPicking;
        if (CreatorButton(picking ? "Stop selecting (Esc)" : "Select an object in the world"))
        {
            if (picking) WbSetTool(WorldBuilderTool.None);
            else { WbSetTool(WorldBuilderTool.GameObject); _wbGoPicking = true; _wbGoMoveArmed = false; }
        }
        ImGui.SameLine();
        ImGui.BeginDisabled(_wbOps.Count > 0);
        WbUndoButton();
        ImGui.EndDisabled();
        if (spawns.Count == 0) { ImGui.TextWrapped("This pack has no placed objects on this map yet."); return; }
        if (ImGui.BeginChild("##wb-go-list", new Vector2(0, 150f * CreatorUiScale), true))
        {
            foreach (var b in spawns.OrderBy(b => Vector3.Distance(WbGoPosition(b), me)).Take(100))
            {
                uint guid = WbNpcUInt(b["guid"]);
                string label = Inv($"{WbGoName(WbNpcUInt(b["id"]))}  {Vector3.Distance(WbGoPosition(b), me):F0} yd##go-spawn{guid}");
                if (ImGui.Selectable(label, guid == _wbGoSelected)) { _wbGoSelected = guid; _wbGoDraft = null; _wbGoMoveArmed = false; }
                ObserveWorldBuilderUiItem($"Server object {guid}");
            }
        }
        ImGui.EndChild();   // always, whatever BeginChild returned
        if (WbGoSpawnDoc(_wbGoSelected) is not { } sel) return;
        ImGui.Separator();
        _wbGoDraft ??= sel.DeepClone().AsObject();
        var draft = _wbGoDraft;
        var pos = WbGoPosition(draft);
        ImGui.TextWrapped(WbGoName(WbNpcUInt(sel["id"])) + (draft.ToJsonString() != sel.ToJsonString() ? "  - unsaved changes are previewed in the world" : ""));
        float heading = WbNpcFloat(draft["orientation"]) * 180f / MathF.PI;
        ImGui.SetNextItemWidth(CreatorControlWidth);
        if (ImGui.SliderFloat("Heading##go-sel", ref heading, 0f, 360f, "%.0f deg")) WbGoSetPosition(draft, pos, heading * MathF.PI / 180f);
        float z = pos.Z;
        ImGui.SetNextItemWidth(CreatorControlWidth);
        if (ImGui.InputFloat("Height##go-sel", ref z, 0.1f, 1f, "%.2f") && float.IsFinite(z))
            WbGoSetPosition(draft, pos with { Z = z }, WbNpcFloat(draft["orientation"]));
        int respawn = (int)WbNpcFloat(draft["spawntimesecsmin"], 300);
        ImGui.SetNextItemWidth(CreatorControlWidth * 0.6f);
        if (ImGui.InputInt("Respawn (seconds)##go-sel", ref respawn, 30, 300))
        {
            respawn = Math.Clamp(respawn, 5, 604800);
            draft["spawntimesecsmin"] = respawn; draft["spawntimesecsmax"] = respawn;
        }
        bool changed = draft.ToJsonString() != sel.ToJsonString();
        ImGui.BeginDisabled(!changed || _wbOps.Count > 0);
        if (CreatorButton("Save object changes")) WbGoSaveRow(draft.DeepClone().AsObject(), $"edit object {_wbGoSelected}");
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (CreatorButton("Reset fields")) _wbGoDraft = null;
        if (CreatorButton(_wbGoMoveArmed ? "Click the ground..." : "Move (click new spot)"))
        { WbSetTool(WorldBuilderTool.GameObject); _wbGoMoveArmed = true; _wbGoPicking = false; }
        ImGui.SameLine();
        if (_controller is not null && CreatorButton("Go to##go")) _controller.Teleport(pos.X, pos.Y, pos.Z + 10f);
        ImGui.SameLine();
        ImGui.BeginDisabled(_wbOps.Count > 0);
        bool remove = WbConfirmButton("Remove object", $"Remove this {WbGoName(WbNpcUInt(sel["id"]))} from the world? Undo brings it back.");
        ObserveWorldBuilderUiItem("Remove server object");
        if (remove)
        {
            uint guid = _wbGoSelected;
            var items = new JsonArray { new JsonObject { ["kind"] = "dbrow:gameobject", ["key"] = guid.ToString(CultureInfo.InvariantCulture), ["body"] = null } };
            WbOp($"remove object {guid}", _wbClient.ContentAsync(SuiWebAppUrl, _wbPackId, $"remove object {guid}", items.ToJsonString()),
                _ => { _wbGoSelected = 0; _wbGoDraft = null; _wbDocsTask = null; WbRequestDocs(); });
        }
        ImGui.EndDisabled();
    }

    // ── custom chest ─────────────────────────────────────────────────────────

    private void DrawWbChestForm()
    {
        var step = new Vector4(1f, .82f, .3f, 1f);
        ImGui.TextWrapped("Make a chest with your own name, money and loot, using the look of a chest from the game.");
        var mine = WbGoPackTemplates().Where(t => t.Type == 3).ToList();
        if (mine.Count > 0)
        {
            ImGui.TextUnformatted("Your chests");
            foreach (var t in mine)
            {
                if (ImGui.Selectable($"{t.Name}##mychest{t.Entry}", t.Entry == _wbChestEditEntry)) WbOpenChest(t);
                ObserveWorldBuilderUiItem("Your chest: " + t.Name);
            }
            if (CreatorButton("New chest")) { _wbChestEditEntry = 0; _wbChestLoot.Clear(); WbSetBuf(_wbChestName, ""); _wbChestGoldMin = _wbChestGoldMax = 0; }
            ImGui.Separator();
        }

        ImGui.TextColored(step, "1. Look");
        WbPumpChestLooks();
        _wbChestLook ??= _wbGoPick is { Type: 3, Pack: false } p ? p : null;
        ImGui.SetNextItemWidth(CreatorControlWidth);
        if (ImGui.BeginCombo("Looks like##chest", _wbChestLook?.Name ?? "Choose a chest from the game"))
        {
            foreach (var l in _wbChestLooks.Where(l => _gameObjectDisplays?.ModelPath(l.DisplayId) is not null).GroupBy(l => l.DisplayId).Select(g => g.First()))
            {
                if (ImGui.Selectable($"{l.Name}##look{l.Entry}", l.Entry == _wbChestLook?.Entry)) _wbChestLook = l;
                ObserveWorldBuilderUiItem("Chest look: " + l.Name);
            }
            ImGui.EndCombo();
        }
        if (_wbChestLooksTask is not null) ImGui.TextDisabled("Loading the game's chests...");

        ImGui.TextColored(step, "2. Name and money");
        ImGui.SetNextItemWidth(CreatorControlWidth);
        ImGui.InputText("Chest name", _wbChestName, (uint)_wbChestName.Length);
        ObserveWorldBuilderUiItem("Chest name");
        WbMoneyInput("Least money", ref _wbChestGoldMin);
        WbMoneyInput("Most money", ref _wbChestGoldMax);
        _wbChestGoldMax = Math.Max(_wbChestGoldMin, _wbChestGoldMax);
        ImGui.Checkbox("Disappears when looted (comes back after its respawn time)", ref _wbChestConsumable);

        ImGui.TextColored(step, "3. Loot");
        _wbItems ??= CreatorItemTable.Load(_config.RepoRoot);
        ImGui.SetNextItemWidth(CreatorControlWidth);
        ImGui.InputText("Find item to add", _wbChestItemSearch, (uint)_wbChestItemSearch.Length);
        ObserveWorldBuilderUiItem("Chest item search");
        string q = WbText(_wbChestItemSearch);
        if (q != _wbChestItemSearchLast) { _wbChestItemSearchLast = q; _wbChestItemHits = q.Length >= 2 ? _wbItems?.Search(q, -2, 30) : null; }
        if (_wbChestItemHits is { Count: > 0 } hits && ImGui.BeginListBox("##wb-chest-hits", new Vector2(-1, 90 * CreatorUiScale)))
        {
            foreach (var item in hits)
                if (ImGui.Selectable($"{item.Name}##chest{item.Entry}") && _wbChestLoot.All(l => l.Item != item.Entry))
                    _wbChestLoot.Add((item.Entry, item.Name, 100f, 1, 1));
            ImGui.EndListBox();
        }
        if (_wbChestLoot.Count == 0) ImGui.TextDisabled("No items yet: search above and click one to add it.");
        for (int i = 0; i < _wbChestLoot.Count; i++)
        {
            var l = _wbChestLoot[i];
            ImGui.PushID((int)l.Item);
            if (ImGui.SmallButton("Remove")) { _wbChestLoot.RemoveAt(i); ImGui.PopID(); break; }
            ImGui.SameLine(); ImGui.TextUnformatted(l.Name);
            float chance = l.Chance; int min = l.Min, max = l.Max;
            ImGui.SetNextItemWidth(CreatorControlWidth * 0.35f); ImGui.SliderFloat("% chance", ref chance, 1f, 100f, "%.0f");
            ImGui.SameLine(); ImGui.SetNextItemWidth(CreatorControlWidth * 0.25f); ImGui.InputInt("min", ref min, 0);
            ImGui.SameLine(); ImGui.SetNextItemWidth(CreatorControlWidth * 0.25f); ImGui.InputInt("max", ref max, 0);
            min = Math.Clamp(min, 1, 200); max = Math.Clamp(max, min, 200);
            _wbChestLoot[i] = (l.Item, l.Name, chance, min, max);
            ImGui.PopID();
        }

        ImGui.TextColored(step, "4. Save");
        string name = WbText(_wbChestName).Trim();
        string? problem = _wbChestLook is null && _wbChestEditEntry == 0 ? "Choose the chest's look (step 1)."
            : name.Length == 0 ? "Name the chest (step 2)."
            : _wbChestLoot.Count == 0 && _wbChestGoldMax == 0 ? "Add at least one item or some money." : null;
        if (problem is not null) ImGui.TextDisabled(problem);
        ImGui.BeginDisabled(problem is not null || _wbChestSaving || _wbOps.Count > 0);
        if (CreatorButton(_wbChestEditEntry == 0 ? "Create chest" : "Save chest changes"))
        {
            if (_wbChestEditEntry == 0) WbCreateChest(_wbChestLook!, name);
            else WbSaveChest(_wbChestEditEntry, name);
        }
        ImGui.EndDisabled();
        if (_wbChestEditEntry != 0 && mine.FirstOrDefault(t => t.Entry == _wbChestEditEntry) is { } open)
        {
            ImGui.SameLine();
            int placed = WbNpcPackDocs("dbrow:gameobject", _wbPackId).Count(d => WbNpcUInt(d["body"]?["id"]) == open.Entry);
            ImGui.BeginDisabled(_wbOps.Count > 0);
            if (WbConfirmButton("Delete chest", $"Delete {open.Name}, its loot and its {placed} placed cop{(placed == 1 ? "y" : "ies")}? Undo brings it back."))
            {
                var docs = WbNpcPackDocs("dbrow:gameobject_template", _wbPackId).Where(d => WbNpcUInt(d["body"]?["entry"]) == open.Entry)
                    .Concat(WbNpcPackDocs("dbrow:gameobject_loot_template", _wbPackId).Where(d => WbNpcUInt(d["body"]?["entry"]) == open.Entry))
                    .Concat(WbNpcPackDocs("dbrow:gameobject", _wbPackId).Where(d => WbNpcUInt(d["body"]?["id"]) == open.Entry));
                WbDeleteDocs($"delete chest {open.Name}", docs, () => { _wbChestEditEntry = 0; _wbChestLoot.Clear(); WbSetBuf(_wbChestName, ""); });
            }
            ImGui.EndDisabled();
        }
        ImGui.TextDisabled("Place your chest from Place objects (it is listed there as yours).");
    }

    /// <summary>Money as gold / silver / copper (stored in copper).</summary>
    private void WbMoneyInput(string label, ref int copper)
    {
        int g = copper / 10000, sv = copper / 100 % 100, c = copper % 100;
        float w = CreatorControlWidth * 0.22f;
        ImGui.PushID(label);
        ImGui.SetNextItemWidth(w); ImGui.InputInt("g", ref g, 0); ImGui.SameLine();
        ImGui.SetNextItemWidth(w); ImGui.InputInt("s", ref sv, 0); ImGui.SameLine();
        ImGui.SetNextItemWidth(w); ImGui.InputInt("c", ref c, 0); ImGui.SameLine();
        ImGui.TextUnformatted(label);
        ImGui.PopID();
        copper = Math.Clamp(g, 0, 9999) * 10000 + Math.Clamp(sv, 0, 99) * 100 + Math.Clamp(c, 0, 99);
    }

    private void WbPumpChestLooks()
    {
        if (_wbChestLooks.Count == 0 && _wbChestLooksTask is null && SuiWebAppUrl.Length > 0)
            _wbChestLooksTask = _wbClient.GameObjectSearchAsync(SuiWebAppUrl, "chest", 3, 80);
        if (_wbChestLooksTask is not { IsCompleted: true } t) return;
        _wbChestLooksTask = null;
        try
        {
            _wbChestLooks = ((JsonNode.Parse(t.Result)?["objects"] as JsonArray)?.OfType<JsonObject>() ?? [])
                .Select(o => new WbGoTemplate(WbNpcUInt(o["entry"]), 3, WbNpcUInt(o["displayId"]), o["name"]?.ToString() ?? "chest", WbNpcFloat(o["size"], 1f), false))
                .OrderBy(l => l.Name).ToList();
        }
        catch { _wbChestLooks = new() { }; }
        if (_wbChestLooks.Count == 0) _wbChestLooks.Add(new WbGoTemplate(2843, 3, 259, "Battered Chest", 1f, false));   // never leave the list empty
    }

    private void WbOpenChest(WbGoTemplate t)
    {
        var body = WbNpcPackDocs("dbrow:gameobject_template", _wbPackId).FirstOrDefault(d => WbNpcUInt(d["body"]?["entry"]) == t.Entry)?["body"];
        if (body is null) return;
        _wbChestEditEntry = t.Entry;
        WbSetBuf(_wbChestName, t.Name);
        _wbChestGoldMin = (int)WbNpcUInt(body["mingold"]); _wbChestGoldMax = (int)WbNpcUInt(body["maxgold"]);
        _wbChestConsumable = WbNpcUInt(body["data3"]) != 0;
        _wbChestLook = new WbGoTemplate(t.Entry, 3, t.DisplayId, "(current look)", t.Size, true);
        _wbChestLoot.Clear();
        uint lootId = WbNpcUInt(body["data1"]);
        foreach (var r in WbNpcPackDocs("dbrow:gameobject_loot_template", _wbPackId).Select(d => d["body"]!).Where(b => WbNpcUInt(b["entry"]) == lootId))
        {
            uint item = WbNpcUInt(r["item"]);
            _wbChestLoot.Add((item, WbItemName(item), WbNpcFloat(r["ChanceOrQuestChance"], 100f), (int)WbNpcUInt(r["mincountOrRef"]), (int)WbNpcUInt(r["maxcount"])));
        }
    }

    /// <summary>One op: the edited template (other columns kept), every current loot row, and removal of dropped rows.</summary>
    private void WbSaveChest(uint entry, string name)
    {
        var doc = WbNpcPackDocs("dbrow:gameobject_template", _wbPackId).FirstOrDefault(d => WbNpcUInt(d["body"]?["entry"]) == entry);
        if (doc is null) return;
        var row = doc["body"]!.DeepClone().AsObject();
        row["name"] = name; row["data3"] = _wbChestConsumable ? 1 : 0;
        row["mingold"] = _wbChestGoldMin; row["maxgold"] = _wbChestGoldMax;
        if (_wbChestLook is { Pack: false } look) { row["displayId"] = look.DisplayId; row["size"] = Math.Round(look.Size, 3); }
        uint lootId = WbNpcUInt(row["data1"]);
        var items = new JsonArray { new JsonObject { ["kind"] = "dbrow:gameobject_template", ["body"] = row } };
        foreach (var l in _wbChestLoot)
            items.Add(new JsonObject
            {
                ["kind"] = "dbrow:gameobject_loot_template",
                ["body"] = new JsonObject
                {
                    ["entry"] = lootId, ["item"] = l.Item, ["ChanceOrQuestChance"] = Math.Round(l.Chance, 2), ["groupid"] = 0,
                    ["mincountOrRef"] = l.Min, ["maxcount"] = l.Max, ["condition_id"] = 0, ["patch_min"] = 0, ["patch_max"] = 10,
                },
            });
        foreach (var old in WbNpcPackDocs("dbrow:gameobject_loot_template", _wbPackId)
                     .Where(d => WbNpcUInt(d["body"]?["entry"]) == lootId && _wbChestLoot.All(l => l.Item != WbNpcUInt(d["body"]?["item"]))))
            items.Add(WbDeleteItem(old));
        WbOp($"edit chest {name}", _wbClient.ContentAsync(SuiWebAppUrl, _wbPackId, $"edit chest {name}", items.ToJsonString()), _ =>
        {
            _wbGoKnown[entry] = (WbNpcUInt(row["displayId"]), WbNpcFloat(row["size"], 1f), name);
            _wbDocsTask = null; WbRequestDocs();
        });
    }

    private void WbCreateChest(WbGoTemplate source, string name)
    {
        uint entry = Math.Max(WbNextId("dbrow:gameobject_template", "entry", WbTemplateBase), WbReservedGoEntry + 1);
        WbReservedGoEntry = entry;
        var loot = _wbChestLoot.ToList();
        int goldMin = _wbChestGoldMin, goldMax = _wbChestGoldMax;
        bool consumable = _wbChestConsumable;
        int pack = _wbPackId;
        string label = $"chest {name} ({entry})";
        _wbChestSaving = true;
        async Task<WorldPackClient.Reply> Create()
        {
            try
            {
                var row = JsonNode.Parse(await _wbClient.GameObjectRowAsync(SuiWebAppUrl, source.Entry))?["obj"] as JsonObject;
                if (row is null) return new WorldPackClient.Reply { Error = $"Could not read the source chest {source.Entry}." };
                // Keep every column of the stock row (lock, flags, size, faction); change only what a custom chest owns.
                row["entry"] = entry; row["patch"] = 0; row["name"] = name;
                row["data1"] = entry;                    // CHEST data1 = loot id (gameobject_loot_template.entry)
                row["data3"] = consumable ? 1 : 0;       // CHEST data3 = consumable: despawn after looting
                row["mingold"] = goldMin; row["maxgold"] = goldMax;
                var items = new JsonArray { new JsonObject { ["kind"] = "dbrow:gameobject_template", ["body"] = row } };
                foreach (var l in loot)
                    items.Add(new JsonObject
                    {
                        ["kind"] = "dbrow:gameobject_loot_template",
                        ["body"] = new JsonObject
                        {
                            ["entry"] = entry, ["item"] = l.Item, ["ChanceOrQuestChance"] = Math.Round(l.Chance, 2), ["groupid"] = 0,
                            ["mincountOrRef"] = l.Min, ["maxcount"] = l.Max, ["condition_id"] = 0, ["patch_min"] = 0, ["patch_max"] = 10,
                        },
                    });
                return await _wbClient.ContentAsync(SuiWebAppUrl, pack, label, items.ToJsonString());
            }
            finally { _wbChestSaving = false; }
        }
        WbOp(label, Create(), _ =>
        {
            _wbGoKnown[entry] = (source.DisplayId, source.Size, name);
            _wbGoPick = new WbGoTemplate(entry, 3, source.DisplayId, name, source.Size, true);
            _wbChestEditEntry = entry;
            _wbDocsTask = null;
            WbRequestDocs();
            _wbMessage = $"{name} created and selected in Place objects: choose Place selected object, then click the ground.";
        });
    }

    // ── previews ─────────────────────────────────────────────────────────────

    private void WbSetGameObjectCursor((uint Display, float Size, Vector3 At, float Facing)? ghost)
    {
        if (ghost is not { } g || _gameObjectDisplays?.ModelPath(g.Display) is not { } model)
        {
            if (_wbGoCursorSig is not null) { _doodads?.RemoveDynamic(WbGoCursorKey); _wmo?.RemoveDynamic(WbGoCursorKey); }
            _wbGoCursorSig = null;
            return;
        }
        string sig = Inv($"{g.Display}|{g.Size}|{g.At.X:F2}|{g.At.Y:F2}|{g.At.Z:F2}|{g.Facing:F3}");
        if (sig == _wbGoCursorSig) return;
        _wbGoCursorSig = WbPlaceGameObjectModel(WbGoCursorKey, model, g.At, g.Facing, g.Size) ? sig : null;
    }

    /// <summary>The same transforms the live gameobject renderer uses, so a preview stands exactly where the server object will.</summary>
    private bool WbPlaceGameObjectModel(ulong key, string model, Vector3 at, float facing, float size)
    {
        float scale = size > 0.0001f ? size : 1f;
        if (Path.GetExtension(model).Equals(".wmo", StringComparison.OrdinalIgnoreCase))
        {
            _doodads?.RemoveDynamic(key);
            var t = WmoRenderer.DynamicGameObjectTransform(at, facing, scale);
            return _wmo?.TryUpdateDynamicTransform(key, t) == true ||
                   _wmo?.AddDynamic(key, model, t) == WmoRenderer.DynamicPlacement.Placed;
        }
        _wmo?.RemoveDynamic(key);
        var m = DynamicM2GameObjectTransform(at, facing, scale);
        return _doodads?.TryUpdateDynamicTransform(key, m) == true ||
               _doodads?.AddDynamic(key, model, m) == MSUIClient.World.Doodads.DoodadRenderer.DynamicPlacement.Placed;
    }

    /// <summary>Saved pack objects on this map draw as models - offline always, online until the server shows the
    /// published object there. Unknown stock templates are looked up once (display id) through /GameObjects/Search.</summary>
    private void WbSyncGameObjectPreviews()
    {
        var want = new Dictionary<ulong, (string Model, Vector3 At, float Facing, float Size)>();
        if (_creatorWorldRequested) EnsureGameObjectDisplays();
        if (_creatorWorldRequested && _wbDocs is not null && _controller is not null && _gameObjectDisplays is not null)
        {
            if (_wbGoDocsRevision != _wbNpcPreviewRevision || _wbGoSpawnCacheMap != _config.Start.Map)
            {
                // Rescan the docs only when they reload (or the map changes), not every frame.
                _wbGoDocsRevision = _wbNpcPreviewRevision;
                _wbGoSpawnCacheMap = _config.Start.Map;
                foreach (var t in WbNpcPackDocs("dbrow:gameobject_template").Select(d => d["body"] as JsonObject).OfType<JsonObject>())
                    _wbGoKnown[WbNpcUInt(t["entry"])] = (WbNpcUInt(t["displayId"]), WbNpcFloat(t["size"], 1f), t["name"]?.ToString() ?? "object");
                _wbGoSpawnCache = WbGoSpawnDocs(null).ToList();
            }
            var active = _wbState?.Packs.Where(p => p.Enabled || p.Id == _wbPackId).Select(p => p.Id).ToHashSet() ?? new HashSet<int> { _wbPackId };
            foreach (var doc in _wbGoSpawnCache.Where(d => active.Contains((int?)d["packId"] ?? 0)))
            {
                var b = doc["body"]!.AsObject();
                var at = WbGoPosition(b);
                if (Vector3.DistanceSquared(at, _controller.Position) > 350f * 350f) continue;
                uint entry = WbNpcUInt(b["id"]);
                if (!_wbGoKnown.TryGetValue(entry, out var known)) { WbLookupGameObject(entry); continue; }
                if (_gameObjectDisplays.ModelPath(known.Display) is not { } model) continue;
                if (WbLiveGameObjectAt(entry, at)) continue;
                var shown = WbNpcUInt(b["guid"]) == _wbGoSelected && _wbGoDraft is { } draft ? draft : b;   // unsaved edits preview in place
                want[WbGoPreviewKey(WbNpcUInt(b["guid"]))] = (model, WbGoPosition(shown), WbNpcFloat(shown["orientation"]), known.Size);
            }
        }
        foreach (var key in _wbGoPreviews.Keys.Where(k => !want.ContainsKey(k)).ToList())
        {
            _doodads?.RemoveDynamic(key); _wmo?.RemoveDynamic(key);
            _wbGoPreviews.Remove(key);
        }
        foreach (var (key, p) in want)
        {
            string sig = Inv($"{p.Model}|{p.At.X:F2}|{p.At.Y:F2}|{p.At.Z:F2}|{p.Facing:F3}|{p.Size}");
            if (_wbGoPreviews.TryGetValue(key, out var have) && have == sig) continue;
            if (WbPlaceGameObjectModel(key, p.Model, p.At, p.Facing, p.Size)) _wbGoPreviews[key] = sig;
            else _wbGoPreviews.Remove(key);   // model still streaming: retried next frame
        }
        WbPumpGoLookup();
    }

    private bool WbLiveGameObjectAt(uint entry, Vector3 at)
    {
        foreach (var e in _entities.Entities.Values)
            if (e.IsGameObject && e.Entry == entry && Vector3.DistanceSquared(e.Position, at) < 2.25f) return true;
        return false;
    }

    private void WbLookupGameObject(uint entry)
    {
        if (_wbGoLookupTask is not null || _wbGoLookupFailed.Contains(entry) || SuiWebAppUrl.Length == 0) return;
        _wbGoLookupEntry = entry;
        _wbGoLookupTask = _wbClient.GameObjectSearchAsync(SuiWebAppUrl, entry.ToString(CultureInfo.InvariantCulture), null, 5);
    }

    private void WbPumpGoLookup()
    {
        if (_wbGoLookupTask is not { IsCompleted: true } t) return;
        _wbGoLookupTask = null;
        try
        {
            var o = t.IsCompletedSuccessfully
                ? (JsonNode.Parse(t.Result)?["objects"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(x => WbNpcUInt(x["entry"]) == _wbGoLookupEntry)
                : null;
            if (o is null) { _wbGoLookupFailed.Add(_wbGoLookupEntry); return; }
            _wbGoKnown[_wbGoLookupEntry] = (WbNpcUInt(o["displayId"]), WbNpcFloat(o["size"], 1f), o["name"]?.ToString() ?? "object");
        }
        catch { _wbGoLookupFailed.Add(_wbGoLookupEntry); }
    }
}

using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// World Builder: NPC placements as human tools (shared_docs/WORLD_BUILDER.md §4, 2026-10-14 usability pass).
//
// The placement list (go to / edit / move / remove), the model ghost under the pointer while placing, saved patrol
// routes drawn in the world, and deletes that take an NPC's dependent rows with it (spawns, routes, groups, vendor,
// trainer, gossip, quest links) - one audited op each, undoable. Placement itself stays in Content.cs
// (DrawWbSpawnSection / WbTrySpawnClick / WbPackItems / WbPatrolItems), shared with the script driver.
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class GameLoop
{
    private const ulong WbNpcCursorGhostKey = 0xB1B1_FFFF_FFFF_FF00UL;
    private uint _wbSpawnSelected;
    private float _wbSpawnYaw;                 // degrees; the ghost and the saved spawn face this way
    private bool _wbNpcCursorGhost;
    private Action? _wbNpcPendingSwitch;       // a form switch waiting on "discard unsaved changes?"

    private JsonObject? WbNpcTemplateBody(uint entry) =>
        WbNpcPackDocs("dbrow:creature_template").FirstOrDefault(d => WbNpcUInt(d["body"]?["entry"]) == entry)?["body"] as JsonObject;

    private string WbNpcName(uint entry) => WbNpcTemplateBody(entry)?["name"]?.ToString() ?? "NPC";

    private static Vector3 WbSpawnPosition(JsonObject b) =>
        new(WbNpcFloat(b["position_x"]), WbNpcFloat(b["position_y"]), WbNpcFloat(b["position_z"]));

    // ── unsaved form guard ───────────────────────────────────────────────────

    private bool WbNpcFormDirty =>
        !_wbNpcSaving && (_wbNpcEditEntry != 0 ? WbNpcFormSignature() != _wbNpcLoadedFormSignature : WbText(_wbNpcName).Length > 0);

    /// <summary>Run <paramref name="next"/> now, or after the person confirms discarding unsaved NPC edits.</summary>
    private void WbNpcGuard(Action next)
    {
        if (WbNpcFormDirty) _wbNpcPendingSwitch = next;
        else next();
    }

    private void DrawWbNpcGuardPrompt()
    {
        if (_wbNpcPendingSwitch is null) return;
        ImGui.TextColored(new Vector4(1f, .72f, .3f, 1f), "This NPC has unsaved changes.");
        if (CreatorButton("Discard changes and continue")) { var next = _wbNpcPendingSwitch; _wbNpcPendingSwitch = null; next(); }
        ObserveWorldBuilderUiItem("Discard changes and continue");
        ImGui.SameLine();
        if (CreatorButton("Keep editing")) _wbNpcPendingSwitch = null;
        ImGui.Separator();
    }

    // ── deletes ──────────────────────────────────────────────────────────────

    private static JsonObject WbDeleteItem(JsonObject doc) =>
        new() { ["kind"] = doc["kind"]?.ToString(), ["key"] = doc["docKey"]?.ToString(), ["body"] = null };

    /// <summary>A spawn and what hangs off it: its patrol points and its group rows (a removed leader dissolves its group).</summary>
    private List<JsonObject> WbSpawnDependents(uint guid)
    {
        var docs = WbNpcPackDocs("dbrow:creature", _wbPackId).Where(d => WbNpcUInt(d["body"]?["guid"]) == guid).ToList();
        docs.AddRange(WbNpcPackDocs("dbrow:creature_movement", _wbPackId).Where(d => WbNpcUInt(d["body"]?["id"]) == guid));
        docs.AddRange(WbNpcPackDocs("dbrow:creature_groups", _wbPackId)
            .Where(d => WbNpcUInt(d["body"]?["member_guid"]) == guid || WbNpcUInt(d["body"]?["leader_guid"]) == guid));
        return docs;
    }

    /// <summary>Every pack row that belongs to an authored NPC: template, its spawns (+ routes, groups), equipment, vendor,
    /// trainer, gossip menu/options/texts and quest giver/ender links.</summary>
    private List<JsonObject> WbNpcDependents(uint entry, out int spawns, out int questLinks)
    {
        var t = WbNpcTemplateBody(entry);
        uint menu = WbNpcUInt(t?["gossip_menu_id"]), equip = WbNpcUInt(t?["equipment_id"]);
        var all = (_wbDocs ?? new()).OfType<JsonObject>().Where(d => (int?)d["packId"] == _wbPackId && d["body"] is JsonObject).ToList();
        var texts = all.Where(d => d["kind"]?.ToString() == "dbrow:gossip_menu" && menu > 0 && WbNpcUInt(d["body"]?["entry"]) == menu)
            .Select(d => WbNpcUInt(d["body"]?["text_id"])).ToHashSet();
        var result = all.Where(d => d["kind"]?.ToString() switch
        {
            "dbrow:creature_template" or "dbrow:npc_vendor" or "dbrow:npc_trainer" => WbNpcUInt(d["body"]?["entry"]) == entry,
            "dbrow:creature_questrelation" or "dbrow:creature_involvedrelation" => WbNpcUInt(d["body"]?["id"]) == entry,
            "dbrow:creature_equip_template" => equip >= WbTemplateBase && WbNpcUInt(d["body"]?["entry"]) == equip,
            "dbrow:gossip_menu" => menu > 0 && WbNpcUInt(d["body"]?["entry"]) == menu,
            "dbrow:gossip_menu_option" => menu > 0 && WbNpcUInt(d["body"]?["menu_id"]) == menu,
            "dbrow:npc_text" => texts.Contains(WbNpcUInt(d["body"]?["ID"])) && WbNpcUInt(d["body"]?["ID"]) >= WbTemplateBase,
            "dbrow:broadcast_text" => WbNpcUInt(d["body"]?["entry"]) == entry,
            _ => false,
        }).ToList();
        questLinks = result.Count(d => d["kind"]?.ToString() is "dbrow:creature_questrelation" or "dbrow:creature_involvedrelation");
        var spawnGuids = all.Where(d => d["kind"]?.ToString() == "dbrow:creature" && WbNpcUInt(d["body"]?["id"]) == entry)
            .Select(d => WbNpcUInt(d["body"]?["guid"])).ToList();
        spawns = spawnGuids.Count;
        foreach (uint g in spawnGuids) result.AddRange(WbSpawnDependents(g));
        return result.GroupBy(d => (d["kind"]?.ToString(), d["docKey"]?.ToString())).Select(g => g.First()).ToList();
    }

    private void WbDeleteDocs(string label, IEnumerable<JsonObject> docs, Action? then = null)
    {
        var items = new JsonArray(docs.Select(d => (JsonNode)WbDeleteItem(d)).ToArray());
        if (items.Count == 0) { _wbMessage = "Nothing to remove."; return; }
        WbOp(label, _wbClient.ContentAsync(SuiWebAppUrl, _wbPackId, label, items.ToJsonString()), _ =>
        {
            _wbDocsTask = null; WbRequestDocs(); then?.Invoke();
        });
    }

    /// <summary>Delete (authored NPC) or restore-the-original (pack version of a stock NPC) buttons for the open form.</summary>
    private void DrawWbNpcDeleteButtons()
    {
        if (_wbNpcEditEntry == 0 || _wbNpcSaving) return;
        if (_wbNpcStockSourceEntry != 0) return;   // not saved in the pack yet: nothing to delete
        var replacement = WbNpcPackDocs("npc-replacement", _wbPackId).FirstOrDefault(d => WbNpcUInt(d["body"]?["replacementEntry"]) == _wbNpcEditEntry);
        uint entry = _wbNpcEditEntry;
        string name = WbNpcName(entry);
        ImGui.BeginDisabled(_wbOps.Count > 0);
        if (replacement is not null)
        {
            if (WbConfirmButton("Restore the original NPC",
                    $"Remove this pack's version of {name}? The world NPC goes back to its original look, name and services after the next publish."))
            {
                var docs = WbNpcDependents(entry, out _, out _).Where(d => d["kind"]?.ToString() != "dbrow:creature").ToList();
                docs.Add(replacement);
                WbDeleteDocs($"restore original NPC {name}", docs, WbNewNpc);
            }
        }
        else
        {
            var deps = WbNpcDependents(entry, out int spawns, out int quests);
            if (WbConfirmButton("Delete this NPC",
                    $"Delete {name} from this pack, with its {spawns} placement(s)" + (quests > 0 ? $" and its {quests} quest giver/ender link(s)" : "") +
                    "? Quests that used this NPC keep their text but lose this giver. Undo brings everything back."))
                WbDeleteDocs($"delete NPC {name}", deps, WbNewNpc);
        }
        ImGui.EndDisabled();
    }

    // ── placements list ──────────────────────────────────────────────────────

    private void DrawWbSpawnList()
    {
        int map = _config.Start.Map;
        var spawns = WbNpcPackDocs("dbrow:creature", _wbPackId).Select(d => d["body"]!.AsObject())
            .Where(b => (int?)b["map"] == map).ToList();
        if (spawns.Count == 0) { ImGui.TextDisabled("Nothing placed on this map yet."); return; }
        Vector3 me = _controller?.Position ?? Vector3.Zero;
        if (ImGui.BeginChild("##wb-spawn-list", new Vector2(0, 130f * CreatorUiScale), true))
        {
            foreach (var b in spawns.OrderBy(b => Vector3.Distance(WbSpawnPosition(b), me)).Take(150))
            {
                uint guid = WbNpcUInt(b["guid"]);
                string kind = WbNpcUInt(b["movement_type"]) == 2 ? "  (patrol)" : "";
                string label = Inv($"{WbNpcName(WbNpcUInt(b["id"]))}{kind}  {Vector2.Distance(new(me.X, me.Y), new(WbNpcFloat(b["position_x"]), WbNpcFloat(b["position_y"]))):F0} yd##spawn{guid}");
                if (ImGui.Selectable(label, guid == _wbSpawnSelected)) _wbSpawnSelected = guid;
                ObserveWorldBuilderUiItem("Placement: " + WbNpcName(WbNpcUInt(b["id"])));
            }
        }
        ImGui.EndChild();   // always, whatever BeginChild returned
        if (spawns.FirstOrDefault(b => WbNpcUInt(b["guid"]) == _wbSpawnSelected) is not { } sel) return;
        var at = WbSpawnPosition(sel);
        uint selGuid = _wbSpawnSelected, selEntry = WbNpcUInt(sel["id"]);
        WbMarker(at, WbNpcName(selEntry), 0xFFFFFFFFu, 1.5f);
        if (_controller is not null && CreatorButton("Go to##spawn")) _controller.Teleport(at.X, at.Y, at.Z + 8f);
        ImGui.SameLine();
        if (CreatorButton("Edit this NPC")) WbNpcGuard(() => { WbLoadPackNpc(selEntry, selGuid); _wbNpcPage = 0; });
        ObserveWorldBuilderUiItem("Edit this NPC");
        ImGui.BeginDisabled(_wbOps.Count > 0);
        WbPickButton("Move it", "the new spot for " + WbNpcName(selEntry), to =>
        {
            var moved = sel.DeepClone().AsObject();
            moved["position_x"] = Math.Round(to.X, 2); moved["position_y"] = Math.Round(to.Y, 2); moved["position_z"] = Math.Round(to.Z, 2);
            WbSaveNpcPlacement($"move {WbNpcName(selEntry)}", new JsonArray { new JsonObject { ["kind"] = "dbrow:creature", ["body"] = moved } });
        });
        ImGui.SameLine();
        var deps = WbSpawnDependents(selGuid);
        if (WbConfirmButton("Remove placement", $"Remove this {WbNpcName(selEntry)} placement" +
                (deps.Count > 1 ? " with its patrol route / group links" : "") + "? The NPC itself stays in the pack."))
            WbDeleteDocs($"remove {WbNpcName(selEntry)} placement", deps, () => _wbSpawnSelected = 0);
        ImGui.EndDisabled();
    }

    // ── in-world: ghost under the pointer, saved patrol routes ──────────────

    /// <summary>While placing single NPCs: the NPC's own model stands under the pointer, facing <see cref="_wbSpawnYaw"/>.</summary>
    private void WbSyncNpcCursorGhost()
    {
        bool want = _wbSpawnArmed && _wbSpawnMode == 0 && _wbCursor is not null && !ImGui.GetIO().WantCaptureMouse &&
                    WbNpcTemplateBody(_wbSpawnEntry) is { } t && WbNpcUInt(t["display_id1"]) > 0;
        if (!want)
        {
            if (_wbNpcCursorGhost) { _entities.RemoveSynthetic(WbNpcCursorGhostKey); _wbNpcCursorGhost = false; }
            return;
        }
        var tpl = WbNpcTemplateBody(_wbSpawnEntry)!;
        uint display = WbNpcUInt(tpl["display_id1"]);
        float facing = _wbSpawnYaw * MathF.PI / 180f;
        if (_entities.TryGet(WbNpcCursorGhostKey, out var ghost) && ghost.Entry == _wbSpawnEntry)
        {
            ghost.Position = _wbCursor!.Value;
            ghost.Orientation = facing;
            return;
        }
        float scale = WbNpcFloat(tpl["display_scale1"], 1f); if (scale <= 0) scale = 1f;
        var fields = ObjectFields.ForSyntheticUnit((int)display, scale);
        fields.SetU32(ObjectFields.OBJECT_ENTRY, _wbSpawnEntry);
        _entities.AddSynthetic(new WorldEntity
        {
            Guid = WbNpcCursorGhostKey, Type = ObjectTypeId.Unit, Entry = _wbSpawnEntry, Fields = fields,
            Position = _wbCursor!.Value, Orientation = facing,
        });
        _wbNpcCursorGhost = true;
    }

    /// <summary>Saved patrol routes of this pack on this map, drawn while the NPC page is open (loop closed back to the start).</summary>
    private void WbDrawSavedPatrols()
    {
        if (_creatorPanel != CreatorPanel.World || _wbPage != "npcs" || _terrain is null) return;
        int map = _config.Start.Map;
        var leaders = WbNpcPackDocs("dbrow:creature", _wbPackId).Select(d => d["body"]!.AsObject())
            .Where(b => (int?)b["map"] == map && WbNpcUInt(b["movement_type"]) == 2).ToList();
        if (leaders.Count == 0) return;
        var moves = WbNpcPackDocs("dbrow:creature_movement", _wbPackId).Select(d => d["body"]!.AsObject())
            .GroupBy(b => WbNpcUInt(b["id"])).ToDictionary(g => g.Key, g => g.OrderBy(b => WbNpcUInt(b["point"])).ToList());
        var draw = ImGui.GetBackgroundDrawList();
        foreach (var leader in leaders)
        {
            if (!moves.TryGetValue(WbNpcUInt(leader["guid"]), out var route) || route.Count < 2) continue;
            var pts = route.Select(WbSpawnPosition).ToList();
            pts.Add(pts[0]);
            bool selected = WbNpcUInt(leader["guid"]) == _wbSpawnSelected;
            WbDrawPathLine(draw, pts, 0f, selected ? 0xF0FFFFFFu : 0xC040D0FFu, selected ? 3f : 2f, true);
        }
    }
}

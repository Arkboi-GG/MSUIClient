using System.Numerics;
using ImGuiNET;
using Silk.NET.Input;
using MSUIClient.Engine;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// NPC dev window EDITING: waypoint drawing, spawn moves and field edits that
// become change-set packets (Formats\DevChangeSet.cs) — NEVER applied locally,
// NEVER sent as GM commands. This partial owns the edit-mode state machine, the
// world-click handling, packet/preview bookkeeping and the edit-related window
// sections. Rendering of the working path lives in Program.DevWindow.Overlays.cs;
// file I/O lives in DevChangeSetFile.
//
// Input contract: the world-click drain (Program.Targeting.cs) calls
// HandleDevEditClick BEFORE the free-view router; an armed mode swallows every
// world click. Escape cancels via the pre-gate in UpdateSettingsInput.
// ─────────────────────────────────────────────────────────────────────────────

public sealed partial class GameLoop
{
    private enum DevEditMode { None, WaypointEdit, SpawnMove }

    private DevEditMode _devEditMode;
    private uint _devEditSpawnGuid;              // mangos.creature.guid being edited
    private uint _devEditEntry;

    // Waypoint editing: a mutable working copy of the resolved path.
    private readonly List<DevWaypointRow> _devEditPath = [];
    private DevWaypointRow[] _devEditPathBefore = [];
    private DevPathOrigin _devEditPathOrigin;
    private uint _devEditPathId;
    private int _devEditSelectedNode = -1;
    /// <summary>Node screen rects, filled by the overlay draw each frame (nameplate
    /// hit-rect idiom) and consumed by the click handler.</summary>
    private readonly List<(ScreenRect Rect, int Index)> _devEditNodeHits = [];

    // Spawn move: proposed position + optional model ghost.
    private Vector3? _devEditSpawnNewPos;
    private ulong _devEditGhostGuid;
    private const ulong DevGhostGuidBase = 0xF000_0000_00DE_0000;   // outside the creator range

    // Change set (one per session, saved on every mutation).
    private DevChangeSet? _devChanges;
    private string? _devChangeFilePath;
    /// <summary>Runtime-only previews per packet id so queued edits stay visible
    /// (green) until applied server-side. Never persisted.</summary>
    private readonly Dictionary<int, DevEditPreview> _devPacketPreviews = [];

    private sealed record DevEditPreview(
        string Type, Vector3[] Points, Vector3? From, uint Entry, float? Detection);

    // ── mode lifecycle ───────────────────────────────────────────────────────

    private void BeginDevWaypointEdit(uint spawnGuid, uint entry)
    {
        CancelDevEdit();
        DevWorldData? world = DevData.World;
        if (world is null) return;
        (_devEditPathOrigin, _, _devEditPathId, DevWaypointRow[]? nodes) =
            world.ResolvePath(spawnGuid, entry);
        _devEditPathBefore = nodes ?? [];
        _devEditPath.Clear();
        _devEditPath.AddRange(_devEditPathBefore);
        _devEditSpawnGuid = spawnGuid;
        _devEditEntry = entry;
        _devEditSelectedNode = _devEditPath.Count - 1;   // clicks append after the tail
        _devEditMode = DevEditMode.WaypointEdit;
    }

    private void BeginDevSpawnMove(uint spawnGuid, uint entry)
    {
        CancelDevEdit();
        _devEditSpawnGuid = spawnGuid;
        _devEditEntry = entry;
        _devEditSpawnNewPos = null;
        _devEditMode = DevEditMode.SpawnMove;
    }

    private void CancelDevEdit()
    {
        _devEditMode = DevEditMode.None;
        _devEditPath.Clear();
        _devEditPathBefore = [];
        _devEditSelectedNode = -1;
        _devEditNodeHits.Clear();
        _devEditSpawnNewPos = null;
        if (_devEditGhostGuid != 0)
        {
            _entities.RemoveSynthetic(_devEditGhostGuid);
            _devEditGhostGuid = 0;
        }
    }

    /// <summary>Escape pre-gate hook (UpdateSettingsInput): an armed edit mode owns
    /// Escape ahead of the whole game-menu ladder. Returns true when consumed.</summary>
    private bool ConsumeDevEditEscape()
    {
        if (_devEditMode == DevEditMode.None) return false;
        CancelDevEdit();
        return true;
    }

    // ── world-click handling (called from the Program.Targeting.cs drain) ────

    /// <summary>True = the click belonged to an armed dev edit mode and was consumed.
    /// Waypoint mode: left on a node selects (again deselects), left on ground inserts
    /// after the selected node (Shift+left MOVES the selected node instead), right on a
    /// node deletes it. Spawn-move mode: left on ground proposes the new position.</summary>
    private bool HandleDevEditClick(WorldMouseClick click)
    {
        if (_devEditMode == DevEditMode.None || !_devWindowOpen) return false;

        if (_devEditMode == DevEditMode.WaypointEdit)
        {
            int hit = HitTestDevEditNode(click.Position);
            if (click.Button == MouseButton.Left)
            {
                if (hit >= 0)
                {
                    _devEditSelectedNode = _devEditSelectedNode == hit ? -1 : hit;
                    return true;
                }
                if (!TryPickGround(click.Position, out Vector3 spot)) return true;
                if (ShiftHeld() && _devEditSelectedNode >= 0 &&
                    _devEditSelectedNode < _devEditPath.Count)
                {
                    _devEditPath[_devEditSelectedNode] =
                        _devEditPath[_devEditSelectedNode] with { Position = spot };
                    return true;
                }
                int insertAt = _devEditSelectedNode >= 0
                    ? Math.Min(_devEditSelectedNode + 1, _devEditPath.Count)
                    : _devEditPath.Count;
                _devEditPath.Insert(insertAt,
                    new DevWaypointRow(0, spot, 0f, 0, 0f, 0, _devEditPathId));
                _devEditSelectedNode = insertAt;
                return true;
            }
            if (click.Button == MouseButton.Right && hit >= 0)
            {
                _devEditPath.RemoveAt(hit);
                if (_devEditSelectedNode >= _devEditPath.Count)
                    _devEditSelectedNode = _devEditPath.Count - 1;
            }
            return true;   // an armed mode swallows every world click - no stray orders
        }

        // SpawnMove
        if (click.Button == MouseButton.Left && TryPickGround(click.Position, out Vector3 pos))
        {
            _devEditSpawnNewPos = pos;
            UpdateDevSpawnGhost(pos);
        }
        return true;
    }

    private int HitTestDevEditNode(Vector2 pixel)
    {
        for (int i = _devEditNodeHits.Count - 1; i >= 0; i--)
            if (_devEditNodeHits[i].Rect.Contains(pixel)) return _devEditNodeHits[i].Index;
        return -1;
    }

    /// <summary>Model ghost at the proposed spawn point when the creature is streamed
    /// (display id known); otherwise the overlay's marker-only preview stands alone.</summary>
    private void UpdateDevSpawnGhost(Vector3 position)
    {
        WorldEntity? streamed = null;
        foreach (WorldEntity unit in _entities.Units)
            if (unit.IsCreature && GuidInfo.High(unit.Guid) == GuidInfo.HighUnit &&
                (uint)(unit.Guid & 0xFFFFFF) == _devEditSpawnGuid)
            { streamed = unit; break; }
        if (streamed is null || streamed.DisplayId == 0) return;

        if (_devEditGhostGuid == 0) _devEditGhostGuid = DevGhostGuidBase + _devEditSpawnGuid;
        _entities.AddSynthetic(new WorldEntity
        {
            Guid = _devEditGhostGuid,
            Type = ObjectTypeId.Unit,
            Fields = ObjectFields.ForSyntheticUnit(streamed.DisplayId,
                streamed.Scale <= 0f ? 1f : streamed.Scale),
            Position = position,
            Orientation = streamed.Orientation,
        });
    }

    // ── commits → packets ────────────────────────────────────────────────────

    private void CommitDevWaypointEdit()
    {
        if (_devEditMode != DevEditMode.WaypointEdit) return;
        // Renumber gapless 1-based - the WaypointManager::Cleanup contract.
        var after = _devEditPath.Select((n, i) => n with { Point = (uint)(i + 1) }).ToList();

        var target = _devEditPathOrigin == DevPathOrigin.Template
            ? new Dictionary<string, object?>
            {
                ["source"] = "creature_movement_template",
                ["entry"] = _devEditEntry,
                ["pathId"] = _devEditPathId,
            }
            : new Dictionary<string, object?>
            {
                // Guid-origin AND brand-new paths both land in the per-guid table:
                // an edit meant for one spawn must never rewrite a shared template.
                ["source"] = "creature_movement",
                ["id"] = _devEditSpawnGuid,
                ["entry"] = _devEditEntry,
                ["pathId"] = 0u,
            };
        int id = AddDevChangePacket("waypoint-path-replace", target,
            new Dictionary<string, object?> { ["points"] = WaypointDicts(_devEditPathBefore) },
            new Dictionary<string, object?> { ["points"] = WaypointDicts(after) },
            _devEditPathOrigin == DevPathOrigin.Template
                ? new Dictionary<string, object?>
                    { ["affectsSpawnCount"] = DevSpawnCountForEntry(_devEditEntry) }
                : null);
        _devPacketPreviews[id] = new DevEditPreview("waypoint-path-replace",
            after.Select(n => n.Position).ToArray(), null, _devEditEntry, null);
        CancelDevEdit();
    }

    private void CommitDevSpawnMove()
    {
        if (_devEditMode != DevEditMode.SpawnMove || _devEditSpawnNewPos is not { } to) return;
        DevSpawnRow? spawn = DevData.World?.SpawnsByGuid.GetValueOrDefault(_devEditSpawnGuid);
        if (spawn is null) { CancelDevEdit(); return; }
        int id = AddDevChangePacket("spawn-move",
            DevSpawnTarget(spawn),
            new Dictionary<string, object?>
            {
                ["position_x"] = spawn.Position.X, ["position_y"] = spawn.Position.Y,
                ["position_z"] = spawn.Position.Z, ["orientation"] = spawn.Orientation,
            },
            new Dictionary<string, object?>
            {
                ["position_x"] = to.X, ["position_y"] = to.Y,
                ["position_z"] = to.Z, ["orientation"] = spawn.Orientation,
            });
        _devPacketPreviews[id] = new DevEditPreview("spawn-move",
            [to], spawn.Position, spawn.Entry, null);
        CancelDevEdit();
    }

    private static List<Dictionary<string, object?>> WaypointDicts(IEnumerable<DevWaypointRow> nodes) =>
        nodes.Select(n => new Dictionary<string, object?>
        {
            ["point"] = n.Point, ["x"] = n.Position.X, ["y"] = n.Position.Y, ["z"] = n.Position.Z,
            ["orientation"] = n.Orientation, ["waittime"] = n.WaitMs,
            ["wander_distance"] = n.WanderDistance, ["script_id"] = n.ScriptId,
        }).ToList();

    private Dictionary<string, object?> DevSpawnTarget(DevSpawnRow spawn) => new()
    {
        ["table"] = "creature", ["guid"] = spawn.Guid, ["entry"] = spawn.Entry, ["map"] = spawn.Map,
    };

    private int DevSpawnCountForEntry(uint entry)
    {
        DevWorldData? world = DevData.World;
        if (world is null) return 0;
        int count = 0;
        foreach (DevSpawnRow spawn in world.SpawnsByGuid.Values)
            if (spawn.EntryPool.Contains(entry)) count++;
        return count;
    }

    // ── change-set bookkeeping (saved to disk on every mutation) ─────────────

    private int AddDevChangePacket(string type, Dictionary<string, object?> target,
        Dictionary<string, object?> before, Dictionary<string, object?> after,
        Dictionary<string, object?>? context = null)
    {
        _devChanges ??= new DevChangeSet
        {
            Session = new DevChangeSession
            {
                CreatedUtc = DateTime.UtcNow,
                Character = _net?.PlayerName is { Length: > 0 } name ? name : "offline",
                SourceSnapshotUtc = DevData.World?.FetchedUtc ?? DateTime.MinValue,
                SuiBase = Settings.DevWindow.SuiBaseUrl,
            },
        };
        var packet = new DevChangePacket
        {
            Id = _devChanges.Packets.Count == 0 ? 1 : _devChanges.Packets.Max(p => p.Id) + 1,
            Type = type,
            Target = target,
            Before = before,
            After = after,
            Context = context,
        };
        _devChanges.Packets.Add(packet);
        SaveDevChanges();
        Console.WriteLine($"[dev-edit] queued packet {packet.Id} {type} -> {_devChangeFilePath}");
        return packet.Id;
    }

    private void SaveDevChanges()
    {
        if (_devChanges is null) return;
        try
        {
            _devChangeFilePath = DevChangeSetFile.Save(_config.RepoRoot, _devChangeFilePath, _devChanges);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[dev-edit] change-set save FAILED: {ex.Message}");
        }
    }

    private void RemoveDevChangePacket(int id)
    {
        if (_devChanges is null) return;
        _devChanges.Packets.RemoveAll(p => p.Id == id);
        _devPacketPreviews.Remove(id);
        SaveDevChanges();
    }

    /// <summary>The pending detection_range override for an entry (queued template-field
    /// packet), so the drawn aggro discs preview the edit before it is ever applied.</summary>
    private float? DevPendingDetection(uint entry)
    {
        foreach (DevEditPreview preview in _devPacketPreviews.Values)
            if (preview.Type == "template-field" && preview.Entry == entry)
                return preview.Detection;
        return null;
    }

    /// <summary>Overlay hook: a queued detection_range edit overrides the fetched
    /// template so the discs draw the PROPOSED radius (DB value untouched).</summary>
    private NpcTemplateInfo? ApplyDevPendingTemplate(NpcTemplateInfo? tpl, uint entry)
    {
        if (DevPendingDetection(entry) is not { } detection) return tpl;
        tpl ??= new NpcTemplateInfo(entry, "", 1, 1, 0, 18f, 5f, 0f, 0, 0, 0);
        return tpl with { DetectionRange = detection };
    }

    // ── window sections (UI only - state above, rendering in Overlays) ───────

    // Staged field-edit buffers, reloaded whenever the inspected spawn changes.
    private uint _devFieldSpawnGuid;
    private int _devFieldSpawnMin, _devFieldSpawnMax, _devFieldMoveType;
    private float _devFieldWander, _devFieldDetection;
    private int _devFieldNodeWait;   // selected waypoint's waittime (ms)

    /// <summary>Edit controls under the Selected NPC section: arm/commit/cancel for the
    /// two click modes, staged field editors with Queue buttons. Called with the
    /// currently inspected creature's spawn row (null when unknown).</summary>
    private void DrawDevEditControls(DevSpawnRow? spawn, uint entry, NpcTemplateInfo? tpl)
    {
        ImGui.Spacing();
        ImGui.TextDisabled("EDIT (queued into the change set - nothing touches the DB)");

        if (_devEditMode != DevEditMode.None)
        {
            DrawDevActiveEditControls();
            return;
        }
        if (spawn is null)
        {
            ImGui.TextDisabled("editing needs this spawn's DB row (see above)");
            return;
        }

        if (ImGui.Button("Edit path")) BeginDevWaypointEdit(spawn.Guid, entry);
        ImGui.SameLine();
        if (ImGui.Button("Move spawn")) BeginDevSpawnMove(spawn.Guid, entry);

        // Staged per-spawn fields.
        if (_devFieldSpawnGuid != spawn.Guid)
        {
            _devFieldSpawnGuid = spawn.Guid;
            _devFieldSpawnMin = (int)spawn.SpawnSecsMin;
            _devFieldSpawnMax = (int)spawn.SpawnSecsMax;
            _devFieldWander = spawn.WanderDistance;
            _devFieldMoveType = (int)spawn.MovementType;
            _devFieldDetection = tpl?.DetectionRange ?? 18f;
        }
        float w = CreatorControlWidth;
        ImGui.SetNextItemWidth(w);
        ImGui.InputInt("respawn min (s)", ref _devFieldSpawnMin);
        ImGui.SetNextItemWidth(w);
        ImGui.InputInt("respawn max (s)", ref _devFieldSpawnMax);
        ImGui.SetNextItemWidth(w);
        ImGui.Combo("movement_type", ref _devFieldMoveType, "0 idle\0" + "1 random (wander)\0" + "2 waypoint\0");
        ImGui.SetNextItemWidth(w);
        ImGui.InputFloat("wander_distance", ref _devFieldWander, 1f, 5f, "%.1f");
        if (ImGui.Button("Queue spawn changes")) QueueDevSpawnFieldPackets(spawn);

        ImGui.Spacing();
        ImGui.SetNextItemWidth(w);
        ImGui.InputFloat("detection_range", ref _devFieldDetection, 1f, 5f, "%.1f");
        int affected = DevSpawnCountForEntry(entry);
        ImGui.TextColored(new Vector4(1f, 0.65f, 0.3f, 1f),
            $"per-ENTRY: affects {affected} spawn(s) of entry {entry}");
        if (ImGui.Button("Queue detection_range change")) QueueDevDetectionPacket(spawn, entry, tpl);
    }

    private void DrawDevActiveEditControls()
    {
        if (_devEditMode == DevEditMode.WaypointEdit)
        {
            ImGui.TextColored(new Vector4(0.4f, 1f, 0.55f, 1f),
                $"PATH EDIT - {_devEditPath.Count} nodes " +
                $"({(_devEditPathOrigin == DevPathOrigin.Template ? "from template - saves as per-guid" : "per-guid")})");
            ImGui.TextDisabled("left: select node / add after selected  -  Shift+left: move selected");
            ImGui.TextDisabled("right on node: delete  -  Esc: cancel");
            if (_devEditSelectedNode >= 0 && _devEditSelectedNode < _devEditPath.Count)
            {
                DevWaypointRow node = _devEditPath[_devEditSelectedNode];
                _devFieldNodeWait = (int)node.WaitMs;
                ImGui.SetNextItemWidth(CreatorControlWidth);
                if (ImGui.InputInt($"node {_devEditSelectedNode + 1} waittime (ms)",
                        ref _devFieldNodeWait))
                    _devEditPath[_devEditSelectedNode] =
                        node with { WaitMs = (uint)Math.Max(0, _devFieldNodeWait) };
            }
            if (ImGui.Button("Commit path")) CommitDevWaypointEdit();
        }
        else
        {
            ImGui.TextColored(new Vector4(0.4f, 1f, 0.55f, 1f),
                _devEditSpawnNewPos is { } p
                    ? $"SPAWN MOVE - new position ({p.X:0.#}, {p.Y:0.#}, {p.Z:0.#})"
                    : "SPAWN MOVE - left-click the ground to place");
            ImGui.TextDisabled("Esc: cancel");
            if (_devEditSpawnNewPos is not null && ImGui.Button("Commit move"))
                CommitDevSpawnMove();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel")) CancelDevEdit();
    }

    private void QueueDevSpawnFieldPackets(DevSpawnRow spawn)
    {
        uint min = (uint)Math.Max(0, _devFieldSpawnMin);
        uint max = (uint)Math.Max(0, _devFieldSpawnMax);
        if (min != spawn.SpawnSecsMin || max != spawn.SpawnSecsMax)
            AddDevChangePacket("spawn-timer", DevSpawnTarget(spawn),
                new Dictionary<string, object?>
                {
                    ["spawntimesecsmin"] = spawn.SpawnSecsMin,
                    ["spawntimesecsmax"] = spawn.SpawnSecsMax,
                },
                new Dictionary<string, object?>
                {
                    ["spawntimesecsmin"] = min, ["spawntimesecsmax"] = max,
                });

        var beforeFields = new Dictionary<string, object?>();
        var afterFields = new Dictionary<string, object?>();
        if ((uint)_devFieldMoveType != spawn.MovementType)
        {
            beforeFields["movement_type"] = spawn.MovementType;
            afterFields["movement_type"] = (uint)_devFieldMoveType;
        }
        if (MathF.Abs(_devFieldWander - spawn.WanderDistance) > 0.01f)
        {
            beforeFields["wander_distance"] = spawn.WanderDistance;
            afterFields["wander_distance"] = _devFieldWander;
        }
        if (afterFields.Count > 0)
            AddDevChangePacket("spawn-field", DevSpawnTarget(spawn), beforeFields, afterFields);
    }

    private void QueueDevDetectionPacket(DevSpawnRow spawn, uint entry, NpcTemplateInfo? tpl)
    {
        float before = tpl?.DetectionRange ?? 18f;
        if (MathF.Abs(_devFieldDetection - before) < 0.01f) return;
        int id = AddDevChangePacket("template-field",
            new Dictionary<string, object?> { ["table"] = "creature_template", ["entry"] = entry },
            new Dictionary<string, object?> { ["detection_range"] = before },
            new Dictionary<string, object?> { ["detection_range"] = _devFieldDetection },
            new Dictionary<string, object?> { ["affectsSpawnCount"] = DevSpawnCountForEntry(entry) });
        _devPacketPreviews[id] = new DevEditPreview("template-field",
            [], null, entry, _devFieldDetection);
    }

    /// <summary>The Change set section: file path, per-packet rows with revert, save.</summary>
    private void DrawDevChangeSetSection()
    {
        if (!ImGui.CollapsingHeader("Change set", ImGuiTreeNodeFlags.DefaultOpen)) return;
        if (_devChanges is null || _devChanges.Packets.Count == 0)
        {
            ImGui.TextDisabled("no queued changes this session");
            return;
        }
        ImGui.TextDisabled(_devChangeFilePath ?? "(unsaved)");
        int? remove = null;
        foreach (DevChangePacket packet in _devChanges.Packets)
        {
            if (ImGui.SmallButton($"x##dev-packet-{packet.Id}")) remove = packet.Id;
            ImGui.SameLine();
            ImGui.Text($"#{packet.Id}  {packet.Type}  {DescribeDevTarget(packet)}");
        }
        if (remove is { } id) RemoveDevChangePacket(id);
        if (ImGui.Button("Save now")) SaveDevChanges();
        ImGui.SameLine();
        ImGui.TextDisabled("upload this file in MangosSuperUI (NpcDev) to verify + apply");
    }

    private static string DescribeDevTarget(DevChangePacket packet)
    {
        Dictionary<string, object?> t = packet.Target;
        if (t.TryGetValue("guid", out object? guid)) return $"creature guid={guid}";
        if (t.TryGetValue("id", out object? pathGuid)) return $"movement id={pathGuid}";
        if (t.TryGetValue("entry", out object? entry)) return $"entry={entry}";
        return "";
    }
}

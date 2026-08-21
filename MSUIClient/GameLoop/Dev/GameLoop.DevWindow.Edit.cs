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
    /// <summary>Read-only DB route nodes for the currently inspected creature. Clicking
    /// one promotes that route into the real waypoint editor instead of falling through
    /// to ordinary ground targeting and clearing the creature selection.</summary>
    private readonly List<(ScreenRect Rect, uint SpawnGuid, uint Entry, int Index)>
        _devDbNodeHits = [];

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

    private void BeginDevWaypointEdit(uint spawnGuid, uint entry, int selectedNode = -1)
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
        _devEditSelectedNode = selectedNode >= 0 && selectedNode < _devEditPath.Count
            ? selectedNode
            : _devEditPath.Count - 1;   // button-started edits append after the tail
        _devEditMode = DevEditMode.WaypointEdit;
        _window.LeftButtonReservedForWorldClicks = true;
    }

    private void BeginDevSpawnMove(uint spawnGuid, uint entry)
    {
        CancelDevEdit();
        _devEditSpawnGuid = spawnGuid;
        _devEditEntry = entry;
        _devEditSpawnNewPos = null;
        _devEditMode = DevEditMode.SpawnMove;
        _window.LeftButtonReservedForWorldClicks = true;
    }

    private void CancelDevEdit()
    {
        _devEditMode = DevEditMode.None;
        _window.LeftButtonReservedForWorldClicks = false;
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
        if (!_devWindowOpen) return false;
        if (_devEditMode == DevEditMode.None)
        {
            // The numbered DB nodes are an affordance, not scenery. A left-click on
            // the inspected creature's route enters the genuine editor at that node.
            // Consume it here so ordinary targeting never sees "empty ground" and
            // clears the inspected creature/focus set.
            if (click.Button != MouseButton.Left) return false;
            int dbHit = HitTestDevDbNode(click.Position);
            if (dbHit < 0) return false;
            var node = _devDbNodeHits[dbHit];
            BeginDevWaypointEdit(node.SpawnGuid, node.Entry, node.Index);
            return true;
        }

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
                if (click.ShiftDown && _devEditSelectedNode >= 0 &&
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

    private int HitTestDevDbNode(Vector2 pixel)
    {
        for (int i = _devDbNodeHits.Count - 1; i >= 0; i--)
            if (_devDbNodeHits[i].Rect.Contains(pixel)) return i;
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

        // Staged per-spawn fields (re-seeded when the inspected spawn changes).
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

        // ── SPAWN ──
        ImGui.TextDisabled("SPAWN");
        if (ImGui.Button("Move spawn")) BeginDevSpawnMove(spawn.Guid, entry);
        ImGui.SetNextItemWidth(w);
        ImGui.InputInt("respawn min (s)", ref _devFieldSpawnMin);
        ImGui.SetNextItemWidth(w);
        ImGui.InputInt("respawn max (s)", ref _devFieldSpawnMax);
        ImGui.SetNextItemWidth(w);
        ImGui.Combo("movement_type", ref _devFieldMoveType, "0 idle\0" + "1 random (wander)\0" + "2 waypoint\0");
        ImGui.SetNextItemWidth(w);
        ImGui.InputFloat("wander_distance", ref _devFieldWander, 1f, 5f, "%.1f");
        if (ImGui.Button("Queue spawn changes")) QueueDevSpawnFieldPackets(spawn);

        // ── PATH ──
        ImGui.Spacing();
        ImGui.TextDisabled("PATH");
        if (ImGui.Button("Edit path")) BeginDevWaypointEdit(spawn.Guid, entry);

        // ── AGGRO ──
        ImGui.Spacing();
        ImGui.TextDisabled("AGGRO");
        ImGui.SetNextItemWidth(w);
        ImGui.InputFloat("detection_range", ref _devFieldDetection, 1f, 5f, "%.1f");
        int affected = DevSpawnCountForEntry(entry);
        ImGui.TextColored(new Vector4(1f, 0.65f, 0.3f, 1f),
            $"per-ENTRY: affects {affected} spawn(s) of entry {entry}");
        if (ImGui.Button("Queue detection_range change")) QueueDevDetectionPacket(spawn, entry, tpl);

        // ── GROUP ──
        DrawDevGroupControls(spawn);
    }

    // ── group (creature_groups formation) ────────────────────────────────────

    private uint _devGroupFlags = 0x3;   // default: formation move + aggro together
    private readonly Dictionary<uint, (float Dist, float Angle)> _devGroupEdits = [];
    private bool _devReloadGroups;

    /// <summary>The GROUP section: relocate (via the leader), reshape member offsets, split a
    /// member out, dissolve, or create a formation from the Ctrl+select set. All go through the
    /// change set as group-set / group-delete packets; reload is `.reload creature_groups`
    /// plus a respawn of the affected spawns.</summary>
    private void DrawDevGroupControls(DevSpawnRow spawn)
    {
        ImGui.Spacing();
        ImGui.TextDisabled("GROUP (creature_groups formation)");
        DevWorldData? world = DevData.World;
        if (world is null) { ImGui.TextDisabled("waiting for world data"); return; }
        uint guid = spawn.Guid;

        DevGroupMember? mine = world.GroupOf(guid);

        // FOLLOWER: act through its leader.
        if (mine is { IsLeaderRow: false })
        {
            ImGui.Text($"follows leader {mine.LeaderGuid}  (dist {mine.Dist:0.#}, angle {mine.Angle:0.##})");
            if (ImGui.Button("Split from group"))
            {
                var remaining = world.GroupRows(mine.LeaderGuid)
                    .Where(m => !m.IsLeaderRow && m.MemberGuid != guid)
                    .Select(m => (m.MemberGuid, m.Dist, m.Angle)).ToList();
                if (remaining.Count == 0) QueueDevGroupDelete(mine.LeaderGuid);
                else QueueDevGroupSet(mine.LeaderGuid, remaining, GroupFlagsOf(world, mine.LeaderGuid));
            }
            ImGui.SameLine();
            ImGui.TextDisabled("inspect the leader to reshape / dissolve");
            return;
        }

        var rows = world.GroupRows(guid);
        var followers = rows.Where(m => !m.IsLeaderRow).ToList();

        // LEADER: reshape / dissolve.
        if (followers.Count > 0)
        {
            ImGui.Text($"leader of {followers.Count} member(s)   flags {rows[0].Flags}");
            ImGui.TextDisabled("relocate: Move spawn (+ Edit path) on THIS leader — members follow.");
            foreach (DevGroupMember f in followers)
            {
                (float Dist, float Angle) e = _devGroupEdits.TryGetValue(f.MemberGuid, out var ex) ? ex : (f.Dist, f.Angle);
                ImGui.PushID((int)f.MemberGuid);
                float dist = e.Dist, angle = e.Angle;
                ImGui.SetNextItemWidth(90f);
                bool ch = ImGui.SliderFloat("dist", ref dist, 0.5f, 15f, "%.1f");
                ImGui.SameLine();
                ImGui.SetNextItemWidth(90f);
                ch |= ImGui.SliderFloat("angle", ref angle, 0f, 6.2832f, "%.2f");
                ImGui.SameLine();
                ImGui.TextDisabled($"m{f.MemberGuid}");
                if (ch) _devGroupEdits[f.MemberGuid] = (dist, angle);
                ImGui.PopID();
            }
            if (ImGui.Button("Queue reshape"))
            {
                var members = followers.Select(f =>
                    (f.MemberGuid,
                     _devGroupEdits.TryGetValue(f.MemberGuid, out var e2) ? e2.Dist : f.Dist,
                     _devGroupEdits.TryGetValue(f.MemberGuid, out var e3) ? e3.Angle : f.Angle)).ToList();
                QueueDevGroupSet(guid, members, rows[0].Flags);
            }
            ImGui.SameLine();
            if (ImGui.Button("Dissolve group")) QueueDevGroupDelete(guid);
            return;
        }

        // UNGROUPED: create from the Ctrl+select set (this spawn = leader).
        List<uint> sel = DevGroupSelectionGuids();
        bool formation = (_devGroupFlags & 0x1) != 0, aggro = (_devGroupFlags & 0x2) != 0;
        if (ImGui.Checkbox("formation move", ref formation))
            _devGroupFlags = formation ? _devGroupFlags | 0x1u : _devGroupFlags & ~0x1u;
        ImGui.SameLine();
        if (ImGui.Checkbox("aggro together", ref aggro))
            _devGroupFlags = aggro ? _devGroupFlags | 0x2u : _devGroupFlags & ~0x2u;

        int followerCount = sel.Count(g => g != guid);
        if (followerCount >= 1)
        {
            if (ImGui.Button($"Create formation — leader {guid}, {followerCount} follower(s)"))
                CreateDevGroupFromSelection(guid, sel, world);
            ImGui.TextDisabled("this inspected spawn = leader; other Ctrl-selected spawns = followers.");
        }
        else
            ImGui.TextDisabled("not in a formation. Ctrl+LeftClick 1+ more spawns, then create with this one as leader.");
    }

    /// <summary>Spawn guids (low-24) in the Ctrl+select focus set that have a fetched DB row.</summary>
    private List<uint> DevGroupSelectionGuids()
    {
        var result = new List<uint>();
        DevWorldData? world = DevData.World;
        if (world is null) return result;
        foreach (ulong g in _devFocusGuids)
            if (GuidInfo.High(g) == GuidInfo.HighUnit)
            {
                uint low = (uint)(g & 0xFFFFFF);
                if (world.SpawnsByGuid.ContainsKey(low) && !result.Contains(low)) result.Add(low);
            }
        return result;
    }

    private void CreateDevGroupFromSelection(uint leaderGuid, List<uint> sel, DevWorldData world)
    {
        if (!world.SpawnsByGuid.TryGetValue(leaderGuid, out DevSpawnRow? leader)) return;
        var members = new List<(uint Guid, float Dist, float Angle)>();
        foreach (uint g in sel)
        {
            if (g == leaderGuid) continue;
            if (!world.SpawnsByGuid.TryGetValue(g, out DevSpawnRow? f)) continue;
            float dx = f.Position.X - leader.Position.X;
            float dy = f.Position.Y - leader.Position.Y;
            float dist = MathF.Sqrt(dx * dx + dy * dy);
            float followAngle = DevNormAngle(MathF.Atan2(dy, dx) - leader.Orientation);
            members.Add((g, dist, followAngle));
        }
        if (members.Count == 0) return;
        QueueDevGroupSet(leaderGuid, members, _devGroupFlags == 0 ? 1u : _devGroupFlags);
    }

    private static uint GroupFlagsOf(DevWorldData world, uint leaderGuid)
    {
        var rows = world.GroupRows(leaderGuid);
        return rows.Count > 0 ? rows[0].Flags : 1u;
    }

    private static float DevNormAngle(float a)
    {
        const float TwoPi = MathF.PI * 2f;
        a %= TwoPi;
        return a < 0 ? a + TwoPi : a;
    }

    // ── group packet builders ─────────────────────────────────────────────────

    private void QueueDevGroupSet(uint leaderGuid, List<(uint Guid, float Dist, float Angle)> members, uint flags)
    {
        var afterMembers = new List<Dictionary<string, object?>>();
        // Leader self-row (dist 0) must be present.
        if (members.All(m => m.Guid != leaderGuid))
            afterMembers.Add(new Dictionary<string, object?> { ["guid"] = leaderGuid, ["dist"] = 0f, ["angle"] = 0f });
        foreach (var m in members)
            afterMembers.Add(new Dictionary<string, object?> { ["guid"] = m.Guid, ["dist"] = m.Dist, ["angle"] = m.Angle });

        AddDevChangePacket("group-set",
            new Dictionary<string, object?> { ["leaderGuid"] = leaderGuid },
            DevGroupPayload(DevData.World?.GroupRows(leaderGuid)),
            new Dictionary<string, object?> { ["flags"] = flags, ["members"] = afterMembers });
    }

    private void QueueDevGroupDelete(uint leaderGuid) =>
        AddDevChangePacket("group-delete",
            new Dictionary<string, object?> { ["leaderGuid"] = leaderGuid },
            DevGroupPayload(DevData.World?.GroupRows(leaderGuid)),
            new Dictionary<string, object?>());

    private static Dictionary<string, object?> DevGroupPayload(IReadOnlyList<DevGroupMember>? rows)
    {
        var members = new List<Dictionary<string, object?>>();
        uint flags = 0;
        if (rows is not null)
            foreach (DevGroupMember r in rows)
            {
                members.Add(new Dictionary<string, object?> { ["guid"] = r.MemberGuid, ["dist"] = r.Dist, ["angle"] = r.Angle });
                flags = r.Flags;
            }
        return new Dictionary<string, object?> { ["flags"] = flags, ["members"] = members };
    }

    /// <summary>Leader + member guids a group packet touches, for the live reload.</summary>
    private static IEnumerable<ulong> DevGroupPacketGuids(DevChangePacket packet)
    {
        if (packet.Target.TryGetValue("leaderGuid", out object? l) && l is not null)
            yield return Convert.ToUInt64(l);
        object? membersObj = packet.After.TryGetValue("members", out object? am) && am is not null
            ? am : packet.Before.GetValueOrDefault("members");
        if (membersObj is System.Collections.IEnumerable seq)
            foreach (object? item in seq)
                if (item is IDictionary<string, object?> md && md.TryGetValue("guid", out object? g) && g is not null)
                    yield return Convert.ToUInt64(g);
    }

    private void DrawDevActiveEditControls()
    {
        if (_devEditMode == DevEditMode.WaypointEdit)
        {
            ImGui.TextColored(new Vector4(0.4f, 1f, 0.55f, 1f),
                $"PATH EDIT - {_devEditPath.Count} nodes " +
                $"({(_devEditPathOrigin == DevPathOrigin.Template ? "from template - saves as per-guid" : "per-guid")})");
            ImGui.TextDisabled("left: select node / add after selected  -  Shift+left: move selected (camera locked)");
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

    private string _devReloadStatus = "";
    // Reload targets captured at commit time, so Reload still works after the set "washes".
    private readonly List<(ulong Guid, bool Template)> _devReloadTargets = [];
    private DateTime _devLastCommitWashUtc;

    /// <summary>The Change set section: queued packets (+ a verdict badge as they commit), then the
    /// owner flow — Commit (audited, through SuperUI) → the applied packets WASH back to the DB
    /// baseline and the world resnapshots → Reload (live, your click) → test. Stale/failed packets
    /// stay for a retry; Save now keeps the local record.</summary>
    private void DrawDevChangeSetSection()
    {
        if (!ImGui.CollapsingHeader("Change set", ImGuiTreeNodeFlags.DefaultOpen)) return;

        NpcApplyResult? applied = DevData.ApplyResult;
        // Once a commit lands OK, wash: drop the applied packets (keeping their reload targets)
        // and force a world resnapshot to the new DB baseline. Runs once per commit result.
        if (applied is { Ok: true } && applied.CompletedUtc > _devLastCommitWashUtc)
        {
            _devLastCommitWashUtc = applied.CompletedUtc;
            WashCommittedPackets(applied);
        }

        bool hasPackets = _devChanges is { Packets.Count: > 0 };
        if (!hasPackets && applied is null)
        {
            ImGui.TextDisabled("no queued changes this session");
            return;
        }

        Dictionary<int, NpcApplyPacketVerdict> verdicts = applied is { Ok: true }
            ? applied.Results.ToDictionary(r => r.Id) : new();

        if (hasPackets)
        {
            ImGui.TextDisabled(_devChangeFilePath ?? "(unsaved)");
            int? remove = null;
            foreach (DevChangePacket packet in _devChanges!.Packets)
            {
                if (ImGui.SmallButton($"x##dev-packet-{packet.Id}")) remove = packet.Id;
                ImGui.SameLine();
                ImGui.Text($"#{packet.Id}  {packet.Type}  {DescribeDevTarget(packet)}");
                if (verdicts.TryGetValue(packet.Id, out NpcApplyPacketVerdict? vd))
                {
                    ImGui.SameLine();
                    ImGui.TextColored(DevVerdictColor(vd.Verdict), $"[{vd.Verdict}]");
                    if (vd.Message is { Length: > 0 } msg && ImGui.IsItemHovered()) ImGui.SetTooltip(msg);
                }
            }
            if (remove is { } id) RemoveDevChangePacket(id);
        }

        // Owner flow: Commit → (wash) → Reload → test.
        bool busy = DevData.Applying;
        if (hasPackets)
        {
            if (busy) ImGui.BeginDisabled();
            if (ImGui.Button("Commit (SuperUI)"))
            {
                _devReloadStatus = "";
                DevData.BeginApply(Settings.DevWindow.SuiBaseUrl, _devChanges);
            }
            if (busy) ImGui.EndDisabled();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Verify + apply + audit this change-set through MangosSuperUI\n" +
                    $"({Settings.DevWindow.SuiBaseUrl}/NpcDev/Apply). Applied packets then wash to the DB baseline.");
            ImGui.SameLine();
        }
        if (_devReloadTargets.Count > 0)
        {
            if (ImGui.Button("Reload selected")) ReloadAppliedNpcChanges();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Make the just-committed changes LIVE on this client (a GM command YOU send):\n" +
                    "  aggro       ->  .reload creature_template\n" +
                    "  spawn/path  ->  .npc reloadspawn <guid>");
            ImGui.SameLine();
        }
        if (hasPackets && ImGui.Button("Save now")) SaveDevChanges();

        if (busy)
            ImGui.TextDisabled($"committing to {Settings.DevWindow.SuiBaseUrl}…");
        else if (applied is not null)
        {
            if (!applied.Ok)
                ImGui.TextColored(new Vector4(1f, 0.45f, 0.4f, 1f), $"commit failed: {applied.Error}");
            else
                ImGui.TextColored(new Vector4(0.5f, 1f, 0.6f, 1f),
                    $"committed OK  {applied.Applied} applied, {applied.Stale} stale, {applied.Failed} failed" +
                    (applied.BatchId is { Length: > 0 } b ? $"  (batch {b})" : ""));
        }
        if (_devReloadStatus.Length > 0) ImGui.TextDisabled(_devReloadStatus);
    }

    private static Vector4 DevVerdictColor(string verdict) => verdict switch
    {
        "applied" => new Vector4(0.5f, 1f, 0.6f, 1f),
        "stale" => new Vector4(1f, 0.82f, 0.3f, 1f),
        _ => new Vector4(1f, 0.5f, 0.45f, 1f),
    };

    /// <summary>Commit landed: capture the applied packets' reload targets, drop those packets from
    /// the set (leaving stale/failed for retry), and force a world resnapshot so the spawn rows show
    /// the committed DB baseline.</summary>
    private void WashCommittedPackets(NpcApplyResult applied)
    {
        _devReloadTargets.Clear();
        _devReloadGroups = false;
        _devGroupEdits.Clear();
        var appliedIds = applied.Results.Where(r => r.Verdict == "applied").Select(r => r.Id).ToHashSet();
        if (_devChanges is not null)
        {
            foreach (DevChangePacket packet in _devChanges.Packets.Where(p => appliedIds.Contains(p.Id)))
            {
                if (packet.Type is "group-set" or "group-delete")
                {
                    _devReloadGroups = true;
                    foreach (ulong g in DevGroupPacketGuids(packet))
                    {
                        (ulong Guid, bool Template) gt = (g, false);
                        if (g != 0 && !_devReloadTargets.Contains(gt)) _devReloadTargets.Add(gt);
                    }
                    continue;
                }
                (ulong Guid, bool Template) target = packet.Type == "template-field"
                    ? (0UL, true)
                    : (DevPacketGuid(packet), false);
                if ((target.Template || target.Guid != 0) && !_devReloadTargets.Contains(target))
                    _devReloadTargets.Add(target);
            }
            _devChanges.Packets.RemoveAll(p => appliedIds.Contains(p.Id));
            foreach (int id in appliedIds) _devPacketPreviews.Remove(id);
            SaveDevChanges();
        }
        int map = _config.Start.Map;
        Vector3 c = _controller?.Position ?? Vector3.Zero;
        DevData.BeginFetchWorld(Settings.DevWindow.SuiBaseUrl, map, c.X, c.Y, forceRefresh: true);
    }

    /// <summary>Owner-clicked live reload of the just-committed targets: .reload creature_template
    /// for aggro (once), .npc reloadspawn &lt;guid&gt; for spawn/path. No DB writes.</summary>
    private void ReloadAppliedNpcChanges()
    {
        if (_devReloadTargets.Count == 0 && !_devReloadGroups) { _devReloadStatus = "nothing applied to reload"; return; }
        bool reloadedTemplates = false;
        int sent = 0;
        if (_devReloadGroups && SendGmCommand(".reload creature_groups", "npc-reload")) sent++;
        foreach ((ulong guid, bool template) in _devReloadTargets)
        {
            if (template)
            {
                if (!reloadedTemplates && SendGmCommand(".reload creature_template", "npc-reload"))
                {
                    reloadedTemplates = true;
                    sent++;
                }
            }
            else if (SendGmCommand($".npc reloadspawn {guid}", "npc-reload")) sent++;
        }
        _devReloadStatus = sent == 0 ? "nothing applied to reload" : $"sent {sent} reload command(s)";
    }

    /// <summary>The spawn guid a packet targets (creature.guid or creature_movement.id); 0 for
    /// entry-only targets (shared template path / template-field) that no single guid reloads.</summary>
    private static ulong DevPacketGuid(DevChangePacket packet)
    {
        if (packet.Target.TryGetValue("guid", out object? g) && g is not null) return Convert.ToUInt64(g);
        if (packet.Target.TryGetValue("id", out object? i) && i is not null) return Convert.ToUInt64(i);
        return 0;
    }

    // ── reset to original (OG baseline) ──────────────────────────────────────

    private DateTime _devLastResetUtc;
    private ulong _devResetReloadGuid;
    private bool _devResetReloadTemplate;

    private string DevCharacter() => _net?.PlayerName is { Length: > 0 } name ? name : "offline";

    /// <summary>Selected-NPC baseline row: shows whether this spawn / its path / its entry's aggro
    /// differ from the captured og_creature* baseline, and offers a per-spawn "reset to original"
    /// (plus a per-entry aggro reset). Reset restores through SuperUI (audited) then reloads live.</summary>
    private void DrawDevBaselineControls(uint spawnGuid, uint entry)
    {
        // Auto-fetch the diff when the inspected spawn changes.
        NpcDiff? diff = DevData.Diff;
        if (!DevData.Diffing && (diff is null || diff.Guid != spawnGuid))
            DevData.BeginDiff(Settings.DevWindow.SuiBaseUrl, spawnGuid, entry);

        // A completed reset: make it live (your click authorized it), resnapshot, re-diff.
        if (DevData.ResetResult is { Ok: true } reset && reset.CompletedUtc > _devLastResetUtc)
        {
            _devLastResetUtc = reset.CompletedUtc;
            if (_devResetReloadTemplate) SendGmCommand(".reload creature_template", "npc-reset");
            if (_devResetReloadGuid != 0) SendGmCommand($".npc reloadspawn {_devResetReloadGuid}", "npc-reset");
            Vector3 c = _controller?.Position ?? Vector3.Zero;
            DevData.BeginFetchWorld(Settings.DevWindow.SuiBaseUrl, _config.Start.Map, c.X, c.Y, forceRefresh: true);
            DevData.BeginDiff(Settings.DevWindow.SuiBaseUrl, spawnGuid, entry);
        }

        ImGui.Spacing();
        ImGui.TextDisabled("BASELINE (original)");

        if (diff is null || diff.Guid != spawnGuid) { ImGui.TextDisabled("checking…"); return; }
        if (!diff.HasBaseline)
        {
            ImGui.TextDisabled("no og_creature baseline captured yet");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Run Baseline/Initialize in MangosSuperUI (creature tables) to capture the original.");
            return;
        }

        bool busy = DevData.Resetting;
        var warn = new Vector4(1f, 0.7f, 0.3f, 1f);
        var okCol = new Vector4(0.5f, 1f, 0.6f, 1f);

        if (!diff.BaselineHasSpawn)
            ImGui.TextDisabled("this spawn isn't in the baseline (added after capture)");
        else if (diff.SpawnModified || diff.PathModified)
        {
            ImGui.TextColored(warn, "• spawn changed from original" + (diff.PathModified ? " (incl. path)" : ""));
            if (busy) ImGui.BeginDisabled();
            if (ImGui.Button($"Reset spawn to original##ogspawn{spawnGuid}"))
            {
                _devResetReloadGuid = spawnGuid;
                _devResetReloadTemplate = false;
                DevData.BeginReset(Settings.DevWindow.SuiBaseUrl, DevCharacter(), new[] { spawnGuid }, Array.Empty<uint>());
            }
            if (busy) ImGui.EndDisabled();
        }
        else
            ImGui.TextColored(okCol, "spawn matches original");

        if (diff.TemplateModified)
        {
            ImGui.TextColored(warn, $"• aggro (entry {entry}) changed from original");
            if (busy) ImGui.BeginDisabled();
            if (ImGui.Button($"Reset aggro to original — all spawns##ogtmpl{entry}"))
            {
                _devResetReloadGuid = 0;
                _devResetReloadTemplate = true;
                DevData.BeginReset(Settings.DevWindow.SuiBaseUrl, DevCharacter(), Array.Empty<uint>(), new[] { entry });
            }
            if (busy) ImGui.EndDisabled();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("detection_range is per-ENTRY — this resets aggro for every spawn of the entry.");
        }

        if (DevData.ResetResult is { Ok: false } rerr)
            ImGui.TextColored(new Vector4(1f, 0.45f, 0.4f, 1f), $"reset failed: {rerr.Error}");
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

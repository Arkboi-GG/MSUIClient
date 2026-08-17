using System.Numerics;
using ImGuiNET;
using MSUIClient.Formats;
using MSUIClient.Net;
using MSUIClient.World.Units;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// NPC dev window overlays: aggro discs (terrain-conforming annuli), through-wall
// "who would aggro me" beams, observed-path polylines and spawn labels. The 3-D
// pieces run in the render pass beside RenderRtsGroundFx; the screen-space
// pieces draw into the background draw list like nameplates. All recording is
// gated on the window being open — nothing accumulates in normal play.
// ─────────────────────────────────────────────────────────────────────────────

public sealed partial class GameLoop
{
    private const uint DevFlagNoAggro = 0x2;               // creature_template.flags_extra
    private const uint DevFlagSessile = 0x100;             // creature_template.static_flags
    private const uint DevFlagIgnoreCombat = 0x02000000;   // creature_template.static_flags

    /// <summary>Per-guid history of SMSG_MONSTER_MOVE spline points, recorded while the
    /// window is open. This is OBSERVED pathing (ground truth of what the server sent),
    /// distinct from the DB waypoint tables that arrive in phase 2.</summary>
    private readonly Dictionary<ulong, List<Vector3>> _devObservedPaths = [];
    private double _devPathPruneAt;

    /// <summary>SMSG_AI_REACTION hostile flashes: guid → NowSeconds of the reaction.</summary>
    private readonly Dictionary<ulong, double> _devAggroFlash = [];

    /// <summary>Rebuilt every frame by the 3-D pass: creatures whose computed aggro radius
    /// currently contains my toon. Read by the label pass for the red badge.</summary>
    private readonly HashSet<ulong> _devAggroingMe = [];

    private readonly List<Vector3> _devBeamScratch = new(64);

    // ── shared lookups ───────────────────────────────────────────────────────

    private NpcTemplateInfo? DevTemplateFor(uint entry) =>
        _devData?.Templates?.ByEntry.GetValueOrDefault(entry);

    private uint? DevMyLevel() =>
        _entities.TryGet(ControlledGuid, out WorldEntity me) && me.Level > 0 ? me.Level : null;

    /// <summary>Where my toon actually stands: the controller when it drives the body,
    /// the streamed entity in the free view (the rig is the camera there).</summary>
    private Vector3? DevPlayerPosition()
    {
        // Offline creator sandbox: there is no _net and no entity for the own
        // toon — the controller IS the toon. In the free view the controller is
        // the SKY RIG, not the toon; the toon stands where Ctrl+F left it.
        // (Returning null here made every "at the player" affordance — raid
        // placement, Snap to me, probe At me — silently target the boss-side
        // fallback instead. The owner caught it: the raid formed 40 yd from
        // Onyxia, nowhere near them.)
        if (_net is null)
        {
            if (!CreatorInWorld || _controller is null) return null;
            return _freeView ? _creatorFreeViewReturn : _controller.Position;
        }
        if (!_freeView && _controller is not null) return _controller.Position;
        return _entities.TryGet(ControlledGuid, out WorldEntity me) ? me.Position
            : _controller?.Position;
    }

    /// <summary>
    /// vmangos Creature::GetAttackDistance (Creature.cpp:2122), aggroRate 1, no aura mods:
    /// radius = detection_range - clamp(targetLevel - npcLevel, -25, ∞), floored at
    /// min(detection_range, 5). Zero when the template can never proximity-aggro.
    /// </summary>
    private static float DevAggroRadius(uint npcLevel, uint targetLevel, NpcTemplateInfo? tpl)
    {
        if (tpl is not null &&
            ((tpl.FlagsExtra & DevFlagNoAggro) != 0 ||
             (tpl.StaticFlags & DevFlagIgnoreCombat) != 0)) return 0f;
        float detection = tpl?.DetectionRange ?? 18f;
        if (detection < 1f) return 0f;
        int levelDif = (int)targetLevel - (int)npcLevel;
        if (levelDif < -25) levelDif = -25;
        return MathF.Max(detection - levelDif, MathF.Min(detection, 5f));
    }

    /// <summary>Band k = k levels below the reference: red → orange → yellow → green → cyan → blue.</summary>
    private static Vector3 DevBandTint(int band) => band switch
    {
        0 => new Vector3(0.95f, 0.20f, 0.15f),
        1 => new Vector3(1.00f, 0.55f, 0.12f),
        2 => new Vector3(1.00f, 0.90f, 0.20f),
        3 => new Vector3(0.35f, 0.90f, 0.35f),
        4 => new Vector3(0.25f, 0.80f, 1.00f),
        _ => new Vector3(0.45f, 0.45f, 1.00f),
    };

    // ── wire taps (called from the Program.Net dispatch) ─────────────────────

    /// <summary>Accumulate a creature's spline points for the observed-path polyline.
    /// Gated on the open window so nothing is recorded during normal play.</summary>
    private void RecordDevObservedPath(MonsterMove mm)
    {
        if (!_devWindowOpen || !Settings.DevWindow.ShowObservedPaths) return;
        if (mm.Stop || mm.Points.Length < 2 || !GuidInfo.IsCreatureOrPet(mm.Guid)) return;

        if (!_devObservedPaths.TryGetValue(mm.Guid, out List<Vector3>? path))
            _devObservedPaths[mm.Guid] = path = new List<Vector3>(32);
        foreach (Vector3 point in mm.Points)
        {
            if (path.Count > 0 && Vector3.DistanceSquared(path[^1], point) < 0.04f) continue;
            path.Add(point);
        }
        if (path.Count > 240) path.RemoveRange(0, path.Count - 240);
    }

    /// <summary>SMSG_AI_REACTION: the server's own "this creature just turned hostile at
    /// you" - free empirical validation of the computed radii. Flash the disc rim and log
    /// predicted vs actual distance.</summary>
    private void ApplyAiReaction(byte[] body)
    {
        if (body.Length < 12) return;
        var r = new PacketReader(body);
        ulong guid = r.ReadU64();
        uint reaction = r.ReadU32();
        if (!_devWindowOpen || reaction != 2) return;   // 2 = AI_REACTION_HOSTILE

        _devAggroFlash[guid] = NowSeconds();
        if (_entities.TryGet(guid, out WorldEntity mob) && DevMyLevel() is { } level &&
            DevPlayerPosition() is { } me)
        {
            float predicted = DevAggroRadius(mob.Level, level, DevTemplateFor(mob.Entry));
            Console.WriteLine(
                $"[dev-aggro] entry {mob.Entry} guid {mob.Guid & 0xFFFFFF} went hostile at " +
                $"{Vector3.Distance(me, mob.Position):0.0} yd (predicted {predicted:0.0} yd)");
        }
    }

    // ── 3-D pass (beside RenderRtsGroundFx, after units populate depth) ──────

    private void RenderDevOverlays3D()
    {
        _devAggroingMe.Clear();
        if (!_devWindowOpen || _spellEffectMeshes is null) return;
        var dev = Settings.DevWindow;
        bool live = _net is { IsInWorld: true };
        if (!live && !_creatorWorldRequested) return;
        if (!dev.ShowAggroDiscs && !(dev.ShowWhoAggros && live)) return;

        _spellEffectMeshes.GatherGround ??= GatherGroundEffectTriangles;
        Vector3 eye = _window.Camera.Position;
        float rangeSq = dev.OverlayRange * dev.OverlayRange;
        uint? myLevel = DevMyLevel();
        Vector3? myPosition = DevPlayerPosition();
        double now = NowSeconds();
        int bandCount = Math.Clamp(dev.AggroBandCount, 1, 6);

        List<SpellEffectMeshRenderer.GroundDisc> discs = [];
        foreach (WorldEntity unit in _entities.Units)
        {
            if (!unit.IsCreature || unit.IsDead) continue;
            if (dev.FocusSelectedOnly && !_devFocusGuids.Contains(unit.Guid)) continue;
            if (Vector3.DistanceSquared(unit.Position, eye) > rangeSq) continue;

            NpcTemplateInfo? tpl = ApplyDevPendingTemplate(DevTemplateFor(unit.Entry), unit.Entry);
            bool hostile = live && ReactionTargetTowardPlayer(unit) == FactionReaction.Hostile;
            // Creator mode has no reaction data - the synthetic spawns are the point there.
            if (dev.HostilesOnly && live && !hostile) continue;

            // Who-aggros-me: ALWAYS vs my toon's level and real position, hostiles only -
            // this is the "stand in the blob, see every mob that would come" highlight.
            if (dev.ShowWhoAggros && hostile && !unit.InCombat &&
                myLevel is { } lvl && myPosition is { } me)
            {
                float radius = DevAggroRadius(unit.Level, lvl, tpl);
                if (radius > 0f && Vector3.DistanceSquared(me, unit.Position) <= radius * radius)
                    _devAggroingMe.Add(unit.Guid);
            }

            if (dev.ShowAggroDiscs)
            {
                uint reference = dev.AggroReference switch
                {
                    "Level60" => 60u,
                    "NpcLevel" => Math.Max(unit.Level, 1u),
                    _ => myLevel ?? 60u,
                };
                float previous = 0f;
                for (int band = 0; band < bandCount; band++)
                {
                    uint target = (uint)Math.Max(1, (int)reference - band);
                    float radius = DevAggroRadius(unit.Level, target, tpl);
                    if (radius <= previous + 0.05f)
                    {
                        previous = MathF.Max(previous, radius);
                        continue;   // clamped at the floor - nothing new to draw
                    }
                    discs.Add(new(unit.Position, previous, radius,
                        DevBandTint(band), dev.DiscOpacity));
                    previous = radius;
                }
            }

            // AI_REACTION flash: a bright rim at the vs-me radius for 1.2 s.
            if (_devAggroFlash.TryGetValue(unit.Guid, out double flashedAt))
            {
                if (now - flashedAt > 1.2) _devAggroFlash.Remove(unit.Guid);
                else if (myLevel is { } flashLevel)
                {
                    float radius = MathF.Max(2f, DevAggroRadius(unit.Level, flashLevel, tpl));
                    float pulse = (float)(1.0 - (now - flashedAt) / 1.2);
                    discs.Add(new(unit.Position, radius * 0.9f, radius, Vector3.One, 0.85f * pulse));
                }
            }
        }

        // DB-truth discs: authored spawn points + wander circles (whether streamed or not).
        if (dev.ShowDbSpawns && DevData.World is { } world && world.Map == _config.Start.Map)
            AddDevDbDiscs(discs, world, eye, rangeSq);

        if (discs.Count > 0)
            _spellEffectMeshes.RenderGroundDiscs(_window.Camera, discs);

        // Through-wall beams on everything that would aggro my toon from here.
        if (_devAggroingMe.Count > 0 && _collisionDebug is not null)
            foreach (ulong guid in _devAggroingMe)
                if (_entities.TryGet(guid, out WorldEntity mob))
                    RenderDevAggroBeam(mob);
    }

    private static readonly Vector3 DevSpawnMarkerTint = new(0.20f, 0.90f, 0.80f);
    private static readonly Vector3 DevSpawnMarkerDimTint = new(0.45f, 0.50f, 0.55f);
    private static readonly Vector3 DevWanderRingTint = new(0.20f, 0.70f, 0.90f);

    /// <summary>Spawn-point markers (small filled discs; dimmed when the server is not
    /// currently streaming that guid) and wander-radius rings for movement_type 1.</summary>
    private void AddDevDbDiscs(List<SpellEffectMeshRenderer.GroundDisc> discs,
        DevWorldData world, Vector3 eye, float rangeSq)
    {
        HashSet<uint> streamed = DevStreamedSpawnLows();
        HashSet<uint>? focusLows = DevFocusSpawnLows();
        foreach (DevSpawnRow spawn in world.SpawnsByGuid.Values)
        {
            if (focusLows is not null && !focusLows.Contains(spawn.Guid)) continue;
            if (Vector3.DistanceSquared(spawn.Position, eye) > rangeSq) continue;
            bool isStreamed = streamed.Contains(spawn.Guid);
            discs.Add(new(spawn.Position, 0f, 0.7f,
                isStreamed ? DevSpawnMarkerTint : DevSpawnMarkerDimTint,
                isStreamed ? 0.55f : 0.30f));
            if (spawn.MovementType == 1 && spawn.WanderDistance > 0.5f)
                discs.Add(new(spawn.Position,
                    MathF.Max(0f, spawn.WanderDistance - 0.35f), spawn.WanderDistance,
                    DevWanderRingTint, 0.35f));
        }
    }

    /// <summary>Low-24-bit spawn guids of every creature the server currently streams —
    /// the join key between live entities and mangos.creature rows. Excludes synthetic
    /// (creator) entities via the high-part check.</summary>
    private HashSet<uint> DevStreamedSpawnLows()
    {
        var lows = new HashSet<uint>();
        foreach (WorldEntity unit in _entities.Units)
            if (unit.IsCreature && GuidInfo.High(unit.Guid) == GuidInfo.HighUnit)
                lows.Add((uint)(unit.Guid & 0xFFFFFF));
        return lows;
    }

    /// <summary>A red hex prism around the mob, drawn depth-test-OFF (the collision
    /// highlight pass) so it reads through terrain and walls.</summary>
    private void RenderDevAggroBeam(WorldEntity mob)
    {
        const int sides = 6;
        _devBeamScratch.Clear();
        float scale = MathF.Max(0.5f, mob.Scale <= 0f ? 1f : mob.Scale);
        float radius = MathF.Max(0.35f, 0.6f * scale);
        float height = UnitOverheadHeight(mob) + 1.5f;
        Vector3 feet = mob.Position;
        Vector3 top = feet + new Vector3(0f, 0f, height);
        for (int i = 0; i < sides; i++)
        {
            float a0 = i * MathF.PI * 2f / sides;
            float a1 = (i + 1) * MathF.PI * 2f / sides;
            var p0 = new Vector3(MathF.Cos(a0), MathF.Sin(a0), 0f) * radius;
            var p1 = new Vector3(MathF.Cos(a1), MathF.Sin(a1), 0f) * radius;
            _devBeamScratch.Add(feet + p0); _devBeamScratch.Add(feet + p1); _devBeamScratch.Add(top + p1);
            _devBeamScratch.Add(feet + p0); _devBeamScratch.Add(top + p1); _devBeamScratch.Add(top + p0);
        }
        _collisionDebug!.RenderHighlight(_window.Camera, _devBeamScratch, mode: 3);
    }

    // ── screen-space pass (labels + path polylines, background draw list) ────

    /// <summary>"N streamed / M DB-only" counters for the window toolbar, rebuilt by the
    /// label pass each frame (read next frame — the window draws before the labels).</summary>
    private int _devStreamedInRange;
    private int _devDbOnlyInRange;

    private void DrawDevOverlayLabels()
    {
        // An active editor always owns left gestures. Outside edit mode, only a
        // currently hovered numbered DB node owns left; ordinary camera look remains
        // available everywhere else in the world.
        _window.LeftButtonReservedForWorldClicks = _devEditMode != DevEditMode.None;
        _devDbNodeHits.Clear();
        if (!_devWindowOpen) return;
        var dev = Settings.DevWindow;
        bool live = _net is { IsInWorld: true };
        if (!live && !_creatorWorldRequested) return;

        PruneDevObservedPaths();
        ImDrawListPtr draw = ImGui.GetBackgroundDrawList();
        Vector2 display = ImGui.GetIO().DisplaySize;
        Vector3 eye = _window.Camera.Position;
        float rangeSq = dev.OverlayRange * dev.OverlayRange;
        DevWorldData? world =
            DevData.World is { } w && w.Map == _config.Start.Map ? w : null;
        HashSet<uint> streamedLows = DevStreamedSpawnLows();
        HashSet<uint>? focusLows = DevFocusSpawnLows();
        _devStreamedInRange = 0;
        _devDbOnlyInRange = 0;

        // Paths already drawn this frame — template routes are shared by every spawn of
        // an entry (identical absolute coordinates), so draw each resolved path once.
        HashSet<(DevPathOrigin, uint, uint)> drawnPaths = [];

        // ── streamed creatures: labels, observed paths, DB route + spawn connector ──
        foreach (WorldEntity unit in _entities.Units)
        {
            if (!unit.IsCreature) continue;
            if (dev.FocusSelectedOnly && !_devFocusGuids.Contains(unit.Guid)) continue;
            if (Vector3.DistanceSquared(unit.Position, eye) > rangeSq) continue;
            _devStreamedInRange++;

            if (dev.ShowObservedPaths &&
                _devObservedPaths.TryGetValue(unit.Guid, out List<Vector3>? observed) &&
                observed.Count > 1)
                DrawDevPath(draw, display, observed, DevPathColor(unit.Guid));

            uint spawnGuid = (uint)(unit.Guid & 0xFFFFFF);
            DevSpawnRow? spawn = GuidInfo.High(unit.Guid) == GuidInfo.HighUnit
                ? world?.SpawnsByGuid.GetValueOrDefault(spawnGuid) : null;

            if (spawn is not null && dev.ShowDbPaths)
                DrawDevDbRoute(draw, display, world!, spawnGuid, spawn.Entry, drawnPaths,
                    interactive: unit.Guid == _selectionGuid);

            // Thin dashed connector current position → authored spawn point.
            if (spawn is not null && dev.ShowDbSpawns &&
                Vector3.DistanceSquared(unit.Position, spawn.Position) > 9f &&
                _window.Camera.TryWorldToScreen(unit.Position + new Vector3(0f, 0f, 0.2f),
                    display, out Vector2 fromPx) &&
                _window.Camera.TryWorldToScreen(spawn.Position + new Vector3(0f, 0f, 0.2f),
                    display, out Vector2 toPx))
                DrawDashedLine(draw, fromPx, toPx, 0x66ffffff, 4f, 6f);

            if (dev.ShowSpawnLabels && !unit.IsDead)
                DrawDevSpawnLabel(draw, display, unit, live);
        }

        // ── DB-only spawns: rows the server is not currently streaming ──────────
        if (world is not null)
            foreach (DevSpawnRow spawn in world.SpawnsByGuid.Values)
            {
                if (streamedLows.Contains(spawn.Guid)) continue;
                if (focusLows is not null && !focusLows.Contains(spawn.Guid)) continue;
                if (Vector3.DistanceSquared(spawn.Position, eye) > rangeSq) continue;
                _devDbOnlyInRange++;
                if (dev.ShowDbPaths)
                    DrawDevDbRoute(draw, display, world, spawn.Guid, spawn.Entry, drawnPaths,
                        interactive: false);
                if (dev.ShowSpawnLabels)
                    DrawDevDbOnlyLabel(draw, display, spawn);
            }

        // ── editing: the working path / proposed spawn move + queued-edit previews ──
        DrawDevEditOverlay(draw, display);
        DrawDevPendingPreviews(draw, display, eye, rangeSq);
        if (_devEditMode == DevEditMode.None && HitTestDevDbNode(_window.MousePosition) >= 0)
            _window.LeftButtonReservedForWorldClicks = true;
    }

    // ── edit-mode overlay (state lives in Program.DevWindow.Edit.cs) ─────────

    private const uint DevEditColor = 0xff50ff80;        // bright green: the live edit
    private const uint DevPendingColor = 0xb050e080;     // translucent green: queued packets

    /// <summary>The armed edit mode's visuals: working waypoint path with numbered,
    /// clickable nodes (hit rects filled here, consumed by HandleDevEditClick) or the
    /// proposed spawn position. Draws regardless of overlay toggles — an armed mode
    /// must always be visible.</summary>
    private void DrawDevEditOverlay(ImDrawListPtr draw, Vector2 display)
    {
        _devEditNodeHits.Clear();
        if (_devEditMode == DevEditMode.None) return;
        var lift = new Vector3(0f, 0f, 0.25f);

        if (_devEditMode == DevEditMode.WaypointEdit)
        {
            Vector2? previous = null;
            for (int i = 0; i < _devEditPath.Count; i++)
            {
                DevWaypointRow node = _devEditPath[i];
                if (!_window.Camera.TryWorldToScreen(node.Position + lift, display, out Vector2 px))
                { previous = null; continue; }
                if (previous is Vector2 from) draw.AddLine(from, px, DevEditColor, 2.5f);
                draw.AddCircleFilled(px, 5f, DevEditColor);
                if (i == _devEditSelectedNode) draw.AddCircle(px, 9f, 0xffffffff, 16, 2f);
                string label = (i + 1) +
                    (node.WaitMs > 0 ? $" ({node.WaitMs / 1000f:0.#}s)" : "");
                draw.AddText(px + new Vector2(6f, -16f), 0xc0000000u, label);
                draw.AddText(px + new Vector2(5f, -17f), DevEditColor, label);
                _devEditNodeHits.Add(
                    (new ScreenRect(px.X - 10f, px.Y - 10f, px.X + 10f, px.Y + 10f), i));
                previous = px;
            }
            return;
        }

        // SpawnMove: ring at the proposal + dashed line from the authored spawn point.
        if (_devEditSpawnNewPos is not { } to) return;
        if (!_window.Camera.TryWorldToScreen(to + lift, display, out Vector2 toPx)) return;
        draw.AddCircle(toPx, 10f, DevEditColor, 20, 3f);
        draw.AddText(toPx + new Vector2(12f, -8f), DevEditColor, "new spawn");
        DevSpawnRow? spawn = DevData.World?.SpawnsByGuid.GetValueOrDefault(_devEditSpawnGuid);
        if (spawn is not null &&
            _window.Camera.TryWorldToScreen(spawn.Position + lift, display, out Vector2 fromPx))
            DrawDashedLine(draw, fromPx, toPx, DevEditColor, 8f, 6f);
    }

    /// <summary>Queued-but-unapplied edits stay visible: each packet's preview (the
    /// replacement path, or the old→new spawn move) draws dim green until the change
    /// set is applied server-side (or the packet is reverted).</summary>
    private void DrawDevPendingPreviews(ImDrawListPtr draw, Vector2 display,
        Vector3 eye, float rangeSq)
    {
        if (_devPacketPreviews.Count == 0) return;
        var lift = new Vector3(0f, 0f, 0.25f);
        foreach (DevEditPreview preview in _devPacketPreviews.Values)
        {
            if (preview.Points.Length == 0) continue;
            if (Vector3.DistanceSquared(preview.Points[0], eye) > rangeSq * 4f) continue;
            Vector2? previous = null;
            if (preview.From is { } from &&
                _window.Camera.TryWorldToScreen(from + lift, display, out Vector2 fromPx))
                previous = fromPx;
            foreach (Vector3 point in preview.Points)
            {
                if (!_window.Camera.TryWorldToScreen(point + lift, display, out Vector2 px))
                { previous = null; continue; }
                if (previous is Vector2 f) DrawDashedLine(draw, f, px, DevPendingColor, 6f, 5f);
                draw.AddCircleFilled(px, 3f, DevPendingColor);
                previous = px;
            }
        }
    }

    // ── DB route + DB-only labels ────────────────────────────────────────────

    private const uint DevGuidPathColor = 0xffffe500;       // cyan: per-GUID creature_movement
    private const uint DevTemplatePathColor = 0xff28c8ff;   // gold: shared creature_movement_template

    /// <summary>The authored patrol route for one spawn, resolved exactly like vmangos
    /// (guid table first, else entry template): solid polyline, numbered nodes, waittime
    /// badges. Color says provenance — cyan = this spawn's own path, gold = template
    /// shared by every spawn of the entry.</summary>
    private void DrawDevDbRoute(ImDrawListPtr draw, Vector2 display, DevWorldData world,
        uint spawnGuid, uint entry, HashSet<(DevPathOrigin, uint, uint)> drawnPaths,
        bool interactive)
    {
        (DevPathOrigin origin, uint key, uint pathId, DevWaypointRow[]? nodes) =
            world.ResolvePath(spawnGuid, entry);
        if (origin == DevPathOrigin.None || nodes is null || nodes.Length == 0) return;
        bool drawRoute = drawnPaths.Add((origin, key, pathId));
        // A shared template route may already have been drawn for a different spawn.
        // Still build hitboxes when this call belongs to the inspected creature.
        if (!drawRoute && !interactive) return;

        uint color = origin == DevPathOrigin.Guid ? DevGuidPathColor : DevTemplatePathColor;
        var lift = new Vector3(0f, 0f, 0.2f);
        Vector2? previous = null;
        for (int nodeIndex = 0; nodeIndex < nodes.Length; nodeIndex++)
        {
            DevWaypointRow node = nodes[nodeIndex];
            if (!_window.Camera.TryWorldToScreen(node.Position + lift, display, out Vector2 px))
            { previous = null; continue; }
            if (drawRoute)
            {
                if (previous is Vector2 from) draw.AddLine(from, px, color, 2f);
                draw.AddCircleFilled(px, 3.5f, color);
                string label = node.WaitMs > 0
                    ? $"{node.Point} ({node.WaitMs / 1000f:0.#}s)" : node.Point.ToString();
                draw.AddText(px + new Vector2(5f, -14f), 0xc0000000u, label);
                draw.AddText(px + new Vector2(4f, -15f), color, label);
            }
            if (interactive)
                _devDbNodeHits.Add((
                    new ScreenRect(px.X - 12f, px.Y - 12f, px.X + 12f, px.Y + 12f),
                    spawnGuid, entry, nodeIndex));
            previous = px;
        }
        // A patrol loops: close the ring visually so route direction reads at a glance.
        if (drawRoute && nodes.Length > 2 &&
            _window.Camera.TryWorldToScreen(nodes[0].Position + lift, display, out Vector2 first) &&
            _window.Camera.TryWorldToScreen(nodes[^1].Position + lift, display, out Vector2 last))
            DrawDashedLine(draw, last, first, (color & 0x00ffffff) | 0x80000000, 6f, 6f);
    }

    /// <summary>Dim label for a mangos.creature row the server is not streaming right now
    /// (despawned, on respawn timer, or beyond the streaming eye).</summary>
    private void DrawDevDbOnlyLabel(ImDrawListPtr draw, Vector2 display, DevSpawnRow spawn)
    {
        Vector3 anchor = spawn.Position + new Vector3(0f, 0f, 0.25f);
        if (!_window.Camera.TryWorldToScreen(anchor, display, out Vector2 screen)) return;
        float fontSize = Math.Clamp(ProjectedWorldPitch(anchor, screen, 0.30f, display), 8f, 18f);
        NpcTemplateInfo? tpl = DevTemplateFor(spawn.Entry);
        string name = _creatureNames.GetValueOrDefault(spawn.Entry, tpl?.Name ?? $"entry {spawn.Entry}");
        string text = $"{name}  g{spawn.Guid}  (not streamed)";
        ImFontPtr font = ImGui.GetFont();
        Vector2 extent = ImGui.CalcTextSize(text) * (fontSize / MathF.Max(ImGui.GetFontSize(), 1f));
        var position = new Vector2(screen.X - extent.X * 0.5f, screen.Y);
        draw.AddText(font, fontSize, position + Vector2.One, 0x90000000u, text);
        draw.AddText(font, fontSize, position, 0xb0a8b0b4, text);
    }

    private void DrawDevSpawnLabel(ImDrawListPtr draw, Vector2 display, WorldEntity unit, bool live)
    {
        Vector3 anchor = unit.Position + new Vector3(0f, 0f, 0.25f);
        if (!_window.Camera.TryWorldToScreen(anchor, display, out Vector2 screen)) return;
        float fontSize = Math.Clamp(ProjectedWorldPitch(anchor, screen, 0.35f, display), 9f, 22f);

        NpcTemplateInfo? tpl = DevTemplateFor(unit.Entry);
        string name = _creatureNames.GetValueOrDefault(unit.Entry, tpl?.Name ?? "");
        string headline = name.Length > 0
            ? $"{name}  L{unit.Level}" : $"entry {unit.Entry}  L{unit.Level}";
        string detail = $"e{unit.Entry} g{unit.Guid & 0xFFFFFF}" +
                        (unit.Spline is not null ? "  ~moving" : "");

        bool aggroing = _devAggroingMe.Contains(unit.Guid);
        uint color = live
            ? ReactionColorU32(ReactionTargetTowardPlayer(unit), unit.IsPlayer, unit.IsDead)
            : 0xffe8e8e8;
        ImFontPtr font = ImGui.GetFont();

        void Line(string text, float size, uint tint, ref Vector2 at)
        {
            Vector2 extent = ImGui.CalcTextSize(text) * (size / MathF.Max(ImGui.GetFontSize(), 1f));
            var position = new Vector2(at.X - extent.X * 0.5f, at.Y);
            draw.AddText(font, size, position + Vector2.One, 0xc0000000u, text);
            draw.AddText(font, size, position, tint, text);
            at.Y += extent.Y + 1f;
        }

        Vector2 cursor = screen;
        if (aggroing) Line("! AGGRO", fontSize, 0xff2020ee, ref cursor);
        Line(headline, fontSize, color, ref cursor);
        Line(detail, fontSize * 0.8f, 0xffb0b0b0, ref cursor);
    }

    private void DrawDevPath(ImDrawListPtr draw, Vector2 display, List<Vector3> path, uint color)
    {
        Vector2? previous = null;
        foreach (Vector3 point in path)
        {
            if (!_window.Camera.TryWorldToScreen(
                    point + new Vector3(0f, 0f, 0.15f), display, out Vector2 screen))
            { previous = null; continue; }
            if (previous is Vector2 from) DrawDashedLine(draw, from, screen, color, 8f, 5f);
            draw.AddCircleFilled(screen, 2.5f, color);
            previous = screen;
        }
    }

    /// <summary>A stable per-guid hue so neighbouring patrol routes stay tellable-apart.</summary>
    private static uint DevPathColor(ulong guid)
    {
        float hue = (guid * 2654435761u % 360u) / 360f;
        float H(float shift)
        {
            float t = (hue + shift) % 1f;
            float v = Math.Clamp(MathF.Abs(t * 6f - 3f) - 1f, 0f, 1f);
            return 0.35f + 0.65f * v;
        }
        byte r = (byte)(H(0f) * 255f), g = (byte)(H(2f / 3f) * 255f), b = (byte)(H(1f / 3f) * 255f);
        return 0xE0000000u | ((uint)b << 16) | ((uint)g << 8) | r;
    }

    /// <summary>Bound the observed-path store: every 5 s, once it is large, drop routes
    /// whose creature no longer exists client-side.</summary>
    private void PruneDevObservedPaths()
    {
        double now = NowSeconds();
        if (now - _devPathPruneAt < 5.0) return;
        _devPathPruneAt = now;
        if (_devObservedPaths.Count <= 300) return;
        List<ulong> stale = [];
        foreach (ulong guid in _devObservedPaths.Keys)
            if (!_entities.TryGet(guid, out _)) stale.Add(guid);
        foreach (ulong guid in stale) _devObservedPaths.Remove(guid);
    }
}

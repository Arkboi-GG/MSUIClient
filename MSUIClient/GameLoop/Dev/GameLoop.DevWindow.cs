using System.Numerics;
using ImGuiNET;
using Silk.NET.Input;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// The NPC dev window (Ctrl+N): a proper chrome window (creator idiom, not the
// raw F1 ImGui stack) for observing NPC spawns, pathing and aggro radii while
// flying the free view — in live mode AND creator mode. Reads DB truth over
// HTTP from MangosSuperUI; edits (later phases) become change-set files, never
// direct writes. Overlay rendering lives in Program.DevWindow.Overlays.cs.
// ─────────────────────────────────────────────────────────────────────────────

public sealed partial class GameLoop
{
    private bool _devWindowOpen;
    private bool _devWindowKeyWasDown;
    private DevDataClient? _devData;

    private DevDataClient DevData => _devData ??= new DevDataClient(_config.RepoRoot);

    /// <summary>Ctrl+N edge toggle, run from UpdateControlInput beside Ctrl+F. Works in
    /// live and creator mode (no in-world gate — the window itself reports "no world").</summary>
    private void UpdateDevWindowInput(bool typing)
    {
        bool ctrl = InputKeyDown(Key.ControlLeft) || InputKeyDown(Key.ControlRight);
        bool pressed = ctrl && InputKeyDown(Key.N);
        if (pressed && !_devWindowKeyWasDown && !typing) ToggleDevWindow();
        _devWindowKeyWasDown = pressed;
    }

    private void ToggleDevWindow()
    {
        _devWindowOpen = !_devWindowOpen;
        if (!_devWindowOpen) CancelDevEdit();   // never leave an armed mode eating clicks
        if (_devWindowOpen && DevData.Templates is null && !DevData.Fetching)
            DevData.BeginFetchTemplates(Settings.DevWindow.SuiBaseUrl);
    }

    // ── overlay focus set (the "Selected only" scope) ────────────────────────

    /// <summary>Creatures the "Selected only" overlay scope draws. Maintained by the
    /// world-click handler below while the window is open; runtime-only.</summary>
    private readonly HashSet<ulong> _devFocusGuids = [];

    /// <summary>
    /// Focus-set maintenance for world LEFT clicks while the window is open, called
    /// from both click routers (normal targeting and the free view) after the edit-mode
    /// intercept. Ctrl+LeftClick toggles a creature in/out of the set and swallows the
    /// click (multi-select must not retarget or clear the marquee selection); a plain
    /// click retargets the set at what was clicked — or clears it on empty ground — and
    /// falls through to the normal click behaviour. Returns true when the click is
    /// consumed.
    /// </summary>
    private bool HandleDevFocusClick(ulong picked)
    {
        if (!_devWindowOpen) return false;
        bool ctrl = InputKeyDown(Key.ControlLeft) || InputKeyDown(Key.ControlRight);
        bool creature = picked != 0 &&
            _entities.TryGet(picked, out WorldEntity unit) && unit.IsCreature;

        if (!ctrl)
        {
            // Keep the set in step with plain targeting so flipping to "Selected
            // only" always starts from whatever is currently clicked. A click on a
            // PLAYER (free-view take-command) leaves the set alone — commanding a
            // toon mid-inspection must not blank the overlays being studied.
            if (creature) { _devFocusGuids.Clear(); _devFocusGuids.Add(picked); }
            else if (picked == 0) _devFocusGuids.Clear();
            return false;
        }

        if (!creature) return true;   // ctrl+click on nothing: keep the set being built
        if (!_devFocusGuids.Remove(picked))
        {
            _devFocusGuids.Add(picked);
            CommitSelection(picked, beginAttack: false);   // panel shows the newest member
        }
        return true;
    }

    /// <summary>Low-24-bit spawn guids of the focus set (the live↔DB join key), or null
    /// when the scope is "All" — the overlay passes use null as "no filtering".</summary>
    private HashSet<uint>? DevFocusSpawnLows()
    {
        if (!Settings.DevWindow.FocusSelectedOnly) return null;
        var lows = new HashSet<uint>();
        foreach (ulong guid in _devFocusGuids)
            if (GuidInfo.High(guid) == GuidInfo.HighUnit)
                lows.Add((uint)(guid & 0xFFFFFF));
        return lows;
    }

    // ── world-data fetch trigger (control logic only — I/O lives in DevDataClient) ──

    private double _devWorldFetchCheckedAt;
    private Vector2 _devWorldFetchCentre;

    /// <summary>Runs every frame the window is open: (re)fetch spawn/waypoint data when
    /// none is loaded, the map changed, or an area-limited snapshot fell 250 yd behind
    /// the camera. Debounced to one attempt per 5 s; never blocks the frame.</summary>
    private void UpdateDevWorldFetch()
    {
        if (DevData.WorldFetching || NowSeconds() - _devWorldFetchCheckedAt < 5.0) return;
        DevWorldData? world = DevData.World;
        int map = _config.Start.Map;
        Vector3 centre3 = _controller?.Position ?? Vector3.Zero;
        var centre = new Vector2(centre3.X, centre3.Y);

        bool need = world is null || (world.Map != map && world.Map != -1) ||
                    (world is { WholeMap: false } &&
                     Vector2.Distance(centre, _devWorldFetchCentre) > 250f);
        if (!need) return;
        _devWorldFetchCheckedAt = NowSeconds();
        _devWorldFetchCentre = centre;
        DevData.BeginFetchWorld(Settings.DevWindow.SuiBaseUrl, map, centre.X, centre.Y);
    }

    /// <summary>The window shell: creator chrome, drawn from a mode-neutral BuildGui
    /// call site (NOT behind the creator-mode return that hides the F1 overlay).</summary>
    private void DrawDevWindow()
    {
        if (!_devWindowOpen) return;
        bool inWorld = _net is { IsInWorld: true };
        if (!inWorld && !_creatorWorldRequested) return;

        _activePanelTune = "npc-dev";
        float cs = CreatorUiScale;
        float s = MathF.Max(ImGui.GetIO().DisplaySize.Y / GlueCanvasH, 0.5f) * cs;
        // Default rect scales by s (display factor × creator dial), unlike the creator
        // panels' cs-only sizing — at 4K a cs-sized default opens as a sliver.
        ImGui.SetNextWindowPos(
            new Vector2(ImGui.GetIO().DisplaySize.X - 470f * s, 64f * s), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(430f * s, 580f * s), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(260f * cs, 180f * cs),
            new Vector2(float.MaxValue, float.MaxValue));
        PushCreatorStyle();
        if (!ImGui.Begin("###npc-dev", CreatorChromeFlags))
        {
            ImGui.End();
            PopCreatorStyle();
            _activePanelTune = null;
            return;
        }
        ClampCreatorWindowOnScreen();
        if (DrawCreatorPanelChrome("NPC Dev", "npc-dev")) { _devWindowOpen = false; CancelDevEdit(); }
        ImGui.SetWindowFontScale(CreatorTextScale);

        UpdateDevWorldFetch();
        DrawDevWindowToolbar();
        BeginCreatorContent();
        DrawDevOverlaySection(inWorld);
        DrawDevAggroSection(inWorld);
        DrawDevSelectionSection(inWorld);
        DrawDevChangeSetSection();
        DrawDevDataSection();
        EndCreatorContent();

        ImGui.SetWindowFontScale(1f);
        ImGui.End();
        PopCreatorStyle();
        _activePanelTune = null;

        // The gear's layout popup is normally drawn by the creator HUD; in live mode
        // nothing else draws it, so the dev window hosts it itself.
        if (!_creatorWorldRequested) DrawCreatorPanelTunePopup();
    }

    private void DrawDevWindowToolbar()
    {
        float cs = CreatorUiScale;
        var data = DevData;
        string status =
            data.Fetching ? "fetching creature_template..."
            : data.Templates is { } t
                ? $"{t.ByEntry.Count} templates ({t.Source}, {DescribeAge(DateTime.UtcNow - t.FetchedUtc)})"
                : "no template data";
        ImGui.TextDisabled(status);
        float avail = ImGui.GetContentRegionAvail().X;
        float buttonW = ImGui.CalcTextSize("Refresh DB").X + 14f * cs;
        ImGui.SameLine(MathF.Max(avail - buttonW, 0f));
        if (ImGui.SmallButton("Refresh DB") && !data.Fetching && !data.WorldFetching)
        {
            data.BeginFetchTemplates(Settings.DevWindow.SuiBaseUrl, forceRefresh: true);
            Vector3 centre = _controller?.Position ?? Vector3.Zero;
            data.BeginFetchWorld(Settings.DevWindow.SuiBaseUrl, _config.Start.Map,
                centre.X, centre.Y, forceRefresh: true);
        }

        string worldStatus =
            data.WorldFetching ? "fetching spawn/waypoint rows..."
            : data.World is { } w && w.Map != -1
                ? $"{w.SpawnsByGuid.Count} spawn rows, {w.GuidPaths.Count}+{w.TemplatePaths.Count} paths " +
                  $"({w.Source}{(w.WholeMap ? ", whole map" : ", area")}, {DescribeAge(DateTime.UtcNow - w.FetchedUtc)})"
                : "no spawn/waypoint data yet";
        ImGui.TextDisabled(worldStatus);
        if (_devStreamedInRange + _devDbOnlyInRange > 0)
            ImGui.TextDisabled(
                $"in range: {_devStreamedInRange} streamed, {_devDbOnlyInRange} DB-only (not streamed)");

        if (DevData.Templates is { Error: { } error })
            ImGui.TextColored(new Vector4(1f, 0.45f, 0.35f, 1f), error);
        ImGui.Separator();
    }

    private static string DescribeSpawnSecs(uint min, uint max) =>
        min == max ? $"{min}s" : $"{min}-{max}s";

    private static string DescribeAge(TimeSpan age) =>
        age.TotalMinutes < 1 ? "just now"
        : age.TotalHours < 1 ? $"{(int)age.TotalMinutes} min old"
        : age.TotalDays < 1 ? $"{age.TotalHours:0.0} h old"
        : $"{age.TotalDays:0.0} d old";

    // ── sections ─────────────────────────────────────────────────────────────

    private void DrawDevOverlaySection(bool inWorld)
    {
        if (!ImGui.CollapsingHeader("Overlays", ImGuiTreeNodeFlags.DefaultOpen)) return;
        var dev = Settings.DevWindow;
        bool save = false;

        bool Check(string label, bool value, Action<bool> set)
        {
            if (ImGui.Checkbox(label, ref value)) { set(value); return true; }
            return false;
        }

        // Scope: everything in range, or only the Ctrl+LeftClick focus set.
        if (ImGui.RadioButton("All NPCs", !dev.FocusSelectedOnly) && dev.FocusSelectedOnly)
        {
            dev.FocusSelectedOnly = false;
            save = true;
        }
        ImGui.SameLine();
        if (ImGui.RadioButton("Selected only", dev.FocusSelectedOnly) && !dev.FocusSelectedOnly)
        {
            dev.FocusSelectedOnly = true;
            save = true;
            // Start from the current target rather than an empty screen.
            if (_devFocusGuids.Count == 0 && _selectionGuid != 0 &&
                _entities.TryGet(_selectionGuid, out WorldEntity target) && target.IsCreature)
                _devFocusGuids.Add(_selectionGuid);
        }
        if (dev.FocusSelectedOnly)
            ImGui.TextDisabled(
                $"{_devFocusGuids.Count} in the set - Ctrl+LeftClick creatures to add/remove");

        save |= Check("Spawn labels", dev.ShowSpawnLabels, v => dev.ShowSpawnLabels = v);
        save |= Check("Observed pathing (recorded while the window is open)",
            dev.ShowObservedPaths, v => dev.ShowObservedPaths = v);
        save |= Check("DB patrol routes (creature_movement)", dev.ShowDbPaths, v => dev.ShowDbPaths = v);
        save |= Check("DB spawn points + wander circles", dev.ShowDbSpawns, v => dev.ShowDbSpawns = v);
        save |= Check("Aggro discs", dev.ShowAggroDiscs, v => dev.ShowAggroDiscs = v);
        save |= Check("Highlight who would aggro my toon (through walls)",
            dev.ShowWhoAggros, v => dev.ShowWhoAggros = v);
        save |= Check("Hostiles only", dev.HostilesOnly, v => dev.HostilesOnly = v);

        float range = dev.OverlayRange;
        ImGui.SetNextItemWidth(CreatorControlWidth);
        if (ImGui.SliderFloat("Overlay range (yd)", ref range, 40f, 400f, "%.0f"))
            dev.OverlayRange = range;
        save |= ImGui.IsItemDeactivatedAfterEdit();

        float opacity = dev.DiscOpacity;
        ImGui.SetNextItemWidth(CreatorControlWidth);
        if (ImGui.SliderFloat("Disc opacity", ref opacity, 0.05f, 0.9f, "%.2f"))
            dev.DiscOpacity = opacity;
        save |= ImGui.IsItemDeactivatedAfterEdit();

        if (!inWorld)
            ImGui.TextDisabled("Creator mode: pathing/who-aggros need the live server.");
        if (save) SettingsFile?.Save();
    }

    private void DrawDevAggroSection(bool inWorld)
    {
        if (!ImGui.CollapsingHeader("Aggro reference", ImGuiTreeNodeFlags.DefaultOpen)) return;
        var dev = Settings.DevWindow;
        bool save = false;

        ReadOnlySpan<string> modes = ["Level60", "MyLevel", "NpcLevel"];
        ReadOnlySpan<string> labels =
            ["vs level 60 (raid)", "vs my toon's level (dungeon)", "vs the NPC's own level"];
        int current = 0;
        for (int i = 0; i < modes.Length; i++)
            if (dev.AggroReference == modes[i]) current = i;
        ImGui.SetNextItemWidth(CreatorControlWidth);
        if (ImGui.BeginCombo("Reference level", labels[current]))
        {
            for (int i = 0; i < modes.Length; i++)
                if (ImGui.Selectable(labels[i], i == current) && i != current)
                {
                    dev.AggroReference = modes[i];
                    save = true;
                }
            ImGui.EndCombo();
        }

        int bands = dev.AggroBandCount;
        ImGui.SetNextItemWidth(CreatorControlWidth);
        if (ImGui.SliderInt("Level bands", ref bands, 1, 6))
            dev.AggroBandCount = bands;
        save |= ImGui.IsItemDeactivatedAfterEdit();

        // Legend: innermost band = the reference level, each outer band one level lower.
        // The swatch is drawn (the UI font has no U+25A0).
        uint reference = DevReferenceLevelPreview();
        for (int k = 0; k < Math.Clamp(dev.AggroBandCount, 1, 6); k++)
        {
            Vector3 tint = DevBandTint(k);
            float em = ImGui.GetTextLineHeight();
            Vector2 at = ImGui.GetCursorScreenPos();
            ImGui.GetWindowDrawList().AddRectFilled(
                at + new Vector2(2f, em * 0.15f), at + new Vector2(2f + em * 0.7f, em * 0.85f),
                ImGui.ColorConvertFloat4ToU32(new Vector4(tint, 1f)));
            ImGui.Dummy(new Vector2(em * 0.7f + 6f, em));
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(tint, 1f),
                k == 0 ? $"level {reference} (reference)"
                       : $"level {Math.Max(1, (int)reference - k)}");
        }
        ImGui.TextDisabled("radius = detection_range - (targetLvl - npcLvl), floor min(det, 5)");
        ImGui.TextDisabled("vmangos Creature::GetAttackDistance; distance-only estimate (no LoS)");
        if (save) SettingsFile?.Save();
    }

    private void DrawDevSelectionSection(bool inWorld)
    {
        if (!ImGui.CollapsingHeader("Selected NPC", ImGuiTreeNodeFlags.DefaultOpen)) return;

        if (_selectionGuid == 0 || !_entities.TryGet(_selectionGuid, out WorldEntity unit) ||
            !unit.IsCreature)
        {
            ImGui.TextDisabled("Click a creature (free view: left-click) to inspect it.");
            return;
        }

        uint entry = unit.Entry;
        uint spawnGuid = (uint)(unit.Guid & 0xFFFFFF);
        NpcTemplateInfo? tpl = DevTemplateFor(entry);
        string name = _creatureNames.GetValueOrDefault(entry, tpl?.Name ?? $"creature {entry}");

        ImGui.Text($"{name}  (level {unit.Level})");
        ImGui.TextDisabled($"entry {entry}   spawn guid {spawnGuid}   faction {unit.Fields.FactionTemplate}");
        if (inWorld)
            ImGui.TextDisabled($"reaction to me: {ReactionTargetTowardPlayer(unit)}");

        ImGui.Spacing();
        ImGui.TextDisabled("DATA PROVENANCE");
        if (tpl is not null)
        {
            ImGui.Text($"detection_range {tpl.DetectionRange:0.#}");
            ImGui.SameLine();
            ImGui.TextDisabled("mangos.creature_template (per-ENTRY: every spawn)");
            ImGui.Text($"call_for_help {tpl.CallForHelpRange:0.#}   leash {tpl.LeashRange:0.#}   movement_type {tpl.MovementType}");
            if ((tpl.FlagsExtra & DevFlagNoAggro) != 0)
                ImGui.TextColored(new Vector4(1f, 0.6f, 0.3f, 1f), "flags_extra: NO_AGGRO");
            if ((tpl.StaticFlags & DevFlagIgnoreCombat) != 0)
                ImGui.TextColored(new Vector4(1f, 0.6f, 0.3f, 1f), "static_flags: IGNORE_COMBAT");
            if ((tpl.StaticFlags & DevFlagSessile) != 0)
                ImGui.TextDisabled("static_flags: SESSILE (never chases)");
        }
        else
            ImGui.TextDisabled("template not fetched - detection_range assumed 18");

        DevWorldData? world = DevData.World;
        DevSpawnRow? spawn = world?.SpawnsByGuid.GetValueOrDefault(spawnGuid);
        if (spawn is not null)
        {
            ImGui.Text($"spawn ({spawn.Position.X:0.#}, {spawn.Position.Y:0.#}, {spawn.Position.Z:0.#})" +
                       $"   respawn {DescribeSpawnSecs(spawn.SpawnSecsMin, spawn.SpawnSecsMax)}");
            ImGui.SameLine();
            ImGui.TextDisabled($"mangos.creature guid={spawnGuid}");
            string movement = spawn.MovementType switch
            {
                0 => "movement_type 0 (idle)",
                1 => $"movement_type 1 (random, wander {spawn.WanderDistance:0.#} yd)",
                2 => "movement_type 2 (waypoint)",
                _ => $"movement_type {spawn.MovementType}",
            };
            ImGui.Text(movement);
            if (spawn.EntryPool.Length > 1)
                ImGui.TextDisabled($"random entry pool: {string.Join(", ", spawn.EntryPool)}");

            (DevPathOrigin origin, uint key, uint pathId, DevWaypointRow[]? nodes) =
                world!.ResolvePath(spawnGuid, spawn.Entry);
            switch (origin)
            {
                case DevPathOrigin.Guid:
                    ImGui.Text($"patrol: {nodes!.Length} points");
                    ImGui.SameLine();
                    ImGui.TextDisabled($"creature_movement id={key} (THIS spawn only)");
                    break;
                case DevPathOrigin.Template:
                    ImGui.Text($"patrol: {nodes!.Length} points");
                    ImGui.SameLine();
                    ImGui.TextDisabled(
                        $"creature_movement_template entry={key} path {pathId} (every spawn of the entry)");
                    break;
                default:
                    ImGui.TextDisabled("patrol: none in the movement tables");
                    break;
            }
        }
        else
            ImGui.TextDisabled(world is null
                ? "spawn row: waiting for spawn/waypoint data"
                : $"spawn row: guid {spawnGuid} not in the fetched {(world.WholeMap ? "map" : "area")} data");

        ImGui.Spacing();
        uint myLevel = DevMyLevel() ?? 60;
        float vsMe = DevAggroRadius(unit.Level, myLevel, tpl);
        float vs60 = DevAggroRadius(unit.Level, 60, tpl);
        ImGui.Text($"aggro radius vs me (L{myLevel}): {vsMe:0.#} yd    vs L60: {vs60:0.#} yd");
        if (inWorld && DevPlayerPosition() is { } me)
            ImGui.Text($"distance to my toon: {Vector3.Distance(me, unit.Position):0.#} yd");

        DrawDevBaselineControls(spawnGuid, entry);
        DrawDevEditControls(spawn, entry, tpl);
    }

    private void DrawDevDataSection()
    {
        if (!ImGui.CollapsingHeader("Data source")) return;
        var dev = Settings.DevWindow;

        string url = dev.SuiBaseUrl;
        ImGui.SetNextItemWidth(CreatorControlWidth);
        if (ImGui.InputText("MangosSuperUI URL", ref url, 128))
            dev.SuiBaseUrl = url;
        if (ImGui.IsItemDeactivatedAfterEdit()) SettingsFile?.Save();

        ImGui.TextDisabled(DevData.CacheAge is { } age
            ? $"disk cache: dev-cache\\creature_template.csv ({DescribeAge(age)})"
            : "disk cache: none yet");
        ImGui.TextDisabled("edits become change-set files for MangosSuperUI (phase 3+),");
        ImGui.TextDisabled("applied there with the audit trail - never written directly.");
    }

    /// <summary>The reference level the legend describes (creature-independent preview).</summary>
    private uint DevReferenceLevelPreview() => Settings.DevWindow.AggroReference switch
    {
        "Level60" => 60,
        "MyLevel" => DevMyLevel() ?? 60,
        _ => DevMyLevel() ?? 60,   // NpcLevel varies per creature; preview with my level
    };
}

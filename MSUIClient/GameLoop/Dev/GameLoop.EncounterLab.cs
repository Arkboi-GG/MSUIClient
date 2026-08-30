using System.Numerics;
using ImGuiNET;
using Silk.NET.Input;
using MSUIClient.Engine;
using MSUIClient.Formats;
using MSUIClient.Net;
using MSUIClient.World.Encounters;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// THE ENCOUNTER LAB (Ctrl+E) — a debugger, animator and inspector for NPC
// combat behaviour.
//
// Where the NPC dev window (Ctrl+N) is SPATIAL and STATIC — spawns, routes,
// aggro radii — this window is TEMPORAL and DYNAMIC. It runs a deterministic
// fixed-step simulation of an encounter definition and lets you play, pause,
// single-step, scrub and rewind it, drop a body capsule anywhere and ask what
// can hit it, when, and why.
//
// Deliberately a separate subsystem with its own data client, its own settings
// block and its own files. It shares the creator chrome and the ground-decal
// renderer; it shares no state with the NPC dev window, so neither feature can
// break the other.
//
// The simulation engine itself is in World/Encounters/ and knows nothing about
// GameLoop, GL or ImGui — this file is the control layer only. Rendering lives
// in GameLoop.EncounterLab.Overlays.cs; the live recorder in
// GameLoop.EncounterLab.Tape.cs.
// ─────────────────────────────────────────────────────────────────────────────

public sealed partial class GameLoop
{
    private bool _encounterLabOpen;

    private EncounterDataClient? _encounterData;
    private EncounterLibrary? _encounterLibrary;
    private CombatPlanStore? _encounterCombatPlanStore;
    private PositioningScriptStore? _encounterPositioningStore;
    private EncounterSpellFacts? _encounterFacts;

    /// <summary>The definition currently loaded into the Lab, and how it was resolved.</summary>
    private EncounterDefinition? _encounterDefinition;
    private string _encounterSourceNote = "nothing loaded";

    private EncounterSim? _encounterSim;
    private bool _encounterPlaying;
    private double _encounterPlaybackCarryMs;
    /// <summary>The instant the overlays and the probe report describe. Equal to the
    /// simulation head while playing; free while scrubbing.</summary>
    private int _encounterViewMs;
    private bool _encounterScrubbing;

    private readonly ProbeTrajectory _encounterProbe = new();
    private ProbeReport _encounterProbeReport = ProbeReport.Empty;
    private bool _encounterProbeDirty = true;

    /// <summary>What the next world click places. Armed from the Scenario section;
    /// a click while armed is swallowed so it never becomes a target or an order.</summary>
    private EncounterPlacement _encounterPlacing = EncounterPlacement.None;
    private string? _encounterPlacingActorKey;
    private readonly List<EncounterActorSpec> _encounterScenario = [];
    private int _encounterDummySerial;

    /// <summary>The owner's aggro timeline: who she is on, from when. Sorted by
    /// time; the sim consumes it verbatim and never second-guesses it.</summary>
    private readonly List<TimedAggro> _encounterAggroPlan = [];

    /// <summary>Standing orders per (phase × job): what melee does when she lifts
    /// off, where ranged falls back to. Seeded with defaults when a definition
    /// loads; the owner edits it in the Scenario section.</summary>
    private readonly List<RaidPhaseDirective> _encounterPlaybook = [];

    /// <summary>The scenario's authored raid doctrine. Defaults are the sensible
    /// raid; class-gated rules (Fear Ward chains, add control) and bucket
    /// assignments are authored onto this instance and travel with the exported
    /// raid plan. The settings toggle gates whether the sim consumes it at all.</summary>
    private RaidDoctrine _encounterDoctrine = new();

    /// <summary>Orders QUEUED for GO, per body key — the owner's model: click a
    /// bot, shift-click its waypoints, repeat for every bot, read the dotted
    /// plan on the floor, THEN send everyone at once. Nothing here moves a body;
    /// GO (or Play) commits each list as real timed moves — first leg at the
    /// current instant, the rest chained on arrival.</summary>
    private readonly Dictionary<string, List<EncounterStagedLeg>> _encounterStagedOrders =
        new(StringComparer.Ordinal);

    /// <summary>One entry per waypoint click. A multi-selection click adds one leg to each
    /// selected body and therefore undoes as one authoring gesture.</summary>
    private readonly List<string[]> _encounterStagedUndo = [];

    private readonly record struct EncounterStagedLeg(Vector3 Position, float ArrivalFacing)
    {
        public bool HasFacing => !float.IsNaN(ArrivalFacing);
    }

    /// <summary>Ability keys toggled on in the phase-aware Ability Visualizer. Spatial
    /// abilities draw their landing footprint at full strength; summon mechanics draw
    /// their authored spawn points; mechanics with neither get an honest boss-anchored
    /// callout. Session state, keyed by <see cref="EncounterAbility.Key"/>.</summary>
    private readonly HashSet<string> _encounterVisualizedAbilities = new(StringComparer.Ordinal);

    /// <summary>Clicks STAGE while the fight at the current view is un-pulled —
    /// authoring time, nothing runs early. Once she is engaged, clicks land as
    /// immediate timed moves (the paused mid-fight what-if workflow). Teleports
    /// are always immediate; that is their whole point.</summary>
    private bool EncounterStagingActive =>
        _encounterSim is not { } sim || sim.EngagedAtMs < 0 ||
        _encounterViewMs < sim.EngagedAtMs;

    private int EncounterStagedCount
    {
        get
        {
            int count = 0;
            foreach (List<EncounterStagedLeg> legs in _encounterStagedOrders.Values)
                count += legs.Count;
            return count;
        }
    }

    private void StageEncounterLeg(string key, Vector3 position)
    {
        if (!_encounterStagedOrders.TryGetValue(key, out List<EncounterStagedLeg>? legs))
            _encounterStagedOrders[key] = legs = [];
        legs.Add(new EncounterStagedLeg(position, float.NaN));
    }

    private void RememberEncounterWaypointGesture(IEnumerable<string> keys) =>
        _encounterStagedUndo.Add(keys.Distinct(StringComparer.Ordinal).ToArray());

    private bool UndoEncounterWaypoint()
    {
        while (_encounterStagedUndo.Count > 0)
        {
            string[] keys = _encounterStagedUndo[^1];
            _encounterStagedUndo.RemoveAt(_encounterStagedUndo.Count - 1);
            int removed = 0;
            foreach (string key in keys)
            {
                if (!_encounterStagedOrders.TryGetValue(key, out List<EncounterStagedLeg>? legs) ||
                    legs.Count == 0) continue;
                legs.RemoveAt(legs.Count - 1);
                removed++;
                if (legs.Count == 0) _encounterStagedOrders.Remove(key);
            }
            if (removed == 0) continue;
            EndEncounterOrientSpin(commit: false);
            AddChatMessage(removed == 1
                ? "Waypoint undone."
                : $"Waypoint undone for {removed} selected bodies.");
            return true;
        }
        return false;
    }

    private void ClearEncounterStagedOrders()
    {
        _encounterStagedOrders.Clear();
        _encounterStagedUndo.Clear();
    }

    /// <summary>GO: commit the whole staged plan and run it. Every body's first
    /// leg fires at the current instant — the raid moves as one — and later legs
    /// chain on arrival, exactly like hand-authored waypoint chains.</summary>
    private void EncounterGoStagedOrders()
    {
        if (_encounterStagedOrders.Count == 0) return;
        int atMs = Math.Max(_encounterViewMs, 0);
        foreach ((string key, List<EncounterStagedLeg> legs) in _encounterStagedOrders)
            for (int i = 0; i < legs.Count; i++)
                AppendScenarioMove(key, atMs, legs[i].Position,
                    anchor: i == 0 ? MoveAnchor.AtTime : MoveAnchor.AfterPrevious,
                    arrivalFacing: legs[i].ArrivalFacing);
        ClearEncounterStagedOrders();
        RebuildEncounterSimKeepingView();
        _encounterPlaying = true;
        AddChatMessage("GO — the plan is running.");
    }

    private enum EncounterPlacement { None, Probe, ProbeWaypoint, Actor, Boss, ActorMove, PlaybookSpot, PositioningSpot }

    /// <summary>Context for an armed PositioningSpot placement: which phase of the
    /// positioning draft the next ground click writes a MoveToSpot into.</summary>
    private string _encounterPlacingPositioningPhase = "";

    /// <summary>Context for an armed PlaybookSpot placement: which cell of the
    /// playbook table the next ground click authors.</summary>
    private string _encounterPlacingPlaybookPhase = "";
    private RaidJob _encounterPlacingPlaybookJob = RaidJob.None;

    private EncounterDataClient EncounterData => _encounterData ??= new EncounterDataClient(_config.RepoRoot);

    private EncounterLibrary EncounterLibraryRef =>
        _encounterLibrary ??= new EncounterLibrary(Path.Combine(_config.RepoRoot, "encounters"));

    private CombatPlanStore EncounterCombatPlanStoreRef
    {
        get
        {
            if (_encounterCombatPlanStore is not null) return _encounterCombatPlanStore;
            _encounterCombatPlanStore = new CombatPlanStore(
                Path.Combine(_config.RepoRoot, "combat-plans.json"));
            _encounterCombatPlanStore.Load();
            return _encounterCombatPlanStore;
        }
    }

    private PositioningScriptStore EncounterPositioningStoreRef
    {
        get
        {
            if (_encounterPositioningStore is not null) return _encounterPositioningStore;
            _encounterPositioningStore = new PositioningScriptStore(
                Path.Combine(_config.RepoRoot, "positioning-scripts.json"));
            _encounterPositioningStore.Load();
            return _encounterPositioningStore;
        }
    }

    // ── input ────────────────────────────────────────────────────────────────

    /// <summary>Two bindings (RTS Controls: Encounter Lab, Undo Waypoint), defaulting to the
    /// Ctrl+E / Ctrl+Z they were hard-coded as. Run from UpdateControlInput beside the free-view
    /// toggle. Works in live and creator mode — the simulator needs no server at all, which is
    /// the entire point of running it here.</summary>
    private void UpdateEncounterLabInput(bool typing)
    {
        if (BindingPressedEdge(GameBinding.RtsEncounterLab, typing)) ToggleEncounterLab();
        if (BindingPressedEdge(GameBinding.RtsUndoWaypoint, typing) && _encounterLabOpen)
            UndoEncounterWaypoint();
    }

    private void ToggleEncounterLab()
    {
        _encounterLabOpen = !_encounterLabOpen;
        if (!_encounterLabOpen) { _encounterPlacing = EncounterPlacement.None; return; }

        // The docked workspace shows the Lab through its rail + deck; opening
        // (Ctrl+E or the rail button) lands directly in the encounter view.
        if (CreatorWorkspaceActive) _workspaceView = WorkspaceView.Encounter;

        EncounterLibraryRef.Reload();
        if (EncounterData.Data is null && !EncounterData.Fetching)
            EncounterData.BeginFetch(Settings.DevWindow.SuiBaseUrl);
    }

    /// <summary>
    /// World-click intercept, called from the click drain ahead of the free-view
    /// router. An armed placement mode owns the click completely — placing a probe
    /// must never also issue an RTS order. No-op when nothing is armed.
    /// </summary>
    // ── boss carry & spin ────────────────────────────────────────────────────
    // "Move boss": she FOLLOWS THE CURSOR over the ground; a click drops her
    // there and flows straight into the radial facing spinner (mouse spins her
    // in place, any click sets it). The sim rebuilds live (throttled) during
    // the whole gesture, so visualized cones and footprints swing with her -
    // the point is seeing where attacks land BEFORE authoring waypoints.
    // Right-click cancels and puts her back exactly where the gesture began;
    // "Reset boss" returns her to the document's spawn.

    private enum BossMoveStage { None, Follow, Spin }

    private BossMoveStage _encounterBossMove;
    private Vector3 _encounterBossMoveOrigin;
    private float _encounterBossMoveOriginFacing;
    private double _encounterBossMoveLastRebuild;

    private void BeginEncounterBossMove()
    {
        if (_encounterScenario.FirstOrDefault(a => a.Role == EncounterActorRole.Boss)
            is not { } boss) return;
        _encounterPlacing = EncounterPlacement.None;
        _encounterBossMove = BossMoveStage.Follow;
        _encounterBossMoveOrigin = boss.Position;
        _encounterBossMoveOriginFacing = boss.Facing;
        AddChatMessage("Boss follows the cursor. Click the ground to drop her, spin the " +
                       "facing, click to set. Right-click cancels.");
    }

    /// <summary>Per-frame while the gesture is live; called from UpdateEncounterLab.</summary>
    private void UpdateEncounterBossMove()
    {
        if (_encounterBossMove == BossMoveStage.None) return;
        if (!_encounterLabOpen) { _encounterBossMove = BossMoveStage.None; return; }
        if (ImGui.GetIO().WantCaptureMouse) return;   // pointer over a panel: hold
        if (!TryPickGround(_window.MousePosition, out Vector3 ground)) return;

        int at = _encounterScenario.FindIndex(a => a.Key == BossActorKey());
        if (at < 0) { _encounterBossMove = BossMoveStage.None; return; }
        EncounterActorSpec boss = _encounterScenario[at];

        if (_encounterBossMove == BossMoveStage.Follow)
        {
            _encounterScenario[at] = boss with { Position = ground };
        }
        else
        {
            float facing = EncounterGeometryLaw.Facing(boss.Position, ground);
            if (float.IsNaN(facing)) return;
            _encounterScenario[at] = boss with { Facing = facing };
        }

        // Throttled LIVE rebuild - interactive re-simulation is the Lab's
        // designed-for case ("the fight re-runs in a frame"), so the overlays
        // track her through the whole drag without waiting for the drop.
        double now = NowSeconds();
        if (now - _encounterBossMoveLastRebuild >= 0.12)
        {
            _encounterBossMoveLastRebuild = now;
            RebuildEncounterSimKeepingView();
        }
    }

    /// <summary>Click owner while the gesture is live: left advances the stage
    /// (drop → spin → set), right cancels and restores the starting pose.</summary>
    private bool HandleEncounterBossMoveClick(WorldMouseClick click)
    {
        if (_encounterBossMove == BossMoveStage.None) return false;
        int at = _encounterScenario.FindIndex(a => a.Key == BossActorKey());
        if (at < 0) { _encounterBossMove = BossMoveStage.None; return true; }

        if (click.Button != MouseButton.Left)
        {
            _encounterScenario[at] = _encounterScenario[at] with
            { Position = _encounterBossMoveOrigin, Facing = _encounterBossMoveOriginFacing };
            _encounterBossMove = BossMoveStage.None;
            RebuildEncounterSimKeepingView();
            AddChatMessage("Boss move cancelled - she is back where she was.");
            return true;
        }

        if (_encounterBossMove == BossMoveStage.Follow)
        {
            if (TryPickGround(click.Position, out Vector3 point))
                _encounterScenario[at] = _encounterScenario[at] with { Position = point };
            _encounterBossMove = BossMoveStage.Spin;
            RebuildEncounterSimKeepingView();
            return true;
        }

        _encounterBossMove = BossMoveStage.None;
        RebuildEncounterSimKeepingView();
        AddChatMessage($"Boss placed, facing {EncounterFacingLabel(_encounterScenario[at].Facing)}. " +
                       "'Reset boss' returns her to spawn.");
        return true;
    }

    /// <summary>Back to the document's spawn - position AND facing.</summary>
    private void ResetEncounterBoss()
    {
        _encounterBossMove = BossMoveStage.None;
        if (_encounterDefinition?.Actors?.FirstOrDefault(a =>
                a.Role == EncounterActorRole.Boss) is not { } home) return;
        int at = _encounterScenario.FindIndex(a => a.Key == BossActorKey());
        if (at < 0) return;
        _encounterScenario[at] = _encounterScenario[at] with
        { Position = home.Position, Facing = home.Facing };
        RebuildEncounterSimKeepingView();
        AddChatMessage("Boss reset to her document spawn.");
    }

    private bool HandleEncounterLabClick(WorldMouseClick click)
    {
        if (!_encounterLabOpen) return false;
        if (HandleEncounterBossMoveClick(click)) return true;
        if (_encounterPlacing == EncounterPlacement.None) return false;
        if (click.Button != MouseButton.Left)
        {
            _encounterPlacing = EncounterPlacement.None;   // right-click cancels
            return true;
        }
        if (!TryPickGround(click.Position, out Vector3 point)) return true;

        switch (_encounterPlacing)
        {
            case EncounterPlacement.Probe:
                _encounterProbe.Clear();
                _encounterProbe.Add(0, point);
                _encounterProbeDirty = true;
                break;

            case EncounterPlacement.ProbeWaypoint:
                // A trajectory, not a point: the probe is tested where the body will BE
                // when each effect lands, so walking out of a lane is expressible.
                _encounterProbe.Add(_encounterProbe.Count == 0 ? 0 : _encounterViewMs, point);
                _encounterProbeDirty = true;
                break;

            case EncounterPlacement.Actor when _encounterPlacingActorKey is { } key:
                MoveScenarioActor(key, point);
                break;

            case EncounterPlacement.Boss:
                MoveScenarioActor(BossActorKey(), point);
                break;

            case EncounterPlacement.ActorMove when _encounterPlacingActorKey is { } moveKey:
                AddScenarioMove(moveKey, _encounterViewMs, point);
                break;

            case EncounterPlacement.PlaybookSpot:
                UpsertPlaybookDirective(_encounterPlacingPlaybookPhase,
                    _encounterPlacingPlaybookJob, RaidDirectiveKind.MoveToSpot, point);
                break;

            case EncounterPlacement.PositioningSpot
                when _encounterPlacingPositioningPhase.Length > 0:
                SetPositioningPhaseSpot(_encounterPlacingPositioningPhase, point);
                break;
        }

        _encounterPlacing = EncounterPlacement.None;
        return true;
    }

    // ── per-frame playback ───────────────────────────────────────────────────

    /// <summary>
    /// Drive the simulation from wall clock. This is the ONLY place real time
    /// touches the simulator: it decides how many fixed steps to take, never how
    /// big they are. That separation is what keeps a run reproducible regardless of
    /// frame rate — a scrub at 200 fps and a scrub at 20 fps give the same fight.
    /// </summary>
    private void UpdateEncounterLab(float dt)
    {
        // Puppets track the sim every frame, playing or not - a scrub moves them
        // too - and self-clear the moment the window closes or models turn off.
        SyncEncounterPuppets(dt);

        // A live waypoint-orientation spin tracks the cursor every frame.
        UpdateEncounterOrientSpin();

        // A live orbit-drag sweeps a body around the boss on its ring every frame.
        UpdateEncounterOrbitDrag();

        // A live boss carry/spin gesture tracks the cursor every frame.
        UpdateEncounterBossMove();

        if (!_encounterLabOpen || _encounterSim is not { } sim) return;
        if (!_encounterPlaying || _encounterScrubbing) return;

        // The clock does not run before the pull. With no body in her ring the whole
        // timeline is dead pre-pull time, so playback HOLDS at the setup - the timer
        // stays at zero until a pull exists. Order a body into her ring (a staged plan
        // that walks in, or a move/teleport across the ring) and the fight begins; the
        // walk-in of a SCHEDULED pull still plays, and the clock starts at the pull.
        if (sim.EngagedAtMs < 0) return;

        float speed = Math.Clamp(Settings.EncounterLab.PlaybackSpeed, 0.05f, 20f);
        _encounterPlaybackCarryMs += dt * 1000.0 * speed;
        int step = Math.Max(sim.Options.StepMs, 1);
        int guard = 0;

        // The fight is fully simulated at load, so playback is a cursor moving
        // over the snapshot ring - the same restore a scrub does, at speed.
        int endMs = sim.Timeline.Count > 0 ? sim.Timeline[^1].TimeMs : 0;
        int viewBefore = _encounterViewMs;
        while (_encounterPlaybackCarryMs >= step && guard++ < 400)
        {
            _encounterPlaybackCarryMs -= step;
            if (_encounterViewMs >= endMs) { _encounterPlaying = false; break; }
            ScrubTo(_encounterViewMs + step);
        }

        // Drive attack animations across the span just played.
        UpdateEncounterCombatAnimations(sim, viewBefore, _encounterViewMs);
    }

    private readonly Dictionary<string, int> _encounterSwingAccumMs = new(StringComparer.Ordinal);

    // ── rotation cosmetic casts ──────────────────────────────────────────────
    // Friendly bodies with an authored CombatPlan.Rotation play their spells as
    // COSMETIC casts: the schedule is a pure function of (rotation, Spell.dbc
    // cadence, the sim's pull time), so scrubbing and replays land on identical
    // cast instants. Nothing here touches the sim - damage stays the DPS dial.

    private sealed record EncounterRotationCast(int TimeMs, uint SpellId, bool Healing);
    private readonly Dictionary<string, List<EncounterRotationCast>> _encounterRotationSchedules =
        new(StringComparer.Ordinal);
    private EncounterSim? _encounterRotationScheduleSim;

    private void EnsureEncounterRotationSchedules(EncounterSim sim)
    {
        if (ReferenceEquals(_encounterRotationScheduleSim, sim)) return;
        _encounterRotationScheduleSim = sim;
        _encounterRotationSchedules.Clear();
        if (_spellCatalog is null) return;

        int pullMs = sim.Events.FirstOrDefault(e => e.Kind == SimEventKind.Aggro)?.TimeMs ?? 0;
        int endMs = sim.Events.Count > 0 ? sim.Events[^1].TimeMs : 0;
        foreach (EncounterActorSpec actor in _encounterScenario)
        {
            if (actor.Role != EncounterActorRole.Friendly) continue;
            if (actor.PlayerRules?.Plan?.Rotation is not { Count: > 0 } rotation) continue;
            var priority = new List<SpellInfo>();
            foreach (CombatAbilityIntent intent in rotation)
                if (intent.Enabled && intent.SpellId != 0 &&
                    _spellCatalog.TryGet(intent.SpellId, out SpellInfo info))
                    priority.Add(info);
            if (priority.Count == 0) continue;
            int deathMs = sim.Events.FirstOrDefault(e =>
                e.Kind == SimEventKind.Death && e.ActorKey == actor.Key)?.TimeMs ?? int.MaxValue;
            // The same phase-offset trick as the melee swings, so ten same-class
            // bodies do not cast in lockstep.
            int start = pullMs + (actor.Key.GetHashCode() & 0x7fffffff) % 900;
            _encounterRotationSchedules[actor.Key] =
                BuildEncounterRotationSchedule(priority, start, Math.Min(endMs, deathMs));
        }
    }

    /// <summary>Greedy priority walk: at each free instant cast the first ready
    /// entry, occupy max(GCD, cast time), and put the entry on its real cooldown.</summary>
    private static List<EncounterRotationCast> BuildEncounterRotationSchedule(
        List<SpellInfo> priority, int startMs, int endMs)
    {
        const int gcdMs = 1500;
        var result = new List<EncounterRotationCast>();
        var readyAt = new int[priority.Count];
        Array.Fill(readyAt, startMs);
        int now = startMs;
        while (now < endMs && result.Count < 4000)
        {
            int pick = -1;
            for (int i = 0; i < priority.Count; i++)
                if (readyAt[i] <= now) { pick = i; break; }
            if (pick < 0)
            {
                int next = readyAt.Min();
                if (next <= now) break;
                now = next;
                continue;
            }
            SpellInfo spell = priority[pick];
            result.Add(new EncounterRotationCast(now, spell.Id,
                spell.EffectIds?.Contains(10u) == true || spell.AuraIds?.Contains(8u) == true));
            int busy = Math.Max(gcdMs, Math.Max(0, spell.CastTimeMs));
            int cooldown = (int)Math.Max(spell.RecoveryMs, spell.CategoryRecoveryMs);
            readyAt[pick] = now + Math.Max(cooldown, busy);
            now += busy;
        }
        return result;
    }

    /// <summary>Emit the rotation casts that crossed (fromMs, toMs] on live bodies.
    /// A scrub jump replays only the trailing 3 s so a 60 s hop cannot burst
    /// hundreds of ApplySpellGo calls into one frame.</summary>
    private void PlayEncounterRotationCasts(EncounterSim sim, int fromMs, int toMs)
    {
        EnsureEncounterRotationSchedules(sim);
        if (_encounterRotationSchedules.Count == 0) return;
        int emitFrom = Math.Max(fromMs, toMs - 3000);
        foreach ((string key, List<EncounterRotationCast> casts) in _encounterRotationSchedules)
        {
            SimActor? actor = sim.Actors.FirstOrDefault(a => a.Key == key);
            if (actor is not { Alive: true }) continue;
            foreach (EncounterRotationCast cast in casts)
            {
                if (cast.TimeMs <= emitFrom) continue;
                if (cast.TimeMs > toMs) break;
                string? targetKey = cast.Healing
                    ? actor.CurrentProtectTargetKey ?? key
                    : actor.CurrentEnemyTargetKey ?? sim.Boss?.Key;
                PlayEncounterSpellCast(new SimEvent(cast.TimeMs, SimEventKind.CastStart, key,
                    "rotation", EncounterFidelity.Heuristic, null, cast.SpellId,
                    null, targetKey));
            }
        }
    }

    /// <summary>Make the models actually FIGHT the real fight: the boss plays each ability's
    /// TRUE spell visual (cast animation, attached kit FX, a flying fireball missile, the
    /// impact on whoever it lands on), bodies take the real impact reaction, and in-reach
    /// tank/melee bodies swing on a ~2 s cadence (no sim event marks a melee autoattack, so
    /// those are synthesized). The spell visuals run through the same ApplySpellGo pipeline
    /// live combat uses - Onyxia's abilities carry real DBC spell ids - so the Lab renders the
    /// encounter's art, not placeholders. All of it is keyed off puppet guids; the sim never
    /// sees any of it.</summary>
    private void UpdateEncounterCombatAnimations(EncounterSim sim, int fromMs, int toMs)
    {
        if (_creatures is null || toMs <= fromMs) return;

        // Boss ability casts and their impacts fire on the events that just crossed.
        string? bossKey = sim.Boss?.Key;
        foreach (SimEvent e in sim.Events)
        {
            if (e.TimeMs <= fromMs || e.TimeMs > toMs) continue;
            if (e.Kind == SimEventKind.CastStart && e.ActorKey == bossKey)
                PlayEncounterSpellCast(e);
            else if (e.Kind == SimEventKind.ActorHit)
                PlayEncounterSpellImpact(e);
        }

        // Friendly rotation casts, on the same crossing rule as the boss's.
        PlayEncounterRotationCasts(sim, fromMs, toMs);

        // Synthesized melee swings: a body in reach of a grounded boss swings ~every 2 s,
        // phase-offset per key so the raid does not swing in lockstep.
        if (sim.Boss is { } boss && !boss.Flying)
        {
            int delta = toMs - fromMs;
            float reach = MathF.Max(boss.Spec.CombatReach, boss.Spec.BoundingRadius) + 5f;
            foreach (SimActor actor in sim.Actors)
            {
                if (!actor.Alive || actor.Spec.Job is not (RaidJob.Tank or RaidJob.Melee)) continue;
                if (EncounterGeometryLaw.GroundDistance(actor.Position, boss.Position) > reach) continue;
                if (!_encounterPuppets.TryGetValue(actor.Key, out ulong guid)) continue;
                int acc = _encounterSwingAccumMs.GetValueOrDefault(actor.Key,
                    (actor.Key.GetHashCode() & 0x7fffffff) % 2000) + delta;
                if (acc >= 2000) { acc -= 2000; _creatures.TriggerCombatSwing(guid, offHand: false); }
                _encounterSwingAccumMs[actor.Key] = acc;
            }
        }
    }

    /// <summary>A boss ability's real cast visual on its puppet: cast animation + attached kit
    /// FX (her breath, her roar), and for a projectile spell a live missile flying to the
    /// target puppet whose arrival plays the impact. Cones/instants (Speed 0) play the caster
    /// visual only; their per-body impacts arrive as ActorHit events. This is the exact
    /// ApplySpellGo path live combat uses, driven on synthetic guids.</summary>
    private void PlayEncounterSpellCast(SimEvent e)
    {
        if (e.SpellId == 0 || _spellCatalog is null || _spellVisualCatalog is null ||
            _spellEffects is null) return;
        if (!_encounterPuppets.TryGetValue(e.ActorKey, out ulong casterGuid)) return;

        bool projectile = _spellCatalog.TryGet(e.SpellId, out SpellInfo info) && info.Speed > 0f;
        ulong[] hits = [];
        SpellTargets targets = new(0, casterGuid, null, null, null, null, null);
        if (projectile && e.TargetKey is { } targetKey &&
            _encounterPuppets.TryGetValue(targetKey, out ulong targetGuid) && targetGuid != casterGuid)
        {
            hits = [targetGuid];
            targets = new SpellTargets(0x0002, targetGuid, null, null, null, null, null);
        }
        ApplySpellGo(new SpellGoPacket(0, casterGuid, e.SpellId, 0, hits, [], targets, null, null));
    }

    /// <summary>A body's impact visual when a cone/instant lands on it - the real impact kit +
    /// authored wound reaction. Projectiles are skipped here (their impact is played by the
    /// missile arrival in the cast), or this would double it.</summary>
    private void PlayEncounterSpellImpact(SimEvent e)
    {
        if (e.SpellId == 0 || _spellEffects is null) return;
        if (!_encounterPuppets.TryGetValue(e.ActorKey, out ulong victimGuid)) return;
        if (_spellCatalog?.TryGet(e.SpellId, out SpellInfo info) == true && info.Speed > 0f) return;
        ApplySpellImpact(victimGuid, e.SpellId);
    }

    // ── simulation lifecycle ─────────────────────────────────────────────────

    private void EnsureEncounterFacts()
    {
        // Rebuilt whenever the DB snapshot changes: cone arcs and DB landing
        // positions only exist server-side, so a facts object built before the
        // fetch landed would silently draw discs where lanes belong.
        if (_encounterFacts is null || !ReferenceEquals(_encounterFacts.Data, EncounterData.Data))
            _encounterFacts = new EncounterSpellFacts(_spellCatalog, EncounterData.Data);
    }

    /// <summary>Resolve an encounter for a creature entry: an authored document wins,
    /// otherwise one is derived from the world DB on the spot.</summary>
    private void LoadEncounterForEntry(uint entry, string fallbackName, uint maxHealth = 100000)
    {
        EnsureEncounterFacts();
        EncounterDefinition? authored = EncounterLibraryRef.ForEntry(entry);
        if (authored is not null)
        {
            _encounterDefinition = authored;
            _encounterSourceNote = $"authored document ({authored.Key}.json)";
        }
        else
        {
            // The binding decides which of the three behaviour tiers this creature is
            // in. Passing it is what makes a compiled-C++ creature declare its hole
            // instead of looking like a mob with no abilities.
            CreatureBehaviourBinding? binding = EncounterData.Data?.Binding(entry);
            _encounterDefinition = EncounterTranslator.FromDatabase(
                entry,
                binding?.Name is { Length: > 0 } name ? name : fallbackName,
                spellListId: binding?.SpellListId ?? 0,
                scriptName: binding?.ScriptName,
                aiName: binding?.AiName,
                EncounterData.Data,
                _encounterFacts,
                maxHealth);
            _encounterSourceNote = binding is null
                ? "derived from world DB (no template binding — fetch may still be running)"
                : $"derived from world DB · {DescribeTier(binding)}";
        }
        BuildScenarioFromDefinition();
        RebuildEncounterSim();
        _encounterPlaying = true;
    }

    private void LoadEncounterDocument(EncounterDefinition definition)
    {
        EnsureEncounterFacts();
        _encounterDefinition = definition;
        _encounterSourceNote = $"authored document ({definition.Key}.json)";
        BuildScenarioFromDefinition();
        RebuildEncounterSim();
        // ALIVE from the first frame. The sim rewinds to t=0 after the
        // pre-simulation, and a view parked at time zero is a world where
        // nothing ever moves — the owner watched a "roaming" boss stand
        // statue-still for exactly this reason. Loading a fight now presses
        // Play; pause and the scrub bar are still right there.
        _encounterPlaying = true;
    }

    private void BuildScenarioFromDefinition()
    {
        InvalidateEncounterPlayerPlanDraft();
        _encounterScenario.Clear();
        ClearEncounterStagedOrders();
        if (_encounterDefinition?.Actors is { Count: > 0 } actors)
            // The boss (and any authored adds) — NEVER the document's friendly
            // fixture bodies. Those stood inside her ring, and the moment the
            // Lab went live-on-load they pulled her into a fight nobody asked
            // for the instant the document loaded. The only raid that exists is
            // the one the owner places.
            _encounterScenario.AddRange(actors.Where(a =>
                a.Role != EncounterActorRole.Friendly));
        else if (_encounterDefinition is { } definition)
            _encounterScenario.Add(new EncounterActorSpec(
                "boss", definition.Name, definition.PrimaryEntry, EncounterActorRole.Boss));

        if (_encounterProbe.Count == 0 && _encounterScenario.Count > 0)
            _encounterProbe.Add(0, _encounterScenario[0].Position + new Vector3(10f, 0f, 0f));

        SeedDefaultPlaybook();
    }

    /// <summary>The vanilla instinct as a starting table: melee keep reach while
    /// she is grounded, everyone else holds the owner's orders. Reseeded whenever
    /// a definition loads; the owner edits from there.
    ///
    /// With the raid doctrine on, the seed is deliberately NOT planted: the derived
    /// formation already keeps melee at reach — arc-aware and flank-split, which the
    /// blunt chase directive is not — and a playbook row would outrank it. Anything
    /// the owner authors here still wins over the formation, doctrine or no.</summary>
    private void SeedDefaultPlaybook()
    {
        _encounterPlaybook.Clear();
        if (Settings.EncounterLab.RaidDoctrine) return;
        if (_encounterDefinition is not { } definition) return;
        foreach (EncounterPhase phase in definition.Phases)
            if (!phase.CasterFlying)
                _encounterPlaybook.Add(new RaidPhaseDirective(
                    phase.Key, RaidJob.Melee, RaidDirectiveKind.ChaseBoss));
    }

    private RaidPhaseDirective? PlaybookDirectiveFor(string phaseKey, RaidJob job)
    {
        foreach (RaidPhaseDirective directive in _encounterPlaybook)
            if (directive.Job == job && directive.PhaseKey == phaseKey) return directive;
        return null;
    }

    private void UpsertPlaybookDirective(string phaseKey, RaidJob job,
        RaidDirectiveKind kind, Vector3 spot = default)
    {
        _encounterPlaybook.RemoveAll(d => d.Job == job && d.PhaseKey == phaseKey);
        _encounterPlaybook.Add(new RaidPhaseDirective(phaseKey, job, kind, spot));
        RebuildEncounterSimKeepingView();
    }

    private void RemovePlaybookDirective(string phaseKey, RaidJob job)
    {
        if (_encounterPlaybook.RemoveAll(d => d.Job == job && d.PhaseKey == phaseKey) > 0)
            RebuildEncounterSimKeepingView();
    }

    /// <summary>The pre-simulated fight's verdict, shown in the transport and the
    /// status line. Written once per rebuild; playback never changes it.</summary>
    private string _encounterOutcome = "";

    private void RebuildEncounterSim()
    {
        // Selective, not wholesale: a puppet's fields are set once at spawn, so
        // only bodies whose DISPLAY actually changed under their key need a
        // respawn. Clearing every puppet here made all the models blink per
        // rebuild — invisible for one-click edits, constant flicker once the
        // boss-move gesture started rebuilding the sim live during a drag.
        PurgeStaleEncounterPuppets();
        if (_encounterDefinition is not { } definition) { _encounterSim = null; return; }
        EnsureEncounterFacts();
        var settings = Settings.EncounterLab;
        _encounterSim = new EncounterSim(definition, _encounterScenario, new EncounterSimOptions
        {
            StepMs = Math.Clamp(settings.StepMs, 20, 1000),
            Seed = (uint)Math.Max(settings.Seed, 0),
            RaidDpsFraction = Math.Clamp(settings.RaidDpsFraction, 0f, 0.5f),
            AggroPlan = _encounterAggroPlan.Count > 0 ? _encounterAggroPlan.ToArray() : null,
            PullRangeYards = Math.Clamp(settings.PullRangeYards, 5f, 100f),
            // She STANDS at spawn until pulled - the exact-db truth (movement_type 0),
            // and what the owner wants to see: no wandering before the engage. The invented
            // sandbox roam is now an explicit opt-in (default off). A document that DECLARES
            // Wander/Waypoints still moves pre-pull with its own radius/route regardless.
            InventPrePullRoam = settings.SandboxRoam,
            RoamRadiusYards = Math.Clamp(settings.RoamRadiusYards, 5f, 60f),
            MeleeDpsNeedsReach = settings.MeleeDpsNeedsReach,
            AddDps = Math.Clamp(settings.AddDps, 0f, 1000f),
            HealerHps = Math.Clamp(settings.HealerHps, 0f, 5000f),
            Doctrine = settings.RaidDoctrine ? _encounterDoctrine : null,
            Playbook = _encounterPlaybook.Count > 0 ? _encounterPlaybook.ToArray() : null,
            Positioning = BuildEncounterPositioningMap(),
        }, _encounterFacts);

        // Run the WHOLE fight now, then rewind the view to the start. Before this,
        // the sim was advanced live by the Play button, so a freshly loaded
        // encounter showed "0.0s / 0.0s" - a scrub bar over nothing, which read as
        // broken rather than as "not yet played". A fight is a couple of thousand
        // snapshot steps; simulating it at load costs a frame and makes the bar,
        // the timeline and the probe describe the finished fight immediately.
        _encounterSim.AdvanceTo(_encounterSim.Options.MaxDurationMs);
        _encounterOutcome = _encounterSim.EngagedAtMs < 0
            ? "never pulled - order a body into her aggro ring to start the fight"
            : _encounterSim.Boss is { Alive: false } dead
                ? $"pulled at {_encounterSim.EngagedAtMs / 1000f:0.0}s, {dead.Spec.Name} dies at " +
                  $"{_encounterSim.TimeMs / 1000f / 60f:0}:{_encounterSim.TimeMs / 1000 % 60:00}"
                : _encounterSim.Boss is { } alive
                    ? $"boss at {alive.HealthFraction * 100f:0}% when the {_encounterSim.Options.MaxDurationMs / 60000} min cap hit - raise dps to reach later phases"
                    : "no boss in the scenario";
        _encounterSim.RestoreTo(0);
        _encounterViewMs = 0;
        _encounterPlaybackCarryMs = 0;
        _encounterProbeDirty = true;
    }

    /// <summary>Which behaviour tier a creature is in, in one phrase. This is the
    /// first thing worth knowing about any NPC you click.</summary>
    private static string DescribeTier(CreatureBehaviourBinding binding)
    {
        List<string> tiers = [];
        if (binding.SpellListId != 0) tiers.Add($"creature_spells {binding.SpellListId}");
        if (binding.AiName is { Length: > 0 }) tiers.Add(binding.AiName);
        if (binding.ScriptName is { Length: > 0 }) tiers.Add($"C++ '{binding.ScriptName}'");
        return tiers.Count == 0 ? "plain melee (no scripted behaviour)" : string.Join(" + ", tiers);
    }

    private string BossActorKey() =>
        _encounterScenario.FirstOrDefault(a => a.Role == EncounterActorRole.Boss)?.Key
        ?? _encounterScenario.FirstOrDefault()?.Key ?? "boss";

    /// <summary>Ground height for a placed body: the COLLISION world's floor
    /// first — Onyxia's Lair is WMO geometry with no meaningful ADT under it,
    /// and sampling bare terrain sank half a raid through the instance floor.
    /// Terrain is the outdoor fallback (sanity-banded against the anchor), the
    /// anchor's own height the last resort.</summary>
    private float EncounterGroundZ(float x, float y, float nearZ)
    {
        if (_collision?.Raycast(new Vector3(x, y, nearZ + 4f),
                new Vector3(0f, 0f, -1f), 44f) is { } hit)
            return hit.Point.Z;
        if (_terrain?.SampleHeight(x, y) is float ground && MathF.Abs(ground - nearZ) < 30f)
            return ground;
        return nearZ;
    }

    /// <summary>
    /// Ten bodies in the classic arrangement, relative to where the boss stands and
    /// faces: a tank on the nose, melee on the flanks (out of the frontal breath and
    /// the rear tail cone both), ranged in a wide arc further back. Replaces any
    /// friendly bodies already placed - it is a preset, not an append - and each
    /// body then really participates: it steers the sim's targeting and is
    /// hit-tested against every footprint that lands.
    /// </summary>
    private void PlaceScenarioRaid()
    {
        if (_encounterScenario.FirstOrDefault(a => a.Role == EncounterActorRole.Boss)
            is not { } boss) return;
        // Preset keys are stable character identities across encounters. Throw away
        // any modal cache before replacing the roster so a reused key can never make
        // an old draft look as though it belongs to the new actor instance.
        InvalidateEncounterPlayerPlanDraft();
        _encounterScenario.RemoveAll(a => a.Role == EncounterActorRole.Friendly);
        ClearEncounterStagedOrders();

        // The raid forms up AT THE PLAYER - stand at the top of the cave, click
        // once, and the raid is authored there, outside her ring, facing her.
        // The fight then starts the way a fight starts: you order someone in.
        Vector3 centre = DevPlayerPosition() ?? boss.Position + new Vector3(40f, 0f, 0f);
        Vector3 toBoss = boss.Position - centre;
        float advance = MathF.Atan2(toBoss.Y, toBoss.X);

        void Add(string key, string name, RaidJob job, float aheadYards, float sideYards,
            uint health, uint display, float dps)
        {
            Vector3 forward = new(MathF.Cos(advance), MathF.Sin(advance), 0f);
            Vector3 side = new(-forward.Y, forward.X, 0f);
            Vector3 at = centre + forward * aheadYards + side * sideYards;
            at.Z = EncounterGroundZ(at.X, at.Y, centre.Z);
            CombatPlan? savedPlan = EncounterCombatPlanStoreRef.Find(key);
            EncounterPlayerRules? savedRules = savedPlan is null
                ? null
                : new EncounterPlayerRules(
                    AlwaysFaceBoss: savedPlan.Movement?.FacePrimaryEnemy == true,
                    Plan: savedPlan);
            _encounterScenario.Add(new EncounterActorSpec(
                key, name, 0, EncounterActorRole.Friendly, at,
                Facing: advance, MaxHealth: health, DisplayId: display, Dps: dps,
                Job: job, PlayerRules: savedRules));
        }

        // The 2 tank / 2 healer / 6 dps ten-man, in marching order: tanks on the
        // nose, melee behind them, ranged in an arc, healers at the rear. CHARACTER
        // models with displays read from creature_template on the vmangos box
        // (2026-08-17): tanks are guards (plate + shield), melee armoured soldiers,
        // ranged robed casters, healers a priestess and an acolyte. Weapons come
        // from each look's real creature_equip_template via the puppet layer.
        Add("raid-tank1", "tank 1", RaidJob.Tank, 6f, -2f, 8000, 3167, 250);
        Add("raid-tank2", "tank 2", RaidJob.Tank, 6f, 2f, 8000, 3258, 250);
        Add("raid-melee1", "melee 1", RaidJob.Melee, 3f, -3f, 5000, 3280, 800);
        Add("raid-melee2", "melee 2", RaidJob.Melee, 3f, 3f, 5000, 1515, 800);
        Add("raid-melee3", "melee 3", RaidJob.Melee, 1f, 0f, 5000, 1985, 800);
        Add("raid-ranged1", "ranged 1", RaidJob.Ranged, -4f, -5f, 4000, 2968, 700);
        Add("raid-ranged2", "ranged 2", RaidJob.Ranged, -4f, 5f, 4000, 3287, 700);
        Add("raid-ranged3", "ranged 3", RaidJob.Ranged, -5f, 0f, 4000, 2968, 700);
        Add("raid-healer1", "healer 1", RaidJob.Healer, -8f, -3f, 4000, 1495, 0);
        Add("raid-healer2", "healer 2", RaidJob.Healer, -8f, 3f, 4000, 3344, 0);

        // Tank 1 opens with aggro - the one default nobody would choose
        // otherwise. Every later swap is the owner's.
        _encounterAggroPlan.Clear();
        _encounterAggroPlan.Add(new TimedAggro(0, "raid-tank1"));
        RebuildEncounterSim();
        _encounterPlaying = true;   // a freshly staged raid is a live scene, not a paused one
    }

    /// <summary>
    /// Re-simulate and stay at the instant the owner is looking at. This is what
    /// makes paused repositioning read as REALTIME: place the aggro holder
    /// somewhere new while paused, the fight re-runs in a frame, the view lands
    /// back on the same millisecond - and the boss now faces the new spot, every
    /// cone on the floor swung with her.
    /// </summary>
    private void RebuildEncounterSimKeepingView()
    {
        int viewMs = _encounterViewMs;
        RebuildEncounterSim();
        if (viewMs > 0) ScrubTo(viewMs);
    }

    private void MoveScenarioActor(string key, Vector3 position)
    {
        for (int i = 0; i < _encounterScenario.Count; i++)
        {
            if (_encounterScenario[i].Key != key) continue;
            _encounterScenario[i] = _encounterScenario[i] with { Position = position };
            RebuildEncounterSimKeepingView();
            return;
        }
    }

    /// <summary>The assigned aggro holder at a moment, for display and overlays.</summary>
    private string? AggroHolderKeyAt(int timeMs)
    {
        string? holder = null;
        foreach (TimedAggro entry in _encounterAggroPlan)
            if (entry.TimeMs <= timeMs) holder = entry.Key;
        return holder;
    }

    private void AssignAggro(string key, int timeMs)
    {
        _encounterAggroPlan.RemoveAll(a => a.TimeMs == timeMs);
        _encounterAggroPlan.Add(new TimedAggro(Math.Max(timeMs, 0), key));
        _encounterAggroPlan.Sort((a, b) => a.TimeMs.CompareTo(b.TimeMs));
        RebuildEncounterSimKeepingView();
    }

    /// <summary>Record "at this time, run there" for a body and replay the fight
    /// with it. The scrub head IS the time argument: scrub to the breath, click
    /// where they should be, and the rebuilt fight shows whether they make it.
    /// Anchors ride along: chain legs, phase-entry orders, teleport what-ifs and
    /// arrival facings are all the same list entry with different flags.</summary>
    private void AddScenarioMove(string key, int timeMs, Vector3 position,
        MoveAnchor anchor = MoveAnchor.AtTime, string? phaseKey = null,
        float arrivalFacing = float.NaN, bool teleport = false)
    {
        if (AppendScenarioMove(key, timeMs, position, anchor, phaseKey, arrivalFacing, teleport))
            RebuildEncounterSimKeepingView();
    }

    /// <summary>The append without the rebuild, for callers issuing one order to a
    /// whole selection — rebuild once at the end, not once per body. The list keeps
    /// AUTHORED order (not time order): chains and phase orders have no meaningful
    /// time to sort by, and dispatch reads each entry on its own trigger.</summary>
    private bool AppendScenarioMove(string key, int timeMs, Vector3 position,
        MoveAnchor anchor = MoveAnchor.AtTime, string? phaseKey = null,
        float arrivalFacing = float.NaN, bool teleport = false)
    {
        for (int i = 0; i < _encounterScenario.Count; i++)
        {
            if (_encounterScenario[i].Key != key) continue;
            var moves = (_encounterScenario[i].Moves ?? []).ToList();
            moves.Add(new TimedMove(Math.Max(timeMs, 0), position, anchor, phaseKey,
                arrivalFacing, teleport));
            _encounterScenario[i] = _encounterScenario[i] with { Moves = moves };
            return true;
        }
        return false;
    }

    /// <summary>Give the LAST order of a body an arrival facing — the Alt-click
    /// "and then face this way" verb. With no orders yet, the body's spawn facing
    /// turns instead, so pre-pull staging can be oriented too.</summary>
    private void FaceScenarioActor(string key, float facing)
    {
        for (int i = 0; i < _encounterScenario.Count; i++)
        {
            if (_encounterScenario[i].Key != key) continue;
            EncounterActorSpec actor = _encounterScenario[i];
            if (actor.Moves is { Count: > 0 } moves)
            {
                var updated = moves.ToList();
                updated[^1] = updated[^1] with { ArrivalFacing = facing };
                _encounterScenario[i] = actor with { Moves = updated };
            }
            else
            {
                _encounterScenario[i] = actor with { Facing = facing };
            }
            return;
        }
    }

    private void RemoveScenarioMove(string key, int index)
    {
        for (int i = 0; i < _encounterScenario.Count; i++)
        {
            if (_encounterScenario[i].Key != key) continue;
            var moves = (_encounterScenario[i].Moves ?? []).ToList();
            if (index < 0 || index >= moves.Count) return;
            moves.RemoveAt(index);
            _encounterScenario[i] = _encounterScenario[i] with { Moves = moves.Count == 0 ? null : moves };
            RebuildEncounterSimKeepingView();
            return;
        }
    }

    private void RefreshProbeReport()
    {
        if (!_encounterProbeDirty || _encounterSim is not { } sim) return;
        _encounterProbeReport = EncounterProbeLaw.Scan(sim, _encounterProbe);
        _encounterProbeDirty = false;
    }

    // ── window ───────────────────────────────────────────────────────────────

    /// <summary>Encounter Lab actions all use the same Blizzard UIPanelButton art as the
    /// creator menus. The two size tiers keep transport/actions readable while compact row
    /// controls do not swell into dominant blocks.</summary>
    private float EncounterPanelButtonWidth(string label, float minWidth = 0f,
        bool compact = false)
    {
        float mul = CreatorButtonMul;
        float padding = compact ? 18f : 36f;
        int idStart = label.IndexOf("##", StringComparison.Ordinal);
        string caption = idStart >= 0 ? label[..idStart] : label;
        return MathF.Max(minWidth * mul,
            ImGui.CalcTextSize(caption).X + padding * CreatorUiScale * mul);
    }

    private float EncounterPanelButtonHeight(bool compact = false)
    {
        if (!compact) return CreatorButtonHeight;
        return (ImGui.GetTextLineHeight() + 4f * CreatorUiScale) * CreatorButtonMul;
    }

    private bool EncounterPanelButton(string label, float minWidth = 0f,
        bool enabled = true, bool compact = false)
    {
        Vector2 size = new(
            EncounterPanelButtonWidth(label, minWidth, compact),
            EncounterPanelButtonHeight(compact));
        return EncounterPanelButtonSized(label, size, enabled);
    }

    private bool EncounterPanelButtonSized(string label, Vector2 size, bool enabled = true)
    {
        NotePanelField("buttons");
        if (size.X <= 0f) size.X = ImGui.GetContentRegionAvail().X;
        if (size.Y <= 0f) size.Y = EncounterPanelButtonHeight();

        if (_skin is not null) return _skin.PanelButton(label, size, enabled);

        if (!enabled) ImGui.BeginDisabled();
        bool clicked = ImGui.Button(label, size);
        if (!enabled) ImGui.EndDisabled();
        return enabled && clicked;
    }

    /// <summary>Keep dense action rows on one line while they fit; once live text/widget/button
    /// tuning consumes the remaining width, let ImGui continue naturally on the next row.</summary>
    private void EncounterSameLineForButton(string label, float minWidth = 0f,
        bool compact = false)
    {
        EncounterSameLineIfFits(EncounterPanelButtonWidth(label, minWidth, compact));
    }

    /// <summary>Use the current content-region edge rather than the raw window edge, so a
    /// child scrollbar is never mistaken for usable row space.</summary>
    private void EncounterSameLineIfFits(float nextWidth)
    {
        float right = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
        float needed = ImGui.GetStyle().ItemSpacing.X + nextWidth + 4f * CreatorUiScale;
        if (right - ImGui.GetItemRectMax().X >= needed) ImGui.SameLine();
    }

    // ── collapse ─────────────────────────────────────────────────────────────
    // Commanding bodies and customizing characters both want the world, not the
    // 470x680 principal window. While puppets are selected or the customizer is
    // open the Lab auto-collapses into a small chip; the chip force-opens it,
    // and that choice is respected until the space pressure ends.

    private bool _encounterLabCollapsed;
    private bool _encounterLabAutoCollapsed;
    private bool _encounterLabUserForcedOpen;

    /// <summary>True while the owner is doing spatial work the Lab window would
    /// cover: puppets selected in the command view, or the customizer open.</summary>
    private bool EncounterLabWantsSpace() =>
        (_freeView && _freecamSelection.Count > 0 &&
         _freecamSelection.Any(guid => EncounterRaidPuppetKey(guid) is not null)) ||
        _encounterPlayerSetupKey is not null;

    private void UpdateEncounterLabCollapse()
    {
        bool wantsSpace = EncounterLabWantsSpace();
        if (wantsSpace && !_encounterLabCollapsed && !_encounterLabUserForcedOpen)
        {
            _encounterLabCollapsed = true;
            _encounterLabAutoCollapsed = true;
        }
        else if (!wantsSpace)
        {
            if (_encounterLabAutoCollapsed) _encounterLabCollapsed = false;
            _encounterLabAutoCollapsed = false;
            _encounterLabUserForcedOpen = false;
        }
    }

    /// <summary>The collapsed Lab: one compact button where the window's top-left
    /// corner normally sits. Clicking it restores the full window.</summary>
    private void DrawEncounterLabChip()
    {
        float cs = CreatorUiScale;
        float s = MathF.Max(ImGui.GetIO().DisplaySize.Y / GlueCanvasH, 0.5f) * cs;
        ImGui.SetNextWindowPos(new Vector2(24f * s, 64f * s), ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoFocusOnAppearing |
            ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.AlwaysAutoResize;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        if (ImGui.Begin("###encounter-lab-chip", flags))
        {
            const string caption = "Encounter Lab";
            float textScale = Math.Clamp(CreatorTextScale, 0.65f, 1.35f);
            ImGui.SetWindowFontScale(textScale);
            Vector2 measured = ImGui.CalcTextSize(caption) * textScale;
            Vector2 size = new(MathF.Max(150f * s, measured.X + 30f * s),
                MathF.Max(30f * s, measured.Y + 12f * s));
            bool clicked = _skin?.PanelButton(caption + "##encounter-lab-chip", size)
                           ?? ImGui.Button(caption, size);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(_encounterDefinition is { } definition
                    ? $"{definition.Name}: {_encounterOutcome}\nOpen the main encounter window."
                    : "Open the main encounter window.");
            if (clicked)
            {
                _encounterLabCollapsed = false;
                _encounterLabAutoCollapsed = false;
                if (EncounterLabWantsSpace()) _encounterLabUserForcedOpen = true;
            }
            ImGui.SetWindowFontScale(1f);
        }
        ImGui.End();
        ImGui.PopStyleVar();
    }

    private void DrawEncounterLab()
    {
        if (!_encounterLabOpen) return;
        // In the docked workspace the Lab lives in the rail + deck; the floating
        // window (and its collapse chip) belongs to the classic layout only.
        if (CreatorWorkspaceActive) return;

        UpdateEncounterLabCollapse();
        if (_encounterLabCollapsed)
        {
            DrawEncounterLabChip();
            return;
        }

        _activePanelTune = "encounter-lab";
        float cs = CreatorUiScale;
        float s = MathF.Max(ImGui.GetIO().DisplaySize.Y / GlueCanvasH, 0.5f) * cs;
        ImGui.SetNextWindowPos(new Vector2(24f * s, 64f * s), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(470f * s, 680f * s), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(300f * cs, 200f * cs),
            new Vector2(float.MaxValue, float.MaxValue));

        PushCreatorStyle();
        if (!ImGui.Begin("###encounter-lab", CreatorChromeFlags))
        {
            ImGui.End();
            PopCreatorStyle();
            _activePanelTune = null;
            return;
        }
        ClampCreatorWindowOnScreen();
        if (DrawCreatorPanelChrome("Encounter Lab", "encounter-lab"))
        {
            _encounterLabOpen = false;
            _encounterPlacing = EncounterPlacement.None;
        }
        ImGui.SetWindowFontScale(CreatorTextScale);

        RefreshProbeReport();
        DrawEncounterToolbar();
        BeginCreatorContent();
        DrawEncounterStatusLine();
        DrawEncounterSubjectSection();
        DrawEncounterOverlaySection();
        DrawEncounterTransportSection();
        DrawEncounterScenarioSection();
        DrawEncounterTimelineSection();
        DrawEncounterProbeSection();
        DrawEncounterAbilitiesSection();
        DrawEncounterCoverageSection();
        DrawEncounterTapeSection();
        EndCreatorContent();

        ImGui.SetWindowFontScale(1f);
        ImGui.End();
        PopCreatorStyle();
        _activePanelTune = null;

    }

    private void DrawEncounterToolbar()
    {
        float cs = CreatorUiScale;
        EncounterDataClient data = EncounterData;
        string status =
            data.Fetching ? "fetching behaviour tables..."
            : data.Data is { } snapshot
                ? $"{snapshot.Describe()} ({snapshot.Source})"
                : "no behaviour data — offline defaults in use";
        // The binary's build time, in the tool where it matters most: every
        // "the fix isn't working" conversation starts by reading this line.
        if (Engine.ClientWindow.BuildStamp is { Length: > 0 } stamp)
            status = $"build {stamp} · {status}";
        float rowStart = ImGui.GetCursorPosX();
        float avail = ImGui.GetContentRegionAvail().X;
        // Minimize collapses the floating window into its chip — meaningless in
        // the docked workspace, where the deck's rail button is the toggle.
        bool offerMinimize = !CreatorWorkspaceActive;
        float buttonW = EncounterPanelButtonWidth("Refresh DB") + (offerMinimize
            ? ImGui.GetStyle().ItemSpacing.X + EncounterPanelButtonWidth("Minimize")
            : 0f);
        bool inline = ImGui.CalcTextSize(status).X + ImGui.GetStyle().ItemSpacing.X +
                      buttonW <= avail;
        if (inline)
        {
            ImGui.TextDisabled(status);
            ImGui.SameLine(rowStart + avail - buttonW);
        }
        else
        {
            ImGui.PushTextWrapPos(0f);
            ImGui.TextDisabled(status);
            ImGui.PopTextWrapPos();
        }
        if (EncounterPanelButton("Refresh DB"))
        {
            data.BeginFetch(Settings.DevWindow.SuiBaseUrl, forceRefresh: true);
            _encounterFacts = null;
        }
        if (offerMinimize)
        {
            ImGui.SameLine();
            if (EncounterPanelButton("Minimize"))
            {
                // A manual collapse sticks until the chip is clicked; it is not
                // undone by the auto-restore that ends an auto collapse.
                _encounterLabCollapsed = true;
                _encounterLabAutoCollapsed = false;
            }
        }
        if (data.Data?.Error is { Length: > 0 } error)
            ImGui.TextColored(new Vector4(1f, .55f, .35f, 1f), error);
    }

    // ── section headers ──────────────────────────────────────────────────────
    // The workspace deck presents sections through its rail + tabs, where a
    // collapsing band inside the content is pure vertical waste; the classic
    // floating window keeps the bands. Every encounter section gates on this
    // helper instead of calling CollapsingHeader directly.

    private bool _encounterSectionsFlat;

    private bool EncounterSectionHeader(string label,
        ImGuiTreeNodeFlags flags = ImGuiTreeNodeFlags.None)
        => _encounterSectionsFlat || ImGui.CollapsingHeader(label, flags);

    /// <summary>One fixed-width card in a side-by-side deck row (flat mode
    /// only): repeating blocks - a playbook phase, a roster body - sit next to
    /// each other across the deck instead of stacking down it. Sets its own
    /// font scale: ImGui consults only the IMMEDIATE parent's FontWindowScale,
    /// so a grandchild of the deck's scaled child would otherwise fall back to
    /// the base font.</summary>
    private void BeginEncounterDeckCard(string id, float width, float height = 0f)
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.10f, 0.09f, 0.075f, 0.65f));
        ImGui.BeginChild(id, new Vector2(width, height), true);
        ImGui.SetWindowFontScale(CreatorTextScale);
    }

    private static void EndEncounterDeckCard()
    {
        ImGui.SetWindowFontScale(1f);
        ImGui.EndChild();
        ImGui.PopStyleColor();
    }

    // ── subject ──────────────────────────────────────────────────────────────

    /// <summary>The first line of the window answers "what is this showing me and
    /// what do I do next" - everything else is detail beneath it.</summary>
    private void DrawEncounterStatusLine()
    {
        if (_encounterSim is not { } sim || _encounterDefinition is not { } definition)
        {
            ImGui.TextColored(new Vector4(1f, .85f, .4f, 1f),
                "pick an encounter below - the fight simulates the moment it loads");
            return;
        }
        int bodies = _encounterScenario.Count(a => a.Role == EncounterActorRole.Friendly);
        ImGui.TextColored(new Vector4(.55f, 1f, .6f, 1f),
            $"{definition.Name}: full fight simulated · {_encounterOutcome}");
        if (bodies == 0)
        {
            ImGui.TextDisabled("no raid bodies placed - 'Place raid (10)' in Scenario puts a raid in front of her");
        }
        else
        {
            float bodyDps = _encounterScenario
                .Where(a => a.Role == EncounterActorRole.Friendly).Sum(a => a.Dps);
            float dialDps = Math.Clamp(Settings.EncounterLab.RaidDpsFraction, 0f, 0.5f) *
                            (sim.Boss?.MaxHealth ?? 0);
            string? holderKey = AggroHolderKeyAt(_encounterViewMs);
            string holder = _encounterScenario.FirstOrDefault(a => a.Key == holderKey)?.Name
                            ?? "nearest body (no plan)";
            ImGui.TextDisabled($"{bodies} bodies · {bodyDps:0}/s from bodies + {dialDps:0}/s dial · " +
                               $"aggro: {holder}");
        }
        ImGui.Separator();
    }

    private void DrawEncounterSubjectSection()
    {
        if (!EncounterSectionHeader("Encounter", ImGuiTreeNodeFlags.DefaultOpen)) return;

        EncounterLibrary library = EncounterLibraryRef;
        if (EncounterPanelButton("Reload documents")) library.Reload();
        ImGui.SameLine();
        ImGui.TextDisabled($"{library.Count} authored");
        foreach (string error in library.Errors)
            ImGui.TextColored(new Vector4(1f, .5f, .4f, 1f), error);

        foreach (EncounterDefinition definition in library.All.OrderBy(d => d.Name))
        {
            bool active = _encounterDefinition?.Key == definition.Key;
            if (ImGui.RadioButton($"{definition.Name}##enc-{definition.Key}", active) && !active)
                LoadEncounterDocument(definition);
        }

        // The selected creature is the other way in: click a mob, inspect its
        // encounter. An authored document wins; otherwise one is derived live.
        // _selectionGuid is the same click-to-inspect selection the NPC dev window
        // reads, so the two tools agree on what "selected" means.
        if (_selectionGuid != 0 && _entities.TryGet(_selectionGuid, out WorldEntity target) &&
            target.IsCreature)
        {
            string name = _creatureNames.GetValueOrDefault(target.Entry, $"creature {target.Entry}");
            if (EncounterPanelButton($"Load selected: {name}##enc-selected"))
                LoadEncounterForEntry(target.Entry, name, Math.Max(target.Fields.MaxHealth, 1000u));
        }
        else
        {
            ImGui.TextDisabled("select a creature in the world to inspect it");
        }

        ImGui.Separator();
        if (_encounterDefinition is not { } loaded)
        {
            ImGui.TextDisabled("no encounter loaded");
            return;
        }

        ImGui.Text(loaded.Name);
        ImGui.TextDisabled($"entry {loaded.PrimaryEntry} · {loaded.Phases.Count} phases · " +
                           $"{loaded.Abilities.Count} abilities");
        EncounterFidelity worst = loaded.WorstFidelity();
        CreatorHelp($"Weakest authored fact: {EncounterSchema.Describe(worst)}\n\n" +
                    "Open provenance & coverage below for the full audit trail.");

        // The transcription essay matters when auditing the document, not when
        // running a fight; folded, it stops being the first screenful a human sees.
        if (ImGui.TreeNode("provenance & coverage"))
        {
            ImGui.TextDisabled(_encounterSourceNote);
            ImGui.TextDisabled($"coverage: {EncounterSchema.Describe(loaded.Coverage)}");
            if (loaded.Provenance.CoreBuildHash is { Length: > 0 } hash)
                ImGui.TextDisabled($"core: {hash}");
            if (loaded.Note is { Length: > 0 } note) ImGui.TextWrapped(note);
            ImGui.TreePop();
        }
    }

    // ── overlays ─────────────────────────────────────────────────────────────

    private void DrawEncounterOverlaySection()
    {
        if (!EncounterSectionHeader("Overlays")) return;
        var settings = Settings.EncounterLab;
        bool changed = false;

        bool footprints = settings.ShowFootprints;
        if (ImGui.Checkbox("footprints at this instant", ref footprints))
        { settings.ShowFootprints = footprints; changed = true; }

        bool structural = settings.ShowStructural;
        if (ImGui.Checkbox("structural (everything that could ever land)", ref structural))
        { settings.ShowStructural = structural; changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Ignores timing. One seeded run is one roll of the dice;\n" +
                             "this answers whether a spot is reachable at all.");

        bool route = settings.ShowRoute;
        if (ImGui.Checkbox("authored route", ref route)) { settings.ShowRoute = route; changed = true; }

        bool actors = settings.ShowActors;
        if (ImGui.Checkbox("actors + probe", ref actors)) { settings.ShowActors = actors; changed = true; }

        bool labels = settings.ShowLabels;
        if (ImGui.Checkbox("labels", ref labels)) { settings.ShowLabels = labels; changed = true; }

        bool models = settings.ShowModels;
        if (ImGui.Checkbox("models (rendered puppets)", ref models))
        { settings.ShowModels = models; changed = true; }

        int linger = settings.FootprintLingerMs;
        ImGui.SetNextItemWidth(160f * CreatorUiScale);
        if (ImGui.SliderInt("linger ms", ref linger, 200, 6000))
        { settings.FootprintLingerMs = linger; changed = true; }

        if (changed) SettingsFile?.Save();

        // Visualized abilities are controlled from their own pop-out; keep the active-phase
        // count and a clear action discoverable with that panel shut.
        int visualizedCount = _encounterDefinition is { } definition && _encounterSim is { } sim
            ? definition.AbilitiesIn(sim.PhaseKey)
                .Count(a => _encounterVisualizedAbilities.Contains(a.Key))
            : 0;
        if (visualizedCount > 0)
        {
            ImGui.Separator();
            ImGui.TextColored(new Vector4(1f, .82f, .35f, 1f),
                $"{visualizedCount} phase ability visualization(s) on");
            if (EncounterPanelButton("clear ability visualizations", compact: true))
                _encounterVisualizedAbilities.Clear();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Toggle individual abilities in the\n" +
                                 "Ability Visualizer pop-out.");
        }

        ImGui.Separator();
        ImGui.TextDisabled("overlay colour key");
        CreatorHelp("Green — exact database fact\n" +
                    "Blue — reviewed compiled-script manifest\n" +
                    "Gold — derived or heuristic fact\n" +
                    "Red — unmodeled or unknown fact");
    }

    // ── transport ────────────────────────────────────────────────────────────

    private void DrawEncounterTransportSection()
    {
        if (!EncounterSectionHeader("Transport", ImGuiTreeNodeFlags.DefaultOpen)) return;
        if (_encounterSim is not { } sim)
        {
            ImGui.TextDisabled("load an encounter to simulate it");
            return;
        }
        DrawEncounterTransportControls(sim);
        ImGui.Separator();
        DrawEncounterTransportDials();
    }

    /// <summary>GO / play / step / reset, the scrub bar, phase and boss health,
    /// and the pull state. A sub-tab in the workspace deck.</summary>
    private void DrawEncounterTransportControls(EncounterSim sim)
    {
        float cs = CreatorUiScale;

        int fightEndMs = sim.Timeline.Count > 0 ? sim.Timeline[^1].TimeMs : 0;
        int stagedCount = EncounterStagedCount;
        if (stagedCount > 0)
        {
            // The staged plan's commit button, impossible to miss. Play does the
            // same thing while a plan is staged — both spellings of "send them".
            if (EncounterPanelButton($"GO ({stagedCount})", 90f * cs))
                EncounterGoStagedOrders();
            bool goHovered = ImGui.IsItemHovered();
            EncounterSameLineForButton(_encounterPlaying ? "Pause" : "Play", 70f * cs);
            if (goHovered)
                ImGui.SetTooltip("Commit every staged waypoint: the whole raid\n" +
                                 "moves at once, chains run leg by leg.");
        }
        if (EncounterPanelButton(_encounterPlaying ? "Pause" : "Play", 70f * cs))
        {
            if (!_encounterPlaying && stagedCount > 0)
            {
                EncounterGoStagedOrders();   // Play IS Go while a plan is staged
            }
            else
            {
                _encounterPlaying = !_encounterPlaying;
                // Play at the end means "run it again", not a dead button.
                if (_encounterPlaying && _encounterViewMs >= fightEndMs) ScrubTo(0);
            }
        }
        if (stagedCount > 0)
        {
            EncounterSameLineForButton("Undo", 70f * cs);
            if (EncounterPanelButton("Undo", 70f * cs))
                UndoEncounterWaypoint();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Remove the last staged waypoint gesture (Ctrl+Z).");
        }
        EncounterSameLineForButton("Step");
        if (EncounterPanelButton("Step"))
        {
            _encounterPlaying = false;
            ScrubTo(_encounterViewMs + sim.Options.StepMs);
        }
        EncounterSameLineForButton("Back");
        if (EncounterPanelButton("Back"))
        {
            _encounterPlaying = false;
            ScrubTo(_encounterViewMs - sim.Options.StepMs);
        }
        EncounterSameLineForButton("Reset");
        if (EncounterPanelButton("Reset")) { _encounterPlaying = false; ScrubTo(0); }

        // Scrubbing is an index into the snapshot ring, not a re-simulation — the
        // state is small enough to store every step, so rewind is free. The bar
        // spans the whole pre-simulated fight from the moment a document loads.
        int headMs = Math.Max(fightEndMs, 1);
        int view = _encounterViewMs;
        // The handle reads the COMBAT clock (pre-pull until she is engaged, then counting
        // from the pull); the track still spans the whole pre-simulated window so the walk-in
        // is scrubbable. The tail shows the fight's length once a pull is known.
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 90f * cs);
        if (ImGui.SliderInt("##enc-scrub", ref view, 0, headMs, EncounterFightClock(sim)))
        {
            _encounterScrubbing = true;
            _encounterPlaying = false;
            ScrubTo(view);
        }
        else if (_encounterScrubbing && !ImGui.IsItemActive()) _encounterScrubbing = false;
        ImGui.SameLine();
        ImGui.TextDisabled(sim.EngagedAtMs >= 0
            ? $"/ {(fightEndMs - sim.EngagedAtMs) / 1000f:0.0}s fight"
            : $"/ {headMs / 1000f:0.0}s");

        ImGui.Text($"phase: {sim.Definition.Phase(sim.PhaseKey)?.Name ?? sim.PhaseKey}");
        EncounterSameLineForButton("Visualize abilities", 150f * cs);
        if (EncounterPanelButton("Visualize abilities", 150f * cs))
            OpenEncounterAbilityVisualizer();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Open a phase-aware pop-out with one Visualize button\n" +
                             "for every boss mechanic available right now.");
        if (sim.Boss is { } boss)
        {
            // A real health BAR: the health gates that drive phases are the whole point,
            // and a dim "100%" buried after the phase text was easy to miss. Red→amber→
            // green by fraction so a glance reads how close she is to the next gate.
            float hp = Math.Clamp(boss.HealthFraction, 0f, 1f);
            Vector4 bar = hp > 0.5f ? new Vector4(.85f, .30f, .28f, 1f)
                        : hp > 0.2f ? new Vector4(.90f, .55f, .20f, 1f)
                                    : new Vector4(.95f, .80f, .20f, 1f);
            ImGui.PushStyleColor(ImGuiCol.PlotHistogram, bar);
            ImGui.ProgressBar(hp, new Vector2(ImGui.GetContentRegionAvail().X, 16f * CreatorUiScale),
                $"{boss.Spec.Name}  {hp * 100f:0}%");
            ImGui.PopStyleColor();
        }
        if (sim.EngagedAtMs < 0)
            ImGui.TextColored(new Vector4(1f, .75f, .4f, 1f),
                "not pulled - she waits behind her ring until a body crosses it");
        else if (_encounterViewMs < sim.EngagedAtMs)
            ImGui.TextDisabled($"pre-pull · she engages at {sim.EngagedAtMs / 1000f:0.0}s");
        if (_encounterOutcome.Length > 0) ImGui.TextDisabled(_encounterOutcome);
    }

    /// <summary>Speed / seed / step / raid-dps dials. A sub-tab in the deck.</summary>
    private void DrawEncounterTransportDials()
    {
        var settings = Settings.EncounterLab;
        float cs = CreatorUiScale;
        float speed = settings.PlaybackSpeed;
        ImGui.SetNextItemWidth(160f * cs);
        if (ImGui.SliderFloat("speed", ref speed, 0.1f, 8f, "%.2fx"))
            settings.PlaybackSpeed = speed;
        if (ImGui.IsItemDeactivatedAfterEdit()) SettingsFile?.Save();

        int seed = settings.Seed;
        ImGui.SetNextItemWidth(120f * cs);
        if (ImGui.InputInt("seed", ref seed))
        {
            settings.Seed = Math.Max(seed, 0);
            RebuildEncounterSim();
        }
        if (ImGui.IsItemDeactivatedAfterEdit()) SettingsFile?.Save();
        ImGui.SameLine();
        ImGui.TextDisabled("names the fight");

        int step = settings.StepMs;
        ImGui.SetNextItemWidth(120f * cs);
        if (ImGui.SliderInt("step ms", ref step, 20, 500))
        {
            settings.StepMs = step;
            RebuildEncounterSim();
        }
        if (ImGui.IsItemDeactivatedAfterEdit()) SettingsFile?.Save();

        float dps = settings.RaidDpsFraction * 100f;
        ImGui.SetNextItemWidth(160f * cs);
        if (ImGui.SliderFloat("raid dps", ref dps, 0f, 5f, "%.2f%% hp/s"))
        {
            settings.RaidDpsFraction = dps / 100f;
            RebuildEncounterSim();
        }
        if (ImGui.IsItemDeactivatedAfterEdit()) SettingsFile?.Save();
        CreatorHelp("This raid-DPS input is a heuristic used to reach health-gated phases; " +
                    "it is not a damage model.");
    }

    private void ScrubTo(int targetMs)
    {
        if (_encounterSim is not { } sim || sim.Timeline.Count == 0) return;
        targetMs = Math.Clamp(targetMs, 0, sim.Timeline[^1].TimeMs);
        int step = Math.Max(sim.Options.StepMs, 1);
        int index = Math.Clamp(targetMs / step, 0, sim.Timeline.Count - 1);
        sim.RestoreTo(index);
        _encounterViewMs = sim.Timeline[index].TimeMs;
        _encounterProbeDirty = true;
    }

    /// <summary>The COMBAT clock the user reads: it holds before the pull and counts from
    /// it. The engine's own clock runs from the moment a document loads so the raid can walk
    /// in and she can roam - but none of that is "the fight". Until a body crosses her ring
    /// (<see cref="EncounterSim.EngagedAtMs"/>) this reads "pre-pull" (with the lead-in to
    /// contact once one is known); at the pull it is 0.0s and counts up from there.</summary>
    private string EncounterFightClock(EncounterSim sim)
    {
        if (sim.EngagedAtMs < 0 || _encounterViewMs < sim.EngagedAtMs) return "pre-pull";
        return $"{(_encounterViewMs - sim.EngagedAtMs) / 1000f:0.0}s";
    }

    // ── scenario ─────────────────────────────────────────────────────────────

    private void DrawEncounterScenarioSection()
    {
        if (!EncounterSectionHeader("Scenario", ImGuiTreeNodeFlags.DefaultOpen)) return;
        if (_encounterDefinition is null)
        {
            ImGui.TextDisabled("load an encounter first");
            return;
        }
        DrawEncounterScenarioPlacement();
        DrawEncounterPlaybook();
        DrawEncounterScenarioAggroPlan();
        ImGui.Separator();
        DrawEncounterScenarioRoster();
    }

    /// <summary>Boss/raid placement, the pull ring, pre-pull movement truth and
    /// the what-if roam, the melee-reach gate, and the RTS verb hints. A sub-tab
    /// in the workspace deck; the first block of the classic stacked section.</summary>
    private void DrawEncounterScenarioPlacement()
    {
        if (_encounterPlacing != EncounterPlacement.None)
            ImGui.TextColored(new Vector4(.5f, 1f, .6f, 1f),
                "click the world to place · right-click cancels");
        if (_encounterBossMove != BossMoveStage.None)
            ImGui.TextColored(new Vector4(.5f, 1f, .6f, 1f),
                _encounterBossMove == BossMoveStage.Follow
                    ? "boss follows the cursor · click the ground to drop her · right-click cancels"
                    : "spin her facing with the mouse · click to set · right-click cancels");

        if (EncounterPanelButton("Move boss", compact: true))
            BeginEncounterBossMove();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("She follows the cursor; click to drop her, spin the\n" +
                             "facing, click to set. Cones and footprints track the\n" +
                             "whole gesture, so you SEE where attacks land before\n" +
                             "authoring waypoints.");
        EncounterSameLineForButton("Reset boss", compact: true);
        if (EncounterPanelButton("Reset boss", compact: true))
            ResetEncounterBoss();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Back to the document's spawn - position and facing.");
        EncounterSameLineForButton("Place boss", compact: true);
        if (EncounterPanelButton("Place boss", compact: true))
            _encounterPlacing = EncounterPlacement.Boss;
        EncounterSameLineForButton("Place raid (10)", compact: true);
        if (EncounterPanelButton("Place raid (10)", compact: true)) PlaceScenarioRaid();
        EncounterSameLineForButton("Add dummy", compact: true);
        if (EncounterPanelButton("Add dummy", compact: true))
        {
            Vector3 near = _encounterScenario.FirstOrDefault()?.Position ?? Vector3.Zero;
            Vector3 at = near + new Vector3(5f, 5f, 0f);
            at.Z = EncounterGroundZ(at.X, at.Y, near.Z);
            _encounterScenario.Add(new EncounterActorSpec(
                $"dummy{++_encounterDummySerial}", $"dummy {_encounterDummySerial}", 0,
                EncounterActorRole.Friendly, at,
                DisplayId: 3167));
            RebuildEncounterSim();
        }
        EncounterSameLineForButton("Snap to me", compact: true);
        if (EncounterPanelButton("Snap to me", compact: true) &&
            DevPlayerPosition() is { } me)
        {
            MoveScenarioActor(BossActorKey(), me);
        }
        ImGui.TextDisabled("bodies steer her targeting AND take the hits - move one, replay, compare");

        // The pull ring: the fight begins when a body crosses it. Authoring the
        // pull is the first order of every plan.
        float pullRange = Settings.EncounterLab.PullRangeYards;
        ImGui.SetNextItemWidth(140f * CreatorUiScale);
        if (ImGui.SliderFloat("pull range yd", ref pullRange, 5f, 100f, "%.0f"))
            Settings.EncounterLab.PullRangeYards = pullRange;
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            SettingsFile?.Save();
            RebuildEncounterSimKeepingView();
        }
        if (_encounterScenario.FirstOrDefault(a => a.Role == EncounterActorRole.Boss)
                ?.DetectionRangeYards is > 0f and var detection)
        {
            ImGui.TextDisabled($"boss detection: {detection:0} yd");
            CreatorHelp("Source fidelity: exact-db. The core adds a level delta on top " +
                        "of this detection_range value.");
        }

        // Pre-pull movement: the document's DB-sourced answer first, the invented
        // what-if second and clearly labeled as such. Stationary is an answer.
        IdleMovementSpec? idle = _encounterScenario
            .FirstOrDefault(a => a.Role == EncounterActorRole.Boss)?.IdleMovement;
        if (idle is { } declared)
        {
            string truth = declared.Kind switch
            {
                IdleMovementKind.Wander => $"wanders {declared.WanderYards:0} yd",
                IdleMovementKind.Waypoints =>
                    $"patrols {declared.Points?.Count ?? 0} waypoints",
                _ => "stands at spawn until pulled",
            };
            ImGui.TextColored(FidelityColor(EncounterFidelity.ExactDb), $"pre-pull: {truth}");
            CreatorHelp("Source fidelity: exact-db." +
                        (declared.Note is { Length: > 0 } note ? $"\n\n{note}" : ""));
        }
        else
        {
            ImGui.TextDisabled("pre-pull: the document does not say how she idles");
        }

        if (idle is null or { Kind: IdleMovementKind.Stationary })
        {
            // Opt-in what-if: default off she stands (exact-db). Toggle it and she wanders
            // the sandbox within the radius — never fights the owner's intent by default.
            bool roam = Settings.EncounterLab.SandboxRoam;
            if (ImGui.Checkbox("sandbox roam (what-if)", ref roam))
            {
                Settings.EncounterLab.SandboxRoam = roam;
                SettingsFile?.Save();
                RebuildEncounterSimKeepingView();
            }
            if (roam)
            {
                ImGui.SameLine();
                float roamRadius = Settings.EncounterLab.RoamRadiusYards;
                ImGui.SetNextItemWidth(110f * CreatorUiScale);
                if (ImGui.SliderFloat("roam yd", ref roamRadius, 5f, 60f, "%.0f"))
                    Settings.EncounterLab.RoamRadiusYards = roamRadius;
                if (ImGui.IsItemDeactivatedAfterEdit())
                {
                    SettingsFile?.Save();
                    RebuildEncounterSimKeepingView();
                }
            }
        }

        bool meleeReach = Settings.EncounterLab.MeleeDpsNeedsReach;
        if (ImGui.Checkbox("tank/melee dps needs reach", ref meleeReach))
        {
            Settings.EncounterLab.MeleeDpsNeedsReach = meleeReach;
            SettingsFile?.Save();
            RebuildEncounterSimKeepingView();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Their dps counts only beside a grounded boss,\n" +
                             "so an air phase honestly stalls her health gates.");

        // The free view is the fast way to author all of this: Ctrl+F, pick the
        // raid up like any RTS, and every verb lands as a sim order at the scrub
        // head — the fight replays around it instantly.
        ImGui.TextColored(new Vector4(.6f, .85f, 1f, 1f),
            "Ctrl+F to command the raid: click/drag selects bodies · Shift+Click" +
            " stages waypoints · GO (or Play) runs the plan · Ctrl+RClick" +
            " teleports (what-if)");
        ImGui.TextColored(new Vector4(.6f, .85f, 1f, 1f),
            "Orient a waypoint: Shift+Right-click a dot to grab it, move the mouse to" +
            " spin the arrow freely, click to set");
    }

    /// <summary>The aggro plan, one line per swap. SHE FACES THE HOLDER, so this
    /// list plus the bodies' positions IS the geometry of the whole fight.</summary>
    private void DrawEncounterScenarioAggroPlan()
    {
        if (_encounterAggroPlan.Count > 0)
        {
            for (int a = 0; a < _encounterAggroPlan.Count; a++)
            {
                TimedAggro entry = _encounterAggroPlan[a];
                string who = _encounterScenario.FirstOrDefault(s => s.Key == entry.Key)?.Name ?? entry.Key;
                ImGui.TextColored(new Vector4(1f, .5f, .4f, 1f),
                    $"aggro: {who} from {entry.TimeMs / 1000f:0.0}s");
                EncounterSameLineForButton($"x##ag{a}", compact: true);
                if (EncounterPanelButton($"x##ag{a}", compact: true))
                {
                    _encounterAggroPlan.RemoveAt(a);
                    RebuildEncounterSimKeepingView();
                    break;
                }
            }
        }
        else ImGui.TextDisabled("no aggro assigned - she turns to the nearest body ('aggro' on a body fixes that)");
    }

    /// <summary>The roster: one block per body with its verbs (rules / place /
    /// move / aggro / remove), its dps input, and its authored moves.</summary>
    private void DrawEncounterScenarioRoster()
    {
        string? aggroNowKey = AggroHolderKeyAt(_encounterViewMs);
        // Flat (deck) mode: one CARD per body, side by side; the row scrolls
        // horizontally when the raid outgrows the width.
        bool flat = _encounterSectionsFlat;
        if (flat)
            ImGui.BeginChild("##roster-cards", new Vector2(0f, 0f), false,
                ImGuiWindowFlags.HorizontalScrollbar);
        bool firstCard = true;
        for (int i = 0; i < _encounterScenario.Count; i++)
        {
            EncounterActorSpec actor = _encounterScenario[i];
            ImGui.PushID(i);
            if (flat)
            {
                if (!firstCard) ImGui.SameLine();
                firstCard = false;
                BeginEncounterDeckCard($"##ros-card-{actor.Key}", 230f * CreatorUiScale);
            }
            ImGui.Text($"{actor.Name}");
            ImGui.SameLine();
            ImGui.TextDisabled(actor.Job == RaidJob.None
                ? $"({actor.Role})"
                : $"({actor.Job})");
            if (actor.Key == aggroNowKey)
            {
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(1f, .5f, .4f, 1f), "AGGRO");
            }
            ImGui.TextDisabled($"  {actor.Position.X:0.#}, {actor.Position.Y:0.#}, {actor.Position.Z:0.#} " +
                               $"· r{actor.BoundingRadius:0.#}");
            if (actor.Role == EncounterActorRole.Friendly)
            {
                bool canCustomize = !_encounterOrbitDragging && !_encounterOrientSpinning;
                if (EncounterPanelButton("rules", enabled: canCustomize, compact: true))
                    OpenEncounterPlayerSetup(actor.Key);
                bool rulesHovered = ImGui.IsItemHovered();
                EncounterSameLineForButton("place", compact: true);
                if (!canCustomize && rulesHovered)
                    ImGui.SetTooltip("Finish the active world-placement gesture first.");
            }
            if (EncounterPanelButton("place", compact: true))
            {
                _encounterPlacing = EncounterPlacement.Actor;
                _encounterPlacingActorKey = actor.Key;
            }
            if (actor.Role == EncounterActorRole.Boss)
            {
                EncounterSameLineForButton("move", compact: true);
                if (EncounterPanelButton("move", compact: true)) BeginEncounterBossMove();
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("She follows the cursor; click to drop, spin, click to set.");
                EncounterSameLineForButton("reset", compact: true);
                if (EncounterPanelButton("reset", compact: true)) ResetEncounterBoss();
            }
            if (actor.Role != EncounterActorRole.Boss)
            {
                EncounterSameLineForButton($"move @ {_encounterViewMs / 1000f:0.0}s",
                    compact: true);
                // The order is stamped with the SCRUB TIME: scrub to the moment,
                // click the ground, and the body runs there at that moment in
                // every replay. This is the "tell them where to move" verb.
                if (EncounterPanelButton($"move @ {_encounterViewMs / 1000f:0.0}s",
                        compact: true))
                {
                    _encounterPlacing = EncounterPlacement.ActorMove;
                    _encounterPlacingActorKey = actor.Key;
                }
                EncounterSameLineForButton($"aggro @ {_encounterViewMs / 1000f:0.0}s",
                    compact: true);
                if (EncounterPanelButton($"aggro @ {_encounterViewMs / 1000f:0.0}s",
                        compact: true))
                    AssignAggro(actor.Key, _encounterViewMs);
                EncounterSameLineForButton("remove", compact: true);
                if (EncounterPanelButton("remove", compact: true))
                {
                    _encounterScenario.RemoveAt(i);
                    _encounterAggroPlan.RemoveAll(a => a.Key == actor.Key);
                    RebuildEncounterSimKeepingView();
                    if (flat) EndEncounterDeckCard();
                    ImGui.PopID();
                    break;
                }

                // This body's damage to the boss, per second - an owner-set input.
                int dps = (int)actor.Dps;
                ImGui.SetNextItemWidth(90f * CreatorUiScale);
                if (ImGui.DragInt("dps##bodydps", ref dps, 5, 0, 10000))
                    _encounterScenario[i] = actor = actor with { Dps = Math.Max(dps, 0) };
                if (ImGui.IsItemDeactivatedAfterEdit()) RebuildEncounterSimKeepingView();
                if (actor.Moves is { Count: > 0 } moves)
                {
                    for (int m = 0; m < moves.Count; m++)
                    {
                        ImGui.TextDisabled($"    {DescribeMove(moves[m])}");
                        EncounterSameLineForButton($"x##mv{m}", compact: true);
                        if (EncounterPanelButton($"x##mv{m}", compact: true))
                        {
                            RemoveScenarioMove(actor.Key, m);
                            break;
                        }
                    }
                }
            }
            if (flat) EndEncounterDeckCard();
            ImGui.PopID();
        }
        if (flat) ImGui.EndChild();
    }

    /// <summary>The (phase × job) standing-orders table. One combo per cell:
    /// "—" (no change at that turn), Hold, Chase boss, or To spot with a placed
    /// point. This is where "what does melee do in the air phase" is decided —
    /// as data the owner edits, never as code.</summary>
    private void DrawEncounterPlaybook()
    {
        if (_encounterDefinition is not { } definition) return;
        // In the workspace deck the playbook has its OWN sub-tab; the TreeNode
        // fold belongs to the classic stacked window.
        if (!_encounterSectionsFlat &&
            !ImGui.TreeNode("role playbook (what each job does when a phase turns)"))
            return;

        ReadOnlySpan<RaidJob> jobs = [RaidJob.Tank, RaidJob.Healer, RaidJob.Melee, RaidJob.Ranged];
        string[] kinds = ["—", "hold", "chase boss", "to spot"];
        float cs = CreatorUiScale;

        // Flat (deck) mode: one CARD per phase, side by side across the width.
        bool flat = _encounterSectionsFlat;
        if (flat)
        {
            ImGui.TextDisabled("— means no change at that turn; explicit orders always override");
            ImGui.BeginChild("##playbook-cards", new Vector2(0f, 0f), false,
                ImGuiWindowFlags.HorizontalScrollbar);
        }
        bool firstCard = true;
        foreach (EncounterPhase phase in definition.Phases)
        {
            if (flat)
            {
                if (!firstCard) ImGui.SameLine();
                firstCard = false;
                BeginEncounterDeckCard($"##pb-card-{phase.Key}", 250f * cs);
            }
            ImGui.Text(phase.Name);
            if (phase.CasterFlying) { ImGui.SameLine(); ImGui.TextDisabled("(flying)"); }
            foreach (RaidJob job in jobs)
            {
                ImGui.PushID($"{phase.Key}-{job}");
                RaidPhaseDirective? directive = PlaybookDirectiveFor(phase.Key, job);
                int current = directive?.Kind switch
                {
                    RaidDirectiveKind.Hold => 1,
                    RaidDirectiveKind.ChaseBoss => 2,
                    RaidDirectiveKind.MoveToSpot => 3,
                    _ => 0,
                };
                ImGui.TextDisabled($"  {job,-6}");
                ImGui.SameLine();
                ImGui.SetNextItemWidth(110f * cs);
                if (ImGui.Combo("##kind", ref current, kinds, kinds.Length))
                {
                    switch (current)
                    {
                        case 0: RemovePlaybookDirective(phase.Key, job); break;
                        case 1: UpsertPlaybookDirective(phase.Key, job, RaidDirectiveKind.Hold); break;
                        case 2: UpsertPlaybookDirective(phase.Key, job, RaidDirectiveKind.ChaseBoss); break;
                        case 3:
                            // Arm the placement; the directive lands with the click.
                            _encounterPlacing = EncounterPlacement.PlaybookSpot;
                            _encounterPlacingPlaybookPhase = phase.Key;
                            _encounterPlacingPlaybookJob = job;
                            break;
                    }
                }
                if (directive is { Kind: RaidDirectiveKind.MoveToSpot } spot)
                {
                    string coordinates = $"({spot.Spot.X:0}, {spot.Spot.Y:0})";
                    float coordinateWidth = ImGui.CalcTextSize(coordinates).X;
                    float rePlaceWidth = EncounterPanelButtonWidth("re-place", compact: true);
                    EncounterSameLineIfFits(coordinateWidth + ImGui.GetStyle().ItemSpacing.X +
                                            rePlaceWidth);
                    ImGui.TextDisabled(coordinates);
                    EncounterSameLineForButton("re-place", compact: true);
                    if (EncounterPanelButton("re-place", compact: true))
                    {
                        _encounterPlacing = EncounterPlacement.PlaybookSpot;
                        _encounterPlacingPlaybookPhase = phase.Key;
                        _encounterPlacingPlaybookJob = job;
                    }
                }
                ImGui.PopID();
            }
            if (flat) EndEncounterDeckCard();
        }
        if (flat) ImGui.EndChild();
        else
        {
            ImGui.TextDisabled("— means no change at that turn; explicit orders always override");
            ImGui.TreePop();
        }
    }

    /// <summary>One move entry, described the way it will fire.</summary>
    private static string DescribeMove(in TimedMove move)
    {
        string verb = move.Teleport ? "teleport" : "run";
        string when = move.Anchor switch
        {
            MoveAnchor.AfterPrevious => "then",
            MoveAnchor.OnPhaseEnter => $"on {move.PhaseKey}:",
            _ => $"@ {move.TimeMs / 1000f:0.0}s",
        };
        string facing = move.HasArrivalFacing ? " (facing)" : "";
        return $"{when} {verb} -> {move.Position.X:0.#}, {move.Position.Y:0.#}{facing}";
    }

    // ── timeline ─────────────────────────────────────────────────────────────

    private void DrawEncounterTimelineSection()
    {
        if (!EncounterSectionHeader("Timeline", ImGuiTreeNodeFlags.DefaultOpen)) return;
        if (_encounterSim is not { } sim)
        {
            ImGui.TextDisabled("nothing simulated yet");
            return;
        }

        // Independent pop-outs: one for the selected actor's live event stream, and one
        // clean phase-aware control surface for boss ability visualizations.
        if (EncounterPanelButton("Pop out > live action panel", compact: true))
            _encounterActionPanelOpen = true;
        bool actionPanelHovered = ImGui.IsItemHovered();
        EncounterSameLineForButton("Abilities > Visualize", compact: true);
        if (actionPanelHovered)
            ImGui.SetTooltip("A separate window that follows the selected body\n" +
                             "(the boss by default) and shows exactly what it\n" +
                             "steps through — casts, phase turns, hits — in real time.");
        if (EncounterPanelButton("Abilities > Visualize", compact: true))
            OpenEncounterAbilityVisualizer();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Open the current phase's boss ability visualizer.");

        // A window around the scrub head: the whole event list is unreadable, and
        // what matters is what just happened and what is about to.
        const int windowMs = 12_000;
        List<SimEvent> nearby = sim.Events
            .Where(e => Math.Abs(e.TimeMs - _encounterViewMs) <= windowMs)
            .OrderBy(e => e.TimeMs)
            .ToList();

        if (nearby.Count == 0) { ImGui.TextDisabled("no events near this instant"); return; }

        float cs = CreatorUiScale;
        // In the deck the list IS the pane: fill the height, no peephole.
        if (ImGui.BeginChild("##enc-timeline",
                new Vector2(0f, _encounterSectionsFlat ? 0f : 180f * cs), true))
        {
            foreach (SimEvent simEvent in nearby)
            {
                bool isNow = Math.Abs(simEvent.TimeMs - _encounterViewMs) <= sim.Options.StepMs;
                Vector4 color = simEvent.Kind switch
                {
                    SimEventKind.Unmodeled => new Vector4(1f, .45f, .35f, 1f),
                    SimEventKind.PhaseEnter => new Vector4(.6f, .85f, 1f, 1f),
                    SimEventKind.CastLand => FidelityColor(simEvent.Fidelity),
                    SimEventKind.ActorHit => new Vector4(1f, .35f, .3f, 1f),
                    SimEventKind.Say => new Vector4(.85f, .8f, .55f, 1f),
                    _ => new Vector4(.72f, .72f, .72f, 1f),
                };
                if (!isNow) color.W = .55f;

                string marker = isNow ? ">" : " ";
                ImGui.TextColored(color,
                    $"{marker} {simEvent.TimeMs / 1000f,6:0.0}s  {simEvent.Text}");
                if (simEvent.Kind == SimEventKind.CastLand &&
                    simEvent.Fidelity != EncounterFidelity.ExactDb &&
                    ImGui.IsItemHovered())
                    ImGui.SetTooltip(EncounterSchema.Describe(simEvent.Fidelity));
            }
        }
        ImGui.EndChild();
    }

    // ── probe ────────────────────────────────────────────────────────────────

    private void DrawEncounterProbeSection()
    {
        if (!EncounterSectionHeader("Position probe", ImGuiTreeNodeFlags.DefaultOpen)) return;

        if (EncounterPanelButton("Place probe", compact: true))
            _encounterPlacing = EncounterPlacement.Probe;
        EncounterSameLineForButton("Add waypoint", compact: true);
        if (EncounterPanelButton("Add waypoint", compact: true))
            _encounterPlacing = EncounterPlacement.ProbeWaypoint;
        EncounterSameLineForButton("Undo point", compact: true);
        if (EncounterPanelButton("Undo point", compact: true))
        { _encounterProbe.RemoveLast(); _encounterProbeDirty = true; }
        EncounterSameLineForButton("At me", compact: true);
        if (EncounterPanelButton("At me", compact: true) && DevPlayerPosition() is { } me)
        {
            _encounterProbe.Clear();
            _encounterProbe.Add(0, me);
            _encounterProbeDirty = true;
        }

        float cs = CreatorUiScale;
        float radius = _encounterProbe.Radius;
        ImGui.SetNextItemWidth(150f * cs);
        if (ImGui.SliderFloat("body radius", ref radius, 0.2f, 6f, "%.1f yd"))
        {
            _encounterProbe.Radius = radius;
            _encounterProbeDirty = true;
        }

        if (_encounterProbe.Count == 0)
        {
            ImGui.TextDisabled("no probe placed");
            return;
        }
        ImGui.TextDisabled(_encounterProbe.Count == 1
            ? "stationary body"
            : $"trajectory: {_encounterProbe.Count} waypoints");

        ProbeReport report = _encounterProbeReport;
        if (report.HitCount == 0)
            ImGui.TextColored(new Vector4(.55f, 1f, .6f, 1f), "nothing lands on this body");
        else
        {
            EncounterFidelity worst = report.WorstHitFidelity();
            ImGui.TextColored(new Vector4(1f, .6f, .5f, 1f),
                $"{report.HitCount} hits · first at {report.FirstHit!.TimeMs / 1000f:0.0}s");
            if (worst != EncounterFidelity.ExactDb)
                CreatorHelp($"Weakest hitting fact: {EncounterSchema.Describe(worst)}");
        }

        // The unmodeled warning is the honest half: a spot is not safe just because
        // the things that would hit it were never modelled.
        if (_encounterDefinition?.Holes().Any() == true)
            ImGui.TextColored(new Vector4(1f, .75f, .4f, 1f),
                "this encounter has unmodeled mechanics — 'safe' is not proof");

        if (ImGui.BeginChild("##enc-probe", new Vector2(0f, 150f * CreatorUiScale), true))
        {
            foreach (ProbeThreat threat in report.Threats.OrderBy(t => t.TimeMs).Take(60))
                ImGui.TextColored(
                    threat.Covered ? new Vector4(1f, .62f, .55f, 1f) : new Vector4(.6f, .7f, .6f, 1f),
                    threat.Describe());
            if (report.Threats.Count == 0)
                ImGui.TextDisabled("no effect came within reporting distance");
        }
        ImGui.EndChild();
    }

    // ── inspector ────────────────────────────────────────────────────────────

    private void DrawEncounterAbilitiesSection()
    {
        if (!EncounterSectionHeader("Abilities")) return;
        if (_encounterDefinition is not { } definition)
        {
            ImGui.TextDisabled("load an encounter first");
            return;
        }

        foreach (EncounterAbility ability in definition.Abilities)
        {
            ImGui.PushID(ability.Key);
            bool open = ImGui.TreeNodeEx(ability.Name, ImGuiTreeNodeFlags.None);
            CreatorHelp($"Source fidelity: {EncounterSchema.Describe(ability.Fidelity)}");
            if (open)
            {
                if (ability.SpellId != 0) ImGui.TextDisabled($"spell {ability.SpellId}");
                ImGui.TextDisabled($"trigger: {ability.Trigger.Kind}" +
                    (ability.Trigger.Threshold != 0f ? $" @ {ability.Trigger.Threshold:0.##}" : ""));
                if (ability.Timing.Repeats || ability.Timing.InitialMaxMs > 0)
                    ImGui.TextDisabled(DescribeTiming(ability.Timing));
                ImGui.TextDisabled($"target: {ability.Target.Kind}");
                ImGui.TextDisabled($"shape: {DescribeGeometry(ability)}");
                if (ability.ChancePercent < 100) ImGui.TextDisabled($"chance: {ability.ChancePercent}%");
                if (ability.Phases is { Count: > 0 } phases)
                    ImGui.TextDisabled($"phases: {string.Join(", ", phases)}");
                if (ability.Note is { Length: > 0 } note) ImGui.TextWrapped(note);
                foreach (EncounterSourceRef source in ability.Sources ?? [])
                    ImGui.TextDisabled($"  <- {source}");
                ImGui.TreePop();
            }
            ImGui.PopID();
        }
    }

    private string DescribeGeometry(EncounterAbility ability)
    {
        EncounterGeometrySpec geometry = ability.Geometry;
        EnsureEncounterFacts();
        switch (geometry.Kind)
        {
            case FootprintKind.None: return "no spatial effect";
            case FootprintKind.Cone:
            {
                float degrees = geometry.ConeDegrees;
                if (degrees == 0f && _encounterFacts is not null &&
                    _encounterFacts.TryGetConeDegrees(ability.SpellId, out float dbDegrees))
                    degrees = dbDegrees;
                string arc = degrees < 0f ? $"{-degrees:0} deg REAR arc" : $"{degrees:0} deg arc";
                return $"cone, {arc}, {ResolvedRadius(ability):0.#} yd";
            }
            case FootprintKind.Circle: return $"circle, {ResolvedRadius(ability):0.#} yd";
            case FootprintKind.Line: return $"line, {geometry.Width:0.#} yd wide";
            case FootprintKind.Projectile: return $"projectile, {ResolvedRadius(ability):0.#} yd impact";
            case FootprintKind.PointChain:
            {
                int count = geometry.Points?.Count ?? geometry.PointSpellIds?.Count ?? 0;
                return $"lane of {count} spheres, {ResolvedRadius(ability):0.#} yd each";
            }
            default: return geometry.Kind.ToString();
        }
    }

    private float ResolvedRadius(EncounterAbility ability)
    {
        if (ability.Geometry.Radius > 0f) return ability.Geometry.Radius;
        EnsureEncounterFacts();
        return _encounterFacts is not null &&
               _encounterFacts.TryGetRadius(ability.SpellId, out float radius) ? radius : 0f;
    }

    private static string DescribeTiming(EncounterTiming timing)
    {
        string initial = timing.InitialMinMs == timing.InitialMaxMs
            ? $"{timing.InitialMinMs / 1000f:0.#}s"
            : $"{timing.InitialMinMs / 1000f:0.#}-{timing.InitialMaxMs / 1000f:0.#}s";
        if (!timing.Repeats) return $"first at {initial}";
        string repeat = timing.RepeatMinMs == timing.RepeatMaxMs
            ? $"{timing.RepeatMinMs / 1000f:0.#}s"
            : $"{timing.RepeatMinMs / 1000f:0.#}-{timing.RepeatMaxMs / 1000f:0.#}s";
        return $"first at {initial}, then every {repeat}";
    }

    // ── coverage ─────────────────────────────────────────────────────────────

    private void DrawEncounterCoverageSection()
    {
        if (!EncounterSectionHeader("Coverage & holes")) return;
        if (_encounterDefinition is not { } definition)
        {
            ImGui.TextDisabled("load an encounter first");
            return;
        }

        ImGui.TextDisabled($"sources consulted: {EncounterSchema.Describe(definition.Coverage)}");
        if (definition.Coverage.HasFlag(World.Encounters.EncounterCoverage.CppCreatureScript))
            ImGui.TextColored(new Vector4(1f, .75f, .4f, 1f),
                "Scripted behaviour exists in compiled C++; this encounter is not fully modeled.");

        List<string> holes = definition.Holes().ToList();
        if (holes.Count == 0)
        {
            ImGui.TextColored(new Vector4(.55f, 1f, .6f, 1f), "no declared holes");
            return;
        }
        ImGui.TextColored(new Vector4(1f, .6f, .45f, 1f), $"{holes.Count} declared holes:");
        foreach (string hole in holes)
        {
            ImGui.Bullet();
            ImGui.TextWrapped(hole);
        }
    }

    // ── shared bits ──────────────────────────────────────────────────────────

    private static Vector4 FidelityColor(EncounterFidelity fidelity) => fidelity switch
    {
        EncounterFidelity.ExactDb => new Vector4(.55f, 1f, .62f, 1f),
        EncounterFidelity.DeclaredCppManifest => new Vector4(.65f, .85f, 1f, 1f),
        EncounterFidelity.DerivedDbc => new Vector4(.85f, .85f, .6f, 1f),
        EncounterFidelity.Heuristic => new Vector4(1f, .78f, .45f, 1f),
        _ => new Vector4(1f, .45f, .38f, 1f),
    };
}

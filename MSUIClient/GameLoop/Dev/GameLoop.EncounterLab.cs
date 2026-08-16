using System.Numerics;
using ImGuiNET;
using Silk.NET.Input;
using MSUIClient.Engine;
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
    private bool _encounterLabKeyWasDown;

    private EncounterDataClient? _encounterData;
    private EncounterLibrary? _encounterLibrary;
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

    private enum EncounterPlacement { None, Probe, ProbeWaypoint, Actor, Boss }

    private EncounterDataClient EncounterData => _encounterData ??= new EncounterDataClient(_config.RepoRoot);

    private EncounterLibrary EncounterLibraryRef =>
        _encounterLibrary ??= new EncounterLibrary(Path.Combine(_config.RepoRoot, "encounters"));

    // ── input ────────────────────────────────────────────────────────────────

    /// <summary>Ctrl+E edge toggle, run from UpdateControlInput beside Ctrl+N/Ctrl+F.
    /// Works in live and creator mode — the simulator needs no server at all, which
    /// is the entire point of running it here.</summary>
    private void UpdateEncounterLabInput(bool typing)
    {
        bool ctrl = InputKeyDown(Key.ControlLeft) || InputKeyDown(Key.ControlRight);
        bool pressed = ctrl && InputKeyDown(Key.E);
        if (pressed && !_encounterLabKeyWasDown && !typing) ToggleEncounterLab();
        _encounterLabKeyWasDown = pressed;
    }

    private void ToggleEncounterLab()
    {
        _encounterLabOpen = !_encounterLabOpen;
        if (!_encounterLabOpen) { _encounterPlacing = EncounterPlacement.None; return; }

        EncounterLibraryRef.Reload();
        if (EncounterData.Data is null && !EncounterData.Fetching)
            EncounterData.BeginFetch(Settings.DevWindow.SuiBaseUrl);
    }

    /// <summary>
    /// World-click intercept, called from the click drain ahead of the free-view
    /// router. An armed placement mode owns the click completely — placing a probe
    /// must never also issue an RTS order. No-op when nothing is armed.
    /// </summary>
    private bool HandleEncounterLabClick(WorldMouseClick click)
    {
        if (!_encounterLabOpen || _encounterPlacing == EncounterPlacement.None) return false;
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
        if (!_encounterLabOpen || _encounterSim is not { } sim) return;
        if (!_encounterPlaying || _encounterScrubbing) return;

        float speed = Math.Clamp(Settings.EncounterLab.PlaybackSpeed, 0.05f, 20f);
        _encounterPlaybackCarryMs += dt * 1000.0 * speed;
        int step = Math.Max(sim.Options.StepMs, 1);
        int guard = 0;

        while (_encounterPlaybackCarryMs >= step && guard++ < 400)
        {
            _encounterPlaybackCarryMs -= step;
            if (sim.Finished) { _encounterPlaying = false; break; }
            sim.Advance();
            _encounterViewMs = sim.TimeMs;
            _encounterProbeDirty = true;
        }
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
    }

    private void LoadEncounterDocument(EncounterDefinition definition)
    {
        EnsureEncounterFacts();
        _encounterDefinition = definition;
        _encounterSourceNote = $"authored document ({definition.Key}.json)";
        BuildScenarioFromDefinition();
        RebuildEncounterSim();
    }

    private void BuildScenarioFromDefinition()
    {
        _encounterScenario.Clear();
        if (_encounterDefinition?.Actors is { Count: > 0 } actors)
            _encounterScenario.AddRange(actors);
        else if (_encounterDefinition is { } definition)
            _encounterScenario.Add(new EncounterActorSpec(
                "boss", definition.Name, definition.PrimaryEntry, EncounterActorRole.Boss));

        if (_encounterProbe.Count == 0 && _encounterScenario.Count > 0)
            _encounterProbe.Add(0, _encounterScenario[0].Position + new Vector3(10f, 0f, 0f));
    }

    private void RebuildEncounterSim()
    {
        if (_encounterDefinition is not { } definition) { _encounterSim = null; return; }
        EnsureEncounterFacts();
        var settings = Settings.EncounterLab;
        _encounterSim = new EncounterSim(definition, _encounterScenario, new EncounterSimOptions
        {
            StepMs = Math.Clamp(settings.StepMs, 20, 1000),
            Seed = (uint)Math.Max(settings.Seed, 0),
            RaidDpsFraction = Math.Clamp(settings.RaidDpsFraction, 0f, 0.5f),
        }, _encounterFacts);
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

    private void MoveScenarioActor(string key, Vector3 position)
    {
        for (int i = 0; i < _encounterScenario.Count; i++)
        {
            if (_encounterScenario[i].Key != key) continue;
            _encounterScenario[i] = _encounterScenario[i] with { Position = position };
            RebuildEncounterSim();
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

    private void DrawEncounterLab()
    {
        if (!_encounterLabOpen) return;

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

        if (!_creatorWorldRequested) DrawCreatorPanelTunePopup();
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
        ImGui.TextDisabled(status);

        float avail = ImGui.GetContentRegionAvail().X;
        float buttonW = ImGui.CalcTextSize("Refresh DB").X + 14f * cs;
        ImGui.SameLine(MathF.Max(avail - buttonW, 0f));
        if (ImGui.Button("Refresh DB"))
        {
            data.BeginFetch(Settings.DevWindow.SuiBaseUrl, forceRefresh: true);
            _encounterFacts = null;
        }
        if (data.Data?.Error is { Length: > 0 } error)
            ImGui.TextColored(new Vector4(1f, .55f, .35f, 1f), error);
    }

    // ── subject ──────────────────────────────────────────────────────────────

    private void DrawEncounterSubjectSection()
    {
        if (!ImGui.CollapsingHeader("Encounter", ImGuiTreeNodeFlags.DefaultOpen)) return;

        EncounterLibrary library = EncounterLibraryRef;
        if (ImGui.Button("Reload documents")) library.Reload();
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
            if (ImGui.Button($"Load selected: {name}##enc-selected"))
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
        ImGui.TextDisabled(_encounterSourceNote);
        EncounterFidelity worst = loaded.WorstFidelity();
        ImGui.TextColored(FidelityColor(worst),
            $"weakest fact: {EncounterSchema.Describe(worst)}");
        ImGui.TextDisabled($"coverage: {EncounterSchema.Describe(loaded.Coverage)}");
        ImGui.TextDisabled($"entry {loaded.PrimaryEntry} · {loaded.Phases.Count} phases · " +
                           $"{loaded.Abilities.Count} abilities");
        if (loaded.Provenance.CoreBuildHash is { Length: > 0 } hash)
            ImGui.TextDisabled($"core: {hash}");
        if (loaded.Note is { Length: > 0 } note) ImGui.TextWrapped(note);
    }

    // ── overlays ─────────────────────────────────────────────────────────────

    private void DrawEncounterOverlaySection()
    {
        if (!ImGui.CollapsingHeader("Overlays")) return;
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

        int linger = settings.FootprintLingerMs;
        ImGui.SetNextItemWidth(160f * CreatorUiScale);
        if (ImGui.SliderInt("linger ms", ref linger, 200, 6000))
        { settings.FootprintLingerMs = linger; changed = true; }

        if (changed) SettingsFile?.Save();

        ImGui.Separator();
        ImGui.TextDisabled("colour = fidelity:");
        foreach (EncounterFidelity fidelity in Enum.GetValues<EncounterFidelity>())
        {
            ImGui.TextColored(FidelityColor(fidelity), $"  {EncounterSchema.Describe(fidelity)}");
        }
    }

    // ── transport ────────────────────────────────────────────────────────────

    private void DrawEncounterTransportSection()
    {
        if (!ImGui.CollapsingHeader("Transport", ImGuiTreeNodeFlags.DefaultOpen)) return;
        if (_encounterSim is not { } sim)
        {
            ImGui.TextDisabled("load an encounter to simulate it");
            return;
        }
        var settings = Settings.EncounterLab;
        float cs = CreatorUiScale;

        if (ImGui.Button(_encounterPlaying ? "Pause" : "Play", new Vector2(70f * cs, 0f)))
            _encounterPlaying = !_encounterPlaying;
        ImGui.SameLine();
        if (ImGui.Button("Step"))
        {
            _encounterPlaying = false;
            if (_encounterViewMs < sim.TimeMs) ScrubTo(_encounterViewMs + sim.Options.StepMs);
            else { sim.Advance(); _encounterViewMs = sim.TimeMs; _encounterProbeDirty = true; }
        }
        ImGui.SameLine();
        if (ImGui.Button("Back"))
        {
            _encounterPlaying = false;
            ScrubTo(_encounterViewMs - sim.Options.StepMs);
        }
        ImGui.SameLine();
        if (ImGui.Button("Reset")) { sim.Reset(); _encounterViewMs = 0; _encounterPlaying = false; _encounterProbeDirty = true; }

        // Scrubbing is an index into the snapshot ring, not a re-simulation — the
        // state is small enough to store every step, so rewind is free.
        int headMs = Math.Max(sim.TimeMs, 1);
        int view = _encounterViewMs;
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 90f * cs);
        if (ImGui.SliderInt("##enc-scrub", ref view, 0, headMs, $"{view / 1000f:0.0}s"))
        {
            _encounterScrubbing = true;
            _encounterPlaying = false;
            ScrubTo(view);
        }
        else if (_encounterScrubbing && !ImGui.IsItemActive()) _encounterScrubbing = false;
        ImGui.SameLine();
        ImGui.TextDisabled($"/ {headMs / 1000f:0.0}s");

        ImGui.Text($"phase: {sim.Definition.Phase(sim.PhaseKey)?.Name ?? sim.PhaseKey}");
        if (sim.Boss is { } boss)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"· boss {boss.HealthFraction * 100f:0}%");
        }
        if (sim.Finished) ImGui.TextDisabled("(simulation finished)");

        ImGui.Separator();
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
        ImGui.TextDisabled("heuristic: a dial to reach health-gated phases, not a damage model");
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

    // ── scenario ─────────────────────────────────────────────────────────────

    private void DrawEncounterScenarioSection()
    {
        if (!ImGui.CollapsingHeader("Scenario")) return;
        if (_encounterDefinition is null)
        {
            ImGui.TextDisabled("load an encounter first");
            return;
        }

        if (_encounterPlacing != EncounterPlacement.None)
            ImGui.TextColored(new Vector4(.5f, 1f, .6f, 1f),
                "click the world to place · right-click cancels");

        if (ImGui.Button("Place boss")) _encounterPlacing = EncounterPlacement.Boss;
        ImGui.SameLine();
        if (ImGui.Button("Add dummy"))
        {
            Vector3 near = _encounterScenario.FirstOrDefault()?.Position ?? Vector3.Zero;
            _encounterScenario.Add(new EncounterActorSpec(
                $"dummy{++_encounterDummySerial}", $"dummy {_encounterDummySerial}", 0,
                EncounterActorRole.Friendly, near + new Vector3(5f, 5f, 0f)));
            RebuildEncounterSim();
        }
        ImGui.SameLine();
        if (ImGui.Button("Snap to me") && DevPlayerPosition() is { } me)
        {
            MoveScenarioActor(BossActorKey(), me);
        }

        ImGui.Separator();
        for (int i = 0; i < _encounterScenario.Count; i++)
        {
            EncounterActorSpec actor = _encounterScenario[i];
            ImGui.PushID(i);
            ImGui.Text($"{actor.Name}");
            ImGui.SameLine();
            ImGui.TextDisabled($"({actor.Role})");
            ImGui.TextDisabled($"  {actor.Position.X:0.#}, {actor.Position.Y:0.#}, {actor.Position.Z:0.#} " +
                               $"· r{actor.BoundingRadius:0.#}");
            if (ImGui.SmallButton("place"))
            {
                _encounterPlacing = EncounterPlacement.Actor;
                _encounterPlacingActorKey = actor.Key;
            }
            if (actor.Role != EncounterActorRole.Boss)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("remove"))
                {
                    _encounterScenario.RemoveAt(i);
                    RebuildEncounterSim();
                    ImGui.PopID();
                    break;
                }
            }
            ImGui.PopID();
        }
    }

    // ── timeline ─────────────────────────────────────────────────────────────

    private void DrawEncounterTimelineSection()
    {
        if (!ImGui.CollapsingHeader("Timeline", ImGuiTreeNodeFlags.DefaultOpen)) return;
        if (_encounterSim is not { } sim)
        {
            ImGui.TextDisabled("nothing simulated yet");
            return;
        }

        // A window around the scrub head: the whole event list is unreadable, and
        // what matters is what just happened and what is about to.
        const int windowMs = 12_000;
        List<SimEvent> nearby = sim.Events
            .Where(e => Math.Abs(e.TimeMs - _encounterViewMs) <= windowMs)
            .OrderBy(e => e.TimeMs)
            .ToList();

        if (nearby.Count == 0) { ImGui.TextDisabled("no events near this instant"); return; }

        float cs = CreatorUiScale;
        if (ImGui.BeginChild("##enc-timeline", new Vector2(0f, 180f * cs), true))
        {
            foreach (SimEvent simEvent in nearby)
            {
                bool isNow = Math.Abs(simEvent.TimeMs - _encounterViewMs) <= sim.Options.StepMs;
                Vector4 color = simEvent.Kind switch
                {
                    SimEventKind.Unmodeled => new Vector4(1f, .45f, .35f, 1f),
                    SimEventKind.PhaseEnter => new Vector4(.6f, .85f, 1f, 1f),
                    SimEventKind.CastLand => FidelityColor(simEvent.Fidelity),
                    SimEventKind.Say => new Vector4(.85f, .8f, .55f, 1f),
                    _ => new Vector4(.72f, .72f, .72f, 1f),
                };
                if (!isNow) color.W = .55f;

                string marker = isNow ? "▶" : " ";
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
        if (!ImGui.CollapsingHeader("Position probe", ImGuiTreeNodeFlags.DefaultOpen)) return;

        if (ImGui.Button("Place probe")) _encounterPlacing = EncounterPlacement.Probe;
        ImGui.SameLine();
        if (ImGui.Button("Add waypoint")) _encounterPlacing = EncounterPlacement.ProbeWaypoint;
        ImGui.SameLine();
        if (ImGui.Button("Undo point")) { _encounterProbe.RemoveLast(); _encounterProbeDirty = true; }
        ImGui.SameLine();
        if (ImGui.Button("At me") && DevPlayerPosition() is { } me)
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
                ImGui.TextColored(FidelityColor(worst),
                    $"weakest hitting fact: {EncounterSchema.Describe(worst)}");
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
        if (!ImGui.CollapsingHeader("Abilities")) return;
        if (_encounterDefinition is not { } definition)
        {
            ImGui.TextDisabled("load an encounter first");
            return;
        }

        foreach (EncounterAbility ability in definition.Abilities)
        {
            ImGui.PushID(ability.Key);
            Vector4 color = FidelityColor(ability.Fidelity);
            bool open = ImGui.TreeNodeEx(ability.Name,
                ImGuiTreeNodeFlags.SpanAvailWidth);
            ImGui.SameLine();
            ImGui.TextColored(color, EncounterSchema.Describe(ability.Fidelity));
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
                    ImGui.TextDisabled($"  ← {source}");
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
        if (!ImGui.CollapsingHeader("Coverage & holes")) return;
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

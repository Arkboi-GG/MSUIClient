using System.Numerics;
using ImGuiNET;
using MSUIClient.World.Encounters;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// Encounter Lab — live "action step-through" pop-out.
//
// A second chrome window that FOLLOWS the selected body (a clicked raid puppet in
// the free view, or the boss by default) and shows exactly what it is stepping
// through as the sim plays: Onyxia's cleave / tail sweep / flame breath / spells,
// her phase turns, and what a raid body is hit by — timestamped relative to the
// scrub head, the current step marked ▶, upcoming actions listed ahead. Every row
// is a sim.Event; the fight is pre-simulated, so "what she is about to do" is real,
// not a guess. No new sim instrumentation — this is pure presentation.
// ─────────────────────────────────────────────────────────────────────────────

public sealed partial class GameLoop
{
    private bool _encounterActionPanelOpen;
    private bool _encounterAbilityPanelOpen;

    /// <summary>The sim actor the action panel follows: an RTS/freecam selection first, then
    /// the world-inspect selection, else the boss. Mapped back through the puppet guid table.</summary>
    private string? EncounterFollowedActorKey(EncounterSim sim)
    {
        foreach (ulong guid in _freecamSelection)
            foreach ((string key, ulong g) in _encounterPuppets)
                if (g == guid) return key;
        if (_selectionGuid != 0)
            foreach ((string key, ulong g) in _encounterPuppets)
                if (g == _selectionGuid) return key;
        return sim.Boss?.Key;
    }

    /// <summary>Drawn every frame from the HUD, right after the Lab window. Independent chrome
    /// so it can sit beside the Lab (or anywhere) while the fight plays.</summary>
    private void DrawEncounterActionPanel()
    {
        DrawEncounterAbilityPanel();
        DrawEncounterPlayerSetupModal();
        if (!_encounterActionPanelOpen) return;

        const string tuneId = "encounter-actions";
        _activePanelTune = tuneId;
        float cs = CreatorUiScale;
        float s = MathF.Max(ImGui.GetIO().DisplaySize.Y / GlueCanvasH, 0.5f) * cs;
        ImGui.SetNextWindowPos(new Vector2(510f * s, 64f * s), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(360f * s, 470f * s), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(260f * cs, 200f * cs),
            new Vector2(float.MaxValue, float.MaxValue));

        PushCreatorStyle();
        if (!ImGui.Begin("###encounter-actions", CreatorChromeFlags))
        {
            ImGui.End();
            PopCreatorStyle();
            _activePanelTune = null;
            return;
        }
        ClampCreatorWindowOnScreen();
        if (DrawCreatorPanelChrome("Action Timeline", tuneId))
            _encounterActionPanelOpen = false;
        ImGui.SetWindowFontScale(CreatorTextScale);
        BeginCreatorContent();

        DrawEncounterActionPanelBody();

        EndCreatorContent();
        ImGui.SetWindowFontScale(1f);
        ImGui.End();
        PopCreatorStyle();
        _activePanelTune = null;
    }

    private void DrawEncounterActionPanelBody()
    {
        if (_encounterSim is not { } sim)
        {
            ImGui.TextDisabled("open an encounter in the Lab (Ctrl+E) and play it");
            return;
        }

        string? key = EncounterFollowedActorKey(sim);
        SimActor? actor = key is null ? null : sim.Actors.FirstOrDefault(a => a.Key == key);
        if (actor is null || key is null)
        {
            ImGui.TextDisabled("no target — click a body in the free view (Ctrl+F)");
            return;
        }

        // Who we follow, and how to change it.
        ImGui.TextColored(RoleColourVec4(actor.Spec.Role, actor.Spec.Job), actor.Spec.Name);
        ImGui.SameLine();
        ImGui.TextDisabled(actor.Spec.Job == RaidJob.None ? $"({actor.Spec.Role})" : $"({actor.Spec.Job})");
        ImGui.TextDisabled("follows your selection — click a body in Ctrl+F to switch");

        // Clock, phase, and (boss) a health bar.
        ImGui.Text($"{EncounterFightClock(sim)}  ·  {sim.Definition.Phase(sim.PhaseKey)?.Name ?? sim.PhaseKey}");
        if (actor.Spec.Role == EncounterActorRole.Boss)
        {
            float hp = Math.Clamp(actor.HealthFraction, 0f, 1f);
            Vector4 bar = hp > .5f ? new Vector4(.85f, .30f, .28f, 1f)
                        : hp > .2f ? new Vector4(.90f, .55f, .20f, 1f)
                                   : new Vector4(.95f, .80f, .20f, 1f);
            ImGui.PushStyleColor(ImGuiCol.PlotHistogram, bar);
            ImGui.ProgressBar(hp, new Vector2(-1f, 14f * CreatorUiScale), $"{hp * 100f:0}%");
            ImGui.PopStyleColor();
        }

        if (EncounterPanelButtonSized("Visualize phase abilities",
                new Vector2(ImGui.GetContentRegionAvail().X, 0f)))
            _encounterAbilityPanelOpen = true;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Open the separate phase-aware Ability Visualizer.");

        // "NOW": the latest thing this body was doing at/just-before the view instant.
        SimEvent? now = null;
        foreach (SimEvent e in sim.Events)
        {
            if (e.TimeMs > _encounterViewMs) break;
            if (e.ActorKey == key && e.Kind is SimEventKind.CastStart or SimEventKind.PhaseEnter
                    or SimEventKind.Move or SimEventKind.Say or SimEventKind.Summon or SimEventKind.Aggro)
                now = e;
        }
        ImGui.Separator();
        if (now is not null)
            ImGui.TextColored(new Vector4(1f, .85f, .35f, 1f), $"NOW   {now.Text}");
        else
            ImGui.TextDisabled(sim.EngagedAtMs < 0 || _encounterViewMs < sim.EngagedAtMs
                ? "NOW   waiting for the pull" : "NOW   —");

        // The stepped stream: everything this body DOES (and is hit BY), windowed around the
        // scrub head, the current step marked ▶, upcoming actions below with a lead time.
        const int windowMs = 20_000;
        List<SimEvent> stream = sim.Events
            .Where(e => (e.ActorKey == key || e.TargetKey == key) &&
                        Math.Abs(e.TimeMs - _encounterViewMs) <= windowMs)
            .OrderBy(e => e.TimeMs)
            .ToList();

        ImGui.Separator();
        if (stream.Count == 0)
        {
            ImGui.TextDisabled(sim.EngagedAtMs < 0
                ? "nothing yet — order a body into her ring to start the fight"
                : "nothing near this instant");
            return;
        }

        if (ImGui.BeginChild("##enc-actionstream", new Vector2(0f, 0f), true))
        {
            foreach (SimEvent e in stream)
            {
                bool isNow = Math.Abs(e.TimeMs - _encounterViewMs) <= sim.Options.StepMs;
                bool future = e.TimeMs > _encounterViewMs;
                Vector4 color = e.Kind switch
                {
                    SimEventKind.CastStart => new Vector4(1f, .80f, .35f, 1f),
                    SimEventKind.CastLand => FidelityColor(e.Fidelity),
                    SimEventKind.ActorHit => new Vector4(1f, .40f, .35f, 1f),
                    SimEventKind.PhaseEnter => new Vector4(.6f, .85f, 1f, 1f),
                    SimEventKind.Aggro => new Vector4(1f, .50f, .40f, 1f),
                    SimEventKind.Say => new Vector4(.85f, .80f, .55f, 1f),
                    SimEventKind.Death => new Vector4(.80f, .80f, .80f, 1f),
                    _ => new Vector4(.72f, .72f, .72f, 1f),
                };
                if (!isNow) color.W = future ? .85f : .5f;

                string marker = isNow ? "▶" : future ? "·" : " ";
                string rel = e.TimeMs >= _encounterViewMs
                    ? $"+{(e.TimeMs - _encounterViewMs) / 1000f:0.0}"
                    : $"-{(_encounterViewMs - e.TimeMs) / 1000f:0.0}";
                ImGui.TextColored(color, $"{marker} {rel,6}s  {e.Text}");
                if (e.Kind == SimEventKind.CastLand && e.Fidelity != EncounterFidelity.ExactDb &&
                    ImGui.IsItemHovered())
                    ImGui.SetTooltip(EncounterSchema.Describe(e.Fidelity));
            }
        }
        ImGui.EndChild();
    }

    /// <summary>A dedicated, non-blocking pop-out so the owner can keep manipulating the
    /// world while an ability is visualized. The list is recomputed from the scrubbed phase
    /// every frame: an out-of-phase ability never gets a misleading control.</summary>
    private void DrawEncounterAbilityPanel()
    {
        if (!_encounterAbilityPanelOpen) return;

        const string tuneId = "encounter-ability-visualizer";
        _activePanelTune = tuneId;
        float cs = CreatorUiScale;
        float s = MathF.Max(ImGui.GetIO().DisplaySize.Y / GlueCanvasH, 0.5f) * cs;
        ImGui.SetNextWindowPos(new Vector2(880f * s, 64f * s), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(430f * s, 510f * s), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(330f * cs, 240f * cs),
            new Vector2(float.MaxValue, float.MaxValue));

        PushCreatorStyle();
        if (!ImGui.Begin("###encounter-ability-visualizer", CreatorChromeFlags))
        {
            ImGui.End();
            PopCreatorStyle();
            _activePanelTune = null;
            return;
        }
        ClampCreatorWindowOnScreen();
        if (DrawCreatorPanelChrome("Ability Visualizer", tuneId))
            _encounterAbilityPanelOpen = false;
        ImGui.SetWindowFontScale(CreatorTextScale);
        BeginCreatorContent();

        DrawEncounterAbilityPanelBody();

        EndCreatorContent();
        ImGui.SetWindowFontScale(1f);
        ImGui.End();
        PopCreatorStyle();
        _activePanelTune = null;
    }

    private void DrawEncounterAbilityPanelBody()
    {
        if (_encounterSim is not { } sim || _encounterDefinition is not { } definition ||
            sim.Boss is null)
        {
            ImGui.TextDisabled("open an encounter in the Lab (Ctrl+E)");
            return;
        }

        List<EncounterAbility> abilities = definition.AbilitiesIn(sim.PhaseKey)
            .ToList();
        EncounterPhase? phase = definition.Phase(sim.PhaseKey);
        int onCount = abilities.Count(a => _encounterVisualizedAbilities.Contains(a.Key));

        ImGui.TextColored(new Vector4(1f, .82f, .28f, 1f), definition.Name);
        ImGui.Text($"{phase?.Name ?? sim.PhaseKey}  ·  {abilities.Count} available");
        ImGui.TextDisabled("Only mechanics usable in the current phase are shown.");
        ImGui.TextDisabled("Choose a mechanic to toggle its world overlay.");

        // Permanent summary row: changing 0 -> 1 visible must never push the ability
        // table (and every button in it) down between clicks.
        if (EncounterPanelButton($"Clear phase ({onCount})", enabled: onCount > 0))
            foreach (EncounterAbility ability in abilities)
                _encounterVisualizedAbilities.Remove(ability.Key);
        float summaryWidth = MathF.Max(ImGui.CalcTextSize("0 visible").X,
            ImGui.CalcTextSize($"{abilities.Count} visible").X);
        EncounterSameLineIfFits(summaryWidth);
        if (onCount > 0)
            ImGui.TextColored(new Vector4(.55f, 1f, .62f, 1f), $"{onCount} visible");
        else
            ImGui.TextDisabled("0 visible");

        ImGui.Separator();
        if (abilities.Count == 0)
        {
            ImGui.TextDisabled("No boss mechanics are authored for this phase.");
            return;
        }

        if (ImGui.BeginChild("##enc-ability-rows", new Vector2(0f, 0f), true))
        {
            float cs = CreatorUiScale;
            float helpSize = MathF.Max(ImGui.GetTextLineHeight(), 18f * cs);
            float captionWidth = MathF.Max(
                ImGui.CalcTextSize("Show").X, ImGui.CalcTextSize("Hide").X) + 28f * cs;
            float preferredButtonWidth = MathF.Max(88f * cs * CreatorButtonMul,
                captionWidth);
            float maxButtonWidth = MathF.Max(captionWidth,
                ImGui.GetContentRegionAvail().X * .42f);
            float buttonWidth = MathF.Min(preferredButtonWidth, maxButtonWidth);
            float rowHeight = MathF.Max(CreatorButtonHeight, helpSize) +
                              ImGui.GetStyle().ItemSpacing.Y;
            if (ImGui.BeginTable("##enc-ability-table", 3,
                    ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH |
                    ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.NoSavedSettings))
            {
                ImGui.TableSetupColumn("Mechanic", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Details", ImGuiTableColumnFlags.WidthFixed,
                    helpSize + 4f * cs);
                ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed,
                    buttonWidth + 4f * cs);
                foreach (EncounterAbility ability in abilities)
                {
                    bool visualized = _encounterVisualizedAbilities.Contains(ability.Key);
                    Vector4 colour = FidelityColor(ability.Fidelity);
                    if (!visualized) colour.W = .78f;

                    ImGui.PushID(ability.Key);
                    ImGui.TableNextRow(ImGuiTableRowFlags.None, rowHeight);
                    ImGui.TableNextColumn();
                    ImGui.PushStyleColor(ImGuiCol.Text, colour);
                    ImGui.TextWrapped($"{(visualized ? "[ON]" : "[  ]")} {ability.Name}");
                    ImGui.PopStyleColor();

                    ImGui.TableNextColumn();
                    DrawEncounterAbilityHelp(ability, helpSize);

                    ImGui.TableNextColumn();
                    string caption = visualized ? "Hide" : "Show";
                    if (EncounterPanelButtonSized(
                            $"{caption}##ability-toggle-{ability.Key}",
                            new Vector2(buttonWidth, CreatorButtonHeight)))
                    {
                        if (visualized) _encounterVisualizedAbilities.Remove(ability.Key);
                        else _encounterVisualizedAbilities.Add(ability.Key);
                    }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(visualized
                            ? $"Stop visualizing {ability.Name}."
                            : $"Visualize {ability.Name} in the world.");
                    ImGui.PopID();
                }
                ImGui.EndTable();
            }
        }
        ImGui.EndChild();
    }

    /// <summary>Route "visualize the boss's abilities" to the right surface: the
    /// workspace deck's Abil section when the docked layout is up, the floating
    /// pop-out window otherwise.</summary>
    private void OpenEncounterAbilityVisualizer()
    {
        if (CreatorWorkspaceActive)
        {
            _workspaceEncSection = "abilities";
            _workspaceSubTabSel["abilities"] = 0;
            _encounterPlayerSetupKey = null;   // reclaim the deck if the customizer holds it
        }
        else _encounterAbilityPanelOpen = true;
    }

    /// <summary>The ability visualizer as a card GRID filling the workspace deck:
    /// every current-phase mechanic side by side with its fidelity colour,
    /// provenance help and Show/Hide toggle. Same `_encounterVisualizedAbilities`
    /// state as the floating pop-out, so either surface flips the same overlays.</summary>
    private void DrawEncounterAbilityDeckGrid()
    {
        if (_encounterSim is not { } sim || _encounterDefinition is not { } definition ||
            sim.Boss is null)
        {
            ImGui.TextDisabled("load an encounter first (Fight section)");
            return;
        }

        List<EncounterAbility> abilities = definition.AbilitiesIn(sim.PhaseKey).ToList();
        EncounterPhase? phase = definition.Phase(sim.PhaseKey);
        int onCount = abilities.Count(a => _encounterVisualizedAbilities.Contains(a.Key));
        float cs = CreatorUiScale;

        // One summary row; the grid gets the rest of the deck.
        ImGui.TextColored(new Vector4(1f, .82f, .28f, 1f), definition.Name);
        ImGui.SameLine();
        ImGui.Text($"· {phase?.Name ?? sim.PhaseKey} · {abilities.Count} available ·");
        ImGui.SameLine();
        if (onCount > 0)
            ImGui.TextColored(new Vector4(.55f, 1f, .62f, 1f), $"{onCount} visible");
        else ImGui.TextDisabled("0 visible");
        EncounterSameLineForButton($"Clear phase ({onCount})", compact: true);
        if (EncounterPanelButton($"Clear phase ({onCount})", enabled: onCount > 0, compact: true))
            foreach (EncounterAbility ability in abilities)
                _encounterVisualizedAbilities.Remove(ability.Key);

        if (abilities.Count == 0)
        {
            ImGui.TextDisabled("No boss mechanics are authored for this phase.");
            return;
        }

        float cardW = 240f * cs;
        float cardH = ImGui.GetTextLineHeightWithSpacing() * 2.1f +
                      CreatorButtonHeight + 22f * cs;
        float spacing = ImGui.GetStyle().ItemSpacing.X;
        int perRow = Math.Max(1,
            (int)((ImGui.GetContentRegionAvail().X - 4f) / (cardW + spacing)));
        // No font scale on this child: it inherits the deck child's; the cards
        // re-assert it themselves (grandchildren do not inherit).
        ImGui.BeginChild("##ability-grid", new Vector2(0f, 0f));
        int drawn = 0;
        foreach (EncounterAbility ability in abilities)
        {
            if (drawn++ % perRow != 0) ImGui.SameLine();
            bool visualized = _encounterVisualizedAbilities.Contains(ability.Key);
            Vector4 colour = FidelityColor(ability.Fidelity);
            if (!visualized) colour.W = .82f;

            ImGui.PushID(ability.Key);
            BeginEncounterDeckCard($"##ab-card-{ability.Key}", cardW, cardH);
            ImGui.PushStyleColor(ImGuiCol.Text, colour);
            ImGui.TextWrapped($"{(visualized ? "[ON] " : "")}{ability.Name}");
            ImGui.PopStyleColor();

            float helpSize = MathF.Max(ImGui.GetTextLineHeight(), 18f * cs);
            ImGui.SetCursorPosY(cardH - CreatorButtonHeight - 10f * cs);
            DrawEncounterAbilityHelp(ability, helpSize);
            ImGui.SameLine();
            string caption = visualized ? "Hide" : "Show";
            if (EncounterPanelButtonSized($"{caption}##ability-toggle-{ability.Key}",
                    new Vector2(ImGui.GetContentRegionAvail().X, CreatorButtonHeight)))
            {
                if (visualized) _encounterVisualizedAbilities.Remove(ability.Key);
                else _encounterVisualizedAbilities.Add(ability.Key);
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(visualized
                    ? $"Stop visualizing {ability.Name}."
                    : $"Visualize {ability.Name} in the world.");
            EndEncounterDeckCard();
            ImGui.PopID();
        }
        ImGui.EndChild();
    }

    /// <summary>A compact, deliberately non-action help affordance. Technical provenance stays
    /// available for encounter authors without competing with the mechanic name and toggle.</summary>
    private void DrawEncounterAbilityHelp(EncounterAbility ability, float size)
    {
        Vector2 pos = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton($"##ability-help-{ability.Key}", new Vector2(size, size));
        bool hovered = ImGui.IsItemHovered();
        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        Vector2 centre = pos + new Vector2(size * .5f, size * .5f);
        uint colour = hovered ? 0xffffffff : VanillaGold;
        draw.AddCircle(centre, size * .36f, colour, 18, MathF.Max(1f, size * .08f));
        Vector2 mark = ImGui.CalcTextSize("?");
        draw.AddText(centre - mark * .5f, colour, "?");

        if (!hovered) return;
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(360f * CreatorUiScale);
        ImGui.TextUnformatted(AbilityVisualizationDetails(ability));
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    private static string AbilityVisualizationDetails(EncounterAbility ability)
    {
        string visualization = ability.Geometry.Kind switch
        {
            FootprintKind.None when ability.Steps?.Any(s =>
                s.Kind == EncounterStepKind.Summon && s.Point != default) == true =>
                "Authored summon locations are marked in the world.",
            FootprintKind.None =>
                "This boss mechanic has no spatial shape modeled; Visualize shows an anchored callout.",
            _ => $"World shape: {GeometryLabel(ability.Geometry.Kind)}.",
        };
        return $"{visualization}\n\nDefinition source: {EncounterSchema.Describe(ability.Fidelity)}";
    }

    private static string GeometryLabel(FootprintKind kind) => kind switch
    {
        FootprintKind.Cone => "cone",
        FootprintKind.Line => "line",
        FootprintKind.PointChain => "lanes",
        FootprintKind.Circle => "circle",
        FootprintKind.Projectile => "bolt",
        _ => "shape",
    };

    /// <summary>The role/job plan colour as a Vector4 (the overlay's EncounterRoleStyle returns
    /// a packed uint; this mirrors its palette for ImGui text).</summary>
    private static Vector4 RoleColourVec4(EncounterActorRole role, RaidJob job)
    {
        if (role == EncounterActorRole.Boss) return new Vector4(1f, .30f, .26f, 1f);
        return job switch
        {
            RaidJob.Tank => new Vector4(1f, .82f, .22f, 1f),
            RaidJob.Healer => new Vector4(.38f, .92f, .45f, 1f),
            RaidJob.Melee or RaidJob.Ranged => new Vector4(1f, .95f, .80f, 1f),
            _ => new Vector4(.6f, .85f, 1f, 1f),
        };
    }
}

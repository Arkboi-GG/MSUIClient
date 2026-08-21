using System.Numerics;
using ImGuiNET;
using MSUIClient.World.Encounters;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// Encounter Lab — the Game Plan tab (read-only, first slice).
//
// The other customizer tabs expose PORTABLE, low-level primitives (a rotation, a
// priority list, a positioning script) and leave the owner to compile "what I want
// in Phase 2" down onto them by hand. This tab inverts that: it reads the LOADED
// encounter and lets the fight lay itself out — one column per phase, each listing
// the hazards active in that phase straight from the EncounterDefinition, plus what
// THIS body's current plan already does when that phase turns.
//
// This first slice is deliberately READ-ONLY. It adds no new fields to CombatPlan
// or PositioningScript: it is the friendly front door onto the same data the
// Rotation / Advanced tabs edit. Per-phase intent EDITING (per-phase
// enemy priority, "avoid this hazard", mechanic-triggered spots) is the next slice
// and is called out honestly in the header rather than faked here.
// ─────────────────────────────────────────────────────────────────────────────

public sealed partial class GameLoop
{
    private void DrawEncounterPlanGamePlan(int actorIndex, EncounterActorSpec actor)
    {
        if (_encounterDefinition is not { } def || def.Phases.Count == 0)
        {
            EncounterPlayerSetupDisabledWrapped(
                "Load an encounter to see it laid out phase by phase here.");
            return;
        }

        ImGui.TextColored(new Vector4(.45f, .9f, 1f, 1f),
            $"{def.Name} — the fight phase by phase, read for this {JobWord(actor.Job)}.");
        ImGui.TextDisabled("Auto-detected from the encounter. Targets and avoidance set here EXECUTE " +
            "in the sim — dodges run, keep-clears hold, adds bite back. Everything unset is " +
            "derived by the raid doctrine; authored overrides live in Advanced.");
        ImGui.Spacing();

        // Resolve, once, what this body's CURRENT plan and positioning say — the
        // same resolution the sim uses, so the columns show live truth, not a guess.
        CombatPlan? plan = _encounterPlayerPlanDraft is { } pd &&
            string.Equals(_encounterPlayerPlanDraftKey, actor.Key, StringComparison.Ordinal)
                ? pd : actor.PlayerRules?.Plan;
        PositioningScript? positioning = ResolveGamePlanPositioning(actor);

        // The three owner decisions the doctrine cannot derive, right at the top:
        // what this body is FOR, which macro group it fights in, what it presses.
        DrawEncounterSlotIdentity(actorIndex, actor);
        ImGui.SameLine();
        DrawEncounterRotationAssignCombo(actorIndex, actor);

        // Multi-select: with more friendlies selected in the free view, every
        // assignment made here (role, group, rotation, targets, avoidance) can
        // broadcast to the whole selection instead of just this body.
        int selectedFriendlies = GamePlanSelectionIndices(actorIndex).Count;
        if (selectedFriendlies > 1)
        {
            ImGui.SameLine();
            ImGui.Checkbox($"apply to selection ({selectedFriendlies})##gp-multi",
                ref _gamePlanApplySelection);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Broadcast every change made on this tab to all " +
                                 "selected friendly bodies (Ctrl+F marquee selection).");
        }
        else _gamePlanApplySelection = false;

        DrawGamePlanExportImport(def);
        if (plan is not null)
            foreach (string warning in EncounterCombatPlanWarnings(plan))
                ImGui.TextColored(new Vector4(1f, .48f, .3f, 1f), $"! {warning}");
        ImGui.Spacing();

        float cs = CreatorUiScale;
        string livePhase = _encounterSim?.PhaseKey ?? "";

        ImGui.BeginChild("##gameplan-cols", new Vector2(0f, 0f), false,
            ImGuiWindowFlags.HorizontalScrollbar);
        // Split the FULL deck width across the N phases so nothing is squished and no
        // width is left wasted. Only a boss with too many phases to sit at a readable
        // width hits the floor and lets the row scroll horizontally instead.
        int phaseCount = def.Phases.Count;
        float spacing = ImGui.GetStyle().ItemSpacing.X;
        float avail = ImGui.GetContentRegionAvail().X;
        float cardW = MathF.Max(
            MathF.Floor((avail - spacing * (phaseCount - 1)) / phaseCount),
            240f * cs);
        for (int p = 0; p < phaseCount; p++)
        {
            EncounterPhase phase = def.Phases[p];
            if (p > 0) ImGui.SameLine();
            BeginEncounterDeckCard($"##gp-{phase.Key}", cardW);
            DrawGamePlanPhaseCard(actorIndex, def, phase, actor, plan, positioning,
                isLive: string.Equals(phase.Key, livePhase, StringComparison.Ordinal));
            EndEncounterDeckCard();
        }
        ImGui.EndChild();
    }

    private bool _gamePlanApplySelection;

    /// <summary>The scenario indices of every selected friendly puppet plus the body
    /// being customized (always first, never duplicated).</summary>
    private List<int> GamePlanSelectionIndices(int actorIndex)
    {
        List<int> indices = [actorIndex];
        foreach (ulong guid in _freecamSelection)
        {
            if (EncounterRaidPuppetKey(guid) is not { } key) continue;
            int i = _encounterScenario.FindIndex(a => a.Key == key);
            if (i >= 0 && i != actorIndex &&
                _encounterScenario[i].Role == EncounterActorRole.Friendly)
                indices.Add(i);
        }
        return indices;
    }

    /// <summary>Where an assignment lands: just this body, or the whole selection
    /// when the broadcast box is ticked.</summary>
    private List<int> GamePlanTargetIndices(int actorIndex) =>
        _gamePlanApplySelection ? GamePlanSelectionIndices(actorIndex) : [actorIndex];

    /// <summary>Export/import the raid plan document (PLAN_19 M-A): the doctrine,
    /// every friendly's encounter rules, and the referenced rotations, one file.</summary>
    private void DrawGamePlanExportImport(EncounterDefinition def)
    {
        string path = Path.Combine(_config.RepoRoot, "raid-plans", $"{def.Key}.json");
        if (EncounterPanelButton("Export plan", compact: true))
        {
            RaidPlanDocument document = RaidPlanFile.Build(
                $"{def.Name} raid plan", def.Key, _encounterDoctrine,
                _encounterScenario, EncounterCombatPlanStoreRef.CharacterPlans);
            AddChatMessage(RaidPlanFile.Save(document, path, out string? saveError)
                ? $"Raid plan exported: {path} ({document.Bodies.Count} bodies)."
                : $"Raid plan export FAILED: {saveError}.");
        }
        ImGui.SameLine();
        if (EncounterPanelButton("Import plan", compact: true))
        {
            RaidPlanDocument? document = RaidPlanFile.Load(path, out string? loadError);
            if (document is null)
                AddChatMessage($"Raid plan import failed: {loadError}.");
            else
            {
                _encounterDoctrine = document.Doctrine;
                int applied = RaidPlanFile.Apply(document, _encounterScenario,
                    (id, plan) => EncounterCombatPlanStoreRef.UpsertLibrary(
                        plan with { Id = id }, out _),
                    out List<string> missing);
                InvalidateEncounterPlayerPlanDraft();
                RebuildEncounterSimKeepingView();
                AddChatMessage($"Raid plan imported: {applied} bodies applied" +
                    (missing.Count > 0 ? $", {missing.Count} keys not in scenario." : "."));
            }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(path);
    }

    /// <summary>The body's positioning for the read-out: its live draft when this is the
    /// body being authored, otherwise its assigned library script. Null falls through to
    /// the movement doctrine every phase, which the card labels as such.</summary>
    private PositioningScript? ResolveGamePlanPositioning(EncounterActorSpec actor)
    {
        if (_encounterPositioningDraft is { } draft &&
            string.Equals(_encounterPositioningDraftKey, actor.Key, StringComparison.Ordinal))
            return draft;
        if (actor.PlayerRules?.PositioningId is { Length: > 0 } id)
            return EncounterPositioningStoreRef.Find(id);
        return null;
    }

    private void DrawGamePlanPhaseCard(int actorIndex, EncounterDefinition def, EncounterPhase phase,
        EncounterActorSpec actor, CombatPlan? plan, PositioningScript? positioning, bool isLive)
    {
        ImGui.TextColored(new Vector4(1f, .82f, .28f, 1f), phase.Name);
        if (isLive)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(.55f, 1f, .6f, 1f), "NOW");
        }
        ImGui.TextDisabled(GamePlanPhaseFacts(phase));
        ImGui.TextDisabled(GamePlanTransitionLine(phase));

        ImGui.SeparatorText("This body");
        // The existing per-role playbook directive, if any — the closest thing to
        // per-phase intent that ships today.
        RaidPhaseDirective? directive = PlaybookDirectiveFor(phase.Key, actor.Job);
        if (directive is { } dir)
            ImGui.TextUnformatted($"Role order: {GamePlanDirectiveWord(dir.Kind)}");

        // Where I stand this phase: the assigned/draft positioning script when one
        // speaks, else the derived formation station (the doctrine default).
        PositioningPhaseStep? step = positioning?.Step(phase.Key);
        string flank = actor.Side switch
        {
            RaidSide.Left => "group 1 (left)",
            RaidSide.Right => "group 2 (right)",
            RaidSide.Center => "center",
            _ => "auto-split group",
        };
        string stand = step?.Kind switch
        {
            RaidDirectiveKind.Hold => "hold my ground",
            RaidDirectiveKind.ChaseBoss => "keep reach on the boss",
            RaidDirectiveKind.MoveToSpot => "run to a placed spot",
            _ => Settings.EncounterLab.RaidDoctrine
                ? $"derived formation — {flank}"
                : "follow movement doctrine",
        };
        ImGui.TextUnformatted($"Stand: {stand}");
        if (plan?.Movement?.FacePrimaryEnemy == true ||
            actor.PlayerRules?.AlwaysFaceBoss == true)
            ImGui.TextDisabled("Facing: always face the boss");

        DrawGamePlanPhaseTargets(actorIndex, actor, phase, plan);

        // The live read-out (salvaged from the old Explain tab): what this body is
        // actually resolving at the scrub head, shown only on the phase that is NOW.
        if (isLive && _encounterSim?.Actors
                .FirstOrDefault(c => c.Key == actor.Key) is { } live)
            ImGui.TextDisabled(
                $"now: enemy {EncounterIntentName(live.CurrentEnemyTargetKey, "none")} · " +
                $"heal {EncounterIntentName(live.CurrentProtectTargetKey, "none")}" +
                (live.MoveTarget is not null ? " · moving on orders" : ""));

        ImGui.SeparatorText("Hazards this phase");
        int shown = 0;
        foreach (EncounterAbility ability in def.Abilities)
        {
            // Only SCHEDULED mechanics: Manual entries are catalogued variants (the eight
            // breath lanes, heated ground, the pull-time aggro sweep) that the sim never
            // fires on its own — listing them all would bury the real hazard set.
            if (ability.Trigger.Kind == EncounterTriggerKind.Manual) continue;
            if (!ability.ActiveIn(phase.Key)) continue;
            if (!GamePlanIsHazard(ability)) continue;
            DrawGamePlanHazardRow(actorIndex, actor, ability);
            shown++;
        }
        if (shown == 0)
            ImGui.TextDisabled("No scheduled hazards — straight tank-and-spank.");
    }

    /// <summary>How a body can RESPOND to a hazard, decided from structure alone.
    /// Telegraphed shapes (a real cast bar, not a tracked missile) can be dodged;
    /// instant cones can be held clear of continuously; everything else is either
    /// unavoidable or handled by targeting, and says so instead of pretending.</summary>
    private static bool GamePlanHazardAvoidable(EncounterAbility ability) =>
        ability.HasFootprint &&
        ability.Geometry.Kind != FootprintKind.Projectile &&
        (ability.CastTimeMs > 0 || ability.Geometry.Kind == FootprintKind.Cone);

    private void SetGamePlanAvoid(int actorIndex, EncounterActorSpec actor,
        EncounterAbility ability, bool on)
    {
        List<int> indices = GamePlanTargetIndices(actorIndex);
        foreach (int i in indices)
        {
            EncounterActorSpec subject = _encounterScenario[i];
            EncounterPlayerRules rules = subject.PlayerRules ?? new EncounterPlayerRules();
            List<string> keys = (rules.AvoidAbilityKeys ?? [])
                .Where(k => !string.Equals(k, ability.Key, StringComparison.Ordinal)).ToList();
            if (on) keys.Add(ability.Key);
            keys.Sort(StringComparer.Ordinal);
            _encounterScenario[i] = subject with
            {
                PlayerRules = rules with { AvoidAbilityKeys = keys.Count > 0 ? keys : null },
            };
        }
        RebuildEncounterSimKeepingView();
        string who = indices.Count > 1 ? $"{indices.Count} bodies" : actor.Name;
        AddChatMessage(on
            ? $"{who}: will stay out of {ability.Name}."
            : $"{who}: stops avoiding {ability.Name}.");
    }

    /// <summary>A mechanic worth a raider's attention: it puts a shape on the ground or
    /// spawns adds. Pure buffs and script-only beats are left out of the hazard list.</summary>
    private static bool GamePlanIsHazard(EncounterAbility ability) =>
        ability.HasFootprint ||
        ability.Steps?.Any(s => s.Kind == EncounterStepKind.Summon) == true;

    private void DrawGamePlanHazardRow(int actorIndex, EncounterActorSpec actor,
        EncounterAbility ability)
    {
        bool warn = ability.Steps?.Any(s => s.Kind == EncounterStepKind.Unmodeled) == true;

        // The avoid switch is the row's verb: checking it makes the sim EXECUTE the
        // response — a run off a telegraph, a continuous slide out of an instant arc.
        if (GamePlanHazardAvoidable(ability))
        {
            bool avoiding = actor.PlayerRules?.AvoidAbilityKeys?
                .Contains(ability.Key, StringComparer.Ordinal) == true;
            if (ImGui.Checkbox($"##avoid-{ability.Key}", ref avoiding))
                SetGamePlanAvoid(actorIndex, actor, ability, avoiding);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(ability.CastTimeMs > 0
                    ? "Dodge it: run to a safe point during the cast, run back after."
                    : "Keep clear: continuously sidestep out of the arc as she turns.");
            ImGui.SameLine(0f, 6f);
        }

        // Group the rest of the row so hovering anywhere on it — name, shape, or
        // hint — raises the one authored tooltip.
        ImGui.BeginGroup();
        if (warn)
        {
            ImGui.TextColored(new Vector4(1f, .72f, .3f, 1f), "!");
            ImGui.SameLine(0f, 4f);
        }
        ImGui.TextUnformatted(ability.Name);
        ImGui.SameLine();
        ImGui.TextDisabled($"— {GamePlanHazardShape(ability)}");

        string? hint = GamePlanRoleHint(actor.Job, ability);
        if (hint is not null)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(.6f, .85f, 1f, 1f), $"({hint})");
        }
        ImGui.EndGroup();

        if (ImGui.IsItemHovered()) GamePlanHazardTooltip(ability, warn);
    }

    private void GamePlanHazardTooltip(EncounterAbility ability, bool warn)
    {
        ImGui.BeginTooltip();
        ImGui.TextColored(new Vector4(1f, .82f, .28f, 1f), ability.Name);
        ImGui.TextDisabled($"{GamePlanHazardShape(ability)} · {GamePlanTimingWord(ability.Timing)}");
        if (warn)
            ImGui.TextColored(new Vector4(1f, .72f, .3f, 1f),
                "Part of this is unmodeled — see the note.");
        if (!string.IsNullOrEmpty(ability.Note))
        {
            ImGui.PushTextWrapPos(360f);
            ImGui.TextColored(new Vector4(1f, .93f, .35f, 1f), ability.Note);
            ImGui.PopTextWrapPos();
        }
        ImGui.EndTooltip();
    }

    // ── per-phase enemy targeting (the first editable slice) ──────────────────────
    // Small, honest presets over the same three portable buckets the Advanced tab
    // edits. Choosing one writes a PhaseTargetOverride onto the body's ENCOUNTER rules
    // (never the portable plan); the sim's ResolveEnemyIntent reads it for the matching
    // phase. "Default" clears the override so the plan's own order applies again.

    private static readonly (string Label, CombatEnemyKind[]? Order)[] GamePlanTargetPresets =
    [
        ("Default (plan order)", null),
        ("Boss, then adds", [CombatEnemyKind.PrimaryEnemy, CombatEnemyKind.AnyAdd]),
        ("Adds, then boss", [CombatEnemyKind.AnyAdd, CombatEnemyKind.PrimaryEnemy]),
        ("Adds only", [CombatEnemyKind.AnyAdd]),
        ("Current target", [CombatEnemyKind.CurrentEnemy]),
    ];

    private void DrawGamePlanPhaseTargets(int actorIndex, EncounterActorSpec actor,
        EncounterPhase phase, CombatPlan? plan)
    {
        PhaseTargetOverride? ov = actor.PlayerRules?.PhaseTargets?
            .FirstOrDefault(t => string.Equals(t.PhaseKey, phase.Key, StringComparison.Ordinal));
        int current = ov is null ? 0 : GamePlanMatchPreset(ov.Priorities);

        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        if (ImGui.BeginCombo($"##targets-{phase.Key}",
                $"Targets: {GamePlanTargetPresets[current].Label}"))
        {
            for (int i = 0; i < GamePlanTargetPresets.Length; i++)
            {
                bool selected = i == current;
                if (ImGui.Selectable(GamePlanTargetPresets[i].Label, selected))
                {
                    CombatEnemyKind[]? presetOrder = GamePlanTargetPresets[i].Order;
                    SetGamePlanPhaseTarget(actorIndex, actor, phase.Key,
                        presetOrder?.Select(k => new CombatEnemyPriority(k)).ToArray());
                }
                if (selected) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        // Show the order this phase actually resolves to (override or plan default), so
        // the picked preset reads as an outcome and an override is visibly distinct.
        IReadOnlyList<CombatEnemyPriority>? effective = ov?.Priorities ?? plan?.EnemyPriorities;
        string order = effective is { Count: > 0 } ? GamePlanTargetOrderText(effective) : "";
        if (order.Length == 0)
            ImGui.TextDisabled(plan is null ? "-> assign a rotation to target" : "-> plan fallback");
        else if (ov is null)
            ImGui.TextDisabled($"-> {order}");                                   // plan default
        else
            ImGui.TextColored(new Vector4(1f, .82f, .28f, 1f), $"-> {order}");   // overridden
    }

    private static int GamePlanMatchPreset(IReadOnlyList<CombatEnemyPriority> priorities)
    {
        CombatEnemyKind[] kinds = priorities.Where(p => p.Enabled).Select(p => p.Kind).ToArray();
        for (int i = 1; i < GamePlanTargetPresets.Length; i++)
            if (GamePlanTargetPresets[i].Order is { } order && order.SequenceEqual(kinds))
                return i;
        return 0;   // presets are the only authors, so an override always matches one
    }

    private void SetGamePlanPhaseTarget(int actorIndex, EncounterActorSpec actor,
        string phaseKey, IReadOnlyList<CombatEnemyPriority>? priorities)
    {
        List<int> indices = GamePlanTargetIndices(actorIndex);
        foreach (int i in indices)
        {
            EncounterActorSpec subject = _encounterScenario[i];
            EncounterPlayerRules rules = subject.PlayerRules ?? new EncounterPlayerRules();
            List<PhaseTargetOverride> list = (rules.PhaseTargets ?? [])
                .Where(t => !string.Equals(t.PhaseKey, phaseKey, StringComparison.Ordinal))
                .ToList();
            if (priorities is { Count: > 0 })
                list.Add(new PhaseTargetOverride(phaseKey, priorities));
            _encounterScenario[i] = subject with
            {
                PlayerRules = rules with { PhaseTargets = list.Count > 0 ? list : null },
            };
        }
        RebuildEncounterSimKeepingView();
        string who = indices.Count > 1 ? $"{indices.Count} bodies" : actor.Name;
        AddChatMessage(priorities is { Count: > 0 }
            ? $"{who}: phase {phaseKey} targets -> {GamePlanTargetOrderText(priorities)}."
            : $"{who}: phase {phaseKey} targets back to plan default.");
    }

    private static string GamePlanTargetOrderText(IReadOnlyList<CombatEnemyPriority> priorities) =>
        string.Join(" -> ", priorities.Where(p => p.Enabled)
            .Select(p => EncounterEnemyPriorityLabel(p.Kind)));

    // ── derived, honest descriptors (structural — from geometry/target/phase only) ──

    private static string JobWord(RaidJob job) => job switch
    {
        RaidJob.Tank => "tank",
        RaidJob.Healer => "healer",
        RaidJob.Melee => "melee",
        RaidJob.Ranged => "ranged",
        _ => "body",
    };

    private static string GamePlanPhaseFacts(EncounterPhase phase)
    {
        string ground = phase.CasterFlying ? "air" : "ground";
        string melee = phase.MeleeEnabled ? "melee reaches her" : "no melee reach";
        return $"{ground} · {melee}";
    }

    private static string GamePlanTransitionLine(EncounterPhase phase)
    {
        if (phase.Transitions is not { Count: > 0 } transitions) return "final phase";
        EncounterTransition t = transitions[0];
        string when = t.Trigger.Kind switch
        {
            EncounterTriggerKind.HealthBelow => $"at {t.Trigger.Threshold * 100f:0}% HP",
            EncounterTriggerKind.HealthAbove => $"above {t.Trigger.Threshold * 100f:0}% HP",
            _ => t.Trigger.Kind.ToString(),
        };
        return $"-> {t.ToPhase} {when}";
    }

    private static string GamePlanDirectiveWord(RaidDirectiveKind kind) => kind switch
    {
        RaidDirectiveKind.Hold => "hold position",
        RaidDirectiveKind.ChaseBoss => "chase the boss",
        RaidDirectiveKind.MoveToSpot => "move to a spot",
        _ => kind.ToString(),
    };

    private static string GamePlanHazardShape(EncounterAbility ability)
    {
        // Non-spatial mechanics (summons, script beats) carry no meaningful "who".
        if (ability.Geometry.Kind == FootprintKind.None)
            return ability.Steps?.Any(s => s.Kind == EncounterStepKind.Summon) == true
                ? "summons adds" : "effect";

        string shape = ability.Geometry.Kind switch
        {
            FootprintKind.Circle => "AoE circle",
            FootprintKind.Cone => ability.Geometry.IsRearCone ? "rear cone" : "frontal cone",
            FootprintKind.Line => "line",
            FootprintKind.PointChain => "moving flame lane",
            FootprintKind.Projectile => "projectile",
            _ => "effect",
        };
        // A lane's danger is the whole path — its DB "who" adds nothing. Only the
        // caster-anchored and target-anchored shapes gain from naming who it lands on.
        if (ability.Geometry.Kind == FootprintKind.PointChain) return shape;

        string who = ability.Target.Kind switch
        {
            EncounterTargetKind.CurrentVictim => "on the tank",
            EncounterTargetKind.RandomHostile or EncounterTargetKind.RandomHostileNotVictim
                => "random target",
            EncounterTargetKind.NearestHostile => "nearest target",
            EncounterTargetKind.AllHostiles => "everyone in range",
            EncounterTargetKind.Self => "around her",
            _ => "",
        };
        return who.Length > 0 ? $"{shape}, {who}" : shape;
    }

    private static string GamePlanTimingWord(in EncounterTiming timing)
    {
        if (!timing.Repeats) return "one-shot / on cue";
        float lo = timing.RepeatMinMs / 1000f, hi = timing.RepeatMaxMs / 1000f;
        return timing.RepeatMinMs == timing.RepeatMaxMs
            ? $"every {lo:0.#}s"
            : $"every {lo:0.#}-{hi:0.#}s";
    }

    /// <summary>A short, role-shaped nudge derived only from structural facts. Kept
    /// deliberately spare — the rich, authored detail lives in the hover note.</summary>
    private static string? GamePlanRoleHint(RaidJob job, EncounterAbility ability)
    {
        if (ability.Geometry.Kind == FootprintKind.Cone)
            return ability.Geometry.IsRearCone
                ? "don't stand behind her"
                : job == RaidJob.Tank ? "aim her away from the raid" : "clear her front";
        if (ability.Geometry.Kind == FootprintKind.PointChain)
            return "stay off the lane";
        if (ability.Geometry.Kind == FootprintKind.Circle)
            return "get out of it";
        if (ability.Geometry.Kind == FootprintKind.Projectile &&
            ability.Target.Kind is EncounterTargetKind.RandomHostile
                or EncounterTargetKind.RandomHostileNotVictim)
            return "tracks its target — can't be outrun";
        if (ability.Steps?.Any(s => s.Kind == EncounterStepKind.Summon) == true)
            return job == RaidJob.Tank ? "set Targets to adds to pull them" : "they bite — kill them";
        return null;
    }
}

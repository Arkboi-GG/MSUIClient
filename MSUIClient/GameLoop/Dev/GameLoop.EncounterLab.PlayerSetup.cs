using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.World.Encounters;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// Encounter Lab — per-player Character Customizer.
//
// A plain click on a raid puppet only selects it. Selection slides a compact,
// game-native Character Customizer button onto the right edge; the customizer
// opens only when that button (or the Scenario rules button) is explicitly
// clicked, as a NON-modal window docked to the right edge - the world stays
// clickable so bodies can be ordered around while a plan is edited.
// The workspace edits a portable CombatPlan: standing doctrine lives on the
// character while encounter- and phase-specific choreography stays an overlay.
// The Lab executes only the parts its combat model can prove and labels the rest
// as typed intent instead of pretending that a rotation has fired.
// ─────────────────────────────────────────────────────────────────────────────

public sealed partial class GameLoop
{
    private string? _encounterPlayerSetupKey;
    private bool _encounterPlayerSetupRequested;
    private string? _encounterPlayerSetupLauncherKey;
    private double _encounterPlayerSetupLauncherAppearedAt;
    private string? _encounterPlayerPlanDraftKey;
    private CombatPlan? _encounterPlayerPlanDraft;
    private CombatPlan? _encounterPlayerPlanBaseline;
    private CombatPlan? _encounterPlayerPlanContinuousUndoBaseline;
    private bool _encounterPlayerPlanDirty;
    private readonly List<CombatPlan> _encounterPlayerPlanUndo = [];
    private readonly List<CombatPlan> _encounterPlayerPlanRedo = [];

    private const double EncounterPlayerSetupLauncherPopSeconds = 0.24;

    private void OpenEncounterPlayerSetup(string key)
    {
        if (_encounterOrbitDragging || _encounterOrientSpinning) return;
        if (_encounterScenario.FirstOrDefault(actor => actor.Key == key) is not
            { Role: EncounterActorRole.Friendly } actor) return;
        EnsureEncounterPlayerPlanDraft(actor);
        _encounterPlayerSetupKey = key;
        _encounterPlayerSetupRequested = true;
        // In the docked workspace the customizer lives in the bottom deck, which
        // only shows in the encounter view.
        if (CreatorWorkspaceActive) _workspaceView = WorkspaceView.Encounter;
    }

    /// <summary>The one selected friendly puppet eligible for customization. Multi-selection
    /// stays an order group and deliberately gets no ambiguous single-body customizer.</summary>
    private string? SelectedEncounterPlayerSetupKey()
    {
        if (!_encounterLabOpen || !_freeView || _freecamSelection.Count != 1 ||
            _encounterOrbitDragging || _encounterOrientSpinning) return null;
        return EncounterRaidPuppetKey(_freecamSelection[0]);
    }

    /// <summary>Selection is intentionally non-blocking: a compact vanilla panel button
    /// slides in from the right edge and is the explicit gateway to the modal.</summary>
    private void DrawEncounterPlayerSetupLauncher()
    {
        string? key = SelectedEncounterPlayerSetupKey();
        if (key is null)
        {
            _encounterPlayerSetupLauncherKey = null;
            return;
        }

        // Keep the launcher out from under its own modal. Retaining its key means it
        // returns quietly when the modal closes instead of replaying the entrance.
        if (_encounterPlayerSetupKey is not null) return;

        if (!string.Equals(_encounterPlayerSetupLauncherKey, key,
                StringComparison.Ordinal))
        {
            _encounterPlayerSetupLauncherKey = key;
            _encounterPlayerSetupLauncherAppearedAt = NowSeconds();
        }

        EncounterActorSpec? actor = _encounterScenario.FirstOrDefault(a => a.Key == key);
        if (actor is null) return;

        const string caption = "Character Customizer";
        Vector2 display = ImGui.GetIO().DisplaySize;
        float s = MathF.Max(display.Y / GlueCanvasH, 0.5f) * CreatorUiScale;
        float textScale = Math.Clamp(CreatorTextScale, 0.65f, 1.35f);
        Vector2 measured = ImGui.CalcTextSize(caption) * textScale;
        Vector2 buttonSize = new(
            MathF.Max(166f * s, measured.X + 30f * s),
            MathF.Max(30f * s, measured.Y + 12f * s));

        float t = (float)Math.Clamp(
            (NowSeconds() - _encounterPlayerSetupLauncherAppearedAt) /
            EncounterPlayerSetupLauncherPopSeconds, 0.0, 1.0);
        // Ease-out-back gives the edge tab a short, readable "pop" without making
        // selection itself wait on animation or stealing focus from world controls.
        float u = t - 1f;
        float eased = 1f + 2.70158f * u * u * u + 1.70158f * u * u;
        float margin = 10f * s + WorkspaceRightInsetX;
        float restingX = display.X - buttonSize.X - margin;
        float hiddenX = display.X + 4f * s;
        float x = hiddenX + (restingX - hiddenX) * eased;
        float y = Math.Clamp(display.Y * 0.46f - buttonSize.Y * 0.5f,
            64f * s, MathF.Max(64f * s, display.Y - buttonSize.Y - 64f * s));

        ImGui.SetNextWindowPos(new Vector2(x, y), ImGuiCond.Always);
        ImGui.SetNextWindowSize(buttonSize, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration |
            ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoFocusOnAppearing |
            ImGuiWindowFlags.NoBackground;

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        if (ImGui.Begin("###encounter-character-customizer-launcher", flags))
        {
            ImGui.SetWindowFontScale(textScale);
            bool clicked = _skin?.PanelButton(
                    $"{caption}##encounter-customize-{key}", buttonSize) ??
                ImGui.Button($"{caption}##encounter-customize-{key}", buttonSize);
            Vector2 buttonMin = ImGui.GetItemRectMin();
            Vector2 buttonMax = ImGui.GetItemRectMax();
            if (clicked) OpenEncounterPlayerSetup(key);

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Customize {actor.Name}'s encounter behavior.");

            // A brief gold rim makes the newly-arrived choice legible, then gets
            // entirely out of the way once the owner has noticed it.
            if (t < 1f)
            {
                float alpha = (1f - t) * 0.85f;
                uint gold = ImGui.ColorConvertFloat4ToU32(
                    new Vector4(1f, 0.78f, 0.22f, alpha));
                Vector2 inset = new(2f * s);
                ImGui.GetWindowDrawList().AddRect(
                    buttonMin + inset,
                    buttonMax - inset,
                    gold, 4f * s, ImDrawFlags.None, MathF.Max(1f, 2f * s));
            }
            ImGui.SetWindowFontScale(1f);
        }
        ImGui.End();
        ImGui.PopStyleVar();
    }

    /// <summary>Drawn from the always-called encounter pop-out pass, so the opt-in
    /// launcher and setup modal remain alive whether the Action Timeline window itself
    /// is open or closed.</summary>
    private void DrawEncounterPlayerSetupModal()
    {
        // The docked workspace has its own gateway (the right rail's Cust
        // button) and hosts the editor in the bottom deck - no slide-in
        // launcher, no floating window.
        if (!CreatorWorkspaceActive) DrawEncounterPlayerSetupLauncher();
        if (!_encounterLabOpen)
        {
            _encounterPlayerSetupKey = null;
            _encounterPlayerSetupRequested = false;
            _encounterPlayerSetupLauncherKey = null;
            return;
        }

        if (CreatorWorkspaceActive || _encounterPlayerSetupKey is null) return;

        const string tuneId = "encounter-player-setup";
        _activePanelTune = tuneId;
        float cs = CreatorUiScale;
        Vector2 display = ImGui.GetIO().DisplaySize;
        float s = MathF.Max(display.Y / GlueCanvasH, 0.5f) * cs;
        // A regular chrome window docked to the RIGHT edge, deliberately NOT a
        // modal: the owner keeps the world - and the very puppet being
        // customized - clickable and orderable while the plan is edited.
        // In the docked workspace, stop above the bottom deck by default.
        float bottomInset = CreatorWorkspaceActive
            ? Math.Clamp(Settings.Creator.DeckFraction, 0.16f, 0.55f) * display.Y
            : 0f;
        Vector2 size = new(
            Math.Clamp(640f * cs, 560f, MathF.Max(560f, display.X * 0.45f)),
            Math.Clamp(690f * cs, 520f, MathF.Max(520f, display.Y - 88f * s - bottomInset)));
        ImGuiCond placement = _creatorLayoutResetFrames > 0
            ? ImGuiCond.Always : ImGuiCond.FirstUseEver;
        ImGui.SetNextWindowPos(new Vector2(
            MathF.Max(12f, display.X - size.X - 12f - WorkspaceRightInsetX), 64f * s),
            placement);
        // FirstUseEver lets ImGui's ini own every later resize.
        ImGui.SetNextWindowSize(size, placement);
        ImGui.SetNextWindowSizeConstraints(new Vector2(560f, 520f),
            new Vector2(MathF.Max(560f, display.X - 24f),
                MathF.Max(520f, display.Y - 24f)));

        PushCreatorStyle();
        if (_encounterPlayerSetupRequested)
        {
            ImGui.SetNextWindowFocus();
            _encounterPlayerSetupRequested = false;
        }
        if (ImGui.Begin("###encounter-player-setup", CreatorChromeFlags))
        {
            ClampCreatorWindowOnScreen();
            if (DrawCreatorPanelChrome("Character Customizer", tuneId))
                _encounterPlayerSetupKey = null;
            else
            {
                ImGui.SetWindowFontScale(CreatorTextScale);
                DrawEncounterPlayerSetupBody();
                ImGui.SetWindowFontScale(1f);
            }
        }
        ImGui.End();
        PopCreatorStyle();
        _activePanelTune = null;
    }

    private void DrawEncounterPlayerSetupBody()
    {
        int actorIndex = _encounterPlayerSetupKey is { } key
            ? _encounterScenario.FindIndex(actor => actor.Key == key)
            : -1;
        if (actorIndex < 0 || _encounterScenario[actorIndex] is not
            { Role: EncounterActorRole.Friendly } actor)
        {
            EncounterPlayerSetupDisabledWrapped(
                "This player is no longer in the encounter scenario.");
            if (EncounterPanelButtonSized("Close",
                    new Vector2(ImGui.GetContentRegionAvail().X, 0f)))
                _encounterPlayerSetupKey = null;
            return;
        }

        EnsureEncounterPlayerPlanDraft(actor);
        CombatPlan plan = _encounterPlayerPlanDraft!;

        ImGui.TextColored(new Vector4(1f, .82f, .28f, 1f), "Combat Plan");
        ImGui.SameLine();
        ImGui.TextColored(RoleColourVec4(actor.Role, actor.Job), actor.Name);
        ImGui.SameLine();
        ImGui.TextDisabled(actor.Job == RaidJob.None ? "friendly" : actor.Job.ToString());
        if (_encounterPlayerPlanDirty)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1f, .65f, .2f, 1f), "DRAFT");
        }

        ImGui.TextWrapped(EncounterCombatPlanSummary(plan));
        ImGui.Separator();

        float footerReserve = ImGui.GetFrameHeightWithSpacing() +
                              ImGui.GetStyle().ItemSpacing.Y * 2f;
        if (ImGui.BeginChild("##combat-plan-content", new Vector2(0f, -footerReserve)))
        {
            if (ImGui.BeginTabBar("##combat-plan-tabs"))
            {
                if (ImGui.BeginTabItem("Slots"))
                {
                    DrawEncounterPlanSlots(actorIndex, actor);
                    ImGui.EndTabItem();
                }
                if (ImGui.BeginTabItem("Quick Plan"))
                {
                    DrawEncounterQuickPlan(actor, plan);
                    ImGui.EndTabItem();
                }
                if (ImGui.BeginTabItem("Priorities"))
                {
                    DrawEncounterPlanPriorities(plan);
                    ImGui.EndTabItem();
                }
                if (ImGui.BeginTabItem("Responsibilities"))
                {
                    DrawEncounterPlanResponsibilities(plan);
                    ImGui.EndTabItem();
                }
                if (ImGui.BeginTabItem("Rotation"))
                {
                    DrawEncounterPlanRotation(plan);
                    ImGui.EndTabItem();
                }
                if (ImGui.BeginTabItem("Encounter Context"))
                {
                    DrawEncounterPlanContext(actor);
                    ImGui.EndTabItem();
                }
                if (ImGui.BeginTabItem("Test & Explain"))
                {
                    DrawEncounterPlanExplain(actor, plan);
                    ImGui.EndTabItem();
                }
                ImGui.EndTabBar();
            }
        }
        ImGui.EndChild();

        ImGui.Separator();
        DrawEncounterPlanFooter(actorIndex, actor, plan);
    }

    /// <summary>The manila folder strip's tab list for the deck customizer.</summary>
    internal static readonly string[] EncounterPlanDeckTabs =
        ["Slots", "Quick Plan", "Priorities", "Responsibilities", "Rotation", "Context", "Explain"];

    /// <summary>The customizer as the workspace deck's content: a one-line
    /// header, the SELECTED plan tab laid out WIDE (its groups as side-by-side
    /// cards), and the Undo/Redo/Save footer. The folder strip above the deck
    /// owns tab selection.</summary>
    private void DrawEncounterPlayerSetupDeckBody(int tab)
    {
        int actorIndex = _encounterPlayerSetupKey is { } key
            ? _encounterScenario.FindIndex(a => a.Key == key)
            : -1;
        if (actorIndex < 0 || _encounterScenario[actorIndex] is not
            { Role: EncounterActorRole.Friendly } actor)
        {
            EncounterPlayerSetupDisabledWrapped(
                "This player is no longer in the encounter scenario.");
            if (EncounterPanelButton("Close")) _encounterPlayerSetupKey = null;
            return;
        }
        EnsureEncounterPlayerPlanDraft(actor);
        CombatPlan plan = _encounterPlayerPlanDraft!;

        ImGui.TextColored(new Vector4(1f, .82f, .28f, 1f), "Combat Plan");
        ImGui.SameLine();
        ImGui.TextColored(RoleColourVec4(actor.Role, actor.Job), actor.Name);
        ImGui.SameLine();
        ImGui.TextDisabled(actor.Job == RaidJob.None ? "friendly" : actor.Job.ToString());
        if (_encounterPlayerPlanDirty)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1f, .65f, .2f, 1f), "DRAFT");
        }
        ImGui.SameLine();
        ImGui.TextDisabled(EncounterCombatPlanSummary(plan));

        float footerReserve = ImGui.GetFrameHeightWithSpacing() +
                              ImGui.GetStyle().ItemSpacing.Y * 2f;
        ImGui.BeginChild("##plan-deck-content", new Vector2(0f, -footerReserve));
        float spacing = ImGui.GetStyle().ItemSpacing.X;
        float avail = ImGui.GetContentRegionAvail().X;

        void Cards(params (string Id, Action Body)[] groups)
        {
            float w = (avail - spacing * (groups.Length - 1)) / groups.Length;
            for (int i = 0; i < groups.Length; i++)
            {
                if (i > 0) ImGui.SameLine();
                BeginEncounterDeckCard($"##plan-card-{groups[i].Id}", w);
                groups[i].Body();
                EndEncounterDeckCard();
            }
        }

        switch (tab)
        {
            case 0: DrawEncounterPlanSlots(actorIndex, actor); break;
            case 1:
                Cards(("intent", () => DrawEncounterQuickPlanIntent(actor)),
                    ("engage", DrawEncounterQuickPlanEngagement));
                break;
            case 2:
                Cards(("protect", DrawEncounterPlanProtectPriorities),
                    ("enemy", DrawEncounterPlanEnemyPriorities));
                break;
            case 3:
                Cards(("standing", DrawEncounterPlanStandingResponsibilities),
                    ("resources", DrawEncounterPlanResourcePolicy),
                    ("fallback", DrawEncounterPlanFallbackRule));
                break;
            case 4:
                // Loadout card left, the spellbook grid takes the rest.
                BeginEncounterDeckCard("##plan-card-loadout",
                    MathF.Min(480f * CreatorUiScale, avail * 0.45f));
                DrawEncounterRotationLoadout();
                EndEncounterDeckCard();
                ImGui.SameLine();
                BeginEncounterDeckCard("##plan-card-spellbook", 0f);
                DrawEncounterRotationSpellbook();
                EndEncounterDeckCard();
                break;
            case 5: DrawEncounterPlanContext(actor); break;
            default: DrawEncounterPlanExplain(actor, plan); break;
        }
        ImGui.EndChild();
        DrawEncounterPlanFooter(actorIndex, actor, plan);
    }

    private void EnsureEncounterPlayerPlanDraft(EncounterActorSpec actor)
    {
        if (_encounterPlayerPlanDraft is not null &&
            string.Equals(_encounterPlayerPlanDraftKey, actor.Key, StringComparison.Ordinal)) return;
        _encounterPlayerPlanDraftKey = actor.Key;
        _encounterPlayerPlanBaseline = CreateEncounterPlayerPlanBaseline(actor);
        _encounterPlayerPlanDraft = _encounterPlayerPlanBaseline;
        _encounterPlayerPlanDirty = false;
        _encounterPlayerPlanContinuousUndoBaseline = null;
        _encounterPlayerPlanUndo.Clear();
        _encounterPlayerPlanRedo.Clear();
    }

    private static CombatPlan CreateEncounterPlayerPlanBaseline(EncounterActorSpec actor) =>
        actor.PlayerRules?.Plan ?? new CombatPlan(
            Name: "Custom plan",
            Movement: new CombatMovementPlan(
                FacePrimaryEnemy: actor.PlayerRules?.AlwaysFaceBoss == true),
            EnemyPriorities: [new CombatEnemyPriority(CombatEnemyKind.PrimaryEnemy)],
            Resources: new CombatResourcePolicy(),
            ClassId: DefaultClassForJob(actor.Job));

    /// <summary>A starting class per job so the Rotation tab opens on a real
    /// spellbook; the owner can re-pick freely.</summary>
    private static uint DefaultClassForJob(RaidJob job) => job switch
    {
        RaidJob.Tank => 1,     // Warrior
        RaidJob.Healer => 5,   // Priest
        RaidJob.Melee => 4,    // Rogue
        RaidJob.Ranged => 8,   // Mage
        _ => 0,
    };

    /// <summary>Roster replacement may reuse stable keys. Clear every piece of modal
    /// state before replacing actors so a draft can never masquerade as the new actor's
    /// applied profile.</summary>
    private void InvalidateEncounterPlayerPlanDraft()
    {
        _encounterPlayerPlanDraftKey = null;
        _encounterPlayerPlanDraft = null;
        _encounterPlayerPlanBaseline = null;
        _encounterPlayerPlanContinuousUndoBaseline = null;
        _encounterPlayerPlanDirty = false;
        _encounterPlayerPlanUndo.Clear();
        _encounterPlayerPlanRedo.Clear();
        InvalidateEncounterPositioningDraft();
    }

    private void SetEncounterPlayerPlanDraft(CombatPlan next)
    {
        CommitEncounterPlayerPlanContinuousUndo();
        if (_encounterPlayerPlanDraft is { } current && current != next)
        {
            _encounterPlayerPlanUndo.Add(current);
            if (_encounterPlayerPlanUndo.Count > 100) _encounterPlayerPlanUndo.RemoveAt(0);
            _encounterPlayerPlanRedo.Clear();
        }
        _encounterPlayerPlanDraft = next;
        _encounterPlayerPlanDirty = next != _encounterPlayerPlanBaseline;
    }

    /// <summary>Text and sliders report every frame while active. Keep their first
    /// value as one undo boundary and commit it when the ImGui edit gesture ends.</summary>
    private void SetEncounterPlayerPlanDraftContinuous(CombatPlan next)
    {
        if (_encounterPlayerPlanDraft is not { } current || current == next) return;
        _encounterPlayerPlanContinuousUndoBaseline ??= current;
        _encounterPlayerPlanDraft = next;
        _encounterPlayerPlanDirty = next != _encounterPlayerPlanBaseline;
        _encounterPlayerPlanRedo.Clear();
    }

    private void FinishEncounterPlayerPlanContinuousEdit()
    {
        if (!ImGui.IsItemDeactivatedAfterEdit()) return;
        CommitEncounterPlayerPlanContinuousUndo();
    }

    private void CommitEncounterPlayerPlanContinuousUndo()
    {
        if (_encounterPlayerPlanContinuousUndoBaseline is not { } baseline) return;
        if (_encounterPlayerPlanDraft is { } current && baseline != current)
        {
            _encounterPlayerPlanUndo.Add(baseline);
            if (_encounterPlayerPlanUndo.Count > 100) _encounterPlayerPlanUndo.RemoveAt(0);
        }
        _encounterPlayerPlanContinuousUndoBaseline = null;
    }

    private static CombatPlan EncounterCombatPlanTemplate(RaidJob job) => job switch
    {
        RaidJob.Healer => new CombatPlan(
            "Tank healer",
            new CombatMovementPlan(CombatMovementMode.Follow, CombatSubject.Tank(1),
                12f, 20f, false),
            CombatEngagementMode.NeverInitiate,
            [
                new CombatSupportPriority(CombatSubject.Tank(1), 90f),
                new CombatSupportPriority(CombatSubject.Tank(2), 75f),
                new CombatSupportPriority(CombatSubject.LowestHealth, 45f),
                new CombatSupportPriority(CombatSubject.Self, 30f),
            ],
            [new CombatEnemyPriority(CombatEnemyKind.PrimaryEnemy)],
            [CombatResponsibility.DispelMagic, CombatResponsibility.Resurrect],
            new CombatResourcePolicy(25, 25, true),
            CombatFallback.ClassDefaults),
        RaidJob.Tank => new CombatPlan(
            "Main tank",
            new CombatMovementPlan(CombatMovementMode.Independent, null, 0f, 0f, true),
            CombatEngagementMode.DefendGroup,
            null,
            [new CombatEnemyPriority(CombatEnemyKind.AnyAdd),
             new CombatEnemyPriority(CombatEnemyKind.PrimaryEnemy)],
            [CombatResponsibility.Interrupt],
            new CombatResourcePolicy(15, 30, true),
            CombatFallback.ClassDefaults),
        RaidJob.Melee => new CombatPlan(
            "Add-control melee",
            new CombatMovementPlan(CombatMovementMode.Independent, null, 0f, 0f, true),
            CombatEngagementMode.DefendGroup,
            null,
            [new CombatEnemyPriority(CombatEnemyKind.AnyAdd),
             new CombatEnemyPriority(CombatEnemyKind.PrimaryEnemy)],
            [CombatResponsibility.Interrupt],
            new CombatResourcePolicy(10, 25, false),
            CombatFallback.ClassDefaults),
        RaidJob.Ranged => new CombatPlan(
            "Add-control ranged",
            new CombatMovementPlan(CombatMovementMode.HoldPosition, null, 0f, 0f, true),
            CombatEngagementMode.DefendGroup,
            null,
            [new CombatEnemyPriority(CombatEnemyKind.AnyAdd),
             new CombatEnemyPriority(CombatEnemyKind.PrimaryEnemy)],
            [CombatResponsibility.Interrupt, CombatResponsibility.CrowdControlAdds],
            new CombatResourcePolicy(15, 25, true),
            CombatFallback.ClassDefaults),
        _ => new CombatPlan(
            "Safe assistant",
            new CombatMovementPlan(),
            CombatEngagementMode.NeverInitiate,
            null,
            [new CombatEnemyPriority(CombatEnemyKind.PrimaryEnemy)],
            null,
            new CombatResourcePolicy(),
            CombatFallback.ClassDefaults),
    };

    private void DrawEncounterQuickPlan(EncounterActorSpec actor, CombatPlan plan)
    {
        DrawEncounterQuickPlanIntent(actor);
        ImGui.SeparatorText("Positioning");
        ImGui.TextDisabled("Where this body stands — follow/hold, per-phase spots, left/right — now lives " +
                           "in the Slots tab's positioning script, held apart from this portable rotation.");
        DrawEncounterQuickPlanEngagement();
    }

    private void DrawEncounterQuickPlanIntent(EncounterActorSpec actor)
    {
        CombatPlan plan = _encounterPlayerPlanDraft!;
        ImGui.SeparatorText("Start from intent");
        if (EncounterPanelButton("Use role template", 150f * CreatorUiScale))
            // Templates rewrite doctrine, not identity: the chosen class and the
            // authored rotation survive.
            SetEncounterPlayerPlanDraft(EncounterCombatPlanTemplate(actor.Job) with
            {
                ClassId = plan.ClassId != 0 ? plan.ClassId : DefaultClassForJob(actor.Job),
                Rotation = plan.Rotation,
            });
        ImGui.SameLine();
        ImGui.TextDisabled("Templates create visible, editable rules; they never apply silently.");

        string name = plan.Name;
        ImGui.SetNextItemWidth(MathF.Min(420f * CreatorUiScale, ImGui.GetContentRegionAvail().X));
        if (ImGui.InputText("Plan name", ref name, 80))
            SetEncounterPlayerPlanDraftContinuous(plan with { Name = name });
        FinishEncounterPlayerPlanContinuousEdit();
    }

    private void DrawEncounterQuickPlanEngagement()
    {
        CombatPlan plan = _encounterPlayerPlanDraft!;
        ImGui.SeparatorText("Engagement");
        int engagement = (int)plan.Engagement;
        if (ImGui.Combo("May begin combat", ref engagement,
                "Never initiate\0Assist follow target\0Defend the group\0Autonomous\0"))
            SetEncounterPlayerPlanDraft(plan with
            { Engagement = (CombatEngagementMode)engagement });
        ImGui.TextWrapped("Movement and engagement are strategic doctrine. Ability rules may act " +
                          "inside them, but cannot steal a waypoint or acquire a forbidden fight. " +
                          "The Lab records engagement permission; it has no player pull-action model yet.");
    }

    private void DrawEncounterPlanPriorities(CombatPlan plan)
    {
        DrawEncounterPlanProtectPriorities();
        DrawEncounterPlanEnemyPriorities();
    }

    private void DrawEncounterPlanProtectPriorities()
    {
        CombatPlan plan = _encounterPlayerPlanDraft!;
        ImGui.SeparatorText("Protect — first applicable row wins");
        ImGui.TextDisabled("Resolved as live intent; healing execution awaits health/resource/cast state.");
        List<CombatSupportPriority> support = (plan.SupportPriorities ?? []).ToList();
        bool supportDiscreteChanged = false;
        bool supportContinuousChanged = false;
        bool supportContinuousFinished = false;
        for (int i = 0; i < support.Count; i++)
        {
            ImGui.PushID($"support-{i}");
            CombatSupportPriority row = support[i];
            bool enabled = row.Enabled;
            if (ImGui.Checkbox("##enabled", ref enabled))
            { support[i] = row = row with { Enabled = enabled }; supportDiscreteChanged = true; }
            ImGui.SameLine();
            CombatSubject target = row.Target;
            if (DrawEncounterSubjectCombo("##target", target, allowLowestHealth: true,
                    out CombatSubject selected))
            { support[i] = row = row with { Target = selected }; supportDiscreteChanged = true; }
            ImGui.SameLine();
            float threshold = row.OnlyWhenBelowHealthPercent;
            ImGui.SetNextItemWidth(150f * CreatorUiScale);
            if (ImGui.SliderFloat("##health", ref threshold, 1f, 100f, "below %.0f%%"))
            {
                support[i] = row with { OnlyWhenBelowHealthPercent = threshold };
                supportContinuousChanged = true;
            }
            supportContinuousFinished |= ImGui.IsItemDeactivatedAfterEdit();
            ImGui.SameLine();
            if (ImGui.SmallButton("Up") && i > 0)
            { (support[i - 1], support[i]) = (support[i], support[i - 1]); supportDiscreteChanged = true; }
            ImGui.SameLine();
            if (ImGui.SmallButton("Down") && i + 1 < support.Count)
            { (support[i + 1], support[i]) = (support[i], support[i + 1]); supportDiscreteChanged = true; }
            ImGui.SameLine();
            if (ImGui.SmallButton("Remove"))
            { support.RemoveAt(i); i--; supportDiscreteChanged = true; }
            ImGui.PopID();
        }
        if (EncounterPanelButton("Add protection priority", compact: true))
        {
            support.Add(new CombatSupportPriority(CombatSubject.LowestHealth, 50f));
            supportDiscreteChanged = true;
        }
        if (supportDiscreteChanged)
            SetEncounterPlayerPlanDraft(plan with { SupportPriorities = support.ToArray() });
        else if (supportContinuousChanged)
            SetEncounterPlayerPlanDraftContinuous(plan with { SupportPriorities = support.ToArray() });
        if (supportContinuousFinished)
            CommitEncounterPlayerPlanContinuousUndo();
    }

    private void DrawEncounterPlanEnemyPriorities()
    {
        CombatPlan plan = _encounterPlayerPlanDraft!;
        ImGui.SeparatorText("Engage — first available enemy bucket wins");
        List<CombatEnemyPriority> enemies = (plan.EnemyPriorities ?? []).ToList();
        bool enemyChanged = false;
        for (int i = 0; i < enemies.Count; i++)
        {
            ImGui.PushID($"enemy-{i}");
            CombatEnemyPriority row = enemies[i];
            bool enabled = row.Enabled;
            if (ImGui.Checkbox("##enabled", ref enabled))
            { enemies[i] = row = row with { Enabled = enabled }; enemyChanged = true; }
            ImGui.SameLine();
            int kind = (int)row.Kind;
            ImGui.SetNextItemWidth(230f * CreatorUiScale);
            if (ImGui.Combo("##kind", ref kind,
                    "Any active add\0Current enemy\0Primary encounter target\0"))
            { enemies[i] = row = row with { Kind = (CombatEnemyKind)kind }; enemyChanged = true; }
            ImGui.SameLine();
            if (ImGui.SmallButton("Up") && i > 0)
            { (enemies[i - 1], enemies[i]) = (enemies[i], enemies[i - 1]); enemyChanged = true; }
            ImGui.SameLine();
            if (ImGui.SmallButton("Down") && i + 1 < enemies.Count)
            { (enemies[i + 1], enemies[i]) = (enemies[i], enemies[i + 1]); enemyChanged = true; }
            ImGui.SameLine();
            if (ImGui.SmallButton("Remove"))
            { enemies.RemoveAt(i); i--; enemyChanged = true; }
            ImGui.PopID();
        }
        if (EncounterPanelButton("Add enemy priority", compact: true))
        { enemies.Add(new CombatEnemyPriority(CombatEnemyKind.PrimaryEnemy)); enemyChanged = true; }
        if (enemyChanged)
            SetEncounterPlayerPlanDraft(plan with { EnemyPriorities = enemies.ToArray() });

        ImGui.TextWrapped("These are semantic buckets, not creature names. The same order works " +
                          "for one dungeon add or forty-player raid waves.");
    }

    private void DrawEncounterPlanResponsibilities(CombatPlan plan)
    {
        DrawEncounterPlanStandingResponsibilities();
        DrawEncounterPlanResourcePolicy();
        DrawEncounterPlanFallbackRule();
    }

    private void DrawEncounterPlanStandingResponsibilities()
    {
        CombatPlan plan = _encounterPlayerPlanDraft!;
        ImGui.SeparatorText("Standing responsibilities");
        ImGui.TextDisabled("Portable ownership contract; cast execution is not simulated yet.");
        HashSet<CombatResponsibility> selected = new(plan.Responsibilities ?? []);
        foreach (CombatResponsibility responsibility in Enum.GetValues<CombatResponsibility>())
        {
            bool on = selected.Contains(responsibility);
            if (!ImGui.Checkbox(EncounterResponsibilityLabel(responsibility), ref on)) continue;
            if (on) selected.Add(responsibility); else selected.Remove(responsibility);
            SetEncounterPlayerPlanDraft(plan with
            { Responsibilities = selected.OrderBy(value => value).ToArray() });
            plan = _encounterPlayerPlanDraft!;
        }
    }

    private void DrawEncounterPlanResourcePolicy()
    {
        CombatPlan plan = _encounterPlayerPlanDraft!;
        ImGui.SeparatorText("Resources and emergencies");
        CombatResourcePolicy resources = plan.Resources ?? new CombatResourcePolicy();
        int reserve = resources.ReservePercent;
        if (ImGui.SliderInt("Reserve resource", ref reserve, 0, 80, "%d%%"))
            SetEncounterPlayerPlanDraftContinuous(plan with
            { Resources = resources with { ReservePercent = reserve } });
        FinishEncounterPlayerPlanContinuousEdit();
        int emergency = resources.EmergencyHealthPercent;
        if (ImGui.SliderInt("Emergency health", ref emergency, 1, 80, "%d%%"))
            SetEncounterPlayerPlanDraftContinuous(plan with
            { Resources = resources with { EmergencyHealthPercent = emergency } });
        FinishEncounterPlayerPlanContinuousEdit();
        bool save = resources.SaveMajorCooldowns;
        if (ImGui.Checkbox("Reserve major cooldowns for emergencies", ref save))
            SetEncounterPlayerPlanDraft(plan with
            { Resources = resources with { SaveMajorCooldowns = save } });
    }

    private void DrawEncounterPlanFallbackRule()
    {
        CombatPlan plan = _encounterPlayerPlanDraft!;
        ImGui.SeparatorText("If no rule can act");
        int fallback = (int)plan.Fallback;
        if (ImGui.Combo("Fallback", ref fallback,
                "Do nothing this tick\0Auto-attack current enemy\0Use class defaults\0"))
            SetEncounterPlayerPlanDraft(plan with { Fallback = (CombatFallback)fallback });
    }

    // ── rotation tab ─────────────────────────────────────────────────────────
    // Real 1.12 spells, offline: SkillLineAbility classmasks pick the class's
    // roster, Spell.dbc levels cap it at 60, rank chains collapse to the top
    // trained rank. The Lab executes the authored list as cosmetic casts.

    private uint _encounterRotationRosterClass;
    private List<SpellInfo>? _encounterRotationRoster;
    private string _encounterRotationSearch = "";

    private List<SpellInfo> EncounterRotationRoster(uint classId)
    {
        if (_encounterRotationRoster is not null &&
            _encounterRotationRosterClass == classId) return _encounterRotationRoster;
        _encounterRotationRosterClass = classId;
        _encounterRotationRoster = _spellCatalog is { } spells && _skillLines is { } skills
            ? ClassSpellList.TrainedAt(spells, skills, (byte)classId, 60, _talents)
            : [];
        return _encounterRotationRoster;
    }

    private void DrawEncounterPlanRotation(CombatPlan plan)
    {
        DrawEncounterRotationLoadout();
        DrawEncounterRotationSpellbook();
    }

    private bool EncounterRotationDataMissing()
    {
        if (_spellCatalog is not null && _skillLines is not null) return false;
        EncounterPlayerSetupDisabledWrapped(
            "Spell data is unavailable (game archives not mounted). The rotation " +
            "editor needs Spell.dbc and SkillLineAbility.dbc from the client MPQs.");
        return true;
    }

    /// <summary>Class picker + the ordered rotation list.</summary>
    private void DrawEncounterRotationLoadout()
    {
        if (EncounterRotationDataMissing()) return;
        CombatPlan plan = _encounterPlayerPlanDraft!;
        float cs = CreatorUiScale;

        ImGui.SeparatorText("Class");
        ImGui.SetNextItemWidth(200f * cs);
        if (ImGui.BeginCombo("##rotation-class", ClassSpellList.ClassName(plan.ClassId)))
        {
            foreach (ClassSpellList.PlayableClass entry in ClassSpellList.Classes)
            {
                bool isSelected = plan.ClassId == entry.Id;
                if (ImGui.Selectable(entry.Name, isSelected))
                    SetEncounterPlayerPlanDraft(plan with { ClassId = entry.Id });
                if (isSelected) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        ImGui.TextDisabled("level 60 · fully trained · top rank per spell");
        plan = _encounterPlayerPlanDraft ?? plan;

        ImGui.SeparatorText("Rotation — first ready ability wins");
        List<CombatAbilityIntent> rotation = (plan.Rotation ?? []).ToList();
        if (rotation.Count == 0)
            ImGui.TextDisabled("Empty. Click spells in the spellbook below to add them, " +
                               "highest priority first.");
        bool changed = false;
        float iconSide = MathF.Max(22f, 24f * cs);
        for (int i = 0; i < rotation.Count; i++)
        {
            ImGui.PushID($"rotation-{i}");
            CombatAbilityIntent entry = rotation[i];
            bool enabled = entry.Enabled;
            if (ImGui.Checkbox("##enabled", ref enabled))
            { rotation[i] = entry = entry with { Enabled = enabled }; changed = true; }
            ImGui.SameLine();
            bool known = _spellCatalog.TryGet(entry.SpellId, out SpellInfo info);
            uint icon = known && _gameplayArt is not null
                ? _gameplayArt.Handle(info.IconPath) : 0;
            if (icon != 0)
            {
                ImGui.Image((nint)icon, new Vector2(iconSide, iconSide));
                if (ImGui.IsItemHovered() && known) EncounterRotationSpellTooltip(info);
                ImGui.SameLine();
            }
            string label = known
                ? string.IsNullOrEmpty(info.Rank) ? info.Name : $"{info.Name} ({info.Rank})"
                : entry.Name.Length > 0 ? entry.Name : $"spell {entry.SpellId}";
            ImGui.TextUnformatted($"{i + 1}. {label}");
            if (ImGui.IsItemHovered() && known) EncounterRotationSpellTooltip(info);
            if (known)
            {
                ImGui.SameLine();
                ImGui.TextDisabled(EncounterRotationSpellCadence(info));
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("Up") && i > 0)
            { (rotation[i - 1], rotation[i]) = (rotation[i], rotation[i - 1]); changed = true; }
            ImGui.SameLine();
            if (ImGui.SmallButton("Down") && i + 1 < rotation.Count)
            { (rotation[i + 1], rotation[i]) = (rotation[i], rotation[i + 1]); changed = true; }
            ImGui.SameLine();
            if (ImGui.SmallButton("Remove")) { rotation.RemoveAt(i); i--; changed = true; }
            ImGui.PopID();
        }
        if (changed)
            SetEncounterPlayerPlanDraft(plan with { Rotation = rotation.ToArray() });
    }

    /// <summary>The class's trained-at-60 spellbook: search + clickable icon grid.</summary>
    private void DrawEncounterRotationSpellbook()
    {
        if (EncounterRotationDataMissing()) return;
        CombatPlan plan = _encounterPlayerPlanDraft!;
        List<CombatAbilityIntent> rotation = (plan.Rotation ?? []).ToList();
        float cs = CreatorUiScale;

        ImGui.SeparatorText("Spellbook");
        if (plan.ClassId == 0)
        {
            EncounterPlayerSetupDisabledWrapped(
                "Choose a class above to open its trained-at-60 spellbook.");
            return;
        }
        List<SpellInfo> roster = EncounterRotationRoster(plan.ClassId);
        if (roster.Count == 0)
        {
            EncounterPlayerSetupDisabledWrapped(
                "No trainable actives resolved for this class - check the DBC mount.");
            return;
        }
        ImGui.SetNextItemWidth(240f * cs);
        ImGui.InputTextWithHint("##rotation-search", "search spells...",
            ref _encounterRotationSearch, 64);
        ImGui.SameLine();
        ImGui.TextDisabled($"{roster.Count} actives");

        var inRotation = new HashSet<uint>(rotation.Select(r => r.SpellId));
        float cell = MathF.Max(28f, 34f * cs);
        float spacing = ImGui.GetStyle().ItemSpacing.X;
        int perRow = Math.Max(1,
            (int)((ImGui.GetContentRegionAvail().X - 4f) / (cell + spacing)));
        int drawn = 0;
        bool added = false;
        foreach (SpellInfo spell in roster)
        {
            if (_encounterRotationSearch.Length > 0 &&
                !spell.Name.Contains(_encounterRotationSearch,
                    StringComparison.OrdinalIgnoreCase)) continue;
            if (drawn++ % perRow != 0) ImGui.SameLine();
            ImGui.PushID((int)spell.Id);
            uint icon = _gameplayArt?.Handle(spell.IconPath) ?? 0;
            Vector2 min = ImGui.GetCursorScreenPos();
            if (icon != 0) ImGui.Image((nint)icon, new Vector2(cell, cell));
            else ImGui.Dummy(new Vector2(cell, cell));
            bool inList = inRotation.Contains(spell.Id);
            if (inList)
                ImGui.GetWindowDrawList().AddRect(min, min + new Vector2(cell, cell),
                    ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.78f, 0.22f, 0.9f)),
                    3f, ImDrawFlags.None, 2f);
            if (ImGui.IsItemHovered())
            {
                ImGui.GetWindowDrawList().AddRect(min, min + new Vector2(cell, cell),
                    ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.65f)), 3f);
                EncounterRotationSpellTooltip(spell,
                    inList ? "Already in the rotation." : "Click to add to the rotation.");
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !inList && !added)
                {
                    // One append per frame keeps the draft/undo bookkeeping simple.
                    added = true;
                    var next = rotation.Append(
                        new CombatAbilityIntent(spell.Id, spell.Name)).ToArray();
                    SetEncounterPlayerPlanDraft(plan with { Rotation = next });
                }
            }
            ImGui.PopID();
        }
        if (drawn == 0) ImGui.TextDisabled("No spell matches the search.");
        ImGui.Spacing();
        ImGui.TextWrapped("The Lab plays these as real cosmetic casts on the body - true " +
                          "spell art, real cast times and cooldowns - while damage stays " +
                          "the owner's DPS dial until the combat evaluator lands.");
    }

    /// <summary>"instant · 8s cd · 30 yd" - the one-line cadence label.</summary>
    private string EncounterRotationSpellCadence(in SpellInfo info)
    {
        string cast = info.CastTimeMs > 0 ? $"{info.CastTimeMs / 1000f:0.#}s cast" : "instant";
        uint recovery = Math.Max(info.RecoveryMs, info.CategoryRecoveryMs);
        string cooldown = recovery > 0 ? $" · {recovery / 1000f:0.#}s cd" : "";
        string range = _spellCatalog is { } catalog &&
                       catalog.TryGetRange(info.RangeIndex, out SpellRangeRow row) &&
                       row.Max > 0f && !row.Melee
            ? $" · {row.Max:0} yd" : "";
        return cast + cooldown + range;
    }

    private void EncounterRotationSpellTooltip(in SpellInfo info, string? footer = null)
    {
        if (_spellCatalog is null) return;
        SpellTooltipView view = SpellTooltipLaw.Build(info, _spellCatalog, 60);
        ImGui.BeginTooltip();
        ImGui.TextColored(new Vector4(1f, .82f, .28f, 1f), view.Name);
        if (view.Rank.Length > 0) { ImGui.SameLine(); ImGui.TextDisabled(view.Rank); }
        string line = string.Join("  ",
            new[] { view.Cost, view.Range, view.CastTime, view.Cooldown }
                .Where(part => !string.IsNullOrEmpty(part)));
        if (line.Length > 0) ImGui.TextUnformatted(line);
        if (view.Description.Length > 0)
        {
            ImGui.PushTextWrapPos(320f);
            ImGui.TextColored(new Vector4(1f, .93f, .35f, 1f), view.Description);
            ImGui.PopTextWrapPos();
        }
        if (footer is not null) ImGui.TextDisabled(footer);
        ImGui.EndTooltip();
    }

    private void DrawEncounterPlanContext(EncounterActorSpec actor)
    {
        ImGui.TextColored(new Vector4(.45f, .9f, 1f, 1f),
            "The saved character plan is encounter-agnostic.");
        ImGui.TextWrapped("It contains no creature entry, encounter key, phase key, boss name, " +
                          "or group-size assumption. Encounter choreography layers over it here; " +
                          "the reusable plan itself does not change.");
        ImGui.TextWrapped("Targets are semantic — self, role ordinals, or lowest-health ally. " +
                          "Named-character assignments belong to a separate roster layer and " +
                          "cannot leak into this reusable plan.");

        ImGui.SeparatorText("Current context (not stored in this plan)");
        ImGui.Text($"Encounter: {_encounterDefinition?.Name ?? "none loaded"}");
        ImGui.Text($"Phase: {_encounterSim?.Definition.Phase(_encounterSim.PhaseKey)?.Name ?? "none"}");
        RaidPhaseDirective? directive = _encounterSim is { } sim
            ? PlaybookDirectiveFor(sim.PhaseKey, actor.Job) : null;
        ImGui.Text($"Role directive: {(directive is null ? "none" : directive.Kind.ToString())}");
        int authoredMoves = actor.Moves?.Count ?? 0;
        ImGui.Text($"Explicit character orders: {authoredMoves}");
        ImGui.TextWrapped("Precedence: direct control and game legality → explicit waypoint/RTS " +
                          "orders → encounter role directive → reusable movement doctrine → " +
                          "ability priorities → fallback.");
    }

    private void DrawEncounterPlanExplain(EncounterActorSpec actor, CombatPlan plan)
    {
        ImGui.SeparatorText("Resolved plan");
        ImGui.TextWrapped(EncounterCombatPlanSummary(plan));

        SimActor? live = _encounterSim?.Actors.FirstOrDefault(candidate => candidate.Key == actor.Key);
        if (live is not null)
        {
            ImGui.SeparatorText("Why right now?");
            ImGui.Text($"Movement: {EncounterIntentName(live.CurrentFollowTargetKey, "not following")}");
            ImGui.Text($"Protect: {EncounterIntentName(live.CurrentProtectTargetKey, "no applicable ally")}");
            ImGui.Text($"Enemy: {EncounterIntentName(live.CurrentEnemyTargetKey, "no legal enemy")}");
            if (live.MoveTarget is not null)
                ImGui.TextColored(new Vector4(1f, .78f, .25f, 1f),
                    "An explicit movement order currently owns translation.");
        }
        else EncounterPlayerSetupDisabledWrapped("No simulation snapshot is available for this body.");

        ImGui.SeparatorText("Honesty boundary");
        ImGui.TextWrapped("Encounter Lab executes follow doctrine and routes each body's owner-authored " +
                          "DPS to its resolved hostile target. It does not model friendly damage, mana, " +
                          "global cooldowns, or healing amounts. Protection priorities are resolved and " +
                          "displayed, but the Lab does not invent a heal cast. An authored rotation " +
                          "plays as COSMETIC casts - real spell art on the real cast/cooldown cadence - " +
                          "without changing any number in the fight. The " +
                          "eventual SuperUI evaluator must execute this same typed plan using real " +
                          "server-authoritative combat state.");

        foreach (string warning in EncounterCombatPlanWarnings(plan))
            ImGui.TextColored(new Vector4(1f, .48f, .3f, 1f), $"! {warning}");
    }

    private void DrawEncounterPlanFooter(int actorIndex, EncounterActorSpec actor, CombatPlan plan)
    {
        bool canUndo = _encounterPlayerPlanUndo.Count > 0;
        if (EncounterPanelButton("Undo", enabled: canUndo, compact: true) && canUndo)
        {
            _encounterPlayerPlanRedo.Add(plan);
            _encounterPlayerPlanDraft = _encounterPlayerPlanUndo[^1];
            _encounterPlayerPlanUndo.RemoveAt(_encounterPlayerPlanUndo.Count - 1);
            _encounterPlayerPlanDirty = _encounterPlayerPlanDraft != _encounterPlayerPlanBaseline;
        }
        ImGui.SameLine();
        bool canRedo = _encounterPlayerPlanRedo.Count > 0;
        if (EncounterPanelButton("Redo", enabled: canRedo, compact: true) && canRedo)
        {
            _encounterPlayerPlanUndo.Add(_encounterPlayerPlanDraft!);
            _encounterPlayerPlanDraft = _encounterPlayerPlanRedo[^1];
            _encounterPlayerPlanRedo.RemoveAt(_encounterPlayerPlanRedo.Count - 1);
            _encounterPlayerPlanDirty = _encounterPlayerPlanDraft != _encounterPlayerPlanBaseline;
        }
        ImGui.SameLine();
        if (EncounterPanelButton("Revert", enabled: _encounterPlayerPlanDirty, compact: true) &&
            _encounterPlayerPlanDirty)
        {
            _encounterPlayerPlanDraft = _encounterPlayerPlanBaseline ??
                                        CreateEncounterPlayerPlanBaseline(actor);
            _encounterPlayerPlanContinuousUndoBaseline = null;
            _encounterPlayerPlanDirty = false;
            _encounterPlayerPlanUndo.Clear();
            _encounterPlayerPlanRedo.Clear();
        }

        float applyWidth = EncounterPanelButtonWidth("Save & apply");
        float closeWidth = EncounterPanelButtonWidth("Close");
        ImGui.SameLine(MathF.Max(ImGui.GetCursorPosX(),
            ImGui.GetWindowContentRegionMax().X - applyWidth - closeWidth -
            ImGui.GetStyle().ItemSpacing.X));
        if (EncounterPanelButton("Save & apply", enabled: _encounterPlayerPlanDirty))
        {
            CombatPlan applied = _encounterPlayerPlanDraft!;
            // Library save: the rotation is a reusable slot keyed by its own id, then
            // assigned to this body by reference (AssignBodySlots stitches it back into
            // the inline plan the sim reads, folding in the positioning slot's movement).
            if (EncounterCombatPlanStoreRef.UpsertLibrary(applied, out CombatPlan stored))
            {
                _encounterPlayerPlanDraft = stored;
                _encounterPlayerPlanBaseline = stored;
                _encounterPlayerPlanContinuousUndoBaseline = null;
                _encounterPlayerPlanDirty = false;
                _encounterPlayerPlanUndo.Clear();
                _encounterPlayerPlanRedo.Clear();
                AssignBodySlots(actorIndex, actor, rotation: stored);
                AddChatMessage($"{actor.Name}: rotation '{stored.Name}' saved and applied.");
            }
            else
            {
                string detail = EncounterCombatPlanStoreRef.Errors.LastOrDefault() ??
                                "unknown persistence error";
                AddChatMessage($"{actor.Name}: combat plan was not saved ({detail}).");
            }
        }
        ImGui.SameLine();
        if (EncounterPanelButton("Close"))
            _encounterPlayerSetupKey = null;
    }

    private bool DrawEncounterSubjectCombo(string label, CombatSubject current,
        bool allowLowestHealth, out CombatSubject selected)
    {
        selected = current;
        bool changed = false;
        string preview = EncounterCombatSubjectLabel(current);
        ImGui.SetNextItemWidth(220f * CreatorUiScale);
        if (!ImGui.BeginCombo(label, preview)) return false;

        var choices = new List<CombatSubject>
        {
            CombatSubject.Tank(1), CombatSubject.Tank(2), CombatSubject.Self,
        };
        if (allowLowestHealth) choices.Add(CombatSubject.LowestHealth);

        foreach (CombatSubject choice in choices)
        {
            bool isSelected = choice == current;
            if (ImGui.Selectable(EncounterCombatSubjectLabel(choice), isSelected))
            { selected = choice; changed = true; }
            if (isSelected) ImGui.SetItemDefaultFocus();
        }
        ImGui.EndCombo();
        return changed;
    }

    private string EncounterCombatSubjectLabel(CombatSubject subject) => subject.Kind switch
    {
        CombatSubjectKind.Self => "self",
        CombatSubjectKind.LowestHealthAlly => "lowest-health ally",
        CombatSubjectKind.RoleOrdinal =>
            $"{subject.Role.ToString().ToLowerInvariant()} {Math.Max(subject.Ordinal, 1)}",
        _ => subject.Kind.ToString(),
    };

    private string EncounterIntentName(string? actorKey, string fallback) => actorKey is null
        ? fallback
        : _encounterScenario.FirstOrDefault(actor => actor.Key == actorKey)?.Name ?? actorKey;

    private string EncounterCombatPlanSummary(CombatPlan plan)
    {
        var sentences = new List<string>();
        CombatMovementPlan movement = plan.Movement ?? new CombatMovementPlan();
        sentences.Add(movement.Mode switch
        {
            CombatMovementMode.Follow =>
                $"Follow {EncounterCombatSubjectLabel(movement.Anchor ?? CombatSubject.Tank(1))} " +
                $"at {movement.MinRangeYards:0}–{movement.MaxRangeYards:0} yd.",
            CombatMovementMode.HoldPosition => "Hold position unless explicitly ordered.",
            _ => "Move independently.",
        });
        sentences.Add(plan.Engagement switch
        {
            CombatEngagementMode.NeverInitiate => "Never initiate combat.",
            CombatEngagementMode.AssistAnchor => "Assist the follow target's engagement.",
            CombatEngagementMode.DefendGroup => "Engage to defend the group.",
            _ => "May acquire an engagement autonomously.",
        });
        if (plan.EnemyPriorities is { Count: > 0 } enemies)
            sentences.Add("Enemy order: " + string.Join(" → ", enemies.Where(row => row.Enabled)
                .Select(row => EncounterEnemyPriorityLabel(row.Kind))) + ".");
        if (plan.SupportPriorities is { Count: > 0 } support)
            sentences.Add("Protect: " + string.Join(" → ", support.Where(row => row.Enabled)
                .Select(row => $"{EncounterCombatSubjectLabel(row.Target)} below " +
                               $"{row.OnlyWhenBelowHealthPercent:0}%")) + ".");
        if (plan.Rotation is { Count: > 0 } rotation)
        {
            int active = rotation.Count(row => row.Enabled);
            if (active > 0)
                sentences.Add($"Rotation: {active} " +
                    $"{ClassSpellList.ClassName(plan.ClassId)} " +
                    (active == 1 ? "ability." : "abilities."));
        }
        return string.Join(" ", sentences);
    }

    private static string EncounterEnemyPriorityLabel(CombatEnemyKind kind) => kind switch
    {
        CombatEnemyKind.AnyAdd => "active adds",
        CombatEnemyKind.CurrentEnemy => "current enemy",
        _ => "primary encounter target",
    };

    private static string EncounterResponsibilityLabel(CombatResponsibility responsibility) =>
        responsibility switch
        {
            CombatResponsibility.DispelMagic => "Dispel magic",
            CombatResponsibility.RemoveCurse => "Remove curses",
            CombatResponsibility.CleansePoison => "Cleanse poison",
            CombatResponsibility.CrowdControlAdds => "Crowd-control adds",
            _ => responsibility.ToString(),
        };

    private static IEnumerable<string> EncounterCombatPlanWarnings(CombatPlan plan)
    {
        if (plan.Movement is { Mode: CombatMovementMode.Follow } movement)
        {
            if (movement.Anchor is null) yield return "Follow requires an anchor.";
            if (movement.Anchor?.Kind == CombatSubjectKind.Self)
                yield return "A character cannot follow itself.";
            if (movement.MinRangeYards > movement.MaxRangeYards)
                yield return "Minimum follow range exceeds maximum range.";
        }
        if (plan.Engagement == CombatEngagementMode.AssistAnchor &&
            plan.Movement is not { Mode: CombatMovementMode.Follow, Anchor: not null })
            yield return "Assist-follow-target needs a Follow anchor; choose Follow or another engagement policy.";
        if (plan.EnemyPriorities is null || !plan.EnemyPriorities.Any(row => row.Enabled))
            yield return "No enemy priority is enabled; only fallback behavior can attack.";
    }

    private static void EncounterPlayerSetupDisabledWrapped(string text)
    {
        ImGui.PushTextWrapPos(0f);
        ImGui.TextDisabled(text);
        ImGui.PopTextWrapPos();
    }
}

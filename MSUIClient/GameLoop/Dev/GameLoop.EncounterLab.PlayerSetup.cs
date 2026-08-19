using System.Numerics;
using ImGuiNET;
using MSUIClient.World.Encounters;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// Encounter Lab — per-player setup modal.
//
// A plain click on a raid puppet only selects it. Selection slides a compact,
// game-native Character Customizer button onto the right edge; this modal opens
// only when that button (or the Scenario rules button) is explicitly clicked.
// Base rules are live simulation inputs. Rotation source and spell queues
// deliberately begin as an honest reserved section: later they can load a
// SuperUI rotation when one is available or accept an authored queue without
// changing this interaction model.
// ─────────────────────────────────────────────────────────────────────────────

public sealed partial class GameLoop
{
    private const string EncounterPlayerSetupPopupId = "Player Setup###encounter-player-setup";
    private string? _encounterPlayerSetupKey;
    private bool _encounterPlayerSetupRequested;
    private string? _encounterPlayerSetupLauncherKey;
    private double _encounterPlayerSetupLauncherAppearedAt;

    private const double EncounterPlayerSetupLauncherPopSeconds = 0.24;

    private void OpenEncounterPlayerSetup(string key)
    {
        if (_encounterOrbitDragging || _encounterOrientSpinning) return;
        if (_encounterScenario.FirstOrDefault(actor => actor.Key == key) is not
            { Role: EncounterActorRole.Friendly }) return;
        _encounterPlayerSetupKey = key;
        _encounterPlayerSetupRequested = true;
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
        float margin = 10f * s;
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
        DrawEncounterPlayerSetupLauncher();
        if (!_encounterLabOpen)
        {
            _encounterPlayerSetupKey = null;
            _encounterPlayerSetupRequested = false;
            _encounterPlayerSetupLauncherKey = null;
            return;
        }

        float cs = CreatorUiScale;
        Vector2 display = ImGui.GetIO().DisplaySize;
        Vector2 size = new(470f * cs, 0f);
        ImGui.SetNextWindowPos(new Vector2(
            MathF.Max(12f, display.X * .5f - size.X * .5f),
            MathF.Max(12f, display.Y * .22f)), ImGuiCond.Appearing);

        PushCreatorStyle();
        if (_encounterPlayerSetupRequested)
        {
            ImGui.OpenPopup(EncounterPlayerSetupPopupId);
            _encounterPlayerSetupRequested = false;
        }

        bool open = true;
        ImGui.SetNextWindowSize(new Vector2(size.X, 0f), ImGuiCond.Appearing);
        ImGui.SetNextWindowSizeConstraints(new Vector2(size.X, 0f),
            new Vector2(size.X, MathF.Max(220f * cs, display.Y - 24f)));
        bool showing = ImGui.BeginPopupModal(EncounterPlayerSetupPopupId, ref open,
            ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoTitleBar);
        if (showing)
        {
            if (_encounterPlayerSetupKey is null)
            {
                ImGui.CloseCurrentPopup();
                ImGui.EndPopup();
                PopCreatorStyle();
                return;
            }
            ImGui.SetWindowFontScale(CreatorTextScale);
            DrawEncounterPlayerSetupBody();
            ImGui.SetWindowFontScale(1f);
            ImGui.EndPopup();
        }
        if (!open) _encounterPlayerSetupKey = null;
        PopCreatorStyle();
    }

    private void DrawEncounterPlayerSetupBody()
    {
        ImGui.TextColored(new Vector4(1f, .82f, .28f, 1f), "Character Customizer");
        ImGui.Separator();

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
            {
                ImGui.CloseCurrentPopup();
                _encounterPlayerSetupKey = null;
            }
            return;
        }

        ImGui.TextColored(RoleColourVec4(actor.Role, actor.Job), actor.Name);
        ImGui.SameLine();
        ImGui.TextDisabled(actor.Job == RaidJob.None ? "(friendly)" : $"({actor.Job})");
        EncounterPlayerSetupDisabledWrapped(
            "Per-player encounter behavior. Changes rebuild the same seeded fight.");

        ImGui.SeparatorText("Base rules");
        bool alwaysFaceBoss = actor.PlayerRules?.AlwaysFaceBoss == true;
        if (ImGui.Checkbox("Always face boss", ref alwaysFaceBoss))
        {
            EncounterPlayerRules rules = actor.PlayerRules ?? new EncounterPlayerRules();
            _encounterScenario[actorIndex] = actor = actor with
            {
                PlayerRules = rules with { AlwaysFaceBoss = alwaysFaceBoss },
            };
            RebuildEncounterSimKeepingView();
            AddChatMessage($"{actor.Name}: always face boss " +
                           (alwaysFaceBoss ? "enabled." : "disabled."));
        }
        ImGui.TextWrapped("Keeps this player aimed at the boss on every simulation step, " +
                          "including while running through her or moving toward a waypoint.");
        EncounterPlayerSetupDisabledWrapped(alwaysFaceBoss
            ? "ACTIVE · overrides movement direction and waypoint arrival facing"
            : "Off · movement direction and waypoint arrival facing control the pose");

        ImGui.SeparatorText("Rotation");
        EncounterPlayerSetupDisabledWrapped(
            "Reserved for the next layer; no rotation is being invented here yet.");
        float rotationAvail = ImGui.GetContentRegionAvail().X;
        float rotationGap = ImGui.GetStyle().ItemSpacing.X;
        float loadWidth = EncounterPanelButtonWidth("Load from SuperUI");
        float queueWidth = EncounterPanelButtonWidth("Build custom spell queue");
        bool rotationSideBySide = loadWidth + rotationGap + queueWidth <= rotationAvail;
        float rotationButtonWidth = rotationSideBySide
            ? (rotationAvail - rotationGap) * .5f
            : MathF.Max(rotationAvail, MathF.Max(loadWidth, queueWidth));
        EncounterPanelButtonSized("Load from SuperUI",
            new Vector2(rotationButtonWidth, 0f), enabled: false);
        if (rotationSideBySide) ImGui.SameLine();
        EncounterPanelButtonSized("Build custom spell queue",
            new Vector2(rotationButtonWidth, 0f), enabled: false);
        ImGui.TextWrapped("When available, this section will choose a SuperUI-provided rotation " +
                          "or an on-the-spot ordered spell queue for this player.");

        ImGui.Separator();
        float closeWidth = EncounterPanelButtonWidth("Close");
        ImGui.SetCursorPosX(MathF.Max(ImGui.GetCursorPosX(),
            ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - closeWidth));
        if (EncounterPanelButtonSized("Close", new Vector2(closeWidth, 0f)))
        {
            ImGui.CloseCurrentPopup();
            _encounterPlayerSetupKey = null;
        }
    }

    private static void EncounterPlayerSetupDisabledWrapped(string text)
    {
        ImGui.PushTextWrapPos(0f);
        ImGui.TextDisabled(text);
        ImGui.PopTextWrapPos();
    }
}

using System.Numerics;
using ImGuiNET;
using MSUIClient.World.Encounters;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// Encounter Lab — per-player setup modal.
//
// A plain click on a raid puppet selects it and opens this modal. Base rules are
// live simulation inputs. Rotation source and spell queues deliberately begin as
// an honest reserved section: later they can load a SuperUI rotation when one is
// available or accept an authored queue without changing this interaction model.
// ─────────────────────────────────────────────────────────────────────────────

public sealed partial class GameLoop
{
    private const string EncounterPlayerSetupPopupId = "Player Setup###encounter-player-setup";
    private string? _encounterPlayerSetupKey;
    private bool _encounterPlayerSetupRequested;

    private void OpenEncounterPlayerSetup(string key)
    {
        if (_encounterScenario.FirstOrDefault(actor => actor.Key == key) is not
            { Role: EncounterActorRole.Friendly }) return;
        _encounterPlayerSetupKey = key;
        _encounterPlayerSetupRequested = true;
    }

    /// <summary>Drawn from the always-called encounter pop-out pass, so the setup modal
    /// remains alive whether the Action Timeline window itself is open or closed.</summary>
    private void DrawEncounterPlayerSetupModal()
    {
        if (!_encounterLabOpen)
        {
            _encounterPlayerSetupKey = null;
            _encounterPlayerSetupRequested = false;
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
        bool showing = ImGui.BeginPopupModal(EncounterPlayerSetupPopupId, ref open,
            ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoCollapse);
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
        int actorIndex = _encounterPlayerSetupKey is { } key
            ? _encounterScenario.FindIndex(actor => actor.Key == key)
            : -1;
        if (actorIndex < 0 || _encounterScenario[actorIndex] is not
            { Role: EncounterActorRole.Friendly } actor)
        {
            ImGui.TextDisabled("This player is no longer in the encounter scenario.");
            if (ImGui.Button("Close", new Vector2(-1f, 0f)))
            {
                ImGui.CloseCurrentPopup();
                _encounterPlayerSetupKey = null;
            }
            return;
        }

        ImGui.TextColored(RoleColourVec4(actor.Role, actor.Job), actor.Name);
        ImGui.SameLine();
        ImGui.TextDisabled(actor.Job == RaidJob.None ? "(friendly)" : $"({actor.Job})");
        ImGui.TextDisabled("Per-player encounter behavior. Changes rebuild the same seeded fight.");

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
        ImGui.TextDisabled(alwaysFaceBoss
            ? "ACTIVE · overrides movement direction and waypoint arrival facing"
            : "Off · movement direction and waypoint arrival facing control the pose");

        ImGui.SeparatorText("Rotation");
        ImGui.TextDisabled("Reserved for the next layer; no rotation is being invented here yet.");
        ImGui.BeginDisabled();
        ImGui.Button("Load from SuperUI", new Vector2(214f * CreatorUiScale, 0f));
        ImGui.SameLine();
        ImGui.Button("Build custom spell queue", new Vector2(214f * CreatorUiScale, 0f));
        ImGui.EndDisabled();
        ImGui.TextWrapped("When available, this section will choose a SuperUI-provided rotation " +
                          "or an on-the-spot ordered spell queue for this player.");

        ImGui.Separator();
        float closeWidth = ImGui.CalcTextSize("Close").X + 28f * CreatorUiScale;
        ImGui.SetCursorPosX(MathF.Max(ImGui.GetCursorPosX(),
            ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - closeWidth));
        if (ImGui.Button("Close", new Vector2(closeWidth, 0f)))
        {
            ImGui.CloseCurrentPopup();
            _encounterPlayerSetupKey = null;
        }
    }
}

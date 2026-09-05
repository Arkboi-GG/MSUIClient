using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    /// <summary>Improved UI player frame, drawn instead of <see cref="DrawPlayerFrame"/> when
    /// <see cref="Engine.GameSettings.ControlSettings.ImprovedUI"/> is set. Always shown.</summary>
    private void DrawImprovedPlayerFrame()
    {
        if ((_net is null && !HudPreview) ||
            !_entities.TryGet(ControlledGuid, out WorldEntity player)) return;
        string name = ControlledGuid == LocalPlayerGuid
            ? _net?.PlayerName ?? "Preview"
            : ResolveUnitName(ControlledGuid);
        DrawImprovedUnitFrame("improved-player-frame", "Player frame (Improved UI)",
            -ImprovedUnitFrameLaw.SideOffsetX, player, name, UiGoldU32(), small: false);
    }

    /// <summary>Improved UI target frame, drawn instead of <see cref="DrawTargetFrame"/> when
    /// <see cref="Engine.GameSettings.ControlSettings.ImprovedUI"/> is set. Shown only while the
    /// player has a target, same as the frame it replaces.</summary>
    private void DrawImprovedTargetFrame()
    {
        ulong targetGuid = _selectionGuid != 0 ? _selectionGuid : _hudEditMode ? ControlledGuid : 0;
        if (targetGuid == 0 || !_entities.TryGet(targetGuid, out WorldEntity target)) return;
        FactionReaction reaction = ReactionTargetTowardPlayer(target);
        string name = target.IsPlayer
            ? _playerNames.GetValueOrDefault(target.Guid, "Player")
            : ResolveCreatureOrPetName(target, $"Creature {target.Entry}");
        DrawImprovedUnitFrame("improved-target-frame", "Target frame (Improved UI)",
            ImprovedUnitFrameLaw.SideOffsetX, target, name,
            ReactionColorU32(reaction, target.IsPlayer, target.IsDead), small: false);
    }

    /// <summary>Improved UI target-of-target frame. No vanilla equivalent exists to replace.
    /// Shown only while the player's target itself has a target.</summary>
    private void DrawImprovedTargetOfTargetFrame()
    {
        if (_selectionGuid == 0 || !_entities.TryGet(_selectionGuid, out WorldEntity target)) return;
        ulong totGuid = target.Fields.Target ?? 0;
        if (totGuid == 0 || !_entities.TryGet(totGuid, out WorldEntity tot)) return;
        FactionReaction reaction = ReactionTargetTowardPlayer(tot);
        string name = tot.IsPlayer
            ? _playerNames.GetValueOrDefault(tot.Guid, "Player")
            : ResolveCreatureOrPetName(tot, $"Creature {tot.Entry}");
        DrawImprovedUnitFrame("improved-tot-frame", "Target of target frame (Improved UI)",
            0f, tot, name, ReactionColorU32(reaction, tot.IsPlayer, tot.IsDead), small: true);
    }

    /// <summary>Shared draw for all three Improved UI frames: a name row, a health bar, and
    /// (unless <paramref name="small"/>, which the target-of-target frame passes) a power bar
    /// below it. Flat drawn rects and the same generic status-bar texture Player Power Bars
    /// already fills bars with — no authored frame art.</summary>
    private void DrawImprovedUnitFrame(string id, string label, float offsetX, WorldEntity unit,
        string name, uint nameColor, bool small)
    {
        float width = small ? ImprovedUnitFrameLaw.TotFrameWidth : ImprovedUnitFrameLaw.FrameWidth;
        float height = small ? ImprovedUnitFrameLaw.TotFrameHeight : ImprovedUnitFrameLaw.FrameHeight;
        HudFrameResult frame = HudFrame(id, label,
            HudPlacement.At(HudAnchor.Bottom, offsetX, -ImprovedUnitFrameLaw.BottomRise),
            new Vector2(width, height));
        if (frame.Hidden) return;

        float s = frame.Scale;
        Vector2 p = frame.ScreenMin;
        ImGui.SetNextWindowPos(p, ImGuiCond.Always);
        ImGui.SetNextWindowSize(frame.ScreenSize, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoInputs |
            ImGuiWindowFlags.NoBringToFrontOnFocus;
        if (!ImGui.Begin($"##{id}", flags)) { ImGui.End(); return; }
        ImDrawListPtr dl = ImGui.GetWindowDrawList();

        string caption = unit.Level > 0 ? $"{unit.Level} {name}" : name;
        DrawUnitFrameText(dl, p + new Vector2(width * 0.5f, ImprovedUnitFrameLaw.NameRowHeight * 0.5f) * s,
            caption, 10f * s, nameColor);

        Vector2 healthMin = p + new Vector2(0f, ImprovedUnitFrameLaw.NameRowHeight) * s;
        Vector2 healthSize = new Vector2(width, ImprovedUnitFrameLaw.HealthBarHeight) * s;
        DrawImprovedBar(dl, healthMin, healthSize, unit.HealthFraction, new Vector4(0, 1, 0, 1),
            $"{unit.Fields.Health}/{unit.Fields.MaxHealth}", s);

        if (!small && unit.Fields.ActiveMaxPower > 0)
        {
            Vector2 powerMin = healthMin + new Vector2(0f,
                ImprovedUnitFrameLaw.HealthBarHeight + ImprovedUnitFrameLaw.BarGap) * s;
            Vector2 powerSize = new Vector2(width, ImprovedUnitFrameLaw.PowerBarHeight) * s;
            DrawImprovedBar(dl, powerMin, powerSize, unit.PowerFraction,
                PowerColor(unit.Fields.PowerType),
                $"{unit.Fields.ActivePower}/{unit.Fields.ActiveMaxPower}", s);
        }

        ImGui.End();
    }

    /// <summary>One bar: dark trough, the generic status-bar texture tinted and clipped to
    /// <paramref name="fraction"/>, a border, and a centered value caption.</summary>
    private void DrawImprovedBar(ImDrawListPtr dl, Vector2 min, Vector2 size, float fraction,
        Vector4 color, string caption, float s)
    {
        dl.AddRectFilled(min, min + size, 0x80000000);
        DrawVanillaStatusBar(dl, min, size, fraction, color);
        dl.AddRect(min, min + size, 0xff000000);
        DrawUnitFrameText(dl, min + size * 0.5f, caption, 10f * s, 0xffffffff);
    }
}

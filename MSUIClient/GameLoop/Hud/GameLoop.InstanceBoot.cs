using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private void DrawInstanceBootWarning()
    {
        UpdateInstanceBootPopup();
        var popup = DuelFrameUiLaw.Visible(_staticPopupSlots, InstanceBootUiLaw.PopupType);
        if (popup is not { } visible || _skin is null) return;
        float scale = GameplayUiScale();
        string text = InstanceBootText(ControlledGuid, _instanceBoot.Remaining(ControlledGuid, NowSeconds()));
        string[] lines = WrapTooltipText(text, "GameFontHighlight", scale,
            StaticPopupCoordinatorLaw.TextWidth * scale).ToArray();
        float textHeight = lines.Length * GameText.LinePitch("GameFontHighlight", 1);
        Vector2 origin = StaticPopupOrigin(visible.Slot, StaticPopupCoordinatorLaw.BaseWidth, scale);
        Vector2 size = DuelFrameUiLaw.PopupSize(textHeight, buttons: false) * scale;
        ImGui.SetNextWindowPos(origin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0);
        bool begun = ImGui.Begin($"##instance-boot-{visible.Slot}",
            ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoNav);
        ImGui.PopStyleVar(2);
        if (!begun) { ImGui.End(); return; }
        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        draw.PushClipRectFullScreen();
        _skin.DrawBackdrop(draw, origin, origin + size, WowSkin.Dialog);
        for (int i = 0; i < lines.Length; i++)
            GameText.DrawCentered(draw, "GameFontHighlight", lines[i],
                origin + DuelFrameUiLaw.TextLineCenter(i) * scale, scale);
        draw.PopClipRect();
        ImGui.End();
    }
}

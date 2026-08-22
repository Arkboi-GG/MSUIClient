using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private void DrawCharacterBindingsConfirmation()
    {
        (int Slot, StaticPopupCoordinatorLaw.Instance Instance)? popup =
            CharacterBindingsUiLaw.Visible(_staticPopupSlots);
        if (popup is not { } visible || _skin is null) return;

        float scale = GameplayUiScale();
        string[] lines = WrapTooltipText(CharacterBindingsUiLaw.ConfirmText,
            "GameFontHighlight", scale, CharacterBindingsUiLaw.TextWidth * scale).ToArray();
        float logicalTextHeight = lines.Length * GameText.LinePitch("GameFontHighlight", 1);
        float logicalHeight = CharacterBindingsUiLaw.Height(logicalTextHeight);
        Vector2 origin = StaticPopupOrigin(visible.Slot, CharacterBindingsUiLaw.Width, scale);
        Vector2 size = new(CharacterBindingsUiLaw.Width * scale, logicalHeight * scale);

        // ImGui supplies only the fixed host and hit regions. Every modal and child rectangle is
        // resolved by CharacterBindingsUiLaw / StaticPopupCoordinatorLaw.
        ImGui.SetNextWindowPos(origin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoNav;
        bool begun = ImGui.Begin($"##character-bindings-popup-{visible.Slot}", flags);
        ImGui.PopStyleVar(2);
        if (!begun) { ImGui.End(); return; }

        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        draw.PushClipRectFullScreen();
        _skin.DrawBackdrop(draw, origin, origin + size, WowSkin.Dialog);
        for (int i = 0; i < lines.Length; i++)
            GameText.DrawCentered(draw, "GameFontHighlight", lines[i],
                origin + new Vector2(CharacterBindingsUiLaw.Width * .5f,
                    CharacterBindingsUiLaw.TextTop +
                    (i + .5f) * GameText.LinePitch("GameFontHighlight", 1)) * scale, scale);

        float buttonTop = CharacterBindingsUiLaw.ButtonTop(logicalTextHeight);
        bool accept = DrawPartyInviteButton(draw, "StaticPopup1Button1",
            CharacterBindingsUiLaw.AcceptText,
            origin + new Vector2(CharacterBindingsUiLaw.ButtonOneX, buttonTop) * scale,
            scale, capture: false, default);
        bool cancel = DrawPartyInviteButton(draw, "StaticPopup1Button2",
            CharacterBindingsUiLaw.CancelText,
            origin + new Vector2(CharacterBindingsUiLaw.ButtonTwoX, buttonTop) * scale,
            scale, capture: false, default);
        draw.PopClipRect();
        ImGui.End();

        if (accept)
            ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Click(
                _staticPopupSlots, visible.Slot, buttonIndex: 1));
        else if (cancel)
            ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Click(
                _staticPopupSlots, visible.Slot, buttonIndex: 2));
    }
}

using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private bool _staticPopupParityOpen;

    private void DrawStaticPopupParityFrame()
    {
        if (!_staticPopupParityOpen || _skin is null) return;
        float s = GameplayUiScale();
        Vector2 display = ImGui.GetIO().DisplaySize;
        Vector2 size = new Vector2(320, 72) * s;
        Vector2 origin = new((display.X - size.X) * .5f, 128 * s);
        ImGui.SetNextWindowPos(origin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        if (!ImGui.Begin("##static-popup-parity", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoNav))
        { ImGui.End(); return; }

        BeginUiParityFrame(origin, s);
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        dl.PushClipRectFullScreen();
        _skin.DrawBackdrop(dl, origin, origin + size, WowSkin.Dialog);
        _skin.GlueImage(dl, "dialog.alert", origin + new Vector2(12, 4) * s, origin + new Vector2(76, 68) * s);
        dl.PopClipRect();

        string message = "Delete this item permanently?";
        Vector2 ts = ImGui.CalcTextSize(message);
        dl.AddText(origin + new Vector2((size.X - ts.X) * .5f, 16 * s), 0xffffffff, message);
        DrawPopupButton(dl, "YES", origin + new Vector2(28, 45) * s, s);
        DrawPopupButton(dl, "NO", origin + new Vector2(164, 45) * s, s);

        CollectUiParityDraw("StaticPopup1", "Frame", origin, size, "",
            new("", 0, "IMGUI_HOST", "TOP", "", "", 0, -128));
        CollectUiParityDraw("StaticPopup1Text", "FontString", origin + new Vector2(15, 16) * s,
            new Vector2(290, 0) * s, "StaticPopup1", new("", 0xffffffff, "IMGUI_TEXT", "TOP", "", "", 0, -16, @"Fonts\FRIZQT__.TTF", 12));
        CollectUiParityDraw("StaticPopup1AlertIcon", "Texture", origin + new Vector2(12, 4) * s,
            new Vector2(64) * s, "StaticPopup1", new(@"Interface\DialogFrame\DialogAlertIcon", 0xffffffff, "IMGUI_IMAGE", "LEFT", "", "", 12, 0));
        CollectUiParityDraw("StaticPopup1Button1", "Button", origin + new Vector2(28, 45) * s,
            new Vector2(128, 20) * s, "StaticPopup1", new(@"Interface\Buttons\UI-DialogBox-Button-Up", 0, "IMGUI_BUTTON", "", "", "", 0, 0));
        CollectUiParityDraw("StaticPopup1Button2", "Button", origin + new Vector2(164, 45) * s,
            new Vector2(128, 20) * s, "StaticPopup1", new(@"Interface\Buttons\UI-DialogBox-Button-Up", 0, "IMGUI_BUTTON", "", "", "", 0, 0));
        if (_uiParityArmed && _uiParityPanel == "static-popup") MarkUiParityFrameComplete();
        ImGui.End();
    }

    private void DrawPopupButton(ImDrawListPtr dl, string caption, Vector2 at, float s)
    {
        Vector2 buttonSize = new Vector2(128, 20) * s;
        uint handle = _skin!.TextureHandle("dialog.button.up");
        if (handle != 0) dl.AddImage((nint)handle, at, at + buttonSize, Vector2.Zero, new Vector2(1, .625f));
        Vector2 text = ImGui.CalcTextSize(caption);
        dl.AddText(at + (buttonSize - text) * .5f, 0xffffffff, caption);
    }
}

using System.Numerics;
using ImGuiNET;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private bool _uiErrorsParityOpen;

    private void DrawUiErrorsParityFrame()
    {
        if (!_uiErrorsParityOpen) return;
        float s = GameplayUiScale();
        Vector2 display = ImGui.GetIO().DisplaySize;
        Vector2 size = new(512 * s, 60 * s);
        Vector2 origin = new((display.X - size.X) * .5f, 96 * s);
        BeginUiParityFrame(origin, s);

        ImDrawListPtr draw = ImGui.GetForegroundDrawList();
        ImFontPtr font = ImGui.GetFont();
        const string message = "You are too far away!";
        float fontSize = 16 * s;
        Vector2 measured = ImGui.CalcTextSize(message) * (fontSize / MathF.Max(ImGui.GetFontSize(), 1));
        Vector2 textAt = origin + new Vector2((size.X - measured.X) * .5f, 2 * s);
        uint red = ImGui.ColorConvertFloat4ToU32(new Vector4(1, .0627f, .0627f, 1));
        draw.AddText(font, fontSize, textAt, red, message);

        CollectUiParityDraw("UIErrorsFrame", "MessageFrame", origin, size, "",
            new("", 0, "IMGUI_FOREGROUND", "TOP", "", "", 0, -96));
        CollectUiParityDraw("UIErrorsFrame/FontString", "FontString", origin, size, "UIErrorsFrame",
            new("", red, "IMGUI_FOREGROUND", "", "", "", 0, 0, @"Fonts\FRIZQT__.TTF", 16));
        if (_uiParityArmed && _uiParityPanel == "ui-errors") MarkUiParityFrameComplete();
    }
}

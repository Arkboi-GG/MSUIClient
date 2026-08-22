using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private readonly UiErrorsFrameState _uiErrors = new();
    private bool _uiErrorsParityOpen;

    private void ResetUiErrors() => _uiErrors.Clear();

    private void DrawUiErrorsParityFrame()
    {
        double now = NowSeconds();
        IReadOnlyList<UiErrorsFrameState.VisibleMessage> live = _uiErrors.Visible(now);
        bool parityFixture = _uiErrorsParityOpen && live.Count == 0;
        if (live.Count == 0 && !parityFixture) return;

        float scale = GameplayUiScale();
        UiErrorsFrameUiLaw.ScreenRect frame = UiErrorsFrameUiLaw.FrameRect(
            ImGui.GetIO().DisplaySize, scale);
        if (_uiErrorsParityOpen) BeginUiParityFrame(frame.Min, scale);
        ImDrawListPtr draw = ImGui.GetForegroundDrawList(); // HIGH strata

        int count = parityFixture ? 1 : live.Count;
        for (int row = 0; row < count; row++)
        {
            UiErrorsFrameState.VisibleMessage message = parityFixture
                ? new("You are too far away!", UiMessageKind.Error, 1f)
                : live[row];
            Vector4 color = UiErrorsFrameUiLaw.Color(message.Kind);
            color.W *= message.Alpha;
            GameText.DrawCentered(draw, UiErrorsFrameUiLaw.Font, message.Text,
                frame.Min + UiErrorsFrameUiLaw.LineCenter(row) * scale, scale,
                ImGui.ColorConvertFloat4ToU32(color));
        }

        if (_uiErrorsParityOpen)
        {
            uint red = ImGui.ColorConvertFloat4ToU32(UiErrorsFrameUiLaw.ErrorColor);
            CollectUiParityDraw("UIErrorsFrame", "MessageFrame", frame.Min, frame.Size, "",
                new("", 0, "IMGUI_FOREGROUND", "TOP", "", "", 0,
                    -UiErrorsFrameUiLaw.TopOffset));
            CollectUiParityDraw("UIErrorsFrame/FontString", "FontString", frame.Min,
                frame.Size, "UIErrorsFrame", new("", red, "IMGUI_FOREGROUND", "", "", "",
                    0, 0, @"Fonts\FRIZQT__.TTF", 16));
            if (_uiParityArmed && _uiParityPanel == "ui-errors") MarkUiParityFrameComplete();
        }
    }
}

using System.Numerics;
using ImGuiNET;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private const float GameplayBarWidth = 1024f;
    private const float GameplayBarHeight = 53f;

    // The 1.12 FrameXML canvas is authored against a 1024x768 logical screen.
    // The historical MSUI default of 1.8 is treated as the neutral user choice;
    // resolution supplies the physical-pixel scale instead of capping it.
    private float GameplayUiScale()
    {
        Vector2 display = ImGui.GetIO().DisplaySize;
        float resolutionScale = MathF.Min(display.X / 1024f, display.Y / 768f);
        float preference = Math.Clamp((_skin?.Scale ?? 1.8f) / 1.8f, 0.5f, 2f);
        return MathF.Max(0.5f, resolutionScale * preference);
    }

    private static Vector2 GameplayBarMin(Vector2 display, float scale) =>
        new((display.X - GameplayBarWidth * scale) * 0.5f,
            display.Y - GameplayBarHeight * scale);
}

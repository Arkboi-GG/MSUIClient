using System.Numerics;
using ImGuiNET;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private const float GameplayBarWidth = 1024f;
    private const float GameplayBarHeight = 53f;
    // Empirical A/B against the 2048x1152 1.12 client: the previous 1.8-as-100% calibration made
    // every authored panel/tooltip about 10% too large. 2.0 is the raw FrameXML reference size;
    // the shipped 1.8 preference therefore reproduces the captured client's 90% UI scale.
    private const float GameplayReferencePreference = 2.0f;

    // The 1.12 FrameXML canvas is authored against a 1024x768 logical screen.
    // Resolution supplies the physical-pixel scale instead of capping it; UiScale remains an
    // accessibility multiplier around the empirically matched 1.8 preference.
    private float GameplayUiScale()
    {
        Vector2 display = ImGui.GetIO().DisplaySize;
        return GameplayUiScaleFor(display.X, display.Y, _skin?.Scale ?? 1.8f);
    }

    // Also used at startup (Program.Main) to size the exact-pixel gameplay text atlases before
    // the window exists - the baked em sizes are only "exact" if this same conversion chose them.
    internal static float GameplayUiScaleFor(float displayWidth, float displayHeight, float uiPreference)
    {
        float resolutionScale = MathF.Min(displayWidth / 1024f, displayHeight / 768f);
        float preference = Math.Clamp(uiPreference / GameplayReferencePreference, 0.5f, 2f);
        return MathF.Max(0.5f, resolutionScale * preference);
    }

    private static Vector2 GameplayBarMin(Vector2 display, float scale) =>
        new((display.X - GameplayBarWidth * scale) * 0.5f,
            display.Y - GameplayBarHeight * scale);
}

using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private const float GameplayBarWidth = 1024f;
    private const float GameplayBarHeight = 53f;
    // The setting is the appearance at the 1600x900 reference window. Read it from Settings—not
    // WowSkin.Scale, which menus temporarily override—then proportion it from the live framebuffer.
    // That makes maximize resize the HUD immediately without Escape open/close changing the result.
    private float GameplayUiScale()
    {
        Vector2 framebuffer = _window.FramebufferSize;
        return GameplayUiScaleFor(framebuffer.X, framebuffer.Y, Settings.Display.UiScale);
    }

    // Also used at startup (Program.Main) to size the exact-pixel gameplay text atlases.
    internal static float GameplayUiScaleFor(float displayWidth, float displayHeight, float uiPreference)
    {
        return InterfaceScaleLaw.ResolveForFramebuffer(displayWidth, displayHeight, uiPreference);
    }

    private static Vector2 GameplayBarMin(Vector2 display, float scale) =>
        new((display.X - GameplayBarWidth * scale) * 0.5f,
            display.Y - GameplayBarHeight * scale);
}

using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private bool _screenshotPending;
    private bool _screenshotWasDown;
    private bool _glueScreenshotWasDown;
    private double _screenshotStatusStarted = -1;
    private bool _screenshotStatusSucceeded;

    private void UpdateScreenshotBinding(bool typing)
    {
        bool down = BindingDown(GameBinding.Screenshot);
        if (down && !_screenshotWasDown && !typing)
        {
            // TakeScreenshot hides the previous result before asking for the next frame. Because
            // Update runs before this frame's Gui build, the old line cannot enter this capture.
            _screenshotStatusStarted = -1;
            _screenshotPending = true;
        }
        _screenshotWasDown = down;
    }

    /// <summary>
    /// The 1.12 glue screens own a separate hard-coded PrintScreen route; account bindings do not
    /// exist yet there and rebinding SCREENSHOT must not move this key. This runs before Update's
    /// pre-world early returns and feeds the same hide-before-final-frame capture transaction.
    /// </summary>
    private void UpdateGlueScreenshotInput()
    {
        bool glue = !_worldLoadStarted && (GlueFrontDoorActive ||
            _config.Server.Enabled && _net?.State != Net.NetState.InWorld);
        bool down = glue && InputKeyDown(Silk.NET.Input.Key.PrintScreen);
        if (down && !_glueScreenshotWasDown)
        {
            _screenshotStatusStarted = -1;
            _screenshotPending = true;
        }
        _glueScreenshotWasDown = down;
    }

    /// <summary>Called from ClientWindow.OnOverlayTop, after ImGui's final framebuffer draw.</summary>
    private void FinishScreenshotCapture()
    {
        if (!_screenshotPending) return;
        _screenshotPending = false;
        string? path = null;
        try
        {
            string directory = Path.Combine(_config.RepoRoot, "Screenshots");
            Directory.CreateDirectory(directory);
            DateTime now = DateTime.Now;
            int suffix = 0;
            do
            {
                path = Path.Combine(directory, ScreenshotStatusUiLaw.FileName(now, suffix));
                suffix++;
            } while (File.Exists(path));
            _screenshotStatusSucceeded = TrySaveGameplayScreenshot(path);
        }
        catch (Exception ex)
        {
            _screenshotStatusSucceeded = false;
            Console.WriteLine($"[screenshot] failed: {ex.Message}");
        }
        _screenshotStatusStarted = ImGui.GetTime();
        Console.WriteLine(_screenshotStatusSucceeded
            ? $"[screenshot] wrote {path}"
            : $"[screenshot] failed to write {path}");
    }

    private void DrawScreenshotStatus()
    {
        if (_screenshotStatusStarted < 0) return;
        double elapsed = ImGui.GetTime() - _screenshotStatusStarted;
        if (elapsed >= ScreenshotStatusUiLaw.FadeSeconds)
        {
            _screenshotStatusStarted = -1;
            return;
        }
        float alpha = ScreenshotStatusUiLaw.Alpha(elapsed);
        string text = _screenshotStatusSucceeded
            ? ScreenshotStatusUiLaw.SuccessText : ScreenshotStatusUiLaw.FailureText;
        float width = GameText.MeasureWidth("SystemFont", text, 1f);
        float height = GameText.EmPixels("SystemFont", 1f);
        uint color = ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, alpha));
        GameText.Draw(ImGui.GetForegroundDrawList(), "SystemFont", text,
            ScreenshotStatusUiLaw.TextMinimum(ImGui.GetIO().DisplaySize, width, height),
            1f, color);
    }
}

using System.Numerics;
using MSUIClient;
using MSUIClient.Engine.UI;

internal static class ScreenshotStatusClinicalChecks
{
    public static void Run()
    {
        Check(ScreenshotStatusUiLaw.SuccessText == "Screen Captured" &&
              ScreenshotStatusUiLaw.FailureText == "Screen Capture Failed" &&
              ScreenshotStatusUiLaw.FadeSeconds == 1.5f &&
              ScreenshotStatusUiLaw.TextCenter(new Vector2(1920, 1080)) ==
                  new Vector2(960, 540) &&
              ScreenshotStatusUiLaw.TextMinimum(new Vector2(1920, 1080), 200, 20) ==
                  new Vector2(860, 530) &&
              ScreenshotStatusUiLaw.Alpha(0) == 1f &&
              ScreenshotStatusUiLaw.Alpha(.75) == .5f &&
              ScreenshotStatusUiLaw.Alpha(1.5) == 0f &&
              ScreenshotStatusUiLaw.FileName(new DateTime(2026, 8, 22, 23, 7, 9)) ==
                  "WoWScrnShot_082226_230709.png" &&
              ScreenshotStatusUiLaw.FileName(new DateTime(2026, 8, 22, 23, 7, 9), 1) ==
                  "WoWScrnShot_082226_230709_2.png",
            "ScreenshotStatus text/fade/center/name law drift");

        string root = ClientConfig.FindRepoRoot();
        string bindings = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Bindings.cs"));
        string status = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Hud",
            "GameLoop.ScreenshotStatus.cs"));
        string host = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Net.cs"));
        string update = SourceText.Read(Path.Combine(root, "MSUIClient", "Program.cs"));
        string window = SourceText.Read(Path.Combine(root, "MSUIClient", "Engine",
            "ClientWindow.cs"));
        Check(bindings.Contains("GameBinding.Screenshot, \"Screen Shot\", Key.PrintScreen",
                  StringComparison.Ordinal) &&
              status.Contains("_screenshotStatusStarted = -1;", StringComparison.Ordinal) &&
              status.IndexOf("_screenshotStatusStarted = -1;", StringComparison.Ordinal) <
              status.IndexOf("_screenshotPending = true;", StringComparison.Ordinal) &&
              status.Contains("Directory.CreateDirectory(directory);", StringComparison.Ordinal) &&
              status.Contains("TrySaveGameplayScreenshot(path)", StringComparison.Ordinal) &&
              status.Contains("ImGui.GetForegroundDrawList()", StringComparison.Ordinal) &&
              status.Contains("ScreenshotStatusUiLaw.TextMinimum", StringComparison.Ordinal) &&
              !status.Contains("new Vector2", StringComparison.Ordinal) &&
              status.Contains("InputKeyDown(Silk.NET.Input.Key.PrintScreen)",
                  StringComparison.Ordinal) &&
              status.Contains("_config.Server.Enabled && _net?.State != Net.NetState.InWorld",
                  StringComparison.Ordinal) &&
              update.IndexOf("UpdateGlueScreenshotInput();", StringComparison.Ordinal) <
              update.IndexOf("if (_config.Server.Enabled && !_worldLoadStarted) return;",
                  StringComparison.Ordinal) &&
              Count(host, "DrawScreenshotStatus();") == 4 &&
              host.Contains("FinishScreenshotCapture();", StringComparison.Ordinal) &&
              window.IndexOf("_imgui.Render();", StringComparison.Ordinal) <
              window.IndexOf("OnOverlayTop?.Invoke();", StringComparison.Ordinal),
            "PrintScreen lost its binding, hide-before-capture order, final-frame readback, or status overlay");
    }

    private static int Count(string source, string value)
    {
        int count = 0;
        for (int at = 0; (at = source.IndexOf(value, at, StringComparison.Ordinal)) >= 0;
             at += value.Length) count++;
        return count;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}

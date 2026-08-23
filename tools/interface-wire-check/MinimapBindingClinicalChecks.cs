using MSUIClient;

internal static class MinimapBindingClinicalChecks
{
    public static void Run()
    {
        string root = ClientConfig.FindRepoRoot();
        string bindings = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop",
            "Panels", "GameLoop.Bindings.cs"));
        string minimap = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop",
            "Hud", "GameLoop.Minimap.cs"));
        string program = SourceText.Read(Path.Combine(root, "MSUIClient", "Program.cs"));
        Check(bindings.Contains(
                  "GameBinding.ToggleMinimap, \"Toggle Minimap\", Key.Unknown",
                  StringComparison.Ordinal) &&
              program.Contains("UpdateMinimapVisibilityBinding(typing);",
                  StringComparison.Ordinal),
            "Benilla's unbound Toggle Minimap command drifted");
        Check(bindings.Contains("down && !_toggleMinimapWasDown && !typing",
                  StringComparison.Ordinal) &&
              bindings.Contains("SetMinimapVisible(!_minimapVisible);",
                  StringComparison.Ordinal) &&
              minimap.Contains("PlayUiSound(visible ? MinimapUiLaw.OpenSound",
                  StringComparison.Ordinal),
            "Toggle Minimap escaped edge/typing dispatch or the shared visible-state seam");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

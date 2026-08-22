using MSUIClient;
using MSUIClient.Engine.UI;

internal static class UiHideClinicalChecks
{
    public static void Run()
    {
        Check(UiHideLaw.ToggleFired(true, true, false, false, false, false, false),
            "fresh ALT-bound-key chord did not fire TOGGLEUI");
        Check(UiHideLaw.ToggleFired(true, false, false) &&
              !UiHideLaw.ToggleFired(false, false, false) &&
              !UiHideLaw.ToggleFired(true, true, false) &&
              !UiHideLaw.ToggleFired(true, false, true),
            "rebindable TOGGLEUI chord edge or typing gate drifted");
        Check(!UiHideLaw.ToggleFired(true, false, false, false, false, false, false) &&
              !UiHideLaw.ToggleFired(true, true, true, false, false, false, false) &&
              !UiHideLaw.ToggleFired(true, true, false, true, false, false, false) &&
              !UiHideLaw.ToggleFired(true, true, false, false, true, false, false) &&
              !UiHideLaw.ToggleFired(true, true, false, false, false, true, false) &&
              !UiHideLaw.ToggleFired(true, true, false, false, false, false, true),
            "TOGGLEUI exact-modifier, edge, or typing gate drift");

        string root = ClientConfig.FindRepoRoot();
        string bindings = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Bindings.cs"));
        string keybindings = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Keybindings.cs"));
        string sheath = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Combat",
            "GameLoop.Sheath.cs"));
        string hud = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Combat",
            "GameLoop.CombatFeedback.cs"));
        string program = SourceText.Read(Path.Combine(root, "MSUIClient", "Program.cs"));

        Check(bindings.Contains("GameBinding.ToggleUi, \"Toggle User Interface\", Key.Z",
                  StringComparison.Ordinal) &&
              bindings.Contains("UiHideLaw.ToggleFired", StringComparison.Ordinal) &&
              bindings.Contains("_uiHidden = !_uiHidden", StringComparison.Ordinal) &&
              bindings.Contains("Alt: row.Binding == GameBinding.ToggleUi",
                  StringComparison.Ordinal) &&
              keybindings.Contains("FriendlyChord(chord)",
                  StringComparison.Ordinal),
            "TOGGLEUI binding, default chord, rebind label, or state flip drift");
        Check(program.Contains("UpdateUiHideBinding(typing);", StringComparison.Ordinal) &&
              hud.Contains("if (_uiHidden) return;", StringComparison.Ordinal) &&
              hud.Contains("_uiHidden = false;", StringComparison.Ordinal) &&
              hud.IndexOf("if (_uiHidden) return;", StringComparison.Ordinal) <
              hud.IndexOf("if (_commanderMapOpen && _freeView)", StringComparison.Ordinal),
            "TOGGLEUI no-draw consumer or world-exit reset drift");
        Check(sheath.Contains("bool physicalDown = BindingBaseDown(GameBinding.Sheath);",
                  StringComparison.Ordinal) &&
              sheath.Contains("bool acceptedDown = BindingDown(GameBinding.Sheath);",
                  StringComparison.Ordinal) &&
              sheath.Contains("_sheathKeyWasDown = physicalDown;", StringComparison.Ordinal),
            "bare sheath binding no longer excludes or consumes the ALT-Z physical edge");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}

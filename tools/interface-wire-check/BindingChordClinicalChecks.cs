using MSUIClient;
using MSUIClient.Engine.UI;
using Silk.NET.Input;

internal static class BindingChordClinicalChecks
{
    public static void Run()
    {
        CheckCodecAndFallback();
        CheckRuntimeSourceFence();
    }

    private static void CheckCodecAndFallback()
    {
        var full = new BindingChord(Key.F1, Alt: true, Control: true, Shift: true);
        Check(BindingChordLaw.Canonical(full) == "ALT-CTRL-SHIFT-F1" &&
              BindingChordLaw.TryParse("ALT-CTRL-SHIFT-F1", out BindingChord parsed) &&
              parsed == full,
            "binding canonical prefix order or round trip drifted");
        Check(BindingChordLaw.TryParse("CTRL--", out BindingChord minus) &&
              minus == new BindingChord(Key.Minus, Control: true) &&
              BindingChordLaw.Canonical(minus) == "CTRL--",
            "Ctrl-minus punctuation chord drifted");
        Check(BindingChordLaw.TryParse("Number1", out BindingChord legacy) &&
              legacy == new BindingChord(Key.Number1) &&
              BindingChordLaw.Canonical(legacy) == "1",
            "legacy enum-name binding migration drifted");
        Check(BindingChordLaw.Fallback(full) ==
                  new BindingChord(Key.F1, Control: true, Shift: true) &&
              BindingChordLaw.Fallback(new BindingChord(Key.W, Control: true, Shift: true)) ==
                  new BindingChord(Key.W, Shift: true) &&
              BindingChordLaw.Fallback(new BindingChord(Key.W, Shift: true)) ==
                  new BindingChord(Key.W) &&
              BindingChordLaw.Fallback(new BindingChord(Key.W)) is null,
            "binding one-step leftmost-modifier fallback drifted");
        Check(BindingChordLaw.IsModifier(Key.AltLeft) &&
              BindingChordLaw.IsModifier(Key.ControlRight) &&
              BindingChordLaw.IsModifier(Key.ShiftLeft) &&
              BindingChordLaw.IsModifier(Key.SuperRight) &&
              !BindingChordLaw.IsModifier(Key.Z) &&
              !BindingChordLaw.TryParse("SUPER-Z", out _),
            "modifier-key/Super exclusion drifted");
        Check(BindingChordLaw.Display(new BindingChord(Key.Z, Alt: true),
                  key => key.ToString()) == "ALT-Z",
            "binding display chord drifted");
    }

    private static void CheckRuntimeSourceFence()
    {
        string root = ClientConfig.FindRepoRoot();
        string bindings = File.ReadAllText(Path.Combine(root, "MSUIClient", "GameLoop",
            "Panels", "GameLoop.Bindings.cs"));
        string page = File.ReadAllText(Path.Combine(root, "MSUIClient", "GameLoop",
            "Panels", "GameLoop.Keybindings.cs"));
        string sheath = File.ReadAllText(Path.Combine(root, "MSUIClient", "GameLoop",
            "Combat", "GameLoop.Sheath.cs"));

        Check(bindings.Contains("record struct BindingPair(BindingChord Primary",
                  StringComparison.Ordinal) &&
              bindings.Contains("BindingChordLaw.Canonical(x.Value.Primary)",
                  StringComparison.Ordinal) &&
              bindings.Contains("BindingChordLaw.TryParse", StringComparison.Ordinal) &&
              bindings.Contains("Alt: row.Binding == GameBinding.ToggleUi",
                  StringComparison.Ordinal) &&
              bindings.Contains("GameBinding[] exact = _bindings",
                  StringComparison.Ordinal) &&
              bindings.Contains("BindingChordLaw.Fallback(live)",
                  StringComparison.Ordinal) &&
              bindings.Contains("if (wasDown || typing || super) continue;",
                  StringComparison.Ordinal) &&
              bindings.Contains("if (typing) _bindingLatches.Clear();",
                  StringComparison.Ordinal),
            "binding storage or exact-first/fallback runtime dispatch escaped the chord law");
        Check(page.Contains("FirstBindableChordDown()", StringComparison.Ordinal) &&
              page.Contains("!BindingChordLaw.IsModifier(key)", StringComparison.Ordinal) &&
              page.Contains("InputKeyDown(Key.SuperLeft)", StringComparison.Ordinal) &&
              page.Contains("Function is Now Unbound!", StringComparison.Ordinal) &&
              page.Contains("FriendlyChord(chord)", StringComparison.Ordinal),
            "keybinding capture/display/conflict feedback escaped the chord law");
        Check(sheath.Contains("BindingBaseDown(GameBinding.Sheath)",
                  StringComparison.Ordinal) &&
              sheath.Contains("bool acceptedDown = BindingDown(GameBinding.Sheath);",
                  StringComparison.Ordinal) &&
              sheath.Contains("_sheathKeyWasDown = physicalDown;", StringComparison.Ordinal),
            "sheath no longer tracks the base edge separately from exact chord dispatch");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

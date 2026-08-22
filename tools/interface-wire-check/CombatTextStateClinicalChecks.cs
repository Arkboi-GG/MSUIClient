using MSUIClient;
using MSUIClient.Engine.UI;

internal static class CombatTextStateClinicalChecks
{
    public static void Run()
    {
        CombatTextResourceTransition full = CombatTextStateUiLaw.Resource(false, 100, 100);
        CombatTextResourceTransition crossing = CombatTextStateUiLaw.Resource(false, 20, 100);
        CombatTextResourceTransition held = CombatTextStateUiLaw.Resource(true, 10, 100);
        CombatTextResourceTransition recovered = CombatTextStateUiLaw.Resource(true, 21, 100);
        Check(!full.Latched && !full.Warn && crossing ==
                  new CombatTextResourceTransition(true, true) &&
              held == new CombatTextResourceTransition(true, false) &&
              recovered == new CombatTextResourceTransition(false, false) &&
              CombatTextStateUiLaw.Resource(false, 0, 100, eligible: false) ==
                  new CombatTextResourceTransition(false, false),
            "20-percent low-resource crossing/latch law drift");

        Check(CombatTextStateUiLaw.CombatState(null, true) is null &&
              CombatTextStateUiLaw.CombatState(false, true)?.Text == "Entering Combat" &&
              CombatTextStateUiLaw.CombatState(true, false)?.Text == "Leaving Combat",
            "combat-state transition law drift");
        Check(CombatTextStateUiLaw.Aura("Arcane Intellect", true, applied: true) ==
                  new CombatTextStateCue("Arcane Intellect", CombatTextStateTone.Green) &&
              CombatTextStateUiLaw.Aura("Frost Nova", false, applied: true)?.Tone ==
                  CombatTextStateTone.Red &&
              CombatTextStateUiLaw.Aura("Arcane Intellect", true, applied: false) is null &&
              CombatTextStateUiLaw.Aura("Arcane Intellect", true, applied: false,
                  showFades: true)?.Text == "<Arcane Intellect> fades",
            "aura gain/default-off fade law drift");

        string root = ClientConfig.FindRepoRoot();
        string feedback = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Combat",
            "GameLoop.CombatFeedback.cs"));
        string aura = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Dev",
            "GameLoop.DevTools.Auras.cs"));
        string net = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Net.cs"));
        Check(feedback.Contains("ResetCombatTextState()", StringComparison.Ordinal) &&
              feedback.Contains("QueueCenterCombatText(cue.Text, cue.Style, cue.Critical)",
                  StringComparison.Ordinal) &&
              aura.Contains("ObservePlayerAuraCombatText(aura, applied: true)",
                  StringComparison.Ordinal) &&
              aura.Contains("CompletePlayerAuraCombatTextBaseline()", StringComparison.Ordinal) &&
              net.Contains("ObservePlayerCombatTextState(player)", StringComparison.Ordinal),
            "player aura/combat/resource center-text feeds are unwired");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}

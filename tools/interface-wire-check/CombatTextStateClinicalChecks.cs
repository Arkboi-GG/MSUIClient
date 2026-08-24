using System.Numerics;
using MSUIClient;
using MSUIClient.Engine;
using MSUIClient.Engine.UI;

internal static class CombatTextStateClinicalChecks
{
    public static void Run()
    {
        var defaults = new GameSettings.ControlSettings();
        Check(!defaults.ShowLootAcquisitionText && !defaults.ShowCombatStateText,
            "loot and combat-state center text must ship disabled");

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

        Check(Near(CombatTextStateUiLaw.WorldTextPosition(
                  new Vector2(500, 400), 100, 20, 0, .5f), new Vector2(444.9f, 380)) &&
              Near(CombatTextStateUiLaw.WorldTextPosition(
                  new Vector2(500, 400), 100, 20, 3, .5f), new Vector2(461.05f, 380)) &&
              Near(CombatTextStateUiLaw.WorldShadow(new Vector2(1000, 500)),
                  new Vector2(2, 1)) &&
              Near(CombatTextStateUiLaw.CenterTextPosition(
                  new Vector2(1920, 1080), 2, 200, 2, .95f, false),
                  new Vector2(860, 535)) &&
              Near(CombatTextStateUiLaw.CenterTextPosition(
                  new Vector2(1920, 1080), 2, 200, 3, .95f, true),
                  new Vector2(896, 760)) &&
              CombatTextStateUiLaw.CenterShadow(2) == new Vector2(4),
            "combat-text world/center placement law drift");

        string root = ClientConfig.FindRepoRoot();
        string legacySettingsPath = Path.Combine(Path.GetTempPath(),
            $"msui-combat-text-defaults-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(legacySettingsPath,
                "{\"Settings\":{\"Version\":10,\"Controls\":{\"ShowPlayerNames\":true}}," +
                "\"Presets\":[]}");
            SettingsStore loaded = SettingsStore.Load(root, legacySettingsPath);
            Check(!loaded.Settings.Controls.ShowLootAcquisitionText &&
                  !loaded.Settings.Controls.ShowCombatStateText,
                "existing v10 settings missing the new keys must inherit both default-off values");
        }
        finally
        {
            if (File.Exists(legacySettingsPath)) File.Delete(legacySettingsPath);
        }
        string feedback = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Combat",
            "GameLoop.CombatFeedback.cs"));
        string aura = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Dev",
            "GameLoop.DevTools.Auras.cs"));
        string net = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Net.cs"));
        string loot = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Loot.cs"));
        string settings = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Settings.cs"));
        Check(feedback.Contains("ResetCombatTextState()", StringComparison.Ordinal) &&
              feedback.Contains("QueueCenterCombatText(cue.Text, cue.Style, cue.Critical)",
                  StringComparison.Ordinal) &&
              aura.Contains("ObservePlayerAuraCombatText(aura, applied: true)",
                  StringComparison.Ordinal) &&
              aura.Contains("CompletePlayerAuraCombatTextBaseline()", StringComparison.Ordinal) &&
              net.Contains("ObservePlayerCombatTextState(player)", StringComparison.Ordinal),
            "player aura/combat/resource center-text feeds are unwired");
        Check(loot.Contains("Settings.Controls.ShowLootAcquisitionText", StringComparison.Ordinal) &&
              settings.Contains("ShowCombatStateText", StringComparison.Ordinal) &&
              settings.Contains("ShowLootAcquisitionText", StringComparison.Ordinal),
            "default-off loot/combat-state Interface Options are unwired");

        int rendererStart = feedback.IndexOf("private void DrawFloatingCombatText",
            StringComparison.Ordinal);
        string renderer = feedback[rendererStart..];
        Check(rendererStart >= 0 &&
              renderer.Contains("CombatTextStateUiLaw.WorldTextPosition", StringComparison.Ordinal) &&
              renderer.Contains("CombatTextStateUiLaw.WorldShadow", StringComparison.Ordinal) &&
              renderer.Contains("CombatTextStateUiLaw.CenterTextPosition", StringComparison.Ordinal) &&
              renderer.Contains("CombatTextStateUiLaw.CenterShadow", StringComparison.Ordinal) &&
              !renderer.Contains("new Vector2", StringComparison.Ordinal) &&
              !renderer.Contains("Vector2 pos = new(", StringComparison.Ordinal),
            "combat-text renderer owns placement geometry");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private static bool Near(Vector2 actual, Vector2 expected) =>
        Vector2.Distance(actual, expected) < .001f;
}

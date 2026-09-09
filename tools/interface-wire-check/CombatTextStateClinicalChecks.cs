using System.Numerics;
using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using MSUIClient;
using MSUIClient.Engine;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

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
        Check(CombatTextStateUiLaw.Aura("Defensive State", true, true, hidden: true) is null &&
              CombatTextStateUiLaw.Aura("Defensive State 2", true, false,
                  showFades: true, hidden: true) is null,
            "hidden reactive auras must not leak internal names through combat text");

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

        float first = CombatTextStateUiLaw.NextCenterStartOffset(Array.Empty<float>());
        float second = CombatTextStateUiLaw.NextCenterStartOffset(new[] { first });
        float third = CombatTextStateUiLaw.NextCenterStartOffset(new[] { first, second });
        Check(first == 0 && second == 26 && third == 52 &&
              CombatTextStateUiLaw.NextCenterStartOffset(new[] { -40f }) == 0 &&
              CombatTextStateUiLaw.NextCenterStartOffset(new[] { 130f }) == 0 &&
              Math.Abs(CombatTextStateUiLaw.CenterMessageOffset(third, .5f, false) -
                  CombatTextStateUiLaw.CenterMessageOffset(second, .5f, false) - 26f) < .001f,
            "simultaneous combat notices must retain vertical spacing while scrolling");

        CheckProductionBurst();

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
              loot.Contains("QueueCenterCombatText(text, style)", StringComparison.Ordinal) &&
              !loot.Contains("new CenterText", StringComparison.Ordinal) &&
              settings.Contains("ShowCombatStateText", StringComparison.Ordinal) &&
              settings.Contains("ShowLootAcquisitionText", StringComparison.Ordinal),
            "loot must use the shared center stack and default-off Interface Options must be wired");

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

    private static void CheckProductionBurst()
    {
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        var loop = (GameLoop)RuntimeHelpers.GetUninitializedObject(typeof(GameLoop));
        FieldInfo rowsField = typeof(GameLoop).GetField("_centerCombatText", hidden)!;
        var rows = (IList)Activator.CreateInstance(rowsField.FieldType)!;
        rowsField.SetValue(loop, rows);
        FieldInfo worldField = typeof(GameLoop).GetField("_floatingCombatText", hidden)!;
        worldField.SetValue(loop, Activator.CreateInstance(worldField.FieldType));
        MethodInfo queue = typeof(GameLoop).GetMethod("QueueCenterCombatText", hidden)!;
        MethodInfo update = typeof(GameLoop).GetMethod("UpdateCombatFeedback", hidden)!;
        for (int i = 0; i < 20; i++)
        {
            queue.Invoke(loop, new object[] { $"-{i + 1}", CenterCombatTextStyle.Damage, false });
            if (i == 2) Check(rows.Count == 3, "ordinary three-message burst must remain intact");
            Check((string)rows[rows.Count - 1]!.GetType().GetField("Text")!.GetValue(rows[rows.Count - 1])! == $"-{i + 1}",
                "overflow must retain the newest combat message");
            float[] offsets = rows.Cast<object>().Select(row =>
            {
                Type type = row.GetType();
                return CombatTextStateUiLaw.CenterMessageOffset(
                    (float)type.GetField("StartOffset")!.GetValue(row)!,
                    (float)type.GetField("Age")!.GetValue(row)!, false);
            }).Order().ToArray();
            for (int j = 1; j < offsets.Length; j++)
                Check(offsets[j] - offsets[j - 1] >= CombatTextStateUiLaw.CenterMessageSpacing - .01f,
                    $"production burst resets onto occupied text at message {i + 1}");
            update.Invoke(loop, new object[] { i % 3 == 2 ? .08f : 0f });
        }
        update.Invoke(loop, new object[] { CombatTextStateUiLaw.CenterLifetime });
        Check(rows.Count == 0, "combat burst must expire on the authored lifetime");

        // Actual live failure: a -10 scroll passes through a stationary +72 critical heal.
        // Exercise the production queue over time, including critical growth reservations
        // and overflow; checking only the insertion frame misses the crossing.
        for (int message = 0; message < 30; message++)
        {
            bool critical = message % 4 == 0;
            queue.Invoke(loop, new object[] { critical ? "+72" : "-10",
                critical ? CenterCombatTextStyle.Heal : CenterCombatTextStyle.Damage, critical });
            for (int frame = 0; frame < 12; frame++)
            {
                update.Invoke(loop, new object[] { .025f });
                var positions = rows.Cast<object>().Select(row =>
                {
                    Type type = row.GetType();
                    bool crit = (bool)type.GetField("Critical")!.GetValue(row)!;
                    float offset = CombatTextStateUiLaw.CenterMessageOffset(
                        (float)type.GetField("StartOffset")!.GetValue(row)!,
                        (float)type.GetField("Age")!.GetValue(row)!, crit);
                    return (Offset: offset, Height: crit ? 60f : 25f);
                }).OrderBy(x => x.Offset).ToArray();
                for (int j = 1; j < positions.Length; j++)
                    Check(positions[j].Offset >= positions[j - 1].Offset + positions[j - 1].Height,
                        $"mixed critical/scroll text overlaps at message {message}, frame {frame}");
            }
        }
        update.Invoke(loop, new object[] { CombatTextStateUiLaw.CenterLifetime });
        Check(rows.Count == 0, "mixed feedback must expire on the authored lifetime");
        queue.Invoke(loop, new object[] { "+72", CenterCombatTextStyle.Heal, true });
        for (int i = 0; i < 20; i++)
        {
            queue.Invoke(loop, new object[] { "-10", CenterCombatTextStyle.Damage, false });
            update.Invoke(loop, new object[] { .05f });
        }
        Check(rows.Cast<object>().Any(row => (bool)row.GetType().GetField("Critical")!.GetValue(row)!),
            "ordinary scrolling feedback must not prematurely retire the critical heal");
        update.Invoke(loop, new object[] { CombatTextStateUiLaw.CenterLifetime });
        Check(rows.Count == 0, "critical reservation must not extend message lifetime");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private static bool Near(Vector2 actual, Vector2 expected) =>
        Vector2.Distance(actual, expected) < .001f;
}

using MSUIClient;
using MSUIClient.Engine;
using MSUIClient.Engine.UI;

internal static class TargetClickClinicalChecks
{
    public static void Run()
    {
        Check(TargetClickLaw.LeftClickSelection(10, 20, stickyTargeting: false) == 20 &&
              TargetClickLaw.LeftClickSelection(10, 20, stickyTargeting: true) == 20,
            "a real picked unit must replace the selection in either mode");
        Check(TargetClickLaw.LeftClickSelection(10, 0, stickyTargeting: false) == 0,
            "the shipped default must clear on an empty world left-click");
        Check(TargetClickLaw.LeftClickSelection(10, 0, stickyTargeting: true) == 10,
            "Sticky Targeting must preserve the current target on an empty click");
        Check(TargetClickLaw.LeftClickSelection(0, 0, stickyTargeting: true) == 0,
            "Sticky Targeting must not invent a selection");
        Check(!new GameSettings.ControlSettings().StickyTargeting,
            "Sticky Targeting default must preserve reference deselectOnClick=1");
        Check(!new GameSettings.ControlSettings().WorldPlayerContextMenus,
            "world-model player menus must remain opt-in; portrait menus are the shipped default");

        string root = ClientConfig.FindRepoRoot();
        string target = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop",
            "Combat", "GameLoop.Targeting.cs"));
        string settings = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop",
            "Panels", "GameLoop.Settings.cs"));
        string search = SourceText.Read(Path.Combine(root, "MSUIClient", "Engine", "UI",
            "OptionsSearchUiLaw.cs"));
        Check(target.Contains("TargetClickLaw.LeftClickSelection", StringComparison.Ordinal) &&
              target.Contains("Settings.Controls.StickyTargeting", StringComparison.Ordinal),
            "Sticky Targeting production click gate drifted");
        Check(settings.Contains("Check(\"Sticky Targeting\"", StringComparison.Ordinal) &&
              search.Contains("\"Sticky Targeting\"", StringComparison.Ordinal),
            "Sticky Targeting Interface option/search wiring drifted");
        Check(target.Contains("Settings.Controls.WorldPlayerContextMenus", StringComparison.Ordinal) &&
              target.Contains("OpenUnitPopup(picked, which, click.Position, InspectBinding.Target);",
                  StringComparison.Ordinal) &&
              settings.Contains("BeginBox(\"msui-options\", \"MSUI Options\")",
                  StringComparison.Ordinal) &&
              settings.Contains("Check(\"Right-click player models for menu\"",
                  StringComparison.Ordinal) &&
              search.Contains("\"Right-click player models for menu\"", StringComparison.Ordinal),
            "world-model player menu toggle or its MSUI Options/search wiring drifted");
        Check(target.Contains("TryToggleQuestWatchAt(", StringComparison.Ordinal) &&
              target.Contains("click.Position, click.Button == MouseButton.Left", StringComparison.Ordinal) &&
              target.IndexOf("TryToggleQuestWatchAt(", StringComparison.Ordinal) <
              target.IndexOf("HandleDevEditClick(click)", StringComparison.Ordinal),
            "quest-watch title clicks must be consumed before world click routing");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

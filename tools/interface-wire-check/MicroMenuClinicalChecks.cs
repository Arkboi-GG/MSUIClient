using MSUIClient;
using MSUIClient.Engine.UI;

internal static class MicroMenuClinicalChecks
{
    public static void Run()
    {
        Check(MicroMenuUiLaw.VisibleButtons(9).Select(x => x.Id).SequenceEqual(
                new[] { MicroMenuButtonId.Character, MicroMenuButtonId.Spellbook,
                    MicroMenuButtonId.QuestLog, MicroMenuButtonId.Social,
                    MicroMenuButtonId.WorldMap, MicroMenuButtonId.MainMenu,
                    MicroMenuButtonId.Help }) &&
              MicroMenuUiLaw.VisibleButtons(10).Count() == 8,
            "talent micro-button hide/reflow gate drift");
        Check(MicroMenuUiLaw.ButtonX(0) == 0 && MicroMenuUiLaw.ButtonX(7) == 182,
            "micro-button 26px anchor chain drift");
        Check(MicroMenuUiLaw.TooltipTitle("Quest Log", "L") == "Quest Log (L)" &&
              MicroMenuUiLaw.TooltipTitle("Help Request", null) == "Help Request" &&
              MicroMenuUiLaw.Buttons.All(x => x.NewbieText.Length > 40),
            "micro-button live-key/newbie tooltip law drift");

        string root = ClientConfig.FindRepoRoot();
        string actionBars = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Hud",
            "GameLoop.ActionBars.cs"));
        string bindings = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Bindings.cs"));
        string settings = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Settings.cs"));
        Check(actionBars.Contains("MicroMenuUiLaw.VisibleButtons", StringComparison.Ordinal) &&
              actionBars.Contains("MicroMenuUiLaw.TooltipTitle", StringComparison.Ordinal) &&
              actionBars.Contains("ImGui.TextWrapped(newbieText)", StringComparison.Ordinal),
            "micro-menu law/render wiring drift");
        Check(bindings.Contains("GameBinding.OpenTalents, \"Talents\", Key.N", StringComparison.Ordinal) &&
              bindings.Contains("GameBinding.OpenQuestLog, \"Quest Log\", Key.L", StringComparison.Ordinal) &&
              bindings.Contains("GameBinding.OpenSocial, \"Social\", Key.O", StringComparison.Ordinal),
            "N/L/O live binding registry drift");
        Check(settings.Contains("if (_settingsOpen) _settingsPopupRequested = true;",
                  StringComparison.Ordinal),
            "game-menu popup lost its one-frame recovery path");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}

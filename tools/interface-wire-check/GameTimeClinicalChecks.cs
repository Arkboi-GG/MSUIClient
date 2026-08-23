using System.Numerics;
using MSUIClient;
using MSUIClient.Engine.UI;

internal static class GameTimeClinicalChecks
{
    public static void Run()
    {
        Check(GameTimeUiLaw.FrameOrigin(new Vector2(1920, 1080), 2f) ==
                  new Vector2(1828, 38) &&
              GameTimeUiLaw.HitRect == new QuestLogicalRect(6, 5, 44, 35) &&
              GameTimeUiLaw.FrameSize(2) == new Vector2(100, 100) &&
              GameTimeUiLaw.HitScreen(new Vector2(1828, 38), 2) ==
                  new GameTimeUiLaw.ScreenRect(new Vector2(1840, 48),
                      new Vector2(88, 70)) &&
              GameTimeUiLaw.DayUvMax == new Vector2(50f / 128f, 50f / 64f) &&
              GameTimeUiLaw.NightUvMin == new Vector2(.5f, 0f),
            "game-time frame geometry/atlas law drift");
        Check(GameTimeUiLaw.IsNight(5, 29) &&
              !GameTimeUiLaw.IsNight(5, 30) &&
              !GameTimeUiLaw.IsNight(20, 59) &&
              GameTimeUiLaw.IsNight(21, 0) &&
              GameTimeUiLaw.TimeParts(21f + 7f / 60f) == (21, 7) &&
              GameTimeUiLaw.ClockText(21, 7) == "21:07" &&
              GameTimeUiLaw.ClockText(0, 7, false) == "12:07 AM" &&
              GameTimeUiLaw.ClockText(13, 7, false) == "1:07 PM",
            "game-time thresholds/formatter drift");

        string root = ClientConfig.FindRepoRoot();
        string runtime = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Hud",
            "GameLoop.GameTime.cs"));
        string composition = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Combat",
            "GameLoop.CombatFeedback.cs"));
        Check(runtime.Contains("GameTimeUiLaw.FrameOrigin", StringComparison.Ordinal) &&
              runtime.Contains("GameTimeUiLaw.HitScreen", StringComparison.Ordinal) &&
              !runtime.Contains("new Vector2", StringComparison.Ordinal) &&
              runtime.Contains("_worldClock.CurrentHours", StringComparison.Ordinal) &&
              runtime.Contains("UI-TOD-Indicator", StringComparison.Ordinal) &&
              runtime.Contains("OfferPreservedSharedGameTooltipRenderer", StringComparison.Ordinal) &&
              composition.Contains("DrawMinimap();\n            DrawGameTimeFrame();",
                  StringComparison.Ordinal),
            "game-time production wiring drift");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}

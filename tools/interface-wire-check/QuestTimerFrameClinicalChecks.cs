using System.Numerics;
using MSUIClient;
using MSUIClient.Engine.UI;

internal static class QuestTimerFrameClinicalChecks
{
    public static void Run()
    {
        Check(QuestTimerFrameUiLaw.FrameHeight(1) == 61f &&
              QuestTimerFrameUiLaw.FrameHeight(2) == 77f &&
              QuestTimerFrameUiLaw.FrameHeight(20) == 365f &&
              QuestTimerFrameUiLaw.ManagedHeight(0) == 0f &&
              QuestTimerFrameUiLaw.ManagedHeight(2) == 77f &&
              QuestTimerFrameUiLaw.FrameOrigin(new Vector2(1920, 1080), 2f) ==
                  new Vector2(1600, 384) &&
              QuestTimerFrameUiLaw.RowRect(0) == new QuestLogicalRect(10, 30, 140, 16) &&
              QuestTimerFrameUiLaw.RowRect(19) == new QuestLogicalRect(10, 334, 140, 16) &&
              QuestFrameUiLaw.SecondsToTime(859) == "14 Mins 19 Secs " &&
              QuestFrameUiLaw.SecondsToTime(44) == "44 Secs ",
            "quest-timer frame geometry/countdown law drift");

        Check(DurabilityFrameUiLaw.FrameOrigin(new Vector2(1920, 1080), 2f, false, 77f) ==
                  new Vector2(1800, 538) &&
              QuestFrameUiLaw.QuestWatchTopRight(new Vector2(1920, 1080), 2f, 77f, true) ==
                  new Vector2(1920, 668),
            "quest-timer managed stack drift");

        string root = ClientConfig.FindRepoRoot();
        string runtime = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Hud",
            "GameLoop.QuestTimerFrame.cs"));
        string quest = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Quest.cs"));
        string composition = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Combat",
            "GameLoop.CombatFeedback.cs"));
        Check(runtime.Contains("QuestTimerFrameUiLaw.FrameOrigin", StringComparison.Ordinal) &&
              runtime.Contains("QuestTimerFrameUiLaw.FrameHeight", StringComparison.Ordinal) &&
              runtime.Contains("QuestSignedSecondsLeft", StringComparison.Ordinal) &&
              runtime.Contains("UI-DialogBox-Header", StringComparison.Ordinal) &&
              runtime.Contains("_questLogSelectedQuestId = timer.QuestId", StringComparison.Ordinal) &&
              quest.Contains("_questTimerFrameHeight, _durabilityFrameShown", StringComparison.Ordinal) &&
              composition.Contains("DrawMinimap();\n            DrawGameTimeFrame();\n            DrawQuestTimerFrame();\n            DrawDurabilityFrame();\n            DrawQuestWatchFrame();",
                  StringComparison.Ordinal),
            "quest-timer production/managed-stack wiring drift");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}

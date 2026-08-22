using System.Numerics;
using MSUIClient;
using MSUIClient.Engine.UI;

internal static class DurabilityFrameClinicalChecks
{
    public static void Run()
    {
        Check(DurabilityFrameUiLaw.AlertStatus(0, 0, 40) == 4 &&
              DurabilityFrameUiLaw.AlertStatus(0, 5, 100) == 3 &&
              DurabilityFrameUiLaw.AlertStatus(0, 6, 20) == 0 &&
              DurabilityFrameUiLaw.AlertStatus(0x10, 100, 100) == 4 &&
              DurabilityFrameUiLaw.AlertStatus(0x08, 0, 40) == 0,
            "durability alert classification drift");
        byte[] bodyOnly = [0, 0, 0, 0, 3, 0, 0, 0, 0, 0, 0];
        byte[] side = [0, 0, 0, 0, 0, 0, 0, 0, 0, 4, 0];
        Check(DurabilityFrameUiLaw.BodyShown(bodyOnly) &&
              !DurabilityFrameUiLaw.SideShown(bodyOnly) &&
              DurabilityFrameUiLaw.FrameShown(bodyOnly) &&
              !DurabilityFrameUiLaw.BodyShown(side) &&
              DurabilityFrameUiLaw.SideShown(side) &&
              DurabilityFrameUiLaw.FrameOrigin(new Vector2(1920, 1080), 2, false) ==
                  new Vector2(1800, 384) &&
              DurabilityFrameUiLaw.FrameOrigin(new Vector2(1920, 1080), 2, true) ==
                  new Vector2(1760, 384) &&
              QuestFrameUiLaw.QuestWatchTopRight(new Vector2(1920, 1080), 2, true) ==
                  new Vector2(1920, 514),
            "durability figure/managed seat drift");
        Check(DurabilityFrameUiLaw.Glyphs.Length == 12 &&
              DurabilityFrameUiLaw.Glyphs.Single(g => g.Kind == DurabilityGlyphKind.Weapon).Min ==
                  new Vector2(-12, 9.5f) &&
              DurabilityFrameUiLaw.Glyphs.Single(g => g.Kind == DurabilityGlyphKind.Ranged).Min ==
                  new Vector2(50.5f, 26.5f),
            "durability atlas geometry drift");

        string root = ClientConfig.FindRepoRoot();
        string runtime = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Hud",
            "GameLoop.DurabilityFrame.cs"));
        string composition = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Combat",
            "GameLoop.CombatFeedback.cs"));
        Check(runtime.Contains("DurabilityFrameUiLaw.FrameOrigin", StringComparison.Ordinal) &&
              runtime.Contains("UI-Durability-Icons", StringComparison.Ordinal) &&
              composition.Contains("DrawMinimap();\n            DrawGameTimeFrame();\n            DrawQuestTimerFrame();\n            DrawDurabilityFrame();\n            DrawQuestWatchFrame();",
                  StringComparison.Ordinal),
            "durability production/managed-stack wiring drift");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}

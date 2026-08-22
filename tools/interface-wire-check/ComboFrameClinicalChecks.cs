using MSUIClient;
using MSUIClient.Engine.UI;
using System.Numerics;

internal static class ComboFrameClinicalChecks
{
    public static void Run()
    {
        Check(ComboFrameUiLaw.VisiblePoints(4, 3, 99, 99) == 3 &&
              ComboFrameUiLaw.VisiblePoints(11, 9, 99, 99) == 5 &&
              ComboFrameUiLaw.VisiblePoints(1, 3, 99, 99) == 0 &&
              ComboFrameUiLaw.VisiblePoints(4, 3, 99, 100) == 0,
            "GetComboPoints class/current-target gate drift");

        Vector2[] expected =
        [
            new(176, 9), new(183, 17), new(188, 27), new(190, 38), new(188.5f, 49),
        ];
        Check(Enumerable.Range(0, 5).All(i => ComboFrameUiLaw.PointMinimum(i) == expected[i]),
            "ComboFrame diagonal point anchor chain drift");
        Check(Near(ComboFrameUiLaw.FrameAlpha(0.15, 0), 0.5f) &&
              Near(ComboFrameUiLaw.HighlightAlpha(0.2, 0), 0.5f) &&
              Near(ComboFrameUiLaw.ShineAlpha(0.55, 0), 0.5f) &&
              Near(ComboFrameUiLaw.ShineAlpha(0.9, 0), 0.5f) &&
              Near(ComboFrameUiLaw.ShineAlpha(1.1, 0), 0),
            "ComboFrame fade/shine chain drift");

        string root = ClientConfig.FindRepoRoot();
        string fields = SourceText.Read(Path.Combine(root, "MSUIClient", "Net", "ObjectFields.cs"));
        string combo = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Hud",
            "GameLoop.ComboFrame.cs"));
        string frames = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Hud",
            "GameLoop.UnitFrames.cs"));
        Check(fields.Contains("PLAYER_FIELD_COMBO_TARGET = 714", StringComparison.Ordinal) &&
              fields.Contains("PlayerComboPoints", StringComparison.Ordinal) &&
              fields.Contains("PlayerComboTarget", StringComparison.Ordinal),
            "combo descriptor fields drift");
        Check(combo.Contains("VisibleComboPoints()", StringComparison.Ordinal) &&
              combo.Contains("ComboFrameUiLaw.ShineAlpha", StringComparison.Ordinal) &&
              frames.Contains("DrawComboFrame(dl, p, s);", StringComparison.Ordinal),
            "ComboFrame target-frame render wiring drift");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private static bool Near(float actual, float expected) => Math.Abs(actual - expected) < 0.0001f;
}

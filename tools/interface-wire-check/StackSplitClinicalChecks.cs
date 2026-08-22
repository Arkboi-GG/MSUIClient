using System.Numerics;
using MSUIClient;
using MSUIClient.Engine.UI;

internal static class StackSplitClinicalChecks
{
    public static void Run()
    {
        Check(StackSplitUiLaw.FrameSize == new Vector2(172, 96) &&
              StackSplitUiLaw.PlateUvMax == new Vector2(.671875f, .75f) &&
              StackSplitUiLaw.LeftArrow == new Vector2(11, 22) &&
              StackSplitUiLaw.RightArrow == new Vector2(150, 22) &&
              StackSplitUiLaw.OkayButton == new Vector2(19, 52) &&
              StackSplitUiLaw.CancelButton == new Vector2(91, 52) &&
              StackSplitUiLaw.Origin(new Vector2(300, 400), 2) == new Vector2(-44, 208),
            "StackSplitFrame authored geometry/owner anchor drift");

        Check(StackSplitUiLaw.AppendDigit(1, false, 2, 20) == (2, true) &&
              StackSplitUiLaw.AppendDigit(2, true, 5, 20) == (20, true) &&
              StackSplitUiLaw.AppendDigit(1, false, 0, 20) == (1, false) &&
              StackSplitUiLaw.Backspace(20, 20) == (2, true) &&
              StackSplitUiLaw.Backspace(2, 20) == (1, false),
            "StackSplitFrame typed-digit/backspace law drift");

        string root = ClientConfig.FindRepoRoot();
        string inventory = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Inventory.cs"));
        Check(inventory.Contains("_splitMaximum = stackCount", StringComparison.Ordinal) &&
              inventory.Contains("_splitCount = 1", StringComparison.Ordinal) &&
              inventory.Contains("new(max.X, min.Y)", StringComparison.Ordinal) &&
              inventory.Contains("StackSplitUiLaw.Origin(_splitOwnerTopRight, scale)",
                  StringComparison.Ordinal) &&
              inventory.Contains("StackSplitUiLaw.PlatePath", StringComparison.Ordinal) &&
              inventory.Contains("StackSplitUiLaw.LeftArrowStem", StringComparison.Ordinal) &&
              inventory.Contains("VanillaButton(draw, \"##stack-split-okay\"", StringComparison.Ordinal) &&
              inventory.Contains("StackSplitDigitPressed(digit)", StringComparison.Ordinal) &&
              !inventory.Contains("ImGui.SliderInt(\"##split-count\"", StringComparison.Ordinal) &&
              !inventory.Contains("mouse + new Vector2(16f)", StringComparison.Ordinal),
            "StackSplitFrame law-positioned/art-backed renderer is unwired");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}

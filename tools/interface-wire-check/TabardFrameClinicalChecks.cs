using MSUIClient;
using MSUIClient.Engine.UI;
using System.Numerics;

internal static class TabardFrameClinicalChecks
{
    public static void Run()
    {
        Check(TabardFrameUiLaw.Frame ==
                  new TabardFrameUiLaw.LogicalRect(0, 104, 384, 512) &&
              TabardFrameUiLaw.Background ==
                  new TabardFrameUiLaw.LogicalRect(23, 76, 316, 325) &&
              TabardFrameUiLaw.Accept ==
                  new TabardFrameUiLaw.LogicalRect(184, 409, 80, 22) &&
              TabardFrameUiLaw.Cancel ==
                  new TabardFrameUiLaw.LogicalRect(265, 409, 80, 22) &&
              TabardFrameUiLaw.Close ==
                  new TabardFrameUiLaw.LogicalRect(323, 8, 32, 32),
            "TabardFrame host/background/action geometry drift");

        TabardFrameUiLaw.SelectorLayout first = TabardFrameUiLaw.Selector(0);
        TabardFrameUiLaw.SelectorLayout last = TabardFrameUiLaw.Selector(4);
        Check(first.LabelCenter == new Vector2(274, 172) &&
              first.Previous == new TabardFrameUiLaw.LogicalRect(218, 180, 32, 32) &&
              first.Next == new TabardFrameUiLaw.LogicalRect(306, 180, 32, 32) &&
              first.ValueCenter == new Vector2(278, 196) &&
              last.LabelCenter == new Vector2(274, 344) &&
              last.Previous.Y == 352 && last.ValueCenter.Y == 368,
            "TabardFrame five-row selector geometry drift");

        string root = ClientConfig.FindRepoRoot();
        string source = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Tabard.cs"));
        string program = SourceText.Read(Path.Combine(root, "MSUIClient", "Program.cs"));
        Check(source.Contains("TabardFrameUiLaw.Frame.Min", StringComparison.Ordinal) &&
              source.Contains("TabardFrameUiLaw.Selector(i)", StringComparison.Ordinal) &&
              source.Contains("TabardFrameUiLaw.Accept.Min", StringComparison.Ordinal) &&
              source.Contains("TryGetSessionBodyPose(out WorldBodyPose sessionBody)",
                  StringComparison.Ordinal) &&
              source.Contains("_tabardOpen && TabardDesignerEligible(",
                  StringComparison.Ordinal) &&
              source.Contains("UpdateTabardLifecycle()", StringComparison.Ordinal) &&
              program.Contains("UpdateTabardLifecycle();", StringComparison.Ordinal) &&
              !source.Contains("new Vector2(0, 104)", StringComparison.Ordinal) &&
              !source.Contains("new Vector2(218, y + 8)", StringComparison.Ordinal) &&
              !source.Contains("new Vector2(323, 8)", StringComparison.Ordinal),
            "TabardFrame renderer bypassed rule-owned window/selector geometry");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

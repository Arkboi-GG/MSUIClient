using MSUIClient;
using MSUIClient.Engine.UI;
using System.Numerics;

internal static class HelpFrameClinicalChecks
{
    public static void Run()
    {
        Check(HelpFrameUiLaw.Frame(new Vector2(1024, 768)) ==
                  new HelpFrameUiLaw.LogicalRect(192, 128, 640, 512) &&
              HelpFrameUiLaw.Art.Length == 6 &&
              HelpFrameUiLaw.Art[0].Rect ==
                  new HelpFrameUiLaw.LogicalRect(0, 0, 256, 256) &&
              HelpFrameUiLaw.Art[5].Rect ==
                  new HelpFrameUiLaw.LogicalRect(512, 256, 128, 256) &&
              HelpFrameUiLaw.Header ==
                  new HelpFrameUiLaw.LogicalRect(140, -12, 336, 64) &&
              HelpFrameUiLaw.Close ==
                  new HelpFrameUiLaw.LogicalRect(566, 3, 32, 32),
            "HelpFrame centered host, art, header, or close geometry drift");

        Check(HelpFrameUiLaw.HomeIssueHeading(2) == new Vector2(54, 289) &&
              HelpFrameUiLaw.HomeIssueDescription(2) ==
                  new HelpFrameUiLaw.LogicalRect(70, 310, 500, 0) &&
              HelpFrameUiLaw.CategoryButton(0) ==
                  new HelpFrameUiLaw.LogicalRect(86, 105, 218, 52) &&
              HelpFrameUiLaw.CategoryButton(5) ==
                  new HelpFrameUiLaw.LogicalRect(336, 253, 218, 52) &&
              HelpFrameUiLaw.TicketInput ==
                  new HelpFrameUiLaw.LogicalRect(44, 125, 548, 265) &&
              HelpFrameUiLaw.TicketDelete ==
                  new HelpFrameUiLaw.LogicalRect(475, 441, 110, 22),
            "HelpFrame page/button geometry drift");

        string root = ClientConfig.FindRepoRoot();
        string source = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Help.cs"));
        Check(source.Contains("HelpFrameUiLaw.Frame(logicalDisplay)",
                  StringComparison.Ordinal) &&
              source.Contains("HelpFrameUiLaw.Art", StringComparison.Ordinal) &&
              source.Contains("HelpFrameUiLaw.TicketInput", StringComparison.Ordinal) &&
              !source.Contains("logicalDisplay.X - 640", StringComparison.Ordinal) &&
              !source.Contains("new Vector2(566, 3)", StringComparison.Ordinal) &&
              !source.Contains("new Vector2(44,125)", StringComparison.Ordinal),
            "HelpFrame renderer bypassed rule-owned window/page geometry");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

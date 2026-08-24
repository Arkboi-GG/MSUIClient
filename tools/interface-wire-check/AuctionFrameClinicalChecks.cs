using System.Numerics;
using MSUIClient;
using MSUIClient.Engine.UI;

internal static class AuctionFrameClinicalChecks
{
    public static void Run()
    {
        Check(AuctionFrameUiLaw.FrameOrigin(2) == new Vector2(0, 208) &&
              AuctionFrameUiLaw.Frame == new AuctionFrameUiLaw.LogicalRect(0, 0, 832, 447) &&
              AuctionFrameUiLaw.Host == new AuctionFrameUiLaw.LogicalRect(0, 0, 832, 479) &&
              AuctionFrameUiLaw.Close == new AuctionFrameUiLaw.LogicalRect(803, 8, 32, 32) &&
              AuctionFrameUiLaw.Search == new AuctionFrameUiLaw.LogicalRect(186, 48, 300, 22) &&
              AuctionFrameUiLaw.SearchButton ==
                  new AuctionFrameUiLaw.LogicalRect(496, 48, 80, 22),
            "AuctionFrame preserved host/search/close geometry drift");

        Check(AuctionFrameUiLaw.Art.Length == 6 &&
              AuctionFrameUiLaw.Art[0].Rect ==
                  new AuctionFrameUiLaw.LogicalRect(0, 0, 256, 256) &&
              AuctionFrameUiLaw.Art[^1].Rect ==
                  new AuctionFrameUiLaw.LogicalRect(576, 256, 256, 256) &&
              AuctionFrameUiLaw.BrowseHeaders.Length == 5 &&
              AuctionFrameUiLaw.BrowseHeaders[^1].Rect ==
                  new AuctionFrameUiLaw.LogicalRect(616, 82, 163, 19),
            "AuctionFrame six-piece shell/header geometry drift");

        Check(AuctionFrameUiLaw.CategoryRow(3) ==
                  new AuctionFrameUiLaw.LogicalRect(22, 148, 145, 20) &&
              AuctionFrameUiLaw.AuctionRow(7) ==
                  new AuctionFrameUiLaw.LogicalRect(195, 369, 597, 37) &&
              AuctionFrameUiLaw.TabMin(0, 80, 60) == new Vector2(15, 436) &&
              AuctionFrameUiLaw.TabMin(1, 80, 60) == new Vector2(87, 436) &&
              AuctionFrameUiLaw.TabMin(2, 80, 60) == new Vector2(139, 436) &&
              AuctionFrameUiLaw.BidButton ==
                  new AuctionFrameUiLaw.LogicalRect(625, 372, 80, 22) &&
              AuctionFrameUiLaw.BuyoutButton ==
                  new AuctionFrameUiLaw.LogicalRect(711, 372, 80, 22),
            "AuctionFrame rows/tabs/action geometry drift");

        string root = ClientConfig.FindRepoRoot();
        string runtime = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Auction.cs"));
        string program = SourceText.Read(Path.Combine(root, "MSUIClient", "Program.cs"));
        Check(runtime.Contains("AuctionFrameUiLaw.FrameOrigin", StringComparison.Ordinal) &&
              runtime.Contains("AuctionFrameUiLaw.Art", StringComparison.Ordinal) &&
              runtime.Contains("AuctionFrameUiLaw.CategoryRow", StringComparison.Ordinal) &&
              runtime.Contains("AuctionFrameUiLaw.AuctionRow", StringComparison.Ordinal) &&
              runtime.Contains("AuctionFrameUiLaw.TabMin", StringComparison.Ordinal) &&
              runtime.Contains("TryGetSessionBodyPose(out WorldBodyPose sessionBody)",
                  StringComparison.Ordinal) &&
              Count(runtime, "AuctionSessionInRange(out _)") >= 6 &&
              runtime.Contains("UpdateAuctionLifecycle()", StringComparison.Ordinal) &&
              program.Contains("UpdateAuctionLifecycle();", StringComparison.Ordinal) &&
              !runtime.Contains("new Vector2", StringComparison.Ordinal) &&
              !runtime.Contains("BeginChild(\"##auction-content\"", StringComparison.Ordinal) &&
              !runtime.Contains("ImGui.Selectable", StringComparison.Ordinal),
            "AuctionFrame renderer-local geometry or unreachable generic fallback returned");
    }

    private static int Count(string text, string needle)
    {
        int count = 0;
        for (int at = 0; (at = text.IndexOf(needle, at, StringComparison.Ordinal)) >= 0;
             at += needle.Length)
            count++;
        return count;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}

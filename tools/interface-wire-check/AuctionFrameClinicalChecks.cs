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
              AuctionFrameUiLaw.Search == new AuctionFrameUiLaw.LogicalRect(83, 57, 150, 20) &&
              AuctionFrameUiLaw.SearchButton ==
                  new AuctionFrameUiLaw.LogicalRect(634, 47, 80, 22),
            "AuctionFrame canonical host/search/close geometry drift");

        Check(AuctionFrameUiLaw.BrowseArt.Length == 6 &&
              AuctionFrameUiLaw.BidArt.Length == 6 &&
              AuctionFrameUiLaw.AuctionArt.Length == 6 &&
              AuctionFrameUiLaw.BrowseArt[0].Rect ==
                  new AuctionFrameUiLaw.LogicalRect(0, 0, 256, 256) &&
              AuctionFrameUiLaw.AuctionArt[^1].Rect ==
                  new AuctionFrameUiLaw.LogicalRect(576, 256, 256, 256) &&
              AuctionFrameUiLaw.ArtForTab(1)[0].Path.Contains("-Bid-", StringComparison.Ordinal) &&
              AuctionFrameUiLaw.ArtForTab(2)[0].Path.Contains("-Auction-", StringComparison.Ordinal) &&
              AuctionFrameUiLaw.BrowseHeaders.Length == 5 &&
              AuctionFrameUiLaw.BrowseHeaders[^1].Rect ==
                  new AuctionFrameUiLaw.LogicalRect(616, 82, 163, 19),
            "AuctionFrame six-piece shell/header geometry drift");

        Check(AuctionFrameUiLaw.CategoryRow(3) ==
                  new AuctionFrameUiLaw.LogicalRect(23, 165, 136, 20) &&
              AuctionFrameUiLaw.AuctionRow(7) ==
                  new AuctionFrameUiLaw.LogicalRect(195, 369, 597, 37) &&
              AuctionFrameUiLaw.TabMin(0, 80, 60) == new Vector2(15, 436) &&
              AuctionFrameUiLaw.TabMin(1, 80, 60) == new Vector2(87, 436) &&
              AuctionFrameUiLaw.TabMin(2, 80, 60) == new Vector2(139, 436) &&
              AuctionFrameUiLaw.BidButton ==
                  new AuctionFrameUiLaw.LogicalRect(584, 411, 80, 22) &&
              AuctionFrameUiLaw.BuyoutButton ==
                  new AuctionFrameUiLaw.LogicalRect(664, 411, 80, 22),
            "AuctionFrame rows/tabs/action geometry drift");
        Check(AuctionFrameUiLaw.BidHeaders.Length == 6 &&
              AuctionFrameUiLaw.BidHeaders[0].Rect ==
                  new AuctionFrameUiLaw.LogicalRect(65, 52, 195, 19) &&
              AuctionFrameUiLaw.BidRow(8) ==
                  new AuctionFrameUiLaw.LogicalRect(27, 372, 793, 37) &&
              AuctionFrameUiLaw.OwnerHeaders.Length == 4 &&
              AuctionFrameUiLaw.OwnerHeaders[0].Rect ==
                  new AuctionFrameUiLaw.LogicalRect(219, 51, 185, 19) &&
              AuctionFrameUiLaw.OwnerRow(8) ==
                  new AuctionFrameUiLaw.LogicalRect(219, 372, 576, 37) &&
              AuctionFrameUiLaw.OwnerItemSlot ==
                  new AuctionFrameUiLaw.LogicalRect(28, 98, 37, 37) &&
              AuctionFrameUiLaw.OwnerCreateButton ==
                  new AuctionFrameUiLaw.LogicalRect(18, 388, 191, 20) &&
              AuctionFrameUiLaw.MoneyInputBorderSlices(30, 10)[^1].Rect ==
                  new AuctionFrameUiLaw.LogicalRect(12, 0, 8, 20),
            "AuctionFrame Bids/Auctions vanilla geometry drift");

        string root = ClientConfig.FindRepoRoot();
        string runtime = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Auction.cs"));
        string program = SourceText.Read(Path.Combine(root, "MSUIClient", "Program.cs"));
        Check(runtime.Contains("AuctionFrameUiLaw.FrameOrigin", StringComparison.Ordinal) &&
              runtime.Contains("AuctionFrameUiLaw.ArtForTab(_auctionTab)", StringComparison.Ordinal) &&
              runtime.Contains("AuctionFrameUiLaw.CategoryRow", StringComparison.Ordinal) &&
              runtime.Contains("AuctionFrameUiLaw.AuctionRow", StringComparison.Ordinal) &&
              runtime.Contains("AuctionFrameUiLaw.BidRow", StringComparison.Ordinal) &&
              runtime.Contains("AuctionFrameUiLaw.OwnerRow", StringComparison.Ordinal) &&
              runtime.Contains("DrawAuctionOwnerForm", StringComparison.Ordinal) &&
              runtime.Contains("MSUI_INVENTORY_ITEM", StringComparison.Ordinal) &&
              runtime.Contains("AuctionFrameUiLaw.TabMin", StringComparison.Ordinal) &&
              runtime.Contains("TryGetSessionBodyPose(out WorldBodyPose sessionBody)",
                  StringComparison.Ordinal) &&
              Count(runtime, "AuctionSessionInRange(out _)") >= 6 &&
              runtime.Contains("UpdateAuctionLifecycle()", StringComparison.Ordinal) &&
              program.Contains("UpdateAuctionLifecycle();", StringComparison.Ordinal) &&
              runtime.Contains("SortHeaderSlices", StringComparison.Ordinal) &&
              runtime.Contains("ItemNameSlices", StringComparison.Ordinal) &&
              !runtime.Contains("AddText(ImGui.GetFont()", StringComparison.Ordinal) &&
              !runtime.Contains("AddRectFilled", StringComparison.Ordinal) &&
              !runtime.Contains("VanillaListRow", StringComparison.Ordinal) &&
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

using System.Numerics;

namespace MSUIClient.Engine.UI;

/// <summary>
/// Build-5875 AuctionFrame geometry transcribed from the local vanilla reference.
/// Immediate mode hosts hit targets only; authored textures, fonts, and anchors own presentation.
/// </summary>
public static class AuctionFrameUiLaw
{
    public readonly record struct LogicalRect(float X, float Y, float Width, float Height)
    {
        public Vector2 Min => new(X, Y);
        public Vector2 Size => new(Width, Height);
        public Vector2 Max => new(X + Width, Y + Height);
    }

    public readonly record struct ArtPiece(string Element, string Path, LogicalRect Rect);
    public readonly record struct Header(string Text, LogicalRect Rect);
    public readonly record struct TextureSlice(LogicalRect Rect, Vector2 UvMin, Vector2 UvMax);

    public const float Top = 104f;
    public const string SortTabPath = @"Interface\FriendsFrame\WhoFrame-ColumnTabs";
    public const string FilterBackgroundPath = @"Interface\AuctionFrame\UI-AuctionFrame-FilterBg";
    public const string FilterHighlightPath =
        @"Interface\PaperDollInfoFrame\UI-Character-Tab-Highlight";
    public const string ItemNameFramePath = @"Interface\AuctionFrame\UI-AuctionItemNameFrame";
    public const string ItemSlotPath = @"Interface\Buttons\UI-Quickslot2";
    public const string InputBorderPath = @"Interface\Common\Common-Input-Border";
    public const string RowHighlightPath = @"Interface\HelpFrame\HelpFrameButton-Highlight";

    public static readonly LogicalRect Frame = new(0, 0, 832, 447);
    public static readonly LogicalRect Host = new(0, 0, 832, 479);
    public static readonly LogicalRect Close = new(803, 8, 32, 32);
    public static readonly Vector2 TitleCenter = new(379, 18);
    public static readonly Vector2 FilterTitleCenter = new(85, 85);
    public static readonly Vector2 NameLabel = new(80, 41);
    public static readonly LogicalRect Search = new(83, 57, 150, 20);
    public static readonly LogicalRect SearchButton = new(634, 47, 80, 22);

    public static readonly ArtPiece[] BrowseArt =
    [
        new("AuctionFrameTopLeft", @"Interface\AuctionFrame\UI-AuctionFrame-Browse-TopLeft",
            new(0, 0, 256, 256)),
        new("AuctionFrameTop", @"Interface\AuctionFrame\UI-AuctionFrame-Browse-Top",
            new(256, 0, 320, 256)),
        new("AuctionFrameTopRight", @"Interface\AuctionFrame\UI-AuctionFrame-Browse-TopRight",
            new(576, 0, 256, 256)),
        new("AuctionFrameBotLeft", @"Interface\AuctionFrame\UI-AuctionFrame-Browse-BotLeft",
            new(0, 256, 256, 256)),
        new("AuctionFrameBot", @"Interface\AuctionFrame\UI-AuctionFrame-Browse-Bot",
            new(256, 256, 320, 256)),
        new("AuctionFrameBotRight", @"Interface\AuctionFrame\UI-AuctionFrame-Browse-BotRight",
            new(576, 256, 256, 256)),
    ];

    public static readonly ArtPiece[] BidArt = SkinArt("Bid");
    public static readonly ArtPiece[] AuctionArt = SkinArt("Auction");

    public static ArtPiece[] ArtForTab(int tab) => tab switch
    {
        1 => BidArt,
        2 => AuctionArt,
        _ => BrowseArt,
    };
    public static readonly Header[] BrowseHeaders =
    [
        new("Item", new(186, 82, 214, 19)),
        new("Lvl", new(398, 82, 57, 19)),
        new("Time Left", new(453, 82, 91, 19)),
        new("Seller", new(542, 82, 76, 19)),
        new("Current Bid / Buyout", new(616, 82, 163, 19)),
    ];

    public static readonly Header[] BidHeaders =
    [
        new("Item", new(65, 52, 195, 19)),
        new("Lvl", new(258, 52, 65, 19)),
        new("Time Left", new(321, 52, 79, 19)),
        new("Buyout Price", new(398, 52, 162, 19)),
        new("Bid Status", new(558, 52, 94, 19)),
        new("Current Bid", new(650, 52, 169, 19)),
    ];

    public static readonly Header[] OwnerHeaders =
    [
        new("Item", new(219, 51, 185, 19)),
        new("Time Left", new(402, 51, 100, 19)),
        new("High Bidder", new(500, 51, 108, 19)),
        new("Current Bid", new(606, 51, 193, 19)),
    ];

    public static readonly LogicalRect BidPrice = new(364, 411, 176, 20);
    public static readonly Vector2 BidPriceLabel = new(258, 415);
    public static readonly LogicalRect OwnerItemSlot = new(28, 98, 37, 37);
    public static readonly Vector2 OwnerItemName = new(70, 101);
    public static readonly Vector2 OwnerItemLabel = new(28, 83);
    public static readonly Vector2 OwnerStartPriceLabel = new(28, 153);
    public static readonly LogicalRect OwnerStartPrice = new(31, 168, 176, 20);
    public static readonly Vector2 OwnerDurationLabel = new(28, 203);
    public static readonly Vector2 OwnerShortDuration = new(31, 218);
    public static readonly Vector2 OwnerMediumDuration = new(31, 234);
    public static readonly Vector2 OwnerLongDuration = new(31, 250);
    public static readonly Vector2 OwnerBuyoutLabel = new(28, 297);
    public static readonly LogicalRect OwnerBuyoutPrice = new(30, 312, 176, 20);
    public static readonly Vector2 OwnerDepositLabel = new(28, 364);
    public static readonly Vector2 OwnerDepositMoney = new(87, 362);
    public static readonly LogicalRect OwnerCreateButton = new(18, 388, 191, 20);
    public static readonly LogicalRect OwnerCancelButton = new(620, 411, 126, 22);
    public static readonly Vector2 SellPromptMin = new(520, 78);
    public static readonly Vector2 BidLabelMin = new(480, 113);
    public static readonly Vector2 BuyoutLabelMin = new(470, 149);
    public static readonly Vector2 DurationLabelMin = new(464, 185);
    public static readonly LogicalRect BidInput = new(520, 108, 170, 22);
    public static readonly LogicalRect BuyoutInput = new(520, 144, 170, 22);
    public static readonly LogicalRect DurationInput = new(520, 180, 170, 22);
    public static readonly LogicalRect CreateButton = new(550, 230, 120, 22);
    public static readonly LogicalRect BidButton = new(584, 411, 80, 22);
    public static readonly LogicalRect BuyoutButton = new(664, 411, 80, 22);
    public static readonly LogicalRect BrowseCloseButton = new(744, 411, 80, 22);
    public static readonly LogicalRect CancelButton = new(650, 372, 120, 22);

    public static Vector2 FrameOrigin(float scale) => new(0, Top * scale);
    public static LogicalRect CategoryRow(int row) => new(23, 105 + row * 20, 136, 20);
    public static LogicalRect AuctionRow(int row) => new(195, 110 + row * 37, 597, 37);
    public static LogicalRect BidRow(int row) => new(27, 76 + row * 37, 793, 37);
    public static LogicalRect OwnerRow(int row) => new(219, 76 + row * 37, 576, 37);
    public static Vector2 HeaderTextMin(in Header header) =>
        new(header.Rect.X + 8, header.Rect.Y + 3);
    private static ArtPiece[] SkinArt(string skin) =>
    [
        new("AuctionFrameTopLeft", $@"Interface\AuctionFrame\UI-AuctionFrame-{skin}-TopLeft",
            new(0, 0, 256, 256)),
        new("AuctionFrameTop", $@"Interface\AuctionFrame\UI-AuctionFrame-{skin}-Top",
            new(256, 0, 320, 256)),
        new("AuctionFrameTopRight", $@"Interface\AuctionFrame\UI-AuctionFrame-{skin}-TopRight",
            new(576, 0, 256, 256)),
        new("AuctionFrameBotLeft", $@"Interface\AuctionFrame\UI-AuctionFrame-{skin}-BotLeft",
            new(0, 256, 256, 256)),
        new("AuctionFrameBot", $@"Interface\AuctionFrame\UI-AuctionFrame-{skin}-Bot",
            new(256, 256, 320, 256)),
        new("AuctionFrameBotRight", $@"Interface\AuctionFrame\UI-AuctionFrame-{skin}-BotRight",
            new(576, 256, 256, 256)),
    ];
    public static Vector2 TabMin(int tab, float browseWidth, float bidsWidth) => tab switch
    {
        0 => new(15, 436),
        1 => new(15 + browseWidth - 8, 436),
        _ => new(15 + browseWidth + bidsWidth - 16, 436),
    };

    public static TextureSlice[] MoneyInputBorderSlices(float boxWidth, float rightInset) =>
    [
        new(new(-5, 0, 8, 20), Vector2.Zero, new(.0625f, .625f)),
        new(new(3, 0, Math.Max(0, boxWidth - rightInset - 11), 20),
            new(.0625f, 0), new(.9375f, .625f)),
        new(new(boxWidth - rightInset - 8, 0, 8, 20),
            new(.9375f, 0), new(1, .625f)),
    ];
    public static TextureSlice[] SortHeaderSlices(float width) =>
    [
        new(new(0, 0, 5, 19), Vector2.Zero, new(.078125f, .59375f)),
        new(new(5, 0, Math.Max(0, width - 9), 19), new(.078125f, 0),
            new(.90625f, .59375f)),
        new(new(Math.Max(5, width - 4), 0, 4, 19), new(.90625f, 0),
            new(.96875f, .59375f)),
    ];

    public static TextureSlice[] ItemNameSlices(float rowWidth) =>
    [
        new(new(34, 2, 10, 32), Vector2.Zero, new(.078125f, 1)),
        new(new(44, 2, Math.Max(0, rowWidth - 54), 32), new(.078125f, 0),
            new(.75f, 1)),
        new(new(Math.Max(44, rowWidth - 10), 2, 10, 32), new(.75f, 0),
            new(.828125f, 1)),
    ];

    public static string TimeLeftText(uint milliseconds) => milliseconds switch
    {
        0 => "",
        <= 30 * 60 * 1000 => "Short",
        <= 2 * 60 * 60 * 1000 => "Medium",
        <= 12 * 60 * 60 * 1000 => "Long",
        _ => "Very Long",
    };
}

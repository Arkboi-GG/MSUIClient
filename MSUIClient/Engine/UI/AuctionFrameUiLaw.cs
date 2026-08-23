using System.Numerics;

namespace MSUIClient.Engine.UI;

/// <summary>
/// Geometry ownership for MSUI's existing Auction House surface. Current Benilla has no
/// AuctionFrame implementation, so these values preserve the established desktop presentation
/// while keeping the immediate-mode renderer a host rather than a layout authority.
/// </summary>
public static class AuctionFrameUiLaw
{
    public readonly record struct LogicalRect(float X, float Y, float Width, float Height)
    {
        public Vector2 Min => new(X, Y);
        public Vector2 Size => new(Width, Height);
    }

    public readonly record struct ArtPiece(string Element, string Path, LogicalRect Rect);
    public readonly record struct Header(string Text, LogicalRect Rect);

    public const float Top = 104f;
    public static readonly LogicalRect Frame = new(0, 0, 832, 447);
    public static readonly LogicalRect Host = new(0, 0, 832, 479);
    public static readonly LogicalRect Close = new(803, 8, 32, 32);
    public static readonly Vector2 TitleCenter = new(384, 18);
    public static readonly LogicalRect Search = new(186, 48, 300, 22);
    public static readonly LogicalRect SearchButton = new(496, 48, 80, 22);

    public static readonly ArtPiece[] Art =
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

    public static readonly Header[] BrowseHeaders =
    [
        new("Item", new(186, 82, 214, 19)),
        new("Lvl", new(398, 82, 57, 19)),
        new("Time Left", new(453, 82, 91, 19)),
        new("Seller", new(542, 82, 76, 19)),
        new("Current Bid / Buyout", new(616, 82, 163, 19)),
    ];

    public static readonly Vector2 SellPromptMin = new(520, 78);
    public static readonly Vector2 BidLabelMin = new(480, 113);
    public static readonly Vector2 BuyoutLabelMin = new(470, 149);
    public static readonly Vector2 DurationLabelMin = new(464, 185);
    public static readonly LogicalRect BidInput = new(520, 108, 170, 22);
    public static readonly LogicalRect BuyoutInput = new(520, 144, 170, 22);
    public static readonly LogicalRect DurationInput = new(520, 180, 170, 22);
    public static readonly LogicalRect CreateButton = new(550, 230, 120, 22);
    public static readonly LogicalRect BidButton = new(625, 372, 80, 22);
    public static readonly LogicalRect BuyoutButton = new(711, 372, 80, 22);
    public static readonly LogicalRect CancelButton = new(650, 372, 120, 22);

    public static Vector2 FrameOrigin(float scale) => new(0, Top * scale);
    public static LogicalRect CategoryRow(int row) => new(22, 82 + row * 22, 145, 20);
    public static LogicalRect AuctionRow(int row) => new(195, 110 + row * 37, 597, 37);
    public static Vector2 HeaderTextMin(in Header header) => new(header.Rect.X + 4, 86);
    public static Vector2 TabMin(int tab, float browseWidth, float bidsWidth) => tab switch
    {
        0 => new(15, 436),
        1 => new(15 + browseWidth - 8, 436),
        _ => new(15 + browseWidth + bidsWidth - 16, 436),
    };
}

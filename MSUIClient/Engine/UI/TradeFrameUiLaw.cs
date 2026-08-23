using System.Numerics;

namespace MSUIClient.Engine.UI;

/// <summary>Current build-5875 TradeFrame geometry and bounded accept/status behavior.</summary>
public static class TradeFrameUiLaw
{
    public readonly record struct StatusError(string GlobalStringKey, string Fallback);
    public readonly record struct SlotText(string Text, uint? Color);
    public readonly record struct TextureSlice(LogicalRect Rect, Vector2 UvMin, Vector2 UvMax);

    public readonly record struct LogicalRect(float X, float Y, float Width, float Height)
    {
        public Vector2 Min => new(X, Y);
        public Vector2 Size => new(Width, Height);
    }

    public enum CancelAction
    {
        Close,
        Unaccept,
    }

    public const int SlotCount = 7;
    public const int OfferedSlotCount = 6;
    public const float Width = 384;
    public const float Height = 512;
    public const float Top = 104;
    public const float SlotWidth = 153;
    public const float SlotHeight = 37;
    public const float SlotPitch = 44;
    public const float EnchantGap = 28;
    public const float HighlightCap = 16;
    public const string EnchantLabel = "Will Be Enchanted";
    public const string NonTradedLabel = "Will Not Be Traded";
    public const uint ProposedEnchantColor = 0xff20ff20;
    public const string HighlightPath = @"Interface\TradeFrame\UI-TradeFrame-Highlight";
    public const string EnchantIconPath = @"Interface\TradeFrame\UI-TradeFrame-EnchantIcon";
    public const string EmptySlotPath = @"Interface\Buttons\UI-EmptySlot";
    public const string ItemNameFramePath = @"Interface\QuestFrame\UI-QuestItemNameFrame";
    public const string MoneyIconPath = @"Interface\MoneyFrame\UI-MoneyIcons";
    public const string MoneyFont = "NumberFontNormal";
    public const float MoneyIconSize = 13;
    public const float MoneyGap = 4;

    public static readonly LogicalRect PlayerPortrait = new(7, 6, 60, 60);
    public static readonly LogicalRect RecipientPortrait = new(183, 6, 60, 60);
    public static readonly LogicalRect PlayerName = new(75, 17, 100, 12);
    public static readonly LogicalRect RecipientName = new(245, 17, 80, 12);
    public static readonly LogicalRect PlayerHighlight = new(19, 100, 161, 266);
    public static readonly LogicalRect RecipientHighlight = new(189, 100, 161, 266);
    public static readonly LogicalRect PlayerEnchantHighlight = new(19, 370, 161, 61);
    public static readonly LogicalRect RecipientEnchantHighlight = new(189, 370, 161, 61);
    // TradePlayerInputMoney is a 176x18 parent at (26,73). Its three 20px-high
    // edit boxes are LEFT anchored to that parent's vertical midpoint.
    public static readonly LogicalRect PlayerGoldInput = new(26, 72, 55, 20);
    public static readonly LogicalRect PlayerSilverInput = new(107, 72, 30, 20);
    public static readonly LogicalRect PlayerCopperInput = new(153, 72, 30, 20);
    public static readonly Vector2 PlayerGoldCoin = new(68, 76);
    public static readonly Vector2 PlayerSilverCoin = new(124, 76);
    public static readonly Vector2 PlayerCopperCoin = new(170, 76);
    // TradeRecipientMoneyFrame TOPRIGHT(-40,-80): its rightmost 13px coin frame
    // ends at x=344 and the remaining denominations grow left in 4px gaps.
    public static readonly Vector2 RecipientMoneyRightTop = new(344, 80);
    public static readonly LogicalRect TradeButton = new(186, 435, 85, 22);
    public static readonly LogicalRect CancelButton = new(274, 435, 77, 22);
    public static readonly LogicalRect CloseButton = new(327, 8, 32, 32);
    public static readonly Vector2 PlayerEnchantLabel = new(26, 374);
    public static readonly Vector2 RecipientEnchantLabel = new(196, 374);

    public static Vector2 FrameOrigin(float scale) => new(0, Top * scale);
    public static Vector2 FrameSize(float scale) => new(Width * scale, Height * scale);

    public static LogicalRect Slot(bool player, int index)
    {
        int clamped = Math.Clamp(index, 0, SlotCount - 1);
        float y = clamped < OfferedSlotCount
            ? 104 + clamped * SlotPitch
            : 104 + (OfferedSlotCount - 1) * SlotPitch + SlotHeight + EnchantGap;
        return new(player ? 26 : 195, y, SlotWidth, SlotHeight);
    }

    public static LogicalRect SlotButton(bool player, int index)
    {
        LogicalRect slot = Slot(player, index);
        return new(slot.X, slot.Y, 37, 37);
    }

    public static LogicalRect EmptySlot(bool player, int index)
    {
        LogicalRect slot = Slot(player, index);
        return new(slot.X - 13, slot.Y - 13, 64, 64);
    }

    public static LogicalRect NameFrame(bool player, int index)
    {
        LogicalRect slot = Slot(player, index);
        return new(slot.X + 31, slot.Y - 13, 124, 64);
    }

    public static Vector2 NameText(bool player, int index)
    {
        LogicalRect slot = Slot(player, index);
        return new(slot.X + 46, slot.Y + 12);
    }

    public static LogicalRect EnchantIcon(bool player) =>
        new(Slot(player, SlotCount - 1).X, Slot(player, SlotCount - 1).Y, 62, 62);

    public static TextureSlice[] HighlightSlices(LogicalRect rect) =>
    [
        new(new(rect.X, rect.Y, rect.Width, HighlightCap),
            Vector2.Zero, new(.62890625f, .0625f)),
        new(new(rect.X, rect.Y + HighlightCap, rect.Width,
                Math.Max(0, rect.Height - HighlightCap * 2)),
            new(0, .0625f), new(.62890625f, .9375f)),
        new(new(rect.X, rect.Y + Math.Max(0, rect.Height - HighlightCap),
                rect.Width, Math.Min(HighlightCap, rect.Height)),
            new(0, .9375f), new(.62890625f, 1))
    ];

    public static Vector2 CountPosition(Vector2 buttonMaximum, float textEm, float scale) =>
        new(buttonMaximum.X - 2 * scale, buttonMaximum.Y - textEm - 2 * scale);

    public static int ComposeMoney(int gold, int silver, int copper) => checked((int)Math.Min(
        int.MaxValue, (long)Math.Max(0, gold) * 10_000L +
                      Math.Clamp(silver, 0, 99) * 100L + Math.Clamp(copper, 0, 99)));

    public static (int Gold, int Silver, int Copper) SplitMoney(int copper) =>
        (Math.Max(0, copper) / 10_000, Math.Max(0, copper) / 100 % 100,
            Math.Max(0, copper) % 100);

    public static Vector2 CoinUvMin(int denomination) =>
        new(Math.Clamp(denomination, 0, 2) * .25f, 0);

    public static Vector2 CoinUvMax(int denomination) =>
        new((Math.Clamp(denomination, 0, 2) + 1) * .25f, 1);

    public static Vector2 CoinSize(float scale) => new(MoneyIconSize * scale);

    public static CancelAction CancelClick(bool accepted) =>
        accepted ? CancelAction.Unaccept : CancelAction.Close;

    public static bool StatusCloses(uint status) => status is
        0 or 3 or 5 or 6 or 8 or 9 or 10 or 11 or 12 or
        14 or 15 or 16 or 17 or 18 or 19 or 20 or 21;

    /// <summary>GlobalStrings.lua-backed red feedback for build-5875 trade refusals.</summary>
    public static StatusError? ErrorForStatus(uint status) => status switch
    {
        0 or 5 => new("ERR_PLAYER_BUSY_S", "%s is busy right now."),
        6 => new("ERR_GENERIC_NO_TARGET", "You have no target."),
        10 => new("ERR_TRADE_TOO_FAR", "Trade target is too far away."),
        11 => new("ERR_PLAYER_WRONG_FACTION", "Target is not part of your alliance."),
        14 => new("ERR_IGNORING_YOU_S", "%s is ignoring you."),
        15 => new("ERR_GENERIC_STUNNED", "You are stunned"),
        16 => new("ERR_TARGET_STUNNED", "Target is stunned"),
        17 => new("ERR_PLAYER_DEAD", "You can't do that when you're dead."),
        18 => new("ERR_TRADE_TARGET_DEAD", "You can't trade with dead players."),
        19 => new("ERR_LOGGING_OUT", "You are logging out"),
        20 => new("ERR_TARGET_LOGGING_OUT", "That player is logging out"),
        21 => new("ERR_RESTRICTED_ACCOUNT", "Trial accounts cannot perform that action"),
        22 => new("ERR_TRADE_WRONG_REALM",
            "You may only trade conjured items to players from other realms"),
        _ => null,
    };

    public static string FormatStatusError(StatusError error, string partner,
        Func<string, string, string> globalString) =>
        globalString(error.GlobalStringKey, error.Fallback)
            .Replace("%s", partner, StringComparison.Ordinal);

    public static SlotText ItemSlotText(string itemName, bool enchantSlot,
        string? proposedEnchant) => enchantSlot
        ? proposedEnchant is { Length: > 0 }
            ? new(proposedEnchant, ProposedEnchantColor)
            : new(NonTradedLabel, 0xffffffff)
        : new(itemName, null);
}

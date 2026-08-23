using System.Numerics;

namespace MSUIClient.Engine.UI;

/// <summary>Current BankFrame and CONFIRM_BUY_BANK_SLOT presentation law.</summary>
public static class BankFrameUiLaw
{
    public readonly record struct TooltipSeat(Vector2 Anchor, Vector2 Pivot);
    public const float Width = 384f;
    public const float Height = 512f;
    public const float Top = 104f;
    public const float ArtSize = 512f;
    public const string Art = @"Interface\BankFrame\UI-BankFrame";
    public const string FallbackTitle = "Banker";
    public const string TitleFont = "GameFontHighlight";
    public const string OpenSound = "igMainMenuOpen";
    public const string CloseSound = "igMainMenuClose";
    public const string PurchaseSound = "igMainMenuOption";
    public const string SoundCategory = "ui.bank";
    public static readonly Vector2 FrameLogicalSize = new(Width, Height);
    public static readonly Vector2 ArtLogicalSize = new(ArtSize, ArtSize);
    public static readonly Vector2 PortraitOffset = new(7, 6);
    public const float PortraitSize = 60f;
    public static readonly Vector2 TitleCenter = new(198, 23);
    public static readonly Vector2 ItemSlotsLabelCenter = new(181, 61);
    public static readonly Vector2 BagSlotsLabelCenter = new(181, 261);
    public static readonly Vector2 PurseRightTop = new(333, 397);
    public static readonly Vector2 PurchaseMessageCenter = new(187, 341);
    public const float PurchaseMessageWidth = 280f;
    public static readonly Vector2 CostLabel = new(57, 365);
    public static readonly Vector2 PurchaseButton = new(183, 360.5f);
    public static readonly Vector2 PurchaseButtonSize = new(124, 21);
    public static readonly Vector2 CloseButton = new(322, 8);
    public static readonly Vector2 CloseButtonSize = new(32, 32);
    public static readonly Vector2 ItemSlotFirst = new(40, 73);
    public static readonly Vector2 ItemSlotSize = new(37, 37);
    public static readonly Vector2 SlotRingMinOffset = new(-14, -14);
    public static readonly Vector2 SlotRingMaxOffset = new(50, 50);
    public const float ItemSlotPitchX = 49f;
    public const float ItemSlotPitchY = 44f;
    public static readonly Vector2 BankBagSlotFirst = new(40, 274);
    public const float BankBagSlotPitch = 49f;
    public const float CostMoneyGap = 4f;

    public const string PurchaseMessageText =
        "Do you wish to purchase space for an additional bag?";
    public const string CostText = "Cost:";
    public const string PurchaseText = "Purchase";

    public static Vector2 FrameOrigin(float scale) => new(0, Top * scale);
    public static Vector2 FrameSize(float scale) => new(Width * scale, Height * scale);
    public static Vector2 ItemSlotMin(int index)
    {
        if (index is < 0 or >= 24) throw new ArgumentOutOfRangeException(nameof(index));
        int row = index / 6, column = index % 6;
        return ItemSlotFirst + new Vector2(column * ItemSlotPitchX, row * ItemSlotPitchY);
    }
    public static Vector2 BankBagSlotMin(int index) => index is >= 0 and < 6
        ? BankBagSlotFirst + new Vector2(index * BankBagSlotPitch, 0)
        : throw new ArgumentOutOfRangeException(nameof(index));
    // BenillaBankBagButton_OnEnter uses ANCHOR_RIGHT.
    public static TooltipSeat BankBagTooltipSeat(Vector2 ownerMin, float scale) =>
        new(ownerMin + new Vector2(ItemSlotSize.X * scale, 0), new Vector2(0, 1));
    public static Vector2 CostMoneyOffset(float labelWidthPixels, float scale) =>
        new(labelWidthPixels + CostMoneyGap * scale, 0);
    public static IReadOnlyList<string> WrapPurchaseMessage(float width,
        Func<string, float> measure)
    {
        if (width <= 0) return [];
        var lines = new List<string>();
        string current = "";
        foreach (string word in PurchaseMessageText.Split(' ',
                     StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = current.Length == 0 ? word : current + " " + word;
            if (current.Length > 0 && measure(candidate) > width)
            {
                lines.Add(current);
                current = word;
            }
            else current = candidate;
        }
        if (current.Length > 0) lines.Add(current);
        return lines;
    }
    public static Vector2 PurchaseMessageMinimum(int lineCount, float linePitchLogical) =>
        new(PurchaseMessageCenter.X - PurchaseMessageWidth * .5f,
            PurchaseMessageCenter.Y - Math.Max(0, lineCount) * linePitchLogical * .5f);
    public static Vector2 PurchaseMessageLine(int line, float linePitchPixels) =>
        new(0, Math.Max(0, line) * linePitchPixels);
    public static string Title(string? npcName) =>
        string.IsNullOrWhiteSpace(npcName) ? FallbackTitle : npcName.Trim();
}

/// <summary>StaticPopup seat and geometry for the bank-slot purchase question.</summary>
public static class BankPurchaseConfirmUiLaw
{
    public const float PopupWidth = 320f;
    public const float TextWidth = 290f;
    public const float TextTop = 16f;
    public const float ButtonWidth = 128f;
    public const float ButtonHeight = 20f;
    public const float ButtonOneX = 26f;
    public const float ButtonTwoX = 167f;
    public const float PopupTop = 128f;
    public const string Prompt = "Do you want to purchase a bank slot for:";
    public const string AcceptText = "Yes";
    public const string CancelText = "No";
    public static readonly Vector2 ButtonSize = new(ButtonWidth, ButtonHeight);
    public static readonly Vector2 ButtonUvMax = new(1f, .625f);

    public readonly record struct ScreenRect(Vector2 Min, Vector2 Size);

    public static float PopupHeight(float textHeight) =>
        StaticPopupCoordinatorLaw.Height(textHeight, ButtonHeight);

    public static ScreenRect PopupRect(Vector2 displayPixels, float scale, float textHeight)
    {
        float width = PopupWidth * scale;
        float height = PopupHeight(textHeight) * scale;
        return new(new Vector2((displayPixels.X - width) * .5f, PopupTop * scale),
            new Vector2(width, height));
    }

    public static Vector2 TextCenter(float textLineCenterY) =>
        new(PopupWidth * .5f, TextTop + textLineCenterY);

    public static float ButtonTop(float textHeight) => 16f + Math.Max(0, textHeight) + 8f;

    public static Vector2 ButtonMin(int buttonIndex, float textHeight) => buttonIndex switch
    {
        1 => new(ButtonOneX, ButtonTop(textHeight)),
        2 => new(ButtonTwoX, ButtonTop(textHeight)),
        _ => throw new ArgumentOutOfRangeException(nameof(buttonIndex)),
    };
}

using System.Numerics;

namespace MSUIClient.Engine.UI;

/// <summary>Current BankFrame and CONFIRM_BUY_BANK_SLOT presentation law.</summary>
public static class BankFrameUiLaw
{
    public const float Width = 384f;
    public const float Height = 512f;
    public const float Top = 104f;
    public const float ArtSize = 512f;
    public const string Art = @"Interface\BankFrame\UI-BankFrame";
    public const string FallbackTitle = "Banker";
    public const string OpenSound = "igMainMenuOpen";
    public const string CloseSound = "igMainMenuClose";
    public const string PurchaseSound = "igMainMenuOption";
    public const string SoundCategory = "ui.bank";
    public static readonly Vector2 PortraitOffset = new(7, 6);
    public const float PortraitSize = 60f;
    public static readonly Vector2 TitleCenter = new(198, 23);
    public static readonly Vector2 PurseRightTop = new(333, 397);
    public static readonly Vector2 PurchaseMessage = new(37, 334);
    public const float PurchaseMessageWidth = 280f;
    public static readonly Vector2 CostLabel = new(57, 365);
    public static readonly Vector2 PurchaseButton = new(183, 351);
    public static readonly Vector2 PurchaseButtonSize = new(124, 21);
    public static readonly Vector2 CloseButton = new(322, 8);
    public static readonly Vector2 BankBagSlotFirst = new(35, 300);
    public const float BankBagSlotPitch = 43f;

    public const string PurchaseMessageText =
        "Do you wish to purchase space for an additional bag?";
    public const string CostText = "Cost:";
    public const string PurchaseText = "Purchase";

    public static Vector2 FrameOrigin(float scale) => new(0, Top * scale);
    public static Vector2 FrameSize(float scale) => new(Width * scale, Height * scale);
    public static Vector2 BankBagSlotMin(int index) => index is >= 0 and < 6
        ? BankBagSlotFirst + new Vector2(index * BankBagSlotPitch, 0)
        : throw new ArgumentOutOfRangeException(nameof(index));
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

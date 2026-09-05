using System.Numerics;
using MSUIClient.Net;

namespace MSUIClient.Engine.UI;

/// <summary>
/// Frozen GroupLootFrame.xml + StaticPopup CONFIRM_LOOT_ROLL policy. Every screen seat and
/// child rectangle is expressed here so the ImGui adapter only supplies input and paint.
/// </summary>
public static class GroupLootFrameUiLaw
{
    public enum IconClickAction { None, DressUp, InsertChat }

    public const float Width = 243f;
    public const float Height = 84f;
    public const float StackGap = 15f;
    public const float ItemPlateSize = 64f;
    public const float IconSize = 34f;
    public const float VoteButtonSize = 32f;
    public const float TimerWidth = 152f;
    public const float TimerHeight = 10f;
    public const float TimerBorderWidth = 156f;
    public const float TimerBorderHeight = 20f;
    public const float ConfirmWidth = 320f;
    public const float ConfirmTextWidth = 290f;
    public const float ConfirmTop = 128f;
    public const float ConfirmButtonWidth = 128f;
    public const float ConfirmButtonHeight = 20f;
    public const string ConfirmText = "Looting this item will bind it to you.";
    public const string AcceptText = "Okay";
    public const string CancelText = "Cancel";
    public const string NameFont = "GameFontNormalSmall";

    public const string EmptySlotPath = @"Interface\Buttons\UI-EmptySlot";
    public const string NamePlatePath = @"Interface\MerchantFrame\UI-Merchant-LabelSlots";
    public const string DragonPath = @"Interface\DialogFrame\UI-DialogBox-Gold-Dragon";
    public const string PlainCornerPath = @"Interface\DialogFrame\UI-DialogBox-Corner";
    public const string GoldCornerPath = @"Interface\DialogFrame\UI-DialogBox-Gold-Corner";
    public const string PassUpPath = @"Interface\Buttons\UI-Panel-MinimizeButton-Up";
    public const string PassDownPath = @"Interface\Buttons\UI-Panel-MinimizeButton-Down";
    public const string PassHighlightPath = @"Interface\Buttons\UI-Panel-MinimizeButton-Highlight";
    public const string NeedUpPath = @"Interface\Buttons\UI-GroupLoot-Dice-Up";
    public const string NeedDownPath = @"Interface\Buttons\UI-GroupLoot-Dice-Down";
    public const string NeedHighlightPath = @"Interface\Buttons\UI-GroupLoot-Dice-Highlight";
    public const string GreedUpPath = @"Interface\Buttons\UI-GroupLoot-Coin-Up";
    public const string GreedDownPath = @"Interface\Buttons\UI-GroupLoot-Coin-Down";
    public const string GreedHighlightPath = @"Interface\Buttons\UI-GroupLoot-Coin-Highlight";
    public const string TimerFillPath = @"Interface\PaperDollInfoFrame\UI-Character-Skills-Bar";
    public const string TimerBorderPath = @"Interface\PaperDollInfoFrame\UI-Character-Skills-BarBorder";

    public static readonly Vector2 FrameLogicalSize = new(Width, Height);
    public static readonly Vector2 ItemPlateLogicalSize = new(ItemPlateSize, ItemPlateSize);
    public static readonly Vector2 NamePlateSize = new(128, 64);
    public static readonly Vector2 DragonSize = new(120, 120);
    public static readonly Vector2 CornerSize = new(32, 32);
    public static readonly Vector2 IconLogicalSize = new(IconSize, IconSize);
    public static readonly Vector2 VoteButtonLogicalSize = new(VoteButtonSize, VoteButtonSize);
    public static readonly Vector2 TimerBorderLogicalSize =
        new(TimerBorderWidth, TimerBorderHeight);
    public static readonly Vector2 ConfirmButtonSize =
        new(ConfirmButtonWidth, ConfirmButtonHeight);
    public static readonly Vector2 ConfirmButtonUvMax = new(1, .625f);

    public readonly record struct ScreenRect(Vector2 Min, Vector2 Size);
    public readonly record struct TooltipSeat(Vector2 Anchor, Vector2 Pivot);

    // Icon and Pass/Need/Greed OnEnter handlers all use ANCHOR_RIGHT.
    public static TooltipSeat RightTooltipSeat(Vector2 ownerMin, Vector2 ownerSize) =>
        new(ownerMin + new Vector2(ownerSize.X, 0), new Vector2(0, 1));

    public static ScreenRect FrameRect(Vector2 displayPixels, float scale, int frameIndex,
        in UiParentManagedState managed)
    {
        if (frameIndex is < 0 or >= GroupLootRollState.FrameCount)
            throw new ArgumentOutOfRangeException(nameof(frameIndex));
        UiParentManagedPlacement placement = UiParentUiLaw.Resolve(
            UiParentManagedConsumer.GroupLoot, managed);
        Vector2 size = new Vector2(Width, Height) * scale;
        float bottom = displayPixels.Y - placement.Y * scale -
            frameIndex * (Height + StackGap) * scale;
        return new(new Vector2((displayPixels.X - size.X) * .5f, bottom - size.Y), size);
    }

    public static Vector2 ItemPlateMin => new(3, 3);
    public static Vector2 NamePlateMin => new(58, 13);
    public static Vector2 NameMin => new(62, 15);
    public static Vector2 NameSize => new(90, 30);
    public static Vector2 DragonMin => new(-30, -15);
    public static Vector2 CornerMin => new(205, 7);
    public static Vector2 IconMin => new(18, 18);
    public static Vector2 PassMin => new(210, 2);
    public static Vector2 NeedMin => new(174, 14);
    public static Vector2 GreedMin => new(172, 44);
    public static Vector2 TimerMin => new(16, 57);
    public static Vector2 TimerBorderMin => new(14, 52);

    public static Vector2 TimerFillSize(float ratio, float scale) =>
        new(TimerWidth * Math.Clamp(ratio, 0, 1) * scale, TimerHeight * scale);

    public static Vector2 TimerFillUvMax(float ratio) =>
        new(Math.Clamp(ratio, 0, 1), 1);

    public static IReadOnlyList<string> WrapItemName(string? text, float width,
        int maximumLines, Func<string, float> measure)
    {
        if (string.IsNullOrWhiteSpace(text) || width <= 0 || maximumLines <= 0) return [];
        var lines = new List<string>();
        string current = "";
        foreach (string word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = current.Length == 0 ? word : current + " " + word;
            if (current.Length > 0 && measure(candidate) > width)
            {
                lines.Add(current);
                if (lines.Count == maximumLines) return lines;
                current = word;
            }
            else current = candidate;
        }
        if (current.Length > 0 && lines.Count < maximumLines) lines.Add(current);
        return lines;
    }

    public static Vector2 NameLineMin(Vector2 frameMin, float scale, int line,
        int lineCount, float pitch) => new(
        frameMin.X + NameMin.X * scale,
        frameMin.Y + NameMin.Y * scale +
            Math.Max(0, NameSize.Y * scale - Math.Max(0, lineCount) * pitch) * .5f +
            Math.Max(0, line) * pitch);

    public static string ItemLink(uint itemId, string name, uint quality) =>
        UiTextMarkupLaw.ItemLink(itemId, name, quality);

    public static IconClickAction IconAction(bool clicked, bool control, bool shift,
        bool chatOpen, bool linkAvailable)
    {
        if (!clicked || !linkAvailable) return IconClickAction.None;
        if (control) return IconClickAction.DressUp;
        if (shift && chatOpen) return IconClickAction.InsertChat;
        return IconClickAction.None;
    }

    public static ScreenRect ConfirmRect(Vector2 displayPixels, float scale, float textHeight)
    {
        float height = StaticPopupCoordinatorLaw.Height(textHeight, ConfirmButtonHeight);
        Vector2 size = new Vector2(ConfirmWidth, height) * scale;
        return new(new Vector2((displayPixels.X - size.X) * .5f, ConfirmTop * scale), size);
    }

    public static Vector2 ConfirmTextCenter(float lineCenterY) =>
        new(ConfirmWidth * .5f, 16f + lineCenterY);

    public static Vector2 ConfirmButtonMin(int index, float textHeight) => index switch
    {
        1 => new(26, 24f + Math.Max(0, textHeight)),
        2 => new(167, 24f + Math.Max(0, textHeight)),
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };
}

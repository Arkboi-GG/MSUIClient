namespace MSUIClient.Engine.UI;

/// <summary>Build-5875 DELETE_ITEM StaticPopup entry and authored geometry.</summary>
public static class DeleteItemUiLaw
{
    public readonly record struct PopupLayout(
        float Width, float Height, StaticPopupCoordinatorLaw.Rect Text,
        StaticPopupCoordinatorLaw.Rect Alert, StaticPopupCoordinatorLaw.Rect Button1,
        StaticPopupCoordinatorLaw.Rect Button2)
    {
        public System.Numerics.Vector2 Size => new(Width, Height);
    }

    public const string PopupType = "DELETE_ITEM";
    public const float Width = 420f;
    public const float TextWidth = 290f;
    public const float TextTop = 16f;
    public const float ButtonWidth = 128f;
    public const float ButtonHeight = 20f;
    public const float ButtonGap = 13f;
    public const float AlertSize = 64f;
    public const float AlertLeft = 12f;
    public const float ScreenTop = StaticPopupCoordinatorLaw.ScreenTop;
    public const float SlotGap = StaticPopupCoordinatorLaw.SecondSlotGap;
    public const string AlertPath = @"Interface\DialogFrame\DialogAlertIcon";

    public static readonly StaticPopupCoordinatorLaw.Definition Definition = new(
        PopupType,
        WhileDead: true,
        HideOnEscape: true,
        HasAccept: true,
        HasCancel: true,
        HasOnUpdate: true,
        ShowAlert: true);

    public static string Text(string itemName) => $"Do you want to destroy {itemName}?";
    public static float Height(float textHeight) =>
        StaticPopupCoordinatorLaw.Height(textHeight, ButtonHeight);
    public static float ButtonTop(float textHeight) => TextTop + textHeight + 8f;
    public static float ButtonOneX(float width) => width * .5f - 134f;
    public static float ButtonTwoX(float width) => ButtonOneX(width) + ButtonWidth + ButtonGap;

    public static PopupLayout Layout(float textHeight)
    {
        float safeTextHeight = Math.Max(0, textHeight);
        float height = Height(safeTextHeight);
        float buttonTop = ButtonTop(safeTextHeight);
        return new(Width, height,
            new((Width - TextWidth) * .5f, TextTop, TextWidth, safeTextHeight),
            new(AlertLeft, (height - AlertSize) * .5f, AlertSize, AlertSize),
            new(ButtonOneX(Width), buttonTop, ButtonWidth, ButtonHeight),
            new(ButtonTwoX(Width), buttonTop, ButtonWidth, ButtonHeight));
    }

    public static System.Numerics.Vector2 TextLineCenter(
        PopupLayout layout, float linePitch, int lineIndex) =>
        new(layout.Width * .5f,
            layout.Text.Y + (Math.Max(0, lineIndex) + .5f) * linePitch);

    public static (int Slot, StaticPopupCoordinatorLaw.Instance Instance)? Visible(
        StaticPopupCoordinatorLaw.Slots slots)
    {
        if (slots.First is { } first && first.Definition.Type == PopupType) return (1, first);
        if (slots.Second is { } second && second.Definition.Type == PopupType) return (2, second);
        return null;
    }
}

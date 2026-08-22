namespace MSUIClient.Engine.UI;

/// <summary>Build-5875 DELETE_ITEM StaticPopup entry and authored geometry.</summary>
public static class DeleteItemUiLaw
{
    public const string PopupType = "DELETE_ITEM";
    public const float Width = 420f;
    public const float TextWidth = 290f;
    public const float TextTop = 16f;
    public const float ButtonWidth = 128f;
    public const float ButtonHeight = 20f;
    public const float ButtonGap = 13f;
    public const float AlertSize = 64f;
    public const float AlertLeft = 12f;
    public const float ScreenTop = 128f;
    public const float SlotGap = 8f;
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

    public static (int Slot, StaticPopupCoordinatorLaw.Instance Instance)? Visible(
        StaticPopupCoordinatorLaw.Slots slots)
    {
        if (slots.First is { } first && first.Definition.Type == PopupType) return (1, first);
        if (slots.Second is { } second && second.Definition.Type == PopupType) return (2, second);
        return null;
    }
}

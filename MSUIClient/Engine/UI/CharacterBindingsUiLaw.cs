namespace MSUIClient.Engine.UI;

/// <summary>
/// Current Benilla character-specific binding-set toggle and its shared StaticPopup entry.
/// The misspelling in <see cref="ConfirmText"/> is source-exact.
/// </summary>
public static class CharacterBindingsUiLaw
{
    public const string PopupType = "CONFIRM_DELETING_CHARACTER_SPECIFIC_BINDINGS";
    public const string ConfirmText =
        "Really switch to general key bindings?  All key bindings specific to this character will be permanantly deleted.";
    public const string AcceptText = "Okay";
    public const string CancelText = "Cancel";
    public const string ToggleSound = "igMainMenuOptionCheckBoxOn";
    public const float Width = StaticPopupCoordinatorLaw.BaseWidth;
    public const float TextWidth = StaticPopupCoordinatorLaw.TextWidth;
    public const float TextTop = StaticPopupCoordinatorLaw.TextTop;
    public const float ButtonWidth = StaticPopupCoordinatorLaw.ButtonWidth;
    public const float ButtonHeight = StaticPopupCoordinatorLaw.ButtonHeight;
    public const float ButtonGap = 13f;

    // The source entry has two buttons and an OnAccept, but no OnCancel callback and does not
    // opt into whileDead or hideOnEscape.
    public static readonly StaticPopupCoordinatorLaw.Definition Definition = new(
        PopupType,
        HasAccept: true);

    public static float Height(float textHeight) =>
        StaticPopupCoordinatorLaw.Height(textHeight, ButtonHeight);

    public static float ButtonTop(float textHeight) => TextTop + textHeight + 8f;
    public static float ButtonOneX => Width * .5f - 134f;
    public static float ButtonTwoX => ButtonOneX + ButtonWidth + ButtonGap;

    public static string CharacterFileName(ulong playerGuid) =>
        $"keybindings.character-{playerGuid:X16}.json";

    public static (int Slot, StaticPopupCoordinatorLaw.Instance Instance)? Visible(
        StaticPopupCoordinatorLaw.Slots slots)
    {
        if (slots.First is { } first && first.Definition.Type == PopupType) return (1, first);
        if (slots.Second is { } second && second.Definition.Type == PopupType) return (2, second);
        return null;
    }
}

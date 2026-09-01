namespace MSUIClient.Engine.UI;

/// <summary>Build-5875 DELETE_ITEM StaticPopup entry and authored geometry.</summary>
public static class DeleteItemUiLaw
{
    public readonly record struct PopupLayout(
        float Width, float Height, StaticPopupCoordinatorLaw.Rect Text,
        StaticPopupCoordinatorLaw.Rect Alert, StaticPopupCoordinatorLaw.Rect Button1,
        StaticPopupCoordinatorLaw.Rect Button2, StaticPopupCoordinatorLaw.Rect EditBox)
    {
        public System.Numerics.Vector2 Size => new(Width, Height);
    }

    public const string PopupType = "DELETE_ITEM";

    // ── typed confirmation for high-quality items ───────────────────────────
    //
    // NOT PARITY, AND DELIBERATELY SO. Build 5875 ships exactly one item-destroy
    // popup: StaticPopupDialogs["DELETE_ITEM"], text "Do you want to destroy %s?",
    // Yes/No, showAlert, no edit box and no quality test anywhere. The typed guard
    // is a later-expansion idea (DELETE_GOOD_ITEM), and this is the MSUI version of
    // it, in MSUI's own words rather than a guessed-at 2.x string. The plain popup
    // above is untouched and still what every green-and-below destroy gets.
    //
    // The threshold is the one thing worth arguing about; Epic is where the reference
    // put it and where "purples" starts, so Epic, Legendary and Artifact are guarded.

    public const string ConfirmPopupType = "DELETE_GOOD_ITEM";

    /// <summary>The word that has to be typed. Matched case-insensitively after a trim.</summary>
    public const string ConfirmWord = "DELETE";

    /// <summary>ItemQuality: 0 Poor, 1 Common, 2 Uncommon, 3 Rare, 4 Epic, 5 Legendary, 6 Artifact.</summary>
    public const uint TypedConfirmMinimumQuality = 4;

    public const int ConfirmMaxLetters = 32;

    public static bool RequiresTypedConfirmation(uint quality) =>
        quality >= TypedConfirmMinimumQuality;

    public static bool ConfirmSatisfied(string? typed) =>
        string.Equals(typed?.Trim(), ConfirmWord, StringComparison.OrdinalIgnoreCase);

    public static string ConfirmText(string itemName) =>
        $"Do you want to destroy {itemName}?\nType {ConfirmWord} to confirm.";

    public static bool IsDeletePopupType(string? type) =>
        string.Equals(type, PopupType, StringComparison.Ordinal) ||
        string.Equals(type, ConfirmPopupType, StringComparison.Ordinal);
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
    public static float Height(float textHeight, bool hasEditBox = false) =>
        StaticPopupCoordinatorLaw.Height(textHeight, ButtonHeight,
            StaticPopupCoordinatorLaw.NarrowEditBoxHeight, hasEditBox);
    public static float ButtonTop(float textHeight) => TextTop + textHeight + 8f;
    public static float ButtonOneX(float width) => width * .5f - 134f;
    public static float ButtonTwoX(float width) => ButtonOneX(width) + ButtonWidth + ButtonGap;

    public static PopupLayout Layout(float textHeight, bool hasEditBox = false)
    {
        float safeTextHeight = Math.Max(0, textHeight);
        float height = Height(safeTextHeight, hasEditBox);

        // Same BOTTOM +45 field anchor the narrow StaticPopup edit box uses, so the guarded
        // popup and the guild/social ones sit their field at identical height off the frame.
        var editBox = hasEditBox
            ? new StaticPopupCoordinatorLaw.Rect(
                (Width - StaticPopupCoordinatorLaw.NarrowEditBoxWidth) * .5f,
                height - StaticPopupCoordinatorLaw.NarrowEditBoxBottomOffset -
                    StaticPopupCoordinatorLaw.NarrowEditBoxHeight,
                StaticPopupCoordinatorLaw.NarrowEditBoxWidth,
                StaticPopupCoordinatorLaw.NarrowEditBoxHeight)
            : default;

        float buttonTop = hasEditBox ? editBox.Bottom + 8f : ButtonTop(safeTextHeight);
        return new(Width, height,
            new((Width - TextWidth) * .5f, TextTop, TextWidth, safeTextHeight),
            new(AlertLeft, (height - AlertSize) * .5f, AlertSize, AlertSize),
            new(ButtonOneX(Width), buttonTop, ButtonWidth, ButtonHeight),
            new(ButtonTwoX(Width), buttonTop, ButtonWidth, ButtonHeight),
            editBox);
    }

    public static System.Numerics.Vector2 TextLineCenter(
        PopupLayout layout, float linePitch, int lineIndex) =>
        new(layout.Width * .5f,
            layout.Text.Y + (Math.Max(0, lineIndex) + .5f) * linePitch);

    public static readonly StaticPopupCoordinatorLaw.Definition ConfirmDefinition = new(
        ConfirmPopupType,
        WhileDead: true,
        HideOnEscape: true,
        HasAccept: true,
        HasCancel: true,
        HasOnUpdate: true,
        ShowAlert: true,
        HasEditBox: true,
        MaxLetters: ConfirmMaxLetters);

    /// <summary>Either destroy popup - the plain one or the typed-confirmation one.</summary>
    public static (int Slot, StaticPopupCoordinatorLaw.Instance Instance)? Visible(
        StaticPopupCoordinatorLaw.Slots slots)
    {
        if (slots.First is { } first && IsDeletePopupType(first.Definition.Type))
            return (1, first);
        if (slots.Second is { } second && IsDeletePopupType(second.Definition.Type))
            return (2, second);
        return null;
    }
}

namespace MSUIClient.Engine.UI;

/// <summary>Build-5875 pet UnitPopup predicates, verbs, and StaticPopup definitions.</summary>
public static class PetMenuUiLaw
{
    public const uint RenameFlag = 0x0000_0010u;
    public const uint AbandonFlag = 0x0000_0020u;
    public const uint DismissWord = 0x0700_0003u;
    public const int RenameMaxLetters = 12;

    public const string AbandonPopupType = "ABANDON_PET";
    public const string RenamePopupType = "RENAME_PET";
    public const string RenameConfirmPopupType = "PETRENAMECONFIRM";
    public const string RenameLabel = "Enter desired name of pet:";
    public const string AbandonText =
        "Are you sure you want to permanently abandon your pet?";
    public const string AcceptText = "Accept";
    public const string CancelText = "Cancel";
    public const string OkayText = "Okay";
    public const string YesText = "Yes";
    public const string NoText = "No";

    // PetFrame is an intentionally preserved MSUI surface. Its right-click affordance still
    // belongs to a law: ImGui hosts this exact authored rectangle and does not size it.
    public const float FrameWidth = 128f;
    public const float FrameHeight = 42f;

    public readonly record struct PlainPopupLayout(
        float Width, float Height,
        StaticPopupCoordinatorLaw.Rect Text,
        StaticPopupCoordinatorLaw.Rect Button1,
        StaticPopupCoordinatorLaw.Rect Button2)
    {
        public System.Numerics.Vector2 Size => new(Width, Height);
    }

    public static readonly StaticPopupCoordinatorLaw.Definition AbandonDefinition = new(
        AbandonPopupType, HideOnEscape: true, HasAccept: true, HasCancel: true);
    public static readonly StaticPopupCoordinatorLaw.Definition RenameDefinition = new(
        RenamePopupType, HideOnEscape: true, HasAccept: true, HasCancel: true,
        HasOnShow: true, HasEditBox: true, MaxLetters: RenameMaxLetters,
        HasEditBoxEnter: true);
    public static readonly StaticPopupCoordinatorLaw.Definition RenameConfirmDefinition = new(
        RenameConfirmPopupType, HideOnEscape: true, HasAccept: true, HasCancel: true);

    public static (bool CanAbandon, bool CanRename) Predicates(
        ulong? summonedBy, ulong playerGuid, uint unitFlags) =>
        summonedBy != playerGuid
            ? (false, false)
            : ((unitFlags & AbandonFlag) != 0, (unitFlags & RenameFlag) != 0);

    public static string RenameConfirmation(string name) => $"Name your pet '{name}'?";

    public static PlainPopupLayout PlainLayout(float textHeight)
    {
        float safeTextHeight = Math.Max(0, textHeight);
        float height = StaticPopupCoordinatorLaw.Height(safeTextHeight,
            StaticPopupCoordinatorLaw.ButtonHeight);
        var text = new StaticPopupCoordinatorLaw.Rect(
            (StaticPopupCoordinatorLaw.BaseWidth - StaticPopupCoordinatorLaw.TextWidth) * .5f,
            StaticPopupCoordinatorLaw.TextTop,
            StaticPopupCoordinatorLaw.TextWidth, safeTextHeight);
        float buttonTop = text.Bottom + 8f;
        float firstRight = StaticPopupCoordinatorLaw.BaseWidth * .5f - 6f;
        var button1 = new StaticPopupCoordinatorLaw.Rect(
            firstRight - StaticPopupCoordinatorLaw.ButtonWidth, buttonTop,
            StaticPopupCoordinatorLaw.ButtonWidth, StaticPopupCoordinatorLaw.ButtonHeight);
        var button2 = new StaticPopupCoordinatorLaw.Rect(
            firstRight + 13f, buttonTop,
            StaticPopupCoordinatorLaw.ButtonWidth, StaticPopupCoordinatorLaw.ButtonHeight);
        return new(StaticPopupCoordinatorLaw.BaseWidth, height, text, button1, button2);
    }

    public static System.Numerics.Vector2 TextLineCenter(
        PlainPopupLayout layout, float linePitch, int lineIndex) =>
        new(layout.Width * .5f,
            layout.Text.Y + (Math.Max(0, lineIndex) + .5f) * linePitch);

    public static (int Slot, StaticPopupCoordinatorLaw.Instance Instance)? Visible(
        StaticPopupCoordinatorLaw.Slots slots, string type)
    {
        if (slots.First is { } first && first.Definition.Type == type) return (1, first);
        if (slots.Second is { } second && second.Definition.Type == type) return (2, second);
        return null;
    }

    public static bool IsPetPopup(string type) =>
        type is AbandonPopupType or RenamePopupType or RenameConfirmPopupType;
}

using System.Numerics;

namespace MSUIClient.Engine.UI;

/// <summary>Current FriendsFrame seat, shared StaticPopup name-entry policy, and contact gates.</summary>
public static class FriendsFrameUiLaw
{
    public readonly record struct LogicalRect(float X, float Y, float Width, float Height)
    {
        public Vector2 Min => new(X, Y);
        public Vector2 Size => new(Width, Height);
    }

    public const float Width = 384;
    public const float Height = 512;
    public const float Top = 104;
    public const string AddFriendPopupType = "ADD_FRIEND";
    public const string AddIgnorePopupType = "ADD_IGNORE";
    public const string AddFriendPopupText = "Enter name of friend to add:";
    public const string AddIgnorePopupText = "Enter name of player to ignore:";
    public const int NameMaxLetters = 12;
    public static readonly StaticPopupCoordinatorLaw.Definition AddFriendPopupDefinition =
        new(AddFriendPopupType, HideOnEscape: true, HasAccept: true,
            HasOnShow: true, HasEditBox: true, MaxLetters: NameMaxLetters,
            HasEditBoxEnter: true);
    public static readonly StaticPopupCoordinatorLaw.Definition AddIgnorePopupDefinition =
        new(AddIgnorePopupType, HideOnEscape: true, HasAccept: true,
            HasOnShow: true, HasEditBox: true, MaxLetters: NameMaxLetters,
            HasEditBoxEnter: true);
    public static readonly LogicalRect AddFriend = new(17, 384, 131, 21);
    public static readonly LogicalRect SendMessage = new(214, 384, 131, 21);
    public static readonly LogicalRect RemoveFriend = new(17, 410, 131, 21);
    public static readonly LogicalRect GroupInvite = new(214, 410, 131, 21);

    public static Vector2 FrameOrigin(float scale) => new(0, Top * scale);
    public static Vector2 FrameSize(float scale) => new(Width * scale, Height * scale);
    public static string PopupText(string type) => type == AddIgnorePopupType
        ? AddIgnorePopupText
        : AddFriendPopupText;

    public static (int Slot, StaticPopupCoordinatorLaw.Instance Instance)? NamePopup(
        StaticPopupCoordinatorLaw.Slots slots)
    {
        if (slots.First is { } first && IsNamePopup(first.Definition.Type)) return (1, first);
        if (slots.Second is { } second && IsNamePopup(second.Definition.Type)) return (2, second);
        return null;
    }

    public static bool IsNamePopup(string type) =>
        type is AddFriendPopupType or AddIgnorePopupType;

    public static bool CanRemove(bool selected) => selected;
    public static bool CanContact(bool selected, bool online, bool nameKnown) =>
        selected && online && nameKnown;
}

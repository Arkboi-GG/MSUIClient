using System.Numerics;

namespace MSUIClient.Engine.UI;

public enum FriendsWhoVariable { Zone, Guild, Race }

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
    public const int FriendsVisibleRows = 10;
    public const int IgnoreVisibleRows = 20;
    public const int WhoVisibleRows = 17;
    public const float FriendRowStep = 34;
    public const float IgnoreRowStep = 16;
    public const float WhoRowStep = 16;
    public const int OuterTabCount = 3;
    public static readonly string[] OuterTabs = ["Friends", "Who", "Guild"];
    public static readonly string[] WhoVariableLabels = ["Zone", "Guild", "Race"];
    public static readonly LogicalRect ScrollIcon = new(7, 6, 60, 60);
    public static readonly LogicalRect FriendsRows = new(23, 76, 298, 304);
    public static readonly LogicalRect FriendsScrollFrame = new(21, 75, 296, 304);
    public static readonly LogicalRect IgnoreRows = new(23, 80, 298, 320);
    public static readonly LogicalRect IgnoreScrollFrame = new(21, 75, 296, 332);
    public static readonly LogicalRect WhoRows = new(15, 95, 298, 272);
    public static readonly LogicalRect WhoScrollFrame = new(21, 96, 296, 287);
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
    public static readonly LogicalRect IgnorePlayer = new(17, 410, 131, 21);
    public static readonly LogicalRect StopIgnore = new(210, 410, 131, 21);
    public static readonly LogicalRect WhoHeaderName = new(20, 70, 83, 24);
    public static readonly LogicalRect WhoHeaderVariable = new(101, 70, 105, 24);
    public static readonly LogicalRect WhoHeaderLevel = new(204, 70, 32, 24);
    public static readonly LogicalRect WhoHeaderClass = new(234, 70, 92, 24);
    public static readonly LogicalRect WhoSearch = new(24, 380, 296, 32);
    public static readonly LogicalRect WhoRefresh = new(19, 408, 85, 22);
    public static readonly LogicalRect WhoAddFriend = new(104, 408, 120, 22);
    public static readonly LogicalRect WhoGroupInvite = new(224, 408, 120, 22);

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

    public static int MaximumOffset(int itemCount, int visibleRows) =>
        Math.Max(0, itemCount - visibleRows);

    public static int ClampOffset(int offset, int itemCount, int visibleRows) =>
        Math.Clamp(offset, 0, MaximumOffset(itemCount, visibleRows));

    public static int WheelOffset(int offset, int itemCount, int visibleRows, float wheel) =>
        ClampOffset(offset - Math.Sign(wheel), itemCount, visibleRows);

    public static LogicalRect Row(LogicalRect rows, float step, int visibleIndex,
        float rowHeight) => new(rows.X, rows.Y + Math.Max(0, visibleIndex) * step,
            rows.Width, rowHeight);

    /// <summary>
    /// FauxScrollFrameTemplate seats its 16px slider six pixels to the right of the frame and
    /// insets the slider track by 16px at both ends. The returned rectangle includes both arrow
    /// buttons; the slider itself is the middle Height-32 pixels.
    /// </summary>
    public static LogicalRect ScrollFurniture(LogicalRect scrollFrame) =>
        new(scrollFrame.X + scrollFrame.Width + 6, scrollFrame.Y, 16, scrollFrame.Height);

    public static bool WhoIsCrowded(int itemCount) => itemCount > WhoVisibleRows;
    public static float WhoVariableWidth(int itemCount) => WhoIsCrowded(itemCount) ? 105 : 120;
    public static float WhoVariableTextWidth(int itemCount) => WhoIsCrowded(itemCount) ? 95 : 110;
    public static float WhoDropdownWidth(int itemCount) => WhoIsCrowded(itemCount) ? 80 : 95;
    public static LogicalRect WhoVariableHeader(int itemCount) =>
        new(101, 70, WhoVariableWidth(itemCount), 24);
    public static LogicalRect WhoLevelHeader(int itemCount)
    {
        LogicalRect variable = WhoVariableHeader(itemCount);
        return new(variable.X + variable.Width - 2, 70, 32, 24);
    }
    public static LogicalRect WhoClassHeader(int itemCount)
    {
        LogicalRect level = WhoLevelHeader(itemCount);
        return new(level.X + level.Width - 2, 70, 92, 24);
    }
    public static LogicalRect WhoDropdownFrame(int itemCount)
    {
        float width = WhoDropdownWidth(itemCount);
        return new(WhoVariableHeader(itemCount).X - 15, 70, width + 50, 32);
    }
    public static LogicalRect WhoDropdownList(int itemCount)
    {
        LogicalRect frame = WhoDropdownFrame(itemCount);
        return new(frame.X + 8, frame.Y + frame.Height - 7,
            WhoDropdownWidth(itemCount) + 32, WhoVariableLabels.Length * 16 + 30);
    }
    public static LogicalRect WhoDropdownRow(int itemCount, int index)
    {
        LogicalRect list = WhoDropdownList(itemCount);
        return new(list.X + 17, list.Y + 15 + Math.Clamp(index, 0,
            WhoVariableLabels.Length - 1) * 16, list.Width - 34, 16);
    }
}

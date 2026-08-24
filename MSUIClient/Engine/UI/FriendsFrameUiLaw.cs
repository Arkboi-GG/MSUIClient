using System.Numerics;

namespace MSUIClient.Engine.UI;

public enum FriendsWhoVariable { Zone, Guild, Race }
public enum FriendsWhoSort { Name, Level, Class, Zone, Guild, Race }

/// <summary>Current FriendsFrame seat, shared StaticPopup name-entry policy, and contact gates.</summary>
public static class FriendsFrameUiLaw
{
    public const string OpenSound = "igMainMenuOpen";
    public const string CloseSound = "igMainMenuClose";
    public const string TabSound = "igCharacterInfoTab";
    public const string RowSound = "igMainMenuOptionCheckBoxOn";
    public const string SoundCategory = "ui.social";
    public const string AddFriendTooltip =
        "Adds a player to your friends list. You will be notified whenever a friend logs on or off. Other players do not know whether they are on your friends list.";
    public const string RemoveFriendTooltip =
        "Removes the selected player from your friends list.";
    public const string SendMessageTooltip =
        "Sends a private message to the selected player.";
    public const string GroupInviteTooltip =
        "Invites the selected player to join a group.";

    public readonly record struct WhoEntry(
        string Name, string Guild, uint Level, uint Class, uint Race, uint Area);

    public readonly record struct LogicalRect(float X, float Y, float Width, float Height)
    {
        public float Right => X + Width;
        public float Bottom => Y + Height;
        public Vector2 Min => new(X, Y);
        public Vector2 Size => new(Width, Height);
        public Vector2 Center => new(X + Width * .5f, Y + Height * .5f);
    }

    public readonly record struct TextureSlice(
        LogicalRect Rect, Vector2 UvMin, Vector2 UvMax);

    public readonly record struct ScrollBarLayout(
        LogicalRect UpButton, LogicalRect DownButton, LogicalRect Track,
        LogicalRect Knob);

    public readonly record struct ShellArt(
        string TopLeft, string TopRight, string BottomLeft, string BottomRight);

    public const string GeneralTopLeft =
        @"Interface\PaperDollInfoFrame\UI-Character-General-TopLeft";
    public const string GeneralTopRight =
        @"Interface\PaperDollInfoFrame\UI-Character-General-TopRight";
    public const string TrainerTopLeft =
        @"Interface\ClassTrainerFrame\UI-ClassTrainer-TopLeft";
    public const string TrainerTopRight =
        @"Interface\ClassTrainerFrame\UI-ClassTrainer-TopRight";
    public const string FriendsBottomLeft =
        @"Interface\FriendsFrame\UI-FriendsFrame-BotLeft";
    public const string FriendsBottomRight =
        @"Interface\FriendsFrame\UI-FriendsFrame-BotRight";
    public const string IgnoreBottomLeft =
        @"Interface\FriendsFrame\UI-IgnoreFrame-BotLeft";
    public const string IgnoreBottomRight =
        @"Interface\FriendsFrame\UI-IgnoreFrame-BotRight";
    public const string WhoBottomLeft =
        @"Interface\FriendsFrame\WhoFrame-BotLeft";
    public const string WhoBottomRight =
        @"Interface\FriendsFrame\WhoFrame-BotRight";
    public const string GuildBottomLeft =
        @"Interface\FriendsFrame\GuildFrame-BotLeft";
    public const string GuildBottomRight =
        @"Interface\FriendsFrame\GuildFrame-BotRight";

    /// <summary>
    /// FriendsFrame_Update's page-dependent four-quadrant art matrix. Page 0
    /// owns the Friends/Ignore sub-toggle, page 1 is Who, and page 2 is Guild.
    /// Keeping this in the rule prevents a functioning subframe from silently
    /// retaining the wrong list recesses and header treatment.
    /// </summary>
    public static ShellArt ShellFor(int page, bool ignore)
    {
        if (page == 1)
            return new(TrainerTopLeft, TrainerTopRight, WhoBottomLeft, WhoBottomRight);
        if (page == 2)
            return new(TrainerTopLeft, TrainerTopRight, GuildBottomLeft, GuildBottomRight);
        return ignore
            ? new(GeneralTopLeft, GeneralTopRight, IgnoreBottomLeft, IgnoreBottomRight)
            : new(GeneralTopLeft, GeneralTopRight, FriendsBottomLeft, FriendsBottomRight);
    }

    public const float Width = 384;
    public const float Height = 512;
    public const float Top = 104;
    public const int FriendsVisibleRows = 10;
    public const int IgnoreVisibleRows = 20;
    public const int WhoVisibleRows = 17;
    public const uint MaximumWhosFromServer = 50;
    public const float FriendRowStep = 34;
    public const float IgnoreRowStep = 16;
    public const float WhoRowStep = 16;
    public const int OuterTabCount = 3;
    public static readonly string[] OuterTabs = ["Friends", "Who", "Guild"];
    public static readonly string[] WhoVariableLabels = ["Zone", "Guild", "Race"];
    public const FriendsWhoVariable DefaultWhoVariable = FriendsWhoVariable.Zone;
    public static readonly Vector2 TitleCenter = new(192, 18);
    public static readonly LogicalRect ScrollIcon = new(7, 6, 60, 60);
    public static readonly Vector2 OuterTabFirst = new(11, 433);
    public const float OuterTabOverlap = 14;
    public static Vector2 OuterTabMinimum(float x) => new(x, OuterTabFirst.Y);
    public static readonly LogicalRect Close = new(322, 8, 32, 32);
    // FriendsListFrame, IgnoreListFrame, WhoFrame and GuildFrame all setAllPoints on the host;
    // their OnMouseWheel handlers therefore own the complete 384x512 subframe, not only rows.
    public static readonly LogicalRect ListWheelRegion = new(0, 0, Width, Height);
    public static readonly Vector2 InsetTabFirst = new(70, 39);
    public static Vector2 InsetTabMinimum(float x) => new(x, InsetTabFirst.Y);
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
    // WhoFrameEditBox has no Backdrop or Common-Input-Border and retains ctor-zero text insets.
    public static readonly Vector2 WhoSearchTextInset = Vector2.Zero;
    public static readonly LogicalRect WhoRefresh = new(19, 408, 85, 22);
    public static readonly LogicalRect WhoAddFriend = new(104, 408, 120, 22);
    public static readonly LogicalRect WhoGroupInvite = new(224, 408, 120, 22);
    public static readonly Vector2 FriendNameOffset = new(10, 3);
    public static readonly Vector2 FriendInfoOffset = new(10, 17);
    public static Vector2 InlineOffset(float width) => new(width, 0);
    public static readonly Vector2 WhoHeaderTextOffset = new(8, 7);
    public const string WhoHeaderHighlightPath =
        @"Interface\PaperDollInfoFrame\UI-Character-Tab-Highlight";
    public const string RowHighlightPath =
        @"Interface\QuestFrame\UI-QuestTitleHighlight";
    public static readonly Vector2 IgnoreNameOffset = new(10, 3);
    public static readonly Vector2 WhoNameTextOffset = new(10, 3);
    public static readonly Vector2 WhoVariableTextOffset = new(98, 3);
    public static Vector2 WhoLevelCenter(float variableWidth) =>
        new(110 + variableWidth, 8);
    public static Vector2 WhoClassTextOffset(float variableWidth) =>
        new(132 + variableWidth, 3);
    // 298x16 FontString anchored BOTTOM at (-10,127) inside the 384x512 frame.
    public static readonly LogicalRect WhoTotalsBox = new(33, 369, 298, 16);
    public static readonly Vector2 ScrollUvMin = new(.25f);
    public static readonly Vector2 ScrollUvMax = new(.75f);

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
    public static bool WhoSelectionValid(int selected, int resultCount) =>
        selected >= 0 && selected < resultCount;
    public static bool CanOpenFriendMenu(bool online, bool nameKnown) =>
        online && nameKnown;
    public static bool ShouldSubmitWhoFilter(bool inputActive, bool enter, bool keypadEnter) =>
        inputActive && (enter || keypadEnter);

    /// <summary>Benilla's app feed orders contacts by resolved name and leaves ask-once name
    /// queries at the bottom. LINQ ordering is stable, so equally unresolved rows retain wire order.</summary>
    public static IReadOnlyList<ulong> ContactOrder(IEnumerable<ulong> contacts,
        Func<ulong, string?> resolveName) => contacts
        .OrderBy(guid => string.IsNullOrEmpty(resolveName(guid)))
        .ThenBy(guid => resolveName(guid) ?? "", StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public static int SelectionForGuid(ulong selectedGuid,
        IReadOnlyList<ulong> orderedContacts)
    {
        for (int index = 0; index < orderedContacts.Count; index++)
            if (orderedContacts[index] == selectedGuid) return index;
        return 0;
    }

    public const string UnknownName = "Unknown";
    // Benilla resolves ChrClasses/ChrRaces/AreaTable ids before FrameXML sees them.
    // Missing table rows become empty strings, never numeric or '?' developer fallbacks.
    public static string ResolvedDisplayLabel(string? resolved) =>
        string.IsNullOrEmpty(resolved) || resolved == "?" ? "" : resolved;
    // GetIgnoreName(index) or "": unresolved asynchronous names leave the authored row blank.
    public static string IgnoreNameLine(string? resolvedName) => resolvedName ?? "";
    public static string StatusTag(byte status) => status switch
    {
        2 => "<AFK>",
        4 => "<DND>",
        _ => "",
    };
    public static string OfflineNameLine(string name) => $"{name} - Offline";
    public static string FriendInfoLine(bool online, uint level, string className) =>
        online ? $"Level {level} {className}" : UnknownName;

    /// <summary>WhoList_Update's singular/plural total and capped-result suffix.</summary>
    public static string WhoTotals(uint totalCount)
    {
        string total = totalCount == 1
            ? $"{totalCount} Person Found"
            : $"{totalCount} People Found";
        string displayed = totalCount > MaximumWhosFromServer
            ? $"({MaximumWhosFromServer} displayed)" : "";
        return $"{total}  {displayed}";
    }

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
    public static DropdownCapsuleUiLaw.Layout WhoDropdown(int itemCount)
    {
        float width = WhoDropdownWidth(itemCount);
        return DropdownCapsuleUiLaw.At(WhoVariableHeader(itemCount).X - 15, 70,
            width, buttonWidth: 24, leftJustified: true);
    }

    public static IReadOnlyList<TextureSlice> WhoColumnHeaderSlices(float width)
    {
        const float leftWidth = 5;
        const float rightWidth = 4;
        const float height = 24;
        float middleWidth = MathF.Max(0, width - leftWidth - rightWidth);
        return
        [
            new(new(0, 0, leftWidth, height), Vector2.Zero,
                new(.078125f, .75f)),
            new(new(leftWidth, 0, middleWidth, height), new(.078125f, 0),
                new(.90625f, .75f)),
            new(new(leftWidth + middleWidth, 0, rightWidth, height),
                new(.90625f, 0), new(.96875f, .75f)),
        ];
    }

    public static LogicalRect WhoHeaderHit(float width) => new(0, 0, width, 24);
    public static LogicalRect WhoHeaderHighlight(float width) =>
        new(-2, -5, width + 4, 36);

    public static FriendsWhoSort SortForVariable(FriendsWhoVariable variable) => variable switch
    {
        FriendsWhoVariable.Guild => FriendsWhoSort.Guild,
        FriendsWhoVariable.Race => FriendsWhoSort.Race,
        _ => FriendsWhoSort.Zone,
    };

    public static IReadOnlyList<WhoEntry> SortWho(
        IEnumerable<WhoEntry> source, FriendsWhoSort sort)
    {
        IEnumerable<WhoEntry> rows = source;
        return sort switch
        {
            FriendsWhoSort.Level => rows.OrderBy(row => row.Level).ToArray(),
            FriendsWhoSort.Class => rows.OrderBy(row => row.Class).ToArray(),
            FriendsWhoSort.Zone => rows.OrderBy(row => row.Area).ToArray(),
            FriendsWhoSort.Guild => rows.OrderBy(row => row.Guild,
                StringComparer.Ordinal).ToArray(),
            FriendsWhoSort.Race => rows.OrderBy(row => row.Race).ToArray(),
            _ => rows.OrderBy(row => row.Name, StringComparer.Ordinal).ToArray(),
        };
    }

    public static ScrollBarLayout ScrollBar(LogicalRect scrollFrame, int value, int maximum)
    {
        LogicalRect furniture = ScrollFurniture(scrollFrame);
        var up = new LogicalRect(furniture.X, furniture.Y, 16, 16);
        var down = new LogicalRect(furniture.X, furniture.Bottom - 16, 16, 16);
        var track = new LogicalRect(furniture.X, furniture.Y + 16, 16,
            MathF.Max(16, furniture.Height - 32));
        float fraction = maximum <= 0 ? 0 : Math.Clamp((float)value / maximum, 0, 1);
        var knob = new LogicalRect(track.X,
            track.Y + fraction * MathF.Max(0, track.Height - 16), 16, 16);
        return new(up, down, track, knob);
    }
}

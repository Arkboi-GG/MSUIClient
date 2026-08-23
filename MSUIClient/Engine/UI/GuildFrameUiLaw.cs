using System.Numerics;

namespace MSUIClient.Engine.UI;

/// <summary>Current Benilla FriendsFrame.xml GuildFrame child geometry and display rules.</summary>
public static class GuildFrameUiLaw
{
    public const int InfoMaxLetters = 500;
    public const uint ModifyGuildInfoRight = 0x0001_0000;
    public const string InfoPlaceholder = "Click here to set message";
    public const uint InviteRight = 0x0000_0010;
    public const uint RemoveRight = 0x0000_0020;
    public const uint PromoteRight = 0x0000_0080;
    public const uint DemoteRight = 0x0000_0100;
    public const uint EditPublicNoteRight = 0x0000_2000;
    public const uint ViewOfficerNoteRight = 0x0000_4000;
    public const uint EditOfficerNoteRight = 0x0000_8000;
    public const string RemoveMemberPopupType = "REMOVE_GUILDMEMBER";
    public const string ConfirmPromotePopupType = "CONFIRM_GUILD_PROMOTE";
    public const string ConfirmLeavePopupType = "CONFIRM_GUILD_LEAVE";
    public const string SetPublicNotePopupType = "SET_GUILDPLAYERNOTE";
    public const string SetOfficerNotePopupType = "SET_GUILDOFFICERNOTE";
    public const int NoteMaxLetters = 31;
    public const string PublicNotePlaceholder = "Click here to set a Public Note.";
    public const string OfficerNotePlaceholder = "Click here to set an Officer's Note.";
    public const string MemberOptionsTooltip =
        "Right-click a guild member for more options.";
    public const string InformationTooltip =
        "Click to see additional information about your guild. If you are an officer or the guild leader, you can put information here for your guild members to read.";
    public const string AddMemberTooltip = "Adds a new player to the guild.";
    public const string ControlTooltip =
        "Allows you to customize the names of each rank in your guild, along with its privileges.";
    public const string RemoveMemberTooltip =
        "Removes the selected player from the guild.";
    public const string PromoteMemberTooltip =
        "Promotes the selected player one rank higher.";
    public const string DemoteMemberTooltip =
        "Demotes the selected player one rank lower.";
    public const string SmallButtonNormalFont = "GameFontNormalSmall";
    public const string SmallButtonHighlightFont = "GameFontHighlightSmall";
    public const string SmallButtonDisabledFont = "GameFontDisableSmall";
    public const string AddRankPopupType = "ADD_GUILDRANK";
    public const string AddRankLabel = "Add Guild Rank:";
    public const int RankNameMaxLetters = 15;
    public const string InvitePopupType = "GUILD_INVITE";
    public const float InvitePopupWidth = 320f;
    public const float InvitePopupTextWidth = 290f;
    public const float InvitePopupTextTop = 16f;
    public const float InvitePopupButtonWidth = 128f;
    public const float InvitePopupButtonHeight = 20f;
    public const float InvitePopupButtonOneX = 26f;
    public const float InvitePopupButtonTwoX = 167f;
    public static readonly Vector2 NarrowPopupEditTextOffset = new(0, 7);
    public static readonly Vector2 WidePopupEditTextOffset = new(0, 23);
    public static readonly LogicalRect WideEditBorderLeft = new(-10, 16, 256, 32);
    public static readonly LogicalRect WideEditBorderMiddle = new(246, 16, 39, 32);
    public static readonly LogicalRect WideEditBorderRight = new(285, 16, 75, 32);
    public static readonly Vector2 WideEditBorderMiddleUvMin = new(.29296875f, 0);
    public static readonly Vector2 WideEditBorderRightUvMin = new(.70703125f, 0);

    public const string AddMemberPopupType = "ADD_GUILDMEMBER";
    public const string AddMemberLabel = "Add Guild Member:";
    public const int AddMemberMaxLetters = 12;
    public static readonly StaticPopupCoordinatorLaw.Definition AddMemberDefinition = new(
        AddMemberPopupType, WhileDead: true, HideOnEscape: true,
        HasAccept: true, HasCancel: true, HasOnShow: true, HasOnHide: true,
        HasEditBox: true, MaxLetters: AddMemberMaxLetters, HasEditBoxEnter: true);
    public static readonly StaticPopupCoordinatorLaw.Definition RemoveMemberDefinition = new(
        RemoveMemberPopupType, WhileDead: true, HideOnEscape: true,
        HasAccept: true, HasCancel: true, HasOnShow: true);
    public static readonly StaticPopupCoordinatorLaw.Definition ConfirmPromoteDefinition = new(
        ConfirmPromotePopupType, WhileDead: true, HideOnEscape: true,
        HasAccept: true, HasCancel: true, HasOnShow: true);
    public static readonly StaticPopupCoordinatorLaw.Definition ConfirmLeaveDefinition = new(
        ConfirmLeavePopupType, WhileDead: true, HideOnEscape: true,
        HasAccept: true, HasCancel: true, HasOnShow: true);
    public static readonly StaticPopupCoordinatorLaw.Definition SetPublicNoteDefinition = new(
        SetPublicNotePopupType, WhileDead: true, HideOnEscape: true,
        HasAccept: true, HasCancel: true, HasOnShow: true, HasOnHide: true,
        HasEditBox: true, MaxLetters: NoteMaxLetters, HasEditBoxEnter: true);
    public static readonly StaticPopupCoordinatorLaw.Definition SetOfficerNoteDefinition = new(
        SetOfficerNotePopupType, WhileDead: true, HideOnEscape: true,
        HasAccept: true, HasCancel: true, HasOnShow: true, HasOnHide: true,
        HasEditBox: true, MaxLetters: NoteMaxLetters, HasEditBoxEnter: true);
    public static readonly StaticPopupCoordinatorLaw.Definition AddRankDefinition = new(
        AddRankPopupType, WhileDead: true, HideOnEscape: true,
        HasAccept: true, HasCancel: true, HasOnShow: true, HasOnHide: true,
        HasEditBox: true, MaxLetters: RankNameMaxLetters, HasEditBoxEnter: true);
    public static readonly StaticPopupCoordinatorLaw.Definition InviteDefinition = new(
        InvitePopupType, WhileDead: true, HideOnEscape: true,
        HasAccept: true, HasCancel: true, TimeoutSeconds: 60);

    public readonly record struct LogicalRect(float X, float Y, float Width, float Height)
    {
        public Vector2 Min => new(X, Y);
        public Vector2 Size => new(Width, Height);
        public Vector2 Center => new(X + Width * .5f, Y + Height * .5f);
    }

    public readonly record struct TextureSlice(
        LogicalRect Rect, Vector2 UvMin, Vector2 UvMax);
    public readonly record struct TooltipSeat(Vector2 Anchor, Vector2 Pivot);

    public const int VisibleRows = 13;
    public const float RowHeight = 16;
    public const float FauxScrollStep = 14;
    public static readonly LogicalRect OfflineFilter = new(132, 38, 210, 28);
    public static readonly LogicalRect OfflineCheck = new(314, 40, 20, 20);
    public static readonly LogicalRect OfflineHit = new(146, 38, 188, 24);
    public static readonly Vector2 OfflineLabelRight = new(304, 49);
    public static IReadOnlyList<TextureSlice> OfflineFilterSlices =>
    [
        new(new(0, 0, 12, 28), Vector2.Zero, new(.09375f, 1)),
        new(new(12, 0, 186, 28), new(.09375f, 0), new(.90625f, 1)),
        new(new(198, 0, 12, 28), new(.90625f, 0), Vector2.One),
    ];
    public static readonly LogicalRect Rows = new(15, 95, 298, VisibleRows * RowHeight);
    public const string RowHighlightPath =
        @"Interface\FriendsFrame\UI-FriendsFrame-HighlightBar";
    public static readonly LogicalRect RowHighlight = new(5, 2, 298, 16);
    public static readonly LogicalRect ScrollFrame = new(21, 98, 296, 237);
    public static readonly LogicalRect MotdLabel = new(23, 344, 225, 11);
    public static readonly LogicalRect MotdText = new(23, 358, 315, 45);
    public static readonly LogicalRect GuildInformation = new(16, 408, 123, 22);
    public static readonly LogicalRect AddMember = new(141, 408, 98, 22);
    public static readonly LogicalRect GuildControl = new(241, 408, 104, 22);
    // Auto-width 16px FontString anchored BOTTOM at (-122,181).
    public static readonly LogicalRect MemberTotalBox = new(70, 315, 0, 16);
    public const float TotalGap = 3;
    public static LogicalRect OnlineTotalBox(float memberTextWidth) =>
        new(MemberTotalBox.X + Math.Max(0, memberTextWidth) + TotalGap,
            MemberTotalBox.Y, 0, MemberTotalBox.Height);
    public static Vector2 ViewToggleLabelRight(int itemCount)
    {
        LogicalRect toggle = ViewToggle(itemCount);
        return new(toggle.X - 4, toggle.Y + 11);
    }
    public static readonly Vector2 PlayerNameOffset = new(14, 3);
    public static readonly Vector2 PlayerZoneOffset = new(95, 3);
    public static Vector2 PlayerLevelCenter(float zoneWidth) => new(109 + zoneWidth, 8);
    public static Vector2 PlayerClassOffset(float zoneWidth) => new(131 + zoneWidth, 3);
    public static readonly Vector2 StatusNameOffset = new(14, 3);
    public static readonly Vector2 StatusRankOffset = new(94, 3);
    public static readonly Vector2 StatusNoteOffset = new(160, 3);
    public static Vector2 StatusOnlineOffset(float noteWidth) => new(170 + noteWidth, 3);

    public static IReadOnlyList<string> WrapMotd(string? text, float width, float height,
        float linePitch, Func<string, float> measure)
    {
        if (string.IsNullOrWhiteSpace(text) || width <= 0 || height <= 0 || linePitch <= 0)
            return [];
        int maximumLines = Math.Max(0, (int)MathF.Floor(height / linePitch));
        if (maximumLines == 0) return [];
        var lines = new List<string>();
        foreach (string paragraph in text.Replace("\r", "").Split('\n'))
        {
            string current = "";
            foreach (string word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
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
            if (current.Length > 0)
            {
                lines.Add(current);
                if (lines.Count == maximumLines) return lines;
            }
        }
        return lines;
    }

    // GuildInfoFrame.xml: the information board shares GuildControlPopupFrame's
    // TOPLEFT, whose authored anchor is GuildFrame TOPRIGHT -35,-65.
    public static readonly Vector2 InfoFrameOffset = new(349, 65);
    public static readonly LogicalRect InfoFrame = new(0, 0, 297, 298);
    public static readonly LogicalRect InfoCorner = new(259, 7, 32, 32);
    public static readonly LogicalRect InfoTitle = new(17, 17, 240, 16);
    public static readonly LogicalRect InfoBottomPatch = new(12, 263, 375, 32);
    public static readonly LogicalRect InfoTextBackground = new(11, 32, 276, 230);
    public static readonly LogicalRect InfoScrollFrame = new(16, 39, 243, 218);
    public static readonly LogicalRect InfoEditBox = new(16, 39, 240, 218);
    public const string InfoTextFont = "GameFontHighlight";
    public static readonly LogicalRect InfoCloseButton = new(262, 3, 32, 32);
    public static readonly LogicalRect InfoSaveButton = new(10, 264, 139, 22);
    public static readonly LogicalRect InfoCancelButton = new(149, 264, 139, 22);

    public const float MemberDetailWidth = 212;
    public const float MemberDetailNormalHeight = 195;
    public const float MemberDetailOfficerHeight = 255;
    public static readonly Vector2 MemberDetailOffset = new(351, 28);
    public static readonly LogicalRect MemberName = new(17, 18, 160, 12);
    public static readonly LogicalRect MemberLevel = new(17, 32, 180, 12);
    public static readonly LogicalRect MemberZoneLabel = new(17, 53, 36, 12);
    public static readonly LogicalRect MemberZoneText = new(55, 53, 147, 12);
    public static readonly LogicalRect MemberRankLabel = new(17, 73, 36, 12);
    public static readonly LogicalRect MemberRankText = new(55, 73, 147, 12);
    public static readonly LogicalRect MemberOnlineLabel = new(17, 93, 68, 12);
    public static readonly LogicalRect MemberOnlineText = new(87, 93, 110, 12);
    public static readonly LogicalRect MemberNoteLabel = new(17, 113, 50, 12);
    public static readonly LogicalRect MemberNotePane = new(15, 125, 181, 40);
    public static readonly LogicalRect MemberNoteText = new(23, 131, 165, 32);
    public const string MemberNoteFont = "GameFontHighlight";
    public static readonly LogicalRect MemberOfficerLabel = new(17, 168, 100, 12);
    public static readonly LogicalRect MemberOfficerPane = new(15, 180, 181, 40);
    public static readonly LogicalRect MemberOfficerText = new(23, 186, 165, 32);

    public static Vector2 FixedTextLineMin(Vector2 origin, LogicalRect box,
        float scale, int line, float pitch) =>
        origin + box.Min * scale + new Vector2(0, Math.Max(0, line) * pitch);

    public static readonly Vector2 ControlFrameOffset = new(349, 65);
    public static readonly LogicalRect ControlFrame = new(0, 0, 297, 298);
    public static readonly LogicalRect ControlShellTopLeft = new(0, 0, 256, 256);
    public static readonly LogicalRect ControlShellTopRight = new(256, 0, 64, 256);
    public static readonly LogicalRect ControlShellBottomLeft = new(0, 256, 256, 64);
    public static readonly LogicalRect ControlShellBottomRight = new(256, 256, 64, 64);
    public static readonly LogicalRect ControlSelectLabel = new(20, 15, 257, 16);
    public static readonly LogicalRect ControlDropDown = new(84, 30, 128, 32);
    public static readonly Vector2 ControlDropDownTextOffset = new(9, 10);
    public static readonly LogicalRect ControlDropDownArrow = new(185, 34, 24, 24);
    public static readonly LogicalRect ControlAddRank = new(197, 39, 16, 16);
    public static readonly LogicalRect ControlRemoveRank = new(211, 39, 16, 16);
    public const string ControlAddRankTooltip = "Click to add rank";
    public const string ControlRemoveRankTooltip = "Click to remove this rank";
    public static readonly LogicalRect ControlRankName = new(118, 68, 130, 32);
    public static readonly LogicalRect ControlRankNameLabel = new(28, 77, 78, 16);
    public static readonly Vector2 ControlRankNameLabelRight = new(106, 84);
    public static readonly Vector2 ControlRankNameTextOffset = new(0, 7);
    public static readonly LogicalRect ControlAllowLabel = new(20, 106, 257, 16);
    public static readonly LogicalRect ControlAccept = new(128, 263, 78, 22);
    public static readonly LogicalRect ControlCancel = new(208, 263, 78, 22);
    public static readonly Vector2 ControlDropDownRowTextOffset = new(5, 3);
    public static readonly Vector2 ControlCheckboxLabelOffset = new(21, 4);
    public static readonly string[] RankRightLabels =
    [
        "Guildchat Listen", "Guildchat Speak", "Officerchat Listen",
        "Officerchat Speak", "Promote", "Demote", "Invite Member",
        "Remove Member", "Set MOTD", "Edit Public Note", "View Officer Note",
        "Edit Officer Note", "Modify Guild Info",
    ];
    public static readonly uint[] RankRightOrder =
    [
        0x0000_0001, 0x0000_0002, 0x0000_0004, 0x0000_0008,
        PromoteRight, DemoteRight, InviteRight, RemoveRight, 0x0000_1000,
        EditPublicNoteRight, ViewOfficerNoteRight, EditOfficerNoteRight,
        ModifyGuildInfoRight,
    ];

    public static Vector2 ControlFrameOrigin(Vector2 guildFrameOrigin, float scale) =>
        guildFrameOrigin + ControlFrameOffset * scale;

    public static TooltipSeat RightTooltipSeat(Vector2 ownerMin, Vector2 ownerSize) =>
        new(ownerMin + Vector2.UnitX * ownerSize.X, Vector2.UnitY);

    public static LogicalRect ControlCheckbox(int oneBasedIndex)
    {
        int index = Math.Clamp(oneBasedIndex, 1, 13) - 1;
        int column = index & 1;
        int row = index / 2;
        return new(25 + column * 135, 123 + row * 20, 20, 20);
    }

    public static bool CanRemoveRank(int selectedRank, int rankCount, int bottomRankMembers) =>
        rankCount > 5 && selectedRank == rankCount - 1 && bottomRankMembers == 0;

    public static bool ShowRemoveRank(int selectedRank, int rankCount) =>
        rankCount > 5 && selectedRank == rankCount - 1;

    public static LogicalRect ControlDropDownList(int rankCount) =>
        new(84, 62, 128, 8 + Math.Clamp(rankCount, 0, 10) * 18);

    public static LogicalRect ControlDropDownRow(int index) =>
        new(88, 66 + Math.Max(0, index) * 18, 120, 18);

    public static Vector2 MemberDetailOrigin(Vector2 guildFrameOrigin, float scale) =>
        guildFrameOrigin + MemberDetailOffset * scale;
    public static float MemberDetailHeight(bool canViewOfficerNote) =>
        canViewOfficerNote ? MemberDetailOfficerHeight : MemberDetailNormalHeight;
    public static Vector2 MemberDetailSize(float height) => new(MemberDetailWidth, height);
    public static LogicalRect MemberCorner(float height) => new(174, 7, 32, 32);
    public static LogicalRect MemberCloseButton(float height) => new(177, 3, 32, 32);
    public static LogicalRect MemberBottomPatch(float height) => new(12, height - 35, 256, 32);
    public static LogicalRect MemberRemoveButton(float height) => new(10, height - 34, 96, 22);
    public static LogicalRect MemberInviteButton(float height) => new(107, height - 34, 96, 22);
    public static LogicalRect MemberPromoteButton(float height) =>
        new(152, height * .5f - 64, 28, 28);
    public static LogicalRect MemberDemoteButton(float height) =>
        new(172, height * .5f - 64, 28, 28);
    public static LogicalRect MemberRankArrowHit(LogicalRect art) =>
        new(art.X + 6, art.Y + 7, 16, 14);

    public static bool HasRight(uint playerRank, IReadOnlyList<uint> rankRights, uint right) =>
        playerRank < rankRights.Count && (rankRights[(int)playerRank] & right) != 0;

    public static bool CanPromote(uint myRank, uint targetRank, IReadOnlyList<uint> rights) =>
        HasRight(myRank, rights, PromoteRight) && targetRank > 1 && targetRank > myRank + 1;
    public static bool CanDemote(uint myRank, uint targetRank, uint bottomRank,
        IReadOnlyList<uint> rights) =>
        HasRight(myRank, rights, DemoteRight) && targetRank >= 1 && targetRank > myRank &&
        targetRank != bottomRank;
    public static bool CanRemove(uint myRank, uint targetRank, IReadOnlyList<uint> rights) =>
        HasRight(myRank, rights, RemoveRight) && targetRank >= 1 && targetRank > myRank;

    public static string DisplayNote(string? note, bool mayEdit, string placeholder) =>
        mayEdit && string.IsNullOrEmpty(note) ? placeholder : note ?? "";

    // GetGuildRosterInfo receives display-resolved ChrClasses/AreaTable strings. Cache misses
    // are empty in Benilla and must not surface MSUI's generic "?" or "Area <id>" fallbacks.
    public static string ResolvedRosterLabel(string? resolved) =>
        string.IsNullOrEmpty(resolved) || resolved == "?" ? "" : resolved;

    public static string TruncateNote(string value) =>
        value.Length <= NoteMaxLetters ? value : value[..NoteMaxLetters];

    public static string RemoveMemberText(string name) =>
        $"Are you sure you want to remove {name} from the guild?";

    public static string GuildActionText(string type, string value) => type switch
    {
        ConfirmPromotePopupType => $"Really promote {value} to Guildmaster?",
        ConfirmLeavePopupType => $"Really leave {value}?",
        _ => RemoveMemberText(value),
    };

    public static (string Accept, string Cancel) GuildActionButtons(string type) =>
        type == RemoveMemberPopupType ? ("Yes", "No") : ("Accept", "Cancel");

    public static string InvitePopupToken(string inviter, string guild) =>
        $"{inviter}\u001f{guild}";

    public static (string Inviter, string Guild) InvitePopupData(string? token)
    {
        string value = token ?? "";
        int separator = value.IndexOf('\u001f');
        return separator < 0 ? (value, "") :
            (value[..separator], value[(separator + 1)..]);
    }

    public static string InvitePopupText(string inviter, string guild) =>
        $"{inviter} invites you to join {guild}.";

    public static float InvitePopupHeight(float measuredTextHeight) =>
        InvitePopupTextTop + MathF.Max(0, measuredTextHeight) + 8f +
        InvitePopupButtonHeight + 16f;

    public static float InvitePopupButtonTop(float measuredTextHeight) =>
        InvitePopupTextTop + MathF.Max(0, measuredTextHeight) + 8f;

    public static Vector2 InvitePopupSize(float measuredTextHeight) =>
        new(InvitePopupWidth, InvitePopupHeight(measuredTextHeight));

    public static Vector2 InvitePopupLineCenter(int index, float linePitch) =>
        new(InvitePopupWidth * .5f,
            InvitePopupTextTop + (Math.Max(0, index) + .5f) * Math.Max(0, linePitch));

    public static LogicalRect InvitePopupButton(int button, float measuredTextHeight) =>
        new(button == 2 ? InvitePopupButtonTwoX : InvitePopupButtonOneX,
            InvitePopupButtonTop(measuredTextHeight), InvitePopupButtonWidth,
            InvitePopupButtonHeight);

    public static Vector2 RemoveMemberPopupSize(float measuredTextHeight) =>
        new(StaticPopupCoordinatorLaw.BaseWidth,
            StaticPopupCoordinatorLaw.Height(measuredTextHeight,
                StaticPopupCoordinatorLaw.ButtonHeight));

    public static Vector2 RemoveMemberPopupLineCenter(int index, float linePitch) =>
        new(StaticPopupCoordinatorLaw.BaseWidth * .5f,
            StaticPopupCoordinatorLaw.TextTop +
            (Math.Max(0, index) + .5f) * Math.Max(0, linePitch));

    public static LogicalRect RemoveMemberPopupButton(int button, float measuredTextHeight) =>
        new(button == 2 ? InvitePopupButtonTwoX : InvitePopupButtonOneX,
            StaticPopupCoordinatorLaw.TextTop + MathF.Max(0, measuredTextHeight) + 8f,
            StaticPopupCoordinatorLaw.ButtonWidth, StaticPopupCoordinatorLaw.ButtonHeight);

    public static Vector2 InfoFrameOrigin(Vector2 guildFrameOrigin, float scale) =>
        guildFrameOrigin + InfoFrameOffset * scale;

    public static bool CanEditInfo(uint playerRank, IReadOnlyList<uint> rankRights) =>
        HasRight(playerRank, rankRights, ModifyGuildInfoRight);

    public static string InitialInfoText(string? guildInfo, bool canEdit) =>
        canEdit && string.IsNullOrEmpty(guildInfo) ? InfoPlaceholder : guildInfo ?? "";

    public static string TruncateInfo(string value)
    {
        if (value.Length <= InfoMaxLetters) return value;
        return value[..InfoMaxLetters];
    }

    public static int MaximumOffset(int itemCount) => Math.Max(0, itemCount - VisibleRows);
    public static int ClampOffset(int offset, int itemCount) =>
        Math.Clamp(offset, 0, MaximumOffset(itemCount));
    public static int WheelOffset(int offset, int itemCount, float wheel) =>
        ClampOffset(offset - Math.Sign(wheel), itemCount);
    public static bool IsCrowded(int itemCount) => itemCount > VisibleRows;

    public static LogicalRect Row(int visibleIndex) =>
        new(Rows.X, Rows.Y + Math.Max(0, visibleIndex) * RowHeight,
            Rows.Width, RowHeight);

    public static LogicalRect ScrollFurniture() =>
        new(ScrollFrame.X + ScrollFrame.Width + 6, ScrollFrame.Y, 16, ScrollFrame.Height);

    public static LogicalRect[] PlayerHeaders(int itemCount)
    {
        float zoneWidth = IsCrowded(itemCount) ? 105 : 120;
        float levelX = 101 + zoneWidth - 2;
        return
        [
            new(20, 70, 83, 24),
            new(101, 70, zoneWidth, 24),
            new(levelX, 70, 32, 24),
            new(levelX + 30, 70, 92, 24),
        ];
    }

    public static LogicalRect[] StatusHeaders(int itemCount)
    {
        float noteWidth = IsCrowded(itemCount) ? 75 : 90;
        return
        [
            new(20, 70, 83, 24),
            new(101, 70, 75, 24),
            new(174, 70, noteWidth, 24),
            new(172 + noteWidth, 70, 75, 24),
        ];
    }

    public static LogicalRect ViewToggle(int itemCount) =>
        new(IsCrowded(itemCount) ? 284 : 307, 307, 32, 32);

    public static string LastOnline(float offlineDays)
    {
        float days = MathF.Max(0, offlineDays);
        int years = (int)MathF.Floor(days / 365f);
        if (years > 0) return years == 1 ? "1 year" : $"{years} years";
        int months = (int)MathF.Floor(days / 30f);
        if (months > 0) return months == 1 ? "1 month" : $"{months} months";
        int wholeDays = (int)MathF.Floor(days);
        if (wholeDays > 0) return wholeDays == 1 ? "1 day" : $"{wholeDays} days";
        int hours = (int)MathF.Floor(days * 24f);
        if (hours > 0) return hours == 1 ? "1 hour" : $"{hours} hours";
        return "< an hour";
    }

    public static string PresenceTag(byte presence) =>
        (presence & 0x04) != 0 ? "<DND>" :
        (presence & 0x02) != 0 ? "<AFK>" : "";

    public static (int Slot, StaticPopupCoordinatorLaw.Instance Instance)? AddMemberPopup(
        StaticPopupCoordinatorLaw.Slots slots)
    {
        if (slots.First is { } first && first.Definition.Type == AddMemberPopupType)
            return (1, first);
        if (slots.Second is { } second && second.Definition.Type == AddMemberPopupType)
            return (2, second);
        return null;
    }

    public static bool IsMemberPopup(string type) =>
        type is RemoveMemberPopupType or ConfirmPromotePopupType or ConfirmLeavePopupType or
            SetPublicNotePopupType or SetOfficerNotePopupType;

    public static bool IsGuildActionPopup(string type) =>
        type is RemoveMemberPopupType or ConfirmPromotePopupType or ConfirmLeavePopupType;

    public static (int Slot, StaticPopupCoordinatorLaw.Instance Instance)? GuildActionPopup(
        StaticPopupCoordinatorLaw.Slots slots)
    {
        if (slots.First is { } first && IsGuildActionPopup(first.Definition.Type))
            return (1, first);
        if (slots.Second is { } second && IsGuildActionPopup(second.Definition.Type))
            return (2, second);
        return null;
    }

    public static (int Slot, StaticPopupCoordinatorLaw.Instance Instance)? MemberPopup(
        StaticPopupCoordinatorLaw.Slots slots)
    {
        if (slots.First is { } first && IsMemberPopup(first.Definition.Type)) return (1, first);
        if (slots.Second is { } second && IsMemberPopup(second.Definition.Type)) return (2, second);
        return null;
    }

    public static (int Slot, StaticPopupCoordinatorLaw.Instance Instance)? Popup(
        StaticPopupCoordinatorLaw.Slots slots, string type)
    {
        if (slots.First is { } first && first.Definition.Type == type) return (1, first);
        if (slots.Second is { } second && second.Definition.Type == type) return (2, second);
        return null;
    }
}

public readonly record struct GuildEventWire(
    byte Event, IReadOnlyList<string> Parameters, ulong? AffectedGuid);

public readonly record struct GuildCommandResultWire(uint Command, string Name, uint Result);

public readonly record struct GuildInviteWire(string Inviter, string Guild);

public readonly record struct GuildInfoWire(
    string Name, uint CreatedDay, uint CreatedMonth, uint CreatedYear,
    uint MemberCount, uint AccountCount);

/// <summary>Build-5875 guild notification decoders and engine-composed system-line laws.</summary>
public static class GuildFramePacketLaw
{
    public const byte Promotion = 0x00;
    public const byte Demotion = 0x01;
    public const byte Motd = 0x02;
    public const byte Joined = 0x03;
    public const byte Left = 0x04;
    public const byte Removed = 0x05;
    public const byte LeaderIs = 0x06;
    public const byte LeaderChanged = 0x07;
    public const byte Disbanded = 0x08;
    public const byte UpdateRankName = 0x0A;
    public const byte UpdateRoster = 0x0B;
    public const byte SignedOn = 0x0C;
    public const byte SignedOff = 0x0D;

    public static GuildEventWire ParseEvent(byte[] body)
    {
        var reader = new Net.PacketReader(body);
        byte eventId = reader.ReadU8();
        byte count = reader.ReadU8();
        if (count > 3)
            throw new InvalidDataException($"SMSG_GUILD_EVENT params={count}");
        var parameters = new string[count];
        for (int i = 0; i < parameters.Length; i++) parameters[i] = reader.ReadCString();
        ulong? guid = eventId is SignedOn or SignedOff ? reader.ReadU64() : null;
        // vmangos also appends a guid to JOINED/LEFT. The reference does not consume it.
        if (guid is not null && reader.Remaining != 0)
            throw new InvalidDataException($"SMSG_GUILD_EVENT trailing={reader.Remaining}");
        return new(eventId, parameters, guid);
    }

    public static GuildCommandResultWire ParseCommandResult(byte[] body)
    {
        var reader = new Net.PacketReader(body);
        var result = new GuildCommandResultWire(
            reader.ReadU32(), reader.ReadCString(), reader.ReadU32());
        RequireConsumed(reader, "SMSG_GUILD_COMMAND_RESULT");
        return result;
    }

    public static GuildInviteWire ParseInvite(byte[] body)
    {
        var reader = new Net.PacketReader(body);
        var invite = new GuildInviteWire(reader.ReadCString(), reader.ReadCString());
        RequireConsumed(reader, "SMSG_GUILD_INVITE");
        return invite;
    }

    public static string ParseDecline(byte[] body)
    {
        var reader = new Net.PacketReader(body);
        string name = reader.ReadCString();
        RequireConsumed(reader, "SMSG_GUILD_DECLINE");
        return name;
    }

    public static GuildInfoWire ParseInfo(byte[] body)
    {
        var reader = new Net.PacketReader(body);
        var info = new GuildInfoWire(reader.ReadCString(), reader.ReadU32(), reader.ReadU32(),
            reader.ReadU32(), reader.ReadU32(), reader.ReadU32());
        RequireConsumed(reader, "SMSG_GUILD_INFO");
        return info;
    }

    public static string? EventLine(GuildEventWire notice, bool ignored)
    {
        string P(int index) => index < notice.Parameters.Count
            ? notice.Parameters[index] : "";
        return notice.Event switch
        {
            Promotion => $"{P(0)} has promoted {P(1)} to {P(2)}.",
            Demotion => $"{P(0)} has demoted {P(1)} to {P(2)}.",
            Joined => $"{P(0)} has joined the guild.",
            Left => $"{P(0)} has left the guild.",
            Removed => $"{P(0)} has been kicked out of the guild by {P(1)}.",
            LeaderIs => $"{P(0)} is the leader of your guild.",
            LeaderChanged => $"{P(0)} has made {P(1)} the new Guild Master.",
            Disbanded => "Guild has been disbanded.",
            SignedOn when !ignored => $"|Hplayer:{P(0)}|h[{P(0)}]|h has come online.",
            SignedOff when !ignored => $"{P(0)} has gone offline.",
            _ => null,
        };
    }

    public static string? CommandLine(GuildCommandResultWire result)
    {
        string name = result.Name;
        if (result.Result == 0)
        {
            return result.Command switch
            {
                0x00 => $"{name} created.",
                0x01 => $"You have invited {name} to join your guild.",
                0x03 => $"You are no longer a member of {name}.",
                0x0E => $"Congratulations, you are a founding member of {name}!",
                _ => null,
            };
        }
        return result.Result switch
        {
            0x01 => "Internal guild error.",
            0x02 => "You are already in a guild.",
            0x03 => $"{name} is already in a guild.",
            0x04 => "You have already been invited into a guild.",
            0x05 => $"{name} has already been invited to a guild.",
            0x06 => "That name contains invalid characters,  Enter a new name.",
            0x07 => $"There is already a guild named \"{name}\".",
            0x08 when result.Command == 0x03 =>
                "You must promote a new Guild Master using /gleader before leaving the guild.",
            0x08 => "You don't have permission to do that.",
            0x09 => "You are not in a guild.",
            0x0A => $"{name} is not in your guild.",
            0x0B => $"\"{name}\" not found.",
            0x0C => "You cannot invite players from the opposing alliance into your guild",
            0x0D => $"{name}'s rank is too high",
            0x0E => $"{name} is already at the lowest rank",
            0x11 => "Temporary guild error.  Please try again!",
            0x12 => "That guild rank is currently in use.",
            0x13 => $"{name} is ignoring you.",
            _ => null,
        };
    }

    public static string MotdLine(string motd) => $"Guild Message of the Day: {motd}";
    public static string InviteLine(GuildInviteWire invite) =>
        $"{invite.Inviter} invites you join {invite.Guild}.";
    public static string DeclineLine(string name) =>
        $"{name} declines your guild invitation.";
    public static string[] InfoLines(GuildInfoWire info) =>
    [
        $"Guild: {info.Name}",
        $"Guild created {info.CreatedMonth}-{info.CreatedDay}-{info.CreatedYear}, " +
        $"{info.MemberCount} players, {info.AccountCount} accounts",
    ];

    public static bool MakesRosterStale(byte eventId) => eventId <= UpdateRoster;

    public static bool CommandMakesRosterStale(GuildCommandResultWire result) =>
        result.Result == 0 && result.Command is 0x13 or 0x14 ||
        result.Result == 0x14 && result.Command == 0x05;

    private static void RequireConsumed(Net.PacketReader reader, string packet)
    {
        if (reader.Remaining != 0)
            throw new InvalidDataException($"{packet} trailing={reader.Remaining}");
    }
}

public enum GuildRosterSortField { Rank, Level, Name, Zone, Class, Group, Online, Note }

public readonly record struct GuildRosterSortProjection(
    string Name, uint Rank, byte Level, string Zone, string Class,
    bool Online, string Note, float LastOnlineDays);

/// <summary>
/// Build-5875's eight-key guild-roster sort stack. A selected key moves to the front while its
/// remembered direction and every older key remain as tie-breaks. Show Offline changes the
/// online prefix count and sort pre-gate; it never removes rows from the backing roster.
/// </summary>
public sealed class GuildRosterSortLaw
{
    private readonly (GuildRosterSortField Field, bool Descending)[] _stack =
    [
        (GuildRosterSortField.Rank, false),
        (GuildRosterSortField.Level, false),
        (GuildRosterSortField.Name, false),
        (GuildRosterSortField.Zone, false),
        (GuildRosterSortField.Class, false),
        (GuildRosterSortField.Group, false),
        (GuildRosterSortField.Online, false),
        (GuildRosterSortField.Note, false),
    ];

    public (GuildRosterSortField Field, bool Descending) Primary => _stack[0];

    public void Select(GuildRosterSortField field)
    {
        int at = Array.FindIndex(_stack, pair => pair.Field == field);
        if (at < 0) return;
        if (at == 0)
        {
            _stack[0].Descending = !_stack[0].Descending;
            return;
        }
        (GuildRosterSortField Field, bool Descending) moved = _stack[at];
        Array.Copy(_stack, 0, _stack, 1, at);
        _stack[0] = moved;
    }

    public int[] Order(IReadOnlyList<GuildRosterSortProjection> rows, bool showOffline)
    {
        int[] order = Enumerable.Range(0, rows.Count).ToArray();
        Array.Sort(order, (left, right) => Compare(rows[left], rows[right], showOffline));
        return order;
    }

    public static int DisplayedCount(IReadOnlyList<GuildRosterSortProjection> rows,
        bool showOffline) => showOffline ? rows.Count : rows.Count(row => row.Online);

    private int Compare(in GuildRosterSortProjection left,
        in GuildRosterSortProjection right, bool showOffline)
    {
        if (!showOffline && left.Online != right.Online)
            return left.Online ? -1 : 1;
        foreach ((GuildRosterSortField field, bool descending) in _stack)
        {
            int result = CompareField(field, left, right);
            if (result != 0) return descending ? -result : result;
        }
        return 0;
    }

    private static int CompareField(GuildRosterSortField field,
        in GuildRosterSortProjection left, in GuildRosterSortProjection right) => field switch
    {
        // Rank is the reference's one reversed numeric column: larger rank ids first.
        GuildRosterSortField.Rank => right.Rank.CompareTo(left.Rank),
        GuildRosterSortField.Level => left.Level.CompareTo(right.Level),
        GuildRosterSortField.Name => CompareText(left.Name, right.Name),
        GuildRosterSortField.Zone => CompareResolved(left.Zone, right.Zone),
        GuildRosterSortField.Class => CompareResolved(left.Class, right.Class),
        GuildRosterSortField.Group => 0,
        GuildRosterSortField.Online => left.Online != right.Online
            ? left.Online ? -1 : 1
            : left.Online ? 0 : left.LastOnlineDays.CompareTo(right.LastOnlineDays),
        GuildRosterSortField.Note => CompareText(left.Note, right.Note),
        _ => 0,
    };

    private static int CompareText(string left, string right) =>
        StringComparer.OrdinalIgnoreCase.Compare(left, right);

    private static int CompareResolved(string left, string right) =>
        string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right) ? 0 :
            CompareText(left, right);
}

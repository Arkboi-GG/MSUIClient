using System.Numerics;

namespace MSUIClient.Engine.UI;

public enum UnitPopupWhich { Self, Pet, Party, Player, Friend }

public enum UnitPopupSubmenu { None, LootMethod, LootThreshold, RaidTargetIcon }

public enum UnitPopupRow
{
    PetPaperDoll, PetRename, PetAbandon, PetDismiss,
    Whisper, Inspect, Invite, Uninvite, Promote, Leave, Trade, Follow, Duel,
    LootMethod, LootThreshold, LootPromote, RaidTargetIcon,
    FreeForAll, RoundRobin, MasterLooter, GroupLoot, NeedBeforeGreed,
    Quality2, Quality3, Quality4,
    RaidTarget1, RaidTarget2, RaidTarget3, RaidTarget4,
    RaidTarget5, RaidTarget6, RaidTarget7, RaidTarget8, RaidTargetNone,
    Cancel,
}

public readonly record struct UnitPopupWidthMeasure(
    float TextWidth, bool NotCheckable, bool HasArrow, bool HasIcon);

/// <summary>
/// Current Benilla UnitPopup.xml and UIDropDownMenu.xml behavior. This law owns menu tables,
/// hide/grey rules, row decoration, both dropdown levels, MENU geometry and edge flips; the
/// ImGui side only turns its seats and decorations into draw/input calls.
/// </summary>
public static class UnitPopupUiLaw
{
    public const float TradeDistanceSq = 11.1111f * 11.1111f;

    private static readonly UnitPopupRow[] SelfMenu =
        [UnitPopupRow.LootMethod, UnitPopupRow.LootThreshold, UnitPopupRow.LootPromote,
         UnitPopupRow.Leave, UnitPopupRow.RaidTargetIcon, UnitPopupRow.Cancel];
    private static readonly UnitPopupRow[] PetMenu =
        [UnitPopupRow.PetPaperDoll, UnitPopupRow.PetRename, UnitPopupRow.PetAbandon,
         UnitPopupRow.PetDismiss, UnitPopupRow.Cancel];
    private static readonly UnitPopupRow[] PartyMenu =
        [UnitPopupRow.Whisper, UnitPopupRow.Promote, UnitPopupRow.LootPromote,
         UnitPopupRow.Uninvite, UnitPopupRow.Inspect, UnitPopupRow.Trade,
         UnitPopupRow.Follow, UnitPopupRow.Duel, UnitPopupRow.RaidTargetIcon,
         UnitPopupRow.Cancel];
    private static readonly UnitPopupRow[] PlayerMenu =
        [UnitPopupRow.Whisper, UnitPopupRow.Inspect, UnitPopupRow.Invite,
         UnitPopupRow.Trade, UnitPopupRow.Follow, UnitPopupRow.Duel,
         UnitPopupRow.RaidTargetIcon, UnitPopupRow.Cancel];
    private static readonly UnitPopupRow[] FriendMenu =
        [UnitPopupRow.Whisper, UnitPopupRow.Invite, UnitPopupRow.Cancel];
    private static readonly UnitPopupRow[] LootMethodMenu =
        [UnitPopupRow.FreeForAll, UnitPopupRow.RoundRobin, UnitPopupRow.MasterLooter,
         UnitPopupRow.GroupLoot, UnitPopupRow.NeedBeforeGreed, UnitPopupRow.Cancel];
    private static readonly UnitPopupRow[] LootThresholdMenu =
        [UnitPopupRow.Quality2, UnitPopupRow.Quality3, UnitPopupRow.Quality4,
         UnitPopupRow.Cancel];
    private static readonly UnitPopupRow[] RaidTargetMenu =
        [UnitPopupRow.RaidTarget1, UnitPopupRow.RaidTarget2, UnitPopupRow.RaidTarget3,
         UnitPopupRow.RaidTarget4, UnitPopupRow.RaidTarget5, UnitPopupRow.RaidTarget6,
         UnitPopupRow.RaidTarget7, UnitPopupRow.RaidTarget8, UnitPopupRow.RaidTargetNone];

    private static UnitPopupRow[] Menu(UnitPopupWhich which) => which switch
    {
        UnitPopupWhich.Self => SelfMenu,
        UnitPopupWhich.Pet => PetMenu,
        UnitPopupWhich.Party => PartyMenu,
        UnitPopupWhich.Friend => FriendMenu,
        _ => PlayerMenu,
    };

    public static UnitPopupRow[] SubmenuRows(UnitPopupSubmenu submenu) => submenu switch
    {
        UnitPopupSubmenu.LootMethod => LootMethodMenu,
        UnitPopupSubmenu.LootThreshold => LootThresholdMenu,
        UnitPopupSubmenu.RaidTargetIcon => RaidTargetMenu,
        _ => [],
    };

    public static UnitPopupSubmenu SubmenuFor(UnitPopupRow row) => row switch
    {
        UnitPopupRow.LootMethod => UnitPopupSubmenu.LootMethod,
        UnitPopupRow.LootThreshold => UnitPopupSubmenu.LootThreshold,
        UnitPopupRow.RaidTargetIcon => UnitPopupSubmenu.RaidTargetIcon,
        _ => UnitPopupSubmenu.None,
    };

    public static string RowText(UnitPopupRow row, byte lootMethod = 0, byte lootThreshold = 2)
        => row switch
        {
            UnitPopupRow.Whisper => "Whisper",
            UnitPopupRow.PetPaperDoll => "Pet Details",
            UnitPopupRow.PetRename => "Rename",
            UnitPopupRow.PetAbandon => "Abandon",
            UnitPopupRow.PetDismiss => "Dismiss",
            UnitPopupRow.Inspect => "Inspect",
            UnitPopupRow.Invite => "Invite",
            UnitPopupRow.Uninvite => "Uninvite",
            UnitPopupRow.Promote => "Promote to leader",
            UnitPopupRow.Leave => "Leave Party",
            UnitPopupRow.Trade => "Trade",
            UnitPopupRow.Follow => "Follow",
            UnitPopupRow.Duel => "Duel",
            UnitPopupRow.LootMethod => LootMethodText(lootMethod),
            UnitPopupRow.LootThreshold => QualityText(lootThreshold),
            UnitPopupRow.LootPromote => "Promote to Master Looter",
            UnitPopupRow.RaidTargetIcon => "Raid Target Icon",
            UnitPopupRow.FreeForAll => "Free for All",
            UnitPopupRow.RoundRobin => "Round Robin",
            UnitPopupRow.MasterLooter => "Master Looter",
            UnitPopupRow.GroupLoot => "Group Loot",
            UnitPopupRow.NeedBeforeGreed => "Need Before Greed",
            UnitPopupRow.Quality2 => "Uncommon",
            UnitPopupRow.Quality3 => "Rare",
            UnitPopupRow.Quality4 => "Epic",
            UnitPopupRow.RaidTarget1 => "Star",
            UnitPopupRow.RaidTarget2 => "Circle",
            UnitPopupRow.RaidTarget3 => "Diamond",
            UnitPopupRow.RaidTarget4 => "Triangle",
            UnitPopupRow.RaidTarget5 => "Moon",
            UnitPopupRow.RaidTarget6 => "Square",
            UnitPopupRow.RaidTarget7 => "Cross",
            UnitPopupRow.RaidTarget8 => "Skull",
            UnitPopupRow.RaidTargetNone => "None",
            _ => "Cancel",
        };

    public static string LootMethodText(byte method) => method switch
    {
        1 => "Round Robin", 2 => "Master Looter", 3 => "Group Loot",
        4 => "Need Before Greed", _ => "Free for All",
    };

    public static string QualityText(byte quality) => quality switch
    {
        3 => "Rare", 4 => "Epic", _ => "Uncommon",
    };

    public static UnitPopupRow[] VisibleRows(UnitPopupWhich which, bool inParty, bool isLeader,
        bool isRaid, bool canCooperate, bool unitInParty, bool isAssistant = false,
        byte lootMethod = 0, bool unitIsLootMaster = false)
        => Menu(which)
            .Where(row => RowShown(row, which, inParty, isLeader, isRaid, canCooperate,
                unitInParty, isAssistant, lootMethod, unitIsLootMaster))
            .ToArray();

    public static UnitPopupRow[] VisiblePetRows(bool ownedSummon, bool canAbandon,
        bool canRename) => PetMenu
        .Where(row => row switch
        {
            _ when !ownedSummon => row == UnitPopupRow.Cancel,
            UnitPopupRow.PetPaperDoll or UnitPopupRow.PetAbandon => canAbandon,
            UnitPopupRow.PetRename => canAbandon && canRename,
            UnitPopupRow.PetDismiss => !canAbandon,
            _ => true,
        }).ToArray();

    private static bool RowShown(UnitPopupRow row, UnitPopupWhich which, bool inParty,
        bool isLeader, bool isRaid, bool canCooperate, bool unitInParty, bool isAssistant,
        byte lootMethod, bool unitIsLootMaster) => row switch
    {
        UnitPopupRow.Whisper or UnitPopupRow.Trade or UnitPopupRow.Follow or UnitPopupRow.Duel or
            UnitPopupRow.Inspect => canCooperate,
        UnitPopupRow.Invite => canCooperate && !unitInParty,
        UnitPopupRow.Promote or UnitPopupRow.Uninvite => inParty && isLeader,
        UnitPopupRow.Leave or UnitPopupRow.LootMethod or UnitPopupRow.LootThreshold => inParty,
        UnitPopupRow.LootPromote => inParty && isLeader && lootMethod == 2 && !unitIsLootMaster,
        UnitPopupRow.RaidTargetIcon => inParty && (isLeader || isAssistant) &&
            (which == UnitPopupWhich.Self || canCooperate),
        _ => true,
    };

    public static bool RowEnabled(UnitPopupRow row, bool inParty, bool isLeader,
        bool isRaid, bool connected, float distanceSquared) => row switch
    {
        UnitPopupRow.Invite => !inParty || isLeader,
        UnitPopupRow.Whisper => connected,
        UnitPopupRow.Trade => distanceSquared < TradeDistanceSq,
        UnitPopupRow.Uninvite or UnitPopupRow.Promote => inParty && isLeader,
        UnitPopupRow.Leave => inParty,
        _ => true,
    };

    public static bool ShouldOpen(UnitPopupRow[] rows) =>
        rows.Any(row => row != UnitPopupRow.Cancel);

    public static bool HasArrow(UnitPopupRow row, bool isLeader) =>
        (isLeader && row is UnitPopupRow.LootMethod or UnitPopupRow.LootThreshold) ||
        row == UnitPopupRow.RaidTargetIcon;

    public static bool IsCheckable(UnitPopupRow row) =>
        row is >= UnitPopupRow.FreeForAll and <= UnitPopupRow.RaidTargetNone;

    public static bool HasRaidIcon(UnitPopupRow row) =>
        row is >= UnitPopupRow.RaidTarget1 and <= UnitPopupRow.RaidTarget8;

    public static byte LootMethodValue(UnitPopupRow row) => row switch
    {
        UnitPopupRow.RoundRobin => 1, UnitPopupRow.MasterLooter => 2,
        UnitPopupRow.GroupLoot => 3, UnitPopupRow.NeedBeforeGreed => 4, _ => 0,
    };

    public static byte QualityValue(UnitPopupRow row) => row switch
    {
        UnitPopupRow.Quality3 => 3, UnitPopupRow.Quality4 => 4, _ => 2,
    };

    public static byte RaidTargetValue(UnitPopupRow row) =>
        row is >= UnitPopupRow.RaidTarget1 and <= UnitPopupRow.RaidTarget8
            ? checked((byte)(row - UnitPopupRow.RaidTarget1 + 1)) : (byte)0;

    public static bool IsChecked(UnitPopupRow row, byte lootMethod, byte lootThreshold,
        byte raidTarget) => row switch
    {
        >= UnitPopupRow.FreeForAll and <= UnitPopupRow.NeedBeforeGreed =>
            LootMethodValue(row) == lootMethod,
        >= UnitPopupRow.Quality2 and <= UnitPopupRow.Quality4 =>
            QualityValue(row) == lootThreshold,
        >= UnitPopupRow.RaidTarget1 and <= UnitPopupRow.RaidTarget8 =>
            RaidTargetValue(row) == raidTarget,
        UnitPopupRow.RaidTargetNone => raidTarget == 0,
        _ => false,
    };

    public static Vector4? RowColor(UnitPopupRow row, byte lootThreshold = 2) => row switch
    {
        UnitPopupRow.LootThreshold => QualityColor(lootThreshold),
        UnitPopupRow.Quality2 => QualityColor(2),
        UnitPopupRow.Quality3 => QualityColor(3),
        UnitPopupRow.Quality4 => QualityColor(4),
        UnitPopupRow.RaidTarget1 => new(1f, .92f, 0f, 1f),
        UnitPopupRow.RaidTarget2 => new(.98f, .57f, 0f, 1f),
        UnitPopupRow.RaidTarget3 => new(.83f, .22f, .9f, 1f),
        UnitPopupRow.RaidTarget4 => new(.04f, .95f, 0f, 1f),
        UnitPopupRow.RaidTarget5 => new(.7f, .82f, .875f, 1f),
        UnitPopupRow.RaidTarget6 => new(0f, .71f, 1f, 1f),
        UnitPopupRow.RaidTarget7 => new(1f, .24f, .168f, 1f),
        UnitPopupRow.RaidTarget8 => new(.98f, .98f, .98f, 1f),
        _ => null,
    };

    public static Vector4 QualityColor(byte quality) => quality switch
    {
        3 => new(0f, .44f, .87f, 1f), 4 => new(.64f, .21f, .93f, 1f),
        _ => new(.12f, 1f, 0f, 1f),
    };

    public static (Vector2 Uv0, Vector2 Uv1) RaidIconUv(UnitPopupRow row)
    {
        int cell = Math.Max(0, RaidTargetValue(row) - 1);
        int atlasRow = cell / 4;
        int atlasColumn = cell - atlasRow * 4;
        return (new(atlasColumn * .25f, atlasRow * .25f),
            new((atlasColumn + 1) * .25f, (atlasRow + 1) * .25f));
    }

    public const float MinimumCardWidth = 45f;
    public const float BorderHeight = 15f;
    public const float RowHeight = 16f;
    public const float PlainRowLeft = 15f;
    public const float CheckableRowLeft = 11f;
    public const float PlainTextLeft = 15f;
    public const float CheckableTextLeft = 38f;
    public const float TextTopInset = 3f;
    public const float AutoCloseSeconds = 5f;
    public const float ViewportMargin = 4f;
    // GameTooltip.xml globals applied by UIDropDownListTemplate's MENU backdrop OnLoad.
    public static readonly Vector4 MenuBackdropFillTint = new(.09f, .09f, .19f, 1f);
    public static readonly Vector4 MenuBackdropEdgeTint = Vector4.One;

    public static float CardWidth(float widestText) => CardWidth(
        [new UnitPopupWidthMeasure(widestText, true, false, false)]);

    /// <summary>Reference settle: text+60, arrow+10, icon+10, notCheckable-30, list+15.</summary>
    public static float CardWidth(IEnumerable<UnitPopupWidthMeasure> rows)
    {
        float widest = 0f;
        foreach (UnitPopupWidthMeasure row in rows)
        {
            float width = float.IsFinite(row.TextWidth) ? MathF.Max(0f, row.TextWidth) : 0f;
            width += 60f;
            if (row.HasArrow) width += 10f;
            if (row.HasIcon) width += 10f;
            if (row.NotCheckable) width -= 30f;
            widest = MathF.Max(widest, width);
        }
        return MathF.Ceiling(MathF.Max(30f, widest) + 15f);
    }

    public static float CardHeight(int rows) => MenuHeight(rows, hasTitle: true);
    public static float MenuHeight(int rows, bool hasTitle) =>
        (Math.Max(0, rows) + (hasTitle ? 1 : 0)) * RowHeight + BorderHeight * 2f;

    public static readonly Vector2 TitleOrigin = new(PlainTextLeft,
        BorderHeight + TextTopInset);

    public static Vector2 RowOrigin(int index, bool hasTitle = true, bool checkable = false) =>
        new(checkable ? CheckableRowLeft : PlainRowLeft,
            BorderHeight + (Math.Max(0, index) + (hasTitle ? 1 : 0)) * RowHeight);

    public static Vector2 RowSize(float cardWidth, bool checkable = false) =>
        new(MathF.Max(1f, cardWidth - (checkable ? CheckableRowLeft : PlainRowLeft)), RowHeight);

    public static Vector2 RowTextOrigin(int index, bool hasTitle = true,
        bool checkable = false) => new(checkable ? CheckableTextLeft : PlainTextLeft,
            BorderHeight + (Math.Max(0, index) + (hasTitle ? 1 : 0)) * RowHeight + TextTopInset);

    public static Vector2 CheckOrigin(int index) =>
        new(CheckableRowLeft, BorderHeight + index * RowHeight - 4f);

    public static Vector2 RightDecorationOrigin(int index, float cardWidth, float size,
        bool hasTitle = true, float inset = 6f) =>
        new(cardWidth - inset - size,
            BorderHeight + (index + (hasTitle ? 1 : 0)) * RowHeight + (RowHeight - size) * .5f);

    /// <summary>Level 2 seats at the parent row TOPRIGHT, then applies Benilla's edge flips.</summary>
    public static Vector2 SubmenuOrigin(Vector2 parentOrigin, float parentWidth, int parentRow,
        Vector2 submenuSize, Vector2 displaySize)
    {
        float rowTop = parentOrigin.Y + BorderHeight + (parentRow + 1) * RowHeight;
        float rowLeft = parentOrigin.X + PlainRowLeft;
        float rowRight = parentOrigin.X + parentWidth;
        Vector2 desired = new(rowRight, rowTop);
        bool offRight = desired.X + submenuSize.X > displaySize.X;
        bool offBottom = desired.Y + submenuSize.Y > displaySize.Y;
        if (offRight) desired.X = rowLeft - submenuSize.X + 11f;
        if (offBottom) desired.Y = rowTop + RowHeight + 14f - submenuSize.Y;
        return desired;
    }

    public static Vector2 ClampOrigin(Vector2 desired, Vector2 menuSize, Vector2 displaySize)
    {
        float x = float.IsFinite(desired.X) ? desired.X : ViewportMargin;
        float y = float.IsFinite(desired.Y) ? desired.Y : ViewportMargin;
        float width = float.IsFinite(menuSize.X) ? MathF.Max(0f, menuSize.X) : 0f;
        float height = float.IsFinite(menuSize.Y) ? MathF.Max(0f, menuSize.Y) : 0f;
        float displayWidth = float.IsFinite(displaySize.X) ? MathF.Max(0f, displaySize.X) : 0f;
        float displayHeight = float.IsFinite(displaySize.Y) ? MathF.Max(0f, displaySize.Y) : 0f;
        float maxX = MathF.Max(ViewportMargin, displayWidth - width - ViewportMargin);
        float maxY = MathF.Max(ViewportMargin, displayHeight - height - ViewportMargin);
        return new(Math.Clamp(x, ViewportMargin, maxX), Math.Clamp(y, ViewportMargin, maxY));
    }
}

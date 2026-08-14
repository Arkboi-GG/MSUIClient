using System.Numerics;

namespace MSUIClient.Engine.UI;

/// <summary>The FrameXML menu token passed to UnitPopup_ShowMenu.</summary>
public enum UnitPopupWhich
{
    Self,
    Party,
    Player,
}

public enum UnitPopupRow
{
    Whisper,
    Inspect,
    Invite,
    Uninvite,
    Promote,
    ConvertToRaid,
    Leave,
    Trade,
    Cancel,
}

/// <summary>
/// The build-5875 UnitPopup menu tables and row gating (UnitPopup.lua). Rows whose backing
/// system does not exist yet (Follow, Duel, the loot-method/threshold submenus, raid target
/// icons, and the dedicated RAID-member menu) are hidden outright rather than shown dead —
/// the reference deferral pattern — and stay tracked in parity/registry/ui/unitpopup.json.
/// Party-to-raid conversion is implemented on the leader's SELF menu.
/// </summary>
public static class UnitPopupUiLaw
{
    /// <summary>
    /// CheckInteractDistance squared-yard entries for the shown distance-gated rows
    /// (check_interact_dist2: inspect dist 1 = 10yd, trade dist 2 = 11.1111yd).
    /// Inspect keeps its gate in InspectUiLaw.PopupRowEnabled.
    /// </summary>
    public const float TradeDistanceSq = 11.1111f * 11.1111f;

    // UnitPopupMenus, in reference order, deferred rows removed.
    private static readonly UnitPopupRow[] SelfMenu =
        [UnitPopupRow.ConvertToRaid, UnitPopupRow.Leave, UnitPopupRow.Cancel];
    private static readonly UnitPopupRow[] PartyMenu =
        [UnitPopupRow.Whisper, UnitPopupRow.Promote, UnitPopupRow.Uninvite,
         UnitPopupRow.Inspect, UnitPopupRow.Trade, UnitPopupRow.Cancel];
    private static readonly UnitPopupRow[] PlayerMenu =
        [UnitPopupRow.Whisper, UnitPopupRow.Inspect, UnitPopupRow.Invite,
         UnitPopupRow.Trade, UnitPopupRow.Cancel];

    private static UnitPopupRow[] Menu(UnitPopupWhich which) => which switch
    {
        UnitPopupWhich.Self => SelfMenu,
        UnitPopupWhich.Party => PartyMenu,
        _ => PlayerMenu,
    };

    /// <summary>The 1.12 GlobalStrings label for each row.</summary>
    public static string RowText(UnitPopupRow row) => row switch
    {
        UnitPopupRow.Whisper => "Whisper",
        UnitPopupRow.Inspect => "Inspect",
        UnitPopupRow.Invite => "Invite",
        UnitPopupRow.Uninvite => "Uninvite",
        UnitPopupRow.Promote => "Promote to leader",
        UnitPopupRow.ConvertToRaid => "Convert to Raid",
        UnitPopupRow.Leave => "Leave party",
        UnitPopupRow.Trade => "Trade",
        _ => "Cancel",
    };

    /// <summary>UnitPopup_HideButtons: which rows of the menu are shown at all.</summary>
    public static UnitPopupRow[] VisibleRows(UnitPopupWhich which, bool inParty, bool isLeader,
        bool isRaid, bool canCooperate, bool unitInParty)
        => Menu(which)
            .Where(row => RowShown(row, inParty, isLeader, isRaid, canCooperate, unitInParty))
            .ToArray();

    private static bool RowShown(UnitPopupRow row, bool inParty, bool isLeader,
        bool isRaid, bool canCooperate, bool unitInParty) => row switch
    {
        // Whisper/Trade/Duel exist only for a unit we can cooperate with (ref l.285/311/339);
        // Invite additionally never targets someone already grouped with us.
        UnitPopupRow.Whisper or UnitPopupRow.Trade => canCooperate,
        UnitPopupRow.Invite => canCooperate && !unitInParty,
        UnitPopupRow.Promote or UnitPopupRow.Uninvite => inParty && isLeader,
        UnitPopupRow.ConvertToRaid => inParty && isLeader && !isRaid,
        UnitPopupRow.Leave => inParty,
        _ => true,
    };

    /// <summary>
    /// UnitPopup_OnUpdate: per-frame enable pass over the shown rows. Inspect is gated at the
    /// call site through InspectUiLaw; rows without a clause keep the enabled default.
    /// </summary>
    public static bool RowEnabled(UnitPopupRow row, bool inParty, bool isLeader,
        bool isRaid, bool connected, float distanceSquared) => row switch
    {
        UnitPopupRow.Invite => !inParty || isLeader,
        UnitPopupRow.Whisper => connected,
        UnitPopupRow.Trade => distanceSquared < TradeDistanceSq,
        UnitPopupRow.Uninvite or UnitPopupRow.Promote => inParty && isLeader,
        UnitPopupRow.ConvertToRaid => inParty && isLeader && !isRaid,
        UnitPopupRow.Leave => inParty,
        _ => true,
    };

    /// <summary>UnitPopup_ShowMenu refuses a menu whose only surviving row is Cancel.</summary>
    public static bool ShouldOpen(UnitPopupRow[] rows)
        => rows.Any(row => row != UnitPopupRow.Cancel);

    // UIDropDownMenu MENU-mode geometry: a compact title + 16px text rows inside the
    // UI-Tooltip backdrop. Width grows for long player/command labels instead of clipping.
    public const float MinCardWidth = 120f;
    public const float MaxCardWidth = 240f;
    public const float HorizontalPadding = 10f;
    public const float BackdropInset = 5f;
    public const float TopPadding = 7f;
    public const float BottomPadding = 7f;
    public const float RowHeight = 16f;
    public const float TitleGap = 2f;
    public const float AutoCloseSeconds = 5f;
    public const float ViewportMargin = 4f;

    public static float CardWidth(float widestText)
    {
        float content = float.IsFinite(widestText) ? MathF.Max(0f, widestText) : 0f;
        return Math.Clamp(MathF.Ceiling(content + HorizontalPadding * 2f),
            MinCardWidth, MaxCardWidth);
    }

    public static float CardHeight(int rows) =>
        TopPadding + RowHeight + TitleGap + Math.Max(0, rows) * RowHeight + BottomPadding;

    public static readonly Vector2 TitleOrigin = new(HorizontalPadding, TopPadding);

    public static Vector2 RowOrigin(int index) => new(BackdropInset,
        TopPadding + RowHeight + TitleGap + Math.Max(0, index) * RowHeight);

    public static Vector2 RowSize(float cardWidth) =>
        new(MathF.Max(1f, cardWidth - BackdropInset * 2f), RowHeight);

    public static Vector2 RowTextOrigin(int index) => new(HorizontalPadding,
        RowOrigin(index).Y + 3f);

    /// <summary>UIDropDownMenu's screen-edge correction for menus opened near a viewport edge.</summary>
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
        return new Vector2(Math.Clamp(x, ViewportMargin, maxX),
            Math.Clamp(y, ViewportMargin, maxY));
    }
}

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
    Leave,
    Trade,
    Cancel,
}

/// <summary>
/// The build-5875 UnitPopup menu tables and row gating (UnitPopup.lua). Rows whose backing
/// system does not exist yet (Follow, Duel, the loot-method/threshold submenus, raid target
/// icons, the RAID menu) are hidden outright rather than shown dead — the reference deferral
/// pattern — and stay tracked in parity/registry/ui/unitpopup.json.
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
        [UnitPopupRow.Leave, UnitPopupRow.Cancel];
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
        UnitPopupRow.Leave => "Leave party",
        UnitPopupRow.Trade => "Trade",
        _ => "Cancel",
    };

    /// <summary>UnitPopup_HideButtons: which rows of the menu are shown at all.</summary>
    public static UnitPopupRow[] VisibleRows(UnitPopupWhich which, bool inParty, bool isLeader,
        bool canCooperate, bool unitInParty)
        => Menu(which)
            .Where(row => RowShown(row, inParty, isLeader, canCooperate, unitInParty))
            .ToArray();

    private static bool RowShown(UnitPopupRow row, bool inParty, bool isLeader,
        bool canCooperate, bool unitInParty) => row switch
    {
        // Whisper/Trade/Duel exist only for a unit we can cooperate with (ref l.285/311/339);
        // Invite additionally never targets someone already grouped with us.
        UnitPopupRow.Whisper or UnitPopupRow.Trade => canCooperate,
        UnitPopupRow.Invite => canCooperate && !unitInParty,
        UnitPopupRow.Promote or UnitPopupRow.Uninvite => inParty && isLeader,
        UnitPopupRow.Leave => inParty,
        _ => true,
    };

    /// <summary>
    /// UnitPopup_OnUpdate: per-frame enable pass over the shown rows. Inspect is gated at the
    /// call site through InspectUiLaw; rows without a clause keep the enabled default.
    /// </summary>
    public static bool RowEnabled(UnitPopupRow row, bool inParty, bool isLeader,
        bool connected, float distanceSquared) => row switch
    {
        UnitPopupRow.Invite => !inParty || isLeader,
        UnitPopupRow.Whisper => connected,
        UnitPopupRow.Trade => distanceSquared < TradeDistanceSq,
        UnitPopupRow.Uninvite or UnitPopupRow.Promote => inParty && isLeader,
        UnitPopupRow.Leave => inParty,
        _ => true,
    };

    /// <summary>UnitPopup_ShowMenu refuses a menu whose only surviving row is Cancel.</summary>
    public static bool ShouldOpen(UnitPopupRow[] rows)
        => rows.Any(row => row != UnitPopupRow.Cancel);

    // The bespoke card geometry (the original fixed 4-row card, made row-count aware).
    public const float CardWidth = 128f;
    public static float CardHeight(int rows) => 41f + rows * 25f;
    public static Vector2 RowOrigin(int index) => new(14f, 34f + index * 25f);
    public static readonly Vector2 RowSize = new(100f, 22f);
}

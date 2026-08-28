namespace MSUIClient.Engine.UI;

public readonly record struct QuestMarkerStyle(string ModelPath);

/// <summary>
/// Which kind of business a member has at a questgiver. Vanilla's eight dialog
/// statuses collapse to three for the purpose of counting a party: you can pick
/// something up here, you can hand something in here, or you cannot act here.
/// </summary>
public enum QuestMarkerFamily
{
    None,
    Take,
    TurnIn,
}

/// <summary>Build-5875 dialog-status to TalkToMe marker mapping.</summary>
public static class QuestMarkerUiLaw
{
    public static readonly QuestMarkerStyle UnknownFlightMaster =
        new(@"Interface\Buttons\TalkToMeGreen.m2");

    public static QuestMarkerStyle? Style(uint status) => status switch
    {
        1 => new(@"Interface\Buttons\TalkToMeGrey.m2"),
        3 => new(@"Interface\Buttons\TalkToMeQuestion_Grey.m2"),
        4 => new(@"Interface\Buttons\TalkToMeQuestion_LTBlue.m2"),
        5 => new(@"Interface\Buttons\TalkToMe.m2"),
        6 or 7 => new(@"Interface\Buttons\TalkToMeQuestionMark.m2"),
        _ => null,
    };

    // ── PLAN_20 P5: the party numeral ────────────────────────────────────────
    //
    // Owner decision 5: keep the exact vanilla art, font and yellow, and hang a
    // parenthesised numeral over it — (4) when four of your group can take what
    // this NPC offers, and the same for turn-ins.

    /// <summary>The vanilla quest font and yellow. Owner 2026-08-28: the LARGE
    /// face read as shouting from the sky and sat on top of the marker art —
    /// the plain size is the label, not a headline.</summary>
    public const string NumeralFontObject = "GameFontNormal";

    /// <summary>
    /// Yards of clearance above the head anchor, so the label clears the WHOLE
    /// TalkToMe M2 (the ! / ? art tops out well above the head) instead of
    /// printing across it. The draw additionally bottom-anchors the text at
    /// this height, so no part of it ever overlaps the marker.
    /// </summary>
    public const float NumeralClearanceYards = 2.75f;

    public static QuestMarkerFamily FamilyOf(uint status) => status switch
    {
        // REWARD_REP (4) is drawn as a blue question mark but MEANS "there is
        // something here you may take" — a repeatable quest you are eligible for.
        // It belongs with AVAILABLE for counting, not with the turn-ins it
        // resembles.
        4 or 5 => QuestMarkerFamily.Take,
        6 or 7 => QuestMarkerFamily.TurnIn,
        _ => QuestMarkerFamily.None,
    };

    /// <summary>
    /// The marker art to draw for a questgiver our OWN character has no business
    /// at, when a companion does — owner 2026-08-27. Take gets the yellow "!",
    /// turn-in the yellow "?", exactly the vanilla art vanilla would draw for us
    /// if we had that business ourselves.
    /// </summary>
    public static QuestMarkerStyle? StyleForFamily(QuestMarkerFamily family) => family switch
    {
        QuestMarkerFamily.Take => Style(5),
        QuestMarkerFamily.TurnIn => Style(6),
        _ => null,
    };

    /// <summary>
    /// Which family this NPC's numeral should count, given our own status there
    /// and what the group can do. Ours decides when we have business here; when
    /// we do not (a grey marker), the numeral speaks for whoever does — which is
    /// the case where it earns its keep, because walking past is the alternative.
    /// </summary>
    public static QuestMarkerFamily NumeralFamily(uint ownStatus, int takers, int finishers)
    {
        QuestMarkerFamily own = FamilyOf(ownStatus);
        if (own != QuestMarkerFamily.None) return own;
        if (takers > 0) return QuestMarkerFamily.Take;
        return finishers > 0 ? QuestMarkerFamily.TurnIn : QuestMarkerFamily.None;
    }

    /// <summary>
    /// Whether a numeral is worth drawing at all.
    ///
    /// One member who is us is what vanilla already says by drawing the marker,
    /// so "(1)" over our own available quest is pure noise — and drawing nothing
    /// there keeps solo play pixel-identical to vanilla, which is the whole
    /// reason this is additive. A count of one that is NOT us is worth saying,
    /// because nothing else on screen would tell us.
    /// </summary>
    public static bool ShowNumeral(uint ownStatus, QuestMarkerFamily family, int count)
    {
        if (family == QuestMarkerFamily.None || count <= 0) return false;
        return count >= 2 || FamilyOf(ownStatus) != family;
    }

    public static string NumeralText(int count) => $"({count})";
}

namespace MSUIClient.Net;

/// <summary>
/// Pure Warcraft-style companion voice law: which 1.12 vocal answers which RTS
/// gesture. Every line already ships in the client's own data — EmotesTextSound.dbc
/// carries the race/gender player vocals ("Yes", "Charge!", hellos, refusals) and
/// the StandardNPC pissed kits carry the click-me-again lines — so companions
/// speak with their authentic vanilla voices and no new asset exists.
/// </summary>
public static class CompanionVoiceLaw
{
    // EmotesText.dbc ids whose EmotesTextSound rows carry the race/gender vocal
    // kits, verified against the build-5875 archives: 19 goodbye, 55 hello,
    // 66 "No", 67 the "Yes" vocal, 305 "charge", 324 "follow me", 327 "open fire".
    public const uint EmoteBye = 19;
    public const uint EmoteHello = 55;
    public const uint EmoteNo = 66;
    public const uint EmoteYes = 67;
    public const uint EmoteCharge = 305;
    public const uint EmoteFollowMe = 324;
    public const uint EmoteOpenFire = 327;

    /// <summary>Drop-not-queue spacing between spoken lines.</summary>
    public const double MinSecondsBetweenLines = 1.2;

    /// <summary>Cascade spacing inside one order's chorus ("Yes—" "Aye—" "Okay").</summary>
    public const double ChorusSpacingSeconds = 0.4;

    /// <summary>
    /// How many companions answer one order: a handful gets a single voice, a
    /// squad gets two, an army gets three. More than three is noise, not troops.
    /// </summary>
    public static int ChorusSize(int subjectCount) =>
        subjectCount >= 10 ? 3 : subjectCount >= 4 ? 2 : 1;

    /// <summary>
    /// The acknowledgement a companion speaks for a CMSG_SUI_ORDER type byte.
    /// 0 = the order has no natural line (link and auto-group are meta orders
    /// whose voices belong to their own gestures).
    /// </summary>
    public static uint OrderEmote(byte orderType, byte classId) => orderType switch
    {
        // move, hold, waypoint, patrol, follow/regroup, formations, sheath,
        // enlistment — all plain compliance
        0 or 2 or 3 or 4 or 5 or 8 or 9 or 10 or 11 => EmoteYes,
        1 => RangedClass(classId) ? EmoteOpenFire : EmoteCharge,
        12 => EmoteBye,   // mustered out: a farewell, not an acknowledgement
        _ => 0,
    };

    /// <summary>Hunters, priests, mages and warlocks open fire; everyone else charges.</summary>
    public static bool RangedClass(byte classId) => classId is 3 or 5 or 8 or 9;

    /// <summary>
    /// SoundEntries kit name for a race/gender's pissed lines. The StandardNPC
    /// set covers 14 of the 16 player combinations; dwarf women and tauren men
    /// borrow their nearest recorded voice. Unknown races stay silent.
    /// </summary>
    public static string? PissedKitName(byte race, byte gender) => (race, gender) switch
    {
        (3, 1) => "DwarfFemaleMaternalNPCPissed",
        (6, 0) => "TaurenMaleWarriorNPCPissed",
        (1, _) => Pissed("Human", gender),
        (2, _) => Pissed("Orc", gender),
        (3, _) => Pissed("Dwarf", gender),
        (4, _) => Pissed("NightElf", gender),
        (5, _) => Pissed("Undead", gender),
        (6, _) => Pissed("Tauren", gender),
        (7, _) => Pissed("Gnome", gender),
        (8, _) => Pissed("Troll", gender),
        _ => null,
    };

    private static string Pissed(string race, byte gender) =>
        $"{race}{(gender == 1 ? "Female" : "Male")}StandardNPCPissed";
}

namespace MSUIClient.Engine.UI;

public enum PlayerFrameStatus { None, HateList, Attacking, Resting }

/// <summary>PlayerFrame/TargetFrame state-derived presentation laws.</summary>
public static class UnitFrameUiLaw
{
    public const uint UnitFlagPvp = 0x0000_1000;
    public const uint PlayerFlagFfaPvp = 0x0000_0080;
    public const uint PlayerFlagResting = 0x0000_0020;

    /// <summary>PlayerFrame_UpdateStatus priority: resting, auto-attack, hate list, none.</summary>
    public static PlayerFrameStatus Status(uint playerFlags, bool autoAttacking,
        bool onHateList)
    {
        if ((playerFlags & PlayerFlagResting) != 0) return PlayerFrameStatus.Resting;
        if (autoAttacking) return PlayerFrameStatus.Attacking;
        return onHateList ? PlayerFrameStatus.HateList : PlayerFrameStatus.None;
    }

    /// <summary>1.12's level cap.</summary>
    public const uint MaxPlayerLevel = 60;

    /// <summary>
    /// MSUI's own layer over <see cref="Status"/>, and a deliberate deviation from
    /// PlayerFrame_UpdateStatus (owner call, 2026-08-30): the RESTING icon stands down at the
    /// level cap. IsResting() has no level test in the reference, so 1.12 shows the Zzz in an
    /// inn at 60 exactly as it does at 12 - but rested XP has nowhere to go there, so the icon
    /// is reporting a bonus that cannot exist. Combat and hate-list states are untouched;
    /// crossed swords still mean what they always meant at any level.
    /// </summary>
    public static PlayerFrameStatus VisibleStatus(PlayerFrameStatus status, uint level) =>
        status == PlayerFrameStatus.Resting && level >= MaxPlayerLevel
            ? PlayerFrameStatus.None : status;

    /// <summary>
    /// Does the level number draw?
    ///
    /// Blizzard stacks them ON PURPOSE - PlayerLevelText is CENTER (-63,-16), which on the
    /// 232x100 frame is (53,66), and PlayerRestIcon is TOPLEFT (37,-49) at 31x33, whose centre
    /// is (52.5,65.5). Concentric. The reference gets away with it because the icon is in the
    /// OVERLAY layer and the text in BACKGROUND, so the bubble simply sits on top of the number.
    ///
    /// MSUI does not get away with it: the rest quadrant measures only 7% opaque, so the number
    /// reads THROUGH the bubble and the two become mush (reported 2026-08-30). Rather than
    /// reorder two draws that would still overlap, the icon REPLACES the number while it is up -
    /// the ordinary "state icon takes the slot" idiom, and the only reading of "they must not
    /// share the same space" that holds at every level rather than just at the cap.
    /// </summary>
    public static bool ShowsLevelText(PlayerFrameStatus visibleStatus) =>
        visibleStatus == PlayerFrameStatus.None;

    /// <summary>The reference 0.5-second triangle pulse, alpha 55/255 through 1.</summary>
    public static float StatusPulse(double seconds)
    {
        double phase = seconds % 1d;
        if (phase < 0) phase += 1d;
        double counter = phase <= .5d ? phase : 1d - phase;
        return (float)((255d - counter * 400d) / 255d);
    }

    /// <summary>
    /// TargetFrame_CheckClassification: 1.12 deliberately shares Elite art for elite,
    /// rare-elite and world-boss. The later-expansion Rare-Elite texture is not used.
    /// </summary>
    public static string TargetFrameTexture(uint creatureRank) => creatureRank switch
    {
        1 or 2 or 3 => @"Interface\TargetingFrame\UI-TargetingFrame-Elite",
        4 => @"Interface\TargetingFrame\UI-TargetingFrame-Rare",
        _ => @"Interface\TargetingFrame\UI-TargetingFrame",
    };

    public static string? PvpIcon(byte race, uint unitFlags, uint playerFlags)
    {
        if ((playerFlags & PlayerFlagFfaPvp) != 0)
            return @"Interface\TargetingFrame\UI-PVP-FFA";
        if ((unitFlags & UnitFlagPvp) == 0) return null;
        string? faction = race switch
        {
            1 or 3 or 4 or 7 => "Alliance",
            2 or 5 or 6 or 8 => "Horde",
            _ => null,
        };
        return faction is null ? null : $@"Interface\TargetingFrame\UI-PVP-{faction}";
    }
}

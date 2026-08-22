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

using System.Numerics;

namespace MSUIClient.Engine.UI;

/// <summary>Build-5875 BuffFrame.xml geometry and runtime presentation rules.</summary>
public static class BuffUiLaw
{
    public const double WarningSeconds = 31d;
    public const double FlashPeriodSeconds = 0.75d;
    public const float FlashMinimumAlpha = 0.3f;

    public static float WarningAlpha(double nowSeconds, double remainingSeconds)
    {
        if (remainingSeconds >= WarningSeconds) return 1f;
        double phase = Math.Abs(nowSeconds % (FlashPeriodSeconds * 2d));
        double ramp = phase <= FlashPeriodSeconds
            ? phase / FlashPeriodSeconds
            : 2d - phase / FlashPeriodSeconds;
        return FlashMinimumAlpha + (1f - FlashMinimumAlpha) * (float)ramp;
    }

    /// <summary>Spell.dbc Dispel mapped through FrameXML's DebuffTypeColor table.</summary>
    public static Vector4 DebuffColor(uint dispelType) => dispelType switch
    {
        1 => new(.2f, .6f, 1f, 1f),
        2 => new(.6f, 0f, 1f, 1f),
        3 => new(.6f, .4f, 0f, 1f),
        4 => new(0f, .6f, 0f, 1f),
        _ => new(.8f, 0f, 0f, 1f),
    };
}

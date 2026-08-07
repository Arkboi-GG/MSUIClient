namespace MSUIClient.Engine.UI;

/// <summary>Timing and UIParent-managed placement transcribed from CastingBar.xml/UIParent.</summary>
public static class CastingBarUiLaw
{
    public const float Width = 195f;
    public const float Height = 13f;
    public const float DefaultBottom = 55f;
    public const float ManagedBottom = 60f;
    public const float BottomBarStep = 40f;
    public const float PetOrStanceStep = 40f;
    public const float ReputationStep = 9f;
    public const double FlashSeconds = 1d / 6d;
    public const double FadeSeconds = 2d / 3d;
    public const double FailureHoldSeconds = 1d;

    public static float BottomOffset(bool bottomMultiBar, bool petOrStance, bool reputation)
        => ManagedBottom + (bottomMultiBar ? BottomBarStep : 0f) +
            (petOrStance ? PetOrStanceStep : 0f) + (reputation ? ReputationStep : 0f);

    public static float FrameAlpha(double elapsed, bool failed)
    {
        double fadeStart = failed ? FailureHoldSeconds : FlashSeconds;
        return (float)Math.Clamp(1d - Math.Max(0d, elapsed - fadeStart) / FadeSeconds, 0d, 1d);
    }

    public static float FlashAlpha(double elapsed) =>
        (float)Math.Clamp(elapsed / FlashSeconds, 0d, 1d);
}

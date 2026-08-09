namespace MSUIClient.Engine.UI;

/// <summary>Timing and UIParent-managed placement transcribed from CastingBar.xml/UIParent.</summary>
public static class CastingBarUiLaw
{
    public readonly record struct ChannelWindow(double Start, double End);
    public readonly record struct StatusFill(float Fraction, float Width, float U1);

    public const float Width = 195f;
    public const float Height = 13f;
    public const float ArtworkWidth = 256f;
    public const float ArtworkHeight = 64f;
    public const float ArtworkTopOffset = 28f;
    public const float SparkSize = 32f;
    public const float SparkOffsetY = 2f;
    // FrameXML Y offsets are positive upward while ImGui's screen Y is positive downward.
    public const float SparkMinY = Height * .5f - SparkOffsetY - SparkSize * .5f;
    public const float SparkMaxY = SparkMinY + SparkSize;
    public const float DefaultBottom = 55f;
    public const float ManagedBottom = 60f;
    public const float BottomBarStep = 40f;
    public const float PetOrStanceStep = 40f;
    public const float ReputationStep = 9f;
    public const double FlashSeconds = 1d / 6d;
    public const double FadeSeconds = 2d / 3d;
    public const double FailureHoldSeconds = 1d;
    public const string FailedText = "Failed";
    public const string InterruptedText = "Interrupted";

    public static float BottomOffset(bool bottomLeftShown, bool bottomRightShown,
        bool petOrStance, bool reputation)
        => ManagedBottom + (bottomLeftShown || bottomRightShown ? BottomBarStep : 0f) +
            (petOrStance ? PetOrStanceStep : 0f) + (reputation ? ReputationStep : 0f);

    public static float BottomOffset(bool bottomMultiBar, bool petOrStance, bool reputation)
        => BottomOffset(bottomMultiBar, bottomMultiBar, petOrStance, reputation);

    /// <summary>MSUI draws both bottom multibar frames even when all their slots are empty.</summary>
    public static float BottomOffsetForMsui(bool petOrStance, bool reputation)
        => BottomOffset(bottomLeftShown: true, bottomRightShown: true, petOrStance, reputation);

    public static bool AcceptCastTerminal(bool casting, uint activeSpell, uint eventSpell)
        => casting && activeSpell == eventSpell;

    public static string TerminalText(string source)
        => source.Contains("INTERRUPT", StringComparison.OrdinalIgnoreCase)
            ? InterruptedText : FailedText;

    public static float Progress(double start, double end, double now, bool channel)
    {
        double duration = Math.Max(.001d, end - start);
        double value = channel ? (end - now) / duration : (now - start) / duration;
        return (float)Math.Clamp(value, 0d, 1d);
    }

    public static ChannelWindow RetimeChannel(double originalStart, double originalEnd,
        double now, uint remainingMs)
    {
        double duration = originalEnd - originalStart;
        double end = now + remainingMs / 1000d;
        return new ChannelWindow(end - duration, end);
    }

    public static StatusFill Fill(float fraction)
    {
        fraction = Math.Clamp(fraction, 0f, 1f);
        return new StatusFill(fraction, Width * fraction, fraction);
    }

    public static float SparkCenter(float fraction) => Width * Math.Clamp(fraction, 0f, 1f);

    public static float FrameAlpha(double elapsed, bool failed)
    {
        double fadeStart = failed ? FailureHoldSeconds : FlashSeconds;
        return (float)Math.Clamp(1d - Math.Max(0d, elapsed - fadeStart) / FadeSeconds, 0d, 1d);
    }

    public static float FlashAlpha(double elapsed) =>
        (float)Math.Clamp(elapsed / FlashSeconds, 0d, 1d);
}

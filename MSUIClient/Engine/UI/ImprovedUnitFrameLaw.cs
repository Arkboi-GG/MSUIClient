namespace MSUIClient.Engine.UI;

/// <summary>
/// Geometry for the Improved UI unit-frame trio (player/target/target-of-target): flat drawn
/// bars, no authored frame art — the same drawn-primitives approach Player Power Bars already
/// uses in place of PlayerFrame's own bars.
/// </summary>
public static class ImprovedUnitFrameLaw
{
    public const float FrameWidth = 190f;
    public const float FrameHeight = 52f;
    public const float TotFrameWidth = 140f;
    public const float TotFrameHeight = 36f;

    public const float NameRowHeight = 16f;
    public const float HealthBarHeight = 20f;
    public const float PowerBarHeight = 14f;
    public const float BarGap = 2f;

    /// <summary>Clearance above the two bottom multibar rows so the trio never overlaps the
    /// action bars.</summary>
    public const float BottomRise = MultiActionBarUiLaw.BottomRowRise + 10f;

    /// <summary>X offset from screen bottom-center for the player frame; the target frame
    /// mirrors it. The target-of-target frame sits centered (offset 0) between them.</summary>
    public const float SideOffsetX = TotFrameWidth * 0.5f + 20f + FrameWidth * 0.5f;
}

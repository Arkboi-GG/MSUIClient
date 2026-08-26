namespace MSUIClient.Engine.UI;

/// <summary>
/// Proportional gameplay-UI scaling. The player's preference describes the appearance at MSUI's
/// 1600x900 reference window; the live framebuffer scales that appearance with the window.
/// </summary>
public static class InterfaceScaleLaw
{
    public const float Minimum = 0.5f;
    public const float Maximum = 4f;
    public const float ReferenceFramebufferWidth = 1600f;
    public const float ReferenceFramebufferHeight = 900f;

    /// <summary>
    /// Logical width the main menu bar assembly occupies: the 1024 dwarf strip plus 96 of
    /// end-cap overhang on each side (GameLoop.ActionBars.DrawMainMenuBarArt anchors the caps
    /// at -96 and +992, each 128 wide). The HUD is unusable once this does not fit, because
    /// GameplayBarMin centres the strip and everything on the bar is placed relative to it.
    ///
    /// Painterly mode draws a flat 1024 strip with no caps, so its true requirement is smaller.
    /// The ceiling deliberately uses the wider vanilla-art span for both: the cost is a slightly
    /// more conservative cap under Painterly, against a clipped bar if the mode flips at runtime.
    /// </summary>
    public const float MainMenuBarSpanWidth = 1216f;

    public static float Resolve(float preference) =>
        Math.Clamp(preference, Minimum, Maximum);

    /// <summary>
    /// The largest EFFECTIVE scale at which the main menu bar still fits the framebuffer.
    /// Effective scale is what the HUD multiplies its logical units by, so the fit test is simply
    /// span * scale &lt;= width. Height does not enter into it - the bar only ever runs out of room
    /// sideways.
    ///
    /// This is what makes the bar resolution-proof. Because ResolveForFramebuffer follows the
    /// LIMITING dimension, the usable logical width for any aspect at or below 16:9 collapses to
    /// exactly ReferenceFramebufferWidth / preference - the same 889 px at 1600x900, 1920x1080 and
    /// 2560x1440 alike. At the shipped 1.8 preference that is 327 px short of the bar, so the caps
    /// (and, with them, the bag cluster and micro menu) hung off the screen at every 16:9 mode
    /// while a wide enough ultrawide happened to clear it. Reported by a tester, 2026-08-26.
    /// </summary>
    public static float MaximumEffectiveScale(float width) =>
        !float.IsFinite(width) || width <= 0f
            ? Maximum
            : Math.Clamp(width / MainMenuBarSpanWidth, Minimum, Maximum);

    /// <summary>
    /// The same ceiling expressed as a PREFERENCE, for the settings slider: the largest value the
    /// player can choose before ResolveForFramebuffer would start clamping them. Divides the
    /// effective ceiling back through the window ratio, so it reads in the slider's own units.
    /// </summary>
    public static float MaximumPreferenceForFramebuffer(float width, float height)
    {
        if (!float.IsFinite(width) || !float.IsFinite(height) || width <= 0f || height <= 0f)
            return Maximum;

        float windowRatio = MathF.Min(
            width / ReferenceFramebufferWidth,
            height / ReferenceFramebufferHeight);
        if (windowRatio <= 0f) return Maximum;

        return Math.Clamp(MaximumEffectiveScale(width) / windowRatio, Minimum, Maximum);
    }

    /// <summary>
    /// Preserve aspect ratio by following the limiting framebuffer dimension. Thus 1.30x remains
    /// 1.30x at 1600x900 and becomes 2.08x at 2560x1440, with no settings change - then never
    /// exceed the width at which the main menu bar would run off the screen.
    ///
    /// The fit ceiling lives HERE rather than in the stored preference on purpose. Display.UiScale
    /// is also the raw value the ImGui menu font atlas and ImGui's one-shot ScaleAllSizes are baked
    /// from at startup, so lowering the stored default would shrink every Escape/Options glyph as a
    /// side effect. Clamping the derived gameplay scale leaves the menu exactly as it was and keeps
    /// the slider's stored value honest; it only refuses to push the HUD off-screen.
    /// </summary>
    public static float ResolveForFramebuffer(float width, float height, float preference)
    {
        float resolvedPreference = Resolve(preference);
        if (!float.IsFinite(width) || !float.IsFinite(height) || width <= 0f || height <= 0f)
            return resolvedPreference;

        float windowRatio = MathF.Min(
            width / ReferenceFramebufferWidth,
            height / ReferenceFramebufferHeight);
        return Math.Clamp(
            MathF.Min(resolvedPreference * windowRatio, MaximumEffectiveScale(width)),
            Minimum, Maximum);
    }
}

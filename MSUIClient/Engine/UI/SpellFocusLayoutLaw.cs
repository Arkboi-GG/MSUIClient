namespace MSUIClient.Engine.UI;

/// <summary>
/// Geometry law for the Spell Workshop's FOCUS LAYOUT (2026-08-31): the mode where
/// the workshop moves into ONE full-height right sidebar — the spell, its phases and
/// the selected phase's dials, stacked — and stands the bottom deck down, leaving
/// everything left of it clear for watching the spell play.
///
/// It was briefly two panes, one per side. That took more screen than the deck it
/// replaced, which defeated the point, so the whole workshop lives on the right and
/// the world keeps the rest.
///
/// State-free on purpose: the numbers are decidable from the display size, the
/// user's scale dial and the stored fraction alone, so they can be tested without
/// standing a UI up (see tools/interface-wire-check --spell-focus-only).
/// </summary>
public static class SpellFocusLayoutLaw
{
    /// <summary>Narrowest the sidebar may get as a fraction of the display.</summary>
    public const float MinFraction = 0.16f;

    /// <summary>Widest the sidebar may get as a fraction of the display.</summary>
    public const float MaxFraction = 0.40f;

    /// <summary>The out-of-the-box pane width.</summary>
    public const float DefaultFraction = 0.26f;

    /// <summary>The glue canvas height every creator scale derives from. A
    /// deliberate restatement of GameLoop's <c>GlueCanvasH</c>
    /// (GameLoop/Scene/GameLoop.Net.cs): Engine must not reference GameLoop
    /// (CODE_STRUCTURE_LAW section 1), so the twins are kept in step by hand.</summary>
    private const float CanvasH = 768f;

    /// <summary>
    /// The sidebar's width in pixels. The stored fraction is honoured between
    /// <see cref="MinFraction"/> and <see cref="MaxFraction"/>, then bounded twice:
    /// a FLOOR wide enough that a dial row (slider + label + reset knob + help) is
    /// still usable, and a CEILING that guarantees a viewing column survives at any
    /// display size or scale.
    /// </summary>
    public static float SidebarWidth(float displayW, float displayH, float uiScale, float fraction)
    {
        float s = MathF.Max(displayH / CanvasH, 0.5f);
        float floor = 360f * s * uiScale;
        float ceil = MathF.Max(displayW - 360f * s, 1f);
        float want = Math.Clamp(fraction, MinFraction, MaxFraction) * displayW;
        // MathF.Min on the lower bound is the min>max guard: on a 1x1 display (boot,
        // minimize) the floor exceeds the ceiling and Math.Clamp would THROW.
        return Math.Clamp(want, MathF.Min(floor, ceil), ceil);
    }

    /// <summary>
    /// The fractions that actually MOVE the sidebar on this display, once the floor
    /// and ceiling in <see cref="SidebarWidth"/> are accounted for. Outside this
    /// band the stored fraction changes but the width does not - so the drag must
    /// clamp here rather than to the nominal Min/Max, or the grab strip follows the
    /// cursor, rewrites settings.json and moves nothing.
    /// </summary>
    public static (float Lo, float Hi) EffectiveFractionBand(
        float displayW, float displayH, float uiScale)
    {
        if (displayW <= 1f) return (DefaultFraction, DefaultFraction);
        float s = MathF.Max(displayH / CanvasH, 0.5f);
        float floor = 360f * s * uiScale;
        float ceil = MathF.Max(displayW - 360f * s, 1f);
        float lo = MathF.Max(MinFraction, MathF.Min(floor, ceil) / displayW);
        float hi = MathF.Min(MaxFraction, ceil / displayW);
        // A display too small to honour both bounds collapses to the one width it can.
        return lo <= hi ? (lo, hi) : (hi, hi);
    }

    /// <summary>
    /// The fraction the sidebar's left-edge grab lands on: absolute mouse-x to
    /// fraction, so the sidebar cannot chase its own geometry mid-drag. Dragging
    /// left widens it. Clamped to the band that actually moves something.
    /// </summary>
    public static float FractionFromDragX(float displayW, float displayH, float uiScale,
        float mouseX)
    {
        if (displayW <= 1f) return DefaultFraction;
        (float lo, float hi) = EffectiveFractionBand(displayW, displayH, uiScale);
        return Math.Clamp((displayW - mouseX) / displayW, lo, hi);
    }
}

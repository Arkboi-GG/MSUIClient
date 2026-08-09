namespace MSUIClient.Engine.UI;

/// <summary>
/// Frozen UIPanelTemplates tab state and width law. Asset paths, UVs, text faces, and actual draw
/// calls remain surface-owned; this class supplies only the shared state/fit decisions.
/// </summary>
public static class PanelTabLaw
{
    public enum State
    {
        Unselected,
        Selected,
        Disabled,
    }

    public enum LabelPaint
    {
        Normal,
        Highlight,
        Gray,
    }

    public readonly record struct Visual(
        State State,
        bool Enabled,
        bool ShowInactiveSlices,
        bool ShowActiveSlices,
        bool ShowHoverHighlight,
        LabelPaint LabelPaint);

    /// <summary>
    /// isDisabled wins over selected. A disabled-by-policy tab shows inactive slices and gray text;
    /// the selected tab shows active slices but is itself disabled, preventing re-click.
    /// </summary>
    public static Visual Resolve(bool selected, bool isDisabled, bool hovered)
    {
        if (isDisabled)
            return new(State.Disabled, false, true, false, false, LabelPaint.Gray);
        if (selected)
            return new(State.Selected, false, false, true, false, LabelPaint.Highlight);
        return new(State.Unselected, true, true, false, hovered, hovered
            ? LabelPaint.Highlight
            : LabelPaint.Normal);
    }

    public readonly record struct Fit(
        float TabWidth,
        float MiddleWidth,
        float TextBoxWidth,
        bool TextUsesNaturalWidth,
        bool AbsoluteArm,
        bool RoomClamped);

    /// <summary>
    /// PanelTemplates_TabResize(padding, tab, absoluteSize, maxWidth), including the frozen
    /// maxWidth quirk: the comparison includes padding and the capped width adds padding again.
    /// That is executable snapshot behavior and must not be silently normalized.
    /// </summary>
    public static Fit Resize(
        float measuredTextWidth,
        float sideWidth,
        float? padding = null,
        float? absoluteSize = null,
        float? maxWidth = null)
    {
        float sideWidths = Math.Max(0, sideWidth) * 2f;
        if (absoluteSize is { } absolute)
        {
            float middle;
            float tab;
            if (absolute < sideWidths)
            {
                middle = 1f;
                tab = sideWidths;
            }
            else
            {
                middle = absolute - sideWidths;
                tab = absolute;
            }
            return new(tab, middle, middle, false, true, false);
        }

        float pad = padding ?? 24f;
        float width = Math.Max(0, measuredTextWidth) + pad;
        bool capped = maxWidth is { } cap && width > cap;
        if (capped)
            width = maxWidth!.Value + pad;

        return new(
            width + sideWidths,
            width,
            capped ? width : 0f,
            !capped,
            false,
            false);
    }

    /// <summary>
    /// Applies the Benilla structural room guard after ordinary TabResize. Room is measured to the
    /// parent's drawn edge (parentRight-rightInset), not necessarily the parent frame's raw edge.
    /// The clamp re-enters TabResize through its absolute-size arm to avoid maxWidth's padding quirk.
    /// </summary>
    public static Fit FitWithinParent(
        float measuredTextWidth,
        float sideWidth,
        float tabLeft,
        float parentRight,
        float rightInset = 0,
        float? padding = null,
        float? maxWidth = null)
    {
        Fit natural = Resize(measuredTextWidth, sideWidth, padding, null, maxWidth);
        float room = Room(tabLeft, parentRight, rightInset);
        if (natural.TabWidth <= room)
            return natural;
        Fit clamped = Resize(measuredTextWidth, sideWidth, padding, room, maxWidth);
        return clamped with { RoomClamped = true };
    }

    public static float Room(float tabLeft, float parentRight, float rightInset = 0) =>
        Math.Max(1f, (parentRight - rightInset) - tabLeft);

    /// <summary>
    /// Benilla's asynchronous text-measure settle uses exact equality and skips non-positive
    /// measures. Adapters may call this every frame without changing a settled tab.
    /// </summary>
    public static bool NeedsSettle(
        float measuredTextWidth,
        float? room,
        float? previousMeasuredWidth,
        float? previousRoom) =>
        measuredTextWidth > 0 &&
        (previousMeasuredWidth != measuredTextWidth || previousRoom != room);
}

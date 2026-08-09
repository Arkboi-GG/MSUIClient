using System.Globalization;

namespace MSUIClient.Engine.UI;

/// <summary>
/// Pure build-5875 Button/CheckButton interaction law. It is intentionally independent of ImGui:
/// adapters provide press ownership, hover, and held state, then keep their existing art/actions.
/// </summary>
public static class ButtonInteractionLaw
{
    public enum Edge
    {
        Down,
        Up,
    }

    public static IReadOnlySet<string> DefaultRegisteredClicks { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "LeftButtonUp" };

    public static IReadOnlySet<string> RegisterForClicks(IEnumerable<string> transitions) =>
        new HashSet<string>(transitions, StringComparer.OrdinalIgnoreCase);

    public static string TransitionName(string button, Edge edge) =>
        button + (edge == Edge.Down ? "Down" : "Up");

    public static bool WantsClick(
        IReadOnlySet<string>? registeredClicks,
        string button,
        Edge edge) =>
        (registeredClicks ?? DefaultRegisteredClicks).Contains(TransitionName(button, edge));

    public enum TextureSlot
    {
        None,
        Normal,
        Pushed,
        Disabled,
    }

    public enum LabelState
    {
        Normal,
        Highlight,
        Disabled,
    }

    public readonly record struct Visual(
        TextureSlot PrimaryTexture,
        bool HighlightVisible,
        bool CheckedVisible,
        bool DisabledCheckedVisible,
        LabelState LabelState,
        bool Pushed);

    /// <summary>
    /// Frozen region_visible: disabled owns the primary state; otherwise pushed is
    /// (held AND hovered) OR scripted PUSHED. Missing pushed art falls back to normal, while a
    /// missing disabled texture draws no primary texture. Locked highlight affects only highlight
    /// art; label font selection still follows actual hover.
    /// </summary>
    public static Visual ResolveVisual(
        bool enabled,
        bool hovered,
        bool held,
        bool scriptedPushed,
        bool isChecked,
        bool lockedHighlight,
        bool hasNormalTexture = true,
        bool hasPushedTexture = true,
        bool hasDisabledTexture = true,
        bool hasDisabledCheckedTexture = true)
    {
        bool pushed = enabled && ((held && hovered) || scriptedPushed);
        TextureSlot primary;
        if (!enabled)
            primary = hasDisabledTexture ? TextureSlot.Disabled : TextureSlot.None;
        else if (pushed)
            primary = hasPushedTexture ? TextureSlot.Pushed :
                hasNormalTexture ? TextureSlot.Normal : TextureSlot.None;
        else
            primary = hasNormalTexture ? TextureSlot.Normal : TextureSlot.None;

        bool disabledChecked = isChecked && !enabled && hasDisabledCheckedTexture;
        bool checkedVisible = isChecked && (enabled || !hasDisabledCheckedTexture);
        return new(
            primary,
            enabled && (hovered || lockedHighlight),
            checkedVisible,
            disabledChecked,
            !enabled ? LabelState.Disabled : hovered ? LabelState.Highlight : LabelState.Normal,
            pushed);
    }

    public readonly record struct ClickPlan(
        bool FireOnClick,
        bool ToggleCheckedBeforeCallback,
        bool CheckedAfter,
        bool DownArgument);

    /// <summary>
    /// Resolves one physical transition. Release requires the same control to own the originating
    /// press and the pointer to be inside at release. A registered ButtonDown fires on the inside
    /// press itself. CheckButton toggles before OnClick, so the callback observes CheckedAfter.
    /// </summary>
    public static ClickPlan PhysicalTransition(
        bool enabled,
        IReadOnlySet<string>? registeredClicks,
        string button,
        Edge edge,
        bool ownsOriginatingPress,
        bool pointerInside,
        bool isCheckButton,
        bool checkedBefore)
    {
        bool ownsTransition = edge == Edge.Down
            ? pointerInside
            : ownsOriginatingPress && pointerInside;
        bool fire = enabled && ownsTransition &&
            WantsClick(registeredClicks, button, edge);
        bool toggle = fire && isCheckButton;
        return new(fire, toggle, toggle ? !checkedBefore : checkedBefore, edge == Edge.Down);
    }

    /// <summary>
    /// Button:Click bypasses RegisterForClicks and emulates a completed release. The enabled gate
    /// and CheckButton toggle-before-callback ordering still apply.
    /// </summary>
    public static ClickPlan ProgrammaticClick(
        bool enabled,
        bool isCheckButton,
        bool checkedBefore)
    {
        bool toggle = enabled && isCheckButton;
        return new(enabled, toggle, toggle ? !checkedBefore : checkedBefore, false);
    }

    /// <summary>
    /// Exact frozen SetChecked coercion for the script-facing internal-support arm. Numbers are
    /// truncated toward zero; recognized "true"/numeric strings are honored; all other values are
    /// false. Native MSUI adapters normally pass a bool and need not expose this API.
    /// </summary>
    public static bool CoerceChecked(object? value) => value switch
    {
        bool b => b,
        byte n => n != 0,
        sbyte n => n != 0,
        short n => n != 0,
        ushort n => n != 0,
        int n => n != 0,
        uint n => n != 0,
        long n => n != 0,
        ulong n => n != 0,
        float n => MathF.Truncate(n) != 0,
        double n => Math.Truncate(n) != 0,
        decimal n => decimal.Truncate(n) != 0,
        string text => CoerceCheckedString(text),
        _ => false,
    };

    private static bool CoerceCheckedString(string text)
    {
        string trimmed = text.Trim();
        if (trimmed.Equals("true", StringComparison.OrdinalIgnoreCase))
            return true;
        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture,
            out double number) && Math.Truncate(number) != 0;
    }
}

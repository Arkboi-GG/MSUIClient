using System.Globalization;
using System.Numerics;

namespace MSUIClient.Engine.UI;

/// <summary>How the value on a bar is written, or whether it is written at all.</summary>
public enum PowerBarText
{
    None,
    ValueOverMax,
    Percent,
}

/// <summary>Where each piece of the stack sits, in logical UI units relative to the
/// frame origin. Everything is derived once so the draw pass, the drag handle and the
/// clinical check all agree about the geometry.</summary>
public readonly record struct PlayerPowerBarsLayout(
    Vector2 Size, Vector2 HealthMin, Vector2 HealthSize,
    Vector2 PowerMin, Vector2 PowerSize, Vector2 ComboMin, Vector2 ComboSize);

/// <summary>
/// Player health and power as a movable, resizable pair of bars. Ported from the
/// MSUI_PowerBars 1.12 addon.
///
/// The addon's defining constraint does not survive the port. Its header records that on
/// this client "UnitRage/UnitEnergy/UnitFocus don't exist here", so it read every power
/// type through <c>UnitMana</c> and recoloured by <c>UnitPowerType</c>. That was a Lua API
/// limitation, not a fact about the game: the native client reads UNIT_FIELD_POWER1..7
/// straight off the object fields, so <c>Fields.ActivePower</c> / <c>ActiveMaxPower</c> is
/// the real per-type value for every class, and no workaround is needed.
///
/// The addon's 5/sec polling loop is gone for the same reason. It existed because "this
/// server's events have already proven unreliable"; the native client holds live entity
/// fields updated from UPDATE_OBJECT, so the bars read the truth every frame for free.
///
/// What DOES survive is the energy tick sweep, because its cause is server-side and real:
/// energy arrives in lumps on a hardcoded 2.0s regen tick (VMaNGOS Player::RegenerateAll,
/// Player.cpp:2318), and no packet announces the tick. It still has to be inferred from the
/// value jumping upward — but natively that is observed on the field change itself rather
/// than sampled five times a second.
/// </summary>
public static class PlayerPowerBarsLaw
{
    public const float MinimumWidth = 60f;
    public const float MaximumWidth = 600f;
    public const float MinimumBarHeight = 6f;
    public const float MaximumBarHeight = 40f;
    public const float MinimumScale = .5f;
    public const float MaximumScale = 2f;
    public const float MinimumSpacing = 0f;
    public const float MaximumSpacing = 12f;

    /// <summary>The server's regen tick. Hardcoded 2.0s in Player::RegenerateAll
    /// (Player.cpp:2318-2336) — confirmed from the server's own source, not assumed from
    /// vanilla-in-general knowledge. Exposed as a setting anyway because a fork can change
    /// it and a wrong sweep period is worse than none.</summary>
    public const float DefaultTickSeconds = 2f;
    public const float MinimumTickSeconds = .5f;
    public const float MaximumTickSeconds = 5f;

    /// <summary>Energy. The tick sweep is meaningful for this power type only.</summary>
    public const byte EnergyPowerType = 3;

    public const float ComboPipSize = 14f;
    public const float ComboPipGap = 4f;
    public const float ComboLift = 6f;

    /// <summary>Width of the sweeping tick cursor, in logical units.</summary>
    public const float TickCursorWidth = 2f;

    public static float ClampWidth(float value) => Math.Clamp(value, MinimumWidth, MaximumWidth);

    public static float ClampBarHeight(float value) =>
        Math.Clamp(value, MinimumBarHeight, MaximumBarHeight);

    public static float ClampScale(float value) => Math.Clamp(value, MinimumScale, MaximumScale);

    public static float ClampSpacing(float value) =>
        Math.Clamp(value, MinimumSpacing, MaximumSpacing);

    public static float ClampTickSeconds(float value) =>
        Math.Clamp(value, MinimumTickSeconds, MaximumTickSeconds);

    /// <summary>
    /// The stack: health on top, power below, combo pips lifted above health. Widths and
    /// heights arrive already clamped so a hand-edited settings.json cannot produce a
    /// zero-height bar or a frame wider than the screen.
    /// </summary>
    public static PlayerPowerBarsLayout Layout(float width, float healthHeight,
        float powerHeight, float spacing, int comboPips)
    {
        width = ClampWidth(width);
        healthHeight = ClampBarHeight(healthHeight);
        powerHeight = ClampBarHeight(powerHeight);
        spacing = ClampSpacing(spacing);

        Vector2 healthMin = new(0f, 0f);
        Vector2 healthSize = new(width, healthHeight);
        Vector2 powerMin = new(0f, healthHeight + spacing);
        Vector2 powerSize = new(width, powerHeight);

        float comboWidth = comboPips > 0
            ? comboPips * ComboPipSize + Math.Max(0, comboPips - 1) * ComboPipGap
            : 0f;
        Vector2 comboSize = new(comboWidth, comboPips > 0 ? ComboPipSize : 0f);
        // Centred over the health bar and lifted clear of it, matching the addon.
        Vector2 comboMin = new((width - comboWidth) * .5f, -(ComboPipSize + ComboLift));

        return new(new Vector2(width, healthHeight + spacing + powerHeight),
            healthMin, healthSize, powerMin, powerSize, comboMin, comboSize);
    }

    /// <summary>Rect of one combo pip, relative to the combo row's own origin.</summary>
    public static Vector2 ComboPipMin(int index) =>
        new(index * (ComboPipSize + ComboPipGap), 0f);

    /// <summary>
    /// How far through the current tick period we are, 0..1, for the sweeping cursor.
    /// Returns null when there is nothing to sweep: no tick seen yet, or the power type
    /// is not Energy. Wrapping rather than clamping keeps the cursor sweeping across
    /// consecutive periods when a tick is missed (at full energy no value change occurs,
    /// so no new tick can be observed and the sweep must not freeze at the right edge).
    /// </summary>
    public static float? TickSweep(bool enabled, byte powerType, double now,
        double? lastTickAt, float tickSeconds)
    {
        if (!enabled || powerType != EnergyPowerType || lastTickAt is not { } stamp) return null;
        float interval = ClampTickSeconds(tickSeconds);
        double elapsed = now - stamp;
        if (elapsed < 0) return null;
        double wrapped = elapsed - Math.Floor(elapsed / interval) * interval;
        return (float)Math.Clamp(wrapped / interval, 0d, 1d);
    }

    /// <summary>
    /// Whether an observed power change is a regen tick. Only an upward move counts —
    /// spending energy is not a tick, and the value going down must not restart the sweep.
    /// </summary>
    public static bool IsRegenTick(byte powerType, uint previous, uint current) =>
        powerType == EnergyPowerType && current > previous;

    /// <summary>The caption on a bar. Max is floored at 1 so a not-yet-populated entity
    /// cannot divide by zero on the frame it appears.</summary>
    public static string Caption(PowerBarText mode, uint value, uint max)
    {
        if (mode == PowerBarText.None) return "";
        uint safeMax = Math.Max(1u, max);
        return mode == PowerBarText.Percent
            ? string.Format(CultureInfo.InvariantCulture, "{0:0}%", 100f * value / safeMax)
            : string.Format(CultureInfo.InvariantCulture, "{0} / {1}", value, max);
    }

    public static PowerBarText TextMode(bool showText, bool showPercent) =>
        !showText ? PowerBarText.None
            : showPercent ? PowerBarText.Percent : PowerBarText.ValueOverMax;
}

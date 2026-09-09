using System.Numerics;

namespace MSUIClient.Engine.UI;

public enum CombatTextStateTone { Red, Green }

public readonly record struct CombatTextStateCue(string Text, CombatTextStateTone Tone);
public readonly record struct CombatTextResourceTransition(bool Latched, bool Warn);
public readonly record struct CenterCombatTextRow(float Offset, bool Critical);

/// <summary>Default build-5875 Blizzard_CombatText state/aura/resource message law.</summary>
public static class CombatTextStateUiLaw
{
    public const float LowResourceThreshold = .2f;
    public const bool ShowAuraGainsByDefault = true;
    public const bool ShowAuraFadesByDefault = false;
    public const bool ShowCombatStateByDefault = true;
    public const bool ShowLowResourcesByDefault = true;
    public const float WorldLaneNear = .30f;
    public const float WorldLaneFar = .65f;
    public const float WorldLaneBase = .35f;
    public const float WorldShadowDisplayFraction = .002f;
    public const float CenterLanePitch = 18f;
    public const float CenterBottomOffset = 110f;
    public const float CenterRise = 225f;
    public const float CenterLifetime = 1.9f;
    public const float CenterShadowOffset = 2f;
    public const float CenterMessageSpacing = 26f; // FrameXML: 16 + COMBAT_TEXT_SPACING(10).
    public const float CenterMaxOffset = 130f;
    public const float CenterCriticalSpacing = 61f; // Reserve the authored 60px peak height.

    public static float CenterMessageOffset(float startOffset, float age, bool critical) =>
        startOffset - (critical ? 0f : age / CenterLifetime * CenterRise);

    public static float NextCenterStartOffset(IEnumerable<float> currentOffsets)
    {
        float offset = 0f;
        foreach (float current in currentOffsets)
            offset = Math.Max(offset, current + CenterMessageSpacing);
        // Blizzard_CombatText resets to its normal origin once the stack exceeds this limit.
        return offset > CenterMaxOffset ? 0f : offset;
    }

    public static bool CenterRowsOverlap(float firstOffset, float secondOffset) =>
        MathF.Abs(firstOffset - secondOffset) + .001f < CenterMessageSpacing;

    public static float NextCenterStartOffset(IReadOnlyList<CenterCombatTextRow> rows, bool critical)
    {
        if (!critical && rows.Any(row => row.Critical))
        {
            // Scrolling text must start above every stationary critical, otherwise
            // it crosses the critical a few frames after an initially clear insertion.
            float ceiling = rows.Where(row => row.Critical).Min(row => row.Offset);
            float start = ceiling - CenterMessageSpacing;
            while (rows.Any(row => CenterRowsOverlap(start, row.Offset)))
            {
                start -= CenterMessageSpacing;
                if (start < ceiling - CenterMaxOffset) return ceiling - CenterMessageSpacing;
            }
            return start;
        }
        float next = 0;
        foreach (CenterCombatTextRow row in rows)
            next = Math.Max(next, row.Offset + (row.Critical ? CenterCriticalSpacing : CenterMessageSpacing));
        return next > CenterMaxOffset ? 0 : next;
    }

    public static bool CenterInsertionConflicts(float start, bool critical, CenterCombatTextRow older)
    {
        // A critical overflow reset must also retire scrolling rows below it:
        // those rows would pass through its reserved area during their lifetime.
        if (critical && !older.Critical) return older.Offset + CenterMessageSpacing > start + .001f;
        float height = critical ? CenterCriticalSpacing : CenterMessageSpacing;
        float olderHeight = older.Critical ? CenterCriticalSpacing : CenterMessageSpacing;
        return start + .001f < older.Offset + olderHeight && older.Offset + .001f < start + height;
    }

    public static Vector2 WorldTextPosition(Vector2 projectedPoint, float scaledWidth,
        float textSize, int lane, float normalizedAge)
    {
        float direction = lane switch
        {
            0 => -WorldLaneNear,
            1 => WorldLaneNear,
            2 => -WorldLaneFar,
            _ => WorldLaneFar,
        };
        return new(projectedPoint.X - scaledWidth * .5f +
            direction * textSize * (WorldLaneBase + normalizedAge),
            projectedPoint.Y - textSize);
    }

    public static Vector2 WorldShadow(Vector2 display) =>
        display * WorldShadowDisplayFraction;

    public static Vector2 CenterTextPosition(Vector2 display, float uiScale,
        float measuredWidth, int lane, float age, bool critical, float startOffset = 0f)
    {
        float laneX = (lane - 2) * CenterLanePitch * uiScale;
        return new(display.X * .5f + laneX - measuredWidth * .5f,
            display.Y * .5f + (CenterBottomOffset +
                CenterMessageOffset(startOffset, age, critical)) * uiScale);
    }

    public static Vector2 CenterShadow(float uiScale) =>
        new Vector2(CenterShadowOffset) * uiScale;

    public static CombatTextResourceTransition Resource(
        bool latched, uint current, uint maximum, bool eligible = true)
    {
        bool low = eligible && maximum > 0 && current / (double)maximum <= LowResourceThreshold;
        if (!low) return new(false, false);
        return new(true, !latched);
    }

    public static CombatTextStateCue? CombatState(bool? previous, bool current)
    {
        if (previous is null || previous.Value == current || !ShowCombatStateByDefault) return null;
        return new(current ? "Entering Combat" : "Leaving Combat", CombatTextStateTone.Red);
    }

    public static CombatTextStateCue? Aura(
        string spellName, bool helpful, bool applied, bool showFades = ShowAuraFadesByDefault,
        bool hidden = false)
    {
        if (hidden || spellName.Length == 0) return null;
        if (applied && ShowAuraGainsByDefault)
            return new(spellName, helpful ? CombatTextStateTone.Green : CombatTextStateTone.Red);
        if (!applied && showFades)
            return new($"<{spellName}> fades",
                helpful ? CombatTextStateTone.Green : CombatTextStateTone.Red);
        return null;
    }
}

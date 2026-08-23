using System.Numerics;

namespace MSUIClient.Engine.UI;

public enum CombatTextStateTone { Red, Green }

public readonly record struct CombatTextStateCue(string Text, CombatTextStateTone Tone);
public readonly record struct CombatTextResourceTransition(bool Latched, bool Warn);

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
        float measuredWidth, int lane, float age, bool critical)
    {
        float laneX = (lane - 2) * CenterLanePitch * uiScale;
        float rise = critical ? 0f : age / CenterLifetime * CenterRise * uiScale;
        return new(display.X * .5f + laneX - measuredWidth * .5f,
            display.Y * .5f + CenterBottomOffset * uiScale - rise);
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
        string spellName, bool helpful, bool applied, bool showFades = ShowAuraFadesByDefault)
    {
        if (spellName.Length == 0) return null;
        if (applied && ShowAuraGainsByDefault)
            return new(spellName, helpful ? CombatTextStateTone.Green : CombatTextStateTone.Red);
        if (!applied && showFades)
            return new($"<{spellName}> fades",
                helpful ? CombatTextStateTone.Green : CombatTextStateTone.Red);
        return null;
    }
}

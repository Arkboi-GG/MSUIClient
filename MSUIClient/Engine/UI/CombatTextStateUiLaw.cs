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

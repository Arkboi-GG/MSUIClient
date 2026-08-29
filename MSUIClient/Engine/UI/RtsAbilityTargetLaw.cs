namespace MSUIClient.Engine.UI;

public enum RtsAbilityCastIntent
{
    Normal,
    ChooseFriendlyTarget,
    CastOnPrimary,
}

/// <summary>Modifier and selection-size grammar for a primary command-card ability.</summary>
public static class RtsAbilityTargetLaw
{
    public static RtsAbilityCastIntent Resolve(int selectedCount, bool altHeld,
        bool acceptsExplicitFriendlyUnit)
    {
        if (!acceptsExplicitFriendlyUnit) return RtsAbilityCastIntent.Normal;
        if (altHeld) return RtsAbilityCastIntent.CastOnPrimary;
        return selectedCount > 1
            ? RtsAbilityCastIntent.ChooseFriendlyTarget
            : RtsAbilityCastIntent.Normal;
    }
}

namespace MSUIClient.Engine.UI;

public enum ProfessionPanelKind
{
    TradeSkill,
    Craft,
}

/// <summary>
/// Exact build-5875 provenance retained from a profession opener. An effect-47 spell is always a
/// client-local opener, even when its panel kind cannot be resolved from the available DBC row.
/// </summary>
public readonly record struct ProfessionPanelOpenerProvenance(
    bool IsProfessionOpener,
    ProfessionPanelKind? PanelKind);

/// <summary>
/// Pure profession-panel fork. Only the opener's first effect lane participates: effect 47 marks
/// the client-local opener and its signed EffectMiscValue[0] selects TradeSkill (zero) or Craft
/// (nonzero). Missing misc provenance remains unresolved rather than being guessed from skill line.
/// </summary>
public static class ProfessionPanelOpenerLaw
{
    public const uint TradeSkillEffect = 47;

    public static ProfessionPanelOpenerProvenance Resolve(
        IReadOnlyList<uint>? effectIds,
        IReadOnlyList<int>? effectMiscValues)
    {
        if (effectIds is not { Count: > 0 } || effectIds[0] != TradeSkillEffect)
            return new(false, null);
        if (effectMiscValues is not { Count: > 0 })
            return new(true, null);

        ProfessionPanelKind kind = effectMiscValues[0] == 0
            ? ProfessionPanelKind.TradeSkill
            : ProfessionPanelKind.Craft;
        return new(true, kind);
    }
}

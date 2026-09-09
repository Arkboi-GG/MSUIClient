using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient.Engine.UI;

public static class SpellReactiveLaw
{
    public static bool HasState(uint states, uint required) =>
        required == 0 || required <= 32 && (states & (1U << (int)(required - 1))) != 0;

    public static CastTargetReason? Refusal(in SpellInfo spell, WorldEntity actor,
        WorldEntity? target, bool checkTarget = true)
    {
        if (!HasState(actor.Fields.AuraState, spell.CasterAuraState)) return CastTargetReason.CasterAuraState;
        if (!checkTarget) return null;
        // The inspected 1.12 Core explicitly checks target state 2 (Execute).
        // Other target states are retained without inventing generic server gates.
        if (spell.TargetAuraState == 2 && (target is null || !HasState(target.Fields.AuraState, 2)))
            return CastTargetReason.TargetAuraState;
        uint implicitTarget = spell.ImplicitTargetsA?.FirstOrDefault() ?? spell.ImplicitTarget;
        bool usesSelectedComboTarget = (spell.AttributesEx & 0x00500000) != 0 &&
            implicitTarget is 6 or 21 or 25 or 35 or 45 or 53 or 57 or 61;
        if (actor.IsPlayer && usesSelectedComboTarget &&
            (target is null || actor.Fields.PlayerComboTarget != target.Guid || actor.Fields.PlayerComboPoints == 0))
            return CastTargetReason.ComboTarget;
        return null;
    }
}

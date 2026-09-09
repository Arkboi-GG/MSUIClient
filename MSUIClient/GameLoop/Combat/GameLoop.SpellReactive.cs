using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private bool RefuseSpellReactive(in SpellInfo spell, ulong targetGuid, bool checkTarget = true)
    {
        if (!_entities.TryGet(ControlledGuid, out WorldEntity actor)) return false;
        WorldEntity? target = _entities.TryGet(targetGuid, out WorldEntity found) && (found.IsPlayer || found.IsCreature) ? found : null;
        if (SpellReactiveLaw.Refusal(spell, actor, target, checkTarget) is not { } reason) return false;
        string text = reason == CastTargetReason.CasterAuraState
            ? InventoryGlobalString("SPELL_FAILED_CASTER_AURASTATE", "You can't do that yet")
            : reason == CastTargetReason.ComboTarget && actor.Fields.Bytes0.Class != 1
                ? InventoryGlobalString("SPELL_FAILED_NO_COMBO_POINTS", "That ability requires combo points")
                : InventoryGlobalString("SPELL_FAILED_BAD_TARGETS", "Invalid target");
        EmitCastVerdict(spell.Id, reason, targetGuid, sent: false);
        RefuseCast(spell.Id, $"LOCAL_{reason}", text);
        return true;
    }
}

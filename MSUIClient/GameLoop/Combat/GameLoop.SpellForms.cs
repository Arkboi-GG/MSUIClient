using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private SpellFormRestriction SpellFormRestrictionFor(in SpellInfo spell, WorldEntity actor)
    {
        byte form = actor.Fields.ShapeshiftForm;
        ShapeshiftFormInfo? info = _shapeshiftForms?.TryGet(form, out var known) == true ? known : null;
        bool talentLearning = spell.EffectIds?.Contains(36u) == true && _talents?.IsRankSpell(spell.Id) == true;
        return SpellFormLaw.Check(spell, form, info, talentLearning);
    }

    private bool RefuseSpellForm(in SpellInfo spell)
    {
        if (!_entities.TryGet(ControlledGuid, out WorldEntity actor)) return false;
        SpellFormRestriction restriction = SpellFormRestrictionFor(spell, actor);
        if (restriction == SpellFormRestriction.None) return false;
        string text;
        if (restriction == SpellFormRestriction.ForbiddenForm)
            text = InventoryGlobalString("SPELL_FAILED_NOT_SHAPESHIFT", "You are in shapeshift form");
        else
        {
            var names = new List<string>();
            for (uint form = 1; form <= 32; form++)
                if ((spell.Stances & (1u << (int)(form - 1))) != 0 &&
                    _shapeshiftForms?.TryGet(form, out var required) == true)
                    names.Add(required.Name);
            string requiredNames = names.Count > 0 ? string.Join(", ", names) : "the required form";
            text = InventoryGlobalString("SPELL_FAILED_ONLY_SHAPESHIFT", "Must be in %s").Replace("%s", requiredNames);
        }
        EmitCastVerdict(spell.Id, CastTargetReason.WrongForm, 0, sent: false);
        RefuseCast(spell.Id, "LOCAL_SHAPESHIFT", text);
        return true;
    }
}

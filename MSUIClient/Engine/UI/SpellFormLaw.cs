using MSUIClient.Formats;

namespace MSUIClient.Engine.UI;

public enum SpellFormRestriction { None, ForbiddenForm, RequiresForm }

/// <summary>SpellEntry::GetErrorAtShapeshiftedCast, for an ordinary client-authored cast.</summary>
public static class SpellFormLaw
{
    public static SpellFormRestriction Check(in SpellInfo spell, byte form,
        ShapeshiftFormInfo? formInfo, bool talentLearningSpell = false)
    {
        if (talentLearningSpell) return SpellFormRestriction.None;
        uint mask = form is > 0 and <= 32 ? 1u << (form - 1) : 0;
        if ((mask & spell.StancesNot) != 0) return SpellFormRestriction.ForbiddenForm;
        if ((mask & spell.Stances) != 0) return SpellFormRestriction.None;
        // An unknown form is also allowed by the core here. Do not invent restrictions
        // for a custom form whose catalog entry is unavailable to this client.
        if (form > 0 && formInfo is null) return SpellFormRestriction.None;
        bool shifted = form > 0 && (formInfo!.Value.Flags & 1) == 0;
        if (shifted && (spell.Attributes & 0x10000) != 0) return SpellFormRestriction.ForbiddenForm;
        if (spell.Stances != 0 && (shifted || (spell.AttributesEx2 & 0x80000) == 0))
            return SpellFormRestriction.RequiresForm;
        return SpellFormRestriction.None;
    }
}

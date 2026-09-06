using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient.Engine.UI;

/// <summary>Local resource availability shared by cast submission and action-button coloring.</summary>
public static class SpellResourceLaw
{
    public const uint HealthPower = unchecked((uint)-2);

    public static uint Rank(in SpellInfo spell, WorldEntity actor, uint skillLine)
    {
        // The inspected Core uses GetLevel (not level*5) for non-player Unit::GetSpellRank.
        uint rank = actor.IsPlayer ? skillLine == 0 ? 0 : actor.Fields.PlayerSkillValueWithBonuses(skillLine)
            : actor.Fields.Level;
        return spell.MaxLevel > 0 ? (uint)Math.Min(rank, (ulong)spell.MaxLevel * 5) : rank;
    }

    public static bool CanPay(in SpellInfo spell, ObjectFields fields, out uint available, out uint cost,
        uint spellRank = 0, SpellModifierTotals modifiers = default)
    {
        bool health = spell.PowerType == HealthPower;
        available = health ? fields.Health : fields.Power((byte)spell.PowerType);
        cost = spell.ManaCost;
        if (!health && spell.PowerType >= 5) return false;
        if (spell.UsesAllPower)
        {
            cost = available;
            // Core CalculatePowerCost returns before ordinary cost modifiers for this flag.
            return !health; // CheckPower still refuses consuming the final health point.
        }
        uint basis = health ? fields.BaseHealth : spell.PowerType == 0
            ? fields.BaseMana : fields.MaxPower((byte)spell.PowerType);
        long amount = spell.ManaCost + (long)spell.ManaCostPerLevel * ((long)spellRank / 5 - spell.BaseLevel);
        amount += (long)basis * spell.ManaCostPercent / 100;
        amount += fields.PowerCostModifier((int)spell.School);
        amount = modifiers.ApplyInteger(amount);
        // Core truncates the creature-level division before applying the school percentage.
        if ((spell.Attributes & 0x00080000) != 0 && fields.Level > 0)
        {
            float divisor = 1.117f * spell.SpellLevel / fields.Level - 0.1327f;
            float scaled = amount / divisor;
            if (float.IsFinite(scaled)) amount = (long)Math.Clamp(scaled, int.MinValue, int.MaxValue);
        }
        float adjusted = amount * (1.0f + fields.PowerCostMultiplier((int)spell.School));
        cost = (uint)Math.Clamp(adjusted, 0, uint.MaxValue);
        // The core refuses a health cost that would consume the caster's last health point.
        return health ? available > cost : available >= cost;
    }
}

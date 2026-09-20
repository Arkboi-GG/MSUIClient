using MSUIClient.World.Encounters;

namespace MSUIClient.Engine.UI;

/// <summary>Classifies learned abilities from actual effect data, independent of the boss executor.</summary>
public static class CommanderRaidCapabilityLaw
{
    public static CommanderRaidMember Resolve(CommanderRaidMember member,
        IEnumerable<CommanderRaidSpellFact> facts, uint immuneSchools)
    {
        var spells = facts.Where(s => member.Spells.Contains(s.Id) && !s.Passive).ToArray();
        bool Has(CommanderRaidSpellFact s, uint effect) => s.Effects.Contains(effect);
        uint Best(IEnumerable<CommanderRaidSpellFact> candidates) => candidates
            .OrderByDescending(s => s.Level).ThenBy(s => Math.Max(0, s.CastTimeMs))
            .ThenBy(s => s.ManaCost).ThenByDescending(s => s.Id).Select(s => s.Id).FirstOrDefault();
        uint heal = Best(spells.Where(s => s.Targets.Contains(21u) && (Has(s, 10) || Has(s, 77) && s.Name is "Holy Light" or "Flash of Light")));
        uint damage = Best(spells.Where(s => (s.Targets.Contains(6u) || s.AutoRepeat) &&
            (Has(s, 2) || s.AutoRepeat) && (s.MaxRange >= 20 || s.AutoRepeat) && !Has(s, 68) && s.School < 7 &&
            (immuneSchools & (1u << (int)s.School)) == 0 && s.RecoveryMs <= 6000));
        uint interrupt = spells.Where(s => Has(s, 68)).OrderBy(s => s.RecoveryMs).ThenByDescending(s => s.Level)
            .Select(s => s.Id).FirstOrDefault();
        uint dispel = spells.Where(s => Has(s, 38) && s.Targets.Any(t => t is 21 or 25 or 45 or 57))
            .OrderByDescending(s => s.Effects.Count(e => e == 38)).ThenByDescending(s => s.Level)
            .Select(s => s.Id).FirstOrDefault();
        uint taunt = Best(spells.Where(s => Has(s, 114)));
        if (taunt == 0) taunt = Best(spells.Where(s => s.Auras.Contains(11u)));
        uint defensive = Best(spells.Where(s => s.Targets.Contains(1u) && s.Auras.Select((a, i) => (a, i)).Any(e =>
                e.a == 87 && e.i < s.BasePoints.Length && s.BasePoints[e.i] < 0)));
        bool bear = spells.Any(s => s.Name is "Bear Form" or "Dire Bear Form");
        return member with
        {
            PreferredHeal = heal, PreferredDamage = damage, InterruptSpell = interrupt,
            DispelSpell = dispel, TauntSpell = taunt, DefensiveSpell = defensive,
            CanTank = member.ClassId is 1 or 2 || member.ClassId == 11 && bear,
        };
    }
    public static double TankScore(CommanderRaidMember m) =>
        (m.TauntSpell != 0 ? 3000 : 0) + m.MaxHealth + m.Armor * .6 + m.Level * 20;
    public static double HealingScore(CommanderRaidMember m) => m.Level * 100 + m.MaxHealth * .05;
}

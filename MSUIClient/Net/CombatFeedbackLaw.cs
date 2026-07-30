namespace MSUIClient.Net;

public enum WorldCombatTextStyle
{
    PlayerMelee,
    PlayerSpell,
    Experience,
}

public readonly record struct WorldCombatTextCue(
    ulong Target,
    string Text,
    WorldCombatTextStyle Style,
    bool Critical = false);

public enum CenterCombatTextStyle { Damage, Heal, Power }
public readonly record struct CenterCombatTextCue(string Text, CenterCombatTextStyle Style, bool Critical = false);

/// <summary>
/// Pure vanilla world-combat-text law. Only the local player's outgoing damage
/// floats over units; incoming and unrelated combat are deliberately suppressed.
/// </summary>
public static class CombatFeedbackLaw
{
    public static List<WorldCombatTextCue> WorldText(CombatEvent combatEvent, ulong playerGuid)
    {
        var cues = new List<WorldCombatTextCue>();
        switch (combatEvent)
        {
            case CombatMeleeSwing swing when swing.Attacker == playerGuid && swing.Victim != playerGuid:
                cues.Add(new(swing.Victim, MeleeText(swing), WorldCombatTextStyle.PlayerMelee,
                    swing.Damage > 0 && (swing.HitInfo & 0x80u) != 0));
                break;

            case CombatSpellDamage spell when spell.Attacker == playerGuid && spell.Target != playerGuid:
                cues.Add(new(spell.Target, SpellText(spell), WorldCombatTextStyle.PlayerSpell,
                    spell.Damage > 0 && (spell.HitInfo & 0x2u) != 0));
                break;

            case CombatPeriodicAura aura when aura.Caster == playerGuid && aura.Target != playerGuid:
                foreach (CombatPeriodicTick tick in aura.Ticks)
                    if (tick.Kind == CombatPeriodicKind.Damage)
                        cues.Add(new(aura.Target, tick.Amount > 0 ? tick.Amount.ToString() :
                            tick.Absorb > 0 ? "Absorb" : tick.Resist != 0 ? "Resist" : "Miss",
                            WorldCombatTextStyle.PlayerSpell));
                break;

            // The shield bearer is the source; Attacker is who receives the reflected damage.
            case CombatDamageShield shield when shield.Victim == playerGuid && shield.Attacker != playerGuid:
                cues.Add(new(shield.Attacker, shield.Damage.ToString(), WorldCombatTextStyle.PlayerSpell));
                break;

            case CombatSpellMiss miss when miss.Caster == playerGuid:
                foreach (CombatMiss target in miss.Misses)
                    if (target.Target != playerGuid)
                        cues.Add(new(target.Target, MissWord(target.MissInfo), WorldCombatTextStyle.PlayerSpell));
                break;

            case CombatXpGain xp:
                cues.Add(new(playerGuid, $"XP: {xp.Total}", WorldCombatTextStyle.Experience));
                break;
        }
        return cues;
    }

    public static IEnumerable<ulong> FeedbackVictims(CombatEvent combatEvent) => combatEvent switch
    {
        CombatMeleeSwing e => [e.Victim],
        CombatSpellDamage e => [e.Target],
        CombatPeriodicAura e => [e.Target],
        CombatHeal e => [e.Target],
        CombatEnergize e => [e.Target],
        CombatDamageShield e => [e.Attacker],
        CombatEnvironmentalDamage e => [e.Victim],
        CombatSpellMiss e => e.Misses.Select(m => m.Target),
        _ => [],
    };

    public static List<CenterCombatTextCue> CenterText(CombatEvent combatEvent, ulong playerGuid)
    {
        var cues = new List<CenterCombatTextCue>();
        switch (combatEvent)
        {
            case CombatMeleeSwing e when e.Victim == playerGuid:
                cues.Add(new(e.Damage > 0 ? $"-{e.Damage}" : MeleeText(e), CenterCombatTextStyle.Damage,
                    e.Damage > 0 && (e.HitInfo & 0x80u) != 0));
                break;
            case CombatSpellDamage e when e.Target == playerGuid:
                cues.Add(new(e.Damage > 0 ? $"-{e.Damage}" : SpellText(e), CenterCombatTextStyle.Damage,
                    e.Damage > 0 && (e.HitInfo & 0x2u) != 0));
                break;
            case CombatPeriodicAura e when e.Target == playerGuid:
                foreach (CombatPeriodicTick tick in e.Ticks)
                {
                    if (tick.Kind == CombatPeriodicKind.Damage)
                        cues.Add(new(tick.Amount > 0 ? $"-{tick.Amount}" :
                            tick.Absorb > 0 ? "Absorb" : tick.Resist != 0 ? "Resist" : "Miss",
                            CenterCombatTextStyle.Damage));
                    else if (tick.Kind == CombatPeriodicKind.Heal)
                        cues.Add(new($"+{tick.Amount}", CenterCombatTextStyle.Heal));
                    else if (tick.Kind is CombatPeriodicKind.Energize or CombatPeriodicKind.ManaLeech)
                        cues.Add(new($"+{tick.Amount}", CenterCombatTextStyle.Power));
                }
                break;
            case CombatHeal e when e.Target == playerGuid:
                cues.Add(new($"+{e.Amount}", CenterCombatTextStyle.Heal, e.Critical));
                break;
            case CombatEnergize e when e.Target == playerGuid:
                cues.Add(new($"+{e.Amount}", CenterCombatTextStyle.Power));
                break;
            case CombatDamageShield e when e.Attacker == playerGuid:
                cues.Add(new($"-{e.Damage}", CenterCombatTextStyle.Damage));
                break;
            case CombatEnvironmentalDamage e when e.Victim == playerGuid:
                cues.Add(new($"-{e.Damage}", CenterCombatTextStyle.Damage));
                break;
            case CombatSpellMiss e:
                foreach (CombatMiss miss in e.Misses)
                    if (miss.Target == playerGuid)
                        cues.Add(new(MissWord(miss.MissInfo), CenterCombatTextStyle.Damage));
                break;
        }
        return cues;
    }

    private static string MeleeText(CombatMeleeSwing swing)
    {
        string? state = VictimStateWord(swing.VictimState);
        if (state is not null) return state;
        if (swing.Damage > 0) return swing.Damage.ToString();
        if ((swing.HitInfo & 0x20u) != 0 || swing.Absorb > 0) return "Absorb";
        if ((swing.HitInfo & 0x40u) != 0 || swing.Resist != 0) return "Resist";
        return "Miss";
    }

    private static string SpellText(CombatSpellDamage spell)
    {
        if (spell.Damage > 0) return spell.Damage.ToString();
        if (spell.Absorb > 0) return "Absorb";
        if (spell.Resist != 0) return "Resist";
        return "Miss";
    }

    private static string? VictimStateWord(uint state) => state switch
    {
        2 => "Dodge",
        3 => "Parry",
        5 => "Block",
        6 => "Evade",
        7 => "Immune",
        8 => "Deflect",
        _ => null,
    };

    private static string MissWord(byte missInfo) => missInfo switch
    {
        2 => "Resist",
        3 => "Dodge",
        4 => "Parry",
        5 => "Block",
        6 => "Evade",
        7 or 8 => "Immune",
        9 => "Deflect",
        10 => "Absorb",
        11 => "Reflect",
        _ => "Miss",
    };
}

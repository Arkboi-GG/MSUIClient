namespace MSUIClient.Engine.UI;

/// <summary>The spell data a readiness reading needs; a projection of one Spell.dbc row.</summary>
public readonly record struct CommanderRaidAuraFact(uint Id, uint Family, int DurationMs,
    uint[] Effects, uint[] Auras, uint[] Targets, uint[] TriggerSpells);

/// <summary>What a raid member visibly carries before a pull.</summary>
public sealed record CommanderRaidReadiness(bool Fed, int Elixirs, bool Shielded, int Buffs)
{
    public static CommanderRaidReadiness None => new(false, 0, false, 0);
    public bool Ready => Fed && Elixirs > 0;
    public string Text =>
        (Fed ? "Fed" : "Not fed") + " - " +
        (Elixirs == 0 ? "no elixirs" : Elixirs == 1 ? "1 elixir" : Elixirs + " elixirs") +
        (Shielded ? " - shielded" : "") +
        (Buffs == 0 ? "" : " - " + Buffs + (Buffs == 1 ? " buff" : " buffs"));
}

/// <summary>
/// Reads pre-pull readiness from a member's visible auras and Spell.dbc shapes alone - no item,
/// spell, class or boss identities. Mirrors the Core's ORDER_PREPARE classification: a well-fed
/// buff is whatever a seated regeneration food triggers; an elixir or flask is a long, aura-only,
/// self-targeted spell of the generic family; a class buff is a long aura-only spell of a class
/// family; a shield is a long school-absorb aura.
/// </summary>
public static class CommanderRaidReadinessLaw
{
    public const int ConsumableMinDurationMs = 15 * 60 * 1000;
    public const int BuffMinDurationMs = 5 * 60 * 1000;
    private const uint ApplyAura = 6, AreaAuraParty = 35;
    private const uint AuraModRegen = 84, AuraObsModHealth = 20, AuraPeriodicTrigger = 23, AuraSchoolAbsorb = 69;
    private const uint TargetSelf = 1;

    /// <summary>Every buff any food promises: the periodic trigger of a seated regeneration spell.</summary>
    public static IReadOnlySet<uint> WellFedSpells(IEnumerable<CommanderRaidAuraFact> catalog)
    {
        var result = new HashSet<uint>();
        foreach (var spell in catalog)
        {
            bool regen = false; uint trigger = 0;
            for (int i = 0; i < spell.Effects.Length; i++)
            {
                if (spell.Effects[i] != ApplyAura) continue;
                uint aura = i < spell.Auras.Length ? spell.Auras[i] : 0;
                if (aura is AuraModRegen or AuraObsModHealth) regen = true;
                if (aura == AuraPeriodicTrigger && i < spell.TriggerSpells.Length && spell.TriggerSpells[i] != 0) trigger = spell.TriggerSpells[i];
            }
            if (regen && trigger != 0) result.Add(trigger);
        }
        return result;
    }

    public static CommanderRaidReadiness Read(IEnumerable<uint> auras, Func<uint, CommanderRaidAuraFact?> lookup, IReadOnlySet<uint> wellFed)
    {
        bool fed = false, shielded = false; int elixirs = 0, buffs = 0;
        foreach (uint id in auras.Distinct())
        {
            if (wellFed.Contains(id)) { fed = true; continue; }
            if (lookup(id) is not { } spell) continue;
            bool aurasOnly = spell.Effects.Length > 0 && spell.Effects.All(e => e is 0 or ApplyAura or AreaAuraParty);
            if (!aurasOnly) continue;
            if (spell.Auras.Contains(AuraSchoolAbsorb) && spell.DurationMs >= ConsumableMinDurationMs) { shielded = true; continue; }
            bool self = spell.Targets.Length > 0 && spell.Targets[0] == TargetSelf;
            if (spell.Family == 0 && self && spell.DurationMs >= ConsumableMinDurationMs) elixirs++;
            else if (spell.Family != 0 && spell.DurationMs >= BuffMinDurationMs) buffs++;
        }
        return new(fed, elixirs, shielded, buffs);
    }
}

using MSUIClient.Net;

namespace MSUIClient.Engine.UI;

/// <summary>Shipped GlobalStrings dispel/death sentences; packet facts never mutate aura or health state.</summary>
public static class CombatNoticeUiLaw
{
    public static IEnumerable<string> Lines(CombatEvent notice, ulong actor,
        Func<ulong, string> unitName, Func<uint, string> spellName)
    {
        switch (notice)
        {
            case CombatSpellExecution execution:
                foreach (var effect in execution.Effects)
                    foreach (var result in effect.Results)
                    {
                        if (result is ExecuteExtraAttacks extra)
                        {
                            string who = extra.Guid == actor ? "You gain" : unitName(extra.Guid) + " gains";
                            yield return $"{who} {extra.Count} extra {(extra.Count == 1 ? "attack" : "attacks")} through {spellName(execution.SpellId)}.";
                        }
                        else if (result is ExecuteInterrupt interrupt)
                        {
                            string who = execution.Caster == actor ? "You interrupt" : unitName(execution.Caster) + " interrupts";
                            string target = interrupt.Guid == actor ? "your" : unitName(interrupt.Guid) + "'s";
                            yield return $"{who} {target} {spellName(interrupt.SpellId)}.";
                        }
                    }
                break;
            case CombatDispel dispel:
                foreach (uint spell in dispel.Spells)
                {
                    string aura = spellName(spell);
                    string owner = dispel.Target == actor ? "Your" : unitName(dispel.Target) + "'s";
                    if (!dispel.Failed) yield return $"{owner} {aura} is removed.";
                    else
                    {
                        string caster = dispel.Caster == actor ? "You fail" : unitName(dispel.Caster) + " fails";
                        string target = dispel.Target == actor ? "your" : unitName(dispel.Target) + "'s";
                        yield return $"{caster} to dispel {target} {aura}.";
                    }
                }
                break;
            case CombatInstantKill kill:
                yield return kill.Target == actor ? $"You are killed by {spellName(kill.SpellId)}." :
                    $"{unitName(kill.Target)} is killed by {spellName(kill.SpellId)}.";
                break;
            case CombatPartyKill kill:
                yield return $"{unitName(kill.Target)} is slain by {unitName(kill.Killer)}!";
                break;
        }
    }
}

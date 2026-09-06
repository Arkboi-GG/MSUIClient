namespace MSUIClient.Net;

public enum WorldCombatTextStyle
{
    PlayerMelee,
    PlayerSpell,
    PetMelee,
    Experience,
    Honor,
}

public enum WorldCombatTextCategory : byte
{
    Normal,
    Absorb,
    Critical,
    Outcome,
    Experience,
    Honor,
}

public readonly record struct WorldCombatTextPresentation(
    float LifetimeSeconds,
    float RiseYards,
    float FadeInEndSeconds,
    float FadeOutStartSeconds,
    uint Color);

public readonly record struct WorldCombatTextCue(
    ulong Target,
    string Text,
    WorldCombatTextStyle Style,
    bool Critical = false);

public enum CenterCombatTextStyle { Damage, Heal, Power, Info }
public readonly record struct CenterCombatTextCue(string Text, CenterCombatTextStyle Style, bool Critical = false);

/// <summary>
/// Pure vanilla world-combat-text law. Only the local player's outgoing damage
/// floats over units; incoming and unrelated combat are deliberately suppressed.
/// </summary>
public static class CombatFeedbackLaw
{
    private const float NormalScale = 0.018333f;
    private const float CriticalScale = 0.0275f;

    public static WorldCombatTextCategory Category(
        WorldCombatTextStyle style, bool critical, string text)
    {
        if (critical) return WorldCombatTextCategory.Critical;
        if (style == WorldCombatTextStyle.Experience) return WorldCombatTextCategory.Experience;
        if (style == WorldCombatTextStyle.Honor) return WorldCombatTextCategory.Honor;
        if (string.Equals(text, "Absorb", StringComparison.OrdinalIgnoreCase))
            return WorldCombatTextCategory.Absorb;
        return uint.TryParse(text, out _)
            ? WorldCombatTextCategory.Normal : WorldCombatTextCategory.Outcome;
    }

    /// <summary>The six frozen WORLDTEXTSTRING category rows.</summary>
    public static WorldCombatTextPresentation Presentation(
        WorldCombatTextStyle style, bool critical, string text)
    {
        WorldCombatTextCategory category = Category(style, critical, text);
        uint color = style switch
        {
            WorldCombatTextStyle.PlayerSpell => 0xFFFFDE00,
            WorldCombatTextStyle.PetMelee => 0xFFFF8400,
            WorldCombatTextStyle.Experience => 0xFF94008B,
            WorldCombatTextStyle.Honor => 0xFFE0CA0A,
            _ => 0xFFFFFFFF,
        };
        return category switch
        {
            WorldCombatTextCategory.Absorb => new(1.5f, 2f, .15f, .09f, color),
            WorldCombatTextCategory.Critical => new(1.5f, 0f, .15f, 1f, color),
            WorldCombatTextCategory.Outcome => new(1.5f, 2f, .15f, 1f, color),
            WorldCombatTextCategory.Experience => new(4.5f, 0f, .5f, 2f, color),
            WorldCombatTextCategory.Honor => new(4.5f, 0f, .5f, 2f, color),
            _ => new(1.5f, 2f, .15f, .76f, color),
        };
    }

    /// <summary>Main/shadow alpha after the reference SetShadowColor min-cap.</summary>
    public static (float Main, float Shadow) Alpha(
        WorldCombatTextPresentation presentation, float ageSeconds)
    {
        float age = Math.Clamp(ageSeconds, 0f, presentation.LifetimeSeconds);
        float main;
        float shadow;
        if (age < presentation.FadeInEndSeconds)
        {
            float t = age / presentation.LifetimeSeconds;
            main = t;
            shadow = 127f / 255f * t;
        }
        else if (age >= presentation.FadeOutStartSeconds)
        {
            float t = (age - presentation.FadeOutStartSeconds) /
                (presentation.LifetimeSeconds - presentation.FadeOutStartSeconds);
            main = 1f - Math.Clamp(t, 0f, 1f);
            shadow = main;
        }
        else
        {
            main = 1f;
            shadow = 127f / 255f;
        }
        return (main, MathF.Min(main, shadow));
    }

    public static float Scale(bool critical, float ageSeconds)
    {
        if (!critical) return NormalScale;
        float t = Math.Clamp(ageSeconds / 1.5f, 0f, 1f);
        float factor = t <= .1f
            ? .1f + 1.9f * (t / .1f)
            : t <= .2f ? 2f - (t - .1f) / .1f : 1f;
        return CriticalScale * factor;
    }

    public static List<WorldCombatTextCue> WorldText(CombatEvent combatEvent, ulong playerGuid,
        Func<ulong, bool>? isOwnedSource = null,
        Func<uint, bool>? isMeleeStyledSpell = null)
    {
        var cues = new List<WorldCombatTextCue>();
        switch (combatEvent)
        {
            case CombatMeleeSwing swing when IsPlayerOrOwned(swing.Attacker, playerGuid,
                    isOwnedSource) && swing.Victim != playerGuid:
                cues.Add(new(swing.Victim, MeleeText(swing),
                    swing.Attacker == playerGuid
                        ? WorldCombatTextStyle.PlayerMelee : WorldCombatTextStyle.PetMelee,
                    swing.Damage > 0 && (swing.HitInfo & 0x80u) != 0));
                break;

            case CombatSpellDamage spell when IsPlayerOrOwned(spell.Attacker, playerGuid,
                    isOwnedSource) && spell.Target != playerGuid:
                cues.Add(new(spell.Target, SpellText(spell), SpellDamageStyle(
                        spell.Attacker, playerGuid, spell.SpellId, isMeleeStyledSpell),
                    spell.Damage > 0 && (spell.HitInfo & 0x2u) != 0));
                break;

            case CombatPeriodicAura aura when IsPlayerOrOwned(aura.Caster, playerGuid,
                    isOwnedSource) && aura.Target != playerGuid:
                foreach (CombatPeriodicTick tick in aura.Ticks)
                    if (tick.Kind == CombatPeriodicKind.Damage)
                        cues.Add(new(aura.Target, tick.Amount > 0 ? tick.Amount.ToString() :
                            tick.Absorb > 0 ? "Absorb" : tick.Resist != 0 ? "Resist" : "Miss",
                            SpellDamageStyle(aura.Caster, playerGuid, aura.SpellId,
                                isMeleeStyledSpell)));
                break;

            // The shield bearer is the source; Attacker is who receives the reflected damage.
            case CombatDamageShield shield when IsPlayerOrOwned(shield.Victim, playerGuid,
                    isOwnedSource) && shield.Attacker != playerGuid:
                cues.Add(new(shield.Attacker, shield.Damage.ToString(), WorldCombatTextStyle.PlayerSpell));
                break;

            case CombatSpellMiss miss when IsPlayerOrOwned(miss.Caster, playerGuid, isOwnedSource):
                foreach (CombatMiss target in miss.Misses)
                    if (target.Target != playerGuid)
                        cues.Add(new(target.Target, MissWord(target.MissInfo), WorldCombatTextStyle.PlayerSpell));
                break;

            case CombatSpellOutcome outcome when IsPlayerOrOwned(outcome.Caster, playerGuid, isOwnedSource)
                    && outcome.Target != playerGuid:
                cues.Add(new(outcome.Target, outcome.Immune ? "Immune" : "Resist",
                    WorldCombatTextStyle.PlayerSpell));
                break;

            case CombatXpGain xp:
                cues.Add(new(playerGuid, $"XP: {xp.Total}", WorldCombatTextStyle.Experience));
                break;
        }
        return cues;
    }

    private static bool IsPlayerOrOwned(ulong source, ulong playerGuid,
        Func<ulong, bool>? isOwnedSource) =>
        source == playerGuid || isOwnedSource?.Invoke(source) == true;

    private static WorldCombatTextStyle SpellDamageStyle(ulong source, ulong playerGuid,
        uint spellId, Func<uint, bool>? isMeleeStyledSpell)
    {
        // The real client treats a NULL SpellRec as melee-styled too. That preserves a visible
        // row-default number when the local catalog is unavailable instead of guessing gold.
        bool meleeStyled = isMeleeStyledSpell?.Invoke(spellId) ?? true;
        if (!meleeStyled) return WorldCombatTextStyle.PlayerSpell;
        return source == playerGuid
            ? WorldCombatTextStyle.PlayerMelee : WorldCombatTextStyle.PetMelee;
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
            case CombatSpellOutcome outcome when outcome.Target == playerGuid:
                cues.Add(new(outcome.Immune ? "Immune" : "Resist", CenterCombatTextStyle.Info));
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

using MSUIClient.Formats;

namespace MSUIClient.Engine.UI;

public enum WeaponIconSubstitution
{
    None,
    MainHand,
    Ranged,
}

/// <summary>
/// Vanilla action/spellbook icon law for spells whose visible icon belongs to
/// equipped gear rather than SpellIcon.dbc. Spell 6603's DBC icon is the
/// internal Temp face and must never be presented to a player.
/// </summary>
public static class ActionIconLaw
{
    public const uint SpellEffectAttack = 78;
    public const uint ThrownWeaponSubclass = 16;
    public const string UnarmedAttackIcon = @"Interface\Buttons\Spell-Reset.blp";

    public static WeaponIconSubstitution Substitution(in SpellInfo spell)
    {
        if (spell.EffectIds is { Length: > 0 } effects &&
            effects[0] == SpellEffectAttack)
            return WeaponIconSubstitution.MainHand;
        if ((spell.Attributes & 0x2) != 0 && (spell.AttributesEx2 & 0x20) != 0)
            return WeaponIconSubstitution.Ranged;
        return WeaponIconSubstitution.None;
    }

    public static string Resolve(in SpellInfo spell, string? equippedIcon,
        uint? equippedSubclass)
    {
        return Substitution(spell) switch
        {
            WeaponIconSubstitution.MainHand =>
                string.IsNullOrWhiteSpace(equippedIcon) ? UnarmedAttackIcon : equippedIcon,
            WeaponIconSubstitution.Ranged when equippedSubclass != ThrownWeaponSubclass &&
                                                !string.IsNullOrWhiteSpace(equippedIcon) =>
                equippedIcon,
            _ => spell.IconPath,
        };
    }
}

using MSUIClient.Formats;

namespace MSUIClient.Engine.UI;

public static class SpellTimingLaw
{
    private static float Factor(float value) => float.IsFinite(value) && value >= 0 ? value : 1;

    public static uint GlobalCooldownMilliseconds(in SpellInfo spell, float castSpeed)
    {
        uint duration = spell.StartRecoveryMs;
        // Player::AddGCD: only the ordinary 1.5s spell category is haste-scaled.
        if (spell.StartRecoveryCategory == 133 && duration == 1500 &&
            spell.DamageClass is not (2 or 3) && (spell.Attributes & 0x12) == 0)
            return (uint)Math.Clamp(duration * Factor(castSpeed), 1000, 1500);
        return duration;
    }

    public static int CastTimeMilliseconds(in SpellInfo spell, float castSpeed)
    {
        if (spell.CastTimeMs <= 0) return 0;
        // Ability/tradeskill timing follows other core branches, not UNIT_MOD_CAST_SPEED.
        if ((spell.Attributes & 0x30) != 0) return spell.CastTimeMs;
        return (int)Math.Clamp((double)spell.CastTimeMs * Factor(castSpeed), 0, int.MaxValue);
    }
}

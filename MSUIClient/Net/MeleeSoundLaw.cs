using MSUIClient.Formats;

namespace MSUIClient.Net;

/// <summary>Pure build-5875 melee outcome-to-sound-slot decisions.</summary>
public static class MeleeSoundLaw
{
    public static bool NoContact(CombatMeleeSwing swing) =>
        (swing.HitInfo & 0x10u) != 0 || swing.VictimState is 2 or 6 or 7 or 8;

    public static bool Defended(uint victimState) => victimState is 3 or 5;

    public static uint MissKit(uint weaponSubclass) =>
        weaponSubclass is 1 or 5 or 6 or 8 or 10 or 17 ? 7081u : 7080u;

    public static int TargetSlot(uint impactType) => impactType switch
    {
        1 => 8, 2 => 7, 3 => 9, _ => 0,
    };

    public static int DefenseSlot(uint victimState, bool victimWooden) =>
        victimState == 3 ? (victimWooden ? 6 : 5) : 3;

    public static uint InjuryKit(in CreatureVoice voice, uint hitInfo)
    {
        bool critical = (hitInfo & 0x80u) != 0;
        uint kit = (hitInfo & 0x8000u) != 0 ? voice.InjuryCrushingSound
            : critical ? voice.InjuryCriticalSound : voice.InjurySound;
        if (kit == 0) kit = critical ? voice.InjuryCriticalSound : voice.InjurySound;
        return kit != 0 ? kit : voice.InjurySound;
    }
}

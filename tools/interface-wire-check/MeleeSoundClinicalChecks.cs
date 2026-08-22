using MSUIClient;
using MSUIClient.Formats;
using MSUIClient.Net;

internal static class MeleeSoundClinicalChecks
{
    public static void Run()
    {
        uint[] impact = new uint[10];
        uint[] crit = new uint[10];
        impact[0] = 143; crit[0] = 144;
        impact[3] = 3263; impact[5] = 1002; impact[6] = 1001;
        impact[7] = 7007; impact[8] = 8008; impact[9] = 9009;
        WeaponImpactCatalog catalog = WeaponImpactCatalog.FromRows((7, true, impact, crit));
        Check(catalog.TryGet(7, false, out WeaponImpactRow fallback) &&
              fallback.Impact[0] == 143 && fallback.Critical[0] == 144,
            "weapon material opposite-row fallback drift");

        var miss = new CombatMeleeSwing(1, 2, 0x10, 0, 0, 0, 0, 0);
        var dodge = miss with { HitInfo = 0, VictimState = 2 };
        var landed = miss with { HitInfo = 0x80, Damage = 10, VictimState = 0 };
        Check(MeleeSoundLaw.NoContact(miss) && MeleeSoundLaw.NoContact(dodge) &&
              !MeleeSoundLaw.NoContact(landed) &&
              MeleeSoundLaw.MissKit(1) == 7081 && MeleeSoundLaw.MissKit(7) == 7080 &&
              MeleeSoundLaw.TargetSlot(0) == 0 && MeleeSoundLaw.TargetSlot(1) == 8 &&
              MeleeSoundLaw.TargetSlot(2) == 7 && MeleeSoundLaw.TargetSlot(3) == 9 &&
              MeleeSoundLaw.DefenseSlot(3, false) == 5 &&
              MeleeSoundLaw.DefenseSlot(3, true) == 6 &&
              MeleeSoundLaw.DefenseSlot(5, false) == 3,
            "melee contact/material/miss/defense slot law drift");

        string root = ClientConfig.FindRepoRoot();
        string sound = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Combat",
            "GameLoop.MeleeSounds.cs"));
        string events = SourceText.Read(Path.Combine(root, "MSUIClient", "World", "Units",
            "CreatureAnimationSoundLaw.cs"));
        Check(events.Contains("\"$CSS\" or \"$CAH\" or \"$AH0\"", StringComparison.Ordinal) &&
              sound.Contains("if (!attackerResolved)", StringComparison.Ordinal) &&
              sound.Contains("PlayMeleeContact(pending, natural)", StringComparison.Ordinal) &&
              sound.Contains("voice.ExertionCriticalSound", StringComparison.Ordinal) &&
              sound.Contains("attackVoice.CustomAttack1Sound", StringComparison.Ordinal),
            "authored impact timing, unresolved fallback, exertion, or natural-attack wiring drift");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}

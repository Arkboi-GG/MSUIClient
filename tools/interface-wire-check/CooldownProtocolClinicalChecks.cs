using MSUIClient.Formats;
using MSUIClient.Net;

internal static class CooldownProtocolClinicalChecks
{
    public static void Run()
    {
        Check((ushort)Op.SMSG_ITEM_COOLDOWN == 0x00B0 &&
              (ushort)Op.SMSG_COOLDOWN_EVENT == 0x0135 &&
              (ushort)Op.SMSG_CLEAR_COOLDOWN == 0x01DE &&
              (ushort)Op.SMSG_COOLDOWN_CHEAT == 0x01E1,
            "cooldown follow-up opcodes drift");

        byte[] itemBody = Convert.FromHexString("4000000000000000B7010000");
        Check(CooldownPackets.ParseItem(itemBody) ==
              new CooldownPackets.ItemCooldown(0x40, 439),
            "SMSG_ITEM_COOLDOWN raw-guid/u32 body drift");
        byte[] addressedBody = Convert.FromHexString("F80600002200000000000000");
        Check(CooldownPackets.ParseAddressedSpell(addressedBody, "clinical") ==
              new CooldownPackets.AddressedSpell(1784, 0x22),
            "cooldown event spell-first/raw-guid body drift");
        Check(CooldownPackets.ParseCheat(Convert.FromHexString("3300000000000000")) == 0x33,
            "SMSG_COOLDOWN_CHEAT raw-guid body drift");

        var actions = new PlayerActions();
        actions.StartCooldown(1784, 12, 10_000, 8_000, 1.0, onHold: true);
        Check(actions.IsOnCooldown(1784, 2.0, 12) &&
              !actions.TryCooldownDisplay(1784, 2.0, 12, out _),
            "on-event cooldown was not parked as disabled/not-ready");
        actions.StartCooldownEvent(1784, 5.0);
        Check(Math.Abs(actions.CooldownRemaining(1784, 6.0, 12) - 9.0) < 0.001,
            "SMSG_COOLDOWN_EVENT did not start parked duration at receive time");
        actions.ClearCooldown(1784);
        Check(!actions.IsOnCooldown(1784, 6.0, 12),
            "SMSG_CLEAR_COOLDOWN retained a spell/category record");

        SpellInfo fireball = default(SpellInfo) with
        {
            Id = 133, StartRecoveryCategory = 133, StartRecoveryMs = 1_500,
            EffectIds = [2, 0, 0],
        };
        SpellInfo frostNova = fireball with
        {
            Id = 122, Category = 35, RecoveryMs = 25_000,
        };
        actions.StartGlobalCooldown(122, frostNova, 20.0);
        actions.StartSpellCooldown(122, frostNova, rangedAttackTimeMs: 0, nowSeconds: 20.1);
        Check(Math.Abs(actions.CooldownRemaining(133, 0, fireball, 20.2) - 1.3) < 0.001,
            "SMSG_SPELL_GO recovery replaced its cast-send GCD node");
        Check(Math.Abs(actions.CooldownRemaining(122, 0, frostNova, 20.2) - 24.9) < 0.001,
            "longest-remaining cooldown resolution drift");

        SpellInfo zeroTimeScroll = fireball with { Id = 8091, StartRecoveryMs = 0 };
        Check(actions.IsOnCooldown(8091, 0, zeroTimeScroll, 20.2),
            "GCD category equality incorrectly consulted the queried spell duration");
        SpellInfo attack = fireball with { Id = 6603, EffectIds = [78, 0, 0] };
        Check(!actions.IsOnCooldown(6603, 0, attack, 20.2),
            "Attack bypassed the cooldown getter head exclusion");

        var wildcard = new PlayerActions();
        wildcard.StartCooldown(5019, 351, 0, 1_500, 30.0, onHold: false,
            categoryWildcard: true);
        Check(wildcard.IsOnCooldown(133, 0, fireball, 30.5),
            "SpellCategory flags-0x2 wildcard did not reach an unrelated action");
        SpellInfo thrown = default(SpellInfo) with
        {
            Id = 2764, Attributes = 0x2, Category = 76, EffectIds = [2, 0, 0],
        };
        wildcard.StartSpellCooldown(2764, thrown, rangedAttackTimeMs: 2_200,
            nowSeconds: 40.0);
        Check(Math.Abs(wildcard.CooldownRemaining(2764, 0, thrown, 41.0) - 1.2) < 0.001,
            "ranged-weapon speed did not pad the category recovery");

        var items = new PlayerActions();
        var useSpell = new ItemSpellTemplate(439, 0, 0, -1, 4, 60_000);
        SpellInfo potion = default(SpellInfo) with
        {
            Id = 439, Category = 4, StartRecoveryCategory = 133, StartRecoveryMs = 1_500,
            EffectIds = [2, 0, 0],
        };
        items.StartItemUseCooldown(118, useSpell, potion, 50.0);
        Check(Math.Abs(items.CooldownRemaining(439, 118, potion, 65.0) - 45.0) < 0.001,
            "item-use wire triple did not retain its item/category recovery");
        items.StartItemPacketCooldown(439, itemEntry: 118, nowSeconds: 70.0);
        SpellInfo uncategorized = potion with { Category = 0 };
        Check(items.IsOnCooldown(439, 118, uncategorized, 71.0) &&
              !items.IsOnCooldown(439, 929, uncategorized, 71.0),
            "SMSG_ITEM_COOLDOWN lost the item/spell pair key");

        actions.StartCooldown(439, 0, 30_000, 10.0);
        actions.StartCooldown(133, 0, 5_000, 10.0);
        actions.ClearAllCooldowns();
        Check(!actions.IsOnCooldown(439, 11.0) && !actions.IsOnCooldown(133, 11.0),
            "SMSG_COOLDOWN_CHEAT did not wipe the complete store");
        CheckThrows(() => CooldownPackets.ParseItem(new byte[11]));
        CheckThrows(() => CooldownPackets.ParseAddressedSpell(new byte[13], "clinical"));
        CheckThrows(() => CooldownPackets.ParseCheat(new byte[9]));
    }

    private static void CheckThrows(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidDataException("malformed cooldown packet was accepted");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}

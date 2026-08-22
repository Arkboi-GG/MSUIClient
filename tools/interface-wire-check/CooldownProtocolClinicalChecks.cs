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
        Check(!actions.IsOnCooldown(1784, 2.0, 12),
            "on-event cooldown ran before SMSG_COOLDOWN_EVENT");
        actions.StartCooldownEvent(1784, 5.0);
        Check(Math.Abs(actions.CooldownRemaining(1784, 6.0, 12) - 9.0) < 0.001,
            "SMSG_COOLDOWN_EVENT did not start parked duration at receive time");
        actions.ClearCooldown(1784);
        Check(!actions.IsOnCooldown(1784, 6.0, 12),
            "SMSG_CLEAR_COOLDOWN retained a spell/category record");

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

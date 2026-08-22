using System.Numerics;
using MSUIClient;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

internal static class ItemEnchantClinicalChecks
{
    public static void Run()
    {
        var writer = new PacketWriter();
        writer.WriteU64(0x1122_3344_5566_7788);
        writer.WriteU32(1);
        writer.WriteU32(90);
        writer.WriteU64(0x8877_6655_4433_2211);
        ItemEnchantTimePacket packet = ItemEnchantTimePackets.Parse(writer.ToArray());
        Check(packet == new ItemEnchantTimePacket(0x1122_3344_5566_7788, 1, 90,
                0x8877_6655_4433_2211),
            "item enchant time 24-byte body drift");
        CheckThrows(() => ItemEnchantTimePackets.Parse(new byte[23]));
        CheckThrows(() => ItemEnchantTimePackets.Parse(new byte[25]));

        var timers = new ItemEnchantTimerState();
        timers.Set(10, 1, 90, 100);
        Check(timers.RemainingMilliseconds(10, 1, 130) == 60_000,
            "temporary enchant deadline integration drift");
        timers.Set(10, 1, 0, 130);
        Check(timers.RemainingMilliseconds(10, 1, 130) is null,
            "zero-second enchant timer must clear");

        Check(ItemEnchantUiLaw.Countdown("Rockbiter", 1_900) == "Rockbiter (1 sec)" &&
              ItemEnchantUiLaw.Countdown("Rockbiter", 241_000) == "Rockbiter (5 min)" &&
              ItemEnchantUiLaw.Countdown("Rockbiter", 3_600_001) == "Rockbiter (2 hrs)" &&
              ItemEnchantUiLaw.Countdown("Rockbiter", 86_400_001) == "Rockbiter (2 days)" &&
              ItemEnchantUiLaw.Text("Poison", null, 1) == "Poison (1 Charge)" &&
              ItemEnchantUiLaw.Color(0, 1) == new Vector4(0, 1, 0, 1) &&
              ItemEnchantUiLaw.Color(1, -1) == new Vector4(1, 0, 0, 1) &&
              ItemEnchantUiLaw.Color(2, -1) == Vector4.One,
            "item enchant tooltip color/countdown/charge law drift");

        string root = ClientConfig.FindRepoRoot();
        string dispatch = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Net.cs"));
        string inventory = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Inventory.cs"));
        Check(dispatch.Contains("case Op.SMSG_ITEM_ENCHANT_TIME_UPDATE:", StringComparison.Ordinal) &&
              dispatch.Contains("ApplyItemEnchantTime(body)", StringComparison.Ordinal) &&
              inventory.Contains("ItemEnchantmentCharges(slot)", StringComparison.Ordinal) &&
              inventory.Contains("enchant.HidesTooltipName", StringComparison.Ordinal) &&
              inventory.Contains("ItemEnchantUiLaw.Color(slot, signedId)", StringComparison.Ordinal),
            "temporary enchant receive or item-tooltip renderer is unwired");
    }

    private static void CheckThrows(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidDataException("malformed item-enchant-time packet was accepted");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}

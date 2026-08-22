using MSUIClient;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

internal static class InventoryFailureClinicalChecks
{
    public static void Run()
    {
        InventoryChangeFailurePacket ok = InventoryFailurePackets.Parse([0]);
        Check(ok.Reason == 0 && ok.RequiredLevel is null && ok.ItemGuid == 0,
            "inventory failure reason-zero bare packet drift");

        var level = new PacketWriter();
        level.WriteU8(1);
        level.WriteU32(40);
        level.WriteU64(0x1122_3344_5566_7788);
        level.WriteU64(0x8877_6655_4433_2211);
        level.WriteU8(19);
        InventoryChangeFailurePacket parsed = InventoryFailurePackets.Parse(level.ToArray());
        Check(parsed == new InventoryChangeFailurePacket(1, 40,
                0x1122_3344_5566_7788, 0x8877_6655_4433_2211, 19),
            "inventory failure conditional required-level branch drift");

        var ordinary = new PacketWriter();
        ordinary.WriteU8(50);
        ordinary.WriteU64(123);
        ordinary.WriteU64(456);
        ordinary.WriteU8(byte.MaxValue);
        Check(InventoryFailurePackets.Parse(ordinary.ToArray()).Reason == 50,
            "inventory failure ordinary 18-byte body drift");
        CheckThrows(() => InventoryFailurePackets.Parse([]));
        CheckThrows(() => InventoryFailurePackets.Parse([0, 1]));
        CheckThrows(() => InventoryFailurePackets.Parse([1, 0, 0]));

        Check(InventoryErrorUiLaw.GlobalStringKey(1) == "ERR_CANT_EQUIP_LEVEL_I" &&
              InventoryErrorUiLaw.GlobalStringKey(37) == "ERR_GENERIC_STUNNED" &&
              InventoryErrorUiLaw.GlobalStringKey(38) == "ERR_PLAYER_DEAD" &&
              InventoryErrorUiLaw.GlobalStringKey(50) == "ERR_INV_FULL" &&
              InventoryErrorUiLaw.GlobalStringKey(51) == "ERR_BANK_FULL" &&
              InventoryErrorUiLaw.GlobalStringKey(67) == "ERR_BAG_FULL" &&
              InventoryErrorUiLaw.IsSilent(59) &&
              InventoryErrorUiLaw.BagFamilyName(1) == "Arrows" &&
              InventoryErrorUiLaw.BagFamilyName(9) == "Keys",
            "build-5875 InventoryResult display table drift");

        string root = ClientConfig.FindRepoRoot();
        string net = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Net.cs"));
        string inventory = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Inventory.cs"));
        Check(net.Contains("case Op.SMSG_INVENTORY_CHANGE_FAILURE:", StringComparison.Ordinal) &&
              net.Contains("ApplyInventoryChangeFailure(body)", StringComparison.Ordinal) &&
              inventory.Contains("matchedOperations", StringComparison.Ordinal) &&
              inventory.Contains("_pendingBagLocks.Clear()", StringComparison.Ordinal) &&
              inventory.Contains("InventoryGlobalString(key)", StringComparison.Ordinal) &&
              inventory.Contains("ShowUiError(text)", StringComparison.Ordinal),
            "inventory refusal live dispatch, prompt unlock, or UIErrorsFrame sink is unwired");
    }

    private static void CheckThrows(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidDataException("malformed inventory failure packet was accepted");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}

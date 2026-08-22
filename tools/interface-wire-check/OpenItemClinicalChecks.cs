using MSUIClient;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

internal static class OpenItemClinicalChecks
{
    public static void Run()
    {
        Check((ushort)Op.CMSG_OPEN_ITEM == 0x00AC &&
              WorldSession.BuildOpenItemBody(255, 23).SequenceEqual(new byte[] { 255, 23 }),
            "CMSG_OPEN_ITEM opcode/body drift");
        Check(InventoryUiLaw.OpensLoot(InventoryUiLaw.ItemFlagLootable) &&
              InventoryUiLaw.ShowsOpenLine(InventoryUiLaw.ItemFlagLootable, 0, 0) &&
              !InventoryUiLaw.ShowsOpenLine(InventoryUiLaw.ItemFlagLootable, 7, 0) &&
              InventoryUiLaw.ShowsOpenLine(InventoryUiLaw.ItemFlagLootable, 7,
                  InventoryUiLaw.ItemDynamicUnlocked) &&
              InventoryUiLaw.UnwrapsGift(InventoryUiLaw.ItemFlagWrapper,
                  InventoryUiLaw.ItemDynamicWrapped),
            "open-click versus tooltip-promise predicate drift");

        string root = ClientConfig.FindRepoRoot();
        string item = SourceText.Read(Path.Combine(root, "MSUIClient", "Net", "Items.cs"));
        string inventory = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Inventory.cs"));
        int gift = inventory.IndexOf("InventoryUiLaw.UnwrapsGift", StringComparison.Ordinal);
        int quest = inventory.IndexOf("else if (item.StartQuest != 0)", gift,
            StringComparison.Ordinal);
        int loot = inventory.IndexOf("else if (InventoryUiLaw.OpensLoot", quest,
            StringComparison.Ordinal);
        int equip = inventory.IndexOf("else if (item.InventoryType != 0)", loot,
            StringComparison.Ordinal);
        Check(item.Contains("item.PageText = r.ReadU32()", StringComparison.Ordinal) &&
              item.Contains("item.LockId = r.ReadU32()", StringComparison.Ordinal) &&
              inventory.Contains("<Right Click to Open>", StringComparison.Ordinal) &&
              inventory.Contains("AddPendingBagLock(container, slot", StringComparison.Ordinal) &&
              inventory.Contains("_net.OpenItem(wire.Bag, wire.Slot)", StringComparison.Ordinal) &&
              gift >= 0 && quest > gift && loot > quest && equip > loot,
            "open-item right-click order, gray lock, or tooltip affordance is unwired");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}

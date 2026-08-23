using System.Numerics;
using MSUIClient;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

internal static class BankFrameClinicalChecks
{
    public static void Run()
    {
        Check(BankFrameUiLaw.FrameOrigin(1.5f) == new Vector2(0, 156) &&
              BankFrameUiLaw.FrameSize(1.5f) == new Vector2(576, 768) &&
              BankFrameUiLaw.FrameLogicalSize == new Vector2(384, 512) &&
              BankFrameUiLaw.ArtLogicalSize == new Vector2(512, 512) &&
              BankFrameUiLaw.PortraitOffset == new Vector2(7, 6) &&
              BankFrameUiLaw.PortraitSize == 60 &&
              BankFrameUiLaw.Title("") == "Banker" &&
              BankFrameUiLaw.Title("  Gnomeregan Banker  ") == "Gnomeregan Banker" &&
              BankFrameUiLaw.TitleFont == "GameFontHighlight" &&
              BankFrameUiLaw.ItemSlotsLabelCenter == new Vector2(181, 61) &&
              BankFrameUiLaw.BagSlotsLabelCenter == new Vector2(181, 261) &&
              BankFrameUiLaw.PurchaseMessageCenter == new Vector2(187, 341) &&
              BankFrameUiLaw.PurchaseMessageMinimum(2, 12) == new Vector2(47, 329) &&
              BankFrameUiLaw.PurchaseButton == new Vector2(183, 360.5f) &&
              BankFrameUiLaw.Art == @"Interface\BankFrame\UI-BankFrame" &&
              BankFrameUiLaw.PurseRightTop == new Vector2(333, 397) &&
              BankFrameUiLaw.ItemSlotMin(0) == new Vector2(40, 73) &&
              BankFrameUiLaw.ItemSlotMin(5) == new Vector2(285, 73) &&
              BankFrameUiLaw.ItemSlotMin(6) == new Vector2(40, 117) &&
              BankFrameUiLaw.ItemSlotMin(23) == new Vector2(285, 205) &&
              BankFrameUiLaw.ItemSlotSize == new Vector2(37, 37) &&
              BankFrameUiLaw.BankBagSlotMin(0) == new Vector2(40, 274) &&
              BankFrameUiLaw.BankBagSlotMin(5) == new Vector2(285, 274) &&
              BankFrameUiLaw.BankBagTooltipSeat(new Vector2(40, 274), 2) ==
                  new BankFrameUiLaw.TooltipSeat(new Vector2(114, 274), new Vector2(0, 1)) &&
              BankFrameUiLaw.CloseButtonSize == new Vector2(32, 32),
            "bank identity/window geometry drift");
        Check(BankFrameUiLaw.WrapPurchaseMessage(24, value => value.Length)
                  .SequenceEqual(["Do you wish to purchase", "space for an additional", "bag?"]),
            "bank purchase-question GameFontHighlight wrap law drift");

        BankPurchaseConfirmUiLaw.ScreenRect popup =
            BankPurchaseConfirmUiLaw.PopupRect(new Vector2(1920, 1080), 1f, 14f);
        Check(popup.Min.X == 800 && popup.Min.Y == 128 && popup.Size.X == 320 &&
              BankPurchaseConfirmUiLaw.ButtonMin(1, 14).X == 26 &&
              BankPurchaseConfirmUiLaw.ButtonMin(2, 14).X == 167 &&
              BankPurchaseConfirmUiLaw.ButtonSize == new Vector2(128, 20) &&
              BankPurchaseConfirmUiLaw.ButtonUvMax == new Vector2(1, .625f) &&
              BankPurchaseConfirmUiLaw.Prompt ==
                  "Do you want to purchase a bank slot for:",
            "bank purchase popup law drift");

        Check((ushort)Op.CMSG_AUTOSTORE_BANK_ITEM == 0x0282 &&
              (ushort)Op.CMSG_AUTOBANK_ITEM == 0x0283 &&
              WorldSession.BuildAutoBankItemBody(255, 31).SequenceEqual(new byte[] { 255, 31 }) &&
              WorldSession.BuildAutostoreBankItemBody(255, 44).SequenceEqual(new byte[] { 255, 44 }),
            "bank auto-move opcode/body drift");

        Check(InventoryUiLaw.ToWire(InventoryUiLaw.BankContainer, 0) ==
                  new InventoryUiLaw.WirePosition(255, 39) &&
              InventoryUiLaw.ToWire(InventoryUiLaw.BankContainer, 23) ==
                  new InventoryUiLaw.WirePosition(255, 62) &&
              InventoryUiLaw.ToWire(InventoryUiLaw.BankContainer, 24) is null &&
              InventoryUiLaw.ToWire(5, 0) == new InventoryUiLaw.WirePosition(63, 0) &&
              InventoryUiLaw.ToWire(10, 35) == new InventoryUiLaw.WirePosition(68, 35) &&
              InventoryUiLaw.ToWire(InventoryUiLaw.BankBagEquipmentContainer, 0) ==
                  new InventoryUiLaw.WirePosition(255, 63) &&
              InventoryUiLaw.ToWire(InventoryUiLaw.BankBagEquipmentContainer, 5) ==
                  new InventoryUiLaw.WirePosition(255, 68) &&
              InventoryUiLaw.PlanMove(InventoryUiLaw.BankContainer, 2, 0, 4,
                  null, 100, 0).Kind == InventoryUiLaw.MoveKind.SwapInventory &&
              InventoryUiLaw.PlanMove(0, 4, InventoryUiLaw.BankContainer, 2,
                  3, 100, 0).Kind == InventoryUiLaw.MoveKind.Split,
            "bank container cursor law drift");

        string root = ClientConfig.FindRepoRoot();
        string runtime = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Bank.cs"));
        string inventory = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Inventory.cs"));
        Check(runtime.Contains("UiPanelFrameOrigin(UiPanelOwnershipRegistry[5], s)",
                  StringComparison.Ordinal) &&
              runtime.Contains("DrawUnitPortraitImage", StringComparison.Ordinal) &&
              runtime.Contains("BankFrameUiLaw.TitleFont", StringComparison.Ordinal) &&
              runtime.Contains("BankFrameUiLaw.ItemSlotsLabelCenter", StringComparison.Ordinal) &&
              runtime.Contains("BankFrameUiLaw.BagSlotsLabelCenter", StringComparison.Ordinal) &&
              runtime.Contains("BankFrameUiLaw.WrapPurchaseMessage", StringComparison.Ordinal) &&
              runtime.Contains("BankFrameUiLaw.PurchaseMessageMinimum", StringComparison.Ordinal) &&
              runtime.Contains("DrawTrainerMoney", StringComparison.Ordinal) &&
              runtime.Contains("CloseBankSession", StringComparison.Ordinal) &&
              runtime.Contains("SetBagWindowOpen(0, true);", StringComparison.Ordinal) &&
              runtime.IndexOf("SetBagWindowOpen(0, true);", StringComparison.Ordinal) <
                  runtime.IndexOf("PlayUiSound(BankFrameUiLaw.OpenSound",
                      StringComparison.Ordinal) &&
              runtime.Contains("BankPurchaseConfirmUiLaw.PopupRect", StringComparison.Ordinal) &&
              runtime.Contains("_bankPurchaseConfirmOpen = true", StringComparison.Ordinal) &&
              runtime.Contains("PlayUiSound(BankFrameUiLaw.OpenSound", StringComparison.Ordinal) &&
              runtime.Contains("_net.AutoBankItem(sourceBag, sourceSlot)", StringComparison.Ordinal) &&
              runtime.Contains("_net.AutostoreBankItem(255, sourceSlot)", StringComparison.Ordinal) &&
              runtime.Contains("ImGui.IsItemClicked(ImGuiMouseButton.Right)", StringComparison.Ordinal) &&
              runtime.Contains("InventoryUiLaw.ClickAction(", StringComparison.Ordinal) &&
              runtime.Contains("PickupOrPlaceItem(InventoryUiLaw.BankContainer", StringComparison.Ordinal) &&
              runtime.Contains("OpenStackSplit(InventoryUiLaw.BankContainer", StringComparison.Ordinal) &&
              runtime.Contains("HandleInventoryDrag(InventoryUiLaw.BankContainer", StringComparison.Ordinal) &&
              runtime.Contains("DrawBankBagButton(", StringComparison.Ordinal) &&
              runtime.Contains("BankFrameUiLaw.BankBagTooltipSeat(min, scale)",
                  StringComparison.Ordinal) &&
              runtime.Contains("ImGui.SetNextWindowPos(tooltipSeat.Anchor, ImGuiCond.Always",
                  StringComparison.Ordinal) &&
              runtime.Contains("InventoryUiLaw.BankBagEquipmentContainer", StringComparison.Ordinal) &&
              runtime.Contains("SetBagWindowOpen(container, !IsBagWindowOpen(container))", StringComparison.Ordinal) &&
              runtime.Contains("destination=server-selected", StringComparison.Ordinal) &&
              !runtime.Contains("_net.SwapItems", StringComparison.Ordinal) &&
              !runtime.Contains("PlayerBankSlot(i) == 0", StringComparison.Ordinal) &&
              !runtime.Contains("PlayerBackpackSlot(i) == 0", StringComparison.Ordinal) &&
              inventory.Contains("DepositBankItem(wire.Bag, wire.Slot, instance)", StringComparison.Ordinal) &&
              inventory.Contains("DrawBankBagWindows();", StringComparison.Ordinal) &&
              inventory.Contains("PlayerBankBagSlot(", StringComparison.Ordinal) &&
              inventory.Contains("DrawContainerBagWindow(", StringComparison.Ordinal) &&
              runtime.Contains("BankFrameUiLaw.ItemSlotMin(i)", StringComparison.Ordinal) &&
              runtime.Contains("BankFrameUiLaw.BankBagSlotMin(index)", StringComparison.Ordinal) &&
              runtime.Contains("BankFrameUiLaw.CloseButtonSize", StringComparison.Ordinal) &&
              !runtime.Contains("new Vector2", StringComparison.Ordinal) &&
              !runtime.Contains("BeginChild(\"##bank-slots\"", StringComparison.Ordinal) &&
              !runtime.Contains("Bag slots purchased:", StringComparison.Ordinal) &&
              !runtime.Contains("Vector2 origin=new(0,104*s)", StringComparison.Ordinal) &&
              !runtime.Contains("UI-BankFrame-TopLeft", StringComparison.Ordinal),
            "bank production wiring bypasses current frame/popup law");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}

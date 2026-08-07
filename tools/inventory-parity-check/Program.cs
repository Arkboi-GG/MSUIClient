using MSUIClient;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidDataException(message);
}

Require(InventoryUiLaw.KeyringSize(1) == 4 && InventoryUiLaw.KeyringSize(39) == 4 &&
        InventoryUiLaw.KeyringSize(40) == 8 && InventoryUiLaw.KeyringSize(49) == 8 &&
        InventoryUiLaw.KeyringSize(50) == 12 && InventoryUiLaw.KeyringSize(60) == 12 &&
        InventoryUiLaw.KeyringSize(61) == 16, "keyring level ladder drift");
Require(InventoryUiLaw.DisableWithGameMenu(0) && InventoryUiLaw.DisableWithGameMenu(4) &&
        !InventoryUiLaw.DisableWithGameMenu(InventoryUiLaw.KeyringContainer),
    "Disable_BagButtons incorrectly included the keyring button");
Require(InventoryUiLaw.ShouldOpenAllBags(false, [false, false, false, false]) &&
        !InventoryUiLaw.ShouldOpenAllBags(true, [false, false, false, false]) &&
        !InventoryUiLaw.ShouldOpenAllBags(false, [false, true, false, false]),
    "OpenAllBags/CloseAllBags decision drift");
Require(InventoryUiLaw.BindingAction(false) == InventoryUiLaw.BagBindingAction.ToggleBackpack &&
        InventoryUiLaw.BindingAction(true) == InventoryUiLaw.BagBindingAction.ToggleAllBags,
    "B/Shift+B bag binding split drift");
Require(InventoryUiLaw.ClickAction(true, false, true, false, true, 5, false, false) ==
            InventoryUiLaw.SlotClickAction.Split &&
        InventoryUiLaw.ClickAction(true, false, true, false, true, 1, false, false) ==
            InventoryUiLaw.SlotClickAction.PickupOrPlace &&
        InventoryUiLaw.ClickAction(false, true, false, true, true, 5, false, false) ==
            InventoryUiLaw.SlotClickAction.ClearCarried &&
        InventoryUiLaw.ClickAction(false, true, false, false, true, 5, false, false) ==
            InventoryUiLaw.SlotClickAction.ContextAction,
    "inventory click routing drift");
Require(InventoryUiLaw.BagBarAction(0, false, false) == InventoryUiLaw.BagBarClickAction.ToggleBackpack &&
        InventoryUiLaw.BagBarAction(2, true, true) == InventoryUiLaw.BagBarClickAction.PickupOrPlace &&
        InventoryUiLaw.BagBarAction(2, false, true) == InventoryUiLaw.BagBarClickAction.ToggleBag &&
        InventoryUiLaw.BagBarAction(2, false, false) == InventoryUiLaw.BagBarClickAction.None,
    "bag-bar click routing drift");
Require(InventoryUiLaw.HoverCursor(true, true) == "Buy" &&
        InventoryUiLaw.HoverCursor(false, true) == "Inspect" &&
        InventoryUiLaw.HoverCursor(false, false) is null,
    "inventory hover cursor priority drift");

Require(InventoryUiLaw.ToWire(0, 0) == new InventoryUiLaw.WirePosition(255, 23) &&
        InventoryUiLaw.ToWire(0, 15) == new InventoryUiLaw.WirePosition(255, 38) &&
        InventoryUiLaw.ToWire(1, 0) == new InventoryUiLaw.WirePosition(19, 0) &&
        InventoryUiLaw.ToWire(4, 35) == new InventoryUiLaw.WirePosition(22, 35) &&
        InventoryUiLaw.ToWire(-2, 0) == new InventoryUiLaw.WirePosition(255, 81) &&
        InventoryUiLaw.ToWire(-2, 15) == new InventoryUiLaw.WirePosition(255, 96) &&
        InventoryUiLaw.ToWire(InventoryUiLaw.EquipmentContainer, 22) ==
            new InventoryUiLaw.WirePosition(255, 22) &&
        InventoryUiLaw.ToWire(-2, 16) is null && InventoryUiLaw.ToWire(0, 16) is null,
    "container-to-wire mapping drift");

Require(InventoryUiLaw.PushContainer(19, 0) == 1 && InventoryUiLaw.PushContainer(22, 0) == 4 &&
        InventoryUiLaw.PushContainer(255, 23) == 0 && InventoryUiLaw.PushContainer(255, 81) == -2 &&
        InventoryUiLaw.PushContainer(255, 112) == -2,
    "item-push destination selector drift");

InventoryUiLaw.BackgroundGeometry oneRow = InventoryUiLaw.Background(4);
InventoryUiLaw.BackgroundGeometry plusTwo = InventoryUiLaw.Background(6);
InventoryUiLaw.BackgroundGeometry twoRows = InventoryUiLaw.Background(8);
InventoryUiLaw.BackgroundGeometry modifiedLarge = InventoryUiLaw.Background(24);
Require(oneRow.Rows == 1 && oneRow.TopHeight == 86 && oneRow.MiddleHeight == 0 && oneRow.Height == 96,
    "one-row bag background drift");
Require(plusTwo.Rows == 2 && plusTwo.PlusTwo && plusTwo.TopHeight == 72 &&
        plusTwo.MiddleHeight == 32 && plusTwo.Height == 114 &&
        plusTwo.TopUvY == new System.Numerics.Vector2(.189453125f, .330078125f),
    "plus-two bag background drift");
Require(twoRows.TopHeight == 94 && twoRows.MiddleHeight == 32 && twoRows.Height == 136,
    "full-row bag background drift");
Require(modifiedLarge.Rows == 6 && modifiedLarge.MiddleHeight == 196 &&
        modifiedLarge.Height == 300 && InventoryUiLaw.ToWire(4, 23) is not null,
    "server-modified container capacities were truncated to stock client sizes");

InventoryUiLaw.SlotGeometry packFirst = InventoryUiLaw.Slot(16, 0, 240, true);
InventoryUiLaw.SlotGeometry packLast = InventoryUiLaw.Slot(16, 15, 240, true);
InventoryUiLaw.SlotGeometry sixFirst = InventoryUiLaw.Slot(6, 0, plusTwo.Height, false);
InventoryUiLaw.SlotGeometry sixSecond = InventoryUiLaw.Slot(6, 1, plusTwo.Height, false);
InventoryUiLaw.SlotGeometry sixThird = InventoryUiLaw.Slot(6, 2, plusTwo.Height, false);
Require(packFirst == new InventoryUiLaw.SlotGeometry(17, 50, 16) &&
        packLast == new InventoryUiLaw.SlotGeometry(143, 173, 1),
    "backpack live-slot/physical-button reversal drift");
Require(sixFirst == new InventoryUiLaw.SlotGeometry(101, 27, 6) &&
        sixSecond == new InventoryUiLaw.SlotGeometry(143, 27, 5) &&
        sixThird == new InventoryUiLaw.SlotGeometry(17, 68, 4),
    "right-aligned plus-two top row drift");

InventoryUiLaw.MovePlan invSwap = InventoryUiLaw.PlanMove(0, 0, 0, 1, null, 100, 200);
InventoryUiLaw.MovePlan bagSwap = InventoryUiLaw.PlanMove(1, 0, 0, 1, null, 100, 0);
InventoryUiLaw.MovePlan keySwap = InventoryUiLaw.PlanMove(-2, 0, 0, 0, null, 100, 0);
InventoryUiLaw.MovePlan split = InventoryUiLaw.PlanMove(0, 0, 1, 2, 5, 100, 100);
Require(invSwap.Kind == InventoryUiLaw.MoveKind.SwapInventory && invSwap.Source.Slot == 23 &&
        invSwap.Destination.Slot == 24, "backpack swap did not select CMSG_SWAP_INV_ITEM");
Require(bagSwap.Kind == InventoryUiLaw.MoveKind.SwapItems && bagSwap.Source.Bag == 19 &&
        bagSwap.Destination.Bag == 255, "bag move did not select destination-first CMSG_SWAP_ITEM");
Require(keySwap.Kind == InventoryUiLaw.MoveKind.SwapInventory && keySwap.Source.Slot == 81,
    "keyring/player-array move did not select CMSG_SWAP_INV_ITEM");
Require(split.Kind == InventoryUiLaw.MoveKind.Split && split.Count == 5 &&
        InventoryUiLaw.PlanMove(0, 0, 1, 2, 5, 100, 200).Kind == InventoryUiLaw.MoveKind.Refuse,
    "split placement accepted a different-item destination");
Require(InventoryUiLaw.PlanMove(0, 0, 0, 0, null, 100, 100).Kind == InventoryUiLaw.MoveKind.Cancel,
    "same-slot placement did not cancel the carry");

bool[] fourFull = [true, true, true, true, false, false, false, false];
Require(InventoryUiLaw.FirstEmptyKeyringSlot(39, fourFull) == -1 &&
        InventoryUiLaw.FirstEmptyKeyringSlot(40, fourFull) == 4,
    "keyring placement escaped or ignored its level gate");

Require(InventoryUiLaw.Money(0).SequenceEqual([new InventoryUiLaw.MoneyDenomination(2, 0)]) &&
        InventoryUiLaw.Money(20_000).SequenceEqual([new InventoryUiLaw.MoneyDenomination(0, 2)]) &&
        InventoryUiLaw.Money(10_203).SequenceEqual([
            new InventoryUiLaw.MoneyDenomination(2, 3),
            new InventoryUiLaw.MoneyDenomination(1, 2),
            new InventoryUiLaw.MoneyDenomination(0, 1)]),
    "SmallMoneyFrame zero-collapse or copper-to-gold purse order drift");

var stack = InventoryUiLaw.LayoutStack(500,
    [new(0, 240), new(1, 200), new(2, 200)]);
Require(stack.Count == 3 && stack[0].Column == 0 && stack[0].BottomOffset == 70 &&
        stack[1].Column == 1 && stack[1].RightOffset == 192 && stack[1].BottomOffset == 70 &&
        stack[2].Column == 1 && stack[2].BottomOffset == 270,
    "bag stack did not wrap and continue in the new 192px column");

InventoryUiLaw.ItemPushSample push0 = InventoryUiLaw.SampleItemPush(0);
InventoryUiLaw.ItemPushSample push133 = InventoryUiLaw.SampleItemPush(.133f);
InventoryUiLaw.ItemPushSample push500 = InventoryUiLaw.SampleItemPush(.5f);
Require(push0.Visible && push0.Alpha == 0 && push0.Size == 36 &&
        push0.Offset == new System.Numerics.Vector2(-12, -48), "item-push initial sample drift");
Require(MathF.Abs(push133.Alpha - 1) < .001f && MathF.Abs(push133.Size - 43.2f) < .001f &&
        push133.Offset == new System.Numerics.Vector2(-12, -48), "item-push 133ms keyframe drift");
Require(push500.Visible && push500.Offset == new System.Numerics.Vector2(-12, -48) &&
        !InventoryUiLaw.SampleItemPush(1).Visible, "item-push drop clamp/duration drift");

Require((ushort)Op.CMSG_SPLIT_ITEM == 0x010E, "CMSG_SPLIT_ITEM opcode drift");
Require(WorldSession.BuildSplitItemBody(19, 2, 255, 25, 5)
        .SequenceEqual(Convert.FromHexString("1302FF1905")),
    "CMSG_SPLIT_ITEM source/destination/count body drift");
Require(WorldSession.BuildSwapInventoryBody(23, 24).SequenceEqual(Convert.FromHexString("1718")) &&
        WorldSession.BuildSwapItemsBody(255, 24, 19, 2).SequenceEqual(Convert.FromHexString("FF181302")),
    "container swap packet ordering drift");

var itemWriter = new PacketWriter();
itemWriter.WriteU32(123); itemWriter.WriteU32(1); itemWriter.WriteU32(0);
itemWriter.WriteCString("Copper Key"); itemWriter.WriteCString(""); itemWriter.WriteCString(""); itemWriter.WriteCString("");
foreach (uint value in new uint[] { 1, 1, 0, 0, 0, 0 }) itemWriter.WriteU32(value);
itemWriter.WriteI32(-1); itemWriter.WriteI32(-1);
for (int i = 0; i < 9; i++) itemWriter.WriteU32(0);
itemWriter.WriteU32(1); itemWriter.WriteU32(20); itemWriter.WriteU32(0);
for (int i = 0; i < 10; i++) { itemWriter.WriteU32(0); itemWriter.WriteI32(0); }
for (int i = 0; i < 5; i++) { itemWriter.WriteF32(0); itemWriter.WriteF32(0); itemWriter.WriteU32(0); }
itemWriter.WriteU32(0); for (int i = 0; i < 6; i++) itemWriter.WriteU32(0);
itemWriter.WriteU32(0); itemWriter.WriteU32(0); itemWriter.WriteF32(0);
for (int i = 0; i < 5; i++)
{ itemWriter.WriteU32(0); itemWriter.WriteU32(0); itemWriter.WriteI32(0); itemWriter.WriteI32(0); itemWriter.WriteU32(0); itemWriter.WriteI32(0); }
itemWriter.WriteU32(0); itemWriter.WriteCString("");
for (int i = 0; i < 5; i++) itemWriter.WriteU32(0);
itemWriter.WriteU32(0); itemWriter.WriteU32(0);
for (int i = 0; i < 6; i++) itemWriter.WriteU32(0);
itemWriter.WriteU32(9);
ItemTemplate parsedKey = ItemTemplate.Parse(itemWriter.ToArray()) ?? throw new InvalidDataException("key template parse failed");
Require(parsedKey.BagFamily == 9, "ItemQuery BagFamily did not reach key detection");

string dataRoot = Path.Combine(ClientConfig.FindRepoRoot(), "GameData", "Data");
using var mpq = new MpqMount(dataRoot);
foreach (string asset in new[]
{
    @"Interface\Buttons\UI-Quickslot2.blp",
    @"Interface\Buttons\UI-Quickslot-Depress.blp",
    @"Interface\Buttons\UI-Button-KeyRing.blp",
    @"Interface\ContainerFrame\UI-Bag-Components.blp",
    @"Interface\ContainerFrame\UI-Bag-Components-Keyring.blp",
    @"Interface\ContainerFrame\KeyRing-Bag-Icon.blp",
    @"Interface\Cursor\Buy.blp",
    @"Interface\Cursor\Inspect.blp",
}) Require(mpq.ReadFile(asset) is not null, $"required inventory asset absent: {asset}");
SoundEntriesCatalog sounds = SoundEntriesCatalog.Load(mpq) ?? throw new InvalidDataException("SoundEntries unavailable");
foreach (string cue in new[] { "igBackPackOpen", "igBackPackClose", "KeyRingOpen", "KeyRingClose" })
    Require(sounds.TryGet(cue, out SoundEntry entry) && entry.Variants.Count > 0,
        $"required bag lifecycle sound absent: {cue}");

Console.WriteLine("inventory-parity-check PASS");

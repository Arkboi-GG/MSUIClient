using System.Numerics;

namespace MSUIClient.Engine.UI;

/// <summary>
/// Build-5875 ContainerFrame/MainMenuBarBagButtons laws shared by the live inventory UI and
/// deterministic verification. Container ids use the FrameXML space: -1 bank, 0 backpack,
/// 1..4 equipped bags, -2 keyring, and <see cref="EquipmentContainer"/> for
/// paper-doll/bag-bar inventory slots.
/// </summary>
public static class InventoryUiLaw
{
    public const int EmptyContainer = int.MinValue;
    public const int EquipmentContainer = -100;
    public const int BankBagEquipmentContainer = -101;
    public const int BankContainer = -1;
    public const int KeyringContainer = -2;
    public const byte PlayerInventoryBag = 255;
    public const int BackpackSlots = 16;
    public const int BankSlots = 24;
    public const int BankBagContainerFirst = 5;
    public const int BankBagContainerLast = 10;
    public const int BankBagWireFirst = 63;
    public const int BankBagCount = 6;
    public const int KeyringAddressableSlots = 16;
    public const int MaxContainerSlots = 36;
    public const int KeyringWireFirst = 81;
    public const float ContainerWidth = 192f;
    public const float ContainerOffsetY = 70f;
    public const float VisibleContainerSpacing = 3f;
    public const uint ItemFlagLootable = 0x0000_0004;
    public const uint ItemFlagWrapper = 0x0000_0200;
    public const uint ItemDynamicUnlocked = 0x0000_0004;
    public const uint ItemDynamicWrapped = 0x0000_0008;
    public const string KeyringNormalTexture = @"Interface\Buttons\UI-Button-KeyRing";
    public const string KeyringPushedTexture = @"Interface\Buttons\UI-Button-KeyRing-Down";
    public const string KeyringHighlightTexture = @"Interface\Buttons\UI-Button-KeyRing-Highlight";
    public static readonly Vector2 KeyringUvMaximum = new(0.5625f, 0.609375f);

    public static string KeyringStateTexture(bool pushed) =>
        pushed ? KeyringPushedTexture : KeyringNormalTexture;

    public readonly record struct WirePosition(byte Bag, byte Slot);
    public readonly record struct BackgroundGeometry(
        int Rows, bool PlusTwo, float TopHeight, float MiddleHeight, float BottomHeight,
        Vector2 TopUvY, Vector2 MiddleUvY, Vector2 BottomUvY)
    {
        public float Height => TopHeight + MiddleHeight + BottomHeight;
    }
    public readonly record struct SlotGeometry(float X, float Y, int PhysicalIndex);
    public readonly record struct StackWindow(int Container, float Height);
    public readonly record struct StackPlacement(int Container, int Column, float RightOffset,
        float BottomOffset, float Height);
    public readonly record struct ItemPushSample(bool Visible, float Alpha, float Size,
        Vector2 Offset);
    public readonly record struct TooltipSeat(Vector2 Position, Vector2 Pivot,
        string Point, string RelativePoint);
    public readonly record struct ProficiencyColors(bool SlotRed, bool TypeRed);
    public readonly record struct MoneyDenomination(int Index, uint Value);
    public enum MoveKind { Refuse, Cancel, SwapInventory, SwapItems, Split }
    public enum BagBindingAction { ToggleBackpack, ToggleAllBags }
    public enum SlotClickAction { None, TradePlace, Split, PickupOrPlace, ClearCarried, ContextAction }
    public enum BagBarClickAction { None, ToggleBackpack, ToggleBag, PickupOrPlace }
    public readonly record struct MovePlan(MoveKind Kind, WirePosition Source,
        WirePosition Destination, byte Count);

    public static int KeyringSize(uint level) => level switch
    {
        >= 61 => 16,
        >= 50 => 12,
        >= 40 => 8,
        _ => 4,
    };

    /// <summary>
    /// The reference's Disable_BagButtons list contains the backpack and four equipped-bag
    /// buttons. The keyring button is intentionally not visually disabled by that routine.
    /// </summary>
    public static bool DisableWithGameMenu(int container) => container is >= 0 and <= 4;

    public static bool ShouldOpenAllBags(bool backpackOpen, IReadOnlyList<bool> equippedOpen) =>
        !backpackOpen && !equippedOpen.Any(open => open);

    /// <summary>Vanilla's B/Shift+B split: B toggles bag 0; Shift+B toggles bags 0..4.</summary>
    public static BagBindingAction BindingAction(bool shiftDown) => shiftDown
        ? BagBindingAction.ToggleAllBags
        : BagBindingAction.ToggleBackpack;

    /// <summary>
    /// The rule for MSUI's OWN dedicated all-bags key (default I). Open every bag the character
    /// actually carries; close them only once every one of them is already open.
    ///
    /// Deliberately NOT <see cref="ShouldOpenAllBags"/>. Vanilla's Shift+B closes everything the
    /// moment ANY window is open, which is coherent for a modifier on the backpack key but wrong
    /// for a key whose whole job is "show me my inventory": with the backpack already open from B,
    /// the first press of the dedicated key CLOSED it instead of opening the rest, so B looked
    /// like the full-inventory key and I looked like it only ever managed the backpack. Reported
    /// 2026-08-26.
    ///
    /// <paramref name="equippedExists"/> is what keeps that honest - an empty bag slot must not
    /// count as "not yet open", or a character carrying no bags could never satisfy the close
    /// condition and the key would never toggle off.
    /// </summary>
    public static bool ShouldOpenEveryCarriedBag(bool backpackOpen,
        IReadOnlyList<bool> equippedOpen, IReadOnlyList<bool> equippedExists)
    {
        if (!backpackOpen) return true;
        int count = Math.Min(equippedOpen.Count, equippedExists.Count);
        for (int bag = 0; bag < count; bag++)
            if (equippedExists[bag] && !equippedOpen[bag]) return true;
        return false;
    }

    public static SlotClickAction ClickAction(bool left, bool right, bool shift, bool hasCarried,
        bool hasInstance, uint stackCount, bool locked, bool tradePlacement) =>
        left && tradePlacement ? SlotClickAction.TradePlace :
        left && !hasCarried && shift && hasInstance && stackCount >= 2 && !locked
            ? SlotClickAction.Split :
        left ? SlotClickAction.PickupOrPlace :
        right && hasCarried ? SlotClickAction.ClearCarried :
        right && hasInstance ? SlotClickAction.ContextAction : SlotClickAction.None;

    public static BagBarClickAction BagBarAction(int container, bool hasCarried, bool occupied) =>
        container == 0 ? BagBarClickAction.ToggleBackpack :
        hasCarried ? BagBarClickAction.PickupOrPlace :
        occupied ? BagBarClickAction.ToggleBag : BagBarClickAction.None;

    public static string? HoverCursor(bool merchantOpen, bool readable) => merchantOpen
        ? "Buy" : readable ? "Inspect" : null;

    /// <summary>The build-5875 INVTYPE_* GlobalString vocabulary used by item tooltips.</summary>
    public static string? InventoryTypeName(uint type) => type switch
    {
        1 => "Head", 2 => "Neck", 3 => "Shoulder", 4 => "Shirt",
        5 or 20 => "Chest", 6 => "Waist", 7 => "Legs", 8 => "Feet",
        9 => "Wrist", 10 => "Hands", 11 => "Finger", 12 => "Trinket",
        13 => "One-Hand", 14 or 22 => "Off Hand", 15 or 26 => "Ranged",
        16 => "Back", 17 => "Two-Hand", 19 => "Tabard", 21 => "Main Hand",
        23 => "Held In Off-hand", 24 => "Projectile", 25 => "Thrown",
        28 => "Relic", _ => null,
    };

    /// <summary>
    /// The tooltip's two proficiency cells recolor independently. A missing own-subclass bit
    /// normally reds the type. For weapons only, a permitted ItemSubClass alternate covers the
    /// type but reds the slot; an off-hand weapon also reds its slot until Dual Wield is known.
    /// No class entry means the server has not declared a restriction and leaves both white.
    /// </summary>
    public static ProficiencyColors ItemProficiencyColors(
        uint itemClass,
        uint subclass,
        uint inventoryType,
        IReadOnlyDictionary<uint, uint> proficiencies,
        uint? weaponAlternative,
        bool canDualWield)
    {
        ArgumentNullException.ThrowIfNull(proficiencies);
        bool slotRed = inventoryType == 22 && !canDualWield;
        bool typeRed = false;
        if (proficiencies.TryGetValue(itemClass, out uint mask) &&
            (subclass >= 32 || (mask & (1u << (int)subclass)) == 0))
        {
            bool alternativeAllowed = itemClass == 2 &&
                weaponAlternative is uint alternative && alternative < 32 &&
                (mask & (1u << (int)alternative)) != 0;
            if (alternativeAllowed) slotRed = true;
            else typeRed = true;
        }
        return new(slotRed, typeRed);
    }

    public static bool IsItemProficient(uint itemClass, uint subclass,
        IReadOnlyDictionary<uint, uint> proficiencies) =>
        !proficiencies.TryGetValue(itemClass, out uint mask) ||
        subclass < 32 && (mask & (1u << (int)subclass)) != 0;

    public static bool UnwrapsGift(uint templateFlags, uint instanceFlags) =>
        (templateFlags & ItemFlagWrapper) != 0 &&
        (instanceFlags & ItemDynamicWrapped) != 0;

    /// <summary>The click's deliberately bare LOOTABLE test; locked boxes still reach the server.</summary>
    public static bool OpensLoot(uint templateFlags) =>
        (templateFlags & ItemFlagLootable) != 0;

    /// <summary>The tooltip promise is narrower than the click: a locked box needs UNLOCKED.</summary>
    public static bool ShowsOpenLine(uint templateFlags, uint lockId, uint instanceFlags) =>
        OpensLoot(templateFlags) && (lockId == 0 || (instanceFlags & ItemDynamicUnlocked) != 0) ||
        UnwrapsGift(templateFlags, instanceFlags);

    /// <summary>
    /// ContainerFrameItemButton_OnEnter: slots in the right screen half use ANCHOR_LEFT
    /// (tooltip TOPRIGHT to slot TOPLEFT); slots in the left half use ANCHOR_RIGHT.
    /// </summary>
    public static TooltipSeat ItemTooltipSeat(Vector2 slotMinimum, Vector2 slotMaximum,
        float screenWidth) => slotMaximum.X >= MathF.Max(0f, screenWidth) * .5f
        ? new(slotMinimum, new Vector2(1f, 0f), "TOPRIGHT", "TOPLEFT")
        : new(new Vector2(slotMaximum.X, slotMinimum.Y), Vector2.Zero,
            "TOPLEFT", "TOPRIGHT");

    public static WirePosition? ToWire(int container, int slot)
    {
        if (slot < 0) return null;
        return container switch
        {
            BankContainer when slot < BankSlots =>
                new(PlayerInventoryBag, (byte)(39 + slot)),
            >= BankBagContainerFirst and <= BankBagContainerLast when slot < MaxContainerSlots =>
                new((byte)(BankBagWireFirst + container - BankBagContainerFirst), (byte)slot),
            BankBagEquipmentContainer when slot < BankBagCount =>
                new(PlayerInventoryBag, (byte)(BankBagWireFirst + slot)),
            0 when slot < BackpackSlots => new(PlayerInventoryBag, (byte)(23 + slot)),
            >= 1 and <= 4 when slot < MaxContainerSlots => new((byte)(18 + container), (byte)slot),
            KeyringContainer when slot < KeyringAddressableSlots =>
                new(PlayerInventoryBag, (byte)(KeyringWireFirst + slot)),
            EquipmentContainer when slot < 23 => new(PlayerInventoryBag, (byte)slot),
            _ => null,
        };
    }

    /// <summary>SMSG_ITEM_PUSH_RESULT wire destination to the bag-button container id.</summary>
    public static int PushContainer(byte wireBag, uint wireSlot)
    {
        if (wireBag != PlayerInventoryBag) return wireBag - 18;
        return wireSlot is >= 81 and <= 112 ? KeyringContainer : 0;
    }

    public static MovePlan PlanMove(int sourceContainer, int sourceSlot, int destinationContainer,
        int destinationSlot, int? splitCount, uint sourceEntry, uint destinationEntry)
    {
        WirePosition? source = ToWire(sourceContainer, sourceSlot);
        WirePosition? destination = ToWire(destinationContainer, destinationSlot);
        if (source is null || destination is null) return default;
        if (sourceContainer == destinationContainer && sourceSlot == destinationSlot)
            return new(MoveKind.Cancel, source.Value, destination.Value, 0);
        if (splitCount is int count)
        {
            if (destinationEntry != 0 && destinationEntry != sourceEntry) return default;
            return new(MoveKind.Split, source.Value, destination.Value,
                (byte)Math.Clamp(count, 1, byte.MaxValue));
        }
        MoveKind kind = source.Value.Bag == PlayerInventoryBag &&
                        destination.Value.Bag == PlayerInventoryBag
            ? MoveKind.SwapInventory : MoveKind.SwapItems;
        return new(kind, source.Value, destination.Value, 0);
    }

    public static int FirstEmptyKeyringSlot(uint level, IReadOnlyList<bool> occupied)
    {
        int size = Math.Min(KeyringSize(level), occupied.Count);
        for (int i = 0; i < size; i++) if (!occupied[i]) return i;
        return -1;
    }

    /// <summary>
    /// SmallMoneyFrame purse order: visible nonzero denominations pack copper-to-gold from the
    /// right; zero displays one copper slot instead of an empty purse. Index 0/1/2 is gold/silver/copper.
    /// </summary>
    public static IReadOnlyList<MoneyDenomination> Money(uint copper)
    {
        uint[] values = [copper / 10_000, copper / 100 % 100, copper % 100];
        var result = new List<MoneyDenomination>(3);
        for (int denomination = 2; denomination >= 0; denomination--)
            if (values[denomination] > 0 || denomination == 2 && values[0] == 0 && values[1] == 0)
                result.Add(new(denomination, values[denomination]));
        return result;
    }

    public static BackgroundGeometry Background(int size)
    {
        int rows = Math.Max(1, (Math.Max(0, size) + 3) / 4);
        bool plusTwo = size > 0 && size % 4 == 2;
        float top = plusTwo ? 72f : rows == 1 ? 86f : 94f;
        Vector2 topUv = plusTwo
            ? new(0.189453125f, 0.330078125f)
            : rows == 1
                ? new(0.00390625f, 0.16796875f)
                : new(0.00390625f, 0.18359375f);
        float middle = rows > 1 ? (rows - 1) * 41f - 9f : 0f;
        return new(rows, plusTwo, top, middle, 10f, topUv,
            new(0.353515625f, 0.353515625f + middle / 512f),
            new(0.330078125f, 0.349609375f));
    }

    /// <summary>
    /// Position of a zero-based live container slot inside a 192-wide frame. Physical button 1 is
    /// bottom-right; live slot 1 maps to physical button <paramref name="size"/>, producing the
    /// reference's right-aligned partial top row for 6/10/14/18-slot bags.
    /// </summary>
    public static SlotGeometry Slot(int size, int slot, float frameHeight, bool backpack)
    {
        if (slot < 0 || slot >= size) throw new ArgumentOutOfRangeException(nameof(slot));
        int physical = size - slot;
        int rowFromBottom = (physical - 1) / 4;
        int columnFromRight = (physical - 1) % 4;
        float bottomOffset = backpack ? 30f : 9f;
        return new(143f - columnFromRight * 42f,
            frameHeight - bottomOffset - 37f - rowFromBottom * 41f, physical);
    }

    public static IReadOnlyList<StackPlacement> LayoutStack(float screenHeight,
        IReadOnlyList<StackWindow> windows)
    {
        var result = new List<StackPlacement>(windows.Count);
        float free = screenHeight - ContainerOffsetY;
        int column = 0;
        float previousTop = ContainerOffsetY;
        for (int i = 0; i < windows.Count; i++)
        {
            StackWindow window = windows[i];
            float bottom;
            if (i == 0)
            {
                bottom = ContainerOffsetY;
            }
            else if (free < window.Height)
            {
                column++;
                free = screenHeight - ContainerOffsetY;
                bottom = ContainerOffsetY;
            }
            else
            {
                bottom = previousTop;
            }
            result.Add(new(window.Container, column, column * ContainerWidth, bottom, window.Height));
            previousTop = bottom + window.Height;
            free -= window.Height + VisibleContainerSpacing;
        }
        return result;
    }

    public static ItemPushSample SampleItemPush(float elapsed)
    {
        if (elapsed < 0f || elapsed >= 1f) return new(false, 0f, 0f, Vector2.Zero);
        float alpha = Sample(elapsed, (0f, 0f), (.133f, 1f), (.267f, 1f), (1f, 0f));
        float scale = Sample(elapsed, (0f, 1f), (.133f, 1.2f), (.267f, 1f), (1f, .014f));
        float drop = Sample(elapsed, (0f, 0f), (.5f, 0f), (1f, 1f));
        return new(true, alpha, 36f * scale, new Vector2(12f * (drop - 1f), -48f * (1f - drop)));
    }

    private static float Sample(float t, params (float Time, float Value)[] keys)
    {
        if (t <= keys[0].Time) return keys[0].Value;
        for (int i = 1; i < keys.Length; i++)
        {
            if (t > keys[i].Time) continue;
            (float pt, float pv) = keys[i - 1];
            (float kt, float kv) = keys[i];
            float span = kt - pt;
            return span <= 0f ? kv : pv + (kv - pv) * (t - pt) / span;
        }
        return keys[^1].Value;
    }
}

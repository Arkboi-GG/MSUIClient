namespace MSUIClient.Engine.UI;

public enum BottomMultiActionBar { Left, Right }
public enum MultiActionItemRoute { None, Use, Equip }
public enum MultiActionItemUseDisposition { Nothing, QuestOffer, ToggleCancel, Use }

public readonly record struct MultiActionKeyTransition(bool Armed, bool Fire);
public readonly record struct MultiActionPlacement(uint DestinationPacked, uint CursorPacked);

public static class MultiActionBarUiLaw
{
    public const int ButtonsPerBar = 12;
    public const int BottomLeftBase = 60;
    public const int BottomRightBase = 48;
    public const float FrameWidth = 500;
    public const float FrameHeight = 38;
    public const float ButtonSize = 36;
    public const float ButtonStep = 42;
    public const float BottomLeftRise = 17;
    public const float BottomBarGap = 10;

    public static int Base(BottomMultiActionBar bar) => bar == BottomMultiActionBar.Left
        ? BottomLeftBase : BottomRightBase;

    public static int WireSlot(BottomMultiActionBar bar, int buttonIndex) =>
        Base(bar) + Math.Clamp(buttonIndex, 0, ButtonsPerBar - 1);

    public static bool ShowEmptyWell(bool cursorPayloadHeld) => cursorPayloadHeld;

    /// <summary>
    /// An authored button does not exist as a mouse target while it is empty and the action-bar
    /// grid is hidden. The parent frame itself is not mouse-enabled.
    /// </summary>
    public static bool InteractiveSlot(bool hasAction, bool cursorPayloadHeld) =>
        hasAction || cursorPayloadHeld;

    public static bool InHorizontalButton(float x, float y, float frameX, float frameY)
    {
        float localX = x - frameX;
        float localY = y - (frameY + 2);
        if (localY < 0 || localY >= ButtonSize || localX < 0) return false;
        int index = (int)(localX / ButtonStep);
        if (index is < 0 or >= ButtonsPerBar) return false;
        return localX - index * ButtonStep < ButtonSize;
    }

    /// <summary>
    /// Multi-action bindings are edge-up/down commands. A press is armed only on an eligible
    /// rising edge; once armed, the matching release still fires if chat focus appears while the
    /// key is held. Conversely, a press that began while typing can never become armed merely
    /// because focus later leaves the edit box.
    /// </summary>
    public static MultiActionKeyTransition AdvanceKey(
        bool armed, bool wasDown, bool isDown, bool typing, bool inWorld)
    {
        if (!inWorld) return new(false, false);
        if (!wasDown && isDown) return new(!typing, false);
        if (wasDown && !isDown) return new(false, armed);
        return new(armed, false);
    }

    /// <summary>Reference item cursor acceptance: usable OR equippable; no quality/class gate.</summary>
    public static bool ItemMayBePlaced(uint inventoryType, uint onUseSpellId) =>
        inventoryType != 0 || onUseSpellId != 0;

    /// <summary>
    /// GetActionCount is visible only for consumable actions. Ammo/thrown items are consumable by
    /// inventory type; other items require at least one ON_USE spell block with negative charges.
    /// </summary>
    public static bool ShowItemCount(uint inventoryType, bool hasNegativeOnUseCharges) =>
        inventoryType is 24 or 25 || hasNegativeOnUseCharges;

    public static MultiActionItemRoute ItemUseRoute(
        uint inventoryType, bool equippedCopy, bool anyCopy)
    {
        if (inventoryType == 0) return anyCopy
            ? MultiActionItemRoute.Use : MultiActionItemRoute.None;
        if (equippedCopy) return MultiActionItemRoute.Use;
        return anyCopy ? MultiActionItemRoute.Equip : MultiActionItemRoute.None;
    }

    /// <summary>
    /// CGItem::Use forks before the ordinary CMSG_USE_ITEM tail. A quest-starting item opens its
    /// offer first; an ON_USE spell with a nonzero ActiveIconID cancels its own cancelable aura;
    /// an item with no ON_USE block sends nothing instead of provoking a server error.
    /// </summary>
    public static MultiActionItemUseDisposition ItemUseDisposition(
        uint startQuest, uint onUseSpellId, uint activeIconId, bool matchingCancelableAura)
    {
        if (startQuest != 0) return MultiActionItemUseDisposition.QuestOffer;
        if (onUseSpellId == 0) return MultiActionItemUseDisposition.Nothing;
        if (activeIconId != 0 && matchingCancelableAura)
            return MultiActionItemUseDisposition.ToggleCancel;
        return MultiActionItemUseDisposition.Use;
    }

    public static bool RequiresLiveCharges(int spellCharges0) =>
        spellCharges0 != 0 && spellCharges0 != -1;

    public static bool LiveChargeCandidate(bool isContainer, int? remainingCharges) =>
        isContainer || remainingCharges is null or not 0;

    public static MultiActionPlacement PickupAction(uint sourcePacked) =>
        new(0, sourcePacked);

    /// <summary>Occupied destinations hop to the cursor; empty destinations clear it.</summary>
    public static MultiActionPlacement PlaceAction(uint heldPacked, uint destinationPacked) =>
        new(heldPacked, destinationPacked);
}

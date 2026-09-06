using MSUIClient.Engine;
using MSUIClient.Engine.UI;
using MSUIClient.Net;
using Silk.NET.Input;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private sealed record GiftWrapCursor(ulong Owner, ulong Paper, uint Entry, int Container, int Slot);
    private GiftWrapCursor? _giftWrap;

    private void CancelGiftWrapping() => _giftWrap = null;

    private bool TryArmGiftWrapping(int container, int slot)
    {
        // Core HandleWrapItemOpcode still uses _player throughout. Never submit a
        // companion's bag addresses to that session-only handler.
        if (!CanAuthorSessionInventory || _net is not { IsInWorld: true } ||
            container is < 0 or > 4 || HasCarriedItem || _vendorRepairMode ||
            _rtsUnitCastSpellId != 0 || _tacticalGroundSpellId != 0 ||
            IsInventorySlotLocked(container, slot) ||
            ResolveInventoryItem(container, slot) is not { } paper ||
            _items?.TryGet(paper.Entry, out ItemTemplate? template) != true || template is null ||
            !GiftWrapLaw.IsPaper(template, paper.Fields.ItemFlags, paper.Fields.ItemGiftCreator) ||
            RefuseTacticalFreezeLiveCommand("wrapping an item")) return false;
        CancelItemTargeting();
        _groundCastSpell = 0;
        _groundCursorPoint = null;
        CancelStackSplit();
        _giftWrap = new(ControlledGuid, paper.Guid, paper.Entry, container, slot);
        return true;
    }

    private bool TryWrapGift(int container, int slot)
    {
        if (_giftWrap is not { } cursor) return false;
        if (cursor.Owner != ControlledGuid || !CanAuthorSessionInventory)
        { CancelGiftWrapping(); return false; }
        if (_net is not { IsInWorld: true } ||
            RefuseTacticalFreezeLiveCommand("wrapping an item")) return false;
        WorldEntity? paper = ResolveInventoryItem(cursor.Container, cursor.Slot);
        if (paper is null || paper.Guid != cursor.Paper || paper.Entry != cursor.Entry ||
            _items?.TryGet(paper.Entry, out ItemTemplate? paperTemplate) != true || paperTemplate is null ||
            !GiftWrapLaw.IsPaper(paperTemplate, paper.Fields.ItemFlags, paper.Fields.ItemGiftCreator))
        { CancelGiftWrapping(); return false; }
        if (IsInventorySlotLocked(cursor.Container, cursor.Slot) ||
            IsInventorySlotLocked(container, slot) || ResolveInventoryItem(container, slot) is not { } item ||
            _items?.TryGet(item.Entry, out ItemTemplate? template) != true || template is null) return false;
        string? refusal = GiftWrapLaw.Refusal(item.Guid == paper.Guid,
            container == InventoryUiLaw.EquipmentContainer, item.Fields.ItemGiftCreator,
            template, item.Fields.ItemFlags,
            _castBarPhase is CastBarPhase.Casting or CastBarPhase.Channel || _pendingCastSpell != 0);
        if (refusal is not null) { ShowUiError(refusal); return false; }
        // Limit the UI to carried inventory; don't turn an open bank into a remote
        // gift workflow. Resolve both addresses again instead of retaining a target GUID.
        if (container is < 0 or > 4 || InventoryUiLaw.ToWire(cursor.Container, cursor.Slot) is not { } source ||
            InventoryUiLaw.ToWire(container, slot) is not { } target) return false;
        if (!_net.WrapItem(source.Bag, source.Slot, target.Bag, target.Slot)) return false;
        CancelGiftWrapping();
        // No optimistic item-entry change, paper consumption or success notification.
        return true;
    }

    private bool TryHandleGiftWorldClick(WorldMouseClick click)
    {
        if (_giftWrap is null) return false;
        if (click.Button == MouseButton.Right) CancelGiftWrapping();
        else ShowUiError("Choose an item in your bags to wrap.");
        return true;
    }
}

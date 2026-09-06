using MSUIClient.Engine.UI;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private sealed record VendorPickup(ulong Owner, ulong Vendor, uint Item, uint Display, uint Stack);
    private VendorPickup? _vendorPickup;

    private bool PickupVendorEntry(uint entry)
    {
        if (!CanAuthorControlledOrSelf || _vendor is null || _vendorRepairMode ||
            HasCarriedItem || _giftWrap is not null || _itemCastSpell != 0 || !IsVendorServiceAvailable(_vendor.VendorGuid, out _, out _) ||
            RefuseTacticalFreezeLiveCommand("picking up vendor merchandise") ||
            RefuseTacticalFrozenActor(_vendor.VendorGuid, "pick up its merchandise")) return false;
        VendorItem row = _vendor.Items.FirstOrDefault(x => x.ItemId == entry);
        if (row.ItemId == 0 || row.Available == 0) return false;
        _vendorPickup = new(ControlledGuid, _vendor.VendorGuid, row.ItemId, row.DisplayId, row.BuyCount);
        return true;
    }

    private bool PlaceVendorPurchase(int container, int slot)
    {
        if (_vendorPickup is not { } pickup) return false;
        if (pickup.Owner != ControlledGuid || _vendor?.VendorGuid != pickup.Vendor ||
            !IsVendorServiceAvailable(pickup.Vendor, out _, out _))
        { _vendorPickup = null; return false; }
        if (!CanAuthorControlledOrSelf || _net is null ||
            !_entities.TryGet(pickup.Owner, out WorldEntity actor) ||
            !_vendor.Items.Any(r => r.ItemId == pickup.Item && r.Available != 0) ||
            RefuseTacticalFreezeLiveCommand("buying into a bag slot") ||
            RefuseTacticalFrozenActor(pickup.Vendor, "buy from it") ||
            IsInventorySlotLocked(container, slot) ||
            InventoryUiLaw.ToWire(container, slot) is not { } destination) return false;
        ulong bagGuid;
        if (container == 0 || container == InventoryUiLaw.EquipmentContainer)
            bagGuid = pickup.Owner;
        else if (container is >= 1 and <= 4)
        {
            bagGuid = actor.Fields.PlayerInventorySlot(18 + container);
            if (bagGuid == 0 || !_entities.TryGet(bagGuid, out WorldEntity bag) ||
                (uint)slot >= bag.Fields.ContainerNumSlots) return false;
        }
        else return false;
        if (!_net.BuyItemInSlot(pickup.Vendor, pickup.Item, bagGuid, destination.Slot, 1)) return false;
        // The server owns stock, coin and inventory changes, including merge/equip refusal.
        _vendorPickup = null;
        EmitInterface("vendor", "buy-in-slot", "SENT", pickup.Owner,
            $"vendor=0x{pickup.Vendor:X};item={pickup.Item};bag=0x{bagGuid:X};slot={destination.Slot};count=1");
        return true;
    }
}

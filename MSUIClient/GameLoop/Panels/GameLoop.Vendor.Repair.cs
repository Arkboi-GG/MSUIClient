using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private DurabilityTables? _durabilityTables;
    private bool _durabilityTablesLoaded;

    private DurabilityTables? MerchantDurabilityTables()
    {
        if (_durabilityTablesLoaded) return _durabilityTables;
        _durabilityTablesLoaded = true;
        if (_mpq is null) return null;
        try { _durabilityTables = DurabilityTables.Load(_mpq); }
        catch { _durabilityTables = null; }
        return _durabilityTables;
    }

    private uint MerchantRepairItemCost(WorldEntity item)
    {
        if (MerchantDurabilityTables() is not { } tables || _items is null)
            return 0;
        if (!_items.TryGet(item.Entry, out ItemTemplate? template) || template is null)
        {
            if (_net is not null) _items.Require(item.Entry, item.Guid, _net);
            return 0;
        }
        return tables.RepairCost(new MerchantFrameUiLaw.RepairItem(
            template.Class, template.Subclass, template.ItemLevel, template.Quality,
            item.Fields.ItemDurability, item.Fields.ItemMaxDurability));
    }

    private uint ComputeVendorRepairAllCost(WorldEntity player)
    {
        ulong total = 0;
        var seen = new HashSet<ulong>();
        void Add(ulong guid)
        {
            if (guid == 0 || !seen.Add(guid) || !_entities.TryGet(guid, out WorldEntity item))
                return;
            total += MerchantRepairItemCost(item);
        }

        for (int slot = 0; slot < 19; slot++) Add(player.Fields.PlayerInventorySlot(slot));
        for (int slot = 0; slot < 16; slot++) Add(player.Fields.PlayerBackpackSlot(slot));
        for (int bagSlot = 19; bagSlot < 23; bagSlot++)
        {
            ulong bagGuid = player.Fields.PlayerInventorySlot(bagSlot);
            if (bagGuid == 0 || !_entities.TryGet(bagGuid, out WorldEntity bag)) continue;
            int slots = (int)Math.Min(36u, bag.Fields.ContainerNumSlots);
            for (int slot = 0; slot < slots; slot++) Add(bag.Fields.ContainerSlot(slot));
        }
        return (uint)Math.Min(total, uint.MaxValue);
    }

    private bool VendorCanRepair()
        => _vendor is { } inventory &&
           _entities.TryGet(inventory.VendorGuid, out WorldEntity vendor) &&
           (vendor.NpcFlags & MerchantFrameUiLaw.RepairVendorNpcFlag) != 0;

    private bool TryRepairMerchantItem(ulong itemGuid)
    {
        if (!_vendorRepairMode) return false;
        if (_vendor is null || itemGuid == 0 || !VendorCanRepair()) return true;
        _net?.RepairItem(_vendor.VendorGuid, itemGuid);
        return true;
    }

    private bool RepairAllVendorItems(WorldEntity player)
    {
        if (_vendor is null || _net is null || !VendorCanRepair() ||
            ComputeVendorRepairAllCost(player) == 0)
            return false;
        return _net.RepairItem(_vendor.VendorGuid, 0);
    }
}

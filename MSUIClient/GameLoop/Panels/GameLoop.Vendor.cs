using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private VendorInventory? _vendor;
    private int _vendorPage = 1;
    private int _vendorTab;

    private bool RequestVendor(ulong guid)
    {
        string outcome = "REFUSED";
        string detail = "descriptorMissing";
        WorldEntity? npc = null;
        float distance = float.PositiveInfinity;
        if (_net is { IsInWorld: true } &&
            IsVendorServiceAvailable(guid, out npc, out distance))
        {
            bool sent = _net.ListInventory(guid);
            outcome = sent ? "SENT" : "SEND_FAILED";
            detail = $"distance={distance:R};npcFlags=0x{npc!.NpcFlags:X8}";
        }
        else if (npc is not null)
        {
            outcome = "REFUSED_RANGE";
            detail = $"distance={distance:R};limit={VendorServiceDistance:R}";
        }
        EmitInterface("vendor", "list", outcome, guid, detail);
        return outcome == "SENT";
    }

    private void ApplyVendorList(byte[] body)
    {
        VendorInventory inventory = VendorPackets.ParseList(body);
        OpenVendorSession(inventory);
        EmitInterface("vendor", "list", inventory.Error == 0 ? "DECODED" : "ERROR",
            inventory.VendorGuid, $"items={inventory.Items.Count};error={inventory.Error}");
        if (_items is not null && _net is not null)
            foreach (VendorItem row in inventory.Items)
                _items.Require(row.ItemId, inventory.VendorGuid, _net);
    }

    private void ApplyVendorStockUpdate(byte[] body)
    {
        VendorStockUpdate update = VendorPackets.ParseStockUpdate(body);
        bool matchingVendor = _vendor?.VendorGuid == update.VendorGuid;
        bool matchingSlot = matchingVendor &&
                            _vendor!.Items.Any(row => row.Slot == update.VendorSlot);
        if (matchingSlot)
        {
            VendorItem[] rows = _vendor!.Items.Select(row => row.Slot == update.VendorSlot
                ? row with { Available = update.NewCount }
                : row).ToArray();
            _vendor = _vendor with { Items = rows };
        }
        EmitInterface("vendor", "stock-update", matchingSlot ? "APPLIED" : "IGNORED",
            update.VendorGuid,
            $"slot={update.VendorSlot};newCount={update.NewCount};" +
            $"purchaseCount={update.PurchaseCount};matchingVendor={matchingVendor}");
    }

    private void ApplyVendorBuyFailure(byte[] body)
    {
        VendorBuyFailure failure = VendorPackets.ParseBuyFailure(body);
        string text = VendorPackets.BuyFailureText(failure.Reason);
        ShowUiError(text);
        EmitInterface("vendor", "buy-failed", "ERROR", failure.VendorGuid,
            $"item={failure.ItemEntry};reason={failure.Reason};" +
            $"text={SanitizeEvidence(text)}");
    }

    private void ApplyVendorSellFailure(byte[] body)
    {
        VendorSellFailure failure = VendorPackets.ParseSellFailure(body);
        string text = VendorPackets.SellFailureText(failure.Reason);
        ShowUiError(text);
        EmitInterface("vendor", "sell-failed", "ERROR", failure.VendorGuid,
            $"itemGuid=0x{failure.ItemGuid:X16};reason={failure.Reason};" +
            $"text={SanitizeEvidence(text)}");
    }

    private bool BuyVendorEntry(uint entry, byte count)
    {
        if (_vendor is null || !_vendor.Items.Any(x => x.ItemId == entry))
        {
            EmitInterface("vendor", "buy", "REFUSED-NOT-LISTED", 0,
                $"item={entry};count={count}");
            return false;
        }
        bool sent = _net?.BuyItem(_vendor.VendorGuid, entry, count) == true;
        EmitInterface("vendor", "buy", sent ? "SENT" : "SEND_FAILED",
            _vendor.VendorGuid,
            $"item={entry};count={count};body=" +
            Convert.ToHexString(WorldSession.BuildBuyItemBody(
                _vendor.VendorGuid, entry, count)));
        return sent;
    }

    private bool SellToOpenVendor(ulong itemGuid, byte count = 0)
    {
        if (_vendor is null || itemGuid == 0 || _net is null) return false;
        bool sent = _net.SellItem(_vendor.VendorGuid, itemGuid, count);
        EmitInterface("vendor", "sell", sent ? "SENT" : "SEND_FAILED",
            _vendor.VendorGuid, $"itemGuid=0x{itemGuid:X16};count={count}");
        return sent;
    }
}

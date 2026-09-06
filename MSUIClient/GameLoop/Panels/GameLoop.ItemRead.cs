using MSUIClient.Engine.UI;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private sealed record ItemReadRequest(ulong Owner, ulong Guid, int Container, int Slot);
    private ItemReadRequest? _pendingItemRead;

    private bool RequestItemPages(int container, int slot)
    {
        _pendingItemRead = null;
        // HandleReadItemOpcode is still session-owned on Core. A companion's bag/slot
        // must never be submitted to that handler as the main character's inventory.
        if (!CanAuthorSessionInventory || _net is null ||
            ResolveInventoryItem(container, slot) is not { } instance ||
            _items?.TryGet(instance.Entry, out ItemTemplate? item) != true || item is null || item.PageText == 0 ||
            InventoryUiLaw.ToWire(container, slot) is not { } wire) return false;
        if (_itemTextRead?.ObjectGuid == instance.Guid)
        {
            CloseItemText(playSound: true);
            return true;
        }
        _pendingItemRead = new(LocalPlayerGuid, instance.Guid, container, slot);
        if (_net.ReadItem(wire.Bag, wire.Slot)) return true;
        _pendingItemRead = null;
        return false;
    }

    private void ApplyItemReadResult(Op opcode, byte[] body)
    {
        ItemReadResult result = ItemReadPackets.Parse(opcode, body);
        if (_pendingItemRead is not { } request || result.Guid != request.Guid) return;
        _pendingItemRead = null;
        // Failure's equipment-error packet supplies the detailed reason. Do not
        // fabricate a page or translate Core's undocumented read-reason byte.
        if (!result.Success || request.Owner != LocalPlayerGuid || request.Owner != ControlledGuid ||
            ResolveInventoryItem(request.Container, request.Slot) is not { } instance ||
            instance.Guid != request.Guid ||
            _items?.TryGet(instance.Entry, out ItemTemplate? item) != true || item is null || item.PageText == 0) return;
        OpenItemTextPages(instance.Guid, item.Name, item.PageText, item.PageMaterial, toggle: false);
    }
}

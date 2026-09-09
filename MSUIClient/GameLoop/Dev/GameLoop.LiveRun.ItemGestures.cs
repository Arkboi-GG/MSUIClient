using System.Numerics;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private bool RunLiveItemGesture(string line)
    {
        string[] p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length < 2 || !_entities.TryGet(ControlledGuid, out WorldEntity actor)) return false;
        if (p.Length == 3 && p[1] == "assert-ground-use" && uint.TryParse(p[2], out uint groundEntry))
        {
            Console.WriteLine($"[live-item-target] actor=0x{ControlledGuid:X};spell={_groundCastSpell};intent={_groundItemUse}");
            return groundEntry == 0 ? _groundItemUse is null :
                _groundItemUse is { } intent && intent.Actor == ControlledGuid &&
                intent.Entry == groundEntry && intent.Spell == _groundCastSpell;
        }
        if (p.Length == 3 && p[1] == "use-entry" && uint.TryParse(p[2], out uint useEntry))
        {
            if (FindCarriedEntry(actor, useEntry) is null) return false;
            UseItemAction(useEntry);
            return true; // Dispatch only; quest/item state assertions are separate.
        }
        if (p[1] == "inspect")
        {
            foreach (int bag in Enumerable.Range(0, 5))
                for (int cell = 0; cell < (bag == 0 ? InventoryUiLaw.BackpackSlots : InventoryUiLaw.MaxContainerSlots); cell++)
                    if (ResolveInventoryItem(bag, cell) is { } item)
                        Console.WriteLine($"[live-inventory-item] actor=0x{ControlledGuid:X};container={bag};slot={cell};entry={item.Entry};count={item.Fields.ItemStackCount}");
            return true;
        }
        if (p.Length == 3 && (p[1] is "pickup-entry" or "split-entry") && uint.TryParse(p[2], out uint entry))
        {
            if (HasCarriedItem || FindCarriedEntry(actor, entry) is not { } source) return false;
            if (p[1] == "pickup-entry")
            {
                PickupOrPlaceItem(source.Container, source.Slot, source.Guid, ignoreModifiers: true);
                return ResolveCarriedItem()?.Guid == source.Guid;
            }
            if (!_entities.TryGet(source.Guid, out WorldEntity stack)) return false;
            OpenStackSplit(source.Container, source.Slot, (int)stack.Fields.ItemStackCount, new Vector2(800, 400));
            return _splitContainer != InventoryUiLaw.EmptyContainer;
        }
        if (p.Length == 4 && (p[1] is "place" or "pickup-slot" or "assert-slot" or "assert-unlocked") && int.TryParse(p[2], out int container)
            && int.TryParse(p[3], out int slot))
        {
            if (InventoryUiLaw.ToWire(container, slot) is null) return false;
            WorldEntity? destination = ResolveInventoryItem(container, slot);
            if (p[1] == "assert-slot") return destination is not null;
            if (p[1] == "assert-unlocked") return !IsInventorySlotLocked(container, slot);
            if (p[1] == "pickup-slot")
            {
                if (HasCarriedItem || destination is null) return false;
                PickupOrPlaceItem(container, slot, destination.Guid, ignoreModifiers: true);
                return ResolveCarriedItem()?.Guid == destination.Guid;
            }
            if (!HasCarriedItem) return false;
            PickupOrPlaceItem(container, slot, destination?.Guid ?? 0, ignoreModifiers: true);
            return !HasCarriedItem;
        }
        if (p.Length == 5 && p[1] == "assert-entry" && int.TryParse(p[2], out int entryContainer)
            && int.TryParse(p[3], out int entrySlot) && uint.TryParse(p[4], out uint expectedEntry))
            return InventoryUiLaw.ToWire(entryContainer, entrySlot) is not null &&
                (ResolveInventoryItem(entryContainer, entrySlot)?.Entry ?? 0) == expectedEntry;
        if (p.Length == 3 && p[1] == "assert-cursor" && uint.TryParse(p[2], out uint cursor))
            return cursor == 0 ? !HasCarriedItem : ResolveCarriedItem()?.Entry == cursor;
        if (p.Length == 4 && p[1] == "assert-count" && uint.TryParse(p[2], out uint countEntry)
            && uint.TryParse(p[3], out uint count))
        {
            int actual = CountItemInBags(actor, countEntry);
            Console.WriteLine($"[live-inventory] actor=0x{ControlledGuid:X};entry={countEntry};count={actual};expected={count}");
            return actual == count;
        }
        return false;
    }
}

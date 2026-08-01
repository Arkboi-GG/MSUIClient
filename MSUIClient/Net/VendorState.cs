namespace MSUIClient.Net;

public readonly record struct VendorItem(uint Slot, uint ItemId, uint DisplayId,
    uint Available, uint Price, uint MaxDurability, uint BuyCount);

public sealed record VendorInventory(ulong VendorGuid, byte Error,
    IReadOnlyList<VendorItem> Items);

public static class VendorPackets
{
    public static VendorInventory ParseList(byte[] body)
    {
        var r = new PacketReader(body);
        ulong guid = r.ReadU64();
        byte count = r.ReadU8();
        if (count == 0)
        {
            byte error = r.ReadU8();
            if (r.Remaining != 0) throw new InvalidDataException("SMSG_LIST_INVENTORY error tail");
            return new VendorInventory(guid, error, []);
        }
        var items = new List<VendorItem>(count);
        for (int i = 0; i < count; i++)
            items.Add(new(r.ReadU32(), r.ReadU32(), r.ReadU32(), r.ReadU32(),
                r.ReadU32(), r.ReadU32(), r.ReadU32()));
        if (r.Remaining != 0) throw new InvalidDataException($"SMSG_LIST_INVENTORY has {r.Remaining} trailing byte(s)");
        return new VendorInventory(guid, 0, items);
    }
}

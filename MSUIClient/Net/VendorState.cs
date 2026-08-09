namespace MSUIClient.Net;

public readonly record struct VendorItem(uint Slot, uint ItemId, uint DisplayId,
    uint Available, uint Price, uint MaxDurability, uint BuyCount);

public sealed record VendorInventory(ulong VendorGuid, byte Error,
    IReadOnlyList<VendorItem> Items);

public readonly record struct VendorStockUpdate(ulong VendorGuid, uint VendorSlot,
    uint NewCount, uint PurchaseCount);

public readonly record struct VendorBuyFailure(ulong VendorGuid, uint ItemEntry,
    byte Reason);

public readonly record struct VendorSellFailure(ulong VendorGuid, ulong ItemGuid,
    byte Reason);

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

    public static VendorStockUpdate ParseStockUpdate(byte[] body)
    {
        var r = new PacketReader(body);
        var result = new VendorStockUpdate(r.ReadU64(), r.ReadU32(), r.ReadU32(), r.ReadU32());
        RequireConsumed(r, "SMSG_BUY_ITEM");
        return result;
    }

    public static VendorBuyFailure ParseBuyFailure(byte[] body)
    {
        var r = new PacketReader(body);
        var result = new VendorBuyFailure(r.ReadU64(), r.ReadU32(), r.ReadU8());
        RequireConsumed(r, "SMSG_BUY_FAILED");
        return result;
    }

    public static VendorSellFailure ParseSellFailure(byte[] body)
    {
        var r = new PacketReader(body);
        var result = new VendorSellFailure(r.ReadU64(), r.ReadU64(), r.ReadU8());
        RequireConsumed(r, "SMSG_SELL_ITEM");
        return result;
    }

    public static string BuyFailureText(byte reason) => reason switch
    {
        0 => "That item does not exist.",
        1 => "That item has already been sold.",
        2 => "You don't have enough money.",
        4 => "The vendor doesn't like you.",
        5 => "You are too far away.",
        7 => "The vendor is sold out of that item.",
        8 => "You can't carry any more of those items.",
        11 => "You don't have the required rank for that item.",
        12 => "You don't have the required reputation for that item.",
        _ => $"Purchase failed ({reason}).",
    };

    public static string SellFailureText(byte reason) => reason switch
    {
        1 => "That item does not exist.",
        2 => "You can't sell that item.",
        3 => "That vendor can't be found.",
        4 => "You don't own that item.",
        6 => "You can only sell an empty bag.",
        _ => $"Sale failed ({reason}).",
    };

    private static void RequireConsumed(PacketReader r, string packet)
    {
        if (r.Remaining != 0)
            throw new InvalidDataException($"{packet} has {r.Remaining} trailing byte(s)");
    }
}

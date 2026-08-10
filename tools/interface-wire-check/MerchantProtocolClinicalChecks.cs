using MSUIClient;
using MSUIClient.Net;

internal static class MerchantProtocolClinicalChecks
{
    private const ulong VendorGuid = 0x0102_0304_0506_0708UL;
    private const ulong ItemGuid = 0x1112_1314_1516_1718UL;

    public static void Run()
    {
        CheckInboundResultPackets();
        CheckDescriptorBasesAndAccessors();
        CheckOutboundOpcodesAndBodies();
        CheckSlotBasedStockRuntimeFence();
    }

    private static void CheckInboundResultPackets()
    {
        byte[] stockBody =
        [
            0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01,
            0x0c, 0x0b, 0x0a, 0x09,
            0x10, 0x0f, 0x0e, 0x0d,
            0x14, 0x13, 0x12, 0x11,
        ];
        VendorStockUpdate stock = VendorPackets.ParseStockUpdate(stockBody);
        Check(stock == new VendorStockUpdate(
                  VendorGuid, 0x090a_0b0c, 0x0d0e_0f10, 0x1112_1314),
            "SMSG_BUY_ITEM exact 8+4+4+4 body drift");
        RejectEveryTruncation(stockBody, VendorPackets.ParseStockUpdate,
            "SMSG_BUY_ITEM");
        RejectTrailing(stockBody, VendorPackets.ParseStockUpdate, "SMSG_BUY_ITEM");

        byte[] buyFailureBody =
        [
            0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01,
            0x0c, 0x0b, 0x0a, 0x09,
            0xa5,
        ];
        VendorBuyFailure buyFailure = VendorPackets.ParseBuyFailure(buyFailureBody);
        Check(buyFailure == new VendorBuyFailure(VendorGuid, 0x090a_0b0c, 0xa5),
            "SMSG_BUY_FAILED exact 8+4+1 body drift");
        RejectEveryTruncation(buyFailureBody, VendorPackets.ParseBuyFailure,
            "SMSG_BUY_FAILED");
        RejectTrailing(buyFailureBody, VendorPackets.ParseBuyFailure,
            "SMSG_BUY_FAILED");

        byte[] sellFailureBody =
        [
            0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01,
            0x18, 0x17, 0x16, 0x15, 0x14, 0x13, 0x12, 0x11,
            0xa1,
        ];
        VendorSellFailure sellFailure = VendorPackets.ParseSellFailure(sellFailureBody);
        Check(sellFailure == new VendorSellFailure(VendorGuid, ItemGuid, 0xa1),
            "SMSG_SELL_ITEM exact 8+8+1 body drift");
        RejectEveryTruncation(sellFailureBody, VendorPackets.ParseSellFailure,
            "SMSG_SELL_ITEM");
        RejectTrailing(sellFailureBody, VendorPackets.ParseSellFailure,
            "SMSG_SELL_ITEM");
    }

    private static void CheckDescriptorBasesAndAccessors()
    {
        Check(ObjectFields.PLAYER_VENDOR_BUYBACK_SLOT_1 == 624 &&
              ObjectFields.PLAYER_FIELD_BUYBACK_PRICE_1 == 1226 &&
              ObjectFields.PLAYER_FIELD_BUYBACK_TIMESTAMP_1 == 1238,
            "build-5875 buyback descriptor bases drift");
        Check(ObjectFields.PLAYER_VENDOR_BUYBACK_SLOT_1 + 12 * 2 ==
                  ObjectFields.PLAYER_KEYRING_SLOT_1 &&
              ObjectFields.PLAYER_FIELD_BUYBACK_PRICE_1 + 12 ==
                  ObjectFields.PLAYER_FIELD_BUYBACK_TIMESTAMP_1 &&
              ObjectFields.PLAYER_FIELD_BUYBACK_TIMESTAMP_1 + 12 ==
                  ObjectFields.PLAYER_FIELD_SESSION_KILLS &&
              ObjectFields.PLAYER_FIELD_SESSION_KILLS == 1250 &&
              ObjectFields.PLAYER_FIELD_LAST_WEEK_RANK == 1259,
            "buyback descriptor arrays no longer form the frozen 12-entry chains");

        var raw = new List<(ushort Index, uint Value)>();
        for (int physical = 0; physical < 12; physical++)
        {
            ushort guid = checked((ushort)(
                ObjectFields.PLAYER_VENDOR_BUYBACK_SLOT_1 + physical * 2));
            raw.Add((guid, 0xa000_0000u + (uint)physical));
            raw.Add((checked((ushort)(guid + 1)), 0xb000_0000u + (uint)physical));
            raw.Add((checked((ushort)(
                ObjectFields.PLAYER_FIELD_BUYBACK_PRICE_1 + physical)),
                10_000u + (uint)physical));
            raw.Add((checked((ushort)(
                ObjectFields.PLAYER_FIELD_BUYBACK_TIMESTAMP_1 + physical)),
                20_000u + (uint)physical));
        }

        ObjectFields fields = ReadFields(raw);
        for (int physical = 0; physical < 12; physical++)
        {
            ulong expectedGuid =
                ((ulong)(0xb000_0000u + (uint)physical) << 32) |
                (0xa000_0000u + (uint)physical);
            Check(fields.PlayerBuybackSlot(physical) == expectedGuid &&
                  fields.PlayerBuybackPrice(physical) == 10_000u + (uint)physical &&
                  fields.PlayerBuybackTimestamp(physical) == 20_000u + (uint)physical,
                $"buyback physical descriptor accessor {physical} drift");
        }
        Check(fields.PlayerBuybackSlot(-1) == 0 && fields.PlayerBuybackSlot(12) == 0 &&
              fields.PlayerBuybackPrice(-1) == 0 && fields.PlayerBuybackPrice(12) == 0 &&
              fields.PlayerBuybackTimestamp(-1) == 0 &&
              fields.PlayerBuybackTimestamp(12) == 0,
            "buyback descriptor accessors escaped their twelve-entry bounds");
    }

    private static void CheckOutboundOpcodesAndBodies()
    {
        Check((ushort)Op.CMSG_LIST_INVENTORY == 0x019e &&
              (ushort)Op.SMSG_LIST_INVENTORY == 0x019f &&
              (ushort)Op.CMSG_SELL_ITEM == 0x01a0 &&
              (ushort)Op.SMSG_SELL_ITEM == 0x01a1 &&
              (ushort)Op.CMSG_BUY_ITEM == 0x01a2 &&
              (ushort)Op.SMSG_BUY_ITEM == 0x01a4 &&
              (ushort)Op.SMSG_BUY_FAILED == 0x01a5 &&
              (ushort)Op.CMSG_BUYBACK_ITEM == 0x0290 &&
              (ushort)Op.CMSG_REPAIR_ITEM == 0x02a8,
            "build-5875 merchant opcode family drift");

        CheckBytes(WorldSession.BuildListInventoryBody(VendorGuid),
        [
            0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01,
        ], "CMSG_LIST_INVENTORY exact u64 body");
        CheckBytes(WorldSession.BuildBuyItemBody(VendorGuid, 0x090a_0b0c, 0x0d),
        [
            0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01,
            0x0c, 0x0b, 0x0a, 0x09, 0x0d, 0x00,
        ], "CMSG_BUY_ITEM exact u64+u32+u8+u8 body");
        CheckBytes(WorldSession.BuildSellItemBody(VendorGuid, ItemGuid, 0x19),
        [
            0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01,
            0x18, 0x17, 0x16, 0x15, 0x14, 0x13, 0x12, 0x11,
            0x19,
        ], "CMSG_SELL_ITEM exact u64+u64+u8 body");
        CheckBytes(WorldSession.BuildBuybackItemBody(VendorGuid, 69),
        [
            0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01,
            0x45, 0x00, 0x00, 0x00,
        ], "CMSG_BUYBACK_ITEM exact vendor+absolute-slot body");
        CheckBytes(WorldSession.BuildBuybackItemBody(VendorGuid, 80),
        [
            0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01,
            0x50, 0x00, 0x00, 0x00,
        ], "CMSG_BUYBACK_ITEM physical-11 absolute-slot body");
        CheckBytes(WorldSession.BuildRepairItemBody(VendorGuid, ItemGuid),
        [
            0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01,
            0x18, 0x17, 0x16, 0x15, 0x14, 0x13, 0x12, 0x11,
        ], "CMSG_REPAIR_ITEM exact vendor+item body");
        CheckBytes(WorldSession.BuildRepairItemBody(VendorGuid, 0),
        [
            0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        ], "CMSG_REPAIR_ITEM repair-all zero item body");
    }

    private static void CheckSlotBasedStockRuntimeFence()
    {
        string root = ClientConfig.FindRepoRoot();
        string vendorSource = SourceText.Read(Path.Combine(root,
            "MSUIClient", "Program.Vendor.cs"));
        string netSource = SourceText.Read(Path.Combine(root,
            "MSUIClient", "Program.Net.cs")).Replace("\r\n", "\n",
                StringComparison.Ordinal);

        int methodStart = vendorSource.IndexOf(
            "private void ApplyVendorStockUpdate(byte[] body)", StringComparison.Ordinal);
        int methodEnd = vendorSource.IndexOf(
            "private void ApplyVendorBuyFailure(byte[] body)", methodStart,
            StringComparison.Ordinal);
        Check(methodStart >= 0 && methodEnd > methodStart,
            "merchant stock-update runtime method fence is missing");
        string method = vendorSource[methodStart..methodEnd];
        int parse = method.IndexOf(
            "VendorPackets.ParseStockUpdate(body)", StringComparison.Ordinal);
        int vendorGuard = method.IndexOf(
            "_vendor?.VendorGuid == update.VendorGuid", StringComparison.Ordinal);
        int slotMatch = method.IndexOf(
            "row.Slot == update.VendorSlot", StringComparison.Ordinal);
        int replacement = method.IndexOf(
            "row with { Available = update.NewCount }", StringComparison.Ordinal);
        int commit = method.IndexOf(
            "_vendor = _vendor with { Items = rows }", StringComparison.Ordinal);
        Check(parse >= 0 && vendorGuard > parse && slotMatch > vendorGuard &&
              replacement > slotMatch && commit > replacement &&
              Count(method, "row.Slot == update.VendorSlot") >= 2 &&
              !method.Contains("row.ItemId == update.VendorSlot", StringComparison.Ordinal) &&
              !method.Contains("row.ItemId == update.NewCount", StringComparison.Ordinal),
            "SMSG_BUY_ITEM must parse atomically, guard vendor, and update Available by vendor slot");

        Check(netSource.Contains(
                  "case Op.SMSG_BUY_ITEM:\n                        ApplyVendorStockUpdate(body);",
                  StringComparison.Ordinal) &&
              netSource.Contains(
                  "case Op.SMSG_BUY_FAILED:\n                        ApplyVendorBuyFailure(body);",
                  StringComparison.Ordinal) &&
              netSource.Contains(
                  "case Op.SMSG_SELL_ITEM:\n                        ApplyVendorSellFailure(body);",
                  StringComparison.Ordinal) &&
              !netSource.Contains("ApplyVendorResult((Op)opcode,body)",
                  StringComparison.Ordinal),
            "merchant result opcodes no longer have distinct typed dispatch routes");
    }

    private static ObjectFields ReadFields(IEnumerable<(ushort Index, uint Value)> source)
    {
        (ushort Index, uint Value)[] fields = source.OrderBy(value => value.Index).ToArray();
        if (fields.Length == 0) return new ObjectFields();
        int blocks = fields[^1].Index / 32 + 1;
        var masks = new uint[blocks];
        foreach ((ushort index, _) in fields)
            masks[index / 32] |= 1u << (index & 31);

        var writer = new PacketWriter(1 + blocks * 4 + fields.Length * 4);
        writer.WriteU8(checked((byte)blocks));
        foreach (uint mask in masks) writer.WriteU32(mask);
        foreach ((_, uint value) in fields) writer.WriteU32(value);
        return ObjectFields.Read(new PacketReader(writer.ToArray()));
    }

    private static void RejectEveryTruncation<T>(byte[] exact,
        Func<byte[], T> parser, string packet)
    {
        for (int length = 0; length < exact.Length; length++)
        {
            byte[] truncated = exact.AsSpan(0, length).ToArray();
            Throws<EndOfStreamException>(() => parser(truncated),
                $"{packet} accepted truncation at {length}/{exact.Length} bytes");
        }
    }

    private static void RejectTrailing<T>(byte[] exact, Func<byte[], T> parser,
        string packet)
    {
        byte[] trailing = [.. exact, 0xcc];
        Throws<InvalidDataException>(() => parser(trailing),
            $"{packet} accepted a trailing byte");
    }

    private static void CheckBytes(byte[] actual, byte[] expected, string message) =>
        Check(actual.AsSpan().SequenceEqual(expected),
            $"{message}: expected {Convert.ToHexString(expected)}, got {Convert.ToHexString(actual)}");

    private static int Count(string source, string value)
    {
        int count = 0;
        for (int at = 0; (at = source.IndexOf(value, at, StringComparison.Ordinal)) >= 0;
             at += value.Length)
            count++;
        return count;
    }

    private static void Throws<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

namespace MSUIClient.Net;

/// <summary>
/// The Browse tab's query as the wire carries it (CMSG_AUCTION_LIST_ITEMS). Sentinels: 0xFFFFFFFF
/// for the four u32 filters, 0 for the level pair and usable. Quality is a MINIMUM, the level
/// pair gates RequiredLevel, and ListFrom is a ROW offset (page × 50).
/// </summary>
public readonly record struct AuctionBrowseQuery(uint ListFrom, string Search, byte LevelMin,
    byte LevelMax, uint InventoryType, uint ItemClass, uint Subclass, uint Quality, bool UsableOnly)
{
    public const uint Any = uint.MaxValue;
    public const uint PageSize = 50;
}

/// <summary>
/// One row of any of the three list results — the three opcodes share one producer
/// (vmangos AuctionEntry::BuildAuctionInfo): 7×u32, u64, 4×u32, u64, u32 = 64 bytes, no strings.
/// RandomPropertyId and SpellCharges are SIGNED on the wire.
/// </summary>
public sealed record AuctionListEntry(uint Id, uint Item, uint PermEnchant, int RandomPropertyId,
    uint SuffixFactor, uint Count, int SpellCharges, ulong Owner, uint StartBid, uint MinIncrement,
    uint Buyout, uint TimeLeftMs, ulong Bidder, uint Bid);

/// <summary>Exact build-5875 auction house bodies (vmangos AuctionHouseHandler.cpp).</summary>
public static class AuctionPackets
{
    public const int RecordBytes = 64;

    // SMSG_AUCTION_COMMAND_RESULT action (vmangos AuctionAction).
    public const uint ActionStarted = 0, ActionRemoved = 1, ActionBidPlaced = 2;
    // SMSG_AUCTION_COMMAND_RESULT error (vmangos AuctionError). 6/8/9/11/12 are never sent.
    public const uint ErrorOk = 0, ErrorInventory = 1, ErrorDatabase = 2, ErrorNotEnoughMoney = 3,
        ErrorItemNotFound = 4, ErrorHigherBid = 5, ErrorBidIncrement = 7, ErrorBidOwn = 10,
        ErrorRestrictedAccount = 13;

    public readonly record struct CommandResult(uint AuctionId, uint Action, uint Error,
        uint NewMinOutbid, uint InventoryResult, ulong NewBidder, uint NewBid);

    public readonly record struct BidderNotification(uint HouseId, uint AuctionId, ulong Bidder,
        uint BidOrZero, uint OutBid, uint ItemEntry, int RandomPropertyId);

    public readonly record struct OwnerNotification(uint AuctionId, uint Bid, uint OutBid,
        ulong Bidder, uint ItemEntry, int RandomPropertyId);

    public readonly record struct RemovedNotification(uint AuctionId, uint ItemEntry,
        int RandomPropertyId);

    /// <summary>
    /// The shared list frame: u32 count, count × record, u32 totalCount (the pre-cap match count,
    /// written LAST). <c>count</c> is an UPPER BOUND, not a record count — vmangos's no-filter
    /// browse fast path counts a stale auction whose item row is gone and then writes zero bytes
    /// for it, so a real server routinely sends count = N with N-k records. The layout is fixed
    /// width, so stop cleanly on a short buffer and keep every intact record; a body cut past
    /// the last record has no trailing total either, so fall back to what was read.
    /// </summary>
    public static (List<AuctionListEntry> Rows, uint Total) ParseList(byte[] body)
    {
        var r = new PacketReader(body);
        uint count = r.ReadU32();
        var rows = new List<AuctionListEntry>((int)Math.Min(count, (uint)(body.Length / RecordBytes)));
        for (uint i = 0; i < count; i++)
        {
            if (r.Remaining < RecordBytes) break;
            uint id = r.ReadU32(), item = r.ReadU32(), permEnchant = r.ReadU32();
            int randomProperty = r.ReadI32();
            uint suffixFactor = r.ReadU32(), stack = r.ReadU32();
            int charges = r.ReadI32();
            ulong owner = r.ReadU64();
            uint start = r.ReadU32(), increment = r.ReadU32(), buyout = r.ReadU32(), left = r.ReadU32();
            ulong bidder = r.ReadU64();
            uint bid = r.ReadU32();
            rows.Add(new(id, item, permEnchant, randomProperty, suffixFactor, stack, charges, owner,
                start, increment, buyout, left, bidder, bid));
        }
        uint total = r.Remaining >= 4 ? r.ReadU32() : (uint)rows.Count;
        return (rows, Math.Max(total, (uint)rows.Count));
    }

    /// <summary>
    /// u32 auctionId (0 on most failure paths — not a correlation handle on an error), u32 action,
    /// u32 error, then the tail the error selects: OK+BID_PLACED → u32 newMinOutbid; INVENTORY →
    /// u32 EQUIP_ERR_*; HIGHER_BID → u64 newBidder, u32 newBid, u32 newMinOutbid.
    /// </summary>
    public static CommandResult ParseCommand(byte[] body)
    {
        var r = new PacketReader(body);
        uint id = r.ReadU32(), action = r.ReadU32(), error = r.ReadU32();
        uint newMin = 0, inventory = 0, newBid = 0; ulong newBidder = 0;
        if (error == ErrorOk && action == ActionBidPlaced && r.Remaining >= 4) newMin = r.ReadU32();
        else if (error == ErrorInventory && r.Remaining >= 4) inventory = r.ReadU32();
        else if (error == ErrorHigherBid && r.Remaining >= 16)
        { newBidder = r.ReadU64(); newBid = r.ReadU32(); newMin = r.ReadU32(); }
        return new(id, action, error, newMin, inventory, newBidder, newBid);
    }

    /// <summary>u32 houseId, u32 auctionId, u64 bidder, u32 bidOrZero (0 = WON), u32 outBid, u32 item, i32 randomProperty.</summary>
    public static BidderNotification ParseBidderNotification(byte[] body)
    {
        var r = new PacketReader(body);
        return new(r.ReadU32(), r.ReadU32(), r.ReadU64(), r.ReadU32(), r.ReadU32(), r.ReadU32(),
            r.ReadI32());
    }

    /// <summary>u32 auctionId, u32 bid, u32 outBid, u64 bidder (0 = sold/expired), u32 item, i32 randomProperty — a different order from the bidder notice, and no house id.</summary>
    public static OwnerNotification ParseOwnerNotification(byte[] body)
    {
        var r = new PacketReader(body);
        return new(r.ReadU32(), r.ReadU32(), r.ReadU32(), r.ReadU64(), r.ReadU32(), r.ReadI32());
    }

    /// <summary>u32 auctionId, u32 item, i32 randomProperty — pushed to a bidder whose auction the seller cancelled.</summary>
    public static RemovedNotification ParseRemovedNotification(byte[] body)
    {
        var r = new PacketReader(body);
        return new(r.ReadU32(), r.ReadU32(), r.ReadI32());
    }
}

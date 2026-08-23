namespace MSUIClient.Net;

/// <summary>
/// One SMSG_LOOT_RESPONSE row. <see cref="Slot"/> is the 0-based WIRE loot slot — the value
/// CMSG_AUTOSTORE_LOOT_ITEM addresses back — and it never changes while rows are removed.
/// Wire order (vmangos LootMgr.cpp:848-873, the ALL_PERMISSION solo branch): u8 slot,
/// u32 itemId, u32 count, u32 displayInfoId, u32 randomSuffix(always 0), u32 randomPropertyId,
/// u8 slotType.
/// </summary>
public readonly record struct LootItem(
    byte Slot, uint ItemId, uint Count, uint DisplayInfoId, uint RandomPropertyId, byte SlotType);

/// <summary>
/// The client side of the single solo-loot session. Mirrors benilla's LootState invariants:
/// exactly one session (open replaces wholesale), rows keep their wire slots forever, the
/// auto-release arms only on the TRANSITION to empty (an empty-at-open window stays up),
/// and clears are idempotent.
/// </summary>
public sealed class LootState
{
    private readonly List<LootItem> _items = new();
    private bool _autoRelease;

    /// <summary>Corpse guid of the open session; 0 = no loot window.</summary>
    public ulong Source { get; private set; }
    public byte LootType { get; private set; }
    /// <summary>The whole coin pile in copper; 0 = no coin row.</summary>
    public uint Gold { get; private set; }
    public IReadOnlyList<LootItem> Items => _items;
    public bool IsOpen => Source != 0;
    public bool IsEmpty => Gold == 0 && _items.Count == 0;

    public void Open(ulong source, byte lootType, uint gold, List<LootItem> items)
    {
        Source = source;
        LootType = lootType;
        Gold = gold;
        _items.Clear();
        _items.AddRange(items);
        _autoRelease = false; // an empty-at-open window stays up (LOOTWINDOWOPENEMPTY case)
    }

    /// <summary>SMSG_LOOT_REMOVED: drop the row with this WIRE slot; display rows shift.</summary>
    public void RemoveSlot(byte wireSlot)
    {
        bool hadRows = !IsEmpty;
        _items.RemoveAll(item => item.Slot == wireSlot);
        if (hadRows && IsEmpty) _autoRelease = true;
    }

    /// <summary>SMSG_LOOT_CLEAR_MONEY: someone took the coins.</summary>
    public void ClearMoney()
    {
        bool hadRows = !IsEmpty;
        Gold = 0;
        if (hadRows && IsEmpty) _autoRelease = true;
    }

    /// <summary>True exactly once after the last row leaves an open window.</summary>
    public bool TakeAutoRelease()
    {
        if (!_autoRelease) return false;
        _autoRelease = false;
        return true;
    }

    public void Clear()
    {
        Source = 0;
        LootType = 0;
        Gold = 0;
        _items.Clear();
        _autoRelease = false;
    }
}

public enum GroupLootVote : byte
{
    Pass = 0,
    Need = 1,
    Greed = 2,
}

public readonly record struct LootRollKey(ulong LootedTarget, uint ItemSlot);

public readonly record struct LootStartRoll(
    ulong LootedTarget, uint ItemSlot, uint ItemId, uint RandomPropertyId, uint CountdownMs);

public readonly record struct LootRollAnnouncement(
    ulong LootedTarget, uint ItemSlot, ulong Roller, uint ItemId, uint RandomPropertyId,
    byte RollNumber, byte RollType)
{
    public bool IsDice => RollNumber is >= 1 and <= 100;
    public GroupLootVote? Vote => IsDice ? null : (RollNumber, RollType) switch
    {
        (0, 0) => GroupLootVote.Need,
        (128, 128) => GroupLootVote.Pass,
        (128, 2) => GroupLootVote.Greed,
        _ => null,
    };
}

public readonly record struct LootRollWon(
    ulong LootedTarget, uint ItemSlot, uint ItemId, uint RandomPropertyId, ulong Winner,
    byte RollNumber, byte RollType);

public readonly record struct LootAllPassed(
    ulong LootedTarget, uint ItemSlot, uint ItemId, uint RandomPropertyId);

/// <summary>
/// The four visible GroupLootFrame instances. The reference assigns a monotonically increasing
/// local roll id, ignores a duplicate target/slot, claims the first hidden frame, and silently
/// leaves a fifth simultaneous roll unframed. A local vote closes its frame immediately.
/// </summary>
public sealed class GroupLootRollState
{
    public sealed record ActiveRoll(
        ulong Id, LootRollKey Key, uint ItemId, uint RandomPropertyId,
        uint CountdownMs, double OpenedAtSeconds);

    public const int FrameCount = 4;
    private readonly ActiveRoll?[] _frames = new ActiveRoll?[FrameCount];
    private ulong _nextId = 1;

    public IReadOnlyList<ActiveRoll?> Frames => _frames;

    public ActiveRoll? Start(in LootStartRoll packet, double nowSeconds)
    {
        var key = new LootRollKey(packet.LootedTarget, packet.ItemSlot);
        if (_frames.Any(frame => frame?.Key == key)) return null;
        int frameIndex = Array.FindIndex(_frames, frame => frame is null);
        if (frameIndex < 0) return null;
        var active = new ActiveRoll(_nextId++, key, packet.ItemId,
            packet.RandomPropertyId, packet.CountdownMs, nowSeconds);
        _frames[frameIndex] = active;
        return active;
    }

    public ActiveRoll? Find(LootRollKey key) =>
        _frames.FirstOrDefault(frame => frame?.Key == key);

    public bool Close(LootRollKey key)
    {
        int index = Array.FindIndex(_frames, frame => frame?.Key == key);
        if (index < 0) return false;
        _frames[index] = null;
        return true;
    }

    public double RemainingMilliseconds(ActiveRoll roll, double nowSeconds) =>
        Math.Max(0, roll.CountdownMs - (nowSeconds - roll.OpenedAtSeconds) * 1000.0);

    public void Clear()
    {
        Array.Clear(_frames);
        _nextId = 1;
    }
}

public static class LootPackets
{
    public static string ParseFishingVerdict(byte[] body, bool escaped)
    {
        if (body.Length != 0)
            throw new InvalidDataException(
                $"{(escaped ? nameof(Op.SMSG_FISH_ESCAPED) : nameof(Op.SMSG_FISH_NOT_HOOKED))} " +
                $"expected empty body, got {body.Length}");
        return escaped ? "ERR_FISH_ESCAPED" : "ERR_FISH_NOT_HOOKED";
    }

    public static LootStartRoll ParseStartRoll(byte[] body)
    {
        var r = Exact(body, 28, nameof(ParseStartRoll));
        ulong target = r.ReadU64();
        uint slot = r.ReadU32();
        uint item = r.ReadU32();
        r.ReadU32(); // random suffix: literal zero
        uint property = r.ReadU32();
        uint countdown = r.ReadU32();
        RequireConsumed(r, nameof(ParseStartRoll));
        return new(target, slot, item, property, countdown);
    }

    public static LootRollAnnouncement ParseRoll(byte[] body)
    {
        var r = Exact(body, 34, nameof(ParseRoll));
        ulong target = r.ReadU64();
        uint slot = r.ReadU32();
        ulong roller = r.ReadU64();
        uint item = r.ReadU32();
        r.ReadU32(); // random suffix
        uint property = r.ReadU32();
        byte number = r.ReadU8();
        byte type = r.ReadU8();
        RequireConsumed(r, nameof(ParseRoll));
        return new(target, slot, roller, item, property, number, type);
    }

    public static LootRollWon ParseRollWon(byte[] body)
    {
        var r = Exact(body, 34, nameof(ParseRollWon));
        ulong target = r.ReadU64();
        uint slot = r.ReadU32();
        uint item = r.ReadU32();
        r.ReadU32(); // random suffix
        uint property = r.ReadU32();
        ulong winner = r.ReadU64();
        byte number = r.ReadU8();
        byte type = r.ReadU8();
        RequireConsumed(r, nameof(ParseRollWon));
        return new(target, slot, item, property, winner, number, type);
    }

    public static LootAllPassed ParseAllPassed(byte[] body)
    {
        var r = Exact(body, 24, nameof(ParseAllPassed));
        ulong target = r.ReadU64();
        uint slot = r.ReadU32();
        uint item = r.ReadU32();
        uint property = r.ReadU32(); // uniquely precedes random suffix in this packet
        r.ReadU32();                 // random suffix
        RequireConsumed(r, nameof(ParseAllPassed));
        return new(target, slot, item, property);
    }

    public static byte[] BuildRollBody(ulong lootedTarget, uint itemSlot, GroupLootVote vote)
    {
        var w = new PacketWriter(13);
        w.WriteU64(lootedTarget);
        w.WriteU32(itemSlot);
        w.WriteU8((byte)vote);
        return w.ToArray();
    }

    /// <summary>
    /// Parse SMSG_LOOT_RESPONSE — both shapes. lootType 0 is the error shape (u8 code follows);
    /// otherwise u32 gold, u8 count, then count rows (see <see cref="LootItem"/>).
    /// </summary>
    public static (ulong Guid, byte LootType, byte Error, uint Gold, List<LootItem> Items)
        ParseResponse(byte[] body)
    {
        var r = new PacketReader(body);
        ulong guid = r.ReadU64();
        byte lootType = r.ReadU8();
        if (lootType == 0)
            return (guid, 0, r.ReadU8(), 0, new List<LootItem>());

        uint gold = r.ReadU32();
        int count = r.ReadU8();
        var items = new List<LootItem>(count);
        for (int i = 0; i < count && r.Remaining >= 22; i++)
        {
            byte slot = r.ReadU8();
            uint itemId = r.ReadU32();
            uint itemCount = r.ReadU32();
            uint displayInfoId = r.ReadU32();
            r.ReadU32(); // randomSuffix — a literal 0 on the wire (LootMgr.cpp:841)
            uint randomPropertyId = r.ReadU32();
            byte slotType = r.ReadU8();
            items.Add(new LootItem(slot, itemId, itemCount, displayInfoId, randomPropertyId, slotType));
        }
        return (guid, lootType, 0, gold, items);
    }

    /// <summary>The strings vmangos's LootError codes surface in the 1.12 client.</summary>
    public static string ErrorText(byte code) => code switch
    {
        0 => "You don't have permission to loot that corpse.",   // DIDNT_KILL
        4 => "You are too far away to loot that corpse.",        // TOO_FAR
        5 => "You must be facing the corpse to loot it.",        // BAD_FACING
        6 => "Locked.",                                          // LOCKED
        8 => "You must be standing to loot something!",          // NOTSTANDING
        9 => "You can't loot anything while stunned!",           // STUNNED
        10 => "Player not found.",                               // PLAYER_NOT_FOUND
        15 => "That target has already been pickpocketed.",      // ALREADY_PICKPOCKETED
        _ => "You can't loot that.",
    };

    private static PacketReader Exact(byte[] body, int length, string packet)
    {
        if (body.Length != length)
            throw new InvalidDataException($"{packet} expected {length} bytes, got {body.Length}");
        return new PacketReader(body);
    }

    private static void RequireConsumed(PacketReader reader, string packet)
    {
        if (reader.Remaining != 0)
            throw new InvalidDataException($"{packet} has {reader.Remaining} trailing bytes");
    }
}

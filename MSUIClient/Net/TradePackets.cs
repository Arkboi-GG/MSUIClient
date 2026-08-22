namespace MSUIClient.Net;

/// <summary>Strict build-5875 decoders for the two server trade packets.</summary>
public static class TradePackets
{
    public readonly record struct Status(
        uint Code,
        ulong Partner = 0,
        uint Result = 0,
        uint ItemLimitCategory = 0,
        byte Slot = 0);

    public readonly record struct Item(
        uint Entry,
        uint DisplayId,
        uint Count,
        bool Wrapped,
        ulong GiftCreator,
        uint PermanentEnchant,
        ulong Creator,
        int Charges,
        uint SuffixFactor,
        uint RandomPropertyId,
        uint LockId,
        uint MaxDurability,
        uint Durability);

    public sealed record Extended(
        bool TheirWindow,
        uint Gold,
        uint EnchantSpellId,
        Item?[] Slots);

    public static Status ParseStatus(byte[] body)
    {
        if (body.Length < 4)
            throw new InvalidDataException(
                $"SMSG_TRADE_STATUS body is {body.Length} bytes, expected at least 4");

        var r = new PacketReader(body);
        uint code = r.ReadU32();
        Status result = code switch
        {
            1 => new(code, Partner: r.ReadU64()),
            12 => new(code, Result: r.ReadU32(),
                ItemLimitCategory: ReadCloseWindowTail(r)),
            22 => new(code, Slot: r.ReadU8()),
            _ => new(code),
        };
        if (r.Remaining != 0)
            throw new InvalidDataException(
                $"SMSG_TRADE_STATUS code {code} has {r.Remaining} unexpected trailing byte(s)");
        return result;
    }

    public static Extended ParseExtended(byte[] body)
    {
        const int expected = 17 + 7 * 61;
        if (body.Length != expected)
            throw new InvalidDataException(
                $"SMSG_TRADE_STATUS_EXTENDED body is {body.Length} bytes, expected {expected}");

        var r = new PacketReader(body);
        bool theirWindow = r.ReadU8() == 1;
        r.ReadU32(); // fixed slot count, repeated by the server
        r.ReadU32();
        uint gold = r.ReadU32();
        uint enchantSpellId = r.ReadU32();
        var slots = new Item?[7];
        for (int i = 0; i < slots.Length; i++)
        {
            byte index = r.ReadU8();
            var item = new Item(
                r.ReadU32(), r.ReadU32(), r.ReadU32(), r.ReadU32() != 0,
                r.ReadU64(), r.ReadU32(), r.ReadU64(), r.ReadI32(),
                r.ReadU32(), r.ReadU32(), r.ReadU32(), r.ReadU32(), r.ReadU32());
            if (index < slots.Length && item.Entry != 0) slots[index] = item;
        }
        return new Extended(theirWindow, gold, enchantSpellId, slots);
    }

    private static uint ReadCloseWindowTail(PacketReader r)
    {
        r.ReadU8(); // vmangos writes this byte; it carries no player-trade state
        return r.ReadU32();
    }
}

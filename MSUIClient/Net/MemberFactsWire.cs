namespace MSUIClient.Net;

/// <summary>
/// Party member facts (SuperUI extension; owner rule: party = full facts,
/// faction = orders). CMSG_SUI_MEMBER_FACTS asks the server to push facts for
/// party/raid AiBot members — an explicit subject list, or empty = every AiBot
/// in the requester's group. Inventory arrives as SMSG_SUI_SNAPSHOT
/// (byte-identical to the possession snapshot); known spells arrive as
/// SMSG_SUI_MEMBER_SPELLS, parsed here. Exact-length parsing by wire law.
/// </summary>
public static class MemberFactsWire
{
    /// <summary>MAX_RAID_SIZE — the largest subject list a pull may carry.</summary>
    public const int MaximumSubjects = 40;

    /// <summary>u8 flags (reserved), u8 count (0 = whole party/raid), u64 guids.</summary>
    public static byte[] BuildMemberFactsBody(IReadOnlyList<ulong> subjects)
    {
        ArgumentNullException.ThrowIfNull(subjects);
        if (subjects.Count > MaximumSubjects)
            throw new ArgumentOutOfRangeException(nameof(subjects),
                $"member-facts pull supports at most {MaximumSubjects} explicit subjects.");
        var w = new PacketWriter(2 + subjects.Count * 8);
        w.WriteU8(0);
        w.WriteU8((byte)subjects.Count);
        foreach (ulong guid in subjects) w.WriteU64(guid);
        return w.ToArray();
    }

    /// <summary>
    /// Instant party item move (Phase C v1 — owner 2026-08-25: manage the
    /// party's bags like a CRPG): u8 flags (reserved), u64 from, u64 to,
    /// u8 bag (255 = character-held, 19-22 = equipped bag), u8 slot. The
    /// server validates the party line and answers with
    /// SMSG_SUI_MEMBER_ITEM_MOVE_RESULT plus fresh snapshots for both ends.
    /// </summary>
    public static byte[] BuildMemberItemMoveBody(ulong from, ulong to, byte bag, byte slot)
    {
        var w = new PacketWriter(19);
        w.WriteU8(0);
        w.WriteU64(from);
        w.WriteU64(to);
        w.WriteU8(bag);
        w.WriteU8(slot);
        return w.ToArray();
    }

    // SMSG_SUI_MEMBER_ITEM_MOVE_RESULT codes (SuperUI-Core SuiPossess.cpp).
    public const byte ItemMoveOk = 0;
    public const byte ItemMoveDenied = 1;
    public const byte ItemMoveNoItem = 2;
    public const byte ItemMoveTargetFull = 3;
    public const byte ItemMoveUnavailable = 4;
    public const byte ItemMoveRefusedItem = 5;

    /// <summary>u8 result, u64 from, u64 to; exact length only.</summary>
    public static bool TryParseMemberItemMoveResult(byte[] body,
        out byte result, out ulong from, out ulong to)
    {
        result = 0;
        from = 0;
        to = 0;
        if (body.Length != 17) return false;
        var r = new PacketReader(body);
        result = r.ReadU8();
        from = r.ReadU64();
        to = r.ReadU64();
        return true;
    }

    /// <summary>u64 guid, u16 count, u32 spellIds[count]; exact length only.</summary>
    public static bool TryParseMemberSpells(byte[] body, out ulong guid, out uint[] spells)
    {
        guid = 0;
        spells = [];
        if (body.Length < 10) return false;
        var r = new PacketReader(body);
        ulong source = r.ReadU64();
        int count = r.ReadU16();
        if (body.Length != 10 + count * 4) return false;
        var ids = new uint[count];
        for (int i = 0; i < count; i++) ids[i] = r.ReadU32();
        guid = source;
        spells = ids;
        return true;
    }
}

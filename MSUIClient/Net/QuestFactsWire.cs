namespace MSUIClient.Net;

/// <summary>
/// One party member's quest-log entry as pushed by a quest-facts capable server.
///
/// The counters are the SERVER-side truth (<c>QuestStatusData::m_creatureOrGOcount</c> /
/// <c>m_itemcount</c>), not the packed update-field mirror — which is the whole point:
/// a quest past the vanilla 20 update-field slots has no mirror to read, and a party
/// member's slots were never streamed to us in the first place.
/// </summary>
/// <param name="QuestId">quest_template id.</param>
/// <param name="Status">Vanilla QUEST_STATUS_* (see <see cref="QuestFactsWire"/>).</param>
/// <param name="Flags">Entry flags — complete / failed / overflow.</param>
/// <param name="Slot">Update-field log slot, or <see cref="QuestFactsWire.NoLogSlot"/>
/// (255) when the quest is held without one (an overflow quest).</param>
/// <param name="ObjectiveCounts">Kill/GO progress per objective (vanilla caps these at a byte).</param>
/// <param name="ItemCounts">Required-item progress per objective.</param>
public readonly record struct MemberQuestEntry(
    uint QuestId,
    byte Status,
    byte Flags,
    byte Slot,
    byte[] ObjectiveCounts,
    ushort[] ItemCounts)
{
    public bool Complete => (Flags & QuestFactsWire.EntryComplete) != 0;
    public bool Failed => (Flags & QuestFactsWire.EntryFailed) != 0;

    /// <summary>Held without an update-field slot — only reachable over this wire.</summary>
    public bool Overflow => (Flags & QuestFactsWire.EntryOverflow) != 0;
}

/// <summary>
/// One subject's complete quest log, as one push. HeldCap is the server's
/// MAX_QUEST_HELD — how many quests this character may hold at once, which stops
/// being twenty once PLAN_20 P2 lands and is not otherwise knowable client-side.
/// Zero means the server did not say.
/// </summary>
public sealed record MemberQuestLog(
    ulong Subject, byte Flags, ushort HeldCap, MemberQuestEntry[] Entries);

/// <summary>
/// Party quest facts (PLAN_20 P1). Extends the member-facts law — party = full facts,
/// faction = orders — from bags and spells to quest logs. CMSG_SUI_QUEST_FACTS asks the
/// server to push quest logs for party/raid members: an explicit subject list, or empty
/// = the whole group INCLUDING the requester's own character.
///
/// Addressing yourself is not a curiosity: it is how the client learns about its own
/// quests held past the vanilla twenty update-field slots. One wire, two jobs.
///
/// Exact-length parsing by wire law.
/// </summary>
public static class QuestFactsWire
{
    /// <summary>MAX_RAID_SIZE — the largest subject list a pull may carry.</summary>
    public const int MaximumSubjects = 40;

    /// <summary>QUEST_OBJECTIVES_COUNT — vanilla holds four of each per quest.</summary>
    public const int ObjectivesPerQuest = 4;

    /// <summary>Sentinel slot for a quest held without an update-field slot.</summary>
    public const byte NoLogSlot = 255;

    // Vanilla QUEST_STATUS_* (QuestDef.h). Carried verbatim so the panel can speak
    // the same language as the local quest log rather than a private dialect.
    public const byte StatusNone = 0;
    public const byte StatusComplete = 1;
    public const byte StatusUnavailable = 2;
    public const byte StatusIncomplete = 3;
    public const byte StatusAvailable = 4;
    public const byte StatusFailed = 5;

    // Entry flags.
    public const byte EntryComplete = 0x01;
    public const byte EntryFailed = 0x02;
    public const byte EntryOverflow = 0x04;

    /// <summary>u64 subject + u8 flags + u16 heldCap + u16 count.</summary>
    public const int LogHeaderBytes = 13;

    /// <summary>u32 quest + u8 status + u8 flags + u8 slot + u8×4 objectives + u16×4 items.</summary>
    public const int LogEntryBytes = 19;

    /// <summary>u8 flags (reserved), u8 count (0 = whole party/raid + self), u64 guids.</summary>
    public static byte[] BuildQuestFactsBody(IReadOnlyList<ulong> subjects)
    {
        ArgumentNullException.ThrowIfNull(subjects);
        if (subjects.Count > MaximumSubjects)
            throw new ArgumentOutOfRangeException(nameof(subjects),
                $"quest-facts pull supports at most {MaximumSubjects} explicit subjects.");
        var w = new PacketWriter(2 + subjects.Count * 8);
        w.WriteU8(0);
        w.WriteU8((byte)subjects.Count);
        foreach (ulong guid in subjects) w.WriteU64(guid);
        return w.ToArray();
    }

    /// <summary>
    /// SMSG_SUI_QUEST_LOG: u64 subject, u8 flags, u16 heldCap, u16 count, then count
    /// fixed-stride entries. Exact length only — a body that is one byte off is a
    /// different packet, not a tolerable one.
    /// </summary>
    public static bool TryParseQuestLog(byte[] body, out MemberQuestLog log)
    {
        log = new MemberQuestLog(0, 0, 0, []);
        if (body.Length < LogHeaderBytes) return false;
        var r = new PacketReader(body);
        ulong subject = r.ReadU64();
        byte flags = r.ReadU8();
        ushort heldCap = r.ReadU16();
        int count = r.ReadU16();
        if (body.Length != LogHeaderBytes + count * LogEntryBytes) return false;
        if (subject == 0) return false;

        var entries = new MemberQuestEntry[count];
        for (int i = 0; i < count; i++)
        {
            uint questId = r.ReadU32();
            byte status = r.ReadU8();
            byte entryFlags = r.ReadU8();
            byte slot = r.ReadU8();
            var objectives = new byte[ObjectivesPerQuest];
            for (int j = 0; j < ObjectivesPerQuest; j++) objectives[j] = r.ReadU8();
            var items = new ushort[ObjectivesPerQuest];
            for (int j = 0; j < ObjectivesPerQuest; j++) items[j] = r.ReadU16();
            entries[i] = new MemberQuestEntry(questId, status, entryFlags, slot, objectives, items);
        }

        log = new MemberQuestLog(subject, flags, heldCap, entries);
        return true;
    }
}

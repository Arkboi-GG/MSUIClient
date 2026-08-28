namespace MSUIClient.Net;

/// <summary>One party member's verdict on one quest at a giver.</summary>
public readonly record struct GiverQuestMemberVerdict(ulong Member, byte Verdict);

/// <summary>One quest a giver offers or ends, with each member's verdict.</summary>
public readonly record struct GiverQuestEntry(uint QuestId, byte Relation,
    GiverQuestMemberVerdict[] Members)
{
    public bool Starts => (Relation & 0x01) != 0;
    public bool Ends => (Relation & 0x02) != 0;
}

/// <summary>The server's answer for one giver: which quests, and per member.</summary>
public readonly record struct GiverQuestsReply(ulong Giver, GiverQuestEntry[] Quests);

/// <summary>
/// PLAN_20 Model B wire. The client is never told which quests an NPC offers, nor a
/// companion's eligibility, so the free-view commander quest window asks the server:
/// for THIS giver, which quests, and for each party member (self included) a verdict
/// and its reason. Nested (per quest → per member) because the shape is genuinely a
/// tree; bounded on both axes so a malformed length is refused, not trusted.
/// </summary>
public static class GiverQuestsWire
{
    public const int MaximumQuests = 64;
    public const int MaximumMembersPerQuest = 40;   // MAX_RAID_SIZE

    // Must match SuiPossess::GiverQuestVerdict on the core.
    public const byte CanTake = 0;         // eligible to accept right now
    public const byte OnIt = 1;            // held, still working it
    public const byte Ready = 2;           // held and complete → can turn in here
    public const byte Done = 3;            // already rewarded (non-repeatable)
    public const byte NeedsPrereq = 4;     // previous/chain/breadcrumb quest first
    public const byte LowLevel = 5;
    public const byte WrongRaceClass = 6;
    public const byte LowSkillRep = 7;     // skill, reputation or condition
    public const byte LogFull = 8;
    public const byte Cant = 9;            // ineligible for another reason

    /// <summary>u8 flags (reserved) + u64 giver.</summary>
    public static byte[] BuildRequest(ulong giver)
    {
        var w = new PacketWriter(9);
        w.WriteU8(0);
        w.WriteU64(giver);
        return w.ToArray();
    }

    /// <summary>
    /// SMSG_SUI_GIVER_QUESTS: u8 flags, u64 giver, u16 questCount, then per quest
    /// (u32 questId, u8 relation, u8 memberCount) followed by memberCount ×
    /// (u64 guid, u8 verdict). Length-checked at every nesting level.
    /// </summary>
    public static bool TryParse(byte[] body, out GiverQuestsReply reply)
    {
        reply = default;
        if (body.Length < 11) return false;             // u8 + u64 + u16
        var r = new PacketReader(body);
        r.ReadU8();                                      // flags, reserved
        ulong giver = r.ReadU64();
        int questCount = r.ReadU16();
        if (giver == 0 || questCount > MaximumQuests) return false;

        var quests = new GiverQuestEntry[questCount];
        for (int i = 0; i < questCount; i++)
        {
            if (r.Remaining < 6) return false;           // u32 + u8 + u8
            uint questId = r.ReadU32();
            byte relation = r.ReadU8();
            int memberCount = r.ReadU8();
            if (memberCount > MaximumMembersPerQuest) return false;
            if (r.Remaining < memberCount * 9) return false;   // (u64 + u8) each
            var members = new GiverQuestMemberVerdict[memberCount];
            for (int m = 0; m < memberCount; m++)
            {
                ulong guid = r.ReadU64();
                byte verdict = r.ReadU8();
                members[m] = new GiverQuestMemberVerdict(guid, verdict);
            }
            quests[i] = new GiverQuestEntry(questId, relation, members);
        }
        reply = new GiverQuestsReply(giver, quests);
        return true;
    }
}

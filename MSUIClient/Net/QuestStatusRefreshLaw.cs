namespace MSUIClient.Net;

/// <summary>
/// Build-5875 inputs that invalidate every visible questgiver's cached dialog status.
/// The server only answers CMSG_QUESTGIVER_STATUS_QUERY, so missing one of these leaves
/// an overhead !/? frozen until the NPC streams out and back in.
/// </summary>
public static class QuestStatusRefreshLaw
{
    public static bool PacketReasks(Op opcode) => opcode is
        Op.SMSG_SET_FACTION_STANDING or
        Op.SMSG_GROUP_LIST or
        Op.SMSG_QUESTGIVER_QUEST_COMPLETE or
        Op.SMSG_QUESTUPDATE_ADD_KILL or
        Op.SMSG_QUESTUPDATE_ADD_ITEM or
        Op.SMSG_QUESTUPDATE_COMPLETE or
        Op.SMSG_QUESTUPDATE_FAILED or
        Op.SMSG_QUESTUPDATE_FAILEDTIMER;

    /// <summary>
    /// Fold the six self-player descriptor watches plus the packet epoch. A different
    /// value means the existing per-NPC asked set must be cleared once.
    /// </summary>
    public static ulong PlayerGeneration(ObjectFields fields, uint packetEpoch)
    {
        const ulong prime = 0x0000_0100_0000_01B3ul;
        ulong hash = 0xcbf2_9ce4_8422_2325ul;
        void Fold(ulong value) { hash ^= value; hash *= prime; }

        Fold(fields.Level);
        Fold(fields.IsDead ? 1u : 0u);
        Fold(fields.PlayerFlags);
        Fold(fields.Coinage);
        foreach (var quest in fields.QuestLog())
            Fold(quest.QuestId ^ ((ulong)quest.Counters << 32));
        foreach (var skill in fields.PlayerSkills())
            Fold(skill.SkillId ^ ((ulong)skill.Value << 32));
        for (int slot = 0; slot < 23; slot++) Fold(fields.PlayerInventorySlot(slot));
        Fold(packetEpoch);
        return hash;
    }
}

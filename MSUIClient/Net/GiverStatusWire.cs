namespace MSUIClient.Net;

/// <summary>
/// One (questgiver, party member) verdict: what that member would see over this
/// NPC's head if they were the one standing here.
/// </summary>
/// <param name="Giver">The questgiver's guid — creature or gameobject.</param>
/// <param name="Member">The party member the verdict is about. May be the requester.</param>
/// <param name="Status">Vanilla DIALOG_STATUS_* — see <see cref="GiverStatusWire"/>.</param>
public readonly record struct GiverMemberStatus(ulong Giver, ulong Member, byte Status);

/// <summary>
/// Party questgiver status (PLAN_20 P5). The world markers grow a parenthesised
/// numeral — <c>(4)</c> when four of your group can take what this NPC offers —
/// and this is the wire that makes the numeral honest.
///
/// It exists because the client structurally cannot compute it. Vanilla's
/// <c>SMSG_QUESTGIVER_STATUS</c> answers for the asking session and nobody else,
/// eligibility depends on level, prerequisites, race, class and exclusive groups
/// that the client never receives for a companion, and the client is never told
/// which quests an NPC even offers or ends. Guessing here would put a wrong
/// number over an NPC's head, which is worse than no number at all — so the
/// server, which already computes exactly this in
/// <c>WorldSession::GetDialogStatus</c>, is asked instead.
///
/// Exact-length parsing by wire law.
/// </summary>
public static class GiverStatusWire
{
    /// <summary>
    /// The largest questgiver list one pull may carry. Comfortably above the
    /// number of markers that can be on screen at nameplate range in a quest hub;
    /// a request past this is a bug, not a busy zone.
    /// </summary>
    public const int MaximumGivers = 64;

    /// <summary>MAX_RAID_SIZE × <see cref="MaximumGivers"/> bounds the answer.</summary>
    public const int MaximumEntries = 64 * 40;

    // Vanilla __QuestGiverStatus (QuestDef.h), carried verbatim so the marker law
    // speaks the same language as _questStatuses does for our own character.
    public const byte DialogNone = 0;
    public const byte DialogUnavailable = 1;
    public const byte DialogChat = 2;
    public const byte DialogIncomplete = 3;
    public const byte DialogRewardRep = 4;
    public const byte DialogAvailable = 5;
    public const byte DialogRewardOld = 6;
    public const byte DialogReward2 = 7;

    /// <summary>u8 flags + u16 count.</summary>
    public const int StatusHeaderBytes = 3;

    /// <summary>u64 giver + u64 member + u8 status.</summary>
    public const int StatusEntryBytes = 17;

    /// <summary>u8 flags (reserved), u8 count, then count questgiver guids.</summary>
    public static byte[] BuildGiverStatusBody(IReadOnlyList<ulong> givers)
    {
        ArgumentNullException.ThrowIfNull(givers);
        if (givers.Count == 0)
            throw new ArgumentOutOfRangeException(nameof(givers),
                "a giver-status pull must name its questgivers; there is no whole-world shorthand.");
        if (givers.Count > MaximumGivers)
            throw new ArgumentOutOfRangeException(nameof(givers),
                $"giver-status pull supports at most {MaximumGivers} questgivers.");
        var w = new PacketWriter(2 + givers.Count * 8);
        w.WriteU8(0);
        w.WriteU8((byte)givers.Count);
        foreach (ulong guid in givers) w.WriteU64(guid);
        return w.ToArray();
    }

    /// <summary>
    /// SMSG_SUI_GIVER_STATUS: u8 flags, u16 count, then count fixed-stride
    /// entries. Flat (giver, member, status) triples rather than a nested
    /// per-giver list — a fixed stride is what makes the exact-length check
    /// meaningful, and nesting would trade that away for a handful of bytes.
    /// </summary>
    public static bool TryParseGiverStatus(byte[] body, out GiverMemberStatus[] entries)
    {
        entries = [];
        if (body.Length < StatusHeaderBytes) return false;
        var r = new PacketReader(body);
        r.ReadU8();                                  // flags, reserved
        int count = r.ReadU16();
        if (count > MaximumEntries) return false;
        if (body.Length != StatusHeaderBytes + count * StatusEntryBytes) return false;

        var parsed = new GiverMemberStatus[count];
        for (int i = 0; i < count; i++)
        {
            ulong giver = r.ReadU64();
            ulong member = r.ReadU64();
            byte status = r.ReadU8();
            if (giver == 0 || member == 0) return false;
            parsed[i] = new GiverMemberStatus(giver, member, status);
        }

        entries = parsed;
        return true;
    }
}

using MSUIClient.Net;

namespace MSUIClient;

/// <summary>
/// Party quest facts (PLAN_20 P1). The member-facts law — party = full facts,
/// faction = orders — extended from bags and spells to quest logs: a quest-facts
/// capable server pushes every party/raid AiBot's quest log without possession,
/// and answers a pull addressed at the whole group.
///
/// The requester's OWN guid is a legal subject, and that is not a curiosity: it
/// is the only way this client can learn about quests it holds past the twenty
/// vanilla update-field slots (PLAN_20 P2). The local log stays the authority
/// for anything that DOES have a slot — the push only fills the gaps.
/// </summary>
public sealed partial class GameLoop
{
    private bool _partyQuestFactsAvailable;
    private double _partyQuestFactsPulledAt;
    private ulong _partyQuestFactsRosterHash;
    private const double PartyQuestFactsPullMinIntervalSeconds = 2.0;

    /// <summary>Per-subject quest logs as last pushed. Never cleared on a miss —
    /// an absent subject means "not told yet", which the panel renders honestly
    /// instead of drawing an empty log the server never claimed.</summary>
    private readonly Dictionary<ulong, MemberQuestEntry[]> _memberQuestLogs = [];
    private readonly Dictionary<ulong, double> _memberQuestLogAges = [];

    /// <summary>
    /// The server's MAX_QUEST_HELD, as last pushed. Twenty until a quest-cap
    /// server says otherwise — that is the vanilla update-field slot count and
    /// the honest assumption against any core that has not lifted the cap.
    /// </summary>
    private ushort _ownQuestHeldCap = VanillaQuestLogSlots;

    /// <summary>MAX_QUEST_LOG_SIZE. Frozen forever: it is the update-field layout.</summary>
    public const int VanillaQuestLogSlots = 20;

    private void ResetPartyQuestFacts()
    {
        _partyQuestFactsAvailable = false;
        _partyQuestFactsPulledAt = 0;
        _partyQuestFactsRosterHash = 0;
        _memberQuestLogs.Clear();
        _memberQuestLogAges.Clear();
        _ownQuestHeldCap = VanillaQuestLogSlots;
    }

    /// <summary>
    /// Our own quest log, both halves: the twenty vanilla update-field slots
    /// (live, authoritative, and the only source with a real slot index) plus
    /// any quest the server says we hold WITHOUT one. The overflow half exists
    /// only over the wire — the update fields have nowhere to put it.
    ///
    /// Overflow rows synthesize the packed Counters word the vanilla renderer
    /// reads, so every downstream consumer keeps working unchanged. That packing
    /// is six bits per objective, exactly as vanilla: lossless here because the
    /// display clamps to the required count and no 1.12 quest requires 64 of
    /// anything, and the server never counts past the requirement anyway.
    /// </summary>
    private (byte Slot, uint QuestId, uint Counters, uint Timer)[] MergedOwnQuestLog()
    {
        if (!_entities.TryGet(_net?.PlayerGuid ?? 0, out WorldEntity player)) return [];
        var merged = new List<(byte, uint, uint, uint)>(player.Fields.QuestLog());
        var slotted = new HashSet<uint>();
        foreach ((_, uint questId, _, _) in merged) slotted.Add(questId);

        foreach (MemberQuestEntry entry in MemberQuestEntries(LocalPlayerGuid))
        {
            if (entry.QuestId == 0 || slotted.Contains(entry.QuestId)) continue;
            uint counters = 0;
            for (int i = 0; i < QuestFactsWire.ObjectivesPerQuest; i++)
                counters |= (Math.Min(entry.ObjectiveCounts[i], (byte)63u) & 0x3fu) << (6 * i);
            uint state = 0;
            if (entry.Complete) state |= 1;
            if (entry.Failed) state |= 2;
            merged.Add((QuestFactsWire.NoLogSlot, entry.QuestId, counters | (state << 24), 0u));
        }
        return [.. merged];
    }

    /// <summary>How many quests this character may hold, per the server.</summary>
    private int OwnQuestHeldCap => Math.Max(VanillaQuestLogSlots, (int)_ownQuestHeldCap);

    /// <summary>True when we hold this quest without an update-field slot —
    /// the abandon and swap opcodes are both slot-indexed and cannot reach it.</summary>
    private bool IsOwnOverflowQuest(uint questId)
    {
        if (questId == 0) return false;
        if (_entities.TryGet(_net?.PlayerGuid ?? 0, out WorldEntity player))
            foreach ((_, uint slotted, _, _) in player.Fields.QuestLog())
                if (slotted == questId) return false;
        foreach (MemberQuestEntry entry in MemberQuestEntries(LocalPlayerGuid))
            if (entry.QuestId == questId) return true;
        return false;
    }

    /// <summary>
    /// Per-frame: watch the party member set and pull when it changes. The server
    /// pushes on its own roster edge as well — this is the belt to its suspenders,
    /// and the only trigger that survives a mid-session reconnect.
    /// </summary>
    private void UpdatePartyQuestFacts()
    {
        if (!_partyQuestFactsAvailable || _net is not { IsInWorld: true }) return;
        ulong hash = 14695981039346656037UL;      // FNV-1a offset basis
        foreach (PartyMember member in _partyMembers)
        {
            hash ^= member.Guid;
            hash *= 1099511628211UL;
        }
        if (hash == _partyQuestFactsRosterHash) return;
        if (RequestPartyQuestFacts("roster changed"))
            _partyQuestFactsRosterHash = hash;
    }

    /// <summary>
    /// Rate-limited whole-group pull; false = retried a later frame. Unlike the
    /// bag pull this is NOT skipped for a soloing player: an empty subject list
    /// still asks about our own character, which is where overflow quests live.
    /// </summary>
    private bool RequestPartyQuestFacts(string reason)
    {
        if (!_partyQuestFactsAvailable || _net is not { IsInWorld: true }) return false;
        double now = NowSeconds();
        if (now - _partyQuestFactsPulledAt < PartyQuestFactsPullMinIntervalSeconds)
            return false;
        _partyQuestFactsPulledAt = now;
        _net.SuiQuestFacts([]);
        Console.WriteLine($"[quest-facts] pulled party quest logs ({reason})");
        return true;
    }

    /// <summary>
    /// SMSG_SUI_QUEST_LOG: one subject's complete quest log. Accepted for any
    /// party/raid member and for our own character; anything else is dropped
    /// loudly, because a silent drop is indistinguishable from "they hold none".
    /// </summary>
    private void ApplySuiQuestLog(byte[] body)
    {
        if (!QuestFactsWire.TryParseQuestLog(body, out MemberQuestLog log))
        {
            EmitInterface("quest-facts", "log", "MALFORMED", 0, $"bytes={body.Length}");
            return;
        }
        if (log.Subject != LocalPlayerGuid && !IsPartyMemberFactsSubject(log.Subject))
        {
            Console.WriteLine($"[quest-facts] log DROPPED for 0x{log.Subject:X} — not a party member");
            EmitInterface("quest-facts", "log", "REFUSED_NOT_PARTY", log.Subject,
                $"entries={log.Entries.Length}");
            return;
        }

        _memberQuestLogs[log.Subject] = log.Entries;
        _memberQuestLogAges[log.Subject] = NowSeconds();
        if (log.Subject == LocalPlayerGuid && log.HeldCap != 0)
            _ownQuestHeldCap = log.HeldCap;

        // Titles and objective text are ordinary quest templates — the query
        // opcode carries a bare quest id and has never required holding it.
        foreach (MemberQuestEntry entry in log.Entries)
            RequireQuestTemplate(entry.QuestId);

        int overflow = log.Entries.Count(e => e.Overflow);
        Console.WriteLine($"[quest-facts] {ResolveUnitName(log.Subject)}: " +
            $"{log.Entries.Length} quests" + (overflow > 0 ? $" ({overflow} past the log slots)" : ""));
        EmitInterface("quest-facts", "log", "APPLIED", log.Subject,
            $"entries={log.Entries.Length};overflow={overflow}");
    }

    /// <summary>
    /// Ask-once template fetch for a quest we may not hold. Mirrors the item
    /// store's Require: the in-flight gate stops a panel redraw from flooding
    /// the socket, and a resolved template is answered into the shared
    /// _questTemplates / _questTitles maps by ApplyQuestQuery.
    /// </summary>
    private void RequireQuestTemplate(uint questId)
    {
        if (questId == 0 || _questTemplates.ContainsKey(questId)) return;
        if (!_questQueries.Add(questId)) return;
        _net?.QuestQuery(questId);
    }

    /// <summary>The last pushed log for a member, or empty when never told.</summary>
    private IReadOnlyList<MemberQuestEntry> MemberQuestEntries(ulong guid) =>
        _memberQuestLogs.TryGetValue(guid, out MemberQuestEntry[]? entries) ? entries : [];

    private bool HasMemberQuestFacts(ulong guid) => _memberQuestLogs.ContainsKey(guid);

    /// <summary>Seconds since this member's log was last pushed, or -1 when never.</summary>
    private double MemberQuestLogAge(ulong guid) =>
        _memberQuestLogAges.TryGetValue(guid, out double at) ? NowSeconds() - at : -1.0;
}

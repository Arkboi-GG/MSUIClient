namespace MSUIClient.Net;

/// <summary>One subject of a party quest act, and the reward it should take.</summary>
/// <param name="Guid">Own character or a party AiBot.</param>
/// <param name="RewardChoice">Index into the quest's choice rewards, or
/// <see cref="PartyQuestWire.RewardChoiceAuto"/> to let the server pick with the
/// same spec-aware heuristic the fleet already uses. Ignored for accept/abandon.</param>
public readonly record struct PartyQuestSubject(ulong Guid, byte RewardChoice);

/// <summary>One subject's outcome. Refusals are named per member, never aggregated.</summary>
public readonly record struct PartyQuestOutcome(ulong Guid, byte Result);

/// <summary>A complete, validated party-quest result.</summary>
public sealed record PartyQuestResult(byte Action, uint QuestId, PartyQuestOutcome[] Outcomes);

/// <summary>
/// Party quest acts (PLAN_20 P3). Owner decision 2026-08-25: accept and turn in
/// for the whole party in one gesture, with the REWARD CHOSEN PER BOT BY THE
/// PLAYER — every member's picker visible at once, defaulted to the server's
/// spec-aware pick and overridable.
///
/// Action 3 (abandon) addressed at your own character is also the id-keyed
/// abandon the vanilla slot-indexed opcode cannot express, which is what closes
/// the P2 gap for a quest held past the update-field slots.
///
/// Exact-length parsing by wire law.
/// </summary>
public static class PartyQuestWire
{
    /// <summary>MAX_RAID_SIZE — the largest subject list one act may carry.</summary>
    public const int MaximumSubjects = 40;

    public const byte ActionAccept = 1;
    public const byte ActionTurnIn = 2;
    public const byte ActionAbandon = 3;

    /// <summary>Let the server choose this member's reward.</summary>
    public const byte RewardChoiceAuto = 255;

    // SMSG_SUI_PARTY_QUEST_RESULT codes (SuperUI-Core SuiPossess.cpp).
    // Deliberately fine-grained: the whole point of a party act is that a refusal
    // names WHICH member and WHY, instead of collapsing five outcomes into "failed".
    public const byte ResultOk = 0;
    public const byte ResultDenied = 1;           // not on the party line / no authority
    public const byte ResultRequirements = 2;     // level, prerequisites, race, class
    public const byte ResultLogFull = 3;          // held-quest cap reached
    public const byte ResultNoQuest = 4;          // NPC does not offer/end it, or not in their log
    public const byte ResultTooFar = 5;           // outside share range, or cannot interact
    public const byte ResultBadReward = 6;        // reward index outside the quest's choices
    public const byte ResultCannotReward = 7;     // bags full, not complete, already rewarded
    public const byte ResultAlreadyHeld = 8;      // accept: already in their log (benign)
    public const byte ResultAlreadyRewarded = 9;  // accept: they already turned it in
    public const byte ResultNeedsChoice = 10;     // turn-in: "auto" asked for, nobody to choose
    public const byte ResultCannotAbandon = 11;   // quest start items cannot be un-equipped

    /// <summary>u8 action, u32 quest, u64 npc, u8 count, then {u64 guid, u8 reward}.</summary>
    public const int RequestHeaderBytes = 14;

    /// <summary>u8 action, u32 quest, u8 count, then {u64 guid, u8 result}.</summary>
    public const int ResultHeaderBytes = 6;

    /// <summary>Both directions carry the same 9-byte per-subject stride.</summary>
    public const int SubjectBytes = 9;

    public static byte[] BuildPartyQuestBody(byte action, uint questId, ulong npcGuid,
        IReadOnlyList<PartyQuestSubject> subjects)
    {
        ArgumentNullException.ThrowIfNull(subjects);
        if (action is < ActionAccept or > ActionAbandon)
            throw new ArgumentOutOfRangeException(nameof(action),
                $"party quest action {action} is not accept/turn-in/abandon.");
        if (questId == 0)
            throw new ArgumentOutOfRangeException(nameof(questId),
                "a party quest act must name a quest.");
        if (subjects.Count == 0)
            throw new ArgumentOutOfRangeException(nameof(subjects),
                "a party quest act needs an explicit subject list — there is no " +
                "whole-party shorthand, because who acts must always be visible.");
        if (subjects.Count > MaximumSubjects)
            throw new ArgumentOutOfRangeException(nameof(subjects),
                $"party quest acts support at most {MaximumSubjects} subjects.");

        var w = new PacketWriter(RequestHeaderBytes + subjects.Count * SubjectBytes);
        w.WriteU8(action);
        w.WriteU32(questId);
        w.WriteU64(npcGuid);
        w.WriteU8((byte)subjects.Count);
        foreach (PartyQuestSubject subject in subjects)
        {
            w.WriteU64(subject.Guid);
            w.WriteU8(subject.RewardChoice);
        }
        return w.ToArray();
    }

    /// <summary>Exact length only; a body that is one byte off is a different packet.</summary>
    public static bool TryParsePartyQuestResult(byte[] body, out PartyQuestResult result)
    {
        result = new PartyQuestResult(0, 0, []);
        if (body.Length < ResultHeaderBytes) return false;
        var r = new PacketReader(body);
        byte action = r.ReadU8();
        uint questId = r.ReadU32();
        int count = r.ReadU8();
        if (body.Length != ResultHeaderBytes + count * SubjectBytes) return false;
        if (action is < ActionAccept or > ActionAbandon) return false;

        var outcomes = new PartyQuestOutcome[count];
        for (int i = 0; i < count; i++)
            outcomes[i] = new PartyQuestOutcome(r.ReadU64(), r.ReadU8());

        result = new PartyQuestResult(action, questId, outcomes);
        return true;
    }

    /// <summary>True when this outcome is a success the player need not act on.</summary>
    public static bool Succeeded(byte result) =>
        result is ResultOk or ResultAlreadyHeld;

    /// <summary>The player-facing reason one member was refused, by name.</summary>
    public static string ResultText(byte result, string who) => result switch
    {
        ResultOk => $"{who}: done.",
        ResultAlreadyHeld => $"{who} already had that quest.",
        ResultDenied => $"{who} is not in your party.",
        ResultRequirements => $"{who} does not meet that quest's requirements.",
        ResultLogFull => $"{who}'s quest log is full.",
        ResultNoQuest => $"{who} has no business with that quest here.",
        ResultTooFar => $"{who} is too far away.",
        ResultBadReward => $"{who} was offered a reward that quest does not have.",
        ResultCannotReward => $"{who} cannot take the reward — bags full, or not finished.",
        ResultAlreadyRewarded => $"{who} has already turned that quest in.",
        ResultNeedsChoice => $"Pick a reward for {who}.",
        ResultCannotAbandon => $"{who} cannot put down that quest's items.",
        _ => $"{who}: failed.",
    };
}

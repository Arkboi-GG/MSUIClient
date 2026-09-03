using MSUIClient.Net;

namespace MSUIClient;

/// <summary>
/// Party quest acts (PLAN_20 P3). Accept, turn in, or abandon a quest for an
/// explicit set of party members — with the reward chosen per member, which is
/// the owner's decision and the reason the subject list carries a choice byte
/// rather than a single global one.
///
/// Addressed at your own character, action 3 is also the id-keyed abandon that
/// the vanilla slot-indexed opcode cannot express: it is what reaches a quest
/// held past the twenty update-field slots (P2).
/// </summary>
public sealed partial class GameLoop
{
    private bool _partyQuestActsAvailable;

    /// <summary>Called by the shared control-ACK capability parser.</summary>
    private void ApplyPartyQuestActsCapability(uint capabilities)
    {
        bool available = (capabilities & SuiCapabilityWire.PartyQuestActsV1) != 0;
        if (available != _partyQuestActsAvailable)
            Console.WriteLine(available
                ? "[quest-acts] server advertised party-quest-acts-v1"
                : "[quest-acts] server has no party-quest-acts-v1 advertisement");
        _partyQuestActsAvailable = available;
    }

    private void ResetPartyQuestActs() => _partyQuestActsAvailable = false;

    /// <summary>
    /// Send one party quest act. The subject list is always explicit — there is
    /// no whole-party shorthand on this wire, because who is about to act on
    /// your order should never be implicit.
    /// </summary>
    private bool RequestPartyQuestAct(byte action, uint questId, ulong npcGuid,
        IReadOnlyList<PartyQuestSubject> subjects)
    {
        if (!_partyQuestActsAvailable)
        {
            ShowUiError("Party questing needs the party-quest-acts server capability.");
            return false;
        }
        if (questId == 0 || subjects.Count == 0) return false;

        bool sent = _net?.SuiPartyQuest(action, questId, npcGuid, subjects) == true;
        EmitInterface("party-quest", ActionName(action), sent ? "SENT" : "SEND_FAILED",
            npcGuid, $"quest={questId};subjects={subjects.Count}");
        return sent;
    }

    /// <summary>
    /// Abandon a quest by id. Routed here rather than through the vanilla
    /// slot-indexed opcode so it can reach a quest held without a log slot;
    /// falls back to the vanilla path when the server has no acts capability.
    /// </summary>
    private bool AbandonQuestById(ulong subject, uint questId) =>
        RequestPartyQuestAct(PartyQuestWire.ActionAbandon, questId, 0,
            [new PartyQuestSubject(subject, PartyQuestWire.RewardChoiceAuto)]);

    private static string ActionName(byte action) => action switch
    {
        PartyQuestWire.ActionAccept => "accept",
        PartyQuestWire.ActionTurnIn => "turn-in",
        PartyQuestWire.ActionAbandon => "abandon",
        _ => "unknown",
    };

    /// <summary>
    /// SMSG_SUI_PARTY_QUEST_RESULT: one outcome per subject. Successes are a
    /// single summary line; every refusal is reported individually and by name,
    /// because "some of your party could not do that" is useless.
    /// </summary>
    private void ApplySuiPartyQuestResult(byte[] body)
    {
        if (!PartyQuestWire.TryParsePartyQuestResult(body, out PartyQuestResult result))
        {
            EmitInterface("party-quest", "result", "MALFORMED", 0, $"bytes={body.Length}");
            return;
        }

        int succeeded = 0;
        var refusals = new List<string>();
        foreach (PartyQuestOutcome outcome in result.Outcomes)
        {
            if (PartyQuestWire.Succeeded(outcome.Result))
            {
                succeeded++;
                // The result is authoritative. Retire the cached row immediately;
                // the following server push/re-pull supplies the complete new log.
                if (result.Action == PartyQuestWire.ActionAbandon)
                    ForgetQuestFact(outcome.Guid, result.QuestId);
            }
            else refusals.Add(PartyQuestWire.ResultText(outcome.Result,
                ResolveUnitName(outcome.Guid)));
        }

        string title = _questTitles.GetValueOrDefault(result.QuestId, $"Quest {result.QuestId}");
        if (succeeded > 0)
            AddChatMessage(result.Action switch
            {
                PartyQuestWire.ActionAccept => $"{succeeded} took \"{title}\".",
                PartyQuestWire.ActionTurnIn => $"{succeeded} turned in \"{title}\".",
                _ => $"{succeeded} put down \"{title}\".",
            });
        foreach (string refusal in refusals)
            AddChatMessage(refusal);
        // One toast only, however many were refused — the chat lines carry the detail.
        if (refusals.Count > 0 && succeeded == 0)
            ShowUiError(refusals.Count == 1 ? refusals[0]
                : $"None of the {refusals.Count} selected could do that.");
        if (result.Action == PartyQuestWire.ActionAbandon && succeeded > 0)
            RequestPartyQuestFacts("party quest abandoned");
        if (result.Action == PartyQuestWire.ActionTurnIn && succeeded > 0)
            RequestPartyQuestFacts("party quest turned in");
        if (result.Action == PartyQuestWire.ActionAccept && succeeded > 0)
        {
            RequestPartyQuestFacts("party quest accepted");
            AutoWatchQuest(result.QuestId);
        }

        EmitInterface("party-quest", ActionName(result.Action), "APPLIED", 0,
            $"quest={result.QuestId};ok={succeeded};refused={refusals.Count}");

        // A party act moved someone's quest state — refresh the commander giver board so a
        // just-turned-in quest stops showing "ready to turn in" without a close/re-open.
        if (succeeded > 0) RefreshGiverQuestsIfOpen();
    }

    /// <summary>
    /// Vanilla quest sharing (PLAN_20 P3). Not a SuperUI act: this is the real
    /// 1.12 push, so a human party member gets the real confirmation dialog.
    /// Companions answer it in their own AI, which is why the button stayed dead
    /// until the server hook existed — sharing to a bot that never answers strands
    /// the share slot and makes every later share to it report "busy".
    /// </summary>
    private void ShareQuestWithParty(uint questId)
    {
        if (questId == 0 || _net is not { IsInWorld: true }) return;
        bool sent = _net.PushQuestToParty(questId);
        EmitInterface("quest", "share", sent ? "SENT" : "SEND_FAILED",
            _net.PlayerGuid, $"quest={questId}");
    }

    // MSG_QUEST_PUSH_RESULT (QuestShareMessages, QuestDef.h).
    private const byte QuestPushSharing = 0;
    private const byte QuestPushCantTake = 1;
    private const byte QuestPushAccepted = 2;
    private const byte QuestPushDeclined = 3;
    private const byte QuestPushTooFar = 4;
    private const byte QuestPushBusy = 5;
    private const byte QuestPushLogFull = 6;
    private const byte QuestPushHaveQuest = 7;
    private const byte QuestPushFinished = 8;

    /// <summary>
    /// MSG_QUEST_PUSH_RESULT: one member's answer to a share we sent. The
    /// "sharing…" notice is noise — every recipient generates one — so it is
    /// dropped and only real answers reach the chat frame.
    /// </summary>
    private void ApplyQuestPushResult(byte[] body)
    {
        if (body.Length != 9) return;
        var r = new PacketReader(body);
        ulong guid = r.ReadU64();
        byte msg = r.ReadU8();
        if (msg == QuestPushSharing) return;

        string who = ResolveUnitName(guid);
        AddChatMessage(msg switch
        {
            QuestPushAccepted => $"{who} accepted your quest.",
            QuestPushDeclined => $"{who} declined your quest.",
            QuestPushCantTake => $"{who} cannot take that quest.",
            QuestPushTooFar => $"{who} is too far away to share with.",
            QuestPushBusy => $"{who} is busy with another quest offer.",
            QuestPushLogFull => $"{who}'s quest log is full.",
            QuestPushHaveQuest => $"{who} already has that quest.",
            QuestPushFinished => $"{who} has already finished that quest.",
            _ => $"{who} did not take your quest.",
        });
    }

    /// <summary>
    /// The companions that may be included in an act on this quest right now.
    /// Purely a display/selection aid — the server re-authorizes every subject,
    /// and its answer is what the player is actually told.
    /// </summary>
    private List<(ulong Guid, string Name)> PartyQuestCandidates()
    {
        var candidates = new List<(ulong, string)>();
        foreach (PartyMember member in _partyMembers)
            // Quest facts are the authoritative bot identity in ordinary embodied
            // play. The RTS roster may not exist until commander view is entered,
            // so using it here made the quest rail disappear outside that mode.
            if (HasMemberQuestFacts(member.Guid))
                candidates.Add((member.Guid, member.Name));
        return candidates;
    }
}

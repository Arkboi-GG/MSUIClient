using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    // The ordinary entity stream around an embodied player is roughly this neighborhood.
    // Free View adds a second, camera-owned stream; quest status queries must not follow it.
    private const float QuestStatusSessionNeighborhoodSquared = 130f * 130f;

    private readonly record struct QuestAbandonConfirmation(
        ulong Subject, uint QuestId, string Title);
    private readonly record struct PartyQuestAbandonConfirmation(
        uint QuestId, string Title, ulong[] Subjects);
    private readonly record struct QuestLogDisplayRow(bool IsHeader, string Header,
        bool Collapsed, byte Slot, uint QuestId, uint Counters, uint Timer);
    private readonly record struct QuestWatchLine(
        uint QuestId, string Text, bool Title, bool Finished);
    private readonly record struct QuestWatchTitleHit(
        uint QuestId, Vector2 Min, Vector2 Max);

    private QuestList? _questList;
    private QuestDetails? _questDetails;
    private QuestOffer? _questOffer;
    private QuestRequestItems? _questRequestItems;
    private bool _questLogOpen;
    private uint _questLogSelectedQuestId;
    private int _questLogOffset;
    private float _questLogDetailScroll;
    private float _questLogDetailContentHeight = QuestFrameUiLaw.QuestLogDetailRect.Height;
    private readonly HashSet<string> _questLogCollapsed = new(StringComparer.Ordinal);
    private QuestSortCatalog? _questSorts;
    private bool _questSortsLoaded;
    private uint _questServerUnix;
    private long _questServerUnixStamp;
    private long _questTimeAskedStamp;
    private QuestAbandonConfirmation? _questAbandonConfirmation;
    private PartyQuestAbandonConfirmation? _partyQuestAbandonConfirmation;
    private readonly List<uint> _questWatches = [];
    private readonly HashSet<uint> _questWatchCollapsed = [];
    private readonly List<QuestWatchTitleHit> _questWatchTitleHits = [];
    private readonly Dictionary<uint, double> _questAutoWatchExpiries = [];
    private readonly HashSet<uint> _questAutoWatchPending = [];
    private readonly Dictionary<uint, string> _questProgress = [];
    private readonly Dictionary<uint, string> _questTitles = [];
    private readonly Dictionary<uint, QuestTemplate> _questTemplates = [];
    private readonly HashSet<uint> _questQueries = [];
    private readonly Dictionary<ulong, uint> _questStatuses = [];
    private readonly Dictionary<ulong, ulong> _questStatusAsked = [];
    private ulong _questStatusPlayerGeneration;
    private bool _questStatusPlayerGenerationKnown;
    private uint _questStatusReaskEpoch;
    private readonly Dictionary<uint, uint> _questWorldStates = [];
    private HashSet<uint> _questLogSnapshot = [];
    private bool _questLogSnapshotKnown;
    private uint _questXpBefore;
    private uint _questMoneyBefore;
    private uint _questExpectedXp;
    private int _questExpectedMoney;
    private uint _questRewardPending;
    private float _questNpcScroll;
    private float _questNpcContentHeight = QuestFrameUiLaw.ScrollHeight;
    private int _questRewardChoice = -1;

    private bool QuestGate(ulong guid, string step, Func<NetworkClient, bool> send)
    {
        string outcome = "REFUSED"; string detail = "descriptorMissing";
        if (_net is not { IsInWorld: true } net)
        {
            outcome = "REFUSED_NOT_IN_WORLD";
            detail = "inWorld=false";
        }
        else if (GuidInfo.IsItem(guid))
        {
            bool sent = send(net); outcome = sent ? "SENT" : "SEND_FAILED";
            detail = "giver=item;rangeGate=bypassed";
        }
        else if (GuidInfo.IsPlayer(guid))
        {
            // A SHARED quest: the details packet's giver is the sharing party member (vmangos
            // QuestHandler.cpp:450), and the accept goes back addressed to that player. This
            // branch used to fall through to "descriptorMissing", so a shared quest could be
            // read but never accepted. The leash is QUEST_SHARE_DISTANCE, not the NPC gate.
            if (TryGetInteractionBodyPose(out WorldBodyPose body) &&
                _entities.TryGet(guid, out WorldEntity sharer))
            {
                float distance = Vector3.Distance(body.Position, sharer.Position);
                if (distance <= InventoryUiLaw.QuestShareDistance)
                {
                    bool sent = send(net); outcome = sent ? "SENT" : "SEND_FAILED";
                    detail = $"giver=player;distance={distance:R}";
                }
                else { outcome = "REFUSED_RANGE"; detail = $"giver=player;distance={distance:R};limit={InventoryUiLaw.QuestShareDistance:R}"; }
            }
            else
            {
                // The sharer may be out of view range; the server enforces the distance anyway.
                bool sent = send(net); outcome = sent ? "SENT" : "SEND_FAILED";
                detail = "giver=player;unstreamed";
            }
        }
        else if (TryGetInteractionBodyPose(out WorldBodyPose sessionBody) &&
            _entities.TryGet(guid, out WorldEntity npc) && npc.IsCreature && !npc.IsDead &&
            (npc.NpcFlags & NpcQuestGiver) != 0)
        {
            float distance = Vector3.Distance(sessionBody.Position, npc.Position);
            if (distance <= GossipInteractDistance)
            {
                bool sent = send(net); outcome = sent ? "SENT" : "SEND_FAILED";
                detail = $"distance={distance:R};npcFlags=0x{npc.NpcFlags:X8}";
            }
            else { outcome = "REFUSED_RANGE"; detail = $"distance={distance:R};limit={GossipInteractDistance:R}"; }
        }
        EmitInterface("quest", step, outcome, guid, detail); return outcome == "SENT";
    }

    private bool RequestQuestStatus(ulong guid) => QuestGate(guid, "status", n => n.QuestgiverStatus(guid));
    private bool RequestQuestHello(ulong guid)
    {
        if (RefuseTacticalFreezeLiveCommand("opening quest services")) return false;
        if (RefuseTacticalFrozenActor(guid, "open its quest service")) return false;
        return QuestGate(guid, "hello", n => n.QuestgiverHello(guid));
    }

    private bool RequestQuestDetails(ulong guid, uint questId)
    {
        if (RefuseTacticalFreezeLiveCommand("opening quest details")) return false;
        if (RefuseTacticalFrozenActor(guid, "request quest details from it")) return false;
        return QuestGate(guid, "details", n => n.QuestgiverQuery(guid, questId));
    }

    private bool AcceptQuest()
    {
        if (_questDetails is null) return false;
        if (RefuseTacticalFreezeLiveCommand("accepting a quest")) return false;
        ulong giver = _questDetails.GiverGuid;
        if (RefuseTacticalFrozenActor(giver, "accept a quest from them")) return false;
        uint quest = _questDetails.QuestId;
        SnapshotQuestEconomy();
        bool sent = QuestGate(giver, "accept", n => n.QuestgiverAccept(giver, quest));
        // An accepted share is answered by the accept packet itself; the close below must
        // not also send DECLINE back to the sharer.
        if (sent) _questShareAnswered = true;
        // Build 5875 closes on the click itself; it does not wait for an accept response.
        CloseQuestNpcFrame(playSound: true);
        // PLAN_20 P2/P3: a quest accepted past the twenty update-field slots has no
        // field change to observe, so without this the client would not learn it
        // exists until an unrelated roster edge. Rate-limited; harmless when the
        // quest did get a slot.
        if (sent)
        {
            RequestPartyQuestFacts("quest accepted");
            AutoWatchQuest(quest);
        }
        // [SUI] P4b: an accept AS a driven bot changes no own-player field and
        // echoes no quest packet, so nothing else invalidates the giver's "!".
        if (sent) BumpQuestStatusReask();
        return sent;
    }

    private bool RequestQuestCompletion(ulong guid, uint id)
    {
        if (id == 0 || RefuseTacticalFreezeLiveCommand("opening quest completion")) return false;
        if (RefuseTacticalFrozenActor(guid, "complete a quest with them")) return false;
        return QuestGate(guid, "complete-request", n => n.QuestgiverComplete(guid, id));
    }

    // Explicit protocol/dev command compatibility. Production Progress Continue uses
    // RequestQuestReward; this overload is only the named COMPLETE_QUEST request path.
    private bool RequestQuestCompletion()
    {
        uint id = _questRequestItems?.QuestId ?? _questOffer?.QuestId ?? _questDetails?.QuestId ?? 0;
        ulong guid = _questRequestItems?.GiverGuid ?? _questOffer?.GiverGuid ??
            _questDetails?.GiverGuid ?? 0;
        return RequestQuestCompletion(guid, id);
    }

    private bool RequestQuestReward()
    {
        uint id = _questRequestItems?.QuestId ?? _questDetails?.QuestId ?? 0;
        ulong guid = _questRequestItems?.GiverGuid ?? _questDetails?.GiverGuid ?? 0;
        if (id != 0 && RefuseTacticalFreezeLiveCommand("opening quest rewards")) return false;
        if (id != 0 && RefuseTacticalFrozenActor(guid, "request a quest reward from them"))
            return false;
        return id != 0 && QuestGate(guid, "reward-request", n => n.QuestgiverRequestReward(guid, id));
    }

    private bool ChooseQuestReward(uint choice)
    {
        if (_questOffer is null || choice >= Math.Max(1, _questOffer.ChoiceRewards.Count)) return false;
        if (RefuseTacticalFreezeLiveCommand("completing a quest")) return false;
        if (RefuseTacticalFrozenActor(_questOffer.GiverGuid,
                "choose a quest reward from them")) return false;
        SnapshotQuestEconomy();
        bool rewarded = QuestGate(_questOffer.GiverGuid, "reward-choice",
            n => n.QuestgiverChooseReward(_questOffer.GiverGuid, _questOffer.QuestId, choice));
        if (rewarded) RequestPartyQuestFacts("quest turned in");
        return rewarded;
    }

    private bool AbandonQuest(uint questId)
    {
        ulong subject = !_freeView && ControlledGuid != 0 ? ControlledGuid : LocalPlayerGuid;
        return AbandonQuest(subject, questId);
    }

    private bool AbandonQuest(ulong subject, uint questId)
    {
        if (_net is null || subject == 0 || questId == 0) return false;
        if (RefuseTacticalFreezeLiveCommand("abandoning a quest")) return false;
        if (RefuseTacticalFrozenActor(subject, "change its quests")) return false;
        var found = QuestLogForSubject(subject).FirstOrDefault(q => q.QuestId == questId);
        bool present = found.QuestId != 0 && found.Slot != QuestFactsWire.NoLogSlot;
        if (found.QuestId == 0)
        {
            EmitInterface("quest", "abandon", "REFUSED_NOT_IN_LOG", subject,
                $"quest={questId}");
            return false;
        }

        // The party act names both quest and subject. Prefer it whenever available:
        // the legacy request names only a slot, so while a bot is controlled it can
        // neither safely address the parked main character nor express an overflow
        // quest. This is also the only valid route for another party member.
        if (_partyQuestActsAvailable)
            return AbandonQuestById(subject, questId);

        if (subject != LocalPlayerGuid)
        {
            ShowUiError("Abandoning a companion's quest needs the party-quest-acts server capability.");
            EmitInterface("quest", "abandon", "REFUSED_NO_PARTY_ACTS", subject,
                $"quest={questId}");
            return false;
        }
        ulong legacySubject = !_freeView && ControlledGuid != 0
            ? ControlledGuid
            : LocalPlayerGuid;
        if (subject != legacySubject)
        {
            ShowUiError("Switch control to that character before abandoning this quest.");
            EmitInterface("quest", "abandon", "REFUSED_WRONG_CONTROLLED_BODY", subject,
                $"quest={questId};controlled={legacySubject}");
            return false;
        }
        if (!present)
        {
            ShowUiError("That quest is held past your quest-log slots — " +
                "abandon a slotted quest first to free one.");
            EmitInterface("quest", "abandon", "REFUSED_NO_LOG_SLOT", subject,
                $"quest={questId}");
            return false;
        }
        bool sent = present && _net.QuestLogRemove(found.Slot);
        if (sent)
        {
            // Abandoning frees a slot, which the server fills by promoting an
            // overflow quest — a change with no update-field signal we can read
            // for the promoted quest and no server push of its own. Without both
            // of these the log kept showing the abandoned quest as an overflow row.
            ForgetQuestFact(subject, questId);
            RequestPartyQuestFacts("quest abandoned");
        }
        EmitInterface("quest", "abandon", sent ? "SENT" : "REFUSED_NOT_IN_LOG", subject,
            $"quest={questId};slot={(present ? found.Slot : 255)}");
        return sent;
    }

    private bool InspectQuestLog()
    {
        if (_net is null || !_entities.TryGet(_net.PlayerGuid, out WorldEntity player)) return false;
        string fields = string.Join(',', Enumerable.Range(188, 90)
            .Select(i => (Index: i, Value: player.Fields.GetU32((ushort)i) ?? 0))
            .Where(x => x.Value != 0).Select(x => $"{x.Index}:{x.Value:X8}"));
        string log = string.Join(',', player.Fields.QuestLog().Select(q => $"{q.Slot}:{q.QuestId}:{q.Counters:X8}:{q.Timer:X8}"));
        EmitInterface("quest", "log-scan", "SNAPSHOT", _net.PlayerGuid,
            $"decoded={SanitizeEvidence(log)};nonzero188to277={SanitizeEvidence(fields)}");
        return true;
    }

    private void ApplyQuestStatus(byte[] body)
    {
        QuestStatus value = QuestPackets.ParseStatus(body);
        _questStatuses[value.GiverGuid] = value.Status;
        EmitInterface("quest", "status", "DECODED", value.GiverGuid, $"status={value.Status}");
    }

    /// <summary>
    /// The server only answers status queries. Mirror the reference's visible-giver sweep and
    /// re-ask when player quest inputs or the giver's service/faction fields change.
    /// </summary>
    private void UpdateQuestGiverStatusQueries()
    {
        // [SUI] P4b: sweep around the INTERACTION body — the driven bot while
        // possessing, the session character otherwise. The server answers
        // CMSG_QUESTGIVER_STATUS_QUERY as GetSuiActor(), so the statuses this
        // sweep collects are the driven bot's; asking about the NPCs around the
        // parked commander while driving a bot across the zone answered for
        // givers nobody is looking at. Never the free-view camera (the
        // interaction body is always a real body).
        if (_net is not { IsInWorld: true } net ||
            !TryGetInteractionBodyPose(out WorldBodyPose sessionBody) ||
            !_entities.TryGet(net.PlayerGuid, out WorldEntity player)) return;

        ulong generation = QuestStatusRefreshLaw.PlayerGeneration(
            player.Fields, _questStatusReaskEpoch);
        if (!_questStatusPlayerGenerationKnown || generation != _questStatusPlayerGeneration)
        {
            _questStatusPlayerGenerationKnown = true;
            _questStatusPlayerGeneration = generation;
            _questStatusAsked.Clear();
        }

        foreach (ulong stale in _questStatusAsked.Keys
                     .Where(guid => !_entities.TryGet(guid, out WorldEntity live) ||
                         !live.IsCreature || Vector3.DistanceSquared(
                             sessionBody.Position, live.Position) >
                             QuestStatusSessionNeighborhoodSquared).ToArray())
        {
            _questStatusAsked.Remove(stale);
            _questStatuses.Remove(stale);
        }

        foreach (WorldEntity npc in _entities.Units.Where(unit => unit.IsCreature))
        {
            if ((npc.NpcFlags & NpcQuestGiver) == 0 ||
                Vector3.DistanceSquared(sessionBody.Position, npc.Position) >
                    QuestStatusSessionNeighborhoodSquared)
            {
                _questStatusAsked.Remove(npc.Guid);
                _questStatuses.Remove(npc.Guid);
                continue;
            }

            ulong key = npc.NpcFlags | ((ulong)npc.Fields.FactionTemplate << 32);
            if (_questStatusAsked.GetValueOrDefault(npc.Guid, ulong.MaxValue) == key) continue;
            if (net.QuestgiverStatus(npc.Guid)) _questStatusAsked[npc.Guid] = key;
        }
    }

    private void BumpQuestStatusReask() =>
        _questStatusReaskEpoch = unchecked(_questStatusReaskEpoch + 1);

    private bool BeginQuestNpcPanel(ulong giverGuid)
    {
        QuestNpcPanel oldPanel = QuestNpcPanelNow();
        ulong oldGiver = QuestGiverGuid();
        if (oldPanel != QuestNpcPanel.None && oldGiver != giverGuid)
            CloseQuestNpcFrame(playSound: true);
        ClosePanelsDisplacedByQuest();
        return QuestNpcPanelNow() == QuestNpcPanel.None;
    }

    private void ClosePanelsDisplacedByQuest()
    {
        // Reuse each MSUI panel's established close path, but deliberately leave bag windows and
        // the current same-giver quest session alone. Benilla's left-panel ownership displaces
        // these surfaces before the incoming quest panel becomes visible.
        if (_bindingCapture is not null) _bindingCapture = null;
        if (_keybindingsOpen)
        {
            if (_bindingSnapshot is not null)
            {
                _bindings.Clear();
                foreach (var pair in _bindingSnapshot) _bindings[pair.Key] = pair.Value;
            }
            _bindingSnapshot = null;
            _keybindingsOpen = false;
        }
        if (_tradeOpen) { _net?.CancelTrade(); ResetTrade(); }
        if (_inspectOpen) CloseInspect(playSound: true);
        if (_auctionOpen) ResetAuction();
        if (_mailOpen) CloseMailSession();
        if (_gossipMenu is not null || _gossipGreeting is not null) ResetGossip();
        CloseVendorSession();
        CloseTrainerSession(playSound: true);
        _gameObjectGuid = 0;
        _worldMapOpen = false;
        if (_macroOpen) CloseMacros();
        _helpOpen = false;
        CloseFriendsFrame();
        CloseProfessionFrame();
        CloseBankSession(playSound: true);
        _tabardOpen = false;
        if (_taxiOpen && !_taxiLocked) CloseTaxiMap(playSound: true);
        _talentOpen = false;
        _questLogOpen = false;
        _spellbookOpen = false;
        if (_characterOpen) SetCharacterPageOpen(false);
        if (_loot.IsOpen) TryCloseLootOnEscape();
    }

    private void ApplyQuestList(byte[] body)
    {
        QuestList parsed = QuestPackets.ParseList(body);
        bool opening = BeginQuestNpcPanel(parsed.GiverGuid);
        _questList = parsed; _questDetails = null; _questOffer = null; _questRequestItems = null;
        _questNpcScroll = 0; _questRewardChoice = -1;
        _questNpcContentHeight = QuestFrameUiLaw.ScrollHeight;
        if (opening) PlayUiSound("igQuestListOpen");
        foreach (GossipQuest quest in _questList.Quests) _questTitles[quest.QuestId] = quest.Title;
        EmitInterface("quest", "list", "DECODED", _questList.GiverGuid,
            $"quests={_questList.Quests.Count};greeting={SanitizeEvidence(_questList.Greeting)}");
    }

    private void ApplyQuestDetails(byte[] body)
    {
        QuestDetails parsed = QuestPackets.ParseDetails(body);
        // A share landing on an already-open quest window is refused as BUSY and the current
        // panel stays; the reference never replaces a window the player is reading.
        if (GuidInfo.IsPlayer(parsed.GiverGuid) && QuestNpcPanelNow() != QuestNpcPanel.None)
        {
            _net?.QuestPushResult(parsed.GiverGuid, QuestPushBusy);
            EmitInterface("quest", "share-answer", "BUSY", parsed.GiverGuid, $"quest={parsed.QuestId}");
            return;
        }
        _questShareAnswered = false;
        bool opening = BeginQuestNpcPanel(parsed.GiverGuid);
        _questDetails = parsed; _questList = null; _questOffer = null; _questRequestItems = null;
        _questNpcScroll = 0; _questRewardChoice = -1;
        _questNpcContentHeight = QuestFrameUiLaw.ScrollHeight;
        if (opening) PlayUiSound("igQuestListOpen");
        PlayUiSound("WriteQuest"); // instant-text mode still scratches exactly once
        _questTitles[_questDetails.QuestId] = _questDetails.Title;
        EmitInterface("quest", "details", "DECODED", _questDetails.GiverGuid,
            $"quest={_questDetails.QuestId};title={SanitizeEvidence(_questDetails.Title)};objectives={SanitizeEvidence(_questDetails.Objectives)};choices={_questDetails.ChoiceRewards.Count};fixed={_questDetails.FixedRewards.Count};money={_questDetails.Money}");
    }

    private void ApplyQuestRequestItems(byte[] body)
    {
        QuestRequestItems parsed = QuestPackets.ParseRequestItems(body);
        bool opening = BeginQuestNpcPanel(parsed.GiverGuid);
        _questRequestItems = parsed;
        _questList = null; _questDetails = null; _questOffer = null;
        _questNpcScroll = 0; _questRewardChoice = -1;
        _questNpcContentHeight = QuestFrameUiLaw.ScrollHeight;
        if (opening) PlayUiSound("igQuestListOpen");
        EmitInterface("quest", "request-items", "DECODED", _questRequestItems.GiverGuid,
            $"quest={_questRequestItems.QuestId};items={_questRequestItems.RequiredItems.Count};money={_questRequestItems.RequiredMoney};completable={_questRequestItems.Completable}");
    }

    private void ApplyQuestOffer(byte[] body)
    {
        QuestOffer parsed = QuestPackets.ParseOffer(body);
        bool opening = BeginQuestNpcPanel(parsed.GiverGuid);
        _questOffer = parsed;
        _questList = null; _questDetails = null; _questRequestItems = null;
        _questNpcScroll = 0; _questRewardChoice = -1;
        _questNpcContentHeight = QuestFrameUiLaw.ScrollHeight;
        if (opening) PlayUiSound("igQuestListOpen");
        _questTitles[_questOffer.QuestId] = _questOffer.Title;
        EmitInterface("quest", "offer", "DECODED", _questOffer.GiverGuid,
            $"quest={_questOffer.QuestId};choices={_questOffer.ChoiceRewards.Count};fixed={_questOffer.FixedRewards.Count};money={_questOffer.Money};title={SanitizeEvidence(_questOffer.Title)}");
    }

    private void ApplyQuestKill(byte[] body)
    {
        QuestKillUpdate value = QuestPackets.ParseKill(body);
        _questProgress[value.QuestId] = $"entry {value.Entry}: {value.Current}/{value.Required}";
        ShowUiInfo(QuestKillProgressText(value));
        AutoWatchQuest(value.QuestId);
        EmitInterface("quest", "objective-kill", "UPDATED", value.Guid,
            $"quest={value.QuestId};entry={value.Entry};current={value.Current};required={value.Required}");
    }

    private void ApplyQuestItem(byte[] body)
    {
        var value = QuestPackets.ParseItem(body);
        if (_net is not null && _entities.TryGet(_net.PlayerGuid, out WorldEntity player))
            foreach (uint questId in MergedOwnQuestLog().Select(q => q.QuestId))
                if (_questTemplates.TryGetValue(questId, out QuestTemplate? template) &&
                    template.Objectives.Any(o => o.ItemId == value.ItemId && o.ItemCount > 0))
                {
                    AutoWatchQuest(questId);
                    QuestLogObjective objective = template.Objectives.First(o =>
                        o.ItemId == value.ItemId && o.ItemCount > 0);
                    uint current = Math.Min(CarriedCount(value.ItemId), objective.ItemCount);
                    string label = objective.Text.Length > 0 ? objective.Text :
                        _items?.TryGet(value.ItemId, out ItemTemplate? item) == true && item is not null
                            ? item.Name : "...";
                    ShowUiInfo($"{label}: {current}/{objective.ItemCount}");
                }
        EmitInterface("quest", "objective-item", "UPDATED", 0, $"item={value.ItemId};added={value.Count}");
    }

    private void ObserveQuestItemTemplateLanding(uint itemId)
    {
        if (itemId == 0 || _questOffer is null) return;
        bool belongs = _questOffer.ChoiceRewards.Any(row => row.ItemId == itemId) ||
            _questOffer.FixedRewards.Any(row => row.ItemId == itemId);
        if (!belongs) return;
        // QUEST_ITEM_UPDATE rebuilds the reward panel and clears its local choice in the frozen UI.
        _questRewardChoice = -1;
        EmitInterface("quest", "item-template", "REWARD_REFRESH", _questOffer.GiverGuid,
            $"item={itemId};selectionReset=true");
    }

    private void ApplyQuestObjectiveComplete(byte[] body)
    {
        uint id = QuestPackets.ParseQuestId(body); _questProgress[id] = "complete";
        string title = _questTitles.GetValueOrDefault(id, "");
        ShowUiInfo(title.Length > 0 ? $"{title} (Complete)" : "Objective Complete.");
        AutoWatchQuest(id);
        EmitInterface("quest", "objective", "COMPLETE", 0, $"quest={id}");
    }

    private void ApplyQuestComplete(byte[] body)
    {
        QuestComplete value = QuestPackets.ParseComplete(body);
        _questHelperRewardedThisSession.Add(value.QuestId);
        ulong giver = QuestGiverGuid();
        _questRewardPending = value.QuestId; _questExpectedXp = value.Experience; _questExpectedMoney = value.Money;
        EmitInterface("quest", "reward", "COMPLETED", _net?.PlayerGuid ?? 0,
            $"quest={value.QuestId};xp={value.Experience};money={value.Money};items={value.Rewards.Count};xpBefore={_questXpBefore};moneyBefore={_questMoneyBefore}");
        PlayUiSound("iQuestComplete");
        CloseQuestNpcFrame(playSound: true);
        if (giver != 0 && !GuidInfo.IsItem(giver)) RequestQuestStatus(giver);
    }

    private QuestNpcPanel QuestNpcPanelNow() => _questList is not null ? QuestNpcPanel.Greeting :
        _questDetails is not null ? QuestNpcPanel.Detail : _questRequestItems is not null ? QuestNpcPanel.Progress :
        _questOffer is not null ? QuestNpcPanel.Reward : QuestNpcPanel.None;

    private static string QuestPanelStem(QuestNpcPanel panel) => panel switch
    {
        QuestNpcPanel.Greeting => "BenillaQuestGreeting",
        QuestNpcPanel.Detail => "BenillaQuestDetail",
        QuestNpcPanel.Progress => "BenillaQuestProgress",
        QuestNpcPanel.Reward => "BenillaQuestReward",
        _ => "BenillaQuest",
    };

    private ulong QuestGiverGuid() => _questList?.GiverGuid ?? _questDetails?.GiverGuid ??
        _questRequestItems?.GiverGuid ?? _questOffer?.GiverGuid ?? 0;

    /// <summary>Set once a shared quest's details have been answered (accepted or declined).</summary>
    private bool _questShareAnswered;

    private void CloseQuestNpcFrame(bool playSound)
    {
        bool wasOpen = QuestNpcPanelNow() != QuestNpcPanel.None;
        // A shared quest that closes unanswered — Decline, Escape, walking away — tells the
        // sharer so: MSG_QUEST_PUSH_RESULT{sharer, DECLINE_QUEST}. Without it the sharer sat on
        // "Sharing quest with X…" forever and the server's share latch was never released.
        if (_questDetails is { } details && GuidInfo.IsPlayer(details.GiverGuid) && !_questShareAnswered)
        {
            _net?.QuestPushResult(details.GiverGuid, QuestPushDeclined);
            EmitInterface("quest", "share-answer", "DECLINED", details.GiverGuid, $"quest={details.QuestId}");
        }
        _questShareAnswered = false;
        _questList = null; _questDetails = null; _questRequestItems = null; _questOffer = null;
        _questNpcScroll = 0; _questRewardChoice = -1;
        _questNpcContentHeight = QuestFrameUiLaw.ScrollHeight;
        // The companion rail's picks belong to the offer that is closing. Leaking
        // them into the next quest would silently hand someone a reward index
        // chosen for a different one.
        ResetQuestPartyRail();
        if (wasOpen && playSound) PlayUiSound("igQuestListClose");
    }

    private void ResetQuestSession(bool clearStatusStore)
    {
        CloseQuestNpcFrame(playSound: false);
        _questLogOpen = false;
        _questLogSelectedQuestId = 0;
        _questLogOffset = 0;
        _questLogDetailScroll = 0;
        _questLogDetailContentHeight = QuestFrameUiLaw.QuestLogDetailRect.Height;
        _questLogCollapsed.Clear();
        _questServerUnix = 0;
        _questServerUnixStamp = 0;
        _questTimeAskedStamp = 0;
        _questAbandonConfirmation = null;
        _partyQuestAbandonConfirmation = null;
        _questWatches.Clear();
        _questWatchCollapsed.Clear();
        _questWatchTitleHits.Clear();
        _questAutoWatchExpiries.Clear();
        _questAutoWatchPending.Clear();
        _questLogSnapshot.Clear();
        _questLogSnapshotKnown = false;
        _questQueries.Clear();
        _questWorldStates.Clear();
        ClearRtsTerritoryCapture();
        if (clearStatusStore)
        {
            _questStatuses.Clear();
            _questStatusAsked.Clear();
            _questStatusPlayerGenerationKnown = false;
            _questStatusReaskEpoch = 0;
        }
    }

    private bool UpdateQuestNpcLifecycle()
    {
        if (_uiParityArmed && _uiParityPanel == "quest-frame" && _uiParityFixtureStaged)
            return false;
        if (QuestNpcPanelNow() == QuestNpcPanel.None) return false;
        ulong giver = QuestGiverGuid();
        string reason = "";
        if (QuestDisplacingPanelOpen())
            reason = "left-panel-displaced";
        else if (GuidInfo.IsItem(giver))
            return false;
        // [SUI] P4b: the OPEN gates (QuestGate, RequestGossip) measure from the
        // interaction body — the driven bot while possessing — but this lifecycle
        // measured from the SESSION body, so a frame the bot legitimately opened
        // far from the parked commander closed itself the very next tick. That was
        // "can't talk to an NPC unless my logged-in character is nearby."
        else if (!TryGetInteractionBodyPose(out WorldBodyPose sessionBody))
            return false;
        else if (!_entities.TryGet(giver, out WorldEntity npc))
            reason = GuidInfo.IsPlayer(giver) ? "" : "giver-despawned";   // a sharer may be out of view range
        else
        {
            float distanceSquared = Vector3.DistanceSquared(sessionBody.Position, npc.Position);
            // A PLAYER giver (a shared quest) leashes at QUEST_SHARE_DISTANCE, not the 5.5 yd NPC
            // gate — a party member ten yards away had the share window torn down the frame
            // after it opened.
            float limitSquared = GuidInfo.IsPlayer(giver)
                ? InventoryUiLaw.QuestShareDistance * InventoryUiLaw.QuestShareDistance
                : NpcSessionUiLaw.ServiceRangeSquared;
            if (distanceSquared > limitSquared)
                reason = $"rangeSquared={distanceSquared:R};limitSquared={limitSquared:R}";
        }
        if (reason.Length == 0) return false;
        EmitInterface("quest", "lifecycle-close", "CLOSED", giver, reason);
        CloseQuestNpcFrame(playSound: true);
        return true;
    }

    private bool QuestDisplacingPanelOpen() => _settingsOpen || _worldMapOpen || _characterOpen ||
        _inspectOpen || _spellbookOpen || _questLogOpen || _socialOpen || _helpOpen ||
        _keybindingsOpen || _macroOpen || _guildOpen || _auctionOpen || _mailOpen ||
        _professionOpen || _talentOpen || _tradeOpen || _bankOpen || _trainer is not null ||
        _taxiOpen || _vendor is not null || _gossipMenu is not null || _deathRezOpen ||
        _tabardOpen || _loot.IsOpen || _itemTextRead is not null;

    private string ExpandQuestText(string text)
    {
        QuestTextMacroLaw.Subject? subject = null;
        if (_net is not null)
        {
            // [SUI] P4b: while driving a party bot, personalise the quest text with the
            // BOT's name/race/class/gender — it is the one the giver is addressing — not
            // the logged-in character. ControlledGuid IS your own guid when unpossessed.
            ulong subjectGuid = ControlledGuid;
            string subjectName = subjectGuid == _net.PlayerGuid
                ? _net.PlayerName : ResolveUnitName(subjectGuid);
            if (_entities.TryGet(subjectGuid, out WorldEntity player))
            {
                var bytes = player.Fields.Bytes0;
                subject = new QuestTextMacroLaw.Subject(subjectName, RaceName(bytes.Race),
                    ClassName(bytes.Class), bytes.Gender);
            }
            else if (_playerTraits.TryGetValue(subjectGuid, out PlayerTraits traits))
            {
                subject = new QuestTextMacroLaw.Subject(subjectName,
                    RaceName(traits.Race), ClassName(traits.Class), traits.Gender);
            }
        }
        return QuestTextMacroLaw.Expand(text, subject, _questWorldStates);
    }

    private void ApplyInitialWorldStates(byte[] body)
    {
        var r = new PacketReader(body);
        uint mapId = r.ReadU32();
        uint zoneId = r.ReadU32();
        ResetRtsTerritoryCaptureContext(mapId, zoneId);
        ushort count = r.ReadU16();
        if (r.Remaining != count * 8)
            throw new InvalidDataException($"world-state init count {count} has {r.Remaining} bytes");
        var received = new (uint Id, uint Value)[count];
        for (int i = 0; i < count; i++) received[i] = (r.ReadU32(), r.ReadU32());
        QuestWorldStateLaw.ApplyInit(_questWorldStates, received);
        foreach ((uint id, uint value) in received)
            ApplyRtsTerritoryWorldState(id, value);
    }

    private void ApplyWorldState(byte[] body)
    {
        if (body.Length != 8) throw new InvalidDataException($"world-state update expected 8 bytes, got {body.Length}");
        var r = new PacketReader(body);
        uint id = r.ReadU32();
        uint value = r.ReadU32();
        _questWorldStates[id] = value;
        ApplyRtsTerritoryWorldState(id, value);
    }

    private void ApplyQuestError(Op opcode, byte[] body)
    {
        switch (opcode)
        {
            case Op.SMSG_QUESTGIVER_QUEST_INVALID:
            {
                uint reason = QuestPackets.ParseInvalidReason(body);
                ulong giver = QuestGiverGuid();
                AddChatMessage(QuestGlobalString(QuestFrameUiLaw.InvalidReasonKey(reason)));
                CloseQuestNpcFrame(playSound: true);
                EmitInterface("quest", "error", "DISPLAYED_CHAT", giver,
                    $"opcode=0x{(ushort)opcode:X4};reason={reason}");
                break;
            }
            case Op.SMSG_QUESTGIVER_QUEST_FAILED:
            {
                QuestGiverFailure failed = QuestPackets.ParseGiverFailure(body);
                ulong giver = QuestGiverGuid();
                string title = QuestTitleForFailure(failed.QuestId);
                AddChatMessage(QuestGlobalString(QuestFrameUiLaw.GiverFailureKey(failed.Reason), title));
                if (failed.Reason is 4 or 50) ShowUiError(QuestGlobalString("ERR_INV_FULL"));
                CloseQuestNpcFrame(playSound: true);
                EmitInterface("quest", "error", "DISPLAYED_CHAT", giver,
                    $"opcode=0x{(ushort)opcode:X4};quest={failed.QuestId};reason={failed.Reason}");
                break;
            }
            case Op.SMSG_QUESTLOG_FULL:
                if (body.Length != 0) throw new InvalidDataException($"quest-log-full has {body.Length} bytes");
                ShowUiError(QuestGlobalString("ERR_QUEST_LOG_FULL"));
                EmitInterface("quest", "error", "DISPLAYED_ERROR", QuestGiverGuid(),
                    $"opcode=0x{(ushort)opcode:X4}");
                break;
            case Op.SMSG_QUESTUPDATE_FAILED:
            case Op.SMSG_QUESTUPDATE_FAILEDTIMER:
            {
                uint quest = QuestPackets.ParseQuestId(body);
                string title = QuestTitleForFailure(quest);
                AddChatMessage(title.Length == 0 ? "Quest failed."
                    : QuestGlobalString("ERR_QUEST_FAILED_S", title));
                EmitInterface("quest", "error", "DISPLAYED_CHAT", 0,
                    $"opcode=0x{(ushort)opcode:X4};quest={quest}");
                break;
            }
        }
    }

    private string QuestTitleForFailure(uint questId)
    {
        if (_questDetails?.QuestId == questId) return _questDetails.Title;
        if (_questRequestItems?.QuestId == questId) return _questRequestItems.Title;
        if (_questOffer?.QuestId == questId) return _questOffer.Title;
        return _questTitles.GetValueOrDefault(questId, "");
    }

    private readonly Dictionary<string, string> _questGlobalStrings = [];
    private bool _questGlobalStringsLoaded;

    private string QuestGlobalString(string key, string fill = "")
    {
        if (!_questGlobalStringsLoaded)
        {
            _questGlobalStringsLoaded = true;
            byte[]? bytes = _mpq?.ReadFile(@"Interface\FrameXML\GlobalStrings.lua");
            if (bytes is not null)
            {
                string source = System.Text.Encoding.UTF8.GetString(bytes);
                foreach (string wanted in QuestStringFallbacks.Keys)
                    if (TryReadLuaString(source, wanted, out string value))
                        _questGlobalStrings[wanted] = value;
            }
        }
        string format = _questGlobalStrings.GetValueOrDefault(key,
            QuestStringFallbacks.GetValueOrDefault(key, key));
        return format.Replace("%s", fill, StringComparison.Ordinal);
    }

    private static bool TryReadLuaString(string source, string key, out string value)
    {
        value = "";
        int at = source.IndexOf(key, StringComparison.Ordinal);
        while (at >= 0)
        {
            bool boundary = (at == 0 || !char.IsLetterOrDigit(source[at - 1]) && source[at - 1] != '_') &&
                (at + key.Length == source.Length ||
                 !char.IsLetterOrDigit(source[at + key.Length]) && source[at + key.Length] != '_');
            int equals = at + key.Length;
            while (boundary && equals < source.Length && source[equals] is ' ' or '\t') equals++;
            if (!boundary || equals >= source.Length || source[equals] != '=') equals = -1;
            int quote = equals + 1;
            while (equals >= 0 && quote < source.Length && source[quote] is ' ' or '\t') quote++;
            if (equals < 0 || quote >= source.Length || source[quote] != '"') quote = -1;
            if (quote >= 0)
            {
                var text = new System.Text.StringBuilder();
                bool escaped = false;
                for (int i = quote + 1; i < source.Length; i++)
                {
                    char c = source[i];
                    if (escaped)
                    {
                        text.Append(c switch { 'n' => '\n', 'r' => '\r', 't' => '\t', _ => c });
                        escaped = false;
                    }
                    else if (c == '\\') escaped = true;
                    else if (c == '"') { value = text.ToString(); return true; }
                    else text.Append(c);
                }
            }
            at = source.IndexOf(key, at + key.Length, StringComparison.Ordinal);
        }
        return false;
    }

    private static readonly IReadOnlyDictionary<string, string> QuestStringFallbacks =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ERR_QUEST_ALREADY_ON"] = "You are already on that quest",
            ["ERR_QUEST_FAILED_LOW_LEVEL"] = "You are not high enough level for that quest.",
            ["ERR_QUEST_FAILED_WRONG_RACE"] = "That quest is not available to your race.",
            ["ERR_QUEST_ONLY_ONE_TIMED"] = "You can only be on one timed quest at a time",
            ["ERR_QUEST_FAILED_MISSING_ITEMS"] = "You don't have the required items with you. Check storage.",
            ["ERR_QUEST_FAILED_NOT_ENOUGH_MONEY"] = "You don't have enough money for that quest",
            ["ERR_QUEST_NEED_PREREQS"] = "You don't meet the requirements for that quest",
            ["ERR_QUEST_FAILED_BAG_FULL_S"] = "%s failed: Inventory is full.",
            ["ERR_QUEST_FAILED_MAX_COUNT_S"] = "%s failed: Duplicate item found.",
            ["ERR_QUEST_FAILED_S"] = "%s failed.",
            ["ERR_QUEST_LOG_FULL"] = "Your quest log is full.",
            ["ERR_INV_FULL"] = "Inventory is full.",
            ["RETRIEVING_ITEM_INFO"] = "Retrieving item information",
        };

    private void SnapshotQuestEconomy()
    {
        if (_net is not null && _entities.TryGet(_net.PlayerGuid, out WorldEntity player))
        { _questXpBefore = player.Fields.Experience; _questMoneyBefore = player.Fields.Coinage; }
    }

    private void ObserveQuestLog()
    {
        if (_net is null || !_entities.TryGet(_net.PlayerGuid, out WorldEntity player)) return;
        HashSet<uint> now = player.Fields.QuestLog().Select(q => q.QuestId).ToHashSet();
        HashSet<uint> watchableNow = [.. now];
        foreach (PartyMember member in _partyMembers)
            foreach (MemberQuestEntry entry in MemberQuestEntries(member.Guid))
                if (!entry.Rewarded) watchableNow.Add(entry.QuestId);
        foreach (uint id in _questAutoWatchPending.Where(watchableNow.Contains).ToArray())
            AutoWatchQuest(id);
        bool playAddedSound = QuestFrameUiLaw.ShouldPlayQuestAddedSound(
            _questLogSnapshotKnown, _questLogSnapshot, now);
        _questWatches.RemoveAll(id => !watchableNow.Contains(id));
        _questWatchCollapsed.RemoveWhere(id => !watchableNow.Contains(id));
        foreach (uint id in _questAutoWatchExpiries.Keys.Where(id => !watchableNow.Contains(id)).ToArray())
            _questAutoWatchExpiries.Remove(id);
        foreach (uint id in now)
            if (!_questTemplates.ContainsKey(id) && _questQueries.Add(id)) _net.QuestQuery(id);
        foreach (uint id in now.Except(_questLogSnapshot)) EmitInterface("quest", "log", "ADDED", _net.PlayerGuid, $"quest={id};count={now.Count}");
        foreach (uint id in _questLogSnapshot.Except(now)) EmitInterface("quest", "log", "REMOVED", _net.PlayerGuid, $"quest={id};count={now.Count}");
        // QUESTADDED -> iQuestActivate.wav is a C++ quest-log transition cue, not
        // an Accept-button sound. Waiting for the field edge keeps refusals silent.
        if (playAddedSound) PlayUiSound(QuestFrameUiLaw.QuestAddedSound);
        // Your own accept/turn-in (from the vanilla L window or anywhere) moves this set —
        // re-pull the commander giver board so it does not sit on a stale verdict.
        if (!now.SetEquals(_questLogSnapshot)) RefreshGiverQuestsIfOpen();
        _questLogSnapshot = now;
        _questLogSnapshotKnown = true;
        if (_questRewardPending != 0)
        {
            uint xpDelta = player.Fields.Experience >= _questXpBefore ? player.Fields.Experience - _questXpBefore : 0;
            long moneyDelta = (long)player.Fields.Coinage - _questMoneyBefore;
            bool matched = xpDelta == _questExpectedXp && moneyDelta == _questExpectedMoney;
            if (matched || xpDelta != 0 || moneyDelta != 0)
            {
                EmitInterface("quest", "economy", matched ? "VERIFIED" : "MISMATCH", _net.PlayerGuid,
                    $"quest={_questRewardPending};xp={_questXpBefore}->{player.Fields.Experience};expectedXp={_questExpectedXp};money={_questMoneyBefore}->{player.Fields.Coinage};expectedMoney={_questExpectedMoney}");
                _questRewardPending = 0;
            }
        }
    }

    private void ApplyQuestQuery(byte[] body)
    {
        QuestTemplate template = QuestPackets.ParseQueryResponse(body);
        if (template.QuestId != 0)
        {
            _questTemplates[template.QuestId] = template;
            if (template.Title.Length > 0) _questTitles[template.QuestId] = template.Title;
            if (_items is not null && _net is not null)
                foreach (uint itemId in template.Objectives
                    .Where(o => o.ItemId != 0 && o.ItemCount > 0)
                    .Select(o => o.ItemId).Distinct())
                    _items.Require(itemId, 0, _net);
            RequireQuestCreatureNames(template);
            if (_questAutoWatchPending.Remove(template.QuestId))
                AutoWatchQuest(template.QuestId);
        }
        _questQueries.Remove(template.QuestId);
        EmitInterface("quest", "query", "DECODED", 0,
            $"quest={template.QuestId};level={template.Level};title={SanitizeEvidence(template.Title)};objectives={template.Objectives.Count}");
    }

    /// <summary>
    /// Empty objective text makes the client compose "Foo slain" from the creature
    /// template. Resolve that static name with the quest rather than waiting for a
    /// matching creature to stream into view. SuperUI-Core ignores this query's GUID.
    /// </summary>
    private void RequireQuestCreatureNames(QuestTemplate template)
    {
        if (_net is null) return;
        foreach (uint entry in template.Objectives
            .Where(o => o.CreatureOrGo != 0 &&
                (o.CreatureOrGo & 0x8000_0000) == 0 && o.Text.Length == 0)
            .Select(o => o.CreatureOrGo & 0x7fff_ffff).Distinct())
            if (TryBeginCreatureQuery(entry))
                _net.CreatureQuery(entry, 0);
    }

    private void SimulateQuestFlow()
    {
        ulong guid = _selectionGuid == 0 ? 0xF130000036000001ul : _selectionGuid;
        var d = new PacketWriter(); d.WriteU64(guid); d.WriteU32(7); d.WriteCString("Kobold Camp Cleanup");
        d.WriteCString("The valley must be made safe."); d.WriteCString("Defeat 10 kobold vermin."); d.WriteU32(0);
        d.WriteU32(1); d.WriteU32(117); d.WriteU32(5); d.WriteU32(1); d.WriteU32(0); d.WriteI32(50); d.WriteU32(0); d.WriteU32(0);
        ApplyQuestDetails(d.ToArray());
        var k = new PacketWriter(); k.WriteU32(7); k.WriteU32(6); k.WriteU32(4); k.WriteU32(10); k.WriteU64(0xF130000006000001ul); ApplyQuestKill(k.ToArray());
        var o = new PacketWriter(); o.WriteU64(guid); o.WriteU32(7); o.WriteCString("Kobold Camp Cleanup"); o.WriteCString("Well done.");
        o.WriteU32(0); o.WriteU32(0); o.WriteU32(1); o.WriteU32(117); o.WriteU32(5); o.WriteU32(1); o.WriteU32(0); o.WriteI32(50); o.WriteU32(0); o.WriteU32(0);
        ApplyQuestOffer(o.ToArray());
        var c = new PacketWriter(); c.WriteU32(7); c.WriteU32(3); c.WriteU32(100); c.WriteI32(50); c.WriteU32(0); ApplyQuestComplete(c.ToArray());
    }

    private bool StageQuestFrameProof(string panel)
    {
        ulong guid = _selectionGuid != 0 ? _selectionGuid : _net?.PlayerGuid ?? 0;
        var choice = new[] { new QuestRewardItem(117, 5, 1), new QuestRewardItem(159, 1, 2) };
        var fixedItems = new[] { new QuestRewardItem(6948, 1, 3) };
        _questList = null; _questDetails = null; _questRequestItems = null; _questOffer = null;
        _questNpcScroll = 0; _questRewardChoice = -1;
        _questNpcContentHeight = QuestFrameUiLaw.ScrollHeight;
        switch (panel.ToLowerInvariant())
        {
            case "greeting":
                uint active = _net is not null && _entities.TryGet(_net.PlayerGuid, out WorldEntity player)
                    ? player.Fields.QuestLog().Select(q => q.QuestId).FirstOrDefault() : 0;
                var quests = new List<GossipQuest>();
                if (active != 0) quests.Add(new(active, 4, 10,
                    _questTitles.GetValueOrDefault(active, "A Current Quest")));
                quests.Add(new(900001, 2, 12, "A New Threat"));
                quests.Add(new(900002, 2, 14, "Supplies for the Journey"));
                _questList = new(guid,
                    "Greetings, traveler. There is much work to be done if this valley is to remain safe.",
                    0, 0, quests);
                break;
            case "detail":
                _questDetails = new(guid, 900001, "A New Threat",
                    "Enemies have gathered beyond the road. Thin their ranks and return when the path is safe.",
                    "Defeat 10 enemies beyond the road.", false, choice, fixedItems, 12345, 0);
                break;
            case "progress":
                _questRequestItems = new(guid, 900001, "A New Threat",
                    "Have you gathered everything we need?", 12345, choice, true);
                break;
            case "reward":
                _questOffer = new(guid, 900001, "A New Threat",
                    "You have done well. Choose your reward.", false, choice, fixedItems, 12345, 0);
                break;
            default: return false;
        }
        return true;
    }

    private void DrawQuestFrame()
    {
        UpdateQuestNpcLifecycle();
        if (!_questLogOpen && _questList is null && _questDetails is null && _questOffer is null && _questRequestItems is null) return;
        bool logMode = _questLogOpen;
        float s=GameplayUiScale();
        Vector2 origin = UiPanelFrameOrigin(UiPanelOwnershipRegistry[logMode ? 8 : 7], s);
        Vector2 logicalSize = QuestFrameUiLaw.FrameRect.Size;
        ImGui.SetNextWindowPos(origin,ImGuiCond.Always); ImGui.SetNextWindowSize(QuestFrameUiLaw.WindowSize(s),ImGuiCond.Always); ImGui.SetNextWindowBgAlpha(0);
        if (!ImGui.Begin("##quest", ImGuiWindowFlags.NoDecoration|ImGuiWindowFlags.NoMove|ImGuiWindowFlags.NoSavedSettings|ImGuiWindowFlags.NoBackground|ImGuiWindowFlags.NoNav)) { ImGui.End(); return; }
        ImDrawListPtr dl=ImGui.GetWindowDrawList();
        bool parityProof = _uiParityArmed && _uiParityPanel is "quest-log" or "quest-frame";
        QuestNpcPanel npcPanel = QuestNpcPanelNow();
        string frameName = logMode ? "QuestLogFrame" : "BenillaQuestFrame";
        string npcPanelName = QuestPanelStem(npcPanel) + "Panel";
        if(parityProof)
        {
            BeginUiParityFrame(origin,s);
            CollectUiParityDraw(frameName,"Frame",origin,logicalSize*s,"",
                new("",0,"IMGUI_HOST","ANCHOR:ABSOLUTE","","",0,8,
                    ContentRect:new(origin.X,origin.Y,origin.X+logicalSize.X*s,
                        origin.Y+logicalSize.Y*s),
                    ClipRect:new(origin.X,origin.Y,origin.X+logicalSize.X*s,
                        origin.Y+logicalSize.Y*s),ClipMask:frameName,Strata:"DIALOG"));
            if (!logMode)
                CollectUiParityDraw(npcPanelName,"Frame",origin,logicalSize*s,frameName,
                    new("",0,"FRAMES","TOPLEFT",frameName,"TOPLEFT",0,0,
                        ContentRect:new(origin.X,origin.Y,origin.X+logicalSize.X*s,
                            origin.Y+logicalSize.Y*s),
                        ClipRect:new(origin.X,origin.Y,origin.X+logicalSize.X*s,
                            origin.Y+logicalSize.Y*s),ClipMask:frameName,Strata:"DIALOG"));
        }
        if (!logMode && _entities.TryGet(QuestGiverGuid(), out WorldEntity giver))
        {
            Vector2 portraitMin = QuestFrameUiLaw.NpcPortraitRect.ScaledMin(origin, s);
            DrawUnitPortraitImage(dl, giver, portraitMin,
                QuestFrameUiLaw.NpcPortraitRect.Width * s, 0, false);
            if (parityProof) CollectUiParityDraw("BenillaQuestFramePortrait", "Texture", portraitMin,
                QuestFrameUiLaw.NpcPortraitRect.ScaledSize(s), frameName,
                new("", 0xffffffff, "ARTWORK", "TOPLEFT", frameName, "TOPLEFT", 7, -6,
                    ContentRect: new(portraitMin.X, portraitMin.Y,
                        portraitMin.X + 60 * s, portraitMin.Y + 60 * s),
                    ClipRect: new(origin.X, origin.Y, origin.X + 384 * s, origin.Y + 512 * s),
                    ClipMask: "BenillaQuestFramePortraitAperture", BlendMode: "BLEND",
                    Strata: "DIALOG"));
        }
        else if (parityProof && !logMode)
            ClassifyUiParity("BenillaQuestFramePortrait", "Texture", frameName,
                "NOT-DRAWN", GuidInfo.IsItem(QuestGiverGuid())
                    ? "item-giver-has-no-unit-portrait" : "quest-giver-entity-is-unavailable");
        IReadOnlyList<QuestFrameArtSeat> art = QuestFrameUiLaw.PanelArt(
            logMode, npcPanel == QuestNpcPanel.Greeting);
        for (int index = 0; index < art.Count; index++)
        {
            QuestFrameArtSeat seat = art[index];
            string element = logMode
                ? index == 0 ? "QuestLogFrame/Texture" : $"QuestLogFrame/Texture#{index + 1}"
                : index == 0 ? npcPanelName + "/Texture" : $"{npcPanelName}/Texture#{index + 1}";
            Vector2 minimum = seat.Rect.ScaledMin(origin, s);
            DrawArt(dl, seat.Path, minimum, seat.Rect.Size, s);
            if (parityProof)
                CollectUiParityDraw(element, "Texture", minimum, seat.Rect.ScaledSize(s),
                    logMode ? frameName : npcPanelName,
                    new(seat.Path, 0xffffffff,
                        seat.Path.EndsWith("UI-Quest-BotLeftPatch",
                            StringComparison.OrdinalIgnoreCase) ? "ARTWORK" : "BACKGROUND",
                        "TOPLEFT", logMode ? frameName : npcPanelName, "TOPLEFT",
                        seat.Rect.X, -seat.Rect.Y,
                        ContentRect:new(minimum.X, minimum.Y,
                            minimum.X + seat.Rect.Width * s, minimum.Y + seat.Rect.Height * s),
                        ClipRect:new(origin.X, origin.Y, origin.X + logicalSize.X * s,
                            origin.Y + logicalSize.Y * s),
                        ClipMask:logMode ? frameName : npcPanelName,
                        BlendMode:"BLEND", Strata:"DIALOG"));
        }
        if (!logMode)
        {
            ulong guid = QuestGiverGuid();
            string name = GuidInfo.IsItem(guid) ? "" : _entities.TryGet(guid, out WorldEntity namedGiver)
                ? _creatureNames.GetValueOrDefault(namedGiver.Entry, "") : "";
            Vector2 nameCenter = origin + QuestFrameUiLaw.NpcNameCenter * s;
            GameText.DrawCentered(dl, QuestFrameUiLaw.NpcNameFont, name, nameCenter, s);
            if (parityProof)
            {
                Vector2 nameFrameMin = QuestFrameUiLaw.NpcNameFrameRect.ScaledMin(origin, s);
                CollectUiParityDraw("BenillaQuestNpcNameFrame", "Frame", nameFrameMin,
                    QuestFrameUiLaw.NpcNameFrameRect.ScaledSize(s), frameName,
                    new("", 0, "FRAMES", "TOP", frameName, "TOP", 0, -23,
                        ClipRect:new(origin.X,origin.Y,origin.X+384*s,origin.Y+512*s),
                        ClipMask:frameName, Strata:"DIALOG"));
                CollectUiParityDraw("BenillaQuestNpcNameText", "FontString",
                    QuestFrameUiLaw.NpcNameTextRect.ScaledMin(origin, s),
                    QuestFrameUiLaw.NpcNameTextRect.ScaledSize(s),
                    "BenillaQuestNpcNameFrame", new("", FontObjectLaw.Get(
                            QuestFrameUiLaw.NpcNameFont).Color,
                        "BACKGROUND", "CENTER", "BenillaQuestNpcNameFrame", "CENTER", 0, 0,
                        FontObjectLaw.Get(QuestFrameUiLaw.NpcNameFont).Face,
                        FontObjectLaw.Get(QuestFrameUiLaw.NpcNameFont).Height,
                        ClipRect:new(origin.X,origin.Y,origin.X+384*s,origin.Y+512*s),
                        ClipMask:"BenillaQuestNpcNameFrame", Strata:"DIALOG"));
            }
        }
        ulong questLogSubject = !_freeView && ControlledGuid != 0
            ? ControlledGuid : _net?.PlayerGuid ?? 0;
        if (_questLogOpen && _entities.TryGet(questLogSubject, out WorldEntity player))
            DrawQuestLogContent(dl, origin, s, player);
        else
            DrawQuestNpcContent(dl, origin, s);
        QuestLogicalRect closeRect = QuestFrameUiLaw.CloseRect(logMode);
        Vector2 close = closeRect.ScaledMin(origin, s);
        DrawImageButton(dl,"##quest-close",close, closeRect.ScaledSize(s),
            @"Interface\Buttons\UI-Panel-MinimizeButton-Up",@"Interface\Buttons\UI-Panel-MinimizeButton-Down",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Highlight");
        bool closeHovered = ImGui.IsItemHovered();
        bool closeActive = ImGui.IsItemActive();
        bool closeClicked = ImGui.IsItemClicked(ImGuiMouseButton.Left);
        if (parityProof)
        {
            string closeElement = logMode ? "QuestLogFrameCloseButton" : "BenillaQuestCloseButton";
            Vector2 closeSize = closeRect.ScaledSize(s);
            Vector4 clip = new(origin.X,origin.Y,origin.X+logicalSize.X*s,origin.Y+logicalSize.Y*s);
            CollectUiParityDraw(closeElement, "Button", close, closeSize, frameName,
                new("", 0, "FRAMES", "CENTER", frameName,
                    logMode ? "TOPLEFT" : "TOPRIGHT", logMode ? 322 : -42,
                    logMode ? -8 : -31, ContentRect:new(close.X,close.Y,
                        close.X+closeSize.X,close.Y+closeSize.Y), ClipRect:clip,
                    ClipMask:frameName, Visible:true, Enabled:true,
                    InteractionState:closeActive?"pressed":closeHovered?"hovered":"normal",
                    HitMin:close, HitMax:close+closeSize, Strata:"DIALOG"));
            string closeStateType = closeActive ? "PushedTexture" : "NormalTexture";
            string closeStatePath = closeActive
                ? @"Interface\Buttons\UI-Panel-MinimizeButton-Down"
                : @"Interface\Buttons\UI-Panel-MinimizeButton-Up";
            CollectUiParityDraw(closeElement + "/" + closeStateType, closeStateType, close,
                closeSize, closeElement, new(closeStatePath, 0xffffffff, "ARTWORK", "TOPLEFT",
                    closeElement, "TOPLEFT", 0, 0, TexCoords:"0|0|1|1",
                    ContentRect:new(close.X,close.Y,close.X+closeSize.X,close.Y+closeSize.Y),
                    ClipRect:clip,ClipMask:closeElement,BlendMode:"BLEND",Strata:"DIALOG"));
            ClassifyUiParity(closeElement + "/" + (closeActive ? "NormalTexture" : "PushedTexture"),
                closeActive ? "NormalTexture" : "PushedTexture", closeElement, "NOT-DRAWN",
                closeActive ? "close-button-is-pressed" : "close-button-is-not-pressed");
            if (closeHovered)
                CollectUiParityDraw(closeElement + "/HighlightTexture", "HighlightTexture", close,
                    closeSize, closeElement,
                    new(@"Interface\Buttons\UI-Panel-MinimizeButton-Highlight",0xffffffff,
                        "HIGHLIGHT","TOPLEFT",closeElement,"TOPLEFT",0,0,
                        TexCoords:"0|0|1|1",ContentRect:new(close.X,close.Y,
                            close.X+closeSize.X,close.Y+closeSize.Y),ClipRect:clip,
                        ClipMask:closeElement,BlendMode:"ADD",Strata:"DIALOG"));
            else
                ClassifyUiParity(closeElement + "/HighlightTexture", "HighlightTexture",
                    closeElement, "NOT-DRAWN", "close-button-is-not-hovered");
        }
        if(closeClicked)
        {
            if (_questLogOpen) _questLogOpen=false; else CloseQuestNpcFrame(playSound: true);
        }
        if(parityProof)MarkUiParityFrameComplete();
        ImGui.End();
    }

    private void DrawQuestLogContent(ImDrawListPtr dl, Vector2 origin, float s, WorldEntity player)
    {
        // Embodied play follows the directly controlled character.
        var quests = DisplayedQuestLog();
        EnsureQuestHeaderCatalogs();
        string[] headers = quests.Select(q => QuestHeaderName(
            _questTemplates.GetValueOrDefault(q.QuestId)?.ZoneOrSort ?? 0)).ToArray();
        var rows = new List<QuestLogDisplayRow>(quests.Length + headers.Distinct().Count());
        IReadOnlyList<QuestLogHeaderGroup> groups =
            QuestFrameUiLaw.GroupQuestLogHeaders(headers);
        foreach (QuestLogHeaderGroup group in groups)
        {
            bool collapsed = _questLogCollapsed.Contains(group.Header);
            rows.Add(new(true, group.Header, collapsed, 0, 0, 0, 0));
            if (collapsed) continue;
            foreach (int questIndex in group.QuestIndexes)
            {
                var q = quests[questIndex];
                rows.Add(new(false, group.Header, false, q.Slot, q.QuestId, q.Counters, q.Timer));
            }
        }
        if (_questLogSelectedQuestId == 0 || !rows.Any(r => !r.IsHeader && r.QuestId == _questLogSelectedQuestId))
            _questLogSelectedQuestId = rows.FirstOrDefault(r => !r.IsHeader).QuestId;
        _questLogOffset = QuestFrameUiLaw.ClampQuestLogOffset(_questLogOffset, rows.Count);
        Vector2 listMin = QuestFrameUiLaw.QuestLogListRect.ScaledMin(origin, s);
        Vector2 listMax = listMin + QuestFrameUiLaw.QuestLogListRect.ScaledSize(s);
        if (ImGui.IsMouseHoveringRect(listMin, listMax, false) && ImGui.GetIO().MouseWheel != 0f)
                _questLogOffset = QuestFrameUiLaw.ClampQuestLogOffset(
                _questLogOffset - Math.Sign(ImGui.GetIO().MouseWheel), rows.Count);
        GameText.DrawCentered(dl, "GameFontHighlight", "Quest Log",
            origin + QuestFrameUiLaw.QuestLogTitleCenter * s, s);
        DrawQuestLogBookControls(dl, origin, s, quests.Length, groups);
        for (int row = 0; row < QuestFrameUiLaw.QuestLogRows; row++)
        {
            int index = _questLogOffset + row;
            if (index >= rows.Count) break;
            QuestLogDisplayRow display = rows[index];
            QuestLogicalRect rowRect = QuestFrameUiLaw.QuestLogRowRect(row);
            Vector2 min = rowRect.ScaledMin(origin, s);
            if (display.IsHeader)
            {
                if (VanillaListRow(dl, $"##quest-log-header-{display.Header}", min,
                        rowRect.Size, s, "    " + display.Header, false, 0xffb3b3b3,
                        fontObject: "GameFontNormal"))
                {
                    if (display.Collapsed) _questLogCollapsed.Remove(display.Header);
                    else _questLogCollapsed.Add(display.Header);
                }
                string foldArt = display.Collapsed
                    ? @"Interface\Buttons\UI-PlusButton-Up"
                    : @"Interface\Buttons\UI-MinusButton-Up";
                uint fold = _gameplayArt?.Handle(foldArt) ?? 0;
                if (fold != 0)
                {
                    QuestLogicalRect foldRect = QuestFrameUiLaw.QuestLogFoldIconRect(row);
                    Vector2 foldMin = foldRect.ScaledMin(origin, s);
                    dl.AddImage((nint)fold, foldMin, foldMin + foldRect.ScaledSize(s));
                }
                continue;
            }
            var quest = (display.Slot, display.QuestId, display.Counters, display.Timer);
            string bareTitle = _questTitles.GetValueOrDefault(quest.QuestId, $"Quest {quest.QuestId}");
            ulong displayedSubject = !_freeView && ControlledGuid != 0
                ? ControlledGuid : LocalPlayerGuid;
            string title = $"{bareTitle} [{PartyQuestHolderCount(quest.QuestId, displayedSubject)}]";
            byte state = (byte)(quest.Counters >> 24);
            if ((state & 1) != 0) title += " (Complete)";
            else if ((state & 2) != 0) title += " (Failed)";
            uint level = _questTemplates.GetValueOrDefault(quest.QuestId)?.Level ?? player.Level;
            uint color = ImGui.ColorConvertFloat4ToU32(
                QuestFrameUiLaw.QuestDifficultyColor(player.Level, level));
            if (VanillaListRow(dl, $"##quest-log-{quest.QuestId}", min, rowRect.Size, s,
                    "  " + title, _questLogSelectedQuestId == quest.QuestId, color,
                    fontObject: "GameFontNormal"))
            {
                if (_questLogSelectedQuestId != quest.QuestId) _questLogDetailScroll = 0;
                _questLogSelectedQuestId = quest.QuestId;
                if (ShiftHeld()) HandleQuestLogShiftClick(quest.QuestId, bareTitle);
            }
            if (_questWatches.Contains(quest.QuestId))
            {
                uint check = _gameplayArt?.Handle(@"Interface\Buttons\UI-CheckBox-Check") ?? 0;
                if (check != 0)
                {
                    float ink = GameText.MeasureWidth("GameFontNormal", "  " + title, s) / s;
                    QuestLogicalRect checkRect = QuestFrameUiLaw.QuestLogWatchCheckRect(row, ink);
                    Vector2 checkMin = checkRect.ScaledMin(origin, s);
                    dl.AddImage((nint)check, checkMin, checkMin + checkRect.ScaledSize(s));
                }
            }
        }
        var selectedSlot = quests.FirstOrDefault(q => q.QuestId == _questLogSelectedQuestId);
        if (selectedSlot.QuestId != 0)
        {
            uint selected = selectedSlot.QuestId;
            string title = _questTitles.GetValueOrDefault(selected, $"Quest {selected}");
            DrawQuestLogDetail(dl, origin, s, player, selectedSlot, title);
            DrawQuestLogDetailScrollBar(dl, origin, s);
            // The bottom action row's hit-rects land a few pixels below where players actually
            // click (confirmed live: a click landing 4 screen px / ~5 logical units above
            // QuestLogAbandonRect's top edge never registers). Rather than nudge the shared
            // logical constants and risk shifting the drawn artwork, extend just the clickable
            // area upward - extraHitTop never touches where the texture/label render.
            const float bottomRowHitForgiveness = 8f;
            Vector2 abandonMin = QuestRectMin(origin, s, QuestFrameUiLaw.QuestLogAbandonRect);
            bool abandonClicked = VanillaButton(dl, "##quest-abandon", "Abandon Quest",
                abandonMin, QuestFrameUiLaw.QuestLogAbandonRect.Size, s,
                extraHitTop: bottomRowHitForgiveness);
            if (abandonClicked)
                _questAbandonConfirmation = new(
                    !_freeView && ControlledGuid != 0 ? ControlledGuid : LocalPlayerGuid,
                    selected, title);
        }
        else VanillaButton(dl, "##quest-abandon", "Abandon Quest",
            QuestRectMin(origin, s, QuestFrameUiLaw.QuestLogAbandonRect),
            QuestFrameUiLaw.QuestLogAbandonRect.Size, s, false);
        // Share Quest was drawn permanently disabled until PLAN_20 P3. The server
        // does NOT gate the push on QUEST_FLAGS_SHARABLE — it forwards anything
        // and then refuses every accept — so the sharable test has to live here.
        uint shareQuest = _questLogSelectedQuestId;
        bool canShare = shareQuest != 0 && _partyMembers.Count > 0 &&
            _questTemplates.GetValueOrDefault(shareQuest)?.Sharable == true;
        if (VanillaButton(dl, "##quest-push", "Share Quest",
                QuestRectMin(origin, s, QuestFrameUiLaw.QuestLogShareRect),
                QuestFrameUiLaw.QuestLogShareRect.Size, s, canShare, extraHitTop: 8f) && canShare)
            ShareQuestWithParty(shareQuest);
        if (VanillaButton(dl, "##quest-exit", "Exit",
                QuestRectMin(origin, s, QuestFrameUiLaw.QuestLogExitRect),
                QuestFrameUiLaw.QuestLogExitRect.Size, s, extraHitTop: 8f)) _questLogOpen = false;
    }

    private static Vector2 QuestRectMin(Vector2 origin, float scale, QuestLogicalRect rect) =>
        rect.ScaledMin(origin, scale);

    private static Vector2 QuestRectSize(float scale, QuestLogicalRect rect) =>
        rect.ScaledSize(scale);

    private void DrawQuestLogBookControls(ImDrawListPtr dl, Vector2 origin, float scale,
        int questCount, IReadOnlyList<QuestLogHeaderGroup> groups)
    {
        string countLabel = "Quests:";
        string countValue = $"{questCount}/{OwnQuestHeldCap}";
        float labelWidth = GameText.MeasureWidth("GameFontNormalSmall", countLabel, scale) / scale;
        float valueWidth = GameText.MeasureWidth("GameFontNormalSmall", countValue, scale) / scale;
        QuestLogicalRect pill = QuestFrameUiLaw.QuestLogCountPillRect(
            labelWidth + 3f + valueWidth);
        Vector2 pillMin = QuestRectMin(origin, scale, pill);
        DrawVanillaInputBorder(dl, pillMin, pill.Size, scale);
        Vector2 logicalTextMin = QuestFrameUiLaw.QuestLogCountTextMin(
            pill, labelWidth, valueWidth);
        Vector2 textMin = origin + logicalTextMin * scale;
        GameText.Draw(dl, "GameFontNormalSmall", countLabel, textMin, scale);
        GameText.Draw(dl, "GameFontNormalSmall", countValue,
            origin + QuestFrameUiLaw.QuestLogCountValueMin(logicalTextMin, labelWidth) * scale,
            scale, 0xffffffff);

        foreach (QuestFrameArtSeat seat in QuestFrameUiLaw.QuestLogCollapseArt)
            DrawArt(dl, seat.Path, seat.Rect.ScaledMin(origin, scale), seat.Rect.Size, scale);

        bool enabled = groups.Count > 0;
        bool allCollapsed = enabled && groups.All(group =>
            _questLogCollapsed.Contains(group.Header));
        QuestLogicalRect button = QuestFrameUiLaw.QuestLogCollapseButtonRect;
        ImGui.SetCursorScreenPos(QuestRectMin(origin, scale, button));
        if (!enabled) ImGui.BeginDisabled();
        ImGui.InvisibleButton("##quest-collapse-all", QuestRectSize(scale, button));
        bool hovered = enabled && ImGui.IsItemHovered();
        bool clicked = enabled && ImGui.IsItemClicked(ImGuiMouseButton.Left);
        if (!enabled) ImGui.EndDisabled();

        QuestLogicalRect icon = QuestFrameUiLaw.QuestLogCollapseIconRect;
        string iconPath = allCollapsed
            ? @"Interface\Buttons\UI-PlusButton-Up"
            : @"Interface\Buttons\UI-MinusButton-Up";
        uint iconArt = _gameplayArt?.Handle(iconPath) ?? 0;
        Vector2 iconMin = QuestRectMin(origin, scale, icon);
        if (iconArt != 0)
            dl.AddImage((nint)iconArt, iconMin, iconMin + QuestRectSize(scale, icon),
                Vector2.Zero, Vector2.One, enabled ? 0xffffffff : 0xff777777);
        if (hovered)
        {
            uint highlight = _gameplayArt?.AdditiveHandle(
                @"Interface\Buttons\UI-PlusButton-Hilight") ?? 0;
            if (highlight != 0)
                dl.AddImage((nint)highlight, iconMin,
                    iconMin + QuestRectSize(scale, icon));
        }
        QuestLogicalRect title = QuestFrameUiLaw.QuestLogCollapseTitleRect;
        GameText.Draw(dl, "GameFontNormal", "All", QuestRectMin(origin, scale, title),
            scale, enabled ? null : 0xff777777);

        if (clicked)
        {
            foreach (QuestLogHeaderGroup group in groups)
            {
                if (allCollapsed) _questLogCollapsed.Remove(group.Header);
                else _questLogCollapsed.Add(group.Header);
            }
            _questLogOffset = 0;
        }

        QuestLogicalRect track = QuestFrameUiLaw.QuestLogTrackRect;
        Vector2 trackMin = QuestRectMin(origin, scale, track);
        Vector2 trackSize = QuestRectSize(scale, track);
        uint radio = _gameplayArt?.Handle(@"Interface\Buttons\UI-RadioButton") ?? 0;
        if (radio != 0)
        {
            dl.AddImage((nint)radio, trackMin, trackMin + trackSize,
                Vector2.Zero, QuestFrameUiLaw.QuestLogRadioBaseUvMax);
            dl.AddImage((nint)radio, trackMin, trackMin + trackSize,
                QuestFrameUiLaw.QuestLogRadioCheckUvMin,
                QuestFrameUiLaw.QuestLogRadioCheckUvMax,
                _questWatches.Count > 0 ? 0xff00ff00 : 0xff0000ff);
        }
        QuestLogicalRect trackTitle = QuestFrameUiLaw.QuestLogTrackTitleRect;
        GameText.Draw(dl, "GameFontHighlightSmall", "Track Quest",
            QuestRectMin(origin, scale, trackTitle), scale);
        ImGui.SetCursorScreenPos(trackMin);
        ImGui.InvisibleButton("##quest-track-indicator", trackSize);
        if (ImGui.IsItemHovered())
            HoverTip("Shift-click a quest to add or remove a quest from your quest watch list.");
    }

    private void DrawQuestLogDetailScrollBar(ImDrawListPtr dl, Vector2 origin, float scale)
    {
        float maximum = Math.Max(0, _questLogDetailContentHeight -
            QuestFrameUiLaw.QuestLogDetailRect.Height);
        if (maximum <= 0 || _gameplayArt is null) return;

        void Arrow(string id, QuestLogicalRect rect, bool up)
        {
            bool enabled = up ? _questLogDetailScroll > 0 : _questLogDetailScroll < maximum;
            Vector2 min = QuestRectMin(origin, scale, rect);
            Vector2 size = QuestRectSize(scale, rect);
            ImGui.SetCursorScreenPos(min);
            if (!enabled) ImGui.BeginDisabled();
            ImGui.InvisibleButton(id, size);
            bool active = enabled && ImGui.IsItemActive();
            bool hovered = enabled && ImGui.IsItemHovered();
            bool clicked = enabled && ImGui.IsItemClicked(ImGuiMouseButton.Left);
            if (!enabled) ImGui.EndDisabled();
            string stem = up ? "UI-ScrollBar-ScrollUpButton" : "UI-ScrollBar-ScrollDownButton";
            string state = !enabled ? "Disabled" : active ? "Down" : "Up";
            uint art = _gameplayArt.Handle($@"Interface\Buttons\{stem}-{state}");
            if (art != 0)
                dl.AddImage((nint)art, min, min + size,
                    QuestFrameUiLaw.QuestLogScrollButtonUvMin,
                    QuestFrameUiLaw.QuestLogScrollButtonUvMax);
            if (hovered)
            {
                uint highlight = _gameplayArt.AdditiveHandle(
                    $@"Interface\Buttons\{stem}-Highlight");
                if (highlight != 0)
                    dl.AddImage((nint)highlight, min, min + size,
                        QuestFrameUiLaw.QuestLogScrollButtonUvMin,
                        QuestFrameUiLaw.QuestLogScrollButtonUvMax);
            }
            if (clicked)
                _questLogDetailScroll = QuestFrameUiLaw.ClampQuestLogDetailScroll(
                    _questLogDetailScroll + (up ? -QuestFrameUiLaw.ScrollStep :
                        QuestFrameUiLaw.ScrollStep), _questLogDetailContentHeight);
        }

        Arrow("##quest-detail-up", QuestFrameUiLaw.QuestLogDetailScrollUpRect, true);
        Arrow("##quest-detail-down", QuestFrameUiLaw.QuestLogDetailScrollDownRect, false);
        QuestLogicalRect thumb = QuestFrameUiLaw.QuestLogDetailThumbRect(
            _questLogDetailScroll, _questLogDetailContentHeight);
        uint knob = _gameplayArt.Handle(@"Interface\Buttons\UI-ScrollBar-Knob");
        if (knob != 0)
        {
            Vector2 min = QuestRectMin(origin, scale, thumb);
            dl.AddImage((nint)knob, min, min + QuestRectSize(scale, thumb),
                QuestFrameUiLaw.QuestLogScrollButtonUvMin,
                QuestFrameUiLaw.QuestLogScrollButtonUvMax);
        }
    }

    private string QuestKillProgressText(QuestKillUpdate value)
    {
        QuestLogObjective? objective = null;
        if (_questTemplates.TryGetValue(value.QuestId, out QuestTemplate? template))
        {
            foreach (QuestLogObjective candidate in template.Objectives)
            {
                if (candidate.CreatureOrGo != value.Entry &&
                    (candidate.CreatureOrGo & 0x7fff_ffff) != value.Entry)
                {
                    continue;
                }

                objective = candidate;
                break;
            }
        }

        if (objective is not QuestLogObjective row)
            return $"... slain: {value.Current}/{value.Required}";
        if (row.Text.Length > 0)
            return $"{row.Text}: {value.Current}/{value.Required}";
        if ((row.CreatureOrGo & 0x8000_0000) != 0)
            return $"Objective: {value.Current}/{value.Required}";
        string name = _creatureNames.GetValueOrDefault(value.Entry, "...");
        return $"{QuestCreatureKillLabel(name, value.Required)}: {value.Current}/{value.Required}";
    }

    private void DrawQuestLogDetail(ImDrawListPtr dl, Vector2 origin, float s, WorldEntity player,
        (byte Slot, uint QuestId, uint Counters, uint Timer) selectedSlot, string title)
    {
        QuestLogicalRect rect = QuestFrameUiLaw.QuestLogDetailRect;
        QuestScreenRect clip = QuestFrameUiLaw.QuestLogDetailClip(origin, s);
        _questLogDetailScroll = QuestFrameUiLaw.ClampQuestLogDetailScroll(
            _questLogDetailScroll, _questLogDetailContentHeight);
        if (ImGui.IsMouseHoveringRect(clip.Min, clip.Max, false) && ImGui.GetIO().MouseWheel != 0)
            _questLogDetailScroll = QuestFrameUiLaw.ClampQuestLogDetailScroll(
                _questLogDetailScroll - ImGui.GetIO().MouseWheel * QuestFrameUiLaw.ScrollStep,
                _questLogDetailContentHeight);
        Vector2 contentOrigin = QuestFrameUiLaw.QuestLogDetailContentOrigin(
            origin, _questLogDetailScroll, s);
        dl.PushClipRect(clip.Min, clip.Max, true);
        GameText.Draw(dl, "QuestTitleFont", title,
            QuestFrameUiLaw.QuestLogDetailTextMin(
                contentOrigin, QuestFrameUiLaw.QuestLogDetailTitleY, s),
            s, 0xff202020);
        GameText.Draw(dl, "QuestTitleFont", "Objectives",
            QuestFrameUiLaw.QuestLogDetailTextMin(
                contentOrigin, QuestFrameUiLaw.QuestLogDetailObjectivesTitleY, s),
            s, 0xff202020);
        float y = QuestFrameUiLaw.QuestLogDetailInitialY;
        if (_questTemplates.TryGetValue(selectedSlot.QuestId, out QuestTemplate? template))
        {
            y += DrawQuestWrappedText(dl, template.ObjectivesText,
                QuestFrameUiLaw.QuestLogDetailTextMin(contentOrigin, y, s),
                QuestFrameUiLaw.QuestLogDetailTextWidth, "QuestFont", s, 0xff202020) / s;
            if (template.ObjectivesText.Length > 0) y += 8f;
            long? secondsLeft = QuestSecondsLeft(selectedSlot.Timer,
                state: (byte)(selectedSlot.Counters >> 24));
            if (secondsLeft is not null)
            {
                string timer = "Time Remaining: " + QuestFrameUiLaw.SecondsToTime(secondsLeft.Value);
                GameText.Draw(dl, "QuestFont", timer,
                    QuestFrameUiLaw.QuestLogDetailTextMin(contentOrigin, y, s), s,
                    0xff202020);
                y += 22f;
            }
            for (int i = 0; i < template.Objectives.Count; i++)
            {
                foreach ((string text, bool finished) in QuestObjectiveLines(
                    player, selectedSlot.Counters, i, template.Objectives[i]))
                {
                    y += DrawQuestWrappedText(dl, finished ? text + " (Complete)" : text,
                        QuestFrameUiLaw.QuestLogDetailTextMin(contentOrigin, y, s),
                        QuestFrameUiLaw.QuestLogDetailTextWidth, "QuestFont", s,
                        finished ? 0xff333333 : 0xff000000) / s + 2f;
                }
            }
            y += 8f;
            GameText.Draw(dl, "QuestTitleFont", "Description",
                QuestFrameUiLaw.QuestLogDetailTextMin(contentOrigin, y, s), s, 0xff202020);
            y += 20f;
            y += DrawQuestWrappedText(dl, ExpandQuestText(template.Details),
                QuestFrameUiLaw.QuestLogDetailTextMin(contentOrigin, y, s),
                QuestFrameUiLaw.QuestLogDetailTextWidth, "QuestFont", s, 0xff202020) / s;
            y += 10f;
            bool hasRewards = template.ChoiceRewards.Count > 0 || template.FixedRewards.Count > 0 ||
                template.Money != 0 || template.RewardSpell != 0;
            if (hasRewards)
            {
                GameText.Draw(dl, "QuestTitleFont", "Rewards",
                    QuestFrameUiLaw.QuestLogDetailTextMin(contentOrigin, y, s), s, 0xff202020);
                y += 23f;
                if (template.ChoiceRewards.Count > 0)
                {
                    GameText.Draw(dl, "QuestFont", "You may choose one of these rewards:",
                        QuestFrameUiLaw.QuestLogDetailTextMin(contentOrigin, y, s), s, 0xff202020);
                    y += 20f;
                    y = DrawQuestItemGrid(dl,
                        QuestFrameUiLaw.QuestLogDetailGridOrigin(contentOrigin, s), s, y,
                        template.ChoiceRewards, false, "log-choice", clip);
                    y += 5f;
                }
                if (template.FixedRewards.Count > 0 || template.Money > 0 || template.RewardSpell != 0)
                {
                    string receive = template.ChoiceRewards.Count > 0
                        ? "You will also receive:" : "You will receive:";
                    GameText.Draw(dl, "QuestFont", receive,
                        QuestFrameUiLaw.QuestLogDetailTextMin(contentOrigin, y, s), s, 0xff202020);
                    if (template.Money > 0)
                        DrawQuestMoney(dl,
                            QuestFrameUiLaw.QuestLogDetailMoneyMin(contentOrigin, y, s),
                            (uint)template.Money, s, "QuestLogMoney");
                    y += 20f;
                    y = DrawQuestItemGrid(dl,
                        QuestFrameUiLaw.QuestLogDetailGridOrigin(contentOrigin, s), s, y,
                        template.FixedRewards, false, "log-reward", clip);
                    if (template.RewardSpell != 0)
                    {
                        SpellInfo rewardSpell = default;
                        bool foundSpell = _spellCatalog is not null &&
                            _spellCatalog.TryGet(template.RewardSpell, out rewardSpell);
                        string spellName = foundSpell ? rewardSpell.Name : $"Spell {template.RewardSpell}";
                        string iconPath = foundSpell ? rewardSpell.IconPath
                            : @"Interface\Icons\INV_Misc_QuestionMark.blp";
                        uint icon = _gameplayArt?.Handle(iconPath) ?? 0;
                        Vector2 spellMin = QuestFrameUiLaw.QuestLogRewardSpellMin(
                            contentOrigin, y, s);
                        if (icon != 0) dl.AddImage((nint)icon, spellMin,
                            spellMin + QuestFrameUiLaw.QuestLogRewardSpellSize * s);
                        GameText.Draw(dl, "GameFontHighlight", spellName,
                            QuestFrameUiLaw.QuestLogRewardSpellTextMin(spellMin, s), s);
                        y += 25f;
                    }
                }
            }
        }
        else
        {
            y += DrawQuestWrappedText(dl, _questProgress.GetValueOrDefault(selectedSlot.QuestId,
                    "Retrieving quest details..."),
                QuestFrameUiLaw.QuestLogDetailTextMin(contentOrigin, y, s),
                QuestFrameUiLaw.QuestLogDetailTextWidth, "QuestFont", s, 0xff202020) / s;
        }
        dl.PopClipRect();
        _questLogDetailContentHeight = Math.Max(rect.Height, y - rect.Y + 10f);
        _questLogDetailScroll = QuestFrameUiLaw.ClampQuestLogDetailScroll(
            _questLogDetailScroll, _questLogDetailContentHeight);
    }

    private void EnsureQuestHeaderCatalogs()
    {
        EnsureQuestServerTime();
        EnsureAreaTableForMinimap();
        if (_questSortsLoaded) return;
        _questSortsLoaded = true;
        try
        {
            byte[]? bytes = _mpq?.ReadFile(QuestSortCatalog.MpqPath);
            if (bytes is not null) _questSorts = QuestSortCatalog.Parse(bytes);
        }
        catch (Exception e) { Console.WriteLine($"[quest] QuestSort load failed: {e.Message}"); }
    }

    private string QuestHeaderName(int zoneOrSort)
    {
        string name = zoneOrSort switch
        {
            > 0 => _areas?.AreaName((uint)zoneOrSort) ?? "",
            < 0 => _questSorts?.Name((uint)-(long)zoneOrSort) ?? "",
            _ => "",
        };
        return name.Length == 0 ? "Quests" : name;
    }

    private void EnsureQuestServerTime()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_questTimeAskedStamp != 0 &&
            System.Diagnostics.Stopwatch.GetElapsedTime(_questTimeAskedStamp, now).TotalHours < 1)
            return;
        if (_net?.QueryTime() == true) _questTimeAskedStamp = now;
    }

    private long? QuestSignedSecondsLeft(uint deadline, byte state)
    {
        if (deadline == 0 || (state & 2) != 0 || _questServerUnixStamp == 0) return null;
        double now = _questServerUnix +
            System.Diagnostics.Stopwatch.GetElapsedTime(_questServerUnixStamp).TotalSeconds;
        return (long)Math.Floor(deadline - now - 1.0);
    }

    private long? QuestSecondsLeft(uint deadline, byte state) =>
        QuestSignedSecondsLeft(deadline, state) is { } seconds ? Math.Max(0, seconds) : null;

    /// <summary>
    /// Every objective line this index produces — which may be TWO. Vanilla's
    /// creature array and item array are independent and share an index, and 89
    /// quest/index pairs across 83 quests in the shipped world DB carry a kill
    /// AND a collect at the same one. This used to return after the kill branch,
    /// so on those quests the collect objective was invisible in the quest log
    /// and — worse — uncounted by the watch frame, which then coloured the quest
    /// title complete while an unfinished objective was still outstanding.
    ///
    /// The objective TEXT belongs to the creature objective (it is
    /// ObjectiveText[i] from the quest query, which vanilla pairs with
    /// ReqCreatureOrGO[i]); when the index carries both, the collect line falls
    /// back to the item's own name rather than repeating the kill's label.
    /// </summary>
    private IEnumerable<(string Text, bool Finished)> QuestObjectiveLines(
        WorldEntity player, uint packedCounters, int index, QuestLogObjective objective)
    {
        bool kill = objective.CreatureOrGo != 0 && objective.RequiredCount > 0;
        bool collect = objective.ItemId != 0 && objective.ItemCount > 0;

        if (kill)
        {
            uint current = (packedCounters >> (6 * index)) & 0x3f;
            current = Math.Min(current, objective.RequiredCount);
            bool gameObject = (objective.CreatureOrGo & 0x8000_0000) != 0;
            uint entry = objective.CreatureOrGo & 0x7fff_ffff;
            string label = objective.Text.Length > 0 ? objective.Text
                : gameObject ? "Objective"
                : QuestCreatureKillLabel(
                    _creatureNames.GetValueOrDefault(entry, $"Creature {entry}"),
                    objective.RequiredCount);
            yield return ($"{label}: {current}/{objective.RequiredCount}",
                current >= objective.RequiredCount);
        }

        // NOT else-if. See the summary.
        if (collect)
        {
            uint current = Math.Min(CarriedCount(objective.ItemId), objective.ItemCount);
            string label = kill ? "" : objective.Text;
            if (label.Length == 0)
                label = QuestObjectiveItemLabel(objective.ItemId);
            yield return ($"{label}: {current}/{objective.ItemCount}",
                current >= objective.ItemCount);
        }
    }

    private string QuestObjectiveItemLabel(uint itemId)
    {
        if (_items is not null && _net is not null)
            _items.Require(itemId, 0, _net);
        string? resolvedName = _items?.TryGet(itemId, out ItemTemplate? item) == true
            ? item?.Name : null;
        return QuestFrameUiLaw.ObjectiveItemLabel(resolvedName);
    }

    private static string QuestCreatureKillLabel(string name, uint requiredCount) =>
        $"{(requiredCount == 1 ? name : PluralizeQuestCreatureName(name))} slain";

    private static string PluralizeQuestCreatureName(string name)
    {
        if (name.Length == 0 || name.StartsWith("Creature ", StringComparison.Ordinal))
            return name;
        if (name.EndsWith("ch", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith("sh", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith('s') || name.EndsWith('x') || name.EndsWith('z'))
            return name + "es";
        if (name.Length > 1 && name.EndsWith('y') &&
            !"aeiou".Contains(char.ToLowerInvariant(name[^2])))
            return name[..^1] + "ies";
        return name + "s";
    }

    /// <summary>The bracket count excludes the character whose log is displayed.</summary>
    private int PartyQuestHolderCount(uint questId, ulong displayedSubject) =>
        _partyMembers.Count(member => member.Guid != displayedSubject &&
            MemberQuestEntries(member.Guid).Any(entry =>
                entry.QuestId == questId && !entry.Rewarded));

    private ulong[] PartyBotsHoldingQuest(uint questId, ulong exceptSubject) =>
        _partyMembers.Where(member => member.Guid != exceptSubject &&
                HasMemberQuestFacts(member.Guid) &&
                MemberQuestEntries(member.Guid).Any(entry =>
                    entry.QuestId == questId && !entry.Rewarded))
            .Select(member => member.Guid).ToArray();

    private void HandleQuestLogShiftClick(uint questId, string title)
    {
        if (_chatEditOpen)
        {
            InsertChatText(title);
            return;
        }

        int existing = _questWatches.IndexOf(questId);
        if (existing >= 0)
        {
            _questWatches.RemoveAt(existing);
            _questWatchCollapsed.Remove(questId);
            _questAutoWatchExpiries.Remove(questId);
            return;
        }
        if (!_questTemplates.TryGetValue(questId, out QuestTemplate? template))
        {
            if (_questQueries.Add(questId)) _net?.QuestQuery(questId);
            ShowUiError("Quest details are still loading.");
            return;
        }
        bool hasObjectives = template.Objectives.Any(o =>
            o.CreatureOrGo != 0 && o.RequiredCount > 0 || o.ItemId != 0 && o.ItemCount > 0);
        if (!hasObjectives)
        {
            ShowUiError("This quest has no objectives to track");
            return;
        }
        _questWatches.Add(questId);
        _questAutoWatchExpiries.Remove(questId); // a Shift-click watch is permanent/manual
    }

    private void AutoWatchQuest(uint questId)
    {
        if (!Settings.Controls.AutomaticQuestTracking)
        {
            _questAutoWatchPending.Remove(questId);
            return;
        }
        if (!_questTemplates.TryGetValue(questId, out QuestTemplate? template))
        {
            _questAutoWatchPending.Add(questId);
            RequireQuestTemplate(questId);
            return;
        }
        _questAutoWatchPending.Remove(questId);
        if (!template.Objectives.Any(o => o.CreatureOrGo != 0 && o.RequiredCount > 0 ||
            o.ItemId != 0 && o.ItemCount > 0)) return;
        if (!QuestHeldByTrackerOwner(questId))
        {
            // Accept/result and objective packets can beat the update fields or party-facts
            // reply. Leave this armed until either source confirms an owner actually holds it.
            _questAutoWatchPending.Add(questId);
            return;
        }
        double expires = System.Diagnostics.Stopwatch.GetTimestamp() /
            (double)System.Diagnostics.Stopwatch.Frequency + QuestFrameUiLaw.AutoQuestWatchSeconds;
        if (_questWatches.Contains(questId))
        {
            // Existing manual watches carry no timer and are never downgraded.
            if (_questAutoWatchExpiries.ContainsKey(questId))
                _questAutoWatchExpiries[questId] = expires;
            return;
        }
        _questWatches.Add(questId);
        _questAutoWatchExpiries[questId] = expires;
    }

    private bool QuestHeldByTrackerOwner(uint questId) =>
        MergedOwnQuestLog().Any(entry => entry.QuestId == questId) ||
        _partyMembers.Any(member => MemberQuestEntries(member.Guid).Any(entry =>
            entry.QuestId == questId && !entry.Rewarded));

    private void ExpireAutomaticQuestWatches()
    {
        if (_questAutoWatchExpiries.Count == 0) return;
        double now = System.Diagnostics.Stopwatch.GetTimestamp() /
            (double)System.Diagnostics.Stopwatch.Frequency;
        foreach (uint id in _questAutoWatchExpiries
            .Where(pair => pair.Value <= now).Select(pair => pair.Key).ToArray())
        {
            _questAutoWatchExpiries.Remove(id);
            _questWatches.Remove(id);
            _questWatchCollapsed.Remove(id);
        }
    }

    private void DrawQuestWatchFrame()
    {
        _questWatchTitleHits.Clear();
        ExpireAutomaticQuestWatches();
        ulong questLogSubject = !_freeView && ControlledGuid != 0
            ? ControlledGuid : _net?.PlayerGuid ?? 0;
        if (_questWatches.Count == 0 || _net is null) return;
        var lines = new List<QuestWatchLine>(
            QuestFrameUiLaw.MaxQuestWatchLines);
        List<(ulong Guid, string Name)> companions = _partyMembers
            .Select(member => (member.Guid, member.Name))
            .ToList();
        if (companions.Count > 0)
        {
            var owners = new List<(ulong Guid, string Name)> { (LocalPlayerGuid, "You") };
            owners.AddRange(companions);
            AppendPartyQuestWatchLines(lines, owners);
        }
        else if (_entities.TryGet(questLogSubject, out WorldEntity player))
        {
            AppendSingleQuestWatchLines(lines, player);
        }
        if (lines.Count == 0) return;

        float s = GameplayUiScale();
        float em = GameText.EmPixels("GameFontHighlight", s) / s;
        float width = lines.Max(line => GameText.MeasureWidth("GameFontHighlight",
            QuestWatchDisplayText(line), s) / s) + 10f;
        Vector2 topRight = QuestFrameUiLaw.QuestWatchTopRight(
            ImGui.GetIO().DisplaySize, s, _questTimerFrameHeight, _durabilityFrameShown);
        Vector2 origin = new(topRight.X - width * s, topRight.Y);
        ImDrawListPtr dl = ImGui.GetBackgroundDrawList();
        float previousBottom = 0f;
        for (int i = 0; i < lines.Count; i++)
        {
            QuestWatchLine line = lines[i];
            float top = QuestFrameUiLaw.QuestWatchLineTop(previousBottom, line.Title, i == 0);
            uint color = line.Title
                ? ImGui.ColorConvertFloat4ToU32(line.Finished
                    ? new Vector4(1f, .82f, 0f, 1f) : new Vector4(.75f, .61f, 0f, 1f))
                : ImGui.ColorConvertFloat4ToU32(line.Finished
                    ? Vector4.One : new Vector4(.8f, .8f, .8f, 1f));
            Vector2 lineMin = origin + new Vector2(0f, top * s);
            if (line.Title)
            {
                string text = QuestWatchDisplayText(line);
                float textWidth = GameText.MeasureWidth("GameFontHighlight", text, s);
                Vector2 lineMax = lineMin + new Vector2(
                    MathF.Max(1f, textWidth), MathF.Max(1f, em * s));
                var hit = new QuestWatchTitleHit(line.QuestId, lineMin, lineMax);
                _questWatchTitleHits.Add(hit);
                bool hovered = QuestWatchTitleContains(hit, _window.MousePosition);
                if (hovered) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                GameText.Draw(dl, "GameFontHighlight", text, lineMin, s,
                    hovered ? VanillaGold : color);
            }
            else
                GameText.Draw(dl, "GameFontHighlight", line.Text, lineMin, s, color);
            previousBottom = top + em;
        }
    }

    private string QuestWatchDisplayText(in QuestWatchLine line) => line.Title
        ? $"{(_questWatchCollapsed.Contains(line.QuestId) ? "+" : "-")} {line.Text}"
        : line.Text;

    private static bool QuestWatchTitleContains(
        in QuestWatchTitleHit hit, Vector2 position) =>
        position.X >= hit.Min.X && position.X <= hit.Max.X &&
        position.Y >= hit.Min.Y && position.Y <= hit.Max.Y;

    private bool IsQuestWatchTitleAt(Vector2 position) =>
        _questWatchTitleHits.Any(hit => QuestWatchTitleContains(hit, position));

    private bool TryToggleQuestWatchAt(Vector2 position, bool leftClick)
    {
        if (!leftClick) return false;
        foreach (QuestWatchTitleHit hit in _questWatchTitleHits)
        {
            if (!QuestWatchTitleContains(hit, position)) continue;
            if (!_questWatches.Contains(hit.QuestId)) return true;
            if (!_questWatchCollapsed.Remove(hit.QuestId))
                _questWatchCollapsed.Add(hit.QuestId);
            return true;
        }
        return false;
    }

    private void AppendSingleQuestWatchLines(
        List<QuestWatchLine> lines, WorldEntity player)
    {
        var slots = DisplayedQuestLog().ToDictionary(q => q.QuestId);
        foreach (uint questId in _questWatches)
        {
            if (!slots.TryGetValue(questId, out var slot) ||
                !_questTemplates.TryGetValue(questId, out QuestTemplate? template)) continue;
            int titleAt = lines.Count;
            lines.Add(new(questId,
                _questTitles.GetValueOrDefault(questId, $"Quest {questId}"), true, false));
            bool collapsed = _questWatchCollapsed.Contains(questId);
            int objectives = 0, complete = 0;
            for (int i = 0; i < template.Objectives.Count; i++)
            {
                foreach ((string text, bool finished) in QuestObjectiveLines(
                    player, slot.Counters, i, template.Objectives[i]))
                {
                    objectives++;
                    if (finished) complete++;
                    if (!collapsed && lines.Count < QuestFrameUiLaw.MaxQuestWatchLines)
                        lines.Add(new(questId, " - " + text, false, finished));
                }
            }
            if (objectives == 0) lines.RemoveAt(titleAt);
            else lines[titleAt] = lines[titleAt] with { Finished = complete == objectives };
            if (lines.Count >= QuestFrameUiLaw.MaxQuestWatchLines) break;
        }
    }

    private void AppendPartyQuestWatchLines(
        List<QuestWatchLine> lines,
        IReadOnlyList<(ulong Guid, string Name)> owners)
    {
        foreach (uint questId in _questWatches)
        {
            if (!_questTemplates.TryGetValue(questId, out QuestTemplate? template)) continue;
            int titleAt = lines.Count;
            lines.Add(new(questId,
                _questTitles.GetValueOrDefault(questId, $"Quest {questId}"), true, false));
            bool collapsed = _questWatchCollapsed.Contains(questId);
            int participants = 0, completeParticipants = 0;
            foreach ((ulong guid, string name) in owners)
            {
                PartyQuestCell cell = PartyQuestCellFor(guid, questId);
                if (cell.Rewarded)
                {
                    if (!collapsed && lines.Count < QuestFrameUiLaw.MaxQuestWatchLines)
                        lines.Add(new(questId, $" - {name}: Turned in.", false, true));
                    participants++;
                    completeParticipants++;
                    continue;
                }
                if (!cell.Held)
                {
                    string state = guid == LocalPlayerGuid || HasMemberQuestFacts(guid)
                        ? "Not on quest." : "Progress unavailable.";
                    if (!collapsed && lines.Count < QuestFrameUiLaw.MaxQuestWatchLines)
                        lines.Add(new(questId, $" - {name}: {state}", false, false));
                    continue;
                }

                participants++;
                int objectives = 0, complete = 0;
                foreach ((string text, bool finished) in PartyQuestObjectiveLines(cell, template))
                {
                    objectives++;
                    if (finished) complete++;
                    if (!collapsed && lines.Count < QuestFrameUiLaw.MaxQuestWatchLines)
                        lines.Add(new(questId, $" - {name}: {text}", false, finished));
                }
                if (objectives > 0 && complete == objectives) completeParticipants++;
            }
            lines[titleAt] = lines[titleAt] with
            {
                Finished = participants > 0 && completeParticipants == participants,
            };
            if (lines.Count >= QuestFrameUiLaw.MaxQuestWatchLines) break;
        }
    }

    private bool TryDismissQuestAbandonOnEscape()
    {
        if (_questAbandonConfirmation is null && _partyQuestAbandonConfirmation is null)
            return false;
        _questAbandonConfirmation = null;
        _partyQuestAbandonConfirmation = null;
        return true;
    }

    private void DrawQuestAbandonConfirmation()
    {
        if (_questAbandonConfirmation is not { } confirmation)
        {
            DrawPartyQuestAbandonConfirmation();
            return;
        }
        if (_skin is null) return;
        if (_net is not { IsInWorld: true } ||
            !_entities.TryGet(confirmation.Subject, out _) ||
            !QuestLogForSubject(confirmation.Subject).Any(q => q.QuestId == confirmation.QuestId))
        {
            _questAbandonConfirmation = null;
            return;
        }

        float s = GameplayUiScale();
        QuestLogicalRect frame = QuestFrameUiLaw.AbandonPopupRect;
        Vector2 origin = QuestFrameUiLaw.AbandonPopupOrigin(ImGui.GetIO().DisplaySize, s);
        Vector2 size = frame.ScaledSize(s);
        ImGui.SetNextWindowPos(origin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);
        ImGui.SetNextWindowFocus();
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoNav;
        if (!ImGui.Begin("##quest-abandon-confirm", flags)) { ImGui.End(); return; }

        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        dl.PushClipRectFullScreen();
        _skin.DrawBackdrop(dl, origin, origin + size, WowSkin.Dialog);
        dl.PopClipRect();

        string subjectName = confirmation.Subject == LocalPlayerGuid
            ? "you"
            : ResolveUnitName(confirmation.Subject);
        string message = confirmation.Subject == LocalPlayerGuid
            ? $"Abandon \"{confirmation.Title}\"?"
            : $"Have {subjectName} abandon \"{confirmation.Title}\"?";
        GameText.DrawCentered(dl, "GameFontHighlight", message,
            QuestFrameUiLaw.AbandonPopupTextCenter(origin, s), s);
        bool yes = DrawQuestAbandonButton(dl, "yes", "Yes", origin,
            QuestFrameUiLaw.AbandonPopupAcceptRect, s);
        bool no = DrawQuestAbandonButton(dl, "no", "No", origin,
            QuestFrameUiLaw.AbandonPopupCancelRect, s);
        ImGui.End();

        if (no)
        {
            _questAbandonConfirmation = null;
            return;
        }
        if (!yes) return;
        if (RefuseTacticalFreezeLiveCommand("abandoning a quest")) return;
        if (AbandonQuest(confirmation.Subject, confirmation.QuestId))
        {
            PlayUiSound("igQuestLogAbandonQuest");
            ulong[] companions = _partyQuestActsAvailable
                ? PartyBotsHoldingQuest(confirmation.QuestId, confirmation.Subject)
                : [];
            if (companions.Length > 0)
                _partyQuestAbandonConfirmation = new(
                    confirmation.QuestId, confirmation.Title, companions);
        }
        _questAbandonConfirmation = null;
    }

    private void DrawPartyQuestAbandonConfirmation()
    {
        if (_partyQuestAbandonConfirmation is not { } confirmation || _skin is null)
            return;
        HashSet<ulong> stillHolding = PartyBotsHoldingQuest(
            confirmation.QuestId, 0).ToHashSet();
        ulong[] subjects = confirmation.Subjects.Where(stillHolding.Contains).ToArray();
        if (subjects.Length == 0)
        {
            _partyQuestAbandonConfirmation = null;
            return;
        }

        float s = GameplayUiScale();
        string prompt = "Do you also want to abandon it on";
        string names = string.Join(", ", subjects.Select(ResolveUnitName)) + "?";
        float widestLine = Math.Max(
            GameText.MeasureWidth("GameFontHighlight", prompt, s),
            GameText.MeasureWidth("GameFontHighlight", names, s)) / s;
        QuestLogicalRect frame = QuestFrameUiLaw.PartyAbandonPopupRect(
            widestLine, ImGui.GetIO().DisplaySize.X, s);
        Vector2 origin = QuestFrameUiLaw.AbandonPopupOrigin(
            ImGui.GetIO().DisplaySize, s, frame);
        Vector2 size = frame.ScaledSize(s);
        ImGui.SetNextWindowPos(origin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);
        ImGui.SetNextWindowFocus();
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoNav;
        if (!ImGui.Begin("##party-quest-abandon-confirm", flags)) { ImGui.End(); return; }

        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        dl.PushClipRectFullScreen();
        _skin.DrawBackdrop(dl, origin, origin + size, WowSkin.Dialog);
        dl.PopClipRect();
        GameText.DrawCentered(dl, "GameFontHighlight", prompt,
            QuestFrameUiLaw.PartyAbandonTextCenter(origin, s, frame, names: false), s);
        GameText.DrawCentered(dl, "GameFontHighlight", names,
            QuestFrameUiLaw.PartyAbandonTextCenter(origin, s, frame, names: true), s);
        bool yes = DrawQuestAbandonButton(dl, "party-yes", "Yes", origin,
            QuestFrameUiLaw.PartyAbandonButtonRect(frame, accept: true), s);
        bool no = DrawQuestAbandonButton(dl, "party-no", "No", origin,
            QuestFrameUiLaw.PartyAbandonButtonRect(frame, accept: false), s);
        ImGui.End();

        if (no)
        {
            _partyQuestAbandonConfirmation = null;
            return;
        }
        if (!yes) return;
        if (RefuseTacticalFreezeLiveCommand("abandoning party quests")) return;
        PartyQuestSubject[] requests = subjects.Select(guid =>
            new PartyQuestSubject(guid, PartyQuestWire.RewardChoiceAuto)).ToArray();
        if (RequestPartyQuestAct(PartyQuestWire.ActionAbandon,
                confirmation.QuestId, 0, requests))
            PlayUiSound("igQuestLogAbandonQuest");
        _partyQuestAbandonConfirmation = null;
    }

    private bool DrawQuestAbandonButton(ImDrawListPtr dl, string id, string caption,
        Vector2 origin, QuestLogicalRect rect, float scale)
    {
        return VanillaButton(dl, $"##quest-abandon-{id}", caption,
            rect.ScaledMin(origin, scale), rect.Size, scale);
    }

    private void DrawQuestNpcContent(ImDrawListPtr dl, Vector2 origin, float s)
    {
        QuestNpcPanel panel = QuestNpcPanelNow();
        if (panel == QuestNpcPanel.None) return;
        string panelStem = QuestPanelStem(panel);
        string panelName = panelStem + "Panel";
        string scrollName = panelStem + "Scroll";
        string scrollChildName = scrollName + "Child";
        QuestScreenRect scrollClip = QuestFrameUiLaw.NpcScrollClip(origin, s);
        Vector2 scrollMin = scrollClip.Min;
        Vector2 scrollSize = QuestFrameUiLaw.NpcScrollRect.ScaledSize(s);
        if (_uiParityArmed && _uiParityPanel == "quest-frame")
            CollectUiParityDraw(scrollName, "ScrollFrame", scrollMin, scrollSize,
                panelName, new("", 0, "IMGUI_CLIP", "TOPLEFT", panelName, "TOPLEFT",
                    QuestFrameUiLaw.ScrollX, -QuestFrameUiLaw.ScrollY,
                    ContentRect:new(scrollMin.X,scrollMin.Y,scrollMin.X+scrollSize.X,
                        scrollMin.Y+scrollSize.Y),
                    ClipRect:new(scrollMin.X,scrollMin.Y,scrollMin.X+scrollSize.X,
                        scrollMin.Y+scrollSize.Y),ClipMask:scrollName,Strata:"DIALOG"));
        _questNpcScroll = QuestFrameUiLaw.ClampScroll(_questNpcScroll, _questNpcContentHeight);
        if (ImGui.IsMouseHoveringRect(scrollClip.Min, scrollClip.Max, false) &&
            ImGui.GetIO().MouseWheel != 0)
        {
            _questNpcScroll -= ImGui.GetIO().MouseWheel * QuestFrameUiLaw.ScrollStep;
            _questNpcScroll = QuestFrameUiLaw.ClampScroll(_questNpcScroll, _questNpcContentHeight);
        }

        dl.PushClipRect(scrollClip.Min, scrollClip.Max, true);
        Vector2 content = QuestFrameUiLaw.NpcScrollContentOrigin(
            scrollMin, _questNpcScroll, s);
        float contentHeight = panel switch
        {
            QuestNpcPanel.Greeting => DrawQuestGreetingContent(dl, content, s),
            QuestNpcPanel.Detail => DrawQuestDetailContent(dl, content, s, scrollClip),
            QuestNpcPanel.Progress => DrawQuestProgressContent(dl, content, s, scrollClip),
            QuestNpcPanel.Reward => DrawQuestRewardContent(dl, content, s, scrollClip),
            _ => 0,
        };
        dl.PopClipRect();
        if (_uiParityArmed && _uiParityPanel == "quest-frame")
        {
            Vector2 contentSize = QuestFrameUiLaw.NpcScrollContentSize(contentHeight, s);
            CollectUiParityDraw(scrollChildName, "Frame", content, contentSize, scrollName,
                new("",0,"FRAMES","TOPLEFT",scrollName,"TOPLEFT",0,_questNpcScroll,
                    ContentRect:new(content.X,content.Y,
                        content.X + contentSize.X, content.Y + contentSize.Y),
                    ClipRect:new(scrollClip.Min.X, scrollClip.Min.Y,
                        scrollClip.Max.X, scrollClip.Max.Y),
                    ClipMask:scrollName,Strata:"DIALOG"));
        }
        _questNpcScroll = QuestFrameUiLaw.ClampScroll(_questNpcScroll, contentHeight);
        _questNpcContentHeight = contentHeight;
        DrawQuestNpcScrollbar(dl, origin, s, contentHeight);

        switch (panel)
        {
            case QuestNpcPanel.Greeting:
                if (QuestPanelButton(dl, "BenillaQuestGreetingGoodbyeButton", "##quest-goodbye",
                        "Goodbye", QuestFrameUiLaw.NpcGreetingGoodbyeRect.ScaledMin(origin, s),
                        QuestFrameUiLaw.NpcGreetingGoodbyeRect.Size, s))
                    CloseQuestNpcFrame(playSound: true);
                break;
            case QuestNpcPanel.Detail:
                if (QuestPanelButton(dl, "BenillaQuestAcceptButton", "##quest-accept", "Accept",
                        QuestFrameUiLaw.NpcDetailAcceptRect.ScaledMin(origin, s),
                        QuestFrameUiLaw.NpcDetailAcceptRect.Size, s)) AcceptQuest();
                if (QuestPanelButton(dl, "BenillaQuestDeclineButton", "##quest-decline", "Decline",
                        QuestFrameUiLaw.NpcDetailDeclineRect.ScaledMin(origin, s),
                        QuestFrameUiLaw.NpcDetailDeclineRect.Size, s))
                { PlayUiSound("igQuestCancel"); CloseQuestNpcFrame(playSound: true); }
                break;
            case QuestNpcPanel.Progress:
                if (QuestPanelButton(dl, "BenillaQuestProgressContinueButton", "##quest-continue",
                        "Continue", QuestFrameUiLaw.NpcProgressPrimaryRect.ScaledMin(origin, s),
                        QuestFrameUiLaw.NpcProgressPrimaryRect.Size, s,
                        _questRequestItems?.Completable == true))
                    RequestQuestReward();
                if (QuestPanelButton(dl, "BenillaQuestProgressGoodbyeButton", "##quest-progress-goodbye",
                        "Goodbye", QuestFrameUiLaw.NpcProgressGoodbyeRect.ScaledMin(origin, s),
                        QuestFrameUiLaw.NpcProgressGoodbyeRect.Size, s))
                { PlayUiSound("igQuestCancel"); CloseQuestNpcFrame(playSound: true); }
                break;
            case QuestNpcPanel.Reward:
                int choices = _questOffer?.ChoiceRewards.Count ?? 0;
                bool enabled = QuestFrameUiLaw.RewardCompleteEnabled(choices, _questRewardChoice);
                if (QuestPanelButton(dl, "BenillaQuestRewardCompleteButton", "##quest-complete",
                        "Complete Quest", QuestFrameUiLaw.NpcRewardPrimaryRect.ScaledMin(origin, s),
                        QuestFrameUiLaw.NpcRewardPrimaryRect.Size, s, enabled))
                {
                    uint choice = (uint)Math.Max(0, _questRewardChoice);
                    if (ChooseQuestReward(choice)) PlayUiSound("igQuestListComplete");
                }
                if (QuestPanelButton(dl, "BenillaQuestRewardCancelButton", "##quest-reward-cancel",
                        "Cancel", QuestFrameUiLaw.NpcRewardCancelRect.ScaledMin(origin, s),
                        QuestFrameUiLaw.NpcRewardCancelRect.Size, s))
                { PlayUiSound("igQuestCancel"); CloseQuestNpcFrame(playSound: true); }
                break;
        }
    }

    private bool QuestPanelButton(ImDrawListPtr dl, string element, string id, string caption,
        Vector2 min, Vector2 logicalSize, float s, bool enabled = true)
    {
        Vector2 size = logicalSize * s;
        ImGui.SetCursorScreenPos(min);
        if (!enabled) ImGui.BeginDisabled();
        ImGui.InvisibleButton(id, size);
        bool clicked = enabled && ImGui.IsItemClicked(ImGuiMouseButton.Left);
        bool active = enabled && ImGui.IsItemActive();
        bool hovered = enabled && ImGui.IsItemHovered();
        if (!enabled) ImGui.EndDisabled();

        string state = !enabled ? "Disabled" : active ? "Down" : "Up";
        string stateType = state switch
        {
            "Down" => "PushedTexture",
            "Disabled" => "DisabledTexture",
            _ => "NormalTexture",
        };
        string texturePath = $@"Interface\Buttons\UI-Panel-Button-{state}";
        uint texture = _gameplayArt?.Handle(texturePath) ?? 0;
        if (texture != 0)
            dl.AddImage((nint)texture, min, min + size, Vector2.Zero, new(.625f, .6875f));
        if (hovered)
        {
            uint highlight = _gameplayArt?.BrightHighlightHandle(
                @"Interface\Buttons\UI-Panel-Button-Highlight") ?? 0;
            if (highlight != 0)
                dl.AddImage((nint)highlight, min, min + size, Vector2.Zero, new(.625f, .6875f));
        }
        string fontObject = !enabled ? "GameFontDisable" : hovered
            ? "GameFontHighlight" : "GameFontNormal";
        GameText.DrawCentered(dl, fontObject, caption, min + size * .5f, s);

        if (_uiParityArmed && _uiParityPanel == "quest-frame")
        {
            string panelName = QuestPanelStem(QuestNpcPanelNow()) + "Panel";
            Vector4 clip = new(_uiParityOrigin.X, _uiParityOrigin.Y,
                _uiParityOrigin.X + QuestFrameUiLaw.Width * s,
                _uiParityOrigin.Y + QuestFrameUiLaw.Height * s);
            string interaction = !enabled ? "disabled" : active ? "pressed" : hovered ? "hovered" : "normal";
            CollectUiParityDraw(element, "Button", min, size, panelName,
                new("", 0, "FRAMES", "TOPLEFT", panelName, "TOPLEFT",
                    (min.X - _uiParityOrigin.X) / s, -((min.Y - _uiParityOrigin.Y) / s),
                    ClipRect: clip, ClipMask: panelName, Visible: true, Enabled: enabled,
                    InteractionState: interaction, HitMin: min, HitMax: min + size,
                    Strata: "DIALOG"));
            CollectUiParityDraw(element + "/" + stateType,
                stateType, min, size, element,
                new(texturePath, 0xffffffff, "ARTWORK", "TOPLEFT", element, "TOPLEFT", 0, 0,
                    TexCoords: "0|0|0.625|0.6875", ClipRect: clip, ClipMask: element,
                    BlendMode: "BLEND", Strata: "DIALOG"));
            foreach (string hiddenType in new[] { "NormalTexture", "PushedTexture", "DisabledTexture" })
                if (!hiddenType.Equals(stateType, StringComparison.Ordinal))
                    ClassifyUiParity(element + "/" + hiddenType, hiddenType, element,
                        "NOT-DRAWN", $"button-state-is-{interaction}");
            if (hovered)
                CollectUiParityDraw(element + "/HighlightTexture", "HighlightTexture", min, size,
                    element, new(@"Interface\Buttons\UI-Panel-Button-Highlight", 0xffffffff,
                        "HIGHLIGHT", "TOPLEFT", element, "TOPLEFT", 0, 0,
                        TexCoords: "0|0|0.625|0.6875", ClipRect: clip, ClipMask: element,
                        BlendMode: "ADD", Strata: "DIALOG"));
            else
                ClassifyUiParity(element + "/HighlightTexture", "HighlightTexture", element,
                    "NOT-DRAWN", enabled ? "button-is-not-hovered" : "button-is-disabled");
            FontObjectSpec font = FontObjectLaw.Get(fontObject);
            Vector2 textSize = new(GameText.MeasureWidth(fontObject, caption, s),
                GameText.EmPixels(fontObject, s));
            Vector2 textMin = min + (size - textSize) * .5f;
            CollectUiParityDraw(element + "/Text", "FontString", textMin, textSize, element,
                new("", font.Color, "OVERLAY", "CENTER", element, "CENTER", 0, 0,
                    font.Face, font.Height, ClipRect: clip, ClipMask: element, Strata: "DIALOG"));
        }
        return clicked;
    }

    private void TraceQuestText(string element, string fontObject, Vector2 min, Vector2 size,
        string parent, uint color)
    {
        if (!_uiParityArmed || _uiParityPanel != "quest-frame") return;
        Vector4 clip = new(_uiParityOrigin.X + QuestFrameUiLaw.ScrollX * _uiParityLogicalScale,
            _uiParityOrigin.Y + QuestFrameUiLaw.ScrollY * _uiParityLogicalScale,
            _uiParityOrigin.X + (QuestFrameUiLaw.ScrollX + QuestFrameUiLaw.ScrollWidth) * _uiParityLogicalScale,
            _uiParityOrigin.Y + (QuestFrameUiLaw.ScrollY + QuestFrameUiLaw.ScrollHeight) * _uiParityLogicalScale);
        FontObjectSpec font = FontObjectLaw.Get(fontObject);
        CollectUiParityDraw(element, "FontString", min, size, parent,
            new("", color, "ARTWORK", "TOPLEFT", parent, "TOPLEFT", 0, 0,
                font.Face, font.Height, ClipRect:clip, ClipMask:parent, Strata:"DIALOG"));
    }

    private static List<string> QuestWrappedLines(string text, float width, string fontObject,
        float s)
    {
        var lines = new List<string>();
        if (text.Length == 0) return lines;
        foreach (string paragraph in text.Replace("\r", "", StringComparison.Ordinal).Split('\n'))
        {
            if (paragraph.Length == 0)
            {
                lines.Add("");
                continue;
            }
            string current = "";
            int cursor = 0;
            while (cursor < paragraph.Length)
            {
                int spacesAt = cursor;
                while (cursor < paragraph.Length && paragraph[cursor] == ' ') cursor++;
                string spaces = paragraph[spacesAt..cursor];
                int wordAt = cursor;
                while (cursor < paragraph.Length && paragraph[cursor] != ' ') cursor++;
                string word = paragraph[wordAt..cursor];
                if (word.Length == 0)
                {
                    current += spaces;
                    break;
                }
                string candidate = current + spaces + word;
                if (current.Trim(' ').Length > 0 &&
                    GameText.MeasureWidth(fontObject, candidate, s) > width)
                {
                    lines.Add(current.TrimEnd(' '));
                    current = word;
                }
                else current = candidate;
            }
            lines.Add(current);
        }
        return lines;
    }

    private static float QuestWrappedLineHeight(string fontObject, float s) =>
        MathF.Ceiling(GameText.EmPixels(fontObject, s) * 1.18f);

    private static float MeasureQuestWrappedText(string text, float logicalWidth,
        string fontObject, float s) => QuestWrappedLines(text, logicalWidth * s, fontObject, s).Count *
        QuestWrappedLineHeight(fontObject, s);

    private static float DrawQuestWrappedText(ImDrawListPtr dl, string text, Vector2 min,
        float logicalWidth, string fontObject, float s, uint color)
    {
        List<string> lines = QuestWrappedLines(text, logicalWidth * s, fontObject, s);
        float lineHeight = QuestWrappedLineHeight(fontObject, s);
        for (int i = 0; i < lines.Count; i++)
            if (lines[i].Length > 0)
                GameText.Draw(dl, fontObject, lines[i],
                    QuestFrameUiLaw.NpcWrappedLineMin(min, i, lineHeight), s, color);
        return lines.Count * lineHeight;
    }

    private float DrawQuestGreetingContent(ImDrawListPtr dl, Vector2 p, float s)
    {
        if (_questList is null) return 0;
        float y = QuestFrameUiLaw.NpcContentInitialY;
        Vector2 greetingMin = QuestFrameUiLaw.GreetingTextMin(p, y, s);
        float greetingHeight = DrawQuestWrappedText(dl, ExpandQuestText(_questList.Greeting),
            greetingMin, QuestFrameUiLaw.GreetingTextWidth, "QuestFont", s, 0xff202020);
        TraceQuestText("BenillaQuestGreetingText", "QuestFont", greetingMin,
            QuestFrameUiLaw.NpcTraceSize(QuestFrameUiLaw.GreetingTextWidth,
                greetingHeight, s), "BenillaQuestGreetingScrollChild", 0xff202020);
        y += greetingHeight / s + 10;
        GossipQuest[] active = _questList.Quests
            .Where(q => QuestFrameUiLaw.GreetingPool(q.Icon) == QuestGreetingPool.Active)
            .Take(QuestFrameUiLaw.MaxTitles).ToArray();
        GossipQuest[] available = _questList.Quests
            .Where(q => QuestFrameUiLaw.GreetingPool(q.Icon) == QuestGreetingPool.Available)
            .Take(QuestFrameUiLaw.MaxTitles - active.Length).ToArray();
        int titleSlot = 1;
        if (active.Length > 0)
        {
            Vector2 headerMin = QuestFrameUiLaw.GreetingTextMin(p, y, s);
            GameText.Draw(dl, "QuestTitleFont", "Current Quests", headerMin, s);
            TraceQuestText("BenillaQuestActiveHeader","QuestTitleFont",headerMin,
                QuestFrameUiLaw.NpcTraceSize(QuestFrameUiLaw.GreetingHeaderTraceWidth,
                    GameText.EmPixels("QuestTitleFont", s), s),
                "BenillaQuestGreetingScrollChild",FontObjectLaw.Get("QuestTitleFont").Color);
            y += 23; // header height + authored 5 px gap to the first row
            foreach (GossipQuest quest in active) y += DrawQuestTitleRow(dl, p, s, y, quest, titleSlot++);
        }
        else if (_uiParityArmed && _uiParityPanel == "quest-frame")
            ClassifyUiParity("BenillaQuestActiveHeader","FontString",
                "BenillaQuestGreetingScrollChild","NOT-DRAWN","no-active-quest-rows");
        if (active.Length > 0 && available.Length > 0)
        {
            QuestLogicalRect breakRect = QuestFrameUiLaw.GreetingBreakRect(y);
            Vector2 breakMin = breakRect.ScaledMin(p, s);
            DrawArt(dl, @"Interface\QuestFrame\UI-HorizontalBreak", breakMin,
                breakRect.Size, s);
            if(_uiParityArmed&&_uiParityPanel=="quest-frame")
                CollectUiParityDraw("BenillaQuestGreetingBreak","Texture",breakMin,
                    breakRect.ScaledSize(s),"BenillaQuestGreetingScrollChild",
                    new(@"Interface\QuestFrame\UI-HorizontalBreak",0xffffffff,"ARTWORK",
                        "TOPLEFT","BenillaQuestGreetingScrollChild","TOPLEFT",22,-(y+10),
                        ClipRect:new(_uiParityOrigin.X+QuestFrameUiLaw.ScrollX*s,
                            _uiParityOrigin.Y+QuestFrameUiLaw.ScrollY*s,
                            _uiParityOrigin.X+(QuestFrameUiLaw.ScrollX+QuestFrameUiLaw.ScrollWidth)*s,
                            _uiParityOrigin.Y+(QuestFrameUiLaw.ScrollY+QuestFrameUiLaw.ScrollHeight)*s),
                        ClipMask:"BenillaQuestGreetingScroll",BlendMode:"BLEND",Strata:"DIALOG"));
            y += 52; // 10 px before break + 32 px break + 10 px before header
        }
        else if (_uiParityArmed && _uiParityPanel == "quest-frame")
            ClassifyUiParity("BenillaQuestGreetingBreak","Texture",
                "BenillaQuestGreetingScrollChild","NOT-DRAWN","one-greeting-pool-is-empty");
        if (available.Length > 0)
        {
            Vector2 headerMin = QuestFrameUiLaw.GreetingTextMin(p, y, s);
            GameText.Draw(dl, "QuestTitleFont", "Available Quests", headerMin, s);
            TraceQuestText("BenillaQuestAvailableHeader","QuestTitleFont",headerMin,
                QuestFrameUiLaw.NpcTraceSize(QuestFrameUiLaw.GreetingHeaderTraceWidth,
                    GameText.EmPixels("QuestTitleFont", s), s),
                "BenillaQuestGreetingScrollChild",FontObjectLaw.Get("QuestTitleFont").Color);
            y += 23; // header height + authored 5 px gap to the first row
            foreach (GossipQuest quest in available) y += DrawQuestTitleRow(dl, p, s, y, quest, titleSlot++);
        }
        else if (_uiParityArmed && _uiParityPanel == "quest-frame")
            ClassifyUiParity("BenillaQuestAvailableHeader","FontString",
                "BenillaQuestGreetingScrollChild","NOT-DRAWN","no-available-quest-rows");
        if (_uiParityArmed && _uiParityPanel == "quest-frame")
            for (int hidden = titleSlot; hidden <= QuestFrameUiLaw.MaxTitles; hidden++)
                ClassifyUiParity($"BenillaQuestTitleButton{hidden}", "Button",
                    "BenillaQuestGreetingScrollChild", "NOT-DRAWN", "quest-title-slot-unused");
        return Math.Max(334, y + 10);
    }

    private float DrawQuestTitleRow(ImDrawListPtr dl, Vector2 p, float s, float y, GossipQuest quest,
        int displayIndex)
    {
        Vector2 min = QuestFrameUiLaw.GreetingRowMin(p, y, s);
        string title = ExpandQuestText(quest.Title);
        float textHeight = Math.Max(16,
            MeasureQuestWrappedText(title, 275, "QuestFont", s) / s);
        DrawArt(dl, @"Interface\QuestFrame\UI-Quest-BulletPoint", min,
            QuestFrameUiLaw.GreetingBulletRect.Size, s);
        ImGui.SetCursorScreenPos(min);
        Vector2 rowSize = QuestFrameUiLaw.GreetingRowSize(textHeight, s);
        bool clicked = ImGui.InvisibleButton($"##quest-title-{quest.QuestId}", rowSize);
        bool hovered = ImGui.IsItemHovered();
        if (hovered)
        {
            uint hi = _gameplayArt!.AdditiveHandle(@"Interface\QuestFrame\UI-QuestTitleHighlight");
            if (hi != 0) dl.AddImage((nint)hi, min, min + rowSize);
        }
        Vector2 titleMin = QuestFrameUiLaw.GreetingTitleMin(min, s);
        DrawQuestWrappedText(dl, title, titleMin, QuestFrameUiLaw.GreetingTitleWidth,
            "QuestFont", s, 0xff202020);
        if (_uiParityArmed && _uiParityPanel == "quest-frame")
        {
            string element = $"BenillaQuestTitleButton{displayIndex}";
            Vector4 clip = new(_uiParityOrigin.X+QuestFrameUiLaw.ScrollX*s,
                _uiParityOrigin.Y+QuestFrameUiLaw.ScrollY*s,
                _uiParityOrigin.X+(QuestFrameUiLaw.ScrollX+QuestFrameUiLaw.ScrollWidth)*s,
                _uiParityOrigin.Y+(QuestFrameUiLaw.ScrollY+QuestFrameUiLaw.ScrollHeight)*s);
            CollectUiParityDraw(element,"Button",min,rowSize,"BenillaQuestGreetingScrollChild",
                new("",0,"FRAMES","TOPLEFT","BenillaQuestGreetingScrollChild","TOPLEFT",0,-y,
                    ClipRect:clip,ClipMask:"BenillaQuestGreetingScroll",Visible:true,Enabled:true,
                    InteractionState:hovered?"hovered":"normal",HitMin:min,HitMax:min+rowSize,
                    Strata:"DIALOG"));
            CollectUiParityDraw(element+"/Bullet","Texture",min,
                QuestFrameUiLaw.GreetingBulletRect.ScaledSize(s),element,
                new(@"Interface\QuestFrame\UI-Quest-BulletPoint",0xffffffff,"BACKGROUND",
                    "TOPLEFT",element,"TOPLEFT",0,0,ClipRect:clip,
                    ClipMask:"BenillaQuestGreetingScroll",BlendMode:"BLEND",Strata:"DIALOG"));
            CollectUiParityDraw(element+"/Text","FontString",titleMin,
                QuestFrameUiLaw.GreetingTitleTraceSize(textHeight, s),element,
                new("",0xff202020,"OVERLAY","LEFT",element,"LEFT",20,0,
                    FontObjectLaw.Get("QuestFont").Face,13,ClipRect:clip,
                    ClipMask:"BenillaQuestGreetingScroll",Strata:"DIALOG"));
            if (hovered)
                CollectUiParityDraw(element+"/HighlightTexture","HighlightTexture",min,rowSize,
                    element,new(@"Interface\QuestFrame\UI-QuestTitleHighlight",0xffffffff,
                        "HIGHLIGHT","TOPLEFT",element,"TOPLEFT",0,0,ClipRect:clip,
                        ClipMask:"BenillaQuestGreetingScroll",BlendMode:"ADD",Strata:"DIALOG"));
            else
                ClassifyUiParity(element+"/HighlightTexture","HighlightTexture",element,
                    "NOT-DRAWN","quest-title-is-not-hovered");
        }
        if (clicked && _questList is not null)
        {
            PlayUiSound("igQuestListSelect");
            if (QuestFrameUiLaw.GreetingAction(quest.Icon) == QuestGreetingAction.Complete)
                RequestQuestCompletion(_questList.GiverGuid, quest.QuestId);
            else
                RequestQuestDetails(_questList.GiverGuid, quest.QuestId);
        }
        return textHeight + 2;
    }

    private float DrawQuestDetailContent(ImDrawListPtr dl, Vector2 p, float s, QuestScreenRect clip)
    {
        if (_questDetails is null) return 0;
        float y = QuestFrameUiLaw.NpcContentInitialY;
        Vector2 titleMin = QuestFrameUiLaw.NpcContentTextMin(p, y, s);
        GameText.Draw(dl, "QuestTitleFont", ExpandQuestText(_questDetails.Title), titleMin, s);
        TraceQuestText("BenillaQuestDetailTitle","QuestTitleFont",titleMin,
            QuestFrameUiLaw.NpcTraceSize(QuestFrameUiLaw.NpcContentTitleTraceWidth,
                GameText.EmPixels("QuestTitleFont", s), s),
            "BenillaQuestDetailScrollChild",FontObjectLaw.Get("QuestTitleFont").Color);
        y += 19;
        Vector2 bodyMin = QuestFrameUiLaw.NpcContentTextMin(p, y, s);
        float bodyHeight=DrawQuestWrappedText(dl, ExpandQuestText(_questDetails.Details), bodyMin,
            QuestFrameUiLaw.NpcContentBodyWidth, "QuestFont", s, 0xff202020);
        TraceQuestText("BenillaQuestDetailBody","QuestFont",bodyMin,
            QuestFrameUiLaw.NpcTraceSize(QuestFrameUiLaw.NpcContentBodyWidth, bodyHeight, s),
            "BenillaQuestDetailScrollChild",0xff202020);
        y += bodyHeight/s+15;
        Vector2 objectiveHeaderMin = QuestFrameUiLaw.NpcContentTextMin(p, y, s);
        GameText.Draw(dl, "QuestTitleFont", "Quest Objectives", objectiveHeaderMin, s);
        TraceQuestText("BenillaQuestDetailObjectiveHeader","QuestTitleFont",objectiveHeaderMin,
            QuestFrameUiLaw.NpcTraceSize(QuestFrameUiLaw.NpcContentTitleTraceWidth,
                GameText.EmPixels("QuestTitleFont", s), s),
            "BenillaQuestDetailScrollChild",FontObjectLaw.Get("QuestTitleFont").Color);
        y += 23;
        Vector2 objectiveMin = QuestFrameUiLaw.NpcContentTextMin(p, y, s);
        float objectiveHeight=DrawQuestWrappedText(dl, ExpandQuestText(_questDetails.Objectives),
            objectiveMin, QuestFrameUiLaw.NpcContentBodyWidth,
            "QuestFont", s, 0xff202020);
        TraceQuestText("BenillaQuestDetailObjective","QuestFont",objectiveMin,
            QuestFrameUiLaw.NpcTraceSize(QuestFrameUiLaw.NpcContentBodyWidth,
                objectiveHeight, s), "BenillaQuestDetailScrollChild",0xff202020);
        y += objectiveHeight/s+15;
        return DrawQuestRewardSet(dl, p, s, y, _questDetails.ChoiceRewards,
            _questDetails.FixedRewards, _questDetails.Money, selectable: false, clip);
    }

    private float DrawQuestProgressContent(ImDrawListPtr dl, Vector2 p, float s, QuestScreenRect clip)
    {
        if (_questRequestItems is null) return 0;
        float y = QuestFrameUiLaw.NpcContentInitialY;
        Vector2 titleMin = QuestFrameUiLaw.NpcContentTextMin(p, y, s);
        GameText.Draw(dl, "QuestTitleFont", ExpandQuestText(_questRequestItems.Title), titleMin, s);
        TraceQuestText("BenillaQuestProgressTitle","QuestTitleFont",titleMin,
            QuestFrameUiLaw.NpcTraceSize(QuestFrameUiLaw.NpcContentTitleTraceWidth,
                GameText.EmPixels("QuestTitleFont", s), s),
            "BenillaQuestProgressScrollChild",FontObjectLaw.Get("QuestTitleFont").Color);
        y += 19;
        Vector2 bodyMin = QuestFrameUiLaw.NpcContentTextMin(p, y, s);
        float bodyHeight=DrawQuestWrappedText(dl, ExpandQuestText(_questRequestItems.Text), bodyMin,
            QuestFrameUiLaw.NpcContentBodyWidth, "QuestFont", s, 0xff202020);
        TraceQuestText("BenillaQuestProgressBody","QuestFont",bodyMin,
            QuestFrameUiLaw.NpcTraceSize(QuestFrameUiLaw.NpcContentBodyWidth, bodyHeight, s),
            "BenillaQuestProgressScrollChild",0xff202020);
        y += bodyHeight/s+15;
        if (_questRequestItems.RequiredItems.Count > 0 || _questRequestItems.RequiredMoney > 0)
        {
            Vector2 requiredMin = QuestFrameUiLaw.NpcContentTextMin(p, y, s);
            GameText.Draw(dl, "QuestTitleFont", "Items Needed:", requiredMin, s);
            TraceQuestText("BenillaQuestProgressRequiredText","QuestTitleFont",requiredMin,
                QuestFrameUiLaw.NpcTraceSize(QuestFrameUiLaw.NpcContentTitleTraceWidth,
                    GameText.EmPixels("QuestTitleFont", s), s),
                "BenillaQuestProgressScrollChild",FontObjectLaw.Get("QuestTitleFont").Color);
            y += 22;
        }
        else if(_uiParityArmed&&_uiParityPanel=="quest-frame")
            ClassifyUiParity("BenillaQuestProgressRequiredText","FontString",
                "BenillaQuestProgressScrollChild","NOT-DRAWN","no-required-items-or-money");
        if (_questRequestItems.RequiredMoney > 0)
        {
            const string requiredMoney = "Required Money:";
            Vector2 moneyLabelMin = QuestFrameUiLaw.NpcContentTextMin(p, y, s);
            GameText.Draw(dl, "QuestFontNormalSmall", requiredMoney, moneyLabelMin, s);
            TraceQuestText("BenillaQuestProgressRequiredMoneyText","QuestFontNormalSmall",
                moneyLabelMin, QuestFrameUiLaw.NpcScreenTextSize(
                    GameText.MeasureWidth("QuestFontNormalSmall", requiredMoney, s),
                    GameText.EmPixels("QuestFontNormalSmall", s)),
                "BenillaQuestProgressScrollChild",FontObjectLaw.Get("QuestFontNormalSmall").Color);
            float moneyWidth = GameText.MeasureWidth("QuestFontNormalSmall", requiredMoney, s);
            DrawQuestMoney(dl, QuestFrameUiLaw.NpcInlineMoneyMin(p, y,
                    QuestFrameUiLaw.NpcContentTextX, moneyWidth, 10, s),
                _questRequestItems.RequiredMoney, s, "BenillaQuestProgressMoney");
            y += 23;
        }
        else if(_uiParityArmed&&_uiParityPanel=="quest-frame")
        {
            ClassifyUiParity("BenillaQuestProgressRequiredMoneyText","FontString",
                "BenillaQuestProgressScrollChild","NOT-DRAWN","no-required-money");
            for (int denomination = 1; denomination <= 3; denomination++)
                ClassifyUiParity($"BenillaQuestProgressMoneyCoin{denomination}", "Frame",
                    "BenillaQuestProgressScrollChild", "NOT-DRAWN", "no-required-money");
        }
        y = DrawQuestItemGrid(dl, p, s, y, _questRequestItems.RequiredItems, selectable: false, "required", clip);
        return Math.Max(334, y + 10);
    }

    private float DrawQuestRewardContent(ImDrawListPtr dl, Vector2 p, float s, QuestScreenRect clip)
    {
        if (_questOffer is null) return 0;
        float y = QuestFrameUiLaw.NpcContentInitialY;
        Vector2 titleMin = QuestFrameUiLaw.NpcContentTextMin(p, y, s);
        GameText.Draw(dl, "QuestTitleFont", ExpandQuestText(_questOffer.Title), titleMin, s);
        TraceQuestText("BenillaQuestRewardTitle","QuestTitleFont",titleMin,
            QuestFrameUiLaw.NpcTraceSize(QuestFrameUiLaw.NpcContentTitleTraceWidth,
                GameText.EmPixels("QuestTitleFont", s), s),
            "BenillaQuestRewardScrollChild",FontObjectLaw.Get("QuestTitleFont").Color);
        y += 19;
        Vector2 bodyMin = QuestFrameUiLaw.NpcContentTextMin(p, y, s);
        float bodyHeight=DrawQuestWrappedText(dl, ExpandQuestText(_questOffer.Text), bodyMin,
            QuestFrameUiLaw.NpcRewardBodyWidth, "QuestFont", s, 0xff202020);
        TraceQuestText("BenillaQuestRewardBody","QuestFont",bodyMin,
            QuestFrameUiLaw.NpcTraceSize(QuestFrameUiLaw.NpcRewardBodyWidth, bodyHeight, s),
            "BenillaQuestRewardScrollChild",0xff202020);
        y += bodyHeight/s+10;
        return DrawQuestRewardSet(dl, p, s, y, _questOffer.ChoiceRewards,
            _questOffer.FixedRewards, _questOffer.Money, selectable: true, clip);
    }

    private float DrawQuestRewardSet(ImDrawListPtr dl, Vector2 p, float s, float y,
        IReadOnlyList<QuestRewardItem> choices, IReadOnlyList<QuestRewardItem> fixedItems,
        int money, bool selectable, QuestScreenRect clip)
    {
        string prefix = QuestNpcPanelNow() == QuestNpcPanel.Detail
            ? "BenillaQuestDetail" : "BenillaQuestReward";
        string child = prefix + "ScrollChild";
        if (choices.Count == 0 && fixedItems.Count == 0 && money <= 0)
        {
            if (_uiParityArmed && _uiParityPanel == "quest-frame")
            {
                ClassifyUiParity(prefix + "RewardTitleText", "FontString", child,
                    "NOT-DRAWN", "quest-has-no-reward");
                ClassifyUiParity(prefix + "ItemChooseText", "FontString", child,
                    "NOT-DRAWN", "quest-has-no-choice-reward");
                ClassifyUiParity(prefix + "ItemReceiveText", "FontString", child,
                    "NOT-DRAWN", "quest-has-no-fixed-or-money-reward");
                for (int denomination = 1; denomination <= 3; denomination++)
                    ClassifyUiParity(prefix + $"MoneyCoin{denomination}", "Frame", child,
                        "NOT-DRAWN", "quest-has-no-money-reward");
                for (int slot = 0; slot < QuestFrameUiLaw.MaxItems; slot++)
                {
                    ClassifyUiParity(QuestItemElementName("choice", slot), "Button", child,
                        "NOT-DRAWN", "quest-has-no-choice-reward");
                    ClassifyUiParity(QuestItemElementName("reward", slot), "Button", child,
                        "NOT-DRAWN", "quest-has-no-fixed-reward");
                }
                if (selectable)
                    ClassifyUiParity("BenillaQuestRewardChoiceHighlight", "HighlightTexture",
                        prefix + "Panel", "NOT-DRAWN", "quest-has-no-choice-reward");
            }
            return Math.Max(334, y + 10);
        }
        Vector2 rewardTitleMin = QuestFrameUiLaw.NpcContentTextMin(p, y, s);
        GameText.Draw(dl, "QuestTitleFont", "Rewards", rewardTitleMin, s);
        TraceQuestText(prefix + "RewardTitleText", "QuestTitleFont", rewardTitleMin,
            QuestFrameUiLaw.NpcTraceSize(QuestFrameUiLaw.NpcRewardTitleTraceWidth,
                GameText.EmPixels("QuestTitleFont", s), s), child,
            FontObjectLaw.Get("QuestTitleFont").Color);
        y += 23;
        if (choices.Count > 0)
        {
            const string chooseText = "You may choose one of these rewards:";
            Vector2 chooseMin = QuestFrameUiLaw.NpcContentTextMin(p, y, s);
            GameText.Draw(dl, "QuestFont", chooseText, chooseMin, s);
            TraceQuestText(prefix + "ItemChooseText", "QuestFont", chooseMin,
                QuestFrameUiLaw.NpcTraceSize(QuestFrameUiLaw.NpcContentTitleTraceWidth,
                    GameText.EmPixels("QuestFont", s), s), child,
                FontObjectLaw.Get("QuestFont").Color);
            y += 20;
            y = DrawQuestItemGrid(dl, p, s, y, choices, selectable, "choice", clip);
            y += 5;
        }
        else if (_uiParityArmed && _uiParityPanel == "quest-frame")
        {
            ClassifyUiParity(prefix + "ItemChooseText", "FontString", child,
                "NOT-DRAWN", "quest-has-no-choice-reward");
            for (int slot = 0; slot < QuestFrameUiLaw.MaxItems; slot++)
                ClassifyUiParity(QuestItemElementName("choice", slot), "Button", child,
                    "NOT-DRAWN", "quest-has-no-choice-reward");
            if (selectable)
                ClassifyUiParity("BenillaQuestRewardChoiceHighlight", "HighlightTexture",
                    prefix + "Panel", "NOT-DRAWN", "quest-has-no-choice-reward");
        }
        if (fixedItems.Count > 0 || money > 0)
        {
            string receive = choices.Count > 0 ? "You will also receive:" : "You will receive:";
            Vector2 receiveMin = QuestFrameUiLaw.NpcReceiveTextMin(p, y, s);
            GameText.Draw(dl, "QuestFont", receive, receiveMin, s);
            TraceQuestText(prefix + "ItemReceiveText", "QuestFont", receiveMin,
                QuestFrameUiLaw.NpcScreenTextSize(
                    GameText.MeasureWidth("QuestFont", receive, s),
                    GameText.EmPixels("QuestFont", s)), child,
                FontObjectLaw.Get("QuestFont").Color);
            if (money > 0)
            {
                float receiveWidth = GameText.MeasureWidth("QuestFont", receive, s);
                DrawQuestMoney(dl, QuestFrameUiLaw.NpcInlineMoneyMin(p, y,
                        QuestFrameUiLaw.NpcReceiveTextX, receiveWidth, 15, s),
                    (uint)money, s,
                    prefix + "Money");
            }
            else if (_uiParityArmed && _uiParityPanel == "quest-frame")
                for (int denomination = 1; denomination <= 3; denomination++)
                    ClassifyUiParity(prefix + $"MoneyCoin{denomination}", "Frame", child,
                        "NOT-DRAWN", "quest-has-no-money-reward");
            y += 20;
            y = DrawQuestItemGrid(dl, p, s, y, fixedItems, selectable: false, "reward", clip);
        }
        else if (_uiParityArmed && _uiParityPanel == "quest-frame")
        {
            ClassifyUiParity(prefix + "ItemReceiveText", "FontString", child,
                "NOT-DRAWN", "quest-has-no-fixed-or-money-reward");
            for (int denomination = 1; denomination <= 3; denomination++)
                ClassifyUiParity(prefix + $"MoneyCoin{denomination}", "Frame", child,
                    "NOT-DRAWN", "quest-has-no-money-reward");
        }
        return Math.Max(334, y + 10);
    }

    private float DrawQuestItemGrid(ImDrawListPtr dl, Vector2 p, float s, float y,
        IReadOnlyList<QuestRewardItem> items, bool selectable, string kind, QuestScreenRect clip)
    {
        int count = Math.Min(items.Count, QuestFrameUiLaw.MaxItems);
        for (int i = 0; i < count; i++)
            DrawQuestItemRow(dl, QuestFrameUiLaw.ItemGridRowMin(p, y, i, s),
                s, items[i], selectable, i, kind, clip);
        if (_uiParityArmed && _uiParityPanel == "quest-frame")
        {
            string scrollChild = QuestPanelStem(QuestNpcPanelNow()) + "ScrollChild";
            for (int hidden = count; hidden < QuestFrameUiLaw.MaxItems; hidden++)
                ClassifyUiParity(QuestItemElementName(kind, hidden), "Button",
                    scrollChild, "NOT-DRAWN", "quest-item-slot-unused");
            if (selectable && (_questRewardChoice < 0 || _questRewardChoice >= count))
                ClassifyUiParity("BenillaQuestRewardChoiceHighlight", "HighlightTexture",
                    "BenillaQuestRewardPanel", "NOT-DRAWN", "no-reward-choice-selected");
        }
        return y + (count == 0 ? 0 : ((count + 1) / 2) * (QuestFrameUiLaw.ItemHeight + 2));
    }

    private void DrawQuestItemRow(ImDrawListPtr dl, Vector2 min, float s, QuestRewardItem row,
        bool selectable, int index, string kind, QuestScreenRect visibilityClip)
    {
        if (_items is not null && _net is not null) _items.Require(row.ItemId, QuestGiverGuid(), _net);
        string name = "...";
        string iconPath = _items?.IconForDisplay(row.DisplayId) ?? @"Interface\Icons\INV_Misc_QuestionMark.blp";
        ItemTemplate? item = null;
        if (_items?.TryGet(row.ItemId, out item) == true && item is not null)
        {
            name = item.Name;
            if (row.DisplayId == 0) iconPath = item.IconPath;
        }
        uint icon = _gameplayArt!.Handle(iconPath);
        Vector2 iconSize = QuestFrameUiLaw.ItemIconRect.ScaledSize(s);
        if (icon != 0) dl.AddImage((nint)icon, min, min + iconSize);
        Vector2 nameFrameMin = QuestFrameUiLaw.ItemNameFrameRect.ScaledMin(min, s);
        DrawArt(dl, @"Interface\QuestFrame\UI-QuestItemNameFrame", nameFrameMin,
            QuestFrameUiLaw.ItemNameFrameRect.Size, s);
        Vector2 nameMin = QuestFrameUiLaw.ItemNameTextMin(min, s);
        GameText.Draw(dl, "GameFontHighlight", name, nameMin, s,
            item is null ? 0xffffffff : ImGui.ColorConvertFloat4ToU32(ItemQualityColor(item.Quality)));
        if (row.Count > 1) GameText.DrawRightAligned(dl, "NumberFontNormal", row.Count.ToString(),
            min + QuestFrameUiLaw.ItemCountAnchor * s, s);
        ImGui.SetCursorScreenPos(min);
        Vector2 itemSize = QuestFrameUiLaw.ItemHitRect.ScaledSize(s);
        // A scrolled-away row still reaches this call with real (off-clip) screen coordinates -
        // rendering already skips it via the caller's PushClipRect, but an InvisibleButton has no
        // such awareness and would silently claim the hover/click at wherever those coordinates
        // land on screen, ahead of whatever real, on-screen widget gets submitted after it this
        // frame (confirmed live: a long quest's reward row landed squarely on the Quest Log's
        // Abandon button and ate every click meant for it). Skip the widget entirely when the
        // row doesn't actually intersect the visible area, rather than just discarding the result.
        // A row that only PARTIALLY peeks past the clip (e.g. a couple of pixels at the scroll
        // edge) still had its entire, oversized hit-region active under an any-overlap test -
        // most of a barely-visible sliver was still fully clickable, which is what was still
        // reaching the Quest Log's Abandon button. Require full containment instead: a row is
        // only interactive if the whole of it is actually on screen.
        bool rowVisible = min.X >= visibilityClip.Min.X && min.X + itemSize.X <= visibilityClip.Max.X &&
            min.Y >= visibilityClip.Min.Y && min.Y + itemSize.Y <= visibilityClip.Max.Y;
        bool clicked = rowVisible && ImGui.InvisibleButton($"##quest-{kind}-{index}", itemSize);
        bool hovered = rowVisible && ImGui.IsItemHovered();
        if (_uiParityArmed && _uiParityPanel == "quest-frame")
        {
            string element = QuestItemElementName(kind, index);
            string scrollName = QuestPanelStem(QuestNpcPanelNow()) + "Scroll";
            string scrollChild = scrollName + "Child";
            Vector4 clip = new(_uiParityOrigin.X + QuestFrameUiLaw.ScrollX * s,
                _uiParityOrigin.Y + QuestFrameUiLaw.ScrollY * s,
                _uiParityOrigin.X + (QuestFrameUiLaw.ScrollX + QuestFrameUiLaw.ScrollWidth) * s,
                _uiParityOrigin.Y + (QuestFrameUiLaw.ScrollY + QuestFrameUiLaw.ScrollHeight) * s);
            CollectUiParityDraw(element, "Button", min, itemSize,
                scrollChild, new("", 0, "FRAMES", "TOPLEFT",
                    scrollChild, "TOPLEFT", 0, 0, ClipRect:clip,
                    ClipMask:scrollName, Visible:true, Enabled:true,
                    InteractionState:hovered?"hovered":"normal", HitMin:min,
                    HitMax:min + itemSize, Strata:"DIALOG"));
            CollectUiParityDraw(element + "/IconTexture", "Texture", min,
                iconSize, element, new(iconPath, 0xffffffff, "BACKGROUND",
                     "TOPLEFT", element, "TOPLEFT", 0, 0,
                     ContentRect:new(min.X, min.Y, min.X + iconSize.X, min.Y + iconSize.Y),
                    ClipRect:clip,
                    ClipMask:scrollName, BlendMode:"BLEND", Strata:"DIALOG"));
            CollectUiParityDraw(element + "/NameFrame", "Texture", nameFrameMin,
                QuestFrameUiLaw.ItemNameFrameRect.ScaledSize(s), element,
                new(@"Interface\QuestFrame\UI-QuestItemNameFrame", 0xffffffff, "BORDER",
                    "TOPLEFT", element, "TOPLEFT", 29, 12,
                    ContentRect:new(nameFrameMin.X,nameFrameMin.Y,
                        nameFrameMin.X + QuestFrameUiLaw.ItemNameFrameRect.Width * s,
                        nameFrameMin.Y + QuestFrameUiLaw.ItemNameFrameRect.Height * s),
                    ClipRect:clip,
                    ClipMask:scrollName, BlendMode:"BLEND", Strata:"DIALOG"));
            string fontObject = "GameFontHighlight";
            CollectUiParityDraw(element + "/Name", "FontString", nameMin,
                new(GameText.MeasureWidth(fontObject,name,s),GameText.EmPixels(fontObject,s)),
                element, new("", item is null ? 0xffffffff :
                        ImGui.ColorConvertFloat4ToU32(ItemQualityColor(item.Quality)), "OVERLAY",
                    "LEFT", element, "LEFT", 44, -12, FontObjectLaw.Get(fontObject).Face,
                    FontObjectLaw.Get(fontObject).Height, ClipRect:clip,
                    ClipMask:scrollName, Strata:"DIALOG"));
            if (row.Count > 1)
            {
                string countText = row.Count.ToString();
                Vector2 countSize = new(GameText.MeasureWidth("NumberFontNormal",countText,s),
                    GameText.EmPixels("NumberFontNormal",s));
                CollectUiParityDraw(element + "/Count", "FontString",
                    QuestFrameUiLaw.ItemCountMin(min, countSize, s), countSize,
                    element, new("", FontObjectLaw.Get("NumberFontNormal").Color, "ARTWORK",
                        "BOTTOMRIGHT", element+"/IconTexture", "BOTTOMRIGHT", -4, 1,
                        FontObjectLaw.Get("NumberFontNormal").Face,
                        FontObjectLaw.Get("NumberFontNormal").Height, ClipRect:clip,
                        ClipMask:scrollName, Strata:"DIALOG"));
            }
            else ClassifyUiParity(element + "/Count", "FontString", element, "NOT-DRAWN",
                "item-count-is-one");
        }
        if (hovered)
        {
            QuestNpcPanel tooltipPanel = QuestNpcPanelNow();
            GameTooltipOwnerKey tooltipOwner = QuestItemGameTooltipOwner(
                tooltipPanel, kind, index);
            QuestTooltipSeat tooltipSeat =
                QuestFrameUiLaw.ItemTooltipSeat(min, itemSize);
            if (item is not null)
            {
                ItemTooltipBodySnapshot tooltipBody =
                    PrepareItemTooltipBodySnapshot(item, row.Count);
                Vector2? tooltipAnchor = tooltipPanel == QuestNpcPanel.None
                    ? null : tooltipSeat.Anchor;
                Vector2? tooltipPivot = tooltipPanel == QuestNpcPanel.None
                    ? null : tooltipSeat.Pivot;
                OfferPreparedItemTooltip(tooltipOwner, tooltipBody, tooltipAnchor,
                    nextWindowPivot: tooltipPivot);
            }
            else
            {
                string retrievingItemInfo = QuestGlobalString("RETRIEVING_ITEM_INFO");
                OfferPreservedSharedGameTooltipRenderer(tooltipOwner, () =>
                {
                    if (tooltipPanel != QuestNpcPanel.None)
                        ImGui.SetNextWindowPos(tooltipSeat.Anchor, ImGuiCond.Always,
                            tooltipSeat.Pivot);
                    ImGui.BeginTooltip();
                    ImGui.TextUnformatted(retrievingItemInfo);
                    ImGui.EndTooltip();
                });
            }
        }
        string itemLink = item is null
            ? ""
            : QuestFrameUiLaw.ItemLink(row.ItemId, item.Name, item.Quality);
        switch (QuestFrameUiLaw.ItemClickAction(clicked, ImGui.GetIO().KeyCtrl,
                    ImGui.GetIO().KeyShift, _chatEditOpen, selectable,
                    itemLink.Length > 0))
        {
            case QuestItemClickAction.DressUp:
                TryOnDressUp(row.ItemId);
                break;
            case QuestItemClickAction.InsertChat:
                InsertChatText(itemLink);
                break;
            case QuestItemClickAction.Select:
                _questRewardChoice = index;
                break;
        }
        if (selectable && _questRewardChoice == index)
        {
            uint glow = _gameplayArt!.AdditiveHandle(@"Interface\QuestFrame\UI-QuestItemHighlight");
            Vector2 glowMin = QuestFrameUiLaw.ItemHighlightRect.ScaledMin(min, s);
            Vector2 glowSize = QuestFrameUiLaw.ItemHighlightRect.ScaledSize(s);
            if (glow != 0) dl.AddImage((nint)glow, glowMin, glowMin + glowSize);
            if (_uiParityArmed && _uiParityPanel == "quest-frame")
                CollectUiParityDraw("BenillaQuestRewardChoiceHighlight",
                    "HighlightTexture", glowMin, glowSize,
                    QuestItemElementName(kind,index),
                    new(@"Interface\QuestFrame\UI-QuestItemHighlight",0xffffffff,"HIGHLIGHT",
                        "CENTER",QuestItemElementName(kind,index),"CENTER",0,0,
                        ClipRect:new(_uiParityOrigin.X+QuestFrameUiLaw.ScrollX*s,
                            _uiParityOrigin.Y+QuestFrameUiLaw.ScrollY*s,
                            _uiParityOrigin.X+(QuestFrameUiLaw.ScrollX+QuestFrameUiLaw.ScrollWidth)*s,
                            _uiParityOrigin.Y+(QuestFrameUiLaw.ScrollY+QuestFrameUiLaw.ScrollHeight)*s),
                        ClipMask:QuestPanelStem(QuestNpcPanelNow())+"Scroll",
                        BlendMode:"ADD",Strata:"DIALOG"));
        }
    }

    private string QuestItemElementName(string kind, int index)
    {
        string prefix = QuestNpcPanelNow() switch
        {
            QuestNpcPanel.Detail => "BenillaQuestDetail",
            QuestNpcPanel.Progress => "BenillaQuestProgress",
            QuestNpcPanel.Reward => "BenillaQuestReward",
            _ => "BenillaQuest",
        };
        string suffix = kind switch
        {
            "choice" => "Choice",
            "required" => "Required",
            _ => "Reward",
        };
        return $"{prefix}{suffix}{index + 1}";
    }

    private static GameTooltipOwnerKey QuestItemGameTooltipOwner(
        QuestNpcPanel panel, string kind, int index)
    {
        // The owner is the authored panel/kind widget at a fixed zero-based grid index. Quest,
        // item, and giver identifiers are payload and must never participate in ownership.
        _ = (panel, kind) switch
        {
            (QuestNpcPanel.Detail, "choice") => true,
            (QuestNpcPanel.Detail, "reward") => true,
            (QuestNpcPanel.Progress, "required") => true,
            (QuestNpcPanel.Reward, "choice") => true,
            (QuestNpcPanel.Reward, "reward") => true,
            (QuestNpcPanel.None, "log-choice") => true,
            (QuestNpcPanel.None, "log-reward") => true,
            _ => throw new InvalidOperationException(
                $"Unsupported quest tooltip surface: panel={panel};kind={kind}"),
        };
        return new GameTooltipOwnerKey($"item:quest:{panel}:{kind}", (ulong)index);
    }

    private void DrawQuestMoney(ImDrawListPtr dl, Vector2 min, uint copper, float s,
        string elementPrefix)
    {
        float x = min.X;
        IReadOnlyList<QuestCoin> coins = QuestFrameUiLaw.Money(copper);
        for (int slot = 0; slot < coins.Count; slot++)
        {
            QuestCoin coin = coins[slot];
            string text = coin.Value.ToString();
            float numberWidth=GameText.MeasureWidth("NumberFontNormal", text, s);
            QuestMoneyCoinSeat seat = QuestFrameUiLaw.MoneyCoinSeat(
                min, x, numberWidth, s);
            GameText.Draw(dl, "NumberFontNormal", text, seat.NumberMin, s);
            DrawMailCoin(dl, coin.Denomination, seat.IconMin, s);
            if(_uiParityArmed&&_uiParityPanel=="quest-frame")
            {
                string element=$"{elementPrefix}Coin{slot+1}";
                string panelStem = QuestPanelStem(QuestNpcPanelNow());
                string scrollName = panelStem + "Scroll";
                string scrollChild = scrollName + "Child";
                bool progressMoney = QuestNpcPanelNow() == QuestNpcPanel.Progress;
                string relativeTo = slot == 0
                    ? progressMoney ? "BenillaQuestProgressRequiredMoneyText"
                        : panelStem + "ItemReceiveText"
                    : $"{elementPrefix}Coin{slot}";
                float offset = QuestFrameUiLaw.MoneyAnchorOffset(slot, progressMoney);
                Vector4 clip=new(_uiParityOrigin.X+QuestFrameUiLaw.ScrollX*s,
                    _uiParityOrigin.Y+QuestFrameUiLaw.ScrollY*s,
                    _uiParityOrigin.X+(QuestFrameUiLaw.ScrollX+QuestFrameUiLaw.ScrollWidth)*s,
                    _uiParityOrigin.Y+(QuestFrameUiLaw.ScrollY+QuestFrameUiLaw.ScrollHeight)*s);
                CollectUiParityDraw(element,"Frame",seat.NumberMin,seat.FrameSize,
                    scrollChild,new("",0,"FRAMES","LEFT",relativeTo,
                        "RIGHT",offset,0,ClipRect:clip,ClipMask:scrollName,
                        Strata:"DIALOG"));
                CollectUiParityDraw(element+"/Num","FontString",seat.NumberMin,
                    seat.NumberSize,element,
                    new("",FontObjectLaw.Get("NumberFontNormal").Color,"OVERLAY","RIGHT",
                        element,"RIGHT",-13,0,FontObjectLaw.Get("NumberFontNormal").Face,
                        FontObjectLaw.Get("NumberFontNormal").Height,ClipRect:clip,
                        ClipMask:scrollName,Strata:"DIALOG"));
                CollectUiParityDraw(element+"/Icon","Texture",seat.IconMin,seat.IconSize,element,
                    new(@"Interface\MoneyFrame\UI-MoneyIcons",0xffffffff,"OVERLAY","RIGHT",
                        element,"RIGHT",0,0,TexCoords:coin.Denomination switch
                        {0=>"0|0|0.25|1",1=>"0.25|0|0.5|1",_=>"0.5|0|0.75|1"},
                        ClipRect:clip,ClipMask:scrollName,BlendMode:"BLEND",
                        Strata:"DIALOG"));
            }
            x = seat.NextX;
        }
        if(_uiParityArmed&&_uiParityPanel=="quest-frame")
            for(int hidden=coins.Count;hidden<3;hidden++)
                ClassifyUiParity($"{elementPrefix}Coin{hidden+1}","Frame",
                    QuestPanelStem(QuestNpcPanelNow())+"ScrollChild","NOT-DRAWN",
                    "zero-denomination-omitted");
    }

    private void DrawQuestNpcScrollbar(ImDrawListPtr dl, Vector2 origin, float s, float contentHeight)
    {
        string panelStem = QuestPanelStem(QuestNpcPanelNow());
        string panelName = panelStem + "Panel";
        string scrollName = panelStem + "Scroll";
        string barName = scrollName + "Bar";
        float range = Math.Max(0, contentHeight - QuestFrameUiLaw.ScrollHeight);
        if (range <= 0)
        {
            if (_uiParityArmed && _uiParityPanel == "quest-frame")
            {
                ClassifyUiParity(barName, "Frame", panelName, "NOT-DRAWN",
                    "quest-content-does-not-overflow");
                ClassifyUiParity(barName + "Up", "Button", barName, "NOT-DRAWN",
                    "quest-content-does-not-overflow");
                ClassifyUiParity(barName + "Down", "Button", barName, "NOT-DRAWN",
                    "quest-content-does-not-overflow");
                ClassifyUiParity(barName + "Track", "Frame", barName, "NOT-DRAWN",
                    "quest-content-does-not-overflow");
                ClassifyUiParity(barName + "Thumb", "Texture", barName + "Track",
                    "NOT-DRAWN", "quest-content-does-not-overflow");
            }
            return;
        }
        Vector2 bar = QuestFrameUiLaw.NpcScrollBarRect.ScaledMin(origin, s);
        Vector4 clip = new(origin.X, origin.Y, origin.X + 384 * s, origin.Y + 512 * s);
        if (_uiParityArmed && _uiParityPanel == "quest-frame")
            CollectUiParityDraw(barName, "Frame", bar,
                QuestFrameUiLaw.NpcScrollBarRect.ScaledSize(s), panelName,
                new("",0,"FRAMES","TOPLEFT",scrollName,"TOPRIGHT",6,0,
                    ContentRect:new(bar.X, bar.Y,
                        bar.X + QuestFrameUiLaw.NpcScrollBarRect.Width * s,
                        bar.Y + QuestFrameUiLaw.NpcScrollBarRect.Height * s), ClipRect:clip,
                    ClipMask:panelName,Strata:"DIALOG"));
        bool up = _questNpcScroll > 0, down = _questNpcScroll < range;
        if (DrawQuestScrollButton(dl, "##quest-scroll-up", barName + "Up", bar, up,
                upward: true, s: s))
            _questNpcScroll = QuestFrameUiLaw.ClampScroll(
                _questNpcScroll - QuestFrameUiLaw.ScrollStep, contentHeight);
        if (DrawQuestScrollButton(dl, "##quest-scroll-down", barName + "Down",
                QuestFrameUiLaw.NpcScrollDownRect.ScaledMin(origin, s),
                down, upward: false, s: s))
            _questNpcScroll = QuestFrameUiLaw.ClampScroll(
                _questNpcScroll + QuestFrameUiLaw.ScrollStep, contentHeight);
        Vector2 trackMin = QuestFrameUiLaw.NpcScrollTrackRect.ScaledMin(origin, s);
        if (_uiParityArmed && _uiParityPanel == "quest-frame")
            CollectUiParityDraw(barName+"Track","Frame",trackMin,
                QuestFrameUiLaw.NpcScrollTrackRect.ScaledSize(s),
                barName,new("",0,"FRAMES","TOPLEFT",barName+"Up","BOTTOMLEFT",0,0,
                    ContentRect:new(trackMin.X, trackMin.Y,
                        trackMin.X + QuestFrameUiLaw.NpcScrollTrackRect.Width * s,
                        trackMin.Y + QuestFrameUiLaw.NpcScrollTrackRect.Height * s),
                    ClipRect:clip,ClipMask:barName,Strata:"DIALOG"));
        uint knob = _gameplayArt!.Handle(@"Interface\Buttons\UI-ScrollBar-Knob");
        if (knob != 0)
        {
            QuestLogicalRect thumbRect = QuestFrameUiLaw.NpcScrollThumbRect(
                _questNpcScroll, contentHeight);
            Vector2 at = thumbRect.ScaledMin(origin, s);
            dl.AddImage((nint)knob, at, at + thumbRect.ScaledSize(s), new(.25f), new(.75f));
            if (_uiParityArmed && _uiParityPanel == "quest-frame")
                CollectUiParityDraw(barName+"Thumb", "Texture", at,
                    thumbRect.ScaledSize(s), barName+"Track",
                    new(@"Interface\Buttons\UI-ScrollBar-Knob", 0xffffffff, "ARTWORK",
                        "TOPLEFT", barName+"Track", "TOPLEFT", 0,
                        -QuestFrameUiLaw.ScrollThumbY(_questNpcScroll, contentHeight),
                        TexCoords:"0.25|0.25|0.75|0.75",
                        ContentRect:new(at.X,at.Y,at.X+16*s,at.Y+16*s),
                        ClipRect:clip, ClipMask:barName, BlendMode:"BLEND", Strata:"DIALOG"));
        }
    }

    private bool DrawQuestScrollButton(ImDrawListPtr dl, string id, string element, Vector2 min,
        bool enabled, bool upward, float s)
    {
        Vector2 size = QuestFrameUiLaw.NpcScrollUpRect.ScaledSize(s);
        ImGui.SetCursorScreenPos(min);
        ImGui.InvisibleButton(id, size);
        bool hovered = enabled && ImGui.IsItemHovered();
        bool held = hovered && ImGui.IsItemActive() && ImGui.IsMouseDown(ImGuiMouseButton.Left);
        string direction = upward ? "Up" : "Down";
        string state = !enabled ? "Disabled" : held ? "Down" : "Up";
        string path = $@"Interface\Buttons\UI-ScrollBar-Scroll{direction}Button-{state}";
        uint texture = _gameplayArt!.Handle(path);
        if (texture != 0)
            dl.AddImage((nint)texture, min, min + size, new(.25f), new(.75f));
        if (hovered)
        {
            uint highlight = _gameplayArt.AdditiveHandle(
                $@"Interface\Buttons\UI-ScrollBar-Scroll{direction}Button-Highlight");
            if (highlight != 0)
                dl.AddImage((nint)highlight, min, min + size, new(.25f), new(.75f));
        }
        if (_uiParityArmed && _uiParityPanel == "quest-frame")
        {
            Vector4 clip = new(_uiParityOrigin.X, _uiParityOrigin.Y,
                _uiParityOrigin.X + 384 * s, _uiParityOrigin.Y + 512 * s);
            string barName = QuestPanelStem(QuestNpcPanelNow()) + "ScrollBar";
            CollectUiParityDraw(element, "Button", min, size, barName,
                new("", 0, "FRAMES", upward?"TOPLEFT":"BOTTOMLEFT", barName,
                    upward?"TOPLEFT":"BOTTOMLEFT", 0, 0,
                    ContentRect:new(min.X,min.Y,min.X+size.X,min.Y+size.Y),
                    ClipRect:clip, ClipMask:barName, Visible:true, Enabled:enabled,
                    InteractionState:!enabled?"disabled":held?"pressed":hovered?"hovered":"normal",
                    HitMin:min, HitMax:min+size, Strata:"DIALOG"));
            string stateType = state switch
            {
                "Down" => "PushedTexture",
                "Disabled" => "DisabledTexture",
                _ => "NormalTexture",
            };
            CollectUiParityDraw(element + "/" + stateType, stateType, min,
                size, element, new(path, 0xffffffff, "ARTWORK", "TOPLEFT", element,
                    "TOPLEFT", 0, 0, TexCoords:"0.25|0.25|0.75|0.75", ClipRect:clip,
                    ClipMask:element, BlendMode:"BLEND", Strata:"DIALOG"));
            foreach (string hiddenType in new[] { "NormalTexture", "PushedTexture", "DisabledTexture" })
                if (!hiddenType.Equals(stateType, StringComparison.Ordinal))
                    ClassifyUiParity(element+"/"+hiddenType,hiddenType,element,"NOT-DRAWN",
                        $"scroll-button-state-is-{(!enabled?"disabled":held?"pressed":"normal")}");
            if (hovered)
                CollectUiParityDraw(element + "/HighlightTexture", "HighlightTexture", min,
                    size, element, new($@"Interface\Buttons\UI-ScrollBar-Scroll{direction}Button-Highlight",
                        0xffffffff, "HIGHLIGHT", "TOPLEFT", element, "TOPLEFT", 0, 0,
                        TexCoords:"0.25|0.25|0.75|0.75", ClipRect:clip, ClipMask:element,
                        BlendMode:"ADD", Strata:"DIALOG"));
            else
                ClassifyUiParity(element + "/HighlightTexture", "HighlightTexture", element,
                    "NOT-DRAWN", enabled ? "button-is-not-hovered" : "button-is-disabled");
        }
        return enabled && ImGui.IsItemClicked(ImGuiMouseButton.Left);
    }
}

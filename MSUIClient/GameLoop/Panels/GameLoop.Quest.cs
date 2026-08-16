using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private QuestList? _questList;
    private QuestDetails? _questDetails;
    private QuestOffer? _questOffer;
    private QuestRequestItems? _questRequestItems;
    private bool _questLogOpen;
    private int _questLogSelected;
    private readonly Dictionary<uint, string> _questProgress = [];
    private readonly Dictionary<uint, string> _questTitles = [];
    private readonly HashSet<uint> _questQueries = [];
    private readonly Dictionary<ulong, uint> _questStatuses = [];
    private readonly Dictionary<uint, uint> _questWorldStates = [];
    private HashSet<uint> _questLogSnapshot = [];
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
        else if (_controller is not null &&
            _entities.TryGet(guid, out WorldEntity npc) && npc.IsCreature && !npc.IsDead &&
            (npc.NpcFlags & NpcQuestGiver) != 0)
        {
            float distance = Vector3.Distance(_controller.Position, npc.Position);
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
    private bool RequestQuestHello(ulong guid) => QuestGate(guid, "hello", n => n.QuestgiverHello(guid));
    private bool RequestQuestDetails(ulong guid, uint questId) => QuestGate(guid, "details", n => n.QuestgiverQuery(guid, questId));

    private bool AcceptQuest()
    {
        if (_questDetails is null) return false;
        ulong giver = _questDetails.GiverGuid;
        uint quest = _questDetails.QuestId;
        SnapshotQuestEconomy();
        bool sent = QuestGate(giver, "accept", n => n.QuestgiverAccept(giver, quest));
        // Build 5875 closes on the click itself; it does not wait for an accept response.
        CloseQuestNpcFrame(playSound: true);
        return sent;
    }

    private bool RequestQuestCompletion(ulong guid, uint id) => id != 0 &&
        QuestGate(guid, "complete-request", n => n.QuestgiverComplete(guid, id));

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
        return id != 0 && QuestGate(guid, "reward-request", n => n.QuestgiverRequestReward(guid, id));
    }

    private bool ChooseQuestReward(uint choice)
    {
        if (_questOffer is null || choice >= Math.Max(1, _questOffer.ChoiceRewards.Count)) return false;
        SnapshotQuestEconomy();
        return QuestGate(_questOffer.GiverGuid, "reward-choice",
            n => n.QuestgiverChooseReward(_questOffer.GiverGuid, _questOffer.QuestId, choice));
    }

    private bool AbandonQuest(uint questId)
    {
        if (_net is null || !_entities.TryGet(_net.PlayerGuid, out WorldEntity player)) return false;
        var found = player.Fields.QuestLog().FirstOrDefault(q => q.QuestId == questId);
        bool present = found.QuestId != 0;
        bool sent = present && _net.QuestLogRemove(found.Slot);
        EmitInterface("quest", "abandon", sent ? "SENT" : "REFUSED_NOT_IN_LOG", _net.PlayerGuid,
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
        if (_gossipMenu is not null || _gossipText is not null) ResetGossip();
        CloseVendorSession();
        _trainer = null;
        _gameObjectGuid = 0;
        _gameObjectPages.Clear();
        _worldMapOpen = false;
        _macroOpen = false;
        _helpOpen = false;
        _socialOpen = false;
        _guildOpen = false;
        _professionOpen = false;
        _bankOpen = false;
        _tabardOpen = false;
        if (_taxiOpen && !_taxiLocked) _taxiOpen = false;
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
        EmitInterface("quest", "objective-kill", "UPDATED", value.Guid,
            $"quest={value.QuestId};entry={value.Entry};current={value.Current};required={value.Required}");
    }

    private void ApplyQuestItem(byte[] body)
    {
        var value = QuestPackets.ParseItem(body);
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
        EmitInterface("quest", "objective", "COMPLETE", 0, $"quest={id}");
    }

    private void ApplyQuestComplete(byte[] body)
    {
        QuestComplete value = QuestPackets.ParseComplete(body);
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

    private void CloseQuestNpcFrame(bool playSound)
    {
        bool wasOpen = QuestNpcPanelNow() != QuestNpcPanel.None;
        _questList = null; _questDetails = null; _questRequestItems = null; _questOffer = null;
        _questNpcScroll = 0; _questRewardChoice = -1;
        _questNpcContentHeight = QuestFrameUiLaw.ScrollHeight;
        if (wasOpen && playSound) PlayUiSound("igQuestListClose");
    }

    private void ResetQuestSession(bool clearStatusStore)
    {
        CloseQuestNpcFrame(playSound: false);
        _questLogOpen = false;
        _questLogSelected = 0;
        _questLogSnapshot.Clear();
        _questQueries.Clear();
        _questWorldStates.Clear();
        ClearRtsTerritoryCapture();
        if (clearStatusStore) _questStatuses.Clear();
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
        else if (_controller is null || !_entities.TryGet(giver, out WorldEntity npc) || !npc.IsCreature)
            reason = "giver-despawned";
        else if (npc.IsDead)
            reason = "giver-dead";
        else
        {
            float distance = Vector3.Distance(_controller.Position, npc.Position);
            if (distance > GossipInteractDistance) reason = $"range={distance:R};limit={GossipInteractDistance:R}";
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
        _hearthOpen || _tabardOpen || _loot.IsOpen || _gameObjectPages.Count > 0;

    private string ExpandQuestText(string text)
    {
        QuestTextMacroLaw.Subject? subject = null;
        if (_net is not null && _entities.TryGet(_net.PlayerGuid, out WorldEntity player))
        {
            var bytes = player.Fields.Bytes0;
            subject = new QuestTextMacroLaw.Subject(_net.PlayerName, RaceName(bytes.Race),
                ClassName(bytes.Class), bytes.Gender);
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
        foreach (uint id in now)
            if (!_questTitles.ContainsKey(id) && _questQueries.Add(id)) _net.QuestQuery(id);
        foreach (uint id in now.Except(_questLogSnapshot)) EmitInterface("quest", "log", "ADDED", _net.PlayerGuid, $"quest={id};count={now.Count}");
        foreach (uint id in _questLogSnapshot.Except(now)) EmitInterface("quest", "log", "REMOVED", _net.PlayerGuid, $"quest={id};count={now.Count}");
        _questLogSnapshot = now;
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
        // Build 5875: fixed fields/rewards/map point occupy 152 bytes, followed
        // by Title, Objectives, Details, EndText and the objective records.
        if (body.Length < 153) return;
        var r = new PacketReader(body);
        uint id = r.ReadU32();
        r.Skip(148);
        string title = r.ReadCString();
        if (id != 0 && title.Length > 0) _questTitles[id] = title;
        _questQueries.Remove(id);
        EmitInterface("quest", "query", "DECODED", 0,
            $"quest={id};title={SanitizeEvidence(title)}");
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
        float s=GameplayUiScale(); Vector2 origin=new(0,104*s), logicalSize=new(384,512);
        ImGui.SetNextWindowPos(origin,ImGuiCond.Always); ImGui.SetNextWindowSize(logicalSize*s,ImGuiCond.Always); ImGui.SetNextWindowBgAlpha(0);
        if (!ImGui.Begin("##quest", ImGuiWindowFlags.NoDecoration|ImGuiWindowFlags.NoMove|ImGuiWindowFlags.NoSavedSettings|ImGuiWindowFlags.NoBackground|ImGuiWindowFlags.NoNav)) { ImGui.End(); return; }
        ImDrawListPtr dl=ImGui.GetWindowDrawList();
        bool parityProof = _uiParityArmed && _uiParityPanel is "quest-log" or "quest-frame";
        bool logMode = _questLogOpen;
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
            Vector2 portraitMin = origin + new Vector2(7, 6) * s;
            DrawUnitPortraitImage(dl, giver, portraitMin, 60 * s, 0, false);
            if (parityProof) CollectUiParityDraw("BenillaQuestFramePortrait", "Texture", portraitMin,
                new Vector2(60) * s, frameName,
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
        string stem = logMode ? "UI-QuestLog" : "UI-QuestGreeting";
        var art = new List<(string Element,string Path,Vector2 Offset,Vector2 Size)>();
        if (logMode) art.Add(("QuestLogFrame/Texture",@"Interface\QuestFrame\UI-QuestLog-BookIcon",new(4,4),new(64,64)));
        art.AddRange([
            (logMode?"QuestLogFrame/Texture#2":npcPanelName+"/Texture",$@"Interface\QuestFrame\{stem}-TopLeft",Vector2.Zero,new(256,256)),
            (logMode?"QuestLogFrame/Texture#3":npcPanelName+"/Texture#2",$@"Interface\QuestFrame\{stem}-TopRight",new(256,0),new(128,256)),
            (logMode?"QuestLogFrame/Texture#4":npcPanelName+"/Texture#3",$@"Interface\QuestFrame\{stem}-BotLeft",new(0,256),new(256,256)),
            (logMode?"QuestLogFrame/Texture#5":npcPanelName+"/Texture#4",$@"Interface\QuestFrame\{stem}-BotRight",new(256,256),new(128,256))]);
        if (!logMode && npcPanel == QuestNpcPanel.Greeting)
            art.Add((npcPanelName+"/Texture#5",
                @"Interface\QuestFrame\UI-Quest-BotLeftPatch", new(22, 380), new(128, 64)));
        foreach(var r in art){Vector2 m=origin+r.Offset*s;DrawArt(dl,r.Path,m,r.Size,s);if(parityProof)CollectUiParityDraw(r.Element,"Texture",m,r.Size*s,logMode?frameName:npcPanelName,new(r.Path,0xffffffff,r.Path.EndsWith("UI-Quest-BotLeftPatch",StringComparison.OrdinalIgnoreCase)?"ARTWORK":"BACKGROUND","TOPLEFT",logMode?frameName:npcPanelName,"TOPLEFT",r.Offset.X,-r.Offset.Y,ContentRect:new(m.X,m.Y,m.X+r.Size.X*s,m.Y+r.Size.Y*s),ClipRect:new(origin.X,origin.Y,origin.X+logicalSize.X*s,origin.Y+logicalSize.Y*s),ClipMask:logMode?frameName:npcPanelName,BlendMode:"BLEND",Strata:"DIALOG"));}
        if (!logMode)
        {
            ulong guid = QuestGiverGuid();
            string name = GuidInfo.IsItem(guid) ? "" : _entities.TryGet(guid, out WorldEntity namedGiver)
                ? _creatureNames.GetValueOrDefault(namedGiver.Entry, "") : "";
            Vector2 nameCenter = origin + new Vector2(192, 30) * s;
            GameText.DrawCentered(dl, "GameFontHighlight", name, nameCenter, s);
            if (parityProof)
            {
                Vector2 nameFrameMin = origin + new Vector2(42, 16) * s;
                CollectUiParityDraw("BenillaQuestNpcNameFrame", "Frame", nameFrameMin,
                    new Vector2(300, 14) * s, frameName,
                    new("", 0, "FRAMES", "TOP", frameName, "TOP", 0, -23,
                        ClipRect:new(origin.X,origin.Y,origin.X+384*s,origin.Y+512*s),
                        ClipMask:frameName, Strata:"DIALOG"));
                CollectUiParityDraw("BenillaQuestNpcNameText", "FontString",
                    origin + new Vector2(74.5f, 20) * s, new Vector2(235, 20) * s,
                    "BenillaQuestNpcNameFrame", new("", FontObjectLaw.Get("GameFontHighlight").Color,
                        "BACKGROUND", "CENTER", "BenillaQuestNpcNameFrame", "CENTER", 0, 0,
                        FontObjectLaw.Get("GameFontHighlight").Face,
                        FontObjectLaw.Get("GameFontHighlight").Height,
                        ClipRect:new(origin.X,origin.Y,origin.X+384*s,origin.Y+512*s),
                        ClipMask:"BenillaQuestNpcNameFrame", Strata:"DIALOG"));
            }
        }
        if (_questLogOpen && _net is not null && _entities.TryGet(_net.PlayerGuid, out WorldEntity player))
            DrawQuestLogContent(dl, origin, s, player);
        else
            DrawQuestNpcContent(dl, origin, s);
        Vector2 close=origin+(logMode ? new Vector2(322,8) : QuestFrameUiLaw.CloseMin)*s;
        DrawImageButton(dl,"##quest-close",close,new Vector2(32)*s,
            @"Interface\Buttons\UI-Panel-MinimizeButton-Up",@"Interface\Buttons\UI-Panel-MinimizeButton-Down",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Highlight");
        bool closeHovered = ImGui.IsItemHovered();
        bool closeActive = ImGui.IsItemActive();
        bool closeClicked = ImGui.IsItemClicked(ImGuiMouseButton.Left);
        if (parityProof)
        {
            string closeElement = logMode ? "QuestLogFrameCloseButton" : "BenillaQuestCloseButton";
            Vector2 closeSize = new Vector2(32) * s;
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
        var quests = player.Fields.QuestLog().ToArray();
        _questLogSelected = Math.Clamp(_questLogSelected, 0, Math.Max(0, quests.Length - 1));
        DrawCenteredText(dl, origin + new Vector2(192, 22) * s, "Quest Log", 12f * s, 0xffffffff);
        dl.AddText(ImGui.GetFont(), 10f * s, origin + new Vector2(267, 45) * s,
            VanillaGold, $"{quests.Length} / 20");
        for (int i = 0; i < quests.Length && i < 6; i++)
        {
            var quest = quests[i];
            string title = _questTitles.GetValueOrDefault(quest.QuestId, $"Quest {quest.QuestId}");
            Vector2 min = origin + new Vector2(19, 75 + i * 15) * s;
            if (VanillaListRow(dl, $"##quest-log-{quest.QuestId}", min, new Vector2(300, 16), s,
                    title, _questLogSelected == i, VanillaGold)) _questLogSelected = i;
        }
        if (quests.Length > 0)
        {
            uint selected = quests[_questLogSelected].QuestId;
            string title = _questTitles.GetValueOrDefault(selected, $"Quest {selected}");
            dl.AddText(ImGui.GetFont(), 14f * s, origin + new Vector2(24, 180) * s,
                0xff202020, title);
            dl.AddText(ImGui.GetFont(), 12f * s, origin + new Vector2(24, 207) * s,
                0xff202020, "Objectives");
            DrawWrappedText(dl, _questProgress.GetValueOrDefault(selected,
                    "Objectives are updated from the server as you progress."),
                origin + new Vector2(24, 228) * s, 285, 10f * s, s, 0xff202020, 8);
            if (VanillaButton(dl, "##quest-abandon", "Abandon Quest",
                    origin + new Vector2(17, 437) * s, new Vector2(125, 21), s))
                AbandonQuest(selected);
        }
        else VanillaButton(dl, "##quest-abandon", "Abandon Quest",
            origin + new Vector2(17, 437) * s, new Vector2(125, 21), s, false);
        VanillaButton(dl, "##quest-push", "Share Quest", origin + new Vector2(141, 437) * s,
            new Vector2(123, 21), s, false);
        if (VanillaButton(dl, "##quest-exit", "Close", origin + new Vector2(264, 437) * s,
                new Vector2(77, 21), s)) _questLogOpen = false;
    }

    private void DrawQuestNpcContent(ImDrawListPtr dl, Vector2 origin, float s)
    {
        QuestNpcPanel panel = QuestNpcPanelNow();
        if (panel == QuestNpcPanel.None) return;
        string panelStem = QuestPanelStem(panel);
        string panelName = panelStem + "Panel";
        string scrollName = panelStem + "Scroll";
        string scrollChildName = scrollName + "Child";
        Vector2 scrollMin = origin + new Vector2(QuestFrameUiLaw.ScrollX, QuestFrameUiLaw.ScrollY) * s;
        Vector2 scrollSize = new(QuestFrameUiLaw.ScrollWidth * s, QuestFrameUiLaw.ScrollHeight * s);
        if (_uiParityArmed && _uiParityPanel == "quest-frame")
            CollectUiParityDraw(scrollName, "ScrollFrame", scrollMin, scrollSize,
                panelName, new("", 0, "IMGUI_CLIP", "TOPLEFT", panelName, "TOPLEFT",
                    QuestFrameUiLaw.ScrollX, -QuestFrameUiLaw.ScrollY,
                    ContentRect:new(scrollMin.X,scrollMin.Y,scrollMin.X+scrollSize.X,
                        scrollMin.Y+scrollSize.Y),
                    ClipRect:new(scrollMin.X,scrollMin.Y,scrollMin.X+scrollSize.X,
                        scrollMin.Y+scrollSize.Y),ClipMask:scrollName,Strata:"DIALOG"));
        _questNpcScroll = QuestFrameUiLaw.ClampScroll(_questNpcScroll, _questNpcContentHeight);
        if (ImGui.IsMouseHoveringRect(scrollMin, scrollMin + scrollSize, false) && ImGui.GetIO().MouseWheel != 0)
        {
            _questNpcScroll -= ImGui.GetIO().MouseWheel * QuestFrameUiLaw.ScrollStep;
            _questNpcScroll = QuestFrameUiLaw.ClampScroll(_questNpcScroll, _questNpcContentHeight);
        }

        dl.PushClipRect(scrollMin, scrollMin + scrollSize, true);
        Vector2 content = scrollMin - new Vector2(0, _questNpcScroll * s);
        float contentHeight = panel switch
        {
            QuestNpcPanel.Greeting => DrawQuestGreetingContent(dl, content, s),
            QuestNpcPanel.Detail => DrawQuestDetailContent(dl, content, s),
            QuestNpcPanel.Progress => DrawQuestProgressContent(dl, content, s),
            QuestNpcPanel.Reward => DrawQuestRewardContent(dl, content, s),
            _ => 0,
        };
        dl.PopClipRect();
        if (_uiParityArmed && _uiParityPanel == "quest-frame")
            CollectUiParityDraw(scrollChildName, "Frame", content,
                new Vector2(QuestFrameUiLaw.ScrollWidth, Math.Max(contentHeight,
                    QuestFrameUiLaw.ScrollHeight)) * s, scrollName,
                new("",0,"FRAMES","TOPLEFT",scrollName,"TOPLEFT",0,_questNpcScroll,
                    ContentRect:new(content.X,content.Y,
                        content.X+QuestFrameUiLaw.ScrollWidth*s,
                        content.Y+Math.Max(contentHeight,QuestFrameUiLaw.ScrollHeight)*s),
                    ClipRect:new(scrollMin.X,scrollMin.Y,scrollMin.X+scrollSize.X,
                        scrollMin.Y+scrollSize.Y),ClipMask:scrollName,Strata:"DIALOG"));
        _questNpcScroll = QuestFrameUiLaw.ClampScroll(_questNpcScroll, contentHeight);
        _questNpcContentHeight = contentHeight;
        DrawQuestNpcScrollbar(dl, origin, s, contentHeight);

        switch (panel)
        {
            case QuestNpcPanel.Greeting:
                if (QuestPanelButton(dl, "BenillaQuestGreetingGoodbyeButton", "##quest-goodbye",
                        "Goodbye", origin + new Vector2(267, 417) * s, new Vector2(78, 22), s))
                    CloseQuestNpcFrame(playSound: true);
                break;
            case QuestNpcPanel.Detail:
                if (QuestPanelButton(dl, "BenillaQuestAcceptButton", "##quest-accept", "Accept",
                        origin + new Vector2(23, 418) * s, new Vector2(77, 22), s)) AcceptQuest();
                if (QuestPanelButton(dl, "BenillaQuestDeclineButton", "##quest-decline", "Decline",
                        origin + new Vector2(267, 418) * s, new Vector2(78, 22), s))
                { PlayUiSound("igQuestCancel"); CloseQuestNpcFrame(playSound: true); }
                break;
            case QuestNpcPanel.Progress:
                if (QuestPanelButton(dl, "BenillaQuestProgressContinueButton", "##quest-continue",
                        "Continue", origin + new Vector2(22, 418) * s, new Vector2(120, 22), s,
                        _questRequestItems?.Completable == true))
                    RequestQuestReward();
                if (QuestPanelButton(dl, "BenillaQuestProgressGoodbyeButton", "##quest-progress-goodbye",
                        "Goodbye", origin + new Vector2(267, 417) * s, new Vector2(78, 22), s))
                { PlayUiSound("igQuestCancel"); CloseQuestNpcFrame(playSound: true); }
                break;
            case QuestNpcPanel.Reward:
                int choices = _questOffer?.ChoiceRewards.Count ?? 0;
                bool enabled = QuestFrameUiLaw.RewardCompleteEnabled(choices, _questRewardChoice);
                if (QuestPanelButton(dl, "BenillaQuestRewardCompleteButton", "##quest-complete",
                        "Complete Quest", origin + new Vector2(22, 418) * s,
                        new Vector2(120, 22), s, enabled))
                {
                    uint choice = (uint)Math.Max(0, _questRewardChoice);
                    if (ChooseQuestReward(choice)) PlayUiSound("igQuestListComplete");
                }
                if (QuestPanelButton(dl, "BenillaQuestRewardCancelButton", "##quest-reward-cancel",
                        "Cancel", origin + new Vector2(267, 417) * s, new Vector2(78, 22), s))
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
            uint highlight = _gameplayArt?.AdditiveHandle(
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
                GameText.Draw(dl, fontObject, lines[i], min + new Vector2(0, i * lineHeight),
                    s, color);
        return lines.Count * lineHeight;
    }

    private float DrawQuestGreetingContent(ImDrawListPtr dl, Vector2 p, float s)
    {
        if (_questList is null) return 0;
        float y = 10;
        Vector2 greetingMin = p + new Vector2(10, y) * s;
        float greetingHeight = DrawQuestWrappedText(dl, ExpandQuestText(_questList.Greeting),
            greetingMin, 270, "QuestFont", s, 0xff202020);
        TraceQuestText("BenillaQuestGreetingText", "QuestFont", greetingMin,
            new Vector2(270*s,greetingHeight), "BenillaQuestGreetingScrollChild", 0xff202020);
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
            Vector2 headerMin=p+new Vector2(10,y)*s;
            GameText.Draw(dl, "QuestTitleFont", "Current Quests", headerMin, s);
            TraceQuestText("BenillaQuestActiveHeader","QuestTitleFont",headerMin,
                new Vector2(300,GameText.EmPixels("QuestTitleFont",s)/s)*s,
                "BenillaQuestGreetingScrollChild",FontObjectLaw.Get("QuestTitleFont").Color);
            y += 23; // header height + authored 5 px gap to the first row
            foreach (GossipQuest quest in active) y += DrawQuestTitleRow(dl, p, s, y, quest, titleSlot++);
        }
        else if (_uiParityArmed && _uiParityPanel == "quest-frame")
            ClassifyUiParity("BenillaQuestActiveHeader","FontString",
                "BenillaQuestGreetingScrollChild","NOT-DRAWN","no-active-quest-rows");
        if (active.Length > 0 && available.Length > 0)
        {
            Vector2 breakMin=p+new Vector2(22,y+10)*s;
            DrawArt(dl, @"Interface\QuestFrame\UI-HorizontalBreak", breakMin,
                new Vector2(256, 32), s);
            if(_uiParityArmed&&_uiParityPanel=="quest-frame")
                CollectUiParityDraw("BenillaQuestGreetingBreak","Texture",breakMin,
                    new Vector2(256,32)*s,"BenillaQuestGreetingScrollChild",
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
            Vector2 headerMin=p+new Vector2(10,y)*s;
            GameText.Draw(dl, "QuestTitleFont", "Available Quests", headerMin, s);
            TraceQuestText("BenillaQuestAvailableHeader","QuestTitleFont",headerMin,
                new Vector2(300,GameText.EmPixels("QuestTitleFont",s)/s)*s,
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
        Vector2 min = p + new Vector2(0, y) * s;
        string title = ExpandQuestText(quest.Title);
        float textHeight = Math.Max(16,
            MeasureQuestWrappedText(title, 275, "QuestFont", s) / s);
        DrawArt(dl, @"Interface\QuestFrame\UI-Quest-BulletPoint", min, new Vector2(16), s);
        ImGui.SetCursorScreenPos(min);
        bool clicked = ImGui.InvisibleButton($"##quest-title-{quest.QuestId}", new Vector2(285, textHeight + 2) * s);
        bool hovered = ImGui.IsItemHovered();
        if (hovered)
        {
            uint hi = _gameplayArt!.AdditiveHandle(@"Interface\QuestFrame\UI-QuestTitleHighlight");
            if (hi != 0) dl.AddImage((nint)hi, min, min + new Vector2(285, textHeight + 2) * s);
        }
        DrawQuestWrappedText(dl, title, min + new Vector2(20, 0) * s, 275,
            "QuestFont", s, 0xff202020);
        if (_uiParityArmed && _uiParityPanel == "quest-frame")
        {
            string element = $"BenillaQuestTitleButton{displayIndex}";
            Vector4 clip = new(_uiParityOrigin.X+QuestFrameUiLaw.ScrollX*s,
                _uiParityOrigin.Y+QuestFrameUiLaw.ScrollY*s,
                _uiParityOrigin.X+(QuestFrameUiLaw.ScrollX+QuestFrameUiLaw.ScrollWidth)*s,
                _uiParityOrigin.Y+(QuestFrameUiLaw.ScrollY+QuestFrameUiLaw.ScrollHeight)*s);
            Vector2 rowSize = new Vector2(285,textHeight+2)*s;
            CollectUiParityDraw(element,"Button",min,rowSize,"BenillaQuestGreetingScrollChild",
                new("",0,"FRAMES","TOPLEFT","BenillaQuestGreetingScrollChild","TOPLEFT",0,-y,
                    ClipRect:clip,ClipMask:"BenillaQuestGreetingScroll",Visible:true,Enabled:true,
                    InteractionState:hovered?"hovered":"normal",HitMin:min,HitMax:min+rowSize,
                    Strata:"DIALOG"));
            CollectUiParityDraw(element+"/Bullet","Texture",min,new Vector2(16)*s,element,
                new(@"Interface\QuestFrame\UI-Quest-BulletPoint",0xffffffff,"BACKGROUND",
                    "TOPLEFT",element,"TOPLEFT",0,0,ClipRect:clip,
                    ClipMask:"BenillaQuestGreetingScroll",BlendMode:"BLEND",Strata:"DIALOG"));
            Vector2 titleMin=min+new Vector2(20,0)*s;
            CollectUiParityDraw(element+"/Text","FontString",titleMin,
                new Vector2(275,textHeight)*s,element,
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

    private float DrawQuestDetailContent(ImDrawListPtr dl, Vector2 p, float s)
    {
        if (_questDetails is null) return 0;
        float y = 10;
        Vector2 titleMin=p+new Vector2(5,y)*s;
        GameText.Draw(dl, "QuestTitleFont", ExpandQuestText(_questDetails.Title), titleMin, s);
        TraceQuestText("BenillaQuestDetailTitle","QuestTitleFont",titleMin,
            new Vector2(295,GameText.EmPixels("QuestTitleFont",s)/s)*s,
            "BenillaQuestDetailScrollChild",FontObjectLaw.Get("QuestTitleFont").Color);
        y += 19;
        Vector2 bodyMin=p+new Vector2(5,y)*s;
        float bodyHeight=DrawQuestWrappedText(dl, ExpandQuestText(_questDetails.Details), bodyMin,
            270, "QuestFont", s, 0xff202020);
        TraceQuestText("BenillaQuestDetailBody","QuestFont",bodyMin,
            new Vector2(270*s,bodyHeight),"BenillaQuestDetailScrollChild",0xff202020);
        y += bodyHeight/s+15;
        Vector2 objectiveHeaderMin=p+new Vector2(5,y)*s;
        GameText.Draw(dl, "QuestTitleFont", "Quest Objectives", objectiveHeaderMin, s);
        TraceQuestText("BenillaQuestDetailObjectiveHeader","QuestTitleFont",objectiveHeaderMin,
            new Vector2(295,GameText.EmPixels("QuestTitleFont",s)/s)*s,
            "BenillaQuestDetailScrollChild",FontObjectLaw.Get("QuestTitleFont").Color);
        y += 23;
        Vector2 objectiveMin=p+new Vector2(5,y)*s;
        float objectiveHeight=DrawQuestWrappedText(dl, ExpandQuestText(_questDetails.Objectives),
            objectiveMin, 270, "QuestFont", s, 0xff202020);
        TraceQuestText("BenillaQuestDetailObjective","QuestFont",objectiveMin,
            new Vector2(270*s,objectiveHeight),"BenillaQuestDetailScrollChild",0xff202020);
        y += objectiveHeight/s+15;
        return DrawQuestRewardSet(dl, p, s, y, _questDetails.ChoiceRewards,
            _questDetails.FixedRewards, _questDetails.Money, selectable: false);
    }

    private float DrawQuestProgressContent(ImDrawListPtr dl, Vector2 p, float s)
    {
        if (_questRequestItems is null) return 0;
        float y = 10;
        Vector2 titleMin=p+new Vector2(5,y)*s;
        GameText.Draw(dl, "QuestTitleFont", ExpandQuestText(_questRequestItems.Title), titleMin, s);
        TraceQuestText("BenillaQuestProgressTitle","QuestTitleFont",titleMin,
            new Vector2(295,GameText.EmPixels("QuestTitleFont",s)/s)*s,
            "BenillaQuestProgressScrollChild",FontObjectLaw.Get("QuestTitleFont").Color);
        y += 19;
        Vector2 bodyMin=p+new Vector2(5,y)*s;
        float bodyHeight=DrawQuestWrappedText(dl, ExpandQuestText(_questRequestItems.Text), bodyMin,
            270, "QuestFont", s, 0xff202020);
        TraceQuestText("BenillaQuestProgressBody","QuestFont",bodyMin,
            new Vector2(270*s,bodyHeight),"BenillaQuestProgressScrollChild",0xff202020);
        y += bodyHeight/s+15;
        if (_questRequestItems.RequiredItems.Count > 0 || _questRequestItems.RequiredMoney > 0)
        {
            Vector2 requiredMin=p+new Vector2(5,y)*s;
            GameText.Draw(dl, "QuestTitleFont", "Items Needed:", requiredMin, s);
            TraceQuestText("BenillaQuestProgressRequiredText","QuestTitleFont",requiredMin,
                new Vector2(295,GameText.EmPixels("QuestTitleFont",s)/s)*s,
                "BenillaQuestProgressScrollChild",FontObjectLaw.Get("QuestTitleFont").Color);
            y += 22;
        }
        else if(_uiParityArmed&&_uiParityPanel=="quest-frame")
            ClassifyUiParity("BenillaQuestProgressRequiredText","FontString",
                "BenillaQuestProgressScrollChild","NOT-DRAWN","no-required-items-or-money");
        if (_questRequestItems.RequiredMoney > 0)
        {
            const string requiredMoney = "Required Money:";
            Vector2 moneyLabelMin=p+new Vector2(5,y)*s;
            GameText.Draw(dl, "QuestFontNormalSmall", requiredMoney, moneyLabelMin, s);
            TraceQuestText("BenillaQuestProgressRequiredMoneyText","QuestFontNormalSmall",
                moneyLabelMin,new Vector2(GameText.MeasureWidth("QuestFontNormalSmall",requiredMoney,s),
                    GameText.EmPixels("QuestFontNormalSmall",s)),
                "BenillaQuestProgressScrollChild",FontObjectLaw.Get("QuestFontNormalSmall").Color);
            float moneyX = 5 + GameText.MeasureWidth("QuestFontNormalSmall", requiredMoney, s) / s + 10;
            DrawQuestMoney(dl, p + new Vector2(moneyX, y) * s,
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
        y = DrawQuestItemGrid(dl, p, s, y, _questRequestItems.RequiredItems, selectable: false, "required");
        return Math.Max(334, y + 10);
    }

    private float DrawQuestRewardContent(ImDrawListPtr dl, Vector2 p, float s)
    {
        if (_questOffer is null) return 0;
        float y = 10;
        Vector2 titleMin=p+new Vector2(5,y)*s;
        GameText.Draw(dl, "QuestTitleFont", ExpandQuestText(_questOffer.Title), titleMin, s);
        TraceQuestText("BenillaQuestRewardTitle","QuestTitleFont",titleMin,
            new Vector2(295,GameText.EmPixels("QuestTitleFont",s)/s)*s,
            "BenillaQuestRewardScrollChild",FontObjectLaw.Get("QuestTitleFont").Color);
        y += 19;
        Vector2 bodyMin=p+new Vector2(5,y)*s;
        float bodyHeight=DrawQuestWrappedText(dl, ExpandQuestText(_questOffer.Text), bodyMin,
            275, "QuestFont", s, 0xff202020);
        TraceQuestText("BenillaQuestRewardBody","QuestFont",bodyMin,
            new Vector2(275*s,bodyHeight),"BenillaQuestRewardScrollChild",0xff202020);
        y += bodyHeight/s+10;
        return DrawQuestRewardSet(dl, p, s, y, _questOffer.ChoiceRewards,
            _questOffer.FixedRewards, _questOffer.Money, selectable: true);
    }

    private float DrawQuestRewardSet(ImDrawListPtr dl, Vector2 p, float s, float y,
        IReadOnlyList<QuestRewardItem> choices, IReadOnlyList<QuestRewardItem> fixedItems,
        int money, bool selectable)
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
        Vector2 rewardTitleMin = p + new Vector2(5, y) * s;
        GameText.Draw(dl, "QuestTitleFont", "Rewards", rewardTitleMin, s);
        TraceQuestText(prefix + "RewardTitleText", "QuestTitleFont", rewardTitleMin,
            new Vector2(285, GameText.EmPixels("QuestTitleFont", s) / s) * s, child,
            FontObjectLaw.Get("QuestTitleFont").Color);
        y += 23;
        if (choices.Count > 0)
        {
            const string chooseText = "You may choose one of these rewards:";
            Vector2 chooseMin = p + new Vector2(5, y) * s;
            GameText.Draw(dl, "QuestFont", chooseText, chooseMin, s);
            TraceQuestText(prefix + "ItemChooseText", "QuestFont", chooseMin,
                new Vector2(295, GameText.EmPixels("QuestFont", s) / s) * s, child,
                FontObjectLaw.Get("QuestFont").Color);
            y += 20;
            y = DrawQuestItemGrid(dl, p, s, y, choices, selectable, "choice");
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
            Vector2 receiveMin = p + new Vector2(8, y) * s;
            GameText.Draw(dl, "QuestFont", receive, receiveMin, s);
            TraceQuestText(prefix + "ItemReceiveText", "QuestFont", receiveMin,
                new Vector2(GameText.MeasureWidth("QuestFont", receive, s),
                    GameText.EmPixels("QuestFont", s)), child,
                FontObjectLaw.Get("QuestFont").Color);
            if (money > 0)
            {
                float moneyX = 8 + GameText.MeasureWidth("QuestFont", receive, s) / s + 15;
                DrawQuestMoney(dl, p + new Vector2(moneyX, y) * s, (uint)money, s,
                    prefix + "Money");
            }
            else if (_uiParityArmed && _uiParityPanel == "quest-frame")
                for (int denomination = 1; denomination <= 3; denomination++)
                    ClassifyUiParity(prefix + $"MoneyCoin{denomination}", "Frame", child,
                        "NOT-DRAWN", "quest-has-no-money-reward");
            y += 20;
            y = DrawQuestItemGrid(dl, p, s, y, fixedItems, selectable: false, "reward");
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
        IReadOnlyList<QuestRewardItem> items, bool selectable, string kind)
    {
        int count = Math.Min(items.Count, QuestFrameUiLaw.MaxItems);
        for (int i = 0; i < count; i++)
            DrawQuestItemRow(dl, p + (new Vector2(-3, y) + QuestFrameUiLaw.ItemGridOffset(i)) * s,
                s, items[i], selectable, i, kind);
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
        bool selectable, int index, string kind)
    {
        if (_items is not null && _net is not null) _items.Require(row.ItemId, QuestGiverGuid(), _net);
        string name = "...";
        string iconPath = _items?.IconForDisplay(row.DisplayId) ?? @"Interface\Icons\INV_Misc_QuestionMark.blp";
        ItemTemplate? item = null;
        if (_items?.TryGet(row.ItemId, out item) == true && item is not null)
            name = item.Name;
        uint icon = _gameplayArt!.Handle(iconPath);
        if (icon != 0) dl.AddImage((nint)icon, min, min + new Vector2(39) * s);
        DrawArt(dl, @"Interface\QuestFrame\UI-QuestItemNameFrame", min + new Vector2(29, -12) * s,
            new Vector2(128, 64), s);
        GameText.Draw(dl, "GameFontHighlight", name, min + new Vector2(44, 12) * s, s,
            item is null ? 0xffffffff : ImGui.ColorConvertFloat4ToU32(ItemQualityColor(item.Quality)));
        if (row.Count > 1) GameText.DrawRightAligned(dl, "NumberFontNormal", row.Count.ToString(),
            min + new Vector2(35, 25) * s, s);
        ImGui.SetCursorScreenPos(min);
        bool clicked = ImGui.InvisibleButton($"##quest-{kind}-{index}", new Vector2(147, 41) * s);
        bool hovered = ImGui.IsItemHovered();
        if (_uiParityArmed && _uiParityPanel == "quest-frame")
        {
            string element = QuestItemElementName(kind, index);
            string scrollName = QuestPanelStem(QuestNpcPanelNow()) + "Scroll";
            string scrollChild = scrollName + "Child";
            Vector4 clip = new(_uiParityOrigin.X + QuestFrameUiLaw.ScrollX * s,
                _uiParityOrigin.Y + QuestFrameUiLaw.ScrollY * s,
                _uiParityOrigin.X + (QuestFrameUiLaw.ScrollX + QuestFrameUiLaw.ScrollWidth) * s,
                _uiParityOrigin.Y + (QuestFrameUiLaw.ScrollY + QuestFrameUiLaw.ScrollHeight) * s);
            CollectUiParityDraw(element, "Button", min, new Vector2(147, 41) * s,
                scrollChild, new("", 0, "FRAMES", "TOPLEFT",
                    scrollChild, "TOPLEFT", 0, 0, ClipRect:clip,
                    ClipMask:scrollName, Visible:true, Enabled:true,
                    InteractionState:hovered?"hovered":"normal", HitMin:min,
                    HitMax:min+new Vector2(147,41)*s, Strata:"DIALOG"));
            CollectUiParityDraw(element + "/IconTexture", "Texture", min,
                new Vector2(39) * s, element, new(iconPath, 0xffffffff, "BACKGROUND",
                     "TOPLEFT", element, "TOPLEFT", 0, 0,
                     ContentRect:new(min.X,min.Y,min.X+39*s,min.Y+39*s), ClipRect:clip,
                    ClipMask:scrollName, BlendMode:"BLEND", Strata:"DIALOG"));
            Vector2 nameFrameMin = min + new Vector2(29, -12) * s;
            CollectUiParityDraw(element + "/NameFrame", "Texture", nameFrameMin,
                new Vector2(128,64)*s, element,
                new(@"Interface\QuestFrame\UI-QuestItemNameFrame", 0xffffffff, "BORDER",
                    "TOPLEFT", element, "TOPLEFT", 29, 12,
                    ContentRect:new(nameFrameMin.X,nameFrameMin.Y,
                        nameFrameMin.X+128*s,nameFrameMin.Y+64*s), ClipRect:clip,
                    ClipMask:scrollName, BlendMode:"BLEND", Strata:"DIALOG"));
            string fontObject = "GameFontHighlight";
            Vector2 nameMin = min + new Vector2(44, 12) * s;
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
                    min + new Vector2(35,25)*s - new Vector2(countSize.X,0), countSize,
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
            GameTooltipOwnerKey tooltipOwner = QuestItemGameTooltipOwner(
                QuestNpcPanelNow(), kind, index);
            if (item is not null)
            {
                ItemTooltipBodySnapshot tooltipBody =
                    PrepareItemTooltipBodySnapshot(item, row.Count);
                OfferPreparedItemTooltip(tooltipOwner, tooltipBody);
            }
            else
            {
                string retrievingItemInfo = QuestGlobalString("RETRIEVING_ITEM_INFO");
                OfferPreservedSharedGameTooltipRenderer(tooltipOwner, () =>
                {
                    ImGui.BeginTooltip();
                    ImGui.TextUnformatted(retrievingItemInfo);
                    ImGui.EndTooltip();
                });
            }
        }
        if (clicked && selectable) _questRewardChoice = index;
        if (selectable && _questRewardChoice == index)
        {
            uint glow = _gameplayArt!.AdditiveHandle(@"Interface\QuestFrame\UI-QuestItemHighlight");
            Vector2 glowMin = min + new Vector2(-8, -7) * s;
            if (glow != 0) dl.AddImage((nint)glow, glowMin,
                min + new Vector2(248, 57) * s);
            if (_uiParityArmed && _uiParityPanel == "quest-frame")
                CollectUiParityDraw("BenillaQuestRewardChoiceHighlight",
                    "HighlightTexture", glowMin, new Vector2(256,64)*s,
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
            GameText.Draw(dl, "NumberFontNormal", text, new Vector2(x, min.Y), s);
            float numberWidth=GameText.MeasureWidth("NumberFontNormal", text, s);
            Vector2 numberMin=new(x,min.Y);
            x += numberWidth;
            Vector2 iconMin=new(x,min.Y);
            DrawMailCoin(dl, coin.Denomination, iconMin, s);
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
                float offset = slot == 0 ? progressMoney ? 10 : 15 : 4;
                Vector4 clip=new(_uiParityOrigin.X+QuestFrameUiLaw.ScrollX*s,
                    _uiParityOrigin.Y+QuestFrameUiLaw.ScrollY*s,
                    _uiParityOrigin.X+(QuestFrameUiLaw.ScrollX+QuestFrameUiLaw.ScrollWidth)*s,
                    _uiParityOrigin.Y+(QuestFrameUiLaw.ScrollY+QuestFrameUiLaw.ScrollHeight)*s);
                CollectUiParityDraw(element,"Frame",numberMin,new Vector2(numberWidth+13*s,13*s),
                    scrollChild,new("",0,"FRAMES","LEFT",relativeTo,
                        "RIGHT",offset,0,ClipRect:clip,ClipMask:scrollName,
                        Strata:"DIALOG"));
                CollectUiParityDraw(element+"/Num","FontString",numberMin,
                    new Vector2(numberWidth,13*s),element,
                    new("",FontObjectLaw.Get("NumberFontNormal").Color,"OVERLAY","RIGHT",
                        element,"RIGHT",-13,0,FontObjectLaw.Get("NumberFontNormal").Face,
                        FontObjectLaw.Get("NumberFontNormal").Height,ClipRect:clip,
                        ClipMask:scrollName,Strata:"DIALOG"));
                CollectUiParityDraw(element+"/Icon","Texture",iconMin,new Vector2(13)*s,element,
                    new(@"Interface\MoneyFrame\UI-MoneyIcons",0xffffffff,"OVERLAY","RIGHT",
                        element,"RIGHT",0,0,TexCoords:coin.Denomination switch
                        {0=>"0|0|0.25|1",1=>"0.25|0|0.5|1",_=>"0.5|0|0.75|1"},
                        ClipRect:clip,ClipMask:scrollName,BlendMode:"BLEND",
                        Strata:"DIALOG"));
            }
            x += 17 * s; // 13 px icon + 4 px inter-slot gap
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
        Vector2 bar = origin + new Vector2(329, 81) * s;
        Vector4 clip = new(origin.X, origin.Y, origin.X + 384 * s, origin.Y + 512 * s);
        if (_uiParityArmed && _uiParityPanel == "quest-frame")
            CollectUiParityDraw(barName, "Frame", bar, new Vector2(16,334)*s, panelName,
                new("",0,"FRAMES","TOPLEFT",scrollName,"TOPRIGHT",6,0,
                    ContentRect:new(bar.X,bar.Y,bar.X+16*s,bar.Y+334*s),ClipRect:clip,
                    ClipMask:panelName,Strata:"DIALOG"));
        bool up = _questNpcScroll > 0, down = _questNpcScroll < range;
        if (DrawQuestScrollButton(dl, "##quest-scroll-up", barName + "Up", bar, up,
                upward: true, s: s))
            _questNpcScroll = QuestFrameUiLaw.ClampScroll(
                _questNpcScroll - QuestFrameUiLaw.ScrollStep, contentHeight);
        if (DrawQuestScrollButton(dl, "##quest-scroll-down", barName + "Down",
                bar + new Vector2(0, 318) * s, down, upward: false, s: s))
            _questNpcScroll = QuestFrameUiLaw.ClampScroll(
                _questNpcScroll + QuestFrameUiLaw.ScrollStep, contentHeight);
        Vector2 trackMin = bar + new Vector2(0,16)*s;
        if (_uiParityArmed && _uiParityPanel == "quest-frame")
            CollectUiParityDraw(barName+"Track","Frame",trackMin,new Vector2(16,302)*s,
                barName,new("",0,"FRAMES","TOPLEFT",barName+"Up","BOTTOMLEFT",0,0,
                    ContentRect:new(trackMin.X,trackMin.Y,trackMin.X+16*s,trackMin.Y+302*s),
                    ClipRect:clip,ClipMask:barName,Strata:"DIALOG"));
        uint knob = _gameplayArt!.Handle(@"Interface\Buttons\UI-ScrollBar-Knob");
        if (knob != 0)
        {
            Vector2 at = bar + new Vector2(0, 16 + QuestFrameUiLaw.ScrollThumbY(_questNpcScroll, contentHeight)) * s;
            dl.AddImage((nint)knob, at, at + new Vector2(16) * s, new(.25f), new(.75f));
            if (_uiParityArmed && _uiParityPanel == "quest-frame")
                CollectUiParityDraw(barName+"Thumb", "Texture", at,
                    new Vector2(16) * s, barName+"Track",
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
        Vector2 size = new Vector2(16) * s;
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

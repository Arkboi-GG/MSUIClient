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
    private HashSet<uint> _questLogSnapshot = [];
    private uint _questXpBefore;
    private uint _questMoneyBefore;
    private uint _questExpectedXp;
    private int _questExpectedMoney;
    private uint _questRewardPending;
    private float _questNpcScroll;
    private int _questRewardChoice = -1;

    private bool QuestGate(ulong guid, string step, Func<NetworkClient, bool> send)
    {
        string outcome = "REFUSED"; string detail = "descriptorMissing";
        if (_net is { IsInWorld: true } net && _controller is not null &&
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
        SnapshotQuestEconomy();
        return QuestGate(_questDetails.GiverGuid, "accept", n => n.QuestgiverAccept(_questDetails.GiverGuid, _questDetails.QuestId));
    }

    private bool RequestQuestCompletion()
    {
        uint id = _questRequestItems?.QuestId ?? _questOffer?.QuestId ?? _questDetails?.QuestId ?? 0;
        ulong guid = _questRequestItems?.GiverGuid ?? _questOffer?.GiverGuid ?? _questDetails?.GiverGuid ?? 0;
        return id != 0 && QuestGate(guid, "complete-request", n => n.QuestgiverComplete(guid, id));
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
        EmitInterface("quest", "status", "DECODED", value.GiverGuid, $"status={value.Status}");
    }

    private void ApplyQuestList(byte[] body)
    {
        bool opening = QuestNpcPanelNow() == QuestNpcPanel.None;
        _questList = QuestPackets.ParseList(body); _questDetails = null; _questOffer = null; _questRequestItems = null;
        _questNpcScroll = 0; _questRewardChoice = -1;
        if (opening) PlayUiSound("igQuestListOpen");
        foreach (GossipQuest quest in _questList.Quests) _questTitles[quest.QuestId] = quest.Title;
        EmitInterface("quest", "list", "DECODED", _questList.GiverGuid,
            $"quests={_questList.Quests.Count};greeting={SanitizeEvidence(_questList.Greeting)}");
    }

    private void ApplyQuestDetails(byte[] body)
    {
        bool opening = QuestNpcPanelNow() == QuestNpcPanel.None;
        _questDetails = QuestPackets.ParseDetails(body); _questList = null; _questOffer = null; _questRequestItems = null;
        _questNpcScroll = 0; _questRewardChoice = -1;
        if (opening) PlayUiSound("igQuestListOpen");
        PlayUiSound("WriteQuest"); // instant-text mode still scratches exactly once
        _questTitles[_questDetails.QuestId] = _questDetails.Title;
        EmitInterface("quest", "details", "DECODED", _questDetails.GiverGuid,
            $"quest={_questDetails.QuestId};title={SanitizeEvidence(_questDetails.Title)};objectives={SanitizeEvidence(_questDetails.Objectives)};choices={_questDetails.ChoiceRewards.Count};fixed={_questDetails.FixedRewards.Count};money={_questDetails.Money}");
    }

    private void ApplyQuestRequestItems(byte[] body)
    {
        bool opening = QuestNpcPanelNow() == QuestNpcPanel.None;
        _questRequestItems = QuestPackets.ParseRequestItems(body);
        _questList = null; _questDetails = null; _questOffer = null;
        _questNpcScroll = 0; _questRewardChoice = -1;
        if (opening) PlayUiSound("igQuestListOpen");
        EmitInterface("quest", "request-items", "DECODED", _questRequestItems.GiverGuid,
            $"quest={_questRequestItems.QuestId};items={_questRequestItems.RequiredItems.Count};money={_questRequestItems.RequiredMoney};completable={_questRequestItems.Completable}");
    }

    private void ApplyQuestOffer(byte[] body)
    {
        bool opening = QuestNpcPanelNow() == QuestNpcPanel.None;
        _questOffer = QuestPackets.ParseOffer(body);
        _questList = null; _questDetails = null; _questRequestItems = null;
        _questNpcScroll = 0; _questRewardChoice = -1;
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

    private void ApplyQuestObjectiveComplete(byte[] body)
    {
        uint id = QuestPackets.ParseQuestId(body); _questProgress[id] = "complete";
        EmitInterface("quest", "objective", "COMPLETE", 0, $"quest={id}");
    }

    private void ApplyQuestComplete(byte[] body)
    {
        QuestComplete value = QuestPackets.ParseComplete(body);
        _questRewardPending = value.QuestId; _questExpectedXp = value.Experience; _questExpectedMoney = value.Money;
        EmitInterface("quest", "reward", "COMPLETED", _net?.PlayerGuid ?? 0,
            $"quest={value.QuestId};xp={value.Experience};money={value.Money};items={value.Rewards.Count};xpBefore={_questXpBefore};moneyBefore={_questMoneyBefore}");
        CloseQuestNpcFrame(playSound: true);
    }

    private QuestNpcPanel QuestNpcPanelNow() => _questList is not null ? QuestNpcPanel.Greeting :
        _questDetails is not null ? QuestNpcPanel.Detail : _questRequestItems is not null ? QuestNpcPanel.Progress :
        _questOffer is not null ? QuestNpcPanel.Reward : QuestNpcPanel.None;

    private ulong QuestGiverGuid() => _questList?.GiverGuid ?? _questDetails?.GiverGuid ??
        _questRequestItems?.GiverGuid ?? _questOffer?.GiverGuid ?? 0;

    private void CloseQuestNpcFrame(bool playSound)
    {
        bool wasOpen = QuestNpcPanelNow() != QuestNpcPanel.None;
        _questList = null; _questDetails = null; _questRequestItems = null; _questOffer = null;
        _questNpcScroll = 0; _questRewardChoice = -1;
        if (wasOpen && playSound) PlayUiSound("igQuestListClose");
    }

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
        if (!_questLogOpen && _questList is null && _questDetails is null && _questOffer is null && _questRequestItems is null) return;
        float s=GameplayUiScale(); Vector2 origin=new(0,104*s), logicalSize=new(384,512);
        ImGui.SetNextWindowPos(origin,ImGuiCond.Always); ImGui.SetNextWindowSize(logicalSize*s,ImGuiCond.Always); ImGui.SetNextWindowBgAlpha(0);
        if (!ImGui.Begin("##quest", ImGuiWindowFlags.NoDecoration|ImGuiWindowFlags.NoMove|ImGuiWindowFlags.NoSavedSettings|ImGuiWindowFlags.NoBackground|ImGuiWindowFlags.NoNav)) { ImGui.End(); return; }
        ImDrawListPtr dl=ImGui.GetWindowDrawList();
        bool parityProof = _uiParityArmed && _uiParityPanel is "quest-log" or "quest-frame";
        if(parityProof){BeginUiParityFrame(origin,s);CollectUiParityDraw(_uiParityPanel=="quest-log"?"QuestLogFrame":"QuestFrame","Frame",origin,logicalSize*s,"",new("",0,"IMGUI_HOST","ANCHOR:ABSOLUTE","","",0,8));}
        bool logMode = _questLogOpen;
        if (!logMode && _entities.TryGet(QuestGiverGuid(), out WorldEntity giver))
        {
            Vector2 portraitMin = origin + new Vector2(7, 6) * s;
            DrawUnitPortraitImage(dl, giver, portraitMin, 60 * s, 0, false);
            if (parityProof) CollectUiParityDraw("QuestFramePortrait", "PlayerModel", portraitMin,
                new Vector2(60) * s, "QuestFrame",
                new("", 0xffffffff, "IMGUI_IMAGE", "TOPLEFT", "QuestFrame", "TOPLEFT", 7, -6));
        }
        string stem = logMode ? "UI-QuestLog" : "UI-QuestGreeting";
        var art = new List<(string Element,string Path,Vector2 Offset,Vector2 Size)>();
        if (logMode) art.Add(("QuestLogFrame/Texture",@"Interface\QuestFrame\UI-QuestLog-BookIcon",new(4,4),new(64,64)));
        art.AddRange([
            ("QuestLogFrame/Texture#2",$@"Interface\QuestFrame\{stem}-TopLeft",Vector2.Zero,new(256,256)),
            ("QuestLogFrame/Texture#3",$@"Interface\QuestFrame\{stem}-TopRight",new(256,0),new(128,256)),
            ("QuestLogFrame/Texture#4",$@"Interface\QuestFrame\{stem}-BotLeft",new(0,256),new(256,256)),
            ("QuestLogFrame/Texture#5",$@"Interface\QuestFrame\{stem}-BotRight",new(256,256),new(128,256))]);
        foreach(var r in art){Vector2 m=origin+r.Offset*s;DrawArt(dl,r.Path,m,r.Size,s);if(parityProof)CollectUiParityDraw(r.Element,"Texture",m,r.Size*s,logMode?"QuestLogFrame":"QuestFrame",new(r.Path,0xffffffff,"IMGUI_IMAGE","TOPLEFT",logMode?"QuestLogFrame":"QuestFrame","TOPLEFT",r.Offset.X,-r.Offset.Y));}
        if (!logMode)
        {
            ulong guid = QuestGiverGuid();
            string name = _entities.TryGet(guid, out WorldEntity namedGiver)
                ? _creatureNames.GetValueOrDefault(namedGiver.Entry, "Quest Giver") : "Quest Giver";
            DrawCenteredText(dl, origin + new Vector2(192, 23) * s, name, 12f * s, 0xffffffff);
            if (parityProof) CollectUiParityDraw("QuestFrameNpcNameText", "FontString",
                origin + new Vector2(67, 15) * s, new Vector2(250, 16) * s, "QuestFrame",
                new("", 0xffffffff, "IMGUI_TEXT", "TOP", "QuestFrame", "TOP", 0, -15));
        }
        if (_questLogOpen && _net is not null && _entities.TryGet(_net.PlayerGuid, out WorldEntity player))
            DrawQuestLogContent(dl, origin, s, player);
        else
            DrawQuestNpcContent(dl, origin, s);
        Vector2 close=origin+(logMode ? new Vector2(322,8) : QuestFrameUiLaw.CloseMin)*s;
        DrawImageButton(dl,"##quest-close",close,new Vector2(32)*s,
            @"Interface\Buttons\UI-Panel-MinimizeButton-Up",@"Interface\Buttons\UI-Panel-MinimizeButton-Down",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Highlight");
        if (parityProof) CollectUiParityDraw("QuestFrameCloseButton", "Button", close,
            new Vector2(32) * s, logMode ? "QuestLogFrame" : "QuestFrame",
            new(@"Interface\Buttons\UI-Panel-MinimizeButton-Up", 0xffffffff, "IMGUI_IMAGE",
                "TOPLEFT", logMode ? "QuestLogFrame" : "QuestFrame", "TOPLEFT",
                logMode ? 322 : QuestFrameUiLaw.CloseMin.X,
                -(logMode ? 8 : QuestFrameUiLaw.CloseMin.Y)));
        if(ImGui.IsItemClicked())
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
        Vector2 scrollMin = origin + new Vector2(QuestFrameUiLaw.ScrollX, QuestFrameUiLaw.ScrollY) * s;
        Vector2 scrollSize = new(QuestFrameUiLaw.ScrollWidth * s, QuestFrameUiLaw.ScrollHeight * s);
        if (_uiParityArmed && _uiParityPanel == "quest-frame")
            CollectUiParityDraw("QuestFrameScrollFrame", "ScrollFrame", scrollMin, scrollSize,
                "QuestFrame", new("", 0, "IMGUI_CLIP", "TOPLEFT", "QuestFrame", "TOPLEFT",
                    QuestFrameUiLaw.ScrollX, -QuestFrameUiLaw.ScrollY));
        if (ImGui.IsMouseHoveringRect(scrollMin, scrollMin + scrollSize, false) && ImGui.GetIO().MouseWheel != 0)
            _questNpcScroll -= ImGui.GetIO().MouseWheel * QuestFrameUiLaw.ScrollStep;

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
        _questNpcScroll = QuestFrameUiLaw.ClampScroll(_questNpcScroll, contentHeight);
        DrawQuestNpcScrollbar(dl, origin, s, contentHeight);

        switch (panel)
        {
            case QuestNpcPanel.Greeting:
                if (VanillaButton(dl, "##quest-goodbye", "Goodbye", origin + new Vector2(267, 417) * s,
                        new Vector2(78, 22), s)) CloseQuestNpcFrame(playSound: true);
                break;
            case QuestNpcPanel.Detail:
                if (VanillaButton(dl, "##quest-accept", "Accept", origin + new Vector2(23, 418) * s,
                        new Vector2(77, 22), s)) AcceptQuest();
                if (VanillaButton(dl, "##quest-decline", "Decline", origin + new Vector2(267, 418) * s,
                        new Vector2(78, 22), s))
                { PlayUiSound("igQuestCancel"); CloseQuestNpcFrame(playSound: false); }
                break;
            case QuestNpcPanel.Progress:
                if (VanillaButton(dl, "##quest-continue", "Continue", origin + new Vector2(22, 418) * s,
                        new Vector2(120, 22), s, _questRequestItems?.Completable == true))
                    RequestQuestCompletion();
                if (VanillaButton(dl, "##quest-progress-goodbye", "Goodbye", origin + new Vector2(267, 417) * s,
                        new Vector2(78, 22), s))
                { PlayUiSound("igQuestCancel"); CloseQuestNpcFrame(playSound: false); }
                break;
            case QuestNpcPanel.Reward:
                int choices = _questOffer?.ChoiceRewards.Count ?? 0;
                bool enabled = QuestFrameUiLaw.RewardCompleteEnabled(choices, _questRewardChoice);
                if (VanillaButton(dl, "##quest-complete", "Complete Quest", origin + new Vector2(22, 418) * s,
                        new Vector2(120, 22), s, enabled))
                {
                    uint choice = (uint)Math.Max(0, _questRewardChoice);
                    if (ChooseQuestReward(choice)) PlayUiSound("igQuestListComplete");
                }
                if (VanillaButton(dl, "##quest-reward-cancel", "Cancel", origin + new Vector2(267, 417) * s,
                        new Vector2(78, 22), s))
                { PlayUiSound("igQuestCancel"); CloseQuestNpcFrame(playSound: false); }
                break;
        }
    }

    private float DrawQuestGreetingContent(ImDrawListPtr dl, Vector2 p, float s)
    {
        if (_questList is null) return 0;
        float y = 10;
        y += DrawWrappedText(dl, _questList.Greeting, p + new Vector2(10, y) * s,
            270, 13f * s, s, 0xff202020, 80) / s + 10;
        HashSet<uint> activeIds = _net is not null && _entities.TryGet(_net.PlayerGuid, out WorldEntity player)
            ? player.Fields.QuestLog().Select(q => q.QuestId).ToHashSet() : [];
        GossipQuest[] active = _questList.Quests.Where(q => activeIds.Contains(q.QuestId))
            .Take(QuestFrameUiLaw.MaxTitles).ToArray();
        GossipQuest[] available = _questList.Quests.Where(q => !activeIds.Contains(q.QuestId))
            .Take(QuestFrameUiLaw.MaxTitles - active.Length).ToArray();
        if (active.Length > 0)
        {
            GameText.Draw(dl, "QuestTitleFont", "Current Quests", p + new Vector2(10, y) * s, s);
            y += 18;
            foreach (GossipQuest quest in active) y += DrawQuestTitleRow(dl, p, s, y, quest);
        }
        if (active.Length > 0 && available.Length > 0)
        {
            DrawArt(dl, @"Interface\QuestFrame\UI-HorizontalBreak", p + new Vector2(22, y + 2) * s,
                new Vector2(256, 32), s);
            y += 42;
        }
        if (available.Length > 0)
        {
            GameText.Draw(dl, "QuestTitleFont", "Available Quests", p + new Vector2(10, y) * s, s);
            y += 18;
            foreach (GossipQuest quest in available) y += DrawQuestTitleRow(dl, p, s, y, quest);
        }
        return Math.Max(334, y + 10);
    }

    private float DrawQuestTitleRow(ImDrawListPtr dl, Vector2 p, float s, float y, GossipQuest quest)
    {
        Vector2 min = p + new Vector2(0, y) * s;
        float textHeight = Math.Max(16, ImGui.CalcTextSize(quest.Title, false, 275 * s).Y / s);
        DrawArt(dl, @"Interface\QuestFrame\UI-Quest-BulletPoint", min, new Vector2(16), s);
        ImGui.SetCursorScreenPos(min);
        bool clicked = ImGui.InvisibleButton($"##quest-title-{quest.QuestId}", new Vector2(285, textHeight + 2) * s);
        if (ImGui.IsItemHovered())
        {
            uint hi = _gameplayArt!.AdditiveHandle(@"Interface\QuestFrame\UI-QuestTitleHighlight");
            if (hi != 0) dl.AddImage((nint)hi, min, min + new Vector2(285, textHeight + 2) * s);
        }
        DrawWrappedText(dl, quest.Title, min + new Vector2(20, 0) * s, 275, 13f * s, s, 0xff202020, 8);
        if (clicked && _questList is not null)
        { PlayUiSound("igQuestListSelect"); RequestQuestDetails(_questList.GiverGuid, quest.QuestId); }
        return textHeight + 2;
    }

    private float DrawQuestDetailContent(ImDrawListPtr dl, Vector2 p, float s)
    {
        if (_questDetails is null) return 0;
        float y = 10;
        GameText.Draw(dl, "QuestTitleFont", _questDetails.Title, p + new Vector2(5, y) * s, s); y += 19;
        y += DrawWrappedText(dl, _questDetails.Details, p + new Vector2(5, y) * s,
            270, 13f * s, s, 0xff202020, 100) / s + 15;
        GameText.Draw(dl, "QuestTitleFont", "Quest Objectives", p + new Vector2(5, y) * s, s); y += 23;
        y += DrawWrappedText(dl, _questDetails.Objectives, p + new Vector2(5, y) * s,
            270, 13f * s, s, 0xff202020, 80) / s + 15;
        return DrawQuestRewardSet(dl, p, s, y, _questDetails.ChoiceRewards,
            _questDetails.FixedRewards, _questDetails.Money, selectable: false);
    }

    private float DrawQuestProgressContent(ImDrawListPtr dl, Vector2 p, float s)
    {
        if (_questRequestItems is null) return 0;
        float y = 10;
        GameText.Draw(dl, "QuestTitleFont", _questRequestItems.Title, p + new Vector2(5, y) * s, s); y += 19;
        y += DrawWrappedText(dl, _questRequestItems.Text, p + new Vector2(5, y) * s,
            270, 13f * s, s, 0xff202020, 100) / s + 15;
        if (_questRequestItems.RequiredItems.Count > 0 || _questRequestItems.RequiredMoney > 0)
        { GameText.Draw(dl, "QuestTitleFont", "Items Needed:", p + new Vector2(5, y) * s, s); y += 22; }
        if (_questRequestItems.RequiredMoney > 0)
        {
            const string requiredMoney = "Required Money:";
            GameText.Draw(dl, "QuestFontNormalSmall", requiredMoney, p + new Vector2(5, y) * s, s);
            float moneyX = 5 + GameText.MeasureWidth("QuestFontNormalSmall", requiredMoney, s) / s + 10;
            DrawQuestMoney(dl, p + new Vector2(moneyX, y) * s, _questRequestItems.RequiredMoney, s);
            y += 23;
        }
        y = DrawQuestItemGrid(dl, p, s, y, _questRequestItems.RequiredItems, selectable: false, "required");
        return Math.Max(334, y + 10);
    }

    private float DrawQuestRewardContent(ImDrawListPtr dl, Vector2 p, float s)
    {
        if (_questOffer is null) return 0;
        float y = 10;
        GameText.Draw(dl, "QuestTitleFont", _questOffer.Title, p + new Vector2(5, y) * s, s); y += 19;
        y += DrawWrappedText(dl, _questOffer.Text, p + new Vector2(5, y) * s,
            275, 13f * s, s, 0xff202020, 100) / s + 10;
        return DrawQuestRewardSet(dl, p, s, y, _questOffer.ChoiceRewards,
            _questOffer.FixedRewards, _questOffer.Money, selectable: true);
    }

    private float DrawQuestRewardSet(ImDrawListPtr dl, Vector2 p, float s, float y,
        IReadOnlyList<QuestRewardItem> choices, IReadOnlyList<QuestRewardItem> fixedItems,
        int money, bool selectable)
    {
        if (choices.Count == 0 && fixedItems.Count == 0 && money <= 0) return Math.Max(334, y + 10);
        GameText.Draw(dl, "QuestTitleFont", "Rewards", p + new Vector2(5, y) * s, s); y += 23;
        if (choices.Count > 0)
        {
            GameText.Draw(dl, "QuestFont", "You may choose one of these rewards:", p + new Vector2(5, y) * s, s);
            y += 20;
            y = DrawQuestItemGrid(dl, p, s, y, choices, selectable, "choice");
            y += 5;
        }
        if (fixedItems.Count > 0 || money > 0)
        {
            string receive = choices.Count > 0 ? "You will also receive:" : "You will receive:";
            GameText.Draw(dl, "QuestFont", receive, p + new Vector2(8, y) * s, s);
            if (money > 0)
            {
                float moneyX = 8 + GameText.MeasureWidth("QuestFont", receive, s) / s + 15;
                DrawQuestMoney(dl, p + new Vector2(moneyX, y) * s, (uint)money, s);
            }
            y += 20;
            y = DrawQuestItemGrid(dl, p, s, y, fixedItems, selectable: false, "reward");
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
        { name = item.Name; if (item.IconPath.Length > 0) iconPath = item.IconPath; }
        if (selectable && _questRewardChoice == index)
        {
            uint glow = _gameplayArt!.AdditiveHandle(@"Interface\QuestFrame\UI-QuestItemHighlight");
            if (glow != 0) dl.AddImage((nint)glow, min + new Vector2(-8, -7) * s,
                min + new Vector2(248, 57) * s);
        }
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
        if (_uiParityArmed && _uiParityPanel == "quest-frame")
            CollectUiParityDraw($"QuestFrame{kind}Item{index + 1}", "Button", min,
                new Vector2(147, 41) * s, "QuestFrameScrollFrame",
                new(iconPath, 0xffffffff, "IMGUI_IMAGE", "TOPLEFT", "QuestFrameScrollFrame",
                    "TOPLEFT", 0, 0));
        if (ImGui.IsItemHovered() && item is not null) DrawItemTooltip(item, row.Count);
        if (clicked && selectable) _questRewardChoice = index;
    }

    private void DrawQuestMoney(ImDrawListPtr dl, Vector2 min, uint copper, float s)
    {
        uint[] values = [copper / 10000, copper / 100 % 100, copper % 100];
        float x = min.X;
        for (int i = 0; i < values.Length; i++)
        {
            string text = values[i].ToString();
            GameText.Draw(dl, "NumberFontNormal", text, new Vector2(x, min.Y), s);
            x += GameText.MeasureWidth("NumberFontNormal", text, s) + 2 * s;
            DrawMailCoin(dl, i, new Vector2(x, min.Y), s);
            x += 17 * s;
        }
    }

    private void DrawQuestNpcScrollbar(ImDrawListPtr dl, Vector2 origin, float s, float contentHeight)
    {
        float range = Math.Max(0, contentHeight - QuestFrameUiLaw.ScrollHeight);
        if (range <= 0) return;
        Vector2 bar = origin + new Vector2(329, 81) * s;
        bool up = _questNpcScroll > 0, down = _questNpcScroll < range;
        string upState = up ? "Up" : "Disabled", downState = down ? "Up" : "Disabled";
        DrawArt(dl, $@"Interface\Buttons\UI-ScrollBar-ScrollUpButton-{upState}", bar,
            new Vector2(16), s);
        DrawArt(dl, $@"Interface\Buttons\UI-ScrollBar-ScrollDownButton-{downState}",
            bar + new Vector2(0, 318) * s, new Vector2(16), s);
        ImGui.SetCursorScreenPos(bar); ImGui.InvisibleButton("##quest-scroll-up", new Vector2(16) * s);
        if (up && ImGui.IsItemClicked()) _questNpcScroll -= QuestFrameUiLaw.ScrollStep;
        ImGui.SetCursorScreenPos(bar + new Vector2(0, 318) * s);
        ImGui.InvisibleButton("##quest-scroll-down", new Vector2(16) * s);
        if (down && ImGui.IsItemClicked()) _questNpcScroll += QuestFrameUiLaw.ScrollStep;
        uint knob = _gameplayArt!.Handle(@"Interface\Buttons\UI-ScrollBar-Knob");
        if (knob != 0)
        {
            Vector2 at = bar + new Vector2(0, 16 + QuestFrameUiLaw.ScrollThumbY(_questNpcScroll, contentHeight)) * s;
            dl.AddImage((nint)knob, at, at + new Vector2(16) * s, new(.25f), new(.75f));
        }
    }
}

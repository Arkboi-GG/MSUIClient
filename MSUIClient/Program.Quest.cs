using System.Numerics;
using ImGuiNET;
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
        _questList = QuestPackets.ParseList(body); _questDetails = null; _questOffer = null; _questRequestItems = null;
        foreach (GossipQuest quest in _questList.Quests) _questTitles[quest.QuestId] = quest.Title;
        EmitInterface("quest", "list", "DECODED", _questList.GiverGuid,
            $"quests={_questList.Quests.Count};greeting={SanitizeEvidence(_questList.Greeting)}");
    }

    private void ApplyQuestDetails(byte[] body)
    {
        _questDetails = QuestPackets.ParseDetails(body); _questOffer = null; _questRequestItems = null;
        _questTitles[_questDetails.QuestId] = _questDetails.Title;
        EmitInterface("quest", "details", "DECODED", _questDetails.GiverGuid,
            $"quest={_questDetails.QuestId};title={SanitizeEvidence(_questDetails.Title)};objectives={SanitizeEvidence(_questDetails.Objectives)};choices={_questDetails.ChoiceRewards.Count};fixed={_questDetails.FixedRewards.Count};money={_questDetails.Money}");
    }

    private void ApplyQuestRequestItems(byte[] body)
    {
        _questRequestItems = QuestPackets.ParseRequestItems(body);
        EmitInterface("quest", "request-items", "DECODED", _questRequestItems.GiverGuid,
            $"quest={_questRequestItems.QuestId};items={_questRequestItems.RequiredItems.Count};money={_questRequestItems.RequiredMoney};completable={_questRequestItems.Completable}");
    }

    private void ApplyQuestOffer(byte[] body)
    {
        _questOffer = QuestPackets.ParseOffer(body);
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

    private void DrawQuestFrame()
    {
        if (!_questLogOpen && _questList is null && _questDetails is null && _questOffer is null && _questRequestItems is null) return;
        float s=GameplayUiScale(); Vector2 origin=new(0,104*s), logicalSize=new(384,512);
        ImGui.SetNextWindowPos(origin,ImGuiCond.Always); ImGui.SetNextWindowSize(logicalSize*s,ImGuiCond.Always); ImGui.SetNextWindowBgAlpha(0);
        if (!ImGui.Begin("##quest", ImGuiWindowFlags.NoDecoration|ImGuiWindowFlags.NoMove|ImGuiWindowFlags.NoSavedSettings|ImGuiWindowFlags.NoBackground|ImGuiWindowFlags.NoNav)) { ImGui.End(); return; }
        ImDrawListPtr dl=ImGui.GetWindowDrawList();
        if(_uiParityArmed&&_uiParityPanel=="quest-log"){BeginUiParityFrame(origin,s);CollectUiParityDraw("QuestLogFrame","Frame",origin,logicalSize*s,"",new("",0,"IMGUI_HOST","ANCHOR:ABSOLUTE","","",0,8));}
        bool logMode = _questLogOpen;
        string stem = logMode ? "UI-QuestLog" : _questList is not null ? "UI-QuestGreeting" : "UI-Quest";
        (string Element,string Path,Vector2 Offset,Vector2 Size)[] art=[
            ("QuestLogFrame/Texture",@"Interface\QuestFrame\UI-QuestLog-BookIcon",new(4,4),new(64,64)),
            ("QuestLogFrame/Texture#2",$@"Interface\QuestFrame\{stem}-TopLeft",Vector2.Zero,new(256,256)),
            ("QuestLogFrame/Texture#3",$@"Interface\QuestFrame\{stem}-TopRight",new(256,0),new(128,256)),
            ("QuestLogFrame/Texture#4",$@"Interface\QuestFrame\{stem}-BotLeft",new(0,256),new(256,256)),
            ("QuestLogFrame/Texture#5",$@"Interface\QuestFrame\{stem}-BotRight",new(256,256),new(128,256))];
        foreach(var r in art){Vector2 m=origin+r.Offset*s;DrawArt(dl,r.Path,m,r.Size,s);if(_uiParityArmed&&_uiParityPanel=="quest-log")CollectUiParityDraw(r.Element,"Texture",m,r.Size*s,"QuestLogFrame",new(r.Path,0xffffffff,"IMGUI_IMAGE","TOPLEFT","QuestLogFrame","TOPLEFT",r.Offset.X,-r.Offset.Y));}
        if (_questLogOpen && _net is not null && _entities.TryGet(_net.PlayerGuid, out WorldEntity player))
            DrawQuestLogContent(dl, origin, s, player);
        else
            DrawQuestNpcContent(dl, origin, s);
        Vector2 close=origin+new Vector2(322,8)*s;
        DrawImageButton(dl,"##quest-close",close,new Vector2(32)*s,
            @"Interface\Buttons\UI-Panel-MinimizeButton-Up",@"Interface\Buttons\UI-Panel-MinimizeButton-Down",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Highlight");
        if(ImGui.IsItemClicked())
        {
            _questLogOpen=false; _questList=null; _questDetails=null; _questOffer=null; _questRequestItems=null;
        }
        if(_uiParityArmed&&_uiParityPanel=="quest-log")MarkUiParityFrameComplete();
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
        if (_questList is not null)
        {
            DrawCenteredText(dl, origin + new Vector2(192, 22) * s, "Quests", 12f * s, 0xffffffff);
            float used = DrawWrappedText(dl, _questList.Greeting, origin + new Vector2(36, 76) * s,
                300, 11f * s, s, 0xff202020, 8);
            int i = 0;
            foreach (GossipQuest quest in _questList.Quests.Take(12))
            {
                Vector2 min = origin + new Vector2(36, 96 + used / s + i * 22) * s;
                if (VanillaListRow(dl, $"##quest-list-{quest.QuestId}", min, new Vector2(300, 20), s,
                        $"[{quest.Level}] {quest.Title}", false, 0xff202020))
                    RequestQuestDetails(_questList.GiverGuid, quest.QuestId);
                i++;
            }
            return;
        }
        string title = _questDetails?.Title ?? _questRequestItems?.Title ?? _questOffer?.Title ?? "Quest";
        string body = _questDetails?.Details ?? _questRequestItems?.Text ?? _questOffer?.Text ?? "";
        DrawCenteredText(dl, origin + new Vector2(192, 22) * s, title, 12f * s, 0xffffffff);
        float usedBody = DrawWrappedText(dl, body, origin + new Vector2(36, 78) * s,
            300, 11f * s, s, 0xff202020, 18);
        if (_questDetails is not null)
        {
            dl.AddText(ImGui.GetFont(), 12f * s, origin + new Vector2(36, 92 + usedBody / s) * s,
                0xff202020, "Quest Objectives");
            DrawWrappedText(dl, _questDetails.Objectives, origin + new Vector2(36, 112 + usedBody / s) * s,
                300, 10f * s, s, 0xff202020, 8);
            if (VanillaButton(dl, "##quest-accept", "Accept", origin + new Vector2(201, 437) * s,
                    new Vector2(80, 21), s)) AcceptQuest();
            if (VanillaButton(dl, "##quest-decline", "Decline", origin + new Vector2(283, 437) * s,
                    new Vector2(80, 21), s))
                _questDetails = null;
        }
        else if (_questRequestItems is not null)
        {
            if (VanillaButton(dl, "##quest-continue", "Continue", origin + new Vector2(263, 437) * s,
                    new Vector2(100, 21), s, _questRequestItems.Completable)) RequestQuestReward();
        }
        else if (_questOffer is not null)
        {
            int choices = Math.Max(1, _questOffer.ChoiceRewards.Count);
            for (int i = 0; i < choices; i++)
                if (VanillaButton(dl, $"##quest-reward-{i}", choices > 1 ? $"Reward {i + 1}" : "Complete Quest",
                        origin + new Vector2(223 - i * 104, 437) * s, new Vector2(120, 21), s))
                    ChooseQuestReward((uint)i);
        }
    }
}

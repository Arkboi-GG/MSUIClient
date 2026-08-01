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
    private readonly Dictionary<uint, string> _questProgress = [];
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
        EmitInterface("quest", "list", "DECODED", _questList.GiverGuid,
            $"quests={_questList.Quests.Count};greeting={SanitizeEvidence(_questList.Greeting)}");
    }

    private void ApplyQuestDetails(byte[] body)
    {
        _questDetails = QuestPackets.ParseDetails(body); _questOffer = null; _questRequestItems = null;
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
        float s=GameplayUiScale(); Vector2 origin=new(0,8*s), logicalSize=new(384,512);
        ImGui.SetNextWindowPos(origin,ImGuiCond.Always); ImGui.SetNextWindowSize(logicalSize*s,ImGuiCond.Always); ImGui.SetNextWindowBgAlpha(0);
        if (!ImGui.Begin("##quest", ImGuiWindowFlags.NoDecoration|ImGuiWindowFlags.NoMove|ImGuiWindowFlags.NoSavedSettings|ImGuiWindowFlags.NoBackground|ImGuiWindowFlags.NoNav)) { ImGui.End(); return; }
        ImDrawListPtr dl=ImGui.GetWindowDrawList();
        if(_uiParityArmed&&_uiParityPanel=="quest-log"){BeginUiParityFrame(origin,s);CollectUiParityDraw("QuestLogFrame","Frame",origin,logicalSize*s,"",new("",0,"IMGUI_HOST","ANCHOR:ABSOLUTE","","",0,8));}
        (string Element,string Path,Vector2 Offset,Vector2 Size)[] art=[
            ("QuestLogFrame/Texture",@"Interface\QuestFrame\UI-QuestLog-BookIcon",new(4,4),new(64,64)),
            ("QuestLogFrame/Texture#2",@"Interface\QuestFrame\UI-QuestLog-TopLeft",Vector2.Zero,new(256,256)),
            ("QuestLogFrame/Texture#3",@"Interface\QuestFrame\UI-QuestLog-TopRight",new(256,0),new(128,256)),
            ("QuestLogFrame/Texture#4",@"Interface\QuestFrame\UI-QuestLog-BotLeft",new(0,256),new(256,256)),
            ("QuestLogFrame/Texture#5",@"Interface\QuestFrame\UI-QuestLog-BotRight",new(256,256),new(128,256))];
        foreach(var r in art){Vector2 m=origin+r.Offset*s;DrawArt(dl,r.Path,m,r.Size,s);if(_uiParityArmed&&_uiParityPanel=="quest-log")CollectUiParityDraw(r.Element,"Texture",m,r.Size*s,"QuestLogFrame",new(r.Path,0xffffffff,"IMGUI_IMAGE","TOPLEFT","QuestLogFrame","TOPLEFT",r.Offset.X,-r.Offset.Y));}
        ImGui.SetCursorScreenPos(origin+new Vector2(30,75)*s); ImGui.BeginChild("##quest-content",new Vector2(320,350)*s,false);
        if (_questList is not null)
        {
            ImGui.TextWrapped(_questList.Greeting);
            foreach (GossipQuest quest in _questList.Quests)
                if (ImGui.Selectable($"[{quest.Level}] {quest.Title}##quest-list-{quest.QuestId}")) RequestQuestDetails(_questList.GiverGuid, quest.QuestId);
        }
        if (_questDetails is not null)
        {
            ImGui.TextColored(new Vector4(1, .82f, 0, 1), _questDetails.Title); ImGui.Separator();
            ImGui.TextWrapped(_questDetails.Details); ImGui.Spacing(); ImGui.TextWrapped(_questDetails.Objectives);
            if (_questProgress.TryGetValue(_questDetails.QuestId, out string? progress)) ImGui.Text($"Progress: {progress}");
            if (ImGui.Button("Accept##quest")) AcceptQuest(); ImGui.SameLine();
            if (ImGui.Button("Complete##quest")) RequestQuestCompletion();
        }
        if (_questRequestItems is not null)
        {
            ImGui.TextColored(new Vector4(1, .82f, 0, 1), _questRequestItems.Title); ImGui.TextWrapped(_questRequestItems.Text);
            if (ImGui.Button("Continue##quest")) RequestQuestReward();
        }
        if (_questOffer is not null)
        {
            ImGui.TextColored(new Vector4(1, .82f, 0, 1), _questOffer.Title); ImGui.TextWrapped(_questOffer.Text);
            for (int i = 0; i < Math.Max(1, _questOffer.ChoiceRewards.Count); i++)
                if (ImGui.Button($"Complete quest{(_questOffer.ChoiceRewards.Count > 1 ? $" (reward {i + 1})" : "")}##quest-reward-{i}")) ChooseQuestReward((uint)i);
        }
        ImGui.EndChild();
        if(_uiParityArmed&&_uiParityPanel=="quest-log")MarkUiParityFrameComplete();
        ImGui.End();
    }
}

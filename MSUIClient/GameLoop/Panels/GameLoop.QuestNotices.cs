using MSUIClient.Net;
using MSUIClient.Formats;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private HashSet<(ulong Owner, uint Item)>? _pendingQuestItemNotices;
    private readonly Dictionary<(uint Quest, uint Item), uint> _ownQuestItemProgress = [];

    private void ObserveOwnQuestItemProgress()
    {
        // Observe a complete object-update slice, using the same bags as the
        // quest tracker. Loading seeds the baseline without replaying progress.
        if (_pendingObjectParse is not null || _pendingObjectUpdates is not null ||
            !_entities.TryGet(LocalPlayerGuid, out WorldEntity player)) return;
        var present = new HashSet<(uint Quest, uint Item)>();
        foreach (uint questId in MergedOwnQuestLog().Select(q => q.QuestId))
        {
            if (!_questTemplates.TryGetValue(questId, out QuestTemplate? template)) continue;
            foreach (QuestLogObjective objective in template.Objectives)
            {
                if (objective.ItemId == 0 || objective.ItemCount == 0) continue;
                var key = (questId, objective.ItemId);
                present.Add(key);
                uint current = Math.Min(CarriedCount(objective.ItemId, player.Guid), objective.ItemCount);
                bool progressed = _ownQuestItemProgress.TryGetValue(key, out uint previous) && current > previous;
                _ownQuestItemProgress[key] = current;
                if (!progressed || _worldLoading) continue;
                string label = objective.Text.Length > 0 ? objective.Text :
                    _items?.TryGet(objective.ItemId, out ItemTemplate? item) == true && item is not null
                        ? item.Name : "...";
                ShowQuestNotice(player.Guid, $"{label}: {current}/{objective.ItemCount}");
                AutoWatchQuest(questId);
                EmitInterface("quest", "item-progress-notice", "DISPLAYED", player.Guid,
                    $"quest={questId};item={objective.ItemId};previous={previous};current={current};required={objective.ItemCount}");
            }
        }
        foreach (var key in _ownQuestItemProgress.Keys.Where(key => !present.Contains(key)).ToArray())
            _ownQuestItemProgress.Remove(key);
    }

    private void ApplyQuestNotice(Op opcode, byte[] body, ulong owner)
    {
        if (owner == 0 || (owner != LocalPlayerGuid && owner != ControlledGuid)) return;
        switch (opcode)
        {
            case Op.SMSG_QUESTUPDATE_ADD_KILL: ApplyOwnedQuestKill(body, owner); break;
            case Op.SMSG_QUESTUPDATE_ADD_ITEM: ApplyOwnedQuestItem(body, owner); break;
            case Op.SMSG_QUESTUPDATE_COMPLETE: ApplyOwnedQuestObjectiveComplete(body, owner); break;
            case Op.SMSG_QUESTGIVER_QUEST_COMPLETE: ApplyOwnedQuestComplete(body, owner); break;
            default: ApplyOwnedQuestError(opcode, body, owner); break;
        }
    }

    private void ShowQuestChat(ulong owner, string text) =>
        AddChatMessage(owner == ControlledGuid ? text : $"[{ResolveUnitName(owner)}] {text}");

    private void ShowQuestNotice(ulong owner, string text, bool error = false)
    {
        if (owner != ControlledGuid) ShowQuestChat(owner, text);
        else if (error) ShowUiError(text);
        else ShowUiInfo(text);
    }

    private void FlushQuestItemNotices(ulong owner)
    {
        if (_pendingQuestItemNotices is null || owner != ControlledGuid || owner == LocalPlayerGuid) return;
        if (MemberQuestEntries(owner).Any(e => !e.Rewarded && !_questTemplates.ContainsKey(e.QuestId)))
        { RequestPartyQuestFacts("quest-item-template-pending"); return; }
        foreach (var pending in _pendingQuestItemNotices.Where(p => p.Owner == owner).ToArray())
        {
            foreach (MemberQuestEntry entry in MemberQuestEntries(owner))
            {
                if (entry.Rewarded) continue;
                if (!_questTemplates.TryGetValue(entry.QuestId, out QuestTemplate? template))
                    continue;
                for (int i = 0; i < template.Objectives.Count && i < entry.ItemCounts.Length; i++)
                {
                    QuestLogObjective objective = template.Objectives[i];
                    if (objective.ItemId != pending.Item || objective.ItemCount == 0) continue;
                    string label = objective.Text.Length > 0 ? objective.Text :
                        _items?.TryGet(pending.Item, out ItemTemplate? item) == true && item is not null ? item.Name : "...";
                    ShowQuestNotice(owner, $"{label}: {Math.Min(entry.ItemCounts[i], objective.ItemCount)}/{objective.ItemCount}");
                    AutoWatchQuest(entry.QuestId);
                }
            }
            _pendingQuestItemNotices.Remove(pending);
        }
    }
}

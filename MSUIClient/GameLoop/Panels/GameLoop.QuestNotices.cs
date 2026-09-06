using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private HashSet<(ulong Owner, uint Item)>? _pendingQuestItemNotices;

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

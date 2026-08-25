using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

namespace MSUIClient;

/// <summary>
/// Party Quest Log (PLAN_20 P1). Owner decision 2026-08-25: real per-character
/// quest logs, MERGED in the view — the same concept as the shared bags. One row
/// per distinct quest across the whole party, one column per member, so "who has
/// this, who is done, who never picked it up" is one glance instead of five.
///
/// Every member's row data is server-pushed (SMSG_SUI_QUEST_LOG); the only column
/// that also reads local state is your own, where the vanilla update-field log is
/// authoritative for the twenty quests that have a slot and the push fills in
/// anything held past them.
/// </summary>
public sealed partial class GameLoop
{
    private bool _partyQuestLogOpen;
    private uint _partyQuestLogSelected;

    private const float PartyQuestTitleColumnWidth = 210f;
    private const float PartyQuestMemberColumnWidth = 84f;
    private const float PartyQuestRowHeight = 19f;

    /// <summary>
    /// One member's state for one quest, already merged from every source.
    /// Progress is resolved ONCE, per objective index, so no consumer has to
    /// re-derive which of the two sources applies — getting that wrong reads
    /// another quest's counters, which is exactly what it did before.
    /// </summary>
    private readonly record struct PartyQuestCell(
        bool Held, bool Complete, bool Failed, bool Overflow,
        uint[] Progress, int ObjectivesDone, int ObjectivesTotal);

    private void OpenPartyQuestLog()
    {
        _partyQuestLogOpen = true;
        RequestPartyQuestFacts("party quest log opened");
    }

    private void DrawPartyQuestLogPanel()
    {
        if (!_partyQuestLogOpen || _net is null || _gameplayArt is null) return;
        float scale = GameplayUiScale();

        List<(ulong Guid, string Name)> owners = [(LocalPlayerGuid, _net.PlayerName ?? "You")];
        foreach (PartyMember member in _partyMembers)
            owners.Add((member.Guid, member.Name));

        uint[] quests = PartyQuestRows(owners);

        float width = PartyQuestTitleColumnWidth +
            owners.Count * PartyQuestMemberColumnWidth + 46f;
        ImGui.SetNextWindowSize(new Vector2(width, 460f) * scale, ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(
            new Vector2(PartyQuestTitleColumnWidth + 120f, 260f) * scale,
            new Vector2(float.MaxValue, float.MaxValue));
        ImGui.SetNextWindowBgAlpha(0f);
        if (!ImGui.Begin("###party-quest-log", ref _partyQuestLogOpen,
                ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoBackground |
                ImGuiWindowFlags.NoCollapse))
        {
            ImGui.End();
            return;
        }
        DrawVanillaPanelChrome("Party Quest Log", scale, ref _partyQuestLogOpen);
        ImGui.Dummy(new Vector2(1, 10 * scale));

        ImGui.BeginChild("##pquest-grid", new Vector2(0, 0), false,
            ImGuiWindowFlags.HorizontalScrollbar);
        // Explicit-position layout under one captured origin — no SameLine flow,
        // so a long quest title can never staircase the member columns.
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        Vector2 c0 = ImGui.GetCursorScreenPos();
        float y = 0f;

        y = DrawPartyQuestHeader(dl, c0, y, scale, owners);
        y = DrawPartyQuestRows(dl, c0, y, scale, owners, quests);
        y = DrawPartyQuestDetail(dl, c0, y, scale, owners);

        // Draw-list output contributes nothing to ImGui's content size; this is
        // what lets the child scroll over manually placed rows.
        ImGui.SetCursorScreenPos(c0 + new Vector2(0, y) * scale);
        ImGui.Dummy(new Vector2(1, 1));
        ImGui.EndChild();
        ImGui.End();
    }

    /// <summary>The union of every owner's quest ids, ordered for a stable read.</summary>
    private uint[] PartyQuestRows(List<(ulong Guid, string Name)> owners)
    {
        var seen = new HashSet<uint>();
        foreach ((ulong guid, _) in owners)
        {
            foreach (MemberQuestEntry entry in MemberQuestEntries(guid))
                seen.Add(entry.QuestId);
            if (guid == LocalPlayerGuid)
                foreach ((_, uint questId, _, _) in LocalQuestLogEntries())
                    seen.Add(questId);
        }
        return [.. seen.OrderBy(id => _questTitles.GetValueOrDefault(id, $"Quest {id}"),
            StringComparer.OrdinalIgnoreCase)];
    }

    private IEnumerable<(byte Slot, uint QuestId, uint Counters, uint Timer)> LocalQuestLogEntries() =>
        _entities.TryGet(_net?.PlayerGuid ?? 0, out WorldEntity player)
            ? player.Fields.QuestLog() : [];

    private float DrawPartyQuestHeader(ImDrawListPtr dl, Vector2 c0, float y, float scale,
        List<(ulong Guid, string Name)> owners)
    {
        GameText.Draw(dl, "GameFontNormal", "Quest", c0 + new Vector2(0, y) * scale, scale);
        float x = PartyQuestTitleColumnWidth;
        foreach ((ulong guid, string name) in owners)
        {
            string label = GameText.EllipsizeToBox("GameFontNormalSmall",
                guid == LocalPlayerGuid ? "You" : name,
                PartyQuestMemberColumnWidth - 6f, PartyQuestRowHeight, scale);
            GameText.Draw(dl, "GameFontNormalSmall", label,
                c0 + new Vector2(x, y + 2f) * scale, scale, VanillaGold);
            x += PartyQuestMemberColumnWidth;
        }
        y += 18f;
        dl.AddLine(c0 + new Vector2(0, y) * scale,
            c0 + new Vector2(x, y) * scale, 0xff2a343d, MathF.Max(1f, scale));
        return y + 4f;
    }

    private float DrawPartyQuestRows(ImDrawListPtr dl, Vector2 c0, float y, float scale,
        List<(ulong Guid, string Name)> owners, uint[] quests)
    {
        if (quests.Length == 0)
        {
            ImGui.SetCursorScreenPos(c0 + new Vector2(0, y) * scale);
            ImGui.TextWrapped(_partyQuestFactsAvailable
                ? "No quests held by anyone in the party yet."
                : "This server has no party-quest-facts support — only your own log is readable.");
            return y + 40f;
        }

        float rowWidth = PartyQuestTitleColumnWidth + owners.Count * PartyQuestMemberColumnWidth;
        foreach (uint questId in quests)
        {
            Vector2 rowMin = c0 + new Vector2(0, y - 2f) * scale;
            var rowSize = new Vector2(rowWidth, PartyQuestRowHeight) * scale;
            ImGui.SetCursorScreenPos(rowMin);
            ImGui.InvisibleButton($"##pquest-row-{questId}", rowSize);
            bool hovered = ImGui.IsItemHovered();
            if (ImGui.IsItemClicked())
                _partyQuestLogSelected = _partyQuestLogSelected == questId ? 0 : questId;
            if (_partyQuestLogSelected == questId)
                dl.AddRectFilled(rowMin, rowMin + rowSize, 0x33d0b060);
            else if (hovered)
                dl.AddRectFilled(rowMin, rowMin + rowSize, 0x22ffffff);

            string title = GameText.EllipsizeToBox("GameFontNormalSmall",
                _questTitles.GetValueOrDefault(questId, $"Quest {questId}"),
                PartyQuestTitleColumnWidth - 8f, PartyQuestRowHeight, scale);
            GameText.Draw(dl, "GameFontNormalSmall", title,
                c0 + new Vector2(0, y) * scale, scale, PartyQuestTitleColor(questId));

            float x = PartyQuestTitleColumnWidth;
            foreach ((ulong guid, _) in owners)
            {
                PartyQuestCell cell = PartyQuestCellFor(guid, questId);
                GameText.Draw(dl, "GameFontNormalSmall", PartyQuestCellLabel(cell),
                    c0 + new Vector2(x, y) * scale, scale, PartyQuestCellColor(cell));
                x += PartyQuestMemberColumnWidth;
            }
            y += PartyQuestRowHeight + 2f;
        }
        return y + 6f;
    }

    /// <summary>Selected row: the per-member objective breakdown, which is the
    /// part a merged grid necessarily compresses away.</summary>
    private float DrawPartyQuestDetail(ImDrawListPtr dl, Vector2 c0, float y, float scale,
        List<(ulong Guid, string Name)> owners)
    {
        if (_partyQuestLogSelected == 0) return y;
        uint questId = _partyQuestLogSelected;
        y += 6f;
        dl.AddLine(c0 + new Vector2(0, y) * scale,
            c0 + new Vector2(PartyQuestTitleColumnWidth +
                owners.Count * PartyQuestMemberColumnWidth, y) * scale,
            0xff2a343d, MathF.Max(1f, scale));
        y += 8f;

        GameText.Draw(dl, "GameFontNormal",
            _questTitles.GetValueOrDefault(questId, $"Quest {questId}"),
            c0 + new Vector2(0, y) * scale, scale, VanillaGold);
        y += 20f;

        if (!_questTemplates.TryGetValue(questId, out QuestTemplate? template))
        {
            ImGui.SetCursorScreenPos(c0 + new Vector2(0, y) * scale);
            ImGui.TextDisabled("Retrieving quest details...");
            RequireQuestTemplate(questId);
            return y + 24f;
        }

        foreach ((ulong guid, string name) in owners)
        {
            PartyQuestCell cell = PartyQuestCellFor(guid, questId);
            if (!cell.Held) continue;
            GameText.Draw(dl, "GameFontNormalSmall",
                guid == LocalPlayerGuid ? "You" : name,
                c0 + new Vector2(0, y) * scale, scale, VanillaGold);
            y += 16f;
            foreach (string line in PartyQuestObjectiveLines(cell, template))
            {
                GameText.Draw(dl, "GameFontNormalSmall", line,
                    c0 + new Vector2(12f, y) * scale, scale, 0xff9aa4ab);
                y += 15f;
            }
            if (cell.Overflow)
            {
                GameText.Draw(dl, "GameFontNormalSmall",
                    "held past the quest-log slots", c0 + new Vector2(12f, y) * scale,
                    scale, 0xff9aa4ab);
                y += 15f;
            }
            y += 4f;
        }
        return y;
    }

    /// <summary>
    /// One member's state for one quest. Your own column merges the two sources:
    /// the vanilla update fields win where they have a slot (they are live, the
    /// push is a snapshot), and the pushed entry covers everything past them.
    /// </summary>
    private PartyQuestCell PartyQuestCellFor(ulong guid, uint questId)
    {
        MemberQuestEntry entry = default;
        bool held = false;
        foreach (MemberQuestEntry candidate in MemberQuestEntries(guid))
            if (candidate.QuestId == questId) { entry = candidate; held = true; break; }

        bool complete = held && entry.Complete;
        bool failed = held && entry.Failed;
        bool overflow = held && entry.Overflow;

        bool hasLocal = false;
        uint localCounters = 0;
        if (guid == LocalPlayerGuid)
        {
            foreach ((_, uint localId, uint counters, _) in LocalQuestLogEntries())
            {
                if (localId != questId) continue;
                byte state = (byte)(counters >> 24);
                held = true;
                hasLocal = true;
                localCounters = counters;
                complete = (state & 1) != 0;
                failed = (state & 2) != 0;
                overflow = false;              // it has a slot by definition
                break;
            }
        }

        var progress = new uint[QuestFactsWire.ObjectivesPerQuest];
        int done = 0, total = 0;
        if (held && _questTemplates.TryGetValue(questId, out QuestTemplate? template))
        {
            for (int i = 0; i < template.Objectives.Count && i < QuestFactsWire.ObjectivesPerQuest; i++)
            {
                QuestLogObjective objective = template.Objectives[i];
                bool kill = objective.CreatureOrGo != 0 && objective.RequiredCount > 0;
                bool collect = !kill && objective.ItemId != 0 && objective.ItemCount > 0;
                if (!kill && !collect) continue;

                // Which source applies is a property of WHOSE column this is,
                // decided here and nowhere else. Ours: the live packed counters
                // (six bits per objective) for kills, and our own carried stacks
                // for items — vanilla never stores item progress in the log.
                // A companion's: the wire, which carries the server-side counter
                // and is therefore neither capped at 63 nor bag-derived.
                if (guid == LocalPlayerGuid && hasLocal)
                    progress[i] = kill
                        ? (localCounters >> (6 * i)) & 0x3f
                        : CarriedCount(objective.ItemId);
                else
                    progress[i] = kill
                        ? (i < entry.ObjectiveCounts.Length ? entry.ObjectiveCounts[i] : 0u)
                        : (i < entry.ItemCounts.Length ? entry.ItemCounts[i] : 0u);

                total++;
                if (progress[i] >= (kill ? objective.RequiredCount : objective.ItemCount)) done++;
            }
        }
        return new PartyQuestCell(held, complete, failed, overflow, progress, done, total);
    }

    private IEnumerable<string> PartyQuestObjectiveLines(PartyQuestCell cell, QuestTemplate template)
    {
        bool any = false;
        for (int i = 0; i < template.Objectives.Count && i < QuestFactsWire.ObjectivesPerQuest; i++)
        {
            QuestLogObjective objective = template.Objectives[i];
            if (objective.CreatureOrGo != 0 && objective.RequiredCount > 0)
            {
                uint current = Math.Min(cell.Progress.Length > i ? cell.Progress[i] : 0u,
                    objective.RequiredCount);
                string label = objective.Text.Length > 0 ? objective.Text
                    : _creatureNames.GetValueOrDefault(objective.CreatureOrGo & 0x7fff_ffff,
                        $"Creature {objective.CreatureOrGo & 0x7fff_ffff}") + " slain";
                any = true;
                yield return $"{label}: {current}/{objective.RequiredCount}";
            }
            else if (objective.ItemId != 0 && objective.ItemCount > 0)
            {
                uint current = Math.Min(cell.Progress.Length > i ? cell.Progress[i] : 0u,
                    objective.ItemCount);
                string label = objective.Text.Length > 0 ? objective.Text
                    : _items?.TryGet(objective.ItemId, out ItemTemplate? proto) == true &&
                      proto is not null ? proto.Name : $"Item {objective.ItemId}";
                any = true;
                yield return $"{label}: {current}/{objective.ItemCount}";
            }
        }
        if (!any)
            yield return cell.Complete ? "Ready to turn in." : "No counted objectives.";
    }

    private static string PartyQuestCellLabel(in PartyQuestCell cell)
    {
        if (!cell.Held) return "—";
        if (cell.Failed) return "failed";
        if (cell.Complete) return "done";
        return cell.ObjectivesTotal > 0
            ? $"{cell.ObjectivesDone}/{cell.ObjectivesTotal}"
            : "on it";
    }

    private static uint PartyQuestCellColor(in PartyQuestCell cell)
    {
        if (!cell.Held) return 0xff5a646b;
        if (cell.Failed) return 0xff4040ff;      // ABGR — red
        if (cell.Complete) return VanillaGold;
        return 0xffd8e0e6;
    }

    private uint PartyQuestTitleColor(uint questId) =>
        _questTemplates.TryGetValue(questId, out QuestTemplate? template) &&
        _entities.TryGet(_net?.PlayerGuid ?? 0, out WorldEntity player)
            ? ImGui.ColorConvertFloat4ToU32(
                QuestFrameUiLaw.QuestDifficultyColor(player.Level, template.Level))
            : 0xffd8e0e6;
}

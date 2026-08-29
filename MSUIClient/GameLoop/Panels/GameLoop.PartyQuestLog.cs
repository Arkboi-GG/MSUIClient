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
    /// <param name="KillProgress">Per index, creature/GO progress.</param>
    /// <param name="ItemProgress">Per index, required-item progress. SEPARATE from
    /// KillProgress on purpose: vanilla's two objective arrays are independent and
    /// the same index may carry one of each, so a single bank per index silently
    /// discarded whichever came second.</param>
    private readonly record struct PartyQuestCell(
        bool Held, bool Complete, bool Failed, bool Overflow, bool Rewarded,
        uint[] KillProgress, uint[] ItemProgress, int ObjectivesDone, int ObjectivesTotal);

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

        // Drop the data clear of the header plaque (256x64 art hanging 12px above the
        // frame top, so its lower edge sits ~52px down) with a real margin — it used
        // to sit jammed a bare 10px under the title.
        Vector2 wMin = ImGui.GetWindowPos();
        Vector2 wMax = wMin + ImGui.GetWindowSize();
        float plaqueBottom = wMin.Y + 52f * scale;
        ImGui.Dummy(new Vector2(1, MathF.Max(6f * scale,
            plaqueBottom + 8f * scale - ImGui.GetCursorScreenPos().Y)));
        Vector2 gridTop = ImGui.GetCursorScreenPos();

        // A denser scrim behind the data. DrawVanillaPanelChrome's Dialog backdrop is
        // flat black at 60% alpha, which a bright outdoor scene reads straight through;
        // this near-black fill inside the riveted border lifts the dim text off the
        // world. Added after the chrome (so it layers over the translucent base) but
        // kept below the plaque so it never paints over the title art.
        float edge = 11f * scale;
        ImGui.GetWindowDrawList().AddRectFilled(
            new Vector2(wMin.X + edge, gridTop.Y - 4f * scale),
            new Vector2(wMax.X - edge, wMax.Y - edge), 0xc00b0e12);

        ImGui.BeginChild("##pquest-grid", new Vector2(0, 0), false,
            ImGuiWindowFlags.HorizontalScrollbar);
        // Explicit-position layout under one captured origin — no SameLine flow,
        // so a long quest title can never staircase the member columns.
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        // Centre the fixed-width grid when the window is wider than it; when the party
        // overflows that width, centre-pad collapses to zero and the horizontal
        // scrollbar carries the grid, pinned left.
        float contentWidth =
            (PartyQuestTitleColumnWidth + owners.Count * PartyQuestMemberColumnWidth) * scale;
        float centerPad = MathF.Max(0f, (ImGui.GetContentRegionAvail().X - contentWidth) * 0.5f);
        Vector2 c0 = ImGui.GetCursorScreenPos() + new Vector2(centerPad, 0f);
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
            {
                bool textOpenOnThis = _giverQuestTextOpen && _giverQuestTextFromPartyLog &&
                    _giverQuestTextQuestId == questId;
                _partyQuestLogSelected = questId;
                _giverQuestTextQuestId = questId;
                _giverQuestTextFromPartyLog = true;
                _giverQuestTextOpen = !textOpenOnThis;
                RequireQuestTemplate(questId);
            }
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
                GameText.Draw(dl, "GameFontNormalSmall", PartyQuestCellLabel(guid, cell),
                    c0 + new Vector2(x, y) * scale, scale, PartyQuestCellColor(guid, cell));
                x += PartyQuestMemberColumnWidth;
            }
            y += PartyQuestRowHeight + 2f;
        }

        // Name the gap rather than letting a column of "?" imply an empty log.
        bool anyUntold = owners.Any(o => o.Guid != LocalPlayerGuid && !HasMemberQuestFacts(o.Guid));
        if (anyUntold)
        {
            GameText.Draw(dl, "GameFontNormalSmall",
                "?  the server does not report this member's quests",
                c0 + new Vector2(0, y + 4f) * scale, scale, 0xff9aa4ab);
            y += 18f;
        }

        // State our own freshness. These facts arrive on a roster edge, after a
        // party act, and on the poll this panel runs while it is open -- none of
        // which is a guarantee, and MemberQuestLogAge existed for exactly this
        // and had no consumer at all, so the grid silently presented arbitrarily
        // old counters as if they were live.
        double oldest = -1.0;
        foreach ((ulong guid, _) in owners)
        {
            double age = MemberQuestLogAge(guid);
            if (age >= 0.0 && age > oldest) oldest = age;
        }
        if (oldest >= 0.0)
        {
            GameText.Draw(dl, "GameFontNormalSmall",
                oldest < 5.0 ? "updated just now" : $"updated {(int)oldest}s ago",
                c0 + new Vector2(0, y + 4f) * scale, scale, 0xff7f888f);
            y += 18f;
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
        bool rewarded = held && entry.Rewarded;

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

        var killProgress = new uint[QuestFactsWire.ObjectivesPerQuest];
        var itemProgress = new uint[QuestFactsWire.ObjectivesPerQuest];
        int done = 0, total = 0;
        if (held && _questTemplates.TryGetValue(questId, out QuestTemplate? template))
        {
            for (int i = 0; i < template.Objectives.Count && i < QuestFactsWire.ObjectivesPerQuest; i++)
            {
                QuestLogObjective objective = template.Objectives[i];
                // Vanilla's creature and item objective arrays are INDEPENDENT:
                // the same index can carry a kill AND a collect objective, and
                // 89 quest/index pairs in the shipped world DB do. Treating the
                // index as either/or threw away the item counter the server had
                // already sent and counted the quest as one objective short.
                bool kill = objective.CreatureOrGo != 0 && objective.RequiredCount > 0;
                bool collect = objective.ItemId != 0 && objective.ItemCount > 0;
                if (!kill && !collect) continue;

                // Which source applies is a property of WHOSE column this is,
                // decided here and nowhere else. Ours: the live packed counters
                // (six bits per objective) for kills, and our own carried stacks
                // for items — vanilla never stores item progress in the log.
                // A companion's: the wire, which carries the server-side counter
                // and is therefore neither capped at 63 nor bag-derived.
                bool own = guid == LocalPlayerGuid && hasLocal;
                if (kill)
                {
                    killProgress[i] = own
                        ? (localCounters >> (6 * i)) & 0x3f
                        : (i < entry.ObjectiveCounts.Length ? entry.ObjectiveCounts[i] : 0u);
                    total++;
                    if (killProgress[i] >= objective.RequiredCount) done++;
                }
                if (collect)
                {
                    itemProgress[i] = own
                        ? CarriedCount(objective.ItemId)
                        : (i < entry.ItemCounts.Length ? entry.ItemCounts[i] : 0u);
                    total++;
                    if (itemProgress[i] >= objective.ItemCount) done++;
                }
            }
        }
        return new PartyQuestCell(held, complete, failed, overflow, rewarded,
            killProgress, itemProgress, done, total);
    }

    private IEnumerable<string> PartyQuestObjectiveLines(PartyQuestCell cell, QuestTemplate template)
    {
        bool any = false;
        for (int i = 0; i < template.Objectives.Count && i < QuestFactsWire.ObjectivesPerQuest; i++)
        {
            QuestLogObjective objective = template.Objectives[i];
            bool kill = objective.CreatureOrGo != 0 && objective.RequiredCount > 0;
            if (kill)
            {
                uint current = Math.Min(cell.KillProgress.Length > i ? cell.KillProgress[i] : 0u,
                    objective.RequiredCount);
                string label = objective.Text.Length > 0 ? objective.Text
                    : _creatureNames.GetValueOrDefault(objective.CreatureOrGo & 0x7fff_ffff,
                        $"Creature {objective.CreatureOrGo & 0x7fff_ffff}") + " slain";
                any = true;
                yield return $"{label}: {current}/{objective.RequiredCount}";
            }
            // NOT else-if: one index can carry a kill and a collect objective.
            if (objective.ItemId != 0 && objective.ItemCount > 0)
            {
                uint current = Math.Min(cell.ItemProgress.Length > i ? cell.ItemProgress[i] : 0u,
                    objective.ItemCount);
                // ObjectiveText[i] belongs to the CREATURE objective at this index
                // (that is the pairing the quest query uses), so when the index
                // carries both, the collect line must not repeat the kill's label.
                string label = kill ? "" : objective.Text;
                if (label.Length == 0)
                    label = _items?.TryGet(objective.ItemId, out ItemTemplate? proto) == true &&
                      proto is not null ? proto.Name : $"Item {objective.ItemId}";
                any = true;
                yield return $"{label}: {current}/{objective.ItemCount}";
            }
        }
        if (!any)
            yield return cell.Complete ? "Ready to turn in." : "No counted objectives.";
    }

    /// <summary>
    /// The cell label. "not told" and "told, holds nothing" are DIFFERENT facts
    /// and used to render as the same em-dash — which meant a human party member,
    /// whom the server never describes (the push is AiBot-only), read exactly like
    /// a companion holding no quests. The panel now says only what it knows.
    /// </summary>
    private string PartyQuestCellLabel(ulong guid, in PartyQuestCell cell)
    {
        if (!cell.Held)
            return guid == LocalPlayerGuid || HasMemberQuestFacts(guid) ? "—" : "?";
        // Rewarded carries the Complete flag too, so it must win over "done"
        // (which here means complete-but-not-yet-turned-in).
        if (cell.Rewarded) return "completed";
        if (cell.Failed) return "failed";
        if (cell.Complete) return "done";
        return cell.ObjectivesTotal > 0
            ? $"{cell.ObjectivesDone}/{cell.ObjectivesTotal}"
            : "on it";
    }

    private uint PartyQuestCellColor(ulong guid, in PartyQuestCell cell)
    {
        if (!cell.Held)
            return guid == LocalPlayerGuid || HasMemberQuestFacts(guid) ? 0xff5a646b : 0xff3f4750;
        if (cell.Rewarded) return 0xff5ab45a;    // ABGR — soft green: turned in & done
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

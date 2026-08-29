using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

namespace MSUIClient;

/// <summary>
/// The companion rail beside the questgiver frame (PLAN_20 P3): who comes with
/// you on this quest, and — on a turn-in — which reward each of them takes.
///
/// It is a SEPARATE window sitting to the right of the 384-wide quest frame, and
/// it is deliberately painted in the SuperUI dialog skin rather than FrameXML
/// parchment. Nothing it draws overlaps x ∈ [0, 384], so no vanilla element
/// moves and the quest frame's parity element tree is untouched — and dressing
/// commander furniture as vanilla art would invite a parity claim it could never
/// satisfy. While a UI-parity proof is armed the rail does not draw at all.
/// </summary>
public sealed partial class GameLoop
{
    private const float QuestRailGap = 6f;
    private const float QuestRailX = QuestFrameUiLaw.Width + QuestRailGap;   // 390
    private const float QuestRailTop = QuestFrameUiLaw.ScrollY;              // 81
    private const float QuestRailWidth = 150f;
    private const float QuestRailHeaderHeight = 18f;
    private const float QuestRailRowPitch = 36f;

    private const float QuestRewardBoardPad = 10f;
    private const float QuestRewardColumnWidth = 47f;
    /// <summary>Left gutter naming each choice row's item. Every member is being
    /// offered the SAME choice list, so the name belongs once per row rather than
    /// crammed into every cell — and without it the board was five identical
    /// unlabelled boxes.</summary>
    private const float QuestRewardNameColumnWidth = 116f;
    private const float QuestRewardHeadHeight = 16f;
    private const float QuestRewardMemberHeight = 32f;

    /// <summary>Companions ticked for the next act. Sticky across frames so a
    /// deselection survives the panel redrawing every frame.</summary>
    private readonly HashSet<ulong> _questRailExcluded = [];

    /// <summary>Per-member reward pick for the current offer; absent = auto.</summary>
    private readonly Dictionary<ulong, byte> _questRailRewardChoice = [];

    private void ResetQuestPartyRail()
    {
        _questRailExcluded.Clear();
        _questRailRewardChoice.Clear();
    }

    /// <summary>The companions this rail offers, in party order.</summary>
    private List<(ulong Guid, string Name)> QuestRailMembers() => PartyQuestCandidates();

    private bool QuestRailIncluded(ulong guid) => !_questRailExcluded.Contains(guid);

    private List<PartyQuestSubject> QuestRailSubjects(bool withRewards)
    {
        var subjects = new List<PartyQuestSubject>();
        foreach ((ulong guid, _) in QuestRailMembers())
        {
            if (!QuestRailIncluded(guid)) continue;
            byte choice = withRewards && _questRailRewardChoice.TryGetValue(guid, out byte pick)
                ? pick : PartyQuestWire.RewardChoiceAuto;
            subjects.Add(new PartyQuestSubject(guid, choice));
        }
        return subjects;
    }

    private void DrawQuestPartyRail()
    {
        if (_uiParityArmed) return;              // never perturb a parity proof
        if (!_partyQuestActsAvailable) return;
        // [SUI] P4b: while DRIVING one character the quest frame is that character's
        // alone — the party "accept for (N)" rail belongs to the commander view, not
        // direct control. Drawing it while possessing is what read as "why is it
        // asking about Blackfel when I'm someone else". The commander-view flow
        // (free view) is where the per-member board lives.
        if (_controlState == ControlState.Possessing) return;
        // Free view is the commander board's turf (DrawGiverQuestsWindow owns the
        // per-member cards + "Accept for party"/"Turn in" there). The comment above
        // always said so, but nothing enforced it — so the old "Send with you" rail
        // drew a SECOND, redundant party surface beside the vanilla quest frame (even
        // with a different member count). Embodied play still uses this rail.
        if (_freeView) return;
        QuestNpcPanel panel = QuestNpcPanelNow();
        if (panel is QuestNpcPanel.None or QuestNpcPanel.Greeting) return;

        List<(ulong Guid, string Name)> members = QuestRailMembers();
        if (members.Count == 0) return;

        uint questId = _questOffer?.QuestId ?? _questRequestItems?.QuestId ??
            _questDetails?.QuestId ?? 0;
        if (questId == 0) return;

        float s = GameplayUiScale();
        Vector2 origin = UiPanelFrameOrigin(UiPanelOwnershipRegistry[7], s);
        bool reward = panel == QuestNpcPanel.Reward;

        int choices = reward
            ? Math.Min(_questOffer?.ChoiceRewards.Count ?? 0, QuestFrameUiLaw.MaxItems) : 0;
        int included = QuestRailSubjects(withRewards: false).Count;
        // ONE resolution point for the questgiver. The four panel states are
        // mutually exclusive and each nulls the others, so reading only two of
        // the three records silently yielded 0 on the reward panel.
        ulong giver = _questOffer?.GiverGuid ?? _questRequestItems?.GiverGuid ??
            _questDetails?.GiverGuid ?? 0;
        if (giver == 0) return;                  // nothing to act on, and no window open yet
        float width = reward && choices > 0
            ? 2 * QuestRewardBoardPad + QuestRewardNameColumnWidth +
              Math.Max(1, included) * QuestRewardColumnWidth
            : QuestRailWidth;
        float height = QuestRailHeaderHeight + members.Count * QuestRailRowPitch + 30f;
        if (reward && choices > 0)
            height = QuestRewardHeadHeight + QuestRewardMemberHeight +
                choices * QuestRewardColumnWidth + 30f;

        ImGui.SetNextWindowPos(origin + new Vector2(QuestRailX, QuestRailTop) * s,
            ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(width, height) * s, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);
        if (!ImGui.Begin("##quest-party-rail", ImGuiWindowFlags.NoTitleBar |
                ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoCollapse |
                ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
                ImGuiWindowFlags.NoScrollbar))
        {
            ImGui.End();
            return;
        }

        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        Vector2 min = ImGui.GetWindowPos();
        _skin?.DrawBackdrop(dl, min, min + ImGui.GetWindowSize(), WowSkin.Dialog);
        Vector2 c0 = min + new Vector2(8f, 8f) * s;

        if (reward && choices > 0)
            DrawQuestRewardBoard(dl, c0, s, members, choices, questId, giver);
        else
            DrawQuestRailRoster(dl, c0, s, members, panel, questId, giver);

        ImGui.End();
    }

    /// <summary>Greeting/Detail/Progress: who comes along, and one act button.</summary>
    private void DrawQuestRailRoster(ImDrawListPtr dl, Vector2 c0, float s,
        List<(ulong Guid, string Name)> members, QuestNpcPanel panel, uint questId,
        ulong giver)
    {
        bool accepting = panel == QuestNpcPanel.Detail;
        // Progress is a waypoint, not a turn-in surface. Offering "Turn in all"
        // here forced auto-pick for every member and made the reward board --
        // the whole point of the phase -- skippable by the button shown first.
        bool acting = accepting || panel == QuestNpcPanel.Reward;
        GameText.Draw(dl, "GameFontNormalSmall",
            accepting ? "Send with you" : "Turning in with you",
            c0, s, VanillaGold);
        float y = QuestRailHeaderHeight;

        foreach ((ulong guid, string name) in members)
        {
            Vector2 rowMin = c0 + new Vector2(0, y) * s;
            var rowSize = new Vector2(QuestRailWidth - 16f, 34f) * s;
            ImGui.SetCursorScreenPos(rowMin);
            ImGui.InvisibleButton($"##quest-rail-{guid}", rowSize);
            if (ImGui.IsItemClicked())
            {
                if (!_questRailExcluded.Remove(guid)) _questRailExcluded.Add(guid);
            }

            bool included = QuestRailIncluded(guid);
            Vector2 boxMin = rowMin + new Vector2(2f, 10f) * s;
            var boxSize = new Vector2(14f, 14f) * s;
            dl.AddRect(boxMin, boxMin + boxSize, 0xff2a343d, 0f, ImDrawFlags.None,
                MathF.Max(1f, s));
            if (included)
                dl.AddRectFilled(boxMin + new Vector2(3f, 3f) * s,
                    boxMin + boxSize - new Vector2(3f, 3f) * s, VanillaGold);

            DrawQuestRailPortrait(dl, guid, rowMin + new Vector2(22f, 1f) * s, 32f * s);

            uint tint = included ? 0xffd8e0e6 : 0xff5a646b;
            GameText.Draw(dl, "GameFontNormalSmall",
                GameText.EllipsizeToBox("GameFontNormalSmall", name, 82f, 12f, s),
                rowMin + new Vector2(58f, 2f) * s, s, tint);
            GameText.Draw(dl, "GameFontNormalSmall", QuestRailVerdict(guid, questId),
                rowMin + new Vector2(58f, 17f) * s, s, 0xff9aa4ab);
            y += QuestRailRowPitch;
        }

        int count = QuestRailSubjects(withRewards: false).Count;
        Vector2 buttonMin = c0 + new Vector2(0, y + 4f) * s;
        var buttonSize = new Vector2(QuestRailWidth - 16f, 22f);
        if (!acting)
        {
            GameText.Draw(dl, "GameFontNormalSmall", "Turn in on the reward page.",
                buttonMin, s, 0xff9aa4ab);
            return;
        }
        string caption = accepting ? $"Accept for party ({count})" : $"Turn in all ({count})";
        if (VanillaButton(dl, "##quest-party-act", caption, buttonMin, buttonSize, s,
                enabled: count > 0) && count > 0)
        {
            RequestPartyQuestAct(
                accepting ? PartyQuestWire.ActionAccept : PartyQuestWire.ActionTurnIn,
                questId, giver, QuestRailSubjects(withRewards: false));
        }
    }

    /// <summary>
    /// Reward panel: one column per included companion, one row per choice —
    /// every member's picker visible at once, which is the owner's decision.
    /// Your own picker stays exactly where vanilla puts it, untouched.
    /// </summary>
    private void DrawQuestRewardBoard(ImDrawListPtr dl, Vector2 c0, float s,
        List<(ulong Guid, string Name)> members, int choices, uint questId, ulong giver)
    {
        GameText.Draw(dl, "GameFontNormalSmall", "Their rewards", c0, s, VanillaGold);

        var included = members.Where(m => QuestRailIncluded(m.Guid)).ToList();

        // Name every choice row down the left gutter. The vanilla reward panel
        // beside us queries these same templates, but the rail must not depend on
        // another panel's draw order for its own labels to resolve.
        for (int k = 0; k < choices; k++)
        {
            QuestRewardItem labelRow = _questOffer!.ChoiceRewards[k];
            if (_items is not null && _net is not null)
                _items.Require(labelRow.ItemId, giver, _net);
            (string rewardName, uint rewardColor) = QuestRewardName(labelRow);
            float labelY = QuestRewardHeadHeight + QuestRewardMemberHeight +
                k * QuestRewardColumnWidth + (QuestFrameUiLaw.ItemIcon - 10f) / 2f;
            GameText.Draw(dl, "GameFontNormalSmall",
                GameText.EllipsizeToBox("GameFontNormalSmall", rewardName,
                    QuestRewardNameColumnWidth - 10f, 11f, s),
                c0 + new Vector2(0f, labelY) * s, s, rewardColor);
        }

        float colX = QuestRewardNameColumnWidth + QuestRewardBoardPad - 8f;
        foreach ((ulong guid, string name) in included)
        {
            DrawQuestRailPortrait(dl, guid,
                c0 + new Vector2(colX + 11.5f, QuestRewardHeadHeight) * s, 24f * s);
            GameText.Draw(dl, "GameFontNormalSmall",
                GameText.EllipsizeToBox("GameFontNormalSmall", name, 45f, 11f, s),
                c0 + new Vector2(colX, QuestRewardHeadHeight + 25f) * s, s, 0xffd8e0e6);

            for (int k = 0; k < choices; k++)
            {
                Vector2 cellMin = c0 + new Vector2(colX + 4f,
                    QuestRewardHeadHeight + QuestRewardMemberHeight +
                    k * QuestRewardColumnWidth) * s;
                var cellSize = new Vector2(QuestFrameUiLaw.ItemIcon, QuestFrameUiLaw.ItemIcon) * s;
                ImGui.SetCursorScreenPos(cellMin);
                ImGui.InvisibleButton($"##quest-party-choice-{guid}-{k}", cellSize);
                if (ImGui.IsItemClicked()) _questRailRewardChoice[guid] = (byte)k;

                // Same icon resolution the vanilla reward row uses: the item's
                // own IconPath unless the offer carried an explicit display id.
                QuestRewardItem row = _questOffer!.ChoiceRewards[k];
                string iconPath = QuestRewardIconPath(row);
                uint icon = iconPath.Length > 0 ? _gameplayArt?.Handle(iconPath) ?? 0 : 0;
                if (icon != 0) dl.AddImage((nint)icon, cellMin, cellMin + cellSize);
                else dl.AddRectFilled(cellMin, cellMin + cellSize, 0x55101418);
                if (row.Count > 1)
                    GameText.DrawRightAligned(dl, "NumberFontNormal", row.Count.ToString(),
                        cellMin + QuestFrameUiLaw.ItemCountAnchor * s, s);

                bool picked = _questRailRewardChoice.TryGetValue(guid, out byte pick) && pick == k;
                dl.AddRect(cellMin, cellMin + cellSize,
                    picked ? VanillaGold : 0xff2a343d, 0f, ImDrawFlags.None,
                    MathF.Max(1f, (picked ? 2f : 1f) * s));
                if (ImGui.IsItemHovered())
                {
                    if (_items?.TryGet(row.ItemId, out ItemTemplate? item) == true && item is not null)
                        OfferPreparedItemTooltip(
                            new GameTooltipOwnerKey($"item:quest-party-rail:{questId}:{guid}",
                                (ulong)(k + 1)),
                            // This column's reward is THIS member's: judge proficiency red against
                            // them, not the commander. A non-local member paints no red (we only
                            // know the login character's proficiencies client-side).
                            PrepareItemTooltipBodySnapshot(item, row.Count, ownerGuid: guid));
                    else HoverTip("Retrieving item information...");
                }
            }
            colX += QuestRewardColumnWidth;
        }

        // Members with no explicit pick are sent as "auto" — the server's own
        // spec-aware chooser, which is the same one the fleet uses unattended.
        int autos = included.Count(m => !_questRailRewardChoice.ContainsKey(m.Guid));
        float y = QuestRewardHeadHeight + QuestRewardMemberHeight + choices * QuestRewardColumnWidth;
        if (autos > 0)
            GameText.Draw(dl, "GameFontNormalSmall",
                autos == included.Count ? "all on auto-pick" : $"{autos} on auto-pick",
                c0 + new Vector2(0, y - 14f) * s, s, 0xff9aa4ab);

        Vector2 buttonMin = c0 + new Vector2(0, y + 4f) * s;
        var buttonSize = new Vector2(Math.Max(90f, included.Count * QuestRewardColumnWidth), 22f);
        if (VanillaButton(dl, "##quest-party-turnin", $"Turn in all ({included.Count})",
                buttonMin, buttonSize, s, enabled: included.Count > 0) && included.Count > 0)
            RequestPartyQuestAct(PartyQuestWire.ActionTurnIn, questId, giver,
                QuestRailSubjects(withRewards: true));
    }

    /// <summary>
    /// Exactly the resolution order the vanilla reward row uses
    /// (<see cref="DrawQuestItemRow"/>): the display id the OFFER carried wins,
    /// the item template's own icon is the fallback, and the question mark is the
    /// floor. This used to return "" whenever the display id was non-zero — which
    /// SMSG_QUESTGIVER_OFFER_REWARD always sets — so the board drew a grey box for
    /// every reward and the player could not tell the choices apart.
    /// </summary>
    private string QuestRewardIconPath(in QuestRewardItem row)
    {
        string? fromDisplay = _items?.IconForDisplay(row.DisplayId);
        if (!string.IsNullOrEmpty(fromDisplay)) return fromDisplay;
        return _items?.TryGet(row.ItemId, out ItemTemplate? item) == true && item is not null
            ? item.IconPath : @"Interface\Icons\INV_Misc_QuestionMark.blp";
    }

    /// <summary>The reward's name and quality colour, or a placeholder while the
    /// item query is still in flight.</summary>
    private (string Name, uint Color) QuestRewardName(in QuestRewardItem row)
    {
        if (_items?.TryGet(row.ItemId, out ItemTemplate? item) == true && item is not null)
            return (item.Name, ImGui.ColorConvertFloat4ToU32(ItemQualityColor(item.Quality)));
        return ("...", 0xffd8e0e6);
    }

    private void DrawQuestRailPortrait(ImDrawListPtr dl, ulong guid, Vector2 min, float size)
    {
        uint art = PartyColumnPortraitHandle(guid);
        var max = min + new Vector2(size, size);
        if (art != 0) dl.AddImage((nint)art, min, max, new Vector2(0, 1), new Vector2(1, 0));
        else dl.AddRectFilled(min, max, 0x55101418);
        DrawClassPortraitBorderRect(dl, min, max, guid, GameplayUiScale());
    }

    /// <summary>
    /// What we can HONESTLY say about this member and this quest. Everything here
    /// comes from their pushed quest log; eligibility to accept is the server's
    /// answer to give, so the rail never predicts it.
    /// </summary>
    private string QuestRailVerdict(ulong guid, uint questId)
    {
        foreach (MemberQuestEntry entry in MemberQuestEntries(guid))
        {
            if (entry.QuestId != questId) continue;
            if (entry.Rewarded) return "completed";   // carries Complete too - check first
            if (entry.Failed) return "failed it";
            if (entry.Complete) return "ready to turn in";
            return entry.Overflow ? "on it (past slots)" : "on it";
        }
        return HasMemberQuestFacts(guid) ? "not in their log" : "log not synced";
    }
}

using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Net;
using MSUIClient.World.Units;

namespace MSUIClient;

/// <summary>
/// PLAN_20 Model B: the free-view COMMANDER quest window. Right-click a questgiver
/// from the sky without possessing anyone, and the server answers which quests it
/// offers/ends and each party member's eligibility (SMSG_SUI_GIVER_QUESTS). This
/// draws one card per member per quest — who can take it, who is on it, who is ready
/// to turn in, and who is blocked and why — and accepts / turns in for the party via
/// the P3 acts wire. No possession, no main standing at the NPC.
/// </summary>
public sealed partial class GameLoop
{
    private bool _partyGiverQuestsAvailable;
    private bool _giverQuestsOpen;
    private ulong _giverQuestsGiverGuid;
    private GiverQuestsReply _giverQuestsReply;
    private double _giverQuestsPulledAt;
    private const double GiverQuestsPullMinIntervalSeconds = 1.0;

    /// <summary>Per (quest, member) reward pick for a ready turn-in; absent = auto
    /// (the server's spec-aware chooser). Owner decision 2: rewards are per bot.</summary>
    private readonly Dictionary<(uint Quest, ulong Member), byte> _giverQuestRewardPick = [];

    /// <summary>The quest whose text pane is open (owner 2026-08-27: clicking a
    /// title pops a simplified, read-only WoW-style page — no accept/decline
    /// buttons, because acting happens from the commander window itself).</summary>
    private uint _giverQuestTextQuestId;
    private bool _giverQuestTextOpen;

    private void ApplyPartyGiverQuestsCapability(uint capabilities)
    {
        bool available = (capabilities & SuiCapabilityWire.PartyGiverQuestsV1) != 0;
        if (available != _partyGiverQuestsAvailable)
            Console.WriteLine(available
                ? "[giver-quests] server advertised party-giver-quests-v1"
                : "[giver-quests] server has no party-giver-quests-v1 advertisement");
        _partyGiverQuestsAvailable = available;
    }

    private void ResetPartyGiverQuests()
    {
        _partyGiverQuestsAvailable = false;
        _giverQuestsOpen = false;
        _giverQuestsGiverGuid = 0;
        _giverQuestsReply = default;
        _giverQuestsPulledAt = 0;
        _giverQuestRewardPick.Clear();
        _giverQuestTextQuestId = 0;
        _giverQuestTextOpen = false;
    }

    /// <summary>Open the commander quest window on a giver: request the fresh
    /// per-member eligibility and show the window (last answer stays until it
    /// arrives, so re-opening the same giver never flashes empty).</summary>
    private void RequestGiverQuests(ulong giver)
    {
        if (!_partyGiverQuestsAvailable || _net is not { IsInWorld: true } || giver == 0) return;
        _giverQuestsGiverGuid = giver;
        _giverQuestsOpen = true;
        double now = NowSeconds();
        if (now - _giverQuestsPulledAt < GiverQuestsPullMinIntervalSeconds) return;
        if (_net.SuiGiverQuests(giver)) _giverQuestsPulledAt = now;
    }

    private void ApplySuiGiverQuests(byte[] body)
    {
        if (!GiverQuestsWire.TryParse(body, out GiverQuestsReply reply))
        {
            EmitInterface("giver-quests", "reply", "MALFORMED", 0, $"bytes={body.Length}");
            return;
        }
        _giverQuestsReply = reply;
        if (_giverQuestsGiverGuid == 0) _giverQuestsGiverGuid = reply.Giver;
        foreach (GiverQuestEntry q in reply.Quests) RequireQuestTemplate(q.QuestId);
        EmitInterface("giver-quests", "reply", "APPLIED", reply.Giver,
            $"quests={reply.Quests.Length}");
    }

    /// <summary>The giver's real name for the plaque. A CREATURE's name resolves by
    /// ENTRY through the creature query (fired here when missing) — never the
    /// raw-guid fallback ("unit F13..."), which is debug text, not a title.</summary>
    private string GiverDisplayName(ulong guid)
    {
        if (_entities.TryGet(guid, out WorldEntity unit))
        {
            EnsureUnitNameRequested(unit);
            if (unit.IsPlayer) return ResolveUnitName(guid);
            string name = ResolveCreatureOrPetName(unit, "");
            if (name.Length > 0) return name;
        }
        return "Questgiver";
    }

    /// <summary>The members this window speaks for: yourself first, then the party.</summary>
    private List<(ulong Guid, string Name)> GiverQuestOwners()
    {
        var owners = new List<(ulong, string)> { (LocalPlayerGuid, _net?.PlayerName ?? "You") };
        foreach (PartyMember member in _partyMembers) owners.Add((member.Guid, member.Name));
        return owners;
    }

    private static string GiverQuestVerdictLabel(byte v) => v switch
    {
        GiverQuestsWire.CanTake => "can accept",
        GiverQuestsWire.OnIt => "on it",
        GiverQuestsWire.Ready => "ready to turn in",
        GiverQuestsWire.Done => "done",
        GiverQuestsWire.NeedsPrereq => "needs an earlier quest",
        GiverQuestsWire.LowLevel => "level too low",
        GiverQuestsWire.WrongRaceClass => "wrong race/class",
        GiverQuestsWire.LowSkillRep => "skill/reputation too low",
        GiverQuestsWire.LogFull => "quest log full",
        _ => "can't take it",
    };

    private static uint GiverQuestVerdictColor(byte v) => v switch
    {
        GiverQuestsWire.CanTake => 0xff6fce6f,   // ABGR green
        GiverQuestsWire.Ready => VanillaGold,
        GiverQuestsWire.OnIt => 0xffd8e0e6,
        GiverQuestsWire.Done => 0xff7f888f,
        _ => 0xff4f5fe0,                          // reddish — blocked
    };

    private void DrawGiverQuestsWindow()
    {
        if (!_giverQuestsOpen || _net is null || _gameplayArt is null) return;
        if (_uiParityArmed) return;
        float scale = GameplayUiScale();

        List<(ulong Guid, string Name)> owners = GiverQuestOwners();
        string giverName = GiverDisplayName(_giverQuestsGiverGuid);

        ImGui.SetNextWindowSize(new Vector2(460, 480) * scale, ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(320, 240) * scale,
            new Vector2(float.MaxValue, float.MaxValue));
        ImGui.SetNextWindowBgAlpha(0f);
        if (!ImGui.Begin("###giver-quests", ref _giverQuestsOpen,
                ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoBackground |
                ImGuiWindowFlags.NoCollapse))
        {
            ImGui.End();
            return;
        }
        DrawVanillaPanelChrome(giverName, scale, ref _giverQuestsOpen);

        Vector2 wMin = ImGui.GetWindowPos();
        Vector2 wMax = wMin + ImGui.GetWindowSize();
        float plaqueBottom = wMin.Y + 52f * scale;
        ImGui.Dummy(new Vector2(1, MathF.Max(6f * scale,
            plaqueBottom + 8f * scale - ImGui.GetCursorScreenPos().Y)));
        float edge = 11f * scale;
        ImGui.GetWindowDrawList().AddRectFilled(
            new Vector2(wMin.X + edge, ImGui.GetCursorScreenPos().Y - 4f * scale),
            new Vector2(wMax.X - edge, wMax.Y - edge), 0xc00b0e12);

        ImGui.BeginChild("##giver-quests-body", new Vector2(0, 0), false);
        // Half our custom modals shipped with text jammed against the backdrop's
        // left border; this one keeps a real gutter on both sides.
        float gutter = 12f * scale;
        ImGui.Indent(gutter);
        if (_giverQuestsReply.Giver != _giverQuestsGiverGuid || _giverQuestsReply.Quests.Length == 0)
        {
            ImGui.TextWrapped(_partyGiverQuestsAvailable
                ? "Reading who can quest here..."
                : "This server has no commander-view questing support.");
        }
        else
        {
            // The server already streams these in DO-ORDER (chains together, in
            // sequence); drawing in wire order IS the ordering.
            foreach (GiverQuestEntry quest in _giverQuestsReply.Quests)
                DrawGiverQuestRow(quest, owners, scale);
        }
        ImGui.Unindent(gutter);
        ImGui.EndChild();
        ImGui.End();
        // The text page is the commander window's satellite: it never outlives it.
        if (!_giverQuestsOpen) _giverQuestTextOpen = false;
        DrawGiverQuestTextWindow();
    }

    private void DrawGiverQuestRow(GiverQuestEntry quest,
        List<(ulong Guid, string Name)> owners, float scale)
    {
        string title = _questTitles.GetValueOrDefault(quest.QuestId, $"Quest {quest.QuestId}");
        // The title is the door to the quest's text page — hovering brightens it
        // the way vanilla brightens a quest log row, clicking opens the page.
        bool textOpenOnThis = _giverQuestTextOpen && _giverQuestTextQuestId == quest.QuestId;
        ImGui.PushStyleColor(ImGuiCol.Text, ImGui.ColorConvertU32ToFloat4(
            PartyQuestTitleColor(quest.QuestId)));
        ImGui.TextUnformatted(title);
        ImGui.PopStyleColor();
        if (ImGui.IsItemHovered())
        {
            ImGui.GetWindowDrawList().AddRectFilled(ImGui.GetItemRectMin(),
                ImGui.GetItemRectMax(), 0x2affd8a0);
            HoverTip("Read this quest's text");
        }
        if (ImGui.IsItemClicked())
        {
            _giverQuestTextQuestId = quest.QuestId;
            _giverQuestTextOpen = !textOpenOnThis;
            RequireQuestTemplate(quest.QuestId);
        }
        ImGui.SameLine();
        ImGui.TextColored(ImGui.ColorConvertU32ToFloat4(0xff7f888f),
            quest.Ends ? (quest.Starts ? "(chain)" : "(turn-in)") : "(offered)");

        var byMember = new Dictionary<ulong, byte>();
        foreach (GiverQuestMemberVerdict mv in quest.Members) byMember[mv.Member] = mv.Verdict;

        // A ready-to-turn-in quest with more than one reward choice earns a per-member
        // reward picker in the card; everything else turns in on auto (the server's
        // spec-aware pick). The template arrives from RequireQuestTemplate above.
        IReadOnlyList<QuestRewardItem>? rewards = null;
        if (quest.Ends && _questTemplates.TryGetValue(quest.QuestId, out QuestTemplate? tmpl) &&
            tmpl is not null && tmpl.ChoiceRewards.Count > 1)
            rewards = tmpl.ChoiceRewards;

        var takers = new List<PartyQuestSubject>();
        var finishers = new List<PartyQuestSubject>();

        // One card per member — portrait, name, verdict (and reward chips on a
        // multi-reward turn-in) — wrapping to the window width.
        var cardSize = new Vector2(118, rewards is not null ? 78 : 44) * scale;
        float avail = ImGui.GetContentRegionAvail().X;
        float rowW = 0f;
        bool first = true;
        foreach ((ulong guid, string name) in owners)
        {
            byte v = byMember.TryGetValue(guid, out byte verdict) ? verdict : GiverQuestsWire.Cant;
            if (!first)
            {
                if (rowW + cardSize.X <= avail) ImGui.SameLine();
                else rowW = 0f;
            }
            DrawGiverQuestMemberCard(quest.QuestId, guid,
                guid == LocalPlayerGuid ? "You" : name, v, cardSize, scale, rewards);
            rowW += cardSize.X + 4f * scale;
            first = false;

            if (v == GiverQuestsWire.CanTake)
                takers.Add(new PartyQuestSubject(guid, PartyQuestWire.RewardChoiceAuto));
            else if (v == GiverQuestsWire.Ready)
            {
                byte choice = _giverQuestRewardPick.TryGetValue((quest.QuestId, guid), out byte pk)
                    ? pk : PartyQuestWire.RewardChoiceAuto;
                finishers.Add(new PartyQuestSubject(guid, choice));
            }
        }

        // Owner 2026-08-27: the raw ImGui buttons are gone — these draw with the
        // real UI-Panel-Button art like every other vanilla surface.
        ImDrawListPtr rowDraw = ImGui.GetWindowDrawList();
        if (_partyQuestActsAvailable && takers.Count > 0)
        {
            string caption = $"Accept for party ({takers.Count})";
            if (GiverQuestActionButton(rowDraw, $"##acc{quest.QuestId}", caption, scale))
                RequestPartyQuestAct(PartyQuestWire.ActionAccept, quest.QuestId,
                    _giverQuestsGiverGuid, takers);
        }
        if (_partyQuestActsAvailable && finishers.Count > 0)
        {
            if (takers.Count > 0) ImGui.SameLine();
            string caption = $"Turn in ({finishers.Count})";
            if (GiverQuestActionButton(rowDraw, $"##fin{quest.QuestId}", caption, scale))
                RequestPartyQuestAct(PartyQuestWire.ActionTurnIn, quest.QuestId,
                    _giverQuestsGiverGuid, finishers);
        }
        ImGui.Dummy(new Vector2(1, 4 * scale));
        ImGui.Separator();
        ImGui.Dummy(new Vector2(1, 2 * scale));
    }

    /// <summary>A vanilla-art panel button sized to its caption, participating in
    /// the ImGui flow (the InvisibleButton inside VanillaButton reserves the
    /// layout box, so SameLine works around it).</summary>
    private bool GiverQuestActionButton(ImDrawListPtr draw, string id, string caption,
        float scale)
    {
        var logical = new Vector2(
            MathF.Max(96f, GameText.MeasureWidth("GameFontNormal", caption, 1f) + 30f), 22f);
        return VanillaButton(draw, id, caption, ImGui.GetCursorScreenPos(), logical, scale);
    }

    private void DrawGiverQuestMemberCard(uint questId, ulong guid, string name,
        byte verdict, Vector2 cardSize, float scale, IReadOnlyList<QuestRewardItem>? rewards)
    {
        ImGui.BeginChild($"##gqc-{questId}-{guid}", cardSize, true,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        Vector2 p = ImGui.GetCursorScreenPos();
        float ps = 30f * scale;

        uint art = PartyColumnPortraitHandle(guid);
        if (art != 0)
            dl.AddImage((nint)art, p, p + new Vector2(ps, ps),
                new Vector2(0, 1), new Vector2(1, 0));   // portraits bake V-flipped
        else
            dl.AddRectFilled(p, p + new Vector2(ps, ps), 0x55101418);
        dl.AddRect(p, p + new Vector2(ps, ps), 0xff2a343d);

        ImGui.SetCursorScreenPos(p + new Vector2(ps + 6f * scale, 0f));
        ImGui.BeginGroup();
        ImGui.TextColored(ImGui.ColorConvertU32ToFloat4(0xffe6ecf0), name);
        ImGui.PushTextWrapPos(p.X + cardSize.X - 6f * scale);
        ImGui.TextColored(ImGui.ColorConvertU32ToFloat4(GiverQuestVerdictColor(verdict)),
            GiverQuestVerdictLabel(verdict));
        ImGui.PopTextWrapPos();
        ImGui.EndGroup();

        // Reward picker: only a member who is READY chooses, and only when the quest
        // offered more than one reward. A click sets their pick; unpicked stays auto.
        if (verdict == GiverQuestsWire.Ready && rewards is not null)
        {
            byte picked = _giverQuestRewardPick.TryGetValue((questId, guid), out byte pk)
                ? pk : (byte)255;
            int shown = Math.Min(rewards.Count, 5);
            for (int k = 0; k < shown; k++)
            {
                QuestRewardItem row = rewards[k];
                if (_items is not null && _net is not null)
                    _items.Require(row.ItemId, _giverQuestsGiverGuid, _net);
                Vector2 cellMin = p + new Vector2(2f + k * 22f, 36f) * scale;
                var cell = new Vector2(20f, 20f) * scale;
                ImGui.SetCursorScreenPos(cellMin);
                ImGui.InvisibleButton($"##gqr-{questId}-{guid}-{k}", cell);
                if (ImGui.IsItemClicked()) _giverQuestRewardPick[(questId, guid)] = (byte)k;
                if (ImGui.IsItemHovered())
                {
                    (string rn, _) = QuestRewardName(row);
                    HoverTip(rn + (picked == k ? "  —  their pick" : "  —  pick for " + name));
                }
                string iconPath = QuestRewardIconPath(row);
                uint icon = iconPath.Length > 0 ? _gameplayArt?.Handle(iconPath) ?? 0 : 0;
                if (icon != 0) dl.AddImage((nint)icon, cellMin, cellMin + cell);
                else dl.AddRectFilled(cellMin, cellMin + cell, 0x55101418);
                dl.AddRect(cellMin, cellMin + cell, picked == k ? VanillaGold : 0xff2a343d,
                    0f, ImDrawFlags.None, MathF.Max(1f, (picked == k ? 2f : 1f) * scale));
            }
        }
        ImGui.EndChild();
    }

    /// <summary>
    /// The quest text page (owner 2026-08-27): a simplified, still-vanilla-flavored
    /// read of one quest — story, objectives, rewards — in the real game fonts.
    /// Deliberately buttonless: accepting and turning in happen from the commander
    /// window's own controls, so this page only ever answers "what is this quest".
    /// </summary>
    private void DrawGiverQuestTextWindow()
    {
        if (!_giverQuestTextOpen || _giverQuestTextQuestId == 0 || _net is null) return;
        if (_uiParityArmed) return;
        float scale = GameplayUiScale();

        _questTemplates.TryGetValue(_giverQuestTextQuestId, out QuestTemplate? template);
        string title = template?.Title
            ?? _questTitles.GetValueOrDefault(_giverQuestTextQuestId,
                $"Quest {_giverQuestTextQuestId}");

        ImGui.SetNextWindowSize(new Vector2(380, 430) * scale, ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(280, 220) * scale,
            new Vector2(float.MaxValue, float.MaxValue));
        ImGui.SetNextWindowBgAlpha(0f);
        if (!ImGui.Begin("###giver-quest-text", ref _giverQuestTextOpen,
                ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoBackground |
                ImGuiWindowFlags.NoCollapse))
        {
            ImGui.End();
            return;
        }
        DrawVanillaPanelChrome(title, scale, ref _giverQuestTextOpen);

        Vector2 wMin = ImGui.GetWindowPos();
        Vector2 wMax = wMin + ImGui.GetWindowSize();
        float plaqueBottom = wMin.Y + 52f * scale;
        ImGui.Dummy(new Vector2(1, MathF.Max(6f * scale,
            plaqueBottom + 8f * scale - ImGui.GetCursorScreenPos().Y)));
        float edge = 11f * scale;
        ImGui.GetWindowDrawList().AddRectFilled(
            new Vector2(wMin.X + edge, ImGui.GetCursorScreenPos().Y - 4f * scale),
            new Vector2(wMax.X - edge, wMax.Y - edge), 0xc00b0e12);

        ImGui.BeginChild("##giver-quest-text-body", new Vector2(0, 0), false);
        float gutter = 12f * scale;
        ImGui.Indent(gutter);
        if (template is null)
        {
            RequireQuestTemplate(_giverQuestTextQuestId);
            ImGui.TextWrapped("Asking the server about this quest...");
        }
        else
        {
            float wrapWidth = ImGui.GetContentRegionAvail().X - gutter;
            DrawGiverQuestParagraph("GameFontHighlight",
                ExpandQuestText(template.Details), scale, wrapWidth);
            if (template.ObjectivesText.Length > 0)
            {
                ImGui.Dummy(new Vector2(1, 8 * scale));
                DrawGiverQuestParagraph("GameFontNormal", "Objectives", scale, wrapWidth);
                DrawGiverQuestParagraph("GameFontHighlight",
                    ExpandQuestText(template.ObjectivesText), scale, wrapWidth);
            }
            DrawGiverQuestTextRewards(template, scale, wrapWidth);
        }
        ImGui.Unindent(gutter);
        ImGui.EndChild();
        ImGui.End();
    }

    /// <summary>Wrapped GameText paragraphs that still participate in the ImGui
    /// flow (each line reserves its box, so the child scrolls like any other).</summary>
    private void DrawGiverQuestParagraph(string fontObject, string text, float scale,
        float wrapWidth)
    {
        if (text.Length == 0) return;
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        foreach (string line in WrapTooltipText(text, fontObject, scale,
                     MathF.Max(80f * scale, wrapWidth)))
        {
            Vector2 pos = ImGui.GetCursorScreenPos();
            if (line.Length > 0) GameText.Draw(dl, fontObject, line, pos, scale);
            ImGui.Dummy(new Vector2(1, GameText.LinePitch(fontObject, scale)));
        }
    }

    private void DrawGiverQuestTextRewards(QuestTemplate template, float scale,
        float wrapWidth)
    {
        bool anyItems = template.ChoiceRewards.Count > 0 || template.FixedRewards.Count > 0;
        if (!anyItems && template.Money <= 0) return;
        ImGui.Dummy(new Vector2(1, 8 * scale));
        DrawGiverQuestParagraph("GameFontNormal", "Rewards", scale, wrapWidth);
        if (template.ChoiceRewards.Count > 1)
            DrawGiverQuestParagraph("GameFontHighlight",
                "You will be able to choose one of these rewards:", scale, wrapWidth);
        DrawGiverQuestTextRewardIcons("choice", template.ChoiceRewards, scale);
        DrawGiverQuestTextRewardIcons("fixed", template.FixedRewards, scale);
        if (template.Money > 0)
            DrawGiverQuestParagraph("GameFontHighlight",
                "You will also receive: " + FormatMoney((uint)template.Money), scale, wrapWidth);
    }

    private void DrawGiverQuestTextRewardIcons(string tag,
        IReadOnlyList<QuestRewardItem> rewards, float scale)
    {
        if (rewards.Count == 0) return;
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        var cell = new Vector2(26f, 26f) * scale;
        for (int k = 0; k < rewards.Count; k++)
        {
            QuestRewardItem row = rewards[k];
            if (_items is not null && _net is not null)
                _items.Require(row.ItemId, _giverQuestsGiverGuid, _net);
            if (k > 0) ImGui.SameLine();
            Vector2 cellMin = ImGui.GetCursorScreenPos();
            ImGui.InvisibleButton($"##gqt-{tag}-{k}", cell);
            if (ImGui.IsItemHovered())
            {
                (string rewardName, _) = QuestRewardName(row);
                HoverTip(row.Count > 1 ? $"{rewardName} x{row.Count}" : rewardName);
            }
            string iconPath = QuestRewardIconPath(row);
            uint icon = iconPath.Length > 0 ? _gameplayArt?.Handle(iconPath) ?? 0 : 0;
            if (icon != 0) dl.AddImage((nint)icon, cellMin, cellMin + cell);
            else dl.AddRectFilled(cellMin, cellMin + cell, 0x55101418);
            dl.AddRect(cellMin, cellMin + cell, 0xff2a343d);
        }
    }
}

using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private TalentCatalog? _talents;
    private bool _talentOpen;
    private uint _talentSelectedTab;
    private uint _talentWipeCost;
    private ulong _talentWipeTrainer;
    private float _talentScroll;
    private (uint Talent, uint Rank, uint Points, double SentAt, bool RankSeen)? _pendingTalent;

    private void InitTalents()
    {
        if (_mpq is null) return;
        try
        {
            _talents = TalentCatalog.Load(_mpq);
            Console.WriteLine(_talents is null ? "[talents] Talent/TalentTab DBC unavailable" :
                $"[talents] catalog ready ({_talents.TalentCount} talents, {_talents.Tabs.Count} tabs)");
        }
        catch (Exception ex) { Console.WriteLine($"[talents] catalog failed: {ex.Message}"); }
    }

    private uint TalentPoints()
        => _net is not null && _entities.TryGet(ControlledGuid, out var player) ? player.Fields.TalentPoints : 0;

    // 1.12: the talent system unlocks at level 10 — the micro button is greyed and the
    // frame refuses to open below it (TalentFrame_LoadUI / MicroButton disable rule).
    private const uint TalentUnlockLevel = 10;

    private bool TalentsUnlocked()
        => _entities.TryGet(ControlledGuid, out var player) && player.Level >= TalentUnlockLevel;

    private int TalentRank(TalentInfo talent)
    {
        int rank = 0;
        for (int i = 0; i < talent.RankSpells.Length; i++)
            if (_actions.KnownSpells.Contains(talent.RankSpells[i])) rank = i + 1;
        return rank;
    }

    private bool TalentEligible(TalentInfo talent, out string reason)
    {
        if (_talents is null) { reason = "catalog-unavailable"; return false; }
        uint points = TalentPoints();
        int rank = TalentRank(talent);
        if (points == 0) { reason = "no-free-points"; return false; }
        if (rank >= talent.RankSpells.Length) { reason = "max-rank"; return false; }
        int tabSpent = _talents.TalentsForTab(talent.TabId).Sum(TalentRank);
        if (tabSpent < talent.Row * 5) { reason = $"tier-needs-{talent.Row * 5}-has-{tabSpent}"; return false; }
        if (talent.DependsOn != 0 && _talents.TryGet(talent.DependsOn, out TalentInfo prerequisite) &&
            TalentRank(prerequisite) <= talent.DependsOnRank)
        { reason = $"prerequisite-{talent.DependsOn}-rank-{talent.DependsOnRank + 1}"; return false; }
        if (talent.RequiredSpell != 0 && !_actions.KnownSpells.Contains(talent.RequiredSpell))
        { reason = $"required-spell-{talent.RequiredSpell}"; return false; }
        reason = "pass"; return true;
    }

    private bool SpendTalent(uint talentId)
    {
        // CMSG_LEARN_TALENT acts on the session character; a possessed bot's tree is read-only.
        if (ControlledGuid != LocalPlayerGuid) return false;
        if (_net is null || _talents is null || !_talents.TryGet(talentId, out TalentInfo talent)) return false;
        int rank = TalentRank(talent);
        bool pass = TalentEligible(talent, out string reason);
        EmitInterface("talent", "pre-send-gate", pass ? "PASS" : "REFUSED", talentId,
            $"requestedRank={rank};points={TalentPoints()};tab={talent.TabId};row={talent.Row};column={talent.Column};reason={reason}");
        if (!pass) return false;
        byte[] body = WorldSession.BuildLearnTalentBody(talentId, (uint)rank);
        _pendingTalent = (talentId, (uint)rank, TalentPoints(), NowSeconds(), false);
        _net.LearnTalent(talentId, (uint)rank);
        EmitInterface("talent", "spend-send", "SENT", talentId,
            $"requestedRank={rank};body={Convert.ToHexString(body)}");
        return true;
    }

    private bool SpendFirstEligibleTalent()
    {
        byte cls = _net is not null && _entities.TryGet(ControlledGuid, out var p) ? p.Fields.Bytes0.Class : (byte)0;
        if (_talents is null) return false;
        foreach (TalentInfo talent in _talents.TabsForClass(cls).SelectMany(t => _talents.TalentsForTab(t.Id)))
            if (TalentEligible(talent, out _)) return SpendTalent(talent.Id);
        EmitInterface("talent", "spend-send", "REFUSED", ControlledGuid,
            $"class={cls};points={TalentPoints()};reason=no-eligible-talent");
        return false;
    }

    private void ObserveTalentTransition()
    {
        if (_pendingTalent is not { } pending || _talents is null || !_talents.TryGet(pending.Talent, out TalentInfo talent)) return;
        uint points = TalentPoints(); int rank = TalentRank(talent);
        if (points < pending.Points)
        {
            EmitInterface("talent", "server-confirm", "CONFIRMED", pending.Talent,
                $"requestedRank={pending.Rank};rankAfter={rank};pointsBefore={pending.Points};pointsAfter={points};delta={(int)points - pending.Points}");
            _pendingTalent = null;
        }
        else if (rank > pending.Rank && !pending.RankSeen)
        {
            EmitInterface("talent", "server-spell-confirm", "CONFIRMED", pending.Talent,
                $"requestedRank={pending.Rank};rankAfter={rank};source=SMSG_LEARNED_SPELL");
            _pendingTalent = (pending.Talent, pending.Rank, pending.Points, pending.SentAt, true);
        }
        else if (NowSeconds() - pending.SentAt > 5)
        {
            EmitInterface("talent", "server-confirm", "NO_DATA", pending.Talent,
                $"requestedRank={pending.Rank};rankAfter={rank};pointsBefore={pending.Points};pointsAfter={points}");
            _pendingTalent = null;
        }
    }

    private void ApplyTalentWipeConfirm(ReadOnlySpan<byte> body)
    {
        var r = new PacketReader(body.ToArray()); ulong guid = r.ReadU64(); uint cost = r.ReadU32();
        if (r.Remaining != 0) throw new InvalidDataException($"talent wipe response trailing bytes={r.Remaining}");
        _talentWipeTrainer = guid; _talentWipeCost = cost; _talentOpen = true;
        EmitInterface("talent", "unlearn-cost", guid == 0 ? "NO_TALENTS" : "DISPLAYED", guid,
            $"costCopper={cost};costText={MoneyText(cost)};body={Convert.ToHexString(body)}");
    }

    private bool ConfirmTalentWipe()
    {
        if (_net is null || _talentWipeTrainer == 0) return false;
        byte[] body = WorldSession.BuildTalentWipeBody(_talentWipeTrainer);
        _net.ConfirmTalentWipe(_talentWipeTrainer);
        EmitInterface("talent", "unlearn-confirm", "SENT", _talentWipeTrainer,
            $"costCopper={_talentWipeCost};body={Convert.ToHexString(body)}");
        return true;
    }

    private bool OpenTalentPanel()
    {
        if (!TalentsUnlocked()) return false;    // below level 10 the panel stays closed
        byte cls = _net is not null && _entities.TryGet(ControlledGuid, out var p) ? p.Fields.Bytes0.Class : (byte)0;
        TalentTabInfo? first = _talents?.TabsForClass(cls).Cast<TalentTabInfo?>().FirstOrDefault();
        if (first is null) return false;
        _talentSelectedTab = first.Value.Id; _talentScroll = 0; _talentOpen = true;
        EmitTalentSnapshot(cls); return true;
    }

    private void EmitTalentSnapshot(byte cls)
    {
        if (_talents is null) return;
        var tabs = _talents.TabsForClass(cls).ToArray();
        EmitInterface("talent", "panel", tabs.Length == 3 ? "COMPLETE" : "INCOMPLETE", cls,
            $"class={cls};tabs={tabs.Length};points={TalentPoints()};names={string.Join('|', tabs.Select(x => SanitizeEvidence(x.Name)))}");
        foreach (TalentTabInfo tab in tabs)
            EmitInterface("talent", "tree", "DECODED", tab.Id,
                $"class={cls};page={tab.Page};name={SanitizeEvidence(tab.Name)};talents={_talents.TalentsForTab(tab.Id).Count()};background={SanitizeEvidence(tab.Background)}");
    }

    private void SimulateTalentRoster()
    {
        if (_talents is null) return;
        foreach (byte cls in new byte[] { 1, 2, 3, 4, 5, 7, 8, 9, 11 }) EmitTalentSnapshot(cls);
        var w = new PacketWriter(12); w.WriteU64(0xF1300001CB000001); w.WriteU32(10000);
        ApplyTalentWipeConfirm(w.ToArray());
    }

    private void DrawTalentFrame()
    {
        if (!_talentOpen || _talents is null || _gameplayArt is null) return;
        WorldEntity? entity = null;
        if (_net is not null && _entities.TryGet(ControlledGuid, out WorldEntity foundEntity))
            entity = foundEntity;
        byte cls = entity?.Fields.Bytes0.Class ?? 0;
        TalentTabInfo[] tabs = _talents.TabsForClass(cls).ToArray();
        if (tabs.Length == 0) return;
        if (tabs.All(x => x.Id != _talentSelectedTab)) { _talentSelectedTab = tabs[0].Id; _talentScroll = 0; }

        float s = GameplayUiScale();
        Vector2 origin = UiPanelFrameOrigin(UiPanelOwnershipRegistry[13], s);
        Vector2 logicalSize = TalentFrameUiLaw.Frame.Size;
        ImGui.SetNextWindowPos(origin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(logicalSize * s, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        if (!ImGui.Begin("##talents", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
                ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoNav))
        { ImGui.End(); return; }
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        if (_uiParityArmed && _uiParityPanel == "talent-frame")
        {
            BeginUiParityFrame(origin, s);
            CollectUiParityDraw("TalentFrame", "Frame", origin, logicalSize * s, "",
                new("", 0, "IMGUI_HOST", "ANCHOR:ABSOLUTE", "", "", 0, 8));
        }

        TalentTabInfo active = tabs.First(x => x.Id == _talentSelectedTab);
        string file = string.IsNullOrWhiteSpace(active.Background) ? "MageFire" : active.Background;

        // BACKGROUND layer: portrait and the four dynamic tree tiles.
        if (entity is not null)
        {
            // Round copy - the talent frame's aperture is authored art in both modes.
            uint portrait = RoundAperturePortrait(_playerPortrait, _playerPortraitUsable);
            TalentFrameUiLaw.LogicalRect portraitRect = TalentFrameUiLaw.Portrait;
            if (portrait != 0)
                dl.AddImage((nint)portrait, origin + portraitRect.Min * s,
                    origin + (portraitRect.Min + portraitRect.Size) * s,
                    TalentFrameUiLaw.PortraitUvMin, TalentFrameUiLaw.PortraitUvMax);
            else DrawUnitPortraitImage(dl, entity, origin + portraitRect.Min * s,
                portraitRect.Width * s, 0, true);
        }
        (string Element, string Suffix, TalentFrameUiLaw.LogicalRect Rect)[] tree =
        [
            ("TalentFrameBackgroundTopLeft", "TopLeft", TalentFrameUiLaw.TreeTopLeft),
            ("TalentFrameBackgroundTopRight", "TopRight", TalentFrameUiLaw.TreeTopRight),
            ("TalentFrameBackgroundBottomLeft", "BottomLeft", TalentFrameUiLaw.TreeBottomLeft),
            ("TalentFrameBackgroundBottomRight", "BottomRight", TalentFrameUiLaw.TreeBottomRight)
        ];
        // The four-piece shell has an opaque black center in the shipped BLPs.
        // Benilla's dynamic tree textures are assigned after the shell is shown,
        // so render the shell first and the tree tiles over that center.
        (string Element, string Path, TalentFrameUiLaw.LogicalRect Rect)[] shell =
        [
            ("TalentFrame/Texture", TalentFrameUiLaw.TopLeftArt, TalentFrameUiLaw.ShellTopLeft),
            ("TalentFrame/Texture#2", TalentFrameUiLaw.TopRightArt, TalentFrameUiLaw.ShellTopRight),
            ("TalentFrame/Texture#3", TalentFrameUiLaw.BottomLeftArt, TalentFrameUiLaw.ShellBottomLeft),
            ("TalentFrame/Texture#4", TalentFrameUiLaw.BottomRightArt, TalentFrameUiLaw.ShellBottomRight)
        ];
        foreach (var region in shell)
        {
            Vector2 min = origin + region.Rect.Min * s;
            DrawArt(dl, region.Path, min, region.Rect.Size, s);
            if (_uiParityArmed && _uiParityPanel == "talent-frame")
                CollectUiParityDraw(region.Element, "Texture", min, region.Rect.Size * s, "TalentFrame",
                    new(region.Path, 0xffffffff, "IMGUI_IMAGE", "TOPLEFT", "TalentFrame", "TOPLEFT",
                        region.Rect.X, -region.Rect.Y));
        }
        foreach (var region in tree)
        {
            string path = $@"Interface\TalentFrame\{file}-{region.Suffix}";
            Vector2 min = origin + region.Rect.Min * s;
            DrawArt(dl, path, min, region.Rect.Size, s);
            if (_uiParityArmed && _uiParityPanel == "talent-frame")
                CollectUiParityDraw(region.Element, "Texture", min, region.Rect.Size * s, "TalentFrame",
                    new(path, 0xffffffff, "IMGUI_IMAGE", "TOPLEFT", "TalentFrame", "TOPLEFT",
                        region.Rect.X, -region.Rect.Y));
        }

        DrawVanillaInputBorder(dl, origin + TalentFrameUiLaw.PointsBorder.Min * s,
            TalentFrameUiLaw.PointsBorder.Size, s);

        _talentScroll = TalentFrameUiLaw.ClampScroll(_talentScroll);
        Vector2 clipMin = origin + TalentFrameUiLaw.ScrollFrame.Min * s;
        Vector2 clipMax = clipMin + TalentFrameUiLaw.ScrollFrame.Size * s;
        ImGui.SetCursorScreenPos(clipMin);
        ImGui.InvisibleButton("##talent-scroll-wheel", clipMax - clipMin);
        if (ImGui.IsItemHovered() && ImGui.GetIO().MouseWheel != 0)
            _talentScroll = TalentFrameUiLaw.WheelScroll(
                _talentScroll, ImGui.GetIO().MouseWheel);

        Vector2 scrollOffset = TalentFrameUiLaw.ScrollOffset(_talentScroll, s);
        TalentInfo[] visibleTalents = _talents.TalentsForTab(_talentSelectedTab).ToArray();
        int tabSpent = visibleTalents.Sum(TalentRank);
        bool VisualRequirementsMet(TalentInfo talent)
        {
            int rank = TalentRank(talent);
            if (TalentPoints() == 0 && rank == 0) return false;
            if (tabSpent < talent.Row * 5) return false;
            if (talent.DependsOn != 0 &&
                visibleTalents.FirstOrDefault(x => x.Id == talent.DependsOn) is { Id: not 0 } prereq &&
                TalentRank(prereq) <= talent.DependsOnRank) return false;
            return talent.RequiredSpell == 0 || _actions.KnownSpells.Contains(talent.RequiredSpell);
        }

        dl.PushClipRect(clipMin, clipMax, true);
        var routes = new List<TalentFrameUiLaw.DependencyRoute>();
        foreach (TalentInfo talent in visibleTalents.Where(x => x.DependsOn != 0))
        {
            TalentInfo prerequisite = visibleTalents.FirstOrDefault(x => x.Id == talent.DependsOn);
            if (prerequisite.Id == 0) continue;
            routes.Add(new((int)talent.Row, (int)talent.Column,
                (int)prerequisite.Row, (int)prerequisite.Column,
                VisualRequirementsMet(talent)));
        }
        uint branchArt = _gameplayArt.Handle(TalentFrameUiLaw.BranchArt);
        uint arrowArt = _gameplayArt.Handle(TalentFrameUiLaw.ArrowArt);
        foreach (TalentFrameUiLaw.ConnectorSprite sprite in TalentFrameUiLaw.BuildConnectors(
                     visibleTalents.Select(x => ((int)x.Row, (int)x.Column)), routes))
        {
            uint art = sprite.Arrow ? arrowArt : branchArt;
            if (art == 0) continue;
            Vector2 min = origin + sprite.Rect.Min * s + scrollOffset;
            dl.AddImage((nint)art, min, min + sprite.Rect.Size * s, sprite.Uv0, sprite.Uv1);
        }
        foreach (TalentInfo talent in visibleTalents)
        {
            int rank = TalentRank(talent);
            bool eligible = TalentEligible(talent, out string reason);
            SpellInfo? spell = _spellCatalog?.TryGet(
                talent.RankSpells[Math.Min(rank, talent.RankSpells.Length - 1)], out SpellInfo si) == true ? si : null;
            string name = spell?.Name ?? $"Talent {talent.Id}";
            TalentFrameUiLaw.LogicalRect buttonRect = TalentFrameUiLaw.TalentButton(
                (int)talent.Row, (int)talent.Column);
            Vector2 min = origin + buttonRect.Min * s + scrollOffset;
            bool maxed = rank == talent.RankSpells.Length;
            bool visualRequirements = VisualRequirementsMet(talent);
            uint slotTint = !visualRequirements ? 0xff808080u :
                maxed ? VanillaGold : 0xff1aff1au;
            uint slot = _gameplayArt.Handle(@"Interface\Buttons\UI-EmptySlot-White");
            TalentFrameUiLaw.LogicalRect slotRect = TalentFrameUiLaw.TalentSlot(
                (int)talent.Row, (int)talent.Column);
            Vector2 slotMin = origin + slotRect.Min * s + scrollOffset;
            if (slot != 0) dl.AddImage((nint)slot, slotMin,
                slotMin + slotRect.Size * s, Vector2.Zero, Vector2.One, slotTint);
            uint icon = _gameplayArt.Handle(spell?.IconPath ?? @"Interface\Icons\INV_Misc_QuestionMark.blp");
            if (icon != 0) dl.AddImage((nint)icon, min, min + buttonRect.Size * s,
                Vector2.Zero, Vector2.One, visualRequirements || rank > 0
                    ? 0xffffffff : 0xffa6a6a6);
            TalentFrameUiLaw.LogicalRect normalRingRect = TalentFrameUiLaw.TalentNormalRing(
                (int)talent.Row, (int)talent.Column);
            Vector2 normalRingMin = origin + normalRingRect.Min * s + scrollOffset;
            uint normalRing = _gameplayArt.Handle(@"Interface\Buttons\UI-Quickslot2");
            if (normalRing != 0) dl.AddImage((nint)normalRing, normalRingMin,
                normalRingMin + normalRingRect.Size * s);
            uint border = _gameplayArt.Handle(@"Interface\TalentFrame\TalentFrame-RankBorder");
            TalentFrameUiLaw.LogicalRect rankRect = TalentFrameUiLaw.TalentRankBorder(
                (int)talent.Row, (int)talent.Column);
            Vector2 rankMin = origin + rankRect.Min * s + scrollOffset;
            bool showRank = visualRequirements || rank > 0;
            if (showRank)
            {
                if (border != 0) dl.AddImage((nint)border, rankMin,
                    rankMin + rankRect.Size * s, Vector2.Zero, Vector2.One,
                    visualRequirements ? 0xffffffff : 0xff808080);
                uint rankColor = !visualRequirements ? 0xff888888u :
                    maxed ? VanillaGold : 0xff00ff00u;
                GameText.DrawCentered(dl, "GameFontNormalSmall", rank.ToString(),
                    rankMin + rankRect.Size * .5f * s, s, rankColor);
            }
            ImGui.SetCursorScreenPos(min);
            ImGui.InvisibleButton($"##talent-{talent.Id}", buttonRect.Size * s);
            if (ImGui.IsItemActive())
                DrawArt(dl, @"Interface\Buttons\UI-Quickslot-Depress", min,
                    buttonRect.Size, s);
            if (ImGui.IsItemHovered())
                DrawArt(dl, @"Interface\Buttons\ButtonHilight-Square", min,
                    buttonRect.Size, s);
            if (ImGui.IsItemClicked() && eligible) SpendTalent(talent.Id);
            if (ImGui.IsItemHovered())
            {
                TalentFrameUiLaw.TooltipSeat tooltipSeat =
                    TalentFrameUiLaw.TalentTooltipSeat(min, buttonRect.Size * s);
                ImGui.SetNextWindowPos(tooltipSeat.Anchor, ImGuiCond.Always,
                    tooltipSeat.Pivot);
                ImGui.BeginTooltip(); ImGui.TextUnformatted(name);
                ImGui.TextUnformatted($"Rank {rank}/{talent.RankSpells.Length}");
                if (!eligible) ImGui.TextDisabled(reason); ImGui.EndTooltip();
            }
        }
        dl.PopClipRect();
        DrawTalentScrollBar(dl, origin, s);

        GameText.DrawCentered(dl, "GameFontNormal", "Talents",
            origin + TalentFrameUiLaw.TitleCenter * s, s);

        string spentPrefix = TalentFrameUiLaw.SpentPointsPrefix(active.Name);
        string spentValue = visibleTalents.Sum(TalentRank).ToString();
        float spentPrefixWidth = GameText.MeasureWidth("GameFontNormalSmall", spentPrefix, s);
        float spentValueWidth = GameText.MeasureWidth("GameFontHighlightSmall", spentValue, s);
        Vector2 spentTop = TalentFrameUiLaw.SpentTextTop(origin, s,
            spentPrefixWidth, spentValueWidth);
        GameText.Draw(dl, "GameFontNormalSmall", spentPrefix, spentTop, s);
        GameText.Draw(dl, "GameFontHighlightSmall", spentValue,
            TalentFrameUiLaw.SpentValueTop(spentTop, spentPrefixWidth), s);

        string talentPoints = TalentPoints().ToString();
        float pointEm = GameText.EmPixels("GameFontHighlightSmall", s);
        Vector2 pointRight = origin + TalentFrameUiLaw.TalentPointsBottomRight * s;
        pointRight.Y -= pointEm;
        float pointWidth = GameText.MeasureWidth("GameFontHighlightSmall", talentPoints, s);
        GameText.DrawRightAligned(dl, "GameFontHighlightSmall", talentPoints, pointRight, s);
        Vector2 labelRight = pointRight with
        {
            X = pointRight.X - pointWidth - TalentFrameUiLaw.TalentPointsLabelGap * s,
        };
        GameText.DrawRightAligned(dl, "GameFontNormalSmall", "Talent Points:", labelRight, s);

        float tabX = TalentFrameUiLaw.FirstTab.X;
        foreach (TalentTabInfo tab in tabs)
        {
            float width = VanillaCharacterTabWidth(tab.Name, s, 10);
            if (VanillaTab(dl, $"##talent-tab-{tab.Id}",
                    TalentFrameUiLaw.TabMinimum(origin, tabX, s),
                    tab.Name, width, s, tab.Id == _talentSelectedTab))
            { _talentSelectedTab = tab.Id; _talentScroll = 0; }
            tabX += width - TalentFrameUiLaw.TabOverlap;
        }
        if (_talentWipeTrainer != 0 && VanillaButton(dl, "##talent-reset", "Unlearn Talents",
                origin + TalentFrameUiLaw.ResetButton.Min * s,
                TalentFrameUiLaw.ResetButton.Size, s)) ConfirmTalentWipe();
        if (VanillaButton(dl, "##talent-cancel", "Close",
                origin + TalentFrameUiLaw.CloseButton.Min * s,
                TalentFrameUiLaw.CloseButton.Size, s)) _talentOpen = false;
        Vector2 close = origin + TalentFrameUiLaw.CloseX.Min * s;
        DrawImageButton(dl, "##talent-close", close, TalentFrameUiLaw.CloseX.Size * s,
            @"Interface\Buttons\UI-Panel-MinimizeButton-Up", @"Interface\Buttons\UI-Panel-MinimizeButton-Down",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Highlight");
        if (ImGui.IsItemClicked()) _talentOpen = false;
        if (_uiParityArmed && _uiParityPanel == "talent-frame") MarkUiParityFrameComplete();
        ImGui.End();
    }

    private void DrawTalentScrollBar(ImDrawListPtr dl, Vector2 origin, float s)
    {
        // TalentFrameScrollFrame artwork from Blizzard_TalentUI.xml.
        uint background = _gameplayArt?.Handle(@"Interface\PaperDollInfoFrame\UI-Character-ScrollBar") ?? 0;
        if (background != 0)
        {
            TalentFrameUiLaw.TextureSlice topSlice =
                TalentFrameUiLaw.ScrollBackgroundTopSlice;
            Vector2 top = topSlice.Rect.ScaledMin(origin, s);
            dl.AddImage((nint)background, top,
                top + topSlice.Rect.ScaledSize(s), topSlice.UvMin, topSlice.UvMax);
            TalentFrameUiLaw.TextureSlice bottomSlice =
                TalentFrameUiLaw.ScrollBackgroundBottomSlice;
            Vector2 bottom = bottomSlice.Rect.ScaledMin(origin, s);
            dl.AddImage((nint)background, bottom,
                bottom + bottomSlice.Rect.ScaledSize(s),
                bottomSlice.UvMin, bottomSlice.UvMax);
        }
        void Button(string id, TalentFrameUiLaw.LogicalRect rect, bool up, bool enabled)
        {
            Vector2 min = origin + rect.Min * s;
            ImGui.SetCursorScreenPos(min); ImGui.InvisibleButton(id, rect.Size * s);
            bool active = enabled && ImGui.IsItemActive();
            string stem = up ? "UI-ScrollBar-ScrollUpButton" : "UI-ScrollBar-ScrollDownButton";
            string state = !enabled ? "Disabled" : active ? "Down" : "Up";
            uint texture = _gameplayArt?.Handle($@"Interface\Buttons\{stem}-{state}") ?? 0;
            if (texture != 0) dl.AddImage((nint)texture, min, min + rect.Size * s,
                TalentFrameUiLaw.ScrollControlUvMin,
                TalentFrameUiLaw.ScrollControlUvMax);
            if (enabled && ImGui.IsItemClicked())
                _talentScroll = TalentFrameUiLaw.ArrowScroll(_talentScroll, up);
        }
        Button("##talent-scroll-up", TalentFrameUiLaw.ScrollUp, true, _talentScroll > 0);
        Button("##talent-scroll-down", TalentFrameUiLaw.ScrollDown, false,
            _talentScroll < TalentFrameUiLaw.ScrollMaximum);

        TalentFrameUiLaw.LogicalRect knobRect = TalentFrameUiLaw.ScrollKnob(_talentScroll);
        Vector2 knob = knobRect.ScaledMin(origin, s);
        uint art = _gameplayArt?.Handle(@"Interface\Buttons\UI-ScrollBar-Knob") ?? 0;
        if (art != 0) dl.AddImage((nint)art, knob,
            knob + knobRect.ScaledSize(s), TalentFrameUiLaw.ScrollControlUvMin,
            TalentFrameUiLaw.ScrollControlUvMax);
        ImGui.SetCursorScreenPos(origin + TalentFrameUiLaw.ScrollTrack.Min * s);
        ImGui.InvisibleButton("##talent-scroll-track", TalentFrameUiLaw.ScrollTrack.Size * s);
        if (ImGui.IsItemActive())
        {
            float logicalY = (ImGui.GetIO().MousePos.Y - origin.Y) / s;
            _talentScroll = TalentFrameUiLaw.ScrollFromKnob(logicalY);
        }
    }

    private static string MoneyText(uint copper) => $"{copper / 10000}g {(copper / 100) % 100}s {copper % 100}c";
}

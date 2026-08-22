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
        Vector2 logicalSize = new(384, 512);
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
            if (portrait != 0)
                dl.AddImage((nint)portrait, origin + new Vector2(7, 6) * s,
                    origin + new Vector2(67, 66) * s, new Vector2(0, 1), new Vector2(1, 0));
            else DrawUnitPortraitImage(dl, entity, origin + new Vector2(7, 6) * s, 60 * s, 0, true);
        }
        (string Element, string Suffix, Vector2 Offset, Vector2 Size)[] tree =
        [
            ("TalentFrameBackgroundTopLeft", "TopLeft", new(23, 77), new(256, 256)),
            ("TalentFrameBackgroundTopRight", "TopRight", new(279, 77), new(64, 256)),
            ("TalentFrameBackgroundBottomLeft", "BottomLeft", new(23, 333), new(256, 128)),
            ("TalentFrameBackgroundBottomRight", "BottomRight", new(279, 333), new(64, 128))
        ];
        // The four-piece shell has an opaque black center in the shipped BLPs.
        // Benilla's dynamic tree textures are assigned after the shell is shown,
        // so render the shell first and the tree tiles over that center.
        (string Element, string Path, Vector2 Offset, Vector2 Size)[] shell =
        [
            ("TalentFrame/Texture", @"Interface\PaperDollInfoFrame\UI-Character-General-TopLeft", new(2, 1), new(256, 256)),
            ("TalentFrame/Texture#2", @"Interface\PaperDollInfoFrame\UI-Character-General-TopRight", new(258, 1), new(128, 256)),
            ("TalentFrame/Texture#3", @"Interface\TalentFrame\UI-TalentFrame-BotLeft", new(2, 257), new(256, 256)),
            ("TalentFrame/Texture#4", @"Interface\TalentFrame\UI-TalentFrame-BotRight", new(258, 257), new(128, 256))
        ];
        foreach (var region in shell)
        {
            Vector2 min = origin + region.Offset * s;
            DrawArt(dl, region.Path, min, region.Size, s);
            if (_uiParityArmed && _uiParityPanel == "talent-frame")
                CollectUiParityDraw(region.Element, "Texture", min, region.Size * s, "TalentFrame",
                    new(region.Path, 0xffffffff, "IMGUI_IMAGE", "TOPLEFT", "TalentFrame", "TOPLEFT",
                        region.Offset.X, -region.Offset.Y));
        }
        foreach (var region in tree)
        {
            string path = $@"Interface\TalentFrame\{file}-{region.Suffix}";
            Vector2 min = origin + region.Offset * s;
            DrawArt(dl, path, min, region.Size, s);
            if (_uiParityArmed && _uiParityPanel == "talent-frame")
                CollectUiParityDraw(region.Element, "Texture", min, region.Size * s, "TalentFrame",
                    new(path, 0xffffffff, "IMGUI_IMAGE", "TOPLEFT", "TalentFrame", "TOPLEFT",
                        region.Offset.X, -region.Offset.Y));
        }

        const float scrollMaximum = 103;
        Vector2 clipMin = origin + new Vector2(23, 77) * s;
        Vector2 clipMax = origin + new Vector2(319, 409) * s; // exact 296x332 ScrollFrame
        ImGui.SetCursorScreenPos(clipMin);
        ImGui.InvisibleButton("##talent-scroll-wheel", clipMax - clipMin);
        if (ImGui.IsItemHovered() && ImGui.GetIO().MouseWheel != 0)
            _talentScroll = Math.Clamp(_talentScroll - Math.Sign(ImGui.GetIO().MouseWheel) * 32, 0, scrollMaximum);

        Vector2 scrollOffset = new(0, -_talentScroll * s);
        TalentInfo[] visibleTalents = _talents.TalentsForTab(_talentSelectedTab).ToArray();
        dl.PushClipRect(clipMin, clipMax, true);
        foreach (TalentInfo talent in visibleTalents.Where(x => x.DependsOn != 0))
        {
            TalentInfo prerequisite = visibleTalents.FirstOrDefault(x => x.Id == talent.DependsOn);
            if (prerequisite.Id == 0) continue;
            Vector2 from = origin + new Vector2(76.5f + prerequisite.Column * 63,
                115.5f + prerequisite.Row * 63) * s + scrollOffset;
            Vector2 to = origin + new Vector2(76.5f + talent.Column * 63,
                115.5f + talent.Row * 63) * s + scrollOffset;
            uint link = TalentRank(prerequisite) >= (int)Math.Max(1, talent.DependsOnRank)
                ? 0xff00b000u : 0xff555555u;
            dl.AddLine(from, to, link, 5 * s);
            dl.AddLine(from, to, 0xff151515, s);
        }
        foreach (TalentInfo talent in visibleTalents)
        {
            int rank = TalentRank(talent);
            bool eligible = TalentEligible(talent, out string reason);
            SpellInfo? spell = _spellCatalog?.TryGet(
                talent.RankSpells[Math.Min(rank, talent.RankSpells.Length - 1)], out SpellInfo si) == true ? si : null;
            string name = spell?.Name ?? $"Talent {talent.Id}";
            Vector2 min = origin + new Vector2(58 + talent.Column * 63,
                97 + talent.Row * 63) * s + scrollOffset;
            bool maxed = rank == talent.RankSpells.Length;
            uint slotTint = !eligible && rank == 0 ? 0xff777777u : maxed ? VanillaGold : 0xff1aff1au;
            uint slot = _gameplayArt.Handle(@"Interface\Buttons\UI-EmptySlot-White");
            if (slot != 0) dl.AddImage((nint)slot, min - new Vector2(13.5f) * s,
                min + new Vector2(50.5f) * s, Vector2.Zero, Vector2.One, slotTint);
            uint icon = _gameplayArt.Handle(spell?.IconPath ?? @"Interface\Icons\INV_Misc_QuestionMark.blp");
            if (icon != 0) dl.AddImage((nint)icon, min, min + new Vector2(37) * s,
                Vector2.Zero, Vector2.One, eligible || rank > 0 ? 0xffffffff : 0xff777777);
            uint border = _gameplayArt.Handle(@"Interface\TalentFrame\TalentFrame-RankBorder");
            Vector2 rankMin = min + new Vector2(21) * s;
            if (border != 0) dl.AddImage((nint)border, rankMin, rankMin + new Vector2(32) * s);
            uint rankColor = !eligible && rank == 0 ? 0xff888888u : maxed ? VanillaGold : 0xff00ff00u;
            GameText.DrawCentered(dl, "GameFontNormalSmall",
                $"{rank}/{talent.RankSpells.Length}", rankMin + new Vector2(16) * s,
                s, rankColor);
            ImGui.SetCursorScreenPos(min);
            ImGui.InvisibleButton($"##talent-{talent.Id}", new Vector2(37) * s);
            if (ImGui.IsItemClicked() && eligible) SpendTalent(talent.Id);
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip(); ImGui.TextUnformatted(name);
                ImGui.TextUnformatted($"Rank {rank}/{talent.RankSpells.Length}");
                if (!eligible) ImGui.TextDisabled(reason); ImGui.EndTooltip();
            }
        }
        dl.PopClipRect();
        DrawTalentScrollBar(dl, origin, s, scrollMaximum);

        GameText.DrawCentered(dl, "GameFontNormal", "Talents",
            origin + TalentFrameUiLaw.TitleCenter * s, s);

        string spentPrefix = TalentFrameUiLaw.SpentPointsPrefix(active.Name);
        string spentValue = visibleTalents.Sum(TalentRank).ToString();
        float spentPrefixWidth = GameText.MeasureWidth("GameFontNormalSmall", spentPrefix, s);
        float spentValueWidth = GameText.MeasureWidth("GameFontHighlightSmall", spentValue, s);
        Vector2 spentTop = origin + new Vector2(
            TalentFrameUiLaw.SpentPointsCenterX * s -
                (spentPrefixWidth + spentValueWidth) * .5f,
            TalentFrameUiLaw.SpentPointsTop * s);
        GameText.Draw(dl, "GameFontNormalSmall", spentPrefix, spentTop, s);
        GameText.Draw(dl, "GameFontHighlightSmall", spentValue,
            spentTop + new Vector2(spentPrefixWidth, 0f), s);

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

        float tabX = 15;
        foreach (TalentTabInfo tab in tabs)
        {
            float width = VanillaCharacterTabWidth(tab.Name, s, 10);
            if (VanillaTab(dl, $"##talent-tab-{tab.Id}", origin + new Vector2(tabX, 434) * s,
                    tab.Name, width, s, tab.Id == _talentSelectedTab))
            { _talentSelectedTab = tab.Id; _talentScroll = 0; }
            tabX += width - 15;
        }
        if (_talentWipeTrainer != 0 && VanillaButton(dl, "##talent-reset", "Unlearn Talents",
                origin + new Vector2(95, 409) * s, new Vector2(120, 22), s)) ConfirmTalentWipe();
        if (VanillaButton(dl, "##talent-cancel", "Close", origin + new Vector2(265, 409) * s,
                new Vector2(80, 22), s)) _talentOpen = false;
        Vector2 close = origin + new Vector2(324, 10) * s;
        DrawImageButton(dl, "##talent-close", close, new Vector2(32) * s,
            @"Interface\Buttons\UI-Panel-MinimizeButton-Up", @"Interface\Buttons\UI-Panel-MinimizeButton-Down",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Highlight");
        if (ImGui.IsItemClicked()) _talentOpen = false;
        if (_uiParityArmed && _uiParityPanel == "talent-frame") MarkUiParityFrameComplete();
        ImGui.End();
    }

    private void DrawTalentScrollBar(ImDrawListPtr dl, Vector2 origin, float s, float maximum)
    {
        // TalentFrameScrollFrame artwork from Blizzard_TalentUI.xml.
        uint background = _gameplayArt?.Handle(@"Interface\PaperDollInfoFrame\UI-Character-ScrollBar") ?? 0;
        if (background != 0)
        {
            Vector2 top = origin + new Vector2(317, 72) * s;
            dl.AddImage((nint)background, top, top + new Vector2(31, 256) * s,
                Vector2.Zero, new Vector2(.484375f, 1));
            Vector2 bottom = origin + new Vector2(317, 305) * s;
            dl.AddImage((nint)background, bottom, bottom + new Vector2(31, 106) * s,
                new Vector2(.515625f, 0), new Vector2(1, .4140625f));
        }
        void Button(string id, Vector2 min, bool up, bool enabled)
        {
            ImGui.SetCursorScreenPos(min); ImGui.InvisibleButton(id, new Vector2(16) * s);
            bool active = enabled && ImGui.IsItemActive();
            string stem = up ? "UI-ScrollBar-ScrollUpButton" : "UI-ScrollBar-ScrollDownButton";
            string state = !enabled ? "Disabled" : active ? "Down" : "Up";
            uint texture = _gameplayArt?.Handle($@"Interface\Buttons\{stem}-{state}") ?? 0;
            if (texture != 0) dl.AddImage((nint)texture, min, min + new Vector2(16) * s,
                new Vector2(.25f), new Vector2(.75f));
            if (enabled && ImGui.IsItemClicked())
                _talentScroll = Math.Clamp(_talentScroll + (up ? -150 : 150), 0, maximum);
        }
        Button("##talent-scroll-up", origin + new Vector2(325, 77) * s, true, _talentScroll > 0);
        Button("##talent-scroll-down", origin + new Vector2(325, 393) * s, false, _talentScroll < maximum);

        float y = 93 + (maximum <= 0 ? 0 : _talentScroll / maximum * 284);
        Vector2 knob = origin + new Vector2(325, y) * s;
        uint art = _gameplayArt?.Handle(@"Interface\Buttons\UI-ScrollBar-Knob") ?? 0;
        if (art != 0) dl.AddImage((nint)art, knob, knob + new Vector2(16) * s,
            new Vector2(.25f), new Vector2(.75f));
        ImGui.SetCursorScreenPos(origin + new Vector2(325, 93) * s);
        ImGui.InvisibleButton("##talent-scroll-track", new Vector2(16, 300) * s);
        if (ImGui.IsItemActive())
        {
            float logicalY = (ImGui.GetIO().MousePos.Y - origin.Y) / s - 101;
            _talentScroll = Math.Clamp(logicalY / 284 * maximum, 0, maximum);
        }
    }

    private static string MoneyText(uint copper) => $"{copper / 10000}g {(copper / 100) % 100}s {copper % 100}c";
}

using ImGuiNET;
using System.Numerics;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

/// <summary>
/// Free View commander console — the WC3-style bottom dock (Drawing 2 +
/// owner design language in CRPG_RTS_MMO_PARTY_COMMAND_UI.md: vanilla skin,
/// WC3 command grammar). Three fixed regions: the SQUAD grid (all ten slots,
/// WC3 group wells), the INFO panel (scope line, then a portrait unit card
/// for a single selection or portrait chips for a group), and the COMMAND
/// CARD — an icon grid whose art is the vanilla pet bar's own idiom
/// (FrameXML: Attack=Ability_GhoulFrenzy, Follow=Ability_Tracking,
/// Wait=Spell_Nature_TimeStop; the rest archive-verified via mpqpeek).
/// Formation and sheath orders are SuperUI-Core order types (8/9/10).
/// </summary>
public sealed partial class GameLoop
{
    // Shelf-level toggle; the next sheath order inverts it. Bots spawn armed.
    private bool _rtsWeaponsSheathed;

    // The PRIMARY of the current selection (WC3-style): the unit whose card + ability row fill the
    // console's middle panel, gold-bordered in the portrait grid. Set by single-clicking a portrait
    // or Tab-cycling; resolves to selection[0] whenever it is unset or has left the selection.
    private ulong _rtsPrimaryGuid;
    private ulong RtsPrimaryGuid =>
        _rtsPrimaryGuid != 0 && _freecamSelection.Contains(_rtsPrimaryGuid)
            ? _rtsPrimaryGuid
            : _freecamSelection.Count > 0 ? _freecamSelection[0] : 0;

    /// <summary>Move the primary one slot through the current selection (Tab / Shift+Tab).</summary>
    private void CycleRtsPrimary(int dir)
    {
        if (_freecamSelection.Count == 0) return;
        int idx = _freecamSelection.IndexOf(RtsPrimaryGuid);
        if (idx < 0) idx = 0;
        int n = _freecamSelection.Count;
        _rtsPrimaryGuid = _freecamSelection[((idx + dir) % n + n) % n];
    }

    // A cast queued while control of the primary is still being handed over (possess-on-cast).
    private ulong _pendingCastPrimary;
    private uint _pendingCastSpellId;
    private double _pendingCastAt;

    /// <summary>
    /// Cast an ability from the PRIMARY's command card, riding the possess wire (step 2, no new
    /// server hook): if you already drive the primary, cast now; otherwise take control of it —
    /// releasing any other body first — and fire the cast the moment its bars are live. The
    /// primary stays possessed afterwards, so further casts on it are instant.
    /// </summary>
    private void CastPrimaryAbility(ulong primary, uint spellId)
    {
        if (primary == 0 || spellId == 0) return;
        if (ControlledGuid == primary && CanAuthorControlledGameplay)
        {
            TryCast(spellId);   // already driving it — instant
            return;
        }
        _pendingCastPrimary = primary;
        _pendingCastSpellId = spellId;
        _pendingCastAt = NowSeconds();
        if (_controlState is ControlState.OwnChar or ControlState.FreeCam)
            RequestPossess(primary);
        else if (_controlState == ControlState.Possessing && _controlTargetGuid != primary)
        {
            _controlSwitchQueued = primary;          // possess the primary once the release lands
            RequestControlRelease(toFreecam: true);  // stay in the sky through the swap
        }
        // PossessPending / ReleasePending: the in-flight transition resolves on its own; the
        // pending cast fires if it lands on the primary, else it times out.
    }

    /// <summary>
    /// Fire a possess-on-cast ability once control has landed on the primary and its spellbook has
    /// synced. Run every frame from UpdateControlInput; gives up after a short window so a denied
    /// or slow possession does not leave a stale cast armed.
    /// </summary>
    private void TryFirePendingPrimaryCast()
    {
        if (_pendingCastSpellId == 0) return;
        if (NowSeconds() - _pendingCastAt > 2.5)
        {
            _pendingCastSpellId = 0;
            _pendingCastPrimary = 0;
            return;
        }
        if (_controlState != ControlState.Possessing || _controlTargetGuid != _pendingCastPrimary ||
            !CanAuthorControlledGameplay || !_actions.KnownSpells.Contains(_pendingCastSpellId))
            return;   // control/spellbook not ready yet — keep waiting until the timeout
        uint spellId = _pendingCastSpellId;
        _pendingCastSpellId = 0;
        _pendingCastPrimary = 0;
        TryCast(spellId);
    }

    private const byte SuiOrderFormationLine = 8;
    private const byte SuiOrderFormationCircle = 9;
    private const byte SuiOrderSheath = 10;

    // Command-card art. The first three are the vanilla pet bar's own tokens
    // read from the shipped FrameXML (PetActionBarFrame.lua) — the native
    // idiom for commanding an AI companion. The rest exist in the archives.
    private const string ConsoleIconFocus = @"Interface\Icons\Ability_GhoulFrenzy";
    private const string ConsoleIconRegroup = @"Interface\Icons\Ability_Tracking";
    private const string ConsoleIconHold = @"Interface\Icons\Spell_Nature_TimeStop";
    private const string ConsoleIconPatrol = @"Interface\Icons\Ability_Hunter_Pathfinding";
    private const string ConsoleIconLine = @"Interface\Icons\INV_Banner_01";
    private const string ConsoleIconCircle = @"Interface\Icons\Spell_Holy_PrayerofHealing";
    private const string ConsoleIconSheathe = @"Interface\Icons\Ability_Warrior_Disarm";
    private const string ConsoleIconDraw = @"Interface\Icons\INV_Sword_04";

    // Console geometry (logical units, × UI scale). Regions are fixed so the
    // dock reads as furniture, never a resizing tooltip.
    private const float ConsoleWidth = 640f;
    private const float ConsoleHeight = 116f;
    private const float ConsoleSquadsX = 10f;
    private const float ConsoleInfoX = 156f;
    private const float ConsoleCardX = 498f;

    private void DrawRtsCommandShelf()
    {
        // The console is the free view's standing furniture — present even with
        // nothing selected, like a WC3 console with an empty info panel.
        if (!_freeView || _net is null) return;
        List<ulong> subjects = [.. RtsControlGroupLaw.NormalizeMembers(_freecamSelection)];

        float scale = GameplayUiScale();
        Vector2 display = ImGui.GetIO().DisplaySize;
        // The commander console owns the bottom edge: the body chrome (action
        // bars, stance, pet, bags, micro menu) stands down in the free view.
        ImGui.SetNextWindowPos(new Vector2(display.X * 0.5f, display.Y - 12f * scale),
            ImGuiCond.Always, new Vector2(0.5f, 1f));
        ImGui.SetNextWindowSize(new Vector2(ConsoleWidth, ConsoleHeight) * scale,
            ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);
        // NoBringToFrontOnFocus: the shelf is standing HUD furniture, so it must never rise above
        // a panel the player opened. Without it the shelf was display-front while every vanilla
        // panel frame is deliberately display-back, and the Key Bindings footer row landed inside
        // the shelf's rect - FindHoveredWindow returned the shelf and Reset/Unbind/Okay/Cancel
        // went dead in the free view while still drawing. Found by audit, 2026-08-26.
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoBringToFrontOnFocus;
        if (!ImGui.Begin("##rts-command-shelf", flags)) { ImGui.End(); return; }

        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        Vector2 origin = ImGui.GetWindowPos();
        // Warcraft-3 carved-stone tablet in place of the flat tooltip skin: a dark stone body, a
        // near-black ground edge, a gilt inlay that catches the light, and corner studs — the same
        // painted-chrome idiom the square minimap/skill panels already wear.
        DrawRtsConsoleBackdrop(dl, origin, origin + ImGui.GetWindowSize(), scale);
        // Region dividers — carved grooves with a gilt catch, not hairline mullions.
        DrawRtsConsoleDivider(dl, origin, ConsoleInfoX - 8f, scale);
        DrawRtsConsoleDivider(dl, origin, ConsoleCardX - 8f, scale);

        // Left region: the WC3-style portrait grid of the CURRENT selection (primary gold-bordered).
        // The saved control-group "squad" grid (DrawRtsSquadGrid) is shelved for the true-RTS mode.
        DrawRtsSelectionPortraits(dl, origin, scale);
        DrawRtsConsoleInfo(dl, origin, subjects, scale);
        DrawRtsCommandCard(dl, origin, subjects, scale);
        ImGui.End();
    }

    /// <summary>The WC3 command console's carved-stone tablet: dark stone body graduating into
    /// shadow, a near-black ground edge, a bevelled gilt inlay, and corner studs.</summary>
    private void DrawRtsConsoleBackdrop(ImDrawListPtr dl, Vector2 min, Vector2 max, float scale)
    {
        float rule = MathF.Max(1f, scale);
        float inset = MathF.Max(2f, 3f * scale);
        // Carved stone body, top-lit → shadowed.
        dl.AddRectFilledMultiColor(min, max,
            PainterlyStoneMid, PainterlyStoneMid, PainterlyStoneLow, PainterlyStoneLow);
        // Near-black ground so the stone has an edge against the world, and the outer bevel.
        dl.AddRect(min, max, PainterlyFrameOuter, 0f, ImDrawFlags.None, rule * 2f);
        DrawBevel(dl, min, max, rule, PainterlyStoneTop, PainterlyFrameOuter);
        // Gilt inlay just inside, itself bevelled so it reads as inlaid metal.
        Vector2 gMin = min + new Vector2(inset), gMax = max - new Vector2(inset);
        dl.AddRect(gMin, gMax, PainterlyFrameRule, 0f, ImDrawFlags.None, rule);
        DrawBevel(dl, gMin, gMax, MathF.Max(1f, rule * 0.5f), PainterlyGoldLit, PainterlyGoldShade);
        DrawCornerStuds(dl, min, max, scale);
    }

    /// <summary>A carved groove between console regions: a shadowed cut with a gilt catch on its
    /// lit side, replacing the old hairline mullion.</summary>
    private void DrawRtsConsoleDivider(ImDrawListPtr dl, Vector2 origin, float x, float scale)
    {
        float rule = MathF.Max(1f, scale);
        Vector2 a = origin + new Vector2(x, 12f) * scale;
        Vector2 b = origin + new Vector2(x, ConsoleHeight - 12f) * scale;
        dl.AddLine(a, b, PainterlyFrameOuter, rule * 2f);
        dl.AddLine(a + new Vector2(rule, 0f), b + new Vector2(rule, 0f), PainterlyGoldShade, rule);
    }

    /// <summary>
    /// WC3-style portrait grid of the CURRENT selection — the console's left region. Each cell is a
    /// unit's portrait over a health bar; the PRIMARY (whose card + abilities fill the middle panel)
    /// wears a gold border. Single-click makes a unit primary, double-click selects only it,
    /// Shift+click drops it. Tab / Shift+Tab cycle the primary. The saved-group "squad" grid
    /// (DrawRtsSquadGrid) is shelved for the true-RTS mode.
    /// </summary>
    private void DrawRtsSelectionPortraits(ImDrawListPtr dl, Vector2 origin, float scale)
    {
        // Scope header: name the matching saved squad, else a plain selected count (the count is
        // the total even when more units are selected than the grid draws).
        string scope = _freecamSelection.Count == 0 ? "Selection"
            : $"Selection · {_freecamSelection.Count}";
        for (int i = 0; i < _rtsControlGroups.Length; i++)
            if (_rtsControlGroups[i].Count > 0 &&
                SameRtsMembers(_rtsControlGroups[i], _freecamSelection))
            { scope = $"Squad {RtsControlGroupLaw.DisplayNumber(i)} · {_freecamSelection.Count}"; break; }
        GameText.Draw(dl, "GameFontNormalSmall", scope,
            origin + new Vector2(ConsoleSquadsX, 8f) * scale, scale);

        ulong primary = RtsPrimaryGuid;
        var cell = new Vector2(21f, 19f) * scale;
        float barH = 3f * scale, gap = 2f * scale;
        const int cols = 6, maxCells = 12;
        ulong primaryPick = 0, soloPick = 0, dropPick = 0;
        int shown = 0;
        for (int i = 0; i < _freecamSelection.Count && shown < maxCells; i++)
        {
            ulong guid = _freecamSelection[i];
            if (!_entities.TryGet(guid, out WorldEntity unit)) continue;
            Vector2 min = origin + new Vector2(ConsoleSquadsX, 26f) * scale +
                new Vector2(shown % cols * (cell.X + gap), shown / cols * (cell.Y + gap));
            shown++;
            Vector2 max = min + cell;
            ImGui.SetCursorScreenPos(min);
            ImGui.InvisibleButton($"##sel-portrait-{i}", cell);
            bool hovered = ImGui.IsItemHovered();

            (_, byte classId, _, _) = unit.Fields.Bytes0;
            var bodyMax = new Vector2(max.X, max.Y - barH);
            uint baked = PartyPortraitHandle(guid);
            if (baked != 0 && !unit.IsDead)
                dl.AddImage((nint)baked, min, bodyMax, new Vector2(0, 1), new Vector2(1, 0));
            else
            {
                dl.AddRectFilled(min, bodyMax, unit.IsDead ? 0xff40444a : ClassChipColor(classId));
                string initial = ResolveUnitName(guid) is { Length: > 0 } name
                    ? name[..1].ToUpperInvariant() : "?";
                Vector2 half = ImGui.CalcTextSize(initial) * 0.5f;
                dl.AddText(new Vector2((min.X + max.X) * 0.5f, (min.Y + bodyMax.Y) * 0.5f) - half,
                    0xe0101418, initial);
            }
            uint maxHp = unit.Fields.MaxHealth;
            float hp = maxHp > 0 ? Math.Clamp(unit.Fields.Health / (float)maxHp, 0f, 1f) : 0f;
            dl.AddRectFilled(new Vector2(min.X, max.Y - barH), max, 0xff101418);
            dl.AddRectFilled(new Vector2(min.X, max.Y - barH),
                new Vector2(min.X + cell.X * hp, max.Y),
                hp > 0.5f ? 0xff40c040u : hp > 0.2f ? 0xff40c0e0u : 0xff4040d0u);

            // Carved slot: the PRIMARY wears a bevelled gilt inlay; others a stone-bevelled frame
            // that takes a gilt edge on hover.
            bool isPrimary = guid == primary;
            if (isPrimary)
            {
                dl.AddRect(min, max, PainterlyFrameRule, 0, ImDrawFlags.None, MathF.Max(2f, 2f * scale));
                DrawBevel(dl, min, max, MathF.Max(1f, scale), PainterlyGoldLit, PainterlyGoldShade);
            }
            else
            {
                DrawBevel(dl, min, max, MathF.Max(1f, scale), PainterlyStoneTop, PainterlyFrameOuter);
                dl.AddRect(min, max, hovered ? PainterlyGoldLit : PainterlyFrameOuter,
                    0, ImDrawFlags.None, MathF.Max(1f, scale));
            }

            if (hovered)
                HoverTip($"{ResolveUnitName(guid)} — {(int)(hp * 100)}%\n" +
                    "Click: make primary · Double-click: select only · Shift+click: drop");
            if (hovered && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) soloPick = guid;
            else if (ImGui.IsItemClicked())
            {
                if (ImGui.GetIO().KeyShift) dropPick = guid;
                else primaryPick = guid;
            }
        }

        // Mutations after the loop — never mutate the selection mid-iteration.
        if (soloPick != 0)
        {
            _freecamSelection.Clear();
            _freecamSelection.Add(soloPick);
            _rtsPrimaryGuid = soloPick;
            PlayCompanionSelectionVoice(soloPick);
        }
        else if (dropPick != 0)
        {
            _freecamSelection.Remove(dropPick);
            if (_rtsPrimaryGuid == dropPick) _rtsPrimaryGuid = 0;   // resolver falls back to [0]
        }
        else if (primaryPick != 0)
            _rtsPrimaryGuid = primaryPick;

        // Utility row: the free view hides the bag bar and micro menu, so the console keeps the
        // party panels reachable. Carried over from the shelved squad grid.
        Vector2 rowPos = origin + new Vector2(ConsoleSquadsX, 74f) * scale;
        if (VanillaButton(dl, "##console-bags", "Bags", rowPos, new Vector2(58f, 20f), scale))
            OpenPartyInventory(_freecamSelection.Count == 1 ? _freecamSelection[0] : LocalPlayerGuid);
        if (ImGui.IsItemHovered())
            HoverTip("Party Inventory — everyone's bags and equipment, side by side");
        ulong tacticsBot = _freecamSelection.FirstOrDefault(IsRtsGroupableBot);
        if (tacticsBot == 0)
            tacticsBot = _partyMembers.FirstOrDefault(m => IsRtsGroupableBot(m.Guid))?.Guid ?? 0;
        if (VanillaButton(dl, "##console-tactics", "Tactics",
                rowPos + new Vector2(64f, 0f) * scale, new Vector2(66f, 20f), scale,
                tacticsBot != 0) && tacticsBot != 0)
            OpenPartyTactics(tacticsBot);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            HoverTip(tacticsBot != 0
                ? "Party Tactics — roles and quick-slot AI policy"
                : "Party Tactics needs a companion bot in the party");
    }

    /// <summary>
    /// All ten WC3 group slots as a fixed 5×2 grid. A filled well recalls its
    /// squad on click; Ctrl+click on ANY well saves the current selection
    /// there (the keys 1-0 / Ctrl+1-0 do the same).
    ///
    /// Shift+click still saves as well. The keyboard chord moved to Ctrl for RTS convention
    /// (2026-08-26), but no other binding competes for a modified click on a well, so the old
    /// gesture keeps working rather than becoming a silent no-op in muscle memory.
    /// </summary>
    private void DrawRtsSquadGrid(ImDrawListPtr dl, Vector2 origin, float scale)
    {
        GameText.Draw(dl, "GameFontNormalSmall", "Squads",
            origin + new Vector2(ConsoleSquadsX, 8f) * scale, scale);
        var cell = new Vector2(25f, 19f) * scale;
        float gap = 2f * scale;
        for (int i = 0; i < _rtsControlGroups.Length; i++)
        {
            Vector2 min = origin + new Vector2(ConsoleSquadsX, 26f) * scale +
                new Vector2(i % 5 * (cell.X + gap), i / 5 * (cell.Y + gap));
            Vector2 max = min + cell;
            ImGui.SetCursorScreenPos(min);
            ImGui.InvisibleButton($"##squad-well-{i}", cell);
            bool hovered = ImGui.IsItemHovered();
            int count = _rtsControlGroups[i].Count;
            string number = RtsControlGroupLaw.DisplayNumber(i);
            if (count > 0)
            {
                dl.AddRectFilled(min, max, 0xd01a222a);
                string label = $"{number}·{count}";
                Vector2 half = ImGui.CalcTextSize(label) * 0.5f;
                dl.AddText((min + max) * 0.5f - half, 0xffd0b060, label);
            }
            else
            {
                // Empty well: the slot exists (the grid is furniture), dimmed.
                dl.AddRectFilled(min, max, 0x66141a20);
                Vector2 half = ImGui.CalcTextSize(number) * 0.5f;
                dl.AddText((min + max) * 0.5f - half, 0x66aabbcc, number);
            }
            dl.AddRect(min, max, hovered ? 0xffd0b060 : 0xff2a343d,
                0, ImDrawFlags.None, MathF.Max(1f, scale));
            if (hovered)
                HoverTip(count > 0
                    ? $"Squad {number} — {count} member(s)\nClick: select (key {number}) · " +
                      $"Ctrl+click: save current selection here"
                    : $"Squad {number} is empty — Ctrl+click (or Ctrl+{number}) " +
                      "saves the current selection");
            if (ImGui.IsItemClicked())
            {
                if (ImGui.GetIO().KeyCtrl || ImGui.GetIO().KeyShift) AssignRtsControlGroup(i);
                else if (count > 0) RecallRtsControlGroup(i);
                else SetRtsControlGroupStatus($"Group {number} is empty — Ctrl+{number} " +
                    "saves the current selection.");
            }
        }

        // Utility row: the free view hides the bag bar and micro menu, so the
        // console is where the party panels live now.
        Vector2 rowPos = origin + new Vector2(ConsoleSquadsX, 74f) * scale;
        if (VanillaButton(dl, "##console-bags", "Bags", rowPos, new Vector2(58f, 20f), scale))
            OpenPartyInventory(_freecamSelection.Count == 1
                ? _freecamSelection[0] : LocalPlayerGuid);
        if (ImGui.IsItemHovered())
            HoverTip("Party Inventory — everyone's bags and equipment, side by side");
        ulong tacticsBot = _freecamSelection.FirstOrDefault(IsRtsGroupableBot);
        if (tacticsBot == 0)
            tacticsBot = _partyMembers.FirstOrDefault(m => IsRtsGroupableBot(m.Guid))?.Guid ?? 0;
        if (VanillaButton(dl, "##console-tactics", "Tactics",
                rowPos + new Vector2(64f, 0f) * scale, new Vector2(66f, 20f), scale,
                tacticsBot != 0) && tacticsBot != 0)
            OpenPartyTactics(tacticsBot);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            HoverTip(tacticsBot != 0
                ? "Party Tactics — roles and quick-slot AI policy"
                : "Party Tactics needs a companion bot in the party");
    }

    /// <summary>The console's center: scope line, then the WC3 info panel —
    /// a portrait unit card for one unit, portrait chips for a group.</summary>
    private void DrawRtsConsoleInfo(ImDrawListPtr dl, Vector2 origin,
        List<ulong> subjects, float scale)
    {
        Vector2 scopePos = origin + new Vector2(ConsoleInfoX, 8f) * scale;
        if (subjects.Count == 0)
        {
            GameText.Draw(dl, "GameFontNormalSmall", "No selection", scopePos, scale);
            dl.AddText(origin + new Vector2(ConsoleInfoX, 30f) * scale, 0xff9aa4ab,
                "Click or drag units in the world,");
            dl.AddText(origin + new Vector2(ConsoleInfoX, 44f) * scale, 0xff9aa4ab,
                "or pick a squad.");
            return;
        }

        // The left portrait grid names the selection now; this panel is the PRIMARY's card.
        // Route readout stays here, right-aligned on the top row: the Patrol draft while one is
        // armed, else the standing chain this selection would patrol.
        string? route = _rtsPatrolAuthoring
            ? $"Drafting route · {_rtsPatrolDraft.Count} pt{(_rtsPatrolDraft.Count == 1 ? "" : "s")}"
            : _rtsWaypointChain.Count > 0 && SameRtsMembers(_rtsWaypointSubjects, subjects)
                ? $"Route · {_rtsWaypointChain.Count} pt{(_rtsWaypointChain.Count == 1 ? "" : "s")}"
                : null;
        if (route is not null)
        {
            float routeW = ImGui.CalcTextSize(route).X;
            dl.AddText(origin + new Vector2(ConsoleCardX - 16f, 9f) * scale -
                new Vector2(routeW, 0f), _rtsPatrolAuthoring ? 0xff60d0f0u : 0xff9aa4abu, route);
        }

        PreparedSharedSpellTooltip? cardTooltip = null;
        Vector2 content = origin + new Vector2(ConsoleInfoX, 26f) * scale;
        ulong primary = RtsPrimaryGuid;
        if (primary != 0 && _entities.TryGet(primary, out WorldEntity cardUnit))
            DrawRtsConsoleUnitCard(primary, cardUnit, content, scale, ref cardTooltip);
        if (cardTooltip is { } preparedCard)
            OfferPreservedSharedGameTooltipRenderer(preparedCard.Owner,
                () => DrawSpellTooltip(preparedCard.Snapshot));
    }

    /// <summary>Portrait, name, level/class, vitals, and the primary's abilities — the console's
    /// answer to WC3's info panel. Clicking an ability casts it: possess-on-cast takes control of
    /// the primary (riding the possess wire) and fires the spell once its bars are live.</summary>
    private void DrawRtsConsoleUnitCard(ulong guid, WorldEntity unit, Vector2 content,
        float scale, ref PreparedSharedSpellTooltip? tooltip)
    {
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        (byte race, byte classId, byte gender, byte powerType) = unit.Fields.Bytes0;

        // Portrait: the party frames' own live 3-D bake when it exists (V is
        // flipped — render target, not a BLP), the static stand-in otherwise.
        var portraitSize = new Vector2(44f) * scale;
        uint baked = PartyPortraitHandle(guid);
        if (baked != 0)
            dl.AddImage((nint)baked, content, content + portraitSize,
                new Vector2(0, 1), new Vector2(1, 0));
        else
        {
            string sex = gender == 1 ? "Female" : "Male";
            string raceName = race == 5 ? "Scourge" : RaceName(race).Replace(" ", "");
            uint standIn = PainterlyArt(
                $@"Interface\CharacterFrame\TemporaryPortrait-{sex}-{raceName}");
            if (standIn != 0)
                dl.AddImage((nint)standIn, content, content + portraitSize);
            else
                dl.AddRectFilled(content, content + portraitSize, 0xd01a222a);
        }
        dl.AddRect(content, content + portraitSize, 0xff2a343d,
            0, ImDrawFlags.None, MathF.Max(1f, scale));
        // Role medallion at the portrait's lower-right corner, fully on the
        // frame — the same disc the party rows wear.
        string unitName = ResolveUnitName(guid);
        DrawRoleMedallion(dl, content + new Vector2(37f, 37f) * scale, 7f * scale,
            LoadBotBars().BotRoles.GetValueOrDefault(unitName, "DPS"), scale);

        // Name, level · class · state, vitals to the portrait's right.
        Vector2 text = content + new Vector2(52f, 0f) * scale;
        GameText.Draw(dl, "GameFontNormalSmall", unitName, text, scale);
        string className = ClassIdName(classId);
        string detail = className.Length != 0
            ? $"Lv {unit.Fields.Level} {className}" : $"Lv {unit.Fields.Level}";
        if (RtsEnlisted(guid)) detail += " · enlisted";
        if (_rtsOrderChips.TryGetValue(guid, out string? chipText)) detail += $" · {chipText}";
        dl.AddText(text + new Vector2(0f, 16f) * scale, 0xff9aa4ab, detail);

        // Per-member panel shortcuts on the card's right edge.
        bool inParty = false;
        foreach (PartyMember member in _partyMembers)
            if (member.Guid == guid) { inParty = true; break; }
        if (VanillaButton(dl, $"##card-bags-{guid}", "Bags",
                content + new Vector2(196f, 0f) * scale, new Vector2(58f, 20f), scale,
                inParty) && inParty)
            OpenPartyInventory(guid);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            HoverTip(inParty ? "This member's bags and equipment (Party Inventory)"
                : "Bags are a party privilege — this unit is not in your party");
        if (VanillaButton(dl, $"##card-tactics-{guid}", "Tactics",
                content + new Vector2(196f, 24f) * scale, new Vector2(58f, 20f), scale,
                inParty) && inParty)
            OpenPartyTactics(guid);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            HoverTip(inParty ? "This member's role and quick-slot AI policy (Party Tactics)"
                : "Tactics are a party privilege — this unit is not in your party");

        Vector2 vmin = text + new Vector2(0f, 31f) * scale;
        float barW = 130f * scale, barH = 5f * scale;
        uint maxHp = unit.Fields.MaxHealth;
        float hp = maxHp > 0 ? Math.Clamp(unit.Fields.Health / (float)maxHp, 0f, 1f) : 0f;
        dl.AddRectFilled(vmin, vmin + new Vector2(barW, barH), 0xff101418);
        dl.AddRectFilled(vmin, vmin + new Vector2(barW * hp, barH), 0xff1db000);
        uint maxPower = unit.Fields.MaxPower(powerType);
        if (maxPower > 0)
        {
            float power = Math.Clamp(unit.Fields.Power(powerType) / (float)maxPower, 0f, 1f);
            Vector2 pmin = vmin + new Vector2(0, barH + 2f * scale);
            dl.AddRectFilled(pmin, pmin + new Vector2(barW, barH), 0xff101418);
            dl.AddRectFilled(pmin, pmin + new Vector2(barW * power, barH), powerType switch
            {
                1 => 0xff0000c0u,   // rage
                3 => 0xff00d1d1u,   // energy
                _ => 0xffde7000u,   // mana
            });
        }

        // The primary's ability row under the portrait — truthful icons; click casts via
        // possess-on-cast (CastPrimaryAbility).
        PlayerActions store = ActionsFor(guid);
        if (store.OccupiedCount == 0) EnsureBotBarForViewing(guid);
        double now = NowSeconds();
        float size = 22f * scale;
        var side = new Vector2(size, size);
        Vector2 rowMin = content + new Vector2(0f, 50f) * scale;
        int drawn = 0;
        for (int slot = 0; slot < 12 && drawn < 8; slot++)
        {
            if (store[slot] is not ActionSlot action || action.Kind != 0 ||
                _spellCatalog?.TryGet(action.ActionId, out SpellInfo spell) != true)
                continue;
            Vector2 min = rowMin + new Vector2(drawn * (size + 3f * scale), 0f);
            drawn++;
            Vector2 max = min + side;
            ImGui.SetCursorScreenPos(min);
            ImGui.InvisibleButton($"##card-{guid}-{slot}", side);
            // Attack's DBC icon is the internal Temp face — the law swaps in
            // the unit's own weapon art (public visible-item entries).
            uint icon = PainterlyArt(ResolveSpellActionIcon(spell, unit));
            if (icon != 0) dl.AddImage((nint)icon, min, max);
            if (store.TryCooldownDisplay(action.ActionId, 0, spell, now,
                    out CooldownDisplay cooldown) && cooldown.SweepFraction is float sweep)
                DrawCooldownSwipe(dl, min, max, sweep);
            DrawBevel(dl, min, max, MathF.Max(1f, scale), PainterlyStoneTop, PainterlyFrameOuter);
            dl.AddRect(min, max, ImGui.IsItemHovered() ? PainterlyFrameRule : PainterlyFrameOuter,
                0, ImDrawFlags.None, MathF.Max(1f, scale));
            if (ImGui.IsItemHovered())
                tooltip = PrepareSharedSpellTooltip(
                    new GameTooltipOwnerKey("console-card", (ulong)slot + 1),
                    spell.Id, scale, SpellTooltipPlacement.DefaultBottomRight);
            if (ImGui.IsItemClicked())
                CastPrimaryAbility(guid, action.ActionId);
        }
        if (drawn == 0)
            dl.AddText(rowMin + new Vector2(0f, 4f * scale), 0xff9aa4ab,
                "abilities syncing…");
    }

    /// <summary>
    /// The WC3 command card: an icon grid of the console verbs. Same orders,
    /// voices, and chat lines as the old text shelf — only the dress changed
    /// (owner design language: command-card styling from WC3, art from the
    /// vanilla pet-bar idiom).
    /// </summary>
    private void DrawRtsCommandCard(ImDrawListPtr dl, Vector2 origin,
        List<ulong> subjects, float scale)
    {
        if (_net is null) return;
        GameText.Draw(dl, "GameFontNormalSmall", "Orders",
            origin + new Vector2(ConsoleCardX, 8f) * scale, scale);

        bool any = subjects.Count > 0;
        bool hostileTargeted = _selectionGuid != 0 &&
            _entities.TryGet(_selectionGuid, out WorldEntity shelfTarget) &&
            !shelfTarget.IsDead && CanAttack(shelfTarget);
        bool routeReady = _rtsWaypointChain.Count > 0 &&
            SameRtsMembers(_rtsWaypointSubjects, subjects);

        int cellIndex = 0;
        bool CardButton(string id, string icon, string tooltip, bool enabled, bool lit = false)
        {
            var side = new Vector2(30f, 30f) * scale;
            Vector2 min = origin + new Vector2(ConsoleCardX, 26f) * scale +
                new Vector2(cellIndex % 4 * (side.X + 3f * scale),
                    cellIndex / 4 * (side.Y + 3f * scale));
            cellIndex++;
            Vector2 max = min + side;
            ImGui.SetCursorScreenPos(min);
            ImGui.InvisibleButton(id, side);
            bool hovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled);
            uint art = PainterlyArt(icon);
            if (art != 0) dl.AddImage((nint)art, min, max);
            if (!enabled) dl.AddRectFilled(min, max, 0xaa10141c);   // dimmed, WC3-gray
            DrawBevel(dl, min, max, MathF.Max(1f, scale), PainterlyStoneTop, PainterlyFrameOuter);
            dl.AddRect(min, max, lit || (hovered && enabled) ? PainterlyFrameRule : PainterlyFrameOuter,
                0, ImDrawFlags.None, MathF.Max(1f, scale));
            if (hovered) HoverTip(tooltip);
            return enabled && ImGui.IsItemClicked();
        }

        if (CardButton("##card-focus", ConsoleIconFocus, hostileTargeted && any
                ? "Focus: send the selection at your current target"
                : "Focus needs a selection and a hostile target\n(click one in the world first)",
                any && hostileTargeted) &&
            _net.SuiOrder(1, subjects, _selectionGuid, 0, 0, 0))
        {
            NoteCompanionOrder(1, subjects);
            AddChatMessage($"{OrderSubjectLabel(subjects)}: attack {ResolveWorldUnitName(_selectionGuid)}!");
        }
        if (CardButton("##card-regroup", ConsoleIconRegroup,
                "Regroup: abandon the tactical route and\nescort the body you drive", any) &&
            _net.SuiOrder(5, subjects, ControlledGuid, 0, 0, 0))
        {
            NoteCompanionOrder(5, subjects);
            AddChatMessage($"{OrderSubjectLabel(subjects)}: regroup on {ResolveUnitName(ControlledGuid)}.");
        }
        if (CardButton("##card-hold", ConsoleIconHold, "Hold: stop and hold this spot", any) &&
            _net.SuiOrder(2, subjects, 0, 0, 0, 0))
        {
            NoteCompanionOrder(2, subjects);
            AddChatMessage($"{OrderSubjectLabel(subjects)}: stand your ground.");
        }
        // Patrol is a MODE (owner 2026-08-25): first click arms the draft,
        // right-clicks chain cold waypoints, the second click engages the
        // loop; Escape cancels. A Shift+RightClick route that already exists
        // for this selection still engages directly, as before.
        string patrolTip = _rtsPatrolAuthoring
            ? $"Patrol (armed): right-click ground to chain waypoints — " +
              $"{_rtsPatrolDraft.Count} so far.\nClick again to engage the loop; Escape cancels."
            : routeReady
                ? "Patrol: loop the authored waypoint route"
                : "Patrol: click, then right-click ground points to chain\n" +
                  "a route, then click Patrol again to engage the loop.";
        if (CardButton("##card-patrol", ConsoleIconPatrol, patrolTip,
                any || _rtsPatrolAuthoring, _rtsPatrolAuthoring))
        {
            if (_rtsPatrolAuthoring)
                EngageRtsPatrolDraft();
            else if (routeReady)
                foreach (ulong patrolGuid in subjects)
                    if (_entities.TryGet(patrolGuid, out WorldEntity unit) && !unit.IsDead)
                    {
                        if (_net.SuiOrder(4, subjects, 0,
                                unit.Position.X, unit.Position.Y, unit.Position.Z))
                        {
                            NoteCompanionOrder(4, subjects);
                            AddChatMessage($"{OrderSubjectLabel(subjects)}: patrol the route.");
                        }
                        break;
                    }
            else
                BeginRtsPatrolAuthoring(subjects);
        }
        if (CardButton("##card-line", ConsoleIconLine,
                "Line: standing army — ranks of five facing you,\nformed where the squad stands", any) &&
            _net.SuiOrder(SuiOrderFormationLine, subjects, 0, 0, 0, 0))
        {
            NoteCompanionOrder(SuiOrderFormationLine, subjects);
            AddChatMessage($"{OrderSubjectLabel(subjects)}: form ranks!");
        }
        if (CardButton("##card-circle", ConsoleIconCircle,
                "Circle: evenly spaced ring, everyone facing outward", any) &&
            _net.SuiOrder(SuiOrderFormationCircle, subjects, 0, 0, 0, 0))
        {
            NoteCompanionOrder(SuiOrderFormationCircle, subjects);
            AddChatMessage($"{OrderSubjectLabel(subjects)}: form a circle!");
        }
        if (CardButton("##card-sheathe", _rtsWeaponsSheathed ? ConsoleIconDraw : ConsoleIconSheathe,
                (_rtsWeaponsSheathed ? "Draw: weapons out" : "Sheathe: weapons away") +
                " — parade discipline.\nEntering combat always draws steel.", any))
        {
            bool draw = _rtsWeaponsSheathed;
            if (_net.SuiOrder(SuiOrderSheath, subjects, 0, draw ? 1f : 0f, 0, 0))
            {
                _rtsWeaponsSheathed = !draw;
                NoteCompanionOrder(SuiOrderSheath, subjects);
                AddChatMessage($"{OrderSubjectLabel(subjects)}: " +
                    (draw ? "weapons out!" : "weapons away."));
            }
        }
    }

    private static uint ClassChipColor(byte classId) => classId switch
    {
        1 => 0xff6e9cc7,   // warrior tan
        2 => 0xffba8cf5,   // paladin pink
        3 => 0xff73d4ab,   // hunter green
        4 => 0xff69f5ff,   // rogue yellow
        5 => 0xffffffff,   // priest white
        7 => 0xffde7000,   // shaman blue
        8 => 0xfff0cc69,   // mage light blue
        9 => 0xffc98294,   // warlock purple
        11 => 0xff0a7dff,  // druid orange
        _ => 0xff9aa4ab,
    };

    /// <summary>
    /// The WC3 multi-unit panel: one chip per selected companion — its baked
    /// portrait when the party frames have one (class color otherwise), name
    /// initial, live health bar. Click takes the chip as the sole selection;
    /// Shift+click drops it from the set.
    /// </summary>
    private void DrawRtsSelectionChips(float scale)
    {
        const int maxChips = 16;
        var chipSize = new Vector2(18f, 24f) * scale;
        int shown = 0;
        ulong soloPick = 0, dropPick = 0;
        for (int i = 0; i < _freecamSelection.Count && shown < maxChips; i++)
        {
            ulong guid = _freecamSelection[i];
            if (!_entities.TryGet(guid, out WorldEntity unit)) continue;
            shown++;
            if (shown > 1) ImGui.SameLine(0f, 3f * scale);
            Vector2 min = ImGui.GetCursorScreenPos();
            Vector2 max = min + chipSize;
            ImGui.InvisibleButton($"##sel-chip-{i}", chipSize);
            ImDrawListPtr dl = ImGui.GetWindowDrawList();
            (_, byte classId, _, _) = unit.Fields.Bytes0;
            float barH = 3f * scale;
            var bodyMax = new Vector2(max.X, max.Y - barH);
            uint baked = PartyPortraitHandle(guid);
            if (baked != 0 && !unit.IsDead)
                // Live bake, V flipped (render target, not a BLP).
                dl.AddImage((nint)baked, min, bodyMax, new Vector2(0, 1), new Vector2(1, 0));
            else
            {
                dl.AddRectFilled(min, bodyMax, unit.IsDead ? 0xff40444a : ClassChipColor(classId));
                string initial = ResolveUnitName(guid) is { Length: > 0 } name
                    ? name[..1].ToUpperInvariant() : "?";
                Vector2 half = ImGui.CalcTextSize(initial) * 0.5f;
                Vector2 center = new((min.X + max.X) * 0.5f, (min.Y + max.Y - barH) * 0.5f);
                dl.AddText(center - half, 0xe0101418, initial);
            }
            uint maxHp = unit.Fields.MaxHealth;
            float hp = maxHp > 0 ? Math.Clamp(unit.Fields.Health / (float)maxHp, 0f, 1f) : 0f;
            dl.AddRectFilled(new Vector2(min.X, max.Y - barH), max, 0xff101418);
            dl.AddRectFilled(new Vector2(min.X, max.Y - barH),
                new Vector2(min.X + chipSize.X * hp, max.Y),
                hp > 0.5f ? 0xff40c040u : hp > 0.2f ? 0xff40c0e0u : 0xff4040d0u);
            dl.AddRect(min, max, ImGui.IsItemHovered() ? 0xffd0b060 : 0xff2a343d,
                0, ImDrawFlags.None, MathF.Max(1f, scale));
            if (ImGui.IsItemHovered())
                HoverTip($"{ResolveUnitName(guid)} — {(int)(hp * 100)}%\n" +
                    "Click: select only this one · Shift+click: drop from selection");
            if (ImGui.IsItemClicked())
            {
                if (ImGui.GetIO().KeyShift) dropPick = guid;
                else soloPick = guid;
            }
        }
        if (_freecamSelection.Count > maxChips)
        {
            ImGui.SameLine(0f, 3f * scale);
            ImGui.TextDisabled($"+{_freecamSelection.Count - maxChips}");
        }
        // Mutations after the loop — never mutate the list mid-iteration.
        if (soloPick != 0)
        {
            _freecamSelection.Clear();
            _freecamSelection.Add(soloPick);
            PlayCompanionSelectionVoice(soloPick);
        }
        else if (dropPick != 0)
            _freecamSelection.Remove(dropPick);
    }
}

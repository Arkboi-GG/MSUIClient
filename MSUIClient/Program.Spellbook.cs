using System.Numerics;
using ImGuiNET;
using Silk.NET.Input;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private bool _spellbookOpen;
    private bool _spellbookKeyWasDown;
    private uint _spellbookLine;
    private int _spellbookPage;
    private uint _pressedSpellId;
    private uint _draggingSpellId;
    private Vector2 _spellPressPosition;
    private PreparedSharedSpellTooltip? _hoveredSpellTooltip;
    private bool _spellbookFontCalibrationOpen = true;
    private bool _spellbookFontCalibrationKeyDown;
    private bool _spellbookFontPixelSnap = true;
    // Diagnostic multiplier on the gameplay text scale, F6 panel only. 1.0 is the derived law
    // (GameTextLaw: em = round(FontHeight x uiScale), draw size from the TTF's own metrics,
    // floor(advance)+1 tracking). The old empirical 1.25 was hand-approximating the measured
    // 1.215 em factor and is gone; nudge this only to A/B against a same-resolution capture.
    private float _spellbookFontDiagnosticScale = 1f;

    private enum SpellTooltipPlacement { OwnerRight, DefaultBottomRight }
    private readonly record struct SpellTooltipRenderSnapshot(
        SpellTooltipView View,
        WowSkin Skin,
        float Scale,
        Vector2 DisplaySize,
        SpellTooltipPlacement Placement,
        Vector2 OwnerMin,
        Vector2 OwnerMax);
    private readonly record struct PreparedSharedSpellTooltip(
        GameTooltipOwnerKey Owner,
        SpellTooltipRenderSnapshot Snapshot);
    private readonly record struct TooltipPaintRow(string Left, string? Right, string FontObject,
        uint Color, bool GapBefore);

    private bool SetSpellbookOpen(bool open)
    {
        if (_spellbookOpen == open) return false;
        _spellbookOpen = open;
        PlayUiSound(open ? SpellbookLaw.OpenSound : SpellbookLaw.CloseSound, "ui.spellbook");
        return true;
    }

    private void UpdateSpellbookInput(bool typing)
    {
        bool calibration = _window.IsDown(Key.F6) || _liveInputHeld.Contains(Key.F6);
        if (calibration && !_spellbookFontCalibrationKeyDown && !typing && _config.DevTools)
            _spellbookFontCalibrationOpen = !_spellbookFontCalibrationOpen;
        _spellbookFontCalibrationKeyDown = calibration;

        bool down = BindingDown(GameBinding.OpenSpellbook);
        if (down && !_spellbookKeyWasDown && !typing && _net is { IsInWorld: true })
            ToggleSpellbookThroughUiPanel();
        _spellbookKeyWasDown = down;
    }

    private void DrawSpellbook()
    {
        if (!_spellbookOpen || _gameplayArt is null || _spellCatalog is null) return;
        float s = GameplayUiScale();
        Vector2 p = new(0, 104f * s);
        ImGui.SetNextWindowPos(p, ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(416, 512) * s, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        if (!ImGui.Begin("##spellbook", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoNav))
        { ImGui.End(); return; }
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        if (_uiParityArmed && _uiParityPanel == "spellbook")
        {
            BeginUiParityFrame(p, s);
            CollectUiParityDraw("SpellBookFrame", "Frame", p, new Vector2(384,512) * s, "",
                new("", 0, "IMGUI_HOST", "ANCHOR:ABSOLUTE", "", "", p.X/s, p.Y/s));
        }
        DrawSpellbookArt(dl, p, s);

        var identity = _net is not null && _entities.TryGet(_net.PlayerGuid, out WorldEntity playerEntity)
            ? playerEntity.Fields.Bytes0 : default;
        var known = _actions.KnownSpells
            .Select(id => _spellCatalog.TryGet(id, out SpellInfo spell) ? (Id: id, Spell: spell) : default)
            .Where(x => x.Id != 0 && SpellbookLaw.Eligible(x.Spell))
            .ToList();
        var tabs = known.GroupBy(x => _skillLines?.SpellTab(x.Id, identity.Race, identity.Class) ?? 0)
            .Select(g => (Id: g.Key,
                Name: g.Key != 0 && _skillLines?.TryGet(g.Key, out SkillLineInfo line) == true
                    ? line.Name : SpellbookLaw.GeneralName,
                Icon: g.Key != 0 && _skillLines?.TryGet(g.Key, out SkillLineInfo iconLine) == true
                    ? iconLine.IconPath : SpellbookLaw.GeneralIcon,
                Spells: g.OrderBy(x => x.Spell.Name)
                    .ThenBy(x => SpellbookLaw.LeadingRankNumber(x.Spell.Rank))
                    .ThenBy(x => x.Spell.Rank).ToList()))
            .OrderBy(x => x.Id == 0 ? 0 : 1).ThenBy(x => x.Name)
            .Take(SpellbookLaw.MaxClassTabs).ToList();
        if (tabs.Count > 0 && tabs.All(t => t.Id != _spellbookLine)) { _spellbookLine = tabs[0].Id; _spellbookPage = 0; }
        var active = tabs.FirstOrDefault(t => t.Id == _spellbookLine);
        int pages = Math.Max(1, ((active.Spells?.Count ?? 0) + 11) / 12);
        _spellbookPage = Math.Clamp(_spellbookPage, 0, pages - 1);

        _hoveredSpellTooltip = null;
        for (int i = 0; i < 12; i++)
        {
            int column = i / 6, row = i % 6;
            Vector2 min = p + new Vector2(34 + column * 157, 85 + row * 51) * s;
            int index = _spellbookPage * 12 + i;
            if (active.Spells is null || index >= active.Spells.Count) continue;
            var entry = active.Spells[index];
            DrawSpellButton(dl, min, s, i + 1, entry.Id, entry.Spell);
        }
        for (int i = 0; i < tabs.Count; i++)
            DrawSpellTab(dl, p + new Vector2(352, 65 + i * 49) * s, s, i + 1,
                tabs[i].Id, tabs[i].Name, tabs[i].Icon);

        GameText.DrawCentered(dl, "GameFontNormal", $"Page {_spellbookPage + 1}",
            p + new Vector2(178, 416) * s, s);
        DrawPageButton(dl, p + new Vector2(34, 391) * s, true, s, _spellbookPage > 0);
        DrawPageButton(dl, p + new Vector2(298, 391) * s, false, s, _spellbookPage + 1 < pages);

        Vector2 close = p + new Vector2(324, 9) * s;
        DrawImageButton(dl, "##spell-close", close, new Vector2(32) * s,
            @"Interface\Buttons\UI-Panel-MinimizeButton-Up", @"Interface\Buttons\UI-Panel-MinimizeButton-Down",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Highlight");
        if (ImGui.IsItemClicked()) SetSpellbookOpen(false);
        if (_uiParityArmed && _uiParityPanel == "spellbook") MarkUiParityFrameComplete();
        ImGui.End();

        if (_hoveredSpellTooltip is { } hoveredSpellTooltip)
        {
            PreparedSharedSpellTooltip prepared = hoveredSpellTooltip;
            OfferPreservedSharedGameTooltipRenderer(prepared.Owner,
                () => DrawSpellTooltip(prepared.Snapshot));
        }
        if (_config.DevTools && _spellbookFontCalibrationOpen)
            DrawSpellbookFontCalibration(p, s);

        if (_pressedSpellId != 0 && ImGui.IsMouseDown(ImGuiMouseButton.Left) &&
            Vector2.Distance(ImGui.GetIO().MousePos, _spellPressPosition) > 6f * s)
            _draggingSpellId = _pressedSpellId;
        if (_draggingSpellId != 0 && _spellCatalog.TryGet(_draggingSpellId, out SpellInfo dragged))
        {
            WorldEntity? player = _net is not null && _entities.TryGet(_net.PlayerGuid,
                out WorldEntity owner) ? owner : null;
            uint icon = _gameplayArt.Handle(ResolveSpellActionIcon(dragged, player));
            if (icon != 0)
            {
                Vector2 min = ImGui.GetIO().MousePos + new Vector2(10) * s;
                ImGui.GetForegroundDrawList().AddImage((nint)icon, min, min + new Vector2(32) * s,
                    Vector2.Zero, Vector2.One, 0xccffffff);
            }
        }
        if (ImGui.IsMouseReleased(ImGuiMouseButton.Left)) _pressedSpellId = 0;
    }

    private bool IsProfessionRecipeSpell(uint id, in SpellInfo spell)
    {
        if (_skillLines?.TryGetRecipe(id, out _) != true) return false;
        return spell.EffectIds?.Any(effect => effect is 24 or 53) == true;
    }

    // These server-applied state auras can be present in an administrative test
    // character's learned set, but the 1.12 client never offers them as actions.
    private static bool IsClientStatusAura(uint id) => id is 2479 or 15007 or 26013;

    private void DrawSpellbookArt(ImDrawListPtr dl, Vector2 p, float s)
    {
        (string Element,string Path,Vector2 Offset,Vector2 Size)[] regions =
        [
            // Spellbook-Icon is BACKGROUND in FrameXML. The four panel quadrants are ARTWORK and
            // must be painted afterward so their circular bezel masks/seats the icon correctly.
            ("SpellBookFrame/Texture", @"Interface\Spellbook\Spellbook-Icon", new(10,8), new(58,58)),
            ("SpellBookFrame/Texture#2", @"Interface\Spellbook\UI-SpellbookPanel-TopLeft", Vector2.Zero, new(256,256)),
            ("SpellBookFrame/Texture#3", @"Interface\Spellbook\UI-SpellbookPanel-TopRight", new(256,0), new(128,256)),
            ("SpellBookFrame/Texture#4", @"Interface\Spellbook\UI-SpellbookPanel-BotLeft", new(0,256), new(256,256)),
            ("SpellBookFrame/Texture#5", @"Interface\Spellbook\UI-SpellbookPanel-BotRight", new(256,256), new(128,256)),
        ];
        foreach(var region in regions)
        {
            Vector2 min=p+region.Offset*s;
            DrawArt(dl,region.Path,min,region.Size,s);
            if(_uiParityArmed&&_uiParityPanel=="spellbook")
                CollectUiParityDraw(region.Element,"Texture",min,region.Size*s,"SpellBookFrame",
                    new(region.Path,0xffffffff,"IMGUI_IMAGE","TOPLEFT","SpellBookFrame","TOPLEFT",region.Offset.X,-region.Offset.Y));
        }
        // SpellBookTitleText inherits GameFontNormal (MPQ SpellBookFrame.xml l.264); its
        // CENTER (6,230) anchor is this (198,26) point. Color/height/shadow ride the registry.
        GameText.DrawCentered(dl, "GameFontNormal", "Spellbook", p + new Vector2(198, 26) * s, s);
    }

    private void DrawSpellButton(ImDrawListPtr dl, Vector2 min, float s, int buttonOrdinal,
        uint id, SpellInfo spell)
    {
        static Vector2 Snap(Vector2 value) => new(MathF.Round(value.X), MathF.Round(value.Y));
        Vector2 iconMin = Snap(min), max = Snap(min + new Vector2(37) * s);
        uint bg = _gameplayArt!.Handle(@"Interface\Spellbook\UI-Spellbook-SpellBackground");
        // FrameXML TOPLEFT(-3,+3) is y-up; ImGui is y-down. The 64x64 backplate therefore spans
        // screen offsets (-3,-3)..(+61,+61), centered around the authored 37x37 icon button.
        if (bg != 0) dl.AddImage((nint)bg, Snap(min + new Vector2(-3, -3) * s),
            Snap(min + new Vector2(61, 61) * s));
        WorldEntity? player = _net is not null && _entities.TryGet(_net.PlayerGuid,
            out WorldEntity owner) ? owner : null;
        uint icon = _gameplayArt.Handle(ResolveSpellActionIcon(spell, player));
        if (icon != 0) dl.AddImage((nint)icon, iconMin, max);
        uint ring = _gameplayArt.Handle(@"Interface\Buttons\UI-Quickslot2");
        if (ring != 0)
        {
            Vector2 center = (iconMin + max) * .5f, half = new(32f * s);
            // FrameXML grays passive spells by blackening the NormalTexture ring. The spell icon
            // itself remains full white; tinting the icon is a visually similar but wrong shortcut.
            dl.AddImage((nint)ring, Snap(center - half), Snap(center + half),
                Vector2.Zero, Vector2.One, spell.Passive ? 0xff000000 : 0xffffffff);
        }
        // SpellName inherits GameFontNormal; passive names are the Lua SetTextColor override.
        uint? nameColor = spell.Passive ? SpellbookLaw.PassiveNameColor : null;
        bool hasRank = !string.IsNullOrWhiteSpace(spell.Rank);
        // fs is the text scale (diagnostic multiplier rides only the glyphs); s alone places
        // the FrameXML anchor boxes. Line pitch and block height are DEVICE pixels from the em
        // law - the client's lineStep is the em itself, not an ascent+descent line height.
        float fs = s * _spellbookFontDiagnosticScale;
        float namePitch = GameText.LinePitch("GameFontNormal", fs);
        List<string> nameLines = WrapSpellbookName(spell.Name, "GameFontNormal", fs,
            SpellbookLaw.NameWidth * s, SpellbookLaw.NameMaxLines);
        float nameBlockHeight = nameLines.Count * namePitch;
        float nameAnchorY = hasRank ? SpellbookLaw.NameAnchorYWithRank
            : SpellbookLaw.NameAnchorYWithoutRank;
        // SpellName's LEFT anchor is the vertical center of its auto-height, wrapped block.
        Vector2 namePos = new(
            min.X + (SpellbookLaw.ButtonSize + SpellbookLaw.NameAnchorX) * s,
            min.Y + SpellbookLaw.ButtonSize * .5f * s - nameAnchorY * s - nameBlockHeight * .5f);
        if (_spellbookFontPixelSnap) namePos = Snap(namePos);
        for (int line = 0; line < nameLines.Count; line++)
            GameText.Draw(dl, "GameFontNormal", nameLines[line],
                namePos + new Vector2(0, line * namePitch), fs, nameColor,
                snap: _spellbookFontPixelSnap);
        if (hasRank)
        {
            // SubSpellName is a FIXED 79x18 box anchored TOPLEFT to the name's BOTTOMLEFT at
            // (0,+4) - and FontStrings default to justifyV MIDDLE, so the 10px text floats
            // vertically centered inside those 18 units. That centering slack is the visible
            // air between name and rank in 1.12; drawing the ink at the box top (the previous
            // conversion) is what made them touch.
            float rankEm = GameText.EmPixels("SubSpellFont", fs);
            Vector2 rankPos = namePos + new Vector2(0,
                nameBlockHeight - SpellbookLaw.RankAnchorY * s +
                (SpellbookLaw.RankBoxHeight * s - rankEm) * .5f);
            GameText.Draw(dl, "SubSpellFont", spell.Rank, rankPos, fs,
                snap: _spellbookFontPixelSnap);
        }
        ImGui.SetCursorScreenPos(min);
        bool clicked = ImGui.InvisibleButton($"##spell-{id}", new Vector2(145, 37) * s);
        if (ImGui.IsItemActivated()) { _pressedSpellId = id; _spellPressPosition = ImGui.GetIO().MousePos; }
        if (clicked && _draggingSpellId == 0 && !TryOpenProfession(id)) TryCast(id);
        if (ImGui.IsItemActive())
        {
            uint depress = _gameplayArt.Handle(@"Interface\Buttons\UI-Quickslot-Depress");
            if (depress != 0) dl.AddImage((nint)depress, iconMin, max);
        }
        if (ImGui.IsItemHovered())
        {
            uint highlight = _gameplayArt.BrightHighlightHandle(spell.Passive
                ? @"Interface\Buttons\UI-PassiveHighlight"
                : @"Interface\Buttons\ButtonHilight-Square");
            if (highlight != 0) dl.AddImage((nint)highlight, iconMin, max);
            _hoveredSpellTooltip = PrepareSharedSpellTooltip(
                new GameTooltipOwnerKey("spellbook-button", (ulong)buttonOrdinal),
                id, s, SpellTooltipPlacement.OwnerRight, min, max);
        }
    }

    private static List<string> WrapSpellbookName(string text, string fontObject, float textScale,
        float maxWidthPx, int maxLines)
    {
        string[] words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return [""];
        List<string> lines = [];
        string current = "";
        for (int i = 0; i < words.Length; i++)
        {
            string candidate = current.Length == 0 ? words[i] : current + " " + words[i];
            if (current.Length > 0 &&
                GameText.MeasureWidth(fontObject, candidate, textScale) > maxWidthPx)
            {
                lines.Add(current);
                if (lines.Count == maxLines - 1)
                {
                    current = string.Join(' ', words[i..]);
                    break;
                }
                current = words[i];
            }
            else current = candidate;
        }
        if (current.Length > 0 && lines.Count < maxLines) lines.Add(current);
        return lines;
    }

    private void DrawSpellbookFontCalibration(Vector2 spellbookOrigin, float s)
    {
        ImGui.SetNextWindowPos(spellbookOrigin + new Vector2(420, 25) * s, ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(430, 0), ImGuiCond.FirstUseEver);
        bool open = _spellbookFontCalibrationOpen;
        if (!ImGui.Begin("Spellbook Font Calibration", ref open,
                ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse))
        {
            _spellbookFontCalibrationOpen = open;
            ImGui.End();
            return;
        }
        _spellbookFontCalibrationOpen = open;
        ImGui.TextWrapped("Text now renders through the derived 1.12 law (GameTextLaw), not a " +
            "hand-tuned conversion: em = round(FontHeight x uiScale) device px; ImGui draw size " +
            "= em x the TTF's own (ascent-descent)/unitsPerEm; per-glyph advance = " +
            "floor(advance)+1 px (0x5d1120); line pitch = em (0x5cdc20).");
        ImGui.TextDisabled("F6 hides/shows this panel");

        float fs = s * _spellbookFontDiagnosticScale;
        ImGui.Text($"Gameplay scale {s:F4}  |  FRIZQT em factor " +
            $"{GameTextLaw.FaceEmFactor(FontFace.FrizQt):F4}");
        ImGui.Text($"Baked: {GameTextLaw.DescribeBake()}");
        ImGui.Text($"Name em {GameText.EmPixels("GameFontNormal", fs)} px, " +
            $"rank em {GameText.EmPixels("SubSpellFont", fs)} px, " +
            $"tooltip header em {GameText.EmPixels("GameTooltipHeaderText", s)} px");
        if (!GameTextLaw.Ready)
            ImGui.TextColored(new Vector4(1f, .4f, .3f, 1f),
                "Exact-size fonts NOT baked - drawing via the legacy supersampled atlas.");

        bool advanceLaw = GameTextLaw.AdvanceLawEnabled;
        if (ImGui.Checkbox("Client advance law: floor(advance)+1 px per glyph", ref advanceLaw))
            GameTextLaw.SetAdvanceLaw(advanceLaw);
        ImGui.SliderFloat("Diagnostic scale (1.0 = derived law)",
            ref _spellbookFontDiagnosticScale, 0.90f, 1.15f, "%.3fx");
        if (MathF.Abs(_spellbookFontDiagnosticScale - 1f) > 0.001f)
            ImGui.TextColored(new Vector4(1f, .8f, .2f, 1f),
                "Off-law scale: off-atlas em sizes rescale the nearest bake and go soft.");
        ImGui.Checkbox("Snap text and shadow to whole pixels", ref _spellbookFontPixelSnap);

        ImGui.SeparatorText("Same-resolution pixel targets");
        ImGui.Text("Attack foreground:       58 x 13 px");
        ImGui.Text("Diplomacy foreground:    97 x 17 px");
        ImGui.Text("Racial Passive foreground: 108 x 11 px");
        ImGui.Text("Name-to-subtext visible gap: about 3 px");
        ImGui.TextWrapped("Capture the General page without a tooltip covering these rows. " +
            "Remaining known divergence at 1.0: stb rasterization is unhinted where the client " +
            "hints - stems can read a hair slimmer. Report that separately from any size or " +
            "spacing miss.");
        ImGui.End();
    }

    private void DrawSpellTab(ImDrawListPtr dl, Vector2 min, float s, int tabOrdinal,
        uint id, string name, string iconPath)
    {
        static Vector2 Snap(Vector2 value) => new(MathF.Round(value.X), MathF.Round(value.Y));
        uint back = _gameplayArt!.Handle(@"Interface\SpellBook\SpellBook-SkillLineTab");
        // FrameXML TOPLEFT(-3,+11) converts to screen (-3,-11); retain the authored 64x64 size.
        if (back != 0) dl.AddImage((nint)back, Snap(min + new Vector2(-3, -11) * s),
            Snap(min + new Vector2(61, 53) * s));
        uint icon = _gameplayArt.Handle(iconPath);
        // Skill-line icons stay full-bright in 1.12. Selection is conveyed solely by the
        // CheckButtonHilight overlay; dimming inactive tabs makes the whole right rail look disabled.
        if (icon != 0) dl.AddImage((nint)icon, Snap(min), Snap(min + new Vector2(32) * s));
        ImGui.SetCursorScreenPos(min);
        ImGui.InvisibleButton($"##spell-tab-{id}", new Vector2(32) * s);
        if (ImGui.IsItemClicked()) { _spellbookLine = id; _spellbookPage = 0; }
        if (id == _spellbookLine)
        {
            uint checkedArt = _gameplayArt.AdditiveHandle(@"Interface\Buttons\CheckButtonHilight");
            if (checkedArt != 0) dl.AddImage((nint)checkedArt, Snap(min), Snap(min + new Vector2(32) * s));
        }
        else if (ImGui.IsItemHovered())
        {
            uint highlight = _gameplayArt.BrightHighlightHandle(@"Interface\Buttons\ButtonHilight-Square");
            if (highlight != 0) dl.AddImage((nint)highlight, Snap(min), Snap(min + new Vector2(32) * s));
        }
        if (ImGui.IsItemHovered())
        {
            string preparedName = name;
            var owner = new GameTooltipOwnerKey("spellbook-tab", (ulong)tabOrdinal);
            OfferPreservedSharedGameTooltipRenderer(owner, () =>
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted(preparedName);
                ImGui.EndTooltip();
            });
        }
    }

    private PreparedSharedSpellTooltip? PrepareSharedSpellTooltip(
        in GameTooltipOwnerKey owner,
        uint spellId,
        float scale,
        SpellTooltipPlacement placement,
        Vector2 ownerMin = default,
        Vector2 ownerMax = default)
    {
        if (_spellCatalog is null || _skin is not { } skin ||
            !_spellCatalog.TryGet(spellId, out SpellInfo spell)) return null;
        uint casterLevel = _net is not null && _entities.TryGet(_net.PlayerGuid, out WorldEntity player)
            ? player.Level : 0;
        SpellTooltipView view = SpellTooltipLaw.Build(spell, _spellCatalog, casterLevel);
        return new PreparedSharedSpellTooltip(owner,
            new SpellTooltipRenderSnapshot(view, skin, scale, ImGui.GetIO().DisplaySize,
                placement, ownerMin, ownerMax));
    }

    private void DrawSpellTooltip(in SpellTooltipRenderSnapshot snapshot)
    {
        SpellTooltipView view = snapshot.View;
        WowSkin skin = snapshot.Skin;
        float s = snapshot.Scale;
        SpellTooltipPlacement placement = snapshot.Placement;
        Vector2 ownerMin = snapshot.OwnerMin;
        Vector2 ownerMax = snapshot.OwnerMax;
        const uint TooltipGold = 0xff00d2ff; // build-5875 tooltip gold RGB(255,210,0).
        List<TooltipPaintRow> rows = [];
        void AddRow(string left, string? right, string fontObject, uint color) =>
            rows.Add(new TooltipPaintRow(left, right, fontObject, color, rows.Count > 0));

        // SetAction shows rank in the right column of the header. Vanilla SetSpell suppresses it,
        // but this client deliberately keeps the user's preferred rank display in the spellbook
        // while retaining the authentic same-line/right-aligned presentation.
        AddRow(view.Name, string.IsNullOrWhiteSpace(view.Rank) ? null : view.Rank,
            "GameTooltipHeaderText", 0xffffffff);
        if (view.Cost is not null || view.Range is not null)
            AddRow(view.Cost ?? view.Range!, view.Cost is null ? null : view.Range,
                "GameTooltipText", 0xffffffff);
        if (view.CastTime is not null)
            AddRow(view.CastTime, view.Cooldown, "GameTooltipText", 0xffffffff);
        if (!string.IsNullOrWhiteSpace(view.Description))
        {
            bool first = true;
            foreach (string line in WrapTooltipText(view.Description, "GameTooltipText",
                         s, SpellTooltipLaw.WrapWidth * s))
            {
                // A wrapped FontString is one logical tooltip row. Its continuation lines use
                // the font's own line height; TOOLTIP_LINE_GAP applies only before the block.
                rows.Add(new TooltipPaintRow(line, null, "GameTooltipText",
                    TooltipGold, first && rows.Count > 0));
                first = false;
            }
        }

        // Widths and the line stack are DEVICE pixels: text width is the summed glyph advances
        // (the client's own measure), row height is the em (lineStep = em + spacing(0)). Only
        // the pads/gaps are FrameXML units and scale by s.
        float contentWidth = 0f, rowStackHeight = 0f;
        foreach (TooltipPaintRow row in rows)
        {
            float rowWidth = GameText.MeasureWidth(row.FontObject, row.Left, s);
            if (!string.IsNullOrEmpty(row.Right))
                rowWidth += SpellTooltipLaw.DoubleGap * s +
                    GameText.MeasureWidth(row.FontObject, row.Right, s);
            contentWidth = MathF.Max(contentWidth, rowWidth);
            rowStackHeight += GameText.LinePitch(row.FontObject, s);
            if (row.GapBefore) rowStackHeight += SpellTooltipLaw.LineGap * s;
        }
        Vector2 size = new(
            MathF.Round(contentWidth + SpellTooltipLaw.Pad * 2f * s),
            MathF.Round(rowStackHeight + SpellTooltipLaw.Pad * 2f * s));
        Vector2 display = snapshot.DisplaySize;
        Vector2 pos = placement == SpellTooltipPlacement.DefaultBottomRight
            // GameTooltip_SetDefaultAnchor: BOTTOMRIGHT of UIParent at
            // (-CONTAINER_OFFSET_X - 13, CONTAINER_OFFSET_Y), defaults 0 and 70.
            ? new Vector2(display.X - 13f * s - size.X, display.Y - 70f * s - size.Y)
            : new Vector2(ownerMax.X + 4f * s, ownerMin.Y);
        if (placement == SpellTooltipPlacement.OwnerRight && pos.X + size.X > display.X - 4f)
            pos.X = ownerMin.X - size.X - 4f * s;
        pos.X = Math.Clamp(pos.X, 4f, Math.Max(4f, display.X - size.X - 4f));
        pos.Y = Math.Clamp(pos.Y, 4f, Math.Max(4f, display.Y - size.Y - 4f));

        ImGui.SetNextWindowPos(pos, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        if (ImGui.Begin("##spell-tooltip", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoInputs))
        {
            ImDrawListPtr dl = ImGui.GetWindowDrawList();
            float savedScale = skin.Scale;
            skin.Scale = s;
            skin.DrawBackdrop(dl, pos, pos + size, WowSkin.Tooltip,
                new Vector4(.09f, .09f, .19f, 1f), Vector4.One);
            skin.Scale = savedScale;
            // GameTooltipHeaderText/GameTooltipText have no MasterFont chain: tooltip text is
            // shadowless in 1.12 (FontObjectLaw carries that; no per-call shadow choices here).
            float y = pos.Y + SpellTooltipLaw.Pad * s;
            for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
            {
                TooltipPaintRow row = rows[rowIndex];
                if (row.GapBefore) y += SpellTooltipLaw.LineGap * s;
                GameText.Draw(dl, row.FontObject, row.Left,
                    new Vector2(pos.X + SpellTooltipLaw.Pad * s, y), s, row.Color);
                if (!string.IsNullOrEmpty(row.Right))
                    GameText.DrawRightAligned(dl, row.FontObject, row.Right,
                        new Vector2(pos.X + size.X - SpellTooltipLaw.Pad * s, y), s,
                        rowIndex == 0 ? SpellTooltipLaw.RankColor : row.Color);
                y += GameText.LinePitch(row.FontObject, s);
            }
        }
        ImGui.End();
        ImGui.PopStyleVar();
    }

    private static IEnumerable<string> WrapTooltipText(string text, string fontObject,
        float textScale, float maxWidthPx)
    {
        foreach (string paragraph in text.Replace("\r", "").Split('\n'))
        {
            if (paragraph.Length == 0) { yield return ""; continue; }
            string current = "";
            foreach (string word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = current.Length == 0 ? word : current + " " + word;
                if (current.Length > 0 &&
                    GameText.MeasureWidth(fontObject, candidate, textScale) > maxWidthPx)
                {
                    yield return current;
                    current = word;
                }
                else current = candidate;
            }
            if (current.Length > 0) yield return current;
        }
    }

    private void DrawPageButton(ImDrawListPtr dl, Vector2 min, bool previous, float s, bool enabled)
    {
        string stem = previous ? "UI-SpellbookIcon-PrevPage" : "UI-SpellbookIcon-NextPage";
        DrawImageButton(dl, previous ? "##spell-prev" : "##spell-next", min, new Vector2(32) * s,
            $@"Interface\Buttons\{stem}-{(enabled ? "Up" : "Disabled")}",
            $@"Interface\Buttons\{stem}-Down", @"Interface\Buttons\UI-Common-MouseHilight");
        if (enabled && ImGui.IsItemClicked())
        {
            _spellbookPage += previous ? -1 : 1;
            PlayUiSound(SpellbookLaw.PageTurnSound, "ui.spellbook");
        }
    }
}

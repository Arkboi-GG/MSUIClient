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
    private bool _spellbookPetBook;
    private bool _spellbookKeyWasDown;
    private bool _petSpellbookKeyWasDown;
    private uint _spellbookLine;
    private int _spellbookPage;
    private uint _pressedSpellId;
    private uint _pressedPetBookWord;
    private uint _draggingSpellId;
    private Vector2 _spellPressPosition;
    private PreparedSharedSpellTooltip? _hoveredSpellTooltip;
    private bool _spellbookFontCalibrationOpen;
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
        string sound = _spellbookPetBook
            ? open ? PetSpellBookUiLaw.OpenSound : PetSpellBookUiLaw.CloseSound
            : open ? SpellbookLaw.OpenSound : SpellbookLaw.CloseSound;
        PlayUiSound(sound, "ui.spellbook");
        return true;
    }

    private bool HasPetBookSpells => _petGuid != 0 && _spellCatalog is not null &&
        _petBookSpells.Any(word => _spellCatalog.TryGet(PetSpellBookUiLaw.SpellId(word),
            out SpellInfo spell) && PetSpellBookUiLaw.Eligible(spell.Attributes));

    private void UpdateSpellbookInput(bool typing)
    {
        bool calibration = _window.IsDown(Key.F6) || _liveInputHeld.Contains(Key.F6);
        if (calibration && !_spellbookFontCalibrationKeyDown && !typing && _config.DevTools)
            _spellbookFontCalibrationOpen = !_spellbookFontCalibrationOpen;
        _spellbookFontCalibrationKeyDown = calibration;

        bool petDown = BindingDown(GameBinding.OpenPetSpellbook);
        if (petDown && !_petSpellbookKeyWasDown && !typing && _net is { IsInWorld: true })
            TogglePetSpellbookThroughUiPanel();
        _petSpellbookKeyWasDown = petDown;

        bool down = BindingDown(GameBinding.OpenSpellbook);
        if (down && !_spellbookKeyWasDown && !typing && _net is { IsInWorld: true })
            ToggleSpellbookThroughUiPanel();
        _spellbookKeyWasDown = down;
    }

    private void DrawSpellbook()
    {
        if (!_spellbookOpen || _gameplayArt is null || _spellCatalog is null) return;
        if (_spellbookPetBook && !HasPetBookSpells)
        {
            SetSpellbookOpen(false);
            _spellbookPetBook = false;
            return;
        }
        float s = GameplayUiScale();
        Vector2 p = UiPanelFrameOrigin(UiPanelOwnershipRegistry[12], s);
        ImGui.SetNextWindowPos(p, ImGuiCond.Always);
        ImGui.SetNextWindowSize(SpellbookLaw.HostSize * s, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        if (!ImGui.Begin("##spellbook", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoNav))
        { ImGui.End(); return; }
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        if (_uiParityArmed && _uiParityPanel == "spellbook")
        {
            BeginUiParityFrame(p, s);
            CollectUiParityDraw("SpellBookFrame", "Frame", p, SpellbookLaw.FrameSize * s, "",
                new("", 0, "IMGUI_HOST", "ANCHOR:ABSOLUTE", "", "", p.X/s, p.Y/s));
        }
        var identity = _net is not null && _entities.TryGet(ControlledGuid, out WorldEntity playerEntity)
            ? playerEntity.Fields.Bytes0 : default;
        string petTitle = PetSpellBookUiLaw.Title(identity.Class);
        DrawSpellbookArt(dl, p, s, _spellbookPetBook ? petTitle : "Spellbook");

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
        var petSpells = _petBookSpells
            .Select(packed => (Packed: packed, Id: PetSpellBookUiLaw.SpellId(packed)))
            .Select(entry => _spellCatalog.TryGet(entry.Id, out SpellInfo spell)
                ? (entry.Packed, entry.Id, Spell: spell) : default)
            .Where(entry => entry.Id != 0 && PetSpellBookUiLaw.Eligible(entry.Spell.Attributes))
            .OrderBy(entry => entry.Spell.Name)
            .ThenBy(entry => SpellbookLaw.LeadingRankNumber(entry.Spell.Rank))
            .ThenBy(entry => entry.Spell.Rank).ToList();
        if (!_spellbookPetBook && tabs.Count > 0 && tabs.All(t => t.Id != _spellbookLine))
        { _spellbookLine = tabs[0].Id; _spellbookPage = 0; }
        var active = tabs.FirstOrDefault(t => t.Id == _spellbookLine);
        int pages = _spellbookPetBook ? 1 :
            Math.Max(1, ((active.Spells?.Count ?? 0) + 11) / 12);
        _spellbookPage = Math.Clamp(_spellbookPage, 0, pages - 1);

        _hoveredSpellTooltip = null;
        for (int i = 0; i < SpellbookLaw.SpellsPerPage; i++)
        {
            Vector2 min = p + SpellbookLaw.SpellButtonSeat(i).Min * s;
            int index = _spellbookPetBook ? i : _spellbookPage * 12 + i;
            if (_spellbookPetBook)
            {
                if (index >= petSpells.Count) continue;
                var entry = petSpells[index];
                DrawSpellButton(dl, min, s, i + 1, entry.Id, entry.Spell,
                    petBook: true, entry.Packed);
            }
            else
            {
                if (active.Spells is null || index >= active.Spells.Count) continue;
                var entry = active.Spells[index];
                DrawSpellButton(dl, min, s, i + 1, entry.Id, entry.Spell);
            }
        }
        if (!_spellbookPetBook)
            for (int i = 0; i < tabs.Count; i++)
                DrawSpellTab(dl, p + SpellbookLaw.SkillLineTabSeat(i).Min * s, s, i + 1,
                    tabs[i].Id, tabs[i].Name, tabs[i].Icon);

        GameText.DrawCentered(dl, "GameFontNormal", $"Page {_spellbookPage + 1}",
            p + SpellbookLaw.PageTextCenter * s, s);
        DrawPageButton(dl, p + SpellbookLaw.PreviousPageButton.Min * s, true, s,
            _spellbookPage > 0);
        DrawPageButton(dl, p + SpellbookLaw.NextPageButton.Min * s, false, s,
            _spellbookPage + 1 < pages);

        if (petSpells.Count > 0)
        {
            DrawSpellbookTypeTab(dl, p, s, petBook: false, "Spellbook");
            DrawSpellbookTypeTab(dl, p, s, petBook: true, petTitle);
        }

        Vector2 close = p + SpellbookLaw.CloseButton.Min * s;
        DrawImageButton(dl, "##spell-close", close, SpellbookLaw.CloseButton.Size * s,
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
        // Diagnostic pop-outs belong to the F1 master stack too: opening the
        // ordinary spellbook must never manufacture a debug window at startup.
        if (_config.DevTools && _devOverlayVisible && _spellbookFontCalibrationOpen)
            DrawSpellbookFontCalibration(p, s);

        if (_pressedSpellId != 0 && ImGui.IsMouseDown(ImGuiMouseButton.Left) &&
            Vector2.Distance(ImGui.GetIO().MousePos, _spellPressPosition) > 6f * s)
            _draggingSpellId = _pressedSpellId;
        if (_pressedPetBookWord != 0 && ImGui.IsMouseDown(ImGuiMouseButton.Left) &&
            Vector2.Distance(ImGui.GetIO().MousePos, _spellPressPosition) > 6f * s &&
            _spellCatalog.TryGet(PetSpellBookUiLaw.SpellId(_pressedPetBookWord),
                out SpellInfo pressedPetSpell))
        {
            PickupPetBookSpell(_pressedPetBookWord, pressedPetSpell);
            _pressedPetBookWord = 0;
        }
        if (_draggingSpellId != 0 && _spellCatalog.TryGet(_draggingSpellId, out SpellInfo dragged))
        {
            WorldEntity? player = _net is not null && _entities.TryGet(ControlledGuid,
                out WorldEntity owner) ? owner : null;
            uint icon = _gameplayArt.Handle(ResolveSpellActionIcon(dragged, player));
            if (icon != 0)
            {
                Vector2 mouse = ImGui.GetIO().MousePos;
                Vector2 min = SpellbookLaw.DragPreviewMin(mouse, s);
                ImGui.GetForegroundDrawList().AddImage((nint)icon, min,
                    SpellbookLaw.DragPreviewMax(mouse, s),
                    Vector2.Zero, Vector2.One, 0xccffffff);
            }
        }
        if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
        { _pressedSpellId = 0; _pressedPetBookWord = 0; }
    }

    private bool IsProfessionRecipeSpell(uint id, in SpellInfo spell)
    {
        if (_skillLines?.TryGetRecipe(id, out _) != true) return false;
        return spell.EffectIds?.Any(effect => effect is 24 or 53) == true;
    }

    // These server-applied state auras can be present in an administrative test
    // character's learned set, but the 1.12 client never offers them as actions.
    private static bool IsClientStatusAura(uint id) => id is 2479 or 15007 or 26013;

    private void PickupSpellToCursor(uint spellId)
    {
        _actionCursor = null;
        _draggingMacroId = 0;
        ClearPetActionCursor();
        _draggingSpellId = spellId;
        _pressedActionSlot = -1;
    }

    private void PickupPetBookSpell(uint packed, in SpellInfo spell)
    {
        _actionCursor = null;
        _draggingMacroId = 0;
        _draggingSpellId = 0;
        _draggingPetAction = packed;
        _draggingPetActionPassive = spell.Passive;
        _draggingPetActionIcon = spell.IconPath;
        _pressedActionSlot = -1;
    }

    private void CastPetBookSpell(uint packed, in SpellInfo spell, WorldEntity? pet)
    {
        if (!CanAuthorControlledGameplay || _petGuid == 0 || spell.Passive) return;
        if (IsPetSpellShowingActive(packed, spell, pet))
        {
            _net?.PetCancelAura(_petGuid, spell.Id);
            return;
        }
        _net?.PetAction(_petGuid, PetSpellBookUiLaw.CastWord(spell.Id),
            PetActionBarUiLaw.ActionTarget(_selectionGuid));
    }

    private void TogglePetBookAutocast(uint spellId)
    {
        if (!CanAuthorControlledGameplay || _petGuid == 0 || !PetSpellBookUiLaw.TryToggleAutocast(
                _petBookSpells, _petActions, spellId, out bool enabled)) return;
        _net?.PetSpellAutocast(_petGuid, spellId, enabled);
    }

    /// <summary>
    /// Frozen MacroFrame_AddMacroLine branch: while the macro body editor is visible a shifted
    /// book click appends the complete /cast token with no inserted separator. Passive spells
    /// consume the gesture but append nothing.
    /// </summary>
    private bool TryAppendSpellToOpenMacro(in SpellInfo spell)
    {
        if (!_macroOpen) return false;
        EnsureMacrosLoaded();
        if (SpellbookLaw.MacroCastLine(spell) is string line) _macroBody += line;
        return true;
    }

    private void DrawSpellbookArt(ImDrawListPtr dl, Vector2 p, float s, string title)
    {
        // Spellbook-Icon is BACKGROUND. The four panel quadrants follow at ARTWORK so their
        // circular bezel masks/seats it; SpellbookLaw retains that authored order.
        for (int index = 0; index < SpellbookLaw.PanelArt.Length; index++)
        {
            SpellbookArtSeat region = SpellbookLaw.PanelArt[index];
            Vector2 min = p + region.Rect.Min * s;
            DrawArt(dl, region.Path, min, region.Rect.Size, s);
            if(_uiParityArmed&&_uiParityPanel=="spellbook")
                CollectUiParityDraw(index == 0 ? "SpellBookFrame/Texture" :
                        $"SpellBookFrame/Texture#{index + 1}", "Texture", min,
                    region.Rect.Size * s, "SpellBookFrame",
                    new(region.Path,0xffffffff,"IMGUI_IMAGE","TOPLEFT","SpellBookFrame","TOPLEFT",
                        region.Rect.X,-region.Rect.Y));
        }
        // SpellBookTitleText inherits GameFontNormal (MPQ SpellBookFrame.xml l.264); its
        // CENTER (6,230) anchor is this (198,26) point. Color/height/shadow ride the registry.
        GameText.DrawCentered(dl, "GameFontNormal", title, p + SpellbookLaw.TitleCenter * s, s);
    }

    private void DrawSpellButton(ImDrawListPtr dl, Vector2 min, float s, int buttonOrdinal,
        uint id, SpellInfo spell, bool petBook = false, uint petPacked = 0)
    {
        static Vector2 Snap(Vector2 value) => new(MathF.Round(value.X), MathF.Round(value.Y));
        Vector2 iconMin = Snap(min), max = Snap(min + SpellbookLaw.ButtonScaledSize(s));
        uint bg = _gameplayArt!.Handle(@"Interface\Spellbook\UI-Spellbook-SpellBackground");
        // FrameXML TOPLEFT(-3,+3) is y-up; ImGui is y-down. The 64x64 backplate therefore spans
        // screen offsets (-3,-3)..(+61,+61), centered around the authored 37x37 icon button.
        if (bg != 0) dl.AddImage((nint)bg,
            Snap(min + SpellbookLaw.SpellButtonBackground.Min * s),
            Snap(min + (SpellbookLaw.SpellButtonBackground.Min +
                SpellbookLaw.SpellButtonBackground.Size) * s));
        WorldEntity? player = _net is not null && _entities.TryGet(ControlledGuid,
            out WorldEntity owner) ? owner : null;
        WorldEntity? pet = _entities.TryGet(_petGuid, out WorldEntity petEntity) && petEntity.IsUnit
            ? petEntity : null;
        string iconPath = petBook && IsPetSpellShowingActive(petPacked, spell, pet)
            ? spell.ActiveIconPath : ResolveSpellActionIcon(spell, player);
        uint icon = _gameplayArt.Handle(iconPath);
        if (icon != 0) dl.AddImage((nint)icon, iconMin, max);
        uint ring = _gameplayArt.Handle(@"Interface\Buttons\UI-Quickslot2");
        if (ring != 0)
        {
            // FrameXML grays passive spells by blackening the NormalTexture ring. The spell icon
            // itself remains full white; tinting the icon is a visually similar but wrong shortcut.
            dl.AddImage((nint)ring,
                Snap(min + SpellbookLaw.SpellButtonNormalRing.Min * s),
                Snap(min + (SpellbookLaw.SpellButtonNormalRing.Min +
                    SpellbookLaw.SpellButtonNormalRing.Size) * s),
                Vector2.Zero, Vector2.One, spell.Passive ? 0xff000000 : 0xffffffff);
        }
        bool professionCurrent = !petBook && _professionOpen && _professionOpenerSpell == id;
        bool checkedState = petBook
            ? IsPetSpellShowingActive(petPacked, spell, pet)
            : SpellbookLaw.Checked(spell, player?.Fields.ShapeshiftForm ?? 0, professionCurrent);
        if (checkedState)
        {
            uint checkedArt = _gameplayArt.AdditiveHandle(SpellbookLaw.CheckedTexture);
            if (checkedArt != 0) dl.AddImage((nint)checkedArt, iconMin, max);
        }
        PlayerActions cooldownStore = petBook ? _petCooldowns : _actions;
        if (cooldownStore.TryCooldownDisplay(id, 0, spell, NowSeconds(),
                out CooldownDisplay cooldown))
        {
            Vector2 cooldownMin = Snap(SpellbookLaw.CooldownMin(min, s));
            Vector2 cooldownMax = Snap(SpellbookLaw.CooldownMax(min, s));
            if (cooldown.SweepFraction is float sweep)
                DrawCooldownSwipe(dl, cooldownMin, cooldownMax, sweep);
            if (cooldown.FlashProgress is float flash)
                DrawCooldownFlash(dl, cooldownMin, cooldownMax, flash);
        }
        bool autocastable = petBook && PetSpellBookUiLaw.AutocastAllowed(petPacked);
        if (autocastable)
        {
            uint overlay = _gameplayArt.Handle(@"Interface\Buttons\UI-AutoCastableOverlay");
            Vector2 overlayMin = Snap(min + SpellbookLaw.SpellButtonAutocastOverlay.Min * s);
            Vector2 overlayMax = Snap(min + (SpellbookLaw.SpellButtonAutocastOverlay.Min +
                SpellbookLaw.SpellButtonAutocastOverlay.Size) * s);
            if (overlay != 0) dl.AddImage((nint)overlay, overlayMin, overlayMax);
            if (PetSpellBookUiLaw.AutocastEnabled(petPacked))
                DrawSpellbookAutocastSparkles(dl, iconMin, s, NowSeconds());
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
        Vector2 namePos = SpellbookLaw.SpellNamePosition(min, s, nameAnchorY, nameBlockHeight);
        if (_spellbookFontPixelSnap) namePos = Snap(namePos);
        for (int line = 0; line < nameLines.Count; line++)
            GameText.Draw(dl, "GameFontNormal", nameLines[line],
                SpellbookLaw.SpellNameLinePosition(namePos, line, namePitch), fs, nameColor,
                snap: _spellbookFontPixelSnap);
        if (hasRank)
        {
            // SubSpellName is a FIXED 79x18 box anchored TOPLEFT to the name's BOTTOMLEFT at
            // (0,+4) - and FontStrings default to justifyV MIDDLE, so the 10px text floats
            // vertically centered inside those 18 units. That centering slack is the visible
            // air between name and rank in 1.12; drawing the ink at the box top (the previous
            // conversion) is what made them touch.
            float rankEm = GameText.EmPixels("SubSpellFont", fs);
            Vector2 rankPos = SpellbookLaw.SpellRankPosition(namePos, nameBlockHeight, s, rankEm);
            GameText.Draw(dl, "SubSpellFont", spell.Rank, rankPos, fs,
                snap: _spellbookFontPixelSnap);
        }
        ImGui.SetCursorScreenPos(min);
        bool clicked = ImGui.InvisibleButton($"##spell-{(petBook ? "pet" : "player")}-{id}",
            SpellbookLaw.ButtonScaledSize(s),
            ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);
        if (ImGui.IsItemActivated())
        {
            if (petBook) { _pressedPetBookWord = petPacked; _pressedSpellId = 0; }
            else { _pressedSpellId = id; _pressedPetBookWord = 0; }
            _spellPressPosition = ImGui.GetIO().MousePos;
        }
        bool rightClick = clicked && ImGui.IsMouseReleased(ImGuiMouseButton.Right);
        bool receiveDrag = ImGui.IsItemHovered() && HasActionBarCursor &&
            (ImGui.IsMouseReleased(ImGuiMouseButton.Left) ||
             ImGui.IsMouseReleased(ImGuiMouseButton.Right));
        if (receiveDrag)
        {
            if (petBook) PickupPetBookSpell(petPacked, spell);
            else PickupSpellToCursor(id);
        }
        else if (clicked && _draggingSpellId == 0)
        {
            if (petBook && rightClick && !ShiftHeld())
                TogglePetBookAutocast(id);
            else if (ShiftHeld())
            {
                if (!TryAppendSpellToOpenMacro(spell))
                {
                    if (petBook) PickupPetBookSpell(petPacked, spell);
                    else PickupSpellToCursor(id);
                }
            }
            else if (petBook) CastPetBookSpell(petPacked, spell, pet);
            else if (!TryOpenProfession(id)) TryCast(id);
        }
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
            GameTooltipOwnerKey tooltipOwner = petBook
                ? new GameTooltipOwnerKey("pet-spellbook-button", (ulong)buttonOrdinal)
                : new GameTooltipOwnerKey("spellbook-button", (ulong)buttonOrdinal);
            _hoveredSpellTooltip = PrepareSharedSpellTooltip(
                tooltipOwner,
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
        ImGui.SetNextWindowPos(SpellbookLaw.FontCalibrationPosition(spellbookOrigin, s),
            ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(SpellbookLaw.FontCalibrationSize, ImGuiCond.FirstUseEver);
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

    private void DrawSpellbookTypeTab(ImDrawListPtr dl, Vector2 frameOrigin, float s,
        bool petBook, string label)
    {
        PetSpellBookUiLaw.LogicalRect authored = petBook
            ? PetSpellBookUiLaw.PetTab : PetSpellBookUiLaw.PlayerTab;
        Vector2 min = authored.ScaledMin(frameOrigin, s);
        Vector2 size = authored.ScaledSize(s);
        Vector2 hitMin = min + PetSpellBookUiLaw.TabHitMin * s;
        Vector2 hitMax = min + PetSpellBookUiLaw.TabHitMax * s;
        ImGui.SetCursorScreenPos(hitMin);
        ImGui.InvisibleButton($"##spellbook-type-{(petBook ? "pet" : "player")}",
            hitMax - hitMin);
        bool hovered = ImGui.IsItemHovered();
        bool selected = _spellbookPetBook == petBook;
        if (!selected && ImGui.IsItemClicked())
            ToggleSpellbookTypeThroughUiPanel(petBook);

        string plate = selected
            ? @"Interface\SpellBook\UI-SpellBook-Tab1-Selected"
            : @"Interface\SpellBook\UI-SpellBook-Tab-Unselected";
        uint texture = _gameplayArt!.Handle(plate);
        if (texture != 0) dl.AddImage((nint)texture, min, min + size);
        if (!selected && hovered)
        {
            uint highlight = _gameplayArt.AdditiveHandle(
                @"Interface\SpellBook\UI-SpellbookPanel-Tab-Highlight");
            if (highlight != 0) dl.AddImage((nint)highlight, min, min + size);
        }
        GameText.DrawCentered(dl, selected || hovered ? "GameFontHighlightSmall" :
            "GameFontNormalSmall", label,
            min + PetSpellBookUiLaw.TabTextCenterOffset * s, s);
        if (hovered)
        {
            string prepared = label;
            var owner = new GameTooltipOwnerKey("spellbook-type-tab", petBook ? 2u : 1u);
            OfferPreservedSharedGameTooltipRenderer(owner, () =>
            {
                ImGui.BeginTooltip();
                ImGui.TextUnformatted(prepared);
                ImGui.EndTooltip();
            });
        }
    }

    private void DrawSpellTab(ImDrawListPtr dl, Vector2 min, float s, int tabOrdinal,
        uint id, string name, string iconPath)
    {
        static Vector2 Snap(Vector2 value) => new(MathF.Round(value.X), MathF.Round(value.Y));
        uint back = _gameplayArt!.Handle(@"Interface\SpellBook\SpellBook-SkillLineTab");
        // FrameXML TOPLEFT(-3,+11) converts to screen (-3,-11); retain the authored 64x64 size.
        if (back != 0) dl.AddImage((nint)back,
            Snap(min + SpellbookLaw.SkillLineTabBackdrop.Min * s),
            Snap(min + (SpellbookLaw.SkillLineTabBackdrop.Min +
                SpellbookLaw.SkillLineTabBackdrop.Size) * s));
        uint icon = _gameplayArt.Handle(iconPath);
        // Skill-line icons stay full-bright in 1.12. Selection is conveyed solely by the
        // CheckButtonHilight overlay; dimming inactive tabs makes the whole right rail look disabled.
        Vector2 tabSize = SpellbookLaw.SkillLineTabScaledSize(s);
        if (icon != 0) dl.AddImage((nint)icon, Snap(min), Snap(min + tabSize));
        ImGui.SetCursorScreenPos(min);
        ImGui.InvisibleButton($"##spell-tab-{id}", tabSize);
        if (ImGui.IsItemClicked()) { _spellbookLine = id; _spellbookPage = 0; }
        if (id == _spellbookLine)
        {
            uint checkedArt = _gameplayArt.AdditiveHandle(@"Interface\Buttons\CheckButtonHilight");
            if (checkedArt != 0) dl.AddImage((nint)checkedArt, Snap(min), Snap(min + tabSize));
        }
        else if (ImGui.IsItemHovered())
        {
            uint highlight = _gameplayArt.BrightHighlightHandle(@"Interface\Buttons\ButtonHilight-Square");
            if (highlight != 0) dl.AddImage((nint)highlight, Snap(min), Snap(min + tabSize));
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

    private void DrawSpellbookAutocastSparkles(ImDrawListPtr draw, Vector2 buttonMin,
        float scale, double now)
    {
        uint star = _gameplayArt!.AdditiveHandle(@"Interface\Buttons\GlowStar");
        if (star == 0) return;
        Vector2 buttonMax = buttonMin + SpellbookLaw.ButtonScaledSize(scale);
        Vector2 display = ImGui.GetIO().DisplaySize;
        float diagonal = MathF.Sqrt(display.X * display.X + display.Y * display.Y);
        float lap = SpellbookLaw.AutocastLap(now);
        draw.PushClipRect(buttonMin, buttonMax, true);
        for (int emitter = 0; emitter < SpellbookLaw.AutocastEmitterCount; emitter++)
        for (int particle = 0; particle < SpellbookLaw.AutocastParticlesPerEmitter; particle++)
        {
            float age = SpellbookLaw.AutocastParticleAge(particle);
            Vector2 center = buttonMin +
                SpellbookLaw.AutocastPoint(lap, emitter, age) * scale;
            float half = SpellbookLaw.AutocastStarHalfExtent(age, diagonal);
            float angle = SpellbookLaw.AutocastSpinRadians * age;
            uint color = ImGui.ColorConvertFloat4ToU32(SpellbookLaw.AutocastStarColor(age));
            draw.AddImageQuad((nint)star,
                SpellbookLaw.AutocastStarCorner(center, -half, -half, angle),
                SpellbookLaw.AutocastStarCorner(center, half, -half, angle),
                SpellbookLaw.AutocastStarCorner(center, half, half, angle),
                SpellbookLaw.AutocastStarCorner(center, -half, half, angle),
                Vector2.Zero, Vector2.UnitX, Vector2.One, Vector2.UnitY, color);
        }
        draw.PopClipRect();
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
        uint casterLevel = _net is not null && _entities.TryGet(ControlledGuid, out WorldEntity player)
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
        Vector2 size = SpellTooltipLaw.FrameSize(contentWidth, rowStackHeight, s);
        Vector2 display = snapshot.DisplaySize;
        Vector2 pos = placement == SpellTooltipPlacement.DefaultBottomRight
            // GameTooltip_SetDefaultAnchor: BOTTOMRIGHT of UIParent at
            // (-CONTAINER_OFFSET_X - 13, CONTAINER_OFFSET_Y), defaults 0 and 70.
            ? SpellTooltipLaw.ClampOrigin(
                SpellTooltipLaw.DefaultBottomRightOrigin(display, size, s), size, display)
            : SpellTooltipLaw.OwnerRightOrigin(ownerMin, ownerMax, size, display, s);

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
                    SpellTooltipLaw.LeftTextPosition(pos, y, s), s, row.Color);
                if (!string.IsNullOrEmpty(row.Right))
                    GameText.DrawRightAligned(dl, row.FontObject, row.Right,
                        SpellTooltipLaw.RightTextPosition(pos, size, y, s), s,
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
        DrawImageButton(dl, previous ? "##spell-prev" : "##spell-next", min,
            SpellbookLaw.PreviousPageButton.Size * s,
            $@"Interface\Buttons\{stem}-{(enabled ? "Up" : "Disabled")}",
            $@"Interface\Buttons\{stem}-Down", @"Interface\Buttons\UI-Common-MouseHilight");
        if (enabled && ImGui.IsItemClicked())
        {
            _spellbookPage += previous ? -1 : 1;
            PlayUiSound(SpellbookLaw.PageTurnSound, "ui.spellbook");
        }
    }
}

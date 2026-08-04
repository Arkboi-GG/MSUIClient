using System.Numerics;
using ImGuiNET;
using Silk.NET.Input;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private bool _characterOpen;
    private bool _characterKeyWasDown;
    private int _characterTab;
    private SkillLineCatalog? _skillLines;
    private readonly HashSet<uint> _collapsedSkillCategories = new();
    private readonly HashSet<uint> _collapsedReputationHeaders = new();
    private int _skillScroll;
    private uint _selectedSkill;

    private void InitCharacterPage()
    {
        if (_mpq is null) return;
        try { _skillLines = SkillLineCatalog.Load(_mpq); }
        catch (Exception ex) { Console.WriteLine($"[character] skill catalog failed: {ex.Message}"); }
        InitReputation();
    }

    private static readonly (int Slot, string Empty)[] LeftPaperDollSlots =
    [
        (0, "Head"), (1, "Neck"), (2, "Shoulder"), (14, "Chest"),
        (4, "Chest"), (3, "Shirt"), (18, "Tabard"), (8, "Wrists"),
    ];

    private static readonly (int Slot, string Empty)[] RightPaperDollSlots =
    [
        (9, "Hands"), (5, "Waist"), (6, "Legs"), (7, "Feet"),
        (10, "Finger"), (11, "Finger"), (12, "Trinket"), (13, "Trinket"),
    ];

    private static readonly (int Slot, string Empty)[] WeaponPaperDollSlots =
        [(15, "MainHand"), (16, "SecondaryHand"), (17, "Ranged")];

    private void UpdateCharacterPageInput(bool typing)
    {
        bool down = BindingDown(GameBinding.OpenCharacter);
        bool control = _window.IsDown(Key.ControlLeft) || _window.IsDown(Key.ControlRight);
        if (down && !_characterKeyWasDown && !control && !typing && _net is { IsInWorld: true })
        {
            _characterOpen = !_characterOpen;
            if (_characterOpen) { _paperDollDirty = true; _spellbookOpen = false; }
        }
        _characterKeyWasDown = down;
    }

    private void DrawCharacterPage()
    {
        if (!_characterOpen || _net is null || _items is null || _gameplayArt is null ||
            !_entities.TryGet(_net.PlayerGuid, out WorldEntity player)) return;

        float scale = GameplayUiScale();
        Vector2 origin = new(0, 104f * scale);
        // CharacterFrame.xml is exactly 384x512. The previous 544px host left a
        // spurious black strip and pushed the five authored tabs below the frame.
        Vector2 size = new(384, 512);
        ImGui.SetNextWindowPos(origin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size * scale, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
                                 ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground |
                                 ImGuiWindowFlags.NoNav;
        if (!ImGui.Begin("##character-page", flags)) { ImGui.End(); return; }
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        if (_uiParityArmed && _uiParityPanel == "character-frame")
        {
            BeginUiParityFrame(origin, scale);
            CollectUiParityDraw("CharacterFrame", "Frame", origin, size * scale, "",
                new("", 0, "IMGUI_HOST", "ANCHOR:ABSOLUTE", "", "", origin.X / scale, origin.Y / scale));
            CollectUiParityDraw("PaperDollFrame", "Frame", origin, new Vector2(384, 512) * scale, "CharacterFrame",
                new("", 0, "IMGUI_HOST", "TOPLEFT", "CharacterFrame", "TOPLEFT", 0, 0));
        }
        if (_characterTab == 0) DrawPaperDollBackground(dl, origin, scale);
        else if (_characterTab == 3) DrawSkillBackground(dl, origin, scale);
        else DrawCharacterGeneralBackground(dl, origin, scale);

        switch (_characterTab)
        {
            case 0: DrawPaperDollPage(dl, origin, scale, player); break;
            case 2: DrawReputationPage(dl, origin, scale, player); break;
            case 3: DrawSkillsPage(dl, origin, scale, player); break;
            case 4: DrawHonorPage(dl, origin, scale, player); break;
        }

        DrawCharacterHeader(dl, origin, scale, player);
        DrawCharacterTabs(dl, origin, scale);
        if (_uiParityArmed && _uiParityPanel == "character-frame") MarkUiParityFrameComplete();
        ImGui.End();
    }

    private void DrawPaperDollBackground(ImDrawListPtr dl, Vector2 p, float s)
    {
        (string Element, string Path, Vector2 Offset, Vector2 Size)[] regions =
        [
            ("PaperDollFrame/Texture", @"Interface\PaperDollInfoFrame\UI-Character-CharacterTab-L1", Vector2.Zero, new(256,256)),
            ("PaperDollFrame/Texture#2", @"Interface\PaperDollInfoFrame\UI-Character-CharacterTab-R1", new(256,0), new(128,256)),
            ("PaperDollFrame/Texture#3", @"Interface\PaperDollInfoFrame\UI-Character-CharacterTab-BottomLeft", new(0,256), new(256,256)),
            ("PaperDollFrame/Texture#4", @"Interface\PaperDollInfoFrame\UI-Character-CharacterTab-BottomRight", new(256,256), new(128,256)),
        ];
        foreach (var region in regions)
        {
            Vector2 min = p + region.Offset * s;
            DrawArt(dl, region.Path, min, region.Size, s);
            if (_uiParityArmed && _uiParityPanel == "character-frame")
                CollectUiParityDraw(region.Element, "Texture", min, region.Size * s, "PaperDollFrame",
                    new(region.Path, 0xffffffff, "IMGUI_IMAGE", "TOPLEFT", "PaperDollFrame", "TOPLEFT", region.Offset.X, -region.Offset.Y));
        }
    }

    private void DrawSkillBackground(ImDrawListPtr dl, Vector2 p, float s)
    {
        DrawArt(dl, @"Interface\PaperDollInfoFrame\UI-Character-General-TopLeft", p + new Vector2(2, 1) * s, new(256, 256), s);
        DrawArt(dl, @"Interface\PaperDollInfoFrame\UI-Character-General-TopRight", p + new Vector2(258, 1) * s, new(128, 256), s);
        DrawArt(dl, @"Interface\PaperDollInfoFrame\SkillFrame-BotLeft", p + new Vector2(2, 255) * s, new(256, 256), s);
        DrawArt(dl, @"Interface\PaperDollInfoFrame\SkillFrame-BotRight", p + new Vector2(258, 255) * s, new(128, 256), s);
    }

    private void DrawCharacterGeneralBackground(ImDrawListPtr dl, Vector2 p, float s)
    {
        DrawArt(dl, @"Interface\PaperDollInfoFrame\UI-Character-General-TopLeft", p + new Vector2(2, 1) * s, new(256), s);
        DrawArt(dl, @"Interface\PaperDollInfoFrame\UI-Character-General-TopRight", p + new Vector2(258, 1) * s, new(128, 256), s);
        DrawArt(dl, @"Interface\PaperDollInfoFrame\UI-Character-General-BottomLeft", p + new Vector2(2, 257) * s, new(256), s);
        DrawArt(dl, @"Interface\PaperDollInfoFrame\UI-Character-General-BottomRight", p + new Vector2(258, 257) * s, new(128, 256), s);
    }

    private void DrawCharacterHeader(ImDrawListPtr dl, Vector2 p, float s, WorldEntity player)
    {
        if (_playerPortrait is not null && _playerPortraitUsable)
            dl.AddImage((nint)_playerPortrait.TextureHandle, p + new Vector2(7, 6) * s,
                p + new Vector2(67, 66) * s, new Vector2(0, 1), new Vector2(1, 0),
                0xffffffff);
        else
            DrawUnitPortraitImage(dl, player, p + new Vector2(7, 6) * s, 60f * s, 0, true);

        string name = _net?.PlayerName ?? "";
        // CharacterNameText inherits GameFontNormal but CharacterFrame.xml overrides its
        // <Color> to white (1,1,1) - the name is white, not GameFontNormal's default gold.
        GameText.DrawCentered(dl, "GameFontNormal", name, p + new Vector2(198, 24) * s, s, 0xffffffff);
        var bytes = player.Fields.Bytes0;
        string level = $"Level {player.Level} {RaceName(bytes.Race)} {ClassName(bytes.Class)}";
        // CharacterLevelText inherits GameFontNormalSmall; TOP of CharacterNameText BOTTOM -6.
        GameText.DrawCentered(dl, "GameFontNormalSmall", level, p + new Vector2(198, 41) * s, s);

        Vector2 close = p + new Vector2(324, 9) * s;
        DrawImageButton(dl, "##char-close", close, new Vector2(32) * s,
            @"Interface\Buttons\UI-Panel-MinimizeButton-Up",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Down",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Highlight");
        if (ImGui.IsItemClicked()) _characterOpen = false;
    }

    private void DrawPaperDollPage(ImDrawListPtr dl, Vector2 p, float s, WorldEntity player)
    {
        Vector2 modelMin = p + new Vector2(65, 78) * s;
        if (_paperDoll is not null && _paperDoll.TextureHandle != 0)
            dl.AddImage((nint)_paperDoll.TextureHandle, modelMin, modelMin + new Vector2(233, 224) * s,
                new Vector2(0, 1), new Vector2(1, 0));

        DrawRotationButton(dl, p + new Vector2(65, 78) * s, true, s);
        DrawRotationButton(dl, p + new Vector2(100, 78) * s, false, s);

        for (int i = 0; i < LeftPaperDollSlots.Length; i++)
            DrawEquipmentSlot(dl, p + new Vector2(21, 74 + i * 41) * s, s, player,
                LeftPaperDollSlots[i].Slot, LeftPaperDollSlots[i].Empty);
        for (int i = 0; i < RightPaperDollSlots.Length; i++)
            DrawEquipmentSlot(dl, p + new Vector2(305, 74 + i * 41) * s, s, player,
                RightPaperDollSlots[i].Slot, RightPaperDollSlots[i].Empty);
        for (int i = 0; i < WeaponPaperDollSlots.Length; i++)
            // CharacterMainHandSlot is TOPLEFT to PaperDollFrame.BOTTOMLEFT
            // (122,127): in top-origin coordinates its top is 512-127=385.
            DrawEquipmentSlot(dl, p + new Vector2(122 + i * 42, 385) * s, s, player,
                WeaponPaperDollSlots[i].Slot, WeaponPaperDollSlots[i].Empty);

        DrawCharacterStats(dl, p, s, player.Fields);
        DrawResistances(dl, p, s, player.Fields);
    }

    private void DrawRotationButton(ImDrawListPtr dl, Vector2 min, bool left, float s)
    {
        string stem = left ? "UI-RotationLeft-Button" : "UI-RotationRight-Button";
        DrawImageButton(dl, left ? "##paper-left" : "##paper-right", min, new Vector2(35) * s,
            $@"Interface\Buttons\{stem}-Up", $@"Interface\Buttons\{stem}-Down",
            @"Interface\Buttons\ButtonHilight-Round");
        if (ImGui.IsItemClicked())
        {
            _paperDollRotation += left ? -0.12f : 0.12f;
            _paperDollDirty = true;
        }
    }

    private void DrawEquipmentSlot(ImDrawListPtr dl, Vector2 min, float s, WorldEntity player,
        int slot, string emptySuffix)
    {
        Vector2 max = min + new Vector2(37) * s;
        ulong guid = player.Fields.PlayerInventorySlot(slot);
        WorldEntity? instance = guid != 0 && _entities.TryGet(guid, out WorldEntity found) ? found : null;
        ItemTemplate? item = null;
        if (instance is not null && _net is not null && _items is not null)
        {
            _items.Require(instance.Entry, instance.Guid, _net);
            _items.TryGet(instance.Entry, out item);
        }

        string art = item?.IconPath ?? $@"Interface\Paperdoll\UI-PaperDoll-Slot-{emptySuffix}";
        uint icon = _gameplayArt?.Handle(art) ?? 0;
        if (icon != 0) dl.AddImage((nint)icon, min, max);

        ImGui.SetCursorScreenPos(min);
        ImGui.InvisibleButton($"##equip-{slot}", max - min,
            ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);
        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
        {
            PickupOrPlaceItem(255, slot, guid);
            _paperDollDirty = true;
        }
        if (ImGui.IsItemHovered() && item is not null)
            DrawItemTooltip(item, instance?.Fields.ItemStackCount ?? 1,
                instance?.Fields.ItemDurability ?? 0, instance?.Fields.ItemMaxDurability ?? 0);

        uint ring = _gameplayArt?.Handle(@"Interface\Buttons\UI-Quickslot2") ?? 0;
        if (ring != 0)
        {
            Vector2 center = (min + max) * 0.5f + new Vector2(0, -s);
            Vector2 half = new(32f * s);
            dl.AddImage((nint)ring, center - half, center + half);
        }
    }

    private void DrawCharacterStats(ImDrawListPtr dl, Vector2 p, float s, ObjectFields f)
    {
        Vector2 basePos = p + new Vector2(67, 291) * s;
        uint bg = _gameplayArt?.Handle(@"Interface\PaperDollInfoFrame\UI-Character-StatBackground") ?? 0;
        if (bg != 0)
        {
            dl.AddImage((nint)bg, basePos, basePos + new Vector2(115, 85) * s,
                new Vector2(0, 0), new Vector2(0.8984375f, 0.609375f));
            dl.AddImage((nint)bg, basePos + new Vector2(115, 0) * s, basePos + new Vector2(230, 85) * s,
                new Vector2(0, 0), new Vector2(0.8984375f, 0.609375f));
        }

        // PaperDollFrame.lua SetStats appends ":" to SPELL_STATi_NAME; ARMOR_COLON is
        // "Armor:" (GlobalStrings.lua). The value is the same stat number either way.
        string[] labels = ["Strength:", "Agility:", "Stamina:", "Intellect:", "Spirit:"];
        for (int i = 0; i < 5; i++) DrawStatRow(dl, basePos + new Vector2(6, 3 + i * 13) * s,
            labels[i], f.Stat(i).ToString(), s);
        DrawStatRow(dl, basePos + new Vector2(6, 68) * s, "Armor:", f.Resistance(0).ToString(), s);

        float speed = f.MainAttackTime > 0 ? f.MainAttackTime / 1000f : 0;
        string damage = f.MaxDamage > 0 ? $"{f.MinDamage:0.#}-{f.MaxDamage:0.#}" : "0-0";
        DrawStatRow(dl, basePos + new Vector2(122, 2) * s, "Attack", speed > 0 ? speed.ToString("0.00") : "—", s);
        DrawStatRow(dl, basePos + new Vector2(127, 15) * s, "Attack Power", f.AttackPower.ToString(), s, 99);
        DrawStatRow(dl, basePos + new Vector2(127, 28) * s, "Damage", damage, s, 99);
        float rangedSpeed = f.RangedAttackTime > 0 ? f.RangedAttackTime / 1000f : 0;
        string rangedDamage = f.MaxRangedDamage > 0 ? $"{f.MinRangedDamage:0.#}-{f.MaxRangedDamage:0.#}" : "—";
        DrawStatRow(dl, basePos + new Vector2(122, 47) * s, "Ranged", rangedSpeed > 0 ? rangedSpeed.ToString("0.00") : "—", s);
        DrawStatRow(dl, basePos + new Vector2(127, 60) * s, "Ranged Power", f.RangedAttackPower.ToString(), s, 99);
        DrawStatRow(dl, basePos + new Vector2(127, 73) * s, "Damage", rangedDamage, s, 99);
    }

    private static void DrawStatRow(ImDrawListPtr dl, Vector2 p, string label, string value, float s, float width = 104)
    {
        GameText.Draw(dl, "GameFontNormalSmall", label, p, s);
        GameText.DrawRightAligned(dl, "GameFontHighlightSmall", value,
            new Vector2(p.X + width * s, p.Y), s);
    }

    private void DrawResistances(ImDrawListPtr dl, Vector2 p, float s, ObjectFields f)
    {
        uint art = _gameplayArt?.Handle(@"Interface\PaperDollInfoFrame\UI-Character-ResistanceIcons") ?? 0;
        float[] tops = [0.2265625f, 0f, 0.11328125f, 0.33984375f, 0.453125f];
        int[] schools = [6, 2, 3, 4, 5];
        for (int i = 0; i < 5; i++)
        {
            // CharacterResistanceFrame is 32px wide and its TOPRIGHT, not its
            // TOPLEFT, is anchored at x=297. Its authored left edge is x=265.
            Vector2 min = p + new Vector2(265, 77 + i * 29) * s;
            if (art != 0) dl.AddImage((nint)art, min, min + new Vector2(32, 29) * s,
                new Vector2(0, tops[i]), new Vector2(1, tops[i] + 0.11328125f));
            string v = f.Resistance(schools[i]).ToString();
            GameText.DrawCentered(dl, "GameFontHighlightSmall", v, min + new Vector2(16, 17) * s, s);
        }
    }

    private void DrawCharacterTabs(ImDrawListPtr dl, Vector2 p, float s)
    {
        // CharacterFrame.xml statically defines all five tabs, but PetPaperDollFrame.lua's
        // PetTab_Update() HIDES CharacterFrameTab2 (Pet) when HasPetUI() is false and
        // re-anchors Reputation into its slot (Tab3 LEFT -> Tab2 LEFT, offset 0), so a
        // petless character shows four tabs with the strip closed up. TryGetControlledPet
        // is this client's HasPetUI(): a summoned/charmed unit with a pet UI.
        bool hasPetUI = TryGetControlledPet(out _);
        string[] labels = ["Character", "Pet", "Reputation", "Skills", "Honor"];
        // CharacterFrameTab1 CENTER is (60, frame bottom - 62). The remaining tabs anchor
        // LEFT to the preceding RIGHT with a deliberate -16 overlap; a hidden Pet tab
        // consumes no slot, which lands Reputation exactly where Pet's LEFT was.
        float x = 60 - VanillaCharacterTabWidth(labels[0], s, 0) * .5f;
        for (int tab = 0; tab < labels.Length; tab++)
        {
            if (tab == 1 && !hasPetUI) continue;
            float width = VanillaCharacterTabWidth(labels[tab], s, 0);
            if (VanillaTab(dl, $"##char-tab-{tab}", p + new Vector2(x, 434) * s,
                    labels[tab], width, s, tab == _characterTab))
                _characterTab = tab;
            x += width - 16;
        }
    }

    private void DrawReputationPage(ImDrawListPtr dl, Vector2 p, float s, WorldEntity player)
    {
        // Factions with a live standing, grouped under their ParentFaction as the 1.12 pane does:
        // the parent (e.g. Alliance) is a collapsible header with no bar of its own; parentless
        // factions collect under "Other".
        var factions = new List<(FactionInfo Info, int Standing)>();
        if (_factionCatalog is not null)
            for (int i = 0; i < _reputation.Length; i++)
                if ((_reputation[i].Flags & 1) != 0 && _factionCatalog.TryGetByReputationIndex(i, out FactionInfo info))
                    factions.Add((info, info.BaseStanding(player.Fields.Bytes0.Race, player.Fields.Bytes0.Class) + _reputation[i].Standing));
        // A faction that heads a group (some other faction's parent) is shown only as that
        // header, never also as its own bar - Alliance is a header, not a row under "Other".
        var parentIds = factions.Select(x => x.Info.ParentFaction).Where(id => id != 0).ToHashSet();
        factions = factions.Where(x => !parentIds.Contains(x.Info.Id)).ToList();
        var groups = factions
            .GroupBy(x => x.Info.ParentFaction)
            .Select(g => (Key: g.Key,
                Name: g.Key != 0 && _factionCatalog!.TryGetName(g.Key, out string header) ? header : "Other",
                Factions: g.OrderBy(x => x.Info.Name, StringComparer.OrdinalIgnoreCase).ToList()))
            .OrderBy(g => g.Name.Equals("Other", StringComparison.Ordinal) ? 1 : 0).ThenBy(g => g.Name)
            .ToList();
        var display = new List<(bool Header, uint Key, string Name, FactionInfo Info, int Standing)>();
        foreach (var group in groups)
        {
            display.Add((true, group.Key, group.Name, default!, 0));
            if (!_collapsedReputationHeaders.Contains(group.Key))
                foreach (var fac in group.Factions)
                    display.Add((false, fac.Info.Id, fac.Info.Name, fac.Info, fac.Standing));
        }

        // ReputationFrameFactionLabel/StandingLabel inherit GameFontHighlight (white).
        GameText.DrawCentered(dl, "GameFontHighlight", "Faction", p + new Vector2(115, 64) * s, s);
        GameText.DrawCentered(dl, "GameFontHighlight", "Standing", p + new Vector2(243, 64) * s, s);
        const int visibleRows = 15;
        _reputationScroll = Math.Clamp(_reputationScroll, 0, Math.Max(0, display.Count - visibleRows));
        Vector2 listMin = p + new Vector2(24, 80) * s;
        ImGui.SetCursorScreenPos(listMin);
        ImGui.InvisibleButton("##reputation-scroll", new Vector2(300, 360) * s);
        if (ImGui.IsItemHovered() && ImGui.GetIO().MouseWheel != 0)
            _reputationScroll = Math.Clamp(_reputationScroll - Math.Sign(ImGui.GetIO().MouseWheel), 0, Math.Max(0, display.Count - visibleRows));

        uint repFrame = _gameplayArt?.Handle(@"Interface\PaperDollInfoFrame\UI-Character-ReputationBar") ?? 0;
        uint repFill = _gameplayArt?.Handle(@"Interface\PaperDollInfoFrame\UI-Character-Skills-Bar") ?? 0;
        uint repHighlight = _gameplayArt?.AdditiveHandle(@"Interface\PaperDollInfoFrame\UI-Character-ReputationBar-Highlight") ?? 0;
        Vector2 mouse = ImGui.GetIO().MousePos;
        // REPUTATIONFRAME_FACTIONHEIGHT = 26 (ReputationFrame.lua:2).
        for (int r = 0; r < visibleRows && r + _reputationScroll < display.Count; r++)
        {
            var row = display[r + _reputationScroll];
            Vector2 rowMin = p + new Vector2(24, 80 + r * 26) * s;
            if (row.Header)
            {
                // Category header: expand/collapse toggle drawn like the skill categories.
                uint glyph = _gameplayArt?.Handle(_collapsedReputationHeaders.Contains(row.Key)
                    ? @"Interface\Buttons\UI-PlusButton-Up" : @"Interface\Buttons\UI-MinusButton-Up") ?? 0;
                if (glyph != 0) dl.AddImage((nint)glyph, rowMin, rowMin + new Vector2(16) * s);
                GameText.Draw(dl, "GameFontNormal", row.Name, rowMin + new Vector2(20, 2) * s, s);
                Vector2 hdrMax = rowMin + new Vector2(285, 16) * s;
                if (mouse.X >= rowMin.X && mouse.X < hdrMax.X && mouse.Y >= rowMin.Y && mouse.Y < hdrMax.Y &&
                    ImGui.IsMouseClicked(ImGuiMouseButton.Left) &&
                    !_collapsedReputationHeaders.Add(row.Key))
                    _collapsedReputationHeaders.Remove(row.Key);
                continue;
            }
            // ReputationBarTemplate: a dark name box on the LEFT and a bordered CHANNEL on the
            // RIGHT (the top strip's right half is a frame with a transparent interior; native
            // x122/256 onward). The frame's rounded LEFT end is baked in; its right end is a
            // separate cap piece (V0.34375.., u0..0.0625). Skills-BarBorder is overlaid on the
            // channel so the inner border reads crisply at this scale. Name (LEFT anchor) and
            // standing (CENTER anchor) are vertically centered in the box (xml:95/104).
            var rank = ReputationRank(row.Standing);
            // Box nearly fills the 26px pitch so rows sit close together (1.12 has a ~2px gap).
            Vector2 boxMin = rowMin + new Vector2(10, 1) * s;
            Vector2 boxSize = new Vector2(262, 24) * s;
            bool hovered = mouse.X >= boxMin.X && mouse.X < boxMin.X + boxSize.X &&
                mouse.Y >= boxMin.Y && mouse.Y < boxMin.Y + boxSize.Y;
            float capW = 12f * s;
            Vector2 boxMax = boxMin + boxSize;
            float bodyW = boxSize.X - capW;   // the frame body spans native x0..256 across bodyW
            int earned = row.Standing - rank.Floor, band = Math.Max(1, rank.Ceiling - rank.Floor);
            float fraction = Math.Clamp((float)earned / band, 0, 1);
            // The channel's transparent hole, MEASURED in the frame texture: native x127..250,
            // y6..bottom of the 256x22 top band. Map it to the box. Draw the fill FIRST, extended
            // a few px under the frame on the left/top/bottom, then the frame ON TOP: the green
            // shows only through the hole - flush to the single border on every side - and its
            // overflow is masked, so it never pokes past the border and always touches the left.
            float holeLeft = boxMin.X + bodyW * (127f / 256f);
            float holeRight = boxMin.X + bodyW * (250f / 256f);
            float holeTop = boxMin.Y + boxSize.Y * (6f / 22f);
            float holeW = holeRight - holeLeft;
            if (repFill != 0 && fraction > 0)
                dl.AddImage((nint)repFill, new Vector2(holeLeft - 3 * s, holeTop - 3 * s),
                    new Vector2(holeLeft + holeW * fraction, boxMax.Y - 2 * s),
                    Vector2.Zero, Vector2.One, rank.Color);
            if (repFrame != 0)
            {
                dl.AddImage((nint)repFrame, boxMin, new Vector2(boxMax.X - capW, boxMax.Y),
                    Vector2.Zero, new Vector2(1f, 0.34375f), 0xffffffff);
                dl.AddImage((nint)repFrame, new Vector2(boxMax.X - capW, boxMin.Y), boxMax,
                    new Vector2(0f, 0.34375f), new Vector2(0.0625f, 0.71875f), 0xffffffff);
            }
            float chanLeft = holeLeft, chanRight = holeRight;   // standing text centers over the hole
            // Hover: additive ReputationBar-Highlight. Template Highlight1 is 256x28 over a 256x22
            // frame (offset out), so draw it LARGER than the row and stack it for a thick, bright
            // yellow glow (ReputationFrame.lua Highlight1/2 OnEnter).
            if (hovered && repHighlight != 0)
            {
                Vector2 hlMin = boxMin - new Vector2(3, 4) * s;
                Vector2 hlMax = boxMax + new Vector2(3, 4) * s;
                float hlCap = 16f * s;
                for (int pass = 0; pass < 2; pass++)
                {
                    dl.AddImage((nint)repHighlight, hlMin, new Vector2(hlMax.X - hlCap, hlMax.Y),
                        Vector2.Zero, new Vector2(1f, 0.4375f), 0xffffffff);
                    dl.AddImage((nint)repHighlight, new Vector2(hlMax.X - hlCap, hlMin.Y), hlMax,
                        new Vector2(0f, 0.4375f), new Vector2(0.06640625f, 0.875f), 0xffffffff);
                }
            }
            int em = GameText.EmPixels("GameFontHighlightSmall", s);
            float textTop = boxMin.Y + (boxSize.Y - em) * 0.5f;
            GameText.Draw(dl, "GameFontHighlightSmall", row.Name, new Vector2(boxMin.X + 10 * s, textTop), s);
            GameText.DrawCentered(dl, "GameFontHighlightSmall",
                hovered ? $"{earned} / {band}" : rank.Name,
                new Vector2((chanLeft + chanRight) * 0.5f, boxMin.Y + boxSize.Y * 0.5f), s);
        }
        if (display.Count == 0)
            GameText.DrawCentered(dl, "GameFontDisable", "No known reputations", p + new Vector2(190, 220) * s, s);
    }

    private void DrawHonorPage(ImDrawListPtr dl, Vector2 p, float s, WorldEntity player)
    {
        DrawArt(dl, @"Interface\PaperDollInfoFrame\UI-Character-Honor-TopLeft", p + new Vector2(22, 69) * s, new(256), s);
        DrawArt(dl, @"Interface\PaperDollInfoFrame\UI-Character-Honor-TopRight", p + new Vector2(275, 69) * s, new(128, 256), s);
        DrawArt(dl, @"Interface\PaperDollInfoFrame\UI-Character-Honor-BottomLeft", p + new Vector2(22, 325) * s, new(256, 128), s);
        DrawArt(dl, @"Interface\PaperDollInfoFrame\UI-Character-Honor-BottomRight", p + new Vector2(275, 325) * s, new(128), s);
        ObjectFields f = player.Fields;
        var session = f.SessionKills; var yesterday = f.YesterdayKills;
        var week = f.ThisWeekKills; var last = f.LastWeekKills;
        // HonorFrameCurrentPVPTitle: GameFontNormal, TOP anchor (0,-87) - the centered rank
        // title box. MSUI has no PVP-rank data, so this stands in for it.
        GameText.DrawCentered(dl, "GameFontNormal", "Honor", p + new Vector2(192, 93) * s, s);
        // Section title tops are the FrameXML anchors (Today 112, then BOTTOMLEFT chains at
        // -41/-43/-42/-64 → 165/220/274/350); MSUI previously used a flat 64px pitch, which
        // drifted the text off the art boxes. HK green, DK red, Contribution gold - value
        // right-aligned at the 278-wide row edge (x=46+278=324), per HonorFrameTemplates.xml.
        const uint green = 0xff1aff1a, red = 0xff1a1aff, gold = 0xff00d1ff;
        DrawHonorSection(dl, p, s, 112, "Today",
            ("Honorable Kills", session.Honorable.ToString(), green),
            ("Dishonorable Kills", session.Dishonorable.ToString(), red));
        DrawHonorSection(dl, p, s, 165, "Yesterday",
            ("Honorable Kills", yesterday.Honorable.ToString(), green),
            ("Honor", f.YesterdayContribution.ToString(), gold));
        DrawHonorSection(dl, p, s, 220, "This Week",
            ("Honorable Kills", week.Honorable.ToString(), green),
            ("Honor", f.ThisWeekContribution.ToString(), gold));
        DrawHonorSection(dl, p, s, 274, "Last Week",
            ("Honorable Kills", last.Honorable.ToString(), green),
            ("Honor", f.LastWeekContribution.ToString(), gold));
        DrawHonorSection(dl, p, s, 350, "Lifetime",
            ("Honorable Kills", f.LifetimeHonorableKills.ToString(), green),
            ("Dishonorable Kills", f.LifetimeDishonorableKills.ToString(), red));
    }

    private static void DrawHonorSection(ImDrawListPtr dl, Vector2 p, float s, float titleY,
        string title, params (string Label, string Value, uint Color)[] rows)
    {
        GameText.Draw(dl, "GameFontNormal", title, p + new Vector2(36, titleY) * s, s);
        for (int i = 0; i < rows.Length; i++)
        {
            float rowY = titleY + 15 + i * 14;
            // Label GameFontHighlightSmall white (LEFT); value the same face recolored per
            // GameFontGreen/Red/NormalSmall via the runtime color override, right-aligned.
            GameText.Draw(dl, "GameFontHighlightSmall", rows[i].Label, p + new Vector2(46, rowY) * s, s);
            GameText.DrawRightAligned(dl, "GameFontHighlightSmall", rows[i].Value,
                p + new Vector2(324, rowY) * s, s, rows[i].Color);
        }
    }

    private void DrawSkillsPage(ImDrawListPtr dl, Vector2 p, float s, WorldEntity player)
    {
        var skills = new List<(SkillLineInfo Info, ushort Value, ushort Max, int Bonus)>();
        for (int slot = 0; slot < 128; slot++)
        {
            ushort field = (ushort)(ObjectFields.PLAYER_SKILL_INFO_1_1 + slot * 3);
            uint packedId = player.Fields.GetU32(field) ?? 0;
            ushort skillId = (ushort)packedId;
            if (skillId == 0 || _skillLines is null || !_skillLines.TryGet(skillId, out SkillLineInfo info) ||
                info.CategoryId == 12) continue;
            uint packedValue = player.Fields.GetU32((ushort)(field + 1)) ?? 0;
            ushort value = (ushort)packedValue;
            ushort max = (ushort)(packedValue >> 16);
            uint packedBonus = player.Fields.GetU32((ushort)(field + 2)) ?? 0;
            int bonus = unchecked((short)packedBonus) + unchecked((short)(packedBonus >> 16));
            skills.Add((info, value, max, bonus));
        }

        var groups = skills.GroupBy(x => x.Info.CategoryId)
            .Select(g => (Category: _skillLines!.TryGetCategory(g.Key, out SkillCategoryInfo c)
                    ? c : new SkillCategoryInfo(g.Key, "Other", 99),
                Skills: g.OrderBy(x => x.Info.Name, StringComparer.OrdinalIgnoreCase).ToList()))
            .OrderBy(g => g.Category.DisplayOrder).ThenBy(g => g.Category.Id).ToList();
        // SkillFrame.lua greys a skill (grey bar, no rank number) when skillMaxRank == 1 - the
        // proficiency case. The mage's Class Skills (spell schools) render this way in 1.12, so
        // treat that whole category as proficiency-style too.
        var rows = new List<(bool Header, uint Key, string Name, ushort Value, ushort Max, int Bonus, bool Grey)>();
        foreach (var group in groups)
        {
            bool classCategory = group.Category.Name.Equals("Class Skills", StringComparison.OrdinalIgnoreCase);
            rows.Add((true, group.Category.Id, group.Category.Name, 0, 0, 0, false));
            if (!_collapsedSkillCategories.Contains(group.Category.Id))
                rows.AddRange(group.Skills.Select(x => (false, x.Info.Id, x.Info.Name, x.Value, x.Max,
                    x.Bonus, classCategory || x.Max <= 1)));
        }

        _skillScroll = Math.Clamp(_skillScroll, 0, Math.Max(0, rows.Count - 12));
        Vector2 listMin = p + new Vector2(22, 79) * s;
        ImGui.SetCursorScreenPos(listMin);
        ImGui.InvisibleButton("##skill-scroll", new Vector2(296, 216) * s);
        if (ImGui.IsItemHovered() && ImGui.GetIO().MouseWheel != 0)
            _skillScroll = Math.Clamp(_skillScroll - Math.Sign(ImGui.GetIO().MouseWheel), 0, Math.Max(0, rows.Count - 12));

        for (int visible = 0; visible < 12 && visible + _skillScroll < rows.Count; visible++)
        {
            var row = rows[visible + _skillScroll];
            Vector2 rowMin = p + new Vector2(22, 79 + visible * 18) * s;
            if (row.Header)
            {
                uint glyph = _gameplayArt?.Handle(_collapsedSkillCategories.Contains(row.Key)
                    ? @"Interface\Buttons\UI-PlusButton-Up" : @"Interface\Buttons\UI-MinusButton-Up") ?? 0;
                if (glyph != 0) dl.AddImage((nint)glyph, rowMin, rowMin + new Vector2(16) * s);
                GameText.Draw(dl, "GameFontHighlight", row.Name, rowMin + new Vector2(25, 0) * s, s);
                ImGui.SetCursorScreenPos(rowMin);
                ImGui.InvisibleButton($"##skill-group-{row.Key}", new Vector2(285, 14) * s);
                if (ImGui.IsItemClicked())
                {
                    if (!_collapsedSkillCategories.Add(row.Key)) _collapsedSkillCategories.Remove(row.Key);
                }
            }
            else
            {
                Vector2 barMin = rowMin + new Vector2(16, 0) * s;
                Vector2 barMax = barMin + new Vector2(271, 15) * s;
                // The row hit-rect is the SkillStatusBarTemplate $parentBorder Button. Submit it
                // first so its hover state is known before the highlight draws; only OnClick has a
                // handler (SkillBar_OnClick) - hover never fills the detail pane, it only glows.
                ImGui.SetCursorScreenPos(barMin);
                ImGui.InvisibleButton($"##skill-{row.Key}", new Vector2(271, 15) * s);
                bool rowHovered = ImGui.IsItemHovered();
                if (ImGui.IsItemClicked()) _selectedSkill = row.Key;
                uint bar = _gameplayArt?.Handle(@"Interface\PaperDollInfoFrame\UI-Character-Skills-Bar") ?? 0;
                uint border = _gameplayArt?.Handle(@"Interface\PaperDollInfoFrame\UI-Character-Skills-BarBorder") ?? 0;
                if (row.Grey)
                {
                    // maxRank==1 proficiency: full-width grey bar, no rank number.
                    if (bar != 0) dl.AddImage((nint)bar, barMin, barMax, Vector2.Zero, Vector2.One, 0xff909090);
                }
                else
                {
                    float fraction = row.Max > 0 ? Math.Clamp((float)row.Value / row.Max, 0, 1) : 0;
                    if (bar != 0 && fraction > 0)
                        dl.AddImage((nint)bar, barMin, new Vector2(barMin.X + (barMax.X - barMin.X) * fraction, barMax.Y),
                            Vector2.Zero, new Vector2(fraction, 1), 0xffbf4040);
                }
                if (border != 0) dl.AddImage((nint)border, barMin - new Vector2(5, 8) * s, barMin + new Vector2(276, 24) * s);
                // $parentBorder's HighlightTexture is UI-Character-Skills-BarBorderHighlight (ADD).
                // SkillFrame_SetStatusBar LockHighlight()s it for the selected row; a mouse-over
                // shows it on any row. Both are the same additive glow over the 281x32 border rect.
                if (rowHovered || _selectedSkill == row.Key)
                {
                    uint hl = _gameplayArt?.AdditiveHandle(@"Interface\PaperDollInfoFrame\UI-Character-Skills-BarBorderHighlight") ?? 0;
                    if (hl != 0) dl.AddImage((nint)hl, barMin - new Vector2(5, 8) * s, barMin + new Vector2(276, 24) * s);
                }
                GameText.Draw(dl, "GameFontNormalSmall", row.Name, barMin + new Vector2(6, 1) * s, s);
                if (!row.Grey)
                {
                    string rank = row.Bonus == 0 ? $"{row.Value} / {row.Max}" : $"{row.Value} + {row.Bonus} / {row.Max}";
                    uint? rankColor = row.Bonus != 0 ? (row.Bonus < 0 ? 0xff4040ff : (uint?)0xff40ff40) : null;
                    GameText.Draw(dl, "GameFontHighlightSmall", rank, barMin + new Vector2(177, 1) * s, s, rankColor);
                }
            }
        }

        // SkillFrame ARTWORK layer: the divider above the detail pane. Two runs of
        // UI-ClassTrainer-HorizontalBar anchored TOPLEFT (15,-290) - a 256px left run (v0..0.25)
        // then a 75px right run (v0.25..0.5). (SkillFrame.xml SkillFrameHorizontalBarLeft.)
        uint hbar = _gameplayArt?.Handle(@"Interface\ClassTrainerFrame\UI-ClassTrainer-HorizontalBar") ?? 0;
        if (hbar != 0)
        {
            dl.AddImage((nint)hbar, p + new Vector2(15, 290) * s, p + new Vector2(271, 306) * s,
                new Vector2(0, 0), new Vector2(1f, 0.25f));
            dl.AddImage((nint)hbar, p + new Vector2(271, 290) * s, p + new Vector2(346, 306) * s,
                new Vector2(0, 0.25f), new Vector2(0.29296875f, 0.5f));
        }
        // Clicking a skill row fills the detail pane (SkillBar_OnClick -> SetSelectedSkill ->
        // SkillDetailFrame_SetStatusBar); hover never touches it. Look the selected skill's live
        // value/max/bonus back up from the field-derived list so the detail bar fills correctly.
        if (_selectedSkill != 0 && _skillLines is not null &&
            _skillLines.TryGet(_selectedSkill, out SkillLineInfo selected))
        {
            var sel = skills.FirstOrDefault(x => x.Info.Id == _selectedSkill);
            bool grey = sel.Info.Id == _selectedSkill &&
                (sel.Max <= 1 || (_skillLines.TryGetCategory(sel.Info.CategoryId, out SkillCategoryInfo cat) &&
                    cat.Name.Equals("Class Skills", StringComparison.OrdinalIgnoreCase)));
            DrawSkillDetail(dl, p, s, selected, sel.Value, sel.Max, sel.Bonus, grey);
        }
    }

    private void DrawSkillDetail(ImDrawListPtr dl, Vector2 p, float s, SkillLineInfo info,
        ushort value, ushort max, int bonus, bool grey)
    {
        // SkillDetailStatusBar (SkillFrame.xml): 211x15, CENTER to SkillDetailScrollChildFrame.TOP
        // offset (-10,-20). Resolving the anchor chain (list scroll TOPRIGHT (317,75) -> detail
        // scroll TOPLEFT (21,303) -> child TOP (181,303)) puts the bar centre at frame-local
        // (171,323), so its top-left is (65.5,315.5).
        Vector2 boxMin = p + new Vector2(65.5f, 315.5f) * s;
        Vector2 boxSize = new Vector2(211, 15) * s;
        Vector2 boxMax = boxMin + boxSize;
        // $parentBorder texture: 220x32, LEFT offset (-5,0) -> top-left (60.5,307). The same rounded
        // frame as the list rows: its transparent channel reveals the fill, its border masks the
        // overflow, so the fill is drawn across the full 15px bar and the frame clips it.
        Vector2 borderMin = p + new Vector2(60.5f, 307f) * s;
        Vector2 borderMax = borderMin + new Vector2(220, 32) * s;

        // SkillDetailFrame_SetStatusBar colours (ABGR). Normal skill: bar SetStatusBarColor(0,0,1,
        // 0.5), background SetVertexColor(0,0,0.75,0.5). Proficiency (skillMaxRank==1): bar
        // (0.5,0.5,0.5) full, background white a0.5, no rank text.
        uint fillColor = grey ? 0xff808080u : 0x7fff0000u;
        uint bgColor = grey ? 0x80ffffffu : 0x7fbf0000u;
        float fraction = grey ? 1f : (max > 0 ? Math.Clamp((float)value / max, 0, 1) : 0);

        uint bar = _gameplayArt?.Handle(@"Interface\PaperDollInfoFrame\UI-Character-Skills-Bar") ?? 0;
        uint border = _gameplayArt?.Handle(@"Interface\PaperDollInfoFrame\UI-Character-Skills-BarBorder") ?? 0;
        dl.AddRectFilled(boxMin, boxMax, bgColor);
        if (bar != 0 && fraction > 0)
            dl.AddImage((nint)bar, boxMin, new Vector2(boxMin.X + boxSize.X * fraction, boxMax.Y),
                Vector2.Zero, new Vector2(fraction, 1), fillColor);
        if (border != 0) dl.AddImage((nint)border, borderMin, borderMax);

        // $parentSkillName GameFontNormalSmall (10px gold) LEFT +(6,1); $parentSkillRank
        // GameFontHighlightSmall (10px white) LEFT to name RIGHT +13 (SkillFrame.lua). Both are
        // vertically centred in the 15px bar (justifyV MIDDLE), nudged up 1px by the (x,1) offset.
        int em = GameText.EmPixels("GameFontNormalSmall", s);
        float textTop = boxMin.Y + (boxSize.Y - em) * 0.5f - 1f * s;
        float nameLeft = boxMin.X + 6f * s;
        GameText.Draw(dl, "GameFontNormalSmall", info.Name, new Vector2(nameLeft, textTop), s);
        if (!grey)
        {
            float rankLeft = nameLeft + GameText.MeasureWidth("GameFontNormalSmall", info.Name, s) + 13f * s;
            if (bonus == 0)
                GameText.Draw(dl, "GameFontHighlightSmall", $"{value}/{max}", new Vector2(rankLeft, textTop), s);
            else
            {
                // "value (" white, "±mod" in GREEN/RED_FONT_COLOR_CODE, ")/max" white (SkillFrame.lua).
                uint modColor = bonus > 0 ? 0xff20ff20u : 0xff2020ffu;
                string pre = $"{value} (", mod = bonus > 0 ? $"+{bonus}" : bonus.ToString(), post = $")/{max}";
                float x = rankLeft;
                GameText.Draw(dl, "GameFontHighlightSmall", pre, new Vector2(x, textTop), s);
                x += GameText.MeasureWidth("GameFontHighlightSmall", pre, s);
                GameText.Draw(dl, "GameFontHighlightSmall", mod, new Vector2(x, textTop), s, modColor);
                x += GameText.MeasureWidth("GameFontHighlightSmall", mod, s);
                GameText.Draw(dl, "GameFontHighlightSmall", post, new Vector2(x, textTop), s);
            }
        }

        // SkillDetailDescriptionText GameFontHighlightSmall (white), width 275, LEFT/TOP. For a
        // normal skill (no cost text) SkillFrame.lua anchors its TOP to SkillDetailCostText's TOP =
        // child TOP + (-10,-40) -> top-centre frame-local (171,343); left edge 171-137.5=33.5.
        if (!string.IsNullOrWhiteSpace(info.Description))
        {
            float descLeft = p.X + 33.5f * s, descTop = p.Y + 343f * s, wrapPx = 275f * s;
            float pitch = GameText.LinePitch("GameFontHighlightSmall", s);
            List<string> lines = WrapSkillDescription(info.Description, "GameFontHighlightSmall", s, wrapPx);
            for (int i = 0; i < lines.Count; i++)
                GameText.Draw(dl, "GameFontHighlightSmall", lines[i], new Vector2(descLeft, descTop + i * pitch), s);
        }
    }

    private static List<string> WrapSkillDescription(string text, string fontObject, float uiScale,
        float maxWidthPx)
    {
        string[] words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        List<string> lines = [];
        string current = "";
        foreach (string word in words)
        {
            string candidate = current.Length == 0 ? word : current + " " + word;
            if (current.Length > 0 && GameText.MeasureWidth(fontObject, candidate, uiScale) > maxWidthPx)
            { lines.Add(current); current = word; }
            else current = candidate;
        }
        if (current.Length > 0) lines.Add(current);
        return lines;
    }

    private void DrawImageButton(ImDrawListPtr dl, string id, Vector2 min, Vector2 size,
        string normal, string pushed, string highlight)
    {
        ImGui.SetCursorScreenPos(min);
        ImGui.InvisibleButton(id, size);
        string path = ImGui.IsItemActive() ? pushed : normal;
        uint art = _gameplayArt?.Handle(path) ?? 0;
        if (art != 0) dl.AddImage((nint)art, min, min + size);
        if (ImGui.IsItemHovered())
        {
            // These are FrameXML HighlightTexture assets authored for ADD blending.
            uint hi = _gameplayArt?.AdditiveHandle(highlight) ?? 0;
            if (hi != 0) dl.AddImage((nint)hi, min, min + size);
        }
    }

    private void DrawArt(ImDrawListPtr dl, string path, Vector2 min, Vector2 size, float s)
    {
        uint art = _gameplayArt?.Handle(path) ?? 0;
        if (art != 0) dl.AddImage((nint)art, min, min + size * s);
    }

    private static void DrawCenteredText(ImDrawListPtr dl, Vector2 center, string text, float size, uint color)
    {
        Vector2 measured = ImGui.CalcTextSize(text) * (size / Math.Max(1f, ImGui.GetFontSize()));
        dl.AddText(ImGui.GetFont(), size, center - measured * 0.5f, color, text);
    }

}

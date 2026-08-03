using System.Numerics;
using ImGuiNET;
using Silk.NET.Input;
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
        // CharacterNameText is a 300x12 region at (48,18), centered at (198,24).
        DrawCenteredText(dl, p + new Vector2(198, 24) * s, name, 12f * s, 0xffffffff);
        var bytes = player.Fields.Bytes0;
        string level = $"Level {player.Level} {RaceName(bytes.Race)} {ClassName(bytes.Class)}";
        // CharacterLevelText is TOP of CharacterNameText BOTTOM -6.
        DrawCenteredText(dl, p + new Vector2(198, 41) * s, level, 10f * s, VanillaGold);

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

        string[] labels = ["Strength", "Agility", "Stamina", "Intellect", "Spirit"];
        for (int i = 0; i < 5; i++) DrawStatRow(dl, basePos + new Vector2(6, 3 + i * 13) * s,
            labels[i], f.Stat(i).ToString(), s);
        DrawStatRow(dl, basePos + new Vector2(6, 68) * s, "Armor", f.Resistance(0).ToString(), s);

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
        dl.AddText(ImGui.GetFont(), 10f * s, p, VanillaGold, label);
        Vector2 text = ImGui.CalcTextSize(value);
        dl.AddText(ImGui.GetFont(), 10f * s, p + new Vector2(width * s - text.X, 0), 0xffffffff, value);
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
            DrawCenteredText(dl, min + new Vector2(16, 17) * s, v, 10f * s, 0xffffffff);
        }
    }

    private void DrawCharacterTabs(ImDrawListPtr dl, Vector2 p, float s)
    {
        string[] labels = ["Character", "Pet", "Reputation", "Skills", "Honor"];
        float[] widths = labels.Select(label => VanillaCharacterTabWidth(label, s, 0)).ToArray();
        // CharacterFrameTab1 CENTER is (60, frame bottom - 62). The remaining
        // tabs anchor LEFT to the preceding RIGHT with a deliberate -16 overlap.
        float x = 60 - widths[0] * .5f;
        for (int tab = 0; tab < labels.Length; tab++)
        {
            if (VanillaTab(dl, $"##char-tab-{tab}", p + new Vector2(x, 434) * s,
                    labels[tab], widths[tab], s, tab == _characterTab, tab != 1))
                _characterTab = tab;
            x += widths[tab] - 16;
        }
    }

    private void DrawReputationPage(ImDrawListPtr dl, Vector2 p, float s, WorldEntity player)
    {
        var rows = new List<(FactionInfo Info, int Standing)>();
        if (_factionCatalog is not null)
            for (int i = 0; i < _reputation.Length; i++)
                if ((_reputation[i].Flags & 1) != 0 && _factionCatalog.TryGetByReputationIndex(i, out FactionInfo info))
                    rows.Add((info, info.BaseStanding(player.Fields.Bytes0.Race, player.Fields.Bytes0.Class) + _reputation[i].Standing));

        DrawCenteredText(dl, p + new Vector2(115, 64) * s, "Faction", 11f * s, VanillaGold);
        DrawCenteredText(dl, p + new Vector2(255, 64) * s, "Standing", 11f * s, VanillaGold);
        _reputationScroll = Math.Clamp(_reputationScroll, 0, Math.Max(0, rows.Count - 15));
        Vector2 listMin = p + new Vector2(25, 86) * s;
        ImGui.SetCursorScreenPos(listMin);
        ImGui.InvisibleButton("##reputation-scroll", new Vector2(300, 360) * s);
        if (ImGui.IsItemHovered() && ImGui.GetIO().MouseWheel != 0)
            _reputationScroll = Math.Clamp(_reputationScroll - Math.Sign(ImGui.GetIO().MouseWheel), 0, Math.Max(0, rows.Count - 15));
        for (int row = 0; row < 15 && row + _reputationScroll < rows.Count; row++)
        {
            var value = rows[row + _reputationScroll];
            var rank = ReputationRank(value.Standing);
            Vector2 min = p + new Vector2(25, 87 + row * 23) * s;
            dl.AddText(ImGui.GetFont(), 11f * s, min, 0xffffffff, value.Info.Name);
            Vector2 barMin = min + new Vector2(137, 0) * s;
            Vector2 barSize = new Vector2(137, 13) * s;
            dl.AddRectFilled(barMin, barMin + barSize, 0xff202020);
            float fraction = Math.Clamp((float)(value.Standing - rank.Floor) / Math.Max(1, rank.Ceiling - rank.Floor), 0, 1);
            dl.AddRectFilled(barMin, barMin + new Vector2(barSize.X * fraction, barSize.Y), rank.Color);
            DrawCenteredText(dl, barMin + barSize * .5f, rank.Name, 10f * s, 0xffffffff);
        }
        if (rows.Count == 0)
            DrawCenteredText(dl, p + new Vector2(190, 220) * s, "No known reputations", 12f * s, 0xffaaaaaa);
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
        DrawCenteredText(dl, p + new Vector2(192, 88) * s, "Honor", 14f * s, VanillaGold);
        DrawHonorBlock(dl, p, s, 112, "Today", session.Honorable, null);
        DrawHonorBlock(dl, p, s, 176, "Yesterday", yesterday.Honorable, f.YesterdayContribution);
        DrawHonorBlock(dl, p, s, 240, "This Week", week.Honorable, f.ThisWeekContribution);
        DrawHonorBlock(dl, p, s, 304, "Last Week", last.Honorable, f.LastWeekContribution);
        dl.AddText(ImGui.GetFont(), 11f * s, p + new Vector2(45, 384) * s, VanillaGold, "Lifetime");
        dl.AddText(ImGui.GetFont(), 10f * s, p + new Vector2(55, 405) * s, 0xffffffff,
            $"Honorable Kills: {f.LifetimeHonorableKills}");
        dl.AddText(ImGui.GetFont(), 10f * s, p + new Vector2(55, 421) * s, 0xffffffff,
            $"Dishonorable Kills: {f.LifetimeDishonorableKills}");
    }

    private static void DrawHonorBlock(ImDrawListPtr dl, Vector2 p, float s, float y, string title,
        uint kills, uint? contribution)
    {
        dl.AddText(ImGui.GetFont(), 11f * s, p + new Vector2(45, y) * s, VanillaGold, title);
        dl.AddText(ImGui.GetFont(), 10f * s, p + new Vector2(55, y + 20) * s, 0xffffffff, $"Honorable Kills: {kills}");
        if (contribution is { } cp)
            dl.AddText(ImGui.GetFont(), 10f * s, p + new Vector2(55, y + 36) * s, 0xffffffff, $"Honor: {cp}");
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
        var rows = new List<(bool Header, uint Key, string Name, ushort Value, ushort Max, int Bonus)>();
        foreach (var group in groups)
        {
            rows.Add((true, group.Category.Id, group.Category.Name, 0, 0, 0));
            if (!_collapsedSkillCategories.Contains(group.Category.Id))
                rows.AddRange(group.Skills.Select(x => (false, x.Info.Id, x.Info.Name, x.Value, x.Max, x.Bonus)));
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
                dl.AddText(ImGui.GetFont(), 12f * s, rowMin + new Vector2(25, 0) * s, 0xffffffff, row.Name);
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
                uint bar = _gameplayArt?.Handle(@"Interface\PaperDollInfoFrame\UI-Character-Skills-Bar") ?? 0;
                uint border = _gameplayArt?.Handle(@"Interface\PaperDollInfoFrame\UI-Character-Skills-BarBorder") ?? 0;
                float fraction = row.Max > 0 ? Math.Clamp((float)row.Value / row.Max, 0, 1) : 0;
                if (bar != 0 && fraction > 0)
                    dl.AddImage((nint)bar, barMin, new Vector2(barMin.X + (barMax.X - barMin.X) * fraction, barMax.Y),
                        Vector2.Zero, new Vector2(fraction, 1), 0xffbf4040);
                if (border != 0) dl.AddImage((nint)border, barMin - new Vector2(5, 8) * s, barMin + new Vector2(276, 24) * s);
                if (_selectedSkill == row.Key)
                {
                    uint hi = _gameplayArt?.Handle(@"Interface\Buttons\UI-Listbox-Highlight2") ?? 0;
                    if (hi != 0) dl.AddImage((nint)hi, barMin - new Vector2(5, 0), barMax + new Vector2(5, 0));
                }
                dl.AddText(ImGui.GetFont(), 10f * s, barMin + new Vector2(6, 1) * s, VanillaGold, row.Name);
                string rank = row.Bonus == 0 ? $"{row.Value} / {row.Max}" : $"{row.Value} + {row.Bonus} / {row.Max}";
                dl.AddText(ImGui.GetFont(), 10f * s, barMin + new Vector2(177, 1) * s,
                    row.Bonus < 0 ? 0xff4040ff : row.Bonus > 0 ? 0xff40ff40 : 0xffffffff, rank);
                ImGui.SetCursorScreenPos(barMin);
                ImGui.InvisibleButton($"##skill-{row.Key}", new Vector2(271, 15) * s);
                if (ImGui.IsItemClicked()) _selectedSkill = row.Key;
            }
        }

        DrawArt(dl, @"Interface\ClassTrainerFrame\UI-ClassTrainer-HorizontalBar",
            p + new Vector2(15, 305) * s, new Vector2(331, 16), s);
        if (_selectedSkill != 0 && _skillLines is not null && _skillLines.TryGet(_selectedSkill, out SkillLineInfo selected))
        {
            dl.AddText(ImGui.GetFont(), 12f * s, p + new Vector2(44, 329) * s, VanillaGold, selected.Name);
            if (!string.IsNullOrWhiteSpace(selected.Description))
                dl.AddText(ImGui.GetFont(), 10f * s, p + new Vector2(38, 353) * s, 0xffffffff,
                    selected.Description);
        }
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

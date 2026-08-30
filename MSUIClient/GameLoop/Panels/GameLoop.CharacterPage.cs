using System.Collections.Immutable;
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
    private bool _skillsKeyWasDown;
    private int _characterTab;
    private SkillLineCatalog? _skillLines;
    private ItemSubClassCatalog? _itemSubClasses;
    private ItemSetCatalog? _itemSets;
    private readonly HashSet<uint> _collapsedSkillCategories = new();
    private readonly HashSet<uint> _collapsedReputationHeaders = new();
    private int _skillScroll;
    private uint _selectedSkill;

    private void InitCharacterPage()
    {
        if (_mpq is null) return;
        try { _skillLines = SkillLineCatalog.Load(_mpq); }
        catch (Exception ex) { Console.WriteLine($"[character] skill catalog failed: {ex.Message}"); }
        try { _itemSubClasses = ItemSubClassCatalog.Load(_mpq); }
        catch (Exception ex) { Console.WriteLine($"[character] item subclass catalog failed: {ex.Message}"); }
        try { _itemSets = ItemSetCatalog.Load(_mpq); }
        catch (Exception ex) { Console.WriteLine($"[character] item set catalog failed: {ex.Message}"); }
        InitReputation();
        InitPetPaperDollData();
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

    private bool SetCharacterPageOpen(bool open, bool playSound = true,
        string soundCategory = "ui")
    {
        if (_characterOpen == open) return false;
        _characterOpen = open;
        if (open)
            _paperDollDirty = true;
        else
            _reputationDetailOpen = false;
        if (playSound)
            PlayCharacterTransition(open ? PaperDollUiLaw.OpenSound : PaperDollUiLaw.CloseSound,
                soundCategory);
        return true;
    }

    private void PlayCharacterTransition(PaperDollUiLaw.SoundTransition transition,
        string soundCategory = "ui")
    {
        for (int i = 0; i < transition.Count; i++)
            PlayUiSound(transition.Cue, soundCategory);
    }

    private void UpdateCharacterPageInput(bool typing)
    {
        bool down = BindingDown(GameBinding.OpenCharacter);
        bool control = InputKeyDown(Key.ControlLeft) || InputKeyDown(Key.ControlRight);
        if (down && !_characterKeyWasDown && !control && !typing && _net is { IsInWorld: true })
            ToggleCharacterPageThroughUiPanel();
        _characterKeyWasDown = down;

        bool skillsDown = BindingDown(GameBinding.OpenSkills);
        bool alt = InputKeyDown(Key.AltLeft) || InputKeyDown(Key.AltRight);
        bool shift = InputKeyDown(Key.ShiftLeft) || InputKeyDown(Key.ShiftRight);
        bool super = InputKeyDown(Key.SuperLeft) || InputKeyDown(Key.SuperRight);
        if (SkillFrameUiLaw.FiresDirectBinding(skillsDown, _skillsKeyWasDown, alt, control,
                shift, super, typing, _net is { IsInWorld: true }))
        {
            switch (SkillFrameUiLaw.ResolveDirectToggle(_characterOpen, _characterTab))
            {
                case SkillFrameUiLaw.ToggleAction.OpenSkills:
                    OpenCharacterPageThroughUiPanel(
                        soundCategory: "ui.skill-frame",
                        requestedTab: SkillFrameUiLaw.SkillsTab);
                    break;
                case SkillFrameUiLaw.ToggleAction.CloseSkills:
                    SetCharacterPageOpen(false, soundCategory: "ui.skill-frame");
                    break;
                case SkillFrameUiLaw.ToggleAction.SwitchToSkills:
                    _characterTab = SkillFrameUiLaw.SkillsTab;
                    PlayUiSound(SkillFrameUiLaw.DirectTabSound, "ui.skill-frame");
                    break;
            }
        }
        _skillsKeyWasDown = skillsDown;
    }

    private void DrawCharacterPage()
    {
        if (!_characterOpen || _net is null || _items is null || _gameplayArt is null ||
            !_entities.TryGet(ControlledGuid, out WorldEntity player)) return;
        if (_characterTab == 1 && !TryGetControlledPet(out _))
        {
            SetCharacterPageOpen(false);
            return;
        }

        float scale = GameplayUiScale();
        Vector2 origin = new(0, 104f * scale);
        // CharacterFrame.xml is exactly 384x512. The previous 544px host left a
        // spurious black strip and pushed the five authored tabs below the frame.
        Vector2 size = new(PaperDollUiLaw.FrameWidth, PaperDollUiLaw.FrameHeight);
        ImGui.SetNextWindowPos(origin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size * scale, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
                                 ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground |
                                 ImGuiWindowFlags.NoNav;
        if (!ImGui.Begin("##character-page", flags)) { ImGui.End(); return; }
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        Vector4 panelClip = new(origin.X, origin.Y,
            origin.X + size.X * scale, origin.Y + size.Y * scale);
        if (CharacterPageUiParityCaptureActive)
        {
            BeginUiParityFrame(origin, scale);
            CollectUiParityDraw("CharacterFrame", "Frame", origin, size * scale, "",
                new("", 0, "IMGUI_HOST", "ANCHOR:ABSOLUTE", "UIParent", "TOPLEFT",
                    origin.X / scale, -origin.Y / scale, ContentRect: panelClip, ClipRect: panelClip,
                    ClipMask: "WINDOW_RECT", Visible: true, Enabled: true,
                    InteractionState: "open", HitMin: origin, HitMax: origin + size * scale,
                    Strata: "MEDIUM"));
            CollectUiParityDraw("PaperDollFrame", "Frame", origin,
                new Vector2(PaperDollUiLaw.FrameWidth, PaperDollUiLaw.FrameHeight) * scale, "CharacterFrame",
                new("", 0, "IMGUI_HOST", "TOPLEFT", "CharacterFrame", "TOPLEFT", 0, 0,
                    ContentRect: panelClip, ClipRect: panelClip, ClipMask: "WINDOW_RECT",
                    Visible: _characterTab == 0, Enabled: true,
                    InteractionState: _characterTab == 0 ? "shown" : "hidden",
                    HitMin: origin, HitMax: origin + size * scale, Strata: "MEDIUM"));
            if (SkillFrameUiParityCaptureActive)
                CollectUiParityDraw("BenillaSkillFrame", "Frame", origin, size * scale,
                    "CharacterFrame", new("", 0, "IMGUI_HOST", "TOPLEFT",
                        "CharacterFrame", "TOPLEFT", 0, 0, ContentRect: panelClip,
                        ClipRect: panelClip, ClipMask: "WINDOW_RECT", Visible: _characterTab == 3,
                        Enabled: true, InteractionState: _characterTab == 3 ? "shown" : "hidden",
                        HitMin: origin, HitMax: origin + size * scale, Strata: "MEDIUM"));
        }
        // CharacterFramePortrait is a parent ARTWORK region; the active page's full-window
        // BACKGROUND slabs paint after it. Keeping that authored order lets their round aperture
        // contain the square live render target instead of letting its corners cover the chrome.
        DrawCharacterPortrait(dl, origin, scale, player, panelClip);
        if (_characterTab == 0) DrawPaperDollBackground(dl, origin, scale);
        else if (_characterTab == 1) DrawPetPaperDollBackground(dl, origin, scale);
        else if (_characterTab == 3) DrawSkillBackground(dl, origin, scale);
        else DrawCharacterGeneralBackground(dl, origin, scale);

        switch (_characterTab)
        {
            case 0: DrawPaperDollPage(dl, origin, scale, player); break;
            case 1:
                if (TryGetControlledPet(out WorldEntity pet))
                    DrawPetPaperDollPage(dl, origin, scale, player, pet);
                break;
            case 2: DrawReputationPage(dl, origin, scale, player); break;
            case 3: DrawSkillsPage(dl, origin, scale, player); break;
            case 4: DrawHonorPage(dl, origin, scale, player); break;
        }

        DrawCharacterHeader(dl, origin, scale, player, panelClip);
        DrawCharacterTabs(dl, origin, scale);
        if (_uiParityArmed && _uiParityPanel == "character-frame" &&
            !_shoppingTooltipParityCompletionPending)
            MarkUiParityFrameComplete();
        ImGui.End();
        if (_characterTab == 2) DrawReputationDetail(origin, scale, player);
    }

    private void DrawPaperDollBackground(ImDrawListPtr dl, Vector2 p, float s)
    {
        Vector4 panelClip = new(p.X, p.Y, p.X + PaperDollUiLaw.FrameWidth * s,
            p.Y + PaperDollUiLaw.FrameHeight * s);
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
                    new(region.Path, 0xffffffff, "BACKGROUND:OVER_PORTRAIT", "TOPLEFT",
                        "PaperDollFrame", "TOPLEFT", region.Offset.X, -region.Offset.Y,
                        TexCoords: "0|0|1|1", ClipRect: panelClip, BlendMode: "BLEND",
                        Visible: true, Strata: "MEDIUM"));
        }
    }

    private void DrawSkillBackground(ImDrawListPtr dl, Vector2 p, float s)
    {
        (string Element, string Path, Vector2 Offset, Vector2 Size)[] regions =
        [
            ("BenillaSkillFrame/BackgroundTopLeft", @"Interface\PaperDollInfoFrame\UI-Character-General-TopLeft", new(2, 1), new(256, 256)),
            ("BenillaSkillFrame/BackgroundTopRight", @"Interface\PaperDollInfoFrame\UI-Character-General-TopRight", new(258, 1), new(128, 256)),
            ("BenillaSkillFrame/BackgroundBottomLeft", @"Interface\PaperDollInfoFrame\SkillFrame-BotLeft", new(2, 255), new(256, 256)),
            ("BenillaSkillFrame/BackgroundBottomRight", @"Interface\PaperDollInfoFrame\SkillFrame-BotRight", new(258, 255), new(128, 256)),
        ];
        Vector4 clip = new(p.X, p.Y, p.X + PaperDollUiLaw.FrameWidth * s,
            p.Y + PaperDollUiLaw.FrameHeight * s);
        foreach (var region in regions)
        {
            Vector2 min = p + region.Offset * s;
            DrawArt(dl, region.Path, min, region.Size, s);
            if (SkillFrameUiParityCaptureActive)
                CollectUiParityDraw(region.Element, "Texture", min, region.Size * s,
                    "BenillaSkillFrame", new(region.Path, 0xffffffff, "BACKGROUND",
                        "TOPLEFT", "BenillaSkillFrame", "TOPLEFT", region.Offset.X,
                        -region.Offset.Y, TexCoords: "0|0|1|1", ClipRect: clip,
                        BlendMode: "BLEND", Visible: true, Strata: "MEDIUM"));
        }
    }

    private void DrawCharacterGeneralBackground(ImDrawListPtr dl, Vector2 p, float s)
    {
        DrawArt(dl, @"Interface\PaperDollInfoFrame\UI-Character-General-TopLeft", p + new Vector2(2, 1) * s, new(256), s);
        DrawArt(dl, @"Interface\PaperDollInfoFrame\UI-Character-General-TopRight", p + new Vector2(258, 1) * s, new(128, 256), s);
        DrawArt(dl, @"Interface\PaperDollInfoFrame\UI-Character-General-BottomLeft", p + new Vector2(2, 257) * s, new(256), s);
        DrawArt(dl, @"Interface\PaperDollInfoFrame\UI-Character-General-BottomRight", p + new Vector2(258, 257) * s, new(128, 256), s);
    }

    private void DrawPetPaperDollBackground(ImDrawListPtr dl, Vector2 p, float s)
    {
        foreach (PetPaperDollUiLaw.ArtSeat seat in PetPaperDollUiLaw.BackgroundArt)
            DrawArt(dl, seat.Path, p + seat.Rect.Min * s, seat.Rect.Size, s);
    }

    private void DrawCharacterPortrait(ImDrawListPtr dl, Vector2 p, float s, WorldEntity player,
        Vector4 panelClip)
    {
        PaperDollUiLaw.LogicalRect logical = PaperDollUiLaw.PortraitRect;
        Vector2 min = p + new Vector2(logical.X, logical.Y) * s;
        Vector2 size = new Vector2(logical.Width, logical.Height) * s;
        bool liveTarget = _playerPortrait is not null && PlayerPortraitCurrent && !_freeView;
        if (CharacterPageUiParityCaptureActive)
            CollectUiParityDraw("CharacterFramePortrait", "PlayerModel", min, size, "CharacterFrame",
                new("", 0xffffffff, "ARTWORK:BEHIND_PAGE_BACKGROUND", "TOPLEFT",
                    "CharacterFrame", "TOPLEFT", logical.X, -logical.Y,
                    TexCoords: "0|1|1|0", ContentRect: new Vector4(min.X, min.Y,
                        min.X + size.X, min.Y + size.Y), ClipRect: panelClip,
                    ClipMask: "AUTHORED_ROUND_APERTURE_OVERLAY", BlendMode: "BLEND",
                    Visible: true, InteractionState: liveTarget ? "live-player-model" : "unit-fallback",
                    Strata: "MEDIUM"));
        // The round copy: this frame's aperture is the authored round overlay in
        // BOTH modes, so it must never be handed the square bake. In free view
        // the streamed-body chain below owns the face instead.
        uint portrait = RoundAperturePortrait(_playerPortrait,
            PlayerPortraitCurrent && !_freeView);
        if (portrait != 0)
            dl.AddImage((nint)portrait, min, min + size,
                new Vector2(0, 1), new Vector2(1, 0), 0xffffffff);
        else
            DrawUnitPortraitImage(dl, player, min, size.X, 0, true);
    }

    private void DrawCharacterHeader(ImDrawListPtr dl, Vector2 p, float s, WorldEntity player,
        Vector4 panelClip)
    {
        // The sheet shows whoever is being DRIVEN. PlayerName is the session
        // character, which kept the owner's name over a possessed bot's page.
        WorldEntity? pet = null;
        bool petPage = _characterTab == 1 && TryGetControlledPet(out pet);
        string name = petPage ? ResolveCreatureOrPetName(pet!, "Pet") :
            ControlledGuid == LocalPlayerGuid ? _net?.PlayerName ?? "" :
            ResolveUnitName(ControlledGuid);
        if (petPage)
            GameText.DrawCentered(dl, PetPaperDollUiLaw.PetNameFont, name,
                PetPaperDollUiLaw.PetNameCenter(p, s), s);
        else
        {
            // CharacterNameText inherits GameFontNormal but CharacterFrame.xml overrides its
            // <Color> to white (1,1,1) - the name is white, not GameFontNormal's default gold.
            GameText.DrawCentered(dl, "GameFontNormal", name,
                p + new Vector2(198, 24) * s, s, 0xffffffff);
        }
        var bytes = player.Fields.Bytes0;
        string level = petPage
            ? PetPaperDollUiLaw.LevelText(pet!.Level, PetFamilyName(pet))
            : $"Level {player.Level} {RaceName(bytes.Race)} {ClassName(bytes.Class)}";
        if (petPage)
            GameText.DrawCentered(dl, PetPaperDollUiLaw.PetLevelFont, level,
                PetPaperDollUiLaw.PetLevelCenter(p, s), s);
        else
            // CharacterLevelText inherits GameFontNormalSmall; TOP of CharacterNameText BOTTOM -6.
            GameText.DrawCentered(dl, "GameFontNormalSmall", level,
                p + new Vector2(198, 41) * s, s);

        Vector2 close = p + new Vector2(324, 9) * s;
        DrawImageButton(dl, "##char-close", close, new Vector2(32) * s,
            @"Interface\Buttons\UI-Panel-MinimizeButton-Up",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Down",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Highlight");
        bool closeHovered = ImGui.IsItemHovered();
        bool closeActive = ImGui.IsItemActive();
        if (CharacterPageUiParityCaptureActive)
        {
            CollectUiParityDraw("CharacterNameText", "FontString", p + new Vector2(48, 18) * s,
                new Vector2(300, 12) * s, "CharacterNameFrame",
                new("", 0xffffffff, "ARTWORK", "CENTER", "CharacterNameFrame", "CENTER", 0, 0,
                    @"Fonts\FRIZQT__.TTF", 12, ContentRect: new Vector4(
                        p.X + 48 * s, p.Y + 18 * s, p.X + 348 * s, p.Y + 30 * s),
                    ClipRect: panelClip, Strata: "MEDIUM"));
            float levelWidth = GameText.MeasureWidth("GameFontNormalSmall", level, s);
            float levelHeight = GameText.EmPixels("GameFontNormalSmall", s);
            Vector2 levelMin = p + new Vector2(198, 41) * s -
                new Vector2(levelWidth, levelHeight) * .5f;
            CollectUiParityDraw("CharacterLevelText", "FontString", levelMin,
                new Vector2(levelWidth, levelHeight), "CharacterFrame",
                new("", 0xff00d1ff, "ARTWORK", "TOP", "CharacterNameText", "BOTTOM", 0, -6,
                    @"Fonts\FRIZQT__.TTF", 10, ClipRect: panelClip, Strata: "MEDIUM"));
            string closeState = closeActive ? "pushed" : closeHovered ? "highlighted" : "normal";
            CollectUiParityDraw("CharacterFrameCloseButton", "Button", close, new Vector2(32) * s,
                "CharacterFrame", new("", 0, "IMGUI_HIT_TARGET", "CENTER", "CharacterFrame",
                    "TOPRIGHT", -44, -25, ClipRect: panelClip, Enabled: true,
                    InteractionState: closeState, HitMin: close, HitMax: close + new Vector2(32) * s,
                    Strata: "MEDIUM+1"));
            string closeTexture = closeActive
                ? @"Interface\Buttons\UI-Panel-MinimizeButton-Down"
                : @"Interface\Buttons\UI-Panel-MinimizeButton-Up";
            CollectUiParityDraw("CharacterFrameCloseButton/" +
                    (closeActive ? "PushedTexture" : "NormalTexture"),
                closeActive ? "PushedTexture" : "NormalTexture", close,
                new Vector2(32) * s, "CharacterFrameCloseButton",
                new(closeTexture, 0xffffffff, "ARTWORK",
                    "CENTER", "CharacterFrameCloseButton", "CENTER", 0, 0,
                    ClipRect: panelClip, BlendMode: "BLEND", Strata: "MEDIUM+1"));
            if (closeHovered)
                CollectUiParityDraw("CharacterFrameCloseButton/HighlightTexture", "HighlightTexture",
                    close, new Vector2(32) * s, "CharacterFrameCloseButton",
                    new(@"Interface\Buttons\UI-Panel-MinimizeButton-Highlight", 0xffffffff,
                        "HIGHLIGHT", "CENTER", "CharacterFrameCloseButton", "CENTER", 0, 0,
                        ClipRect: panelClip, BlendMode: "ADD", InteractionState: "highlighted",
                        Strata: "MEDIUM+1"));
        }
        if (ImGui.IsItemClicked()) SetCharacterPageOpen(false);
    }

    private string? PetFamilyName(WorldEntity pet)
    {
        if (_creatureQueryRecords.TryGetValue(pet.Entry, out CreatureQueryInfo? query) &&
            query is not null && _creatureFamilies?.TryGet(query.PetFamily,
                out CreatureFamilyInfo family) == true)
            return family.Name;
        return null;
    }

    private void DrawPetPaperDollPage(ImDrawListPtr dl, Vector2 p, float s,
        WorldEntity player, WorldEntity pet)
    {
        PetPaperDollUiLaw.LogicalRect model = PetPaperDollUiLaw.Model;
        Vector2 modelMin = p + model.Min * s;
        Vector2 modelSize = model.Size * s;
        if (_petPaperDollUsable && _petPaperDoll is not null)
            dl.AddImage((nint)_petPaperDoll.TextureHandle, modelMin, modelMin + modelSize,
                PetPaperDollUiLaw.ModelUvMin, PetPaperDollUiLaw.ModelUvMax);

        DrawPetPaperDollRotation(dl, p, s, true);
        DrawPetPaperDollRotation(dl, p, s, false);

        uint stat = _gameplayArt?.Handle(PetPaperDollUiLaw.StatBackgroundPath) ?? 0;
        if (stat != 0)
        {
            foreach (bool right in new[] { false, true })
            {
                PetPaperDollUiLaw.LogicalRect plate =
                    PetPaperDollUiLaw.AttributePlate(right);
                Vector2 plateMin = p + plate.Min * s;
                dl.AddImage((nint)stat, plateMin, plateMin + plate.Size * s,
                    Vector2.Zero, PetPaperDollUiLaw.StatBackgroundUvMax);
            }
        }

        string[] leftLabels = ["Strength:", "Agility:", "Stamina:", "Intellect:", "Spirit:"];
        Vector4 petPanelClip = PetPaperDollUiLaw.PanelClip(p, s);
        for (int i = 0; i < PetPaperDollUiLaw.StatRows; i++)
        {
            DrawPetPaperDollStat(dl, p, s, false, i, leftLabels[i],
                pet.Fields.Stat(i).ToString());
            PetPaperDollUiLaw.LogicalRect row = PetPaperDollUiLaw.StatRow(false, i);
            DrawCharacterTooltipHit($"PetStatFrame{i + 1}", p + row.Min * s, row.Size * s,
                petPanelClip, "PetAttributesFrame",
                PaperDollUiLaw.PrimaryStatTooltip(PetPaperDollUiLaw.StatNames[i],
                    pet.Fields.Stat(i), pet.Fields.StatPositive(i),
                    pet.Fields.StatNegative(i)),
                PaperDollUiLaw.StatSubtexts[i]);
        }
        string damage = $"{pet.Fields.MinDamage:0.#}-{pet.Fields.MaxDamage:0.#}";
        string[] rightLabels = ["Attack:", "Power:", "Damage:", "Defense:", "Armor:"];
        string[] rightValues =
        [
            PetPaperDollUiLaw.CreatureSkill(pet.Level).ToString(),
            pet.Fields.AttackPower.ToString(), damage,
            PetPaperDollUiLaw.CreatureSkill(pet.Level).ToString(),
            pet.Fields.Resistance(0).ToString()
        ];
        for (int i = 0; i < PetPaperDollUiLaw.StatRows; i++)
        {
            DrawPetPaperDollStat(dl, p, s, true, i, rightLabels[i], rightValues[i]);
            PetPaperDollUiLaw.LogicalRect row = PetPaperDollUiLaw.StatRow(true, i);
            string title;
            string[] lines;
            if (i == 0) { title = "Attack Rating"; lines = []; }
            else if (i == 1)
            {
                title = PaperDollUiLaw.ModifierTooltip("Melee Attack Power",
                    pet.Fields.AttackPower, pet.Fields.AttackPowerPositive,
                    pet.Fields.AttackPowerNegative);
                lines = [$"Increases damage with melee weapons by " +
                    $"{Math.Max(pet.Fields.AttackPower, 0) / 14f:0.0} damage per second."];
            }
            else if (i == 2)
            {
                float attackSpeed = pet.Fields.MainAttackTime / 1000f;
                PaperDollUiLaw.DamageTooltipData breakdown = PaperDollUiLaw.DamageTooltip(
                    pet.Fields.MinDamage, pet.Fields.MaxDamage, 0, 0, 1, attackSpeed);
                title = "Main Hand";
                lines = CharacterDamageTooltipLines(breakdown);
            }
            else if (i == 3) { title = "Defense Rating"; lines = []; }
            else
            {
                int armor = pet.Fields.Resistance(0);
                title = PaperDollUiLaw.ModifierTooltip("Armor", armor,
                    pet.Fields.ResistancePositive(0), pet.Fields.ResistanceNegative(0));
                lines = [PaperDollUiLaw.ArmorTooltipSubtext(armor, pet.Level)];
            }
            DrawCharacterTooltipHit(i switch
                {
                    0 => "PetAttackFrame", 1 => "PetAttackPowerFrame",
                    2 => "PetDamageFrame", 3 => "PetDefenseFrame", _ => "PetArmorFrame"
                }, p + row.Min * s, row.Size * s, petPanelClip, "PetAttributesFrame",
                title, lines);
        }

        DrawPetPaperDollResistances(dl, p, s, pet.Fields);
        DrawPetPaperDollExperience(dl, p, s, pet.Fields);

        byte loyalty = pet.Fields.PetLoyaltyLevel;
        string loyaltyName = PetPaperDollUiLaw.LoyaltyName(loyalty);
        if (loyaltyName.Length > 0)
            GameText.DrawCentered(dl, PetPaperDollUiLaw.PetLoyaltyFont, loyaltyName,
                PetPaperDollUiLaw.PetLoyaltyCenter(p, s), s);

        bool hunterPet = player.Fields.Bytes0.Class == 3 && pet.Fields.PetNumber != 0;
        if (hunterPet)
        {
            (ushort total, ushort spent) =
                PetPaperDollUiLaw.TrainingPoints(pet.Fields.PetTrainingPoints);
            string trainingValue = ((int)total - spent).ToString();
            float trainingValueWidth = GameText.MeasureWidth(
                PetPaperDollUiLaw.TrainingValueFont, trainingValue, s);
            GameText.DrawRightAligned(dl, PetPaperDollUiLaw.TrainingLabelFont,
                "Training Points:", PetPaperDollUiLaw.TrainingLabelRightTop(
                    p, s, trainingValueWidth), s);
            GameText.DrawRightAligned(dl, PetPaperDollUiLaw.TrainingValueFont, trainingValue,
                PetPaperDollUiLaw.TrainingValueTopRight(p, s), s);
            DrawPetDietAffordance(dl, p, s, pet);
        }

        PetPaperDollUiLaw.LogicalRect close = PetPaperDollUiLaw.Close;
        DrawImageButton(dl, "##pet-paper-doll-close", p + close.Min * s, close.Size * s,
            @"Interface\Buttons\UI-Panel-Button-Up",
            @"Interface\Buttons\UI-Panel-Button-Down",
            @"Interface\Buttons\UI-Panel-Button-Highlight");
        GameText.DrawCentered(dl, ImGui.IsItemHovered() ? "GameFontHighlight" : "GameFontNormal",
            "Close", p + (close.Min + close.Size * .5f) * s, s);
        if (ImGui.IsItemClicked()) SetCharacterPageOpen(false);
    }

    private void DrawPetPaperDollRotation(ImDrawListPtr dl, Vector2 p, float s, bool left)
    {
        PetPaperDollUiLaw.LogicalRect logical = left
            ? PetPaperDollUiLaw.RotateLeft : PetPaperDollUiLaw.RotateRight;
        string stem = left ? "UI-RotationLeft-Button" : "UI-RotationRight-Button";
        DrawImageButton(dl, left ? "##pet-doll-left" : "##pet-doll-right",
            p + logical.Min * s, logical.Size * s,
            $@"Interface\Buttons\{stem}-Up", $@"Interface\Buttons\{stem}-Down",
            @"Interface\Buttons\ButtonHilight-Round");
        bool changed = false;
        if (ImGui.IsItemClicked())
        {
            _petPaperDollRotation = PaperDollUiLaw.ClickFacing(_petPaperDollRotation, left);
            PlayCharacterTransition(PaperDollUiLaw.RotateTapSound);
            changed = true;
        }
        if (ImGui.IsItemActive())
        {
            _petPaperDollRotation = PaperDollUiLaw.HeldFacing(_petPaperDollRotation, left,
                ImGui.GetIO().DeltaTime);
            changed = true;
        }
        if (changed) _petPaperDollDirty = true;
    }

    private static void DrawPetPaperDollStat(ImDrawListPtr dl, Vector2 p, float s,
        bool right, int row, string label, string value)
    {
        PetPaperDollUiLaw.LogicalRect logical = PetPaperDollUiLaw.StatRow(right, row);
        Vector2 min = p + logical.Min * s;
        GameText.Draw(dl, PetPaperDollUiLaw.StatLabelFont, label,
            PetPaperDollUiLaw.StatLabelMin(min, s), s);
        GameText.DrawRightAligned(dl, PetPaperDollUiLaw.StatValueFont, value,
            PetPaperDollUiLaw.StatValueRightTop(min, s), s);
    }

    private void DrawPetPaperDollResistances(ImDrawListPtr dl, Vector2 p, float s,
        ObjectFields fields)
    {
        uint icons = _gameplayArt?.Handle(PetPaperDollUiLaw.ResistanceIconsPath) ?? 0;
        for (int i = 0; i < 5; i++)
        {
            PetPaperDollUiLaw.LogicalRect logical = PetPaperDollUiLaw.ResistanceRow(i);
            Vector2 min = p + logical.Min * s;
            if (icons != 0)
                dl.AddImage((nint)icons, min, min + logical.Size * s,
                    PetPaperDollUiLaw.ResistanceUvMin(i),
                    PetPaperDollUiLaw.ResistanceUvMax(i));
            int school = PetPaperDollUiLaw.ResistanceSchoolIds[i];
            int value = fields.Resistance(school);
            uint color = value < 0 ? 0xff3333ff : value > 0 ? 0xff33ff33 : 0xffffffff;
            GameText.DrawCentered(dl, PetPaperDollUiLaw.ResistanceFont, value.ToString(),
                PetPaperDollUiLaw.ResistanceTextCenter(min, s), s, color);
            DrawCharacterTooltipHit($"PetMagicResFrame{i + 1}", min, logical.Size * s,
                PetPaperDollUiLaw.PanelClip(p, s), "PetResistanceFrame",
                PetPaperDollUiLaw.ResistanceTooltip(
                    PetPaperDollUiLaw.ResistanceNames[i], value,
                    fields.ResistancePositive(school),
                    fields.ResistanceNegative(school)));
        }
    }

    private void DrawPetPaperDollExperience(ImDrawListPtr dl, Vector2 p, float s,
        ObjectFields fields)
    {
        PetPaperDollUiLaw.LogicalRect logical = PetPaperDollUiLaw.Experience;
        Vector2 min = p + logical.Min * s;
        DrawVanillaStatusBar(dl, min, logical.Size * s,
            PetPaperDollUiLaw.ExperienceFraction(fields.PetExperience,
                fields.PetNextLevelExperience), PetPaperDollUiLaw.ExperienceColor);
        uint dwarf = _gameplayArt?.Handle(PetPaperDollUiLaw.ExperienceDwarfPath) ?? 0;
        if (dwarf != 0)
        {
            foreach (bool right in new[] { false, true })
            {
                PetPaperDollUiLaw.LogicalRect piece =
                    PetPaperDollUiLaw.ExperienceDwarfPiece(right);
                Vector2 pieceMin = min + piece.Min * s;
                dl.AddImage((nint)dwarf, pieceMin, pieceMin + piece.Size * s,
                    PetPaperDollUiLaw.ExperienceDwarfUvMin,
                    PetPaperDollUiLaw.ExperienceDwarfUvMax);
            }
        }
    }

    private void DrawPetDietAffordance(ImDrawListPtr dl, Vector2 p, float s, WorldEntity pet)
    {
        PetPaperDollUiLaw.LogicalRect logical = PetPaperDollUiLaw.Diet;
        Vector2 min = p + logical.Min * s;
        uint icon = _gameplayArt?.Handle(PetPaperDollUiLaw.DietPath) ?? 0;
        if (icon != 0)
            dl.AddImage((nint)icon, min, min + logical.Size * s,
                Vector2.Zero, PetPaperDollUiLaw.DietUvMax);
        ImGui.SetCursorScreenPos(min);
        ImGui.InvisibleButton("##pet-diet", logical.Size * s);
        if (!ImGui.IsItemHovered()) return;
        string diet = "";
        if (_creatureQueryRecords.TryGetValue(pet.Entry, out CreatureQueryInfo? query) &&
            query is not null)
            diet = _creatureFamilies?.Diet(query.PetFamily) ?? "";
        string text = diet.Length > 0 ? $"Diet: {diet}" : "Diet";
        PetPaperDollUiLaw.TooltipSeat tooltipSeat =
            PetPaperDollUiLaw.RightTooltipSeat(min, logical.Size * s);
        OfferPreservedSharedGameTooltipRenderer(
            new GameTooltipOwnerKey("pet-paper-doll-diet", pet.Guid),
            () =>
            {
                ImGui.SetNextWindowPos(tooltipSeat.Anchor, ImGuiCond.Always,
                    tooltipSeat.Pivot);
                DrawPetTokenTooltip(text);
            });
    }

    private void DrawPaperDollPage(ImDrawListPtr dl, Vector2 p, float s, WorldEntity player)
    {
        Vector4 panelClip = new(p.X, p.Y, p.X + PaperDollUiLaw.FrameWidth * s,
            p.Y + PaperDollUiLaw.FrameHeight * s);
        PaperDollUiLaw.LogicalRect model = PaperDollUiLaw.ModelRect;
        Vector2 modelMin = p + new Vector2(model.X, model.Y) * s;
        Vector2 modelSize = new Vector2(model.Width, model.Height) * s;
        bool modelVisible = _paperDoll is not null && _paperDoll.TextureHandle != 0;
        if (_uiParityArmed && _uiParityPanel == "character-frame")
            CollectUiParityDraw("CharacterModelFrameTexture", "PlayerModel", modelMin, modelSize,
                "CharacterModelFrame", new("", 0xffffffff, "ARTWORK", "TOPLEFT",
                    "PaperDollFrame", "TOPLEFT", model.X, -model.Y,
                    TexCoords: "0|1|1|0", ContentRect: new Vector4(modelMin.X, modelMin.Y,
                        modelMin.X + modelSize.X, modelMin.Y + modelSize.Y),
                    ClipRect: new Vector4(modelMin.X, modelMin.Y,
                        modelMin.X + modelSize.X, modelMin.Y + modelSize.Y),
                    ClipMask: "MODEL_PANE_RECT", BlendMode: "BLEND", Visible: modelVisible,
                    InteractionState: modelVisible ? "live-paper-doll" : "not-ready",
                    Strata: "MEDIUM"));
        if (modelVisible)
            dl.AddImage((nint)_paperDoll!.TextureHandle, modelMin, modelMin + modelSize,
                new Vector2(0, 1), new Vector2(1, 0));

        ImGui.SetCursorScreenPos(modelMin);
        ImGui.InvisibleButton("##paper-model-drop", modelSize,
            ImGuiButtonFlags.MouseButtonLeft);
        bool modelHovered = ImGui.IsItemHovered();
        bool modelActive = ImGui.IsItemActive();
        if (_uiParityArmed && _uiParityPanel == "character-frame")
            CollectUiParityDraw("CharacterModelFrame", "Button", modelMin, modelSize,
                "PaperDollFrame", new("", 0, "IMGUI_HIT_TARGET", "TOPLEFT",
                    "PaperDollFrame", "TOPLEFT", model.X, -model.Y, ClipRect: panelClip,
                    Enabled: true, InteractionState: HasCarriedItem ? "cursor-drop-ready" :
                        modelActive ? "pressed" : modelHovered ? "hovered" : "normal",
                    HitMin: modelMin, HitMax: modelMin + modelSize, Strata: "MEDIUM"));
        if (ImGui.IsItemClicked(ImGuiMouseButton.Left) && HasCarriedItem)
            AutoEquipCarriedPaperDollItem();
        if (ImGui.BeginDragDropTarget())
        {
            ImGui.AcceptDragDropPayload("MSUI_INVENTORY_ITEM");
            if (ImGui.IsMouseReleased(ImGuiMouseButton.Left) && HasCarriedItem)
                AutoEquipCarriedPaperDollItem();
            ImGui.EndDragDropTarget();
        }

        DrawRotationButton(dl, p + new Vector2(65, 78) * s, true, s, panelClip);
        DrawRotationButton(dl, p + new Vector2(100, 78) * s, false, s, panelClip);

        for (int i = 0; i < LeftPaperDollSlots.Length; i++)
        {
            (int slot, string empty) = LeftPaperDollSlots[i];
            PaperDollUiLaw.LogicalRect rect = PaperDollUiLaw.EquipmentSlotRect(slot);
            DrawEquipmentSlot(dl, p + new Vector2(rect.X, rect.Y) * s, s, player, slot, empty,
                panelClip);
        }
        for (int i = 0; i < RightPaperDollSlots.Length; i++)
        {
            (int slot, string empty) = RightPaperDollSlots[i];
            PaperDollUiLaw.LogicalRect rect = PaperDollUiLaw.EquipmentSlotRect(slot);
            DrawEquipmentSlot(dl, p + new Vector2(rect.X, rect.Y) * s, s, player, slot, empty,
                panelClip);
        }
        for (int i = 0; i < WeaponPaperDollSlots.Length; i++)
        {
            // CharacterMainHandSlot is TOPLEFT to PaperDollFrame.BOTTOMLEFT
            // (122,127): in top-origin coordinates its top is 512-127=385.
            (int slot, string empty) = WeaponPaperDollSlots[i];
            PaperDollUiLaw.LogicalRect rect = PaperDollUiLaw.EquipmentSlotRect(slot);
            DrawEquipmentSlot(dl, p + new Vector2(rect.X, rect.Y) * s, s, player, slot, empty,
                panelClip);
        }

        PaperDollUiLaw.LogicalRect ammo = PaperDollUiLaw.AmmoHitRect;
        DrawAmmoSlot(dl, p + new Vector2(ammo.X, ammo.Y) * s, s, player, panelClip);

        DrawCharacterStats(dl, p, s, player);
        DrawResistances(dl, p, s, player.Fields);
    }

    private void DrawRotationButton(ImDrawListPtr dl, Vector2 min, bool left, float s,
        Vector4 panelClip)
    {
        string stem = left ? "UI-RotationLeft-Button" : "UI-RotationRight-Button";
        DrawImageButton(dl, left ? "##paper-left" : "##paper-right", min, new Vector2(35) * s,
            $@"Interface\Buttons\{stem}-Up", $@"Interface\Buttons\{stem}-Down",
            @"Interface\Buttons\ButtonHilight-Round");
        bool active = ImGui.IsItemActive();
        bool hovered = ImGui.IsItemHovered();
        if (_uiParityArmed && _uiParityPanel == "character-frame")
        {
            string button = left ? "CharacterModelFrameRotateLeftButton" :
                "CharacterModelFrameRotateRightButton";
            CollectUiParityDraw(button, "Button", min, new Vector2(35) * s,
                "CharacterModelFrame", new("", 0, "IMGUI_HIT_TARGET", "TOPLEFT",
                    left ? "CharacterModelFrame" : "CharacterModelFrameRotateLeftButton",
                    left ? "TOPLEFT" : "TOPRIGHT", 0, 0, ClipRect: panelClip,
                    Enabled: true, InteractionState: active ? "pushed" : hovered ? "highlighted" : "normal",
                    HitMin: min, HitMax: min + new Vector2(35) * s, Strata: "MEDIUM"));
            string texture = $@"Interface\Buttons\{stem}-{(active ? "Down" : "Up")}";
            CollectUiParityDraw(button + (active ? "/PushedTexture" : "/NormalTexture"),
                active ? "PushedTexture" : "NormalTexture", min, new Vector2(35) * s, button,
                new(texture, 0xffffffff, "ARTWORK", "CENTER", button, "CENTER", 0, 0,
                    ClipRect: panelClip, BlendMode: "BLEND", Strata: "MEDIUM"));
            if (hovered)
                CollectUiParityDraw(button + "/HighlightTexture", "HighlightTexture", min,
                    new Vector2(35) * s, button,
                    new(@"Interface\Buttons\ButtonHilight-Round", 0xffffffff, "HIGHLIGHT",
                        "CENTER", button, "CENTER", 0, 0, ClipRect: panelClip,
                        BlendMode: "ADD", Strata: "MEDIUM"));
        }
        bool changed = false;
        if (ImGui.IsItemClicked())
        {
            // Preserve MSUI's existing tap increment; held rotation and its sound were absent.
            _paperDollRotation = PaperDollUiLaw.ClickFacing(_paperDollRotation, left);
            PlayCharacterTransition(PaperDollUiLaw.RotateTapSound);
            changed = true;
        }
        if (ImGui.IsItemActive())
        {
            _paperDollRotation = PaperDollUiLaw.HeldFacing(_paperDollRotation, left,
                ImGui.GetIO().DeltaTime);
            changed = true;
        }
        if (changed) _paperDollDirty = true;
    }

    private void DrawEquipmentSlot(ImDrawListPtr dl, Vector2 min, float s, WorldEntity player,
        int slot, string emptySuffix, Vector4 panelClip)
    {
        Vector2 max = min + new Vector2(37) * s;
        string parityButton = PaperDollSlotElement(slot);
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
        bool locked = IsInventorySlotLocked(InventoryUiLaw.EquipmentContainer, slot);
        bool broken = instance is not null && PaperDollUiLaw.IsBroken(instance.Fields.ItemFlags,
            instance.Fields.ItemDurability, instance.Fields.ItemMaxDurability);
        ItemTemplate? carried = CarriedPaperDollTemplate();
        bool fits = carried is not null && PaperDollUiLaw.FitsEquipmentSlot(carried.InventoryType, slot);
        if (_uiParityArmed && _uiParityPanel == "character-frame")
            CollectUiParityDraw(parityButton + "IconTexture", "Texture", min, max - min,
                parityButton, new(art, PaperDollUiLaw.IconTint(locked, broken), "BACKGROUND",
                    "CENTER", parityButton, "CENTER", 0, 0, TexCoords: "0|0|1|1",
                    ContentRect: new Vector4(min.X, min.Y, max.X, max.Y), ClipRect: panelClip,
                    BlendMode: "BLEND", Visible: icon != 0,
                    InteractionState: locked ? "locked" : broken ? "broken" :
                        instance is null ? "empty" : "equipped", Strata: "MEDIUM"));
        if (icon != 0) dl.AddImage((nint)icon, min, max, Vector2.Zero, Vector2.One,
            PaperDollUiLaw.IconTint(locked, broken));

        ImGui.SetCursorScreenPos(min);
        ImGui.InvisibleButton($"##equip-{slot}", max - min,
            ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);
        bool hovered = ImGui.IsItemHovered();
        bool repairReleased = _vendorRepairMode && hovered &&
            ImGui.IsMouseReleased(ImGuiMouseButton.Left) && ImGui.IsItemDeactivated();
        bool leftClicked = !_vendorRepairMode && ImGui.IsItemClicked(ImGuiMouseButton.Left);
        bool rightClicked = !_vendorRepairMode && ImGui.IsItemClicked(ImGuiMouseButton.Right);
        bool dressUpClick = leftClicked && ImGui.GetIO().KeyCtrl && instance is not null;
        if (dressUpClick)
        {
            TryOnDressUp(instance!.Entry);
            leftClicked = rightClicked = false;
        }
        if (repairReleased) TryRepairMerchantItem(instance?.Guid ?? 0);
        if (_itemCastSpell != 0)
        {
            if (rightClicked)
            {
                CancelItemTargeting();
                leftClicked = rightClicked = false;
            }
            else if (leftClicked)
            {
                if (instance is not null) TryBindItemCast(instance, item, bindConfirmed: false);
                leftClicked = rightClicked = false;
            }
        }
        if (_enchantConfirmation is not null) leftClicked = rightClicked = false;
        PaperDollUiLaw.SlotClickAction action = PaperDollUiLaw.ClickAction(
            leftClicked, rightClicked, ImGui.GetIO().KeyShift, ImGui.GetIO().KeyCtrl);
        if (action == PaperDollUiLaw.SlotClickAction.PickupOrPlace)
        {
            PickupOrPlaceItem(InventoryUiLaw.EquipmentContainer, slot, guid);
            _paperDollDirty = true;
        }
        else if (action == PaperDollUiLaw.SlotClickAction.Use && instance is not null && item is not null)
            SendItemUse(InventoryUiLaw.PlayerInventoryBag, (byte)slot, instance, item);
        if (!dressUpClick && !_vendorRepairMode && _itemCastSpell == 0 && _enchantConfirmation is null)
            HandleInventoryDrag(InventoryUiLaw.EquipmentContainer, slot, guid, item);

        if (_uiParityArmed && _uiParityPanel == "character-frame")
            CollectUiParityDraw(parityButton, "Button", min, max - min, "PaperDollFrame",
                new("", 0, "IMGUI_HIT_TARGET", "ABSOLUTE", "PaperDollFrame", "TOPLEFT",
                    (min.X - _uiParityOrigin.X) / s, -((min.Y - _uiParityOrigin.Y) / s),
                    ClipRect: panelClip, Enabled: true,
                    InteractionState: locked ? "locked" : ImGui.IsItemActive() ? "pushed" :
                        hovered ? "highlighted" : fits ? "cursor-fits" : broken ? "broken" : "normal",
                    HitMin: min, HitMax: max, Strata: "MEDIUM"));
        if (hovered)
        {
            if (_vendorRepairMode) DrawBagHoverCursor("Repair");
            var tooltipOwner = new GameTooltipOwnerKey(
                "item:character-paper-doll", (ulong)slot);
            if (item is not null)
            {
                ItemTooltipBodySnapshot body = PrepareItemTooltipBodySnapshot(item,
                    instance?.Fields.ItemStackCount ?? 1,
                    instance?.Fields.ItemDurability ?? 0,
                    instance?.Fields.ItemMaxDurability ?? 0,
                    instanceFlags: instance?.Fields.ItemFlags,
                    liveInstance: instance);
                body = AppendVendorMoneyRow(body, item,
                    instance?.Fields.ItemStackCount ?? 1, instance);
                InventoryUiLaw.TooltipSeat tooltipSeat = InventoryUiLaw.ItemTooltipSeat(
                    min, max, ImGui.GetIO().DisplaySize.X);
                OfferPreparedItemTooltip(tooltipOwner, body, tooltipSeat.Position,
                    nextWindowPivot: tooltipSeat.Pivot);
            }
            else
            {
                string tooltipText = PaperDollUiLaw.EquipmentSlotLabel(slot);
                OfferPreservedSharedGameTooltipRenderer(tooltipOwner, () =>
                {
                    ImGui.BeginTooltip();
                    ImGui.TextUnformatted(tooltipText);
                    ImGui.EndTooltip();
                });
            }
        }

        uint ring = _gameplayArt?.Handle(@"Interface\Buttons\UI-Quickslot2") ?? 0;
        if (ring != 0)
        {
            Vector2 center = (min + max) * 0.5f + new Vector2(0, -s);
            Vector2 half = new(32f * s);
            if (_uiParityArmed && _uiParityPanel == "character-frame")
                CollectUiParityDraw(parityButton + "NormalTexture", "NormalTexture",
                    center - half, half * 2, parityButton,
                    new(@"Interface\Buttons\UI-Quickslot2",
                        PaperDollUiLaw.RingTint(fits, broken), "ARTWORK", "CENTER",
                        parityButton, "CENTER", 0, -1, TexCoords: "0|0|1|1",
                        ClipRect: panelClip, BlendMode: "BLEND", Visible: true,
                        InteractionState: fits ? "cursor-fits" : broken ? "broken" : "normal",
                        Strata: "MEDIUM"));
            dl.AddImage((nint)ring, center - half, center + half, Vector2.Zero, Vector2.One,
                PaperDollUiLaw.RingTint(fits, broken));
        }
        if (ImGui.IsItemActive())
        {
            uint depress = _gameplayArt?.Handle(@"Interface\Buttons\UI-Quickslot-Depress") ?? 0;
            if (depress != 0)
            {
                if (CharacterPageUiParityCaptureActive)
                    CollectUiParityDraw(parityButton + "PushedTexture", "PushedTexture", min,
                        max - min, parityButton,
                        new(@"Interface\Buttons\UI-Quickslot-Depress", 0xffffffff, "ARTWORK",
                            "CENTER", parityButton, "CENTER", 0, 0, ClipRect: panelClip,
                            BlendMode: "BLEND", InteractionState: "pushed", Strata: "MEDIUM"));
                dl.AddImage((nint)depress, min, max);
            }
        }
        if (hovered)
        {
            uint highlight = _gameplayArt?.AdditiveHandle(@"Interface\Buttons\ButtonHilight-Square") ?? 0;
            if (highlight != 0)
            {
                if (_uiParityArmed && _uiParityPanel == "character-frame")
                    CollectUiParityDraw(parityButton + "HighlightTexture", "HighlightTexture", min,
                        max - min, parityButton,
                        new(@"Interface\Buttons\ButtonHilight-Square", 0xffffffff, "HIGHLIGHT",
                            "CENTER", parityButton, "CENTER", 0, 0, ClipRect: panelClip,
                            BlendMode: "ADD", InteractionState: "highlighted", Strata: "MEDIUM"));
                dl.AddImage((nint)highlight, min, max);
            }
        }
        uint count = instance?.Fields.ItemStackCount ?? 0;
        if (count > 1)
        {
            string countText = count.ToString();
            float countWidth = GameText.MeasureWidth("NumberFontNormal", countText, s);
            float countTop = max.Y - GameText.EmPixels("NumberFontNormal", s) - 2f * s;
            if (CharacterPageUiParityCaptureActive)
                CollectUiParityDraw(parityButton + "Count", "FontString",
                    new Vector2(max.X - 5f * s - countWidth, countTop),
                    new Vector2(countWidth, GameText.EmPixels("NumberFontNormal", s)), parityButton,
                    new("", 0xffffffff, "OVERLAY", "BOTTOMRIGHT", parityButton, "BOTTOMRIGHT",
                        -5, 2, @"Fonts\ARIALN.TTF", 14, ClipRect: panelClip,
                        Visible: true, InteractionState: "stack-count", Strata: "MEDIUM"));
            GameText.DrawRightAligned(dl, "NumberFontNormal", count.ToString(),
                new Vector2(max.X - 5f * s,
                    max.Y - GameText.EmPixels("NumberFontNormal", s) - 2f * s), s);
        }
    }

    private static string PaperDollSlotElement(int slot) => slot switch
    {
        0 => "CharacterHeadSlot",
        1 => "CharacterNeckSlot",
        2 => "CharacterShoulderSlot",
        3 => "CharacterShirtSlot",
        4 => "CharacterChestSlot",
        5 => "CharacterWaistSlot",
        6 => "CharacterLegsSlot",
        7 => "CharacterFeetSlot",
        8 => "CharacterWristSlot",
        9 => "CharacterHandsSlot",
        10 => "CharacterFinger0Slot",
        11 => "CharacterFinger1Slot",
        12 => "CharacterTrinket0Slot",
        13 => "CharacterTrinket1Slot",
        14 => "CharacterBackSlot",
        15 => "CharacterMainHandSlot",
        16 => "CharacterSecondaryHandSlot",
        17 => "CharacterRangedSlot",
        18 => "CharacterTabardSlot",
        _ => $"CharacterSlot{slot}",
    };

    private ItemTemplate? CarriedPaperDollTemplate()
    {
        if (_items is null || ResolveCarriedItem() is not { } carried) return null;
        _items.TryGet(carried.Entry, out ItemTemplate? item);
        return item;
    }

    private bool AutoEquipCarriedPaperDollItem(bool ammoOnly = false)
    {
        if (!CanAuthorControlledGameplay || _net is null ||
            ResolveCarriedItem() is not { } carried ||
            CarriedPaperDollTemplate() is not { } item) return false;
        if (!CanAuthorSessionInventory) return false;
        if (ammoOnly && !PaperDollUiLaw.IsAmmo(item.InventoryType)) return false;
        bool isAmmo = PaperDollUiLaw.IsAmmo(item.InventoryType);
        bool sent;
        if (isAmmo)
            sent = _net.SetAmmo(carried.Entry);
        else if (item.InventoryType != 0 &&
                 InventoryUiLaw.ToWire(_carriedContainer, _carriedSlot) is { } wire)
            sent = _net.AutoEquipItem(wire.Bag, wire.Slot);
        else return false;
        if (!sent) return false;
        // SET_AMMO selects the carried stack's entry; it does not move that stack.
        if (!isAmmo) AddPendingBagLock(_carriedContainer, _carriedSlot, ++_pendingBagOperation);
        ClearCarriedItem();
        _paperDollDirty = true;
        return true;
    }

    private void DrawAmmoSlot(ImDrawListPtr dl, Vector2 min, float s, WorldEntity player,
        Vector4 panelClip)
    {
        if (_gameplayArt is null || _items is null || _net is null) return;
        Vector2 max = min + new Vector2(27) * s;
        uint frame = _gameplayArt.Handle(@"Interface\PaperdollInfoFrame\UI-Character-AmmoSlot");
        if (frame != 0)
        {
            PaperDollUiLaw.LogicalRect hitLaw = PaperDollUiLaw.AmmoHitRect;
            PaperDollUiLaw.LogicalRect frameLaw = PaperDollUiLaw.AmmoBackgroundRect;
            Vector2 frameMin = min + new Vector2(frameLaw.X - hitLaw.X,
                frameLaw.Y - hitLaw.Y) * s;
            Vector2 frameSize = new Vector2(frameLaw.Width, frameLaw.Height) * s;
            if (_uiParityArmed && _uiParityPanel == "character-frame")
                CollectUiParityDraw("CharacterAmmoSlotBackground", "TextureUv", frameMin,
                    frameSize, "CharacterAmmoSlot",
                    new(@"Interface\PaperdollInfoFrame\UI-Character-AmmoSlot", 0xffffffff,
                        "BACKGROUND", "CENTER", "CharacterAmmoSlot", "CENTER", 0, 0,
                        TexCoords: "0|0|0.640625|0.640625", ClipRect: panelClip,
                        BlendMode: "BLEND", Visible: true, Strata: "MEDIUM"));
            dl.AddImage((nint)frame, frameMin, frameMin + frameSize,
                Vector2.Zero, new Vector2(.640625f));
        }

        uint entry = player.Fields.PlayerAmmoId;
        if (entry != 0) _items.Require(entry, 0, _net);
        _items.TryGet(entry, out ItemTemplate? ammo);
        uint count = entry == 0 ? 0 : CarriedAmmoCount(player, entry);
        uint icon = _gameplayArt.Handle(ammo?.IconPath ?? "");
        if (_uiParityArmed && _uiParityPanel == "character-frame")
            CollectUiParityDraw("CharacterAmmoSlotIconTexture", "Texture", min, max - min,
                "CharacterAmmoSlot", new(ammo?.IconPath ?? "", 0xffffffff, "ARTWORK", "CENTER",
                    "CharacterAmmoSlot", "CENTER", 0, 0, TexCoords: "0|0|1|1",
                    ContentRect: new Vector4(min.X, min.Y, max.X, max.Y), ClipRect: panelClip,
                    BlendMode: "BLEND", Visible: icon != 0,
                    InteractionState: ammo is null ? "empty" : "loaded-ammo", Strata: "MEDIUM"));
        if (icon != 0) dl.AddImage((nint)icon, min, max);

        ImGui.SetCursorScreenPos(min);
        ImGui.InvisibleButton("##paper-ammo", max - min, ImGuiButtonFlags.MouseButtonLeft);
        bool ammoHovered = ImGui.IsItemHovered();
        bool ammoActive = ImGui.IsItemActive();
        ItemTemplate? carried = CarriedPaperDollTemplate();
        string ammoState = carried is not null && PaperDollUiLaw.IsAmmo(carried.InventoryType)
            ? "cursor-ammo-ready" : carried is not null ? "cursor-rejected" :
                ammoActive ? "pushed" : ammoHovered ? "highlighted" : "normal";
        if (_uiParityArmed && _uiParityPanel == "character-frame")
            CollectUiParityDraw("CharacterAmmoSlot", "Button", min, max - min, "PaperDollFrame",
                new("", 0, "IMGUI_HIT_TARGET", "TOPLEFT", "PaperDollFrame", "TOPLEFT", 258,
                    -390, ClipRect: panelClip, Enabled: true, InteractionState: ammoState,
                    HitMin: min, HitMax: max, Strata: "MEDIUM"));
        if (ImGui.IsItemClicked(ImGuiMouseButton.Left)) AutoEquipCarriedPaperDollItem(ammoOnly: true);
        if (ImGui.BeginDragDropTarget())
        {
            ImGui.AcceptDragDropPayload("MSUI_INVENTORY_ITEM");
            if (ImGui.IsMouseReleased(ImGuiMouseButton.Left)) AutoEquipCarriedPaperDollItem(ammoOnly: true);
            ImGui.EndDragDropTarget();
        }
        if (ImGui.IsItemHovered())
        {
            var tooltipOwner = new GameTooltipOwnerKey("item:character-ammo", 0);
            if (ammo is not null)
            {
                ItemTooltipBodySnapshot body = PrepareItemTooltipBodySnapshot(ammo, count);
                OfferPreparedItemTooltip(tooltipOwner, body);
            }
            else
            {
                OfferPreservedSharedGameTooltipRenderer(tooltipOwner, () =>
                {
                    ImGui.BeginTooltip();
                    ImGui.TextUnformatted("Ammo");
                    ImGui.EndTooltip();
                });
            }
        }
        if (count > 1)
        {
            string countText = count.ToString();
            float countWidth = GameText.MeasureWidth("NumberFontNormal", countText, s);
            float countTop = max.Y - GameText.EmPixels("NumberFontNormal", s) - 2f * s;
            if (_uiParityArmed && _uiParityPanel == "character-frame")
                CollectUiParityDraw("CharacterAmmoSlotCount", "FontString",
                    new Vector2(max.X - s - countWidth, countTop),
                    new Vector2(countWidth, GameText.EmPixels("NumberFontNormal", s)),
                    "CharacterAmmoSlot", new("", 0xffffffff, "OVERLAY", "BOTTOMRIGHT",
                        "CharacterAmmoSlot", "BOTTOMRIGHT", -1, 2,
                        @"Fonts\ARIALN.TTF", 14, ClipRect: panelClip,
                        InteractionState: "total-ammo-count", Strata: "MEDIUM"));
            GameText.DrawRightAligned(dl, "NumberFontNormal", count.ToString(),
                new Vector2(max.X - s, max.Y - GameText.EmPixels("NumberFontNormal", s) - 2f * s), s);
        }

        if (frame != 0)
        {
            PaperDollUiLaw.LogicalRect hitLaw = PaperDollUiLaw.AmmoHitRect;
            PaperDollUiLaw.LogicalRect overlayLaw = PaperDollUiLaw.AmmoOverlayRect;
            Vector2 overlayMin = min + new Vector2(overlayLaw.X - hitLaw.X,
                overlayLaw.Y - hitLaw.Y) * s;
            Vector2 overlaySize = new Vector2(overlayLaw.Width, overlayLaw.Height) * s;
            if (_uiParityArmed && _uiParityPanel == "character-frame")
                CollectUiParityDraw("CharacterAmmoSlotOverlay", "TextureUv", overlayMin,
                    overlaySize, "CharacterAmmoSlot",
                    new(@"Interface\PaperdollInfoFrame\UI-Character-AmmoSlot", 0xffffffff,
                        "OVERLAY", "CENTER", "CharacterAmmoSlot", "CENTER", -22, 0,
                        TexCoords: "0.640625|0|1|0.640625", ClipRect: panelClip,
                        BlendMode: "BLEND", Visible: true, Strata: "MEDIUM"));
            dl.AddImage((nint)frame, overlayMin, overlayMin + overlaySize,
                new Vector2(.640625f, 0), new Vector2(1f, .640625f));
        }
    }

    private uint CarriedAmmoCount(WorldEntity player, uint entry)
    {
        uint count = 0;
        void Add(ulong guid)
        {
            if (guid != 0 && _entities.TryGet(guid, out WorldEntity item) && item.Entry == entry)
                count += Math.Max(1, item.Fields.ItemStackCount);
        }
        for (int slot = 0; slot < InventoryUiLaw.BackpackSlots; slot++) Add(player.Fields.PlayerBackpackSlot(slot));
        for (int bagIndex = 0; bagIndex < 4; bagIndex++)
        {
            ulong bagGuid = player.Fields.PlayerInventorySlot(19 + bagIndex);
            if (bagGuid == 0 || !_entities.TryGet(bagGuid, out WorldEntity bag)) continue;
            int slots = PaperDollUiLaw.ContainerSlotScanCount(bag.Fields.ContainerNumSlots);
            for (int slot = 0; slot < slots; slot++) Add(bag.Fields.ContainerSlot(slot));
        }
        return count;
    }

    private void DrawCharacterStats(ImDrawListPtr dl, Vector2 p, float s, WorldEntity player)
    {
        ObjectFields f = player.Fields;
        Vector2 basePos = p + new Vector2(67, 291) * s;
        Vector4 panelClip = new(p.X, p.Y, p.X + PaperDollUiLaw.FrameWidth * s,
            p.Y + PaperDollUiLaw.FrameHeight * s);
        uint bg = _gameplayArt?.Handle(@"Interface\PaperDollInfoFrame\UI-Character-StatBackground") ?? 0;
        if (bg != 0)
        {
            if (_uiParityArmed && _uiParityPanel == "character-frame")
            {
                CollectUiParityDraw("CharacterPlayerStatBackground", "TextureUv", basePos,
                    new Vector2(115, 85) * s, "CharacterAttributesFrame",
                    new(@"Interface\PaperDollInfoFrame\UI-Character-StatBackground", 0xffffffff,
                        "BACKGROUND", "TOPLEFT", "CharacterAttributesFrame", "TOPLEFT", 0, 0,
                        TexCoords: "0|0|0.8984375|0.609375", ClipRect: panelClip,
                        ClipMask: "PRESERVED_MSUI_COMPOSITE_PANEL", BlendMode: "BLEND",
                        Visible: true, Strata: "MEDIUM"));
                CollectUiParityDraw("CharacterAttackStatBackground", "TextureUv",
                    basePos + new Vector2(115, 0) * s, new Vector2(115, 85) * s,
                    "CharacterAttributesFrame",
                    new(@"Interface\PaperDollInfoFrame\UI-Character-StatBackground", 0xffffffff,
                        "BACKGROUND", "TOPLEFT", "CharacterAttributesFrame", "TOPLEFT", 115, 0,
                        TexCoords: "0|0|0.8984375|0.609375", ClipRect: panelClip,
                        ClipMask: "PRESERVED_MSUI_COMPOSITE_PANEL", BlendMode: "BLEND",
                        Visible: true, Strata: "MEDIUM"));
            }
            dl.AddImage((nint)bg, basePos, basePos + new Vector2(115, 85) * s,
                new Vector2(0, 0), new Vector2(0.8984375f, 0.609375f));
            dl.AddImage((nint)bg, basePos + new Vector2(115, 0) * s,
                basePos + new Vector2(230, 85) * s,
                new Vector2(0, 0), new Vector2(0.8984375f, 0.609375f));
        }

        // PaperDollFrame.lua SetStats appends ":" to SPELL_STATi_NAME; ARMOR_COLON is
        // "Armor:" (GlobalStrings.lua). The value is the same stat number either way.
        string[] labels = ["Strength:", "Agility:", "Stamina:", "Intellect:", "Spirit:"];
        for (int i = 0; i < 5; i++)
        {
            Vector2 rowMin = basePos + new Vector2(6, 3 + i * 13) * s;
            DrawStatRow(dl, rowMin, labels[i], f.Stat(i).ToString(), s,
                valueColor: PaperDollUiLaw.ModifierTextColor(f.StatPositive(i), f.StatNegative(i)));
            DrawCharacterStatTooltipHit($"CharacterStatFrame{i + 1}", rowMin,
                new Vector2(104, 13) * s, panelClip,
                PaperDollUiLaw.PrimaryStatTooltip(PaperDollUiLaw.StatNames[i], f.Stat(i),
                    f.StatPositive(i), f.StatNegative(i)), PaperDollUiLaw.StatSubtexts[i]);
        }
        Vector2 armorMin = basePos + new Vector2(6, 68) * s;
        DrawStatRow(dl, armorMin, "Armor:", f.Resistance(0).ToString(), s,
            valueColor: PaperDollUiLaw.ModifierTextColor(f.ResistancePositive(0),
                f.ResistanceNegative(0)));
        DrawCharacterStatTooltipHit("CharacterArmorFrame", armorMin, new Vector2(104, 13) * s,
            panelClip, PaperDollUiLaw.ModifierTooltip("Armor", f.Resistance(0),
                f.ResistancePositive(0), f.ResistanceNegative(0)),
            PaperDollUiLaw.ArmorTooltipSubtext(f.Resistance(0), f.Level));

        float speed = f.MainAttackTime > 0 ? f.MainAttackTime / 1000f : 0;
        string damage = f.MaxDamage > 0 ? $"{f.MinDamage:0.#}-{f.MaxDamage:0.#}" : "0-0";
        Vector2 attackMin = basePos + new Vector2(122, 2) * s;
        DrawStatRow(dl, attackMin, "Attack", speed > 0 ? speed.ToString("0.00") : "—", s);
        DrawCharacterStatTooltipHit("CharacterAttackFrame", attackMin, new Vector2(104, 13) * s,
            panelClip, "Attack Rating",
            "Your attack rating affects your chance to hit a target, and is based on the weapon " +
            "skill of the weapon you are currently wielding.");
        Vector2 attackPowerMin = basePos + new Vector2(127, 15) * s;
        DrawStatRow(dl, attackPowerMin, "Attack Power",
            f.AttackPower.ToString(), s, 99,
            PaperDollUiLaw.ModifierTextColor(f.AttackPowerPositive, f.AttackPowerNegative));
        DrawCharacterStatTooltipHit("CharacterAttackPowerFrame", attackPowerMin,
            new Vector2(99, 13) * s, panelClip,
            PaperDollUiLaw.ModifierTooltip("Melee Attack Power", f.AttackPower,
                f.AttackPowerPositive, f.AttackPowerNegative),
            $"Increases damage with melee weapons by {Math.Max(f.AttackPower, 0) / 14f:0.0} damage per second.");
        Vector2 damageMin = basePos + new Vector2(127, 28) * s;
        DrawStatRow(dl, damageMin, "Damage", damage, s, 99);
        PaperDollUiLaw.DamageTooltipData meleeDamage = PaperDollUiLaw.DamageTooltip(
            f.MinDamage, f.MaxDamage, f.DamageDonePositive(0), f.DamageDoneNegative(0),
            f.DamageDonePercent(0), speed);
        ItemTemplate? offhand = CharacterEquippedTemplate(f, 16);
        DrawCharacterStatTooltipHit("CharacterDamageFrame", damageMin, new Vector2(99, 13) * s,
            panelClip, "Main Hand", CharacterDamageTooltipLines(meleeDamage,
                offhand?.Class == 2
                    ? PaperDollUiLaw.DamageTooltip(f.MinOffhandDamage, f.MaxOffhandDamage,
                        f.DamageDonePositive(0), f.DamageDoneNegative(0),
                        f.DamageDonePercent(0), f.OffhandAttackTime > 0
                            ? f.OffhandAttackTime / 1000f : 0f)
                    : null));
        float rangedSpeed = f.RangedAttackTime > 0 ? f.RangedAttackTime / 1000f : 0;
        string rangedDamage = f.MaxRangedDamage > 0
            ? $"{f.MinRangedDamage:0.#}-{f.MaxRangedDamage:0.#}" : "—";
        Vector2 rangedMin = basePos + new Vector2(122, 47) * s;
        DrawStatRow(dl, rangedMin, "Ranged", rangedSpeed > 0 ? rangedSpeed.ToString("0.00") : "—", s);
        ItemTemplate? ranged = CharacterEquippedTemplate(f, 17);
        // This OnEnter is static in the frozen XML and remains active even while the displayed
        // ranged value is N/A. Only the AP and damage handlers are gated by an equipped weapon.
        DrawCharacterStatTooltipHit("CharacterRangedAttackFrame", rangedMin,
            new Vector2(104, 13) * s, panelClip, "Ranged Attack Rating",
            "Your attack rating affects your chance to hit a target, and is based on the weapon " +
            "skill of the weapon you are currently wielding.");
        Vector2 rangedPowerMin = basePos + new Vector2(127, 60) * s;
        DrawStatRow(dl, rangedPowerMin, "Ranged Power",
            f.RangedAttackPower.ToString(), s, 99,
            PaperDollUiLaw.ModifierTextColor(f.RangedAttackPowerPositive,
                f.RangedAttackPowerNegative));
        if (ranged is { Class: 2, Subclass: not 19 })
            DrawCharacterStatTooltipHit("CharacterRangedAttackPowerFrame", rangedPowerMin,
                new Vector2(99, 13) * s, panelClip,
                PaperDollUiLaw.ModifierTooltip("Ranged Attack Power", f.RangedAttackPower,
                    f.RangedAttackPowerPositive, f.RangedAttackPowerNegative),
                $"Increases damage with ranged weapons by {Math.Max(f.RangedAttackPower, 0) / 14f:0.0} damage per second.");
        Vector2 rangedDamageMin = basePos + new Vector2(127, 73) * s;
        DrawStatRow(dl, rangedDamageMin, "Damage", rangedDamage, s, 99);
        if (ranged is not null)
        {
            PaperDollUiLaw.DamageTooltipData rangedBreakdown = PaperDollUiLaw.DamageTooltip(
                f.MinRangedDamage, f.MaxRangedDamage, f.DamageDonePositive(0),
                f.DamageDoneNegative(0), f.DamageDonePercent(0), rangedSpeed);
            DrawCharacterStatTooltipHit("CharacterRangedDamageFrame", rangedDamageMin,
                new Vector2(99, 13) * s, panelClip, "Ranged",
                CharacterDamageTooltipLines(rangedBreakdown));
        }
    }

    private ItemTemplate? CharacterEquippedTemplate(ObjectFields fields, int slot)
    {
        ulong guid = fields.PlayerInventorySlot(slot);
        if (guid == 0 || _net is null || _items is null ||
            !_entities.TryGet(guid, out WorldEntity instance)) return null;
        _items.Require(instance.Entry, instance.Guid, _net);
        _items.TryGet(instance.Entry, out ItemTemplate? item);
        return item;
    }

    private static string[] CharacterDamageTooltipLines(PaperDollUiLaw.DamageTooltipData main,
        PaperDollUiLaw.DamageTooltipData? offhand = null)
    {
        var lines = new List<string>
        {
            $"Attack Speed: {main.AttackSpeed:0.00}",
            $"Damage: {main.Damage}",
            $"Damage per Second: {main.Dps:0.0}",
        };
        if (offhand is { } secondary)
        {
            lines.Add("Off Hand");
            lines.Add($"Attack Speed: {secondary.AttackSpeed:0.00}");
            lines.Add($"Damage: {secondary.Damage}");
            lines.Add($"Damage per Second: {secondary.Dps:0.0}");
        }
        return [.. lines];
    }

    private static void DrawStatRow(ImDrawListPtr dl, Vector2 p, string label, string value, float s,
        float width = 104, uint? valueColor = null)
    {
        GameText.Draw(dl, "GameFontNormalSmall", label, p, s);
        GameText.DrawRightAligned(dl, "GameFontHighlightSmall", value,
            new Vector2(p.X + width * s, p.Y), s, valueColor);
    }

    private void DrawCharacterStatTooltipHit(string element, Vector2 min, Vector2 size,
        Vector4 panelClip, string title, params string[] lines)
        => DrawCharacterTooltipHit(element, min, size, panelClip, "CharacterAttributesFrame",
            title, lines);

    private void DrawCharacterTooltipHit(string element, Vector2 min, Vector2 size,
        Vector4 panelClip, string parent, string title, params string[] lines)
    {
        ImGui.SetCursorScreenPos(min);
        ImGui.InvisibleButton($"##{element}-tooltip", size);
        bool hovered = ImGui.IsItemHovered();
        if (_uiParityArmed && _uiParityPanel == "character-frame")
            CollectUiParityDraw(element, "Frame", min, size, parent,
                new("", 0, "IMGUI_HIT_TARGET", "ABSOLUTE", parent, "TOPLEFT",
                    (min.X - _uiParityOrigin.X) / _uiParityLogicalScale,
                    -((min.Y - _uiParityOrigin.Y) / _uiParityLogicalScale),
                    ContentRect: new Vector4(min.X, min.Y, min.X + size.X, min.Y + size.Y),
                    ClipRect: panelClip,
                    Enabled: true, InteractionState: hovered ? "tooltip-hovered" : "normal",
                    HitMin: min, HitMax: min + size, Strata: "MEDIUM"));
        if (!hovered) return;
        string preparedTitle = title;
        ImmutableArray<string> preparedLines = lines
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToImmutableArray();
        float wrapWidth = PaperDollUiLaw.TooltipWrapWidth;
        var tooltipOwner = new GameTooltipOwnerKey($"paperdoll-stat:{element}", 0);
        PetPaperDollUiLaw.TooltipSeat? petTooltipSeat =
            parent.StartsWith("Pet", StringComparison.Ordinal)
                ? PetPaperDollUiLaw.RightTooltipSeat(min, size)
                : null;
        OfferPreservedSharedGameTooltipRenderer(tooltipOwner, () =>
        {
            if (petTooltipSeat is { } tooltipSeat)
                ImGui.SetNextWindowPos(tooltipSeat.Anchor, ImGuiCond.Always,
                    tooltipSeat.Pivot);
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(preparedTitle);
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + wrapWidth);
            foreach (string line in preparedLines)
                ImGui.TextColored(new Vector4(1f, .82f, 0f, 1f), line);
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        });
    }

    private void DrawResistances(ImDrawListPtr dl, Vector2 p, float s, ObjectFields f)
    {
        uint art = _gameplayArt?.Handle(@"Interface\PaperDollInfoFrame\UI-Character-ResistanceIcons") ?? 0;
        Vector4 panelClip = new(p.X, p.Y, p.X + PaperDollUiLaw.FrameWidth * s,
            p.Y + PaperDollUiLaw.FrameHeight * s);
        float[] tops = [0.2265625f, 0f, 0.11328125f, 0.33984375f, 0.453125f];
        int[] schools = [6, 2, 3, 4, 5];
        for (int i = 0; i < 5; i++)
        {
            // CharacterResistanceFrame is 32px wide and its TOPRIGHT, not its
            // TOPLEFT, is anchored at x=297. Its authored left edge is x=265.
            Vector2 min = p + new Vector2(265, 77 + i * 29) * s;
            if (_uiParityArmed && _uiParityPanel == "character-frame")
                CollectUiParityDraw($"MagicResFrame{i + 1}Icon", "TextureUv", min,
                    new Vector2(32, 29) * s, $"MagicResFrame{i + 1}",
                    new(@"Interface\PaperDollInfoFrame\UI-Character-ResistanceIcons", 0xffffffff,
                        "ARTWORK", "TOPLEFT", "PaperDollFrame", "TOPLEFT", 265,
                        -(77 + i * 29), TexCoords: $"0|{tops[i]:R}|1|{tops[i] + .11328125f:R}",
                        ClipRect: panelClip, BlendMode: "BLEND", Visible: art != 0,
                        InteractionState: f.ResistanceNegative(schools[i]) < 0 ? "negative" :
                            f.ResistancePositive(schools[i]) > 0 ? "positive" : "base",
                        Strata: "MEDIUM"));
            if (art != 0) dl.AddImage((nint)art, min, min + new Vector2(32, 29) * s,
                new Vector2(0, tops[i]), new Vector2(1, tops[i] + 0.11328125f));
            string v = f.Resistance(schools[i]).ToString();
            GameText.DrawCentered(dl, "GameFontHighlightSmall", v, min + new Vector2(16, 17) * s, s,
                PaperDollUiLaw.ResistanceTextColor(f.ResistancePositive(schools[i]),
                    f.ResistanceNegative(schools[i])));
            DrawCharacterTooltipHit($"MagicResFrame{i + 1}", min, new Vector2(32, 29) * s,
                panelClip, "CharacterResistanceFrame",
                PaperDollUiLaw.ResistanceTooltip(PaperDollUiLaw.ResistanceNames[i],
                    f.Resistance(schools[i]), f.ResistancePositive(schools[i]),
                    f.ResistanceNegative(schools[i])),
                PaperDollUiLaw.ResistanceTooltipSubtext(PaperDollUiLaw.ResistanceTypes[i],
                    f.Resistance(schools[i]), f.Level));
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
        Vector4 panelClip = new(p.X, p.Y, p.X + PaperDollUiLaw.FrameWidth * s,
            p.Y + PaperDollUiLaw.FrameHeight * s);
        string previousVisibleTab = "";
        for (int tab = 0; tab < labels.Length; tab++)
        {
            float width = VanillaCharacterTabWidth(labels[tab], s, 0);
            Vector2 tabMin = p + new Vector2(x, 434) * s;
            string tabElement = $"CharacterFrameTab{tab + 1}";
            if (tab == 1 && !hasPetUI)
            {
                if (CharacterPageUiParityCaptureActive)
                    CollectUiParityDraw(tabElement, "Button", tabMin, new Vector2(width, 32) * s,
                        "CharacterFrame", new("", 0, "IMGUI_HIT_TARGET", "LEFT",
                            previousVisibleTab, "RIGHT", -16, 0, ClipRect: panelClip,
                            Visible: false, Enabled: false, InteractionState: "hidden-no-pet",
                            HitMin: tabMin, HitMax: tabMin, Strata: "MEDIUM"));
                continue;
            }
            bool selected = tab == _characterTab;
            bool clicked = VanillaTab(dl, $"##char-tab-{tab}", tabMin,
                labels[tab], width, s, selected);
            bool hovered = ImGui.IsItemHovered();
            bool active = ImGui.IsItemActive();
            if (CharacterPageUiParityCaptureActive)
            {
                CollectUiParityDraw(tabElement, "Button", tabMin, new Vector2(width, 32) * s,
                    "CharacterFrame", new("", 0, "IMGUI_HIT_TARGET",
                        tab == 0 ? "CENTER" : "LEFT",
                        tab == 0 ? "CharacterFrame" : previousVisibleTab,
                        tab == 0 ? "BOTTOMLEFT" : "RIGHT", tab == 0 ? 60 : -16,
                        tab == 0 ? 62 : 0, ClipRect: panelClip, Visible: true, Enabled: true,
                        InteractionState: active ? "pushed" : hovered ? "highlighted" :
                            selected ? "selected" : "normal", HitMin: tabMin,
                        HitMax: tabMin + new Vector2(width, 32) * s, Strata: "MEDIUM"));
                string texture = selected
                    ? @"Interface\PaperDollInfoFrame\UI-Character-ActiveTab"
                    : @"Interface\PaperDollInfoFrame\UI-Character-InActiveTab";
                float cap = MathF.Min(20f, width * .5f);
                float middle = MathF.Max(0f, width - cap * 2f);
                Vector2 artMin = tabMin + new Vector2(0, selected ? -5 : 0) * s;
                CollectUiParityDraw(tabElement + "/NormalTextureLeft", "TextureUv", artMin,
                    new Vector2(cap, 32) * s, tabElement,
                    new(texture, 0xffffffff, "ARTWORK", "TOPLEFT", tabElement, "TOPLEFT", 0,
                        selected ? 5 : 0, TexCoords: "0|0|0.15625|1", ClipRect: panelClip,
                        BlendMode: "BLEND", InteractionState: selected ? "selected" : "normal",
                        Strata: "MEDIUM"));
                if (middle > 0)
                    CollectUiParityDraw(tabElement + "/NormalTextureMiddle", "TextureUv",
                        artMin + new Vector2(cap, 0) * s, new Vector2(middle, 32) * s, tabElement,
                        new(texture, 0xffffffff, "ARTWORK", "LEFT", tabElement + "/NormalTextureLeft",
                            "RIGHT", 0, 0, TexCoords: "0.15625|0|0.84375|1", ClipRect: panelClip,
                            BlendMode: "BLEND", InteractionState: selected ? "selected" : "normal",
                            Strata: "MEDIUM"));
                CollectUiParityDraw(tabElement + "/NormalTextureRight", "TextureUv",
                    artMin + new Vector2(width - cap, 0) * s, new Vector2(cap, 32) * s, tabElement,
                    new(texture, 0xffffffff, "ARTWORK", "RIGHT", tabElement, "RIGHT", 0,
                        selected ? 5 : 0, TexCoords: "0.84375|0|1|1", ClipRect: panelClip,
                        BlendMode: "BLEND", InteractionState: selected ? "selected" : "normal",
                        Strata: "MEDIUM"));
            }
            if (clicked && tab != _characterTab)
            {
                _characterTab = tab;
                if (tab != 2) _reputationDetailOpen = false;
                if (tab == 1) _petPaperDollDirty = true;
                PlayCharacterTransition(PaperDollUiLaw.TabSwitchSound);
            }
            previousVisibleTab = tabElement;
            x += width - 16;
        }
    }

    private void DrawReputationPage(ImDrawListPtr dl, Vector2 p, float s, WorldEntity player)
    {
        // Factions with a live standing, grouped under their ParentFaction as the 1.12 pane does:
        // the parent (e.g. Alliance) is a collapsible header with no bar of its own; parentless
        // factions collect under "Other".
        var factions = new List<(int Slot, FactionInfo Info, int Standing, byte Flags)>();
        if (_factionCatalog is not null)
            for (int i = 0; i < _reputation.Length; i++)
                if (ReputationFrameUiLaw.IsVisible(_reputation[i].Flags) &&
                    _factionCatalog.TryGetByReputationIndex(i, out FactionInfo info))
                    factions.Add((i, info,
                        info.BaseStanding(player.Fields.Bytes0.Race, player.Fields.Bytes0.Class) +
                        _reputation[i].Standing, _reputation[i].Flags));
        // Header identity is the live 0x08 flag. Inactive rows are re-parented under the synthetic
        // Inactive group; parentless rows use the synthetic Other group.
        factions = factions.Where(x => !ReputationFrameUiLaw.IsHeader(x.Flags)).ToList();
        var groups = factions
            .GroupBy(x => ReputationFrameUiLaw.IsInactive(x.Flags)
                ? ReputationFrameUiLaw.InactiveHeaderKey : x.Info.ParentFaction)
            .Select(g => (Key: g.Key,
                Name: g.Key == ReputationFrameUiLaw.InactiveHeaderKey ? "Inactive" :
                    g.Key != 0 && _factionCatalog!.TryGetName(g.Key, out string header) ? header : "Other",
                Factions: g.OrderBy(x => x.Info.Name, StringComparer.OrdinalIgnoreCase).ToList()))
            .OrderBy(g => g.Key is 0 or ReputationFrameUiLaw.InactiveHeaderKey ? 1 : 0)
            .ThenBy(g => g.Key == ReputationFrameUiLaw.InactiveHeaderKey ? 1 : 0)
            .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var display = new List<(bool Header, uint Key, string Name, int Slot,
            FactionInfo Info, int Standing, byte Flags)>();
        foreach (var group in groups)
        {
            display.Add((true, group.Key, group.Name, -1, default!, 0, 0));
            if (!_collapsedReputationHeaders.Contains(group.Key))
                foreach (var fac in group.Factions)
                    display.Add((false, fac.Info.Id, fac.Info.Name, fac.Slot,
                        fac.Info, fac.Standing, fac.Flags));
        }

        // ReputationFrameFactionLabel/StandingLabel inherit GameFontHighlight (white).
        GameText.DrawCentered(dl, "GameFontHighlight", "Faction", p + new Vector2(115, 64) * s, s);
        GameText.DrawCentered(dl, "GameFontHighlight", "Standing", p + new Vector2(243, 64) * s, s);
        const int visibleRows = 15;
        _reputationScroll = Math.Clamp(_reputationScroll, 0, Math.Max(0, display.Count - visibleRows));
        Vector2 listMin = p + new Vector2(24, 80) * s;
        // Rect test, not a button: a full-area InvisibleButton claims ActiveId on the press
        // frame and makes everything drawn inside it unclickable. Harmless here today (the
        // reputation rows are display-only) but it is the same shape that silently killed the
        // Key Bindings and Talent frames, so it does not stay in the tree.
        if (ImGui.IsMouseHoveringRect(listMin, listMin + new Vector2(300, 360) * s, false) &&
            ImGui.GetIO().MouseWheel != 0)
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
            bool selected = _reputationDetailOpen && _selectedReputationSlot == row.Slot;
            if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                SelectReputationDetail(row.Slot);
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
            if ((hovered || selected) && repHighlight != 0)
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
            if (ReputationFrameUiLaw.IsAtWar(row.Flags) && repFrame != 0)
            {
                Vector2 warMin = new(boxMax.X - 2 * s, boxMin.Y + s);
                dl.AddImage((nint)repFrame, warMin, warMin + new Vector2(24, 22) * s,
                    new Vector2(.0625f, .34375f), new Vector2(.15625f, .71875f));
            }
        }
        if (display.Count == 0)
            GameText.DrawCentered(dl, "GameFontDisable", "No known reputations", p + new Vector2(190, 220) * s, s);
    }

    private void DrawReputationDetail(Vector2 characterOrigin, float s, WorldEntity player)
    {
        if (!_reputationDetailOpen || _selectedReputationSlot is < 0 or >= 64 ||
            _factionCatalog?.TryGetByReputationIndex(_selectedReputationSlot,
                out FactionInfo info) != true || _skin is null)
            return;

        ReputationState state = _reputation[_selectedReputationSlot];
        if (!ReputationFrameUiLaw.IsVisible(state.Flags))
        {
            _reputationDetailOpen = false;
            return;
        }

        ReputationFrameUiLaw.ScreenRect frame =
            ReputationFrameUiLaw.DetailScreenRect(characterOrigin, s);
        ImGui.SetNextWindowPos(frame.Min, ImGuiCond.Always);
        ImGui.SetNextWindowSize(frame.Size, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoNav;
        if (!ImGui.Begin("##reputation-detail", flags))
        {
            ImGui.End();
            return;
        }

        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        _skin.DrawBackdrop(draw, frame.Min, frame.Max, WowSkin.Dialog);
        DrawArt(draw, @"Interface\PaperDollInfoFrame\UI-Character-Reputation-DetailBackground",
            frame.Min + ReputationFrameUiLaw.DetailArt.Min * s,
            ReputationFrameUiLaw.DetailArt.Size, s);
        DrawArt(draw, @"Interface\DialogFrame\UI-DialogBox-Corner",
            frame.Min + ReputationFrameUiLaw.Corner.Min * s,
            ReputationFrameUiLaw.Corner.Size, s);
        DrawArt(draw, @"Interface\DialogFrame\UI-DialogBox-Divider",
            frame.Min + ReputationFrameUiLaw.Divider.Min * s,
            ReputationFrameUiLaw.Divider.Size, s);

        GameText.Draw(draw, "GameFontNormal", info.Name,
            frame.Min + ReputationFrameUiLaw.Name.Min * s, s);
        DrawWrappedText(draw, info.Description,
            frame.Min + ReputationFrameUiLaw.Description.Min * s,
            ReputationFrameUiLaw.Description.Width, 10 * s, s, 0xffffffff, 8);

        ReputationFrameUiLaw.LogicalRect close = ReputationFrameUiLaw.Close;
        DrawImageButton(draw, "##reputation-detail-close", frame.Min + close.Min * s,
            close.Size * s, @"Interface\Buttons\UI-Panel-MinimizeButton-Up",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Down",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Highlight");
        if (ImGui.IsItemClicked()) _reputationDetailOpen = false;

        int totalStanding = info.BaseStanding(player.Fields.Bytes0.Race,
            player.Fields.Bytes0.Class) + state.Standing;
        bool atWar = ReputationFrameUiLaw.IsAtWar(state.Flags);
        bool canToggle = ReputationFrameUiLaw.CanToggleAtWar(state.Flags, totalStanding);
        if (DrawReputationCheck(draw, frame.Min, ReputationFrameUiLaw.AtWarCheck,
                "##reputation-at-war", "At War", atWar, canToggle, true, s,
                ReputationFrameUiLaw.AtWarDescription))
        {
            SetSelectedFactionAtWar(!atWar, totalStanding);
            PlayUiSound(!atWar ? "igMainMenuOptionCheckBoxOn" :
                "igMainMenuOptionCheckBoxOff", "ui.reputation");
        }

        bool inactive = ReputationFrameUiLaw.IsInactive(state.Flags);
        if (DrawReputationCheck(draw, frame.Min, ReputationFrameUiLaw.InactiveCheck,
                "##reputation-inactive", "Move to Inactive", inactive, true, false, s))
        {
            SetSelectedFactionInactive(!inactive);
            PlayUiSound(!inactive ? "igMainMenuOptionCheckBoxOn" :
                "igMainMenuOptionCheckBoxOff", "ui.reputation");
        }

        bool watched = player.Fields.WatchedFactionIndex == _selectedReputationSlot;
        if (DrawReputationCheck(draw, frame.Min, ReputationFrameUiLaw.MainScreenCheck,
                "##reputation-watched", "Show as Experience Bar", watched, true, false, s))
        {
            SetSelectedFactionWatched(!watched);
            PlayUiSound(!watched ? "igMainMenuOptionCheckBoxOn" :
                "igMainMenuOptionCheckBoxOff", "ui.reputation");
        }
        ImGui.End();
    }

    private bool DrawReputationCheck(ImDrawListPtr draw, Vector2 origin,
        ReputationFrameUiLaw.LogicalRect logical, string id, string label, bool value,
        bool enabled, bool sword, float s, string? tooltip = null)
    {
        ReputationFrameUiLaw.CheckGeometry geometry =
            ReputationFrameUiLaw.Check(origin, logical, s, sword);
        ImGui.SetCursorScreenPos(geometry.Hit.Min);
        ImGui.InvisibleButton(id, geometry.Hit.Size);
        bool active = enabled && ImGui.IsItemActive();
        bool itemHovered = ImGui.IsItemHovered();
        bool hovered = enabled && itemHovered;
        uint box = _gameplayArt?.Handle(active
            ? @"Interface\Buttons\UI-CheckBox-Down"
            : @"Interface\Buttons\UI-CheckBox-Up") ?? 0;
        if (box != 0) draw.AddImage((nint)box, geometry.Hit.Min, geometry.Hit.Max);
        if (value)
        {
            string markPath = enabled
                ? sword ? @"Interface\Buttons\UI-CheckBox-SwordCheck" :
                    @"Interface\Buttons\UI-CheckBox-Check"
                : @"Interface\Buttons\UI-CheckBox-Check-Disabled";
            uint mark = _gameplayArt?.Handle(markPath) ?? 0;
            if (mark != 0)
            {
                draw.AddImage((nint)mark, geometry.MarkMin,
                    geometry.MarkMin + geometry.MarkSize);
            }
        }
        if (hovered)
        {
            uint highlight = _gameplayArt?.AdditiveHandle(
                @"Interface\Buttons\UI-CheckBox-Highlight") ?? 0;
            if (highlight != 0)
                draw.AddImage((nint)highlight, geometry.Hit.Min, geometry.Hit.Max);
        }
        uint labelColor = enabled ? sword ? 0xff3333ff : 0xffffffff : 0xff777777;
        GameText.Draw(draw, "GameFontNormalSmall", label,
            geometry.LabelPosition, s, labelColor);
        if (itemHovered && tooltip is not null)
        {
            ReputationFrameUiLaw.TooltipSeat tooltipSeat =
                ReputationFrameUiLaw.RightTooltipSeat(geometry);
            OfferOwnerAnchoredSharedGameTooltip(new("reputation-detail-at-war", 0),
                [new(tooltip, GameTooltipTextTone.White, Wrap: true)],
                tooltipSeat.Anchor, tooltipSeat.Pivot);
        }
        return enabled && ImGui.IsItemClicked();
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
        Vector4 skillClip = new(p.X, p.Y, p.X + PaperDollUiLaw.FrameWidth * s,
            p.Y + PaperDollUiLaw.FrameHeight * s);
        var skills = new List<SkillFrameEntry>();
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
            skills.Add(new SkillFrameEntry(info, value, max, bonus));
        }

        var groups = skills.GroupBy(x => x.Info.CategoryId)
            .Select(g => (Category: _skillLines!.TryGetCategory(g.Key, out SkillCategoryInfo c)
                    ? c : new SkillCategoryInfo(g.Key, "Other", 99),
                Skills: g.OrderBy(x => x.Info.Name, StringComparer.OrdinalIgnoreCase).ToList()))
            .OrderBy(g => g.Category.DisplayOrder).ThenBy(g => g.Category.Id).ToList();
        uint[] categoryIds = groups.Select(x => x.Category.Id).ToArray();
        DrawSkillCollapseAll(dl, p, s, categoryIds);

        // SkillFrame.lua greys a skill (grey bar, no rank number) when skillMaxRank == 1 - the
        // proficiency case. The mage's Class Skills (spell schools) render this way in 1.12, so
        // treat that whole category as proficiency-style too. A 0/0 row is independently barless:
        // it must never inherit that category-wide grey fill.
        var rows = new List<SkillFrameRow>();
        foreach (var group in groups)
        {
            bool classCategory = group.Category.Name.Equals("Class Skills", StringComparison.OrdinalIgnoreCase);
            rows.Add(new SkillFrameRow(true, group.Category.Id, group.Category.Name, 0, 0, 0,
                SkillFrameUiLaw.BarPresentation.Barless));
            if (!_collapsedSkillCategories.Contains(group.Category.Id))
                rows.AddRange(group.Skills.Select(x => new SkillFrameRow(false, x.Info.Id,
                    x.Info.Name, x.Value, x.Max, x.Bonus,
                    SkillFrameUiLaw.BarFor(x.Max, classCategory))));
        }

        ReconcileSkillSelection(player, skills);
        _skillScroll = SkillFrameUiLaw.ClampScroll(_skillScroll, rows.Count);
        SkillFrameUiLaw.LogicalRect wheel = SkillFrameUiLaw.WheelCatcherRect;
        Vector2 listMin = p + new Vector2(wheel.X, wheel.Y) * s;
        Vector2 listMax = listMin + new Vector2(wheel.Width, wheel.Height) * s;
        if (SkillFrameUiParityCaptureActive)
            CollectUiParityDraw("BenillaSkillWheelCatcher", "Button", listMin,
                listMax - listMin, "BenillaSkillFrame",
                new("", 0, "IMGUI_HIT_TARGET", "TOPLEFT", "BenillaSkillFrame", "TOPLEFT",
                    wheel.X, -wheel.Y, ClipRect: skillClip, Visible: true, Enabled: true,
                    InteractionState: "wheel-catcher", HitMin: listMin, HitMax: listMax,
                    Strata: "MEDIUM"));
        if (ImGui.IsMouseHoveringRect(listMin, listMax, false) && ImGui.GetIO().MouseWheel != 0)
            _skillScroll = SkillFrameUiLaw.WheelScroll(
                _skillScroll, rows.Count, ImGui.GetIO().MouseWheel);

        // Every rank row's authored border button is 281x32 on an 18px pitch. The later-created
        // bar frames out-rank every header, and the higher visible slot wins where two borders
        // overlap. Resolve that ordering explicitly instead of silently shrinking the click area
        // to the 271x15 status bar.
        int hoveredEntry = -1, hoveredHeader = -1;
        if (ImGui.IsWindowHovered())
        {
            for (int visible = Math.Min(11, rows.Count - 1 - _skillScroll);
                 visible >= 0; visible--)
            {
                SkillFrameRow candidate = rows[visible + _skillScroll];
                if (candidate.Header) continue;
                SkillFrameUiLaw.LogicalRect hit = SkillFrameUiLaw.SkillRowHitRect(visible);
                if (ImGui.IsMouseHoveringRect(
                        p + new Vector2(hit.X, hit.Y) * s,
                        p + new Vector2(hit.X + hit.Width, hit.Y + hit.Height) * s, false))
                { hoveredEntry = visible; break; }
            }
            if (hoveredEntry < 0)
            {
                for (int visible = Math.Min(11, rows.Count - 1 - _skillScroll);
                     visible >= 0; visible--)
                {
                    SkillFrameRow candidate = rows[visible + _skillScroll];
                    if (!candidate.Header) continue;
                    Vector2 min = p + new Vector2(22, 79 + visible * 18) * s;
                    if (ImGui.IsMouseHoveringRect(min, min + new Vector2(285, 14) * s, false))
                    { hoveredHeader = visible; break; }
                }
            }
        }
        bool rowClick = ImGui.IsMouseClicked(ImGuiMouseButton.Left);

        for (int visible = 0; visible < SkillFrameUiLaw.VisibleRows &&
             visible + _skillScroll < rows.Count; visible++)
        {
            SkillFrameRow row = rows[visible + _skillScroll];
            Vector2 rowMin = p + new Vector2(22, 79 + visible * 18) * s;
            if (row.Header)
            {
                string glyphPath = _collapsedSkillCategories.Contains(row.Key)
                    ? @"Interface\Buttons\UI-PlusButton-Up" : @"Interface\Buttons\UI-MinusButton-Up";
                Vector2 glyphMin = rowMin + new Vector2(3, 0) * s;
                uint glyph = _gameplayArt?.Handle(glyphPath) ?? 0;
                if (glyph != 0)
                    dl.AddImage((nint)glyph, glyphMin, glyphMin + new Vector2(16) * s);
                GameText.Draw(dl, "GameFontHighlight", row.Name, rowMin + new Vector2(25, 0) * s, s);
                if (SkillFrameUiParityCaptureActive)
                {
                    string element = $"BenillaSkillTypeLabel{visible + 1}";
                    CollectUiParityDraw(element, "Button", rowMin, new Vector2(285, 14) * s,
                        "BenillaSkillFrame", new("", 0, "FRAMES", "LEFT",
                            "BenillaSkillFrame", "TOPLEFT", 22, -(86 + visible * 18),
                            ClipRect: skillClip, Visible: true, Enabled: true,
                            InteractionState: hoveredHeader == visible ? "hovered" :
                                _collapsedSkillCategories.Contains(row.Key) ? "collapsed" : "expanded",
                            HitMin: rowMin, HitMax: rowMin + new Vector2(285, 14) * s,
                            Strata: "MEDIUM+1"));
                    CollectUiParityDraw(element + "/NormalTexture", "NormalTexture", glyphMin,
                        new Vector2(16) * s, element,
                        new(glyphPath, 0xffffffff, "ARTWORK", "LEFT", element, "LEFT", 3, 0,
                            TexCoords: "0|0|1|1", ClipRect: skillClip, BlendMode: "BLEND",
                            Visible: glyph != 0, Strata: "MEDIUM+1"));
                    Vector2 textMin = rowMin + new Vector2(25, 0) * s;
                    CollectUiParityDraw(element + "/ButtonText", "FontString", textMin,
                        new Vector2(GameText.MeasureWidth("GameFontHighlight", row.Name, s),
                            GameText.EmPixels("GameFontHighlight", s)), element,
                        new("", 0xffffffff, "ARTWORK", "LEFT", element, "LEFT", 25, 0,
                            @"Fonts\FRIZQT__.TTF", 12, ClipRect: skillClip, Visible: true,
                            Strata: "MEDIUM+1"));
                }
                if (rowClick && hoveredHeader == visible)
                {
                    bool collapsed = _collapsedSkillCategories.Add(row.Key);
                    if (!collapsed) _collapsedSkillCategories.Remove(row.Key);
                    if (collapsed && _selectedSkill != 0 && _skillLines is not null &&
                        _skillLines.TryGet(_selectedSkill, out SkillLineInfo selectedLine) &&
                        selectedLine.CategoryId == row.Key && _skillUnlearnConfirmation is not null)
                        ClearSkillUnlearnConfirmation();
                }
            }
            else
            {
                Vector2 barMin = rowMin + new Vector2(16, 0) * s;
                Vector2 barMax = barMin + new Vector2(271, 15) * s;
                // The row hit-rect is the SkillStatusBarTemplate $parentBorder Button. Submit it
                // first so its hover state is known before the highlight draws; only OnClick has a
                // handler (SkillBar_OnClick) - hover never fills the detail pane, it only glows.
                bool rowHovered = hoveredEntry == visible;
                if (rowClick && rowHovered)
                {
                    _selectedSkill = row.Key;
                    if (_skillUnlearnConfirmation is { SkillId: var armed } && armed != row.Key)
                        ClearSkillUnlearnConfirmation();
                }
                uint bar = _gameplayArt?.Handle(@"Interface\PaperDollInfoFrame\UI-Character-Skills-Bar") ?? 0;
                uint border = _gameplayArt?.Handle(@"Interface\PaperDollInfoFrame\UI-Character-Skills-BarBorder") ?? 0;
                if (row.Bar == SkillFrameUiLaw.BarPresentation.Proficiency)
                {
                    // maxRank==1 proficiency: full-width grey bar, no rank number.
                    if (bar != 0) dl.AddImage((nint)bar, barMin, barMax, Vector2.Zero, Vector2.One, 0xff909090);
                }
                else if (row.Bar == SkillFrameUiLaw.BarPresentation.Progress)
                {
                    float fraction = row.Max > 0 ? Math.Clamp((float)row.Value / row.Max, 0, 1) : 0;
                    if (bar != 0 && fraction > 0)
                        dl.AddImage((nint)bar, barMin, new Vector2(barMin.X + (barMax.X - barMin.X) * fraction, barMax.Y),
                            Vector2.Zero, new Vector2(fraction, 1), 0xffbf4040);
                }
                Vector2 rowBorderMin = barMin - new Vector2(5, 8.5f) * s;
                Vector2 rowBorderMax = rowBorderMin + new Vector2(281, 32) * s;
                if (border != 0) dl.AddImage((nint)border, rowBorderMin, rowBorderMax);
                // $parentBorder's HighlightTexture is UI-Character-Skills-BarBorderHighlight (ADD).
                // SkillFrame_SetStatusBar LockHighlight()s it for the selected row; a mouse-over
                // shows it on any row. Both are the same additive glow over the 281x32 border rect.
                if (rowHovered || _selectedSkill == row.Key)
                {
                    uint hl = _gameplayArt?.AdditiveHandle(@"Interface\PaperDollInfoFrame\UI-Character-Skills-BarBorderHighlight") ?? 0;
                    if (hl != 0) dl.AddImage((nint)hl, rowBorderMin, rowBorderMax);
                }
                GameText.Draw(dl, "GameFontNormalSmall", row.Name, barMin + new Vector2(6, 1) * s, s);
                if (row.Bar == SkillFrameUiLaw.BarPresentation.Progress)
                {
                    string rank = row.Bonus == 0 ? $"{row.Value} / {row.Max}" : $"{row.Value} + {row.Bonus} / {row.Max}";
                    uint? rankColor = row.Bonus != 0 ? (row.Bonus < 0 ? 0xff4040ff : (uint?)0xff40ff40) : null;
                    GameText.Draw(dl, "GameFontHighlightSmall", rank, barMin + new Vector2(177, 1) * s, s, rankColor);
                }
                if (SkillFrameUiParityCaptureActive)
                {
                    string element = $"BenillaSkillRankFrame{visible + 1}";
                    Vector2 borderMin = rowBorderMin;
                    Vector2 borderSize = new Vector2(281, 32) * s;
                    CollectUiParityDraw(element, "StatusBar", barMin,
                        new Vector2(271, 15) * s, "BenillaSkillFrame",
                        new("", 0, "FRAMES", "TOPLEFT", "BenillaSkillFrame", "TOPLEFT",
                            38, -(79 + visible * 18), ClipRect: skillClip, Visible: true,
                            InteractionState: row.Bar.ToString().ToLowerInvariant(),
                            Strata: "MEDIUM+1"));
                    CollectUiParityDraw(element + "Border", "Button", borderMin, borderSize,
                        element, new("", 0, "FRAMES", "LEFT", element, "LEFT", -5, 0,
                            ClipRect: skillClip, Visible: true, Enabled: true,
                            InteractionState: rowHovered ? "hovered" :
                                _selectedSkill == row.Key ? "selected" : "normal",
                            HitMin: borderMin, HitMax: borderMin + borderSize,
                            Strata: "MEDIUM+2"));
                    CollectUiParityDraw(element + "Border/NormalTexture", "NormalTexture",
                        borderMin, borderSize, element + "Border",
                        new(@"Interface\PaperDollInfoFrame\UI-Character-Skills-BarBorder",
                            0xffffffff, "ARTWORK", "TOPLEFT", element + "Border", "TOPLEFT",
                            0, 0, TexCoords: "0|0|1|1", ClipRect: skillClip,
                            BlendMode: "BLEND", Visible: border != 0, Strata: "MEDIUM+2"));
                    float fraction = row.Bar == SkillFrameUiLaw.BarPresentation.Proficiency ? 1f :
                        row.Bar == SkillFrameUiLaw.BarPresentation.Progress && row.Max > 0
                            ? Math.Clamp((float)row.Value / row.Max, 0, 1) : 0f;
                    if (fraction > 0)
                        CollectUiParityDraw(element + "Bar", "BarTexture", barMin,
                            new Vector2(271 * fraction, 15) * s, element,
                            new(@"Interface\PaperDollInfoFrame\UI-Character-Skills-Bar",
                                row.Bar == SkillFrameUiLaw.BarPresentation.Proficiency
                                    ? 0xff909090 : 0xffbf4040, "BACKGROUND", "LEFT", element,
                                "LEFT", 0, 0, TexCoords: $"0|0|{fraction:R}|1",
                                ClipRect: skillClip, BlendMode: "BLEND", Visible: true,
                                InteractionState: $"fraction={fraction:R}", Strata: "MEDIUM+1"));
                    else
                        ClassifyUiParity(element + "Bar", "BarTexture", element, "NOT-DRAWN",
                            "max-zero-barless-or-zero-value");
                    Vector2 nameMin = barMin + new Vector2(6, 1) * s;
                    CollectUiParityDraw(element + "SkillName", "FontString", nameMin,
                        new Vector2(GameText.MeasureWidth("GameFontNormalSmall", row.Name, s),
                            GameText.EmPixels("GameFontNormalSmall", s)), element,
                        new("", 0xff00d1ff, "ARTWORK", "LEFT", element, "LEFT", 6, 1,
                            @"Fonts\FRIZQT__.TTF", 10, ClipRect: skillClip, Visible: true,
                            Strata: "MEDIUM+2"));
                    if (row.Bar != SkillFrameUiLaw.BarPresentation.Progress)
                        ClassifyUiParity(element + "SkillRank", "FontString", element,
                            "NOT-DRAWN", row.Bar == SkillFrameUiLaw.BarPresentation.Barless
                                ? "max-zero-rank-blank" : "proficiency-rank-blank");
                    else
                    {
                        string rank = row.Bonus == 0 ? $"{row.Value} / {row.Max}" :
                            $"{row.Value} + {row.Bonus} / {row.Max}";
                        Vector2 rankMin = barMin + new Vector2(177, 1) * s;
                        CollectUiParityDraw(element + "SkillRank", "FontString", rankMin,
                            new Vector2(GameText.MeasureWidth("GameFontHighlightSmall", rank, s),
                                GameText.EmPixels("GameFontHighlightSmall", s)), element,
                            new("", row.Bonus == 0 ? 0xffffffff :
                                    row.Bonus < 0 ? 0xff4040ff : 0xff40ff40,
                                "ARTWORK", "LEFT", element, "LEFT", 177, 1,
                                @"Fonts\FRIZQT__.TTF", 10, ClipRect: skillClip,
                                Visible: true, InteractionState: rank, Strata: "MEDIUM+2"));
                    }
                    if (rowHovered || _selectedSkill == row.Key)
                        CollectUiParityDraw(element + "Border/HighlightTexture", "HighlightTexture",
                            borderMin, borderSize, element + "Border",
                            new(@"Interface\PaperDollInfoFrame\UI-Character-Skills-BarBorderHighlight",
                                0xffffffff, "HIGHLIGHT", "TOPLEFT", element + "Border", "TOPLEFT",
                                0, 0, TexCoords: "0|0|1|1", ClipRect: skillClip,
                                BlendMode: "ADD", Visible: true,
                                InteractionState: rowHovered ? "hovered" : "selected",
                                Strata: "MEDIUM+2"));
                }
            }

            if (SkillFrameUiParityCaptureActive)
            {
                string absent = row.Header ? $"BenillaSkillRankFrame{visible + 1}" :
                    $"BenillaSkillTypeLabel{visible + 1}";
                ClassifyUiParity(absent, row.Header ? "StatusBar" : "Button",
                    "BenillaSkillFrame", "NOT-DRAWN",
                    row.Header ? "slot-is-category-header" : "slot-is-skill-rank");
            }
        }
        if (SkillFrameUiParityCaptureActive)
        {
            int shown = Math.Min(SkillFrameUiLaw.VisibleRows,
                Math.Max(0, rows.Count - _skillScroll));
            for (int visible = shown; visible < SkillFrameUiLaw.VisibleRows; visible++)
            {
                ClassifyUiParity($"BenillaSkillTypeLabel{visible + 1}", "Button",
                    "BenillaSkillFrame", "NOT-DRAWN", "visible-slot-empty");
                ClassifyUiParity($"BenillaSkillRankFrame{visible + 1}", "StatusBar",
                    "BenillaSkillFrame", "NOT-DRAWN", "visible-slot-empty");
            }
        }
        DrawSkillScrollBar(dl, p, s, rows.Count);

        // SkillFrame ARTWORK layer: the law-owned divider above the detail pane. Current Benilla
        // keeps it at y=305 so the authored 12th 32px border no longer crosses it.
        uint hbar = _gameplayArt?.Handle(@"Interface\ClassTrainerFrame\UI-ClassTrainer-HorizontalBar") ?? 0;
        SkillFrameUiLaw.LogicalRect dividerLeft = SkillFrameUiLaw.DividerLeftRect;
        SkillFrameUiLaw.LogicalRect dividerRight = SkillFrameUiLaw.DividerRightRect;
        if (hbar != 0)
        {
            Vector2 leftMin = p + dividerLeft.Min * s;
            Vector2 rightMin = p + dividerRight.Min * s;
            dl.AddImage((nint)hbar, leftMin, leftMin + dividerLeft.Size * s,
                new Vector2(0, 0), new Vector2(1f, 0.25f));
            dl.AddImage((nint)hbar, rightMin, rightMin + dividerRight.Size * s,
                new Vector2(0, 0.25f), new Vector2(0.29296875f, 0.5f));
        }
        if (SkillFrameUiParityCaptureActive)
        {
            CollectUiParityDraw("BenillaSkillHorizontalBarLeft", "TextureUv",
                p + dividerLeft.Min * s, dividerLeft.Size * s,
                "BenillaSkillFrame", new(@"Interface\ClassTrainerFrame\UI-ClassTrainer-HorizontalBar",
                    0xffffffff, "ARTWORK", "TOPLEFT", "BenillaSkillFrame", "TOPLEFT", 15,
                    -305, TexCoords: "0|0|1|0.25", ClipRect: skillClip,
                    BlendMode: "BLEND", Visible: hbar != 0, Strata: "MEDIUM+1"));
            CollectUiParityDraw("BenillaSkillHorizontalBarRight", "TextureUv",
                p + dividerRight.Min * s, dividerRight.Size * s,
                "BenillaSkillFrame", new(@"Interface\ClassTrainerFrame\UI-ClassTrainer-HorizontalBar",
                    0xffffffff, "ARTWORK", "LEFT", "BenillaSkillHorizontalBarLeft", "RIGHT", 0,
                    0, TexCoords: "0|0.25|0.29296875|0.5", ClipRect: skillClip,
                    BlendMode: "BLEND", Visible: hbar != 0, Strata: "MEDIUM+1"));
        }
        // Clicking a skill row fills the detail pane (SkillBar_OnClick -> SetSelectedSkill ->
        // SkillDetailFrame_SetStatusBar); hover never touches it. Look the selected skill's live
        // value/max/bonus back up from the field-derived list so the detail bar fills correctly.
        if (_selectedSkill != 0 && SelectedSkillIsExpanded() && _skillLines is not null &&
            _skillLines.TryGet(_selectedSkill, out SkillLineInfo selected))
        {
            SkillFrameEntry sel = skills.FirstOrDefault(x => x.Info.Id == _selectedSkill);
            if (sel.Info.Id == _selectedSkill)
            {
                bool proficiencyCategory = _skillLines.TryGetCategory(
                    sel.Info.CategoryId, out SkillCategoryInfo cat) &&
                    cat.Name.Equals("Class Skills", StringComparison.OrdinalIgnoreCase);
                DrawSkillDetail(dl, p, s, player, selected, sel.Value, sel.Max, sel.Bonus,
                    SkillFrameUiLaw.BarFor(sel.Max, proficiencyCategory));
                return;
            }
        }
        if (SkillFrameUiParityCaptureActive)
        {
            string reason = _selectedSkill == 0 ? "no-selected-skill" :
                !SelectedSkillIsExpanded() ? "selected-category-collapsed" :
                "selected-skill-not-in-authoritative-fields";
            ClassifyUiParity("BenillaSkillDetailBar", "StatusBar", "BenillaSkillFrame",
                "NOT-DRAWN", reason);
            ClassifyUiParity("BenillaSkillDetailBarBorder", "Button",
                "BenillaSkillDetailBar", "NOT-DRAWN", reason);
            ClassifyUiParity("BenillaSkillDetailBarSkillName", "FontString",
                "BenillaSkillDetailBar", "NOT-DRAWN", reason);
            ClassifyUiParity("BenillaSkillDetailBarSkillRank", "FontString",
                "BenillaSkillDetailBar", "NOT-DRAWN", reason);
            ClassifyUiParity("BenillaSkillDetailDescriptionText", "FontString",
                "BenillaSkillFrame", "NOT-DRAWN", reason);
            ClassifyUiParity("BenillaSkillDetailUnlearnButton", "Button",
                "BenillaSkillDetailBar", "NOT-DRAWN", reason);
        }
    }

    private void DrawSkillDetail(ImDrawListPtr dl, Vector2 p, float s, WorldEntity player,
        SkillLineInfo info, ushort value, ushort max, int bonus,
        SkillFrameUiLaw.BarPresentation presentation)
    {
        // Current SkillFrame.xml reuses the full 271x15 rank template for the selected-skill row.
        // Its bar, border, description and unlearn seats are resolved in SkillFrameUiLaw.
        SkillFrameUiLaw.LogicalRect detail = SkillFrameUiLaw.DetailBarRect;
        SkillFrameUiLaw.LogicalRect detailBorder = SkillFrameUiLaw.DetailBorderRect;
        Vector2 boxMin = p + detail.Min * s;
        Vector2 boxSize = detail.Size * s;
        Vector2 boxMax = boxMin + boxSize;
        Vector2 borderMin = p + detailBorder.Min * s;
        Vector2 borderMax = borderMin + detailBorder.Size * s;

        // SkillDetailFrame_SetStatusBar colours (ABGR). Normal skill: bar SetStatusBarColor(0,0,1,
        // 0.5), background SetVertexColor(0,0,0.75,0.5). Proficiency (skillMaxRank==1): bar
        // (0.5,0.5,0.5) full, background white a0.5, no rank text.
        bool grey = presentation == SkillFrameUiLaw.BarPresentation.Proficiency;
        bool barless = presentation == SkillFrameUiLaw.BarPresentation.Barless;
        uint fillColor = grey ? 0xff808080u : 0x7fff0000u;
        uint bgColor = barless ? 0x33ffffffu : grey ? 0x80ffffffu : 0x7fbf0000u;
        float fraction = barless ? 0f : grey ? 1f :
            (max > 0 ? Math.Clamp((float)value / max, 0, 1) : 0);
        Vector4 skillClip = new(p.X, p.Y, p.X + PaperDollUiLaw.FrameWidth * s,
            p.Y + PaperDollUiLaw.FrameHeight * s);

        uint bar = _gameplayArt?.Handle(@"Interface\PaperDollInfoFrame\UI-Character-Skills-Bar") ?? 0;
        uint border = _gameplayArt?.Handle(@"Interface\PaperDollInfoFrame\UI-Character-Skills-BarBorder") ?? 0;
        dl.AddRectFilled(boxMin, boxMax, bgColor);
        if (bar != 0 && fraction > 0)
            dl.AddImage((nint)bar, boxMin, new Vector2(boxMin.X + boxSize.X * fraction, boxMax.Y),
                Vector2.Zero, new Vector2(fraction, 1), fillColor);
        if (border != 0) dl.AddImage((nint)border, borderMin, borderMax);
        DrawSkillUnlearnButton(dl, p, s, player, info);

        if (SkillFrameUiParityCaptureActive)
        {
            CollectUiParityDraw("BenillaSkillDetailBar", "StatusBar", boxMin, boxSize,
                "BenillaSkillFrame", new("", 0, "FRAMES", "TOPLEFT",
                    "BenillaSkillFrame", "TOPLEFT", detail.X, -detail.Y,
                    ClipRect: skillClip, Visible: true,
                    InteractionState: presentation.ToString().ToLowerInvariant(),
                    Strata: "MEDIUM+1"));
            CollectUiParityDraw("BenillaSkillDetailBarBackground", "Texture", boxMin,
                boxSize, "BenillaSkillDetailBar", new("", bgColor, "BACKGROUND", "TOPLEFT",
                    "BenillaSkillDetailBar", "TOPLEFT", 0, 0, ClipRect: skillClip,
                    BlendMode: "BLEND", Visible: true,
                    InteractionState: "solid-fill", Strata: "MEDIUM+1"));
            if (fraction > 0)
                CollectUiParityDraw("BenillaSkillDetailBarBar", "BarTexture", boxMin,
                    new Vector2(boxSize.X * fraction, boxSize.Y), "BenillaSkillDetailBar",
                    new(@"Interface\PaperDollInfoFrame\UI-Character-Skills-Bar", fillColor,
                        "BACKGROUND", "LEFT", "BenillaSkillDetailBar", "LEFT", 0, 0,
                        TexCoords: $"0|0|{fraction:R}|1", ClipRect: skillClip,
                        BlendMode: "BLEND", Visible: bar != 0,
                        InteractionState: $"fraction={fraction:R}", Strata: "MEDIUM+1"));
            else
                ClassifyUiParity("BenillaSkillDetailBarBar", "BarTexture",
                    "BenillaSkillDetailBar", "NOT-DRAWN",
                    barless ? "max-zero-barless" : "zero-value");
            CollectUiParityDraw("BenillaSkillDetailBarBorder", "Texture", borderMin,
                borderMax - borderMin, "BenillaSkillDetailBar",
                new(@"Interface\PaperDollInfoFrame\UI-Character-Skills-BarBorder", 0xffffffff,
                    "ARTWORK", "LEFT", "BenillaSkillDetailBar", "LEFT", -5, 0,
                    TexCoords: "0|0|1|1", ClipRect: skillClip, BlendMode: "BLEND",
                    Visible: border != 0, Enabled: false, InteractionState: "decorative-no-op",
                    Strata: "MEDIUM+2"));
        }

        // $parentSkillName GameFontNormalSmall (10px gold) LEFT +(6,1); $parentSkillRank
        // GameFontHighlightSmall (10px white) LEFT to name RIGHT +13 (SkillFrame.lua). Both are
        // vertically centred in the 15px bar (justifyV MIDDLE), nudged up 1px by the (x,1) offset.
        int em = GameText.EmPixels("GameFontNormalSmall", s);
        float textTop = boxMin.Y + (boxSize.Y - em) * 0.5f - 1f * s;
        float nameLeft = boxMin.X + 6f * s;
        GameText.Draw(dl, "GameFontNormalSmall", info.Name, new Vector2(nameLeft, textTop), s);
        if (SkillFrameUiParityCaptureActive)
            CollectUiParityDraw("BenillaSkillDetailBarSkillName", "FontString",
                new Vector2(nameLeft, textTop),
                new Vector2(GameText.MeasureWidth("GameFontNormalSmall", info.Name, s), em),
                "BenillaSkillDetailBar", new("", 0xff00d1ff, "ARTWORK", "LEFT",
                    "BenillaSkillDetailBar", "LEFT", 6, 1, @"Fonts\FRIZQT__.TTF", 10,
                    ClipRect: skillClip, Visible: true, InteractionState: info.Name,
                    Strata: "MEDIUM+2"));
        if (presentation == SkillFrameUiLaw.BarPresentation.Progress)
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
            if (SkillFrameUiParityCaptureActive)
            {
                string rankText = bonus == 0 ? $"{value}/{max}" :
                    $"{value} ({(bonus > 0 ? "+" : "")}{bonus})/{max}";
                CollectUiParityDraw("BenillaSkillDetailBarSkillRank", "FontString",
                    new Vector2(rankLeft, textTop),
                    new Vector2(GameText.MeasureWidth("GameFontHighlightSmall", rankText, s), em),
                    "BenillaSkillDetailBar", new("", 0xffffffff, "ARTWORK", "LEFT",
                        "BenillaSkillDetailBarSkillName", "RIGHT", 13, 0,
                        @"Fonts\FRIZQT__.TTF", 10, ClipRect: skillClip, Visible: true,
                        InteractionState: rankText, Strata: "MEDIUM+2"));
            }
        }
        else if (SkillFrameUiParityCaptureActive)
            ClassifyUiParity("BenillaSkillDetailBarSkillRank", "FontString",
                "BenillaSkillDetailBar", "NOT-DRAWN",
                barless ? "max-zero-rank-blank" : "proficiency-rank-blank");

        // SkillDetailDescriptionText is TOPLEFT to detail-bar BOTTOMLEFT at (-2,-10).
        if (!string.IsNullOrWhiteSpace(info.Description))
        {
            SkillFrameUiLaw.LogicalRect description = SkillFrameUiLaw.DetailDescriptionRect;
            float descLeft = p.X + description.X * s;
            float descTop = p.Y + description.Y * s;
            float wrapPx = description.Width * s;
            float pitch = GameText.LinePitch("GameFontHighlightSmall", s);
            List<string> lines = WrapSkillDescription(info.Description, "GameFontHighlightSmall", s, wrapPx);
            for (int i = 0; i < lines.Count; i++)
                GameText.Draw(dl, "GameFontHighlightSmall", lines[i], new Vector2(descLeft, descTop + i * pitch), s);
            if (SkillFrameUiParityCaptureActive)
            {
                float measuredWidth = lines.Count == 0 ? 0 : lines.Max(line =>
                    GameText.MeasureWidth("GameFontHighlightSmall", line, s));
                CollectUiParityDraw("BenillaSkillDetailDescriptionText", "FontString",
                    new Vector2(descLeft, descTop),
                    new Vector2(measuredWidth, Math.Max(1, lines.Count) * pitch),
                    "BenillaSkillFrame", new("", 0xffffffff, "ARTWORK", "TOPLEFT",
                        "BenillaSkillDetailBar", "BOTTOMLEFT", -2, -10,
                        @"Fonts\FRIZQT__.TTF", 10, ClipRect: skillClip, Visible: true,
                        InteractionState: $"lines={lines.Count};wrap=275", Strata: "MEDIUM+2"));
            }
        }
        else if (SkillFrameUiParityCaptureActive)
            ClassifyUiParity("BenillaSkillDetailDescriptionText", "FontString",
                "BenillaSkillFrame", "NOT-DRAWN", "skill-description-empty");
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

    // size is a device-pixel height; drawn from the exact-size FRIZQT bake (uiScale 1f), never
    // the ImGui default font (game UI never uses the ImGui font).
    private static void DrawCenteredText(ImDrawListPtr dl, Vector2 center, string text, float size, uint color)
        => GameText.DrawPlainCentered(dl, text, center, size, 1f, color);

}

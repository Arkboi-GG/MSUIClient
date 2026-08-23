using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private bool _inspectOpen;
    private ulong _inspectGuid;
    private float _inspectRotation = InspectUiLaw.DefaultFacing;
    private InspectBinding _inspectBinding = InspectBinding.Target;
    private ulong _inspectObservedSelectionGuid;
    private int _inspectObservedPartyRevision;
    private int _inspectParityHoveredSlots;

    /// <summary>
    /// The reply is only an echoed guid. It is not the gate that opens the window: build 5875
    /// calls NotifyInspect and ShowUIPanel in the same UI gesture because worn gear is already in
    /// the public PLAYER_VISIBLE_ITEM fields.
    /// </summary>
    private void ApplyInspect(byte[] body)
    {
        if (body.Length != 8) throw new InvalidDataException($"SMSG_INSPECT has {body.Length} bytes");
        ulong guid = new PacketReader(body).ReadU64();
        if (_inspectOpen && guid != _inspectGuid)
            Console.WriteLine($"[inspect] ignored stale reply for 0x{guid:X16}; active=0x{_inspectGuid:X16}");
    }

    private bool RequestInspect(ulong guid, InspectBinding? binding = null)
    {
        // BenillaInspectFrame_Show hides the current panel before it re-evaluates CanInspect.
        // This makes repeat requests a complete close/request/open transition, and an invalid
        // replacement request cannot leave stale equipment visible.
        if (_inspectOpen) CloseInspect(playSound: true);
        if (_net is null || _controller is null ||
            !_entities.TryGet(guid, out WorldEntity unit)) return false;
        bool canInspect = InspectUiLaw.CanInspect(
            unit.IsPlayer, guid == ControlledGuid, CanAttack(unit),
            Vector3.DistanceSquared(_controller.Position, unit.Position));
        if (!canInspect) return false;

        if (!_net.Inspect(guid)) return false;

        // Inspect is a left UIPanel. Preserve MSUI's existing panels, but do not stack them in the
        // same slot behind this one.
        SetCharacterPageOpen(false);
        _spellbookOpen = false;
        _talentOpen = false;
        _inspectGuid = guid;
        _inspectBinding = binding ?? InspectBinding.Target;
        _inspectObservedSelectionGuid = _selectionGuid;
        _inspectObservedPartyRevision = _partyRosterRevision;
        _inspectRotation = InspectUiLaw.DefaultFacing;
        _inspectOpen = true;
        _inspectPaperDollDirty = true;
        PlayUiSound(InspectUiLaw.OpenSound, InspectUiLaw.SoundCategory);
        return true;
    }

    private void CloseInspect(bool playSound)
    {
        if (!_inspectOpen) return;
        _inspectOpen = false;
        _inspectGuid = 0;
        _inspectBinding = InspectBinding.Target;
        _inspectObservedSelectionGuid = 0;
        _inspectObservedPartyRevision = _partyRosterRevision;
        _inspectPaperDollGuid = 0;
        _inspectPaperDollUsable = false;
        if (playSound) PlayUiSound(InspectUiLaw.CloseSound, InspectUiLaw.SoundCategory);
    }

    private void UpdateInspectLifecycle()
    {
        if (!_inspectOpen) return;
        // The shipped InspectFrame watches the target token even when it was reached from another
        // unit token: clearing target closes; retargeting re-runs the complete inspect gate.
        if (_selectionGuid == 0 || !_entities.TryGet(_selectionGuid, out _))
        {
            CloseInspect(playSound: true);
            return;
        }
        bool targetChanged = _selectionGuid != _inspectObservedSelectionGuid;
        bool partyChanged = _partyRosterRevision != _inspectObservedPartyRevision;
        if (!InspectUiLaw.RefreshForEvent(_inspectBinding, targetChanged, partyChanged)) return;

        InspectBinding binding = _inspectBinding;
        ulong guid = binding.Kind switch
        {
            InspectTokenKind.Target => _selectionGuid,
            InspectTokenKind.Party => PartyFrameMemberGuid(binding.PartyIndex),
            _ => 0,
        };
        if (guid == 0 || !RequestInspect(guid, binding)) CloseInspect(playSound: true);
    }

    private void DrawInspectFrame()
    {
        if (!_inspectOpen || _items is null || _gameplayArt is null ||
            !_entities.TryGet(_inspectGuid, out WorldEntity player)) return;
        float s = GameplayUiScale();
        Vector2 p = UiPanelFrameOrigin(UiPanelOwnershipRegistry[11], s);
        ImGui.SetNextWindowPos(p, ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(InspectUiLaw.FrameWidth,
            InspectUiLaw.FrameHeight) * s, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        if (!ImGui.Begin("##inspect-frame", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoNav))
        { ImGui.End(); return; }
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        bool parity = _uiParityArmed && _uiParityPanel == "inspect-frame";
        Vector4 panelClip = new(p.X, p.Y, p.X + InspectUiLaw.FrameWidth * s,
            p.Y + InspectUiLaw.FrameHeight * s);
        if (parity)
        {
            _inspectParityHoveredSlots = 0;
            BeginUiParityFrame(p, s);
            CollectUiParityDraw("InspectFrame", "Frame", p,
                new Vector2(InspectUiLaw.FrameWidth, InspectUiLaw.FrameHeight) * s, "",
                new("", 0, "IMGUI_HOST", "ANCHOR:ABSOLUTE", "UIParent", "TOPLEFT",
                    p.X / s, -p.Y / s, ContentRect: panelClip, ClipRect: panelClip,
                    ClipMask: "WINDOW_RECT", Visible: true, Enabled: true,
                    InteractionState: "open", HitMin: p,
                    HitMax: p + new Vector2(InspectUiLaw.HitWidth, InspectUiLaw.HitHeight) * s,
                    Strata: "MEDIUM"));
            CollectUiParityDraw("InspectPaperDollFrame", "Frame", p,
                new Vector2(InspectUiLaw.FrameWidth, InspectUiLaw.FrameHeight) * s, "InspectFrame",
                new("", 0, "IMGUI_HOST", "TOPLEFT", "InspectFrame", "TOPLEFT", 0, 0,
                    ContentRect: panelClip, ClipRect: panelClip, ClipMask: "WINDOW_RECT",
                    Visible: true, Enabled: true, InteractionState: "shown",
                    HitMin: p, HitMax: p + new Vector2(InspectUiLaw.HitWidth,
                        InspectUiLaw.HitHeight) * s, Strata: "MEDIUM"));
        }
        // The square live portrait is authored below the paper-doll page. Its BACKGROUND
        // quadrants provide the circular aperture and must paint after the portrait.
        if (parity)
        {
            InspectUiLaw.LogicalRect portrait = InspectUiLaw.PortraitRect;
            Vector2 portraitMin = p + new Vector2(portrait.X, portrait.Y) * s;
            CollectUiParityDraw("InspectFramePortrait", "PlayerModel", portraitMin,
                new Vector2(portrait.Width, portrait.Height) * s, "InspectFrame",
                new("", 0xffffffff, "ARTWORK:BEHIND_PAGE_BACKGROUND", "TOPLEFT",
                    "InspectFrame", "TOPLEFT", 7, -6, TexCoords: "0|1|1|0",
                    ContentRect: new Vector4(portraitMin.X, portraitMin.Y,
                        portraitMin.X + portrait.Width * s, portraitMin.Y + portrait.Height * s),
                    ClipRect: panelClip, ClipMask: "AUTHORED_ROUND_APERTURE_OVERLAY",
                    BlendMode: "BLEND", Visible: true,
                    InteractionState: "live-inspected-unit", Strata: "MEDIUM"));
            CollectInspectBackgroundTelemetry(p, s, panelClip);
        }
        DrawUnitPortraitImage(dl, player,
            p + new Vector2(InspectUiLaw.PortraitRect.X, InspectUiLaw.PortraitRect.Y) * s,
            InspectUiLaw.PortraitRect.Width * s, 0, false);
        DrawPaperDollBackground(dl, p, s);
        string name = _playerNames.GetValueOrDefault(player.Guid, "Player");
        if (parity) CollectInspectIdentityTelemetry(p, s, panelClip, player, name);
        GameText.DrawCentered(dl, "GameFontNormal", name,
            p + new Vector2(198, 24) * s, s, 0xffffffff);
        var b = player.Fields.Bytes0;
        GameText.DrawCentered(dl, "GameFontNormalSmall",
            $"Level {player.Level} {RaceName(b.Race)} {ClassName(b.Class)}",
            p + new Vector2(198, 41) * s, s);

        InspectUiLaw.LogicalRect model = InspectUiLaw.ModelRect;
        Vector2 modelMin = p + new Vector2(model.X, model.Y) * s;
        if (parity)
            CollectUiParityDraw("InspectModel", "PlayerModel", modelMin,
                new Vector2(model.Width, model.Height) * s, "InspectPaperDollFrame",
                new("", 0xffffffff, "ARTWORK", "TOPLEFT", "InspectPaperDollFrame",
                    "TOPLEFT", 65, -78, TexCoords: "0|1|1|0",
                    ContentRect: new Vector4(modelMin.X, modelMin.Y,
                        modelMin.X + model.Width * s, modelMin.Y + model.Height * s),
                    ClipRect: panelClip, ClipMask: "WINDOW_RECT", BlendMode: "BLEND",
                    Visible: _inspectPaperDollUsable,
                    InteractionState: _inspectPaperDollUsable ? "live-remote-paper-doll" : "model-unavailable",
                    Strata: "MEDIUM"));
        if (_inspectPaperDoll is not null && _inspectPaperDollUsable)
            dl.AddImage((nint)_inspectPaperDoll.TextureHandle, modelMin,
                modelMin + new Vector2(model.Width, model.Height) * s,
                new Vector2(0, 1), new Vector2(1, 0));
        DrawInspectRotationButton(dl, p + new Vector2(65, 78) * s, left: true, s);
        DrawInspectRotationButton(dl, p + new Vector2(100, 78) * s, left: false, s);

        for (int i = 0; i < LeftPaperDollSlots.Length; i++)
            DrawInspectSlot(dl, p + new Vector2(21, 74 + i * 41) * s, s, player,
                LeftPaperDollSlots[i].Slot, LeftPaperDollSlots[i].Empty);
        for (int i = 0; i < RightPaperDollSlots.Length; i++)
            DrawInspectSlot(dl, p + new Vector2(305, 74 + i * 41) * s, s, player,
                RightPaperDollSlots[i].Slot, RightPaperDollSlots[i].Empty);
        for (int i = 0; i < WeaponPaperDollSlots.Length; i++)
            DrawInspectSlot(dl, p + new Vector2(122 + i * 42, 385) * s, s, player,
                WeaponPaperDollSlots[i].Slot, WeaponPaperDollSlots[i].Empty);

        // PanelTemplates_SelectTab disables the selected tab; the physical button is inert.
        float tabWidth = VanillaCharacterTabWidth("Character", s, 0);
        VanillaTab(dl, "##inspect-tab-character",
            p + new Vector2(60 - tabWidth * .5f, 434) * s,
            "Character", tabWidth, s, selected: true, enabled: false);
        if (parity) CollectInspectTabTelemetry(p, s, panelClip, tabWidth);

        Vector2 close = p + new Vector2(InspectUiLaw.CloseRect.X, InspectUiLaw.CloseRect.Y) * s;
        DrawImageButton(dl, "##inspect-close", close,
            new Vector2(InspectUiLaw.CloseRect.Width, InspectUiLaw.CloseRect.Height) * s,
            @"Interface\Buttons\UI-Panel-MinimizeButton-Up",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Down",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Highlight");
        if (parity) CollectInspectCloseTelemetry(close, s, panelClip);
        if (ImGui.IsItemClicked()) CloseInspect(playSound: true);
        if (parity)
        {
            ClassifyUiParity("InspectHonorFrame", "Frame", "InspectFrame", "NOT-DRAWN",
                "reference-honor-surface-intentionally-absent");
            ClassifyUiParity("InspectFrameTab2", "Button", "InspectFrame", "NOT-DRAWN",
                "reference-honor-tab-intentionally-absent");
            ClassifyUiParity("InspectAmmoSlot", "Button", "InspectPaperDollFrame", "NOT-DRAWN",
                "reference-inspect-has-no-ammo-slot");
            ClassifyUiParity("InspectBagSlots", "Frame", "InspectPaperDollFrame", "NOT-DRAWN",
                "reference-inspect-has-no-bag-slots");
            SnapshotUiParityScenario();
            MarkUiParityFrameComplete();
        }
        ImGui.End();
    }

    private void CollectInspectBackgroundTelemetry(Vector2 p, float s, Vector4 panelClip)
    {
        (string Element, string Path, Vector2 Offset, Vector2 Size)[] regions =
        [
            ("InspectPaperDollFrame/Texture", @"Interface\PaperDollInfoFrame\UI-Character-CharacterTab-L1", Vector2.Zero, new(256, 256)),
            ("InspectPaperDollFrame/Texture#2", @"Interface\PaperDollInfoFrame\UI-Character-CharacterTab-R1", new(256, 0), new(128, 256)),
            ("InspectPaperDollFrame/Texture#3", @"Interface\PaperDollInfoFrame\UI-Character-CharacterTab-BottomLeft", new(0, 256), new(256, 256)),
            ("InspectPaperDollFrame/Texture#4", @"Interface\PaperDollInfoFrame\UI-Character-CharacterTab-BottomRight", new(256, 256), new(128, 256)),
        ];
        foreach (var region in regions)
        {
            Vector2 min = p + region.Offset * s;
            CollectUiParityDraw(region.Element, "Texture", min, region.Size * s,
                "InspectPaperDollFrame",
                new(region.Path, 0xffffffff, "BACKGROUND:OVER_PORTRAIT", "TOPLEFT",
                    "InspectPaperDollFrame", "TOPLEFT", region.Offset.X, -region.Offset.Y,
                    TexCoords: "0|0|1|1", ClipRect: panelClip,
                    ClipMask: "WINDOW_RECT", BlendMode: "BLEND", Visible: true,
                    Strata: "MEDIUM"));
        }
    }

    private void CollectInspectIdentityTelemetry(Vector2 p, float s, Vector4 panelClip,
        WorldEntity player, string name)
    {
        float nameWidth = GameText.MeasureWidth("GameFontNormal", name, s);
        float nameHeight = GameText.EmPixels("GameFontNormal", s);
        Vector2 nameMin = p + new Vector2(198, 24) * s -
            new Vector2(nameWidth, nameHeight) * .5f;
        CollectUiParityDraw("InspectNameText", "FontString", nameMin,
            new Vector2(nameWidth, nameHeight), "InspectNameFrame",
            new("", 0xffffffff, "ARTWORK", "CENTER", "InspectNameFrame", "CENTER", 0, 0,
                @"Fonts\FRIZQT__.TTF", 12, ContentRect: new Vector4(nameMin.X, nameMin.Y,
                    nameMin.X + nameWidth, nameMin.Y + nameHeight), ClipRect: panelClip,
                Strata: "MEDIUM"));

        var b = player.Fields.Bytes0;
        string level = $"Level {player.Level} {RaceName(b.Race)} {ClassName(b.Class)}";
        float levelWidth = GameText.MeasureWidth("GameFontNormalSmall", level, s);
        float levelHeight = GameText.EmPixels("GameFontNormalSmall", s);
        Vector2 levelMin = p + new Vector2(198, 41) * s -
            new Vector2(levelWidth, levelHeight) * .5f;
        CollectUiParityDraw("InspectLevelText", "FontString", levelMin,
            new Vector2(levelWidth, levelHeight), "InspectNameFrame",
            new("", VanillaGold, "ARTWORK", "CENTER", "InspectNameText", "BOTTOM", 0, -6,
                @"Fonts\FRIZQT__.TTF", 10, ContentRect: new Vector4(levelMin.X, levelMin.Y,
                    levelMin.X + levelWidth, levelMin.Y + levelHeight), ClipRect: panelClip,
                Strata: "MEDIUM"));
    }

    private void CollectInspectTabTelemetry(Vector2 p, float s, Vector4 panelClip,
        float tabWidth)
    {
        Vector2 buttonMin = p + new Vector2(60 - tabWidth * .5f, 434) * s;
        Vector2 size = new(tabWidth * s, 32 * s);
        CollectUiParityDraw("InspectFrameTab1", "Button", buttonMin, size, "InspectFrame",
            new("", 0, "IMGUI_HIT_TARGET", "BOTTOMLEFT", "InspectFrame", "BOTTOMLEFT",
                60 - tabWidth * .5f, 78, ClipRect: panelClip, Enabled: false,
                InteractionState: "selected-disabled", HitMin: buttonMin,
                HitMax: buttonMin + size, Strata: "MEDIUM"));

        const string active = @"Interface\PaperDollInfoFrame\UI-Character-ActiveTab";
        float cap = MathF.Min(20f, tabWidth * .5f);
        float middle = MathF.Max(0f, tabWidth - cap * 2f);
        Vector2 artMin = buttonMin + new Vector2(0, -5) * s;
        CollectUiParityDraw("InspectFrameTab1/LeftTexture", "Texture", artMin,
            new Vector2(cap, 32) * s, "InspectFrameTab1",
            new(active, 0xffffffff, "ARTWORK", "TOPLEFT", "InspectFrameTab1", "TOPLEFT", 0, 5,
                TexCoords: "0|0|0.15625|1", ClipRect: panelClip, BlendMode: "BLEND",
                Strata: "MEDIUM"));
        if (middle > 0)
            CollectUiParityDraw("InspectFrameTab1/MiddleTexture", "Texture",
                artMin + new Vector2(cap, 0) * s, new Vector2(middle, 32) * s,
                "InspectFrameTab1",
                new(active, 0xffffffff, "ARTWORK", "TOP", "InspectFrameTab1", "TOP", 0, 5,
                    TexCoords: "0.15625|0|0.84375|1", ClipRect: panelClip,
                    BlendMode: "BLEND", Strata: "MEDIUM"));
        CollectUiParityDraw("InspectFrameTab1/RightTexture", "Texture",
            artMin + new Vector2(tabWidth - cap, 0) * s, new Vector2(cap, 32) * s,
            "InspectFrameTab1",
            new(active, 0xffffffff, "ARTWORK", "TOPRIGHT", "InspectFrameTab1", "TOPRIGHT", 0, 5,
                TexCoords: "0.84375|0|1|1", ClipRect: panelClip, BlendMode: "BLEND",
                Strata: "MEDIUM"));
        float textWidth = GameText.MeasureWidth("GameFontHighlightSmall", "Character", s);
        float textHeight = GameText.EmPixels("GameFontHighlightSmall", s);
        Vector2 textMin = buttonMin + new Vector2(tabWidth * .5f, 14) * s -
            new Vector2(textWidth, textHeight) * .5f;
        CollectUiParityDraw("InspectFrameTab1/Text", "FontString", textMin,
            new Vector2(textWidth, textHeight), "InspectFrameTab1",
            new("", 0xff808080, "OVERLAY", "CENTER", "InspectFrameTab1", "CENTER", 0, 0,
                @"Fonts\FRIZQT__.TTF", 10, ClipRect: panelClip, Strata: "MEDIUM"));
    }

    private void CollectInspectCloseTelemetry(Vector2 min, float s, Vector4 panelClip)
    {
        Vector2 size = new Vector2(32) * s;
        bool active = ImGui.IsItemActive();
        bool hovered = ImGui.IsItemHovered();
        CollectUiParityDraw("InspectFrameCloseButton", "Button", min, size, "InspectFrame",
            new("", 0, "IMGUI_HIT_TARGET", "TOPRIGHT", "InspectFrame", "TOPRIGHT", -28, -5,
                ClipRect: panelClip, Enabled: true,
                InteractionState: active ? "pushed" : hovered ? "highlighted" : "normal",
                HitMin: min, HitMax: min + size, Strata: "MEDIUM"));
        string normal = active
            ? @"Interface\Buttons\UI-Panel-MinimizeButton-Down"
            : @"Interface\Buttons\UI-Panel-MinimizeButton-Up";
        CollectUiParityDraw("InspectFrameCloseButton/NormalTexture", "Texture", min, size,
            "InspectFrameCloseButton",
            new(normal, 0xffffffff, "ARTWORK", "CENTER", "InspectFrameCloseButton", "CENTER", 0, 0,
                ClipRect: panelClip, BlendMode: "BLEND", InteractionState: active ? "pushed" : "normal",
                Strata: "MEDIUM"));
        if (hovered)
            CollectUiParityDraw("InspectFrameCloseButton/HighlightTexture", "HighlightTexture",
                min, size, "InspectFrameCloseButton",
                new(@"Interface\Buttons\UI-Panel-MinimizeButton-Highlight", 0xffffffff,
                    "HIGHLIGHT", "CENTER", "InspectFrameCloseButton", "CENTER", 0, 0,
                    ClipRect: panelClip, BlendMode: "ADD", InteractionState: "highlighted",
                    Strata: "MEDIUM"));
        else
            ClassifyUiParity("InspectFrameCloseButton/HighlightTexture", "HighlightTexture",
                "InspectFrameCloseButton", "NOT-DRAWN", "close-button-not-hovered");
    }

    private void DrawInspectRotationButton(ImDrawListPtr dl, Vector2 min, bool left, float s)
    {
        string stem = left ? "UI-RotationLeft-Button" : "UI-RotationRight-Button";
        DrawImageButton(dl, left ? "##inspect-left" : "##inspect-right", min, new Vector2(35) * s,
            $@"Interface\Buttons\{stem}-Up", $@"Interface\Buttons\{stem}-Down",
            @"Interface\Buttons\ButtonHilight-Round");
        if (_uiParityArmed && _uiParityPanel == "inspect-frame")
        {
            string element = left ? "InspectModelRotateLeftButton" : "InspectModelRotateRightButton";
            bool active = ImGui.IsItemActive();
            bool hovered = ImGui.IsItemHovered();
            Vector2 size = new Vector2(35) * s;
            Vector4 panelClip = new(_uiParityOrigin.X, _uiParityOrigin.Y,
                _uiParityOrigin.X + 384 * s, _uiParityOrigin.Y + 512 * s);
            CollectUiParityDraw(element, "Button", min, size, "InspectPaperDollFrame",
                new("", 0, "IMGUI_HIT_TARGET", "TOPLEFT", "InspectPaperDollFrame", "TOPLEFT",
                    left ? 65 : 100, -78, ClipRect: panelClip, Enabled: true,
                    InteractionState: active ? "pushed" : hovered ? "highlighted" : "normal",
                    HitMin: min, HitMax: min + size, Strata: "MEDIUM"));
            CollectUiParityDraw(element + "/NormalTexture", "Texture", min, size, element,
                new($@"Interface\Buttons\{stem}-{(active ? "Down" : "Up")}", 0xffffffff,
                    "ARTWORK", "CENTER", element, "CENTER", 0, 0, ClipRect: panelClip,
                    BlendMode: "BLEND", InteractionState: active ? "pushed" : "normal",
                    Strata: "MEDIUM"));
            if (hovered)
                CollectUiParityDraw(element + "/HighlightTexture", "HighlightTexture", min,
                    size, element, new(@"Interface\Buttons\ButtonHilight-Round", 0xffffffff,
                        "HIGHLIGHT", "CENTER", element, "CENTER", 0, 0, ClipRect: panelClip,
                        BlendMode: "ADD", InteractionState: "highlighted", Strata: "MEDIUM"));
            else
                ClassifyUiParity(element + "/HighlightTexture", "HighlightTexture", element,
                    "NOT-DRAWN", "rotation-button-not-hovered");
        }
        bool changed = false;
        if (ImGui.IsItemActivated())
        {
            _inspectRotation = InspectUiLaw.ClickFacing(_inspectRotation, left);
            PlayUiSound(InspectUiLaw.RotateSound, InspectUiLaw.SoundCategory);
            changed = true;
        }
        if (ImGui.IsItemActive())
        {
            _inspectRotation = InspectUiLaw.HeldFacing(
                _inspectRotation, left, ImGui.GetIO().DeltaTime);
            changed = true;
        }
        if (ImGui.IsItemDeactivated())
        {
            _inspectRotation = InspectUiLaw.ClickFacing(_inspectRotation, left);
            PlayUiSound(InspectUiLaw.RotateSound, InspectUiLaw.SoundCategory);
            changed = true;
        }
        if (changed) _inspectPaperDollDirty = true;
    }

    private void DrawInspectSlot(ImDrawListPtr dl, Vector2 min, float s, WorldEntity player,
        int slot, string emptySuffix)
    {
        Vector2 max = min + new Vector2(InspectUiLaw.SlotSize) * s;
        string element = InspectSlotElement(slot);
        bool parity = _uiParityArmed && _uiParityPanel == "inspect-frame";
        Vector4 panelClip = new(_uiParityOrigin.X, _uiParityOrigin.Y,
            _uiParityOrigin.X + 384 * s, _uiParityOrigin.Y + 512 * s);
        uint entry = player.Fields.PlayerVisibleItemEntry(slot);
        ItemTemplate? item = null;
        if (entry != 0 && _net is not null)
        {
            _items!.Require(entry, 0, _net);
            _items.TryGet(entry, out item);
        }
        string iconPath = item?.IconPath ??
            $@"Interface\Paperdoll\UI-PaperDoll-Slot-{emptySuffix}";
        uint icon = _gameplayArt?.Handle(iconPath) ?? 0;
        if (icon != 0) dl.AddImage((nint)icon, min, max);
        if (parity)
            CollectUiParityDraw(element + "IconTexture", "Texture", min, max - min, element,
                new(iconPath, 0xffffffff, "BACKGROUND", "CENTER", element, "CENTER", 0, 0,
                    ClipRect: panelClip, BlendMode: "BLEND", Visible: icon != 0,
                    InteractionState: entry == 0 ? "empty-slot-art" :
                        item is null ? "item-template-pending" : "public-visible-item",
                    Strata: "MEDIUM"));
        ImGui.SetCursorScreenPos(min);
        ImGui.InvisibleButton($"##inspect-slot-{slot}", max - min);
        bool hovered = ImGui.IsItemHovered();
        bool active = ImGui.IsItemActive();
        if (entry != 0 && ImGui.GetIO().KeyCtrl &&
            ImGui.IsItemClicked(ImGuiMouseButton.Left))
            TryOnDressUp(entry);
        if (parity)
        {
            CollectUiParityDraw(element, "Button", min, max - min, "InspectPaperDollFrame",
                new("", 0, "IMGUI_HIT_TARGET", "TOPLEFT", "InspectPaperDollFrame", "TOPLEFT",
                    (min.X - _uiParityOrigin.X) / s,
                    -((min.Y - _uiParityOrigin.Y) / s), ClipRect: panelClip, Enabled: true,
                    InteractionState: active ? "pushed-inert" : hovered ? "highlighted" : "normal",
                    HitMin: min, HitMax: max, Strata: "MEDIUM"));
            if (hovered) _inspectParityHoveredSlots++;
            ClassifyUiParity(element + "PushedTexture", "PushedTexture", element, "NOT-DRAWN",
                "reference-inspect-slot-template-has-no-pushed-texture");
            ClassifyUiParity(element + "Count", "FontString", element, "NOT-DRAWN",
                "foreign-public-slot-does-not-show-count");
            ClassifyUiParity(element + "Cooldown", "Cooldown", element, "NOT-DRAWN",
                "reference-inspect-slot-has-no-cooldown");
        }
        if (hovered)
        {
            var tooltipOwner = new GameTooltipOwnerKey(
                "item:inspect-paper-doll", (ulong)slot);
            if (item is not null)
            {
                ItemTooltipBodySnapshot body = PrepareInspectItemTooltipBody(
                    item, player.Fields, slot);
                OfferPreparedItemTooltip(tooltipOwner, body, max);
            }
            else
            {
                Vector2 tooltipPosition = max;
                string tooltipText = PaperDollUiLaw.EquipmentSlotLabel(slot);
                OfferPreservedSharedGameTooltipRenderer(tooltipOwner, () =>
                {
                    ImGui.SetNextWindowPos(tooltipPosition, ImGuiCond.Always);
                    ImGui.BeginTooltip();
                    ImGui.TextUnformatted(tooltipText);
                    ImGui.EndTooltip();
                });
            }
        }
        uint ring = _gameplayArt?.Handle(@"Interface\Buttons\UI-Quickslot2") ?? 0;
        if (ring != 0)
        {
            Vector2 center = (min + max) * .5f + new Vector2(0, -s);
            if (parity)
                CollectUiParityDraw(element + "NormalTexture", "Texture",
                    center - new Vector2(InspectUiLaw.SlotRingSize * .5f * s),
                    new Vector2(InspectUiLaw.SlotRingSize * s), element,
                    new(@"Interface\Buttons\UI-Quickslot2", 0xffffffff, "ARTWORK", "CENTER",
                        element, "CENTER", 0, 1, ClipRect: panelClip, BlendMode: "BLEND",
                        InteractionState: "normal", Strata: "MEDIUM"));
            dl.AddImage((nint)ring,
                center - new Vector2(InspectUiLaw.SlotRingSize * .5f * s),
                center + new Vector2(InspectUiLaw.SlotRingSize * .5f * s));
        }
        if (hovered)
        {
            uint hi = _gameplayArt?.AdditiveHandle(@"Interface\Buttons\ButtonHilight-Square") ?? 0;
            if (parity)
                CollectUiParityDraw(element + "HighlightTexture", "HighlightTexture", min,
                    max - min, element,
                    new(@"Interface\Buttons\ButtonHilight-Square", 0xffffffff, "HIGHLIGHT",
                        "CENTER", element, "CENTER", 0, 0, ClipRect: panelClip,
                        BlendMode: "ADD", InteractionState: "highlighted", Strata: "MEDIUM"));
            if (hi != 0) dl.AddImage((nint)hi, min, max);
        }
        else if (parity)
            ClassifyUiParity(element + "HighlightTexture", "HighlightTexture", element,
                "NOT-DRAWN", "slot-not-hovered");
    }

    private static string InspectSlotElement(int slot) => slot switch
    {
        0 => "InspectHeadSlot", 1 => "InspectNeckSlot", 2 => "InspectShoulderSlot",
        3 => "InspectShirtSlot", 4 => "InspectChestSlot", 5 => "InspectWaistSlot",
        6 => "InspectLegsSlot", 7 => "InspectFeetSlot", 8 => "InspectWristSlot",
        9 => "InspectHandsSlot", 10 => "InspectFinger0Slot", 11 => "InspectFinger1Slot",
        12 => "InspectTrinket0Slot", 13 => "InspectTrinket1Slot", 14 => "InspectBackSlot",
        15 => "InspectMainHandSlot", 16 => "InspectSecondaryHandSlot",
        17 => "InspectRangedSlot", 18 => "InspectTabardSlot",
        _ => $"InspectUnknownSlot{slot}",
    };

    private ItemTooltipBodySnapshot PrepareInspectItemTooltipBody(
        ItemTemplate item,
        ObjectFields fields,
        int slot)
    {
        // Preserve MSUI's established item body, then append only the public inspected-player
        // enchant names. Foreign durability, counts, creator and private item data stay absent.
        ItemTooltipBodySnapshot body = PrepareItemTooltipBodySnapshot(item, 1);
        var enchantOperations = new List<PreparedItemTooltipPaintOp>();
        for (int enchantSlot = 0;
             InspectUiLaw.VisibleEnchantsAllowed(item.Flags) && enchantSlot < 7;
             enchantSlot++)
        {
            uint raw = fields.PlayerVisibleItemEnchant(slot, enchantSlot);
            if (raw == 0 || _enchantCatalog is null) continue;
            int signed = unchecked((int)raw);
            uint catalogId = signed < 0 ? (uint)(-(long)signed) : raw;
            if (!_enchantCatalog.TryGet(catalogId, out EnchantInfo enchant) ||
                enchant.HidesTooltipName || enchant.Name.Length == 0) continue;
            enchantOperations.Add(PreparedItemTooltipColored(enchant.Name,
                ItemEnchantUiLaw.Color(enchantSlot, signed)));
        }
        return enchantOperations.Count == 0
            ? body
            : AppendPreparedItemTooltipBody(body, [.. enchantOperations]);
    }
}

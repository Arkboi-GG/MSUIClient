using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private bool SkillFrameUiParityCaptureActive =>
        _uiParityArmed && _uiParityPanel == "skill-frame" &&
        _characterOpen && _characterTab == SkillFrameUiLaw.SkillsTab;
    private bool CharacterPageUiParityCaptureActive =>
        _uiParityArmed && (_uiParityPanel == "character-frame" ||
                           _uiParityPanel == "skill-frame");

    private readonly record struct SkillFrameEntry(
        SkillLineInfo Info, ushort Value, ushort Max, int Bonus);

    private readonly record struct SkillFrameRow(
        bool Header, uint Key, string Name, ushort Value, ushort Max, int Bonus,
        SkillFrameUiLaw.BarPresentation Bar);

    private sealed record SkillUnlearnConfirmation(uint SkillId, string SkillName, long Deadline);

    private SkillUnlearnConfirmation? _skillUnlearnConfirmation;

    private void PlaySkillPopupSound(string cue) => PlayUiSound(cue, "ui.skill-frame");

    private void ShowSkillUnlearnConfirmation(uint skillId, string skillName)
    {
        if (!CanAuthorControlledGameplay || ControlledGuid != LocalPlayerGuid) return;
        // StaticPopup_Show overrides a visible dialog of the same type by hiding and showing it.
        if (_skillUnlearnConfirmation is not null)
            ClearSkillUnlearnConfirmation();
        _skillUnlearnConfirmation = new(skillId, skillName,
            Stopwatch.GetTimestamp() + (long)(SkillFrameUiLaw.UnlearnTimeoutSeconds *
                Stopwatch.Frequency));
        PlaySkillPopupSound(SkillFrameUiLaw.PopupOpenSound);
    }

    private void ClearSkillUnlearnConfirmation()
    {
        if (_skillUnlearnConfirmation is null) return;
        _skillUnlearnConfirmation = null;
        PlaySkillPopupSound(SkillFrameUiLaw.PopupCloseSound);
    }

    private bool TryDismissSkillUnlearnConfirmationOnEscape()
    {
        if (_skillUnlearnConfirmation is null) return false;
        ClearSkillUnlearnConfirmation();
        return true;
    }

    private static bool PlayerHasSkill(WorldEntity player, uint skillId)
    {
        for (int slot = 0; slot < 128; slot++)
        {
            ushort field = (ushort)(ObjectFields.PLAYER_SKILL_INFO_1_1 + slot * 3);
            if ((ushort)(player.Fields.GetU32(field) ?? 0) == skillId) return true;
        }
        return false;
    }

    private bool SkillIsCurrentlyAbandonable(WorldEntity player, uint skillId)
    {
        (byte race, byte @class, _, _) = player.Fields.Bytes0;
        return PlayerHasSkill(player, skillId) &&
            _skillLines?.Abandonable(skillId, race, @class) == true;
    }

    private bool SelectedSkillIsExpanded()
    {
        if (_selectedSkill == 0 || _skillLines?.TryGet(_selectedSkill, out SkillLineInfo line) != true)
            return false;
        return !_collapsedSkillCategories.Contains(line.CategoryId);
    }

    private void ReconcileSkillSelection(WorldEntity player, IReadOnlyList<SkillFrameEntry> skills)
    {
        if (_selectedSkill != 0 && !skills.Any(x => x.Info.Id == _selectedSkill))
            _selectedSkill = 0;

        if (_skillUnlearnConfirmation is not { } confirmation) return;
        if (_selectedSkill != confirmation.SkillId || !SelectedSkillIsExpanded() ||
            !SkillIsCurrentlyAbandonable(player, confirmation.SkillId))
            ClearSkillUnlearnConfirmation();
    }

    private void DrawSkillCollapseAll(ImDrawListPtr dl, Vector2 origin, float scale,
        IReadOnlyList<uint> categoryIds)
    {
        Vector4 panelClip = SkillFrameUiLaw.Clip(origin, PaperDollUiLaw.FrameWidth,
            PaperDollUiLaw.FrameHeight, scale);
        if (SkillFrameUiParityCaptureActive)
        {
            SkillFrameUiLaw.LogicalRect frame = SkillFrameUiLaw.CollapseFrameRect;
            Vector2 frameMin = frame.ScaledMin(origin, scale);
            CollectUiParityDraw("BenillaSkillExpandButtonFrame", "Frame", frameMin,
                frame.ScaledSize(scale), "BenillaSkillFrame",
                new("", 0, "FRAMES", "TOPLEFT", "BenillaSkillFrame", "TOPLEFT",
                    frame.X, -frame.Y, ClipRect: panelClip, Visible: true,
                    InteractionState: "shown", Strata: "MEDIUM"));
        }

        (SkillFrameUiLaw.LogicalRect Rect, string Path)[] pieces =
        [
            (SkillFrameUiLaw.CollapseLeftRect,
                @"Interface\QuestFrame\UI-QuestLogSortTab-Left"),
            (SkillFrameUiLaw.CollapseMiddleRect,
                @"Interface\QuestFrame\UI-QuestLogSortTab-Middle"),
            (SkillFrameUiLaw.CollapseRightRect,
                @"Interface\QuestFrame\UI-QuestLogSortTab-Right"),
        ];
        foreach ((SkillFrameUiLaw.LogicalRect rect, string path) in pieces)
        {
            uint art = _gameplayArt?.Handle(path) ?? 0;
            Vector2 min = rect.ScaledMin(origin, scale);
            if (art != 0) dl.AddImage((nint)art, min, min + rect.ScaledSize(scale));
            if (SkillFrameUiParityCaptureActive)
            {
                string suffix = path.EndsWith("-Left", StringComparison.OrdinalIgnoreCase)
                    ? "Left" : path.EndsWith("-Middle", StringComparison.OrdinalIgnoreCase)
                    ? "Middle" : "Right";
                CollectUiParityDraw($"BenillaSkillExpandTab{suffix}", "Texture", min,
                    rect.ScaledSize(scale), "BenillaSkillExpandButtonFrame",
                    new(path, 0xffffffff, "BACKGROUND", "TOPLEFT",
                        "BenillaSkillFrame", "TOPLEFT", rect.X, -rect.Y,
                        TexCoords: "0|0|1|1", ClipRect: panelClip,
                        BlendMode: "BLEND", Visible: art != 0, Strata: "MEDIUM"));
            }
        }

        bool enabled = categoryIds.Count > 0;
        bool someCollapsed = categoryIds.Any(_collapsedSkillCategories.Contains);
        SkillFrameUiLaw.LogicalRect button = SkillFrameUiLaw.CollapseButtonRect;
        Vector2 buttonMin = button.ScaledMin(origin, scale);
        Vector2 buttonSize = button.ScaledSize(scale);
        ImGui.SetCursorScreenPos(buttonMin);
        if (!enabled) ImGui.BeginDisabled();
        ImGui.InvisibleButton("##skill-collapse-all", buttonSize);
        bool hovered = enabled && ImGui.IsItemHovered();
        bool active = enabled && ImGui.IsItemActive();
        bool clicked = enabled && ImGui.IsItemClicked();
        if (!enabled) ImGui.EndDisabled();

        SkillFrameUiLaw.LogicalRect icon = SkillFrameUiLaw.CollapseIconRect;
        Vector2 iconMin = icon.ScaledMin(origin, scale);
        Vector2 iconSize = icon.ScaledSize(scale);
        string iconPath = someCollapsed
            ? @"Interface\Buttons\UI-PlusButton-Up"
            : @"Interface\Buttons\UI-MinusButton-Up";
        uint iconArt = _gameplayArt?.Handle(iconPath) ?? 0;
        if (iconArt != 0)
            dl.AddImage((nint)iconArt, iconMin, iconMin + iconSize,
                Vector2.Zero, Vector2.One, enabled ? 0xffffffff : 0xff777777);
        if (hovered)
        {
            uint highlight = _gameplayArt?.AdditiveHandle(
                @"Interface\Buttons\UI-PlusButton-Hilight") ?? 0;
            if (highlight != 0)
                dl.AddImage((nint)highlight, iconMin, iconMin + iconSize);
        }
        int em = GameText.EmPixels(SkillFrameUiLaw.CollapseLabelFont, scale);
        Vector2 textMin = SkillFrameUiLaw.CollapseTextMin(buttonMin, buttonSize, em, scale);
        GameText.Draw(dl, SkillFrameUiLaw.CollapseLabelFont, SkillFrameUiLaw.CollapseLabel,
            textMin, scale,
            enabled ? null : 0xff777777);

        if (SkillFrameUiParityCaptureActive)
        {
            CollectUiParityDraw("BenillaSkillCollapseAllButton", "Button", buttonMin,
                buttonSize, "BenillaSkillExpandButtonFrame",
                new("", 0, "FRAMES", "LEFT", "BenillaSkillExpandTabLeft", "RIGHT", -3,
                    -3, ClipRect: panelClip, Visible: true, Enabled: enabled,
                    InteractionState: active ? "pushed" : hovered ? "highlighted" :
                        enabled ? "normal" : "disabled", HitMin: buttonMin,
                    HitMax: buttonMin + buttonSize, Strata: "MEDIUM+1"));
            CollectUiParityDraw("BenillaSkillCollapseAllButton/NormalTexture", "NormalTexture",
                iconMin, iconSize, "BenillaSkillCollapseAllButton",
                new(iconPath, enabled ? 0xffffffff : 0xff777777, "ARTWORK", "LEFT",
                    "BenillaSkillCollapseAllButton", "LEFT", 0, 0, TexCoords: "0|0|1|1",
                    ClipRect: panelClip, BlendMode: "BLEND", Visible: iconArt != 0,
                    InteractionState: someCollapsed ? "expand-all" : "collapse-all",
                    Strata: "MEDIUM+1"));
            CollectUiParityDraw("BenillaSkillCollapseAllButton/ButtonText", "FontString",
                textMin, SkillFrameUiLaw.MeasuredSize(
                    GameText.MeasureWidth(SkillFrameUiLaw.CollapseLabelFont,
                        SkillFrameUiLaw.CollapseLabel, scale), em),
                "BenillaSkillCollapseAllButton",
                new("", enabled ? 0xffffffff : 0xff777777, "ARTWORK", "LEFT",
                    "BenillaSkillCollapseAllButton", "LEFT",
                    SkillFrameUiLaw.CollapseLabelOffsetX, 0,
                    @"Fonts\FRIZQT__.TTF", 12, ClipRect: panelClip, Visible: true,
                    Strata: "MEDIUM+1"));
            if (hovered)
                CollectUiParityDraw("BenillaSkillCollapseAllButton/HighlightTexture",
                    "HighlightTexture", iconMin, iconSize, "BenillaSkillCollapseAllButton",
                    new(@"Interface\Buttons\UI-PlusButton-Hilight", 0xffffffff, "HIGHLIGHT",
                        "LEFT", "BenillaSkillCollapseAllButton", "LEFT", 0, 0,
                        TexCoords: "0|0|1|1", ClipRect: panelClip, BlendMode: "ADD",
                        Visible: true, InteractionState: "highlighted", Strata: "MEDIUM+1"));
        }

        if (!clicked) return;
        if (someCollapsed)
        {
            foreach (uint categoryId in categoryIds)
                _collapsedSkillCategories.Remove(categoryId);
        }
        else
        {
            foreach (uint categoryId in categoryIds)
                _collapsedSkillCategories.Add(categoryId);
            _skillScroll = 0;
            if (_skillUnlearnConfirmation is not null) ClearSkillUnlearnConfirmation();
        }
    }

    private void DrawSkillScrollBar(ImDrawListPtr dl, Vector2 origin, float scale, int rowCount)
    {
        int maximum = SkillFrameUiLaw.MaximumScroll(rowCount);
        Vector4 panelClip = SkillFrameUiLaw.Clip(origin, PaperDollUiLaw.FrameWidth,
            PaperDollUiLaw.FrameHeight, scale);
        if (SkillFrameUiParityCaptureActive)
        {
            SkillFrameUiLaw.LogicalRect list = SkillFrameUiLaw.ListRect;
            Vector2 listMin = list.ScaledMin(origin, scale);
            Vector2 listSize = list.ScaledSize(scale);
            CollectUiParityDraw("BenillaSkillListScrollFrame", "ScrollFrame", listMin,
                listSize, "BenillaSkillFrame",
                new("", 0, "FRAMES", "TOPLEFT", "BenillaSkillFrame", "TOPLEFT",
                    list.X, -list.Y, ClipRect: panelClip, Visible: true, Enabled: true,
                    InteractionState: maximum > 0 ? "scrollable" : "at-rest",
                    HitMin: listMin, HitMax: listMin + listSize,
                    Strata: "MEDIUM"));
            SkillFrameUiLaw.LogicalRect sliderTrace = SkillFrameUiLaw.ScrollSliderRect;
            Vector2 sliderTraceMin = sliderTrace.ScaledMin(origin, scale);
            Vector2 sliderTraceSize = sliderTrace.ScaledSize(scale);
            CollectUiParityDraw("BenillaSkillListScrollFrameScrollBar", "Slider",
                sliderTraceMin, sliderTraceSize,
                "BenillaSkillListScrollFrame",
                new("", 0, "FRAMES", "TOPLEFT", "BenillaSkillListScrollFrame", "TOPRIGHT",
                    6, -16, ClipRect: panelClip, Visible: true, Enabled: maximum > 0,
                    InteractionState: $"value={_skillScroll};maximum={maximum}",
                    HitMin: sliderTraceMin,
                    HitMax: sliderTraceMin + sliderTraceSize, Strata: "MEDIUM+1"));
        }
        void Arrow(string id, SkillFrameUiLaw.LogicalRect logical, bool upward)
        {
            bool enabled = upward ? _skillScroll > 0 : _skillScroll < maximum;
            Vector2 min = logical.ScaledMin(origin, scale);
            Vector2 size = logical.ScaledSize(scale);
            ImGui.SetCursorScreenPos(min);
            if (!enabled) ImGui.BeginDisabled();
            ImGui.InvisibleButton(id, size);
            bool active = enabled && ImGui.IsItemActive();
            bool hovered = enabled && ImGui.IsItemHovered();
            bool clicked = enabled && ImGui.IsItemClicked();
            if (!enabled) ImGui.EndDisabled();

            string direction = upward ? "Up" : "Down";
            string state = !enabled ? "Disabled" : active ? "Down" : "Up";
            string path = $@"Interface\Buttons\UI-ScrollBar-Scroll{direction}Button-{state}";
            uint art = _gameplayArt?.Handle(path) ?? 0;
            if (art != 0)
                dl.AddImage((nint)art, min, min + size,
                    SkillFrameUiLaw.ScrollControlUvMin,
                    SkillFrameUiLaw.ScrollControlUvMax);
            if (hovered)
            {
                uint hi = _gameplayArt?.AdditiveHandle(
                    $@"Interface\Buttons\UI-ScrollBar-Scroll{direction}Button-Highlight") ?? 0;
                if (hi != 0)
                    dl.AddImage((nint)hi, min, min + size,
                        SkillFrameUiLaw.ScrollControlUvMin,
                        SkillFrameUiLaw.ScrollControlUvMax);
            }
            if (SkillFrameUiParityCaptureActive)
            {
                string element = "BenillaSkillListScrollFrameScrollBarScroll" +
                    (upward ? "Up" : "Down") + "Button";
                CollectUiParityDraw(element, "Button", min, size,
                    "BenillaSkillListScrollFrameScrollBar",
                    new("", 0, "FRAMES", upward ? "BOTTOM" : "TOP",
                        "BenillaSkillListScrollFrameScrollBar", upward ? "TOP" : "BOTTOM", 0,
                        0, ClipRect: panelClip, Visible: true, Enabled: enabled,
                        InteractionState: !enabled ? "disabled" : active ? "pushed" :
                            hovered ? "highlighted" : "normal", HitMin: min,
                        HitMax: min + size, Strata: "MEDIUM+2"));
                CollectUiParityDraw(element + "/" +
                        (!enabled ? "DisabledTexture" : active ? "PushedTexture" : "NormalTexture"),
                    !enabled ? "DisabledTexture" : active ? "PushedTexture" : "NormalTexture",
                    min, size, element,
                    new(path, 0xffffffff, "ARTWORK", "CENTER", element, "CENTER", 0, 0,
                        TexCoords: "0.25|0.25|0.75|0.75", ClipRect: panelClip,
                        BlendMode: "BLEND", Visible: art != 0,
                        InteractionState: state.ToLowerInvariant(), Strata: "MEDIUM+2"));
            }
            if (!clicked) return;
            _skillScroll = SkillFrameUiLaw.ArrowScroll(_skillScroll, rowCount, upward);
            PlayUiSound(SkillFrameUiLaw.ScrollButtonSound, "ui.skill-frame");
        }

        Arrow("##skill-scroll-up", SkillFrameUiLaw.ScrollUpRect, upward: true);
        Arrow("##skill-scroll-down", SkillFrameUiLaw.ScrollDownRect, upward: false);

        SkillFrameUiLaw.LogicalRect slider = SkillFrameUiLaw.ScrollSliderRect;
        Vector2 sliderMin = slider.ScaledMin(origin, scale);
        Vector2 sliderSize = slider.ScaledSize(scale);
        SkillFrameUiLaw.LogicalRect thumb = SkillFrameUiLaw.ScrollThumbRect(
            _skillScroll, maximum);
        Vector2 thumbMin = thumb.ScaledMin(origin, scale);
        Vector2 thumbSize = thumb.ScaledSize(scale);
        uint knob = _gameplayArt?.Handle(@"Interface\Buttons\UI-ScrollBar-Knob") ?? 0;
        if (knob != 0)
            dl.AddImage((nint)knob, thumbMin, thumbMin + thumbSize,
                SkillFrameUiLaw.ScrollControlUvMin,
                SkillFrameUiLaw.ScrollControlUvMax);

        if (SkillFrameUiParityCaptureActive)
            CollectUiParityDraw("BenillaSkillListScrollFrameScrollBarThumbTexture", "ThumbTexture",
                thumbMin, thumbSize, "BenillaSkillListScrollFrameScrollBar",
                new(@"Interface\Buttons\UI-ScrollBar-Knob", 0xffffffff, "ARTWORK", "TOP",
                    "BenillaSkillListScrollFrameScrollBar", "TOP", 0,
                    -(SkillFrameUiLaw.ScrollThumbY(_skillScroll, maximum) - slider.Y),
                    TexCoords: "0.25|0.25|0.75|0.75", ClipRect: panelClip,
                    BlendMode: "BLEND", Visible: knob != 0,
                    InteractionState: maximum > 0 ? "positioned" : "at-top",
                    Strata: "MEDIUM+1"));

        ImGui.SetCursorScreenPos(sliderMin);
        ImGui.InvisibleButton("##skill-scroll-slider", sliderSize);
        if (maximum > 0 && ImGui.IsItemActive())
        {
            float y = (ImGui.GetIO().MousePos.Y - sliderMin.Y - 8 * scale) /
                (SkillFrameUiLaw.ScrollThumbTravel * scale);
            _skillScroll = Math.Clamp((int)MathF.Round(Math.Clamp(y, 0, 1) * maximum),
                0, maximum);
        }
    }

    private void DrawSkillUnlearnButton(ImDrawListPtr dl, Vector2 panelOrigin,
        float scale, WorldEntity player, SkillLineInfo skill)
    {
        if (!SkillIsCurrentlyAbandonable(player, skill.Id))
        {
            if (SkillFrameUiParityCaptureActive)
            {
                ClassifyUiParity("BenillaSkillDetailUnlearnButton", "Button",
                    "BenillaSkillDetailBar", "NOT-DRAWN",
                    "authoritative-skill-route-not-abandonable");
                ClassifyUiParity("BenillaSkillDetailUnlearnButton/NormalTexture",
                    "NormalTexture", "BenillaSkillDetailUnlearnButton", "NOT-DRAWN",
                    "unlearn-button-hidden");
            }
            return;
        }

        SkillFrameUiLaw.LogicalRect visual = SkillFrameUiLaw.DetailUnlearnRect;
        SkillFrameUiLaw.LogicalRect hit = SkillFrameUiLaw.InsetUnlearnHitRect(visual);
        Vector2 visualMin = panelOrigin + visual.Min * scale;
        Vector2 visualSize = visual.Size * scale;
        Vector2 hitMin = panelOrigin + hit.Min * scale;
        Vector2 hitSize = hit.Size * scale;
        ImGui.SetCursorScreenPos(hitMin);
        ImGui.InvisibleButton("##skill-unlearn", hitSize);
        bool active = ImGui.IsItemActive();
        bool hovered = ImGui.IsItemHovered();
        bool clicked = ImGui.IsItemClicked();

        string path = active
            ? @"Interface\Buttons\CancelButton-Down"
            : @"Interface\Buttons\CancelButton-Up";
        uint art = _gameplayArt?.Handle(path) ?? 0;
        if (art != 0) dl.AddImage((nint)art, visualMin, visualMin + visualSize);
        if (hovered)
        {
            uint hi = _gameplayArt?.AdditiveHandle(
                @"Interface\Buttons\CancelButton-Highlight") ?? 0;
            if (hi != 0) dl.AddImage((nint)hi, visualMin, visualMin + visualSize);
            SkillFrameUiLaw.TooltipSeat tooltipSeat =
                SkillFrameUiLaw.UnlearnTooltipSeat(visualMin, visualSize);
            ImGui.SetNextWindowPos(tooltipSeat.Anchor, ImGuiCond.Always,
                tooltipSeat.Pivot);
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(SkillFrameUiLaw.UnlearnTooltip);
            ImGui.EndTooltip();
        }
        if (SkillFrameUiParityCaptureActive)
        {
            Vector4 panelClip = SkillFrameUiLaw.Clip(_uiParityOrigin,
                PaperDollUiLaw.FrameWidth, PaperDollUiLaw.FrameHeight, scale);
            CollectUiParityDraw("BenillaSkillDetailUnlearnButton", "Button", visualMin,
                visualSize, "BenillaSkillDetailBar",
                new("", 0, "FRAMES", "LEFT", "BenillaSkillDetailBarBorder", "RIGHT", -2,
                    -1, ClipRect: panelClip, Visible: true, Enabled: true,
                    InteractionState: active ? "pushed" : hovered ? "highlighted" : "normal",
                    HitMin: hitMin, HitMax: hitMin + hitSize, Strata: "MEDIUM+2"));
            CollectUiParityDraw("BenillaSkillDetailUnlearnButton/" +
                    (active ? "PushedTexture" : "NormalTexture"),
                active ? "PushedTexture" : "NormalTexture", visualMin, visualSize,
                "BenillaSkillDetailUnlearnButton",
                new(path, 0xffffffff, "ARTWORK", "TOPLEFT",
                    "BenillaSkillDetailUnlearnButton", "TOPLEFT", 0, 0,
                    TexCoords: "0|0|1|1", ClipRect: panelClip, BlendMode: "BLEND",
                    Visible: art != 0, Strata: "MEDIUM+2"));
            if (hovered)
                CollectUiParityDraw("BenillaSkillDetailUnlearnButton/HighlightTexture",
                    "HighlightTexture", visualMin, visualSize,
                    "BenillaSkillDetailUnlearnButton",
                    new(@"Interface\Buttons\CancelButton-Highlight", 0xffffffff, "HIGHLIGHT",
                        "TOPLEFT", "BenillaSkillDetailUnlearnButton", "TOPLEFT", 0, 0,
                        TexCoords: "0|0|1|1", ClipRect: panelClip, BlendMode: "ADD",
                        Visible: true, InteractionState: "highlighted", Strata: "MEDIUM+2"));
        }
        if (clicked) ShowSkillUnlearnConfirmation(skill.Id, skill.Name);
    }

    private void DrawSkillUnlearnConfirmation()
    {
        if (_skillUnlearnConfirmation is not { } confirmation || _skin is null)
        {
            ClassifySkillPopupNotDrawn(_skin is null ? "skin-unavailable" :
                "no-unlearn-confirmation");
            return;
        }
        if (Stopwatch.GetTimestamp() >= confirmation.Deadline)
        {
            ClearSkillUnlearnConfirmation();
            ClassifySkillPopupNotDrawn("confirmation-timeout");
            return;
        }
        if (!CanAuthorControlledGameplay || ControlledGuid != LocalPlayerGuid ||
            _net is not { IsInWorld: true } net ||
            !_entities.TryGet(net.PlayerGuid, out WorldEntity player) ||
            _selectedSkill != confirmation.SkillId ||
            !SkillIsCurrentlyAbandonable(player, confirmation.SkillId))
        {
            ClearSkillUnlearnConfirmation();
            ClassifySkillPopupNotDrawn("authoritative-unlearn-gate-invalidated");
            return;
        }

        float s = GameplayUiScale();
        Vector2 display = ImGui.GetIO().DisplaySize;
        SkillFrameUiLaw.ScreenRect popup = SkillFrameUiLaw.PopupLayout(display, s);
        Vector2 size = popup.Size;
        Vector2 origin = popup.Min;
        ImGui.SetNextWindowPos(origin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        ImGui.SetNextWindowFocus();
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoNav;
        if (!ImGui.Begin("##skill-unlearn-confirm", flags)) { ImGui.End(); return; }
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        Vector4 popupClip = SkillFrameUiLaw.PopupClip(origin, s);
        if (SkillFrameUiParityCaptureActive)
            CollectUiParityDraw("StaticPopup1", "Frame", origin, size, "",
                new("", 0, "IMGUI_HOST", "TOP", "UIParent", "TOP", 0,
                    -SkillFrameUiLaw.PopupRect.Y,
                    ContentRect: popupClip, ClipRect: popupClip, ClipMask: "ImGui-window",
                    Visible: true, Enabled: true, InteractionState: "unlearn-skill",
                    HitMin: origin, HitMax: origin + size, Strata: "DIALOG"));
        dl.PushClipRectFullScreen();
        _skin.DrawBackdrop(dl, origin, origin + size, WowSkin.Dialog);
        dl.PopClipRect();

        if (SkillFrameUiParityCaptureActive)
        {
            float skinScale = _skin.Scale;
            Vector2 fillMin = SkillFrameUiLaw.BackdropFillMin(origin, skinScale,
                WowSkin.Dialog.InsetL, WowSkin.Dialog.InsetT);
            Vector2 fillMax = SkillFrameUiLaw.BackdropFillMax(origin, size, skinScale,
                WowSkin.Dialog.InsetR, WowSkin.Dialog.InsetB);
            bool fillTexture = _skin.TextureHandle(WowSkin.Dialog.Bg) != 0;
            bool edgeTexture = _skin.TextureHandle(WowSkin.Dialog.Edge) != 0;
            float tile = MathF.Max(WowSkin.Dialog.TileSize * skinScale, 1f);
            CollectUiParityDraw("StaticPopup1/BackdropBackground", "TiledTexture",
                fillMin, fillMax - fillMin, "StaticPopup1",
                new(fillTexture ? @"Interface\DialogFrame\UI-DialogBox-Background" : "",
                    fillTexture ? 0xffffffff :
                        ImGui.ColorConvertFloat4ToU32(WowSkin.Fill), "BACKGROUND",
                    "TOPLEFT", "StaticPopup1", "TOPLEFT", WowSkin.Dialog.InsetL,
                    -WowSkin.Dialog.InsetT,
                    TexCoords: fillTexture
                        ? $"0|0|{(fillMax.X - fillMin.X) / tile:R}|" +
                          $"{(fillMax.Y - fillMin.Y) / tile:R}"
                        : "", ClipRect: popupClip,
                    ClipMask: "dialog-backdrop;insets=11|12|12|11", BlendMode: "BLEND",
                    Visible: true, Strata: "DIALOG"));
            CollectUiParityDraw("StaticPopup1/BackdropBorder", "NineSliceTexture", origin,
                size, "StaticPopup1",
                new(edgeTexture ? @"Interface\DialogFrame\UI-DialogBox-Border" : "",
                    edgeTexture ? 0xffffffff : ImGui.ColorConvertFloat4ToU32(WowSkin.GoldDim),
                    "BORDER",
                    "TOPLEFT", "StaticPopup1", "TOPLEFT", 0, 0, TexCoords: "0|0|1|1",
                    ClipRect: popupClip, ClipMask: "8-cell-nine-slice;edge=32",
                    BlendMode: "BLEND", Visible: true, Strata: "DIALOG"));
        }

        string message = string.Format(CultureInfo.InvariantCulture,
            SkillFrameUiLaw.UnlearnQuestionFormat, confirmation.SkillName);
        int em = GameText.EmPixels("GameFontHighlight", s);
        GameText.DrawCentered(dl, "GameFontHighlight", message,
            SkillFrameUiLaw.PopupMessageCenter(origin, s, em), s);
        if (SkillFrameUiParityCaptureActive)
        {
            Vector2 textSize = SkillFrameUiLaw.MeasuredSize(
                GameText.MeasureWidth("GameFontHighlight", message, s), em);
            Vector2 textMin = SkillFrameUiLaw.PopupMessageMin(origin, s, textSize.X);
            CollectUiParityDraw("StaticPopup1Text", "FontString", textMin, textSize,
                "StaticPopup1", new("", 0xffffffff, "ARTWORK", "TOP", "StaticPopup1", "TOP",
                    0, -16, @"Fonts\FRIZQT__.TTF", 12, ClipRect: popupClip, Visible: true,
                    InteractionState: message, Strata: "DIALOG"));
        }

        bool accept = DrawSkillPopupButton(dl, SkillFrameUiLaw.UnlearnButtonText,
            origin, SkillFrameUiLaw.PopupAcceptRect, s, "accept");
        bool cancel = DrawSkillPopupButton(dl, SkillFrameUiLaw.CancelButtonText,
            origin, SkillFrameUiLaw.PopupCancelRect, s, "cancel");
        ImGui.End();

        if (cancel)
        {
            ClearSkillUnlearnConfirmation();
            return;
        }
        if (!accept) return;

        // Revalidate at the final send edge. The server owns the state mutation and reports it
        // later through PLAYER_SKILL_INFO; MSUI never removes the line optimistically.
        if (CanAuthorControlledGameplay && ControlledGuid == LocalPlayerGuid &&
            _selectedSkill == confirmation.SkillId &&
            SkillIsCurrentlyAbandonable(player, confirmation.SkillId))
            net.UnlearnSkill(confirmation.SkillId);
        ClearSkillUnlearnConfirmation();
    }

    private void ClassifySkillPopupNotDrawn(string reason)
    {
        if (!SkillFrameUiParityCaptureActive) return;
        ClassifyUiParity("StaticPopup1", "Frame", "UIParent", "NOT-DRAWN", reason);
        ClassifyUiParity("StaticPopup1Text", "FontString", "StaticPopup1", "NOT-DRAWN",
            reason);
        ClassifyUiParity("StaticPopup1Button1", "Button", "StaticPopup1", "NOT-DRAWN",
            reason);
        ClassifyUiParity("StaticPopup1Button2", "Button", "StaticPopup1", "NOT-DRAWN",
            reason);
    }

    private bool DrawSkillPopupButton(ImDrawListPtr dl, string caption, Vector2 origin,
        SkillFrameUiLaw.LogicalRect logical, float scale, string id)
    {
        Vector2 min = logical.ScaledMin(origin, scale);
        Vector2 size = logical.ScaledSize(scale);
        ImGui.SetCursorScreenPos(min);
        ImGui.InvisibleButton($"##skill-unlearn-{id}", size);
        bool active = ImGui.IsItemActive(), hovered = ImGui.IsItemHovered();
        uint art = _skin!.TextureHandle(active ? "dialog.button.down" : "dialog.button.up");
        if (art != 0)
            dl.AddImage((nint)art, min, min + size, Vector2.Zero,
                SkillFrameUiLaw.PopupButtonUvMax);
        if (hovered)
        {
            uint hi = _skin.TextureHandle("dialog.button.hi");
            if (hi != 0)
                dl.AddImage((nint)hi, min, min + size, Vector2.Zero,
                    SkillFrameUiLaw.PopupButtonUvMax);
        }
        GameText.DrawCentered(dl,
            hovered ? "DialogButtonHighlightText" : "DialogButtonNormalText",
            caption, min + size * .5f, scale);
        if (SkillFrameUiParityCaptureActive)
        {
            string element = id == "accept" ? "StaticPopup1Button1" : "StaticPopup1Button2";
            Vector4 popupClip = SkillFrameUiLaw.PopupClip(origin, scale);
            CollectUiParityDraw(element, "Button", min, size, "StaticPopup1",
                new("", 0, "FRAMES", id == "accept" ? "TOPRIGHT" : "LEFT",
                    id == "accept" ? "StaticPopup1Text" : "StaticPopup1Button1",
                    id == "accept" ? "BOTTOM" : "RIGHT", id == "accept" ? -6 : 13,
                    id == "accept" ? -8 : 0, ClipRect: popupClip, Visible: true,
                    Enabled: true, InteractionState: active ? "pushed" :
                        hovered ? "highlighted" : "normal", HitMin: min,
                    HitMax: min + size, Strata: "DIALOG+1"));
            string texture = active ? @"Interface\Buttons\UI-DialogBox-Button-Down"
                : @"Interface\Buttons\UI-DialogBox-Button-Up";
            CollectUiParityDraw(element + "/" + (active ? "PushedTexture" : "NormalTexture"),
                active ? "PushedTexture" : "NormalTexture", min, size, element,
                new(texture, 0xffffffff, "ARTWORK", "TOPLEFT", element, "TOPLEFT", 0, 0,
                    TexCoords: "0|0|1|0.625", ClipRect: popupClip, BlendMode: "BLEND",
                    Visible: art != 0, Strata: "DIALOG+1"));
            Vector2 textSize = SkillFrameUiLaw.MeasuredSize(GameText.MeasureWidth(
                    hovered ? "DialogButtonHighlightText" : "DialogButtonNormalText",
                    caption, scale),
                GameText.EmPixels("DialogButtonNormalText", scale));
            CollectUiParityDraw(element + "/ButtonText", "FontString",
                min + (size - textSize) * .5f, textSize, element,
                new("", hovered ? 0xffffffff : 0xff00d1ff, "ARTWORK", "CENTER", element,
                    "CENTER", 0, 0, @"Fonts\FRIZQT__.TTF", 12, ClipRect: popupClip,
                    Visible: true, Strata: "DIALOG+1"));
        }
        return ImGui.IsItemClicked();
    }
}

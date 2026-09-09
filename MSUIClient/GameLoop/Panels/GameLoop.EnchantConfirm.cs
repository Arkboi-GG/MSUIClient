using System.Globalization;
using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private enum EnchantConfirmationKind { Bind, Replace }

    private sealed record EnchantConfirmation(
        EnchantConfirmationKind Kind, uint SpellId, ulong ItemGuid,
        string ExistingName = "", string NewName = "");

    private EnchantConfirmation? _enchantConfirmation;

    private static string EnchantPopupType(EnchantConfirmation confirmation) =>
        confirmation.Kind == EnchantConfirmationKind.Bind
            ? EnchantConfirmUiLaw.BindPopupType
            : EnchantConfirmUiLaw.ReplacePopupType;

    private static StaticPopupCoordinatorLaw.Definition EnchantPopupDefinition(
        EnchantConfirmation confirmation) =>
        confirmation.Kind == EnchantConfirmationKind.Bind
            ? EnchantConfirmUiLaw.BindDefinition
            : EnchantConfirmUiLaw.ReplaceDefinition;

    private void ShowEnchantConfirmation(EnchantConfirmation confirmation)
    {
        bool dead = _entities.TryGet(ControlledGuid, out WorldEntity player) && player.IsDead;
        StaticPopupCoordinatorLaw.Plan plan = StaticPopupCoordinatorLaw.Show(
            _staticPopupSlots, EnchantPopupDefinition(confirmation), dead);
        ExecuteStaticPopupPlan(plan);
        _enchantConfirmation = plan.Outcome == StaticPopupCoordinatorLaw.Outcome.Shown
            ? confirmation : null;
    }

    private void ClearEnchantConfirmation()
    {
        string type = _enchantConfirmation is { } open ? EnchantPopupType(open) : "";
        _enchantConfirmation = null;
        if (type.Length > 0)
            ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.HideByType(
                _staticPopupSlots, type));
    }

    private bool TryDismissEnchantConfirmationOnEscape()
    {
        if (_enchantConfirmation is null) return false;
        // The normal path is consumed by the shared StaticPopup escape rung immediately before
        // this fallback. Retain a state-only cleanup for a staged/incomplete popup owner.
        ClearEnchantConfirmation();
        return true;
    }

    private void TryBindItemCast(WorldEntity instance, ItemTemplate? template, bool bindConfirmed)
    {
        uint spellId = _itemCastSpell;
        if (spellId == 0 || _spellCatalog?.TryGet(spellId, out SpellInfo spell) != true) return;

        if (!CastTargetLaw.AcceptsItem(spell))
        {
            RefuseCast(spellId, "LOCAL_INVALID_ITEM_TARGET", "Invalid target");
            return;
        }

        // The reference is permissive when an item template has not settled: it binds and lets
        // the server judge. Bag rendering normally resolves the template before this is reached.
        if (template is null)
        {
            CommitItemCast(spellId, instance.Guid);
            return;
        }

        bool alreadyBound = (instance.Fields.ItemFlags & 0x1) != 0;
        if (!alreadyBound && _enchantCatalog is not null)
        {
            for (int slot = 0; slot < 7; slot++)
            {
                uint raw = instance.Fields.ItemEnchantmentId(slot);
                if (unchecked((int)raw) > 0 && _enchantCatalog.BindsItem(raw))
                { alreadyBound = true; break; }
            }
        }

        static uint LiveEnchant(ObjectFields fields, int slot, EnchantCatalog? catalog)
        {
            uint raw = fields.ItemEnchantmentId(slot);
            return unchecked((int)raw) > 0 && catalog?.TryGet(raw, out _) == true ? raw : 0;
        }

        var clicked = new EnchantClickedItem(
            template.Class, template.Subclass, template.InventoryType, alreadyBound,
            LiveEnchant(instance.Fields, 0, _enchantCatalog),
            LiveEnchant(instance.Fields, 1, _enchantCatalog));
        EnchantBindVerdict verdict = EnchantConfirmUiLaw.Decide(
            spell, clicked, _enchantCatalog, bindConfirmed);
        switch (verdict.Kind)
        {
            case EnchantBindKind.Refuse:
                EmitCastVerdict(spellId, CastTargetReason.InvalidItemTarget, instance.Guid, sent: false);
                RefuseCast(spellId, "LOCAL_INVALID_ITEM_TARGET", "Invalid target");
                return;
            case EnchantBindKind.ConfirmBind:
                ShowEnchantConfirmation(new(
                    EnchantConfirmationKind.Bind, spellId, instance.Guid));
                return;
            case EnchantBindKind.ConfirmReplace:
                ShowEnchantConfirmation(new(
                    EnchantConfirmationKind.Replace, spellId, instance.Guid,
                    _enchantCatalog?.Name(verdict.ExistingEnchant) ?? "",
                    _enchantCatalog?.Name(verdict.NewEnchant) ?? ""));
                return;
            default:
                CommitItemCast(spellId, instance.Guid);
                return;
        }
    }

    private void AcceptEnchantConfirmation()
    {
        EnchantConfirmation? answer = _enchantConfirmation;
        if (answer is null) return;
        if (_itemCastSpell != answer.SpellId ||
            !_entities.TryGet(answer.ItemGuid, out WorldEntity instance))
        {
            ClearEnchantConfirmation();
            return;
        }

        if (answer.Kind == EnchantConfirmationKind.Replace)
        {
            // There is no CMSG_REPLACE_ENCHANT in build 5875. Yes binds the parked item to the
            // same pending CMSG_CAST_SPELL and deliberately bypasses the local gate.
            CommitItemCast(answer.SpellId, answer.ItemGuid);
            // StaticPopup_OnClick hides after OnAccept regardless of whether the send tail could
            // commit. Keep the targeting word armed on send failure, but never strand the modal.
            if (ReferenceEquals(_enchantConfirmation, answer)) ClearEnchantConfirmation();
            return;
        }
        ItemTemplate? template = null;
        if (_items is not null) _items.TryGet(instance.Entry, out template);
        // BindEnchant re-enters 0x495d60 with the confirmed flag. This can immediately raise
        // the replacement question for the same item; the replacement leg ignores that flag.
        TryBindItemCast(instance, template, bindConfirmed: true);
        // A refusal is the only valid re-entry exit that neither commits nor opens the next
        // popup. StaticPopup_OnClick still hides the accepted bind question in that case.
        if (ReferenceEquals(_enchantConfirmation, answer)) ClearEnchantConfirmation();
    }

    private bool EnchantUiParityCaptureActive =>
        _uiParityArmed && _uiParityPanel == "enchant-confirm";

    private EnchantConfirmation? EnchantConfirmationForDraw()
    {
        if (!EnchantUiParityCaptureActive || !_uiParityFixtureStaged)
            return _enchantConfirmation;
        bool replace = _uiParityEnchantConfirmRequestedState.Equals(
            "replace", StringComparison.OrdinalIgnoreCase);
        return replace
            ? new(EnchantConfirmationKind.Replace, 0, 0, "Agility +15", "Crusader")
            : new(EnchantConfirmationKind.Bind, 0, 0);
    }

    private static string EnchantConfirmationState(EnchantConfirmation? confirmation) =>
        confirmation?.Kind == EnchantConfirmationKind.Bind ? "bind" :
        confirmation?.Kind == EnchantConfirmationKind.Replace ? "replace" : "none";

    private static string EnchantConfirmationMessage(EnchantConfirmation confirmation) =>
        confirmation.Kind == EnchantConfirmationKind.Bind
            ? EnchantConfirmUiLaw.BindMessage
            : string.Format(CultureInfo.InvariantCulture,
                EnchantConfirmUiLaw.ReplaceMessageFormat,
                confirmation.ExistingName, confirmation.NewName);

    private string CurrentEnchantConfirmUiParityScenarioSummary()
    {
        EnchantConfirmation? rendered = EnchantConfirmationForDraw();
        string requested = _uiParityEnchantConfirmRequestedState.Length == 0
            ? "any" : _uiParityEnchantConfirmRequestedState;
        return $"panel=enchant-confirm;requestedState={requested};" +
               $"state={EnchantConfirmationState(rendered)};" +
               $"stateSource={(_uiParityFixtureStaged ? "ui-parity-stage" : "item-target-runtime")};" +
               $"fixtureStaged={_uiParityFixtureStaged.ToString().ToLowerInvariant()};" +
               "layout=benilla-staticpopup-alert-420;captureMutation=false";
    }

    private void AddEnchantConfirmUiParityScenario(Dictionary<string, object?> scenario)
    {
        EnchantConfirmation? rendered = EnchantConfirmationForDraw();
        string state = EnchantConfirmationState(rendered);
        scenario["requestedState"] = _uiParityEnchantConfirmRequestedState.Length == 0
            ? "any" : _uiParityEnchantConfirmRequestedState;
        scenario["capturedState"] = state;
        scenario["stateSource"] = _uiParityFixtureStaged
            ? "ui-parity-stage" : "item-target-runtime";
        scenario["productionPopupPresent"] = _enchantConfirmation is not null;
        scenario["productionPopupState"] = EnchantConfirmationState(_enchantConfirmation);
        scenario["pendingItemTargetSpellId"] = _itemCastSpell;
        scenario["renderSpellId"] = rendered?.SpellId ?? 0;
        scenario["renderItemGuid"] = rendered is null ? "0x0000000000000000" :
            $"0x{rendered.ItemGuid:X16}";
        scenario["message"] = rendered is null ? "" : EnchantConfirmationMessage(rendered);
        scenario["acceptButton"] = rendered?.Kind == EnchantConfirmationKind.Bind ? "Okay" :
            rendered?.Kind == EnchantConfirmationKind.Replace ? "Yes" : "";
        scenario["declineButton"] = rendered?.Kind == EnchantConfirmationKind.Bind ? "Cancel" :
            rendered?.Kind == EnchantConfirmationKind.Replace ? "No" : "";
        scenario["existingEnchantName"] = rendered?.ExistingName ?? "";
        scenario["newEnchantName"] = rendered?.NewName ?? "";
        scenario["frameWidth"] = EnchantConfirmUiLaw.FrameWidth;
        scenario["frameHeight"] = EnchantConfirmUiLaw.FrameHeight;
        scenario["frameTop"] = EnchantConfirmUiLaw.FrameTop;
        scenario["layoutProfile"] = "benilla-staticpopup-alert-420";
        scenario["alertIconVisible"] = true;
        scenario["benillaShowAlertFieldInert"] = false;
        scenario["benillaExclusiveFieldInert"] = true;
        scenario["staticPopupType"] = rendered is null ? "" : EnchantPopupType(rendered);
        scenario["buttonsInteractive"] = !_uiParityFixtureStaged;
        scenario["captureStateMutation"] = false;
        scenario["captureNetworkMutation"] = false;
    }

    private void DrawEnchantConfirmation()
    {
        EnchantConfirmation? selected = EnchantConfirmationForDraw();
        if (selected is not { } confirmation || _skin is null) return;
        bool stagedFixture = EnchantUiParityCaptureActive && _uiParityFixtureStaged;
        (int Slot, StaticPopupCoordinatorLaw.Instance Instance)? popup =
            EnchantConfirmUiLaw.Visible(_staticPopupSlots);
        if (!stagedFixture && popup is null) return;
        if (!stagedFixture && _itemCastSpell != confirmation.SpellId)
        {
            ClearEnchantConfirmation();
            return;
        }

        float s = GameplayUiScale();
        string message = EnchantConfirmationMessage(confirmation);
        string[] lines = WrapTooltipText(message, "GameFontHighlight", s,
            EnchantConfirmUiLaw.MessageWrapWidth * s).ToArray();
        float logicalTextHeight = lines.Length *
            GameText.LinePitch("GameFontHighlight", 1);
        EnchantConfirmUiLaw.PopupLayout layout =
            EnchantConfirmUiLaw.Layout(logicalTextHeight);
        int slot = popup?.Slot ?? 1;
        Vector2 origin = StaticPopupOrigin(slot, layout.Width, s);
        EnchantConfirmUiLaw.ScreenRect frame =
            EnchantConfirmUiLaw.ScaledFrame(origin, layout, s);
        Vector2 size = frame.Size;
        ImGui.SetNextWindowPos(origin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);
        ImGui.SetNextWindowFocus();
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoNav;
        if (!ImGui.Begin("##enchant-confirm", flags)) { ImGui.End(); return; }

        bool parityProof = EnchantUiParityCaptureActive;
        Vector4 frameClip = EnchantConfirmUiLaw.ClipRect(frame);
        if (parityProof)
        {
            BeginUiParityFrame(origin, s);
            CollectUiParityDraw("StaticPopup1", "Frame", origin, size, "",
                new("", 0, "IMGUI_HOST", "TOP", "UIParent", "TOP", 0,
                    -EnchantConfirmUiLaw.FrameTop, ContentRect: frameClip,
                    ClipRect: frameClip, ClipMask: "ImGui-window", Strata: "DIALOG"));
        }

        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        dl.PushClipRectFullScreen();
        _skin.DrawBackdrop(dl, origin, origin + size, WowSkin.Dialog);
        EnchantConfirmUiLaw.LogicalRect alert = layout.Alert;
        EnchantConfirmUiLaw.ScreenRect alertScreen =
            EnchantConfirmUiLaw.ScaledRect(origin, alert, s);
        Vector2 alertMin = alertScreen.Min;
        Vector2 alertSize = alertScreen.Size;
        _skin.GlueImage(dl, "dialog.alert", alertMin, alertMin + alertSize);
        dl.PopClipRect();

        if (parityProof)
        {
            // WowSkin's backdrop implementation uses its own current scale for insets/tile UVs;
            // report those draw variables, not the popup's logical layout scale by assumption.
            float backdropScale = _skin.Scale;
            EnchantConfirmUiLaw.ScreenRect fill =
                EnchantConfirmUiLaw.BackdropFillRect(frame, backdropScale);
            Vector2 fillMin = fill.Min;
            Vector2 fillSize = fill.Size;
            static string Number(float value) =>
                value.ToString("0.#####", CultureInfo.InvariantCulture);
            string fillTexCoords = $"0|0|{Number(fillSize.X / (32f * backdropScale))}|" +
                                   Number(fillSize.Y / (32f * backdropScale));
            CollectUiParityDraw("StaticPopup1/BackdropBackground", "TiledTexture",
                fillMin, fillSize, "StaticPopup1",
                new(@"Interface\DialogFrame\UI-DialogBox-Background", 0xffffffff,
                    "BACKGROUND", "TOPLEFT", "StaticPopup1", "TOPLEFT",
                    11f * backdropScale / s, -12f * backdropScale / s,
                    TexCoords: fillTexCoords, ClipRect: frameClip,
                    ClipMask: $"dialog-backdrop-skin-scale={Number(backdropScale)};" +
                              "insets=11|12|12|11",
                    BlendMode: "BLEND", Strata: "DIALOG"));
            CollectUiParityDraw("StaticPopup1/BackdropBorder", "NineSliceTexture",
                origin, size, "StaticPopup1",
                new(@"Interface\DialogFrame\UI-DialogBox-Border", 0xffffffff,
                    "BORDER", "TOPLEFT", "StaticPopup1", "TOPLEFT", 0, 0,
                    TexCoords: "0|0|1|1", ClipRect: frameClip,
                    ClipMask: $"8-cell-nine-slice;skin-scale={Number(backdropScale)};" +
                              "edge=32;partial-edge-tiles-clipped",
                    BlendMode: "BLEND", Strata: "DIALOG"));
            CollectUiParityDraw("StaticPopup1AlertIcon", "Texture", alertMin, alertSize,
                "StaticPopup1", new(@"Interface\DialogFrame\DialogAlertIcon", 0xffffffff,
                    "ARTWORK", "TOPLEFT", "StaticPopup1", "TOPLEFT", alert.X, -alert.Y,
                    TexCoords: "0|0|1|1", ClipRect: frameClip,
                    ClipMask: "StaticPopup1", BlendMode: "BLEND", Strata: "DIALOG"));
        }

        float pitch = GameText.LinePitch("GameFontHighlight", s);
        FontObjectSpec messageFont = FontObjectLaw.Get("GameFontHighlight");
        for (int i = 0; i < lines.Length; i++)
        {
            Vector2 center = EnchantConfirmUiLaw.MessageLineCenter(origin, s, pitch, i);
            GameText.DrawCentered(dl, "GameFontHighlight", lines[i], center, s);
            if (parityProof)
            {
                Vector2 textSize = EnchantConfirmUiLaw.MeasuredSize(
                    GameText.MeasureWidth("GameFontHighlight", lines[i], s),
                    GameText.EmPixels("GameFontHighlight", s));
                Vector2 textMin = center - textSize * .5f;
                CollectUiParityDraw($"StaticPopup1Text{i + 1}", "FontString", textMin,
                    textSize, "StaticPopup1", new("", messageFont.Color, "ARTWORK",
                        "CENTER", "StaticPopup1", "TOPLEFT",
                        EnchantConfirmUiLaw.MessageCenterX,
                        -(EnchantConfirmUiLaw.MessageTop +
                          pitch / s * (i + .5f)), messageFont.Face, messageFont.Height,
                        ClipRect: frameClip,
                        ClipMask: "StaticPopup1", Strata: "DIALOG"));
            }
        }

        string accept = confirmation.Kind == EnchantConfirmationKind.Bind ? "Okay" : "Yes";
        string decline = confirmation.Kind == EnchantConfirmationKind.Bind ? "Cancel" : "No";
        bool accepted = DrawInstrumentedEnchantPopupButton(dl, "StaticPopup1Button1", accept,
            layout.AcceptButton, origin, s, !stagedFixture, frameClip);
        bool declined = DrawInstrumentedEnchantPopupButton(dl, "StaticPopup1Button2", decline,
            layout.DeclineButton, origin, s, !stagedFixture, frameClip);
        if (parityProof)
        {
            SnapshotUiParityScenario();
            MarkUiParityFrameComplete();
        }
        ImGui.End();

        if (stagedFixture) return;
        if (accepted)
            ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Click(
                _staticPopupSlots, slot, buttonIndex: 1));
        else if (declined)
            ExecuteStaticPopupPlan(StaticPopupCoordinatorLaw.Click(
                _staticPopupSlots, slot, buttonIndex: 2));
    }

    private bool DrawInstrumentedEnchantPopupButton(ImDrawListPtr dl, string element, string caption,
        EnchantConfirmUiLaw.LogicalRect rect, Vector2 origin, float s, bool interactive,
        Vector4 frameClip)
    {
        EnchantConfirmUiLaw.ScreenRect button =
            EnchantConfirmUiLaw.ScaledRect(origin, rect, s);
        Vector2 at = button.Min;
        Vector2 size = button.Size;
        bool clicked = false, held = false, hovered = false;
        if (interactive)
        {
            ImGui.SetCursorScreenPos(at);
            clicked = ImGui.InvisibleButton($"##enchant-{element}", size);
            held = ImGui.IsItemActive();
            hovered = ImGui.IsItemHovered();
        }
        uint art = _skin!.TextureHandle(held ? "dialog.button.down" : "dialog.button.up");
        if (art != 0)
            dl.AddImage((nint)art, at, at + size,
                EnchantConfirmUiLaw.ButtonUvMin, EnchantConfirmUiLaw.ButtonUvMax);
        if (hovered)
        {
            uint hi = _skin.TextureHandle("dialog.button.hi");
            if (hi != 0)
                dl.AddImage((nint)hi, at, at + size,
                    EnchantConfirmUiLaw.ButtonUvMin, EnchantConfirmUiLaw.ButtonUvMax);
        }
        string fontObject = hovered ? "DialogButtonHighlightText" : "DialogButtonNormalText";
        GameText.DrawCentered(dl, fontObject, caption, at + size * .5f, s);

        if (EnchantUiParityCaptureActive)
        {
            string state = interactive ? held ? "pressed" : hovered ? "hovered" : "normal"
                : "fixture-inert";
            CollectUiParityDraw(element, "Button", at, size, "StaticPopup1",
                new("", 0, "FRAMES", "TOPLEFT", "StaticPopup1", "TOPLEFT", rect.X,
                    -rect.Y, ClipRect: frameClip,
                    ClipMask: "StaticPopup1", Visible: true, Enabled: interactive,
                    InteractionState: state, HitMin: at, HitMax: at + size,
                    Strata: "DIALOG"));

            string stateElement = held ? element + "/PushedTexture" : element + "/NormalTexture";
            string statePath = held
                ? @"Interface\Buttons\UI-DialogBox-Button-Down"
                : @"Interface\Buttons\UI-DialogBox-Button-Up";
            CollectUiParityDraw(stateElement, held ? "PushedTexture" : "NormalTexture",
                at, size, element, new(statePath, 0xffffffff, "ARTWORK", "TOPLEFT",
                    element, "TOPLEFT", 0, 0, TexCoords: "0|0|1|0.625",
                    ClipRect: frameClip,
                    ClipMask: element, BlendMode: "BLEND", Strata: "DIALOG"));
            ClassifyUiParity(held ? element + "/NormalTexture" : element + "/PushedTexture",
                held ? "NormalTexture" : "PushedTexture", element, "NOT-DRAWN",
                held ? "button-is-pressed" : "button-is-not-pressed");
            if (hovered)
                CollectUiParityDraw(element + "/HighlightTexture", "HighlightTexture", at,
                    size, element, new(@"Interface\Buttons\UI-DialogBox-Button-Highlight",
                        0xffffffff, "HIGHLIGHT", "TOPLEFT", element, "TOPLEFT", 0, 0,
                        TexCoords: "0|0|1|0.625", ClipRect: frameClip,
                        ClipMask: element, BlendMode: "ADD",
                        Strata: "DIALOG"));
            else
                ClassifyUiParity(element + "/HighlightTexture", "HighlightTexture", element,
                    "NOT-DRAWN", interactive ? "button-is-not-hovered" : "fixture-input-disabled");

            FontObjectSpec buttonFont = FontObjectLaw.Get(fontObject);
            Vector2 textSize = EnchantConfirmUiLaw.MeasuredSize(
                GameText.MeasureWidth(fontObject, caption, s),
                GameText.EmPixels(fontObject, s));
            Vector2 textMin = at + (size - textSize) * .5f;
            CollectUiParityDraw(element + "/Text", "FontString", textMin, textSize, element,
                new("", buttonFont.Color, "OVERLAY", "CENTER", element, "CENTER", 0, 0,
                    buttonFont.Face, buttonFont.Height, ClipRect: frameClip,
                    ClipMask: element, Strata: "DIALOG"));
        }
        return clicked;
    }

    // Shared by the other existing alert-style dialogs. Keep this plain production helper
    // separate from EnchantConfirm's capture-aware wrapper so their behavior is untouched.
    private bool DrawEnchantPopupButton(ImDrawListPtr dl, string caption, Vector2 at, float s)
    {
        Vector2 size = EnchantConfirmUiLaw.PlainButtonSize * s;
        ImGui.SetCursorScreenPos(at);
        bool clicked = ImGui.InvisibleButton($"##enchant-{caption}", size);
        bool held = ImGui.IsItemActive();
        bool hovered = ImGui.IsItemHovered();
        uint art = _skin!.TextureHandle(held ? "dialog.button.down" : "dialog.button.up");
        if (art != 0)
            dl.AddImage((nint)art, at, at + size,
                EnchantConfirmUiLaw.ButtonUvMin, EnchantConfirmUiLaw.ButtonUvMax);
        if (hovered)
        {
            uint hi = _skin.TextureHandle("dialog.button.hi");
            if (hi != 0)
                dl.AddImage((nint)hi, at, at + size,
                    EnchantConfirmUiLaw.ButtonUvMin, EnchantConfirmUiLaw.ButtonUvMax);
        }
        GameText.DrawCentered(dl, hovered ? "DialogButtonHighlightText" : "DialogButtonNormalText",
            caption, at + size * .5f, s);
        return clicked;
    }
}

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

    private void PlayEnchantPopupTransition(bool wasOpen, bool willOpen, bool chainedPopup = false)
    {
        foreach (string cue in EnchantConfirmUiLaw.PopupSoundCues(wasOpen, willOpen, chainedPopup))
            PlayUiSound(cue, "ui.enchant-confirm");
    }

    private void ShowEnchantConfirmation(EnchantConfirmation confirmation,
        bool acceptedBindChain = false)
    {
        bool wasOpen = _enchantConfirmation is not null;
        _enchantConfirmation = confirmation;
        // BindEnchant can synchronously raise REPLACE_ENCHANT on a second StaticPopup instance;
        // its OnShow runs before the accepted bind popup's OnHide. Other replacements of an
        // already-visible question use the ordinary Hide-then-Show order instead.
        PlayEnchantPopupTransition(wasOpen, willOpen: true,
            chainedPopup: wasOpen && acceptedBindChain);
    }

    private void ClearEnchantConfirmation()
    {
        bool wasOpen = _enchantConfirmation is not null;
        _enchantConfirmation = null;
        PlayEnchantPopupTransition(wasOpen, willOpen: false);
    }

    private bool TryDismissEnchantConfirmationOnEscape()
    {
        if (_enchantConfirmation is null) return false;
        // StaticPopup hideOnEscape has no OnCancel: the question disappears, no packet is sent,
        // and the item-targeting word remains armed for the next click.
        ClearEnchantConfirmation();
        return true;
    }

    private void TryBindItemCast(WorldEntity instance, ItemTemplate? template, bool bindConfirmed)
    {
        uint spellId = _itemCastSpell;
        if (spellId == 0 || _spellCatalog?.TryGet(spellId, out SpellInfo spell) != true) return;

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
                bool acceptedBindChain = bindConfirmed &&
                    _enchantConfirmation is { Kind: EnchantConfirmationKind.Bind } openBind &&
                    openBind.SpellId == spellId && openBind.ItemGuid == instance.Guid;
                ShowEnchantConfirmation(new(
                    EnchantConfirmationKind.Replace, spellId, instance.Guid,
                    _enchantCatalog?.Name(verdict.ExistingEnchant) ?? "",
                    _enchantCatalog?.Name(verdict.NewEnchant) ?? ""), acceptedBindChain);
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
               "layout=msui-preserved-alert-360x96;captureMutation=false";
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
        scenario["layoutProfile"] = "msui-preserved-alert-360x96";
        scenario["alertIconVisible"] = true;
        scenario["benillaShowAlertFieldInert"] = true;
        scenario["benillaExclusiveFieldInert"] = true;
        scenario["buttonsInteractive"] = !_uiParityFixtureStaged;
        scenario["captureStateMutation"] = false;
        scenario["captureNetworkMutation"] = false;
    }

    private void DrawEnchantConfirmation()
    {
        EnchantConfirmation? selected = EnchantConfirmationForDraw();
        if (selected is not { } confirmation || _skin is null) return;
        bool stagedFixture = EnchantUiParityCaptureActive && _uiParityFixtureStaged;
        if (!stagedFixture && _itemCastSpell != confirmation.SpellId)
        {
            ClearEnchantConfirmation();
            return;
        }

        float s = GameplayUiScale();
        Vector2 display = ImGui.GetIO().DisplaySize;
        Vector2 size = new(EnchantConfirmUiLaw.FrameWidth * s,
            EnchantConfirmUiLaw.FrameHeight * s);
        Vector2 origin = new((display.X - size.X) * .5f, EnchantConfirmUiLaw.FrameTop * s);
        ImGui.SetNextWindowPos(origin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);
        ImGui.SetNextWindowFocus();
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoNav;
        if (!ImGui.Begin("##enchant-confirm", flags)) { ImGui.End(); return; }

        bool parityProof = EnchantUiParityCaptureActive;
        Vector4 frameClip = new(origin.X, origin.Y, origin.X + size.X, origin.Y + size.Y);
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
        EnchantConfirmUiLaw.LogicalRect alert = EnchantConfirmUiLaw.AlertRect;
        Vector2 alertMin = origin + new Vector2(alert.X, alert.Y) * s;
        Vector2 alertSize = new Vector2(alert.Width, alert.Height) * s;
        _skin.GlueImage(dl, "dialog.alert", alertMin, alertMin + alertSize);
        dl.PopClipRect();

        if (parityProof)
        {
            // WowSkin's backdrop implementation uses its own current scale for insets/tile UVs;
            // report those draw variables, not the popup's logical layout scale by assumption.
            float backdropScale = _skin.Scale;
            Vector2 fillMin = origin + new Vector2(11f, 12f) * backdropScale;
            Vector2 fillMax = origin + size - new Vector2(12f, 11f) * backdropScale;
            Vector2 fillSize = Vector2.Max(Vector2.Zero, fillMax - fillMin);
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

        string message = EnchantConfirmationMessage(confirmation);
        IReadOnlyList<string> lines = WrapEnchantMessage(message,
            EnchantConfirmUiLaw.MessageWrapWidth * s, s);
        float pitch = GameText.LinePitch("GameFontNormal", s);
        float textTop = origin.Y + EnchantConfirmUiLaw.MessageTop * s;
        FontObjectSpec messageFont = FontObjectLaw.Get("GameFontNormal");
        for (int i = 0; i < lines.Count; i++)
        {
            Vector2 center = new(origin.X + EnchantConfirmUiLaw.MessageCenterX * s,
                textTop + pitch * (i + .5f));
            GameText.DrawCentered(dl, "GameFontNormal", lines[i], center, s);
            if (parityProof)
            {
                Vector2 textSize = new(GameText.MeasureWidth("GameFontNormal", lines[i], s),
                    GameText.EmPixels("GameFontNormal", s));
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
            EnchantConfirmUiLaw.AcceptButtonRect, origin, s, !stagedFixture, frameClip);
        bool declined = DrawInstrumentedEnchantPopupButton(dl, "StaticPopup1Button2", decline,
            EnchantConfirmUiLaw.DeclineButtonRect, origin, s, !stagedFixture, frameClip);
        if (parityProof)
        {
            SnapshotUiParityScenario();
            MarkUiParityFrameComplete();
        }
        ImGui.End();

        if (accepted) AcceptEnchantConfirmation();
        else if (declined) ClearEnchantConfirmation();
    }

    private static IReadOnlyList<string> WrapEnchantMessage(string message, float width, float scale)
    {
        var lines = new List<string>();
        string current = "";
        foreach (string word in message.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = current.Length == 0 ? word : current + " " + word;
            if (current.Length > 0 && GameText.MeasureWidth("GameFontNormal", candidate, scale) > width)
            { lines.Add(current); current = word; }
            else current = candidate;
        }
        if (current.Length > 0) lines.Add(current);
        return lines;
    }

    private bool DrawInstrumentedEnchantPopupButton(ImDrawListPtr dl, string element, string caption,
        EnchantConfirmUiLaw.LogicalRect rect, Vector2 origin, float s, bool interactive,
        Vector4 frameClip)
    {
        Vector2 at = origin + new Vector2(rect.X, rect.Y) * s;
        Vector2 size = new Vector2(rect.Width, rect.Height) * s;
        bool clicked = false, held = false, hovered = false;
        if (interactive)
        {
            ImGui.SetCursorScreenPos(at);
            clicked = ImGui.InvisibleButton($"##enchant-{element}", size);
            held = ImGui.IsItemActive();
            hovered = ImGui.IsItemHovered();
        }
        uint art = _skin!.TextureHandle(held ? "dialog.button.down" : "dialog.button.up");
        if (art != 0) dl.AddImage((nint)art, at, at + size, Vector2.Zero, new Vector2(1f, .625f));
        if (hovered)
        {
            uint hi = _skin.TextureHandle("dialog.button.hi");
            if (hi != 0) dl.AddImage((nint)hi, at, at + size, Vector2.Zero, new Vector2(1f, .625f));
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
            Vector2 textSize = new(GameText.MeasureWidth(fontObject, caption, s),
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
        Vector2 size = new Vector2(128f, 20f) * s;
        ImGui.SetCursorScreenPos(at);
        bool clicked = ImGui.InvisibleButton($"##enchant-{caption}", size);
        bool held = ImGui.IsItemActive();
        bool hovered = ImGui.IsItemHovered();
        uint art = _skin!.TextureHandle(held ? "dialog.button.down" : "dialog.button.up");
        if (art != 0) dl.AddImage((nint)art, at, at + size, Vector2.Zero, new Vector2(1f, .625f));
        if (hovered)
        {
            uint hi = _skin.TextureHandle("dialog.button.hi");
            if (hi != 0) dl.AddImage((nint)hi, at, at + size, Vector2.Zero, new Vector2(1f, .625f));
        }
        GameText.DrawCentered(dl, hovered ? "DialogButtonHighlightText" : "DialogButtonNormalText",
            caption, at + size * .5f, s);
        return clicked;
    }
}

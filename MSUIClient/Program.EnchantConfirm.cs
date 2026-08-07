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

    private void ClearEnchantConfirmation() => _enchantConfirmation = null;

    private bool TryDismissEnchantConfirmationOnEscape()
    {
        if (_enchantConfirmation is null) return false;
        // StaticPopup hideOnEscape has no OnCancel: the question disappears, no packet is sent,
        // and the item-targeting word remains armed for the next click.
        _enchantConfirmation = null;
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
                _enchantConfirmation = new(
                    EnchantConfirmationKind.Bind, spellId, instance.Guid);
                return;
            case EnchantBindKind.ConfirmReplace:
                _enchantConfirmation = new(
                    EnchantConfirmationKind.Replace, spellId, instance.Guid,
                    _enchantCatalog?.Name(verdict.ExistingEnchant) ?? "",
                    _enchantCatalog?.Name(verdict.NewEnchant) ?? "");
                return;
            default:
                CommitItemCast(spellId, instance.Guid);
                return;
        }
    }

    private void AcceptEnchantConfirmation()
    {
        EnchantConfirmation? answer = _enchantConfirmation;
        _enchantConfirmation = null;
        if (answer is null || _itemCastSpell != answer.SpellId ||
            !_entities.TryGet(answer.ItemGuid, out WorldEntity instance)) return;

        if (answer.Kind == EnchantConfirmationKind.Replace)
        {
            // There is no CMSG_REPLACE_ENCHANT in build 5875. Yes binds the parked item to the
            // same pending CMSG_CAST_SPELL and deliberately bypasses the local gate.
            CommitItemCast(answer.SpellId, answer.ItemGuid);
            return;
        }
        ItemTemplate? template = null;
        if (_items is not null) _items.TryGet(instance.Entry, out template);
        // BindEnchant re-enters 0x495d60 with the confirmed flag. This can immediately raise
        // the replacement question for the same item; the replacement leg ignores that flag.
        TryBindItemCast(instance, template, bindConfirmed: true);
    }

    private void DrawEnchantConfirmation()
    {
        if (_enchantConfirmation is not { } confirmation || _skin is null) return;
        if (_itemCastSpell != confirmation.SpellId)
        {
            _enchantConfirmation = null;
            return;
        }

        float s = GameplayUiScale();
        Vector2 display = ImGui.GetIO().DisplaySize;
        Vector2 size = new Vector2(360f, 96f) * s;
        Vector2 origin = new((display.X - size.X) * .5f, 128f * s);
        ImGui.SetNextWindowPos(origin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);
        ImGui.SetNextWindowFocus();
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoNav;
        if (!ImGui.Begin("##enchant-confirm", flags)) { ImGui.End(); return; }

        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        dl.PushClipRectFullScreen();
        _skin.DrawBackdrop(dl, origin, origin + size, WowSkin.Dialog);
        _skin.GlueImage(dl, "dialog.alert", origin + new Vector2(12f, 8f) * s,
            origin + new Vector2(76f, 72f) * s);
        dl.PopClipRect();

        string message = confirmation.Kind == EnchantConfirmationKind.Bind
            ? "Enchanting this item will bind it to you."
            : $"Do you want to replace \"{confirmation.ExistingName}\" with \"{confirmation.NewName}\"?";
        IReadOnlyList<string> lines = WrapEnchantMessage(message, 260f * s, s);
        float pitch = GameText.LinePitch("GameFontNormal", s);
        float textTop = origin.Y + 15f * s;
        for (int i = 0; i < lines.Count; i++)
            GameText.DrawCentered(dl, "GameFontNormal", lines[i],
                new Vector2(origin.X + 212f * s, textTop + pitch * (i + .5f)), s);

        string accept = confirmation.Kind == EnchantConfirmationKind.Bind ? "Okay" : "Yes";
        string decline = confirmation.Kind == EnchantConfirmationKind.Bind ? "Cancel" : "No";
        bool accepted = DrawEnchantPopupButton(dl, accept, origin + new Vector2(62f, 68f) * s, s);
        bool declined = DrawEnchantPopupButton(dl, decline, origin + new Vector2(198f, 68f) * s, s);
        ImGui.End();

        if (accepted) AcceptEnchantConfirmation();
        else if (declined) _enchantConfirmation = null;
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

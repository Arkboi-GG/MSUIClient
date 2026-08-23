using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private uint _itemRefEntry;
    private string _itemRefPayload = "";

    private void ActivateChatLink(UiTextLinkInfo link, ImGuiMouseButton button)
    {
        if (link.Payload.StartsWith("player:", StringComparison.OrdinalIgnoreCase))
        {
            string rawName = link.Payload[7..];
            string name = rawName.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault() ?? "";
            if (name.Length == 0) return;
            switch (ItemRefTooltipUiLaw.PlayerAction(ImGui.GetIO().KeyShift,
                        _chatEditOpen, button == ImGuiMouseButton.Right))
            {
                case ItemRefClickAction.InsertPlayerName:
                    InsertChatText(name);
                    break;
                case ItemRefClickAction.OpenFriendMenu:
                    OpenFriendPopup(name, ImGui.GetIO().MousePos);
                    break;
                case ItemRefClickAction.Whisper:
                    OpenChatEditWith($"/w {name} ");
                    break;
            }
            return;
        }

        if (!link.Payload.StartsWith("item:", StringComparison.OrdinalIgnoreCase)) return;
        string entryText = link.Payload[5..].Split(':', 2)[0];
        if (!uint.TryParse(entryText, out uint entry) || entry == 0) return;
        string itemMarkup = link.FullMarkup.Length > 0 ? link.FullMarkup : link.Markup;
        switch (ItemRefTooltipUiLaw.ItemAction(ImGui.GetIO().KeyCtrl,
                    ImGui.GetIO().KeyShift, _chatEditOpen, itemMarkup.Length > 0))
        {
            case ItemRefClickAction.DressUp:
                TryOnDressUp(entry);
                return;
            case ItemRefClickAction.InsertItemLink:
                InsertChatText(itemMarkup);
                return;
            case ItemRefClickAction.None:
                return;
        }
        _itemRefEntry = entry;
        _itemRefPayload = link.Payload;
        if (_items is not null && _net is not null) _items.Require(entry, 0, _net);
        EmitInterface("item-ref", "open", "OPENED", 0,
            $"entry={entry};control={ImGui.GetIO().KeyCtrl};payload={SanitizeEvidence(link.Payload)}");
    }

    private void DrawItemRefTooltip()
    {
        if (_itemRefEntry == 0 || _gameplayArt is null || _skin is null) return;
        ItemTooltipBodySnapshot? prepared = null;
        if (_items?.TryGet(_itemRefEntry, out ItemTemplate? item) == true && item is not null)
            prepared = PrepareItemTooltipBodySnapshot(item, 1);

        string loading = $"Retrieving item information ({_itemRefEntry})";
        int lineCount = prepared?.Operations.Length ?? 1;
        float contentWidth = prepared is { } body
            ? body.Operations.Select(operation => GameText.MeasureWidth("GameTooltipText",
                operation.Text, 1f)).DefaultIfEmpty(0).Max()
            : GameText.MeasureWidth("GameTooltipText", loading, 1f);
        Vector2 size = ItemRefTooltipUiLaw.Size(contentWidth, lineCount);
        float scale = GameplayUiScale();
        Vector2 logicalDisplay = ImGui.GetIO().DisplaySize / scale;
        Vector2 logicalOrigin = ItemRefTooltipUiLaw.Origin(logicalDisplay, size);
        if (!BeginVanillaWindow("##item-ref-tooltip", logicalOrigin, size,
                out ImDrawListPtr draw, out Vector2 origin, out float s)) { ImGui.End(); return; }
        _skin.DrawBackdrop(draw, origin, origin + size * s, WowSkin.Tooltip);

        float y = ItemRefTooltipUiLaw.Padding;
        if (prepared is { } snapshot)
        {
            foreach (PreparedItemTooltipPaintOp operation in snapshot.Operations)
            {
                string font = operation.Kind == PreparedItemTooltipPaintKind.Disabled
                    ? "GameFontDisableSmall" : "GameTooltipText";
                uint? color = operation.Kind == PreparedItemTooltipPaintKind.Colored
                    ? ImGui.ColorConvertFloat4ToU32(operation.Color) : null;
                GameText.Draw(draw, font, operation.Text,
                    ItemRefTooltipUiLaw.LinePosition(origin, y, s), s, color);
                y += ItemRefTooltipUiLaw.LinePitch;
            }
        }
        else
        {
            GameText.Draw(draw, "GameTooltipText", loading,
                ItemRefTooltipUiLaw.LinePosition(origin, y, s), s);
        }

        DrawImageButton(draw, "##item-ref-close",
            origin + ItemRefTooltipUiLaw.CloseOrigin(size) * s,
            ItemRefTooltipUiLaw.Close.Size * s,
            @"Interface\Buttons\UI-Panel-MinimizeButton-Up",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Down",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Highlight");
        if (ImGui.IsItemClicked()) CloseItemRefTooltip();
        ImGui.End();
    }

    private bool CloseItemRefTooltip()
    {
        if (_itemRefEntry == 0) return false;
        uint entry = _itemRefEntry;
        _itemRefEntry = 0;
        _itemRefPayload = "";
        EmitInterface("item-ref", "close", "CLOSED", 0, $"entry={entry}");
        return true;
    }
}

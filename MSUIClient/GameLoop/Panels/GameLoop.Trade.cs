using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private sealed record TradeItem(uint Entry, uint Count, uint MaxDurability, uint Durability);
    private bool _tradeOpen;
    private bool _tradeAccepted;
    private bool _tradePartnerAccepted;
    private ulong _tradePartnerGuid;
    private ulong _tradeInviteGuid;
    private int _tradeMoney;
    private uint _tradePartnerMoney;
    private uint _tradeMineEnchantSpell;
    private uint _tradePartnerEnchantSpell;
    private int _tradePlaceSlot = -1;
    private readonly TradeItem?[] _tradeMine = new TradeItem?[7];
    private readonly TradeItem?[] _tradeTheirs = new TradeItem?[7];

    private void ApplyTradeStatus(byte[] body)
    {
        TradePackets.Status wire = TradePackets.ParseStatus(body);
        uint status = wire.Code;
        string statusPartner = _playerNames.GetValueOrDefault(_tradePartnerGuid, "That player");
        if (TradeFrameUiLaw.ErrorForStatus(status) is { } statusError)
            ShowUiError(TradeFrameUiLaw.FormatStatusError(statusError, statusPartner,
                InventoryGlobalString));
        if (TradeFrameUiLaw.StatusCloses(status))
        {
            ResetTrade();
        }
        else switch (status)
        {
            case 1:
                ulong initiator = wire.Partner;
                TradeFrameUiLaw.IncomingRequestAction action = TradeFrameUiLaw.IncomingRequest(
                    _ignored.Contains(initiator), _tradeOpen || _tradeInviteGuid != 0);
                if (action == TradeFrameUiLaw.IncomingRequestAction.Ignore)
                {
                    _net?.IgnoreTrade();
                    break;
                }
                if (action == TradeFrameUiLaw.IncomingRequestAction.Busy)
                {
                    _net?.BusyTrade();
                    break;
                }
                _tradeInviteGuid = initiator;
                if (!_playerNames.ContainsKey(_tradeInviteGuid)) _net?.NameQuery(_tradeInviteGuid);
                break;
            case 2:
                _tradeInviteGuid = 0; _tradeOpen = true; _tradeAccepted = _tradePartnerAccepted = false;
                break;
            case 4: _tradePartnerAccepted = true; break;
            case 7: _tradeAccepted = _tradePartnerAccepted = false; break;
        }
        EmitInterface("trade", "status", "DECODED", _tradePartnerGuid,
            $"status={status};open={_tradeOpen};invite=0x{_tradeInviteGuid:X16};bytes={body.Length}");
    }

    private void ApplyTradeExtended(byte[] body)
    {
        TradePackets.Extended wire = TradePackets.ParseExtended(body);
        bool partner = wire.TheirWindow;
        TradeItem?[] target = partner ? _tradeTheirs : _tradeMine;
        Array.Clear(target);
        for (int i = 0; i < wire.Slots.Length; i++)
        {
            if (wire.Slots[i] is TradePackets.Item item)
            {
                target[i] = new(item.Entry, item.Count, item.MaxDurability, item.Durability);
                _items?.Require(item.Entry, 0, _net!);
            }
        }
        if (partner)
        {
            _tradePartnerMoney = wire.Gold;
            _tradePartnerEnchantSpell = wire.EnchantSpellId;
        }
        else
        {
            _tradeMoney = (int)Math.Min(int.MaxValue, wire.Gold);
            _tradeMineEnchantSpell = wire.EnchantSpellId;
        }
        _tradeAccepted = _tradePartnerAccepted = false;
    }

    private void ResetTrade()
    {
        _tradeOpen = false; _tradeInviteGuid = 0; _tradePartnerGuid = 0;
        _tradeMoney = 0; _tradePartnerMoney = 0; _tradePlaceSlot = -1;
        _tradeMineEnchantSpell = _tradePartnerEnchantSpell = 0;
        _tradeAccepted = _tradePartnerAccepted = false; Array.Clear(_tradeMine); Array.Clear(_tradeTheirs);
    }

    private bool PlaceTradeItem(byte bag, byte slot)
    {
        if (!_tradeOpen || _tradePlaceSlot is < 0 or > 6 || _net is null) return false;
        bool sent = _net.SetTradeItem((byte)_tradePlaceSlot, bag, slot);
        if (sent) _tradePlaceSlot = -1;
        return sent;
    }

    private void DrawTradeFrame()
    {
        if (_tradeInviteGuid != 0) DrawTradeInvitation();
        if (!_tradeOpen || _gameplayArt is null) return;
        if (!BeginVanillaWindow("##trade", TradeFrameUiLaw.FrameOrigin(1f),
                TradeFrameUiLaw.FrameSize(1f),
                out ImDrawListPtr dl, out Vector2 origin, out float s)) { ImGui.End(); return; }
        if (_entities.TryGet(ControlledGuid, out WorldEntity player))
            DrawUnitPortraitImage(dl, player,
                origin + TradeFrameUiLaw.PlayerPortrait.Min * s,
                TradeFrameUiLaw.PlayerPortrait.Width * s, 0, true);
        if (_entities.TryGet(_tradePartnerGuid, out WorldEntity recipient))
            DrawUnitPortraitImage(dl, recipient,
                origin + TradeFrameUiLaw.RecipientPortrait.Min * s,
                TradeFrameUiLaw.RecipientPortrait.Width * s, 0, false);
        DrawFourPieceShell(dl, origin, s,
            @"Interface\TradeFrame\UI-TradeFrame-TopLeft", @"Interface\TradeFrame\UI-TradeFrame-TopRight",
            @"Interface\TradeFrame\UI-TradeFrame-BotLeft", @"Interface\TradeFrame\UI-TradeFrame-BotRight");
        string partner = _playerNames.GetValueOrDefault(_tradePartnerGuid, "Trade Partner");
        GameText.Draw(dl, "GameFontNormal", _net?.PlayerName ?? "Player",
            origin + TradeFrameUiLaw.PlayerName.Min * s, s);
        GameText.Draw(dl, "GameFontNormal", partner,
            origin + TradeFrameUiLaw.RecipientName.Min * s, s);
        DrawTradeAcceptHighlight(dl, origin, s, playerSide: true, _tradeAccepted);
        DrawTradeAcceptHighlight(dl, origin, s, playerSide: false, _tradePartnerAccepted);
        GameText.Draw(dl, "GameFontHighlightSmall", TradeFrameUiLaw.EnchantLabel,
            origin + TradeFrameUiLaw.PlayerEnchantLabel * s, s);
        GameText.Draw(dl, "GameFontHighlightSmall", TradeFrameUiLaw.EnchantLabel,
            origin + TradeFrameUiLaw.RecipientEnchantLabel * s, s);
        for (int i = 0; i < 7; i++)
        {
            DrawTradeSlot(dl, origin, s, _tradeMine[i], i, true);
            DrawTradeSlot(dl, origin, s, _tradeTheirs[i], i, false);
        }
        GameText.Draw(dl, "GameFontHighlightSmall", FormatMoney(_tradePartnerMoney),
            origin + TradeFrameUiLaw.RecipientMoney * s, s);
        if (VanillaInputInt(dl,"##trade-money",ref _tradeMoney,
                origin + TradeFrameUiLaw.PlayerMoneyInput.Min * s,
                TradeFrameUiLaw.PlayerMoneyInput.Size, s))
        { _tradeMoney = Math.Max(0, _tradeMoney); _net?.SetTradeGold((uint)_tradeMoney); }
        if (VanillaButton(dl, "##trade-accept", _tradeAccepted ? "Accepted" : "Trade",
                origin + TradeFrameUiLaw.TradeButton.Min * s,
                TradeFrameUiLaw.TradeButton.Size, s, !_tradeAccepted))
        { _tradeAccepted = _net?.AcceptTrade() == true; }
        if (VanillaButton(dl, "##trade-cancel", "Cancel",
                origin + TradeFrameUiLaw.CancelButton.Min * s,
                TradeFrameUiLaw.CancelButton.Size, s))
        {
            if (TradeFrameUiLaw.CancelClick(_tradeAccepted) ==
                TradeFrameUiLaw.CancelAction.Unaccept)
            {
                if (_net?.UnacceptTrade() == true) _tradeAccepted = false;
            }
            else
            {
                _net?.CancelTrade();
                ResetTrade();
            }
        }
        DrawImageButton(dl, "##trade-close", origin + TradeFrameUiLaw.CloseButton.Min * s,
            TradeFrameUiLaw.CloseButton.Size * s,
            @"Interface\Buttons\UI-Panel-MinimizeButton-Up", @"Interface\Buttons\UI-Panel-MinimizeButton-Down",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Highlight");
        if (ImGui.IsItemClicked()) { _net?.CancelTrade(); ResetTrade(); }
        ImGui.End();
    }

    private void DrawTradeAcceptHighlight(ImDrawListPtr dl, Vector2 origin, float s,
        bool playerSide, bool accepted)
    {
        if (!accepted) return;
        DrawTradeHighlightSlices(dl, origin, s, playerSide
            ? TradeFrameUiLaw.PlayerHighlight : TradeFrameUiLaw.RecipientHighlight);
        DrawTradeHighlightSlices(dl, origin, s, playerSide
            ? TradeFrameUiLaw.PlayerEnchantHighlight : TradeFrameUiLaw.RecipientEnchantHighlight);
    }

    private void DrawTradeHighlightSlices(ImDrawListPtr dl, Vector2 origin, float s,
        TradeFrameUiLaw.LogicalRect logical)
    {
        uint art = _gameplayArt?.AdditiveHandle(TradeFrameUiLaw.HighlightPath) ?? 0;
        if (art == 0) return;
        Vector2 min = origin + logical.Min * s;
        Vector2 max = min + logical.Size * s;
        float cap = 16 * s;
        dl.AddImage((nint)art, min, new Vector2(max.X, min.Y + cap),
            Vector2.Zero, new Vector2(.62890625f, .0625f));
        dl.AddImage((nint)art, new Vector2(min.X, min.Y + cap),
            new Vector2(max.X, max.Y - cap), new Vector2(0, .0625f),
            new Vector2(.62890625f, .9375f));
        dl.AddImage((nint)art, new Vector2(min.X, max.Y - cap), max,
            new Vector2(0, .9375f), new Vector2(.62890625f, 1));
    }

    private void DrawTradeSlot(ImDrawListPtr dl, Vector2 origin, float s, TradeItem? row,
        int slot, bool mine)
    {
        TradeFrameUiLaw.LogicalRect button = TradeFrameUiLaw.SlotButton(mine, slot);
        Vector2 min = origin + button.Min * s;
        Vector2 max = min + button.Size * s;
        TradeFrameUiLaw.LogicalRect empty = TradeFrameUiLaw.EmptySlot(mine, slot);
        DrawArt(dl, TradeFrameUiLaw.EmptySlotPath, origin + empty.Min * s, empty.Size, s);
        TradeFrameUiLaw.LogicalRect nameFrame = TradeFrameUiLaw.NameFrame(mine, slot);
        DrawArt(dl, TradeFrameUiLaw.ItemNameFramePath, origin + nameFrame.Min * s,
            nameFrame.Size, s);
        ItemTemplate? item = null;
        if (row is not null && _items?.TryGet(row.Entry, out ItemTemplate? resolved) == true &&
            resolved is not null)
        {
            item = resolved;
            uint icon = _gameplayArt?.Handle(item.IconPath) ?? 0;
            if (icon != 0) dl.AddImage((nint)icon, min, max);
            bool enchantSlot = slot == TradeFrameUiLaw.SlotCount - 1;
            uint enchantSpell = mine ? _tradeMineEnchantSpell : _tradePartnerEnchantSpell;
            string? enchantName = enchantSlot && enchantSpell != 0 &&
                _spellCatalog?.TryGet(enchantSpell, out SpellInfo proposed) == true
                ? proposed.Name : null;
            TradeFrameUiLaw.SlotText slotText = TradeFrameUiLaw.ItemSlotText(
                item.Name, enchantSlot, enchantName);
            GameText.Draw(dl, "GameFontNormalSmall", slotText.Text,
                origin + TradeFrameUiLaw.NameText(mine, slot) * s, s,
                slotText.Color);
            if (row.Count > 1)
                GameText.DrawRightAligned(dl, "NumberFontNormal", row.Count.ToString(),
                    max - new Vector2(2, GameText.EmPixels("NumberFontNormal", s) + 2 * s), s);
        }
        if (slot == TradeFrameUiLaw.SlotCount - 1)
        {
            TradeFrameUiLaw.LogicalRect enchant = TradeFrameUiLaw.EnchantIcon(mine);
            DrawArt(dl, TradeFrameUiLaw.EnchantIconPath, origin + enchant.Min * s,
                enchant.Size, s);
        }
        ImGui.SetCursorScreenPos(min);
        ImGui.InvisibleButton($"##trade-{(mine ? "mine" : "theirs")}-{slot}", button.Size * s);
        bool hovered = ImGui.IsItemHovered();
        if (hovered)
        {
            uint highlight = _gameplayArt?.BrightHighlightHandle(
                @"Interface\Buttons\ButtonHilight-Square") ?? 0;
            if (highlight != 0) dl.AddImage((nint)highlight, min, max);
            if (row is not null && item is not null)
                OfferPreparedItemTooltip(new($"item:trade-{(mine ? "player" : "target")}",
                    (ulong)(slot + 1)), PrepareItemTooltipBodySnapshot(item, row.Count,
                    row.Durability, row.MaxDurability));
        }
        if (mine && ImGui.IsItemClicked())
        {
            if (row is null) _tradePlaceSlot = slot;
            else { _net?.ClearTradeItem((byte)slot); _tradePlaceSlot = -1; }
        }
    }

    private void DrawTradeInvitation()
    {
        float s = GameplayUiScale();
        TradeInvitationUiLaw.ScreenRect frame = TradeInvitationUiLaw.PopupRect(
            ImGui.GetIO().DisplaySize, s);
        if (!BeginVanillaWindow("##trade-invite", frame.Min, frame.Size,
                out ImDrawListPtr dl, out Vector2 p, out s)) { ImGui.End(); return; }
        dl.AddRectFilled(p, p + new Vector2(TradeInvitationUiLaw.Width,
            TradeInvitationUiLaw.Height) * s, 0xee101010, 8f * s);
        string name = _playerNames.GetValueOrDefault(_tradeInviteGuid, "Another player");
        DrawCenteredText(dl, p + TradeInvitationUiLaw.MessageCenter * s,
            $"{name} wants to trade with you.", 11f * s, 0xffffffff);
        if (VanillaButton(dl, "##trade-invite-accept", "Accept",
                p + TradeInvitationUiLaw.Accept * s, TradeInvitationUiLaw.ButtonSize, s))
        { _tradePartnerGuid = _tradeInviteGuid; _net?.BeginTrade(); _tradeInviteGuid = 0; }
        if (VanillaButton(dl, "##trade-invite-decline", "Cancel",
                p + TradeInvitationUiLaw.Decline * s, TradeInvitationUiLaw.ButtonSize, s))
        { _net?.BusyTrade(); _tradeInviteGuid = 0; }
        ImGui.End();
    }
}

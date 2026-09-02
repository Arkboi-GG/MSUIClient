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
    private int _tradeMoney;
    private uint _tradePartnerMoney;
    private uint _tradeMineEnchantSpell;
    private uint _tradePartnerEnchantSpell;
    private int _tradePlaceSlot = -1;
    private readonly TradeItem?[] _tradeMine = new TradeItem?[7];
    private readonly TradeItem?[] _tradeTheirs = new TradeItem?[7];

    /// <summary>Our own CMSG_INITIATE_TRADE is in flight — the reply is not a request to answer.</summary>
    private bool _tradeInitiateInFlight;

    /// <summary>Ask another player to trade (the unit popup's Trade row).</summary>
    private bool RequestTradeWith(ulong guid)
    {
        bool sent = _net?.InitiateTrade(guid) == true;
        if (sent) { _tradeInitiateInFlight = true; _tradePartnerGuid = guid; }
        return sent;
    }

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
                AnswerTradeRequest(wire.Partner);
                break;
            case 2:
                _tradeOpen = true; _tradeAccepted = _tradePartnerAccepted = false;
                break;
            case 4: _tradePartnerAccepted = true; break;
            case 7: _tradeAccepted = _tradePartnerAccepted = false; break;
        }
        EmitInterface("trade", "status", "DECODED", _tradePartnerGuid,
            $"status={status};open={_tradeOpen};bytes={body.Length}");
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

    /// <summary>
    /// TRADE_STATUS_BEGIN_TRADE — someone wants to trade. The reference's ladder, in order:
    /// our own initiate in flight (the echo of our request: answer nothing), the initiator on
    /// the ignore list (CMSG_IGNORE_TRADE), not a streamed player (nothing), we are dead, our
    /// body is not ours to author, the auction house is open (CMSG_BUSY_TRADE); only then
    /// CMSG_BEGIN_TRADE. Every incoming request used to be accepted unconditionally, and a
    /// request landing mid-trade overwrote the partner. Reported 2026-09-01.
    /// </summary>
    private void AnswerTradeRequest(ulong initiator)
    {
        if (_tradeInitiateInFlight)
        {
            // The server echoes BEGIN_TRADE (partner guid 0) to the initiator; the window
            // opens on OPEN_WINDOW once the other side accepts. Nothing to answer.
            EmitInterface("trade", "request", "OWN-INITIATE", _tradePartnerGuid, "reply=none");
            return;
        }
        if (_tradeOpen)
        {
            // A request cannot address a player mid-trade (the server refuses it as BUSY),
            // but a stale one must never replace the live partner.
            _net?.BusyTrade();
            EmitInterface("trade", "request", "BUSY-IN-TRADE", initiator, "reply=CMSG_BUSY_TRADE");
            return;
        }
        if (_ignored.Contains(initiator))
        {
            _net?.IgnoreTrade();
            EmitInterface("trade", "request", "IGNORED", initiator, "reply=CMSG_IGNORE_TRADE");
            return;
        }
        if (!_entities.TryGet(initiator, out WorldEntity who) || !who.IsPlayer)
        {
            EmitInterface("trade", "request", "UNSTREAMED", initiator, "reply=none");
            return;
        }
        bool selfDead = _entities.TryGet(ControlledGuid, out WorldEntity self) && self.IsDead;
        if (selfDead || !CanAuthorControlledOrSelf || _auctionOpen)
        {
            _net?.BusyTrade();
            EmitInterface("trade", "request", "BUSY", initiator,
                $"reply=CMSG_BUSY_TRADE;dead={selfDead};authorable={CanAuthorControlledOrSelf};auction={_auctionOpen}");
            return;
        }
        _tradePartnerGuid = initiator;
        if (!_playerNames.ContainsKey(_tradePartnerGuid)) _net?.NameQuery(_tradePartnerGuid);
        _net?.BeginTrade();
        EmitInterface("trade", "request", "ACCEPTED", initiator, "reply=CMSG_BEGIN_TRADE");
    }

    private void ResetTrade()
    {
        _tradeInitiateInFlight = false;
        _tradeOpen = false; _tradePartnerGuid = 0;
        _tradeMoney = 0; _tradePartnerMoney = 0; _tradePlaceSlot = -1;
        _tradeMineEnchantSpell = _tradePartnerEnchantSpell = 0;
        _tradeAccepted = _tradePartnerAccepted = false; Array.Clear(_tradeMine); Array.Clear(_tradeTheirs);
    }

    private bool PlaceTradeItem(byte bag, byte slot, WorldEntity? instance)
    {
        if (!_tradeOpen || _tradePlaceSlot is < 0 or > 6 || _net is null) return false;
        bool sent = _net.SetTradeItem((byte)_tradePlaceSlot, bag, slot);
        if (sent)
        {
            // OUR column is filled here, not off the wire: vmangos echoes
            // SMSG_TRADE_STATUS_EXTENDED only to the PARTNER (TradeData::SetItem → Update(true)),
            // so waiting for a their_window=0 frame left our six slots blank and made the
            // occupied-slot click (CMSG_CLEAR_TRADE_ITEM) unreachable. Reported 2026-09-01.
            if (instance is not null)
                _tradeMine[_tradePlaceSlot] = new(instance.Entry, Math.Max(1, instance.Fields.ItemStackCount),
                    instance.Fields.ItemMaxDurability, instance.Fields.ItemDurability);
            _tradeAccepted = _tradePartnerAccepted = false;
            _tradePlaceSlot = -1;
        }
        return sent;
    }

    private void DrawTradeFrame()
    {
        if (!_tradeOpen || _gameplayArt is null) return;
        if (!BeginVanillaWindow("##trade", UiPanelFrameLogicalOrigin(UiPanelOwnershipRegistry[3]),
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
        DrawTradeMoneyInputs(dl, origin, s);
        DrawTradeMoney(dl, _tradePartnerMoney,
            origin + TradeFrameUiLaw.RecipientMoneyRightTop * s, s);
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

    private void DrawTradeMoneyInputs(ImDrawListPtr dl, Vector2 origin, float s)
    {
        (int gold, int silver, int copper) = TradeFrameUiLaw.SplitMoney(_tradeMoney);
        bool changed = VanillaInputInt(dl, "##trade-money-gold", ref gold,
            origin + TradeFrameUiLaw.PlayerGoldInput.Min * s,
            TradeFrameUiLaw.PlayerGoldInput.Size, s);
        DrawTradeCoin(dl, 0, origin + TradeFrameUiLaw.PlayerGoldCoin * s, s);
        changed |= VanillaInputInt(dl, "##trade-money-silver", ref silver,
            origin + TradeFrameUiLaw.PlayerSilverInput.Min * s,
            TradeFrameUiLaw.PlayerSilverInput.Size, s);
        DrawTradeCoin(dl, 1, origin + TradeFrameUiLaw.PlayerSilverCoin * s, s);
        changed |= VanillaInputInt(dl, "##trade-money-copper", ref copper,
            origin + TradeFrameUiLaw.PlayerCopperInput.Min * s,
            TradeFrameUiLaw.PlayerCopperInput.Size, s);
        DrawTradeCoin(dl, 2, origin + TradeFrameUiLaw.PlayerCopperCoin * s, s);
        if (!changed) return;
        _tradeMoney = TradeFrameUiLaw.ComposeMoney(gold, silver, copper);
        if (_net?.SetTradeGold((uint)_tradeMoney) == true)
            _tradeAccepted = _tradePartnerAccepted = false;   // any change un-accepts both sides
    }

    private void DrawTradeMoney(ImDrawListPtr dl, uint copper, Vector2 rightTop, float s)
    {
        IReadOnlyList<MailUiLaw.MoneyDenomination> denominations = MailUiLaw.Money(copper);
        float width = denominations.Sum(denomination =>
            GameText.MeasureWidth(TradeFrameUiLaw.MoneyFont,
                denomination.Value.ToString(), s) + TradeFrameUiLaw.MoneyIconSize * s) +
            Math.Max(0, denominations.Count - 1) * TradeFrameUiLaw.MoneyGap * s;
        float x = rightTop.X - width;
        float textTop = GameText.BoxCenteredTop(TradeFrameUiLaw.MoneyFont, rightTop.Y,
            TradeFrameUiLaw.MoneyIconSize, s);
        foreach (MailUiLaw.MoneyDenomination denomination in denominations)
        {
            string text = denomination.Value.ToString();
            GameText.Draw(dl, TradeFrameUiLaw.MoneyFont, text, new(x, textTop), s);
            x += GameText.MeasureWidth(TradeFrameUiLaw.MoneyFont, text, s);
            DrawTradeCoin(dl, denomination.Icon, new(x, rightTop.Y), s);
            x += (TradeFrameUiLaw.MoneyIconSize + TradeFrameUiLaw.MoneyGap) * s;
        }
    }

    private void DrawTradeCoin(ImDrawListPtr dl, int denomination, Vector2 min, float s)
    {
        uint art = _gameplayArt?.Handle(TradeFrameUiLaw.MoneyIconPath) ?? 0;
        if (art == 0) return;
        dl.AddImage((nint)art, min, min + TradeFrameUiLaw.CoinSize(s),
            TradeFrameUiLaw.CoinUvMin(denomination),
            TradeFrameUiLaw.CoinUvMax(denomination));
    }

    private void DrawTradeHighlightSlices(ImDrawListPtr dl, Vector2 origin, float s,
        TradeFrameUiLaw.LogicalRect logical)
    {
        uint art = _gameplayArt?.AdditiveHandle(TradeFrameUiLaw.HighlightPath) ?? 0;
        if (art == 0) return;
        foreach (TradeFrameUiLaw.TextureSlice slice in
                 TradeFrameUiLaw.HighlightSlices(logical))
        {
            Vector2 min = origin + slice.Rect.Min * s;
            Vector2 max = min + slice.Rect.Size * s;
            dl.AddImage((nint)art, min, max, slice.UvMin, slice.UvMax);
        }
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
                    TradeFrameUiLaw.CountPosition(max,
                        GameText.EmPixels("NumberFontNormal", s), s), s);
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
            else if (_net?.ClearTradeItem((byte)slot) == true)
            {
                _tradeMine[slot] = null;
                _tradeAccepted = _tradePartnerAccepted = false;
                _tradePlaceSlot = -1;
            }
        }
    }

}

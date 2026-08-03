using System.Numerics;
using ImGuiNET;
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
    private int _tradePlaceSlot = -1;
    private readonly TradeItem?[] _tradeMine = new TradeItem?[7];
    private readonly TradeItem?[] _tradeTheirs = new TradeItem?[7];

    private void ApplyTradeStatus(byte[] body)
    {
        var r = new PacketReader(body); uint status = r.ReadU32();
        switch (status)
        {
            case 1:
                _tradeInviteGuid = r.ReadU64();
                if (!_playerNames.ContainsKey(_tradeInviteGuid)) _net?.NameQuery(_tradeInviteGuid);
                break;
            case 2:
                _tradeInviteGuid = 0; _tradeOpen = true; _tradeAccepted = _tradePartnerAccepted = false;
                break;
            case 3 or 8 or 9 or 12 or 13:
                ResetTrade();
                break;
            case 4: _tradePartnerAccepted = true; break;
            case 7: _tradeAccepted = _tradePartnerAccepted = false; break;
        }
        EmitInterface("trade", "status", "DECODED", _tradePartnerGuid,
            $"status={status};open={_tradeOpen};invite=0x{_tradeInviteGuid:X16};bytes={body.Length}");
    }

    private void ApplyTradeExtended(byte[] body)
    {
        var r = new PacketReader(body); bool partner = r.ReadU8() != 0;
        uint slots = r.ReadU32(); r.ReadU32(); uint money = r.ReadU32(); r.ReadU32();
        TradeItem?[] target = partner ? _tradeTheirs : _tradeMine;
        Array.Clear(target);
        for (int i = 0; i < slots && r.Remaining >= 61; i++)
        {
            byte slot = r.ReadU8(); uint entry = r.ReadU32(); r.ReadU32(); uint count = r.ReadU32();
            r.ReadU32(); r.ReadU64(); r.ReadU32(); r.ReadU64(); r.ReadU32(); r.ReadU32(); r.ReadU32(); r.ReadU32();
            uint maxDurability = r.ReadU32(); uint durability = r.ReadU32();
            if (slot < 7 && entry != 0)
            {
                target[slot] = new(entry, count, maxDurability, durability);
                _items?.Require(entry, 0, _net!);
            }
        }
        if (partner) _tradePartnerMoney = money; else _tradeMoney = (int)Math.Min(int.MaxValue, money);
        _tradeAccepted = _tradePartnerAccepted = false;
    }

    private void ResetTrade()
    {
        _tradeOpen = false; _tradeInviteGuid = 0; _tradePartnerGuid = 0;
        _tradeMoney = 0; _tradePartnerMoney = 0; _tradePlaceSlot = -1;
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
        if (!BeginVanillaWindow("##trade", new Vector2(0, 104), new Vector2(384, 512),
                out ImDrawListPtr dl, out Vector2 origin, out float s)) { ImGui.End(); return; }
        DrawFourPieceShell(dl, origin, s,
            @"Interface\TradeFrame\UI-TradeFrame-TopLeft", @"Interface\TradeFrame\UI-TradeFrame-TopRight",
            @"Interface\TradeFrame\UI-TradeFrame-BotLeft", @"Interface\TradeFrame\UI-TradeFrame-BotRight");
        DrawCenteredText(dl, origin + new Vector2(192, 18) * s, "Trade", 14f * s, VanillaGold);
        string partner = _playerNames.GetValueOrDefault(_tradePartnerGuid, "Trade Partner");
        DrawCenteredText(dl, origin + new Vector2(101, 68) * s, _net?.PlayerName ?? "Player", 11f * s, 0xffffffff);
        DrawCenteredText(dl, origin + new Vector2(279, 68) * s, partner, 11f * s, 0xffffffff);
        for (int i = 0; i < 7; i++)
        {
            DrawTradeSlot(dl, origin + new Vector2(42, 91 + i * 43) * s, s, _tradeMine[i], i, true);
            DrawTradeSlot(dl, origin + new Vector2(220, 91 + i * 43) * s, s, _tradeTheirs[i], i, false);
        }
        dl.AddText(ImGui.GetFont(), 10f * s, origin + new Vector2(41, 398) * s, VanillaGold, $"Money: {FormatMoney((uint)Math.Max(0, _tradeMoney))}");
        dl.AddText(ImGui.GetFont(), 10f * s, origin + new Vector2(219, 398) * s, VanillaGold, $"Money: {FormatMoney(_tradePartnerMoney)}");
        if (VanillaInputInt(dl,"##trade-money",ref _tradeMoney,
                origin+new Vector2(41,415)*s,new Vector2(120,22),s))
        { _tradeMoney = Math.Max(0, _tradeMoney); _net?.SetTradeGold((uint)_tradeMoney); }
        if (VanillaButton(dl, "##trade-accept", _tradeAccepted ? "Accepted" : "Trade",
                origin + new Vector2(89, 459) * s, new Vector2(80, 22), s, !_tradeAccepted))
        { _tradeAccepted = _net?.AcceptTrade() == true; }
        if (VanillaButton(dl, "##trade-cancel", "Cancel", origin + new Vector2(217, 459) * s,
                new Vector2(80, 22), s)) { _net?.CancelTrade(); ResetTrade(); }
        if (_tradePartnerAccepted)
            DrawCenteredText(dl, origin + new Vector2(279, 443) * s, "Trade accepted", 10f * s, 0xff40ff40);
        DrawImageButton(dl, "##trade-close", origin + new Vector2(326, 14) * s, new Vector2(32) * s,
            @"Interface\Buttons\UI-Panel-MinimizeButton-Up", @"Interface\Buttons\UI-Panel-MinimizeButton-Down",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Highlight");
        if (ImGui.IsItemClicked()) { _net?.CancelTrade(); ResetTrade(); }
        ImGui.End();
    }

    private void DrawTradeSlot(ImDrawListPtr dl, Vector2 min, float s, TradeItem? row, int slot, bool mine)
    {
        uint ring = _gameplayArt?.Handle(@"Interface\Buttons\UI-Quickslot2") ?? 0;
        if (row is not null && _items?.TryGet(row.Entry, out ItemTemplate? item) == true && item is not null)
        {
            uint icon = _gameplayArt?.Handle(item.IconPath) ?? 0;
            if (icon != 0) dl.AddImage((nint)icon, min, min + new Vector2(37) * s);
        }
        if (ring != 0) dl.AddImage((nint)ring, min - new Vector2(14) * s, min + new Vector2(50) * s);
        ImGui.SetCursorScreenPos(min); ImGui.InvisibleButton($"##trade-{(mine ? "mine" : "theirs")}-{slot}", new Vector2(37) * s);
        if (mine && ImGui.IsItemClicked())
        {
            if (row is null) _tradePlaceSlot = slot;
            else { _net?.ClearTradeItem((byte)slot); _tradePlaceSlot = -1; }
        }
    }

    private void DrawTradeInvitation()
    {
        float s = GameplayUiScale(); Vector2 origin = new(374, 270);
        if (!BeginVanillaWindow("##trade-invite", origin, new Vector2(276, 120),
                out ImDrawListPtr dl, out Vector2 p, out s)) { ImGui.End(); return; }
        dl.AddRectFilled(p, p + new Vector2(276, 120) * s, 0xee101010, 8f * s);
        string name = _playerNames.GetValueOrDefault(_tradeInviteGuid, "Another player");
        DrawCenteredText(dl, p + new Vector2(138, 32) * s, $"{name} wants to trade with you.", 11f * s, 0xffffffff);
        if (VanillaButton(dl, "##trade-invite-accept", "Accept", p + new Vector2(48, 72) * s, new Vector2(80, 22), s))
        { _tradePartnerGuid = _tradeInviteGuid; _net?.BeginTrade(); _tradeInviteGuid = 0; }
        if (VanillaButton(dl, "##trade-invite-decline", "Cancel", p + new Vector2(148, 72) * s, new Vector2(80, 22), s))
        { _net?.CancelTrade(); _tradeInviteGuid = 0; }
        ImGui.End();
    }
}

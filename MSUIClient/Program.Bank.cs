using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private bool _bankOpen;
    private ulong _bankSource;
    private BankBagSlotPriceTable? _bankPrices;
    private BankTransition? _pendingBankTransition;
    private byte _bankSlotCountBefore;

    private sealed record BankTransition(string Kind, ulong ItemGuid, uint Entry, int From, int To, double SentAt);

    private void InitBank()
    {
        if (_mpq?.ReadFile(BankBagSlotPriceTable.MpqPath) is { } bytes)
            _bankPrices = BankBagSlotPriceTable.Parse(bytes);
    }

    private bool RequestBank(ulong guid)
    {
        bool eligible = _entities.TryGet(guid, out WorldEntity banker) && banker.IsCreature &&
                        (banker.NpcFlags & NpcBanker) != 0;
        bool sent = eligible && _net?.BankerActivate(guid) == true;
        EmitInterface("bank", "open", sent ? "SENT" : "REFUSED", guid,
            $"eligible={eligible};npcFlags=0x{banker?.NpcFlags ?? 0:X8};body={Convert.ToHexString(WorldSession.BuildBankGuidBody(guid))}");
        return sent;
    }

    private void ApplyShowBank(byte[] body)
    {
        if (body.Length < 8) return;
        _bankSource = BitConverter.ToUInt64(body, 0); _bankOpen = true;
        int occupied = 0;
        if (_net is not null && _entities.TryGet(_net.PlayerGuid, out WorldEntity player))
            occupied = Enumerable.Range(0, 24).Count(i => player.Fields.PlayerBankSlot(i) != 0);
        EmitInterface("bank", "open", "OPEN", _bankSource, $"occupied={occupied};body={Convert.ToHexString(body.AsSpan(0, 8))}");
    }

    private void ApplyBuyBankSlotResult(byte[] body)
    {
        if (body.Length < 4) return;
        uint result = BitConverter.ToUInt32(body, 0);
        EmitInterface("bank", "buy-result", result == 0 ? "SUCCESS" : $"FAILED-{result}", _bankSource,
            $"result={result};slotBefore={_bankSlotCountBefore}");
    }

    private bool DepositBankEntry(uint entry)
    {
        if (!_bankOpen || _net is null || !_entities.TryGet(_net.PlayerGuid, out WorldEntity player)) return false;
        int from = Enumerable.Range(0, 16).FirstOrDefault(i =>
        {
            ulong guid = player.Fields.PlayerBackpackSlot(i);
            return guid != 0 && _entities.TryGet(guid, out WorldEntity item) && item.Entry == entry;
        }, -1);
        int to = Enumerable.Range(0, 24).FirstOrDefault(i => player.Fields.PlayerBankSlot(i) == 0, -1);
        if (from < 0 || to < 0) return false;
        ulong itemGuid = player.Fields.PlayerBackpackSlot(from);
        bool sent = _net.SwapItems(255, (byte)(39 + to), 255, (byte)(23 + from));
        EmitInterface("bank", "deposit-send", sent ? "SENT" : "SEND_FAILED", itemGuid,
            $"item={entry};from={23 + from};to={39 + to};body={Convert.ToHexString(WorldSession.BuildSwapItemsBody(255, (byte)(39 + to), 255, (byte)(23 + from)))}");
        if (sent) _pendingBankTransition = new("deposit", itemGuid, entry, 23 + from, 39 + to, NowSeconds());
        return sent;
    }

    private bool WithdrawBankEntry(uint entry)
    {
        if (!_bankOpen || _net is null || !_entities.TryGet(_net.PlayerGuid, out WorldEntity player)) return false;
        int from = Enumerable.Range(0, 24).FirstOrDefault(i =>
        {
            ulong guid = player.Fields.PlayerBankSlot(i);
            return guid != 0 && _entities.TryGet(guid, out WorldEntity item) && item.Entry == entry;
        }, -1);
        int to = Enumerable.Range(0, 16).FirstOrDefault(i => player.Fields.PlayerBackpackSlot(i) == 0, -1);
        if (from < 0 || to < 0) return false;
        ulong itemGuid = player.Fields.PlayerBankSlot(from);
        bool sent = _net.SwapItems(255, (byte)(23 + to), 255, (byte)(39 + from));
        EmitInterface("bank", "withdraw-send", sent ? "SENT" : "SEND_FAILED", itemGuid,
            $"item={entry};from={39 + from};to={23 + to};body={Convert.ToHexString(WorldSession.BuildSwapItemsBody(255, (byte)(23 + to), 255, (byte)(39 + from)))}");
        if (sent) _pendingBankTransition = new("withdraw", itemGuid, entry, 39 + from, 23 + to, NowSeconds());
        return sent;
    }

    private bool BuyNextBankSlot()
    {
        if (!_bankOpen || _net is null || !_entities.TryGet(_net.PlayerGuid, out WorldEntity player)) return false;
        _bankSlotCountBefore = player.Fields.BankBagSlotCount;
        bool sent = _net.BuyBankSlot(_bankSource);
        uint price = _bankPrices?.Price(_bankSlotCountBefore + 1) ?? 0;
        EmitInterface("bank", "buy-send", sent ? "SENT" : "SEND_FAILED", _bankSource,
            $"slot={_bankSlotCountBefore + 1};price={price};money={player.Fields.Coinage};body={Convert.ToHexString(WorldSession.BuildBankGuidBody(_bankSource))}");
        if (sent) _pendingBankTransition = new("buy", 0, 0, _bankSlotCountBefore, _bankSlotCountBefore + 1, NowSeconds());
        return sent;
    }

    private void ObserveBankTransition()
    {
        if (_pendingBankTransition is not { } pending || _net is null ||
            !_entities.TryGet(_net.PlayerGuid, out WorldEntity player)) return;
        bool complete = pending.Kind switch
        {
            "deposit" => Enumerable.Range(0, 24).Any(i => player.Fields.PlayerBankSlot(i) == pending.ItemGuid),
            "withdraw" => Enumerable.Range(0, 16).Any(i => player.Fields.PlayerBackpackSlot(i) == pending.ItemGuid),
            "buy" => player.Fields.BankBagSlotCount > pending.From,
            _ => false,
        };
        if (complete)
        {
            if (pending.Kind == "buy")
                EmitInterface("bank", "buy-result", "SUCCESS-BY-DELTA", _bankSource,
                    $"slotBefore={pending.From};slotAfter={player.Fields.BankBagSlotCount};serverSuccessHasNoResultPacket=true");
            EmitInterface("bank", pending.Kind == "buy" ? "bag-slot" : "transfer",
                pending.Kind == "deposit" ? "DEPOSITED" : pending.Kind == "withdraw" ? "WITHDRAWN" : "PURCHASED",
                pending.ItemGuid, $"item={pending.Entry};from={pending.From};to={pending.To};slotCount={player.Fields.BankBagSlotCount}");
            _pendingBankTransition = null;
        }
        else if (NowSeconds() - pending.SentAt > 5)
        {
            EmitInterface("bank", pending.Kind == "buy" ? "bag-slot" : "transfer", "TIMEOUT", pending.ItemGuid,
                $"kind={pending.Kind};item={pending.Entry};from={pending.From};to={pending.To}");
            _pendingBankTransition = null;
        }
    }

    private void DrawBankFrame()
    {
        if (!_bankOpen || _net is null || !_entities.TryGet(_net.PlayerGuid, out WorldEntity player)) return;
        ImGui.SetNextWindowPos(new Vector2(430, 90), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(510, 420), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Bank##bank", ref _bankOpen)) { ImGui.End(); return; }
        ImGui.TextUnformatted($"Bank slots — {Enumerable.Range(0, 24).Count(i => player.Fields.PlayerBankSlot(i) != 0)}/24 occupied");
        for (int i = 0; i < 24; i++)
        {
            ulong guid = player.Fields.PlayerBankSlot(i);
            string label = $"{39 + i}: Empty";
            if (guid != 0 && _entities.TryGet(guid, out WorldEntity item))
            {
                _items?.Require(item.Entry, guid, _net);
                label = _items?.TryGet(item.Entry, out ItemTemplate? t) == true && t is not null
                    ? $"{39 + i}: {t.Name} x{item.Fields.ItemStackCount}" : $"{39 + i}: Item {item.Entry}";
            }
            ImGui.Selectable(label + $"##bank-{i}");
        }
        ImGui.Separator();
        byte count = player.Fields.BankBagSlotCount;
        uint price = _bankPrices?.Price(count + 1) ?? 0;
        ImGui.TextUnformatted($"Bag slots purchased: {count}/6 — next: {FormatMoney(price)}");
        for (int i = 0; i < 6; i++) { if (i > 0) ImGui.SameLine(); ImGui.TextUnformatted(i < count ? "[Open]" : "[Locked]"); }
        if (count < 6 && ImGui.Button("Purchase next bag slot")) BuyNextBankSlot();
        if (_config.DevTools && ImGui.Button("Copy bank evidence"))
            CopyVerdictText(string.Join(Environment.NewLine, _verdicts.Snapshot("interface").OfType<InterfaceVerdict>()
                .Where(v => v.Family == "bank").Select(v => $"[verdict:interface] {v.ToLine()}")));
        ImGui.End();
    }
}

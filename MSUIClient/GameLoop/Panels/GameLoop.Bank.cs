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
        if (!_bankOpen || _net is null || _gameplayArt is null || !_entities.TryGet(_net.PlayerGuid, out WorldEntity player)) return;
        float s=GameplayUiScale(); Vector2 origin=new(0,104*s), logicalSize=new(384,512);
        ImGui.SetNextWindowPos(origin,ImGuiCond.Always); ImGui.SetNextWindowSize(logicalSize*s,ImGuiCond.Always); ImGui.SetNextWindowBgAlpha(0);
        if (!ImGui.Begin("##bank", ImGuiWindowFlags.NoDecoration|ImGuiWindowFlags.NoMove|ImGuiWindowFlags.NoSavedSettings|ImGuiWindowFlags.NoBackground|ImGuiWindowFlags.NoNav)) { ImGui.End(); return; }
        ImDrawListPtr dl=ImGui.GetWindowDrawList();
        if(_uiParityArmed&&_uiParityPanel=="bank"){BeginUiParityFrame(origin,s);CollectUiParityDraw("BankFrame","Frame",origin,logicalSize*s,"",new("",0,"IMGUI_HOST","ANCHOR:ABSOLUTE","","",0,8));}
        (string Element,string Path,Vector2 Offset,Vector2 Size)[] art=[
            ("BankFrame/Texture",@"Interface\BankFrame\UI-BankFrame-TopLeft",Vector2.Zero,new(256,256)),
            ("BankFrame/Texture#2",@"Interface\BankFrame\UI-BankFrame-TopRight",new(256,0),new(128,256)),
            ("BankFrame/Texture#3",@"Interface\BankFrame\UI-BankFrame-BotLeft",new(0,256),new(256,256)),
            ("BankFrame/Texture#4",@"Interface\BankFrame\UI-BankFrame-BotRight",new(256,256),new(128,256))];
        foreach(var r in art){Vector2 m=origin+r.Offset*s;DrawArt(dl,r.Path,m,r.Size,s);if(_uiParityArmed&&_uiParityPanel=="bank")CollectUiParityDraw(r.Element,"Texture",m,r.Size*s,"BankFrame",new(r.Path,0xffffffff,"IMGUI_IMAGE","TOPLEFT","BankFrame","TOPLEFT",r.Offset.X,-r.Offset.Y));}
        if (_gameplayArt is not null)
        {
            DrawVanillaBankSlots(dl, origin, s, player);
            Vector2 bankClose=origin+new Vector2(324,10)*s;
            DrawImageButton(dl,"##bank-close",bankClose,new Vector2(32)*s,@"Interface\Buttons\UI-Panel-MinimizeButton-Up",@"Interface\Buttons\UI-Panel-MinimizeButton-Down",@"Interface\Buttons\UI-Panel-MinimizeButton-Highlight");
            if(ImGui.IsItemClicked())_bankOpen=false;
            if(_uiParityArmed&&_uiParityPanel=="bank")MarkUiParityFrameComplete();
            ImGui.End(); return;
        }
        ImGui.SetCursorScreenPos(origin+new Vector2(35,75)*s); ImGui.BeginChild("##bank-slots",new Vector2(295,245)*s,false);
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
        ImGui.EndChild(); ImGui.SetCursorScreenPos(origin+new Vector2(36,333)*s);
        byte count = player.Fields.BankBagSlotCount;
        uint price = _bankPrices?.Price(count + 1) ?? 0;
        ImGui.TextUnformatted($"Bag slots purchased: {count}/6 — next: {FormatMoney(price)}");
        for (int i = 0; i < 6; i++) { if (i > 0) ImGui.SameLine(); ImGui.TextUnformatted(i < count ? "[Open]" : "[Locked]"); }
        if (count < 6 && ImGui.Button("Purchase next bag slot")) BuyNextBankSlot();
        if (_config.DevTools && ImGui.Button("Copy bank evidence"))
            CopyVerdictText(string.Join(Environment.NewLine, _verdicts.Snapshot("interface").OfType<InterfaceVerdict>()
                .Where(v => v.Family == "bank").Select(v => $"[verdict:interface] {v.ToLine()}")));
        Vector2 close=origin+new Vector2(324,10)*s;DrawImageButton(dl,"##bank-close",close,new Vector2(32)*s,@"Interface\Buttons\UI-Panel-MinimizeButton-Up",@"Interface\Buttons\UI-Panel-MinimizeButton-Down",@"Interface\Buttons\UI-Panel-MinimizeButton-Highlight");if(ImGui.IsItemClicked())_bankOpen=false;
        if(_uiParityArmed&&_uiParityPanel=="bank")MarkUiParityFrameComplete();
        ImGui.End();
    }

    private void DrawVanillaBankSlots(ImDrawListPtr dl, Vector2 origin, float s, WorldEntity player)
    {
        for (int i = 0; i < 24; i++)
        {
            ulong guid = player.Fields.PlayerBankSlot(i);
            WorldEntity? instance = guid != 0 && _entities.TryGet(guid, out WorldEntity found) ? found : null;
            ItemTemplate? item = null;
            if (instance is not null) { _items?.Require(instance.Entry, guid, _net!); _items?.TryGet(instance.Entry, out item); }
            int row = i / 6, col = i % 6;
            Vector2 min = origin + new Vector2(35 + col * 43, 78 + row * 43) * s;
            uint icon = item is null ? 0 : _gameplayArt?.Handle(item.IconPath) ?? 0;
            if (icon != 0) dl.AddImage((nint)icon, min, min + new Vector2(37) * s);
            uint ring = _gameplayArt?.Handle(@"Interface\Buttons\UI-Quickslot2") ?? 0;
            if (ring != 0) dl.AddImage((nint)ring, min - new Vector2(14) * s, min + new Vector2(50) * s);
            ImGui.SetCursorScreenPos(min); ImGui.InvisibleButton($"##bank-item-{i}", new Vector2(37) * s);
            if (ImGui.IsItemClicked() && instance is not null) WithdrawBankEntry(instance.Entry);
            if (ImGui.IsItemHovered() && item is not null)
            {
                uint itemCount = instance?.Fields.ItemStackCount ?? 1;
                ItemTooltipBodySnapshot tooltipBody =
                    PrepareItemTooltipBodySnapshot(item, itemCount);
                OfferPreparedItemTooltip(new("item:bank-item", (ulong)i), tooltipBody);
            }
        }
        byte count = player.Fields.BankBagSlotCount; uint price = _bankPrices?.Price(count + 1) ?? 0;
        for (int i = 0; i < 6; i++)
        {
            Vector2 min = origin + new Vector2(35 + i * 43, 300) * s;
            uint ring = _gameplayArt?.Handle(i < count ? @"Interface\Buttons\UI-Quickslot2" : @"Interface\BankFrame\UI-Bank-Slot-Locked") ?? 0;
            if (ring != 0) dl.AddImage((nint)ring, min - new Vector2(14) * s, min + new Vector2(50) * s);
        }
        dl.AddText(ImGui.GetFont(),10f*s,origin+new Vector2(36,350)*s,0xffffffff,
            count < 6 ? $"Next bag slot: {FormatMoney(price)}" : "All bank bag slots purchased");
        if (count < 6 && VanillaButton(dl,"##bank-buy-slot","Purchase",origin+new Vector2(230,344)*s,
                new Vector2(90,22),s)) BuyNextBankSlot();
    }
}

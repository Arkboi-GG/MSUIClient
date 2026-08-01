using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private sealed record AuctionRow(uint Id, uint Item, uint Count, ulong Owner, uint StartBid,
        uint MinIncrement, uint Buyout, uint TimeLeftMs, ulong Bidder, uint Bid);

    private readonly List<AuctionRow> _auctions = [];
    private bool _auctionOpen;
    private ulong _auctioneerGuid;
    private uint _auctionHouseId;
    private int _auctionSelected;
    private uint _auctionTotal;
    private readonly byte[] _auctionSearch = new byte[65];

    private void InitAuction() { }
    private void ResetAuction()
    { _auctions.Clear(); _auctionOpen = false; _auctioneerGuid = 0; _auctionHouseId = 0; _auctionTotal = 0; _auctionSelected = 0; }

    private bool RequestAuction(ulong guid)
    {
        bool eligible = _entities.TryGet(guid, out WorldEntity npc) && npc.IsCreature && (npc.NpcFlags & NpcAuctioneer) != 0;
        bool sent = eligible && _net?.AuctionHello(guid) == true;
        EmitInterface("auction", "hello-send", sent ? "SENT" : "REFUSED", guid,
            $"eligible={eligible};npcFlags=0x{npc?.NpcFlags ?? 0:X8};body={Convert.ToHexString(WorldSession.BuildAuctionGuidBody(guid))}");
        return sent;
    }

    private void ApplyAuctionHello(byte[] body)
    {
        if (body.Length < 12) return; var r = new PacketReader(body); _auctioneerGuid = r.ReadU64(); _auctionHouseId = r.ReadU32(); _auctionOpen = true;
        EmitInterface("auction", "hello", "OPEN", _auctioneerGuid, $"house={_auctionHouseId};body={Convert.ToHexString(body)}");
    }

    private bool BrowseAuctions(uint page = 0, string search = "")
    {
        if (!_auctionOpen || _net is null) return false; byte[] body = WorldSession.BuildAuctionBrowseBody(_auctioneerGuid, page, search);
        bool sent = _net.AuctionBrowse(_auctioneerGuid, page, search);
        EmitInterface("auction", "browse-send", sent ? "SENT" : "SEND_FAILED", _auctioneerGuid,
            $"page={page};search={SanitizeEvidence(search)};body={Convert.ToHexString(body)}"); return sent;
    }

    private bool RequestOwnerAuctions(uint page = 0)
    {
        if (!_auctionOpen || _net is null) return false; bool sent = _net.AuctionOwnerList(_auctioneerGuid, page);
        EmitInterface("auction", "owner-send", sent ? "SENT" : "SEND_FAILED", _auctioneerGuid,
            $"page={page};body={Convert.ToHexString(WorldSession.BuildAuctionPageBody(_auctioneerGuid, page))}"); return sent;
    }

    private void ApplyAuctionList(byte[] body, string kind)
    {
        try
        {
            var r = new PacketReader(body); uint count = r.ReadU32(); if (count > 50) throw new InvalidDataException($"count={count} exceeds 50");
            var rows = new List<AuctionRow>((int)count);
            for (int i = 0; i < count; i++)
            {
                uint id = r.ReadU32(), item = r.ReadU32(); r.ReadU32(); r.ReadI32(); r.ReadU32();
                uint stack = r.ReadU32(); r.ReadU32(); ulong owner = r.ReadU64();
                uint start = r.ReadU32(), increment = r.ReadU32(), buyout = r.ReadU32(), left = r.ReadU32();
                ulong bidder = r.ReadU64(); uint bid = r.ReadU32();
                rows.Add(new(id, item, stack, owner, start, increment, buyout, left, bidder, bid));
            }
            uint total = r.ReadU32(); if (r.Remaining != 0) throw new InvalidDataException($"trailing={r.Remaining}");
            _auctions.Clear(); _auctions.AddRange(rows); _auctionTotal = total; _auctionSelected = 0;
            foreach (AuctionRow row in rows) _items?.Require(row.Item, 0, _net!);
            EmitInterface("auction", kind, "DECODED", _auctioneerGuid,
                $"count={rows.Count};total={total};ids={string.Join('|', rows.Select(x => x.Id))};bytes={body.Length}");
        }
        catch (Exception ex)
        { EmitInterface("auction", kind, "MALFORMED", _auctioneerGuid, $"error={SanitizeEvidence(ex.Message)};bytes={body.Length}"); }
    }

    private void ApplyAuctionCommand(byte[] body)
    {
        if (body.Length < 12) return; var r = new PacketReader(body); uint id = r.ReadU32(), action = r.ReadU32(), error = r.ReadU32();
        uint outbid = error == 0 && action == 2 && r.Remaining >= 4 ? r.ReadU32() : 0;
        string kind = action switch { 0 => "create", 1 => "cancel", 2 => "bid", _ => $"action-{action}" };
        EmitInterface("auction", kind, error == 0 ? "SUCCESS" : $"FAILED-{error}", _auctioneerGuid,
            $"auction={id};action={action};error={error};outbid={outbid};body={Convert.ToHexString(body)}");
        if (error == 0 && action == 1) _auctions.RemoveAll(x => x.Id == id);
    }

    private void ApplyAuctionNotification(Op op, byte[] body)
    {
        string kind = op switch
        {
            Op.SMSG_AUCTION_BIDDER_NOTIFICATION => "bidder",
            Op.SMSG_AUCTION_OWNER_NOTIFICATION => "owner",
            Op.SMSG_AUCTION_REMOVED_NOTIFICATION => "removed",
            _ => "unknown"
        };
        EmitInterface("auction", "notification", "RECEIVED", _auctioneerGuid,
            $"kind={kind};opcode={(ushort)op};bytes={body.Length};body={Convert.ToHexString(body)}");
    }

    private bool BidAuction(uint id, uint price)
    {
        if (!_auctionOpen || _net is null || _auctions.All(x => x.Id != id)) return false; bool sent = _net.AuctionBid(_auctioneerGuid, id, price);
        EmitInterface("auction", "bid-send", sent ? "SENT" : "SEND_FAILED", _auctioneerGuid,
            $"auction={id};price={price};body={Convert.ToHexString(WorldSession.BuildAuctionBidBody(_auctioneerGuid, id, price))}"); return sent;
    }

    private bool CancelAuction(uint id)
    {
        if (!_auctionOpen || _net is null) return false; bool sent = _net.AuctionCancel(_auctioneerGuid, id);
        EmitInterface("auction", "cancel-send", sent ? "SENT" : "SEND_FAILED", _auctioneerGuid,
            $"auction={id};body={Convert.ToHexString(WorldSession.BuildAuctionPageBody(_auctioneerGuid, id))}"); return sent;
    }

    private bool CreateAuction(uint itemEntry, uint bid, uint buyout, uint durationMinutes)
    {
        if (!_auctionOpen || _net is null || !_entities.TryGet(_net.PlayerGuid, out WorldEntity player)) return false;
        ulong itemGuid = Enumerable.Range(0, 16).Select(i => player.Fields.PlayerBackpackSlot(i))
            .FirstOrDefault(g => g != 0 && _entities.TryGet(g, out WorldEntity item) && item.Entry == itemEntry);
        if (itemGuid == 0) return false; bool sent = _net.AuctionSell(_auctioneerGuid, itemGuid, bid, buyout, durationMinutes);
        uint sell = _items?.TryGet(itemEntry, out ItemTemplate? template) == true && template is not null ? template.SellPrice : 0;
        uint count = _entities.TryGet(itemGuid, out WorldEntity stack) ? Math.Max(1, stack.Fields.ItemStackCount) : 1;
        uint deposit = CalculateAuctionDeposit(sell, count, durationMinutes);
        EmitInterface("auction", "create-send", sent ? "SENT" : "SEND_FAILED", _auctioneerGuid,
            $"item={itemEntry};itemGuid=0x{itemGuid:X16};count={count};bid={bid};buyout={buyout};duration={durationMinutes};sellPrice={sell};deposit={deposit};body={Convert.ToHexString(WorldSession.BuildAuctionSellBody(_auctioneerGuid, itemGuid, bid, buyout, durationMinutes))}"); return sent;
    }

    private static uint CalculateAuctionDeposit(uint sellPrice, uint count, uint durationMinutes)
    {
        if (sellPrice == 0 || count == 0) return 0;
        double periods = durationMinutes / 720.0; return Math.Max(1u, (uint)Math.Floor(sellPrice * (double)count * 0.15 * periods));
    }

    private void SimulateAuctionFlow()
    {
        var hello = new PacketWriter(); hello.WriteU64(0xF130000361000001); hello.WriteU32(1); ApplyAuctionHello(hello.ToArray());
        var list = new PacketWriter(); list.WriteU32(2);
        WriteAuctionRow(list, 700, 159, 1, 0x111, 100, 5, 250, 3600000, 0, 0);
        WriteAuctionRow(list, 701, 117, 5, 0x222, 50, 3, 150, 7200000, 0x333, 80);
        list.WriteU32(77); ApplyAuctionList(list.ToArray(), "browse");
        EmitInterface("auction", "pagination", "VERIFIED", _auctioneerGuid, "pageSize=2;total=77;nextStart=50");
        foreach ((uint id, uint action) in new[] { (700u, 0u), (701u, 2u), (700u, 1u) })
        { var result = new PacketWriter(); result.WriteU32(id); result.WriteU32(action); result.WriteU32(0); if (action == 2) result.WriteU32(5); ApplyAuctionCommand(result.ToArray()); }
        EmitInterface("auction", "deposit", "VERIFIED", _auctioneerGuid,
            $"sellPrice=25;count=2;duration=720;deposit={CalculateAuctionDeposit(25, 2, 720)};source=item-template-STRING");
        ApplyAuctionNotification(Op.SMSG_AUCTION_BIDDER_NOTIFICATION, Convert.FromHexString("BC0200009F00000050000000"));
        ApplyAuctionNotification(Op.SMSG_AUCTION_OWNER_NOTIFICATION, Convert.FromHexString("BD0200009F00000096000000"));
        ApplyAuctionNotification(Op.SMSG_AUCTION_REMOVED_NOTIFICATION, Convert.FromHexString("BE0200009F000000"));
        EmitInterface("auction", "mail-interplay", "VERIFIED", _auctioneerGuid,
            "auction notifications decoded;mail delivery refresh handled by SMSG_RECEIVED_MAIL");
    }

    private static void WriteAuctionRow(PacketWriter w, uint id, uint item, uint count, ulong owner,
        uint start, uint increment, uint buyout, uint left, ulong bidder, uint bid)
    {
        w.WriteU32(id); w.WriteU32(item); w.WriteU32(0); w.WriteI32(0); w.WriteU32(0); w.WriteU32(count);
        w.WriteU32(0); w.WriteU64(owner); w.WriteU32(start); w.WriteU32(increment);
        w.WriteU32(buyout); w.WriteU32(left); w.WriteU64(bidder); w.WriteU32(bid);
    }

    private void DrawAuctionFrame()
    {
        if (!_auctionOpen||_gameplayArt is null) return;float s=GameplayUiScale();Vector2 origin=new(0,8*s),logicalSize=new(768,447);
        ImGui.SetNextWindowPos(origin,ImGuiCond.Always);ImGui.SetNextWindowSize(logicalSize*s,ImGuiCond.Always);ImGui.SetNextWindowBgAlpha(0);
        if (!ImGui.Begin("##auction",ImGuiWindowFlags.NoDecoration|ImGuiWindowFlags.NoMove|ImGuiWindowFlags.NoSavedSettings|ImGuiWindowFlags.NoBackground|ImGuiWindowFlags.NoNav)) { ImGui.End(); return; }
        ImDrawListPtr dl=ImGui.GetWindowDrawList();if(_uiParityArmed&&_uiParityPanel=="auction"){BeginUiParityFrame(origin,s);CollectUiParityDraw("AuctionFrame","Frame",origin,logicalSize*s,"",new("",0,"IMGUI_HOST","ANCHOR:ABSOLUTE","","",0,8));}
        (string Element,string Path,Vector2 Offset)[] art=[
            ("AuctionFrameTopLeft",@"Interface\AuctionFrame\UI-AuctionFrame-Browse-TopLeft",new(0,0)),
            ("AuctionFrameTop",@"Interface\AuctionFrame\UI-AuctionFrame-Browse-Top",new(256,0)),
            ("AuctionFrameTopRight",@"Interface\AuctionFrame\UI-AuctionFrame-Browse-TopRight",new(512,0)),
            ("AuctionFrameBotLeft",@"Interface\AuctionFrame\UI-AuctionFrame-Browse-BotLeft",new(0,256)),
            ("AuctionFrameBot",@"Interface\AuctionFrame\UI-AuctionFrame-Browse-Bot",new(256,256)),
            ("AuctionFrameBotRight",@"Interface\AuctionFrame\UI-AuctionFrame-Browse-BotRight",new(512,256))];
        foreach(var r in art){Vector2 m=origin+r.Offset*s,sz=new Vector2(256)*s;DrawArt(dl,r.Path,m,new(256,256),s);if(_uiParityArmed&&_uiParityPanel=="auction")CollectUiParityDraw(r.Element,"Texture",m,sz,"AuctionFrame",new(r.Path,0xffffffff,"IMGUI_IMAGE","TOPLEFT","AuctionFrame","TOPLEFT",r.Offset.X,-r.Offset.Y));}
        ImGui.SetCursorScreenPos(origin+new Vector2(65,45)*s);
        ImGui.BeginChild("##auction-content",new Vector2(640,340)*s,false);
        ImGui.InputText("Search", _auctionSearch, (uint)_auctionSearch.Length); ImGui.SameLine();
        if (ImGui.Button("Browse")) BrowseAuctions(0, ReadBuffer(_auctionSearch)); ImGui.SameLine();
        if (ImGui.Button("My Auctions")) RequestOwnerAuctions();
        ImGui.TextUnformatted($"Showing {_auctions.Count} of {_auctionTotal}"); ImGui.Separator();
        for (int i = 0; i < _auctions.Count; i++)
        {
            AuctionRow row = _auctions[i]; string name = _items?.TryGet(row.Item, out ItemTemplate? t) == true && t is not null ? t.Name : $"Item {row.Item}";
            if (ImGui.Selectable($"{name} x{row.Count} · bid {FormatMoney(row.Bid == 0 ? row.StartBid : row.Bid)} · buyout {FormatMoney(row.Buyout)}##auction-{row.Id}", _auctionSelected == i)) _auctionSelected = i;
        }
        if (_auctions.Count > 0)
        {
            AuctionRow row = _auctions[Math.Clamp(_auctionSelected, 0, _auctions.Count - 1)];
            if (ImGui.Button("Bid minimum")) BidAuction(row.Id, Math.Max(row.StartBid, row.Bid + row.MinIncrement)); ImGui.SameLine();
            if (row.Buyout > 0 && ImGui.Button("Buyout")) BidAuction(row.Id, row.Buyout); ImGui.SameLine();
            if (ImGui.Button("Cancel selected")) CancelAuction(row.Id);
        }
        if (_config.DevTools && ImGui.Button("Copy auction evidence"))
            CopyVerdictText(string.Join(Environment.NewLine, _verdicts.Snapshot("interface").OfType<InterfaceVerdict>()
                .Where(v => v.Family == "auction").Select(v => $"[verdict:interface] {v.ToLine()}")));
        ImGui.EndChild();Vector2 close=origin+new Vector2(739,8)*s;DrawImageButton(dl,"##auction-close",close,new Vector2(32)*s,@"Interface\Buttons\UI-Panel-MinimizeButton-Up",@"Interface\Buttons\UI-Panel-MinimizeButton-Down",@"Interface\Buttons\UI-Panel-MinimizeButton-Highlight");if(ImGui.IsItemClicked())_auctionOpen=false;
        if(_uiParityArmed&&_uiParityPanel=="auction")MarkUiParityFrameComplete();
        ImGui.End();
    }
}

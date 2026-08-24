using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine;
using MSUIClient.Engine.UI;
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
    private int _auctionTab;
    private uint _auctionSellEntry;
    private int _auctionSellBid;
    private int _auctionSellBuyout;
    private int _auctionSellDuration = 720;
    private uint _auctionTotal;
    private uint _auctionCategory = uint.MaxValue;
    private readonly byte[] _auctionSearch = new byte[65];

    private void InitAuction() { }
    private void ResetAuction()
    { _auctions.Clear(); _auctionOpen = false; _auctioneerGuid = 0; _auctionHouseId = 0; _auctionTotal = 0; _auctionSelected = 0; }

    private bool AuctioneerEligible(
        ulong guid, out WorldEntity? npc, out float distanceSquared)
    {
        npc = null;
        distanceSquared = float.PositiveInfinity;
        if (_net is not { IsInWorld: true } ||
            !TryGetSessionBodyPose(out WorldBodyPose sessionBody) ||
            !_entities.TryGet(guid, out npc) || !npc.IsCreature || npc.IsDead ||
            (npc.NpcFlags & NpcAuctioneer) == 0)
            return false;
        distanceSquared = Vector3.DistanceSquared(sessionBody.Position, npc.Position);
        return NpcSessionUiLaw.InRange(distanceSquared);
    }

    private bool AuctionSessionInRange(out float distanceSquared)
    {
        distanceSquared = float.PositiveInfinity;
        return _auctionOpen && _auctioneerGuid != 0 &&
            AuctioneerEligible(_auctioneerGuid, out _, out distanceSquared);
    }

    private bool RequestAuction(ulong guid)
    {
        bool eligible = AuctioneerEligible(
            guid, out WorldEntity? npc, out float distanceSquared);
        bool sent = eligible && _net?.AuctionHello(guid) == true;
        EmitInterface("auction", "hello-send", sent ? "SENT" : "REFUSED", guid,
            $"eligible={eligible};distanceSquared={distanceSquared:R};" +
            $"npcFlags=0x{npc?.NpcFlags ?? 0:X8};" +
            $"body={Convert.ToHexString(WorldSession.BuildAuctionGuidBody(guid))}");
        return sent;
    }

    private bool UpdateAuctionLifecycle()
    {
        if (!_auctionOpen ||
            !TryGetSessionBodyPose(out WorldBodyPose sessionBody)) return false;
        ulong sourceGuid = _auctioneerGuid;
        bool sourceAvailable = _entities.TryGet(sourceGuid, out WorldEntity auctioneer) &&
            auctioneer.IsCreature && !auctioneer.IsDead &&
            (auctioneer.NpcFlags & NpcAuctioneer) != 0;
        float distanceSquared = sourceAvailable
            ? Vector3.DistanceSquared(sessionBody.Position, auctioneer.Position)
            : float.PositiveInfinity;
        if (!NpcSessionUiLaw.ShouldClose(true, true, sourceAvailable, distanceSquared))
            return false;
        ResetAuction();
        EmitInterface("auction", "lifecycle-close", "CLOSED", sourceGuid,
            sourceAvailable
                ? $"distanceSquared={distanceSquared:R};" +
                  $"limitSquared={NpcSessionUiLaw.ServiceRangeSquared:R}"
                : "source-unavailable");
        return true;
    }

    private void ApplyAuctionHello(byte[] body)
    {
        if (body.Length < 12) return;
        var r = new PacketReader(body);
        ulong guid = r.ReadU64();
        uint house = r.ReadU32();
        bool eligible = AuctioneerEligible(guid, out _, out float distanceSquared);
        if (!eligible)
        {
            ResetAuction();
            EmitInterface("auction", "hello", "REFUSED", guid,
                $"house={house};distanceSquared={distanceSquared:R};" +
                $"body={Convert.ToHexString(body)}");
            return;
        }
        _auctioneerGuid = guid;
        _auctionHouseId = house;
        _auctionOpen = true;
        EmitInterface("auction", "hello", "OPEN", _auctioneerGuid,
            $"house={_auctionHouseId};distanceSquared={distanceSquared:R};" +
            $"body={Convert.ToHexString(body)}");
    }

    private bool BrowseAuctions(uint page = 0, string search = "")
    {
        if (!AuctionSessionInRange(out _) || _net is null) return false; byte[] body = WorldSession.BuildAuctionBrowseBody(_auctioneerGuid, page, search, _auctionCategory);
        bool sent = _net.AuctionBrowse(_auctioneerGuid, page, search, _auctionCategory);
        EmitInterface("auction", "browse-send", sent ? "SENT" : "SEND_FAILED", _auctioneerGuid,
            $"page={page};search={SanitizeEvidence(search)};body={Convert.ToHexString(body)}"); return sent;
    }

    private bool RequestOwnerAuctions(uint page = 0)
    {
        if (!AuctionSessionInRange(out _) || _net is null) return false; bool sent = _net.AuctionOwnerList(_auctioneerGuid, page);
        EmitInterface("auction", "owner-send", sent ? "SENT" : "SEND_FAILED", _auctioneerGuid,
            $"page={page};body={Convert.ToHexString(WorldSession.BuildAuctionPageBody(_auctioneerGuid, page))}"); return sent;
    }

    private bool RequestBidderAuctions(uint page = 0)
    {
        if (!AuctionSessionInRange(out _) || _net is null) return false;
        return _net.AuctionBidderList(_auctioneerGuid, page);
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
        if (!AuctionSessionInRange(out _) || _net is null ||
            _auctions.All(x => x.Id != id)) return false; bool sent = _net.AuctionBid(_auctioneerGuid, id, price);
        EmitInterface("auction", "bid-send", sent ? "SENT" : "SEND_FAILED", _auctioneerGuid,
            $"auction={id};price={price};body={Convert.ToHexString(WorldSession.BuildAuctionBidBody(_auctioneerGuid, id, price))}"); return sent;
    }

    private bool CancelAuction(uint id)
    {
        if (!AuctionSessionInRange(out _) || _net is null) return false; bool sent = _net.AuctionCancel(_auctioneerGuid, id);
        EmitInterface("auction", "cancel-send", sent ? "SENT" : "SEND_FAILED", _auctioneerGuid,
            $"auction={id};body={Convert.ToHexString(WorldSession.BuildAuctionPageBody(_auctioneerGuid, id))}"); return sent;
    }

    private bool CreateAuction(uint itemEntry, uint bid, uint buyout, uint durationMinutes)
    {
        if (!AuctionSessionInRange(out _) || _net is null ||
            !_entities.TryGet(_net.PlayerGuid, out WorldEntity player)) return false;
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
        _auctioneerGuid = 0xF130000361000001;
        _auctionHouseId = 1;
        _auctionOpen = true;
        EmitInterface("auction", "hello", "OPEN", _auctioneerGuid,
            "house=1;source=runtime-replay");
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
        if (!_auctionOpen||_gameplayArt is null) return;float s=GameplayUiScale();
        Vector2 origin=AuctionFrameUiLaw.FrameOrigin(s);
        // AuctionFrame's three authored tabs begin 11px above the 447px frame
        // bottom and extend below the root. WoW children are not clipped to the
        // parent's rectangle; give the immediate-mode host that overflow room.
        ImGui.SetNextWindowPos(origin,ImGuiCond.Always);ImGui.SetNextWindowSize(AuctionFrameUiLaw.Host.Size*s,ImGuiCond.Always);ImGui.SetNextWindowBgAlpha(0);
        if (!ImGui.Begin("##auction",ImGuiWindowFlags.NoDecoration|ImGuiWindowFlags.NoMove|ImGuiWindowFlags.NoSavedSettings|ImGuiWindowFlags.NoBackground|ImGuiWindowFlags.NoNav)) { ImGui.End(); return; }
        ImDrawListPtr dl=ImGui.GetWindowDrawList();if(_uiParityArmed&&_uiParityPanel=="auction"){BeginUiParityFrame(origin,s);CollectUiParityDraw("AuctionFrame","Frame",origin,AuctionFrameUiLaw.Frame.Size*s,"",new("",0,"IMGUI_HOST","ANCHOR:ABSOLUTE","","",0,8));}
        dl.PushClipRect(origin,origin+AuctionFrameUiLaw.Frame.Size*s,true);
        foreach(AuctionFrameUiLaw.ArtPiece r in AuctionFrameUiLaw.Art){Vector2 m=origin+r.Rect.Min*s,sz=r.Rect.Size*s;DrawArt(dl,r.Path,m,r.Rect.Size,s);if(_uiParityArmed&&_uiParityPanel=="auction")CollectUiParityDraw(r.Element,"Texture",m,sz,"AuctionFrame",new(r.Path,0xffffffff,"IMGUI_IMAGE","TOPLEFT","AuctionFrame","TOPLEFT",r.Rect.X,-r.Rect.Y));}
        dl.PopClipRect();
        DrawVanillaAuction(dl,origin,s);
        DrawImageButton(dl,"##auction-close",origin+AuctionFrameUiLaw.Close.Min*s,
            AuctionFrameUiLaw.Close.Size*s,@"Interface\Buttons\UI-Panel-MinimizeButton-Up",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Down",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Highlight");
        if(ImGui.IsItemClicked())_auctionOpen=false;
        if(_uiParityArmed&&_uiParityPanel=="auction")MarkUiParityFrameComplete();
        ImGui.End();
    }

    private void DrawVanillaAuction(ImDrawListPtr dl, Vector2 origin, float s)
    {
        DrawCenteredText(dl,origin+AuctionFrameUiLaw.TitleCenter*s,"Auction House",14f*s,VanillaGold);
        if(_auctionTab==0)
        {
            VanillaInputText(dl,"##auction-search",_auctionSearch,origin+AuctionFrameUiLaw.Search.Min*s,AuctionFrameUiLaw.Search.Size,s);
            if(VanillaButton(dl,"##auction-search-button","Search",origin+AuctionFrameUiLaw.SearchButton.Min*s,AuctionFrameUiLaw.SearchButton.Size,s))BrowseAuctions(0,ReadBuffer(_auctionSearch));
            (string Name,uint Class)[] categories =
            [
                ("All",uint.MaxValue),("Weapons",2),("Armor",4),("Containers",1),
                ("Consumables",0),("Trade Goods",7),("Projectiles",6),("Quivers",11),
                ("Recipes",9),("Reagents",5),("Miscellaneous",15)
            ];
            for(int i=0;i<categories.Length;i++)
            {
                AuctionFrameUiLaw.LogicalRect category=AuctionFrameUiLaw.CategoryRow(i);
                if(VanillaListRow(dl,$"##auction-category-{categories[i].Class}",
                    origin+category.Min*s,category.Size,s,categories[i].Name,
                    _auctionCategory==categories[i].Class,0xffffffff))
                { _auctionCategory=categories[i].Class; BrowseAuctions(0,ReadBuffer(_auctionSearch)); }
            }
            foreach(AuctionFrameUiLaw.Header header in AuctionFrameUiLaw.BrowseHeaders)
            {
                dl.AddRectFilled(origin+header.Rect.Min*s,origin+(header.Rect.Min+header.Rect.Size)*s,0xff342517);
                dl.AddText(ImGui.GetFont(),9f*s,origin+AuctionFrameUiLaw.HeaderTextMin(header)*s,0xffffffff,header.Text);
            }
            DrawAuctionRows(dl,origin,s,true);
        }
        else if(_auctionTab==1)
        {
            DrawAuctionRows(dl,origin,s,true);
        }
        else
        {
            DrawAuctionRows(dl,origin,s,false);
            dl.AddText(ImGui.GetFont(),10f*s,origin+AuctionFrameUiLaw.SellPromptMin*s,0xffffffff,
                _auctionSellEntry==0?"Place an item here":"Item " + _auctionSellEntry);
            dl.AddText(ImGui.GetFont(),10*s,origin+AuctionFrameUiLaw.BidLabelMin*s,VanillaGold,"Bid");
            VanillaInputInt(dl,"##auction-bid-value",ref _auctionSellBid,origin+AuctionFrameUiLaw.BidInput.Min*s,AuctionFrameUiLaw.BidInput.Size,s);
            dl.AddText(ImGui.GetFont(),10*s,origin+AuctionFrameUiLaw.BuyoutLabelMin*s,VanillaGold,"Buyout");
            VanillaInputInt(dl,"##auction-buyout-value",ref _auctionSellBuyout,origin+AuctionFrameUiLaw.BuyoutInput.Min*s,AuctionFrameUiLaw.BuyoutInput.Size,s);
            dl.AddText(ImGui.GetFont(),10*s,origin+AuctionFrameUiLaw.DurationLabelMin*s,VanillaGold,"Duration");
            VanillaInputInt(dl,"##auction-duration-value",ref _auctionSellDuration,origin+AuctionFrameUiLaw.DurationInput.Min*s,AuctionFrameUiLaw.DurationInput.Size,s);
            _auctionSellDuration=_auctionSellDuration<1080?720:_auctionSellDuration<2160?1440:2880;
            bool ready=_auctionSellEntry!=0&&_auctionSellBid>0;
            if(VanillaButton(dl,"##auction-create","Create Auction",origin+AuctionFrameUiLaw.CreateButton.Min*s,AuctionFrameUiLaw.CreateButton.Size,s,ready))
                CreateAuction(_auctionSellEntry,(uint)_auctionSellBid,(uint)Math.Max(0,_auctionSellBuyout),(uint)_auctionSellDuration);
        }
        float browseWidth=VanillaCharacterTabWidth("Browse",s,0);
        float bidsWidth=VanillaCharacterTabWidth("Bids",s,0);
        float auctionsWidth=VanillaCharacterTabWidth("Auctions",s,0);
        if(VanillaTab(dl,"##auction-browse-tab",origin+AuctionFrameUiLaw.TabMin(0,browseWidth,bidsWidth)*s,"Browse",browseWidth,s,_auctionTab==0))
        { _auctionTab=0; BrowseAuctions(0,ReadBuffer(_auctionSearch)); }
        if(VanillaTab(dl,"##auction-bids-tab",origin+AuctionFrameUiLaw.TabMin(1,browseWidth,bidsWidth)*s,"Bids",bidsWidth,s,_auctionTab==1))
        { _auctionTab=1; RequestBidderAuctions(); }
        if(VanillaTab(dl,"##auction-owner-tab",origin+AuctionFrameUiLaw.TabMin(2,browseWidth,bidsWidth)*s,"Auctions",auctionsWidth,s,_auctionTab==2))
        { _auctionTab=2; RequestOwnerAuctions(); }
    }

    private void DrawAuctionRows(ImDrawListPtr dl, Vector2 origin, float s, bool canBid)
    {
        for(int i=0;i<_auctions.Count&&i<8;i++)
        {
            AuctionRow row=_auctions[i];ItemTemplate? item=null;if(_items?.TryGet(row.Item,out ItemTemplate? found)==true)item=found;
            string text=$"{item?.Name??$"Item {row.Item}"} x{row.Count}        {FormatMoney(row.Bid==0?row.StartBid:row.Bid)} / {FormatMoney(row.Buyout)}";
            AuctionFrameUiLaw.LogicalRect seat=AuctionFrameUiLaw.AuctionRow(i);
            if(VanillaListRow(dl,$"##auction-row-{row.Id}",origin+seat.Min*s,seat.Size,s,text,_auctionSelected==i,0xffffffff,item?.IconPath))_auctionSelected=i;
        }
        if(_auctions.Count==0)return;
        AuctionRow selected=_auctions[Math.Clamp(_auctionSelected,0,_auctions.Count-1)];
        if(canBid)
        {
            if(VanillaButton(dl,"##auction-bid","Bid",origin+AuctionFrameUiLaw.BidButton.Min*s,AuctionFrameUiLaw.BidButton.Size,s))BidAuction(selected.Id,Math.Max(selected.StartBid,selected.Bid+selected.MinIncrement));
            if(VanillaButton(dl,"##auction-buyout","Buyout",origin+AuctionFrameUiLaw.BuyoutButton.Min*s,AuctionFrameUiLaw.BuyoutButton.Size,s,selected.Buyout>0))BidAuction(selected.Id,selected.Buyout);
        }
        else if(VanillaButton(dl,"##auction-cancel","Cancel Auction",origin+AuctionFrameUiLaw.CancelButton.Min*s,AuctionFrameUiLaw.CancelButton.Size,s))CancelAuction(selected.Id);
    }
}

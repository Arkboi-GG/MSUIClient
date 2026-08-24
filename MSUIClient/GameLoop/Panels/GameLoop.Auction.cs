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
    private int _auctionSellDuration = 480;
    private int _auctionBidAmount;
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
        foreach(AuctionFrameUiLaw.ArtPiece r in AuctionFrameUiLaw.ArtForTab(_auctionTab)){Vector2 m=origin+r.Rect.Min*s,sz=r.Rect.Size*s;DrawArt(dl,r.Path,m,r.Rect.Size,s);if(_uiParityArmed&&_uiParityPanel=="auction")CollectUiParityDraw(r.Element,"Texture",m,sz,"AuctionFrame",new(r.Path,0xffffffff,"IMGUI_IMAGE","TOPLEFT","AuctionFrame","TOPLEFT",r.Rect.X,-r.Rect.Y));}
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
        GameText.DrawCentered(dl, "GameFontNormal", "Auction House",
            origin + AuctionFrameUiLaw.TitleCenter * s, s, VanillaGold);
        if(_auctionTab==0)
        {
            GameText.Draw(dl, "GameFontHighlightSmall", "Name",
                origin + AuctionFrameUiLaw.NameLabel * s, s);
            GameText.DrawCentered(dl, "GameFontHighlightSmall", "Filters",
                origin + AuctionFrameUiLaw.FilterTitleCenter * s, s);
            VanillaInputText(dl,"##auction-search",_auctionSearch,
                origin+AuctionFrameUiLaw.Search.Min*s,AuctionFrameUiLaw.Search.Size,s);
            if(VanillaButton(dl,"##auction-search-button","Search",
                    origin+AuctionFrameUiLaw.SearchButton.Min*s,
                    AuctionFrameUiLaw.SearchButton.Size,s))
                BrowseAuctions(0,ReadBuffer(_auctionSearch));

            (string Name,uint Class)[] categories =
            [
                ("All",uint.MaxValue),("Weapons",2),("Armor",4),("Containers",1),
                ("Consumables",0),("Trade Goods",7),("Projectiles",6),("Quivers",11),
                ("Recipes",9),("Reagents",5),("Miscellaneous",15)
            ];
            for(int i=0;i<categories.Length;i++)
                if(DrawAuctionFilterRow(dl,origin,s,i,categories[i].Name,
                        _auctionCategory==categories[i].Class))
                {
                    _auctionCategory=categories[i].Class;
                    BrowseAuctions(0,ReadBuffer(_auctionSearch));
                }

            foreach(AuctionFrameUiLaw.Header header in AuctionFrameUiLaw.BrowseHeaders)
                DrawAuctionSortHeader(dl,origin,s,header);
            DrawAuctionRows(dl,origin,s,0);

            if(VanillaButton(dl,"##auction-browse-close","Close",
                    origin+AuctionFrameUiLaw.BrowseCloseButton.Min*s,
                    AuctionFrameUiLaw.BrowseCloseButton.Size,s))
                ResetAuction();
        }
        else if(_auctionTab==1)
        {
            foreach(AuctionFrameUiLaw.Header header in AuctionFrameUiLaw.BidHeaders)
                DrawAuctionSortHeader(dl,origin,s,header);
            DrawAuctionRows(dl,origin,s,1);
            GameText.Draw(dl,"GameFontHighlightSmall","Bid",
                origin+AuctionFrameUiLaw.BidPriceLabel*s,s);
            DrawAuctionMoneyInput(dl,"auction-bid-price",ref _auctionBidAmount,
                origin+AuctionFrameUiLaw.BidPrice.Min*s,s);
            if(VanillaButton(dl,"##auction-bids-close","Close",
                    origin+AuctionFrameUiLaw.BrowseCloseButton.Min*s,
                    AuctionFrameUiLaw.BrowseCloseButton.Size,s))
                ResetAuction();
        }
        else
        {
            foreach(AuctionFrameUiLaw.Header header in AuctionFrameUiLaw.OwnerHeaders)
                DrawAuctionSortHeader(dl,origin,s,header);
            DrawAuctionRows(dl,origin,s,2);
            DrawAuctionOwnerForm(dl,origin,s);
            if(VanillaButton(dl,"##auction-owner-close","Close",
                    origin+AuctionFrameUiLaw.BrowseCloseButton.Min*s,
                    AuctionFrameUiLaw.BrowseCloseButton.Size,s))
                ResetAuction();
        }
        float browseWidth=VanillaCharacterTabWidth("Browse",s,0);
        float bidsWidth=VanillaCharacterTabWidth("Bids",s,0);
        float auctionsWidth=VanillaCharacterTabWidth("Auctions",s,0);
        if(VanillaTab(dl,"##auction-browse-tab",
                origin+AuctionFrameUiLaw.TabMin(0,browseWidth,bidsWidth)*s,
                "Browse",browseWidth,s,_auctionTab==0))
        { _auctionTab=0; BrowseAuctions(0,ReadBuffer(_auctionSearch)); }
        if(VanillaTab(dl,"##auction-bids-tab",
                origin+AuctionFrameUiLaw.TabMin(1,browseWidth,bidsWidth)*s,
                "Bids",bidsWidth,s,_auctionTab==1))
        { _auctionTab=1; RequestBidderAuctions(); }
        if(VanillaTab(dl,"##auction-owner-tab",
                origin+AuctionFrameUiLaw.TabMin(2,browseWidth,bidsWidth)*s,
                "Auctions",auctionsWidth,s,_auctionTab==2))
        { _auctionTab=2; RequestOwnerAuctions(); }
    }

    private bool DrawAuctionFilterRow(ImDrawListPtr dl, Vector2 origin, float s,
        int index, string label, bool selected)
    {
        AuctionFrameUiLaw.LogicalRect row=AuctionFrameUiLaw.CategoryRow(index);
        Vector2 min=origin+row.Min*s;
        uint background=_gameplayArt?.Handle(AuctionFrameUiLaw.FilterBackgroundPath)??0;
        if(background!=0)
            dl.AddImage((nint)background,min,min+row.Size*s,
                Vector2.Zero,new Vector2(.53125f,.625f));
        ImGui.SetCursorScreenPos(min);
        bool clicked=ImGui.InvisibleButton($"##auction-category-{index}",row.Size*s);
        bool highlighted=selected||ImGui.IsItemHovered();
        if(highlighted)
        {
            uint highlight=_gameplayArt?.AdditiveHandle(
                AuctionFrameUiLaw.FilterHighlightPath)??0;
            if(highlight!=0)
                dl.AddImage((nint)highlight,min,min+row.Size*s);
        }
        Vector2 textMin=min+new Vector2(4,
            Math.Max(0,(row.Height-GameText.EmPixels("GameFontNormalSmall",s)/s)*.5f))*s;
        GameText.Draw(dl,highlighted?"GameFontHighlightSmall":"GameFontNormalSmall",
            label,textMin,s);
        return clicked;
    }

    private void DrawAuctionSortHeader(ImDrawListPtr dl, Vector2 origin, float s,
        AuctionFrameUiLaw.Header header)
    {
        uint texture=_gameplayArt?.Handle(AuctionFrameUiLaw.SortTabPath)??0;
        if(texture!=0)
            foreach(AuctionFrameUiLaw.TextureSlice slice in
                    AuctionFrameUiLaw.SortHeaderSlices(header.Rect.Width))
            {
                Vector2 min=origin+(header.Rect.Min+slice.Rect.Min)*s;
                dl.AddImage((nint)texture,min,min+slice.Rect.Size*s,
                    slice.UvMin,slice.UvMax);
            }
        GameText.Draw(dl,"GameFontHighlightSmall",header.Text,
            origin+AuctionFrameUiLaw.HeaderTextMin(header)*s,s);
    }
    private void DrawAuctionRows(ImDrawListPtr dl, Vector2 origin, float s, int tab)
    {
        int maximumRows = tab == 0 ? 8 : 9;
        for(int i=0;i<_auctions.Count&&i<maximumRows;i++)
        {
            AuctionRow row=_auctions[i];
            ItemTemplate? item=null;
            if(_items?.TryGet(row.Item,out ItemTemplate? found)==true)item=found;
            AuctionFrameUiLaw.LogicalRect seat=tab switch
            {
                1=>AuctionFrameUiLaw.BidRow(i),
                2=>AuctionFrameUiLaw.OwnerRow(i),
                _=>AuctionFrameUiLaw.AuctionRow(i),
            };
            Vector2 rowMin=origin+seat.Min*s;
            ImGui.SetCursorScreenPos(rowMin);
            bool clicked=ImGui.InvisibleButton($"##auction-row-{tab}-{row.Id}",seat.Size*s);
            bool highlighted=_auctionSelected==i||ImGui.IsItemHovered();

            uint nameFrame=_gameplayArt?.Handle(AuctionFrameUiLaw.ItemNameFramePath)??0;
            if(nameFrame!=0)
                foreach(AuctionFrameUiLaw.TextureSlice slice in
                        AuctionFrameUiLaw.ItemNameSlices(seat.Width))
                {
                    Vector2 min=rowMin+slice.Rect.Min*s;
                    dl.AddImage((nint)nameFrame,min,min+slice.Rect.Size*s,
                        slice.UvMin,slice.UvMax);
                }

            if(item is not null)
            {
                uint icon=_gameplayArt?.Handle(item.IconPath)??0;
                if(icon!=0)dl.AddImage((nint)icon,rowMin,rowMin+new Vector2(32)*s);
            }
            DrawArt(dl,AuctionFrameUiLaw.ItemSlotPath,
                rowMin-new Vector2(14)*s,new Vector2(60),s);
            if(row.Count>1)
                GameText.DrawRightAligned(dl,"NumberFontNormal",row.Count.ToString(),
                    rowMin+new Vector2(29,20)*s,s,0xffffffff);

            float textTop=GameText.BoxCenteredTop("GameFontNormal",rowMin.Y,32,s);
            uint nameColor=item is null?0xffffffff:
                ImGui.ColorConvertFloat4ToU32(ItemQualityColor(item.Quality));
            GameText.Draw(dl,"GameFontNormal",item?.Name??$"Item {row.Item}",
                new Vector2(rowMin.X+(tab==1?41:43)*s,textTop),s,nameColor);

            if(tab==0)
            {
                GameText.DrawCentered(dl,"GameFontHighlightSmall",
                    (item?.RequiredLevel??0).ToString(),rowMin+new Vector2(231,16)*s,s);
                GameText.DrawCentered(dl,"GameFontHighlightSmall",
                    AuctionFrameUiLaw.TimeLeftText(row.TimeLeftMs),
                    rowMin+new Vector2(299,16)*s,s);
                GameText.DrawCentered(dl,"GameFontHighlightSmall",
                    _playerNames.GetValueOrDefault(row.Owner,""),
                    rowMin+new Vector2(384,16)*s,s);
                uint current=row.Bid==0?row.StartBid:row.Bid;
                DrawTrainerMoney(dl,current,rowMin+new Vector2(seat.Width+10,3)*s,
                    s,0xffffffff,true);
                if(row.Buyout>0)
                    DrawTrainerMoney(dl,row.Buyout,rowMin+new Vector2(seat.Width+10,19)*s,
                        s,VanillaGold,true);
            }
            else if(tab==1)
            {
                GameText.DrawCentered(dl,"GameFontHighlightSmall",
                    (item?.RequiredLevel??0).ToString(),rowMin+new Vector2(266,16)*s,s);
                GameText.DrawCentered(dl,"GameFontHighlightSmall",
                    AuctionFrameUiLaw.TimeLeftText(row.TimeLeftMs),
                    rowMin+new Vector2(332,16)*s,s);
                if(row.Buyout>0)
                    DrawTrainerMoney(dl,row.Buyout,rowMin+new Vector2(535,10)*s,
                        s,VanillaGold,true);
                string status=row.Bidder==0?"":row.Bidder==_net?.PlayerGuid?"High Bidder":"Outbid";
                GameText.DrawCentered(dl,"GameFontHighlightSmall",status,
                    rowMin+new Vector2(580,16)*s,s);
                DrawTrainerMoney(dl,row.Bid==0?row.StartBid:row.Bid,
                    rowMin+new Vector2(795,10)*s,s,0xffffffff,true);
            }
            else
            {
                GameText.DrawCentered(dl,"GameFontHighlightSmall",
                    AuctionFrameUiLaw.TimeLeftText(row.TimeLeftMs),
                    rowMin+new Vector2(223,16)*s,s);
                GameText.DrawCentered(dl,"GameFontHighlightSmall",
                    _playerNames.GetValueOrDefault(row.Bidder,""),
                    rowMin+new Vector2(331,16)*s,s);
                DrawTrainerMoney(dl,row.Bid==0?row.StartBid:row.Bid,
                    rowMin+new Vector2(seat.Width,3)*s,s,0xffffffff,true);
                if(row.Buyout>0)
                {
                    GameText.Draw(dl,"GameFontNormalSmall","Buyout",
                        rowMin+new Vector2(401,20)*s,s,VanillaGold);
                    DrawTrainerMoney(dl,row.Buyout,rowMin+new Vector2(seat.Width,19)*s,
                        s,VanillaGold,true);
                }
            }

            if(highlighted)
            {
                uint highlight=_gameplayArt?.AdditiveHandle(
                    AuctionFrameUiLaw.RowHighlightPath)??0;
                float width=tab switch {1=>670,2=>540,_=>523};
                if(highlight!=0)
                    dl.AddImage((nint)highlight,rowMin+new Vector2(33,0)*s,
                        rowMin+new Vector2(33+width,32)*s,
                        Vector2.Zero,new Vector2(1,.578125f));
            }
            if(clicked)
            {
                _auctionSelected=i;
                if(tab!=2)
                {
                    uint minimum=Math.Max(row.StartBid,row.Bid+row.MinIncrement);
                    _auctionBidAmount=(int)Math.Min((uint)int.MaxValue,minimum);
                }
            }
        }

        if(_auctions.Count==0)return;
        AuctionRow selected=_auctions[Math.Clamp(_auctionSelected,0,_auctions.Count-1)];
        if(tab is 0 or 1)
        {
            uint minimum=Math.Max(selected.StartBid,selected.Bid+selected.MinIncrement);
            uint offered=(uint)Math.Max((long)minimum,_auctionBidAmount);
            if(VanillaButton(dl,$"##auction-{tab}-bid","Bid",
                    origin+AuctionFrameUiLaw.BidButton.Min*s,
                    AuctionFrameUiLaw.BidButton.Size,s))
                BidAuction(selected.Id,offered);
            if(VanillaButton(dl,$"##auction-{tab}-buyout","Buyout",
                    origin+AuctionFrameUiLaw.BuyoutButton.Min*s,
                    AuctionFrameUiLaw.BuyoutButton.Size,s,selected.Buyout>0))
                BidAuction(selected.Id,selected.Buyout);
        }
        else if(VanillaButton(dl,"##auction-owner-cancel","Cancel Auction",
                origin+AuctionFrameUiLaw.OwnerCancelButton.Min*s,
                AuctionFrameUiLaw.OwnerCancelButton.Size,s))
            CancelAuction(selected.Id);
    }

    private void DrawAuctionOwnerForm(ImDrawListPtr dl, Vector2 origin, float s)
    {
        GameText.DrawCentered(dl,"GameFontHighlightSmall","Create Auction",
            origin+new Vector2(121,55)*s,s);
        GameText.Draw(dl,"GameFontHighlightSmall","Auction Item",
            origin+AuctionFrameUiLaw.OwnerItemLabel*s,s);

        ItemTemplate? item=null;
        if(_auctionSellEntry!=0&&_items?.TryGet(_auctionSellEntry,out ItemTemplate? found)==true)
            item=found;
        Vector2 slotMin=origin+AuctionFrameUiLaw.OwnerItemSlot.Min*s;
        if(item is not null)
        {
            uint icon=_gameplayArt?.Handle(item.IconPath)??0;
            if(icon!=0)dl.AddImage((nint)icon,slotMin,
                slotMin+AuctionFrameUiLaw.OwnerItemSlot.Size*s);
            GameText.Draw(dl,"GameFontNormal",item.Name,
                origin+AuctionFrameUiLaw.OwnerItemName*s,s,
                ImGui.ColorConvertFloat4ToU32(ItemQualityColor(item.Quality)));
        }
        ImGui.SetCursorScreenPos(slotMin);
        bool clicked=ImGui.InvisibleButton("##auction-sell-item",
            AuctionFrameUiLaw.OwnerItemSlot.Size*s);
        if(ImGui.IsItemHovered())
        {
            uint highlight=_gameplayArt?.AdditiveHandle(
                @"Interface\Buttons\ButtonHilight-Square")??0;
            if(highlight!=0)dl.AddImage((nint)highlight,slotMin,
                slotMin+AuctionFrameUiLaw.OwnerItemSlot.Size*s);
        }
        if(clicked)
        {
            if(HasCarriedItem&&ResolveCarriedItem() is { } carried)
            { _auctionSellEntry=carried.Entry; ClearCarriedItem(); }
            else if(_auctionSellEntry!=0)_auctionSellEntry=0;
        }
        if(ImGui.BeginDragDropTarget())
        {
            ImGui.AcceptDragDropPayload("MSUI_INVENTORY_ITEM");
            if(ImGui.IsMouseReleased(ImGuiMouseButton.Left)&&
                ResolveCarriedItem() is { } carried)
            { _auctionSellEntry=carried.Entry; ClearCarriedItem(); }
            ImGui.EndDragDropTarget();
        }

        GameText.Draw(dl,"GameFontHighlightSmall","Starting Price",
            origin+AuctionFrameUiLaw.OwnerStartPriceLabel*s,s);
        DrawAuctionMoneyInput(dl,"auction-start-price",ref _auctionSellBid,
            origin+AuctionFrameUiLaw.OwnerStartPrice.Min*s,s);
        GameText.Draw(dl,"GameFontHighlightSmall","Auction Duration",
            origin+AuctionFrameUiLaw.OwnerDurationLabel*s,s);
        DrawMailRadio(dl,"##auction-duration-short",
            origin+AuctionFrameUiLaw.OwnerShortDuration*s,"2 Hours",
            _auctionSellDuration==120,true,s,()=>_auctionSellDuration=120);
        DrawMailRadio(dl,"##auction-duration-medium",
            origin+AuctionFrameUiLaw.OwnerMediumDuration*s,"8 Hours",
            _auctionSellDuration==480,true,s,()=>_auctionSellDuration=480);
        DrawMailRadio(dl,"##auction-duration-long",
            origin+AuctionFrameUiLaw.OwnerLongDuration*s,"24 Hours",
            _auctionSellDuration==1440,true,s,()=>_auctionSellDuration=1440);
        GameText.Draw(dl,"GameFontHighlightSmall","Buyout Price (optional)",
            origin+AuctionFrameUiLaw.OwnerBuyoutLabel*s,s);
        DrawAuctionMoneyInput(dl,"auction-buyout-price",ref _auctionSellBuyout,
            origin+AuctionFrameUiLaw.OwnerBuyoutPrice.Min*s,s);
        GameText.Draw(dl,"GameFontNormal","Deposit:",
            origin+AuctionFrameUiLaw.OwnerDepositLabel*s,s,VanillaGold);
        uint deposit=item is null?0:CalculateAuctionDeposit(item.SellPrice,1,
            (uint)_auctionSellDuration);
        DrawTrainerMoney(dl,deposit,origin+AuctionFrameUiLaw.OwnerDepositMoney*s,
            s,0xffffffff,false);

        bool ready=_auctionSellEntry!=0&&_auctionSellBid>0;
        if(VanillaButton(dl,"##auction-create","Create Auction",
                origin+AuctionFrameUiLaw.OwnerCreateButton.Min*s,
                AuctionFrameUiLaw.OwnerCreateButton.Size,s,ready))
            CreateAuction(_auctionSellEntry,(uint)_auctionSellBid,
                (uint)Math.Max(0,_auctionSellBuyout),(uint)_auctionSellDuration);
    }

    private bool DrawAuctionMoneyEditBox(ImDrawListPtr dl, string id, ref int value,
        Vector2 min, Vector2 logicalSize, float rightInset, float s)
    {
        uint border=_gameplayArt?.Handle(AuctionFrameUiLaw.InputBorderPath)??0;
        if(border!=0)
            foreach(AuctionFrameUiLaw.TextureSlice slice in
                    AuctionFrameUiLaw.MoneyInputBorderSlices(logicalSize.X,rightInset))
            {
                Vector2 sliceMin=min+slice.Rect.Min*s;
                dl.AddImage((nint)border,sliceMin,sliceMin+slice.Rect.Size*s,
                    slice.UvMin,slice.UvMax);
            }
        ImGui.SetCursorScreenPos(min+new Vector2(6,2)*s);
        ImGui.SetNextItemWidth((logicalSize.X-12)*s);
        ImGui.PushStyleColor(ImGuiCol.FrameBg,Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered,Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive,Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.Border,Vector4.Zero);
        bool changed=ImGui.InputInt(id,ref value,0,0);
        ImGui.PopStyleColor(4);
        return changed;
    }
    private void DrawAuctionMoneyInput(ImDrawListPtr dl, string id, ref int copper,
        Vector2 min, float s)
    {
        int safe=Math.Max(0,copper);
        int gold=safe/10000;
        int silver=safe/100%100;
        int bronze=safe%100;
        bool changed=DrawAuctionMoneyEditBox(dl,$"##{id}-gold",ref gold,min,
            new Vector2(58,20),0,s);
        DrawMailCoin(dl,0,min+new Vector2(60,3)*s,s);
        changed|=DrawAuctionMoneyEditBox(dl,$"##{id}-silver",ref silver,
            min+new Vector2(84,0)*s,new Vector2(30,20),10,s);
        DrawMailCoin(dl,1,min+new Vector2(106,3)*s,s);
        changed|=DrawAuctionMoneyEditBox(dl,$"##{id}-copper",ref bronze,
            min+new Vector2(130,0)*s,new Vector2(30,20),10,s);
        DrawMailCoin(dl,2,min+new Vector2(152,3)*s,s);
        if(changed)
            copper=(int)Math.Min(int.MaxValue,
                (long)Math.Max(0,gold)*10000L+(long)Math.Clamp(silver,0,99)*100L+
                Math.Clamp(bronze,0,99));
    }
}

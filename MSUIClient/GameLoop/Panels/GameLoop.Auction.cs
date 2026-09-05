using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    // ── Model ────────────────────────────────────────────────────────────────────────────────
    // Three lists, one session (Browse / Bids / Auctions = tab 0 / 1 / 2). Each holds at most one
    // 50-row server page plus the pre-cap match count the pager needs, its own page, scroll, sort
    // and selection. Sorting is ours and paging is the server's: a header click re-orders rows we
    // hold and sends nothing; a page turn re-sends the query. The selection is an AUCTION ID, never
    // a row index — a resort must not point the Bid button at a different auction.
    //
    // ACTOR: everything here acts as the body standing at the auctioneer — ControlledGuid. While
    // possessing a bot that is the bot: its purse gates the buttons, its bags feed the sell slot,
    // "your auction"/"high bidder" compare against ITS guid, and the server's GetSuiActor-threaded
    // handlers list, bid and cancel as it (re-snapshotting its purse/bags back to this session).

    private sealed class AuctionList
    {
        public readonly List<AuctionListEntry> Rows = [];
        public uint Total;
        public uint Page;
        public int Scroll;
        public uint SelectedId;
        public string SortKey = "";
        public bool SortReversed;
        public void Clear() { Rows.Clear(); Total = 0; Page = 0; Scroll = 0; SelectedId = 0; }
    }

    private readonly AuctionList[] _auctionLists = [new(), new(), new()];
    private bool _auctionOpen;
    private ulong _auctioneerGuid;
    private uint _auctionHouseId;
    private int _auctionTab;
    // Sell slot: the (container, slot) the item sat in when attached, plus the guid/entry to
    // verify against at send time — the reference re-reads the slot when Create fires, and a
    // stale re-resolve would auction whatever else has landed there since.
    private int _auctionSellContainer = -1;
    private int _auctionSellSlot = -1;
    private ulong _auctionSellGuid;
    private uint _auctionSellEntry;
    private int _auctionSellBid;
    private int _auctionSellBuyout;
    private int _auctionSellDuration = 480;
    private int _auctionBidAmount;
    // Browse filters.
    private readonly byte[] _auctionSearch = new byte[65];
    private readonly byte[] _auctionMinLevel = new byte[3];
    private readonly byte[] _auctionMaxLevel = new byte[3];
    private bool _auctionUsableOnly;
    private int _auctionRarityIndex;
    private bool _auctionRarityOpen;
    private uint _auctionClass = AuctionBrowseQuery.Any;
    private uint _auctionSubclass = AuctionBrowseQuery.Any;
    private uint _auctionInventoryType = AuctionBrowseQuery.Any;
    private int _auctionFilterScroll;
    private double _auctionBrowseGateUntil;
    private bool _auctionRefreshOwner;
    private bool _auctionRefreshBidder;
    // The confirm dialogs (BUYOUT_AUCTION / CANCEL_AUCTION).
    private int _auctionConfirmKind;          // 0 none, 1 buyout, 2 cancel
    private uint _auctionConfirmId;
    private uint _auctionConfirmMoney;
    // Outcome lines that name an item wait for the item's template, exactly like the reference
    // defers through its item-info callback. They outlive the window on purpose: "your auction
    // sold" arrives wherever the player is standing.
    private readonly List<(string Key, string Fallback, uint Item)> _auctionPendingLines = [];
    private AuctionHouseCatalog? _auctionHouses;
    private bool _auctionHousesLoaded;
    private ItemClassCatalog? _itemClasses;
    private bool _itemClassesLoaded;

    private void InitAuction() { }

    private void ResetAuction()
    {
        // AUCTION_HOUSE_CLOSED → PlaySound("AuctionWindowClose") (reference AuctionFrame.xml).
        if (_auctionOpen) PlayUiSound("AuctionWindowClose", "ui.auction");
        foreach (AuctionList list in _auctionLists) list.Clear();
        _auctionOpen = false; _auctioneerGuid = 0; _auctionHouseId = 0;
        _auctionRefreshOwner = _auctionRefreshBidder = false;
        _auctionConfirmKind = 0; _auctionRarityOpen = false;
        ClearAuctionSellSlot();
    }

    private void ClearAuctionSellSlot()
    {
        _auctionSellContainer = -1; _auctionSellSlot = -1; _auctionSellGuid = 0; _auctionSellEntry = 0;
    }

    /// <summary>The purse the window shows and gates on: the driven body's.</summary>
    private uint AuctionActorMoney() =>
        _entities.TryGet(ControlledGuid, out WorldEntity actor) ? actor.Fields.Coinage : 0;

    private void EnsureAuctionCatalogs()
    {
        if (!_auctionHousesLoaded && _mpq is not null)
        {
            _auctionHousesLoaded = true;
            try { _auctionHouses = AuctionHouseCatalog.Load(_mpq); }
            catch (Exception e) { Console.WriteLine($"[auction] AuctionHouse.dbc load failed: {e.Message}"); }
        }
        if (!_itemClassesLoaded && _mpq is not null)
        {
            _itemClassesLoaded = true;
            try { _itemClasses = ItemClassCatalog.Load(_mpq); }
            catch (Exception e) { Console.WriteLine($"[auction] ItemClass.dbc load failed: {e.Message}"); }
            try { _itemSubClasses ??= ItemSubClassCatalog.Load(_mpq); }
            catch (Exception e) { Console.WriteLine($"[auction] ItemSubClass.dbc load failed: {e.Message}"); }
        }
    }

    /// <summary>The house's deposit rate: AuctionHouse.dbc by the hello reply's id (5 faction / 25 goblin).</summary>
    private uint AuctionDepositPercent()
    {
        EnsureAuctionCatalogs();
        return _auctionHouses?.DepositPercent(_auctionHouseId) ?? (_auctionHouseId == 7 ? 25u : 5u);
    }

    // ── Session ──────────────────────────────────────────────────────────────────────────────

    private bool AuctioneerEligible(ulong guid, out WorldEntity? npc, out float distanceSquared)
    {
        npc = null;
        distanceSquared = float.PositiveInfinity;
        // Interaction range is measured from the DRIVEN body (the bot while possessing), the
        // same law the trainer and vendor sessions use.
        if (_net is not { IsInWorld: true } ||
            !TryGetInteractionBodyPose(out WorldBodyPose body) ||
            !_entities.TryGet(guid, out npc) || !npc.IsCreature || npc.IsDead ||
            (npc.NpcFlags & NpcAuctioneer) == 0)
            return false;
        distanceSquared = Vector3.DistanceSquared(body.Position, npc.Position);
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
        if (RefuseTacticalFreezeLiveCommand("opening the auction house")) return false;
        if (RefuseTacticalFrozenActor(guid, "open its auction service")) return false;
        bool eligible = AuctioneerEligible(guid, out WorldEntity? npc, out float distanceSquared);
        bool sent = eligible && _net?.AuctionHello(guid) == true;
        EmitInterface("auction", "hello-send", sent ? "SENT" : "REFUSED", guid,
            $"eligible={eligible};distanceSquared={distanceSquared:R};actor=0x{ControlledGuid:X};" +
            $"npcFlags=0x{npc?.NpcFlags ?? 0:X8}");
        return sent;
    }

    /// <summary>Per frame: range close, deferred outcome lines, and server-driven list refreshes.</summary>
    private bool UpdateAuctionLifecycle()
    {
        FlushAuctionPendingLines();
        if (!_auctionOpen) return false;
        if (!TryGetInteractionBodyPose(out WorldBodyPose body)) return false;
        ulong sourceGuid = _auctioneerGuid;
        bool sourceAvailable = _entities.TryGet(sourceGuid, out WorldEntity auctioneer) &&
            auctioneer.IsCreature && !auctioneer.IsDead &&
            (auctioneer.NpcFlags & NpcAuctioneer) != 0;
        float distanceSquared = sourceAvailable
            ? Vector3.DistanceSquared(body.Position, auctioneer.Position)
            : float.PositiveInfinity;
        if (NpcSessionUiLaw.ShouldClose(true, true, sourceAvailable, distanceSquared))
        {
            ResetAuction();
            EmitInterface("auction", "lifecycle-close", "CLOSED", sourceGuid,
                sourceAvailable
                    ? $"distanceSquared={distanceSquared:R};" +
                      $"limitSquared={NpcSessionUiLaw.ServiceRangeSquared:R}"
                    : "source-unavailable");
            return true;
        }
        // A sale, an outbid or a cancel made a list we hold stale: re-ask page 0 rather than
        // patching the local copy — the server is the only thing that knows the page now.
        if (_auctionRefreshOwner) { _auctionRefreshOwner = false; RequestOwnerAuctions(0); }
        if (_auctionRefreshBidder) { _auctionRefreshBidder = false; RequestBidderAuctions(0); }
        return false;
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
                $"house={house};distanceSquared={distanceSquared:R}");
            return;
        }
        // A different auctioneer resets everything; the same one keeps its lists and just
        // re-shows (the reference re-fires its show on every greeting).
        if (_auctioneerGuid != guid) foreach (AuctionList list in _auctionLists) list.Clear();
        _auctioneerGuid = guid;
        _auctionHouseId = house;
        // AUCTION_HOUSE_SHOW → PlaySound("AuctionWindowOpen"); re-fired on every greeting.
        PlayUiSound("AuctionWindowOpen", "ui.auction");
        _auctionOpen = true;
        _auctionBrowseGateUntil = 0;   // the window opening clears the throttle
        _auctionConfirmKind = 0;
        EnsureAuctionCatalogs();
        EmitInterface("auction", "hello", "OPEN", _auctioneerGuid,
            $"house={_auctionHouseId};deposit%={AuctionDepositPercent()};distanceSquared={distanceSquared:R}");
    }

    // ── Queries ──────────────────────────────────────────────────────────────────────────────

    private bool AuctionBrowseGated => NowSeconds() < _auctionBrowseGateUntil;

    private AuctionBrowseQuery BuildBrowseQuery(uint page)
    {
        byte.TryParse(ReadBuffer(_auctionMinLevel), out byte levelMin);
        byte.TryParse(ReadBuffer(_auctionMaxLevel), out byte levelMax);
        uint quality = _auctionRarityIndex <= 0 ? AuctionBrowseQuery.Any : (uint)(_auctionRarityIndex - 1);
        return new AuctionBrowseQuery(page * AuctionBrowseQuery.PageSize, ReadBuffer(_auctionSearch),
            levelMin, levelMax, _auctionInventoryType, _auctionClass, _auctionSubclass, quality,
            _auctionUsableOnly);
    }

    /// <summary>
    /// The browse query, behind the reference's 5 s gate — a query inside the gate is dropped
    /// with no event at all, which is why the Search button reads the gate every frame.
    /// </summary>
    private bool BrowseAuctions(uint page)
    {
        if (!AuctionSessionInRange(out _) || _net is null || AuctionBrowseGated) return false;
        AuctionBrowseQuery query = BuildBrowseQuery(page);
        bool sent = _net.AuctionBrowse(_auctioneerGuid, query);
        if (sent)
        {
            _auctionBrowseGateUntil = NowSeconds() + AuctionFrameUiLaw.BrowseThrottleSeconds;
            _auctionLists[0].Page = page;
        }
        EmitInterface("auction", "browse-send", sent ? "SENT" : "SEND_FAILED", _auctioneerGuid,
            $"page={page};search={SanitizeEvidence(query.Search)};class={query.ItemClass};sub={query.Subclass};" +
            $"inv={query.InventoryType};quality={query.Quality};levels={query.LevelMin}-{query.LevelMax};usable={query.UsableOnly}");
        return sent;
    }

    private bool RequestOwnerAuctions(uint page)
    {
        if (!AuctionSessionInRange(out _) || _net is null) return false;
        bool sent = _net.AuctionOwnerList(_auctioneerGuid, page * AuctionBrowseQuery.PageSize);
        if (sent) _auctionLists[2].Page = page;
        EmitInterface("auction", "owner-send", sent ? "SENT" : "SEND_FAILED", _auctioneerGuid, $"page={page}");
        return sent;
    }

    private bool RequestBidderAuctions(uint page)
    {
        if (!AuctionSessionInRange(out _) || _net is null) return false;
        bool sent = _net.AuctionBidderList(_auctioneerGuid, page * AuctionBrowseQuery.PageSize);
        if (sent) _auctionLists[1].Page = page;
        EmitInterface("auction", "bidder-send", sent ? "SENT" : "SEND_FAILED", _auctioneerGuid, $"page={page}");
        return sent;
    }

    // ── Wire in ──────────────────────────────────────────────────────────────────────────────

    private void ApplyAuctionList(byte[] body, string kind)
    {
        int tab = kind switch { "bidder" => 1, "owner" => 2, _ => 0 };
        try
        {
            (List<AuctionListEntry> rows, uint total) = AuctionPackets.ParseList(body);
            // A page can land for a window that just closed (we walked away mid-flight).
            if (!_auctionOpen)
            {
                EmitInterface("auction", kind, "DROPPED-NO-SESSION", 0, $"count={rows.Count}");
                return;
            }
            if (tab == 1)
            {
                // The server emits the explicitly refreshed ids first and then every auction we
                // hold the bid on, so one auction can appear twice; keep the first.
                var seen = new HashSet<uint>();
                rows = rows.Where(row => seen.Add(row.Id)).ToList();
            }
            AuctionList list = _auctionLists[tab];
            list.Rows.Clear(); list.Rows.AddRange(rows);
            list.Total = Math.Max(total, (uint)rows.Count);
            list.Scroll = 0;
            if (list.SelectedId != 0 && rows.All(row => row.Id != list.SelectedId)) list.SelectedId = 0;
            foreach (AuctionListEntry row in rows)
            {
                _items?.Require(row.Item, 0, _net!);
                ulong who = tab == 2 ? row.Bidder : row.Owner;
                if (who != 0 && !_playerNames.ContainsKey(who)) _net?.NameQuery(who);
            }
            EmitInterface("auction", kind, "DECODED", _auctioneerGuid,
                $"count={rows.Count};total={list.Total};page={list.Page};bytes={body.Length}");
        }
        catch (Exception ex)
        {
            EmitInterface("auction", kind, "MALFORMED", _auctioneerGuid,
                $"error={SanitizeEvidence(ex.Message)};bytes={body.Length}");
        }
    }

    private void ApplyAuctionCommand(byte[] body)
    {
        if (body.Length < 12) return;
        AuctionPackets.CommandResult result = AuctionPackets.ParseCommand(body);
        string kind = result.Action switch { 0 => "create", 1 => "cancel", 2 => "bid", _ => $"action-{result.Action}" };
        EmitInterface("auction", kind, result.Error == 0 ? "SUCCESS" : $"FAILED-{result.Error}", _auctioneerGuid,
            $"auction={result.AuctionId};action={result.Action};error={result.Error};" +
            $"newMin={result.NewMinOutbid};inv={result.InventoryResult}");
        if (result.Error == AuctionPackets.ErrorOk)
        {
            // Each success says so in CHAT and re-queries the list it changed.
            switch (result.Action)
            {
                case AuctionPackets.ActionStarted:
                    ClearAuctionSellSlot();      // the item is gone from the bag
                    _auctionRefreshOwner = true;
                    AddChatMessage(AuctionGlobalString("ERR_AUCTION_STARTED", "Auction created."));
                    break;
                case AuctionPackets.ActionRemoved:
                    _auctionRefreshOwner = true;
                    AddChatMessage(AuctionGlobalString("ERR_AUCTION_REMOVED", "Auction cancelled."));
                    break;
                case AuctionPackets.ActionBidPlaced:
                    _auctionRefreshBidder = true;
                    if (_auctionTab == 0) _auctionRefreshBrowseAfterBid = true;
                    AddChatMessage(AuctionGlobalString("ERR_AUCTION_BID_PLACED", "Bid accepted."));
                    break;
            }
            return;
        }
        // HIGHER_BID is the live outbid UPDATE path, not a message: the row re-syncs and the line
        // the player sees is ERR_AUCTION_OUTBID_S off the bidder notification.
        if (result.Error == AuctionPackets.ErrorHigherBid)
        {
            _auctionRefreshBidder = true;
            _auctionRefreshBrowseAfterBid = true;
            return;
        }
        string text = result.Error switch
        {
            AuctionPackets.ErrorInventory => InventoryGlobalString(
                InventoryErrorUiLaw.GlobalStringKey((byte)Math.Min(255, result.InventoryResult)),
                "Internal auction error."),
            AuctionPackets.ErrorNotEnoughMoney => AuctionGlobalString("ERR_NOT_ENOUGH_MONEY", "You don't have enough money."),
            AuctionPackets.ErrorItemNotFound => AuctionGlobalString("ERR_ITEM_NOT_FOUND", "Item not found."),
            AuctionPackets.ErrorBidIncrement => AuctionGlobalString("ERR_AUCTION_BID_INCREMENT", "Your bid must be at least the minimum increment higher than the current bid."),
            AuctionPackets.ErrorBidOwn => AuctionGlobalString("ERR_AUCTION_BID_OWN", "You cannot bid on your own auction."),
            AuctionPackets.ErrorRestrictedAccount => AuctionGlobalString("ERR_RESTRICTED_ACCOUNT", "Your account is restricted."),
            _ => AuctionGlobalString("ERR_AUCTION_DATABASE_ERROR", "Internal auction error."),
        };
        ShowUiError(text);
    }

    private bool _auctionRefreshBrowseAfterBid;

    private void ApplyAuctionNotification(Op op, byte[] body)
    {
        try
        {
            switch (op)
            {
                case Op.SMSG_AUCTION_BIDDER_NOTIFICATION:
                {
                    AuctionPackets.BidderNotification n = AuctionPackets.ParseBidderNotification(body);
                    // bidOrZero == 0 means WON, not "no bid" — the server overloads the field.
                    bool won = n.BidOrZero == 0;
                    QueueAuctionItemLine(won ? "ERR_AUCTION_WON_S" : "ERR_AUCTION_OUTBID_S",
                        won ? "You won an auction for %s" : "You have been outbid on %s.", n.ItemEntry);
                    _auctionRefreshBidder = true;
                    EmitInterface("auction", "notification", won ? "WON" : "OUTBID", _auctioneerGuid,
                        $"auction={n.AuctionId};item={n.ItemEntry};bid={n.BidOrZero};outBid={n.OutBid}");
                    break;
                }
                case Op.SMSG_AUCTION_OWNER_NOTIFICATION:
                {
                    AuctionPackets.OwnerNotification n = AuctionPackets.ParseOwnerNotification(body);
                    // Two stages: a NON-zero bidder guid is "somebody bid on your auction" — the
                    // row updates and the client says nothing. Only a zeroed guid reaches the
                    // message path, where the bid picks the line: non-zero sold, zero expired.
                    string outcome = "BID";
                    if (n.Bidder == 0)
                    {
                        bool sold = n.Bid != 0;
                        outcome = sold ? "SOLD" : "EXPIRED";
                        QueueAuctionItemLine(sold ? "ERR_AUCTION_SOLD_S" : "ERR_AUCTION_EXPIRED_S",
                            sold ? "Your auction of %s sold." : "Your auction of %s expired.", n.ItemEntry);
                    }
                    _auctionRefreshOwner = true;
                    EmitInterface("auction", "notification", outcome, _auctioneerGuid,
                        $"auction={n.AuctionId};item={n.ItemEntry};bid={n.Bid};bidder=0x{n.Bidder:X}");
                    break;
                }
                case Op.SMSG_AUCTION_REMOVED_NOTIFICATION:
                {
                    AuctionPackets.RemovedNotification n = AuctionPackets.ParseRemovedNotification(body);
                    QueueAuctionItemLine("ERR_AUCTION_REMOVED_S", "Your bid on %s has been cancelled.", n.ItemEntry);
                    _auctionRefreshBidder = true;
                    EmitInterface("auction", "notification", "REMOVED", _auctioneerGuid,
                        $"auction={n.AuctionId};item={n.ItemEntry}");
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            EmitInterface("auction", "notification", "MALFORMED", _auctioneerGuid,
                $"opcode={(ushort)op};error={SanitizeEvidence(ex.Message)};bytes={body.Length}");
        }
    }

    private string AuctionGlobalString(string key, string fallback) => InventoryGlobalString(key, fallback);

    private void QueueAuctionItemLine(string key, string fallback, uint itemEntry)
    {
        if (_net is not null) _items?.Require(itemEntry, 0, _net);
        _auctionPendingLines.Add((key, fallback, itemEntry));
    }

    /// <summary>Print the item-named outcome lines whose template has landed; keep the rest queued.</summary>
    private void FlushAuctionPendingLines()
    {
        if (_auctionPendingLines.Count == 0) return;
        for (int i = _auctionPendingLines.Count - 1; i >= 0; i--)
        {
            (string key, string fallback, uint item) = _auctionPendingLines[i];
            if (_items?.TryGet(item, out ItemTemplate? template) != true || template is null) continue;
            AddChatMessage(AuctionGlobalString(key, fallback).Replace("%s", template.Name, StringComparison.Ordinal));
            _auctionPendingLines.RemoveAt(i);
        }
    }

    // ── Wire out ─────────────────────────────────────────────────────────────────────────────

    private bool BidAuction(int tab, uint id, uint price)
    {
        if (!AuctionSessionInRange(out _) || _net is null ||
            _auctionLists[tab].Rows.All(x => x.Id != id)) return false;
        if (RefuseTacticalFreezeLiveCommand("placing an auction bid")) return false;
        if (RefuseTacticalFrozenActor(_auctioneerGuid, "place an auction bid through it"))
            return false;
        bool sent = _net.AuctionBid(_auctioneerGuid, id, price);
        EmitInterface("auction", "bid-send", sent ? "SENT" : "SEND_FAILED", _auctioneerGuid,
            $"auction={id};price={price};actor=0x{ControlledGuid:X}");
        return sent;
    }

    private bool CancelAuction(uint id)
    {
        if (!AuctionSessionInRange(out _) || _net is null) return false;
        if (RefuseTacticalFreezeLiveCommand("cancelling an auction")) return false;
        if (RefuseTacticalFrozenActor(_auctioneerGuid, "cancel an auction through it"))
            return false;
        bool sent = _net.AuctionCancel(_auctioneerGuid, id);
        EmitInterface("auction", "cancel-send", sent ? "SENT" : "SEND_FAILED", _auctioneerGuid,
            $"auction={id};actor=0x{ControlledGuid:X}");
        return sent;
    }

    /// <summary>The staged sell item, re-resolved from its slot (the DRIVEN body's bags).</summary>
    private WorldEntity? ResolveAuctionSellItem()
    {
        if (_auctionSellGuid == 0 || _auctionSellContainer < 0) return null;
        WorldEntity? item = ResolveInventoryItem(_auctionSellContainer, _auctionSellSlot);
        if (item is null || item.Guid != _auctionSellGuid)
        {
            // The slot no longer holds what the player chose: let go rather than auction a stranger.
            ClearAuctionSellSlot();
            return null;
        }
        return item;
    }

    private bool CreateAuction(uint bid, uint buyout, uint durationMinutes)
    {
        if (!AuctionSessionInRange(out _) || _net is null) return false;
        WorldEntity? item = ResolveAuctionSellItem();
        if (item is null) return false;
        if (RefuseTacticalFreezeLiveCommand("creating an auction")) return false;
        if (RefuseTacticalFrozenActor(_auctioneerGuid, "create an auction through it"))
            return false;
        bool sent = _net.AuctionSell(_auctioneerGuid, item.Guid, bid, buyout, durationMinutes);
        EmitInterface("auction", "create-send", sent ? "SENT" : "SEND_FAILED", _auctioneerGuid,
            $"item={item.Entry};itemGuid=0x{item.Guid:X16};count={item.Fields.ItemStackCount};bid={bid};" +
            $"buyout={buyout};duration={durationMinutes};actor=0x{ControlledGuid:X}");
        return sent;
    }

    private uint AuctionSellDeposit(WorldEntity? item)
    {
        if (item is null || _items?.TryGet(item.Entry, out ItemTemplate? template) != true || template is null) return 0;
        return AuctionFrameUiLaw.Deposit(AuctionDepositPercent(), template.SellPrice,
            Math.Max(1, item.Fields.ItemStackCount), (uint)_auctionSellDuration);
    }

    /// <summary>Dev replay (LiveRun "auction simulate"): exercise the decoders with correctly sized bodies.</summary>
    private void SimulateAuctionFlow()
    {
        _auctioneerGuid = 0xF130000361000001;
        _auctionHouseId = 1;
        _auctionOpen = true;
        EmitInterface("auction", "hello", "OPEN", _auctioneerGuid, "house=1;source=runtime-replay");
        var list = new PacketWriter(); list.WriteU32(2);
        WriteAuctionRow(list, 700, 159, 1, 0x111, 100, 5, 250, 3600000, 0, 0);
        WriteAuctionRow(list, 701, 117, 5, 0x222, 50, 3, 150, 7200000, 0x333, 80);
        list.WriteU32(77); ApplyAuctionList(list.ToArray(), "browse");
        foreach ((uint id, uint action) in new[] { (700u, 0u), (701u, 2u), (700u, 1u) })
        {
            var result = new PacketWriter(); result.WriteU32(id); result.WriteU32(action); result.WriteU32(0);
            if (action == 2) result.WriteU32(5); ApplyAuctionCommand(result.ToArray());
        }
        EmitInterface("auction", "deposit", "VERIFIED", _auctioneerGuid,
            $"rate=5;sellPrice=10000;count=1;duration=1440;deposit={AuctionFrameUiLaw.Deposit(5, 10000, 1, 1440)}");
        var bidder = new PacketWriter(); bidder.WriteU32(1); bidder.WriteU32(700); bidder.WriteU64(0x111);
        bidder.WriteU32(0); bidder.WriteU32(0); bidder.WriteU32(159); bidder.WriteI32(0);
        ApplyAuctionNotification(Op.SMSG_AUCTION_BIDDER_NOTIFICATION, bidder.ToArray());
        var owner = new PacketWriter(); owner.WriteU32(701); owner.WriteU32(150); owner.WriteU32(0);
        owner.WriteU64(0); owner.WriteU32(117); owner.WriteI32(0);
        ApplyAuctionNotification(Op.SMSG_AUCTION_OWNER_NOTIFICATION, owner.ToArray());
        var removed = new PacketWriter(); removed.WriteU32(700); removed.WriteU32(159); removed.WriteI32(0);
        ApplyAuctionNotification(Op.SMSG_AUCTION_REMOVED_NOTIFICATION, removed.ToArray());
    }

    private static void WriteAuctionRow(PacketWriter w, uint id, uint item, uint count, ulong owner,
        uint start, uint increment, uint buyout, uint left, ulong bidder, uint bid)
    {
        w.WriteU32(id); w.WriteU32(item); w.WriteU32(0); w.WriteI32(0); w.WriteU32(0); w.WriteU32(count);
        w.WriteU32(0); w.WriteU64(owner); w.WriteU32(start); w.WriteU32(increment);
        w.WriteU32(buyout); w.WriteU32(left); w.WriteU64(bidder); w.WriteU32(bid);
    }

    // ── Sorting ──────────────────────────────────────────────────────────────────────────────

    private List<AuctionListEntry> SortedAuctionRows(AuctionList list, int tab)
    {
        if (string.IsNullOrEmpty(list.SortKey)) return list.Rows;
        ulong self = ControlledGuid;
        string NameOf(AuctionListEntry row) =>
            _items?.TryGet(row.Item, out ItemTemplate? t) == true && t is not null ? t.Name : "";
        uint LevelOf(AuctionListEntry row) =>
            _items?.TryGet(row.Item, out ItemTemplate? t) == true && t is not null ? t.RequiredLevel : 0;
        IEnumerable<AuctionListEntry> ordered = list.SortKey switch
        {
            "name" => list.Rows.OrderBy(NameOf, StringComparer.OrdinalIgnoreCase),
            "level" => list.Rows.OrderBy(LevelOf),
            "duration" => list.Rows.OrderBy(row => AuctionFrameUiLaw.TimeLeftBucket(row.TimeLeftMs)),
            "seller" => list.Rows.OrderBy(row => _playerNames.GetValueOrDefault(row.Owner, ""), StringComparer.OrdinalIgnoreCase),
            "bidder" => list.Rows.OrderBy(row => _playerNames.GetValueOrDefault(row.Bidder, ""), StringComparer.OrdinalIgnoreCase),
            "buyout" => list.Rows.OrderBy(row => row.Buyout),
            "status" => list.Rows.OrderBy(row => row.Bidder == self ? 0 : 1),
            "bid" => list.Rows.OrderBy(row => row.Bid == 0 ? row.StartBid : row.Bid),
            _ => list.Rows,
        };
        List<AuctionListEntry> result = ordered.ToList();
        if (list.SortReversed) result.Reverse();
        return result;
    }

    // ── Draw ─────────────────────────────────────────────────────────────────────────────────

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
        // SetPortraitTexture(AuctionPortraitTexture, "npc"): the auctioneer's face on the
        // BACKGROUND layer, so the shell's ring art lands over it. It was never drawn.
        if (_entities.TryGet(_auctioneerGuid, out WorldEntity auctioneerUnit))
            DrawUnitPortraitImage(dl, auctioneerUnit, origin + AuctionFrameUiLaw.Portrait.Min * s,
                AuctionFrameUiLaw.Portrait.Width * s, 0, false);
        foreach(AuctionFrameUiLaw.ArtPiece r in AuctionFrameUiLaw.ArtForTab(_auctionTab)){Vector2 m=origin+r.Rect.Min*s,sz=r.Rect.Size*s;DrawArt(dl,r.Path,m,r.Rect.Size,s);if(_uiParityArmed&&_uiParityPanel=="auction")CollectUiParityDraw(r.Element,"Texture",m,sz,"AuctionFrame",new(r.Path,0xffffffff,"IMGUI_IMAGE","TOPLEFT","AuctionFrame","TOPLEFT",r.Rect.X,-r.Rect.Y));}
        dl.PopClipRect();
        DrawVanillaAuction(dl,origin,s);
        DrawImageButton(dl,"##auction-close",origin+AuctionFrameUiLaw.Close.Min*s,
            AuctionFrameUiLaw.Close.Size*s,@"Interface\Buttons\UI-Panel-MinimizeButton-Up",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Down",
            @"Interface\Buttons\UI-Panel-MinimizeButton-Highlight");
        // A client-side close: vanilla sends nothing, and the session state goes with it.
        if(ImGui.IsItemClicked())ResetAuction();
        if(_uiParityArmed&&_uiParityPanel=="auction")MarkUiParityFrameComplete();
        ImGui.End();
        DrawAuctionConfirmPopup();
    }

    private void DrawVanillaAuction(ImDrawListPtr dl, Vector2 origin, float s)
    {
        GameText.DrawCentered(dl, "GameFontNormal", "Auction House",
            origin + AuctionFrameUiLaw.TitleCenter * s, s, VanillaGold);
        if(_auctionTab==0)
        {
            DrawAuctionBrowseFilters(dl,origin,s);
            DrawAuctionCategoryTree(dl,origin,s);
            foreach(AuctionFrameUiLaw.Header header in AuctionFrameUiLaw.BrowseHeaders)
                DrawAuctionSortHeader(dl,origin,s,header);
            DrawAuctionRows(dl,origin,s,0);
            if(VanillaButton(dl,"##auction-browse-close","Close",
                    origin+AuctionFrameUiLaw.BrowseCloseButton.Min*s,
                    AuctionFrameUiLaw.BrowseCloseButton.Size,s))
                ResetAuction();
            if(_auctionRarityOpen)DrawAuctionRarityMenu(dl,origin,s);
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
        { _auctionTab=0; _auctionConfirmKind=0; }
        if(VanillaTab(dl,"##auction-bids-tab",
                origin+AuctionFrameUiLaw.TabMin(1,browseWidth,bidsWidth)*s,
                "Bids",bidsWidth,s,_auctionTab==1))
        { _auctionTab=1; _auctionConfirmKind=0; _auctionRarityOpen=false; RequestBidderAuctions(0); }
        if(VanillaTab(dl,"##auction-owner-tab",
                origin+AuctionFrameUiLaw.TabMin(2,browseWidth,bidsWidth)*s,
                "Auctions",auctionsWidth,s,_auctionTab==2))
        { _auctionTab=2; _auctionConfirmKind=0; _auctionRarityOpen=false; RequestOwnerAuctions(0); }
    }

    private void DrawAuctionBrowseFilters(ImDrawListPtr dl, Vector2 origin, float s)
    {
        GameText.Draw(dl, "GameFontHighlightSmall", "Name", origin + AuctionFrameUiLaw.NameLabel * s, s);
        GameText.DrawCentered(dl, "GameFontHighlightSmall", "Filters",
            origin + AuctionFrameUiLaw.FilterTitleCenter * s, s);
        VanillaInputText(dl,"##auction-search",_auctionSearch,
            origin+AuctionFrameUiLaw.Search.Min*s,AuctionFrameUiLaw.Search.Size,s);
        bool enterSearch = ImGui.IsItemDeactivated() && ImGui.IsKeyPressed(ImGuiKey.Enter);
        GameText.Draw(dl, "GameFontHighlightSmall", "Level Range",
            origin + AuctionFrameUiLaw.LevelRangeLabel * s, s);
        VanillaInputText(dl,"##auction-min-level",_auctionMinLevel,
            origin+AuctionFrameUiLaw.MinLevel.Min*s,AuctionFrameUiLaw.MinLevel.Size,s);
        GameText.Draw(dl, "GameFontHighlightSmall", "-", origin + AuctionFrameUiLaw.LevelHyphen * s, s);
        VanillaInputText(dl,"##auction-max-level",_auctionMaxLevel,
            origin+AuctionFrameUiLaw.MaxLevel.Min*s,AuctionFrameUiLaw.MaxLevel.Size,s);
        GameText.Draw(dl, "GameFontHighlightSmall", "Rarity", origin + AuctionFrameUiLaw.RarityLabel * s, s);
        string rarity = AuctionFrameUiLaw.RarityRows[Math.Clamp(_auctionRarityIndex, 0, AuctionFrameUiLaw.RarityRows.Length - 1)];
        if (VanillaDropdownCapsule(dl, "##auction-rarity", origin, s, AuctionFrameUiLaw.RarityDropDown, rarity))
            _auctionRarityOpen = !_auctionRarityOpen;
        GameText.Draw(dl, "GameFontHighlightSmall", "Usable Items", origin + AuctionFrameUiLaw.UsableLabel * s, s);
        VanillaCheckButton(dl, "##auction-usable", origin + AuctionFrameUiLaw.UsableCheck * s, "", s, ref _auctionUsableOnly);
        bool canSearch = !AuctionBrowseGated;
        if(VanillaButton(dl,"##auction-search-button","Search",
                origin+AuctionFrameUiLaw.SearchButton.Min*s,
                AuctionFrameUiLaw.SearchButton.Size,s,canSearch) || enterSearch && canSearch)
            BrowseAuctions(0);
    }

    private void DrawAuctionRarityMenu(ImDrawListPtr draw, Vector2 origin, float scale)
    {
        string[] rows = AuctionFrameUiLaw.RarityRows;
        DropdownCapsuleUiLaw.LogicalRect list = DropdownCapsuleUiLaw.List(AuctionFrameUiLaw.RarityDropDown, rows.Length);
        Vector2 listMin = origin + list.Min * scale;
        Vector2 listMax = listMin + list.Size * scale;
        _skin?.DrawBackdrop(draw, listMin, listMax, WowSkin.Dialog);
        for (int i = 0; i < rows.Length; i++)
        {
            DropdownCapsuleUiLaw.LogicalRect logicalRow = DropdownCapsuleUiLaw.Row(AuctionFrameUiLaw.RarityDropDown, i);
            Vector2 min = origin + logicalRow.Min * scale;
            ImGui.SetCursorScreenPos(min);
            bool clicked = ImGui.InvisibleButton($"##auction-rarity-{i}", logicalRow.Size * scale);
            bool selected = _auctionRarityIndex == i;
            if (selected || ImGui.IsItemHovered())
            {
                uint highlight = _gameplayArt?.AdditiveHandle(DropdownCapsuleUiLaw.RowHighlight) ?? 0;
                if (highlight != 0) draw.AddImage((nint)highlight, min, min + logicalRow.Size * scale);
            }
            if (selected)
            {
                uint check = _gameplayArt?.Handle(DropdownCapsuleUiLaw.RowCheck) ?? 0;
                if (check != 0)
                {
                    Vector2 checkMin = min + DropdownCapsuleUiLaw.Check.Min * scale;
                    draw.AddImage((nint)check, checkMin, checkMin + DropdownCapsuleUiLaw.Check.Size * scale);
                }
            }
            uint color = i == 0 ? 0xffffffff : ImGui.ColorConvertFloat4ToU32(ItemQualityLaw.Color((uint)(i - 1)));
            GameText.Draw(draw, DropdownCapsuleUiLaw.SelectionFont, rows[i],
                min + DropdownCapsuleUiLaw.RowTextOffset * scale, scale, color);
            if (clicked)
            {
                _auctionRarityIndex = i;
                _auctionRarityOpen = false;
                PlayUiSound(DropdownCapsuleUiLaw.RowSound);
            }
        }
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !ImGui.IsMouseHoveringRect(listMin, listMax))
            _auctionRarityOpen = false;
    }

    /// <summary>
    /// The Browse filter tree from the player's own DBCs: the ten auctionable classes in the
    /// reference's order, the selected class expanded to its (non-hidden) subclasses, and an
    /// Armor subclass expanded to the fourteen equip slots. A click re-searches page 0.
    /// </summary>
    private void DrawAuctionCategoryTree(ImDrawListPtr dl, Vector2 origin, float s)
    {
        EnsureAuctionCatalogs();
        var rows = new List<(string Label, int Depth, uint Class, uint Sub, uint Inv, bool Selected)>();
        bool allSelected = _auctionClass == AuctionBrowseQuery.Any;
        rows.Add(("All", 0, AuctionBrowseQuery.Any, AuctionBrowseQuery.Any, AuctionBrowseQuery.Any, allSelected));
        foreach (uint itemClass in AuctionFrameUiLaw.AuctionClasses)
        {
            string name = _itemClasses?.Name(itemClass) ?? "";
            if (string.IsNullOrWhiteSpace(name)) name = $"Class {itemClass}";
            bool classSelected = _auctionClass == itemClass;
            rows.Add((name, 0, itemClass, AuctionBrowseQuery.Any, AuctionBrowseQuery.Any,
                classSelected && _auctionSubclass == AuctionBrowseQuery.Any));
            if (!classSelected || _itemSubClasses is null) continue;
            foreach (uint sub in _itemSubClasses.SubclassesOf(itemClass))
            {
                if (_itemSubClasses.HiddenFromAuctions(itemClass, sub)) continue;
                string subName = _itemSubClasses.Name(itemClass, sub);
                if (string.IsNullOrWhiteSpace(subName)) continue;
                bool subSelected = _auctionSubclass == sub;
                rows.Add((subName, 1, itemClass, sub, AuctionBrowseQuery.Any,
                    subSelected && _auctionInventoryType == AuctionBrowseQuery.Any));
                if (!subSelected || itemClass != 4) continue;
                foreach ((string invName, uint invType) in AuctionFrameUiLaw.ArmorInventoryTypes)
                    rows.Add((invName, 2, itemClass, sub, invType, _auctionInventoryType == invType));
            }
        }
        int visible = AuctionFrameUiLaw.FilterVisibleRows;
        int maxScroll = Math.Max(0, rows.Count - visible);
        Vector2 treeMin = origin + AuctionFrameUiLaw.CategoryRow(0).Min * s;
        Vector2 treeMax = origin + AuctionFrameUiLaw.CategoryRow(visible - 1).Max * s;
        if (ImGui.IsMouseHoveringRect(treeMin, treeMax) && ImGui.GetIO().MouseWheel != 0)
            _auctionFilterScroll = Math.Clamp(_auctionFilterScroll - Math.Sign(ImGui.GetIO().MouseWheel), 0, maxScroll);
        _auctionFilterScroll = Math.Clamp(_auctionFilterScroll, 0, maxScroll);
        for (int i = 0; i < visible && i + _auctionFilterScroll < rows.Count; i++)
        {
            (string label, int depth, uint itemClass, uint sub, uint inv, bool selected) = rows[i + _auctionFilterScroll];
            if (DrawAuctionFilterRow(dl, origin, s, i, label, depth, selected))
            {
                _auctionClass = itemClass; _auctionSubclass = sub; _auctionInventoryType = inv;
                BrowseAuctions(0);
            }
        }
    }

    private bool DrawAuctionFilterRow(ImDrawListPtr dl, Vector2 origin, float s,
        int index, string label, int depth, bool selected)
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
        float indent = 4 + depth * AuctionFrameUiLaw.FilterIndent;
        Vector2 textMin=min+new Vector2(indent,
            Math.Max(0,(row.Height-GameText.EmPixels("GameFontNormalSmall",s)/s)*.5f))*s;
        GameText.Draw(dl,highlighted?"GameFontHighlightSmall":"GameFontNormalSmall",
            label,textMin,s);
        return clicked;
    }

    private void DrawAuctionSortHeader(ImDrawListPtr dl, Vector2 origin, float s,
        AuctionFrameUiLaw.Header header)
    {
        uint texture=_gameplayArt?.Handle(AuctionFrameUiLaw.SortTabPath)??0;
        Vector2 headerMin = origin + header.Rect.Min * s;
        if(texture!=0)
            foreach(AuctionFrameUiLaw.TextureSlice slice in
                    AuctionFrameUiLaw.SortHeaderSlices(header.Rect.Width))
            {
                Vector2 min=origin+(header.Rect.Min+slice.Rect.Min)*s;
                dl.AddImage((nint)texture,min,min+slice.Rect.Size*s,
                    slice.UvMin,slice.UvMax);
            }
        // A header click sorts the rows we hold and sends nothing (paging stays the server's).
        ImGui.SetCursorScreenPos(headerMin);
        bool clicked = ImGui.InvisibleButton($"##auction-sort-{_auctionTab}-{header.Text}", header.Rect.Size * s);
        if (ImGui.IsItemHovered())
        {
            uint highlight = _gameplayArt?.AdditiveHandle(AuctionFrameUiLaw.RowHighlightPath) ?? 0;
            if (highlight != 0)
                dl.AddImage((nint)highlight, headerMin, headerMin + header.Rect.Size * s,
                    Vector2.Zero, new Vector2(1, .578125f));
        }
        GameText.Draw(dl,"GameFontHighlightSmall",header.Text,
            origin+AuctionFrameUiLaw.HeaderTextMin(header)*s,s);
        if (!clicked) return;
        int index = Array.FindIndex(_auctionTab switch
        {
            1 => AuctionFrameUiLaw.BidHeaders,
            2 => AuctionFrameUiLaw.OwnerHeaders,
            _ => AuctionFrameUiLaw.BrowseHeaders,
        }, h => h.Text == header.Text);
        string key = AuctionFrameUiLaw.SortKey(_auctionTab, index);
        AuctionList list = _auctionLists[_auctionTab];
        if (list.SortKey == key) list.SortReversed = !list.SortReversed;
        else { list.SortKey = key; list.SortReversed = false; }
        PlayUiSound("igMainMenuOptionCheckBoxOn");
    }

    private void DrawAuctionRows(ImDrawListPtr dl, Vector2 origin, float s, int tab)
    {
        AuctionList list = _auctionLists[tab];
        List<AuctionListEntry> rows = SortedAuctionRows(list, tab);
        int visible = AuctionFrameUiLaw.VisibleRows(tab);
        // More than one page: the last visible seat hosts the page turners instead of a row.
        bool paged = list.Total > AuctionBrowseQuery.PageSize || list.Page > 0;
        int rowSeats = paged ? visible - 1 : visible;
        int maxScroll = Math.Max(0, rows.Count - rowSeats);
        AuctionFrameUiLaw.LogicalRect Seat(int i) => tab switch
        {
            1=>AuctionFrameUiLaw.BidRow(i),
            2=>AuctionFrameUiLaw.OwnerRow(i),
            _=>AuctionFrameUiLaw.AuctionRow(i),
        };
        Vector2 areaMin = origin + Seat(0).Min * s;
        Vector2 areaMax = origin + Seat(visible - 1).Max * s;
        if (ImGui.IsMouseHoveringRect(areaMin, areaMax) && ImGui.GetIO().MouseWheel != 0)
            list.Scroll = Math.Clamp(list.Scroll - Math.Sign(ImGui.GetIO().MouseWheel), 0, maxScroll);
        list.Scroll = Math.Clamp(list.Scroll, 0, maxScroll);
        ulong self = ControlledGuid;
        uint money = AuctionActorMoney();

        for(int i=0;i<rowSeats&&i+list.Scroll<rows.Count;i++)
        {
            AuctionListEntry row=rows[i+list.Scroll];
            ItemTemplate? item=null;
            if(_items?.TryGet(row.Item,out ItemTemplate? found)==true)item=found;
            AuctionFrameUiLaw.LogicalRect seat=Seat(i);
            Vector2 rowMin=origin+seat.Min*s;
            ImGui.SetCursorScreenPos(rowMin);
            bool clicked=ImGui.InvisibleButton($"##auction-row-{tab}-{row.Id}",seat.Size*s);
            bool highlighted=list.SelectedId==row.Id||ImGui.IsItemHovered();

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
                // ItemButtonTemplate law: Count sits BOTTOMRIGHT (-5, 2) of the 32px item button.
                GameText.DrawRightAligned(dl,"NumberFontNormal",row.Count.ToString(),
                    rowMin+new Vector2(27f*s,32f*s-GameText.EmPixels("NumberFontNormal",s)-2f*s),s,0xffffffff);
            if (ImGui.IsMouseHoveringRect(rowMin, rowMin + new Vector2(32) * s) && item is not null)
                ShowAuctionRowTooltip(item, row.Count, row.Id, rowMin, s);

            float textTop=GameText.BoxCenteredTop("GameFontNormal",rowMin.Y,32,s);
            uint nameColor=item is null?0xffffffff:
                ImGui.ColorConvertFloat4ToU32(ItemQualityLaw.Color(item.Quality));
            GameText.Draw(dl,"GameFontNormal",item?.Name??$"Item {row.Item}",
                new Vector2(rowMin.X+(tab==1?41:43)*s,textTop),s,nameColor);

            uint current=row.Bid==0?row.StartBid:row.Bid;
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
                // The bid column reads green when the driven body holds the high bid.
                // Right-aligned INSIDE the "Current Bid / Buyout" column (its right edge is the
                // header's, 779 in frame space = 584 in row space), not past the row's end.
                float moneyRight = AuctionFrameUiLaw.BrowseHeaders[^1].Rect.X + AuctionFrameUiLaw.BrowseHeaders[^1].Rect.Width - seat.X - 8;
                DrawTrainerMoney(dl,current,rowMin+new Vector2(moneyRight,3)*s,
                    s,row.Bidder==self&&row.Bid!=0?0xff20ff20:0xffffffff,true);
                if(row.Buyout>0)
                    DrawTrainerMoney(dl,row.Buyout,rowMin+new Vector2(moneyRight,19)*s,
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
                bool highBidder=row.Bidder==self&&row.Bid!=0;
                string status=row.Bidder==0?"":highBidder?"High Bidder":"Outbid";
                GameText.DrawCentered(dl,"GameFontHighlightSmall",status,
                    rowMin+new Vector2(580,16)*s,s,highBidder?0xff20ff20:0xff2020ff);
                DrawTrainerMoney(dl,current,rowMin+new Vector2(795,10)*s,s,0xffffffff,true);
            }
            else
            {
                GameText.DrawCentered(dl,"GameFontHighlightSmall",
                    AuctionFrameUiLaw.TimeLeftText(row.TimeLeftMs),
                    rowMin+new Vector2(223,16)*s,s);
                GameText.DrawCentered(dl,"GameFontHighlightSmall",
                    row.Bidder==0?"":_playerNames.GetValueOrDefault(row.Bidder,""),
                    rowMin+new Vector2(331,16)*s,s);
                float ownerMoneyRight = AuctionFrameUiLaw.OwnerHeaders[^1].Rect.X + AuctionFrameUiLaw.OwnerHeaders[^1].Rect.Width - seat.X - 8;
                DrawTrainerMoney(dl,current,rowMin+new Vector2(ownerMoneyRight,3)*s,s,0xffffffff,true);
                if(row.Buyout>0)
                {
                    GameText.Draw(dl,"GameFontNormalSmall","Buyout",
                        rowMin+new Vector2(401,20)*s,s,VanillaGold);
                    DrawTrainerMoney(dl,row.Buyout,rowMin+new Vector2(ownerMoneyRight,19)*s,
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
                list.SelectedId=row.Id;
                // Any change of selection invalidates whatever a popup was about.
                _auctionConfirmKind=0;
                if(tab!=2)
                    _auctionBidAmount=(int)Math.Min((uint)int.MaxValue,
                        AuctionFrameUiLaw.MinimumBid(row.StartBid,row.Bid,row.MinIncrement));
            }
        }

        if (paged) DrawAuctionPager(dl, origin, s, tab, list);
        if (tab == 0 && list.Total > 0 && list.Rows.Count > 0)
        {
            uint from = list.Page * AuctionBrowseQuery.PageSize + 1;
            uint to = Math.Min(list.Total, from + (uint)list.Rows.Count - 1);
            GameText.DrawCentered(dl, "GameFontHighlightSmall", $"{from} - {to} of {list.Total}",
                origin + AuctionFrameUiLaw.SearchCountCenter * s, s);
        }

        AuctionListEntry? selected = rows.FirstOrDefault(row => row.Id == list.SelectedId);
        if(tab is 0 or 1)
        {
            bool haveSelection = selected is not null;
            bool ownAuction = selected is not null && selected.Owner == self;
            bool highBidder = selected is not null && selected.Bidder == self && selected.Bid != 0;
            uint minimum = selected is null ? 0 : AuctionFrameUiLaw.MinimumBid(selected.StartBid, selected.Bid, selected.MinIncrement);
            uint offered = (uint)Math.Max((long)minimum, _auctionBidAmount);
            bool canBid = haveSelection && AuctionFrameUiLaw.CanBid(highBidder, ownAuction, money, offered);
            bool canBuyout = haveSelection && AuctionFrameUiLaw.CanBuyout(highBidder, ownAuction, money,
                selected!.Bid, selected.Buyout);
            if (tab == 0)
            {
                GameText.Draw(dl, "GameFontHighlightSmall", "Bid", origin + AuctionFrameUiLaw.BidPriceLabel * s, s);
                DrawAuctionMoneyInput(dl, "auction-browse-bid-price", ref _auctionBidAmount,
                    origin + AuctionFrameUiLaw.BidPrice.Min * s, s);
            }
            if(VanillaButton(dl,$"##auction-{tab}-bid","Bid",
                    origin+AuctionFrameUiLaw.BidButton.Min*s,
                    AuctionFrameUiLaw.BidButton.Size,s,canBid))
                BidAuction(tab,selected!.Id,offered);
            if(VanillaButton(dl,$"##auction-{tab}-buyout","Buyout",
                    origin+AuctionFrameUiLaw.BuyoutButton.Min*s,
                    AuctionFrameUiLaw.BuyoutButton.Size,s,canBuyout))
            { _auctionConfirmKind=1; _auctionConfirmId=selected!.Id; _auctionConfirmMoney=selected.Buyout; }
        }
        else if(VanillaButton(dl,"##auction-owner-cancel","Cancel Auction",
                origin+AuctionFrameUiLaw.OwnerCancelButton.Min*s,
                AuctionFrameUiLaw.OwnerCancelButton.Size,s,selected is not null))
        {
            // A cancelled auction that HAS bids costs the seller the house cut (5% of the bid).
            _auctionConfirmKind=2; _auctionConfirmId=selected!.Id;
            _auctionConfirmMoney=selected.Bidder!=0?(uint)((ulong)selected.Bid*5/100):0;
        }
    }

    private void DrawAuctionPager(ImDrawListPtr dl, Vector2 origin, float s, int tab, AuctionList list)
    {
        AuctionFrameUiLaw.LogicalRect seat = AuctionFrameUiLaw.PagerRow(tab);
        float size = AuctionFrameUiLaw.PageButtonSize;
        Vector2 prevMin = origin + new Vector2(seat.X + 5, seat.Y + (seat.Height - size) * .5f) * s;
        Vector2 nextMin = origin + new Vector2(seat.X + seat.Width - 2 - size, seat.Y + (seat.Height - size) * .5f) * s;
        uint pages = Math.Max(1, (list.Total + AuctionBrowseQuery.PageSize - 1) / AuctionBrowseQuery.PageSize);
        bool canPrev = list.Page > 0 && !(tab == 0 && AuctionBrowseGated);
        bool canNext = list.Page + 1 < pages && !(tab == 0 && AuctionBrowseGated);
        DrawImageButton(dl, $"##auction-prev-{tab}", prevMin, new Vector2(size) * s,
            canPrev ? @"Interface\Buttons\UI-SpellbookIcon-PrevPage-Up" : @"Interface\Buttons\UI-SpellbookIcon-PrevPage-Disabled",
            @"Interface\Buttons\UI-SpellbookIcon-PrevPage-Down", @"Interface\Buttons\UI-Common-MouseHilight");
        bool prev = ImGui.IsItemClicked() && canPrev;
        GameText.Draw(dl, "GameFontNormal", "Prev", prevMin + new Vector2(size + 2, 8) * s, s, VanillaGold);
        DrawImageButton(dl, $"##auction-next-{tab}", nextMin, new Vector2(size) * s,
            canNext ? @"Interface\Buttons\UI-SpellbookIcon-NextPage-Up" : @"Interface\Buttons\UI-SpellbookIcon-NextPage-Disabled",
            @"Interface\Buttons\UI-SpellbookIcon-NextPage-Down", @"Interface\Buttons\UI-Common-MouseHilight");
        bool next = ImGui.IsItemClicked() && canNext;
        GameText.DrawRightAligned(dl, "GameFontNormal", "Next", nextMin + new Vector2(-2, 8) * s, s, VanillaGold);
        GameText.DrawCentered(dl, "GameFontHighlightSmall", $"Page {list.Page + 1} of {pages}",
            origin + new Vector2(seat.X + seat.Width * .5f, seat.Y + seat.Height * .5f) * s, s);
        if (!prev && !next) return;
        uint page = next ? list.Page + 1 : list.Page - 1;
        PlayUiSound("igMainMenuOptionCheckBoxOn");
        if (tab == 0) BrowseAuctions(page);
        else if (tab == 1) RequestBidderAuctions(page);
        else RequestOwnerAuctions(page);
    }

    /// <summary>The row's item hover: the shared item tooltip, seated off the icon's right edge.</summary>
    private void ShowAuctionRowTooltip(ItemTemplate item, uint count, ulong ownerToken, Vector2 iconMin, float s)
    {
        if (_skin is null) return;
        ItemTooltipBodySnapshot body = PrepareItemTooltipBodySnapshot(item, count);
        OfferPreparedItemTooltip(new("item:auction-row", ownerToken), body,
            iconMin + new Vector2(34 * s, 0), nextWindowPivot: new Vector2(0, 1));
    }

    private void DrawAuctionOwnerForm(ImDrawListPtr dl, Vector2 origin, float s)
    {
        GameText.DrawCentered(dl,"GameFontHighlightSmall","Create Auction",
            origin+new Vector2(121,55)*s,s);
        GameText.Draw(dl,"GameFontHighlightSmall","Auction Item",
            origin+AuctionFrameUiLaw.OwnerItemLabel*s,s);

        WorldEntity? sellItem = ResolveAuctionSellItem();
        ItemTemplate? item=null;
        if(sellItem is not null&&_items?.TryGet(sellItem.Entry,out ItemTemplate? found)==true)
            item=found;
        Vector2 slotMin=origin+AuctionFrameUiLaw.OwnerItemSlot.Min*s;
        if(item is not null)
        {
            uint icon=_gameplayArt?.Handle(item.IconPath)??0;
            if(icon!=0)dl.AddImage((nint)icon,slotMin,
                slotMin+AuctionFrameUiLaw.OwnerItemSlot.Size*s);
            uint count = sellItem!.Fields.ItemStackCount;
            if (count > 1)
                // AuctionsItemButton is 37px; Count BOTTOMRIGHT (-5, 2) like every ItemButton.
                GameText.DrawRightAligned(dl, "NumberFontNormal", count.ToString(),
                    slotMin + new Vector2(32f * s,
                        AuctionFrameUiLaw.OwnerItemSlot.Height * s -
                        GameText.EmPixels("NumberFontNormal", s) - 2f * s), s, 0xffffffff);
            GameText.Draw(dl,"GameFontNormal",item.Name,
                origin+AuctionFrameUiLaw.OwnerItemName*s,s,
                ImGui.ColorConvertFloat4ToU32(ItemQualityLaw.Color(item.Quality)));
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
            if (item is not null)
                ShowAuctionRowTooltip(item, sellItem!.Fields.ItemStackCount, sellItem.Guid, slotMin, s);
        }
        if(clicked)
        {
            if(HasCarriedItem&&ResolveCarriedItem() is { } carried) AttachAuctionSellItem(carried);
            else if(_auctionSellGuid!=0) ClearAuctionSellSlot();
        }
        if(ImGui.BeginDragDropTarget())
        {
            ImGui.AcceptDragDropPayload("MSUI_INVENTORY_ITEM");
            if(ImGui.IsMouseReleased(ImGuiMouseButton.Left)&&
                ResolveCarriedItem() is { } carried) AttachAuctionSellItem(carried);
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
        uint startBid = (uint)Math.Max(0, _auctionSellBid);
        uint buyout = (uint)Math.Max(0, _auctionSellBuyout);
        if (buyout > 0 && buyout < startBid)
            GameText.Draw(dl, "GameFontNormalSmall", "Buyout must be at least the starting price",
                origin + new Vector2(28, 334) * s, s, 0xff2020ff);
        GameText.Draw(dl,"GameFontNormal","Deposit:",
            origin+AuctionFrameUiLaw.OwnerDepositLabel*s,s,VanillaGold);
        uint deposit=AuctionSellDeposit(sellItem);
        DrawTrainerMoney(dl,deposit,origin+AuctionFrameUiLaw.OwnerDepositMoney*s,
            s,AuctionActorMoney()>=deposit?0xffffffff:0xff2020ff,false);

        bool ready=AuctionFrameUiLaw.CreateAllowed(sellItem is not null,startBid,buyout)&&
            AuctionActorMoney()>=deposit;
        if(VanillaButton(dl,"##auction-create","Create Auction",
                origin+AuctionFrameUiLaw.OwnerCreateButton.Min*s,
                AuctionFrameUiLaw.OwnerCreateButton.Size,s,ready))
            CreateAuction(startBid,buyout,(uint)_auctionSellDuration);
    }

    /// <summary>
    /// Attach the cursor's item to the sell slot: remember WHERE it sat (the driven body's bag
    /// and slot) plus its guid, seat the reference's suggested opening price
    /// (max(100, floor(1.5 × vendor price × stack))), and put the cursor down.
    /// </summary>
    private void AttachAuctionSellItem(WorldEntity carried)
    {
        _auctionSellContainer = _carriedContainer;
        _auctionSellSlot = _carriedSlot;
        _auctionSellGuid = carried.Guid;
        _auctionSellEntry = carried.Entry;
        ClearCarriedItem();
        if (_items?.TryGet(carried.Entry, out ItemTemplate? template) == true && template is not null)
        {
            ulong suggested = Math.Max(100UL, (ulong)template.SellPrice * Math.Max(1u, carried.Fields.ItemStackCount) * 3 / 2);
            _auctionSellBid = (int)Math.Min(int.MaxValue, suggested);
            _auctionSellBuyout = 0;
        }
        EmitInterface("auction", "sell-slot", "ATTACHED", _auctioneerGuid,
            $"item={carried.Entry};guid=0x{carried.Guid:X16};container={_auctionSellContainer};slot={_auctionSellSlot}");
    }

    /// <summary>A bag right-click while the Auctions tab is up seats that slot's item in the sell slot.</summary>
    private void StageAuctionSellItem(int container, int slot, WorldEntity instance)
    {
        _auctionSellContainer = container;
        _auctionSellSlot = slot;
        _auctionSellGuid = instance.Guid;
        _auctionSellEntry = instance.Entry;
        if (_items?.TryGet(instance.Entry, out ItemTemplate? template) == true && template is not null)
        {
            ulong suggested = Math.Max(100UL, (ulong)template.SellPrice * Math.Max(1u, instance.Fields.ItemStackCount) * 3 / 2);
            _auctionSellBid = (int)Math.Min(int.MaxValue, suggested);
            _auctionSellBuyout = 0;
        }
        EmitInterface("auction", "sell-slot", "ATTACHED", _auctioneerGuid,
            $"item={instance.Entry};guid=0x{instance.Guid:X16};container={container};slot={slot};via=right-click");
    }

    /// <summary>BUYOUT_AUCTION / CANCEL_AUCTION: the reference's StaticPopups, on the dialog skin.</summary>
    private void DrawAuctionConfirmPopup()
    {
        if (_auctionConfirmKind == 0 || !_auctionOpen || _skin is null) return;
        float scale = GameplayUiScale();
        string prompt = _auctionConfirmKind == 1
            ? AuctionGlobalString("BUYOUT_AUCTION_CONFIRMATION", "Buyout auction for:")
            : _auctionConfirmMoney > 0
                ? AuctionGlobalString("CANCEL_AUCTION_CONFIRMATION_MONEY", "Cancelling this auction will cost:")
                : AuctionGlobalString("CANCEL_AUCTION_CONFIRMATION", "Are you sure you want to cancel this auction?");
        string[] lines = WrapTooltipText(prompt, "GameFontHighlight", scale,
            BankPurchaseConfirmUiLaw.TextWidth * scale).ToArray();
        float linePitch = GameText.LinePitch("GameFontHighlight", 1f);
        bool showMoney = _auctionConfirmKind == 1 || _auctionConfirmMoney > 0;
        float textHeight = (lines.Length + (showMoney ? 1 : 0)) * linePitch;
        BankPurchaseConfirmUiLaw.ScreenRect frame = BankPurchaseConfirmUiLaw.PopupRect(
            ImGui.GetIO().DisplaySize, scale, textHeight);

        ImGui.SetNextWindowPos(frame.Min, ImGuiCond.Always);
        ImGui.SetNextWindowSize(frame.Size, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoNav;
        bool begun = ImGui.Begin("##auction-confirm", flags);
        ImGui.PopStyleVar(2);
        if (!begun) { ImGui.End(); return; }

        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        _skin.DrawBackdrop(draw, frame.Min, frame.Min + frame.Size, WowSkin.Dialog);
        for (int i = 0; i < lines.Length; i++)
            GameText.DrawCentered(draw, "GameFontHighlight", lines[i],
                frame.Min + BankPurchaseConfirmUiLaw.TextCenter((i + .5f) * linePitch) * scale, scale);
        if (showMoney)
        {
            Vector2 center = frame.Min + BankPurchaseConfirmUiLaw.TextCenter((lines.Length + .5f) * linePitch) * scale;
            DrawTrainerMoney(draw, _auctionConfirmMoney, center + new Vector2(40, -7) * scale, scale, 0xffffffff, true);
        }
        bool accept = DrawAuctionConfirmButton(draw, 1, "Accept",
            frame.Min + BankPurchaseConfirmUiLaw.ButtonMin(1, textHeight) * scale, scale);
        bool cancel = DrawAuctionConfirmButton(draw, 2, "Cancel",
            frame.Min + BankPurchaseConfirmUiLaw.ButtonMin(2, textHeight) * scale, scale);
        ImGui.End();

        if (accept)
        {
            int kind = _auctionConfirmKind; uint id = _auctionConfirmId;
            _auctionConfirmKind = 0;
            if (kind == 1) BidAuction(_auctionTab is 0 or 1 ? _auctionTab : 0, id, _auctionConfirmMoney);
            else CancelAuction(id);
        }
        else if (cancel || ImGui.IsKeyPressed(ImGuiKey.Escape))
            _auctionConfirmKind = 0;
    }

    private bool DrawAuctionConfirmButton(ImDrawListPtr draw, int buttonIndex, string caption,
        Vector2 min, float scale)
    {
        Vector2 size = BankPurchaseConfirmUiLaw.ButtonSize * scale;
        ImGui.SetCursorScreenPos(min);
        bool clicked = ImGui.InvisibleButton($"##auction-confirm-{buttonIndex}", size);
        bool pressed = ImGui.IsItemActive();
        bool hovered = ImGui.IsItemHovered();
        uint art = _skin!.TextureHandle(pressed ? "dialog.button.down" : "dialog.button.up");
        if (art != 0)
            draw.AddImage((nint)art, min, min + size, Vector2.Zero, BankPurchaseConfirmUiLaw.ButtonUvMax);
        if (hovered)
        {
            uint highlight = _gameplayArt?.BrightHighlightHandle(
                @"Interface\Buttons\UI-DialogBox-Button-Highlight") ?? 0;
            if (highlight != 0)
                draw.AddImage((nint)highlight, min, min + size, Vector2.Zero, BankPurchaseConfirmUiLaw.ButtonUvMax);
        }
        GameText.DrawCentered(draw, hovered ? "GameFontHighlight" : "GameFontNormal", caption,
            min + size * .5f, scale);
        return clicked;
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

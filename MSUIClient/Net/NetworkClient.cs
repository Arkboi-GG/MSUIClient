using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Threading;

namespace MSUIClient.Net;

// Top-level networking orchestrator: realmd logon -> world connect -> char enum
// -> PARK AT CHARACTER SELECT -> (app picks) -> player login -> in-world, on a
// background thread. Runs the ~30s keepalive ping and hands the game loop a
// thread-safe queue of inbound packets + a one-shot "entered world" signal.
//
// The character-select park is faithful to benilla (events.rs CharacterList: "the
// world socket is authenticated and parked at character select ... waits for the
// app's pick before CMSG_PLAYER_LOGIN moves the session into the world"). We do
// NOT auto-log-in a character; the app shows the roster and calls SelectCharacter.

public enum NetState
{
    Idle,
    ConnectingRealm,
    Authenticating,
    ConnectingWorld,
    CharacterSelect,   // parked: roster is up, waiting for the app's pick
    EnteringWorld,
    InWorld,
    Failed,
    Disconnected,
}

/// <summary>The server-authoritative pose to snap to on entering (or changing) world.</summary>
public readonly record struct EnterWorldInfo(uint Map, Vector3 Position, float Orientation);

/// <summary>Connection settings, built from ClientConfig by the app.</summary>
public sealed record NetSettings(
    string RealmdHost,
    int RealmdPort,
    int WorldPortFallback,
    string Account,
    string Password,
    string? RealmName = null,
    string? CharacterName = null,
    int TimeoutMs = 10000,
    bool WorldUsesRealmdHost = true);

public sealed class NetworkClient : IDisposable
{
    private const int MaxInboundPackets = 4096;
    private readonly NetSettings _cfg;
    private readonly WirePacketObserver? _wireObserver;
    private readonly SocketWriteObserver? _socketWriteObserver;
    private readonly ConcurrentQueue<(ushort Opcode, byte[] Body, long ReceivedStamp)> _inbound = new();

    private Thread? _worker;
    private Timer? _pingTimer;
    private WorldSession? _session;
    private volatile bool _running;
    private uint _pingSeq;
    private readonly ConcurrentDictionary<uint, uint> _pingSentAt = new();
    private int _latencyMs;

    // Game-loop-visible state (all reads are cheap snapshots).
    public volatile NetState State = NetState.Idle;
    private volatile string _status = "offline";
    public string Status => _status;
    public int LatencyMs => Volatile.Read(ref _latencyMs);

    public ulong PlayerGuid { get; private set; }
    public string PlayerName { get; private set; } = "";
    public Character? Player { get; private set; }

    /// <summary>The human realm NAME from the realmlist (e.g. "Barrens Local (PVP)"), set once a realm is
    /// picked at logon. Empty before the first connection. The glue chrome shows this instead of host:port.</summary>
    public string RealmName { get; private set; } = "";

    /// <summary>The account roster, published when we reach CharacterSelect. Read by the select screen.</summary>
    public IReadOnlyList<Character> Characters { get; private set; } = Array.Empty<Character>();

    // Character-select park: the worker waits on _pick until the app calls SelectCharacter.
    private readonly ManualResetEventSlim _pick = new(false);
    private ulong _pickGuid;

    // Park action: the app either enters the world with a pick, or fires a character CREATE while
    // still parked at select (benilla carries both over the same pick channel).
    private enum ParkReq { None, Enter, Create, Delete }
    private volatile ParkReq _parkReq = ParkReq.None;
    private readonly object _createLock = new();
    private CharCreateParams _pendingCreate;
    private int _createResult = -1;   // -1 = none pending; else the SMSG_CHAR_CREATE result byte
    private ulong _pendingDelete;
    private int _deleteResult = -1;   // -1 = none pending; else the SMSG_CHAR_DELETE result byte

    private string _account;
    private string _password;

    public NetworkClient(NetSettings cfg, WirePacketObserver? wireObserver = null,
        SocketWriteObserver? socketWriteObserver = null)
    {
        _cfg = cfg;
        _wireObserver = wireObserver;
        _socketWriteObserver = socketWriteObserver;
        _account = cfg.Account;
        _password = cfg.Password;
    }

    public bool IsInWorld => State == NetState.InWorld;
    public bool AtCharacterSelect => State == NetState.CharacterSelect;

    /// <summary>Set credentials (from the login screen) and start connecting.</summary>
    public void Login(string account, string password)
    {
        _account = account;
        _password = password;
        Start();
    }

    /// <summary>Pick a character at the select screen — unblocks the worker to send CMSG_PLAYER_LOGIN.</summary>
    public void SelectCharacter(ulong guid)
    {
        _pickGuid = guid;
        _parkReq = ParkReq.Enter;
        _pick.Set();
    }

    /// <summary>Create a character while parked at select (benilla CharRequest::Create over the pick
    /// channel). The worker sends CMSG_CHAR_CREATE, waits for the result, and on success re-enums the
    /// roster; the app polls the result via <see cref="TryTakeCreateResult"/>.</summary>
    public void CreateCharacter(in CharCreateParams p)
    {
        lock (_createLock) { _pendingCreate = p; _createResult = -1; }
        _parkReq = ParkReq.Create;
        _pick.Set();
    }

    /// <summary>Delete a character while parked at select. Same channel as the create: the worker
    /// sends CMSG_CHAR_DELETE, waits for the result and re-enumerates the roster on success, so the
    /// row disappears without a reconnect. The app polls via <see cref="TryTakeDeleteResult"/>.</summary>
    public void DeleteCharacter(ulong guid)
    {
        lock (_createLock) { _pendingDelete = guid; _deleteResult = -1; }
        _parkReq = ParkReq.Delete;
        _pick.Set();
    }

    /// <summary>Take the last SMSG_CHAR_DELETE result once (game-thread poll). False if none pending.</summary>
    public bool TryTakeDeleteResult(out byte code)
    {
        lock (_createLock)
        {
            if (_deleteResult < 0) { code = 0; return false; }
            code = (byte)_deleteResult;
            _deleteResult = -1;
            return true;
        }
    }

    /// <summary>Take the last SMSG_CHAR_CREATE result once (game-thread poll). False if none pending.</summary>
    public bool TryTakeCreateResult(out byte code)
    {
        lock (_createLock)
        {
            if (_createResult < 0) { code = 0; return false; }
            code = (byte)_createResult;
            _createResult = -1;
            return true;
        }
    }

    /// <summary>
    /// Start a connection attempt. Safe to call again after a failed one.
    ///
    /// THE RETRY BUG (fixed 2026-07-29). This used to be `if (_running) return;`. A bad password makes
    /// RealmClient.Logon throw AuthRejectException(0x04); the worker catches it, calls Fail() and
    /// EXITS - but nothing ever cleared `_running`, so every later Login() hit that guard and silently
    /// did nothing. The login screen stayed up, the button did nothing, and there was no way to try
    /// again short of restarting the client (Nico: "if I type in the wrong password I don't get to try
    /// again ... the login just stops doing anything"). The flag was tracking "a worker was started",
    /// not "a worker is running", so gate on the THREAD instead - and wipe the previous attempt's
    /// state so a retry starts clean rather than on a half-open session.
    /// </summary>
    public void Start()
    {
        if (_worker is { IsAlive: true }) return;

        if (_worker is not null)
        {
            try { _worker.Join(250); } catch { }
            _worker = null;
        }
        ResetForNewAttempt();

        _running = true;
        _worker = new Thread(Run) { IsBackground = true, Name = "MSUI-Net" };
        _worker.Start();
    }

    /// <summary>Drop everything the previous attempt left behind, so a retry cannot inherit a stale
    /// roster, a half-open socket, a queued packet or a pending character pick.</summary>
    private void ResetForNewAttempt()
    {
        try { _pingTimer?.Dispose(); } catch { }
        _pingTimer = null;
        try { _session?.Dispose(); } catch { }
        _session = null;
        _pingSentAt.Clear();
        Volatile.Write(ref _latencyMs, 0);

        while (_inbound.TryDequeue(out _)) { }
        Characters = Array.Empty<Character>();
        Player = null;
        PlayerGuid = 0;
        PlayerName = "";
        RealmName = "";
        _pickGuid = 0;
        _parkReq = ParkReq.None;
        _pick.Reset();
        lock (_createLock) { _createResult = -1; _deleteResult = -1; }

        State = NetState.Idle;
        _status = "connecting";
    }

    /// <summary>Drain inbound packets (call once per frame). Returns false when the queue is empty.</summary>
    public bool TryDequeue(out ushort opcode, out byte[] body, out long receivedStamp)
    {
        if (_inbound.TryDequeue(out var p))
        {
            opcode = p.Opcode; body = p.Body; receivedStamp = p.ReceivedStamp; return true;
        }
        opcode = 0; body = Array.Empty<byte>(); receivedStamp = 0; return false;
    }

    // --- outbound pass-throughs (safe no-ops when not in world) ---------------------------------

    public void SendMovement(Op moveOp, MovementInfo info)
    {
        if (State == NetState.InWorld) { try { _session?.SendMovement(moveOp, info); } catch { /* dropped on disconnect */ } }
    }

    public void MoveRootAck(ulong guid, uint counter, bool rooted, MovementInfo info)
    {
        if (State == NetState.InWorld)
        {
            try { _session?.MoveRootAck(guid, counter, rooted, info); }
            catch { /* dropped on disconnect */ }
        }
    }

    public void CompleteCinematic() { try { _session?.CompleteCinematic(); } catch { } }
    public bool LogoutRequest() => InWorld(s => s.LogoutRequest());
    public bool LogoutCancel() => InWorld(s => s.LogoutCancel());

    /// <summary>Game-thread acknowledgement after SMSG_NEW_WORLD adoption.</summary>
    public bool WorldportAck() => InWorld(s => s.WorldportAck());

    public void TeleportAck(ulong guid, uint counter)
    {
        if (State != NetState.InWorld || _session is null) return;
        try { _session.TeleportAck(guid, counter); } catch { }
    }

    public void SetSelection(ulong guid) { try { _session?.SetSelection(guid); } catch { } }
    public bool SuiControlRequest(ulong guid) => InWorld(s => s.SuiControlRequest(guid));
    public bool SuiControlRelease(byte mode) => InWorld(s => s.SuiControlRelease(mode));
    public bool SuiOrder(byte orderType, IReadOnlyList<ulong> subjects, ulong targetGuid, float x, float y, float z) =>
        InWorld(s => s.SuiOrder(orderType, subjects, targetGuid, x, y, z));
    public bool SuiCam(float x, float y, float z, bool active = true) =>
        InWorld(s => s.SuiCam(x, y, z, active));
    public bool SuiZoneIntel() => InWorld(s => s.SuiZoneIntel());
    public bool SuiRtsState() => InWorld(s => s.SuiRtsState());
    public bool SuiRtsAction(byte action, ulong subjectGuid) =>
        InWorld(s => s.SuiRtsAction(action, subjectGuid));
    public bool SuiForceRoster(uint requestId, uint zoneId, uint afterGuidLow,
        byte limit = RtsWire.MaximumForcePageSize) =>
        InWorld(s => s.SuiForceRoster(requestId, zoneId, afterGuidLow, limit));
    public bool SuiPortalPrepare(uint requestId, ulong portalGuid, ushort requestFlags = 0) =>
        InWorld(s => s.SuiPortalPrepare(requestId, portalGuid, requestFlags));
    public bool SuiPortalPrepare(PortalPreparePacket packet) =>
        InWorld(s => s.SuiPortalPrepare(packet));
    public bool SuiPortalReady(PortalLoadResult loadResult, ulong portalGuid,
        uint spawnGeneration, uint descriptorRevision, ulong ticket) =>
        InWorld(s => s.SuiPortalReady(
            loadResult, portalGuid, spawnGeneration, descriptorRevision, ticket));
    public bool SuiPortalReady(PortalReadyPacket packet) =>
        InWorld(s => s.SuiPortalReady(packet));
    public void SetActiveMover(ulong guid) { try { _session?.SetActiveMover(guid); } catch { } }
    public bool Inspect(ulong guid) => InWorld(s => s.Inspect(guid));
    public bool PetAction(ulong petGuid, uint packedAction, ulong targetGuid) =>
        InWorld(s => s.PetAction(petGuid, packedAction, targetGuid));
    public bool PetStopAttack(ulong petGuid) => InWorld(s => s.PetStopAttack(petGuid));
    public bool PetSetAction(ulong petGuid, IReadOnlyList<(uint Position, uint Packed)> entries) =>
        InWorld(s => s.PetSetAction(petGuid, entries));
    public bool PetCancelAura(ulong petGuid, uint spellId) =>
        InWorld(s => s.PetCancelAura(petGuid, spellId));
    public void ZoneUpdate(uint zoneId) { try { _session?.ZoneUpdate(zoneId); } catch { } }
    public bool TogglePvp() => InWorld(s => s.TogglePvp());
    public bool AreaTrigger(uint triggerId) => InWorld(s => s.AreaTrigger(triggerId));
    public bool WorldTeleport(uint mapId, Vector3 position, float orientation) =>
        InWorld(s => s.WorldTeleport(mapId, position, orientation));
    public Action<Op, ulong>? CombatSendObserved { get; set; }
    public void AttackSwing(ulong guid)
    {
        if (State != NetState.InWorld || _session is null) return;
        try { _session.AttackSwing(guid); CombatSendObserved?.Invoke(Op.CMSG_ATTACKSWING, guid); } catch { }
    }
    public void AttackStop()
    {
        if (State != NetState.InWorld || _session is null) return;
        try { _session.AttackStop(); CombatSendObserved?.Invoke(Op.CMSG_ATTACKSTOP, 0); } catch { }
    }
    public bool SendChatSay(string text)
    {
        if (State != NetState.InWorld || _session is null) return false;
        try { _session.SendChatSay(text, Player?.FactionLanguage ?? 0); return true; } catch { return false; }
    }

    /// <summary>Send chat of any type. <paramref name="type"/> is the wire CHAT_MSG_*
    /// byte; <paramref name="target"/> is the whisper recipient or channel name, else null.</summary>
    public bool SendChat(uint type, string? target, string text)
    {
        if (State != NetState.InWorld || _session is null) return false;
        try { _session.SendChat(type, Player?.FactionLanguage ?? 0, target, text); return true; } catch { return false; }
    }

    /// <summary>Tell a whisper sender that the local player is ignoring them.</summary>
    public bool ChatIgnored(ulong senderGuid) => InWorld(s => s.ChatIgnored(senderGuid));

    public void ForceSpeedChangeAck(ulong guid, MovementSpeedKind kind, uint counter,
        MovementInfo info, float speed)
    {
        if (State == NetState.InWorld)
        {
            try { _session?.ForceSpeedChangeAck(guid, kind, counter, info, speed); }
            catch { /* dropped on disconnect */ }
        }
    }

    public void MoveModeAck(ulong guid, MovementModeKind kind, uint counter, bool apply,
        MovementInfo info)
    {
        if (State == NetState.InWorld)
        {
            try { _session?.MoveModeAck(guid, kind, counter, apply, info); }
            catch { /* dropped on disconnect */ }
        }
    }

    public bool JoinChannel(string name, string password = "") =>
        InWorld(s => s.JoinChannel(name, password));
    public bool LeaveChannel(string name) => InWorld(s => s.LeaveChannel(name));
    public bool ChannelList(string name) => InWorld(s => s.ChannelList(name));

    /// <summary>Send a numbered text emote (/wave, /dance, ...) by its EmoteCommandLaw
    /// id. <paramref name="targetGuid"/> is 0 for an untargeted emote.</summary>
    public bool SendTextEmote(uint textEmoteId, ulong targetGuid)
    {
        if (State != NetState.InWorld || _session is null) return false;
        try { _session.SendTextEmote(textEmoteId, targetGuid); return true; } catch { return false; }
    }
    public bool PlayedTime()
    {
        if (State != NetState.InWorld || _session is null) return false;
        try { _session.PlayedTime(); return true; } catch { return false; }
    }
    public bool QueryTime()
    {
        if (State != NetState.InWorld || _session is null) return false;
        try { _session.QueryTime(); return true; } catch { return false; }
    }
    public bool RandomRoll(uint minimum, uint maximum)
    {
        if (State != NetState.InWorld || _session is null) return false;
        try { _session.RandomRoll(minimum, maximum); return true; } catch { return false; }
    }
    public void SetSheathed(byte state) { try { _session?.SetSheathed(state); } catch { } }
    public bool CastSpell(uint spellId, ulong targetGuid)
    {
        if (State != NetState.InWorld || _session is null) return false;
        try { _session.CastSpell(spellId, targetGuid); return true; } catch { return false; }
    }
    public bool CastSpellAtLocation(uint spellId, System.Numerics.Vector3 dest)
    {
        if (State != NetState.InWorld || _session is null) return false;
        try { _session.CastSpellAtLocation(spellId, dest); return true; } catch { return false; }
    }
    public bool CastSpellOnGameObject(uint spellId, ulong gameObjectGuid)
    {
        if (State != NetState.InWorld || _session is null) return false;
        try { _session.CastSpellOnGameObject(spellId, gameObjectGuid); return true; }
        catch { return false; }
    }
    public bool CastSpellOnItem(uint spellId, ulong itemGuid)
    {
        if (State != NetState.InWorld || _session is null) return false;
        try { _session.CastSpellOnItem(spellId, itemGuid); return true; } catch { return false; }
    }
    public void CancelCast(uint spellId) { try { _session?.CancelCast(spellId); } catch { } }
    public void CancelChannelling(uint spellId) { try { _session?.CancelChannelling(spellId); } catch { } }
    public bool CancelAura(uint spellId)
    {
        if (State != NetState.InWorld || _session is null) return false;
        try { _session.CancelAura(spellId); return true; } catch { return false; }
    }
    public void CancelAutoRepeat() { try { _session?.CancelAutoRepeat(); } catch { } }

    public bool StandStateChange(uint state)
    {
        if (State != NetState.InWorld || _session is null) return false;
        try { _session.StandStateChange(state); return true; } catch { return false; }
    }

    public bool MountSpecial()
    {
        if (State != NetState.InWorld || _session is null) return false;
        try { _session.MountSpecial(); return true; } catch { return false; }
    }

    public void OpenItem(byte bag, byte slot) { try { _session?.OpenItem(bag, slot); } catch { } }
    public void DestroyItem(byte bag, byte slot, byte count)
    { try { _session?.DestroyItem(bag, slot, count); } catch { } }
    public bool UnlearnSkill(uint skillId)
    {
        if (State != NetState.InWorld || _session is null) return false;
        try { _session.UnlearnSkill(skillId); return true; } catch { return false; }
    }
    public void SetActionButton(byte wireSlot, uint packed) { try { _session?.SetActionButton(wireSlot, packed); } catch { } }
    public void CreatureQuery(uint entry, ulong guid) { try { _session?.CreatureQuery(entry, guid); } catch { } }
    public bool PetNameQuery(uint petNumber, ulong guid) =>
        InWorld(s => s.PetNameQuery(petNumber, guid));
    public void GameObjectQuery(uint entry, ulong guid) { try { _session?.GameObjectQuery(entry, guid); } catch { } }
    public void ItemQuery(uint entry, ulong guid) { try { _session?.ItemQuery(entry, guid); } catch { } }
    public bool GossipHello(ulong guid)
    {
        if (State != NetState.InWorld || _session is null) return false;
        try { _session.GossipHello(guid); return true; } catch { return false; }
    }
    public bool GameObjectUse(ulong guid) => InWorld(s => s.GameObjectUse(guid));
    public bool TaxiNodeStatusQuery(ulong guid) => InWorld(s => s.TaxiNodeStatusQuery(guid));
    public bool TaxiQueryAvailableNodes(ulong guid) => InWorld(s => s.TaxiQueryAvailableNodes(guid));
    public bool ActivateTaxi(ulong guid, uint sourceNode, uint destinationNode) =>
        InWorld(s => s.ActivateTaxi(guid, sourceNode, destinationNode));
    public bool ActivateTaxiExpress(ulong guid, uint totalCost, IReadOnlyList<uint> nodes) =>
        InWorld(s => s.ActivateTaxiExpress(guid, totalCost, nodes));
    public bool RepopRequest() => InWorld(s => s.RepopRequest());
    public bool CorpseQuery() => InWorld(s => s.CorpseQuery());
    public bool ReclaimCorpse(ulong guid) => InWorld(s => s.ReclaimCorpse(guid));
    public bool SpiritHealerActivate(ulong guid) => InWorld(s => s.SpiritHealerActivate(guid));
    public bool ResurrectResponse(ulong guid, bool accept) => InWorld(s => s.ResurrectResponse(guid, accept));
    public bool PageTextQuery(uint pageId) => InWorld(s => s.PageTextQuery(pageId));
    public bool GossipSelect(ulong guid, uint listId, string? code = null)
    {
        if (State != NetState.InWorld || _session is null) return false;
        try { _session.GossipSelect(guid, listId, code); return true; } catch { return false; }
    }
    public bool TrainerList(ulong guid)
    {
        if (State != NetState.InWorld || _session is null) return false;
        try { _session.TrainerList(guid); return true; } catch { return false; }
    }
    public bool BinderActivate(ulong guid) => InWorld(s => s.BinderActivate(guid));
    public bool TrainerBuySpell(ulong guid, uint spellId)
    {
        if (State != NetState.InWorld || _session is null) return false;
        try { _session.TrainerBuySpell(guid, spellId); return true; } catch { return false; }
    }
    public bool LearnTalent(uint talentId, uint requestedRank) => InWorld(s => s.LearnTalent(talentId, requestedRank));
    public bool ConfirmTalentWipe(ulong trainerGuid) => InWorld(s => s.ConfirmTalentWipe(trainerGuid));
    public bool BankerActivate(ulong guid) => InWorld(s => s.BankerActivate(guid));
    public bool BuyBankSlot(ulong guid) => InWorld(s => s.BuyBankSlot(guid));
    public bool GetMailList(ulong guid) => InWorld(s => s.GetMailList(guid));
    public bool SendMail(ulong guid, string receiver, string subject, string body, ulong item, uint money, uint cod)
        => InWorld(s => s.SendMail(guid, receiver, subject, body, item, money, cod));
    public bool MailTakeMoney(ulong guid, uint id) => InWorld(s => s.MailTakeMoney(guid, id));
    public bool MailTakeItem(ulong guid, uint id) => InWorld(s => s.MailTakeItem(guid, id));
    public bool MailMarkAsRead(ulong guid, uint id) => InWorld(s => s.MailMarkAsRead(guid, id));
    public bool MailReturn(ulong guid, uint id) => InWorld(s => s.MailReturn(guid, id));
    public bool MailDelete(ulong guid, uint id) => InWorld(s => s.MailDelete(guid, id));
    public bool MailCreateTextItem(ulong guid, uint id) => InWorld(s => s.MailCreateTextItem(guid, id));
    public bool ItemTextQuery(uint textId, uint mailId) => InWorld(s => s.ItemTextQuery(textId, mailId));
    public bool QueryNextMailTime() => InWorld(s => s.QueryNextMailTime());
    public bool AuctionHello(ulong guid) => InWorld(s => s.AuctionHello(guid));
    public bool AuctionBrowse(ulong guid, uint page, string search, uint itemClass = uint.MaxValue) =>
        InWorld(s => s.AuctionBrowse(guid, page, search, itemClass));
    public bool AuctionOwnerList(ulong guid, uint page) => InWorld(s => s.AuctionOwnerList(guid, page));
    public bool AuctionBidderList(ulong guid, uint page) => InWorld(s => s.AuctionBidderList(guid, page));
    public bool AuctionBid(ulong guid, uint id, uint price) => InWorld(s => s.AuctionBid(guid, id, price));
    public bool AuctionCancel(ulong guid, uint id) => InWorld(s => s.AuctionCancel(guid, id));
    public bool AuctionSell(ulong guid, ulong item, uint bid, uint buyout, uint duration) => InWorld(s => s.AuctionSell(guid, item, bid, buyout, duration));
    public bool GuildRoster() => InWorld(s => s.GuildRoster());
    public bool GuildMotd(string text) => InWorld(s => s.GuildMotd(text));
    public bool GuildPromote(string name) => InWorld(s => s.GuildPromote(name));
    public bool GuildDemote(string name) => InWorld(s => s.GuildDemote(name));
    public bool GuildLeave() => InWorld(s => s.GuildLeave());
    public bool GuildDisband() => InWorld(s => s.GuildDisband());
    public bool SaveGuildEmblem(ulong vendorGuid, uint emblemStyle, uint emblemColor,
        uint borderStyle, uint borderColor, uint backgroundColor)
        => InWorld(s => s.SaveGuildEmblem(vendorGuid, emblemStyle, emblemColor,
            borderStyle, borderColor, backgroundColor));
    public bool QuestgiverStatus(ulong guid) => InWorld(s => s.QuestgiverStatus(guid));
    public bool QuestQuery(uint questId) => InWorld(s => s.QuestQuery(questId));
    public bool QuestgiverHello(ulong guid) => InWorld(s => s.QuestgiverHello(guid));
    public bool QuestgiverQuery(ulong guid, uint questId) => InWorld(s => s.QuestgiverQuery(guid, questId));
    public bool QuestgiverAccept(ulong guid, uint questId) => InWorld(s => s.QuestgiverAccept(guid, questId));
    public bool QuestgiverComplete(ulong guid, uint questId) => InWorld(s => s.QuestgiverComplete(guid, questId));
    public bool QuestgiverRequestReward(ulong guid, uint questId) => InWorld(s => s.QuestgiverRequestReward(guid, questId));
    public bool QuestgiverChooseReward(ulong guid, uint questId, uint choice) => InWorld(s => s.QuestgiverChooseReward(guid, questId, choice));
    public bool QuestLogRemove(byte slot) => InWorld(s => s.QuestLogRemove(slot));

    private bool InWorld(Action<WorldSession> send)
    {
        if (State != NetState.InWorld || _session is null) return false;
        try { send(_session); return true; } catch { return false; }
    }
    public bool NpcTextQuery(uint textId, ulong guid)
    {
        if (State != NetState.InWorld || _session is null) return false;
        try { _session.NpcTextQuery(textId, guid); return true; } catch { return false; }
    }
    public bool ListInventory(ulong guid) { if (State!=NetState.InWorld||_session is null) return false; try { _session.ListInventory(guid); return true; } catch { return false; } }
    public bool BuyItem(ulong vendor, uint item, byte count) { if (State!=NetState.InWorld||_session is null) return false; try { _session.BuyItem(vendor,item,count); return true; } catch { return false; } }
    public bool SellItem(ulong vendor, ulong item, byte count) { if (State!=NetState.InWorld||_session is null) return false; try { _session.SellItem(vendor,item,count); return true; } catch { return false; } }
    public bool BuybackItem(ulong vendor, uint slot) { if (State!=NetState.InWorld||_session is null) return false; try { _session.BuybackItem(vendor,slot); return true; } catch { return false; } }
    public bool RepairItem(ulong vendor, ulong item) => InWorld(s => s.RepairItem(vendor, item));
    public void UseItem(byte bag, byte slot, byte spellSlot) { try { _session?.UseItem(bag, slot, spellSlot); } catch { } }
    public bool AutoEquipItem(byte bag, byte slot) => InWorld(s => s.AutoEquipItem(bag, slot));
    public bool SetAmmo(uint entry) => InWorld(s => s.SetAmmo(entry));
    public bool SwapInventoryItems(byte sourceSlot, byte destinationSlot) => InWorld(s => s.SwapInventoryItems(sourceSlot, destinationSlot));
    public bool SwapItems(byte destinationBag, byte destinationSlot, byte sourceBag, byte sourceSlot) =>
        InWorld(s => s.SwapItems(destinationBag, destinationSlot, sourceBag, sourceSlot));
    public bool AutoBankItem(byte sourceBag, byte sourceSlot) =>
        InWorld(s => s.AutoBankItem(sourceBag, sourceSlot));
    public bool AutostoreBankItem(byte sourceBag, byte sourceSlot) =>
        InWorld(s => s.AutostoreBankItem(sourceBag, sourceSlot));
    public bool SplitItem(byte sourceBag, byte sourceSlot, byte destinationBag, byte destinationSlot,
        byte count) => InWorld(s => s.SplitItem(sourceBag, sourceSlot, destinationBag, destinationSlot, count));
    public void NameQuery(ulong guid) { try { _session?.NameQuery(guid); } catch { } }
    public bool FriendList() => InWorld(s => s.FriendList());
    public bool AddFriend(string name) => InWorld(s => s.AddFriend(name));
    public bool DeleteFriend(ulong guid) => InWorld(s => s.DeleteFriend(guid));
    public bool Who(string name) => InWorld(s => s.Who(name));
    public bool Who(SocialPackets.WhoRequest request) => InWorld(s => s.Who(request));
    public bool AddIgnore(string name) => InWorld(s => s.AddIgnore(name));
    public bool DeleteIgnore(ulong guid) => InWorld(s => s.DeleteIgnore(guid));
    public bool GroupInvite(string name) => InWorld(s => s.GroupInvite(name));
    public bool GroupAccept() => InWorld(s => s.GroupAccept());
    public bool GroupDecline() => InWorld(s => s.GroupDecline());
    public bool GroupUninvite(string name) => InWorld(s => s.GroupUninvite(name));
    public bool GroupUninviteGuid(ulong guid) => InWorld(s => s.GroupUninviteGuid(guid));
    public bool GroupSetLeader(ulong guid) => InWorld(s => s.GroupSetLeader(guid));
    public bool GroupLootMethod(uint method, ulong lootMaster, uint threshold) =>
        InWorld(s => s.GroupLootMethod(method, lootMaster, threshold));
    public bool GroupDisband() => InWorld(s => s.GroupDisband());
    public bool RequestPartyMemberStats(ulong guid) => InWorld(s => s.RequestPartyMemberStats(guid));
    public bool GroupChangeSubGroup(string name, byte groupNumber) =>
        InWorld(s => s.GroupChangeSubGroup(name, groupNumber));
    public bool GroupSwapSubGroup(string name, string swapWith) =>
        InWorld(s => s.GroupSwapSubGroup(name, swapWith));
    public bool GroupRaidConvert() => InWorld(s => s.GroupRaidConvert());
    public bool GroupAssistantLeader(ulong guid, bool grant) =>
        InWorld(s => s.GroupAssistantLeader(guid, grant));
    public bool GroupMinimapPing(float x, float y) => InWorld(s => s.GroupMinimapPing(x, y));
    public bool SetRaidTarget(byte icon, ulong guid) => InWorld(s => s.SetRaidTarget(icon, guid));
    public bool RequestRaidTargets() => InWorld(s => s.RequestRaidTargets());
    public bool StartReadyCheck() => InWorld(s => s.StartReadyCheck());
    public bool AnswerReadyCheck(bool ready) => InWorld(s => s.AnswerReadyCheck(ready));
    public bool InitiateTrade(ulong guid) => InWorld(s => s.InitiateTrade(guid));
    public bool BeginTrade() => InWorld(s => s.BeginTrade());
    public bool BusyTrade() => InWorld(s => s.BusyTrade());
    public bool IgnoreTrade() => InWorld(s => s.IgnoreTrade());
    public bool AcceptTrade() => InWorld(s => s.AcceptTrade());
    public bool UnacceptTrade() => InWorld(s => s.UnacceptTrade());
    public bool CancelTrade() => InWorld(s => s.CancelTrade());
    public bool SetTradeItem(byte tradeSlot, byte bag, byte slot) => InWorld(s => s.SetTradeItem(tradeSlot, bag, slot));
    public bool ClearTradeItem(byte tradeSlot) => InWorld(s => s.ClearTradeItem(tradeSlot));
    public bool SetTradeGold(uint gold) => InWorld(s => s.SetTradeGold(gold));
    public bool DuelAccepted(ulong arbiter) => InWorld(s => s.DuelAccepted(arbiter));
    public bool DuelCancelled(ulong arbiter) => InWorld(s => s.DuelCancelled(arbiter));
    public bool GmTicketCreate(byte type, uint map, Vector3 position, string message) =>
        InWorld(s => s.GmTicketCreate(type, map, position, message));
    public bool GmTicketUpdate(byte type, string message) => InWorld(s => s.GmTicketUpdate(type, message));
    public bool GmTicketGet() => InWorld(s => s.GmTicketGet());
    public bool GmTicketDelete() => InWorld(s => s.GmTicketDelete());
    public bool GmTicketSystemStatus() => InWorld(s => s.GmTicketSystemStatus());
    public bool Loot(ulong guid) => InWorld(s => s.Loot(guid));
    public bool LootMoney() => InWorld(s => s.LootMoney());
    public bool LootRelease(ulong guid) => InWorld(s => s.LootRelease(guid));
    public bool AutostoreLootItem(byte lootSlot) => InWorld(s => s.AutostoreLootItem(lootSlot));
    public bool LootRoll(ulong lootedTarget, uint itemSlot, GroupLootVote vote) =>
        InWorld(s => s.LootRoll(lootedTarget, itemSlot, vote));

    // --- worker ---------------------------------------------------------------------------------

    private void Run()
    {
        var timeout = TimeSpan.FromMilliseconds(_cfg.TimeoutMs);
        try
        {
            // 1. realmd logon.
            SetState(NetState.ConnectingRealm, $"connecting to realmd {_cfg.RealmdHost}:{_cfg.RealmdPort}");
            var logon = RealmClient.Logon(_cfg.RealmdHost, _cfg.RealmdPort, _account, _password, timeout);

            if (logon.Realms.Count == 0) { Fail("realm list is empty"); return; }
            RealmInfo realm = PickRealm(logon.Realms);
            RealmName = realm.Name;                 // published for the glue chrome (name, not host:port)
            var (worldHost, worldPort) = HostPort(realm.Address, _cfg.WorldPortFallback);
            // Private servers usually run mangosd on the same box as realmd, and the realmlist DB
            // often advertises an internal / unreachable IP (yours advertised 10.30.37.30). Prefer the
            // realmd host we already reached, keeping the advertised port.
            if (_cfg.WorldUsesRealmdHost && !string.IsNullOrWhiteSpace(_cfg.RealmdHost)
                && !string.Equals(worldHost, _cfg.RealmdHost, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"[net] realm advertises world {worldHost}:{worldPort}; using realmd host {_cfg.RealmdHost} instead");
                worldHost = _cfg.RealmdHost;
            }

            // 2. world connect + auth handshake.
            SetState(NetState.ConnectingWorld, $"connecting to world {worldHost}:{worldPort} ({realm.Name})");
            _session = WorldSession.Connect(worldHost, worldPort, _account, logon.SessionKey,
                timeout, _wireObserver, _socketWriteObserver);

            // 3-5 repeat on the same authenticated world socket. SMSG_LOGOUT_COMPLETE ends one
            // character session, not the account session: refresh the roster, park again, and let
            // the player choose another character without inventing a reconnect.
            bool allowConfiguredFastPath = true;
            while (_running)
            {
                SetState(NetState.Authenticating, "requesting character list");
                var chars = _session.CharEnum();
                Characters = chars;

                ulong wanted = 0;
                // Dev fast path is consumed once. A deliberate logout must show character select,
                // never bounce straight back into the configured character.
                if (allowConfiguredFastPath && !string.IsNullOrWhiteSpace(_cfg.CharacterName))
                    wanted = chars.FirstOrDefault(c =>
                        string.Equals(c.Name, _cfg.CharacterName, StringComparison.OrdinalIgnoreCase))?.Guid ?? 0;
                allowConfiguredFastPath = false;

                if (wanted == 0)
                {
                    // Park at character select. Create/delete are serviced on this worker so their
                    // reply and the refreshed roster stay ordered on the socket.
                    while (true)
                    {
                        SetState(NetState.CharacterSelect,
                            chars.Count == 0 ? "no characters on this account" : $"character select - {chars.Count} character(s)");
                        _pickGuid = 0;
                        _pick.Reset();
                        _pick.Wait();
                        if (!_running) return;

                        ParkReq req = _parkReq;
                        _parkReq = ParkReq.None;
                        if (req == ParkReq.Create)
                        {
                            CharCreateParams create;
                            lock (_createLock) create = _pendingCreate;
                            SetState(NetState.CharacterSelect, $"creating {create.Name}...");
                            byte code = _session.CreateCharacter(create);
                            if (code == 0x2E)
                            {
                                chars = _session.CharEnum();
                                Characters = chars;
                            }
                            lock (_createLock) _createResult = code;
                            continue;
                        }
                        if (req == ParkReq.Delete)
                        {
                            ulong doomed;
                            lock (_createLock) doomed = _pendingDelete;
                            SetState(NetState.CharacterSelect, "deleting character...");
                            byte code = _session.DeleteCharacter(doomed);
                            if (code == 0x39)
                            {
                                chars = _session.CharEnum();
                                Characters = chars;
                            }
                            lock (_createLock) _deleteResult = code;
                            continue;
                        }

                        wanted = _pickGuid;
                        if (req == ParkReq.Enter && wanted != 0) break;
                    }
                }

                Character? pick = chars.FirstOrDefault(c => c.Guid == wanted);
                if (pick is null) { Fail("selected character not found in roster"); return; }
                Player = pick;
                PlayerGuid = pick.Guid;
                PlayerName = pick.Name;

                SetState(NetState.EnteringWorld, $"entering world as {pick.Name} (L{pick.Level})");
                _session.PlayerLogin(pick.Guid);
                _session.SetActiveMover(pick.Guid);

                // LOGIN_VERIFY_WORLD flips us to InWorld. A clean logout returns true and loops
                // back through CharEnum; socket failure still escapes through the outer catch.
                if (!ReadLoop()) return;
                Player = null;
                PlayerGuid = 0;
                PlayerName = "";
                _pickGuid = 0;
                _parkReq = ParkReq.None;
            }
        }
        catch (Exception ex) when (_running)
        {
            Fail(ex.Message);
        }
        catch (Exception)
        {
            // shutting down — swallow
        }
        finally
        {
            // The worker is DONE. Drop the flag here and nowhere else, so Start() can run a fresh
            // attempt after a failure - the missing half of the retry bug documented on Start().
            // Ordering is safe: the `when (_running)` filters above are evaluated before this runs,
            // so a genuine error still reports through Fail() and a Stop() still reads as shutdown.
            _running = false;
        }
    }

    /// <returns>True only for a clean SMSG_LOGOUT_COMPLETE boundary.</returns>
    private bool ReadLoop()
    {
        var session = _session!;
        while (_running)
        {
            (ushort opcode, byte[] body) = session.ReceivePacket();
            bool logoutComplete = false;
            switch ((Op)opcode)
            {
                case Op.SMSG_LOGIN_VERIFY_WORLD:
                    {
                        var r = new PacketReader(body);
                        var info = new EnterWorldInfo(r.ReadU32(), r.ReadVector3(), r.ReadF32());
                        SetState(NetState.InWorld, $"in world: {PlayerName} - map {info.Map} at ({info.Position.X:F0}, {info.Position.Y:F0}, {info.Position.Z:F0})");
                        StartPing();
                        break;
                    }
                case Op.SMSG_NEW_WORLD:
                    {
                        var r = new PacketReader(body);
                        var info = new EnterWorldInfo(r.ReadU32(), r.ReadVector3(), r.ReadF32());
                        _status = $"changing to map {info.Map}";
                        break;
                    }
                case Op.SMSG_PONG:
                    {
                        if (body.Length >= 4)
                        {
                            uint sequence = new PacketReader(body).ReadU32();
                            if (_pingSentAt.TryRemove(sequence, out uint sentAt))
                            {
                                int sample = (int)Math.Min(unchecked(MovementInfo.ClientUptimeMs() - sentAt), 60_000u);
                                int previous = Volatile.Read(ref _latencyMs);
                                Volatile.Write(ref _latencyMs, previous <= 0 ? sample : (previous * 3 + sample) / 4);
                            }
                        }
                        break;
                    }
                case Op.SMSG_LOGOUT_COMPLETE:
                    logoutComplete = true;
                    break;
            }
            _inbound.Enqueue((opcode, body, Stopwatch.GetTimestamp()));

            // An ordered world boundary may be anywhere in this queue. Dropping
            // the oldest packet can discard LOGIN_VERIFY_WORLD/NEW_WORLD while
            // retaining object updates that belong after it, so overflow is a
            // fatal protocol failure rather than a lossy eviction policy.
            if (_inbound.Count > MaxInboundPackets)
            {
                Fail($"inbound packet queue exceeded {MaxInboundPackets} entries");
                return false;
            }
            if (logoutComplete) return true;
        }
        return false;
    }

    private void StartPing()
    {
        _pingTimer ??= new Timer(_ =>
        {
            try
            {
                uint sequence = unchecked(_pingSeq++);
                _pingSentAt[sequence] = MovementInfo.ClientUptimeMs();
                _session?.Ping(sequence, (uint)Math.Max(0, LatencyMs));
            }
            catch { /* disconnect handled by read loop */ }
        }, null, 0, 10_000);
    }

    private void SetState(NetState s, string status)
    {
        State = s;
        _status = status;
        Console.WriteLine($"[net] {s}: {status}");
    }

    private void Fail(string reason)
    {
        State = NetState.Failed;
        _status = $"failed: {reason}";
        Console.WriteLine($"[net] FAILED: {reason}");
    }

    private RealmInfo PickRealm(List<RealmInfo> realms)
    {
        if (!string.IsNullOrWhiteSpace(_cfg.RealmName))
            foreach (var r in realms)
                if (string.Equals(r.Name, _cfg.RealmName, StringComparison.OrdinalIgnoreCase)) return r;
        return realms[0];
    }

    /// <summary>Split "host:port" (realm address). A bare host or non-numeric suffix keeps the default port.</summary>
    public static (string Host, int Port) HostPort(string address, int defaultPort)
    {
        int i = address.LastIndexOf(':');
        if (i > 0 && i < address.Length - 1 && !address.AsSpan(0, i).Contains(':')
            && int.TryParse(address.AsSpan(i + 1), out int port))
            return (address[..i], port);
        return (address, defaultPort);
    }

    public void Stop()
    {
        _running = false;
        _pick.Set();                  // wake a worker parked at character select so it can exit
        try { _pingTimer?.Dispose(); } catch { }
        _pingTimer = null;
        _pingSentAt.Clear();
        try { _session?.Dispose(); } catch { } // unblocks the read loop with an IOException
        _session = null;
        try { _worker?.Join(1000); } catch { }
        _worker = null;
        while (_inbound.TryDequeue(out _)) { }
        Characters = Array.Empty<Character>();
        if (State != NetState.Failed) State = NetState.Disconnected;
    }

    public void Dispose() => Stop();
}

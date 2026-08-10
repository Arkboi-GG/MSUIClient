using System.Numerics;
using System.Diagnostics;
using System.Text;
using ImGuiNET;
using Silk.NET.OpenGL;
using MSUIClient.Engine;
using MSUIClient.Engine.UI;   // WowSkin (glue login chrome)
using MSUIClient.Net;
using MSUIClient.World;
using MSUIClient.World.Units;
using MSUIClient.Formats;   // AreaTableCatalog (roster zone names) + AdtTerrainReader

namespace MSUIClient;

// Phase 2 networking glue for the game loop. Hooks in Program.cs:
//   Load()    -> InitNet(gl);  and BeginWorldLoad(gl) only runs when OFFLINE
//   Update()  -> PumpNet(dt);  then a guard that skips gameplay until the world loads
//   Gui()     -> NetHud();     login screen -> character select -> in-world status
//   Dispose() -> _net?.Dispose();
//
// The glue flow mirrors the real client (and benilla): Login screen -> Character
// Select (roster, pick a character) -> World. Networked mode stays STATELESS until
// SMSG_LOGIN_VERIFY_WORLD assigns a character + spawn point; only then does
// BeginWorldLoad run. Offline mode is unchanged.
public sealed partial class GameLoop
{
    private const int NetDrainPacketBudget = 256;
    private const double NetDrainTimeBudgetMs = 2.0;

    private NetworkClient? _net;
    private readonly WireRing _wire = new();
    private readonly WireLogRecorder _wireLog = new();
    private readonly EntityStore _entities = new();   // game-thread-owned world model (from UPDATE_OBJECT)
    private readonly CreatureLifecycleTracker _creatureLifecycle = new();
    private readonly CombatState _combat = new();
    private readonly LocalMovementSender _movementSender = new();
    private GL? _gl;                                   // kept so we can start the world load after login
    private GlueScene? _glue;                          // the login-screen glue scene (UI_MainMenu)
    private GlueBooth? _booth;                         // the character-select per-race booth (UI_<Race>)
    private CreatureRenderer? _creatures;              // draws streamed creatures and remote players (UPDATE_OBJECT)
    private SelectionRingRenderer? _selectionRing;
    private SpellEffectSource? _spellEffects;
    private SpellEffectMeshRenderer? _spellEffectMeshes;
    private SpellRibbonRenderer? _spellRibbons;
    private World.Spells.SpellParticleSystem? _spellParticles;
    private World.Spells.SpellSoundSystem? _spellSounds;
    private bool _worldLoadStarted;                   // false until BeginWorldLoad has run (offline or on login)
    private int _worldEntryTransitionStage;           // 1=booth avatar, 2=HUD prime, 3=begin load, 4=adopt; never in network pump
    private long _netInbound;
    private int _netUpdatesLastFrame;
    private int _creaturesLogged;
    private Task<ObjectUpdateBuffer>? _pendingObjectParse;
    private ObjectUpdateBuffer? _pendingObjectUpdates;
    // Three retained 4K chunks cover the observed login burst without ever
    // creating a contiguous >85-KB reference array.
    private ObjectUpdateBuffer _objectUpdateBuffer = new(12_000);
    private int _pendingObjectUpdateIndex;
    private long _pendingObjectReceivedStamp;

    // Login-screen input buffers (password is never persisted anywhere).
    private readonly byte[] _acctBuf = new byte[64];
    private readonly byte[] _passBuf = new byte[128];
    private bool _loginInit;
    private bool _rememberAccount = true;   // the "Remember Account Name" checkbox

    // Character-select selection.
    private int _selectedChar;
    private bool _charSelectionRestored;

    // AreaTable.dbc (zone names for the roster rows), loaded once on first character-select draw.
    private AreaTableCatalog? _areas;
    private bool _areasLoaded;
    private FactionTemplateCatalog? _factions;

    /// <summary>Create the network client (does not connect). Called at the end of Load(). Stores gl for the deferred world load.</summary>
    private void InitNet(GL gl)
    {
        _gl = gl;

        // The Launch Options choice is the user's declared intent and wins over the
        // legacy server.enabled master switch. Before this, picking "SuperUI Client
        // Mode" saved the sticky LaunchMode but a server.enabled=false config kept
        // the serverless front door and never built the network client — the only
        // escape was editing client-config.json (and its bin-dir copy) by hand.
        // Fresh installs (empty LaunchMode) keep the offline-viewer default.
        if (!_config.Server.Enabled && Settings.LaunchMode == LaunchModeClient && !BatchInstrumentActive)
        {
            _config.Server.Enabled = true;
            Console.WriteLine("[net] server.enabled=false overridden by Launch Options (LaunchMode=Client)");
        }

        bool creator = CreatorLaunchActive;
        if (!_config.Server.Enabled && !GlueFrontDoorActive)
        {
            Console.WriteLine("[net] disabled (server.enabled = false) - offline batch mode");
            return;
        }

        // Live mode: do NOT render the offline debug character (the base HumanMale in Battlegear).
        // benilla shows no character at the login screen; the selected character appears only at
        // character-select, and the real in-world player model comes from the roster on login.
        if (_character is not null) _character.Enabled = false;

        // The login-screen glue scene (UI_MainMenu burning gate). Best-effort; draws only if it loads.
        try { if (_mpq is not null) _glue = new GlueScene(gl, _mpq, _config); }
        catch (Exception ex) { Console.WriteLine($"[glue] init failed: {ex.Message}"); }

        // The character-select per-race booth (UI_<Race> backgrounds, fog off). Best-effort; the
        // scene itself loads lazily on the first SetRace once we reach character select.
        try
        {
            if (_mpq is not null)
                _booth = new GlueBooth(gl, _mpq, _config, _assetWorkers, _uploads);
        }
        catch (Exception ex) { Console.WriteLine($"[booth] init failed: {ex.Message}"); }

        // The networked unit renderer. Loads the creature DBCs; draws streamed NPCs and
        // remote players as M2s once we are in world. Best-effort.
        try
        {
            if (_mpq is not null)
            {
                _creatures = new CreatureRenderer(
                    gl, _mpq, _config, _creatureLifecycle, _assetWorkers, _uploads);
                _creatures.AnimationResolved = CaptureAnimationChoice;
            }
        }
        catch (Exception ce) { Console.WriteLine($"[creature] init failed: {ce.Message}"); }
        try { if (_mpq is not null) _selectionRing = new SelectionRingRenderer(gl, _mpq); }
        catch (Exception ex) { Console.WriteLine($"[target] selection ring unavailable: {ex.Message}"); }

        InitPortraits(gl);
        InitGameplayUi(gl);
        if (_mpq is not null)
        {
            _spellEffects = new SpellEffectSource(_mpq);
            _spellSounds = new World.Spells.SpellSoundSystem(_mpq);
            _spellEffects.AnimationSoundEvent = (sound, unit, position) =>
                PlaySpellSoundAt(unit, sound, position, forceLoop: false, trackHold: false);
            try
            {
                _spellEffectMeshes = new SpellEffectMeshRenderer(gl, _mpq);
                string fxShaderDir = Path.Combine(AppContext.BaseDirectory, "Shaders");
                if (!File.Exists(Path.Combine(fxShaderDir, "attached.vert")))
                    fxShaderDir = Path.Combine(_config.RepoRoot, "MSUIClient", "Shaders");
                _spellEffectMeshes.LoadShaders(fxShaderDir);
                _spellRibbons = new SpellRibbonRenderer(gl, _mpq);
                _spellRibbons.LoadShaders();
                string spellShaderDir = Path.Combine(AppContext.BaseDirectory, "Shaders");
                if (!File.Exists(Path.Combine(spellShaderDir, "spell_particle.vert")))
                    spellShaderDir = Path.Combine(_config.RepoRoot, "MSUIClient", "Shaders");
                _spellParticles = new World.Spells.SpellParticleSystem(gl, _config, _mpq);
                _spellParticles.LoadShaders(spellShaderDir);
            }
            catch (Exception ex)
            {
                _spellEffectMeshes?.Dispose(); _spellEffectMeshes = null;
                _spellRibbons?.Dispose(); _spellRibbons = null;
                _spellParticles?.Dispose(); _spellParticles = null;
                Console.WriteLine($"[spell-fx] mesh renderer unavailable: {ex.Message}");
            }
        }
        InitInventory();
        InitCharacterPage();

        try
        {
            byte[]? bytes = _mpq?.ReadFile(FactionTemplateCatalog.MpqPath);
            _factions = bytes is null ? null : FactionTemplateCatalog.Parse(bytes);
            Console.WriteLine(_factions is null
                ? "[target] FactionTemplate.dbc unavailable - neutral fallback"
                : $"[target] FactionTemplate.dbc loaded ({_factions.Count} rows)");
        }
        catch (Exception ex) { Console.WriteLine($"[target] faction init failed: {ex.Message}"); }

        // No server configured: the front door + presentation stack above is all an
        // interactive session needs - never construct the network client. (With a
        // server configured the client is still created so the login screen works
        // if the user switches modes, but auto-login is suppressed for creator.)
        if (!_config.Server.Enabled)
        {
            Console.WriteLine("[creator] no server configured - front door up, network skipped");
            return;
        }

        EnsureNetworkClient(suppressAutoLogin: creator);
    }

    /// <summary>
    /// Construct the network client (does not connect the world; auto-login only when
    /// configured and not suppressed). Safe to call again: a live client is kept. Also
    /// the runtime half of the Launch Options switch — picking "SuperUI Client Mode"
    /// on a serverless boot builds the client here so the switch works without a
    /// restart or a config edit.
    /// </summary>
    private void EnsureNetworkClient(bool suppressAutoLogin)
    {
        if (_net is not null) return;
        try
        {
            NetSettings netSettings = _config.ToNetSettings();
            if (!string.IsNullOrWhiteSpace(_liveRunOptions?.Character))
            {
                netSettings = netSettings with { CharacterName = _liveRunOptions.Character };
                Console.WriteLine($"[live-run] selecting requested character {_liveRunOptions.Character}");
            }

            _net = new NetworkClient(netSettings, CaptureWirePacket,
                _config.DevTools ? ObserveSocketWrite : null);
            _net.CombatSendObserved = ObserveCombatSend;
            if (!suppressAutoLogin && _config.Server.AutoConnect &&
                !string.IsNullOrWhiteSpace(_config.Server.Account) &&
                !string.IsNullOrWhiteSpace(_config.Server.Password))
            {
                _net.Login(_config.Server.Account, _config.Server.Password);
                Console.WriteLine($"[net] auto-login as {_config.Server.Account}");
            }
            else
            {
                Console.WriteLine("[net] ready - log in via the in-client login screen");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[net] init failed: {ex.Message}");
            _net = null;
        }
    }

    /// <summary>Pump the network client once per frame. Called near the top of Update(dt), before the world-load guard.</summary>
    private void PumpNet(float dt)
    {
        // BeginWorldLoad can be entered from this method while handling the login
        // result.  Packets later in that same pre-load drain are not evidence that
        // the loading loop pumps the network, so only count a drain whose frame
        // entered PumpNet with the curtain already active.
        bool loadActiveAtPumpEntry = _worldLoading;
        _wireLog.Pump();
        if (_net is null) return;
        if (_net.State is NetState.Failed or NetState.Disconnected)
        {
            ResetQuestSession(clearStatusStore: true);
            ResetPetActionBar();
            ResetVendor();
            // Frozen group-session clear feeds PARTY_INVITE_CANCEL before the retained UI feed
            // drops the roster. ResetParty is idempotent after the first edge; a decline attempt
            // may be rejected by the already-closed socket and is not claimed as a successful wire.
            ResetParty();
        }
        // StaticPopup's monotonic two-slot Advance runs even when WorldMap or another full-screen
        // owner suppresses DrawCombatHud. Keep slot time in the always-pumped lifecycle;
        // DrawPartyInvite only presents the current PARTY_INVITE instance.
        UpdatePartyInviteLifecycle();

        // Surface any character-create result (the create request runs while parked at select).
        if (_net.TryTakeCreateResult(out byte ccCode)) OnCreateResult(ccCode);

        // The server has assigned our character + spawn point (SMSG_LOGIN_VERIFY_WORLD / SMSG_NEW_WORLD).
        if (_net.TakeEnterWorld() is { } enter && _controller is not null)
        {
            if (_pendingObjectParse is not null)
                _objectUpdateBuffer = new ObjectUpdateBuffer(12_000);
            _entities.Clear();
            _combat.Clear();
            _actions.Clear();
            // TakeEnterWorld also carries SMSG_NEW_WORLD. Group state is session-owned and must
            // survive zoning; the disconnected/session edge above is the authoritative reset.
            ResetPetActionBar();
            EnterPlayerAuraWorld(_net.PlayerGuid);
            _movementRooted = false;
            _iceBlockFrozen = false;
            _iceBlockFacing = enter.Orientation;
            _movementSender.Reset(enter.Orientation);
            ResetTargeting();
            ResetCombatFeedback();
            ResetLoot();
            ResetGameObjects();
            ResetRestXp();
            ResetDeathRez();
            ResetHearth();
            ResetTaxi();
            ResetGossip();
            ResetVendor();
            ResetQuestSession(clearStatusStore: true);
            ResetMail();
            _net.QueryNextMailTime();
            ResetAuction();
            ResetGuild();
            ResetTabard();
            _creaturesLogged = 0;
            _pendingObjectParse = null;
            _pendingObjectUpdates = null;
            _pendingObjectUpdateIndex = 0;

            // Commit to the server-authoritative spawn. BeginWorldLoad reads _config.Start for the
            // load centre, and its Finish phase teleports us onto real ground there.
            _config.Start.Map = (int)enter.Map;
            _config.Start.X = enter.Position.X;
            _config.Start.Y = enter.Position.Y;
            _config.Start.Z = enter.Position.Z;
            _config.Start.Orientation = enter.Orientation;
            _controller.Teleport(enter.Position.X, enter.Position.Y, enter.Position.Z);

            // THE CAMERA IS THE FACING, so setting the controller's Yaw alone
            // did nothing: CharacterController.Update overwrites Yaw from
            // input.Yaw every frame, and input.Yaw is Camera.Yaw. The server's
            // spawn orientation was therefore discarded on the very next frame
            // and every login faced whatever the camera happened to be at -
            // Start.Orientation, which is zero. Set the one the controller
            // actually reads, and clear the orbit so the camera starts behind us
            // rather than wherever it was left at the character screen.
            _controller.Yaw = enter.Orientation;
            _window.Camera.Yaw = enter.Orientation;
            _window.Camera.OrbitYaw = 0f;
            _window.Camera.Target = _controller.Position;

            if (!_worldLoadStarted)
            {
                Console.WriteLine($"[net] entering world: map {enter.Map} at " +
                                  $"({enter.Position.X:F0}, {enter.Position.Y:F0}, {enter.Position.Z:F0}) - loading");
                _worldLoadStarted = true;
                _preWorldHudPrimed = false;
                _worldEntryTransitionStage = 1;
            }
            else
            {
                // SMSG_NEW_WORLD changes the authoritative map, not just the coordinates.
                // Keep the renderer/ADT map in the same transaction; otherwise a server
                // teleport to Deadmines leaves Azeroth loaded at instance coordinates and
                // the live runner can never observe the instance's streamed objects.
                if (_config.Start.Map != (int)enter.Map)
                {
                    EnsureInstanceData();
                    MapRow? destinationMap = _maps?.Get((int)enter.Map);
                    if (destinationMap is not null && _adts is not null)
                    {
                        TearDownWorldContent();
                        _adts.SetMap(destinationMap.Directory);
                        _residentCentre = null;
                        _config.Start.MapName = destinationMap.Directory;
                    }
                    else Console.WriteLine($"[net] map {enter.Map} has no Map.dbc/WDT identity");
                }
                Console.WriteLine($"[net] moved to map {enter.Map} at " +
                                  $"({enter.Position.X:F0}, {enter.Position.Y:F0}, {enter.Position.Z:F0})");
                _worldEntryTransitionStage = 3;
            }
        }

        // Drain + dispatch the inbound packet stream into the entity store. A
        // login burst can contain thousands of object updates; cap both work
        // units and elapsed time so the curtain and reveal frames stay bounded.
        int updates = 0;
        int packetsDrained = 0;
        long drainStarted = Stopwatch.GetTimestamp();

        if (_pendingObjectParse is { } parse)
        {
            if (!parse.IsCompleted) goto FinishNetPump;
            try { _pendingObjectUpdates = parse.GetAwaiter().GetResult(); }
            catch (Exception ex)
            {
                Console.WriteLine($"[net] object-update parse error: {ex.Message}");
                _pendingObjectUpdates = null;
                _objectUpdateBuffer.Clear();
            }
            _pendingObjectParse = null;
            _pendingObjectUpdateIndex = 0;
        }

        if (_pendingObjectUpdates is { } pending)
        {
            while (_pendingObjectUpdateIndex < pending.Count &&
                   Stopwatch.GetElapsedTime(drainStarted).TotalMilliseconds < NetDrainTimeBudgetMs)
                ApplyUpdate(pending[_pendingObjectUpdateIndex++], _pendingObjectReceivedStamp);
            if (_pendingObjectUpdateIndex < pending.Count) goto FinishNetPump;
            pending.Clear();
            _pendingObjectUpdates = null;
            _pendingObjectUpdateIndex = 0;
            updates++;
        }

        bool parseStarted = false;
        while (packetsDrained < NetDrainPacketBudget &&
               Stopwatch.GetElapsedTime(drainStarted).TotalMilliseconds < NetDrainTimeBudgetMs &&
               _net.TryDequeue(out ushort opcode, out byte[] body, out long receivedStamp))
        {
            packetsDrained++;
            _netInbound++;
            NoteLoadPacketPumped(loadActiveAtPumpEntry);
            try
            {
                switch ((Op)opcode)
                {
                    case Op.SMSG_LOGOUT_RESPONSE:
                        ApplyLogoutResponse(body);
                        break;
                    case Op.SMSG_LOGOUT_CANCEL_ACK:
                        ApplyLogoutCancelAck();
                        break;
                    case Op.SMSG_LOGOUT_COMPLETE:
                        ApplyLogoutComplete();
                        break;
                    case Op.SMSG_GROUP_LIST:
                        ApplyPartyRoster(body);
                        break;
                    case Op.SMSG_GROUP_INVITE:
                        ApplyPartyInvite(body);
                        break;
                    case Op.SMSG_GROUP_DECLINE:
                        ApplyPartyDecline(body);
                        break;
                    case Op.SMSG_GROUP_UNINVITE:
                        ApplyPartyUninvited(body);
                        break;
                    case Op.SMSG_GROUP_SET_LEADER:
                        ApplyPartyLeaderChanged(body);
                        break;
                    case Op.SMSG_GROUP_DESTROYED:
                        ApplyPartyDestroyed(body);
                        break;
                    case Op.SMSG_PARTY_COMMAND_RESULT:
                        ApplyPartyCommandResult(body);
                        break;
                    case Op.SMSG_PARTY_MEMBER_STATS:
                        ApplyPartyMemberStats(body, fullSnapshot: false);
                        break;
                    case Op.SMSG_PARTY_MEMBER_STATS_FULL:
                        ApplyPartyMemberStats(body, fullSnapshot: true);
                        break;
                    case Op.MSG_MINIMAP_PING:
                        // Frozen Benilla validates this rebroadcast but has no apply/UI consumer.
                        _ = PartyFramePacketLaw.ParseMinimapPing(body);
                        break;
                    case Op.MSG_RAID_TARGET_UPDATE:
                        ApplyPartyRaidTargetUpdate(body);
                        break;
                    case Op.MSG_RAID_READY_CHECK:
                        // Frozen Benilla validates ready-check shapes but intentionally ignores them.
                        _ = PartyFramePacketLaw.ParseReadyCheck(body);
                        break;
                    case Op.SMSG_PET_SPELLS:
                        ApplyPetSpells(body);
                        break;
                    case Op.SMSG_PET_MODE:
                        ApplyPetMode(body);
                        break;
                    case Op.SMSG_PET_ACTION_FEEDBACK:
                        ApplyPetActionFeedback(body);
                        break;
                    case Op.SMSG_PET_CAST_FAILED:
                        ApplyPetCastFailed(body);
                        break;
                    case Op.SMSG_SPELL_COOLDOWN:
                        ApplyAddressedSpellCooldowns(body);
                        break;
                    case Op.SMSG_INSPECT:
                        ApplyInspect(body);
                        break;
                    case Op.SMSG_FRIEND_LIST:
                        ApplyFriendList(body);
                        break;
                    case Op.SMSG_FRIEND_STATUS:
                        ApplyFriendStatus(body);
                        break;
                    case Op.SMSG_IGNORE_LIST:
                        ApplyIgnoreList(body);
                        break;
                    case Op.SMSG_WHO:
                        ApplyWhoList(body);
                        break;
                    case Op.SMSG_TRADE_STATUS:
                        ApplyTradeStatus(body);
                        break;
                    case Op.SMSG_TRADE_STATUS_EXTENDED:
                        ApplyTradeExtended(body);
                        break;
                    case Op.SMSG_GMTICKET_CREATE:
                    case Op.SMSG_GMTICKET_UPDATETEXT:
                    case Op.SMSG_GMTICKET_DELETETICKET:
                    case Op.SMSG_GMTICKET_SYSTEMSTATUS:
                    case Op.SMSG_GMTICKET_GETTICKET:
                        ApplyHelpTicketPacket((Op)opcode, body);
                        break;
                    case Op.SMSG_INITIALIZE_FACTIONS:
                        ApplyInitialFactions(body);
                        break;
                    case Op.SMSG_SET_FACTION_VISIBLE:
                        ApplyFactionVisible(body);
                        break;
                    case Op.SMSG_SET_FACTION_STANDING:
                        ApplyFactionStanding(body);
                        break;
                    case Op.MSG_MOVE_TELEPORT_ACK:
                        {
                            // Build 5875 server->client same-map teleport:
                            // packed mover guid, movement counter, destination MovementInfo.
                            var teleportReader = new PacketReader(body);
                            ulong moverGuid = teleportReader.ReadPackedGuid();
                            uint counter = teleportReader.ReadU32();
                            MovementInfo destination = MovementInfo.Read(teleportReader);
                            if (teleportReader.Remaining != 0)
                                throw new InvalidDataException(
                                    $"MSG_MOVE_TELEPORT_ACK has {teleportReader.Remaining} trailing byte(s)");
                            if (_controller is null || moverGuid != _net.PlayerGuid)
                            {
                                Console.WriteLine($"[net] ignored same-map teleport for mover 0x{moverGuid:X16} " +
                                                  $"(player 0x{_net.PlayerGuid:X16})");
                                break;
                            }

                            // Apply on the game thread before acknowledging. Camera yaw is the
                            // next frame's MovementInput yaw, so updating it is what makes the
                            // server orientation survive rather than being overwritten immediately.
                            _controller.Teleport(destination.Position.X, destination.Position.Y,
                                destination.Position.Z);
                            _controller.Yaw = destination.Orientation;
                            _window.Camera.Yaw = destination.Orientation;
                            _window.Camera.OrbitYaw = 0f;
                            _window.Camera.Target = _controller.Position;
                            _character?.SnapFacing(destination.Orientation);
                            _movementSender.Reset(destination.Orientation);
                            ObserveTeleportApplied(moverGuid, counter, destination);
                            _net.TeleportAck(moverGuid, counter);
                        }
                        break;
                    case Op.SMSG_FORCE_MOVE_ROOT:
                    case Op.SMSG_FORCE_MOVE_UNROOT:
                        {
                            var rootReader = new PacketReader(body);
                            ulong moverGuid = rootReader.ReadPackedGuid();
                            uint counter = rootReader.ReadU32();
                            if (rootReader.Remaining != 0)
                                throw new InvalidDataException(
                                    $"{(Op)opcode} has {rootReader.Remaining} trailing byte(s)");
                            if (_controller is null || moverGuid != _net.PlayerGuid)
                            {
                                Console.WriteLine($"[movement] ignored {(Op)opcode} for mover " +
                                                  $"0x{moverGuid:X16}");
                                break;
                            }

                            bool rooted = (Op)opcode == Op.SMSG_FORCE_MOVE_ROOT;
                            _movementRooted = rooted;
                            if (rooted) _movementSender.ParkForRoot(_net, _controller);
                            MovementInfo ack = MovementInfo.Create(_controller.Position,
                                _controller.Yaw, rooted ? MovementFlags.Root : MovementFlags.None);
                            _net.MoveRootAck(moverGuid, counter, rooted, ack);
                            Console.WriteLine($"[movement] mover {(rooted ? "rooted" : "unrooted")} " +
                                              $"counter={counter} ackFlags=0x{ack.Flags:X8}");
                        }
                        break;
                    case Op.SMSG_UPDATE_OBJECT:
                        _pendingObjectReceivedStamp = receivedStamp;
                        ObjectUpdateBuffer updateBuffer = _objectUpdateBuffer;
                        _pendingObjectParse = Task.Run(
                            () => UpdateObjectParser.Parse(body, updateBuffer));
                        parseStarted = true;
                        break;
                    case Op.SMSG_COMPRESSED_UPDATE_OBJECT:
                        _pendingObjectReceivedStamp = receivedStamp;
                        ObjectUpdateBuffer compressedBuffer = _objectUpdateBuffer;
                        _pendingObjectParse = Task.Run(
                            () => UpdateObjectParser.ParseCompressed(body, compressedBuffer));
                        parseStarted = true;
                        break;
                    case Op.SMSG_DESTROY_OBJECT:
                        _entities.Remove(new PacketReader(body).ReadU64());
                        break;
                    case Op.SMSG_TRIGGER_CINEMATIC:
                        {
                            uint cinematicId = body.Length >= 4
                                ? new PacketReader(body).ReadU32()
                                : 0;
                            _net.CompleteCinematic();
                            Console.WriteLine($"[net] cinematic {cinematicId} triggered - acked (skip)");
                        }
                        break;
                    case Op.SMSG_MONSTER_MOVE:
                        {
                            // Creature locomotion: attach the server spline so this NPC walks.
                            var mm = MonsterMoveParser.Parse(body);
                            if (mm is not null)
                            {
                                ObserveTaxiSpline(mm);
                                _entities.ApplyMonsterMove(mm, MovementInfo.ClientUptimeMs());
                            }
                        }
                        break;
                    case Op.SMSG_ACTION_BUTTONS:
                        _actions.ApplyButtons(body);
                        break;
                    case Op.SMSG_UPDATE_AURA_DURATION:
                        ApplyAuraDuration(body);
                        break;
                    case Op.SMSG_INITIAL_SPELLS:
                        _actions.ApplyInitialSpells(body, MovementInfo.ClientUptimeMs() / 1000.0);
                        break;
                    case Op.SMSG_LEARNED_SPELL:
                        {
                            var spellReader = new PacketReader(body);
                            uint learned = spellReader.ReadU16();
                            _actions.Learn(learned);
                            ObserveTrainerLearned(learned);
                            ObserveProfessionLearned(learned);
                        }
                        break;
                    case Op.SMSG_SUPERCEDED_SPELL:
                        {
                            var spellReader = new PacketReader(body);
                            _actions.Supercede(spellReader.ReadU16(), spellReader.ReadU16());
                        }
                        break;
                    case Op.SMSG_REMOVED_SPELL:
                        {
                            var spellReader = new PacketReader(body);
                            uint removed = spellReader.ReadU16();
                            _actions.Remove(removed);
                            EmitInterface("talent", "spell-removed", "APPLIED", removed, "source=SMSG_REMOVED_SPELL");
                        }
                        break;
                    case Op.SMSG_ITEM_QUERY_SINGLE_RESPONSE:
                        if (_items is not null)
                        {
                            uint landedItem = _items.Apply(body);
                            ObserveQuestItemTemplateLanding(landedItem);
                        }
                        break;
                    case Op.SMSG_GOSSIP_MESSAGE:
                        ApplyGossipMenu(body);
                        break;
                    case Op.SMSG_GOSSIP_COMPLETE:
                        EmitInterface("gossip", "complete", "RECEIVED", _gossipMenu?.SourceGuid ?? 0, "serverClosed=true");
                        ResetGossip();
                        CloseQuestNpcFrame(playSound: true);
                        break;
                    case Op.SMSG_NPC_TEXT_UPDATE:
                        ApplyNpcText(body);
                        break;
                    case Op.SMSG_GAMEOBJECT_CUSTOM_ANIM:
                        ApplyGameObjectCustomAnim(body);
                        break;
                    case Op.SMSG_PAGE_TEXT_QUERY_RESPONSE:
                        ApplyPageText(body);
                        break;
                    case Op.SMSG_LIST_INVENTORY:
                        ApplyVendorList(body);
                        break;
                    case Op.SMSG_TRAINER_LIST:
                        ApplyTrainerList(body);
                        break;
                    case Op.SMSG_TRAINER_BUY_SUCCEEDED:
                        ApplyTrainerSuccess(body);
                        break;
                    case Op.SMSG_TRAINER_BUY_FAILED:
                        ApplyTrainerFailure(body);
                        break;
                    case Op.SMSG_BINDER_CONFIRM:
                        ApplyBinderConfirm(body);
                        break;
                    case Op.SMSG_BINDPOINTUPDATE:
                        ApplyBindPoint(body);
                        break;
                    case Op.SMSG_TAXINODE_STATUS:
                        ApplyTaxiNodeStatus(body);
                        break;
                    case Op.SMSG_SHOWTAXINODES:
                        ApplyTaxiNodes(body);
                        break;
                    case Op.SMSG_ACTIVATETAXIREPLY:
                        ApplyTaxiReply(body);
                        break;
                    case Op.MSG_TALENT_WIPE_CONFIRM:
                        ApplyTalentWipeConfirm(body);
                        break;
                    case Op.SMSG_SHOW_BANK:
                        ApplyShowBank(body);
                        break;
                    case Op.SMSG_BUY_BANK_SLOT_RESULT:
                        ApplyBuyBankSlotResult(body);
                        break;
                    case Op.SMSG_MAIL_LIST_RESULT:
                        ApplyMailList(body);
                        break;
                    case Op.SMSG_SEND_MAIL_RESULT:
                        ApplyMailResult(body);
                        break;
                    case Op.SMSG_ITEM_TEXT_QUERY_RESPONSE:
                        ApplyMailItemText(body);
                        break;
                    case Op.SMSG_RECEIVED_MAIL:
                        ApplyReceivedMail(body);
                        break;
                    case Op.MSG_QUERY_NEXT_MAIL_TIME:
                        ApplyNextMailTime(body);
                        break;
                    case Op.MSG_AUCTION_HELLO:
                        ApplyAuctionHello(body);
                        break;
                    case Op.SMSG_AUCTION_LIST_RESULT:
                        ApplyAuctionList(body, "browse");
                        break;
                    case Op.SMSG_AUCTION_OWNER_LIST_RESULT:
                        ApplyAuctionList(body, "owner");
                        break;
                    case Op.SMSG_AUCTION_BIDDER_LIST_RESULT:
                        ApplyAuctionList(body, "bidder");
                        break;
                    case Op.SMSG_AUCTION_COMMAND_RESULT:
                        ApplyAuctionCommand(body);
                        break;
                    case Op.SMSG_AUCTION_BIDDER_NOTIFICATION:
                    case Op.SMSG_AUCTION_OWNER_NOTIFICATION:
                    case Op.SMSG_AUCTION_REMOVED_NOTIFICATION:
                        ApplyAuctionNotification((Op)opcode, body);
                        break;
                    case Op.SMSG_GUILD_ROSTER:
                        ApplyGuildRoster(body);
                        break;
                    case Op.SMSG_GUILD_EVENT:
                        ApplyGuildEvent(body);
                        break;
                    case Op.SMSG_GUILD_COMMAND_RESULT:
                        ApplyGuildCommandResult(body);
                        break;
                    case Op.SMSG_TABARDVENDOR_ACTIVATE:
                        ApplyTabardVendorActivate(body);
                        break;
                    case Op.MSG_SAVE_GUILD_EMBLEM:
                        ApplySaveGuildEmblemResult(body);
                        break;
                    case Op.SMSG_QUESTGIVER_STATUS:
                        ApplyQuestStatus(body);
                        break;
                    case Op.SMSG_QUEST_QUERY_RESPONSE:
                        ApplyQuestQuery(body);
                        break;
                    case Op.SMSG_QUESTGIVER_QUEST_LIST:
                        ApplyQuestList(body);
                        break;
                    case Op.SMSG_QUESTGIVER_QUEST_DETAILS:
                        ApplyQuestDetails(body);
                        break;
                    case Op.SMSG_QUESTGIVER_REQUEST_ITEMS:
                        ApplyQuestRequestItems(body);
                        break;
                    case Op.SMSG_QUESTGIVER_OFFER_REWARD:
                        ApplyQuestOffer(body);
                        break;
                    case Op.SMSG_QUESTUPDATE_ADD_KILL:
                        ApplyQuestKill(body);
                        break;
                    case Op.SMSG_QUESTUPDATE_ADD_ITEM:
                        ApplyQuestItem(body);
                        break;
                    case Op.SMSG_QUESTUPDATE_COMPLETE:
                        ApplyQuestObjectiveComplete(body);
                        break;
                    case Op.SMSG_QUESTGIVER_QUEST_COMPLETE:
                        ApplyQuestComplete(body);
                        break;
                    case Op.SMSG_QUESTGIVER_QUEST_INVALID:
                    case Op.SMSG_QUESTGIVER_QUEST_FAILED:
                    case Op.SMSG_QUESTUPDATE_FAILED:
                    case Op.SMSG_QUESTUPDATE_FAILEDTIMER:
                    case Op.SMSG_QUESTLOG_FULL:
                        ApplyQuestError((Op)opcode, body);
                        break;
                    case Op.SMSG_INIT_WORLD_STATES:
                        ApplyInitialWorldStates(body);
                        break;
                    case Op.SMSG_UPDATE_WORLD_STATE:
                        ApplyWorldState(body);
                        break;
                    case Op.SMSG_BUY_ITEM:
                        ApplyVendorStockUpdate(body);
                        break;
                    case Op.SMSG_BUY_FAILED:
                        ApplyVendorBuyFailure(body);
                        break;
                    case Op.SMSG_SELL_ITEM:
                        ApplyVendorSellFailure(body);
                        break;
                    case Op.SMSG_NAME_QUERY_RESPONSE:
                        {
                            var r = new PacketReader(body);
                            ulong guid = r.ReadU64();
                            string name = r.ReadCString();
                            if (name.Length > 0) _playerNames[guid] = name;
                        }
                        break;
                    case Op.SMSG_CREATURE_QUERY_RESPONSE:
                        {
                            CreatureQueryResponse response = CreatureQueryPacket.Parse(body);
                            _queriedCreatureNames.Remove(response.Entry);
                            _creatureQueryRecords[response.Entry] = response.Info;
                            if (response.Info is { Name.Length: > 0 } info)
                                _creatureNames[response.Entry] = info.Name;
                            else
                                _creatureNames.Remove(response.Entry);
                        }
                        break;
                    case Op.SMSG_SPELL_START:
                        EnqueueSpellPresentation(new SpellStartEvent(SpellPacketParser.ParseStart(body)));
                        break;
                    case Op.SMSG_SPELL_GO:
                        EnqueueSpellPresentation(new SpellGoEvent(SpellPacketParser.ParseGo(body)));
                        break;
                    case Op.SMSG_CAST_RESULT:
                        {
                            var result = SpellPacketParser.ParseResult(body);
                            if (result.Status == 2)
                                EnqueueSpellPresentation(new SpellCastResultEvent(result.SpellId, result.Reason));
                        }
                        break;
                    case Op.SMSG_SPELL_FAILED_OTHER:
                        {
                            var r = new PacketReader(body);
                            ulong caster = r.ReadU64(); uint spell = r.ReadU32();
                            EnqueueSpellPresentation(new SpellFailedOtherEvent(caster, spell));
                        }
                        break;
                    case Op.SMSG_SPELL_DELAYED:
                        {
                            SpellDelayedPacket delayed = SpellLifecyclePacketParser.ParseDelayed(body);
                            EnqueueSpellPresentation(new SpellDelayedEvent(delayed.Caster, delayed.DelayMs));
                        }
                        break;
                    case Op.MSG_CHANNEL_START:
                        {
                            SpellChannelStartPacket channel = SpellLifecyclePacketParser.ParseChannelStart(body);
                            EnqueueSpellPresentation(new SpellChannelStartEvent(
                                channel.SpellId, channel.DurationMs));
                        }
                        break;
                    case Op.MSG_CHANNEL_UPDATE:
                        EnqueueSpellPresentation(new SpellChannelUpdateEvent(
                            SpellLifecyclePacketParser.ParseChannelUpdate(body)));
                        break;
                    case Op.SMSG_PLAY_SPELL_VISUAL:
                        {
                            var r = new PacketReader(body);
                            EnqueueSpellPresentation(new SpellKitPushEvent(r.ReadU64(), r.ReadU32()));
                        }
                        break;
                    case Op.SMSG_CANCEL_AUTO_REPEAT:
                        EnqueueSpellPresentation(new SpellAutoRepeatCancelledEvent());
                        break;
                    case Op.SMSG_LOOT_RESPONSE:
                        ApplyLootResponse(body);
                        break;
                    case Op.SMSG_LOOT_REMOVED:
                        ApplyLootRemoved(body);
                        break;
                    case Op.SMSG_LOOT_CLEAR_MONEY:
                        ApplyLootClearMoney();
                        break;
                    case Op.SMSG_LOOT_RELEASE_RESPONSE:
                        ApplyLootReleaseResponse(body);
                        break;
                    case Op.SMSG_LOOT_MONEY_NOTIFY:
                        break; // the purse rides PLAYER_FIELD_COINAGE; nothing to do
                    case Op.SMSG_ITEM_PUSH_RESULT:
                        ApplyItemPushResult(body);
                        break;
                    case Op.SMSG_ATTACKSTART:
                    case Op.SMSG_ATTACKSTOP:
                    case Op.SMSG_ATTACKERSTATEUPDATE:
                    case Op.SMSG_SPELLNONMELEEDAMAGELOG:
                    case Op.SMSG_PERIODICAURALOG:
                    case Op.SMSG_SPELLHEALLOG:
                    case Op.SMSG_SPELLENERGIZELOG:
                    case Op.SMSG_SPELLDAMAGESHIELD:
                    case Op.SMSG_ENVIRONMENTALDAMAGELOG:
                    case Op.SMSG_SPELLLOGMISS:
                    case Op.SMSG_LOG_XPGAIN:
                        CombatEvent combatEvent = _combat.Apply(
                            CombatPacketParser.Parse((Op)opcode, body), _entities);
                        ObserveCombatReceive((Op)opcode, combatEvent);
                        ObserveChannelCombat(combatEvent);
                        ApplyCombatAnimation(combatEvent);
                        break;
                    case Op.SMSG_GAMEOBJECT_QUERY_RESPONSE:
                        ApplyGameObjectQuery(body);
                        break;
                    case Op.SMSG_LEVELUP_INFO:
                        ApplyLevelUpInfo(body);
                        break;
                    case Op.SMSG_RESURRECT_REQUEST:
                        ApplyResurrectRequest(body);
                        break;
                    case Op.SMSG_CORPSE_RECLAIM_DELAY:
                        ApplyCorpseReclaimDelay(body);
                        break;
                    case Op.SMSG_ATTACKSWING_NOTINRANGE:
                    case Op.SMSG_ATTACKSWING_BADFACING:
                    case Op.SMSG_ATTACKSWING_NOTSTANDING:
                    case Op.SMSG_ATTACKSWING_DEADTARGET:
                    case Op.SMSG_ATTACKSWING_CANT_ATTACK:
                        ObserveCombatError((Op)opcode, body);
                        break;
                    case Op.SMSG_MESSAGECHAT:
                    case Op.SMSG_NOTIFICATION:
                        ObserveGmChatResponse(body);
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[net] parse error on opcode 0x{opcode:X4}: {ex.Message}");
            }
            if (parseStarted) break;
        }

    FinishNetPump:
        _netUpdatesLastFrame = updates;

        // Spell packet parsing is intentionally side-effect free.  Apply its ordered edge stream
        // only after the current object-update slice, before any animation/spline simulation.
        DrainSpellPresentationEvents();

        // Advance every in-progress creature spline so NPCs actually move between packets.
        _entities.TickSplines(MovementInfo.ClientUptimeMs());
        if (_controller is not null)
            _entities.FaceIdleTargets(dt, _net.PlayerGuid, _controller.Position);
        DiscoverItemTemplates();
    }

    private void CaptureWirePacket(bool outgoing, ushort opcode, ReadOnlySpan<byte> payload)
    {
        var packet = new WirePacket(NowSeconds(), outgoing, opcode,
            WireRing.NameFor(opcode), payload.Length);
        ReadOnlySpan<byte> visiblePayload = WireLogRecorder.ShouldStorePayload(opcode)
            ? payload
            : ReadOnlySpan<byte>.Empty;
        _wire.Add(packet, visiblePayload);
        _wireLog.Enqueue(packet, payload);
        if ((Op)opcode is Op.CMSG_MESSAGECHAT or Op.SMSG_MESSAGECHAT or Op.SMSG_NOTIFICATION)
            ObserveGmChatWire(outgoing, opcode, payload);
        if ((Op)opcode is Op.MSG_MOVE_TELEPORT_ACK or Op.SMSG_NEW_WORLD or Op.SMSG_TRANSFER_PENDING)
            ObserveTeleportWire(outgoing, opcode, payload);
        if (outgoing) _creatureLifecycle.NoteOutgoingPacket(opcode, payload.Length);
    }

    private void ApplyUpdate(ObjectUpdate u, long receivedStamp)
    {
        _entities.TryGet(u.Guid, out WorldEntity? auraUnitBefore);
        Dictionary<byte, AuraSnapshot> aurasBefore = SnapshotAuras(auraUnitBefore);
        if ((u.Kind is UpdateKind.CreateObject or UpdateKind.CreateObject2) &&
            u.Type == ObjectTypeId.Unit)
            _creatureLifecycle.NoteSpawnPacket(
                u.Guid, u.Fields?.DisplayId ?? 0, receivedStamp);
        if (u.Kind == UpdateKind.OutOfRange && u.Guids is not null)
            foreach (ulong guid in u.Guids)
                _creatureLifecycle.NoteReason(
                    guid, CreatureLifecycleTracker.ReasonCode.NOT_IN_WORLD);
        _entities.Apply(u);
        if ((u.Kind is UpdateKind.CreateObject or UpdateKind.CreateObject2) &&
            u.Type == ObjectTypeId.GameObject && _entities.TryGet(u.Guid, out WorldEntity streamedGo))
            RequireGameObjectTemplate(streamedGo);
        if (u.Guid == _net?.PlayerGuid) { ObserveQuestLog(); ObserveRestXp(); ObserveDeathRez(); }
        ObserveProfessionProductTransition();
        if (u.Type == ObjectTypeId.Corpse || (u.Guid != 0 && _entities.TryGet(u.Guid, out WorldEntity corpse) && corpse.Type == ObjectTypeId.Corpse)) ObserveCorpseStore();
        ObserveAuraObjectUpdate(u.Guid, aurasBefore);
        if ((u.Kind is UpdateKind.CreateObject or UpdateKind.CreateObject2) &&
            u.Type == ObjectTypeId.Unit && _creaturesLogged < 50)
        {
            _creaturesLogged++;
            var p = u.Movement?.Position ?? Vector3.Zero;
            Console.WriteLine($"[net] creature entry {u.Fields?.Entry ?? GuidInfo.Entry(u.Guid) ?? 0} " +
                              $"display {u.Fields?.DisplayId ?? 0} L{u.Fields?.Level ?? 0} at ({p.X:F0}, {p.Y:F0}, {p.Z:F0})");
        }
    }

    /// <summary>
    /// Configure the player avatar from the logged-in roster character instead of the
    /// offline test body. Faithful to benilla: the picked roster Character carries every
    /// appearance byte AND 19 already-resolved equipment display ids, which is sufficient
    /// to build the correct model at spawn (benilla's char-select preview builds the exact
    /// same composited model straight from the roster).
    /// </summary>
    private void ApplyServerCharacter(bool rebuild = true)
    {
        if (_character is null) return;
        Character? c = _net?.Player;
        if (c is null) { _character.Enabled = true; return; }

        string race = RaceFolder(c.Race);
        string gender = c.Gender == 1 ? "Female" : "Male";

        bool sameBody = race.Equals(_character.Race, StringComparison.OrdinalIgnoreCase) &&
                        gender.Equals(_character.Gender, StringComparison.OrdinalIgnoreCase);
        CharacterEquipment equipment = BuildEquipment(c);
        bool sameAppearance = sameBody &&
                              _character.SkinId == c.Skin &&
                              _character.FaceId == c.Face &&
                              _character.HairStyleId == c.HairStyle &&
                              _character.HairColorId == c.HairColor &&
                              _character.FacialHairId == c.FacialHair &&
                              EquipmentVisuallyMatches(_character.Equipment, equipment);

        if (!rebuild && sameAppearance)
        {
            _character.Enabled = true;
            _playerPortraitDirty = true;
            Console.WriteLine($"[character] player model reused: {race} {gender} " +
                              $"({c.Equipment.Count(e => e.DisplayId != 0)} equipped)");
            return;
        }

        if (sameBody && _character.QueueAppearanceUpdate(
                c.Skin, c.Face, c.HairStyle, c.HairColor, c.FacialHair, equipment))
        {
            _character.Enabled = true;
            _playerPortraitDirty = true;
            Console.WriteLine($"[character] queued async player appearance diff: {race} {gender} " +
                              $"({c.Equipment.Count(e => e.DisplayId != 0)} equipped)");
            return;
        }

        // Re-load the base model only if the race/gender differs from what is loaded
        // (the offline test body is Human/Male; most logins will differ).
        if (!race.Equals(_character.Race, StringComparison.OrdinalIgnoreCase) ||
            !gender.Equals(_character.Gender, StringComparison.OrdinalIgnoreCase))
        {
            if (!_character.Load(race, gender))
                Console.WriteLine($"[character] could not load {race} {gender}; keeping current body");
        }

        _character.SkinId = c.Skin;
        _character.FaceId = c.Face;
        _character.HairStyleId = c.HairStyle;
        _character.HairColorId = c.HairColor;
        _character.FacialHairId = c.FacialHair;
        _character.Equipment = equipment;
        _character.Reload();          // rebuild texture slots + geosets, then composite the gear
        _character.Enabled = true;
        _playerPortraitDirty = true;

        Console.WriteLine($"[character] player model: {race} {gender} " +
                          $"skin {c.Skin} face {c.Face} hair {c.HairStyle}/{c.HairColor} facial {c.FacialHair} " +
                          $"({c.Equipment.Count(e => e.DisplayId != 0)} equipped)");
    }

    /// <summary>
    /// Enter-world ownership transfer is deliberately outside <see cref="PumpNet"/>.  The load
    /// bootstrap and avatar hand-off are independent bounded stages, so neither can turn a login
    /// packet into a long network-pump frame and the loader cannot stack work on either frame.
    /// </summary>
    private bool PumpWorldEntryTransition()
    {
        if (_worldEntryTransitionStage == 0) return false;

        if (_worldEntryTransitionStage == 1)
        {
            // Auto-login can leave CharacterSelect on the network thread before the booth ever
            // renders. Materialize the selected roster avatar now, while the curtain is already
            // opaque but before BeginWorldLoad starts the measured interval.
            if (_net?.Player is { } rosterAvatar) _booth?.SetCharacter(rosterAvatar);
            _worldEntryTransitionStage = 2;
            return true;
        }

        if (_worldEntryTransitionStage == 2)
        {
            // Give the now-authoritative in-world HUD one invisible build while
            // the already-armed curtain is up, before BeginWorldLoad starts the
            // measured interval. Gui marks the prime complete later this frame.
            _worldEntryTransitionStage = 3;
            return true;
        }

        if (_worldEntryTransitionStage == 3)
        {
            if (_gl is not null) BeginWorldLoad(_gl);
            _worldEntryTransitionStage = 4;
            return true;
        }

        bool reusedSelectedAvatar = false;
        if (_net?.Player is { } selected &&
            _booth?.TakeCharacter(selected.Guid) is { } selectedAvatar)
        {
            selectedAvatar.CopyRuntimeTuningFrom(_character);
            _character?.Dispose();
            _character = selectedAvatar;
            reusedSelectedAvatar = true;
            Console.WriteLine("[character] adopted cached character-select avatar");
        }

        _glue?.Dispose(); _glue = null;
        _booth?.Dispose(); _booth = null;
        ApplyServerCharacter(rebuild: !reusedSelectedAvatar);
        _worldEntryTransitionStage = 0;
        return true;
    }

    /// <summary>Turn the roster's 19 visible-item display ids into a dressable equipment set.</summary>
    private static CharacterEquipment BuildEquipment(Character c)
    {
        const uint HideHelm = 0x400, HideCloak = 0x800;   // CHARACTER_FLAG_HIDE_HELM / _HIDE_CLOAK
        var kit = new CharacterEquipment();
        for (int i = 0; i < c.Equipment.Length; i++)
        {
            var eq = c.Equipment[i];
            if (eq.DisplayId == 0) continue;
            int inv = eq.InventoryType;
            if (inv == CharacterEquipment.Slot.Head && (c.Flags & HideHelm) != 0) continue;
            if (inv == CharacterEquipment.Slot.Cloak && (c.Flags & HideCloak) != 0) continue;
            kit.Add($"slot{i}", eq.DisplayId, inv, i);
        }
        return kit;
    }

    /// <summary>ChrRaces id -> character model folder name (Undead's folder is "Scourge").</summary>
    private static string RaceFolder(byte race) => race switch
    {
        1 => "Human", 2 => "Orc", 3 => "Dwarf", 4 => "NightElf",
        5 => "Scourge", 6 => "Tauren", 7 => "Gnome", 8 => "Troll", _ => "Human"
    };

    /// <summary>Draw the login glue scene (UI_MainMenu) behind the glue UI. Called from Render(), networked + pre-world only.</summary>
    private void DrawGlueScene()
    {
        if (_glue is not { Ok: true } || _gl is null) return;
        if (_worldLoadStarted) return;
        // The front door renders the same UI_MainMenu scene with no net at all.
        if (!GlueFrontDoorActive)
        {
            if (!_config.Server.Enabled || _net is null) return;
            if (_net.State == NetState.CharacterSelect) return;   // the per-race booth draws here instead
        }

        Span<int> vp = stackalloc int[4];
        _gl.GetInteger(GetPName.Viewport, vp);
        int w = vp[2] > 0 ? vp[2] : _config.Window.Width;
        int h = vp[3] > 0 ? vp[3] : _config.Window.Height;
        _glue.Render(w, h);
    }

    /// <summary>
    /// Draw the character-select per-race booth (the UI_&lt;Race&gt; background) fullscreen, behind the
    /// roster chrome. Called from Render() right after DrawGlueScene(); CharacterSelect only. The
    /// race shown follows the SELECTED roster entry (Orc placeholder for an empty account), so
    /// clicking a different character swaps the background to that character's race scene.
    /// </summary>
    private void DrawCharacterSelectScene()
    {
        if (_booth is null || _gl is null) return;
        if (!_config.Server.Enabled || _net is null || _worldLoadStarted) return;
        if (_net.State != NetState.CharacterSelect) return;

        if (_charCreateOpen)
        {
            // Drive the booth from the create selection: race scene + live preview dressed in the
            // (race,class,sex) starting outfit (CharStartOutfit.dbc).
            _booth.SetCreateLook(_cc.Race, _cc.Sex, _cc.Class, _cc.Dials[0], _cc.Dials[1], _cc.Dials[2], _cc.Dials[3], _cc.Dials[4], BuildStartOutfit());
        }
        else
        {
            var chars = _net.Characters;
            if (chars.Count > 0)
            {
                if (_selectedChar >= chars.Count) _selectedChar = 0;
                _booth.SetCharacter(chars[_selectedChar]);   // switch background + build the dressed model
            }
            else
            {
                _booth.ShowPlaceholder();
            }
        }

        Span<int> vp = stackalloc int[4];
        _gl.GetInteger(GetPName.Viewport, vp);
        int w = vp[2] > 0 ? vp[2] : _config.Window.Width;
        int h = vp[3] > 0 ? vp[3] : _config.Window.Height;
        _booth.Render(w, h);
    }

    /// <summary>Draw streamed NPCs and remote players. Called from the in-world render pass.</summary>
    private void DrawCreatures()
    {
        if (_creatures is null) return;
        if (!_creatorWorldRequested && (_net is null || !_net.IsInWorld)) return;
        _creatures.SelfPlayerGuid = LocalPlayerGuid;
        _creatures.Render(_window.Camera, _entities);
    }

    // ── Glue screens: Login -> Character Select -> in-world status ──────────────────────────────

    /// <summary>Called from Gui(). Draws whichever glue/status screen matches the connection state.</summary>
    private void NetHud()
    {
        // Creator sandbox owns the world: its own overlay, no net states involved.
        if (_creatorWorldRequested) { DrawCreatorHud(); return; }

        // The glue front door: the login screen doubles as the launch menu, even
        // with no network client at all. Enter World commits the chosen mode.
        if (GlueFrontDoorActive) { DrawLoginScreen(); DrawGlueTuning(); return; }

        if (!_config.Server.Enabled || _net is null) return;

        NetState st = _net.State;
        if (st == NetState.InWorld)
        {
            DrawCombatHud();
            if (_config.DevTools && !_uiParityArmed && !PlayerPanelOpen) DrawInWorldPanel();
            return;
        }

        // The login screen is full-bleed glue chrome and owns its own full-screen window; the
        // connecting / character-select dialogs stay as centered panels for now.
        if (st is NetState.Idle or NetState.Failed or NetState.Disconnected) { DrawLoginScreen(); DrawGlueTuning(); return; }

        // Character select is full-bleed skinned chrome (its own window), like the login. The create
        // screen (Program.CharCreate.cs) is a client-side overlay on the same parked net state.
        if (st == NetState.CharacterSelect)
        {
            if (_charCreateOpen) { DrawCharacterCreate(); DrawCreateTuning(); } else DrawCharacterSelect();
            DrawBoothTuning();
            return;
        }

        ImGui.SetNextWindowPos(ImGui.GetIO().DisplaySize * 0.5f, ImGuiCond.Always, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new Vector2(460, 0), ImGuiCond.Always);
        DrawConnecting();
    }

    // The glue canvas the login layout is authored in (Blizzard's 1024x768 UI space). Every position
    // below is in these units, scaled to the window by (height / 768) and anchored to a screen edge -
    // exactly how the real client's UIParent lays the login out. Numbers transcribed from benilla
    // login/screen.rs (which read them from AccountLogin.xml); the cosmetic side/bottom buttons
    // benilla cut are placed by eye and want a screenshot pass.
    private const float GlueCanvasH = 768f;

    private void DrawLoginScreen()
    {
        var io = ImGui.GetIO();
        Vector2 disp = io.DisplaySize;
        float s = MathF.Max(disp.Y / GlueCanvasH, 0.5f);   // glue scales with height, letterboxes width
        float cx = disp.X * 0.5f;

        if (!_loginInit)
        {
            WriteBuf(_acctBuf, _config.Server.Account ?? "");
            _rememberAccount = !string.IsNullOrEmpty(_config.Server.Account);
            _loginInit = true;
        }

        // Full-screen, transparent, input-catching window kept at the back (NoBringToFrontOnFocus)
        // so the dev HUD / settings modal sit over it. The glue widgets hit-test inside it.
        ImGui.SetNextWindowPos(Vector2.Zero, ImGuiCond.Always);
        ImGui.SetNextWindowSize(disp, ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove
                  | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse
                  | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoBackground
                  | ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoNavFocus
                  | ImGuiWindowFlags.NoSavedSettings;
        bool open = ImGui.Begin("##glue-login", flags);
        ImGui.PopStyleVar();
        if (!open) { ImGui.End(); return; }

        var dl = ImGui.GetWindowDrawList();
        float savedScale = _skin?.Scale ?? 1f;
        if (_skin is not null) _skin.Scale = s;   // glue widgets scale with the login canvas

        // Static art: the WoW logo (TOPLEFT 3,7 at 256x128) and the Blizzard logo (BOTTOM 0,8 100x100).
        if (_skin is not null)
        {
            _skin.GlueImage(dl, "glue.logo", new Vector2(3f, 7f) * s, new Vector2(259f, 135f) * s);
            float bw = 100f * s, bh = 100f * s;
            var bmin = new Vector2(cx - bw * 0.5f, disp.Y - 8f * s - bh);
            _skin.GlueImage(dl, "glue.blizz", bmin, bmin + new Vector2(bw, bh));
        }

        // Version (BOTTOMLEFT) + copyright (BOTTOM), both GlueFontNormalSmall gold.
        GlueText(dl, "Version 1.12.1 (5875) (Release)", 4f * s, disp.Y - 34f * s, 12f * s, WowSkin.GlueGold, 0);
        GlueText(dl, "Sep 19 2006", 4f * s, disp.Y - 19f * s, 12f * s, WowSkin.GlueGold, 0);
        GlueText(dl, "Copyright 2004-2006  Blizzard Entertainment. All Rights Reserved.",
                 cx, disp.Y - 17f * s, 12f * s, WowSkin.GlueGold, 1);

        // Creator mode and serverless client mode have no account. Serverless
        // shows BOTH destinations as their own buttons - clicking one IS the
        // mode selection (saved sticky), so there is no way to land in the
        // wrong world. Networked client mode keeps the account + password
        // fields (160x37, bottom-anchored 345 / 270, centered; password Enter
        // submits) and the Login button.
        bool creatorMode = CreatorLaunchActive;
        bool serverless = !_config.Server.Enabled;
        var loginSize = new Vector2(170f * s, 45f * s * GlueTune.ButtonHeightMul);
        if (serverless)
        {
            // No server configured: creator mode IS the offline world, so one
            // button covers it (the separate offline viewer was redundant).
            // Hidden while the Launch Options modal is up — its top edge pokes
            // into the modal's bottom border and would steal those clicks.
            if (!_launchMenuOpen)
            {
                var bigSize = new Vector2(230f * s, 45f * s * GlueTune.ButtonHeightMul);
                ImGui.SetCursorScreenPos(new Vector2(cx - bigSize.X * 0.5f, 505f * s));
                if (_skin?.GlueButton("Enter Creator Mode", bigSize) ?? ImGui.Button("Enter Creator Mode", bigSize))
                {
                    SetLaunchMode(LaunchModeCreator);
                    EnterOfflineWorld();
                }
            }
        }
        else if (creatorMode)
        {
            if (!_launchMenuOpen)   // the Launch Options modal sits over this spot
            {
                GlueText(dl, "Creator Mode", cx, disp.Y - 320f * s, 15f * s, WowSkin.GlueGold, 1);
                GlueText(dl, "The offline sandbox: spells, characters, gear and world tools.",
                         cx, disp.Y - 296f * s, 12f * s, WowSkin.Muted, 1);
            }
            var bigSize = new Vector2(230f * s, 45f * s * GlueTune.ButtonHeightMul);
            ImGui.SetCursorScreenPos(new Vector2(cx - bigSize.X * 0.5f, 519f * s));
            if (_skin?.GlueButton("Enter Creator Mode", bigSize) ?? ImGui.Button("Enter Creator Mode", bigSize))
                EnterOfflineWorld();
        }
        else if (!_launchMenuOpen)
        {
            // The account/password fields and Login button sit exactly where the Launch
            // Options modal draws. They are real ImGui items: left visible under the
            // modal they EAT its clicks wherever the rects overlap (the "Creator Mode"
            // button's centre lands on the account box — clicking it did nothing), and
            // their foreground text bled through the parchment. While the modal is up,
            // this whole cluster simply does not exist.
            float boxW = 160f * s, boxH = 37f * s;
            LoginField(dl, "Account Name", "##acct", _acctBuf, cx, disp.Y - 345f * s, boxW, boxH, s, false, out _);
            LoginField(dl, "Account Password", "##pass", _passBuf, cx, disp.Y - 270f * s, boxW, boxH, s, true, out bool submit);

            // Login (170x45, TOP 519, centered). Height is live-tunable (grows downward from 519).
            ImGui.SetCursorScreenPos(new Vector2(cx - loginSize.X * 0.5f, 519f * s));
            bool loginClick = _skin?.GlueButton("Login", loginSize) ?? ImGui.Button("Login", loginSize);
            if (loginClick || submit)
            {
                string a = BufToString(_acctBuf), p = BufToString(_passBuf);
                if (a.Length > 0 && p.Length > 0 && _net is not null)
                {
                    _config.Server.Account = _rememberAccount ? a : "";
                    _net.Login(a, p);
                }
            }
            if (_net is { State: NetState.Failed } failedNet && !string.IsNullOrEmpty(failedNet.Status))
                GlueText(dl, failedNet.Status, cx, 519f * s + loginSize.Y + 6f * s, 12f * s, new Vector4(1f, 0.5f, 0.4f, 1f), 1);
        }

        // Remember Account Name checkbox (at 17,653). Box + label sizes are live-tunable - the label
        // needs an explicit glue size or it renders at the tiny ambient font next to the s-scaled box.
        if (_skin is not null && !creatorMode && !serverless)
        {
            ImGui.SetCursorScreenPos(new Vector2(17f * s, 653f * s));
            _skin.CheckBox("Remember Account Name", ref _rememberAccount,
                           GlueTune.CheckBoxUnits, GlueTune.CheckLabelUnits * s);
        }

        // The cosmetic main-menu buttons. Positions are by eye (benilla cut these) - screenshot pass.
        // Left column sits ABOVE the Remember checkbox (canvas top 653) so it never clips it:
        // Manage Account 565, Community Site 607 -> Community's bottom (~641) clears the checkbox.
        var small = new Vector2(150f * s, 34f * s * GlueTune.ButtonHeightMul);
        float gap = small.Y + 6f * s;
        float rightX = disp.X - 24f * s - small.X;
        GlueMenuButton("Cinematics", new Vector2(rightX, 300f * s), small);
        GlueMenuButton("Credits", new Vector2(rightX, 300f * s + gap), small);
        GlueMenuButton("Terms of Use", new Vector2(rightX, 300f * s + 2f * gap), small);
        float leftX = 17f * s;
        GlueMenuButton("Manage Account", new Vector2(leftX, 565f * s), small);
        GlueMenuButton("Community Site", new Vector2(leftX, 607f * s), small);

        // Launch Options - the one wired menu button: what does this client boot into?
        ImGui.SetCursorScreenPos(new Vector2(rightX, 300f * s + 3f * gap));
        if (_skin?.GlueButton("Launch Options", small) == true)
            _launchMenuOpen = !_launchMenuOpen;

        // The realm line (opens the realm modal in Stage B) and Quit (150x38, BOTTOMRIGHT 5,29).
        var quitSize = new Vector2(150f * s, 38f * s * GlueTune.ButtonHeightMul);
        float quitTop = disp.Y - 29f * s - quitSize.Y;
        if (!creatorMode && !serverless)
        {
            string realm = RealmDisplayName();   // the realm NAME once connected, else configured name, else host:port
            GlueText(dl, "Realm", disp.X - 6f * s, quitTop - 46f * s, 11f * s, WowSkin.Muted, 2);
            float rScale = ImGui.GetFontSize() > 0f ? (13f * s / ImGui.GetFontSize()) : 1f;
            Vector2 rSz = ImGui.CalcTextSize(realm) * rScale;
            var rPos = new Vector2(disp.X - 6f * s - rSz.X, quitTop - 32f * s);
            ImGui.SetCursorScreenPos(rPos);
            ImGui.InvisibleButton("##realm", new Vector2(MathF.Max(rSz.X, 40f), MathF.Max(rSz.Y, 12f * s)));
            // Stage B wires this click to the realm-select modal; for now it's a hover affordance.
            GlueText(dl, realm, disp.X - 6f * s, quitTop - 32f * s, 13f * s,
                     ImGui.IsItemHovered() ? WowSkin.Highlight : WowSkin.GlueGold, 2);
        }

        ImGui.SetCursorScreenPos(new Vector2(disp.X - 5f * s - quitSize.X, quitTop));
        if (_skin?.GlueButton("Quit", quitSize) ?? ImGui.Button("Quit", quitSize))
            _quitRequested = true;

        // Dev: a small top-right toggle for the live tuning modal (clear of the logo at top-left).
        ImGui.SetCursorScreenPos(new Vector2(disp.X - 58f * s, 6f * s));
        if (ImGui.InvisibleButton("##glue-tune-toggle", new Vector2(52f * s, 18f * s)))
            _glueTuneOpen = !_glueTuneOpen;
        GlueText(dl, "tune", disp.X - 6f * s, 6f * s, 12f * s,
                 (_glueTuneOpen || ImGui.IsItemHovered()) ? WowSkin.Highlight : WowSkin.Muted, 2);

        // The Launch Options modal draws last so it sits over the rest of the login chrome.
        DrawLaunchOptionsMenu(dl, s);

        if (_skin is not null) _skin.Scale = savedScale;
        ImGui.End();
    }

    private bool _glueTuneOpen;

    /// <summary>
    /// The live login-tuning modal - the same skinned-ImGui approach as the in-game options modal.
    /// Every slider writes straight into GlueTune, which the glue widgets read next frame, so the
    /// login updates as the slider moves. "Log values" prints the current set to the console so a
    /// dialed-in look can be read off and baked into GlueTune's defaults. Toggled by the small "tune"
    /// text at the login's top-right; rendered from NetHud right after the login screen.
    /// </summary>
    private void DrawGlueTuning()
    {
        if (!_glueTuneOpen) return;

        ImGui.SetNextWindowSize(new Vector2(380f, 0f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowPos(new Vector2(48f, 48f), ImGuiCond.FirstUseEver);
        _skin?.PushStyle();
        // PushStyle makes WindowBg transparent (the in-game frames paint their own backdrop); this is
        // a plain window, so give it an opaque dark panel so the sliders are readable over the scene.
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.05f, 0.04f, 0.03f, 0.96f));
        bool open = _glueTuneOpen;
        if (ImGui.Begin("Glue Login Tuning", ref open, ImGuiWindowFlags.NoCollapse))
        {
            ImGui.TextWrapped("Drag this panel aside by its title bar - it is covering the Login button. "
                            + "Values apply live as you drag a slider.");
            ImGui.Spacing();
            ImGui.TextDisabled("'Caption shadow' is the drop shadow behind the Login text.");
            ImGui.TextDisabled("Hover the button (text turns white) to see it most clearly.");
            ImGui.Spacing();

            ImGui.TextDisabled("BUTTONS");
            ImGui.SliderFloat("Height x", ref GlueTune.ButtonHeightMul, 0.8f, 1.8f);
            ImGui.SliderFloat("Caption size", ref GlueTune.CaptionSizeRatio, 0.25f, 0.65f);
            ImGui.SliderFloat("Caption lift", ref GlueTune.CaptionLift, 0f, 0.30f);
            ImGui.SliderFloat("Caption shadow", ref GlueTune.CaptionShadowRatio, 0f, 0.14f);
            ImGui.SliderFloat("Hover glow", ref GlueTune.HoverGlow, 0f, 4f);
            ImGui.SliderFloat("Text outline", ref GlueTune.OutlinePx, 0f, 0.14f);
            ImGui.Spacing();

            ImGui.TextDisabled("SHADOW (labels, fields, checkbox)");
            ImGui.SliderFloat("Strength", ref GlueTune.ShadowAlpha, 0.30f, 1.00f);
            ImGui.SliderFloat("Offset", ref GlueTune.ShadowOffsetRatio, 0.02f, 0.20f);
            ImGui.Spacing();

            ImGui.TextDisabled("FIELDS");
            ImGui.SliderFloat("Label size", ref GlueTune.FieldLabelUnits, 10f, 30f);
            ImGui.SliderFloat("Typed text size", ref GlueTune.TypedTextUnits, 10f, 30f);
            ImGui.Spacing();

            ImGui.TextDisabled("REMEMBER CHECKBOX");
            ImGui.SliderFloat("Box size", ref GlueTune.CheckBoxUnits, 12f, 40f);
            ImGui.SliderFloat("Label size##chk", ref GlueTune.CheckLabelUnits, 8f, 30f);
            ImGui.Spacing();

            ImGui.TextDisabled("GLUE SCENE");
            ImGui.SliderFloat("Ember size", ref GlueTune.ParticleSize, 0.25f, 3f);
            ImGui.SliderFloat("Brazier size", ref GlueTune.BrazierSize, 0.25f, 3f);
            ImGui.Spacing();
            ImGui.Separator();

            if (ImGui.Button("Log values")) GlueTune.LogValues();
            ImGui.SameLine();
            if (ImGui.Button("Reset")) GlueTune.Reset();
            ImGui.SameLine();
            if (ImGui.Button("Close")) open = false;
        }
        ImGui.End();
        ImGui.PopStyleColor();
        _skin?.PopStyle();
        _glueTuneOpen = open;
    }

    /// <summary>A GlueFont label with a 1px shadow, scaled to sizePx. align: 0=left, 1=centre, 2=right (x is that edge).</summary>
    private static void GlueText(ImDrawListPtr dl, string text, float x, float y, float sizePx, Vector4 col, int align)
    {
        var font = ImGui.GetFont();
        float baseFs = ImGui.GetFontSize();
        float scale = baseFs > 0f ? sizePx / baseFs : 1f;
        Vector2 sz = ImGui.CalcTextSize(text) * scale;
        float left = align == 1 ? x - sz.X * 0.5f : align == 2 ? x - sz.X : x;
        var pos = new Vector2(left, y);
        // The 1.12 MasterFont drop shadow: black, down-right, scaled to the text size so it reads the
        // same at every glue font size. Strength (alpha) and offset are live-tunable via GlueTune.
        float so = MathF.Max(1f, MathF.Round(sizePx * GlueTune.ShadowOffsetRatio));
        dl.AddText(font, sizePx, pos + new Vector2(so, so), ImGui.ColorConvertFloat4ToU32(GlueTune.ShadowColor), text);
        WowSkin.OutlineText(dl, font, sizePx, pos, text);
        dl.AddText(font, sizePx, pos, ImGui.ColorConvertFloat4ToU32(col), text);
    }

    /// <summary>
    /// One AccountLogin edit box: a dark bordered box (benilla BOX_FILL/BOX_BORDER), a gold label
    /// centred ~9px above it, and an ImGui InputText inset 15px (AccountLogin.xml TextInsets). The
    /// box is bottom-anchored at boxBottomY and centred on cx. Password boxes mask + submit on Enter.
    /// </summary>
    private void LoginField(ImDrawListPtr dl, string label, string id, byte[] buf, float cx,
                            float boxBottomY, float boxW, float boxH, float s, bool password, out bool submitted)
    {
        var boxMin = new Vector2(cx - boxW * 0.5f, boxBottomY - boxH);
        var boxMax = new Vector2(cx + boxW * 0.5f, boxBottomY);
        // The field is the real AccountLogin.xml Backdrop: UI-Tooltip-Background tiled inside a
        // Glue-Tooltip-Border nine-slice (WowSkin.GlueEditBox) - the recessed frame, not a flat
        // dark rectangle. The InputText on top is frameless (every FrameBg state transparent, no
        // border), so there is no second rectangle inside it. Fallback to a plain box with no skin.
        // Tinted with AccountLogin's DEFAULT_TOOLTIP_COLOR (benilla BOX_FILL 0.09 / BOX_BORDER 0.8):
        // the UI-Tooltip-Background sheet is light, so at full white it read whitish-grey over the
        // bright valley; the near-black fill tint is what makes it the OG's dark recessed well.
        if (_skin is not null)
            _skin.DrawBackdrop(dl, boxMin, boxMax, WowSkin.GlueEditBox, WowSkin.GlueBoxFill, WowSkin.GlueBoxBorder);
        else
        {
            dl.AddRectFilled(boxMin, boxMax, ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.72f)));
            dl.AddRect(boxMin, boxMax, ImGui.ColorConvertFloat4ToU32(WowSkin.GoldDim));
        }
        GlueText(dl, label, cx, boxMin.Y - 22f * s, GlueTune.FieldLabelUnits * s, WowSkin.GlueGold, 1);

        // The typed line is GlueEditBoxFont (ARIALN 18, benilla EDIT_FONT_SIZE) - enlarge the window
        // font to that size for the InputText only, then restore it. Frame height is read AFTER the
        // scale so the text is vertically centred at its real height inside the 37px box.
        float typedPx = GlueTune.TypedTextUnits * s;
        float baseFs = ImGui.GetFontSize();
        float wfScale = baseFs > 0f ? typedPx / baseFs : 1f;
        ImGui.SetWindowFontScale(wfScale);

        float inset = 15f * s;
        float frameH = ImGui.GetFrameHeight();
        ImGui.SetCursorScreenPos(new Vector2(boxMin.X + inset, boxMin.Y + (boxH - frameH) * 0.5f));
        ImGui.SetNextItemWidth(boxW - inset - 6f * s);
        var clear = new Vector4(0f, 0f, 0f, 0f);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, clear);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, clear);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, clear);
        ImGui.PushStyleColor(ImGuiCol.Text, WowSkin.Normal);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 0f);
        var iflags = ImGuiInputTextFlags.EnterReturnsTrue | (password ? ImGuiInputTextFlags.Password : ImGuiInputTextFlags.None);
        submitted = ImGui.InputText(id, buf, (uint)buf.Length, iflags);
        ImGui.PopStyleVar();
        ImGui.PopStyleColor(4);
        ImGui.SetWindowFontScale(1f);
    }

    /// <summary>A cosmetic main-menu glue button placed at an absolute screen pos. Draws + hovers; click
    /// is a no-op for now (Cinematics / Credits / URLs get wired later). No-op with no glue art.</summary>
    private void GlueMenuButton(string label, Vector2 pos, Vector2 size)
    {
        if (_skin is null) return;
        ImGui.SetCursorScreenPos(pos);
        _skin.GlueButton(label, size);
    }

    /// <summary>
    /// The logon progress dialog. 1.12 runs the same states we do - connecting, authenticating,
    /// "Authentication Successful", retrieving the character list - and shows each in a GlueDialog:
    /// the riveted DialogFrame box (the same border the in-game menus use), a gold caption, and one
    /// Cancel button. This used to be a bare ImGui window with the raw enum name in it, which broke
    /// the illusion at exactly the moment the player is staring at the screen waiting.
    /// </summary>
    private void DrawConnecting()
    {
        var io = ImGui.GetIO();
        var disp = io.DisplaySize;
        float s = MathF.Max(disp.Y / 768f, 0.1f);

        ImGui.SetNextWindowPos(Vector2.Zero, ImGuiCond.Always);
        ImGui.SetNextWindowSize(disp, ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove
                  | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse
                  | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoBackground
                  | ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoNavFocus
                  | ImGuiWindowFlags.NoSavedSettings;
        bool open = ImGui.Begin("##glue-connecting", flags);
        ImGui.PopStyleVar();
        if (!open) { ImGui.End(); return; }

        var dl = ImGui.GetWindowDrawList();
        float savedScale = _skin?.Scale ?? 1f;
        if (_skin is not null) _skin.Scale = s;

        string caption = LogonCaption(_net!.State);
        float w = GlueTune.LogonBoxW * s, h = GlueTune.LogonBoxH * s;
        var min = new Vector2((disp.X - w) * 0.5f, (disp.Y - h) * 0.5f + GlueTune.LogonBoxDY * s);
        var max = min + new Vector2(w, h);

        if (_skin is not null)
            _skin.DrawBackdrop(dl, min, max, WowSkin.Dialog);
        else
            dl.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.85f)));

        float cx = min.X + w * 0.5f;
        GlueText(dl, caption, cx, min.Y + 18f * s, GlueTune.LogonTitlePx * s, WowSkin.GlueGold, 1);
        if (!string.IsNullOrWhiteSpace(_net.Status))
            GlueText(dl, _net.Status, cx, min.Y + 18f * s + GlueTune.LogonTitlePx * s + 8f * s,
                     GlueTune.LogonStatusPx * s, WowSkin.Normal, 1);

        var btn = new Vector2(GlueTune.LogonBtnW * s, GlueTune.LogonBtnH * s);
        ImGui.SetCursorScreenPos(new Vector2(cx - btn.X * 0.5f, max.Y - btn.Y - 16f * s));
        bool cancel = _skin?.GlueButton("Cancel", btn) ?? ImGui.Button("Cancel", btn);
        if (cancel) { _net.Stop(); Array.Clear(_passBuf); }

        if (_skin is not null) _skin.Scale = savedScale;
        ImGui.End();
    }

    /// <summary>The 1.12 GlueDialog captions for the logon states we actually pass through.</summary>
    private static string LogonCaption(NetState s) => s switch
    {
        NetState.ConnectingRealm => "Connecting",
        NetState.Authenticating => "Authentication Successful",
        NetState.ConnectingWorld => "Connecting",
        NetState.EnteringWorld => "Entering World",
        _ => "Connecting",
    };

    private bool _boothTuneOpen;
    private ulong _deleteConfirmGuid;   // non-zero while the delete confirmation is up
    private string _deleteConfirmName = "";
    private byte _deleteConfirmLevel;
    private byte _deleteConfirmClass;
    private readonly byte[] _deleteConfirmText = new byte[33];
    private bool _deleteConfirmFocus;

    private void OpenDeleteConfirm(Character character)
    {
        _deleteConfirmGuid = character.Guid;
        _deleteConfirmName = character.Name;
        _deleteConfirmLevel = character.Level;
        _deleteConfirmClass = character.Class;
        Array.Clear(_deleteConfirmText);
        _deleteConfirmFocus = true;
    }

    private void CloseDeleteConfirm()
    {
        _deleteConfirmGuid = 0;
        _deleteConfirmName = "";
        _deleteConfirmLevel = 0;
        _deleteConfirmClass = 0;
        Array.Clear(_deleteConfirmText);
        _deleteConfirmFocus = false;
    }

    /// <summary>
    /// The 1.12 character-select 2D chrome, full-bleed over the 3D booth - the same skinned-ImGui
    /// approach as the login (WowSkin logo / tinted tooltip backdrop / GlueButton / GlueText), scaled
    /// to a 1024x768 glue canvas by s = height/768. Layout from SYSTEM_CHARACTER_SELECT.md section 4
    /// (benilla char_select/screen.rs + CharacterSelect.xml): WoW logo top-left; the selected name over
    /// the model; Enter World + a rotate pair bottom-centre; Back + Delete bottom-right; a right-column
    /// frame holding the realm banner, a disabled Change Realm, up to ten character rows, and Create
    /// New Character. Realm select, the delete dialog, the create screen and AreaTable zone names are
    /// later phases (those buttons are present-but-disabled for now).
    /// </summary>
    private void DrawCharacterSelect()
    {
        var io = ImGui.GetIO();
        Vector2 disp = io.DisplaySize;
        float s = MathF.Max(disp.Y / GlueCanvasH, 0.5f);
        var chars = _net!.Characters;
        if (chars.Count > 0 && _selectedChar >= chars.Count) _selectedChar = 0;
        if (!_charSelectionRestored && chars.Count > 0)
        {
            _charSelectionRestored = true;
            ulong remembered = Settings.LastCharacterGuid;
            int rememberedIndex = -1;
            if (remembered != 0)
                for (int i = 0; i < chars.Count; i++)
                    if (chars[i].Guid == remembered) { rememberedIndex = i; break; }
            if (rememberedIndex >= 0) _selectedChar = rememberedIndex;
        }

        // A just-created character: select its row once the refreshed roster arrives.
        if (_ccArmName is not null)
            for (int i = 0; i < chars.Count; i++)
                if (string.Equals(chars[i].Name, _ccArmName, StringComparison.OrdinalIgnoreCase))
                { _selectedChar = i; _ccArmName = null; break; }

        // AreaTable.dbc once (zone names for the roster rows); best-effort, a miss just hides the zone.
        if (!_areasLoaded)
        {
            _areasLoaded = true;
            try
            {
                var atBytes = AdtTerrainReader.ReadFileFromMpqs(_config.ClientDataPath, AreaTableCatalog.MpqPath);
                if (atBytes is not null) _areas = AreaTableCatalog.Parse(atBytes);
            }
            catch (Exception e) { Console.WriteLine($"[glue] AreaTable load failed: {e.Message}"); }
        }

        ImGui.SetNextWindowPos(Vector2.Zero, ImGuiCond.Always);
        ImGui.SetNextWindowSize(disp, ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove
                  | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse
                  | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoBackground
                  | ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoNavFocus
                  | ImGuiWindowFlags.NoSavedSettings;
        bool open = ImGui.Begin("##glue-charselect", flags);
        ImGui.PopStyleVar();
        if (!open) { ImGui.End(); return; }

        var dl = ImGui.GetWindowDrawList();
        float savedScale = _skin?.Scale ?? 1f;
        if (_skin is not null) _skin.Scale = s;
        // Capture the glue font + atlas for the after-render GL text pass (valid only in-frame).
        _glueAdd?.SetGlueFont(ImGui.GetFont(), (uint)ImGui.GetIO().Fonts.TexID.ToInt64());

        ulong enterGuid = 0;

        // WoW logo, TOPLEFT (3,7) 256x128 - same asset/placement as the login.
        _skin?.GlueImage(dl, "glue.logo", new Vector2(3f, 7f) * s, new Vector2(259f, 135f) * s);

        // Right-column character frame: 260x642 at TOPRIGHT (-5,-15). The tinted Glue-Tooltip backdrop
        // is the SAME mechanism the login edit boxes use (WowSkin.DrawBackdrop with the box tints).
        float frameW = 260f * s, frameH = 642f * s;
        var frameMin = new Vector2(disp.X - 5f * s - frameW, 15f * s);
        var frameMax = frameMin + new Vector2(frameW, frameH);

        // Row geometry is needed BEFORE the panel is drawn, because the lit row's highlight card cuts a
        // hole in the panel fill (see below). These are the same numbers the row loop uses further down.
        float rowX = frameMin.X + 12f * s, rowW = frameW - 24f * s;
        float rowH = 54f * s, pitch = 60f * s, rowsTop = frameMin.Y + 74f * s;
        int rows = Math.Min(chars.Count, 10);
        int litSel = _selectedChar;

        // Which row the mouse is over, resolved HERE rather than from ImGui.IsItemHovered in the loop:
        // the panel is drawn before the row buttons exist, and the hole and the glow must agree on the
        // same frame. IsWindowHovered keeps a window on top (the tuning modal) from stealing the
        // highlight, exactly as IsItemHovered would. The rows' InvisibleButtons still own the clicking;
        // this is a read-only hit test and changes no ImGui state.
        int hoverRow = -1;
        if (ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows))
        {
            var mp = io.MousePos;
            for (int i = 0; i < rows; i++)
            {
                float rt = rowsTop + i * pitch;
                if (mp.X >= rowX && mp.X <= rowX + rowW && mp.Y >= rt && mp.Y <= rt + rowH) { hoverRow = i; break; }
            }
        }

        // THE ROW-TEXT LAYERING FIX. benilla order is panel -> glow -> TEXT: the row text sits in FRONT
        // of the additive highlight card. The card can only be a dedicated GL pass (an ImGui blend
        // callback tears the Silk render loop down), and re-drawing the text over an on-top card needs a
        // SECOND ImGui frame - which destroys ImGui's cross-frame hover/click tracking and is exactly
        // what made this whole screen unclickable. So neither the text nor the card moves: the PANEL
        // does. The glow composites UNDER the single ImGui pass (text in front for free, interaction
        // untouched); the panel would dim it from up there, so the panel FILL moves down with it - it is
        // drawn in the GL pass, from the panel's own tiled art (BackdropFillSlice -> EnqueueBlend), and
        // ImGui draws only the nine-sliced EDGE over the top. The panel looks exactly as before: only the
        // layer order changed. One ImGui frame, one GL pass, no interaction change.
        //
        // AND IT IS THE WHOLE FILL, NOT A BAND. The first cut punched a hole in the ImGui fill just
        // behind each lit card and patched that band into the GL pass. It worked, but left a hairline
        // under the lower card: a band edge is a seam between two rasterisers (ImGui clips with an
        // integer glScissor; the GL quad rasterises by its own pixel-centre rule) and snapping the edge
        // to whole pixels did not reliably close it. Moving the ENTIRE fill leaves no internal boundary
        // to misalign - the fill is one continuous stretch of the same tiled art it always was.
        var rosterFill = WowSkin.GlueBoxFill;
        rosterFill.W *= GlueTune.RosterAlpha;          // lower = more cobblestone through
        List<Vector2>? hiHoles = null;
        bool panelUnderGlow = !GlueTune.SelectHiOnTop && GlueTune.SelectHiPanelHole
                              && _skin is not null && _glueAdd is not null
                              && _skin.Has("glue.select.hi");
        if (panelUnderGlow
            && _skin!.BackdropFillSlice(frameMin, frameMax, WowSkin.GlueEditBox, frameMin.Y, frameMax.Y,
                                        out uint bgTex, out var pMin, out var pMax,
                                        out var pUv0, out var pUv1))
        {
            _glueAdd!.EnqueueBlend(pMin, pMax, pUv0, pUv1, rosterFill, false, bgTex);
            hiHoles = [new Vector2(frameMin.Y, frameMax.Y)];   // ImGui: edge only, no fill at all
        }

        if (_skin is not null)
            _skin.DrawBackdrop(dl, frameMin, frameMax, WowSkin.GlueEditBox, rosterFill, WowSkin.GlueBoxBorder, hiHoles);
        else
            dl.AddRectFilled(frameMin, frameMax, ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.72f)));

        // Realm banner (the realm NAME from the realmlist, not host:port) + a DISABLED Change Realm.
        GlueText(dl, RealmDisplayName(), frameMin.X + frameW * 0.5f, frameMin.Y + 10f * s, 14f * s, WowSkin.GlueGold, 1);
        // Change Realm: width = the panel minus an inset each side, so it stays centred in the column
        // however the panel is resized. Height and both offsets are dials (they were 12 / 32 / 30).
        float crInset = GlueTune.ChangeRealmInset * s;
        ImGui.SetCursorScreenPos(new Vector2(frameMin.X + crInset, frameMin.Y + GlueTune.ChangeRealmTop * s));
        _skin?.GlueButton("Change Realm", new Vector2(frameW - crInset * 2f, GlueTune.ChangeRealmH * s), enabled: false);

        // Up to ten character rows (name / "Level X Class" / zone). The 1.12 palette: the NAME stays
        // gold (GlueGold) whether selected or not - the bright-yellow highlight box IS the selection
        // cue, not a text-colour change; "Level X Class" is WHITE; the zone (AreaTable name) is a gray
        // third line, shown only when known. All keep the black drop shadow. The highlight box hugs the
        // content - it grows to include the zone line so a location never spills out the bottom.
        for (int i = 0; i < rows; i++)
        {
            Character c = chars[i];
            var rMin = new Vector2(rowX, rowsTop + i * pitch);
            ImGui.SetCursorScreenPos(rMin);
            if (ImGui.InvisibleButton($"##row{i}", new Vector2(rowW, rowH)))
            {
                _selectedChar = i;
                RememberCharacterSelection(c.Guid);
                if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) enterGuid = c.Guid;
            }
            // Use the pre-panel snapshot, not ImGui.IsItemHovered / the live _selectedChar: the panel
            // hole above was cut from exactly these two, and a lit card with no hole (or vice versa)
            // would flicker for a frame on click.
            bool selected = i == litSel, hovered = i == hoverRow;

            // The row highlight is the REAL 1.12 art - Interface\Glues\CharacterSelect\
            // Glue-CharacterSelect-Highlight.blp, a yellow glow authored on black for ADD blend (WowSkin
            // rebuilds its alpha from luma so an alpha draw only brightens, exactly as the login button
            // highlight does). benilla char_select/screen.rs draws it as ONE 256x74 card and refresh.rs:145
            // shows it for `lit = selected || hovered` - hover and the LOCKED selected row at the SAME
            // full brightness (the ref's LockHighlight). The soft glow edge (the "shadow" around the
            // yellow) is baked into the BLP; there is no rounded rect and no dimmer hover.
            bool lit = selected || hovered;
            if (lit)
            {
                var hiMin = new Vector2(frameMin.X + GlueTune.SelectHiInsetX * s, rMin.Y + GlueTune.SelectHiTop * s);
                var hiMax = new Vector2(frameMin.X + frameW - GlueTune.SelectHiInsetX * s, hiMin.Y + GlueTune.SelectHiHeight * s);
                if (_skin is not null && _skin.Has("glue.select.hi"))
                {
                    // TRUE additive (alphaMode="ADD") - the blend benilla's AddUiMaterial uses for this
                    // card (glue/add_material.rs, SrcAlpha/One). Enqueue the RAW (non-luma) copy of the
                    // BLP as an additive quad; GlueAdditive flushes it onto the framebuffer BEFORE the
                    // ImGui HUD pass, so the row text below draws in front of it. The panel would
                    // normally dim it from there, which is why its fill is cut away behind the card
                    // (hiHoles, above). SelectHi.W is the glow intensity, RGB the tint.
                    // Falls back to the straight-alpha translucent draw if the overlay/raw art is absent.
                    uint rawTex = _skin.TextureHandle("glue.select.hi.raw");
                    if (_glueAdd is not null && rawTex != 0)
                        _glueAdd.Enqueue(hiMin, hiMax, GlueTune.SelectHi, GlueTune.SelectHiGain, GlueTune.SelectHiContrast, GlueTune.SelectHiOnTop, rawTex);
                    else
                        _skin.GlueImage(dl, "glue.select.hi", hiMin, hiMax, GlueTune.SelectHi);
                }
                else   // art missing from the MPQs: a faint fallback so the selection stays legible
                    dl.AddRectFilled(hiMin, hiMax,
                        ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.87f, 0.12f, 0.30f)), 8f * s);
            }

            // Row text (benilla screen.rs row_button): NAME gold 15 (+5), INFO "Level X Class" white 12
            // (+24), LOCATION gray 12 (+38) - all carrying the glue black drop shadow via GlueText.
            GlueText(dl, c.Name, rMin.X + 10f * s, rMin.Y + 5f * s, 15f * s, WowSkin.GlueGold, 0);
            GlueText(dl, $"Level {c.Level} {ClassName(c.Class)}" + (c.IsGhost ? "  (dead)" : ""),
                     rMin.X + 10f * s, rMin.Y + 24f * s, 12f * s, WowSkin.Normal, 0);
            string zone = _areas?.ZoneName(c.Zone) ?? "";
            if (zone.Length > 0)
                GlueText(dl, zone, rMin.X + 10f * s, rMin.Y + 38f * s, 12f * s, WowSkin.Muted, 0);

            // The on-top glow draws OVER this row text. Queue it as GL TEXT (same additive pass, after
            // the glow, using the ImGui font atlas) so it redraws crisp IN FRONT of the glow - no second
            // ImGui frame (that broke interaction). benilla: the row text sits over the ADD card.
            if (lit && GlueTune.SelectHiOnTop && _glueAdd is not null)
            {
                _glueAdd.EnqueueText(c.Name, rMin.X + 10f * s, rMin.Y + 5f * s, 15f * s, WowSkin.GlueGold);
                _glueAdd.EnqueueText($"Level {c.Level} {ClassName(c.Class)}" + (c.IsGhost ? "  (dead)" : ""),
                                     rMin.X + 10f * s, rMin.Y + 24f * s, 12f * s, WowSkin.Normal);
                if (zone.Length > 0)
                    _glueAdd.EnqueueText(zone, rMin.X + 10f * s, rMin.Y + 38f * s, 12f * s, WowSkin.Muted);
            }
        }

        // Create New Character at the frame's bottom - opens the create screen (Program.CharCreate.cs).
        // Size and offsets were hardcoded (34 tall, inset 12, 12 up from the panel bottom); dials now,
        // with the width derived from the inset so it stays centred in the column at any panel width.
        float cnInset = GlueTune.CreateCharInset * s;
        ImGui.SetCursorScreenPos(new Vector2(frameMin.X + cnInset,
                                             frameMax.Y - (GlueTune.CreateCharH + GlueTune.CreateCharBottom) * s));
        if (_skin?.GlueButton("Create New Character", new Vector2(frameW - cnInset * 2f, GlueTune.CreateCharH * s),
                              enabled: true, captionPx: GlueTune.CreateCharTextPx * s) ?? false)
            OpenCharCreate();

        // Enter World (200x60 at BOTTOM 0,30), flanked by a rotate pair that spins the model while held.
        // Enter World: size and bottom margin are dials (they were 200 x 60 at 30 off the bottom).
        // ButtonHeightMul still rides on top so the shared glue-button proportion applies here too.
        var ewSize = new Vector2(GlueTune.EnterWorldW * s, GlueTune.EnterWorldH * s * GlueTune.ButtonHeightMul);
        float ewTop = disp.Y - GlueTune.EnterWorldBottom * s - ewSize.Y;

        // The selected character's NAME over the model, clearly ABOVE the Enter World button
        // (GlueFontNormalHuge; benilla centres it over the model, above the button).
        float nameY = ewTop - 30f * s - 16f * s;
        if (chars.Count > 0)
            GlueText(dl, chars[_selectedChar].Name, disp.X * 0.5f, nameY, 30f * s, WowSkin.GlueGold, 1);
        else
            GlueText(dl, "No characters on this account", disp.X * 0.5f, nameY, 20f * s, WowSkin.Muted, 1);

        ImGui.SetCursorScreenPos(new Vector2(disp.X * 0.5f - ewSize.X * 0.5f, ewTop));
        bool ewClick = _skin?.GlueButton("Enter World", ewSize, chars.Count > 0, GlueTune.EnterWorldTextPx * s)
                       ?? ImGui.Button("Enter World", ewSize);
        if (ewClick && chars.Count > 0) enterGuid = chars[_selectedChar].Guid;

        // The stylized rotate pair, UNDER Enter World and centred on it - the 1.12 placement, and the
        // same UI-RotationRight-Big art the create screen uses (RotateButton, shared partial). It used
        // to be two text glue buttons flanking Enter World, which is neither the art nor the position.
        float rot = GlueTune.RotateSize * s;
        float rotSpan = rot * 2f + GlueTune.RotateGap * s;
        float rotX = disp.X * 0.5f - rotSpan * 0.5f + GlueTune.RotateDX * s;
        float rotY = ewTop + ewSize.Y + GlueTune.RotateTop * s;
        RotateButton(dl, "##selRotL", true, new Vector2(rotX, rotY), rot);
        RotateButton(dl, "##selRotR", false, new Vector2(rotX + rot + GlueTune.RotateGap * s, rotY), rot);

        // Back (100x35 at BOTTOMRIGHT -30,25) and Delete Character (165x35 to its left; disabled for now).
        var backSize = new Vector2(100f * s, 35f * s * GlueTune.ButtonHeightMul);
        float backTop = disp.Y - 25f * s - backSize.Y;
        var delSize = new Vector2(165f * s, 35f * s * GlueTune.ButtonHeightMul);
        ImGui.SetCursorScreenPos(new Vector2(disp.X - 30f * s - backSize.X - 8f * s - delSize.X, backTop));
        bool canDelete = chars.Count > 0 && _selectedChar >= 0 && _selectedChar < chars.Count;
        if ((_skin?.GlueButton("Delete Character", delSize, canDelete) ?? false) && canDelete)
            OpenDeleteConfirm(chars[_selectedChar]);
        ImGui.SetCursorScreenPos(new Vector2(disp.X - 30f * s - backSize.X, backTop));
        if (_skin?.GlueButton("Back", backSize) ?? ImGui.Button("Back", backSize)) { _net.Stop(); Array.Clear(_passBuf); }

        // AddOns bottom-left (cosmetic/disabled), for OG parity.
        ImGui.SetCursorScreenPos(new Vector2(30f * s, backTop));
        _skin?.GlueButton("AddOns", new Vector2(100f * s, 35f * s * GlueTune.ButtonHeightMul), enabled: false);

        // Dev: the booth-tuning toggle (small "tune" at the top-right, clear of the logo).
        ImGui.SetCursorScreenPos(new Vector2(disp.X - 58f * s, 6f * s));
        if (ImGui.InvisibleButton("##booth-tune-toggle", new Vector2(52f * s, 18f * s)))
            _boothTuneOpen = !_boothTuneOpen;
        GlueText(dl, "tune", disp.X - 6f * s, 6f * s, 12f * s,
                 (_boothTuneOpen || ImGui.IsItemHovered()) ? WowSkin.Highlight : WowSkin.Muted, 2);

        // DRAG THE MODEL TO SPIN IT (benilla char_select/input.rs rotate_model; 1.12 lets you grab the
        // character anywhere on the scene). Registered LAST on purpose: ImGui gives hover to the FIRST
        // item that claims a position, so every real widget above - the rows, Enter World, the rotate
        // pair, Back, AddOns, the tune toggle - wins the hit test and this catcher only ever sees what
        // is left, i.e. the booth itself. It also stops at the roster panel's left edge, so dragging on
        // the panel does nothing, same as the reference. Left-drag only; a plain click does nothing.
        float dragW = MathF.Max(frameMin.X, 1f);
        if (_deleteConfirmGuid == 0)
        {
            ImGui.SetCursorScreenPos(Vector2.Zero);
            ImGui.InvisibleButton("##booth-drag", new Vector2(dragW, MathF.Max(disp.Y, 1f)));
        }
        // Sign: drag RIGHT turns the model's front toward the viewer's right, i.e. you push the shoulder
        // you grabbed. Same convention as the `>` button, which decreases the yaw.
        if (_deleteConfirmGuid == 0 && ImGui.IsItemActive() && io.MouseDelta.X != 0f)
            BoothTune.CharYawDegrees += io.MouseDelta.X * BoothTune.DragRotateDegPerPx;

        // Keep the facing in [-180,180] so the tuning slider stays meaningful after a long spin
        // (the drag and the </> buttons both accumulate freely).
        if (BoothTune.CharYawDegrees is > 180f or < -180f)
            BoothTune.CharYawDegrees -= 360f * MathF.Round(BoothTune.CharYawDegrees / 360f);

        // The delete confirmation (benilla char_select/dialog.rs): a GlueDialog over the roster, the
        // doomed character named in it, Accept/Cancel. The worker does the CMSG_CHAR_DELETE and
        // re-enumerates on success, so the row simply disappears.
        if (_deleteConfirmGuid != 0)
        {
            var doomed = chars.FirstOrDefault(c => c.Guid == _deleteConfirmGuid);
            if (doomed is null) CloseDeleteConfirm();
            else
            {
                float dw = 512f * s, dh = 256f * s;
                var dmin = new Vector2((disp.X - dw) * 0.5f, (disp.Y - dh) * 0.5f);
                var dmax = dmin + new Vector2(dw, dh);
                if (_skin is not null) _skin.DrawBackdrop(dl, dmin, dmax, WowSkin.Dialog);
                else dl.AddRectFilled(dmin, dmax, ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.9f)));

                float dcx = dmin.X + dw * 0.5f;
                GlueText(dl, "Do you want to delete", dcx, dmin.Y + 16f * s,
                    18f * s, WowSkin.GlueGold, 1);
                GlueText(dl,
                    $"{_deleteConfirmName}   Level {_deleteConfirmLevel}   {ClassName(_deleteConfirmClass)}?",
                    dcx, dmin.Y + 40f * s, 18f * s, WowSkin.Normal, 1);
                GlueText(dl, "Type \"DELETE\" into the field to confirm.", dcx,
                    dmin.Y + 83f * s, 12f * s, WowSkin.GlueGold, 1);

                ImGui.SetCursorScreenPos(new Vector2(dcx - 65f * s, dmin.Y + 102f * s));
                ImGui.SetNextItemWidth(130f * s);
                if (_deleteConfirmFocus)
                {
                    ImGui.SetKeyboardFocusHere();
                    _deleteConfirmFocus = false;
                }
                ImGui.InputText("##delete-confirm-text", _deleteConfirmText,
                    (uint)_deleteConfirmText.Length);
                bool armed = string.Equals(BufToString(_deleteConfirmText), "DELETE",
                    StringComparison.OrdinalIgnoreCase);
                bool cancelDelete = ImGui.IsKeyPressed(ImGuiKey.Escape);
                bool confirmDelete = armed && ImGui.IsKeyPressed(ImGuiKey.Enter);

                var dbtn = new Vector2(200f * s, 40f * s);
                ImGui.SetCursorScreenPos(new Vector2(dcx - dbtn.X - 6f * s,
                    dmax.Y - dbtn.Y - 16f * s));
                bool okay = _skin?.GlueButton("Okay", dbtn, armed) ??
                    (armed && ImGui.Button("Okay", dbtn));
                if ((okay && armed) || confirmDelete)
                {
                    _net.DeleteCharacter(_deleteConfirmGuid);
                    CloseDeleteConfirm();
                }
                ImGui.SetCursorScreenPos(new Vector2(dcx + 7f * s,
                    dmax.Y - dbtn.Y - 16f * s));
                if ((_skin?.GlueButton("Cancel", dbtn) ?? ImGui.Button("Cancel", dbtn)) ||
                    cancelDelete)
                    CloseDeleteConfirm();
            }
        }

        // Keep the selection inside the roster after a delete shrinks it.
        if (_net.TryTakeDeleteResult(out byte delCode))
        {
            Console.WriteLine($"[net] SMSG_CHAR_DELETE result 0x{delCode:X2}");
            _selectedChar = 0;
        }

        if (enterGuid != 0)
        {
            RememberCharacterSelection(enterGuid);
            if (_gl is not null)
            {
                Character? entering = chars.FirstOrDefault(c => c.Guid == enterGuid);
                ArmEnterWorldCurtain(_gl, entering is null ? _config.Start.Map : (int)entering.Map);

                // An automated Enter action can land on the first character-select frame,
                // before DrawCharacterSelectScene has built the selected booth avatar. Build it
                // now, behind the already-armed curtain, so enter-world can transfer that exact
                // renderer instead of falling back to an in-load equipment rebuild.
                if (entering is not null) _booth?.SetCharacter(entering);
            }
            _net.SelectCharacter(enterGuid);
        }
        if (_skin is not null) _skin.Scale = savedScale;
        ImGui.End();
    }

    private void RememberCharacterSelection(ulong guid)
    {
        if (guid == 0 || Settings.LastCharacterGuid == guid) return;
        Settings.LastCharacterGuid = guid;
        SettingsFile?.Save();
    }

    /// <summary>ClientWindow.OnOverlay: flush the queued additive glue quads (char-select row
    /// highlight, alphaMode=ADD) onto the framebuffer, beneath the ImGui HUD pass. No-op when nothing
    /// was queued (off the character-select screen).</summary>
    public void Overlay()    => _glueAdd?.Flush(ImGui.GetIO().DisplaySize, onTop: false);

    /// <summary>ClientWindow.OnOverlayTop: the "on top" additive glue quads, drawn over the HUD.</summary>
    public void OverlayTop()
    {
        _glueAdd?.Flush(ImGui.GetIO().DisplaySize, onTop: true);
        FinishGameplayDump();
        FinishUiParityCapture();
        FinishBagContainmentCapture();
    }


    /// <summary>The booth dev-tuning modal (fine-tune nudges on the scene-camera placement). Toggled by
    /// the "tune" text at char-select; the same skinned-window pattern as the login's DrawGlueTuning.</summary>
    private void DrawBoothTuning()
    {
        if (!_boothTuneOpen) return;

        ImGui.SetNextWindowSize(new Vector2(360f, 0f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowPos(new Vector2(48f, 48f), ImGuiCond.FirstUseEver);
        _skin?.PushStyle();
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.05f, 0.04f, 0.03f, 0.96f));
        bool open = _boothTuneOpen;
        if (ImGui.Begin("Character-Select Booth Tuning", ref open, ImGuiWindowFlags.NoCollapse))
        {
            ImGui.TextDisabled("Fine-tune nudges on the scene-camera placement.");
            ImGui.Spacing();
            ImGui.TextDisabled("Selected-row highlight (drag the RGBA / alpha bar):");
            ImGui.ColorEdit4("Row highlight (RGB tint, A = coverage)", ref GlueTune.SelectHi, ImGuiColorEditFlags.AlphaBar);
            ImGui.SliderFloat("Highlight brightness (ADD gain)", ref GlueTune.SelectHiGain, 0f, 6f);
            ImGui.SliderFloat("Highlight crispness (contrast)", ref GlueTune.SelectHiContrast, 1f, 5f);
            ImGui.Checkbox("Glow on top (over panel AND text - benilla draws it UNDER the text)", ref GlueTune.SelectHiOnTop);
            ImGui.Checkbox("Panel behind the glow, not over it (undimmed, text in front)", ref GlueTune.SelectHiPanelHole);
            if (GlueTune.SelectHiOnTop) ImGui.TextDisabled("   (ignored while 'Glow on top' is ticked)");
            ImGui.SliderFloat("Roster panel opacity", ref GlueTune.RosterAlpha, 0.2f, 1f);
            ImGui.Spacing();
            ImGui.TextDisabled("Character-select buttons:");
            ImGui.SliderFloat("Enter World width", ref GlueTune.EnterWorldW, 100f, 420f);
            ImGui.SliderFloat("Enter World height", ref GlueTune.EnterWorldH, 24f, 120f);
            ImGui.SliderFloat("Enter World bottom margin", ref GlueTune.EnterWorldBottom, 0f, 160f);
            ImGui.SliderFloat("Enter World text px (0 = auto)", ref GlueTune.EnterWorldTextPx, 0f, 48f);
            ImGui.SliderFloat("Change Realm height", ref GlueTune.ChangeRealmH, 16f, 80f);
            ImGui.SliderFloat("Change Realm top", ref GlueTune.ChangeRealmTop, 0f, 140f);
            ImGui.SliderFloat("Change Realm side inset", ref GlueTune.ChangeRealmInset, 0f, 60f);
            ImGui.SliderFloat("Create Character height", ref GlueTune.CreateCharH, 16f, 90f);
            ImGui.SliderFloat("Create Character bottom", ref GlueTune.CreateCharBottom, 0f, 120f);
            ImGui.SliderFloat("Create Character side inset", ref GlueTune.CreateCharInset, 0f, 60f);
            ImGui.SliderFloat("Create Character text px (0 = auto)", ref GlueTune.CreateCharTextPx, 0f, 40f);
            ImGui.SliderFloat("Rotate size", ref GlueTune.RotateSize, 16f, 90f);
            ImGui.SliderFloat("Rotate gap", ref GlueTune.RotateGap, -20f, 60f);
            ImGui.SliderFloat("Rotate X nudge", ref GlueTune.RotateDX, -200f, 200f);
            // Wide range on purpose: this is the pair's whole vertical travel, so it can sit well
            // above Enter World (negative) or far below it, not just "a gap under the button".
            ImGui.SliderFloat("Rotate Y offset (under Enter World)", ref GlueTune.RotateTop, -300f, 200f);
            ImGui.SliderFloat("Highlight inset X", ref GlueTune.SelectHiInsetX, -12f, 30f);
            ImGui.SliderFloat("Highlight top", ref GlueTune.SelectHiTop, -30f, 20f);
            ImGui.SliderFloat("Highlight height", ref GlueTune.SelectHiHeight, 36f, 100f);
            ImGui.Spacing();
            ImGui.SliderFloat("Model scale", ref BoothTune.CharScale, 0.2f, 3f);
            ImGui.SliderFloat("Vertical nudge", ref BoothTune.CharZOffset, -3f, 3f);
            ImGui.SliderFloat("Facing tweak deg", ref BoothTune.CharYawDegrees, -180f, 180f);
            ImGui.Checkbox("Auto-rotate", ref BoothTune.AutoRotate);
            ImGui.SameLine();
            ImGui.SliderFloat("deg/s", ref BoothTune.AutoRotateSpeed, 5f, 180f);
            ImGui.SliderFloat("Drag spin deg/px", ref BoothTune.DragRotateDegPerPx, 0.02f, 1.0f);
            ImGui.SliderFloat("Ambient (shadow)", ref BoothTune.AmbientIntensity, 0f, 2f);
            ImGui.SliderFloat("Sun (key)", ref BoothTune.SunIntensity, 0f, 3f);
            ImGui.SliderFloat("Sun warmth", ref BoothTune.CharSunWarmth, -0.5f, 0.5f);
            ImGui.SliderFloat("Shadow warmth", ref BoothTune.CharAmbientWarmth, -0.5f, 0.5f);
            ImGui.SliderFloat("Shadow softness", ref BoothTune.CharShadowSoftness, 0f, 1f);
            ImGui.SliderFloat("Key azimuth deg", ref BoothTune.CharKeyAzimuthDeg, -180f, 180f);
            ImGui.SliderFloat("Key elevation deg", ref BoothTune.CharKeyElevationDeg, 0f, 90f);
            ImGui.Spacing();
            ImGui.TextDisabled("Backdrop (scene mesh) - login unaffected");
            ImGui.SliderFloat("Scene brightness", ref BoothTune.SceneBrightness, 0.5f, 2.5f);
            ImGui.SliderFloat("Scene warmth", ref BoothTune.SceneWarmth, -0.2f, 0.5f);
            ImGui.TextDisabled("Sun fill (rides the scene's own sun; lights the floor)");
            ImGui.SliderFloat("Fill intensity", ref BoothTune.SunFillIntensity, 0f, 1.5f);
            ImGui.SliderFloat("Fill elevation", ref BoothTune.SunFillElevDeg, 0f, 90f);
            ImGui.SliderFloat("Fill azimuth offset", ref BoothTune.SunFillAzimOffsetDeg, -180f, 180f);
            ImGui.SliderFloat("Fill warmth", ref BoothTune.SunFillWarmth, -0.2f, 0.5f);
            ImGui.Spacing();
            if (ImGui.Button("Log booth values")) { BoothTune.LogValues(); GlueTune.LogValues(); }
            ImGui.SameLine();
            if (ImGui.Button("Reset booth")) BoothTune.Reset();
            ImGui.SameLine();
            if (ImGui.Button("Close")) open = false;
        }
        ImGui.End();
        ImGui.PopStyleColor();
        _skin?.PopStyle();
        _boothTuneOpen = open;
    }

    private void DrawInWorldPanel()
    {
        ImGui.SetNextWindowSize(new Vector2(390, 0), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Server", ImGuiWindowFlags.NoCollapse)) { ImGui.End(); return; }
        ImGui.TextUnformatted($"player: {_net!.PlayerName}   guid 0x{_net.PlayerGuid:X}");
        ImGui.TextUnformatted($"entities: {_entities.Count}  (creatures {_entities.CreatureCount}, players {_entities.PlayerCount})");
        ImGui.TextUnformatted($"packets in: {_netInbound}  (updates last frame {_netUpdatesLastFrame})");
        ImGui.TextUnformatted($"moving (splines): {_entities.MovingCount}");
        ImGui.TextUnformatted($"movement out: {_movementSender.PacketsSent}  " +
                              $"flags 0x{_movementSender.LastFlags:X}" +
                              (_movementSender.LastOpcode is { } moveOp ? $"  last {moveOp}" : ""));
        ImGui.TextUnformatted($"combat events: {_combat.ReceivedCount}  buffered {_combat.BufferedCount}  " +
                              $"engaged {_combat.EngagedCount}");
        ImGui.TextUnformatted($"target: hover 0x{_hoveredGuid:X}  selected 0x{_selectionGuid:X}  " +
                              $"attack 0x{_attackTargetGuid:X}");
        ImGui.TextUnformatted($"world combat text: {_worldCombatTextSpawned} spawned  " +
                              $"{_floatingCombatText.Count} active  {_worldCombatTextDropped} dropped  " +
                              $"center {_centerCombatText.Count}");
        if (_combat.LastEvent is { } lastCombat)
            ImGui.TextDisabled($"last combat: {lastCombat.GetType().Name}");
        if (_creatures is not null)
        {
            ImGui.TextUnformatted($"creatures drawn: {_creatures.DrawnLastFrame}" +
                                  (_creatures.Ok ? "" : "  (renderer off - creature DBCs missing)"));
            ImGui.TextUnformatted($"remote players drawn: {_creatures.PlayersDrawnLastFrame}");
            bool drawC = _creatures.Enabled;
            if (ImGui.Checkbox("Draw creatures", ref drawC)) _creatures.Enabled = drawC;
            float hoff = _creatures.HeadingOffsetDegrees;
            if (ImGui.SliderFloat("Creature heading deg", ref hoff, -180f, 180f)) _creatures.HeadingOffsetDegrees = hoff;
            float cscale = _creatures.ScaleMultiplier;
            if (ImGui.SliderFloat("Creature scale x", ref cscale, 0.1f, 3f)) _creatures.ScaleMultiplier = cscale;
            ImGui.TextUnformatted($"animated: {_creatures.AnimatedLastFrame}");
            ImGui.TextUnformatted($"combat anims: {_creatures.CombatActionsTriggered} triggered  " +
                                  $"{_creatures.CombatActionsActive} active" +
                                  (_character is null
                                      ? ""
                                      : $"  self {_character.CombatActionsTriggered} ({_character.CurrentAnimation})"));
            bool animC = _creatures.Animate;
            if (ImGui.Checkbox("Animate creatures", ref animC)) _creatures.Animate = animC;
            float adist = _creatures.AnimateDistance;
            if (ImGui.SliderFloat("Animate distance", ref adist, 20f, 300f)) _creatures.AnimateDistance = adist;
        }
        if (_controller is not null && ImGui.CollapsingHeader("Nearest units"))
        {
            Vector3 me = _controller.Position;
            foreach (var e in _entities.NearestUnits(me, 12))
                ImGui.TextUnformatted(
                    $"{Vector3.Distance(e.Position, me),5:F0}yd  " +
                    $"{(e.IsPlayer ? "player" : $"npc {e.Entry}")}  disp {e.DisplayId}  L{e.Level}  " +
                    $"hp {e.HealthFraction * 100f,3:F0}%{(e.IsMoving ? "  walking" : "")}");
        }
        ImGui.Separator();
        if (ImGui.Button("Disconnect")) { _net.Stop(); Array.Clear(_passBuf); }
        ImGui.End();
    }

    /// <summary>The realm banner/label text: the realmlist NAME once connected (NetworkClient.RealmName),
    /// else a configured realm name, else the host:port. Used by both the login and character-select chrome.</summary>
    private string RealmDisplayName()
    {
        if (_net is not null && !string.IsNullOrWhiteSpace(_net.RealmName)) return _net.RealmName;
        if (!string.IsNullOrWhiteSpace(_config.Server.Realm)) return _config.Server.Realm!;
        return $"{_config.RealmdHost}:{_config.RealmdPort}";
    }

    private static string RaceName(byte r) => r switch
    {
        1 => "Human", 2 => "Orc", 3 => "Dwarf", 4 => "Night Elf",
        5 => "Undead", 6 => "Tauren", 7 => "Gnome", 8 => "Troll", _ => "?"
    };

    private static string ClassName(byte c) => c switch
    {
        1 => "Warrior", 2 => "Paladin", 3 => "Hunter", 4 => "Rogue", 5 => "Priest",
        7 => "Shaman", 8 => "Mage", 9 => "Warlock", 11 => "Druid", _ => "?"
    };

    private static string BufToString(byte[] b)
    {
        int n = Array.IndexOf(b, (byte)0);
        if (n < 0) n = b.Length;
        return Encoding.UTF8.GetString(b, 0, n);
    }

    private static void WriteBuf(byte[] b, string s)
    {
        Array.Clear(b);
        byte[] bytes = Encoding.UTF8.GetBytes(s);
        Array.Copy(bytes, b, Math.Min(bytes.Length, b.Length - 1));
    }
}

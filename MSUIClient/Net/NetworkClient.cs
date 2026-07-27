using System.Collections.Concurrent;
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
    private readonly NetSettings _cfg;
    private readonly ConcurrentQueue<(ushort Opcode, byte[] Body)> _inbound = new();

    private Thread? _worker;
    private Timer? _pingTimer;
    private WorldSession? _session;
    private volatile bool _running;
    private uint _pingSeq;

    // Game-loop-visible state (all reads are cheap snapshots).
    public volatile NetState State = NetState.Idle;
    private volatile string _status = "offline";
    public string Status => _status;

    public ulong PlayerGuid { get; private set; }
    public string PlayerName { get; private set; } = "";
    public Character? Player { get; private set; }

    /// <summary>The account roster, published when we reach CharacterSelect. Read by the select screen.</summary>
    public IReadOnlyList<Character> Characters { get; private set; } = Array.Empty<Character>();

    // Character-select park: the worker waits on _pick until the app calls SelectCharacter.
    private readonly ManualResetEventSlim _pick = new(false);
    private ulong _pickGuid;

    private EnterWorldInfo? _pendingEnter;
    private readonly object _enterLock = new();

    private string _account;
    private string _password;

    public NetworkClient(NetSettings cfg)
    {
        _cfg = cfg;
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
        _pick.Set();
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _worker = new Thread(Run) { IsBackground = true, Name = "MSUI-Net" };
        _worker.Start();
    }

    /// <summary>Drain inbound packets (call once per frame). Returns false when the queue is empty.</summary>
    public bool TryDequeue(out ushort opcode, out byte[] body)
    {
        if (_inbound.TryDequeue(out var p)) { opcode = p.Opcode; body = p.Body; return true; }
        opcode = 0; body = Array.Empty<byte>(); return false;
    }

    /// <summary>Non-null exactly once after entering or changing world — the pose the game loop teleports/loads to.</summary>
    public EnterWorldInfo? TakeEnterWorld()
    {
        lock (_enterLock)
        {
            var e = _pendingEnter;
            _pendingEnter = null;
            return e;
        }
    }

    // --- outbound pass-throughs (safe no-ops when not in world) ---------------------------------

    public void SendMovement(Op moveOp, MovementInfo info)
    {
        if (State == NetState.InWorld) { try { _session?.SendMovement(moveOp, info); } catch { /* dropped on disconnect */ } }
    }

    public void SetSelection(ulong guid) { try { _session?.SetSelection(guid); } catch { } }
    public void AttackSwing(ulong guid) { try { _session?.AttackSwing(guid); } catch { } }
    public void AttackStop() { try { _session?.AttackStop(); } catch { } }
    public void CreatureQuery(uint entry, ulong guid) { try { _session?.CreatureQuery(entry, guid); } catch { } }
    public void NameQuery(ulong guid) { try { _session?.NameQuery(guid); } catch { } }

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
            _session = WorldSession.Connect(worldHost, worldPort, _account, logon.SessionKey, timeout);

            // 3. character enum -> PARK at character select. We do NOT auto-log-in.
            SetState(NetState.Authenticating, "requesting character list");
            var chars = _session.CharEnum();
            Characters = chars;

            ulong wanted = 0;
            // Dev fast path only: if the config names a character, skip the screen (benilla's WOW_CHAR).
            if (!string.IsNullOrWhiteSpace(_cfg.CharacterName))
                wanted = chars.FirstOrDefault(c =>
                    string.Equals(c.Name, _cfg.CharacterName, StringComparison.OrdinalIgnoreCase))?.Guid ?? 0;

            if (wanted == 0)
            {
                SetState(NetState.CharacterSelect,
                    chars.Count == 0 ? "no characters on this account" : $"character select - {chars.Count} character(s)");
                _pick.Reset();
                _pick.Wait();                 // parked until the app calls SelectCharacter (or Stop)
                if (!_running) return;
                wanted = _pickGuid;
            }

            Character? pick = chars.FirstOrDefault(c => c.Guid == wanted);
            if (pick is null) { Fail("selected character not found in roster"); return; }
            Player = pick;
            PlayerGuid = pick.Guid;
            PlayerName = pick.Name;

            // 4. enter world.
            SetState(NetState.EnteringWorld, $"entering world as {pick.Name} (L{pick.Level})");
            _session.PlayerLogin(pick.Guid);

            // 5. stream. LOGIN_VERIFY_WORLD flips us to InWorld; everything else is queued for the app.
            ReadLoop();
        }
        catch (Exception ex) when (_running)
        {
            Fail(ex.Message);
        }
        catch (Exception)
        {
            // shutting down — swallow
        }
    }

    private void ReadLoop()
    {
        var session = _session!;
        while (_running)
        {
            (ushort opcode, byte[] body) = session.ReceivePacket();
            switch ((Op)opcode)
            {
                case Op.SMSG_LOGIN_VERIFY_WORLD:
                    {
                        var r = new PacketReader(body);
                        var info = new EnterWorldInfo(r.ReadU32(), r.ReadVector3(), r.ReadF32());
                        SetEnter(info);
                        SetState(NetState.InWorld, $"in world: {PlayerName} - map {info.Map} at ({info.Position.X:F0}, {info.Position.Y:F0}, {info.Position.Z:F0})");
                        session.SetActiveMover(PlayerGuid);   // become the confirmed mover
                        StartPing();
                        break;
                    }
                case Op.SMSG_NEW_WORLD:
                    {
                        var r = new PacketReader(body);
                        var info = new EnterWorldInfo(r.ReadU32(), r.ReadVector3(), r.ReadF32());
                        session.WorldportAck();
                        SetEnter(info);
                        _status = $"changing to map {info.Map}";
                        break;
                    }
            }
            _inbound.Enqueue((opcode, body));

            // Keep the queue from growing without bound if the app stops draining.
            while (_inbound.Count > 4096 && _inbound.TryDequeue(out _)) { }
        }
    }

    private void StartPing()
    {
        _pingTimer ??= new Timer(_ =>
        {
            try { _session?.Ping(unchecked(_pingSeq++), 0); } catch { /* disconnect handled by read loop */ }
        }, null, 30_000, 30_000);
    }

    private void SetEnter(EnterWorldInfo info)
    {
        lock (_enterLock) _pendingEnter = info;
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
        try { _session?.Dispose(); } catch { } // unblocks the read loop with an IOException
        _session = null;
        try { _worker?.Join(1000); } catch { }
        _worker = null;
        Characters = Array.Empty<Character>();
        if (State != NetState.Failed) State = NetState.Disconnected;
    }

    public void Dispose() => Stop();
}
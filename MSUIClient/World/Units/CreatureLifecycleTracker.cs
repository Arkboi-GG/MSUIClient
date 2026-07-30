using System.Diagnostics;
using System.Numerics;
using MSUIClient.Engine;

namespace MSUIClient.World.Units;

/// <summary>
/// S6 Step 0: monotonic per-GUID evidence from socket receipt through first draw.
/// Mutations are scalar writes after a spawn record exists; snapshots allocate
/// only when a load or hitch JSON is already being written.
/// </summary>
public sealed class CreatureLifecycleTracker
{
    public enum ReasonCode
    {
        QUERY_PENDING,
        RESOLVE_FAILED,
        LOAD_QUEUED,
        LOADING,
        NOT_IN_WORLD,
        DRAWN,
    }

    public enum AdmissionCode
    {
        UNKNOWN,
        VISIBLE,
        OUT_OF_RADIUS,
        OUT_OF_FRUSTUM,
    }

    private sealed class State
    {
        public ulong Guid;
        public int DisplayId;
        public string ModelPath = "";
        public string EnqueueCaller = "";
        public int SpawnCount;
        public long FirstSpawnPacketStamp;
        public long SpawnPacketStamp;
        public long SpawnAppliedStamp;
        public long DisplayResolveStamp;
        public long LoadEnqueueStamp;
        public long ModelReadyStamp;
        public long FirstDrawStamp;
        public long ReasonStamp;
        public long LastTouchedStamp;
        public ReasonCode Reason = ReasonCode.QUERY_PENDING;
        public AdmissionCode Admission;
        public float AdmissionDistanceSq;
        public Vector3 AdmissionEntityPosition;
        public Vector3 AdmissionCameraPosition;
        public Vector3 AdmissionCameraTarget;
    }

    private readonly record struct ProtocolState(long Stamp, ushort Opcode, int Size);
    public readonly record struct LifecycleSnapshot(
        string Guid, int DisplayId, string ModelPath, string EnqueueCaller,
        int SpawnCount, double? FirstSpawnPacketMs, double? SpawnPacketMs,
        double? SpawnAppliedMs, double? DisplayResolveMs,
        double? LoadEnqueueMs, double? ModelReadyMs, double? FirstDrawMs,
        string Reason, double? ReasonSinceMs, string Admission, float? AdmissionDistance,
        float? EntityX, float? EntityY, float? EntityZ,
        float? CameraX, float? CameraY, float? CameraZ,
        float? CameraTargetX, float? CameraTargetY, float? CameraTargetZ);
    public readonly record struct ProtocolSnapshot(double TimeMs, ushort Opcode, string Name, int Size);

    private const int ProtocolCapacity = 256;
    private const int HitchLifecycleCapacity = 256;
    private readonly object _gate = new();
    private readonly Dictionary<ulong, State> _states = [];
    private readonly ProtocolState[] _protocol = new ProtocolState[ProtocolCapacity];
    private int _protocolWrite;
    private int _protocolCount;

    public long WorldLoadStamp { get; private set; }

    public void BeginWorldLoad(long stamp)
    {
        lock (_gate)
        {
            WorldLoadStamp = stamp;
            _states.Clear();
        }
    }

    public void NoteSpawnPacket(ulong guid, int displayId, long receivedStamp)
    {
        long now = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            if (!_states.TryGetValue(guid, out State? state))
            {
                state = new State { Guid = guid };
                _states[guid] = state;
            }
            state.DisplayId = displayId;
            state.SpawnCount++;
            if (state.FirstSpawnPacketStamp == 0) state.FirstSpawnPacketStamp = receivedStamp;
            state.SpawnPacketStamp = receivedStamp;
            state.SpawnAppliedStamp = now;
            state.DisplayResolveStamp = 0;
            state.LoadEnqueueStamp = 0;
            state.ModelReadyStamp = 0;
            state.FirstDrawStamp = 0;
            state.ModelPath = "";
            state.EnqueueCaller = "";
            state.Admission = AdmissionCode.UNKNOWN;
            state.AdmissionDistanceSq = 0f;
            Touch(state, now, displayId > 0 ? ReasonCode.LOAD_QUEUED : ReasonCode.QUERY_PENDING);
        }
    }

    public void NoteDisplayResolved(ulong guid, int displayId, string modelPath)
    {
        long now = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            State state = GetOrCreate(guid, now);
            state.DisplayId = displayId;
            state.ModelPath = modelPath;
            if (state.DisplayResolveStamp == 0) state.DisplayResolveStamp = now;
            state.LastTouchedStamp = now;
        }
    }

    public bool NoteLoadEnqueued(ulong guid, string caller)
    {
        long now = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            State state = GetOrCreate(guid, now);
            bool first = state.LoadEnqueueStamp == 0;
            if (first)
            {
                state.LoadEnqueueStamp = now;
                state.EnqueueCaller = caller;
            }
            Touch(state, now, ReasonCode.LOAD_QUEUED);
            return first;
        }
    }

    public void NoteModelLoading(ulong guid) => NoteReason(guid, ReasonCode.LOADING);

    public void NoteModelReady(ulong guid, bool succeeded)
    {
        long now = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            State state = GetOrCreate(guid, now);
            if (succeeded && state.ModelReadyStamp == 0) state.ModelReadyStamp = now;
            Touch(state, now, succeeded ? ReasonCode.LOAD_QUEUED : ReasonCode.RESOLVE_FAILED);
        }
    }

    public void NoteFirstDraw(ulong guid)
    {
        long now = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            State state = GetOrCreate(guid, now);
            if (state.FirstDrawStamp == 0) state.FirstDrawStamp = now;
            Touch(state, now, ReasonCode.DRAWN);
        }
    }

    public void NoteReason(ulong guid, ReasonCode reason)
    {
        long now = Stopwatch.GetTimestamp();
        lock (_gate) Touch(GetOrCreate(guid, now), now, reason);
    }

    public void NoteAdmission(ulong guid, float distanceSq, AdmissionCode admission,
        Vector3 entityPosition, Vector3 cameraPosition, Vector3 cameraTarget)
    {
        long now = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            State state = GetOrCreate(guid, now);
            state.AdmissionDistanceSq = distanceSq;
            state.Admission = admission;
            state.AdmissionEntityPosition = entityPosition;
            state.AdmissionCameraPosition = cameraPosition;
            state.AdmissionCameraTarget = cameraTarget;
            state.LastTouchedStamp = now;
        }
    }

    public void NoteOutgoingPacket(ushort opcode, int size)
    {
        lock (_gate)
        {
            _protocol[_protocolWrite] = new ProtocolState(Stopwatch.GetTimestamp(), opcode, size);
            _protocolWrite = (_protocolWrite + 1) % _protocol.Length;
            _protocolCount = Math.Min(_protocolCount + 1, _protocol.Length);
        }
    }

    public LifecycleSnapshot[] Snapshot(long originStamp, bool rolling = false)
    {
        lock (_gate)
        {
            IEnumerable<State> states = _states.Values.OrderBy(s => s.SpawnPacketStamp);
            if (rolling) states = states.OrderByDescending(s => s.LastTouchedStamp)
                .Take(HitchLifecycleCapacity).OrderBy(s => s.SpawnPacketStamp);
            return states.Select(s => new LifecycleSnapshot(
                $"0x{s.Guid:X16}", s.DisplayId, s.ModelPath, s.EnqueueCaller,
                s.SpawnCount, Ms(s.FirstSpawnPacketStamp, originStamp),
                Ms(s.SpawnPacketStamp, originStamp), Ms(s.SpawnAppliedStamp, originStamp),
                Ms(s.DisplayResolveStamp, originStamp), Ms(s.LoadEnqueueStamp, originStamp),
                Ms(s.ModelReadyStamp, originStamp), Ms(s.FirstDrawStamp, originStamp),
                s.Reason.ToString(), Ms(s.ReasonStamp, originStamp), s.Admission.ToString(),
                s.Admission == AdmissionCode.UNKNOWN
                    ? null : MathF.Sqrt(MathF.Max(0f, s.AdmissionDistanceSq)),
                Coordinate(s, s.AdmissionEntityPosition.X), Coordinate(s, s.AdmissionEntityPosition.Y),
                Coordinate(s, s.AdmissionEntityPosition.Z), Coordinate(s, s.AdmissionCameraPosition.X),
                Coordinate(s, s.AdmissionCameraPosition.Y), Coordinate(s, s.AdmissionCameraPosition.Z),
                Coordinate(s, s.AdmissionCameraTarget.X), Coordinate(s, s.AdmissionCameraTarget.Y),
                Coordinate(s, s.AdmissionCameraTarget.Z))).ToArray();
        }
    }

    private static float? Coordinate(State state, float value) =>
        state.Admission == AdmissionCode.UNKNOWN ? null : value;

    public ProtocolSnapshot[] SnapshotProtocol(long originStamp)
    {
        lock (_gate)
        {
            var result = new List<ProtocolSnapshot>(_protocolCount);
            int start = (_protocolWrite - _protocolCount + _protocol.Length) % _protocol.Length;
            for (int i = 0; i < _protocolCount; i++)
            {
                ProtocolState p = _protocol[(start + i) % _protocol.Length];
                result.Add(new ProtocolSnapshot(
                    Stopwatch.GetElapsedTime(originStamp, p.Stamp).TotalMilliseconds,
                    p.Opcode, WireRing.NameFor(p.Opcode), p.Size));
            }
            return result.ToArray();
        }
    }

    private State GetOrCreate(ulong guid, long now)
    {
        if (_states.TryGetValue(guid, out State? state)) return state;
        state = new State { Guid = guid, SpawnAppliedStamp = now, LastTouchedStamp = now };
        _states[guid] = state;
        return state;
    }

    private static void Touch(State state, long now, ReasonCode reason)
    {
        if (state.Reason != reason || state.ReasonStamp == 0)
        {
            state.Reason = reason;
            state.ReasonStamp = now;
        }
        state.LastTouchedStamp = now;
    }

    private static double? Ms(long stamp, long originStamp) => stamp == 0
        ? null
        : Stopwatch.GetElapsedTime(originStamp, stamp).TotalMilliseconds;
}

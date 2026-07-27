using System.Numerics;

namespace MSUIClient.Net;

// The client-side world model: entities keyed by GUID, built from SMSG_UPDATE_OBJECT.
// Game-thread-owned (populated by the game loop draining NetworkClient.Inbound), so
// no locking — the net worker only does socket I/O + framing.

public sealed class WorldEntity
{
    public ulong Guid;
    public ObjectTypeId Type;
    public uint Entry;                     // creature/GO template entry
    public ObjectFields Fields = new();
    public Vector3 Position;               // raw WoW space
    public float Orientation;
    public float[]? Speeds;                // [walk, run, run_back, swim, swim_back, turn]

    // The active SMSG_MONSTER_MOVE spline this creature is walking, if any. Advanced
    // each frame by EntityStore.TickSplines; cleared when it finishes or an
    // authoritative position snapshot supersedes it.
    public CreatureSpline? Spline;

    public bool IsPlayer => Type == ObjectTypeId.Player;
    public bool IsCreature => Type == ObjectTypeId.Unit;
    public bool IsUnit => Type is ObjectTypeId.Unit or ObjectTypeId.Player;
    public bool IsGameObject => Type == ObjectTypeId.GameObject;

    public int DisplayId => Fields.DisplayId;
    public uint Level => Fields.Level;
    public float HealthFraction => Fields.HealthFraction;
    public bool IsDead => Fields.IsDead;
    public float Scale => Fields.Scale;
    public uint NpcFlags => Fields.NpcFlags;

    /// <summary>True while a spline is actively moving this unit (drives future walk/run animation choice).</summary>
    public bool IsMoving => Spline is not null;
}

public sealed class EntityStore
{
    private readonly Dictionary<ulong, WorldEntity> _entities = new();

    public IReadOnlyDictionary<ulong, WorldEntity> Entities => _entities;
    public int Count => _entities.Count;
    public int UnitCount => _entities.Values.Count(e => e.IsUnit);
    public int CreatureCount => _entities.Values.Count(e => e.IsCreature);
    public int PlayerCount => _entities.Values.Count(e => e.IsPlayer);
    public int MovingCount => _entities.Values.Count(e => e.Spline is not null);

    public bool TryGet(ulong guid, out WorldEntity entity) => _entities.TryGetValue(guid, out entity!);

    public void Apply(ObjectUpdate u)
    {
        switch (u.Kind)
        {
            case UpdateKind.CreateObject:
            case UpdateKind.CreateObject2:
            {
                var e = new WorldEntity
                {
                    Guid = u.Guid,
                    Type = u.Type,
                    Fields = u.Fields ?? new ObjectFields(),
                };
                e.Entry = e.Fields.Entry ?? GuidInfo.Entry(u.Guid) ?? 0;
                if (u.Movement?.Position is { } p)
                {
                    e.Position = p;
                    e.Orientation = u.Movement.Orientation;
                    e.Speeds = u.Movement.Speeds;
                }
                _entities[u.Guid] = e;   // a fresh create drops any prior spline with the old entity
                break;
            }
            case UpdateKind.Values:
                if (u.Fields is not null)
                {
                    if (_entities.TryGetValue(u.Guid, out var ent)) ent.Fields.Merge(u.Fields);
                    else _entities[u.Guid] = new WorldEntity { Guid = u.Guid, Fields = u.Fields, Entry = GuidInfo.Entry(u.Guid) ?? 0 };
                }
                break;
            case UpdateKind.Movement:
                if (_entities.TryGetValue(u.Guid, out var em) && u.Movement?.Position is { } mp)
                {
                    // An authoritative position snapshot supersedes an in-progress spline (benilla parity).
                    em.Spline = null;
                    em.Position = mp;
                    em.Orientation = u.Movement.Orientation;
                    if (u.Movement.Speeds is not null) em.Speeds = u.Movement.Speeds;
                }
                break;
            case UpdateKind.OutOfRange:
                if (u.Guids is not null) foreach (var g in u.Guids) _entities.Remove(g);
                break;
            case UpdateKind.Near:
                break; // objects entering range; the CREATE that follows carries their data
        }
    }

    /// <summary>Attach a freshly parsed SMSG_MONSTER_MOVE to its unit (replacing any in-progress spline).</summary>
    public void ApplyMonsterMove(MonsterMove mm, long nowMs)
    {
        if (!_entities.TryGetValue(mm.Guid, out var e)) return;   // unknown guid — not streamed to us

        if (mm.Stop || mm.DurationMs == 0 || mm.Points.Length < 2)
        {
            e.Spline = null;                                       // freeze where the unit is
            if (mm.FacingAngle is { } fa) e.Orientation = fa;      // path-less re-face
            return;
        }
        e.Spline = new CreatureSpline(mm.Points, mm.DurationMs, mm.Flying, nowMs);
    }

    /// <summary>Advance every active spline. Call once per frame with a monotonic ms clock.</summary>
    public void TickSplines(long nowMs)
    {
        foreach (var e in _entities.Values)
        {
            if (e.Spline is null) continue;
            bool running = e.Spline.Sample(nowMs, out Vector3 pos, out float? facing);
            e.Position = pos;
            if (facing is { } f) e.Orientation = f;
            if (!running) e.Spline = null;   // finished — hold at the endpoint
        }
    }

    public void Remove(ulong guid) => _entities.Remove(guid);
    public void Clear() => _entities.Clear();

    public IEnumerable<WorldEntity> Units => _entities.Values.Where(e => e.IsUnit);

    /// <summary>The nearest units to a point (for a HUD list / targeting).</summary>
    public List<WorldEntity> NearestUnits(Vector3 from, int max) =>
        _entities.Values.Where(e => e.IsUnit)
            .OrderBy(e => Vector3.DistanceSquared(e.Position, from))
            .Take(max)
            .ToList();
}

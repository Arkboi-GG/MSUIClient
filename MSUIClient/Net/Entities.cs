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
    public float PowerFraction => Fields.PowerFraction;
    public bool IsDead => Fields.IsDead;
    public bool InCombat => Fields.InCombat;
    public float Scale => Fields.Scale;
    public uint NpcFlags => Fields.NpcFlags;
    public uint GameObjectType => Fields.GameObjectType;

    /// <summary>Auto-attack engagement bracketed by SMSG_ATTACKSTART/STOP.</summary>
    public bool Engaged { get; internal set; }
    public ulong? CombatTarget { get; internal set; }

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

    /// <summary>Creator/DevTools: place (or replace) a locally synthesized entity. Never
    /// used on the wire path - the server's create/delete blocks go through Apply.</summary>
    public void AddSynthetic(WorldEntity entity) => _entities[entity.Guid] = entity;

    /// <summary>Creator/DevTools: remove a locally synthesized entity.</summary>
    public bool RemoveSynthetic(ulong guid) => _entities.Remove(guid);

    public void SetEngaged(ulong guid, bool engaged, ulong? victim = null)
    {
        if (_entities.TryGetValue(guid, out var entity))
        {
            entity.Engaged = engaged;
            entity.CombatTarget = engaged ? victim : null;
        }
    }

    public void StopMovement(ulong guid)
    {
        if (_entities.TryGetValue(guid, out var entity)) entity.Spline = null;
    }

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
                if (e.IsDead) e.Spline = null;
                _entities[u.Guid] = e;   // a fresh create drops any prior spline with the old entity
                break;
            }
            case UpdateKind.Values:
                if (u.Fields is not null)
                {
                    if (_entities.TryGetValue(u.Guid, out var ent))
                    {
                        ent.Fields.Merge(u.Fields);
                        if (ent.IsDead) ent.Spline = null;
                    }
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
            if (e.IsDead) { e.Spline = null; continue; }
            if (e.Spline is null) continue;
            bool running = e.Spline.Sample(nowMs, out Vector3 pos, out float? facing);
            e.Position = pos;
            if (facing is { } f) e.Orientation = f;
            if (!running) e.Spline = null;   // finished — hold at the endpoint
        }
    }

    /// <summary>
    /// Idle creatures locally square up on UNIT_FIELD_TARGET. vmangos SetInFront
    /// is server-side only, so no MONSTER_MOVE is expected for this turn.
    /// </summary>
    public void FaceIdleTargets(float dt, ulong playerGuid, Vector3 playerPosition)
    {
        float maxStep = 8f * MathF.Max(0f, dt);
        foreach (WorldEntity entity in _entities.Values)
        {
            if (!entity.IsCreature || entity.IsDead || entity.Spline is not null) continue;
            ulong? targetGuid = entity.Fields.Target ?? entity.CombatTarget;
            if (targetGuid is null) continue;

            Vector3 targetPosition;
            if (targetGuid.Value == playerGuid) targetPosition = playerPosition;
            else if (_entities.TryGetValue(targetGuid.Value, out WorldEntity? target)) targetPosition = target.Position;
            else continue;

            float dx = targetPosition.X - entity.Position.X;
            float dy = targetPosition.Y - entity.Position.Y;
            if (dx * dx + dy * dy < 1e-4f) continue;
            entity.Orientation = TurnToward(entity.Orientation, MathF.Atan2(dy, dx), maxStep);
        }
    }

    public static float TurnToward(float current, float goal, float maxStep)
    {
        const float tau = MathF.PI * 2f;
        float delta = ((goal - current) % tau + tau) % tau;
        float direction = 1f;
        if (delta > MathF.PI) { delta = tau - delta; direction = -1f; }
        if (delta < 1e-4f) return ((current % tau) + tau) % tau;
        float result = current + direction * MathF.Min(delta, MathF.Max(0f, maxStep));
        return ((result % tau) + tau) % tau;
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

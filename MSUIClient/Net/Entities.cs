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

    /// <summary>
    /// Persistent airborne presentation state, independent of whether a movement spline is
    /// currently active. Encounter Lab puppets set this from their simulation actor so a flyer
    /// keeps hovering between path legs; live units continue to use spline/movement flags until
    /// the wire path exposes an equivalent durable flag.
    /// </summary>
    public bool Flying;

    public bool IsPlayer => Type == ObjectTypeId.Player;
    public bool IsCreature => Type == ObjectTypeId.Unit;
    public bool IsUnit => Type is ObjectTypeId.Unit or ObjectTypeId.Player;
    public bool IsGameObject => Type == ObjectTypeId.GameObject;

    public int DisplayId => Fields.DisplayId;

    /// <summary>
    /// UNIT_FIELD_MOUNTDISPLAYID: the CreatureDisplayInfo id of the steed this unit is
    /// riding, 0 when on foot. 1.12 has no vehicle system — a "vehicle" (the Mirage
    /// Raceway rocket cars included) is a mount display like any other, and the rider is
    /// seated on the mount M2's attachment 0. See CreatureRenderer.Mounts.cs.
    /// </summary>
    public int MountDisplayId => (int)Fields.MountDisplayId;

    public uint Level => Fields.Level;
    public float HealthFraction => Fields.HealthFraction;
    public float PowerFraction => Fields.PowerFraction;
    public bool IsDead => Fields.IsDead;
    public bool InCombat => Fields.InCombat;
    public float Scale => Fields.Scale;
    public uint NpcFlags => Fields.NpcFlags;
    public uint GameObjectType => Fields.GameObjectType;

    /// <summary>
    /// Render yaw for a gameobject. Build 5875 carries the facing twice: the
    /// GAMEOBJECT_ROTATION quaternion (fields 10-13; vmangos writes
    /// z=sin(yaw/2), w=cos(yaw/2), x=y=0 for every static spawn) and the
    /// movement block's Orientation. Prefer the quaternion when it is set —
    /// it is the authoritative descriptor — else fall back to Orientation.
    /// </summary>
    public float GameObjectFacing
    {
        get
        {
            System.Numerics.Quaternion q = Fields.GameObjectRotation;
            return q.Z != 0f || q.W != 0f ? 2f * MathF.Atan2(q.Z, q.W) : Orientation;
        }
    }

    /// <summary>Auto-attack engagement bracketed by SMSG_ATTACKSTART/STOP.</summary>
    public bool Engaged { get; internal set; }
    public ulong? CombatTarget { get; internal set; }

    /// <summary>ClientUptime ms of the last remote MSG_MOVE_* applied to this unit (observer interpolation).</summary>
    internal long LastMoveMs;

    /// <summary>Last MSG_MOVE_* movement flags for a remote unit — direction (fwd/back/strafe/turn/swim)
    /// for gait selection. Speed comes from the interpolation spline; direction can only come from here.</summary>
    internal uint MoveFlags;

    /// <summary>
    /// True (creatures): the active spline drives facing toward the travel direction. False (remote
    /// players): facing comes from the authoritative MSG_MOVE_* orientation instead, so a backpedalling
    /// or strafing player keeps facing their aim rather than snapping to the movement vector.
    /// </summary>
    internal bool FacingFromSpline = true;

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

    /// <summary>
    /// Predict only the facing edge of a server-driven RTS move. Position remains untouched until
    /// SMSG_MONSTER_MOVE supplies the authoritative path, but the selected body can square toward
    /// its destination on the command frame instead of holding its prior cast/combat bearing.
    /// </summary>
    public void PredictServerMoveFacing(ulong guid, Vector3 destination)
    {
        if (!_entities.TryGetValue(guid, out WorldEntity? entity) || entity.IsDead) return;
        float dx = destination.X - entity.Position.X;
        float dy = destination.Y - entity.Position.Y;
        if (dx * dx + dy * dy > 1e-6f)
            entity.Orientation = MathF.Atan2(dy, dx);

        // A player body can alternate between client MSG_MOVE_* and server AI splines across
        // possession/free-view hand-offs. This order puts it back under spline-facing policy.
        entity.FacingFromSpline = true;
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

        float? dictatedFacing = MonsterMoveFacingLaw.Resolve(mm.Facing, mm.Start, guid =>
            _entities.TryGetValue(guid, out WorldEntity? target) ? target.Position : null);
        if (dictatedFacing is { } snap) e.Orientation = snap;

        if (mm.Stop || mm.DurationMs == 0 || mm.Points.Length < 2)
        {
            e.Spline = null;                                       // freeze where the unit is
            return;
        }
        // SMSG_MONSTER_MOVE is a server-owned path even when its subject is a Player bot.
        // It supersedes the client-facing policy that ApplyRemotePlayerMove installs while a
        // human drives that same body, so turns follow the new path after a free-view order.
        e.FacingFromSpline = true;
        e.Spline = new CreatureSpline(mm.Points, mm.DurationMs, mm.Flying, nowMs);
    }

    /// <summary>Position-changing movement flags (turning/pitch alone do not relocate a unit).</summary>
    private const uint LocomotionMask =
        (uint)(MovementFlags.Forward | MovementFlags.Backward |
               MovementFlags.StrafeLeft | MovementFlags.StrafeRight);

    /// <summary>
    /// Apply a broadcast MSG_MOVE_* for ANOTHER player, with observer interpolation. While the player
    /// is locomoting we glide from their current interpolated spot to the newly reported one over the
    /// inter-packet interval (a short 2-point <see cref="CreatureSpline"/>), so heartbeats ~500 ms apart
    /// read as smooth motion and the derived AverageSpeed drives walk vs run. A stop / turn-in-place /
    /// fall-land snaps to the authoritative pose. Facing always comes from the packet, not the spline.
    /// </summary>
    public void ApplyRemotePlayerMove(ulong guid, MovementInfo mi, long nowMs)
    {
        if (!_entities.TryGetValue(guid, out var e) || !e.IsPlayer) return;
        e.FacingFromSpline = false;   // players face their reported aim, never the travel vector
        e.MoveFlags = mi.Flags;       // direction bits for gait selection (speed comes from the spline)

        if (e.IsDead || (mi.Flags & LocomotionMask) == 0)
        {
            // Dead, or a stop / turn-only / fall-land / reface: snap to the authoritative pose.
            e.Spline = null;
            e.Position = mi.Position;
            e.Orientation = mi.Orientation;
            e.LastMoveMs = nowMs;
            return;
        }

        long last = e.LastMoveMs;
        long dt = nowMs - last;
        e.Orientation = mi.Orientation;
        e.LastMoveMs = nowMs;

        // Facing/heartbeat with negligible positional delta while already gliding: keep the current
        // spline so we don't stutter to idle mid-run.
        if (e.Spline is not null && Vector3.DistanceSquared(e.Position, mi.Position) < 0.04f)
            return;

        if (last == 0 || dt <= 0 || dt > 1000)
        {
            // First move after sighting/idle, or a gap too large to interpolate cleanly: snap.
            e.Spline = null;
            e.Position = mi.Position;
            return;
        }

        // Glide from wherever we currently have them to the freshly reported spot over the packet gap.
        e.Spline = new CreatureSpline(new[] { e.Position, mi.Position }, (uint)dt, flying: false, nowMs);
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
            if (facing is { } f && e.FacingFromSpline) e.Orientation = f;
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

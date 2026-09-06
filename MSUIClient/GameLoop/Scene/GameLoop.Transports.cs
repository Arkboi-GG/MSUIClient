using System.Numerics;
using MSUIClient.Formats;
using MSUIClient.Net;
using MSUIClient.Player;
using MSUIClient.World.Collision;
using MSUIClient.World.Wmo;
using MSUIClient.World.Units;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private sealed class ElevatorTransportState
    {
        public required WorldEntity Entity;
        public required ElevatorKeyframe[] Frames;
        public Vector3 SpawnPosition;
        public Quaternion SpawnRotation;
        public uint AnchorProgress;
        public long AnchorReceivedAt;
        public bool WasMoving;
    }

    private readonly record struct MoTransportKey(uint PathId, int MoveSpeed, int AccelRate);

    private sealed class MoTransportState
    {
        public required WorldEntity Entity;
        public required MoTransportKey Key;
        public required MoTransportTimetable Timetable;
        public uint AnchorProgress;
        public long AnchorReceivedAt;
        public bool WasMoving;
    }

    private sealed class ControlledTransportRide
    {
        public ulong Guid;
        public Vector3 LocalPosition;
        public float TransportYaw;
    }

    private TransportAnimationCatalog? _transportAnimations;
    private bool _transportAnimationsAttempted;
    private readonly Dictionary<ulong, ElevatorTransportState> _elevatorTransports = [];
    private readonly HashSet<ulong> _elevatorTransportSeen = [];
    private TaxiPathNodeCatalog? _taxiPathNodes;
    private bool _taxiPathNodesAttempted;
    private readonly Dictionary<MoTransportKey, MoTransportTimetable?> _moTransportTimetables = [];
    private readonly Dictionary<ulong, MoTransportState> _moTransports = [];
    private readonly HashSet<ulong> _moTransportSeen = [];
    private readonly HashSet<ulong> _offMapTransports = [];
    private ControlledTransportRide? _controlledTransportRide;

    /// <summary>
    /// Advance type-11 lifts and type-15 vessels from their server-supplied path clock.
    /// This runs before dynamic-doodad reconciliation, so the ordinary GO renderer
    /// and picker consume the same this-frame world position.
    /// </summary>
    private void UpdateGameObjectTransports()
    {
        EnsureTransportAnimations();
        EnsureTaxiPathNodes();

        uint now = MovementInfo.ClientUptimeMs();
        _elevatorTransportSeen.Clear();
        _moTransportSeen.Clear();
        _offMapTransports.Clear();
        foreach (WorldEntity go in _entities.Entities.Values)
        {
            if (!go.IsGameObject || go.TransportProgress is not uint progress) continue;
            RequireGameObjectTemplate(go);
            if (!_gameObjectTemplates.TryGetValue(go.Entry, out GameObjectTemplate? template))
                continue;

            if (template.Type == 11 && _transportAnimations is not null &&
                _transportAnimations.TryGet(go.Entry, out ElevatorKeyframe[] frames))
                UpdateElevatorTransport(go, progress, frames, now);
            else if (template.Type == 15 && template.Data.Length >= 3)
                UpdateMoTransport(go, progress, template, now);
        }

        foreach (ulong guid in _elevatorTransports.Keys.ToArray())
            if (!_elevatorTransportSeen.Contains(guid))
            {
                _doodads?.SetDynamicCollisionLive(guid, false);
                _elevatorTransports.Remove(guid);
            }
        foreach (ulong guid in _moTransports.Keys.ToArray())
            if (!_moTransportSeen.Contains(guid))
            {
                if (_moTransports[guid].Entity is { } old)
                    old.TransportFacingOverride = null;
                _moTransports.Remove(guid);
            }

        ComposeObservedTransportRiders();
    }

    private void UpdateElevatorTransport(WorldEntity go, uint progress,
        ElevatorKeyframe[] frames, uint now)
    {
        _elevatorTransportSeen.Add(go.Guid);
        if (!_elevatorTransports.TryGetValue(go.Guid, out ElevatorTransportState? state) ||
            !ReferenceEquals(state.Entity, go))
        {
            Quaternion rotation = go.Fields.GameObjectRotation;
            if (rotation.LengthSquared() <= 1e-8f)
                rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, go.Orientation);
            state = new ElevatorTransportState
            {
                Entity = go,
                Frames = frames,
                SpawnPosition = go.Position,
                SpawnRotation = rotation,
                AnchorProgress = progress,
                AnchorReceivedAt = go.TransportProgressReceivedMs,
            };
            _elevatorTransports[go.Guid] = state;
        }
        else if (state.AnchorProgress != progress ||
                 state.AnchorReceivedAt != go.TransportProgressReceivedMs)
        {
            // A movement-only re-anchor changes the clock, not the stationary
            // spawn frame. A create replaces WorldEntity and takes the branch above.
            state.AnchorProgress = progress;
            state.AnchorReceivedAt = go.TransportProgressReceivedMs;
        }

        ulong elapsed = (ulong)Math.Max(0L, (long)now - state.AnchorReceivedAt);
        ElevatorTransportLaw.Sample sample = ElevatorTransportLaw.Evaluate(
            state.Frames, state.SpawnPosition, state.SpawnRotation,
            (ulong)state.AnchorProgress + elapsed);
        go.Position = sample.Position;
        // Support probes run before ordinary GameObject render reconciliation. Keep an already
        // published car hull on this exact keyframe and out of the static collision BVH.
        _doodads?.SetDynamicCollisionLive(go.Guid);
        _doodads?.TryUpdateDynamicTransform(go.Guid,
            DynamicM2GameObjectTransform(go.Position, go.GameObjectFacing,
                go.Scale > .0001f ? go.Scale : 1f));
        if (sample.Moving != state.WasMoving)
            Console.WriteLine($"[transport] lift {go.Guid:x} " +
                (sample.Moving ? "departed" : "docked"));
        state.WasMoving = sample.Moving;
    }

    private void UpdateMoTransport(WorldEntity go, uint progress,
        GameObjectTemplate template, uint now)
    {
        uint pathId = unchecked((uint)Math.Max(0, template.Data[0]));
        int moveSpeed = Math.Max(0, template.Data[1]);
        int accelRate = Math.Max(0, template.Data[2]);
        var key = new MoTransportKey(pathId, moveSpeed, accelRate);
        if (!_moTransportTimetables.TryGetValue(key, out MoTransportTimetable? timetable))
        {
            timetable = _taxiPathNodes is not null &&
                _taxiPathNodes.TryGet(pathId, out TaxiPathNode[] nodes)
                ? MoTransportTimetable.Build(nodes, moveSpeed, accelRate)
                : null;
            _moTransportTimetables[key] = timetable;
            Console.WriteLine(timetable is null
                ? $"[transport] type-15 entry {go.Entry} path {pathId} unavailable"
                : $"[transport] type-15 entry {go.Entry} path {pathId} " +
                  $"period={timetable.PeriodMs}ms armed");
        }
        if (timetable is null) return;

        _moTransportSeen.Add(go.Guid);
        if (!_moTransports.TryGetValue(go.Guid, out MoTransportState? state) ||
            !ReferenceEquals(state.Entity, go) || state.Key != key)
        {
            state = new MoTransportState
            {
                Entity = go,
                Key = key,
                Timetable = timetable,
                AnchorProgress = progress,
                AnchorReceivedAt = go.TransportProgressReceivedMs,
            };
            _moTransports[go.Guid] = state;
        }
        else if (state.AnchorProgress != progress ||
                 state.AnchorReceivedAt != go.TransportProgressReceivedMs)
        {
            state.AnchorProgress = progress;
            state.AnchorReceivedAt = go.TransportProgressReceivedMs;
        }

        ulong elapsed = (ulong)Math.Max(0L, (long)now - state.AnchorReceivedAt);
        MoTransportSample sample = state.Timetable.Sample(
            (ulong)state.AnchorProgress + elapsed);
        go.Position = sample.Position;
        go.TransportFacingOverride = sample.Heading;
        // The owner-aware deck probe runs during movement, earlier than the
        // ordinary render reconciliation. Move an already-published hull now
        // so its support mesh and the carried rider share this exact sample;
        // first publication still happens on the normal async render lane.
        _wmo?.TryUpdateDynamicTransform(go.Guid,
            WmoRenderer.DynamicGameObjectTransform(go.Position,
                go.GameObjectFacing, go.Scale));
        if (sample.MapId != unchecked((uint)Math.Max(0, _config.Start.Map)))
            _offMapTransports.Add(go.Guid);
        if (sample.Moving != state.WasMoving)
            Console.WriteLine($"[transport] vessel {go.Guid:x} " +
                (sample.Moving ? "departed" : "docked"));
        state.WasMoving = sample.Moving;
    }

    /// <summary>
    /// The server already sends observed riders in the platform's local frame.
    /// Compose only through a transport whose client-side drive is armed; a
    /// missing/static boat must retain the server's fallback world pose.
    /// </summary>
    private void ComposeObservedTransportRiders()
    {
        foreach (WorldEntity rider in _entities.Units)
        {
            if (TacticalFreezePoseLaw.IsFrozen(rider.Guid) ||
                (rider.Guid == ControlledGuid && !ControlledBodyIsStreamed) ||
                rider.Transport is not { } local ||
                (!_elevatorTransports.ContainsKey(local.Guid) &&
                 !_moTransports.ContainsKey(local.Guid)) ||
                !_entities.TryGet(local.Guid, out WorldEntity transport))
                continue;
            TransportRiderLaw.WorldPose world = TransportRiderLaw.Compose(
                transport.Position, transport.GameObjectFacing,
                local.Pos, local.Orientation);
            rider.Position = world.Position;
            rider.Orientation = world.Orientation;
        }
    }

    private bool IsArmedTransport(ulong guid) =>
        (_elevatorTransports.ContainsKey(guid) || _moTransports.ContainsKey(guid)) &&
        !_offMapTransports.Contains(guid);

    private MovingGroundHit? ProbeMovingTransportGround(Vector3 origin, float maxDistance)
    {
        MovingGroundHit? best = null;
        if (_wmo?.TryRaycastDynamicCollision(origin, -Vector3.UnitZ,
                maxDistance, out ulong wmoGuid, out RayHit wmoHit, IsArmedTransport) == true)
            best = new MovingGroundHit(wmoGuid, wmoHit.Distance, wmoHit.Point, wmoHit.Normal);
        if (_doodads?.TryRaycastDynamicCollision(origin, -Vector3.UnitZ,
                best?.Distance ?? maxDistance, out ulong m2Guid, out RayHit m2Hit,
                IsArmedTransport) == true)
            best = new MovingGroundHit(m2Guid, m2Hit.Distance, m2Hit.Point, m2Hit.Normal);
        return best;
    }

    /// <summary>Rigidly carry the controlled mover by the platform before this
    /// frame's input integrates. Camera orbit is preserved because both facing
    /// and the visible view receive the same transport-yaw delta.</summary>
    private void CarryControlledTransportRider()
    {
        // A detached, parked, or pending controller is not a world body. Its former ride can
        // survive until reconciliation at frame end, but only an embodied controlled mover may
        // be carried by this client-side transport cache.
        if (!ControllerOwnsControlledBodyPose || _controller is null ||
            _controlledTransportRide is not { } ride) return;
        if (!IsArmedTransport(ride.Guid) ||
            !_entities.TryGet(ride.Guid, out WorldEntity transport))
        {
            _controlledTransportRide = null;
            _controller.Transport = null;
            return;
        }

        float yaw = transport.GameObjectFacing;
        TransportRiderLaw.WorldPose world = TransportRiderLaw.Compose(
            transport.Position, yaw, ride.LocalPosition, 0f);
        float delta = ShortestYawDelta(yaw - ride.TransportYaw);
        _controller.Position = world.Position;
        _controller.Yaw = TransportRiderLaw.NormalizeOrientation(_controller.Yaw + delta);
        _window.Camera.Yaw = TransportRiderLaw.NormalizeOrientation(_window.Camera.Yaw + delta);
        ride.TransportYaw = yaw;
    }

    /// <summary>Attach from owner-aware deck support, retain the platform frame
    /// through jumps, and publish the exact boat-local movement tail.</summary>
    private void ReconcileControlledTransportRider()
    {
        if (_controller is null) return;
        if (!ControllerOwnsControlledBodyPose)
        {
            // In Free View the controller's collision probes belong to the observer rig. Do not
            // turn a camera resting over a deck into a gameplay rider, and do not retain the
            // embodied unit's transport tail across a parked/pending ownership transition.
            _controlledTransportRide = null;
            _controller.Transport = null;
            return;
        }
        ulong support = _controller.GroundOwnerGuid;
        if (_controller.Swimming || _controller.Flying ||
            (_controller.Grounded && support == 0))
            _controlledTransportRide = null;

        if (support != 0 && IsArmedTransport(support) &&
            _entities.TryGet(support, out WorldEntity supportedTransport))
        {
            _controlledTransportRide ??= new ControlledTransportRide();
            _controlledTransportRide.Guid = support;
            _controlledTransportRide.TransportYaw = supportedTransport.GameObjectFacing;
        }

        if (_controlledTransportRide is not { } ride ||
            !IsArmedTransport(ride.Guid) ||
            !_entities.TryGet(ride.Guid, out WorldEntity transport))
        {
            _controlledTransportRide = null;
            _controller.Transport = null;
            return;
        }

        float yaw = transport.GameObjectFacing;
        Vector3 delta = _controller.Position - transport.Position;
        float sin = MathF.Sin(-yaw), cos = MathF.Cos(-yaw);
        ride.LocalPosition = new Vector3(
            delta.X * cos - delta.Y * sin,
            delta.X * sin + delta.Y * cos,
            delta.Z);
        ride.TransportYaw = yaw;
        _controller.Transport = new TransportPose(
            ride.Guid, ride.LocalPosition,
            TransportRiderLaw.NormalizeOrientation(_controller.Yaw - yaw));
    }

    private static float ShortestYawDelta(float angle)
    {
        float wrapped = (angle + MathF.PI) % (MathF.PI * 2f);
        if (wrapped < 0f) wrapped += MathF.PI * 2f;
        return wrapped - MathF.PI;
    }

    private void EnsureTransportAnimations()
    {
        if (_transportAnimationsAttempted || _mpq is null) return;
        _transportAnimationsAttempted = true;
        _transportAnimations = TransportAnimationCatalog.Load(_mpq);
        Console.WriteLine(_transportAnimations is null
            ? "[transport] TransportAnimation.dbc unavailable; type-11 cars stay parked"
            : $"[transport] {_transportAnimations.Count} type-11 path(s) loaded");
    }

    private void EnsureTaxiPathNodes()
    {
        if (_taxiPathNodesAttempted || _mpq is null) return;
        _taxiPathNodesAttempted = true;
        _taxiPathNodes = TaxiPathNodeCatalog.Load(_mpq);
        Console.WriteLine(_taxiPathNodes is null
            ? "[transport] TaxiPathNode.dbc unavailable; type-15 vessels stay parked"
            : $"[transport] {_taxiPathNodes.Count} taxi/transport path(s) loaded");
    }

    private void ResetGameObjectTransportState()
    {
        _controlledTransportRide = null;
        if (_controller is not null) _controller.Transport = null;
        foreach (ulong guid in _elevatorTransports.Keys)
            _doodads?.SetDynamicCollisionLive(guid, false);
        _elevatorTransports.Clear();
        _elevatorTransportSeen.Clear();
        foreach (MoTransportState state in _moTransports.Values)
            state.Entity.TransportFacingOverride = null;
        _moTransports.Clear();
        _moTransportSeen.Clear();
        _offMapTransports.Clear();
    }
}

using System.Numerics;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;
using MSUIClient.World.Collision;
using MSUIClient.World.Doodads;
using MSUIClient.World.Wmo;

namespace MSUIClient;

// Server-gameobject RENDERING (2026-08-12). Everything the server spawns as a
// gameobject — Stormwind's shop signs, mailboxes, chests, herb/ore nodes — is
// invisible without this: those models sit in GameObjectDisplayInfo.dbc, not in
// any ADT/WMO placement list, and CreatureRenderer only draws units.
//
// Each visible gameobject entity gets an owner-keyed dynamic placement,
// resynced every frame against the entity store (create/despawn/out-of-range,
// GAMEOBJECT_DISPLAYID changes, transport motion, and renderer residency
// resets). M2 displays use DoodadRenderer. WMO displays use WmoRenderer and
// publish their set-0 MODD props through DoodadRenderer so vessel hulls, sails,
// rotors, furniture, cull/pick bounds, and animation identities move together.
// GAMEOBJECT_STATE poses/transitions and controlled-player transport physics
// are owner-local additions to that placement path; see the system document.
public sealed partial class GameLoop
{
    private GameObjectDisplayTable? _gameObjectDisplays;
    private bool _gameObjectDisplaysAttempted;

    /// <summary>Last-synced signature per placed gameobject GUID. A mismatch
    /// (display change, move, scale change) re-adds the placement.</summary>
    private readonly Dictionary<ulong, (uint DisplayId, Vector3 Position, float Yaw, float Scale)>
        _gameObjectPlacements = [];

    /// <summary>Display ids that cannot render this session (no DBC row / no
    /// model path / M2 missing from the MPQs). Each is logged exactly once.</summary>
    private readonly HashSet<uint> _gameObjectDisplaysUnrenderable = [];

    private readonly List<ulong> _gameObjectPlacementScratch = [];
    private readonly HashSet<int> _gameObjectWmoPropScratch = [];
    private readonly HashSet<ulong> _gameObjectDespawnAnimations = [];
    private readonly Dictionary<ulong, double> _gameObjectRetainedDestroys = [];

    private sealed class GameObjectAnimationState
    {
        public uint DisplayId;
        public uint? LastWire;
        public uint ClientState;
        public uint? Shown;
    }

    /// <summary>
    /// One client-side state per family-A GameObject. LastWire prevents an
    /// unrelated VALUES update from undoing a locally predicted chest lid;
    /// Shown remains null until its asynchronously loaded M2 accepts the pose.
    /// </summary>
    private readonly Dictionary<ulong, GameObjectAnimationState>
        _gameObjectAnimationStates = [];

    /// <summary>The gameobject under the mouse this frame (0 = none). Set by
    /// UpdateTargeting alongside the unit hover; nonzero only when the GO hit is
    /// strictly nearer than any unit hit, so the two hovers are exclusive.
    /// Drives the doodad highlight, the world-GO tooltip, and right-click use.</summary>
    private ulong _hoveredGameObjectGuid;

    /// <summary>
    /// The same axis basis CreatureRenderer applies to unit M2s — and the same
    /// linear part as DoodadRenderer.PlacementToWorld — mapping M2 model space
    /// onto server world space (X/Y horizontal, Z up).
    /// </summary>
    private static readonly Matrix4x4 GameObjectBasis = new(
        0f, -1f, 0f, 0f,
        0f, 0f, 1f, 0f,
        -1f, 0f, 0f, 0f,
        0f, 0f, 0f, 1f);

    private static Matrix4x4 DynamicM2GameObjectTransform(
        Vector3 position, float yaw, float scale) =>
        Matrix4x4.CreateScale(scale)
        * Matrix4x4.CreateRotationY(yaw + MathF.PI / 2f)
        * GameObjectBasis
        * Matrix4x4.CreateTranslation(position);

    private void EnsureGameObjectDisplays()
    {
        if (_gameObjectDisplaysAttempted || _mpq is null) return;
        _gameObjectDisplaysAttempted = true;
        byte[]? bytes = _mpq.ReadFile(GameObjectDisplayTable.MpqPath);
        _gameObjectDisplays = bytes is null ? null : GameObjectDisplayTable.Parse(bytes);
        if (_gameObjectDisplays is null)
            Console.WriteLine("[gameobject] GameObjectDisplayInfo.dbc unavailable - " +
                              "server gameobjects will not render");
    }

    /// <summary>
    /// Per-frame reconciliation of gameobject entities against DoodadRenderer's
    /// dynamic placements. Mirrors how CreatureRenderer consumes the entity
    /// store (a full walk each frame rather than create/destroy hooks): the
    /// store has no spawn events, and the walk is a few dozen entities.
    /// </summary>
    // A gameobject (mailbox, chest, brazier) is a dynamic M2 in the room it stands
    // in, lit like the room's own props: the floor's baked MOCV under it, or the
    // sky. WmoRenderer.ResolveInteriorLight is the one law; this converts to the
    // doodad payload (a = daylight blend). The building usually streams in AFTER
    // the server has spawned its gameobjects, so placements are re-asked whenever
    // a WMO becomes resident, and every couple of seconds for doors and hulls.
    private int _gameObjectLightVersion = -1;
    private float _gameObjectLightRefreshAt;
    private const float GameObjectLightRefreshSeconds = 2f;

    private Vector4 GameObjectInteriorLight(Vector3 position)
    {
        if (_wmo is null || _doodads is null || !_doodads.InteriorLighting || _interiorUnitLightProbeOff)
            return new Vector4(0f, 0f, 0f, 1f);
        return _wmo.ResolveInteriorLight(position, _terrain?.SampleHeight(position.X, position.Y))
            is Vector3 color
            ? new Vector4(color, 0f)
            : new Vector4(0f, 0f, 0f, 1f);
    }

    private void RefreshGameObjectInteriorLight()
    {
        if (_wmo is null || _doodads is null) return;
        bool worldChanged = _wmo.ResidentVersion != _gameObjectLightVersion || _interiorUnitLightProbeOff;
        if (!worldChanged && _worldTime - _gameObjectLightRefreshAt < GameObjectLightRefreshSeconds)
            return;
        _gameObjectLightVersion = _wmo.ResidentVersion;
        _gameObjectLightRefreshAt = _worldTime;
        foreach (var (guid, placed) in _gameObjectPlacements)
            _doodads.TrySetDynamicLight(guid, GameObjectInteriorLight(placed.Position));
    }

    private void UpdateGameObjectDoodads()
    {
        if (_doodads is null) return;

        // SMSG_DESTROY_OBJECT removes gameplay authority immediately, but an authored
        // AnimationData 157 placement remains until its exact one-shot window ends.
        _gameObjectPlacementScratch.Clear();
        foreach ((ulong guid, double retainedUntil) in _gameObjectRetainedDestroys)
            if (GameObjectAnimationLaw.RetentionFinished(
                    _doodads.NowSeconds, retainedUntil))
                _gameObjectPlacementScratch.Add(guid);
        foreach (ulong guid in _gameObjectPlacementScratch)
        {
            RemoveGameObjectPlacement(guid);
            _gameObjectRetainedDestroys.Remove(guid);
        }

        EnsureGameObjectDisplays();
        if (_gameObjectDisplays is null) return;

        RefreshGameObjectInteriorLight();

        foreach (WorldEntity e in _entities.Entities.Values)
        {
            if (!e.IsGameObject) continue;

            uint displayId = e.Fields.GameObjectDisplayId;
            bool tracked = _gameObjectPlacements.TryGetValue(e.Guid, out var placedSignature);

            // A type-15 boat/zeppelin keeps one cross-map timetable. During a
            // leg on another map the authoritative entity may remain streamed,
            // but it must not leave its last local-map model parked in view.
            if (_offMapTransports.Contains(e.Guid))
            {
                if (tracked) RemoveGameObjectPlacement(e.Guid);
                continue;
            }

            // REAL_PORTALS owns the complete presentation for the six stock
            // Mage portals. Their legacy display is InstancePortal.m2, a
            // particle-only narrow vortex which otherwise stacks inside the
            // large procedural aperture. Keep the authoritative WorldEntity --
            // queries, tooltip, selection and CMSG_GAMEOBJ_USE all key off it --
            // but do not publish its cosmetic M2 to DoodadRenderer. The full
            // procedural aperture remains the two-sided pick target.
            if (RealPortalsEnabled && IsPredictedMagePortal(e))
            {
                if (tracked) RemoveGameObjectPlacement(e.Guid);
                // Placement bookkeeping can be reset independently of particle
                // lifetime during a residency rebuild. Remove by exact owner on
                // every reconcile so an orphaned legacy vortex cannot linger
                // inside the procedural aperture after its model is suppressed.
                _particles?.RemoveOwnedEmitterPools(e.Guid);
                continue;
            }

            // Display 0 (not yet streamed, or authored invisible) and known-bad
            // displays render nothing; drop any stale placement from before.
            if (displayId == 0 || _gameObjectDisplaysUnrenderable.Contains(displayId))
            {
                if (tracked) RemoveGameObjectPlacement(e.Guid);
                continue;
            }

            float yaw = e.GameObjectFacing;
            float scale = e.Scale > 0.0001f ? e.Scale : 1f;
            var signature = (displayId, e.Position, yaw, scale);

            string? modelPath = _gameObjectDisplays.ModelPath(displayId);
            if (modelPath is null)
            {
                if (_gameObjectDisplaysUnrenderable.Add(displayId))
                    Console.WriteLine($"[gameobject] displayId {displayId} has no " +
                                      $"GameObjectDisplayInfo model (entry {e.Entry}) - not rendered");
                if (tracked) RemoveGameObjectPlacement(e.Guid);
                continue;
            }

            bool wmoModel = Path.GetExtension(modelPath).Equals(
                ".wmo", StringComparison.OrdinalIgnoreCase);
            if (tracked && placedSignature == signature &&
                (wmoModel ? _wmo?.HasDynamic(e.Guid) == true : _doodads.HasDynamic(e.Guid)))
            {
                if (wmoModel) SyncDynamicWmoGameObjectProps(e.Guid);
                continue;
            }

            if (wmoModel)
            {
                Matrix4x4 wmoTransform = WmoRenderer.DynamicGameObjectTransform(
                    e.Position, yaw, scale);
                if (tracked && placedSignature.DisplayId == displayId &&
                    _wmo?.TryUpdateDynamicTransform(e.Guid, wmoTransform) == true)
                {
                    _gameObjectPlacements[e.Guid] = signature;
                    SyncDynamicWmoGameObjectProps(e.Guid);
                    continue;
                }

                // A display swap can cross the M2/WMO boundary. Remove both
                // dynamic lanes before publishing the new owner.
                _doodads.RemoveDynamic(e.Guid);
                _doodads.RemoveDynamicWmoProps(e.Guid);
                WmoRenderer.DynamicPlacement result = _wmo?.AddDynamic(
                    e.Guid, modelPath, wmoTransform) ?? WmoRenderer.DynamicPlacement.Pending;
                if (result == WmoRenderer.DynamicPlacement.Placed)
                {
                    _gameObjectPlacements[e.Guid] = signature;
                    SyncDynamicWmoGameObjectProps(e.Guid);
                }
                else
                {
                    if (result == WmoRenderer.DynamicPlacement.Unavailable &&
                        _gameObjectDisplaysUnrenderable.Add(displayId))
                        Console.WriteLine($"[gameobject] WMO for displayId {displayId} " +
                                          $"unavailable: {modelPath} - not rendered");
                    _gameObjectPlacements.Remove(e.Guid);
                    _doodads.RemoveDynamicWmoProps(e.Guid);
                }
                continue;
            }

            // Same convention as CreatureRenderer's unit transform
            // (Scale * RotY(yaw + 90°) * Basis * Translate(pos)): a gameobject's
            // facing is a server yaw exactly like a creature's orientation.
            Matrix4x4 transform = DynamicM2GameObjectTransform(e.Position, yaw, scale);

            _wmo?.RemoveDynamic(e.Guid);
            _doodads.RemoveDynamicWmoProps(e.Guid);

            if (tracked && placedSignature.DisplayId == displayId &&
                _doodads.TryUpdateDynamicTransform(e.Guid, transform))
            {
                _gameObjectPlacements[e.Guid] = signature;
                continue;
            }

            switch (_doodads.AddDynamic(e.Guid, modelPath, transform,
                        liveCollision: _elevatorTransports.ContainsKey(e.Guid) ||
                            GameObjectAnimationLaw.CollisionFollowsState(e.GameObjectType),
                        light: GameObjectInteriorLight(e.Position)))
            {
                case DoodadRenderer.DynamicPlacement.Placed:
                    _gameObjectPlacements[e.Guid] = signature;
                    break;
                case DoodadRenderer.DynamicPlacement.Unavailable:
                    if (_gameObjectDisplaysUnrenderable.Add(displayId))
                        Console.WriteLine($"[gameobject] model for displayId {displayId} " +
                                          $"unavailable: {modelPath} - not rendered");
                    if (tracked) _gameObjectPlacements.Remove(e.Guid);
                    break;
                case DoodadRenderer.DynamicPlacement.Pending:
                    // Model still streaming; AddDynamic queued it. Retry next frame.
                    if (tracked) _gameObjectPlacements.Remove(e.Guid);
                    break;
            }
        }

        // Entities that despawned or left range (SMSG_UPDATE_OBJECT OutOfRange
        // removes them from the store) lose their placement.
        _gameObjectPlacementScratch.Clear();
        foreach (ulong guid in _gameObjectPlacements.Keys)
            if ((!_entities.TryGet(guid, out WorldEntity entity) || !entity.IsGameObject) &&
                !_gameObjectRetainedDestroys.ContainsKey(guid))
                _gameObjectPlacementScratch.Add(guid);
        foreach (ulong guid in _gameObjectPlacementScratch)
            RemoveGameObjectPlacement(guid);

        UpdateGameObjectStateAnimations();
    }

    private void UpdateGameObjectStateAnimations()
    {
        if (_doodads is null) return;
        foreach (WorldEntity go in _entities.Entities.Values)
        {
            if (!go.IsGameObject || !GameObjectAnimationLaw.Animates(go.GameObjectType))
                continue;

            uint wire = go.Fields.GameObjectState;
            if (!_gameObjectAnimationStates.TryGetValue(go.Guid, out var state))
            {
                state = new GameObjectAnimationState
                {
                    DisplayId = go.Fields.GameObjectDisplayId,
                    LastWire = wire,
                    ClientState = wire,
                };
                _gameObjectAnimationStates[go.Guid] = state;
            }
            else
            {
                if (state.DisplayId != go.Fields.GameObjectDisplayId)
                {
                    state.DisplayId = go.Fields.GameObjectDisplayId;
                    state.Shown = null;
                }
                if (state.LastWire != wire)
                {
                    state.LastWire = wire;
                    state.ClientState = wire;
                }
            }

            if (state.Shown == state.ClientState) continue;
            uint? previous = state.Shown;
            if (!_doodads.TryApplyDynamicStateAnimation(go.Guid, previous,
                    state.ClientState, out int animationId, out bool transition))
                continue; // model is still streaming; seed it when placement arrives

            state.Shown = state.ClientState;
            EmitInterface("gameobject", "state-animation",
                animationId >= 0 ? (transition ? "TRANSITION" : "REST") : "STATIC",
                go.Guid,
                $"previous={previous?.ToString() ?? "FIRST"};state={state.ClientState};" +
                $"animationData={animationId};type={go.GameObjectType}");
        }

        foreach (ulong stale in _gameObjectAnimationStates.Keys.Where(guid =>
                     !_entities.TryGet(guid, out WorldEntity entity) ||
                     !entity.IsGameObject ||
                     !GameObjectAnimationLaw.Animates(entity.GameObjectType)).ToArray())
            _gameObjectAnimationStates.Remove(stale);
    }

    private void PredictGameObjectAnimationState(ulong guid, uint clientState)
    {
        if (!_entities.TryGet(guid, out WorldEntity go) || !go.IsGameObject ||
            !GameObjectAnimationLaw.Animates(go.GameObjectType)) return;
        uint wire = go.Fields.GameObjectState;
        if (!_gameObjectAnimationStates.TryGetValue(guid, out var state))
        {
            state = new GameObjectAnimationState
            {
                DisplayId = go.Fields.GameObjectDisplayId,
                LastWire = wire,
                ClientState = wire,
            };
            _gameObjectAnimationStates[guid] = state;
        }
        state.ClientState = clientState;
    }

    private RayHit? ProbeStatefulGameObjectCollision(
        Vector3 origin, Vector3 direction, float maxDistance)
    {
        if (_doodads?.TryRaycastDynamicCollision(origin, direction, maxDistance,
                out _, out RayHit hit, guid =>
                    _entities.TryGet(guid, out WorldEntity go) && go.IsGameObject &&
                    GameObjectAnimationLaw.CollisionFollowsState(go.GameObjectType) &&
                    GameObjectAnimationLaw.ColliderIsSolid(go.Fields.GameObjectState)) == true)
            return hit;
        return null;
    }

    private void RemoveGameObjectPlacement(ulong guid)
    {
        _doodads?.RemoveDynamic(guid);
        _doodads?.RemoveDynamicWmoProps(guid);
        _wmo?.RemoveDynamic(guid);
        _gameObjectPlacements.Remove(guid);
    }

    private void SyncDynamicWmoGameObjectProps(ulong guid)
    {
        if (_wmo is null || _doodads is null) return;
        _gameObjectWmoPropScratch.Clear();
        foreach ((int propIndex, string modelPath, Matrix4x4 transform,
                     Vector4 light, int wmoInstanceId, int[] ownerGroups) in
                 _wmo.EnumerateDynamicDoodads(guid))
        {
            _gameObjectWmoPropScratch.Add(propIndex);
            if (_doodads.TryUpdateDynamicWmoPropTransform(guid, propIndex, transform))
                continue;
            _doodads.AddDynamicWmoProp(guid, propIndex, modelPath, transform,
                light, wmoInstanceId, ownerGroups);
        }
        _doodads.RemoveDynamicWmoPropsExcept(guid, _gameObjectWmoPropScratch);
    }

    /// <summary>
    /// The gameobject under a window pixel, mirroring PickUnit's shape: camera
    /// ray, nearest hit within <see cref="TargetPickDistance"/>, world collision
    /// strictly nearer wins. <paramref name="nearestUnitHit"/> is the unit
    /// picker's hit distance for the same pixel — a GO only picks when its hit
    /// is STRICTLY nearer, so a unit in front always beats the mailbox behind
    /// it (and a nameplate hit, distance 0, always beats everything).
    /// Only dynamic doodad placements are tested; scenery never picks.
    /// </summary>
    private ulong PickGameObject(Vector2 pixel, float nearestUnitHit, out float distance)
    {
        distance = float.PositiveInfinity;
        var ray = _window.Camera.ScreenPointToRay(pixel, _window.FramebufferSize);
        if (ray is null) return 0;
        (Vector3 origin, Vector3 direction) = ray.Value;

        float limit = MathF.Min(TargetPickDistance, nearestUnitHit);
        ulong guid = 0;
        float hit = limit;
        if (_doodads?.TryPickDynamic(origin, direction, hit,
                out ulong doodadGuid, out float doodadHit) == true)
        {
            guid = doodadGuid;
            hit = doodadHit;
        }
        if (_wmo?.TryPickDynamic(origin, direction, hit,
                out ulong wmoGuid, out float wmoHit) == true)
        {
            guid = wmoGuid;
            hit = wmoHit;
        }
        if (TryPickRealPortalAperture(origin, direction, hit,
                out ulong portalGuid, out float portalHit))
        {
            guid = portalGuid;
            hit = portalHit;
        }
        if (guid == 0) return 0;

        // The placement map can briefly outlive the entity store between the
        // despawn and the next reconcile; never hover a ghost.
        if (!_entities.TryGet(guid, out WorldEntity go) || !go.IsGameObject) return 0;

        if (!GameObjectMouseoverEligible(go)) return 0;

        // A genuinely nearer wall still occludes the GameObject. Flush-mounted signs and
        // mailboxes are a special geometric case: Stormwind's server prop intersects or sits
        // immediately against the static city WMO, so that supporting surface may be reported a
        // few centimetres before the prop's authored AABB. Do not let the host wall veto its own
        // sign, but retain normal through-wall rejection for every unrelated surface.
        if (_collision?.Raycast(origin, direction, hit) is { } worldHit &&
            worldHit.Distance < hit - 0.01f)
        {
            bool supportingSurface = _doodads?.IsWorldPointNearDynamicPickBounds(
                    guid, worldHit.Point, 0.35f) == true;
            if (!supportingSurface) return 0;
        }

        distance = hit;
        return guid;
    }
}

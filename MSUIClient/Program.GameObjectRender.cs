using System.Numerics;
using MSUIClient.Formats;
using MSUIClient.Net;
using MSUIClient.World.Doodads;

namespace MSUIClient;

// Server-gameobject RENDERING (2026-08-12). Everything the server spawns as a
// gameobject — Stormwind's shop signs, mailboxes, chests, herb/ore nodes — is
// invisible without this: those models sit in GameObjectDisplayInfo.dbc, not in
// any ADT/WMO placement list, and CreatureRenderer only draws units.
//
// Static placement only this pass: each visible gameobject entity gets one
// DoodadRenderer dynamic placement keyed by GUID, resynced every frame against
// the entity store (create/despawn/out-of-range, GAMEOBJECT_DISPLAYID changes,
// position moves, DoodadRenderer.ResetPlacements on a tile crossing). Doors,
// GO state/animation, traps and transports are explicitly out of scope — see
// docs/systems/SYSTEM_GAMEOBJECT_RENDERING.md.
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
    private void UpdateGameObjectDoodads()
    {
        if (_doodads is null) return;
        EnsureGameObjectDisplays();
        if (_gameObjectDisplays is null) return;

        foreach (WorldEntity e in _entities.Entities.Values)
        {
            if (!e.IsGameObject) continue;

            uint displayId = e.Fields.GameObjectDisplayId;
            bool tracked = _gameObjectPlacements.TryGetValue(e.Guid, out var placedSignature);

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
            if (tracked && placedSignature == signature && _doodads.HasDynamic(e.Guid)) continue;

            string? modelPath = _gameObjectDisplays.ModelPath(displayId);
            if (modelPath is null)
            {
                if (_gameObjectDisplaysUnrenderable.Add(displayId))
                    Console.WriteLine($"[gameobject] displayId {displayId} has no " +
                                      $"GameObjectDisplayInfo model (entry {e.Entry}) - not rendered");
                if (tracked) RemoveGameObjectPlacement(e.Guid);
                continue;
            }

            // Same convention as CreatureRenderer's unit transform
            // (Scale * RotY(yaw + 90°) * Basis * Translate(pos)): a gameobject's
            // facing is a server yaw exactly like a creature's orientation.
            Matrix4x4 transform = Matrix4x4.CreateScale(scale)
                * Matrix4x4.CreateRotationY(yaw + MathF.PI / 2f)
                * GameObjectBasis
                * Matrix4x4.CreateTranslation(e.Position);

            switch (_doodads.AddDynamic(e.Guid, modelPath, transform))
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
            if (!_entities.TryGet(guid, out WorldEntity entity) || !entity.IsGameObject)
                _gameObjectPlacementScratch.Add(guid);
        foreach (ulong guid in _gameObjectPlacementScratch)
            RemoveGameObjectPlacement(guid);
    }

    private void RemoveGameObjectPlacement(ulong guid)
    {
        _doodads?.RemoveDynamic(guid);
        _gameObjectPlacements.Remove(guid);
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
        if (_doodads is null) return 0;
        var ray = _window.Camera.ScreenPointToRay(pixel, _window.FramebufferSize);
        if (ray is null) return 0;
        (Vector3 origin, Vector3 direction) = ray.Value;

        float limit = MathF.Min(TargetPickDistance, nearestUnitHit);
        if (!_doodads.TryPickDynamic(origin, direction, limit, out ulong guid, out float hit))
            return 0;

        // The placement map can briefly outlive the entity store between the
        // despawn and the next reconcile; never hover a ghost.
        if (!_entities.TryGet(guid, out WorldEntity go) || !go.IsGameObject) return 0;

        // Same occlusion rule as PickUnit: static world geometry strictly nearer
        // than the hit blocks it. A GO's own collision hull can never block its
        // own pick - the ray always enters the AABB before reaching the hull.
        if (_collision?.Raycast(origin, direction, hit) is { } worldHit &&
            worldHit.Distance < hit - 0.01f)
            return 0;

        distance = hit;
        return guid;
    }
}

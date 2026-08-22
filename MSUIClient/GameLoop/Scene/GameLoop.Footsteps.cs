using System.Numerics;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

/// <summary>Benilla/current vanilla footstep event, surface and sound-kit chain.</summary>
public sealed partial class GameLoop
{
    private FootstepCatalog? _footsteps;

    private void WireFootstepPlayback()
    {
        if (_mpq is not null) _footsteps = FootstepCatalog.Load(_mpq);
        if (_creatures is not null)
            _creatures.FootstepAnimationEvent = PlayFootstepAnimationEvent;
        if (_character is not null)
            _character.FootstepAnimationEvent = () =>
            {
                ulong guid = ControlledGuid;
                if (guid == 0 || ControlledBodyIsStreamed || _controller is null ||
                    !_entities.TryGet(guid, out WorldEntity unit)) return;
                PlayFootstepAnimationEvent(guid, unit.DisplayId,
                    _controller.Position, unit.Scale);
            };
        Console.WriteLine(_footsteps is null
            ? "[footstep] terrain lookup unavailable"
            : $"[footstep] {_footsteps.Count} terrain/class lookup row(s)");
    }

    private void PlayFootstepAnimationEvent(
        ulong rootGuid, int displayId, Vector3 feet, float renderScale)
    {
        if (!_soundscapePlaybackArmed || _footsteps is null ||
            _creatureVoices is null || _spellSounds is null ||
            displayId <= 0 || !_entities.TryGet(rootGuid, out WorldEntity root)) return;

        uint moveFlags = rootGuid == ControlledGuid
            ? _movementSender.LastFlags : root.MoveFlags;
        if ((moveFlags & (uint)MovementFlags.Hover) != 0 ||
            root.Fields.UnitIsStealthed ||
            root.IsPlayer && root.Fields.PlayerIsGhost) return;

        if (!_creatureVoices.TryGet((uint)displayId, out CreatureVoice voice) ||
            voice.FootstepClass == 0) return;

        float? terrainZ = _terrain?.SampleHeight(feet.X, feet.Y);
        uint terrainType = 0;
        bool wmoOwnsColumn = _wmo?.TrySampleFootstepTerrain(
            feet, terrainZ, out terrainType) == true;
        if (!wmoOwnsColumn)
        {
            int? effect = _terrain?.SampleGroundEffect(feet.X, feet.Y);
            if (effect is not int id || !_footsteps.TryTerrainForEffect(id, out terrainType))
                return;
        }

        if (!_footsteps.TryResolveTerrain(voice.FootstepClass, terrainType, out var kits))
            return;

        // LiquidRenderer's query is ADT-only.  Never sample that lake through an
        // owning WMO floor; WMO-liquid depth remains dry until its retained mesh
        // gains an equivalent point query.
        float? depth = !wmoOwnsColumn &&
            _liquid?.TryGetSurface(feet.X, feet.Y, out float surface, out _) == true &&
            surface > feet.Z
                ? surface - feet.Z
                : null;
        float height = _creatureVoices.CollisionHeight((uint)displayId, renderScale);
        if (depth is float deep && deep > 0.75f * height) return;

        uint kit = depth is not null && kits.Splash != 0 ? kits.Splash : kits.Dry;
        if (kit == 0) return;
        Vector3 listener = _controller?.Position ?? feet;
        _spellSounds.Play(kit, rootGuid, feet, listener,
            forceLoop: false, trackHold: false, category: "sfx");
    }
}

using System.Numerics;
using MSUIClient.Net;
using MSUIClient.World.Sound;
using MSUIClient.World.Wmo;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private readonly record struct WaterSplashState(
        Vector3 Position, int DisplayId, float Scale, bool BeyondLine);

    private readonly Dictionary<ulong, WaterSplashState> _waterSplashStates = [];
    private readonly Dictionary<ulong, long> _waterSplashVoices = [];
    private readonly List<ulong> _waterSplashStale = [];

    private void RefreshRetainedWmoLiquid()
    {
        if (_liquid is not null && _wmo is not null)
            _liquid.UpdateWmoLiquid(_wmo.LiquidVersion, _wmo.EnumerateLiquid());
    }

    /// <summary>
    /// The body-scoped liquid query. An interior collision floor delegates to
    /// that placed WMO's MLIQ only; outdoors delegates to ADT only.
    /// </summary>
    private bool TryGetBodyLiquidSurface(Vector3 point, out float height, out byte type,
        bool waterOnly = false)
    {
        height = 0f;
        type = 0;
        if (_liquid is null) return false;
        RefreshRetainedWmoLiquid();
        int instanceId = 0;
        int groupIndex = -1;
        bool inside = _wmo?.TrySampleFootstepTerrain(point,
            _terrain?.SampleHeight(point.X, point.Y), out _, out instanceId,
            out groupIndex) == true;
        if (inside)
        {
            if (_wmo!.TryGetGroupLiquidOverride(instanceId, groupIndex, out byte floodedType))
            {
                if (waterOnly && !WmoLiquidPointLaw.IsWater(floodedType)) return false;
                height = float.MaxValue;
                type = floodedType;
                return true;
            }
            return _liquid.TryGetWmoSurface(
                point, instanceId, out height, out type, waterOnly);
        }
        return _liquid.TryGetSurface(point.X, point.Y, out height, out type) &&
               (!waterOnly || WmoLiquidPointLaw.IsWater(type));
    }

    /// <summary>The render eye has its own room claim, independent of the body.</summary>
    private bool TryGetEyeLiquidSurface(Vector3 eye, out float height, out byte type)
    {
        height = 0f;
        type = 0;
        if (_liquid is null) return false;
        RefreshRetainedWmoLiquid();
        if (_wmo?.CameraGroup is not { IsExterior: false } room)
            return _liquid.TryGetSurface(eye.X, eye.Y, out height, out type);
        if (_wmo.TryGetGroupLiquidOverride(
                room.InstanceId, room.GroupIndex, out byte floodedType))
        {
            height = float.MaxValue;
            type = floodedType;
            return true;
        }
        return _liquid.TryGetWmoSurface(eye, room.InstanceId, out height, out type);
    }

    /// <summary>
    /// Detect the dedicated 0.4-collision-height water edge for every moved unit. First sight
    /// arms silently; either direction thereafter plays the medium splash, unless that unit's
    /// preceding splash is still live. Liquid ownership is exclusive: an interior unit samples
    /// only that placed WMO's MLIQ, while an outdoor unit samples only ADT liquid.
    /// </summary>
    private void UpdateWaterSplashSounds()
    {
        if (_spellSounds is null || _creatureVoices is null || _liquid is null) return;

        foreach (WorldEntity unit in _entities.Units)
        {
            if (!unit.IsUnit || unit.DisplayId <= 0) continue;
            Vector3 position = unit.Guid == ControlledGuid && !ControlledBodyIsStreamed &&
                _controller is not null ? _controller.Position : unit.Position;
            bool armed = _waterSplashStates.TryGetValue(unit.Guid, out WaterSplashState previous);
            if (armed &&
                previous.Position == position && previous.DisplayId == unit.DisplayId &&
                previous.Scale == unit.Scale)
                continue;

            float? surface = TryGetBodyLiquidSurface(
                position, out float surfaceHeight, out _, waterOnly: true)
                    ? surfaceHeight : null;
            float collisionHeight = _creatureVoices.CollisionHeight(
                (uint)unit.DisplayId, unit.Scale);
            bool beyond = WaterSplashSoundLaw.BeyondSplashLine(
                surface, position.Z, collisionHeight);
            _waterSplashStates[unit.Guid] = new WaterSplashState(
                position, unit.DisplayId, unit.Scale, beyond);

            if (!armed ||
                !WaterSplashSoundLaw.Crossed(previous.BeyondLine, beyond))
                continue;
            if (_waterSplashVoices.TryGetValue(unit.Guid, out long current) &&
                _spellSounds.IsLive(current))
                continue;
            long voice = _spellSounds.Play(WaterSplashSoundLaw.MediumSplashKit,
                unit.Guid, position, _controller?.Position ?? position,
                forceLoop: false, trackHold: false, category: "sfx");
            if (voice != 0) _waterSplashVoices[unit.Guid] = voice;
        }

        _waterSplashStale.Clear();
        foreach (ulong guid in _waterSplashStates.Keys)
            if (!_entities.TryGet(guid, out _)) _waterSplashStale.Add(guid);
        foreach (ulong guid in _waterSplashStale)
        {
            _waterSplashStates.Remove(guid);
            if (_waterSplashVoices.Remove(guid, out long voice)) _spellSounds.Stop(voice);
        }
    }

    private void ResetWaterSplashSounds()
    {
        if (_spellSounds is not null)
            foreach (long voice in _waterSplashVoices.Values) _spellSounds.Stop(voice);
        _waterSplashVoices.Clear();
        _waterSplashStates.Clear();
        _waterSplashStale.Clear();
    }
}

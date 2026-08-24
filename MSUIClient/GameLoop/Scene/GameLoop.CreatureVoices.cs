using MSUIClient.Formats;
using MSUIClient.Net;
using MSUIClient.World.Sound;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private readonly Dictionary<ulong, bool> _knownCreatureReadsDead = [];
    private double _creatureStandVocalLastAt = double.NegativeInfinity;
    private readonly Dictionary<ulong, (uint Kit, long Voice)> _creatureBodyLoops = [];
    private ulong _hardLandingControlledGuid;
    private float? _controlledFallStartZ;

    private void WireCreatureAnimationVoices()
    {
        if (!AudioFeaturePolicy.ExpandedWorldAudioEnabled)
        {
            if (_creatures is not null) _creatures.CreatureAnimationSoundEvent = null;
            if (_character is not null) _character.CreatureAnimationSoundEvent = null;
            return;
        }
        if (_creatures is not null)
            _creatures.CreatureAnimationSoundEvent = PlayCreatureAnimationSoundEvent;
        if (_character is not null)
            _character.CreatureAnimationSoundEvent = identifier =>
            {
                ulong guid = ControlledGuid;
                if (guid == 0 || ControlledBodyIsStreamed || _controller is null ||
                    !_entities.TryGet(guid, out WorldEntity unit)) return;
                PlayCreatureAnimationSoundEvent(
                    guid, unit.DisplayId, _controller.Position, identifier);
            };
    }

    private void PlayCreatureAnimationSoundEvent(
        ulong rootGuid, int displayId, System.Numerics.Vector3 position, string identifier)
    {
        if (!_soundscapePlaybackArmed || _creatureVoices is null || _spellSounds is null ||
            displayId <= 0 || !_creatureVoices.TryGet((uint)displayId, out var voice)) return;

        if (identifier == "$FDX")
        {
            if (rootGuid == LocalPlayerGuid) return;
            uint roll = (uint)Random.Shared.NextInt64(0, 1L << 32);
            if (!World.Units.CreatureAnimationSoundLaw.StandChancePass(roll)) return;
            double now = NowSeconds();
            if (now < _creatureStandVocalLastAt + 10.0) return;
            // The one world-global window is consumed before the zero-kit bail.
            _creatureStandVocalLastAt = now;
        }

        uint kit = identifier switch
        {
            "$FD1" => voice.Fidget1Sound,
            "$FD2" => voice.Fidget2Sound,
            "$FD3" => voice.Fidget3Sound,
            "$FD4" => voice.Fidget4Sound,
            "$FDX" => voice.StandSound,
            "$WNG" => voice.WingFlapSound,
            "$WGG" => voice.WingGlideSound,
            _ => 0,
        };
        if (kit == 0) return;
        var listener = _controller?.Position ?? position;
        _spellSounds.Play(kit, rootGuid, position, listener,
            forceLoop: false, trackHold: false, category: "sfx");
    }

    /// <summary>
    /// Benilla's client-side landing predictor: retain the driven body's launch
    /// height, then play CreatureSoundData's ordinary injury row when the
    /// grounded edge reports a descent strictly greater than thirteen yards.
    /// Flying and control changes are discontinuities, never fall arcs.
    /// </summary>
    private void ObserveControlledHardLanding(
        bool wasGrounded,
        bool nowGrounded,
        bool wasFlying,
        bool nowFlying,
        float previousZ,
        float currentZ)
    {
        if (!AudioFeaturePolicy.ExpandedWorldAudioEnabled)
        {
            ResetControlledHardLandingArc();
            return;
        }
        ulong guid = ControlledGuid;
        if (guid == 0 || wasFlying || nowFlying || guid != _hardLandingControlledGuid)
        {
            _hardLandingControlledGuid = guid;
            _controlledFallStartZ = null;
            return;
        }

        var step = World.Units.HardLandingSoundLaw.Step(
            !wasGrounded, !nowGrounded, _controlledFallStartZ, previousZ, currentZ);
        _controlledFallStartZ = step.StartZ;
        if (step.Descent is not float descent ||
            !World.Units.HardLandingSoundLaw.IsHardLanding(descent) ||
            !_soundscapePlaybackArmed || _creatureVoices is null || _spellSounds is null ||
            !_entities.TryGet(guid, out WorldEntity unit) || !unit.IsUnit ||
            unit.DisplayId <= 0 ||
            !_creatureVoices.TryGet((uint)unit.DisplayId, out CreatureVoice voice) ||
            voice.InjurySound == 0) return;

        var position = _controller?.Position ?? unit.Position;
        _spellSounds.Play(voice.InjurySound, guid, position, position,
            forceLoop: false, trackHold: false, category: "sfx");
    }

    private void ResetControlledHardLandingArc()
    {
        _hardLandingControlledGuid = ControlledGuid;
        _controlledFallStartZ = null;
    }

    /// <summary>
    /// Exact live edge for CreatureSoundData.DeathID. A first-sighted corpse is
    /// only seeded; health death, Feign Death's dynamic bit, and stand-state 7
    /// all enter through ObjectFields.ReadsDead.
    /// </summary>
    private void ObserveCreatureDeathVoice(ulong guid, bool? previousReadsDead)
    {
        if (!AudioFeaturePolicy.ExpandedWorldAudioEnabled)
        {
            _knownCreatureReadsDead.Remove(guid);
            return;
        }
        if (!_entities.TryGet(guid, out WorldEntity unit) || !unit.IsUnit)
        {
            _knownCreatureReadsDead.Remove(guid);
            return;
        }

        bool dead = unit.Fields.ReadsDead;
        bool? previous = previousReadsDead;
        if (previous is null && _knownCreatureReadsDead.TryGetValue(guid, out bool known))
            previous = known;
        _knownCreatureReadsDead[guid] = dead;
        if (previous != false || !dead || !_soundscapePlaybackArmed ||
            _creatureVoices is null || _spellSounds is null ||
            !_creatureVoices.TryGet((uint)unit.DisplayId, out var voice) ||
            voice.DeathSound == 0) return;

        var listener = _controller?.Position ?? unit.Position;
        _spellSounds.Play(voice.DeathSound, guid, unit.Position, listener,
            forceLoop: false, trackHold: false, category: "sfx");
    }

    private void ForgetCreatureVoiceState(IEnumerable<ulong> guids)
    {
        foreach (ulong guid in guids)
        {
            _knownCreatureReadsDead.Remove(guid);
            if (_creatureBodyLoops.Remove(guid, out var loop)) _spellSounds?.Stop(loop.Voice);
        }
    }

    private void UpdateCreatureBodyLoops()
    {
        if (!AudioFeaturePolicy.ExpandedWorldAudioEnabled)
        {
            StopCreatureBodyLoops();
            return;
        }
        if (!_soundscapePlaybackArmed || _creatureVoices is null || _spellSounds is null)
        {
            foreach (var loop in _creatureBodyLoops.Values) _spellSounds?.Stop(loop.Voice);
            _creatureBodyLoops.Clear();
            return;
        }

        HashSet<ulong> seen = [];
        System.Numerics.Vector3 listener = _controller?.Position ?? default;
        foreach (WorldEntity unit in _entities.Units)
        {
            if (!unit.IsUnit) continue;
            seen.Add(unit.Guid);
            int displayId = unit.MountDisplayId > 0 ? unit.MountDisplayId : unit.DisplayId;
            uint desired = unit.Fields.Health > 0 && !unit.Fields.ReadsDead &&
                displayId > 0 && _creatureVoices.TryGet((uint)displayId, out var voice)
                    ? voice.LoopSound : 0;

            if (_creatureBodyLoops.TryGetValue(unit.Guid, out var armed) &&
                (desired != armed.Kit || !_spellSounds.IsLive(armed.Voice)))
            {
                _spellSounds.Stop(armed.Voice);
                _creatureBodyLoops.Remove(unit.Guid);
            }
            if (desired == 0 || _creatureBodyLoops.ContainsKey(unit.Guid)) continue;

            // Match the reference's out-of-range no-allocation/retry behavior.
            if (_spellSounds.TryGetEntry(desired, out var entry) &&
                entry.CutoffDistance > 0 &&
                System.Numerics.Vector3.Distance(unit.Position, listener) >= entry.CutoffDistance)
                continue;
            long id = _spellSounds.Play(desired, unit.Guid, unit.Position, listener,
                forceLoop: true, trackHold: false, category: "sfx");
            if (id != 0) _creatureBodyLoops[unit.Guid] = (desired, id);
        }

        foreach (ulong stale in _creatureBodyLoops.Keys
                     .Where(guid => !seen.Contains(guid)).ToArray())
        {
            _spellSounds.Stop(_creatureBodyLoops[stale].Voice);
            _creatureBodyLoops.Remove(stale);
        }
    }

    private void StopCreatureBodyLoops()
    {
        foreach (var loop in _creatureBodyLoops.Values) _spellSounds?.Stop(loop.Voice);
        _creatureBodyLoops.Clear();
    }
}

using System.Collections.Concurrent;
using System.Numerics;
using MSUIClient.Formats;
using MSUIClient.World.Sound;

namespace MSUIClient.World.Spells;

/// <summary>
/// SPELL AND MISSILE AUDIO POLICY. What a cast, an impact or a persistent area
/// effect sounds like, where in the world it is, and how long it is allowed to
/// keep sounding.
///
/// It owns three things and no others:
///   * POSITIONAL GAIN - the SoundEntries min/cutoff distance falloff against the
///     listener, re-evaluated every frame for looping voices.
///   * UNIT HOLDS - one looping voice per unit, so a re-cast replaces its own
///     loop instead of stacking a second copy on the same caster.
///   * The play journal the audio protocols assert against.
///
/// It owns NO device: files, threads, MCI and the master mix belong to
/// <see cref="AudioMixer"/>, and which file a kit resolves to belongs to
/// <see cref="SoundKitLibrary"/>. Both are shared with the world soundscape, which
/// used to have to reach them through this class - the reason the split exists.
/// </summary>
public sealed class SpellSoundSystem
{
    /// <summary>Per-voice state the positional loop needs. Game-thread owned; the
    /// mixer's liveness is what retires entries.</summary>
    private readonly record struct SpellVoice(SoundEntry Entry, ulong Unit, string Category);

    private readonly AudioMixer _mixer;
    private readonly SoundKitLibrary _library;

    private readonly ConcurrentDictionary<long, SpellVoice> _loopingVoices = new();

    /// <summary>One looping voice per unit. Game-thread owned: every play and stop
    /// enters through this class, and the mixer preserves request order, so
    /// stopping the old hold here lands ahead of the new play there.</summary>
    private readonly Dictionary<ulong, long> _holds = [];

    /// <summary>Last volume actually sent per positional looping voice, so a
    /// standing voice does not re-send an identical gain every frame.</summary>
    private readonly Dictionary<long, (int Volume, int Pan)> _sentVolume = [];
    private Vector3 _listenerPosition;
    private float _listenerYaw;
    private bool _listenerSet;

    public SpellSoundSystem(AudioMixer mixer, SoundKitLibrary library)
    {
        _mixer = mixer;
        _library = library;
    }

    public long Plays => _mixer.Plays;
    public string LastCue => _library.LastCue;

    public IReadOnlyList<AudioMixer.SoundPlayJournalEntry> JournalSnapshot()
        => _mixer.JournalSnapshot();

    /// <summary>Describe an authored cue without exposing the catalog owner.</summary>
    public bool TryGetEntry(uint? soundId, out SoundEntry entry)
        => _library.TryGet(soundId, out entry);

    public bool IsAuthoredLoop(uint? soundId) => _library.IsAuthoredLoop(soundId);

    public bool IsLive(long voiceId) => _mixer.IsLive(voiceId);

    /// <summary>Current character-head listener pose; orientation is character facing.</summary>
    public void SetListener(Vector3 position, float yaw)
    {
        if (!AudioFeaturePolicy.ExpandedWorldAudioEnabled)
        {
            _listenerSet = false;
            return;
        }
        _listenerPosition = position;
        _listenerYaw = yaw;
        _listenerSet = true;
    }

    public long Play(uint? soundId, ulong unit, Vector3 source, Vector3 listener,
        bool forceLoop = false, bool trackHold = true, string category = "spell")
    {
        if (!_library.TryGet(soundId, out SoundEntry entry) || entry.Variants.Count == 0) return 0;
        return PlayResolved(soundId!.Value.ToString(), category, entry, unit, source, listener,
            forceLoop, trackHold, variation: null);
    }

    public long PlayVariant(uint soundId, int variation, ulong unit, Vector3 source,
        Vector3 listener, bool forceLoop = false, bool trackHold = true,
        string category = "spell")
    {
        if (!_library.TryGet(soundId, out SoundEntry entry) ||
            (uint)variation >= (uint)entry.Variants.Count) return 0;
        return PlayResolved(soundId.ToString(), category, entry, unit, source, listener,
            forceLoop, trackHold, variation);
    }

    public long Play(string soundName, ulong unit, Vector3 source, Vector3 listener,
        string category = "ui")
    {
        if (!_library.TryGet(soundName, out SoundEntry entry) || entry.Variants.Count == 0) return 0;
        return PlayResolved(soundName, category, entry, unit, source, listener,
            forceLoop: false, trackHold: false, variation: null);
    }

    /// <summary>
    /// Preview/play a creator-owned WAV or MP3 before it exists in an MPQ or in
    /// SoundEntries.dbc. The virtual MPQ path is also the path written by the
    /// creator exporter, so live preview and the produced patch name the same
    /// asset. The ordinary positional gain and lifecycle machinery are
    /// deliberately reused.
    /// </summary>
    public long PlayCustom(string requestedCue, string virtualPath, byte[] bytes,
        ulong unit, Vector3 source, Vector3 listener, float volume, bool looping,
        bool noDuplicates, float minDistance, float cutoffDistance,
        bool trackHold = false, string category = "creator", uint extraFlags = 0,
        uint eax = 0)
    {
        if (string.IsNullOrWhiteSpace(virtualPath) || bytes.Length == 0) return 0;
        virtualPath = virtualPath.Replace('/', '\\');
        _mixer.RegisterCustomFile(virtualPath, bytes);
        uint flags = extraFlags & ~(0x200u | 0x20u);
        flags |= (looping ? 0x200u : 0u) | (noDuplicates ? 0x20u : 0u);
        var entry = new SoundEntry(0, 1, requestedCue,
            [new SoundVariant(virtualPath, 1)],
            Math.Clamp(volume, 0f, 1f), flags,
            Math.Max(0f, minDistance), Math.Max(0f, cutoffDistance), eax);
        return PlayResolved(requestedCue, category, entry, unit, source, listener,
            forceLoop: false, trackHold: trackHold, variation: null);
    }

    public void RemoveCustomFile(string virtualPath) => _mixer.RemoveCustomFile(virtualPath);

    private long PlayResolved(string requestedCue, string category, in SoundEntry entry,
        ulong unit, Vector3 source, Vector3 listener, bool forceLoop, bool trackHold,
        int? variation)
    {
        SoundVariant variant = variation is int exact
            ? _library.PickVariantAt(entry, exact)
            : _library.PickVariant(entry);
        bool looping = forceLoop || entry.Looping;

        // Replace this unit's own loop BEFORE the new one is requested, so the
        // mixer runs the stop first and the two never overlap.
        if (looping && trackHold && _holds.Remove(unit, out long held)) _mixer.Stop(held);

        Vector3 effectiveListener = _listenerSet ? _listenerPosition : listener;
        float gain = Gain(entry, source, effectiveListener) * _mixer.CategoryAmp(category);
        float pan = AudioFeaturePolicy.ExpandedWorldAudioEnabled &&
                    entry.CutoffDistance > 0f
            ? SpatialAudioLaw.Pan(source, effectiveListener, _listenerYaw) : 0f;
        long voiceId = _mixer.Play(new AudioPlayRequest(
            variant.Path, category, gain, looping,
            RequestedCue: requestedCue, SoundId: entry.Id, Owner: unit, TrackHold: trackHold,
            Pan: pan));
        if (voiceId == 0) return 0;

        if (looping)
        {
            _loopingVoices[voiceId] = new SpellVoice(entry, unit, category);
            if (trackHold) _holds[unit] = voiceId;
        }
        return voiceId;
    }

    public void Stop(long voiceId)
    {
        if (voiceId == 0) return;
        _loopingVoices.TryRemove(voiceId, out _);
        _sentVolume.Remove(voiceId);
        _mixer.Stop(voiceId);
    }

    public void StopHold(ulong unit)
    {
        if (_holds.Remove(unit, out long voiceId)) Stop(voiceId);
    }

    /// <summary>
    /// Game-thread pulse: gain math only, plus retiring what the mixer says is gone.
    /// Everything expensive is the mixer's problem.
    /// </summary>
    public void Tick(Vector3 listener, Func<ulong, (bool Found, Vector3 Position)> unitPosition)
    {
        foreach ((long id, SpellVoice voice) in _loopingVoices)
        {
            if (!_mixer.IsLive(id))
            {
                _loopingVoices.TryRemove(id, out _);
                _sentVolume.Remove(id);
                continue;
            }
            (bool found, Vector3 position) = unitPosition(voice.Unit);
            if (!found) { Stop(id); continue; }
            Vector3 effectiveListener = _listenerSet ? _listenerPosition : listener;
            float gain = Gain(voice.Entry, position, effectiveListener) *
                _mixer.CategoryAmp(voice.Category);
            if (gain <= 0) { Stop(id); continue; }
            float pan = AudioFeaturePolicy.ExpandedWorldAudioEnabled &&
                        voice.Entry.CutoffDistance > 0f
                ? SpatialAudioLaw.Pan(position, effectiveListener, _listenerYaw) : 0f;

            // Only when the quantized value moved. Every mciSendString is a
            // synchronous cross-process call made on the one thread that also pumps
            // DirectShow's notification window, so a standing looping voice
            // re-sending an IDENTICAL volume every frame put a few hundred blocking
            // calls a second in front of that pump.
            int volume = (int)Math.Clamp(gain * 1000f, 0, 1000);
            int panLevel = (int)Math.Clamp(pan * 1000f, -1000f, 1000f);
            if (_sentVolume.TryGetValue(id, out var last) &&
                last == (volume, panLevel)) continue;
            _sentVolume[id] = (volume, panLevel);
            if (AudioFeaturePolicy.ExpandedWorldAudioEnabled)
                _mixer.SetVoiceGainPan(id, gain, pan);
            else
                _mixer.SetVoiceGain(id, gain);
        }

        // A hold whose voice finished on its own leaves a dead id behind; stopping
        // one is harmless, but let them go rather than accumulate per unit.
        foreach (ulong unit in _holds.Keys.ToArray())
            if (!_mixer.IsLive(_holds[unit])) _holds.Remove(unit);
    }

    private static float Gain(in SoundEntry entry, Vector3 source, Vector3 listener)
    {
        if (AudioFeaturePolicy.ExpandedWorldAudioEnabled)
            return SpatialAudioLaw.Gain(entry.Volume, entry.MinDistance,
                entry.CutoffDistance, source, listener);

        // Preserve the attenuation law from the last known-clean audio build.
        // The expanded pass replaced this with inverse rolloff; switching that
        // law while the old per-voice WinMM backend is still active makes moving
        // loops pump much more sharply than the established client did.
        float volume = float.IsFinite(entry.Volume)
            ? Math.Clamp(entry.Volume, 0f, 1f) : 1f;
        if (entry.CutoffDistance <= 0) return volume;
        float distance = Vector3.Distance(source, listener);
        if (distance >= entry.CutoffDistance) return 0;
        if (distance <= entry.MinDistance) return volume;
        float span = Math.Max(.001f, entry.CutoffDistance - entry.MinDistance);
        return volume * (1f - (distance - entry.MinDistance) / span);
    }
}

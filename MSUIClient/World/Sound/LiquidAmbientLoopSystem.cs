using System.Numerics;
using MSUIClient.Formats;

namespace MSUIClient.World.Sound;

/// <summary>Pure geometry/timing constants for the reference liquid-loop driver.</summary>
public static class LiquidAmbientLoopLaw
{
    public const float TriggerRadius = 9f;
    public const float SlewPerTick = 1f / 6f;
    public const float NearClampDistance = 5.892557f;
    public const float FadeSeconds = 5f;
    public const int MaxConcurrent = 2;

    public static Vector3 NearClamp(Vector3 point, Vector3 player)
    {
        Vector3 delta = point - player;
        float length = delta.Length();
        if (length >= NearClampDistance) return point;
        if (length > 1e-4f) return player + delta * (NearClampDistance / length);
        return player + Vector3.UnitX * NearClampDistance;
    }

    public static Vector3 Slew(Vector3 current, Vector3 target)
    {
        Vector3 delta = target - current;
        float length = delta.Length();
        return length > SlewPerTick
            ? current + delta * (SlewPerTick / length)
            : target;
    }

    public static float FadeStep(double deltaSeconds) => FadeSeconds <= 0f
        ? 1f : MathF.Max(0f, (float)deltaSeconds) / FadeSeconds;
}

/// <summary>
/// The four-class above-water liquid ambience transport. It owns movable,
/// force-looped ambience voices directly over the shared mixer; spell unit holds
/// and zone-bed slots remain independent.
/// </summary>
public sealed class LiquidAmbientLoopSystem
{
    private sealed class LoopVoice
    {
        public required uint Kit;
        public required SoundEntry Entry;
        public long Voice;
        public float Envelope;
        public int SentVolume = -1;
        public int SentPan = int.MinValue;
    }

    private sealed class ClassLoop
    {
        public required Vector3 Position;
        public required LoopVoice Current;
        public LoopVoice? Retiring;
    }

    private readonly AudioMixer _mixer;
    private readonly SoundKitLibrary _library;
    private readonly SoundWaterTypeCatalog? _waterSounds;
    private readonly ClassLoop?[] _classes = new ClassLoop?[4];
    private bool _wasUnderwater;
    private double _lastUpdateAt = -1;

    public LiquidAmbientLoopSystem(AudioMixer mixer, SoundKitLibrary library, MpqMount mpq)
    {
        _mixer = mixer;
        _library = library;
        _waterSounds = SoundWaterTypeCatalog.Load(mpq);
    }

    public int CatalogCount => _waterSounds?.Count ?? 0;

    public void Update(double now, Vector3 player, Vector3 listener, float listenerYaw,
        IReadOnlyList<LiquidRenderer.LiquidSoundCandidate?> nearest, bool submergedInWater)
    {
        // No backend is a startup fact. Do not turn a silent CI/probe run into a
        // failed resolve/play attempt every frame.
        if (!_mixer.Audible) return;

        double delta = _lastUpdateAt < 0 ? 0 : Math.Max(0, now - _lastUpdateAt);
        _lastUpdateAt = now;

        // The reference hard-stops these shoreline layers below water. Magma and
        // slime submersion do not take this edge; only water/ocean does.
        if (submergedInWater)
        {
            StopAllVoices();
            _wasUnderwater = true;
            return;
        }

        bool resurfaced = _wasUnderwater;
        _wasUnderwater = false;
        float fadeStep = LiquidAmbientLoopLaw.FadeStep(delta);
        int budget = LiquidAmbientLoopLaw.MaxConcurrent;

        for (int liquidClass = 0; liquidClass < _classes.Length; liquidClass++)
        {
            LiquidRenderer.LiquidSoundCandidate? candidate =
                liquidClass < nearest.Count && budget > 0 ? nearest[liquidClass] : null;
            uint desiredKit = 0;
            SoundEntry desiredEntry = default;
            if (candidate is { } found &&
                _waterSounds?.TryGetKit(found.Nibble, out desiredKit) == true &&
                _library.TryGet(desiredKit, out desiredEntry) &&
                desiredEntry.Variants.Count > 0)
                budget--;
            else
                candidate = null;

            ClassLoop? loop = _classes[liquidClass];
            if (loop is null && candidate is { } arm)
            {
                Vector3 position = LiquidAmbientLoopLaw.NearClamp(arm.Point, player);
                var voice = NewVoice(desiredKit, desiredEntry, resurfaced ? 1f : 0f);
                loop = new ClassLoop { Position = position, Current = voice };
                _classes[liquidClass] = loop;
            }
            else if (loop is not null && candidate is { } active)
            {
                Vector3 target = LiquidAmbientLoopLaw.NearClamp(active.Point, player);
                loop.Position = LiquidAmbientLoopLaw.Slew(loop.Position, target);

                if (loop.Current.Kit != desiredKit)
                {
                    if (loop.Retiring is not null) StopVoice(loop.Retiring);
                    loop.Retiring = loop.Current;
                    loop.Current = NewVoice(desiredKit, desiredEntry, 0f);
                }

                loop.Current.Envelope = resurfaced
                    ? 1f : MathF.Min(1f, loop.Current.Envelope + fadeStep);
            }
            else if (loop is not null)
            {
                loop.Current.Envelope -= fadeStep;
                if (loop.Current.Envelope <= 0f)
                {
                    StopVoice(loop.Current);
                    if (loop.Retiring is not null) StopVoice(loop.Retiring);
                    _classes[liquidClass] = null;
                    continue;
                }
            }

            loop = _classes[liquidClass];
            if (loop is null) continue;

            EnsureVoice(loop.Current, loop.Position, listener, listenerYaw, liquidClass);
            PushMix(loop.Current, loop.Position, listener, listenerYaw);

            if (loop.Retiring is not null)
            {
                loop.Retiring.Envelope -= fadeStep;
                if (loop.Retiring.Envelope <= 0f)
                {
                    StopVoice(loop.Retiring);
                    loop.Retiring = null;
                }
                else
                    PushMix(loop.Retiring, loop.Position, listener, listenerYaw);
            }
        }
    }

    public void Reset()
    {
        StopAllVoices();
        _wasUnderwater = false;
        _lastUpdateAt = -1;
    }

    private static LoopVoice NewVoice(uint kit, SoundEntry entry, float envelope) => new()
    {
        Kit = kit,
        Entry = entry,
        Envelope = envelope,
    };

    private void EnsureVoice(LoopVoice voice, Vector3 source, Vector3 listener,
        float listenerYaw, int liquidClass)
    {
        if (voice.Voice != 0 && _mixer.IsLive(voice.Voice)) return;
        StartVoice(voice, source, listener, listenerYaw, liquidClass);
    }

    private void StartVoice(LoopVoice voice, Vector3 source, Vector3 listener,
        float listenerYaw, int liquidClass)
    {
        SoundVariant variant = _library.PickVariant(voice.Entry);
        float gain = VoiceGain(voice, source, listener);
        float pan = SpatialAudioLaw.Pan(source, listener, listenerYaw);
        voice.Voice = _mixer.Play(new AudioPlayRequest(
            variant.Path, "ambience", gain, Looping: true,
            RequestedCue: $"liquid-{voice.Kit}", SoundId: voice.Kit,
            Owner: (ulong)(liquidClass + 1), StartWhenSilent: true, Announce: true,
            Pan: pan));
        voice.SentVolume = (int)Math.Clamp(gain * 1000f, 0, 1000);
        voice.SentPan = (int)Math.Clamp(pan * 1000f, -1000, 1000);
    }

    private void PushMix(LoopVoice voice, Vector3 source, Vector3 listener, float listenerYaw)
    {
        if (voice.Voice == 0) return;
        float gain = VoiceGain(voice, source, listener);
        float pan = SpatialAudioLaw.Pan(source, listener, listenerYaw);
        int volume = (int)Math.Clamp(gain * 1000f, 0, 1000);
        int panLevel = (int)Math.Clamp(pan * 1000f, -1000, 1000);
        if (volume == voice.SentVolume && panLevel == voice.SentPan) return;
        voice.SentVolume = volume;
        voice.SentPan = panLevel;
        _mixer.SetVoiceGainPan(voice.Voice, gain, pan);
    }

    private float VoiceGain(LoopVoice voice, Vector3 source, Vector3 listener) =>
        SpatialAudioLaw.Gain(voice.Entry.Volume, voice.Entry.MinDistance,
            voice.Entry.CutoffDistance, source, listener) *
        _mixer.CategoryAmp("ambience") * Math.Clamp(voice.Envelope, 0f, 1f);

    private void StopVoice(LoopVoice voice)
    {
        if (voice.Voice != 0) _mixer.Stop(voice.Voice);
        voice.Voice = 0;
        voice.SentVolume = -1;
        voice.SentPan = int.MinValue;
    }

    private void StopAllVoices()
    {
        for (int i = 0; i < _classes.Length; i++)
        {
            if (_classes[i] is not { } loop) continue;
            StopVoice(loop.Current);
            if (loop.Retiring is not null) StopVoice(loop.Retiring);
            _classes[i] = null;
        }
    }
}

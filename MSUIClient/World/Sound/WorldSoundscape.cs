using MSUIClient.Formats;

namespace MSUIClient.World.Sound;

/// <summary>
/// The ambient world soundscape: zone music and the zone/interior ambience bed.
///
/// This is the layer whose absence made the world feel mute - spells and UI
/// clicked, but Elwynn never played its forest theme and Loch Modan never had
/// birds. All laws here are the 1.12 client's own, verified against the
/// benilla reference (sound/zone.rs) and the shipped DBCs:
///
/// MUSIC TRANSPORT
///   - Day phase is a hard step: day iff 05:30 &lt;= clock &lt; 21:00. No fade.
///   - A zone-music-id CHANGE fades the outgoing track over 4000 ms and starts
///     the incoming one IMMEDIATELY at full volume. The randomized silence
///     interval is only same-zone track-to-track spacing - reusing it on a
///     zone change is the bug that leaves fresh zones silent for minutes.
///   - On a track's natural end, the next starts after
///     uniform(SilenceMin, SilenceMax)[phase] milliseconds.
///   - The session's very first track waits 6 s after the world goes live.
///   - Intro music (ZoneIntroMusicTable) preempts the zone track on entry,
///     throttled per row by MinDelayMinutes.
///
/// AMBIENCE
///   - One bed at a time. Priority: submerged (kit 4123) &gt; WMO interior row
///     &gt; terrain area row, each resolved [day, night].
///   - Bed changes crossfade over 5000 ms - EXCEPT submerge/emerge, which is
///     instant in the reference.
///   - Beds are force-looped: the SoundAmbience kits are the loop authority
///     even where the SoundEntries flag word omits 0x200.
///
/// Voices run straight on the shared <see cref="AudioMixer"/>: this class owns the
/// whole gain product (entry volume x category amp x envelope) and pushes it only
/// when the quantized MCI value actually changes.
/// </summary>
public sealed class WorldSoundscape
{
    private const float MusicFadeOutSeconds = 4.0f;
    private const float AmbienceFadeSeconds = 5.0f;
    private const float FirstTrackDelaySeconds = 6.0f;
    private const uint UnderwaterLoopKit = 4123;

    private readonly AudioMixer _mixer;
    private readonly SoundKitLibrary _library;
    private readonly ZoneMusicTable? _zoneMusic;
    private readonly SoundAmbienceTable? _soundAmbience;
    private readonly ZoneIntroMusicTable? _introMusic;
    private readonly AreaTableCatalog? _areas;

    // ── per-frame inputs, set by the game loop before Update ─────────────────

    /// <summary>Current AreaTable id at the player (0 = unknown).</summary>
    public uint AreaId { get; set; }

    /// <summary>WMO interior overrides (0 = none). Nonzero fields beat the
    /// terrain area's chain, matching WMOAreaTable precedence.</summary>
    public uint InteriorZoneMusicId { get; set; }
    public uint InteriorAmbienceId { get; set; }
    public uint InteriorIntroSoundId { get; set; }

    /// <summary>Hard day/night step: day iff 05:30 &lt;= clock &lt; 21:00.</summary>
    public bool DayPhase { get; set; } = true;

    /// <summary>Head under a liquid surface: ambience becomes the underwater
    /// loop, instantly.</summary>
    public bool Submerged { get; set; }

    // ── music state ──────────────────────────────────────────────────────────

    private uint _currentZoneMusicId;
    private long _musicVoice;
    private uint _musicKit;
    private float _musicEntryVolume = 1f;
    private double _nextTrackAt = double.MaxValue;
    private bool _sessionStarted;
    private long _fadingMusicVoice;
    private float _fadingMusicEntryVolume;
    private double _musicFadeStartedAt;
    private readonly Dictionary<uint, double> _introPlayedAt = [];
    private int _musicSentVolume = -1;
    private int _fadeSentVolume = -1;

    // ── ambience state ───────────────────────────────────────────────────────

    private uint _ambienceKit;
    private long _ambienceVoice;
    private float _ambienceEntryVolume = 1f;
    private double _ambienceStartedAt;
    private long _fadingAmbienceVoice;
    private float _fadingAmbienceEntryVolume;
    private double _ambienceFadeStartedAt;
    private int _ambienceSentVolume = -1;
    private int _ambienceFadeSentVolume = -1;

    public string Status { get; private set; } = "idle";

    public WorldSoundscape(AudioMixer mixer, SoundKitLibrary library, MpqMount mpq)
    {
        _mixer = mixer;
        _library = library;
        _zoneMusic = ParseTable(mpq, ZoneMusicTable.MpqPath, ZoneMusicTable.Parse);
        _soundAmbience = ParseTable(mpq, SoundAmbienceTable.MpqPath, SoundAmbienceTable.Parse);
        _introMusic = ParseTable(mpq, ZoneIntroMusicTable.MpqPath, ZoneIntroMusicTable.Parse);
        _areas = ParseTable(mpq, AreaTableCatalog.MpqPath, AreaTableCatalog.Parse);
    }

    private static T? ParseTable<T>(MpqMount mpq, string path, Func<byte[], T?> parse)
        where T : class
    {
        try
        {
            byte[]? bytes = mpq.ReadFile(path);
            return bytes is null ? null : parse(bytes);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[soundscape] {path} failed to load - {ex.Message}");
            return null;
        }
    }

    /// <summary>Reset on leaving the world (logout, map change to loading).</summary>
    public void Reset()
    {
        StopVoice(ref _musicVoice);
        StopVoice(ref _fadingMusicVoice);
        StopVoice(ref _ambienceVoice);
        StopVoice(ref _fadingAmbienceVoice);
        _currentZoneMusicId = 0;
        _musicKit = 0;
        _ambienceKit = 0;
        _sessionStarted = false;
        _nextTrackAt = double.MaxValue;
        _musicSentVolume = _fadeSentVolume = _ambienceSentVolume = _ambienceFadeSentVolume = -1;
        Status = "idle";
    }

    private void StopVoice(ref long voice)
    {
        if (voice != 0) _mixer.Stop(voice);
        voice = 0;
    }

    /// <summary>Start one of this class's own channels: 2D, unowned, and started at
    /// an explicit gain so a crossfade can begin at silence.</summary>
    private long PlayKit(uint kit, string category, bool forceLoop, float startGain)
    {
        if (!_library.TryGet(kit, out SoundEntry entry) || entry.Variants.Count == 0) return 0;
        SoundVariant variant = _library.PickVariant(entry);
        return _mixer.Play(new AudioPlayRequest(
            variant.Path, category, Math.Clamp(startGain, 0f, 1f), forceLoop || entry.Looping,
            RequestedCue: kit.ToString(), SoundId: kit,
            StartWhenSilent: true, Announce: true));
    }

    public void Update(double now)
    {
        if (!_sessionStarted)
        {
            _sessionStarted = true;
            _nextTrackAt = now + FirstTrackDelaySeconds;
        }

        UpdateMusic(now);
        UpdateAmbience(now);
    }

    // ── music ────────────────────────────────────────────────────────────────

    private void UpdateMusic(double now)
    {
        (uint areaAmbience, uint areaMusic, uint areaIntro) =
            _areas?.ResolveAudio(AreaId) ?? (0u, 0u, 0u);
        _ = areaAmbience;
        uint desired = InteriorZoneMusicId != 0 ? InteriorZoneMusicId : areaMusic;

        if (desired != _currentZoneMusicId)
        {
            // Zone change: old track fades over 4 s, new starts NOW, full.
            if (_musicVoice != 0)
            {
                StopVoice(ref _fadingMusicVoice);
                _fadingMusicVoice = _musicVoice;
                _fadingMusicEntryVolume = _musicEntryVolume;
                _musicFadeStartedAt = now;
                _fadeSentVolume = -1;
                _musicVoice = 0;
            }
            _currentZoneMusicId = desired;
            _musicKit = 0;
            if (desired != 0)
            {
                uint intro = InteriorIntroSoundId != 0 ? InteriorIntroSoundId : areaIntro;
                if (!TryStartIntro(intro, now)) StartZoneTrack(now);
            }
        }

        // Fade-out envelope for the outgoing track.
        if (_fadingMusicVoice != 0)
        {
            float t = (float)((now - _musicFadeStartedAt) / MusicFadeOutSeconds);
            if (t >= 1f || !_mixer.IsLive(_fadingMusicVoice))
            {
                StopVoice(ref _fadingMusicVoice);
            }
            else
            {
                PushGain(_fadingMusicVoice,
                    _fadingMusicEntryVolume * _mixer.CategoryAmp("music") * (1f - t),
                    ref _fadeSentVolume);
            }
        }

        if (_currentZoneMusicId == 0) return;

        if (_musicVoice != 0)
        {
            if (_mixer.IsLive(_musicVoice))
            {
                // Keep the playing track's volume live against the sliders.
                PushGain(_musicVoice, _musicEntryVolume * _mixer.CategoryAmp("music"),
                    ref _musicSentVolume);
                return;
            }

            // Natural end: schedule the next track after the authored silence.
            _musicVoice = 0;
            if (_zoneMusic?.TryGet(_currentZoneMusicId, out ZoneMusicEntry set) == true)
            {
                uint minMs = set.SilenceMinMs(DayPhase);
                uint maxMs = Math.Max(set.SilenceMaxMs(DayPhase), minMs);
                double silence = (minMs + Random.Shared.NextDouble() * (maxMs - minMs)) / 1000.0;
                _nextTrackAt = now + silence;
                Status = $"music silent {silence:F0}s (set {_currentZoneMusicId})";
            }
            return;
        }

        if (now >= _nextTrackAt) StartZoneTrack(now);
    }

    private void StartZoneTrack(double now)
    {
        _nextTrackAt = double.MaxValue;
        if (_zoneMusic?.TryGet(_currentZoneMusicId, out ZoneMusicEntry set) != true) return;
        StartMusicKit(set.Sound(DayPhase), $"zone set {set.Id} '{set.SetName}'");
        _ = now;
    }

    private bool TryStartIntro(uint introId, double now)
    {
        if (introId == 0 ||
            _introMusic?.TryGet(introId, out ZoneIntroMusicEntry intro) != true ||
            intro.SoundId == 0) return false;
        double last = _introPlayedAt.GetValueOrDefault(introId, double.MinValue);
        if (now - last < intro.MinDelayMinutes * 60.0) return false;
        _introPlayedAt[introId] = now;
        StartMusicKit(intro.SoundId, $"intro {intro.Id} '{intro.Name}'");
        return true;
    }

    private void StartMusicKit(uint kit, string why)
    {
        if (kit == 0) return;
        _musicEntryVolume = _library.TryGet(kit, out SoundEntry entry)
            ? Math.Clamp(entry.Volume, 0f, 1f) : 1f;
        float gain = _musicEntryVolume * _mixer.CategoryAmp("music");
        _musicVoice = PlayKit(kit, "music", forceLoop: false, gain);
        _musicKit = kit;
        _musicSentVolume = (int)Math.Clamp(gain * 1000f, 0, 1000);
        Status = $"music kit {kit} ({why})";
        Console.WriteLine($"[soundscape] {Status}");
    }

    // ── ambience ─────────────────────────────────────────────────────────────

    private void UpdateAmbience(double now)
    {
        uint desired = DesiredAmbienceKit();

        if (desired != _ambienceKit)
        {
            // Submerge/emerge swaps instantly; everything else crossfades 5 s.
            bool instant = Submerged || _ambienceKit == UnderwaterLoopKit;

            if (_ambienceVoice != 0)
            {
                if (instant)
                {
                    StopVoice(ref _ambienceVoice);
                }
                else
                {
                    StopVoice(ref _fadingAmbienceVoice);
                    _fadingAmbienceVoice = _ambienceVoice;
                    _fadingAmbienceEntryVolume = _ambienceEntryVolume;
                    _ambienceFadeStartedAt = now;
                    _ambienceFadeSentVolume = -1;
                    _ambienceVoice = 0;
                }
            }

            _ambienceKit = desired;
            if (desired != 0)
            {
                _ambienceEntryVolume = _library.TryGet(desired, out SoundEntry entry)
                    ? Math.Clamp(entry.Volume, 0f, 1f) : 1f;
                float start = instant
                    ? _ambienceEntryVolume * _mixer.CategoryAmp("ambience") : 0f;
                _ambienceVoice = PlayKit(desired, "ambience", forceLoop: true, start);
                _ambienceStartedAt = instant ? now - AmbienceFadeSeconds : now;
                _ambienceSentVolume = -1;
                Console.WriteLine($"[soundscape] ambience kit {desired}" +
                                  (instant ? " (instant)" : " (crossfade)"));
            }
        }

        if (_fadingAmbienceVoice != 0)
        {
            float t = (float)((now - _ambienceFadeStartedAt) / AmbienceFadeSeconds);
            if (t >= 1f) StopVoice(ref _fadingAmbienceVoice);
            else PushGain(_fadingAmbienceVoice,
                _fadingAmbienceEntryVolume * _mixer.CategoryAmp("ambience") * (1f - t),
                ref _ambienceFadeSentVolume);
        }

        if (_ambienceVoice != 0)
        {
            float t = Math.Clamp((float)((now - _ambienceStartedAt) / AmbienceFadeSeconds), 0f, 1f);
            PushGain(_ambienceVoice,
                _ambienceEntryVolume * _mixer.CategoryAmp("ambience") * t,
                ref _ambienceSentVolume);
        }
    }

    private uint DesiredAmbienceKit()
    {
        if (Submerged) return UnderwaterLoopKit;

        uint rowId = InteriorAmbienceId;
        if (rowId == 0 && _areas is not null) (rowId, _, _) = _areas.ResolveAudio(AreaId);
        if (rowId == 0) return 0;
        return _soundAmbience?.TryGet(rowId, out SoundAmbienceEntry row) == true
            ? row.Kit(DayPhase) : 0;
    }

    /// <summary>Push a gain to the worker only when the quantized MCI volume
    /// actually changed - mciSendString is a synchronous call and 60 Hz of
    /// identical set-volume commands would be pure waste.</summary>
    private void PushGain(long voice, float gain, ref int sentVolume)
    {
        int volume = (int)Math.Clamp(gain * 1000f, 0, 1000);
        if (volume == sentVolume) return;
        sentVolume = volume;
        _mixer.SetVoiceGain(voice, gain);
    }
}

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
/// when the quantized renderer target actually changes.
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

    /// <summary>The WMO/zonetext indoor verdict. Outdoor weather replaces the
    /// area ambience; indoors the WMO/area bed wins and the storm goes quiet.</summary>
    public bool Interior { get; set; }

    /// <summary>Last SMSG_WEATHER SoundEntries loop kit (0 = clear).</summary>
    public uint WeatherAmbienceKit { get; set; }

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
    private bool _musicReconciled;
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
    private double _ambienceRetryAt;
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
        _musicReconciled = false;
        _ambienceRetryAt = 0;
        _nextTrackAt = double.MaxValue;
        _musicSentVolume = _fadeSentVolume = _ambienceSentVolume = _ambienceFadeSentVolume = -1;
        Status = "idle";
    }

    private void StopVoice(ref long voice)
    {
        if (voice != 0) _mixer.Stop(voice);
        voice = 0;
    }

    /// <summary>Start a streamed zone bed. Benilla's pick_stream path uses weighted
    /// selection and entry volume, but deliberately bypasses ordinary channel flags
    /// such as NO_DUPLICATES and VARY_PITCH. Music plays naturally; ambience is
    /// force-looped by its transport.</summary>
    private long PlayStreamKit(uint kit, string category, bool forceLoop, float startGain)
    {
        if (!_library.TryGet(kit, out SoundEntry entry) || entry.Variants.Count == 0) return 0;
        SoundVariant variant = _library.PickVariant(entry);
        return _mixer.Play(new AudioPlayRequest(
            variant.Path, category, Math.Clamp(startGain, 0f, 1f), forceLoop,
            RequestedCue: kit.ToString(), SoundId: kit,
            StartWhenSilent: true, Announce: true));
    }

    /// <summary>Ordinary flat SoundEntries playback (SMSG_PLAY_SOUND), including
    /// the process-wide 0x20 gate and authored pitch law.</summary>
    private long PlayOrdinaryKit(uint kit, string category, float startGain)
    {
        if (!_library.TryGet(kit, out SoundEntry entry) || entry.Variants.Count == 0) return 0;
        long noDuplicateReservation = entry.NoDuplicates
            ? _mixer.TryReserveNoDuplicate(entry.Id, entry.Variants[0].Path) : 0;
        if (entry.NoDuplicates && noDuplicateReservation == 0) return 0;
        try
        {
            SoundVariant variant = _library.PickVariant(entry);
            uint playbackFrequency = entry.VaryPitch
                ? SoundVariationLaw.NextPitchFrequency() : 0;
            return _mixer.Play(new AudioPlayRequest(
                variant.Path, category, Math.Clamp(startGain, 0f, 1f),
                entry.Looping,
                RequestedCue: kit.ToString(), SoundId: kit,
                StartWhenSilent: true, Announce: true,
                PlaybackFrequency: playbackFrequency, NoDuplicates: entry.NoDuplicates,
                NoDuplicateReservation: noDuplicateReservation));
        }
        catch
        {
            _mixer.ReleaseNoDuplicateReservation(noDuplicateReservation);
            throw;
        }
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
        bool firstReconciliation = !_musicReconciled;
        _musicReconciled = true;
        (uint areaAmbience, uint areaMusic, uint areaIntro) =
            _areas?.ResolveAudio(AreaId) ?? (0u, 0u, 0u);
        _ = areaAmbience;
        uint desired = InteriorZoneMusicId != 0 ? InteriorZoneMusicId : areaMusic;

        // MUTE IS A TRANSPORT GATE, NOT ONLY A GAIN GATE. PlayStreamKit asks for
        // StartWhenSilent, which
        // defeats the mixer's own "never open a device for an inaudible request" rule - so with
        // music off the client still resolved a kit, decoded the whole mp3 and burned the track at
        // gain 0, then scheduled another multi-minute silence behind it. Unchecking the box
        // therefore appeared to do nothing until the cycle happened to come round again, which is
        // most of what "Enable Music is inconsistent" was. SpellSoundSystem already retires its
        // looping voices at zero gain; this is the same rule for the music lane.
        bool musicAudible = _mixer.CategoryAmp("music") > 0f;

        if (desired != _currentZoneMusicId)
        {
            // Later zone changes start immediately. On the first reconciliation,
            // an eligible intro also starts immediately; without an intro, keep
            // the reference client's six-second cold-start deadline.
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
                // Zone identity is tracked even while muted, so unmuting starts the track for the
                // zone you are actually standing in. The intro is NOT consumed here when muted:
                // TryStartIntro stamps a per-row throttle that would otherwise be spent on a
                // performance nobody heard.
                if (musicAudible)
                {
                    uint intro = InteriorIntroSoundId != 0 ? InteriorIntroSoundId : areaIntro;
                    if (!TryStartIntro(intro, now) && !firstReconciliation)
                        StartZoneTrack(now);
                }
                else _nextTrackAt = now;
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

        // Muted: retire the transport outright and hold the next start at NOW, so unmuting is
        // audible immediately instead of landing wherever the silence cycle had drifted to.
        if (!musicAudible)
        {
            StopVoice(ref _musicVoice);
            _musicKit = 0;
            _musicSentVolume = -1;
            if (_currentZoneMusicId != 0) _nextTrackAt = now;
            Status = "music muted";
            return;
        }

        // The live-gain push sits ABOVE the zone-id guard on purpose. PlayServerMusic puts a kit
        // on this same slot without setting _currentZoneMusicId, so in an area with no ZoneMusic
        // row the old ordering skipped the refresh entirely: a server-pushed track ignored the
        // Music slider and could never be retired. Any live music voice is refreshed here now,
        // whatever put it there.
        if (_musicVoice != 0)
        {
            if (_mixer.IsLive(_musicVoice))
            {
                // Keep the playing track's volume live against the sliders.
                PushGain(_musicVoice, _musicEntryVolume * _mixer.CategoryAmp("music"),
                    ref _musicSentVolume);
                return;
            }

            // The voice is gone. That is USUALLY a natural end, but it is also what a failed
            // start looks like from here - Play registers the id at request time and every worker
            // failure path (missing file, undecodable mp3, waveOut refusal) removes it again. The
            // two are indistinguishable at this point, so say so rather than reporting silence as
            // if the track had played.
            _musicVoice = 0;
            _musicKit = 0;
            if (_currentZoneMusicId != 0 &&
                _zoneMusic?.TryGet(_currentZoneMusicId, out ZoneMusicEntry set) == true)
            {
                uint minMs = set.SilenceMinMs(DayPhase);
                uint maxMs = Math.Max(set.SilenceMaxMs(DayPhase), minMs);
                double silence = (minMs + Random.Shared.NextDouble() * (maxMs - minMs)) / 1000.0;
                _nextTrackAt = now + silence;
                Status = $"music silent {silence:F0}s (set {_currentZoneMusicId})";
            }
            return;
        }

        if (_currentZoneMusicId == 0) return;

        if (now >= _nextTrackAt) StartZoneTrack(now);
    }

    private void StartZoneTrack(double now)
    {
        _nextTrackAt = double.MaxValue;
        if (_zoneMusic?.TryGet(_currentZoneMusicId, out ZoneMusicEntry set) != true)
        {
            // Parking is correct here - this zone id has no ZoneMusic row, so there is nothing to
            // retry - but it used to happen mutely, leaving "this zone has no music" and "the
            // music system is wedged" looking identical from the console.
            Status = $"music has no ZoneMusic row for set {_currentZoneMusicId}";
            Console.WriteLine($"[soundscape] {Status}");
            return;
        }
        StartMusicKit(set.Sound(DayPhase), $"zone set {set.Id} '{set.SetName}'", now);
    }

    private bool TryStartIntro(uint introId, double now)
    {
        if (introId == 0 ||
            _introMusic?.TryGet(introId, out ZoneIntroMusicEntry intro) != true ||
            intro.SoundId == 0) return false;
        double last = _introPlayedAt.GetValueOrDefault(introId, double.MinValue);
        if (now - last < intro.MinDelayMinutes * 60.0) return false;
        if (!StartMusicKit(intro.SoundId, $"intro {intro.Id} '{intro.Name}'", now))
            return false;
        _introPlayedAt[introId] = now;
        return true;
    }

    private bool StartMusicKit(uint kit, string why, double now)
    {
        if (kit == 0) return false;
        // The single choke point for every music start - zone track, zone intro, and the
        // server's own PlayServerMusic push. UpdateMusic already refuses to start a track while
        // the category is silent, but PlayServerMusic arrives from the packet handler and would
        // otherwise still resolve a kit and decode an mp3 that nobody can hear, for the one frame
        // before the transport retired it again.
        if (_mixer.CategoryAmp("music") <= 0f)
        {
            Status = $"music kit {kit} suppressed while muted ({why})";
            return false;
        }
        if (!_library.TryGet(kit, out SoundEntry entry) || entry.Variants.Count == 0)
        {
            Status = $"music kit {kit} UNRESOLVED ({why})";
            Console.WriteLine($"[soundscape] {Status}");
            return false;
        }
        _musicEntryVolume = Math.Clamp(entry.Volume, 0f, 1f);
        float gain = _musicEntryVolume * _mixer.CategoryAmp("music");

        _musicVoice = PlayStreamKit(kit, "music", forceLoop: false, gain);
        _musicKit = kit;
        _musicSentVolume = (int)Math.Clamp(gain * 1000f, 0, 1000);
        if (_musicVoice == 0)
        {
            // The row is valid (checked above), so zero here is a transient route
            // refusal. Retry shortly; never leave a valid zone parked forever.
            if (_currentZoneMusicId != 0) _nextTrackAt = now + 0.25;
            Status = $"music kit {kit} busy; retry scheduled ({why})";
            Console.WriteLine($"[soundscape] {Status}");
            return false;
        }
        Status = $"music kit {kit} ({why})";
        Console.WriteLine($"[soundscape] {Status}");
        return true;
    }

    /// <summary>
    /// Put a server-pushed SoundEntries kit on the one music slot. Event emitters
    /// re-push the same id every few seconds to keep a finished track alive, so a
    /// repeat is ignored only while that exact kit is still playing. A different
    /// push takes the slot immediately while the outgoing stream receives the
    /// ordinary four-second music fade.
    /// </summary>
    public void PlayServerMusic(uint kit, double now)
    {
        if (kit == 0 ||
            (_musicKit == kit && _musicVoice != 0 && _mixer.IsLive(_musicVoice))) return;

        if (_musicVoice != 0)
        {
            if (_mixer.IsLive(_musicVoice))
            {
                StopVoice(ref _fadingMusicVoice);
                _fadingMusicVoice = _musicVoice;
                _fadingMusicEntryVolume = _musicEntryVolume;
                _musicFadeStartedAt = now;
                _fadeSentVolume = -1;
            }
            _musicVoice = 0;
        }

        StartMusicKit(kit, "server push", now);
    }

    /// <summary>Play the flat SFX form of SMSG_PLAY_SOUND through the shared
    /// SoundEntries resolver and effects-volume lane.</summary>
    public long PlayServerSound2d(uint kit)
    {
        if (!_library.TryGet(kit, out SoundEntry entry) || entry.Variants.Count == 0) return 0;
        float gain = Math.Clamp(entry.Volume, 0f, 1f) * _mixer.CategoryAmp("sfx");
        return PlayOrdinaryKit(kit, "sfx", gain);
    }

    // ── ambience ─────────────────────────────────────────────────────────────

    private void UpdateAmbience(double now)
    {
        uint desired = DesiredAmbienceKit();

        if (desired != _ambienceKit && now >= _ambienceRetryAt)
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

            // No main voice owns the old identity after the move above. Commit
            // the desired kit only after PlayStreamKit actually routes it;
            // otherwise a transient backend refusal would make later frames
            // believe a bed existed and suppress every retry.
            _ambienceKit = 0;
            _ambienceRetryAt = 0;
            if (desired != 0)
            {
                _ambienceEntryVolume = _library.TryGet(desired, out SoundEntry entry)
                    ? Math.Clamp(entry.Volume, 0f, 1f) : 1f;
                float start = instant
                    ? _ambienceEntryVolume * _mixer.CategoryAmp("ambience") : 0f;
                _ambienceVoice = PlayStreamKit(
                    desired, "ambience", forceLoop: true, start);
                if (_ambienceVoice != 0)
                {
                    _ambienceKit = desired;
                    _ambienceRetryAt = 0;
                    _ambienceStartedAt = instant ? now - AmbienceFadeSeconds : now;
                    _ambienceSentVolume = -1;
                    Console.WriteLine($"[soundscape] ambience kit {desired}" +
                                      (instant ? " (instant)" : " (crossfade)"));
                }
                else
                {
                    _ambienceRetryAt = now + 0.25;
                    Status = $"ambience kit {desired} busy; retry scheduled";
                }
            }
        }

        if (_fadingAmbienceVoice != 0)
        {
            float t = (float)((now - _ambienceFadeStartedAt) / AmbienceFadeSeconds);
            if (t >= 1f || !_mixer.IsLive(_fadingAmbienceVoice))
                StopVoice(ref _fadingAmbienceVoice);
            else PushGain(_fadingAmbienceVoice,
                _fadingAmbienceEntryVolume * _mixer.CategoryAmp("ambience") * (1f - t),
                ref _ambienceFadeSentVolume);
        }

        if (_ambienceVoice != 0)
        {
            if (!_mixer.IsLive(_ambienceVoice))
            {
                _ambienceVoice = 0;
                _ambienceKit = 0;
                _ambienceSentVolume = -1;
                _ambienceRetryAt = now + 0.25;
            }
            else
            {
                float t = Math.Clamp(
                    (float)((now - _ambienceStartedAt) / AmbienceFadeSeconds), 0f, 1f);
                PushGain(_ambienceVoice,
                    _ambienceEntryVolume * _mixer.CategoryAmp("ambience") * t,
                    ref _ambienceSentVolume);
            }
        }
    }

    private uint DesiredAmbienceKit()
    {
        if (Submerged) return UnderwaterLoopKit;
        if (!Interior && WeatherAmbienceKit != 0) return WeatherAmbienceKit;

        uint rowId = InteriorAmbienceId;
        if (rowId == 0 && _areas is not null) (rowId, _, _) = _areas.ResolveAudio(AreaId);
        if (rowId == 0) return 0;
        return _soundAmbience?.TryGet(rowId, out SoundAmbienceEntry row) == true
            ? row.Kit(DayPhase) : 0;
    }

    /// <summary>Push a gain to the control worker only when the quantized renderer
    /// target changed. Re-sending identical frame-rate state is pure queue churn.</summary>
    private void PushGain(long voice, float gain, ref int sentVolume)
    {
        int volume = (int)Math.Clamp(gain * 1000f, 0, 1000);
        if (volume == sentVolume) return;
        sentVolume = volume;
        _mixer.SetVoiceGain(voice, gain);
    }
}

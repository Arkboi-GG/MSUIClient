using System.Numerics;
using MSUIClient.Engine.UI;
using MSUIClient.Net;
using MSUIClient.World;
using MSUIClient.World.Sound;
using MSUIClient.World.Wmo;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// World-soundscape wiring: feeds the WorldSoundscape (zone music + ambience)
// its per-frame inputs and runs it every frame, in every mode that has a live
// world - including creator mode, where the vanilla HUD (and therefore the
// minimap's own area resolution) does not draw. That is why this file resolves
// the area and WMO interior itself at 4 Hz instead of reading _minimapAreaId:
// the soundscape must not fall silent just because the minimap is hidden.
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class GameLoop
{
    private WorldSoundscape? _soundscape;
    private double _soundscapeNextResolveAt;
    private uint _soundscapeAreaId;
    private (uint Music, uint Ambience, uint Intro) _soundscapeInterior;
    private bool _soundscapeIndoors;
    private uint _weatherSoundKit;
    private readonly WeatherVisualLaw _weatherVisual = new();
    private WeatherPrecipitationRenderer? _weatherPrecipitation;
    private readonly Dictionary<ulong, int> _knownMountSoundDisplays = [];
    private const string DismountSoundName = "SpiritWolf (DONOTRENAME)";
    private bool _soundscapePlaybackArmed;
    private bool _audioSelfTestStarted;
    private int _soundscapeSettledFrames;
    private double _soundscapeArmDeadline;
    private long _glueMusicVoice;
    private double _glueMusicFadeStartedAt = -1;
    private bool _audioCompatibilityAnnounced;

    /// <summary>Consecutive settled frames required before the first voices of a
    /// world are allowed to exist. Small: this is a "the loader let go" test, not
    /// a smoothness measurement.</summary>
    private const int SoundscapeSettledFrames = 8;

    /// <summary>The world must never end up permanently mute because something
    /// streams forever. Past this, arm regardless and take the risk.</summary>
    private const double SoundscapeArmTimeoutSeconds = 6.0;

    /// <summary>Runs every Update, before the loading-state early returns, so
    /// leaving the world (logout, loading curtain) resets the transport instead
    /// of stranding a looping bed in the glue screen.</summary>
    private void UpdateWorldSoundscape()
    {
        // The soundscape is a caller of the shared audio device, not of the spell
        // system, so these two are what it actually needs.
        if (_audioMixer is null || _soundKits is null) return;

        // Device housekeeping (retiring finished one-shots) belongs to the device
        // and runs whatever the world is doing. It used to hang off the spell
        // system's per-frame tick, which meant a music track could only be noticed
        // as finished on frames where spell audio happened to be ticked.
        _audioMixer.PollFinished();
        // A multi-companion order answers as a cascade; the tail plays from here.
        UpdateCompanionVoicePending();
        if (!AudioFeaturePolicy.ExpandedWorldAudioEnabled && !_audioCompatibilityAnnounced)
        {
            _audioCompatibilityAnnounced = true;
            Console.WriteLine("[audio] known-clean producer set active; " +
                              "expanded creature/GO/liquid emitters quarantined");
        }
        UpdateGlueAudio();

        // SoundListenerAtCharacter=1: ordinary play listens from the avatar's head and facing,
        // independent of camera zoom/orbit. Detached free view falls back to the camera pose.
        Vector3 listenerPosition;
        float listenerYaw;
        if (_controller is not null && !_freeView)
        {
            listenerPosition = SpatialAudioLaw.CharacterListener(_controller.Position);
            listenerYaw = _controller.Yaw;
        }
        else
        {
            listenerPosition = _window.Camera.Position;
            listenerYaw = _window.Camera.ViewYaw;
        }
        if (AudioFeaturePolicy.ExpandedWorldAudioEnabled)
            _spellSounds?.SetListener(listenerPosition, listenerYaw);

        bool inWorld = _terrain is not null && _worldLoadStarted && !_worldLoading &&
                       !GlueFrontDoorActive && _controller is not null;
        if (!inWorld)
        {
            StopCreatureBodyLoops();
            ResetWaterSplashSounds();
            _liquidAmbient?.Reset();
            _knownMountSoundDisplays.Clear();
            _soundscape?.Reset();
            _soundscapePlaybackArmed = false;
            _soundscapeSettledFrames = 0;
            _soundscapeArmDeadline = 0;
            return;
        }

        // The mount-up attach is silent. Only a live nonzero->zero edge plays
        // the fixed global dismount kit; first sight merely seeds the observer.
        if (AudioFeaturePolicy.ExpandedWorldAudioEnabled)
            ObserveDismountSoundTransitions(_soundscapePlaybackArmed);

        // DO NOT LET THE FIRST MCI/DirectShow VOICES COME INTO EXISTENCE WHILE THE
        // LOADER STILL OWNS THE MACHINE. On Windows a graph built under that
        // contention can stay choppy for its whole life, long after the machine is
        // idle again - the defect does not clear when the cause does, which is what
        // makes it worth refusing to start rather than trying to recover.
        //
        // Focus was the first half of this law and it was not enough: the curtain
        // lifts while the DBC readers, the font-atlas rebuild and the doodad and
        // collision streams are all still running, and both voices of a zone were
        // being opened into exactly that. So the test is now "the loader let go",
        // measured as a few consecutive frames with nothing left in flight. Once a
        // world is armed, ordinary background playback stays allowed.
        if (!_soundscapePlaybackArmed)
        {
            if (!_window.IsFocused) { _soundscapeSettledFrames = 0; return; }
            double armNow = NowSeconds();
            if (_soundscapeArmDeadline <= 0) _soundscapeArmDeadline = armNow + SoundscapeArmTimeoutSeconds;
            bool expired = armNow >= _soundscapeArmDeadline;
            bool loaderBusy = _collisionBuildTask is not null ||
                              (_doodads?.PendingPreloads ?? 0) > 0 ||
                              (_wmo?.PendingPreloads ?? 0) > 0;
            if (loaderBusy) _soundscapeSettledFrames = 0;
            else _soundscapeSettledFrames++;
            if (_soundscapeSettledFrames < SoundscapeSettledFrames && !expired) return;
            _soundscapePlaybackArmed = true;
            Console.WriteLine(expired && _soundscapeSettledFrames < SoundscapeSettledFrames
                ? $"[audio] world playback armed after {SoundscapeArmTimeoutSeconds:F0}s " +
                  "(streaming never settled - voices may open under load)"
                : "[audio] world settled; playback armed");
        }

        // MSUI_AUDIO_TONE=1 replaces the world's own audio with the self test, so
        // the only thing playing is a signal that cannot be starved by this
        // process. Diagnostic only; nothing reads it unless it is asked for.
        if (!_audioSelfTestStarted &&
            Environment.GetEnvironmentVariable("MSUI_AUDIO_TONE") == "1")
        {
            _audioSelfTestStarted = true;
            _audioMixer.PlayTestTone();
            return;
        }
        if (_audioSelfTestStarted) return;

        if (_soundscape is null)
        {
            if (_mpq is null) return;
            _soundscape = new WorldSoundscape(_audioMixer, _soundKits, _mpq);
        }

        double now = NowSeconds();
        var feet = _controller!.Position;

        // Area + interior identity, at 4 Hz: the WMO containment ray is the
        // expensive half, and zone transitions are human-speed events.
        if (now >= _soundscapeNextResolveAt)
        {
            _soundscapeNextResolveAt = now + 0.25;
            EnsureAreaTableForMinimap();
            EnsureWmoAreaTableForMinimap();

            var probe = feet + new Vector3(0f, 0f, 1.7f);
            float? terrainZ = _terrain!.SampleHeight(probe.X, probe.Y);
            uint areaId = 0;
            _soundscapeInterior = (0, 0, 0);
            _soundscapeIndoors = false;

            if (_wmo?.ResolveAreaMinimapIdentity(feet, terrainZ) is { } interior)
            {
                _soundscapeIndoors = true;
                if (_wmoAreas?.Resolve(interior.RootWmoId, interior.NameSetId,
                        interior.GroupWmoId) is { } row)
                {
                    _soundscapeInterior = (row.ZoneMusicId, row.AmbienceId, row.IntroSoundId);
                    areaId = row.AreaTableId;
                }
            }

            if (areaId == 0)
            {
                var projection = MinimapProjection.FromWorld(feet);
                if (_adts?.TryPeek(projection.TileColumn, projection.TileRow, out var adt) == true)
                    areaId = projection.AreaId(adt);
            }
            if (areaId != 0) _soundscapeAreaId = areaId;
        }

        // Submerged means the HEAD is under a liquid surface - the same
        // camera-eye test the underwater screen tint uses.
        var eye = _window.Camera.Position;
        bool submerged = TryGetEyeLiquidSurface(eye, out float surfaceZ, out byte liquidType) &&
                         eye.Z < surfaceZ;

        float hours = _atmosphere.TimeOfDayHours;

        _soundscape.AreaId = _soundscapeAreaId;
        _soundscape.InteriorZoneMusicId = _soundscapeInterior.Music;
        _soundscape.InteriorAmbienceId = _soundscapeInterior.Ambience;
        _soundscape.InteriorIntroSoundId = _soundscapeInterior.Intro;
        _soundscape.Interior = _soundscapeIndoors;
        _soundscape.WeatherAmbienceKit = _weatherSoundKit;
        if (!AudioFeaturePolicy.ExpandedWorldAudioEnabled)
        {
            _soundscape.Interior = false;
            _soundscape.WeatherAmbienceKit = 0;
        }
        _soundscape.DayPhase = hours >= 5.5f && hours < 21f;   // vanilla's hard step
        _soundscape.Submerged = submerged;
        _soundscape.Update(now);
        if (AudioFeaturePolicy.ExpandedWorldAudioEnabled &&
            _liquid is not null && _liquidAmbient is not null)
        {
            RefreshRetainedWmoLiquid();
            LiquidRenderer.LiquidSoundCandidate?[] nearest =
                _liquid.NearestSoundSources(feet, LiquidAmbientLoopLaw.TriggerRadius);
            _liquidAmbient.Update(now, feet, listenerPosition, listenerYaw, nearest,
                submerged && WmoLiquidPointLaw.IsWater(liquidType));
        }
        UpdateWaterSplashSounds();
    }

    /// <summary>
    /// Keep the 1.12 title theme alive across login, character select, and character
    /// create. Entering a world owns the only stop edge and fades the held voice over
    /// two seconds; returning to glue restores or restarts it.
    /// </summary>
    private void UpdateGlueAudio()
    {
        if (_audioMixer is null) return;
        double now = NowSeconds();
        bool glueActive = GlueAudioLaw.ShouldPlayMusic(
            GlueFrontDoorActive, CreatorInWorld, _net?.State);
        if (glueActive)
        {
            _glueMusicFadeStartedAt = -1;
            if (_glueMusicVoice == 0 || !_audioMixer.IsLive(_glueMusicVoice))
            {
                // Mute is a transport gate here too. The theme is Looping:false, so with
                // StartWhenSilent the client re-read and re-decoded the whole 27 MB mp3 every
                // time it ended, at gain 0, for as long as anyone sat at character select. Same
                // defect the world music lane carried, in the one place a player is most likely
                // to be sitting when they turn music off.
                if (_audioMixer.CategoryAmp(GlueAudioLaw.MusicCategory) > 0f)
                    _glueMusicVoice = _audioMixer.Play(new AudioPlayRequest(
                        GlueAudioLaw.MusicPath,
                        GlueAudioLaw.MusicCategory,
                        _audioMixer.CategoryAmp(GlueAudioLaw.MusicCategory),
                        Looping: false,
                        RequestedCue: "glue-title-theme",
                        StartWhenSilent: true,
                        Announce: true));
            }
            else if (_audioMixer.CategoryAmp(GlueAudioLaw.MusicCategory) <= 0f)
            {
                // Muted while it was already playing: retire it rather than leaving a silent
                // 27 MB voice resident for the rest of the glue session.
                _audioMixer.Stop(_glueMusicVoice);
                _glueMusicVoice = 0;
            }
            else
                _audioMixer.SetVoiceGain(_glueMusicVoice,
                    _audioMixer.CategoryAmp(GlueAudioLaw.MusicCategory));
            return;
        }

        if (_glueMusicVoice == 0) return;
        if (!_audioMixer.IsLive(_glueMusicVoice))
        {
            _glueMusicVoice = 0;
            _glueMusicFadeStartedAt = -1;
            return;
        }
        if (_glueMusicFadeStartedAt < 0) _glueMusicFadeStartedAt = now;
        float envelope = GlueAudioLaw.FadeEnvelope(now, _glueMusicFadeStartedAt);
        _audioMixer.SetVoiceGain(_glueMusicVoice,
            _audioMixer.CategoryAmp(GlueAudioLaw.MusicCategory) * envelope);
        if (!GlueAudioLaw.FadeFinished(now, _glueMusicFadeStartedAt)) return;
        _audioMixer.Stop(_glueMusicVoice);
        _glueMusicVoice = 0;
        _glueMusicFadeStartedAt = -1;
    }

    private void ObserveDismountSoundTransitions(bool playbackAllowed)
    {
        HashSet<ulong> seen = [];
        Vector3 listener = _controller?.Position ?? Vector3.Zero;
        foreach (WorldEntity unit in _entities.Entities.Values.Where(entity => entity.IsUnit))
        {
            seen.Add(unit.Guid);
            int mount = unit.MountDisplayId;
            if (_knownMountSoundDisplays.TryGetValue(unit.Guid, out int previous) &&
                previous != 0 && mount == 0 && playbackAllowed)
            {
                _spellSounds?.Play(DismountSoundName, unit.Guid, unit.Position, listener,
                    category: "sfx");
            }
            _knownMountSoundDisplays[unit.Guid] = mount;
        }
        foreach (ulong stale in _knownMountSoundDisplays.Keys.Where(guid => !seen.Contains(guid)).ToArray())
            _knownMountSoundDisplays.Remove(stale);
    }

    /// <summary>
    /// Apply one of the server's scripted SoundEntries triggers. Packets that
    /// arrive while the loading curtain owns the world are intentionally dropped,
    /// matching Benilla's world-hold gate rather than being replayed late.
    /// </summary>
    private void ApplyServerSound(Op opcode, byte[] body)
    {
        if (!AudioFeaturePolicy.ExpandedWorldAudioEnabled) return;
        switch (opcode)
        {
            case Op.SMSG_PLAY_SOUND:
                {
                    ServerSoundPacket sound = ServerSoundPackets.ParseSound(body);
                    if (ServerSoundPlaybackHeld) return;
                    _soundscape!.PlayServerSound2d(sound.SoundId);
                }
                break;
            case Op.SMSG_PLAY_MUSIC:
                {
                    ServerSoundPacket music = ServerSoundPackets.ParseMusic(body);
                    if (ServerSoundPlaybackHeld) return;
                    _soundscape!.PlayServerMusic(music.SoundId, NowSeconds());
                }
                break;
            case Op.SMSG_PLAY_OBJECT_SOUND:
                {
                    ServerObjectSoundPacket sound = ServerSoundPackets.ParseObjectSound(body);
                    if (ServerSoundPlaybackHeld) return;
                    Vector3 listener = _controller?.Position ?? Vector3.Zero;
                    var pose = SpellEffectUnitPose(sound.SourceGuid);
                    Vector3 source = pose.Found ? pose.Position : listener;
                    _spellSounds?.Play(sound.SoundId, sound.SourceGuid, source, listener,
                        forceLoop: false, trackHold: false, category: "sfx");
                }
                break;
        }
    }

    private bool ServerSoundPlaybackHeld
        => !_soundscapePlaybackArmed || _worldLoading || _soundscape is null;

    private void ApplyWeather(byte[] body)
    {
        WeatherPacket weather = WeatherPackets.Parse(body);
        // The sound kit is state, unlike the three one-shot/server-music pushes:
        // retain it through a loading cover so the ambience selector sees the
        // destination zone's weather when playback arms. Grade is visual-only.
        _weatherSoundKit = weather.SoundId;
        _weatherVisual.Apply(weather.WeatherType, weather.Grade, weather.Instant, NowSeconds());
        Console.WriteLine($"[weather] type={weather.WeatherType} grade={weather.Grade:F2} " +
                          $"sound={weather.SoundId} instant={weather.Instant}");
    }

    /// <summary>Push the persisted audio settings onto the mix. Called from
    /// ApplySettings so the Sound Options sliders are live while dragged. The
    /// master mix belongs to the device, so every category reaches every caller.</summary>
    private void ApplyAudioSettings(Engine.GameSettings settings)
    {
        if (_audioMixer is null) return;
        var audio = settings.Audio;
        _audioMixer.SoundEnabled = audio.EnableAll;
        _audioMixer.MusicEnabled = audio.EnableMusic;
        _audioMixer.AmbienceEnabled = audio.EnableAmbience;
        _audioMixer.MasterVolume = Math.Clamp(audio.MasterVolume, 0f, 1f);
        _audioMixer.EffectsVolume = Math.Clamp(audio.EffectsVolume, 0f, 1f);
        _audioMixer.MusicVolume = Math.Clamp(audio.MusicVolume, 0f, 1f);
        _audioMixer.AmbienceVolume = Math.Clamp(audio.AmbienceVolume, 0f, 1f);
    }
}

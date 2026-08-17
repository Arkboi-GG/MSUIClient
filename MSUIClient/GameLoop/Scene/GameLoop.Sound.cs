using System.Numerics;
using MSUIClient.Engine.UI;
using MSUIClient.World.Sound;

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
    private bool _soundscapePlaybackArmed;
    private bool _audioSelfTestStarted;
    private int _soundscapeSettledFrames;
    private double _soundscapeArmDeadline;

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

        bool inWorld = _terrain is not null && _worldLoadStarted && !_worldLoading &&
                       !GlueFrontDoorActive && _controller is not null;
        if (!inWorld)
        {
            _soundscape?.Reset();
            _soundscapePlaybackArmed = false;
            _soundscapeSettledFrames = 0;
            _soundscapeArmDeadline = 0;
            return;
        }

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

            if (_wmo?.ResolveAreaMinimapIdentity(feet, terrainZ) is { } interior &&
                _wmoAreas?.Resolve(interior.RootWmoId, interior.NameSetId,
                    interior.GroupWmoId) is { } row)
            {
                _soundscapeInterior = (row.ZoneMusicId, row.AmbienceId, row.IntroSoundId);
                areaId = row.AreaTableId;
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
        bool submerged = _liquid?.TryGetSurface(eye.X, eye.Y, out float surfaceZ, out _) == true &&
                         eye.Z < surfaceZ;

        float hours = _atmosphere.TimeOfDayHours;

        _soundscape.AreaId = _soundscapeAreaId;
        _soundscape.InteriorZoneMusicId = _soundscapeInterior.Music;
        _soundscape.InteriorAmbienceId = _soundscapeInterior.Ambience;
        _soundscape.InteriorIntroSoundId = _soundscapeInterior.Intro;
        _soundscape.DayPhase = hours >= 5.5f && hours < 21f;   // vanilla's hard step
        _soundscape.Submerged = submerged;
        _soundscape.Update(now);
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

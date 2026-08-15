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

    /// <summary>Runs every Update, before the loading-state early returns, so
    /// leaving the world (logout, loading curtain) resets the transport instead
    /// of stranding a looping bed in the glue screen.</summary>
    private void UpdateWorldSoundscape()
    {
        if (_spellSounds is null) return;

        bool inWorld = _terrain is not null && _worldLoadStarted && !_worldLoading &&
                       !GlueFrontDoorActive && _controller is not null;
        if (!inWorld)
        {
            _soundscape?.Reset();
            return;
        }

        if (_soundscape is null)
        {
            if (_mpq is null) return;
            _soundscape = new WorldSoundscape(_spellSounds, _mpq);
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
    /// ApplySettings so the Sound Options sliders are live while dragged.</summary>
    private void ApplyAudioSettings(Engine.GameSettings settings)
    {
        if (_spellSounds is null) return;
        var audio = settings.Audio;
        _spellSounds.SoundEnabled = audio.EnableAll;
        _spellSounds.MusicEnabled = audio.EnableMusic;
        _spellSounds.AmbienceEnabled = audio.EnableAmbience;
        _spellSounds.MasterVolume = Math.Clamp(audio.MasterVolume, 0f, 1f);
        _spellSounds.EffectsVolume = Math.Clamp(audio.EffectsVolume, 0f, 1f);
        _spellSounds.MusicVolume = Math.Clamp(audio.MusicVolume, 0f, 1f);
        _spellSounds.AmbienceVolume = Math.Clamp(audio.AmbienceVolume, 0f, 1f);
    }
}

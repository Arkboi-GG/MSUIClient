namespace MSUIClient.World.Sound;

/// <summary>
/// Compatibility boundary for the broad world-audio parity pass introduced after
/// the last known-clean build (efc9027). Those producers can create a large burst
/// of independent WinMM streams from renderer animation events. The current
/// backend owns one waveOut handle per voice, so that burst destabilizes playback
/// on otherwise healthy endpoints.
///
/// Keep the known-clean music, ambience, spell, and established UI paths enabled
/// by default. Expanded world emitters remain available for explicit development
/// runs while the backend is moved to one shared software-mixed output stream.
/// </summary>
internal static class AudioFeaturePolicy
{
    public static readonly bool ExpandedWorldAudioEnabled =
        Environment.GetEnvironmentVariable("MSUI_EXPANDED_WORLD_AUDIO") == "1";
}

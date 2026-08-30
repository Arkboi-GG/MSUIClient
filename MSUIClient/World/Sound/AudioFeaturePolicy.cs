namespace MSUIClient.World.Sound;

/// <summary>
/// The world-audio quarantine, now OPT-OUT. It holds footsteps, creature voices,
/// melee, emotes, NPC greetings, gameobject sounds, chat and inventory cues.
///
/// It was opt-in while every producer owned a separate legacy waveOut handle. That
/// backend used <c>waveOutSetVolume</c> as though it were per voice, but modern Windows
/// maps all of those streams into one process audio session: each positional footstep
/// was changing the gain and stereo balance of music, ambience, and every other cue.
/// Byte-progress probes stayed clean because no bytes had to be missing for the whole
/// session to pump ten times a second.
///
/// Voices now meet in one <see cref="SharedWaveOutMixer"/> and receive independent
/// sample-domain gain/pan before summing. The default producer set can therefore be
/// on; the flag remains as a broad diagnostic boundary, not as the fix.
///
/// So the default is ON, and the flag survives only as a kill switch: set
/// <c>MSUI_EXPANDED_WORLD_AUDIO=0</c> to put the quarantine back for one run.
/// </summary>
internal static class AudioFeaturePolicy
{
    /// <summary>Env <c>MSUI_EXPANDED_WORLD_AUDIO</c>: "0" quarantines, anything else
    /// (including unset) plays.</summary>
    public static readonly bool ExpandedWorldAudioEnabled =
        Environment.GetEnvironmentVariable("MSUI_EXPANDED_WORLD_AUDIO") != "0";
}

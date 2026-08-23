using MSUIClient.Net;

namespace MSUIClient.Engine.UI;

/// <summary>Current 1.12 glue-screen audio lifecycle and authored cue facts.</summary>
public static class GlueAudioLaw
{
    public const string MusicPath = @"Sound\Music\GlueScreenMusic\wow_main_theme.mp3";
    public const string MusicCategory = "music";
    public const float MusicFadeOutSeconds = 2f;

    public static bool ShouldPlayMusic(bool frontDoorActive, bool creatorInWorld,
        NetState? state) =>
        !creatorInWorld && (frontDoorActive || state is { } netState &&
            netState != NetState.InWorld);

    public static float FadeEnvelope(double now, double startedAt) =>
        1f - Math.Clamp((float)((now - startedAt) / MusicFadeOutSeconds), 0f, 1f);

    public static bool FadeFinished(double now, double startedAt) =>
        now - startedAt >= MusicFadeOutSeconds;
}

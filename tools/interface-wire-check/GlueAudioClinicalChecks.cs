using MSUIClient;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

internal static class GlueAudioClinicalChecks
{
    public static void Run()
    {
        Check(GlueAudioLaw.MusicPath ==
                  @"Sound\Music\GlueScreenMusic\wow_main_theme.mp3" &&
              GlueAudioLaw.MusicCategory == "music" &&
              GlueAudioLaw.MusicFadeOutSeconds == 2f,
            "glue title-theme source/category/fade law drift");
        Check(GlueAudioLaw.ShouldPlayMusic(true, false, null) &&
              GlueAudioLaw.ShouldPlayMusic(false, false, NetState.Idle) &&
              GlueAudioLaw.ShouldPlayMusic(false, false, NetState.CharacterSelect) &&
              !GlueAudioLaw.ShouldPlayMusic(false, false, NetState.InWorld) &&
              !GlueAudioLaw.ShouldPlayMusic(true, true, NetState.InWorld) &&
              MathF.Abs(GlueAudioLaw.FadeEnvelope(11, 10) - .5f) < .0001f &&
              !GlueAudioLaw.FadeFinished(11.999, 10) &&
              GlueAudioLaw.FadeFinished(12, 10),
            "glue-screen state or two-second fade envelope drift");
        Check(CharSelectUiLaw.DeleteSound == "gsCharacterSelectionDelCharacter" &&
              CharSelectUiLaw.CreateSound == "gsCharacterSelectionCreateNew" &&
              CharSelectUiLaw.BackSound == "gsCharacterSelectionExit" &&
              CharSelectUiLaw.EnterWorldSound == "gsCharacterSelectionEnterWorld" &&
              CharSelectUiLaw.AcceptSound == "gsTitleOptionOK" &&
              CharSelectUiLaw.CancelSound == "gsTitleOptionExit" &&
              CharCreateUiLaw.ClassChoiceSound == "gsCharacterCreationClass" &&
              CharCreateUiLaw.LookChoiceSound == "gsCharacterCreationLook" &&
              CharCreateUiLaw.CancelSound == "gsCharacterCreationCancel" &&
              CharCreateUiLaw.CreateSound == "gsCharacterCreationCreateChar",
            "character-select/create authored glue cue law drift");

        string root = ClientConfig.FindRepoRoot();
        string sound = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Sound.cs"));
        string select = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Net.cs"));
        string create = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.CharCreate.cs"));
        int glueStart = sound.IndexOf("private void UpdateGlueAudio()", StringComparison.Ordinal);
        int glueEnd = sound.IndexOf("private void ObserveDismountSoundTransitions", glueStart,
            StringComparison.Ordinal);
        string glueUpdate = glueStart >= 0 && glueEnd > glueStart
            ? sound[glueStart..glueEnd]
            : "";
        Check(sound.Contains("UpdateGlueAudio();", StringComparison.Ordinal) &&
              sound.Contains("GlueAudioLaw.ShouldPlayMusic", StringComparison.Ordinal) &&
              sound.Contains("new AudioPlayRequest(", StringComparison.Ordinal) &&
              sound.Contains("GlueAudioLaw.FadeEnvelope", StringComparison.Ordinal) &&
              sound.Contains("GlueAudioLaw.FadeFinished", StringComparison.Ordinal) &&
              sound.Contains("_audioMixer.Stop(_glueMusicVoice)", StringComparison.Ordinal) &&
              !glueUpdate.Contains("ExpandedWorldAudioEnabled", StringComparison.Ordinal),
            "glue title-theme start/restart/fade handoff drift");
        Check(select.Contains("PlayUiSound(CharSelectUiLaw.DeleteSound",
                  StringComparison.Ordinal) &&
              select.Contains("PlayUiSound(CharSelectUiLaw.BackSound",
                  StringComparison.Ordinal) &&
              select.Contains("PlayUiSound(CharSelectUiLaw.EnterWorldSound",
                  StringComparison.Ordinal) &&
              select.Contains("PlayUiSound(CharSelectUiLaw.AcceptSound",
                  StringComparison.Ordinal) &&
              select.Contains("PlayUiSound(CharSelectUiLaw.CancelSound",
                  StringComparison.Ordinal) &&
              create.Contains("PlayUiSound(CharSelectUiLaw.CreateSound",
                  StringComparison.Ordinal) &&
              create.Contains("PlayCharCreateSound(CharCreateUiLaw.ClassChoiceSound)",
                  StringComparison.Ordinal) &&
              create.Contains("PlayCharCreateSound(CharCreateUiLaw.LookChoiceSound)",
                  StringComparison.Ordinal),
            "character-select/create glue cue wiring drift");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

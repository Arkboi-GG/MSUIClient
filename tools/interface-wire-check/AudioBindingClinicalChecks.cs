using MSUIClient;
using MSUIClient.Engine.UI;

internal static class AudioBindingClinicalChecks
{
    public static void Run()
    {
        Check(MathF.Abs(BindingCommandLaw.StepMasterVolume(.5f, 1) - .6f) < .0001f &&
              BindingCommandLaw.StepMasterVolume(.95f, 1) == 1f &&
              BindingCommandLaw.StepMasterVolume(.05f, -1) == 0f,
            "master-volume tenth-step/clamp law drifted");

        string root = ClientConfig.FindRepoRoot();
        string bindings = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop",
            "Panels", "GameLoop.Bindings.cs"));
        string program = SourceText.Read(Path.Combine(root, "MSUIClient", "Program.cs"));
        Check(bindings.Contains("new BindingChord(Key.M, Control: true)",
                  StringComparison.Ordinal) &&
              bindings.Contains("new BindingChord(Key.S, Control: true)",
                  StringComparison.Ordinal) &&
              bindings.Contains("new BindingChord(Key.Equal, Control: true)",
                  StringComparison.Ordinal) &&
              bindings.Contains("new BindingChord(Key.Minus, Control: true)",
                  StringComparison.Ordinal),
            "Benilla's Ctrl-M/Ctrl-S/Ctrl-=/Ctrl-- audio defaults drifted");
        Check(program.Contains("UpdateAudioBindings(typing);", StringComparison.Ordinal) &&
              bindings.Contains("if (!typing)", StringComparison.Ordinal) &&
              bindings.Contains("Settings.Audio.EnableMusic = !Settings.Audio.EnableMusic",
                  StringComparison.Ordinal) &&
              bindings.Contains("Settings.Audio.EnableAll = !Settings.Audio.EnableAll",
                  StringComparison.Ordinal) &&
              bindings.Contains("BindingCommandLaw.StepMasterVolume(",
                  StringComparison.Ordinal) &&
              bindings.Contains("ApplyAudioSettings(Settings);", StringComparison.Ordinal) &&
              !bindings.Contains("ApplySettings(Settings);", StringComparison.Ordinal) &&
              bindings.Contains("SettingsFile?.Save();", StringComparison.Ordinal),
            "audio bindings escaped the typing gate, audio-only apply or persistence seam");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

using System.Numerics;
using MSUIClient;
using MSUIClient.Engine;
using MSUIClient.World.Sound;

internal static class SpatialAudioClinicalChecks
{
    public static void Run()
    {
        Check(SpatialAudioLaw.CharacterListener(new Vector3(10f, 20f, 30f)) ==
              new Vector3(10f, 20f, 31.7f),
            "character listener must sit at the avatar head");

        float inside = SpatialAudioLaw.Gain(.8f, 10f, 100f,
            new Vector3(10f, 0f, 0f), Vector3.Zero);
        float inverse = SpatialAudioLaw.Gain(1f, 10f, 100f,
            new Vector3(20f, 0f, 0f), Vector3.Zero);
        float finalBand = SpatialAudioLaw.Gain(1f, 10f, 100f,
            new Vector3(95f, 0f, 0f), Vector3.Zero);
        Check(Near(inside, .8f) && Near(inverse, .2f) &&
              Near(finalBand, (10f / 350f) * .5f) &&
              SpatialAudioLaw.Gain(1f, 10f, 100f,
                  new Vector3(100f, 0f, 0f), Vector3.Zero) == 0f,
            "FMOD factor-four rolloff, last-ten-percent fade, or strict cutoff drifted");

        Check(Near(SpatialAudioLaw.Pan(new Vector3(0f, -10f, 0f), Vector3.Zero, 0f), 1f) &&
              Near(SpatialAudioLaw.Pan(new Vector3(0f, 10f, 0f), Vector3.Zero, 0f), -1f) &&
              Near(SpatialAudioLaw.Pan(new Vector3(10f, 0f, 0f), Vector3.Zero,
                  MathF.PI * .5f), 1f) &&
              SpatialAudioLaw.Pan(Vector3.Zero, Vector3.Zero, 1f) == 0f,
            "stereo side must follow character facing, not camera orbit");

        Check(SpatialAudioLaw.StereoLevels(.8f, 0f) == (.8f, .8f) &&
              SpatialAudioLaw.StereoLevels(.8f, 1f) == (0f, .8f) &&
              SpatialAudioLaw.StereoLevels(.8f, -1f) == (.8f, 0f) &&
              SpatialAudioLaw.StereoLevels(.8f, .25f) == (.6f, .8f),
            "waveOut stereo balance projection drifted");

        string root = ClientConfig.FindRepoRoot();
        string mixer = SourceText.Read(Path.Combine(root, "MSUIClient", "World", "Sound",
            "AudioMixer.cs"));
        string voice = SourceText.Read(Path.Combine(root, "MSUIClient", "World", "Sound",
            "WaveOutVoice.cs"));
        string policy = SourceText.Read(Path.Combine(root, "MSUIClient", "World", "Sound",
            "AudioFeaturePolicy.cs"));
        string spells = SourceText.Read(Path.Combine(root, "MSUIClient", "World", "Spells",
            "SpellSoundSystem.cs"));
        string runtime = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Sound.cs"));
        string creatures = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.CreatureVoices.cs"));
        string footsteps = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Footsteps.cs"));
        string gameObjects = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.GameObjectSounds.cs"));
        Check(mixer.Contains("SetVoiceGainPan", StringComparison.Ordinal) &&
              voice.Contains("SpatialAudioLaw.StereoLevels", StringComparison.Ordinal) &&
              spells.Contains("SpatialAudioLaw.Pan", StringComparison.Ordinal) &&
              spells.Contains("SpatialAudioLaw.Gain", StringComparison.Ordinal) &&
              runtime.Contains("SpatialAudioLaw.CharacterListener", StringComparison.Ordinal) &&
              runtime.Contains("_window.Camera.ViewYaw", StringComparison.Ordinal),
            "production listener/source/pan/rolloff wiring drifted");
        Check(policy.Contains("MSUI_EXPANDED_WORLD_AUDIO", StringComparison.Ordinal) &&
              policy.Contains("== \"1\"", StringComparison.Ordinal) &&
              creatures.Contains("if (!AudioFeaturePolicy.ExpandedWorldAudioEnabled)",
                  StringComparison.Ordinal) &&
              footsteps.Contains("if (!AudioFeaturePolicy.ExpandedWorldAudioEnabled)",
                  StringComparison.Ordinal) &&
              gameObjects.Contains("if (!AudioFeaturePolicy.ExpandedWorldAudioEnabled)",
                  StringComparison.Ordinal) &&
              spells.Contains("Preserve the attenuation law from the last known-clean audio build.",
                  StringComparison.Ordinal),
            "known-clean producer compatibility boundary drifted");
        int prepare = voice.IndexOf("waveOutPrepareHeader", StringComparison.Ordinal);
        int loopFlags = voice.IndexOf("header.Flags |= WhdrBeginLoop | WhdrEndLoop;",
            StringComparison.Ordinal);
        int write = voice.IndexOf("waveOutWrite", StringComparison.Ordinal);
        Check(prepare >= 0 && loopFlags > prepare && write > loopFlags,
            "WinMM loop flags must be installed after prepare and before write");
    }

    private static bool Near(float left, float right) => MathF.Abs(left - right) < 1e-5f;

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

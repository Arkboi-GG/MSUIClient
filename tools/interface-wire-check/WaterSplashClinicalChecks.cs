using MSUIClient;
using MSUIClient.Engine;
using MSUIClient.World.Sound;

internal static class WaterSplashClinicalChecks
{
    public static void Run()
    {
        Check(WaterSplashSoundLaw.MediumSplashKit == 1096 &&
              WaterSplashSoundLaw.CollisionHeightFraction == .4f &&
              !WaterSplashSoundLaw.BeyondSplashLine(11f, 10f, 2.5f) &&
              WaterSplashSoundLaw.BeyondSplashLine(11.0001f, 10f, 2.5f) &&
              !WaterSplashSoundLaw.BeyondSplashLine(null, 10f, 2f),
            "water splash kit or strict 0.4-collision-height depth line drifted");
        Check(WaterSplashSoundLaw.Crossed(false, true) &&
              WaterSplashSoundLaw.Crossed(true, false) &&
              !WaterSplashSoundLaw.Crossed(false, false) &&
              !WaterSplashSoundLaw.Crossed(true, true),
            "water splash edge must be symmetric and fire only on a crossing");

        string root = ClientConfig.FindRepoRoot();
        string water = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.WaterSounds.cs"));
        string runtime = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Sound.cs"));
        Check(water.Contains("bool armed = _waterSplashStates.TryGetValue",
                  StringComparison.Ordinal) &&
              water.Contains("if (!armed ||", StringComparison.Ordinal) &&
              water.Contains("TrySampleFootstepTerrain", StringComparison.Ordinal) &&
              water.Contains("_spellSounds.IsLive(current)", StringComparison.Ordinal) &&
              water.Contains("WaterSplashSoundLaw.MediumSplashKit", StringComparison.Ordinal) &&
              runtime.Contains("UpdateWaterSplashSounds();", StringComparison.Ordinal) &&
              runtime.Contains("ResetWaterSplashSounds();", StringComparison.Ordinal),
            "production first-seen/symmetric/overlap/WMO/reset water-splash wiring drifted");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

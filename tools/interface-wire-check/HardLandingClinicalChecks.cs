using MSUIClient;
using MSUIClient.World.Units;

internal static class HardLandingClinicalChecks
{
    public static void Run()
    {
        HardLandingArcStep takeoff = HardLandingSoundLaw.Step(
            wasAirborne: false, nowAirborne: true, startZ: null,
            previousZ: 100f, currentZ: 100.4f);
        HardLandingArcStep falling = HardLandingSoundLaw.Step(
            wasAirborne: true, nowAirborne: true, takeoff.StartZ,
            previousZ: 91f, currentZ: 80f);
        HardLandingArcStep landed = HardLandingSoundLaw.Step(
            wasAirborne: true, nowAirborne: false, falling.StartZ,
            previousZ: 79f, currentZ: 70f);

        Check(takeoff.StartZ == 100f && takeoff.Descent is null &&
              falling.StartZ == 100f && falling.Descent is null &&
              landed.StartZ is null && landed.Descent == 30f,
            "fall arcs must latch launch Z and report descent only on the landing edge");
        Check(!HardLandingSoundLaw.IsHardLanding(13f) &&
              HardLandingSoundLaw.IsHardLanding(13.0001f) &&
              !HardLandingSoundLaw.IsHardLanding(float.NaN),
            "hard landings must use Benilla's strict descent-greater-than-thirteen gate");

        HardLandingArcStep unknown = HardLandingSoundLaw.Step(
            wasAirborne: true, nowAirborne: false, startZ: null,
            previousZ: 50f, currentZ: 20f);
        Check(unknown.StartZ is null && unknown.Descent is null,
            "an arc first observed in flight must not fabricate a landing descent");

        string root = ClientConfig.FindRepoRoot();
        string loop = SourceText.Read(Path.Combine(root, "MSUIClient", "Program.cs"));
        string voices = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.CreatureVoices.cs"));
        Check(loop.Contains("ObserveControlledHardLanding(", StringComparison.Ordinal) &&
              loop.Contains("movementWasFlying, _controller.Flying", StringComparison.Ordinal),
            "the controlled movement landing edge or its flying discontinuity gate drift");
        Check(voices.Contains("voice.InjurySound", StringComparison.Ordinal) &&
              voices.Contains("ResetControlledHardLandingArc()", StringComparison.Ordinal) &&
              voices.Contains("forceLoop: false, trackHold: false, category: \"sfx\"",
                  StringComparison.Ordinal),
            "hard-landing injury-row positional SFX playback drift");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}

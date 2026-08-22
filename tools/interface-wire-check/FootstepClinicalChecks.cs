using MSUIClient;
using MSUIClient.Formats;
using MSUIClient.World.Units;

internal static class FootstepClinicalChecks
{
    public static void Run()
    {
        var model = new M2Model();
        model.Sequences.Add(new M2Sequence
        {
            AnimationId = 4,
            StartTimestamp = 1_000,
            EndTimestamp = 2_000,
            Flags = 0,
        });
        model.Events.Add(new M2EventMarker
        {
            Identifier = "$FSD",
            Times = [1_250, 1_750, 2_500],
        });
        model.Events.Add(new M2EventMarker
        {
            Identifier = "$FR0",
            Times = [1_250, 1_750],
        });
        model.Events.Add(new M2EventMarker
        {
            Identifier = "$FD1",
            Times = [1_500],
        });
        var loop = new M2Animator.Clip
        {
            SequenceIndex = 0,
            AnimationId = 4,
            DurationSeconds = 1f,
            Looping = true,
        };
        Check(FootstepAnimationLaw.CountCrossings(model, loop, 0.20, 0.80) == 2 &&
              FootstepAnimationLaw.CountCrossings(model, loop, 0.80, 2.30) == 3,
            "$FSD cadence/loop crossing drift or visual side tags started making sound");
        Check(CreatureAnimationSoundLaw.CrossedVocalEvents(model, loop, 0.20, 0.80)
                  .SequenceEqual(["$FD1"]) &&
              CreatureAnimationSoundLaw.StandChancePass(0) &&
              !CreatureAnimationSoundLaw.StandChancePass(uint.MaxValue),
            "authored creature-vocal crossings or the 41/101 stand roll drift");

        loop.Looping = false;
        Check(FootstepAnimationLaw.CountCrossings(model, loop, 0.0, 3.0) == 2,
            "clamped footstep clips must fire each in-band $FSD once");

        string root = ClientConfig.FindRepoRoot();
        string wmo = SourceText.Read(Path.Combine(root, "MSUIClient", "World", "Wmo",
            "WmoRenderer.cs"));
        string playback = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Footsteps.cs"));
        string fields = SourceText.Read(Path.Combine(root, "MSUIClient", "Net",
            "ObjectFields.cs"));
        string voices = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.CreatureVoices.cs"));
        Check(wmo.Contains("(flags & 0x88) != 0", StringComparison.Ordinal) &&
              wmo.Contains("root.Materials[materialId].GroundType", StringComparison.Ordinal) &&
              wmo.Contains("return true;", StringComparison.Ordinal),
            "WMO render-face material ownership seam drift");
        Check(playback.Contains("!wmoOwnsColumn", StringComparison.Ordinal) &&
              playback.Contains("deep > 0.75f * height", StringComparison.Ordinal) &&
              playback.Contains("kits.Splash != 0 ? kits.Splash : kits.Dry",
                  StringComparison.Ordinal),
            "surface arbitration or collision-height wading law drift");
        Check(fields.Contains("& 0x02u) != 0", StringComparison.Ordinal) &&
              fields.Contains("PlayerFlags & 0x10u", StringComparison.Ordinal),
            "root stealth/ghost footstep gates drift");
        Check(fields.Contains("(DynamicFlags & 0x20u) != 0 || UnitStandState == 7",
                  StringComparison.Ordinal) &&
              voices.Contains("previous != false || !dead", StringComparison.Ordinal) &&
              voices.Contains("_creatureStandVocalLastAt = now", StringComparison.Ordinal) &&
              voices.Contains("unit.MountDisplayId > 0 ? unit.MountDisplayId : unit.DisplayId",
                  StringComparison.Ordinal) &&
              voices.Contains("forceLoop: true, trackHold: false", StringComparison.Ordinal),
            "live death, global stand-vocal, or alive-only mount-preferred body loop drift");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}

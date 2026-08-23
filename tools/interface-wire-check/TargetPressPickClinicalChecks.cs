using System.Numerics;
using MSUIClient;
using MSUIClient.Engine.UI;

internal static class TargetPressPickClinicalChecks
{
    public static void Run()
    {
        Vector3 ground = new(1, 2, 3);
        TargetPressPick latched = TargetPressPickLaw.Update(default,
            down: true, wasDown: false, otherDown: false, 10, 20, ground);
        Check(latched == new TargetPressPick(true, 10, 20, ground),
            "first primary-button down edge must freeze all pick subjects");
        Check(TargetPressPickLaw.Update(latched,
                down: true, wasDown: true, otherDown: false, 30, 40, null) == latched,
            "held button must not re-pick a moving subject");
        Check(TargetPressPickLaw.Update(latched,
                down: false, wasDown: true, otherDown: false, 30, 40, null) == latched,
            "release must preserve the latch until the host consumes it");
        Check(!TargetPressPickLaw.Update(default,
                down: true, wasDown: false, otherDown: true, 10, 20, ground).Armed,
            "second-button chord edge must not arm a pick");
        (TargetPressPick left, TargetPressPick right) = TargetPressPickLaw.CancelChord(
            true, true, latched, latched);
        Check(!left.Armed && !right.Armed,
            "two-primary-button camera chord must cancel both picks");

        string root = ClientConfig.FindRepoRoot();
        string source = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop",
            "Combat", "GameLoop.Targeting.cs"));
        Check(source.Contains("TargetPressPickLaw.Update") &&
              source.Contains("TargetPressPickLaw.CancelChord") &&
              source.Contains("pressPick.GroundPoint") &&
              source.Contains("pressPick.UnitGuid") &&
              source.Contains("pressPick.GameObjectGuid"),
            "press-subject latch production wiring drifted");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

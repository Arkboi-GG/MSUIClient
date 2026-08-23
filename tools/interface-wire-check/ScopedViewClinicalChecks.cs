using MSUIClient;
using MSUIClient.Engine;

internal static class ScopedViewClinicalChecks
{
    public static void Run()
    {
        float? sixfold = ScopedViewLaw.ZoomFraction([0, ScopedViewLaw.FarSightAura, 0],
            [99, 15, 1]);
        float? wrongLane = ScopedViewLaw.ZoomFraction([ScopedViewLaw.FarSightAura, 0, 0],
            [0, 15, 0]);
        Check(sixfold == 1f / 6f && wrongLane is null &&
              ScopedViewLaw.ZoomFraction([ScopedViewLaw.FarSightAura], [0]) is null &&
              MathF.Abs(ScopedViewLaw.VerticalFieldOfViewRadians(70f, sixfold.Value) -
                  70f * MathF.PI / 180f / 6f) < .000001f,
            "spyglass same-effect-lane ratio/zero-restore law drift");

        string root = ClientConfig.FindRepoRoot();
        string host = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.ScopedView.cs"));
        string program = SourceText.Read(Path.Combine(root, "MSUIClient", "Program.cs"));
        string bindings = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Bindings.cs"));
        Check(host.Contains("_entities.TryGet(net.PlayerGuid", StringComparison.Ordinal) &&
              !host.Contains("ControlledGuid", StringComparison.Ordinal) &&
              host.Contains("spell.AuraIds, spell.EffectMiscValues", StringComparison.Ordinal) &&
              host.Contains("camera.FieldOfViewDegrees, fraction", StringComparison.Ordinal) &&
              host.Contains("camera.Distance = camera.EffectiveDistance =", StringComparison.Ordinal) &&
              host.Contains("AuthoredVerticalFieldOfViewRadians = null", StringComparison.Ordinal) &&
              program.Contains("ResolveCameraCollision(dt);\n        UpdateScopedView();",
                  StringComparison.Ordinal) &&
              bindings.Contains("!_window.FreeSelectMode && !_scopedViewActive",
                  StringComparison.Ordinal),
            "spyglass local-player/first-person/FOV/wheel runtime wiring drift");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}

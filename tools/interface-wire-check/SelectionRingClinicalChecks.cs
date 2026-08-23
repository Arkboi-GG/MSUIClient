using MSUIClient;
using MSUIClient.Engine.UI;

internal static class SelectionRingClinicalChecks
{
    public static void Run()
    {
        Check(SelectionRingLaw.DiedWhileSelected(10, false, 10, true),
            "same-target alive-to-dead edge must clear selection");
        Check(!SelectionRingLaw.DiedWhileSelected(0, false, 10, true) &&
              !SelectionRingLaw.DiedWhileSelected(9, false, 10, true) &&
              !SelectionRingLaw.DiedWhileSelected(10, true, 10, true) &&
              !SelectionRingLaw.DiedWhileSelected(10, false, 10, false),
            "first-seen corpse, target changes and stable vitals must not clear selection");

        (float sin0, float cos0) = SelectionRingLaw.ProjectorRotation(0f);
        (float sin90, float cos90) = SelectionRingLaw.ProjectorRotation(MathF.PI * .5f);
        Near(sin0, 0f, "zero-yaw projector sine");
        Near(cos0, 1f, "zero-yaw projector cosine");
        Near(sin90, -1f, "quarter-turn projector sine");
        Near(cos90, 0f, "quarter-turn projector cosine");

        string root = ClientConfig.FindRepoRoot();
        string target = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop",
            "Combat", "GameLoop.Targeting.cs"));
        string meshes = SourceText.Read(Path.Combine(root, "MSUIClient", "World", "Units",
            "SpellEffectMeshRenderer.cs"));
        Check(target.Contains("SelectionRingLaw.DiedWhileSelected") &&
              target.Contains("RenderUnitSelectionRing") &&
              meshes.Contains(@"Textures\UnitSelectTexture.blp") &&
              meshes.Contains("ProjectDecal(frame, uv, camera.Position)") &&
              meshes.Contains("UnitAwareDepthBias"),
            "projected selection-ring or death-edge production wiring drifted");
        Check(!File.Exists(Path.Combine(root, "MSUIClient", "World", "Units",
                "SelectionRingRenderer.cs")),
            "obsolete flat-quad selection-ring renderer returned");
    }

    private static void Near(float actual, float expected, string label)
    {
        if (MathF.Abs(actual - expected) > 1e-5f)
            throw new InvalidOperationException($"{label} drifted: {actual} != {expected}");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

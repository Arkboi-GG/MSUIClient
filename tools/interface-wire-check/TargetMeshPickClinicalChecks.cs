using System.Numerics;
using MSUIClient;
using MSUIClient.Engine;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.World.Units;

internal static class TargetMeshPickClinicalChecks
{
    public static void Run()
    {
        M2Model triangle = Triangle(
            new(-.5f, -.5f, 0f), new(.5f, -.5f, 0f), new(0f, .5f, 0f),
            Vector3.UnitX);
        var translated = new SpellUnitPose(true, Vector3.Zero, 0f,
            Matrix4x4.CreateTranslation(0f, 0f, 2f), triangle, null);
        Check(TargetMeshPickLaw.TryPick(translated, new Vector3(0f, 0f, 5f),
                  -Vector3.UnitZ, inflated: false, out float translatedHit) &&
              Near(translatedHit, 3f),
            "world transform or exact two-sided triangle distance drifted");

        M2Model skinned = Triangle(
            new(-.5f, -.5f, 0f), new(.5f, -.5f, 0f), new(0f, .5f, 0f),
            Vector3.UnitX, boneIndex: 1);
        var skin = new[] { Matrix4x4.Identity, Matrix4x4.CreateTranslation(2f, 0f, 0f) };
        var posed = new SpellUnitPose(true, Vector3.Zero, 0f, Matrix4x4.Identity, skinned, skin);
        Check(TargetMeshPickLaw.TryPick(posed, new Vector3(2f, 0f, 5f),
                  -Vector3.UnitZ, inflated: false, out float posedHit) && Near(posedHit, 5f) &&
              !TargetMeshPickLaw.TryPick(posed, new Vector3(0f, 0f, 5f),
                  -Vector3.UnitZ, inflated: false, out _),
            "live joint palette did not move the pick mesh with the rendered pose");

        M2Model clamped = Triangle(
            new(-.5f, -.5f, 0f), new(.5f, -.5f, 0f), new(0f, .5f, 0f),
            Vector3.UnitX, boneIndex: 200);
        var clampedPose = new SpellUnitPose(true, Vector3.Zero, 0f, Matrix4x4.Identity,
            clamped, new[] { Matrix4x4.CreateTranslation(2f, 0f, 0f) });
        Check(TargetMeshPickLaw.TryPick(clampedPose, new Vector3(2f, 0f, 5f),
                  -Vector3.UnitZ, inflated: false, out _),
            "bone indices outside the uploaded 160-row palette must mirror GPU clamp-to-zero");

        var haloPose = new SpellUnitPose(true, Vector3.Zero, 0f,
            Matrix4x4.CreateScale(2f), triangle, null);
        Check(!TargetMeshPickLaw.TryPick(haloPose, new Vector3(2f, 0f, 5f),
                  -Vector3.UnitZ, inflated: false, out _) &&
              TargetMeshPickLaw.TryPick(haloPose, new Vector3(2f, 0f, 5f),
                  -Vector3.UnitZ, inflated: true, out float haloHit) && Near(haloHit, 5f),
            "pass-two normal inflation must be one model unit carried through world scale");

        M2Model geosets = TwoGeosets();
        var filtered = new SpellUnitPose(true, Vector3.Zero, 0f, Matrix4x4.Identity,
            geosets, null, new HashSet<int> { 2 });
        Check(!TargetMeshPickLaw.TryPick(filtered, new Vector3(0f, 0f, 5f),
                  -Vector3.UnitZ, inflated: false, out _) &&
              TargetMeshPickLaw.TryPick(filtered, new Vector3(3f, 0f, 5f),
                  -Vector3.UnitZ, inflated: false, out _),
            "hidden character geosets must not remain clickable");

        uint dead = TargetMeshPickLaw.HaloPriority(previousPick: false, dead: true);
        uint alive = TargetMeshPickLaw.HaloPriority(previousPick: false, dead: false);
        uint sticky = TargetMeshPickLaw.HaloPriority(previousPick: true, dead: true);
        Check(sticky == uint.MaxValue && alive > dead &&
              TargetMeshPickLaw.HaloWins(20f, alive, 2f, dead) &&
              !TargetMeshPickLaw.HaloWins(20f, dead, 2f, alive) &&
              TargetMeshPickLaw.HaloWins(2f, alive, 3f, alive),
            "halo sticky/alive/dead/distance priority ladder drifted");

        string root = ClientConfig.FindRepoRoot();
        string targeting = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop",
            "Combat", "GameLoop.Targeting.cs"));
        string creatures = SourceText.Read(Path.Combine(root, "MSUIClient", "World", "Units",
            "CreatureRenderer.cs"));
        string mounts = SourceText.Read(Path.Combine(root, "MSUIClient", "World", "Units",
            "CreatureRenderer.Mounts.cs"));
        Check(targeting.Contains("TargetMeshPickLaw.TryPick", StringComparison.Ordinal) &&
              targeting.Contains("TryGetMountSpellPose", StringComparison.Ordinal) &&
              !targeting.Contains("RayVerticalCylinder", StringComparison.Ordinal) &&
              creatures.Contains("_spellPoses.Clear();", StringComparison.Ordinal) &&
              mounts.Contains("Pose: pose", StringComparison.Ordinal),
            "production posed-mesh/drawn-only/mount wiring drifted");
    }

    private static M2Model Triangle(Vector3 a, Vector3 b, Vector3 c, Vector3 normal,
        byte boneIndex = 0)
    {
        var model = new M2Model();
        model.Vertices.Add(Vertex(a, normal, boneIndex));
        model.Vertices.Add(Vertex(b, normal, boneIndex));
        model.Vertices.Add(Vertex(c, normal, boneIndex));
        model.Indices.AddRange([0, 1, 2]);
        return model;
    }

    private static M2Model TwoGeosets()
    {
        M2Model model = Triangle(new(-.5f, -.5f, 0f), new(.5f, -.5f, 0f),
            new(0f, .5f, 0f), Vector3.UnitX);
        model.Vertices.Add(Vertex(new(2.5f, -.5f, 0f), Vector3.UnitX, 0));
        model.Vertices.Add(Vertex(new(3.5f, -.5f, 0f), Vector3.UnitX, 0));
        model.Vertices.Add(Vertex(new(3f, .5f, 0f), Vector3.UnitX, 0));
        model.Indices.AddRange([3, 4, 5]);
        model.Submeshes.Add(new M2Submesh { Id = 1, IndexStart = 0, IndexCount = 3 });
        model.Submeshes.Add(new M2Submesh { Id = 2, IndexStart = 3, IndexCount = 3 });
        return model;
    }

    private static M2Vertex Vertex(Vector3 point, Vector3 normal, byte boneIndex) => new()
    {
        PosX = point.X,
        PosY = point.Y,
        PosZ = point.Z,
        NormX = normal.X,
        NormY = normal.Y,
        NormZ = normal.Z,
        BoneWeight0 = 255,
        BoneIndex0 = boneIndex,
    };

    private static bool Near(float left, float right) => MathF.Abs(left - right) < 1e-5f;

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

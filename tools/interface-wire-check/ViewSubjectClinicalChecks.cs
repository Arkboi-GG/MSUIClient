using System.Numerics;
using MSUIClient;
using MSUIClient.Engine;
using MSUIClient.Net;

internal static class ViewSubjectClinicalChecks
{
    public static void Run()
    {
        float character = ViewSubjectLaw.PivotHeight(1.0f, -1f, 9f, 2f);
        float fallback = ViewSubjectLaw.PivotHeight(null, -0.5f, 1.5f, 1f);
        Check(MathF.Abs(character - 2.1944f) < .00001f &&
              MathF.Abs(fallback - 1.8f) < .00001f &&
              ViewSubjectLaw.PivotHeight(0f, 0f, 0f, 0f) == ViewSubjectLaw.PivotFloor &&
              ViewSubjectLaw.EyeTarget(new Vector3(100, 20, 3), 2) ==
                  new Vector3(100, 20, 5) &&
              ViewSubjectLaw.VoteBody(true).SequenceEqual(new byte[] { 1 }) &&
              ViewSubjectLaw.VoteBody(false).SequenceEqual(new byte[] { 0 }),
            "far-sight pivot/target/vote law drift");

        var created = new ObjectFields().AsCreated();
        created.SetGuid(ObjectFields.PLAYER_FARSIGHT, 0x0000567800001234ul);
        Check(created.PlayerFarsight == 0x0000567800001234ul &&
              ObjectFields.PLAYER_FARSIGHT == ViewSubjectLaw.PlayerFarsightField &&
              (ushort)Op.CMSG_FAR_SIGHT == 0x027A,
            "PLAYER_FARSIGHT descriptor or CMSG_FAR_SIGHT opcode drift");

        string root = ClientConfig.FindRepoRoot();
        string host = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.ViewSubject.cs"));
        string program = SourceText.Read(Path.Combine(root, "MSUIClient", "Program.cs"));
        string sound = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Sound.cs"));
        Check(host.Contains("_entities.TryGet(net.PlayerGuid", StringComparison.Ordinal) &&
              !host.Contains("ControlledGuid", StringComparison.Ordinal) &&
              host.Contains("_net?.FarSight(anchor != 0)", StringComparison.Ordinal) &&
              host.Contains("camera.AuthoredTarget = null", StringComparison.Ordinal) &&
              host.Contains("_entities.TryGet(anchor", StringComparison.Ordinal) &&
              program.Contains("_window.Camera.Target = _controller.Position;\n        UpdateViewSubject();",
                  StringComparison.Ordinal) &&
              sound.Contains("SpatialAudioLaw.CharacterListener(_controller.Position)",
                  StringComparison.Ordinal),
            "far-sight local-owner/edge-vote/body-fallback/character-audio wiring drift");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}

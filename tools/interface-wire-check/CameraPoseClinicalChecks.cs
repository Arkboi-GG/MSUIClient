using MSUIClient;
using MSUIClient.Engine;

internal static class CameraPoseClinicalChecks
{
    public static void Run()
    {
        float referencePitch = 13.449968f * MathF.PI / 180f;
        Check(CameraPoseLaw.Render(16.068569f, referencePitch) ==
              "cameraDistance 16.068569\ncameraPitch 13.449968\n",
            "camera pose writer shape/order/precision drift");

        CameraPoseLaw.Pose parsed = CameraPoseLaw.Parse(
            "futureKey 7\r\nCAMERADISTANCE 12.5\r\ncamerapitch 10.0\r\n",
            1.5f, 40f, Camera.PitchLimit);
        Check(parsed.Distance == 12.5f && parsed.PitchRadians is float down && down > 0 &&
              MathF.Abs(down - 10f * MathF.PI / 180f) < .00001f,
            "camera pose permissive parse or positive-looking-down convention drift");

        CameraPoseLaw.Pose clamped = CameraPoseLaw.Parse(
            "cameraDistance 999\ncameraPitch -400\n", 1.5f, 40f, Camera.PitchLimit);
        CameraPoseLaw.Pose independent = CameraPoseLaw.Parse(
            "cameraDistance banana\ncameraPitch 5\n", 1.5f, 40f, Camera.PitchLimit);
        Check(clamped.Distance == 40f && clamped.PitchRadians == -Camera.PitchLimit &&
              independent.Distance is null && independent.PitchRadians is not null &&
              CameraPoseLaw.CharacterFileName("Hydraxian Waterlords", "Probe/one") ==
                  "Hydraxian_Waterlords-Probe_one.txt" &&
              CameraPoseLaw.CharacterFileName("", "") == "unknown-unknown.txt",
            "camera pose clamps/independent keys/path token drift");

        string root = ClientConfig.FindRepoRoot();
        string host = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.CameraPose.cs"));
        string net = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Scene",
            "GameLoop.Net.cs"));
        string logout = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Logout.cs"));
        string program = SourceText.Read(Path.Combine(root, "MSUIClient", "Program.cs"));
        Check(host.Contains("camera.Distance = camera.EffectiveDistance = distance;",
                  StringComparison.Ordinal) &&
              host.Contains("CameraPoseLaw.Render(camera.Distance, camera.Pitch)",
                  StringComparison.Ordinal) &&
              !host.Contains("Render(camera.EffectiveDistance", StringComparison.Ordinal) &&
              host.Contains("Path.Combine(_config.RepoRoot, \"camera\"",
                  StringComparison.Ordinal) &&
              host.Contains("File.Move(temporary, path, overwrite: true);",
                  StringComparison.Ordinal) &&
              !host.Contains("OrbitYaw", StringComparison.Ordinal) &&
              !host.Contains("camera.Yaw", StringComparison.Ordinal),
            "camera pose load/save/path/atomic host drift");

        int disconnectSave = net.IndexOf(
            "SaveCameraPoseForSession(forgetIdentity: true);", StringComparison.Ordinal);
        int identityReset = net.IndexOf("ResetPlayerIdentitySession();",
            disconnectSave, StringComparison.Ordinal);
        int entryLoad = net.IndexOf("LoadCameraPoseForWorldEntry();", StringComparison.Ordinal);
        int disposeSave = program.IndexOf("try { SaveCameraPoseForSession(); }",
            StringComparison.Ordinal);
        int netDispose = program.IndexOf("_net?.Dispose();", StringComparison.Ordinal);
        Check(disconnectSave >= 0 && identityReset > disconnectSave && entryLoad >= 0 &&
              logout.Contains("SaveCameraPoseForSession(forgetIdentity: true);",
                  StringComparison.Ordinal) &&
              disposeSave >= 0 && netDispose > disposeSave,
            "camera pose load-on-entry or session/app-exit lifecycle drift");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}

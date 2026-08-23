using System.Numerics;
using MSUIClient;
using MSUIClient.Player;

internal static class SwimmingClinicalChecks
{
    public static void Run()
    {
        Check(SwimmingMovementLaw.EnterDepth(2f) == 1.5f &&
              MathF.Abs(SwimmingMovementLaw.ExitDepth(2f) -
                        (1.5f - 1f / 36f)) < .0001f &&
              !SwimmingMovementLaw.NextState(false, 1.5f, 0f, 2f) &&
              SwimmingMovementLaw.NextState(false, 1.5001f, 0f, 2f) &&
              SwimmingMovementLaw.NextState(true, 1.5f - 1f / 36f, 0f, 2f) &&
              !SwimmingMovementLaw.NextState(true, 1.47f, 0f, 2f),
            "swim enter/exit strictness or 1/36-yard hysteresis drift");
        Vector3 up = SwimmingMovementLaw.DesiredVelocity(0f, MathF.PI / 2f, 1f, 0f,
            4f, 2f);
        Vector3 back = SwimmingMovementLaw.DesiredVelocity(0f, 0f, -1f, 0f, 4f, 2f);
        Vector3 redirected = SwimmingMovementLaw.RedirectAtRestLine(
            new Vector3(3f, 0f, 4f), 0f);
        Check(MathF.Abs(up.Z - 4f) < .0001f && back == new Vector3(-2f, 0f, 0f) &&
              MathF.Abs(redirected.X - 5f) < .0001f && redirected.Z == 0f,
            "pitched swim velocity, backward min-speed, or top-cap redirect drift");

        string root = ClientConfig.FindRepoRoot();
        string controller = SourceText.Read(Path.Combine(root, "MSUIClient", "Player",
            "CharacterController.cs"));
        string sender = SourceText.Read(Path.Combine(root, "MSUIClient", "Net",
            "LocalMovementSender.cs"));
        string program = SourceText.Read(Path.Combine(root, "MSUIClient", "Program.cs"));
        string renderer = SourceText.Read(Path.Combine(root, "MSUIClient", "World", "Units",
            "CharacterRenderer.cs"));
        Check(controller.Contains("SwimmingMovementLaw.NextState", StringComparison.Ordinal) &&
              controller.Contains("UpdateSwimming(dt, input)", StringComparison.Ordinal) &&
              sender.Contains("Op.MSG_MOVE_START_SWIM", StringComparison.Ordinal) &&
              sender.Contains("Op.MSG_MOVE_STOP_SWIM", StringComparison.Ordinal) &&
              sender.Contains("info.Pitch = controller.SwimPitch", StringComparison.Ordinal) &&
              program.Contains("Pitch = -_window.Camera.Pitch", StringComparison.Ordinal) &&
              program.Contains("_controller.LiquidSurfaceZ = movementLiquidZ",
                  StringComparison.Ordinal) &&
              renderer.Contains("int swimId = !state.Moving ? 41", StringComparison.Ordinal),
            "swim controller/wire/liquid/animation wiring drift");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}

using MSUIClient;
using MSUIClient.Net;
using MSUIClient.Player;

internal static class DrunkMovementClinicalChecks
{
    public static void Run()
    {
        Check(DrunkMovementLaw.Fraction(0) == 0f &&
              DrunkMovementLaw.Fraction(50) == .5f &&
              DrunkMovementLaw.Fraction(100) == DrunkMovementLaw.Fraction(255) &&
              MathF.Abs(DrunkMovementLaw.Fraction(100) - 1f) < 1e-6f,
            "drunk byte fraction scale or clamp drift");
        Check(DrunkMovementLaw.Wobble(123_456, 0f) == 0f &&
              MathF.Abs(DrunkMovementLaw.Wobble(0, 1f) -
                        DrunkMovementLaw.PulseAmplitude) < 1e-7f &&
              MathF.Abs(DrunkMovementLaw.Wobble(0, .5f) -
                        DrunkMovementLaw.PulseAmplitude * .5f) < 1e-7f,
            "drunk pulse sober or phase-zero amplitude drift");

        float lo = float.MaxValue;
        float hi = float.MinValue;
        for (uint ms = 0; ms < 60_000; ms += 16)
        {
            float wobble = DrunkMovementLaw.Wobble(ms, 1f);
            Check(MathF.Abs(wobble) <= DrunkMovementLaw.PulseAmplitude * 1.0001f,
                $"drunk pulse exceeded amplitude at {ms}ms");
            lo = MathF.Min(lo, wobble);
            hi = MathF.Max(hi, wobble);
        }
        Check(hi > DrunkMovementLaw.PulseAmplitude * .5f &&
              lo < -DrunkMovementLaw.PulseAmplitude * .5f,
            "drunk pulse did not visit both signs");
        Check(DrunkMovementLaw.FacingWobble(.1f, true, false) == .1f &&
              DrunkMovementLaw.FacingWobble(.1f, true, true) == 0f &&
              DrunkMovementLaw.SwimPitchWobble(.1f, true, true) == .4f,
            "drunk facing/keyboard-turn/swim gate drift");

        var fields = new ObjectFields();
        fields.SetU32(ObjectFields.PLAYER_BYTES_3, 73u << 8);
        Check(fields.PlayerDrunkByte == 73, "PLAYER_BYTES_3 byte-one accessor drift");

        string root = ClientConfig.FindRepoRoot();
        string program = SourceText.Read(Path.Combine(root, "MSUIClient", "Program.cs"));
        Check(program.Contains("_entities.TryGet(ControlledGuid, out WorldEntity movementPlayer) && movementPlayer.IsPlayer",
                  StringComparison.Ordinal) &&
              program.Contains("DrunkMovementLaw.FacingWobble", StringComparison.Ordinal) &&
              program.Contains("keyboardTurning: MathF.Abs(turn) > 0.01f", StringComparison.Ordinal) &&
              program.Contains("DrunkMovementLaw.SwimPitchWobble", StringComparison.Ordinal) &&
              !program.Contains("DrunkMovementLaw.Fov", StringComparison.Ordinal),
            "logged-in-player drunk movement wiring or no-FOV contract drift");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}

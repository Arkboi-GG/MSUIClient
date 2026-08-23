namespace MSUIClient.Player;

/// <summary>
/// Current Benilla's build-5875 drunken movement pulse. This is movement-owned:
/// normal play does not apply a screen-space or field-of-view effect.
/// </summary>
public static class DrunkMovementLaw
{
    private static readonly float DrunkScale = BitConverter.UInt32BitsToSingle(0x3c23_d70a);
    private static readonly float DegreesToRadians = BitConverter.UInt32BitsToSingle(0x3c8e_fa35);
    private static readonly float PulseFrequency1 = BitConverter.UInt32BitsToSingle(0x3dbe_76c9);
    private static readonly float PulseFrequency2 = BitConverter.UInt32BitsToSingle(0x3e1d_b22d);
    private static readonly float PulseFrequency3 = BitConverter.UInt32BitsToSingle(0x3e47_ae14);
    private static readonly float PulseMean = BitConverter.UInt32BitsToSingle(0x3eaa_aa9f);
    public static readonly float PulseAmplitude = BitConverter.UInt32BitsToSingle(0x3c56_7750);

    public const float SwimPitchScale = 4f;

    public static float Fraction(byte value) =>
        (float)(Math.Min(value, (byte)100) * (double)DrunkScale);

    public static float Wobble(uint nowMs, float fraction)
    {
        if (fraction == 0f) return 0f;

        double phase = nowMs * (double)DegreesToRadians * fraction;
        double roundedPhase = (float)phase;
        double c1 = Math.Cos(phase * PulseFrequency1);
        double c2 = Math.Cos(roundedPhase * PulseFrequency2);
        double c3 = Math.Cos(roundedPhase * PulseFrequency3);
        double mean = ((c1 + c2) + c3) * PulseMean;
        return (float)(mean * (PulseAmplitude * (double)fraction));
    }

    public static float FacingWobble(float wobble, bool translating, bool keyboardTurning) =>
        translating && !keyboardTurning ? wobble : 0f;

    public static float SwimPitchWobble(float wobble, bool swimming, bool translating) =>
        swimming && translating ? wobble * SwimPitchScale : 0f;
}

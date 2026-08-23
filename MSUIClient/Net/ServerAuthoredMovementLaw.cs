namespace MSUIClient.Net;

/// <summary>Build-5875's merge law for a bare server-authored move addressed to our mover.</summary>
public static class ServerAuthoredMovementLaw
{
    // Client.exe 0x618c30: old ^ ((old ^ wire) & 0x75a07dff). ON_TRANSPORT is
    // deliberately outside this mask, so a correction cannot board/deboard the player.
    public const uint AuthoredFlagMask = 0x75A07DFF;

    public static uint MergeFlags(uint local, uint wire) =>
        (local & ~AuthoredFlagMask) | (wire & AuthoredFlagMask);

    public static float FacingDelta(float local, float wire) => WrapPi(wire - local);

    public static float WrapTau(float radians)
    {
        float result = radians % MathF.Tau;
        return result < 0f ? result + MathF.Tau : result;
    }

    private static float WrapPi(float radians)
    {
        float result = (radians + MathF.PI) % MathF.Tau;
        if (result < 0f) result += MathF.Tau;
        return result - MathF.PI;
    }
}

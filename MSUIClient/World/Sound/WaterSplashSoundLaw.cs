namespace MSUIClient.World.Sound;

/// <summary>Current Benilla's unit water-crossing trigger, separate from wading footsteps.</summary>
public static class WaterSplashSoundLaw
{
    public const uint MediumSplashKit = 1096;
    public const float CollisionHeightFraction = .4f;

    public static bool BeyondSplashLine(float? surfaceHeight, float feetHeight,
        float collisionHeight) =>
        surfaceHeight is float surface && float.IsFinite(surface) &&
        float.IsFinite(feetHeight) && float.IsFinite(collisionHeight) &&
        surface - feetHeight > CollisionHeightFraction * MathF.Max(0f, collisionHeight);

    public static bool Crossed(bool previouslyBeyond, bool beyond) => previouslyBeyond != beyond;
}

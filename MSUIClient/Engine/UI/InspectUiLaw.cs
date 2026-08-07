namespace MSUIClient.Engine.UI;

/// <summary>Observable build-5875 inspect gates and model rotation constants.</summary>
public static class InspectUiLaw
{
    public const float MaxDistance = 10f;
    public const float DefaultFacing = 0.61f;
    public const float TapRadians = 0.03f;
    public const float RotationsPerSecond = 0.5f;

    public static bool CanInspect(bool isPlayer, bool isSelf, bool attackable, float distanceSquared)
        => isPlayer && !isSelf && !attackable && distanceSquared <= MaxDistance * MaxDistance;

    public static float ClickFacing(float facing, bool left)
        => facing + (left ? -TapRadians : TapRadians);

    public static float HeldFacing(float facing, bool left, float elapsed)
    {
        float step = Math.Max(0f, elapsed) * 2f * MathF.PI * RotationsPerSecond;
        // This sign reversal between tap and hold is the reference behavior.
        return Wrap(facing + (left ? step : -step));
    }

    public static float Wrap(float facing)
    {
        float turn = 2f * MathF.PI;
        facing %= turn;
        return facing < 0f ? facing + turn : facing;
    }
}

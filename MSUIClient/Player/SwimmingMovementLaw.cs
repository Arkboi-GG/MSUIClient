using System.Numerics;

namespace MSUIClient.Player;

/// <summary>Current Benilla's verified build-5875 swimming thresholds and floating motion.</summary>
public static class SwimmingMovementLaw
{
    public const float DefaultForwardSpeed = 4.722222f;
    public const float DefaultBackwardSpeed = 2.5f;
    public const float JumpSpeed = 9.096748f;
    public const float DepthFraction = 0.75f;
    public const float Hysteresis = 1f / 36f;

    public static float EnterDepth(float collisionHeight) =>
        DepthFraction * MathF.Max(0.01f, collisionHeight);

    public static float ExitDepth(float collisionHeight) =>
        EnterDepth(collisionHeight) - Hysteresis;

    public static bool NextState(bool swimming, float? surfaceZ, float feetZ,
        float collisionHeight, bool breachBlocked = false)
    {
        if (surfaceZ is not float surface) return false;
        float depth = surface - feetZ;
        return swimming
            ? depth >= ExitDepth(collisionHeight)
            : !breachBlocked && depth > EnterDepth(collisionHeight);
    }

    public static float RestLine(float surfaceZ, float collisionHeight) =>
        surfaceZ - EnterDepth(collisionHeight);

    public static Vector3 DesiredVelocity(float yaw, float pitch, float forwardInput,
        float strafeInput, float forwardSpeed, float backwardSpeed)
    {
        Vector3 flatForward = new(MathF.Cos(yaw), MathF.Sin(yaw), 0f);
        Vector3 right = new(MathF.Sin(yaw), -MathF.Cos(yaw), 0f);
        Vector3 pitchedForward = flatForward * MathF.Cos(pitch) +
                                 Vector3.UnitZ * MathF.Sin(pitch);
        Vector3 wish = pitchedForward * forwardInput + right * strafeInput;
        if (wish.LengthSquared() < 1e-8f) return Vector3.Zero;
        float speed = forwardInput < -0.01f
            ? MathF.Min(forwardSpeed, backwardSpeed)
            : forwardSpeed;
        return Vector3.Normalize(wish) * MathF.Max(0f, speed);
    }

    /// <summary>Redirect a rising stroke level at the top cap while preserving total speed.</summary>
    public static Vector3 RedirectAtRestLine(Vector3 velocity, float maximumRisePerSecond)
    {
        if (velocity.Z <= maximumRisePerSecond) return velocity;
        float speed = velocity.Length();
        Vector2 level = new(velocity.X, velocity.Y);
        if (level.LengthSquared() < 1e-8f) return new Vector3(0f, 0f,
            Math.Clamp(maximumRisePerSecond, 0f, speed));
        float z = Math.Clamp(maximumRisePerSecond, 0f, speed);
        float levelSpeed = MathF.Sqrt(MathF.Max(0f, speed * speed - z * z));
        level = Vector2.Normalize(level) * levelSpeed;
        return new Vector3(level.X, level.Y, z);
    }
}

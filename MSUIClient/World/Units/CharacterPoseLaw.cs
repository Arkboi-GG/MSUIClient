namespace MSUIClient.World.Units;

/// <summary>Pure character-pose boundaries shared by runtime rendering and regression checks.</summary>
public static class CharacterPoseLaw
{
    /// <summary>
    /// Splits a moving strafe between the model heading and upper body. A stationary body/aim
    /// chase is a whole-body heading lag, not a strafe twist; sparse turn clips get their missing
    /// shoulder channels from Stand separately in M2Animator.
    /// </summary>
    public static float TorsoCounterYaw(bool bindPose, bool frozenStandPose, bool splitStyle,
        bool moving, bool forcedDiagnostic, float torsoFollow, float bodyOffsetYaw)
    {
        if (bindPose || frozenStandPose || !splitStyle || !moving && !forcedDiagnostic) return 0f;
        return (Math.Clamp(torsoFollow, 0f, 1f) - 1f) * bodyOffsetYaw;
    }

    /// <summary>
    /// Signed body step for a stationary turn. Steering still enforces the lag ceiling
    /// immediately; after release, catch-up is rate-limited so the shuffle visibly carries the
    /// body back onto the aim instead of closing ninety degrees in roughly four frames.
    /// </summary>
    public static float StandingBodyStep(float deltaYaw, bool steering, float ceilingRadians,
        float dt, float bodyTurnRate, float chaseRate)
    {
        float magnitude = MathF.Abs(deltaYaw);
        float wanted = steering
            ? MathF.Max(magnitude - MathF.Max(0f, ceilingRadians), 0f)
            : magnitude;

        if (!steering)
        {
            float maxStep = MathF.Max(0f, dt) * MathF.Max(0f, bodyTurnRate) *
                MathF.Max(0f, chaseRate);
            wanted = MathF.Min(wanted, maxStep);
        }

        return MathF.CopySign(MathF.Min(wanted, magnitude), deltaYaw);
    }
}

namespace MSUIClient.Net;

/// <summary>Core 1.12 spline fall timing (Movement/spline/util.cpp), starting at rest.</summary>
public static class FallTrajectory
{
    public const double Gravity = 19.29110527038574;
    public const float TerminalVelocity = 60.148003f;
    private static readonly double TerminalTime = TerminalVelocity / Gravity;
    private static readonly double TerminalDistance = TerminalVelocity * TerminalVelocity / (2 * Gravity);

    public static float Distance(double seconds)
    {
        seconds = Math.Max(0, seconds);
        return (float)(seconds > TerminalTime
            ? TerminalDistance + TerminalVelocity * (seconds - TerminalTime)
            : .5 * Gravity * seconds * seconds);
    }

    public static float Time(float distance) => distance <= 0 ? 0 : (float)(distance >= TerminalDistance
        ? (distance - TerminalDistance) / TerminalVelocity + TerminalTime
        : Math.Sqrt(2 * distance / Gravity));
}

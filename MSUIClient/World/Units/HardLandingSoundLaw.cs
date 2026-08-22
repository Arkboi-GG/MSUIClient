namespace MSUIClient.World.Units;

public readonly record struct HardLandingArcStep(float? StartZ, float? Descent);

/// <summary>
/// Client-predicted fall-height law shared by the controlled landing sound path.
/// A fall begins on the grounded-to-airborne edge, retains that launch height
/// for the whole arc, and reports launch Z minus landing Z exactly once.
/// </summary>
public static class HardLandingSoundLaw
{
    public const float DescentThreshold = 13f;

    public static HardLandingArcStep Step(
        bool wasAirborne,
        bool nowAirborne,
        float? startZ,
        float previousZ,
        float currentZ) => (wasAirborne, nowAirborne) switch
        {
            (false, true) => new(previousZ, null),
            (true, true) => new(startZ, null),
            (true, false) => new(null, startZ is float launchZ ? launchZ - currentZ : null),
            _ => new(null, null),
        };

    public static bool IsHardLanding(float descent) =>
        float.IsFinite(descent) && descent > DescentThreshold;
}

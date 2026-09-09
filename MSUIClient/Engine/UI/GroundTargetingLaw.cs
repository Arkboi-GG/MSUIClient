using System.Numerics;
using MSUIClient.Formats;

namespace MSUIClient.Engine.UI;

/// <summary>Ground hover feedback only; the server still judges the committed point.</summary>
public static class GroundTargetingLaw
{
    public const float DefaultRadius = 1.3888889f;
    public const float ProjectionHalfHeight = 2f;

    public static bool InRange(SpellRangeRow? range, Vector3 actor, Vector3 point)
    {
        float distance = Vector3.DistanceSquared(actor, point);
        if (!float.IsFinite(distance)) return false;
        return range is not { } row ||
            ((row.Min <= 0 || distance >= row.Min * row.Min) && distance <= row.Max * row.Max);
    }

    public static float Radius(bool acceptable, float authoredRadius) =>
        acceptable && authoredRadius > 0f
            ? Math.Min(authoredRadius, 20f) : DefaultRadius;
}

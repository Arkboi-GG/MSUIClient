namespace MSUIClient.World.Units;

/// <summary>
/// Reference-compatible visibility election for unit animation sound tracks.
/// Frustum loss parks a track only after a short grace window so a unit grazing
/// the camera edge cannot repeatedly destroy and recreate its event clock.
/// </summary>
public static class AnimationEventElectionLaw
{
    public const float ParkGraceSeconds = 0.5f;

    /// <summary>The reference expands the unit's selection footprint by four
    /// yards, with a six-yard fallback when no useful radius is available.</summary>
    public static float PaddedRadius(float renderedScale) =>
        MathF.Max(6f, MathF.Max(0f, renderedScale) * 2f + 4f);

    /// <param name="outOfViewSince">Negative means the track is not parked or
    /// waiting to park. The caller stores the non-negative timestamp per unit.</param>
    public static bool IsElected(bool visible, bool moreAudible, float now,
        ref float outOfViewSince)
    {
        if (visible || moreAudible)
        {
            outOfViewSince = -1f;
            return true;
        }

        if (outOfViewSince < 0f || !float.IsFinite(outOfViewSince) || now < outOfViewSince)
        {
            outOfViewSince = now;
            return true;
        }
        return now - outOfViewSince < ParkGraceSeconds;
    }
}

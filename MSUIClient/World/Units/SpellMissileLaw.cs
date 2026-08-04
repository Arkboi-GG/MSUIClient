using System.Numerics;
using MSUIClient.Formats;

namespace MSUIClient.World.Units;

/// <summary>
/// Pure vanilla missile decisions: release marker/finish fallback, fixed GO-time deadline,
/// arrive-on-time homing, and the roll-free parsed-model-to-world flight frame.
/// </summary>
public static class SpellMissileLaw
{
    public const float MissingAnimationWaitSeconds = .25f;
    public static readonly string[] ReleaseIdentifiers = ["$CSL", "$CSR", "$CST", "$BWR"];
    public static readonly string[] MarkerFallbacks = ["$CSL", "$CSR", "$CST"];

    public readonly record struct Release(float DelaySeconds, bool StrictlyAfterDelay,
        bool UsesMarker, ushort Bone, Vector3 Position, string Identifier);

    public readonly record struct Motion(Vector3 Position, Vector3 Direction,
        double RemainingSeconds, bool Arrived);

    /// <summary>
    /// A playing cast one-shot releases at its first authored release event. With no such event,
    /// its animation-finish edge flushes through the CSL/CSR/CST marker cascade. No requested
    /// animation launches immediately; a requested animation absent from this model uses the
    /// reference-port's 250 ms never-started backstop.
    /// </summary>
    public static Release ResolveRelease(M2Model? model, ushort? animationId)
    {
        if (model is null)
        {
            float unavailableDelay = animationId is { } unavailable && unavailable != 0
                ? MissingAnimationWaitSeconds : 0f;
            return new Release(unavailableDelay, unavailableDelay > 0f,
                false, 0, default, "");
        }

        int sequenceIndex = animationId is { } id && id != 0
            ? model.TryFindSequenceIndexByAnimationId(id) : -1;
        if (sequenceIndex >= 0)
        {
            M2Sequence sequence = model.Sequences[sequenceIndex];
            var fired = model.Events.SelectMany(e => e.Times
                    .Where(t => t >= sequence.StartTimestamp && t <= sequence.EndTimestamp)
                    .Select(t => (Event: e, Time: t)))
                .Where(x => ReleaseIdentifiers.Contains(x.Event.Identifier,
                    StringComparer.Ordinal))
                .OrderBy(x => x.Time)
                .FirstOrDefault();
            if (fired.Event is not null)
                return new Release(Math.Max(0f,
                        (fired.Time - sequence.StartTimestamp) / 1000f),
                    false, true, fired.Event.Bone, fired.Event.Position,
                    fired.Event.Identifier);

            float span = Math.Max(0f,
                (sequence.EndTimestamp - sequence.StartTimestamp) / 1000f);
            return Cascade(model, span);
        }

        float delay = animationId is { } requested && requested != 0
            ? MissingAnimationWaitSeconds : 0f;
        return Cascade(model, delay, delay > 0f);
    }

    private static Release Cascade(M2Model model, float delay, bool strictlyAfterDelay = false)
    {
        foreach (string identifier in MarkerFallbacks)
            if (model.Events.FirstOrDefault(e => e.Identifier == identifier) is { } marker)
                return new Release(delay, strictlyAfterDelay, true, marker.Bone,
                    marker.Position, identifier);
        return new Release(delay, strictlyAfterDelay, false, 0, default, "");
    }

    public static double RemainingAtRelease(float distance, float speed, double queuedSeconds)
        => speed > 0f && float.IsFinite(distance) && float.IsFinite(speed)
            ? distance / speed - Math.Max(0, queuedSeconds)
            : 0;

    /// <summary>Consume real elapsed time; missile movement is not particle simulation.</summary>
    public static Motion Advance(Vector3 position, Vector3 destination, Vector3 direction,
        double remainingSeconds, double elapsedSeconds)
    {
        double dt = double.IsFinite(elapsedSeconds) ? Math.Max(0, elapsedSeconds) : 0;
        if (remainingSeconds <= dt)
            return new Motion(position, direction, remainingSeconds, Arrived: true);
        if (dt == 0)
            return new Motion(position, direction, remainingSeconds, Arrived: false);

        Vector3 gap = destination - position;
        Vector3 next = position + gap * (float)(dt / remainingSeconds);
        return new Motion(next, gap.LengthSquared() > 1e-8f ? gap : direction,
            remainingSeconds - dt, Arrived: false);
    }

    /// <summary>
    /// MSUI M2 data is parsed into Y-up model space. Authored +X points down the flight path,
    /// parsed +Y remains world-up-ish, and parsed +Z completes the roll-free right-handed frame.
    /// </summary>
    public static Matrix4x4 FlightTransform(Vector3 position, Vector3 direction)
    {
        Vector3 forward = direction.LengthSquared() > 1e-8f
            ? Vector3.Normalize(direction) : Vector3.UnitX;
        Vector3 referenceUp = MathF.Abs(Vector3.Dot(forward, Vector3.UnitZ)) > .98f
            ? Vector3.UnitY : Vector3.UnitZ;
        Vector3 side = Vector3.Normalize(Vector3.Cross(referenceUp, forward));
        Vector3 up = Vector3.Normalize(Vector3.Cross(forward, side));
        Vector3 parsedZ = -side;
        return new Matrix4x4(
            forward.X, forward.Y, forward.Z, 0f,
            up.X, up.Y, up.Z, 0f,
            parsedZ.X, parsedZ.Y, parsedZ.Z, 0f,
            position.X, position.Y, position.Z, 1f);
    }
}

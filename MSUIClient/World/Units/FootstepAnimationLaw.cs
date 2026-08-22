using MSUIClient.Formats;

namespace MSUIClient.World.Units;

/// <summary>
/// Archived animation-event half of footstep sound.  Only $FSD is a sound
/// event; the per-foot $FL/$FR/$RL/... family drives decals and spray.
/// </summary>
public static class FootstepAnimationLaw
{
    public const string SoundIdentifier = "$FSD";
    private const int MaxEventsPerTick = 64;

    public static int CountCrossings(M2Model model, M2Animator.Clip clip,
        double previousAgeSeconds, double currentAgeSeconds)
    {
        if (clip.SequenceIndex < 0 || clip.SequenceIndex >= model.Sequences.Count ||
            !double.IsFinite(previousAgeSeconds) || !double.IsFinite(currentAgeSeconds) ||
            currentAgeSeconds <= previousAgeSeconds || currentAgeSeconds < 0)
            return 0;

        M2Sequence sequence = model.Sequences[clip.SequenceIndex];
        double span = clip.DurationSeconds;
        if (span <= 0) return 0;

        int count = 0;
        foreach (M2EventMarker marker in model.Events)
        {
            if (!string.Equals(marker.Identifier, SoundIdentifier,
                    StringComparison.Ordinal)) continue;
            foreach (uint timestamp in marker.Times)
            {
                if (timestamp < sequence.StartTimestamp || timestamp > sequence.EndTimestamp)
                    continue;
                double offset = (timestamp - sequence.StartTimestamp) / 1000.0;
                if (!clip.Looping)
                {
                    if (offset > previousAgeSeconds && offset <= currentAgeSeconds &&
                        ++count >= MaxEventsPerTick) return count;
                    continue;
                }

                long cycle = Math.Max(0L,
                    (long)Math.Floor((previousAgeSeconds - offset) / span) + 1L);
                for (;; cycle++)
                {
                    double occurrence = cycle * span + offset;
                    if (occurrence > currentAgeSeconds + 1e-9) break;
                    if (occurrence > previousAgeSeconds && ++count >= MaxEventsPerTick)
                        return count;
                }
            }
        }
        return count;
    }
}

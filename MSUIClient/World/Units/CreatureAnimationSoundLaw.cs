using MSUIClient.Formats;

namespace MSUIClient.World.Units;

/// <summary>CreatureSoundData vocal tags carried by a model's live base clip.</summary>
public static class CreatureAnimationSoundLaw
{
    private const int MaxEventsPerTick = 64;

    public static IReadOnlyList<string> CrossedVocalEvents(M2Model model,
        M2Animator.Clip clip, double previousAgeSeconds, double currentAgeSeconds)
    {
        if (clip.SequenceIndex < 0 || clip.SequenceIndex >= model.Sequences.Count ||
            !double.IsFinite(previousAgeSeconds) || !double.IsFinite(currentAgeSeconds) ||
            currentAgeSeconds <= previousAgeSeconds || currentAgeSeconds < 0)
            return [];

        M2Sequence sequence = model.Sequences[clip.SequenceIndex];
        double span = clip.DurationSeconds;
        if (span <= 0) return [];
        List<(double At, string Identifier)>? crossed = null;

        foreach (M2EventMarker marker in model.Events)
        {
            if (!IsSoundEvent(marker.Identifier)) continue;
            foreach (uint timestamp in marker.Times)
            {
                if (timestamp < sequence.StartTimestamp || timestamp > sequence.EndTimestamp)
                    continue;
                double offset = (timestamp - sequence.StartTimestamp) / 1000.0;
                if (!clip.Looping)
                {
                    if (offset > previousAgeSeconds && offset <= currentAgeSeconds)
                        (crossed ??= []).Add((offset, marker.Identifier));
                    if (crossed?.Count >= MaxEventsPerTick) break;
                    continue;
                }

                long cycle = Math.Max(0L,
                    (long)Math.Floor((previousAgeSeconds - offset) / span) + 1L);
                for (;; cycle++)
                {
                    double occurrence = cycle * span + offset;
                    if (occurrence > currentAgeSeconds + 1e-9) break;
                    if (occurrence > previousAgeSeconds)
                        (crossed ??= []).Add((occurrence, marker.Identifier));
                    if (crossed?.Count >= MaxEventsPerTick) break;
                }
                if (crossed?.Count >= MaxEventsPerTick) break;
            }
            if (crossed?.Count >= MaxEventsPerTick) break;
        }

        if (crossed is null) return [];
        crossed.Sort(static (a, b) => a.At.CompareTo(b.At));
        return crossed.Select(static e => e.Identifier).ToArray();
    }

    public static bool StandChancePass(uint roll) =>
        ((101ul * roll) >> 32) <= 40ul;

    private static bool IsSoundEvent(string identifier) => identifier is
        "$FD1" or "$FD2" or "$FD3" or "$FD4" or "$FDX" or "$WNG" or "$WGG" or
        "$CSS" or "$CAH" or "$AH0" or "$AH1" or "$AH2" or "$AH3";
}

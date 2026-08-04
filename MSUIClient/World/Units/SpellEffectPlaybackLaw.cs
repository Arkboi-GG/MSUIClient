using System.Numerics;
using MSUIClient.Formats;

namespace MSUIClient.World.Units;

/// <summary>
/// The one playback decision shared by every visible part of an effect model.
/// Ordinary spell effects use file-order sequence zero. Missiles prefer the
/// authored InFlight animation (AnimationData 144) and fall back to sequence
/// zero when the model does not provide it.
/// </summary>
public readonly record struct SpellEffectPlayback(
    int SequenceIndex,
    int AnimationId,
    double SpanSeconds,
    bool Looping);

public readonly record struct SpellEffectSoundEvent(
    uint SoundId,
    string Identifier,
    double OccurrenceSeconds,
    ushort Bone,
    Vector3 Position);

public static class SpellEffectPlaybackLaw
{
    public const int InFlightAnimationId = 144;
    private const int MaxEventsPerTick = 64;
    private static readonly HashSet<string> SoundIdentifiers =
        ["$SND", "$DSL", "$DSO"];

    public static SpellEffectPlayback Resolve(M2Model model, bool missile)
    {
        if (model.Sequences.Count == 0)
            return new(-1, 0, SpellAttachment.FallbackSpan, false);

        int sequence = missile
            ? model.TryFindSequenceIndexByAnimationId(InFlightAnimationId)
            : 0;
        if (sequence < 0) sequence = 0;
        M2Sequence selected = model.Sequences[sequence];
        double span = selected.DurationMs > 1
            ? selected.DurationMs / 1000.0
            : SpellAttachment.FallbackSpan;
        return new(sequence, selected.AnimationId, span, selected.IsLooping);
    }

    /// <summary>
    /// Find effect sound markers crossed on a monotonic instance clock.
    /// A clamped sequence fires each marker once; a looping sequence repeats it
    /// once per pass. Global animation clocks are deliberately unrelated.
    /// </summary>
    public static IReadOnlyList<SpellEffectSoundEvent> CrossedSoundEvents(
        M2Model model, in SpellEffectPlayback playback,
        double previousAgeSeconds, double currentAgeSeconds)
    {
        if (playback.SequenceIndex < 0 ||
            playback.SequenceIndex >= model.Sequences.Count ||
            !double.IsFinite(previousAgeSeconds) ||
            !double.IsFinite(currentAgeSeconds) ||
            currentAgeSeconds < 0 || currentAgeSeconds <= previousAgeSeconds)
            return [];

        M2Sequence sequence = model.Sequences[playback.SequenceIndex];
        double span = playback.SpanSeconds;
        if (span <= 0) return [];

        var crossed = new List<SpellEffectSoundEvent>();
        foreach (M2EventMarker marker in model.Events)
        {
            if (!SoundIdentifiers.Contains(marker.Identifier) || marker.Data == 0) continue;
            foreach (uint timestamp in marker.Times)
            {
                if (timestamp < sequence.StartTimestamp || timestamp > sequence.EndTimestamp)
                    continue;
                double offset = (timestamp - sequence.StartTimestamp) / 1000.0;
                if (!playback.Looping)
                {
                    if (offset > previousAgeSeconds && offset <= currentAgeSeconds)
                        crossed.Add(new(marker.Data, marker.Identifier, offset,
                            marker.Bone, marker.Position));
                    continue;
                }

                long cycle = Math.Max(0L,
                    (long)Math.Floor((previousAgeSeconds - offset) / span) + 1L);
                for (; crossed.Count < MaxEventsPerTick; cycle++)
                {
                    double occurrence = cycle * span + offset;
                    if (occurrence > currentAgeSeconds + 1e-9) break;
                    if (occurrence > previousAgeSeconds)
                        crossed.Add(new(marker.Data, marker.Identifier, occurrence,
                            marker.Bone, marker.Position));
                }
                if (crossed.Count >= MaxEventsPerTick) break;
            }
            if (crossed.Count >= MaxEventsPerTick) break;
        }

        // OrderBy is stable, preserving file order for markers authored at the
        // same timestamp while still merging repeated occurrences chronologically.
        return crossed.OrderBy(static e => e.OccurrenceSeconds).ToArray();
    }
}

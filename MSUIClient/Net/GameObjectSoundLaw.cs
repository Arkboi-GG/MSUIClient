using MSUIClient.Formats;

namespace MSUIClient.Net;

public readonly record struct GameObjectSlotEvent(int Slot, double OccurrenceSeconds);

public static class GameObjectSoundLaw
{
    public const int OpenSlot = 1;
    public const int CloseSlot = 3;
    private const int MaxEventsPerTick = 64;

    public static int StateSlot(uint state) => state switch
    {
        0 => OpenSlot,
        1 => CloseSlot,
        _ => -1,
    };

    public static int EventSlot(string identifier)
    {
        if (identifier.Length != 4 || identifier[0] != '$') return -1;
        char digit = identifier[3];
        if (identifier[1] == 'G' && identifier[2] == 'O' && digit is >= '0' and <= '5')
            return digit - '0';
        if (identifier[1] == 'G' && identifier[2] == 'C' && digit is >= '0' and <= '3')
            return 6 + digit - '0';
        return -1;
    }

    public static IReadOnlyList<GameObjectSlotEvent> CrossedEvents(M2Model model,
        int sequenceIndex, double previousSeconds, double currentSeconds)
    {
        if (sequenceIndex < 0 || sequenceIndex >= model.Sequences.Count ||
            !double.IsFinite(previousSeconds) || !double.IsFinite(currentSeconds) ||
            currentSeconds <= previousSeconds) return [];
        M2Sequence sequence = model.Sequences[sequenceIndex];
        double span = sequence.DurationMs / 1000.0;
        if (span <= 0) return [];

        var crossed = new List<GameObjectSlotEvent>();
        foreach (M2EventMarker marker in model.Events)
        {
            int slot = EventSlot(marker.Identifier);
            if (slot < 0) continue;
            foreach (uint timestamp in marker.Times)
            {
                if (timestamp < sequence.StartTimestamp || timestamp > sequence.EndTimestamp)
                    continue;
                double offset = (timestamp - sequence.StartTimestamp) / 1000.0;
                if (!sequence.IsLooping)
                {
                    if (offset > previousSeconds && offset <= currentSeconds)
                        crossed.Add(new(slot, offset));
                    continue;
                }
                long cycle = Math.Max(0L,
                    (long)Math.Floor((previousSeconds - offset) / span) + 1L);
                for (; crossed.Count < MaxEventsPerTick; cycle++)
                {
                    double occurrence = cycle * span + offset;
                    if (occurrence > currentSeconds + 1e-9) break;
                    if (occurrence > previousSeconds) crossed.Add(new(slot, occurrence));
                }
                if (crossed.Count >= MaxEventsPerTick) break;
            }
            if (crossed.Count >= MaxEventsPerTick) break;
        }
        return crossed.OrderBy(entry => entry.OccurrenceSeconds).ToArray();
    }
}

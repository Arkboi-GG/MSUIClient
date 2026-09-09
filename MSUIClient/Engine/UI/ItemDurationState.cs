namespace MSUIClient.Engine.UI;

public sealed class ItemDurationState
{
    private readonly Dictionary<ulong, double> _deadlines = [];

    public void Set(ulong guid, uint seconds, double now)
    {
        if (guid != 0) _deadlines[guid] = now + seconds;
    }

    public uint? RemainingSeconds(ulong guid, double now) =>
        _deadlines.TryGetValue(guid, out double deadline)
            ? (uint)Math.Clamp(Math.Ceiling(deadline - now), 0, uint.MaxValue) : null;

    public void Clear() => _deadlines.Clear();

    public static (string Key, string Fallback, uint Count) Display(uint seconds) => seconds switch
    {
        >= 86400 => ("ITEM_DURATION_DAYS", "Duration: %d days", seconds / 86400),
        >= 3600 => ("ITEM_DURATION_HOURS", "Duration: %d hrs", seconds / 3600),
        >= 60 => ("ITEM_DURATION_MIN", "Duration: %d min", seconds / 60),
        _ => ("ITEM_DURATION_SEC", "Duration: %d sec", seconds),
    };
}

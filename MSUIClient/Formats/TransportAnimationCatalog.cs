using System.Numerics;

namespace MSUIClient.Formats;

public readonly record struct ElevatorKeyframe(uint TimeMs, Vector3 LocalPosition);

/// <summary>
/// Build-5875 TransportAnimation.dbc, keyed by type-11 gameobject template entry.
/// The last keyframe time is the cycle period; positions are spawn-local WoW axes.
/// </summary>
public sealed class TransportAnimationCatalog
{
    public const string MpqPath = @"DBFilesClient\TransportAnimation.dbc";
    private readonly Dictionary<uint, ElevatorKeyframe[]> _paths = [];

    public int Count => _paths.Count;
    public bool TryGet(uint entry, out ElevatorKeyframe[] path) =>
        _paths.TryGetValue(entry, out path!);

    public static TransportAnimationCatalog? Load(MpqMount mpq)
    {
        DbcFile? dbc = mpq.ReadFile(MpqPath) is { } bytes ? DbcFile.Parse(bytes) : null;
        if (dbc is null || dbc.FieldCount < 7) return null;

        var grouped = new Dictionary<uint, List<ElevatorKeyframe>>();
        for (int row = 0; row < dbc.RecordCount; row++)
        {
            uint entry = dbc.GetUInt(row, 1);
            uint time = dbc.GetUInt(row, 2);
            Vector3 position = new(dbc.GetFloat(row, 3), dbc.GetFloat(row, 4),
                dbc.GetFloat(row, 5));
            if (!grouped.TryGetValue(entry, out List<ElevatorKeyframe>? frames))
                grouped[entry] = frames = [];
            frames.Add(new ElevatorKeyframe(time, position));
        }

        var result = new TransportAnimationCatalog();
        foreach ((uint entry, List<ElevatorKeyframe> frames) in grouped)
        {
            ElevatorKeyframe[] sorted = frames.OrderBy(frame => frame.TimeMs).ToArray();
            if (sorted.Length >= 2 && sorted[0].TimeMs == 0 && sorted[^1].TimeMs > 0)
                result._paths[entry] = sorted;
        }
        return result;
    }
}

/// <summary>Pure type-11 transport cycle evaluator.</summary>
public static class ElevatorTransportLaw
{
    public readonly record struct Sample(Vector3 Position, bool Moving);

    public static uint Period(IReadOnlyList<ElevatorKeyframe> frames) =>
        frames.Count == 0 ? 1u : Math.Max(1u, frames[^1].TimeMs);

    public static Sample Evaluate(IReadOnlyList<ElevatorKeyframe> frames,
        Vector3 spawnPosition, Quaternion spawnRotation, ulong progressMs)
    {
        if (frames.Count < 2 || frames[0].TimeMs != 0)
            return new Sample(spawnPosition, false);

        uint target = (uint)(progressMs % Period(frames));
        int hi = 1;
        while (hi < frames.Count && frames[hi].TimeMs <= target) hi++;
        ElevatorKeyframe previous = frames[hi - 1];
        ElevatorKeyframe next = frames[Math.Min(hi, frames.Count - 1)];

        Vector3 local;
        if (previous.LocalPosition == next.LocalPosition || next.TimeMs == previous.TimeMs)
            local = previous.LocalPosition;
        else
        {
            float fraction = (float)(target - previous.TimeMs) /
                (next.TimeMs - previous.TimeMs);
            local = Vector3.Lerp(previous.LocalPosition, next.LocalPosition, fraction);
        }
        return new Sample(spawnPosition + Vector3.Transform(local, spawnRotation),
            previous.LocalPosition != next.LocalPosition);
    }
}

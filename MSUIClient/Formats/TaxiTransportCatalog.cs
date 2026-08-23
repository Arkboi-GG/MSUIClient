using System.Numerics;

namespace MSUIClient.Formats;

public readonly record struct TaxiPathNode(
    uint Id, uint PathId, uint NodeIndex, uint MapId, Vector3 Position,
    uint Flags, uint DelaySeconds);

/// <summary>Build-5875 TaxiPathNode.dbc grouped and sorted by PathID/NodeIndex.</summary>
public sealed class TaxiPathNodeCatalog
{
    public const string MpqPath = @"DBFilesClient\TaxiPathNode.dbc";
    private readonly Dictionary<uint, TaxiPathNode[]> _paths = [];

    public int Count => _paths.Count;
    public bool TryGet(uint pathId, out TaxiPathNode[] nodes) =>
        _paths.TryGetValue(pathId, out nodes!);

    public static TaxiPathNodeCatalog? Load(MpqMount mpq)
    {
        DbcFile? dbc = mpq.ReadFile(MpqPath) is { } bytes ? DbcFile.Parse(bytes) : null;
        if (dbc is null || dbc.FieldCount < 9) return null;
        var grouped = new Dictionary<uint, List<TaxiPathNode>>();
        for (int row = 0; row < dbc.RecordCount; row++)
        {
            uint path = dbc.GetUInt(row, 1);
            if (!grouped.TryGetValue(path, out List<TaxiPathNode>? nodes))
                grouped[path] = nodes = [];
            nodes.Add(new TaxiPathNode(
                dbc.GetUInt(row, 0), path, dbc.GetUInt(row, 2), dbc.GetUInt(row, 3),
                new Vector3(dbc.GetFloat(row, 4), dbc.GetFloat(row, 5), dbc.GetFloat(row, 6)),
                dbc.GetUInt(row, 7), dbc.GetUInt(row, 8)));
        }
        var result = new TaxiPathNodeCatalog();
        foreach ((uint path, List<TaxiPathNode> nodes) in grouped)
            result._paths[path] = nodes.OrderBy(node => node.NodeIndex).ToArray();
        return result;
    }
}

public readonly record struct MoTransportSample(
    uint MapId, Vector3 Position, float Heading, bool Moving);

/// <summary>
/// One type-15 MO_TRANSPORT timetable. The keyframe windows and Catmull-Rom
/// evaluator follow vmangos/Benilla; the period is replaced by the exact
/// build-5875 client computation because the server clock is in that domain.
/// </summary>
public sealed class MoTransportTimetable
{
    private sealed class Frame
    {
        public uint MapId;
        public Vector3 Position;
        public float InitialOrientation;
        public float DistSinceStop;
        public float DistUntilStop;
        public float NextDistance;
        public float TimeFrom;
        public float TimeTo;
        public uint DepartureTime;
        public uint NextArriveTime;
        public int Leg;
        public int LocalIndex;
    }

    private readonly record struct RawFrame(
        uint MapId, Vector3 Position, bool IsStop, float DelaySeconds,
        float InitialOrientation, bool Teleport);

    private sealed class Leg
    {
        public required Vector3[] Controls;

        private Vector3 Control(int index)
        {
            if (index < 0)
            {
                Vector3 first = Controls[0];
                Vector3 second = Controls.Length > 1 ? Controls[1] : first;
                return first * 2f - second;
            }
            return index >= Controls.Length ? Controls[^1] : Controls[index];
        }

        public Vector3 Evaluate(int segment, float t) => CatmullRom(
            Control(segment - 1), Control(segment), Control(segment + 1),
            Control(segment + 2), t);

        public Vector3 Derivative(int segment, float t) => CatmullRomDerivative(
            Control(segment - 1), Control(segment), Control(segment + 1),
            Control(segment + 2), t);

        public double SegmentLength(int segment, int steps)
        {
            Vector3 current = Control(segment);
            double total = 0;
            for (int i = 1; i <= steps; i++)
            {
                Vector3 next = Evaluate(segment, (float)i / steps);
                double dx = current.X - next.X;
                double dy = current.Y - next.Y;
                double dz = current.Z - next.Z;
                total += Math.Sqrt(dx * dx + dy * dy + dz * dz);
                current = next;
            }
            return total;
        }
    }

    private readonly Frame[] _frames;
    private readonly Leg[] _legs;
    private readonly float _moveSpeed;
    private readonly float _accelRate;
    private readonly float _accelTime;
    private readonly float _accelDistance;

    public uint PeriodMs { get; private set; }

    private MoTransportTimetable(uint periodMs, Frame[] frames, Leg[] legs,
        float moveSpeed, float accelRate, float accelTime, float accelDistance)
    {
        PeriodMs = periodMs;
        _frames = frames;
        _legs = legs;
        _moveSpeed = moveSpeed;
        _accelRate = accelRate;
        _accelTime = accelTime;
        _accelDistance = accelDistance;
    }

    public static MoTransportTimetable? Build(IReadOnlyList<TaxiPathNode> nodes,
        float moveSpeed, float accelRate)
    {
        if (nodes.Count < 3 || !(moveSpeed > 0f) || !(accelRate > 0f)) return null;

        var raw = new List<RawFrame>();
        bool mapChange = false;
        for (int i = 1; i < nodes.Count - 1; i++)
        {
            if (mapChange)
            {
                mapChange = false;
                continue;
            }
            TaxiPathNode node = nodes[i];
            TaxiPathNode next = nodes[i + 1];
            if ((node.Flags & 1) != 0 || node.MapId != next.MapId)
            {
                if (raw.Count > 0) raw[^1] = raw[^1] with { Teleport = true };
                mapChange = true;
                continue;
            }
            Vector3 previousPosition = nodes[i - 1].Position;
            Vector3 nextPosition = nodes[i + 1].Position;
            float orientation = NormalizeOrientation(MathF.Atan2(
                nextPosition.Y - previousPosition.Y,
                nextPosition.X - previousPosition.X) + MathF.PI);
            raw.Add(new RawFrame(node.MapId, node.Position, (node.Flags & 2) != 0,
                node.DelaySeconds, orientation, false));
        }
        if (raw.Count == 0) return null;
        raw[^1] = raw[^1] with { Teleport = true };

        int count = raw.Count;
        int[] legOf = new int[count];
        int[] localIndex = new int[count];
        var legControls = new List<List<Vector3>> { new() };
        int legIndex = 0;
        for (int i = 0; i < count; i++)
        {
            if (i > 0 && raw[i - 1].Teleport)
            {
                legIndex++;
                legControls.Add([]);
            }
            legOf[i] = legIndex;
            localIndex[i] = legControls[legIndex].Count;
            legControls[legIndex].Add(raw[i].Position);
        }
        Leg[] legs = legControls.Select(controls => new Leg
            { Controls = controls.ToArray() }).ToArray();

        var distanceFromPrevious = new float[count];
        for (int i = 0; i < count; i++)
            if (localIndex[i] > 0)
                distanceFromPrevious[i] = (float)legs[legOf[i]].SegmentLength(
                    localIndex[i] - 1, 20);
        var nextDistance = new float[count];
        for (int i = 0; i < count; i++)
            nextDistance[i] = raw[i].Teleport || i == count - 1
                ? 0f : distanceFromPrevious[i + 1];

        int firstStop = 0, lastStop = 0;
        bool sawStop = false;
        for (int i = 0; i < count; i++)
            if (raw[i].IsStop)
            {
                if (!sawStop) firstStop = i;
                lastStop = i;
                sawStop = true;
            }

        var distanceSinceStop = new float[count];
        float accumulator = 0;
        for (int i = 0; i < count; i++)
        {
            int index = (i + lastStop) % count;
            accumulator = raw[index].IsStop || index == lastStop
                ? 0f : accumulator + distanceFromPrevious[index];
            distanceSinceStop[index] = accumulator;
        }

        var distanceUntilStop = new float[count];
        accumulator = 0;
        for (int i = count - 1; i >= 0; i--)
        {
            int index = (i + firstStop) % count;
            accumulator += distanceFromPrevious[(index + 1) % count];
            distanceUntilStop[index] = accumulator;
            if (raw[index].IsStop || index == firstStop) accumulator = 0;
        }

        float accelDistance = .5f * moveSpeed * moveSpeed / accelRate;
        float accelTime = moveSpeed / accelRate;
        var timeTo = new float[count];
        for (int i = 0; i < count; i++)
        {
            float since = distanceSinceStop[i];
            float until = distanceUntilStop[i];
            float total = since + until;
            if (total < 2f * accelDistance)
            {
                if (since < until)
                {
                    float segmentTime = 2f * MathF.Sqrt((until + since) / accelRate);
                    timeTo[i] = segmentTime - MathF.Sqrt(2f * since / accelRate);
                }
                else timeTo[i] = MathF.Sqrt(2f * until / accelRate);
            }
            else if (since < accelDistance)
            {
                float segmentTime = (until + since) / moveSpeed + moveSpeed / accelRate;
                timeTo[i] = segmentTime - MathF.Sqrt(2f * since / accelRate);
            }
            else if (until < accelDistance)
                timeTo[i] = MathF.Sqrt(2f * until / accelRate);
            else
                timeTo[i] = until / moveSpeed + .5f * moveSpeed / accelRate;
        }

        var timeFrom = new float[count];
        float currentSegmentTime = 0;
        for (int i = 0; i < count; i++)
        {
            int index = (i + lastStop) % count;
            if (raw[index].IsStop || index == lastStop)
                currentSegmentTime = timeTo[index];
            timeFrom[index] = currentSegmentTime - timeTo[index];
        }

        AccumulateVmangos(raw, timeTo, out uint[] departure,
            out uint[] nextArrival, out uint approximatePeriod);
        Frame[] frames = Enumerable.Range(0, count).Select(i => new Frame
        {
            MapId = raw[i].MapId,
            Position = raw[i].Position,
            InitialOrientation = raw[i].InitialOrientation,
            DistSinceStop = distanceSinceStop[i],
            DistUntilStop = distanceUntilStop[i],
            NextDistance = nextDistance[i],
            TimeFrom = timeFrom[i],
            TimeTo = timeTo[i],
            DepartureTime = departure[i],
            NextArriveTime = nextArrival[i],
            Leg = legOf[i],
            LocalIndex = localIndex[i],
        }).ToArray();
        var result = new MoTransportTimetable(approximatePeriod, frames, legs,
            moveSpeed, accelRate, accelTime, accelDistance);
        if (ClientPeriodMs(nodes, moveSpeed, accelRate) is uint exact)
            result.OverridePeriod(exact);
        return result;
    }

    public bool TouchesMap(uint mapId) => _frames.Any(frame => frame.MapId == mapId);

    public MoTransportSample Sample(ulong progressMs)
    {
        if (_frames.Length == 0 || PeriodMs == 0) return default;
        uint cycle = (uint)(progressMs % PeriodMs);
        cycle = Math.Min(cycle, PeriodMs - 1);
        int index = Array.FindIndex(_frames, frame => cycle < frame.NextArriveTime);
        if (index < 0) index = _frames.Length - 1;
        Frame current = _frames[index];
        if (cycle < current.DepartureTime || current.NextDistance <= 0f)
            return new MoTransportSample(current.MapId, current.Position,
                current.InitialOrientation, false);

        float now = cycle * .001f;
        float sinceDeparture = now - current.DepartureTime * .001f;
        float timeSinceStop = current.TimeFrom + sinceDeparture;
        float timeUntilStop = current.TimeTo - sinceDeparture;
        float distance;
        if (timeSinceStop < timeUntilStop)
        {
            float fromStop = timeSinceStop < _accelTime
                ? .5f * _accelRate * timeSinceStop * timeSinceStop
                : _accelDistance + (timeSinceStop - _accelTime) * _moveSpeed;
            distance = fromStop - current.DistSinceStop;
        }
        else
        {
            float toStop = timeUntilStop < _accelTime
                ? .5f * _accelRate * timeUntilStop * timeUntilStop
                : _accelDistance + (timeUntilStop - _accelTime) * _moveSpeed;
            distance = current.DistUntilStop - toStop;
        }
        float t = Math.Clamp(distance / current.NextDistance, 0f, 1f);
        Leg leg = _legs[current.Leg];
        Vector3 position = leg.Evaluate(current.LocalIndex, t);
        Vector3 direction = leg.Derivative(current.LocalIndex, t);
        float heading = NormalizeOrientation(MathF.Atan2(direction.Y, direction.X) + MathF.PI);
        return new MoTransportSample(current.MapId, position, heading, true);
    }

    private void OverridePeriod(uint exact)
    {
        if (exact == 0 || exact == PeriodMs) return;
        if (exact > PeriodMs && _frames.Length > 0)
        {
            _frames[^1].DepartureTime = exact;
            _frames[^1].NextArriveTime = exact;
        }
        PeriodMs = exact;
    }

    private static void AccumulateVmangos(IReadOnlyList<RawFrame> raw,
        IReadOnlyList<float> timeTo, out uint[] departure, out uint[] nextArrival,
        out uint period)
    {
        int count = raw.Count;
        var arrival = new uint[count];
        departure = new uint[count];
        nextArrival = new uint[count];
        float pathTime = 0;
        if (raw[0].IsStop)
        {
            pathTime = raw[0].DelaySeconds;
            departure[0] = (uint)(pathTime * 1000f);
        }
        for (int i = 1; i < count; i++)
        {
            pathTime += timeTo[i - 1];
            if (raw[i].IsStop)
            {
                arrival[i] = (uint)(pathTime * 1000f);
                nextArrival[i - 1] = arrival[i];
                pathTime += raw[i].DelaySeconds;
                departure[i] = (uint)(pathTime * 1000f);
            }
            else
            {
                pathTime -= timeTo[i];
                arrival[i] = (uint)(pathTime * 1000f);
                nextArrival[i - 1] = arrival[i];
                departure[i] = arrival[i];
            }
        }
        nextArrival[^1] = departure[^1];
        period = departure[^1];
    }

    public static uint? ClientPeriodMs(IReadOnlyList<TaxiPathNode> nodes,
        float speed, float acceleration)
    {
        if (nodes.Count == 0 || !(speed > 0f) || !(acceleration > 0f)) return null;
        long period = 0;
        var points = new List<Vector3>();
        var stops = new List<(int Index, int DelayMs)>();
        uint legMap = nodes[0].MapId;
        bool previousTeleport = false;
        foreach (TaxiPathNode node in nodes)
        {
            if (node.MapId != legMap || previousTeleport)
            {
                (int duration, int delays) = ClosePeriodLeg(points, stops, speed, acceleration);
                period += duration + delays;
                points.Clear();
                stops.Clear();
                legMap = node.MapId;
            }
            if ((node.Flags & 2) != 0 && points.Count > 0)
                stops.Add((points.Count, unchecked((int)node.DelaySeconds * 1000)));
            points.Add(node.Position);
            previousTeleport = (node.Flags & 1) != 0;
        }
        if (points.Count > 0)
        {
            (int duration, int delays) = ClosePeriodLeg(points, stops, speed, acceleration);
            period += duration + delays;
        }
        return period is > 0 and <= uint.MaxValue ? (uint)period : null;
    }

    private static (int Duration, int Delays) ClosePeriodLeg(
        IReadOnlyList<Vector3> points, IReadOnlyList<(int Index, int DelayMs)> stops,
        float speed, float acceleration)
    {
        float[] segmentLengths;
        float total;
        if (points.Count > 3)
        {
            segmentLengths = new float[points.Count - 3];
            double sum = 0;
            for (int i = 0; i < segmentLengths.Length; i++)
            {
                segmentLengths[i] = ClientSegmentArcLength(points, i);
                sum += segmentLengths[i];
            }
            total = (float)sum;
        }
        else
        {
            segmentLengths = [];
            total = 0;
        }

        int duration = 0, delays = 0, processed = 0;
        float startDistance = 0;
        foreach ((int pointIndex, int delayMs) in stops)
        {
            delays += delayMs;
            if (pointIndex + 1 >= points.Count) break;
            double endDistance = KnotSum(segmentLengths, pointIndex);
            duration += ArcTimeMs(endDistance, startDistance, speed, acceleration,
                processed == 0);
            startDistance = (float)endDistance;
            processed++;
        }
        duration += ArcTimeFinalMs(total, startDistance, speed, acceleration,
            processed == 0);
        return (duration, delays);
    }

    private static float ClientSegmentArcLength(IReadOnlyList<Vector3> points, int segment)
    {
        Vector3 previous = ClientCubicPoint(points, segment, 0f);
        float accumulator = 0;
        float t = BitConverter.UInt32BitsToSingle(0x3d4c_cccd);
        for (int i = 0; i < 20; i++)
        {
            Vector3 current = ClientCubicPoint(points, segment, t);
            double dx = current.X - (double)previous.X;
            double dy = current.Y - (double)previous.Y;
            double dz = current.Z - (double)previous.Z;
            double length = Math.Sqrt((dz * dz + dy * dy) + dx * dx);
            accumulator = (float)(length + accumulator);
            t = (float)((double)t + .05d);
            previous = current;
        }
        return accumulator;
    }

    private static Vector3 ClientCubicPoint(IReadOnlyList<Vector3> points, int segment, float t)
    {
        var output = Vector3.Zero;
        for (int i = 0; i < 4; i++)
        {
            Vector3 point = points[segment + i];
            double weight = ClientBasisWeight(i, t);
            output.X = (float)(weight * point.X + output.X);
            float yTerm = (float)(weight * point.Y);
            float zTerm = (float)(weight * point.Z);
            output.Y = (float)((double)yTerm + output.Y);
            output.Z = (float)((double)zTerm + output.Z);
        }
        return output;
    }

    private static double ClientBasisWeight(int row, float t)
    {
        ReadOnlySpan<float> coefficients = row switch
        {
            0 => [-.5f, 1f, -.5f, 0f],
            1 => [1.5f, -2.5f, 0f, 1f],
            2 => [-1.5f, 2f, .5f, 0f],
            _ => [.5f, -.5f, 0f, 0f],
        };
        double weight = coefficients[0];
        for (int i = 1; i < coefficients.Length; i++)
            weight = weight * t + coefficients[i];
        return weight;
    }

    private static double KnotSum(IReadOnlyList<float> lengths, int pointIndex)
    {
        double sum = 0;
        for (int i = 0; i < Math.Min(lengths.Count, Math.Max(0, pointIndex - 1)); i++)
            sum += lengths[i];
        return sum;
    }

    private static int ArcTimeMs(double endDistance, float startDistance,
        float speed, float acceleration, bool first)
    {
        double distance = endDistance - startDistance;
        float accelTime = (float)((double)speed / acceleration);
        double liveAccelDistance = .5d * speed * accelTime;
        float storedAccelDistance = (float)liveAccelDistance;
        double seconds;
        if (first)
            seconds = liveAccelDistance < distance
                ? (distance - storedAccelDistance) / speed + accelTime
                : Math.Sqrt(2d * distance / acceleration);
        else
            seconds = liveAccelDistance < .5d * distance
                ? (distance - 2d * storedAccelDistance) / speed + 2d * accelTime
                : 2d * Math.Sqrt(distance / acceleration);
        return RoundFtol(seconds);
    }

    private static int ArcTimeFinalMs(float endDistance, float startDistance,
        float speed, float acceleration, bool first)
    {
        double distance = (double)endDistance - startDistance;
        double seconds;
        if (first) seconds = distance / speed;
        else
        {
            float accelTime = (float)((double)speed / acceleration);
            double liveAccelDistance = .5d * speed * accelTime;
            seconds = liveAccelDistance < distance
                ? (distance - (float)liveAccelDistance) / speed + accelTime
                : Math.Sqrt(2d * distance / acceleration);
        }
        return RoundFtol(seconds);
    }

    private static int RoundFtol(double seconds)
    {
        double scaled = seconds * 1000d;
        return (int)Math.Truncate(scaled > 0 ? scaled + .5d : scaled - .5d);
    }

    private static float NormalizeOrientation(float orientation)
    {
        float wrapped = orientation % (MathF.PI * 2f);
        return wrapped < 0 ? wrapped + MathF.PI * 2f : wrapped;
    }

    private static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2,
        Vector3 p3, float t)
    {
        float t2 = t * t, t3 = t2 * t;
        return p0 * (-.5f * t3 + t2 - .5f * t) +
               p1 * (1.5f * t3 - 2.5f * t2 + 1f) +
               p2 * (-1.5f * t3 + 2f * t2 + .5f * t) +
               p3 * (.5f * t3 - .5f * t2);
    }

    private static Vector3 CatmullRomDerivative(Vector3 p0, Vector3 p1,
        Vector3 p2, Vector3 p3, float t)
    {
        float t2 = t * t;
        return p0 * (-1.5f * t2 + 2f * t - .5f) +
               p1 * (4.5f * t2 - 5f * t) +
               p2 * (-4.5f * t2 + 4f * t + .5f) +
               p3 * (1.5f * t2 - t);
    }
}

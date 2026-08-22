using System.Numerics;

namespace MSUIClient.Formats;

public readonly record struct TaxiPathInfo(uint Id, uint From, uint To, uint Cost);

/// <summary>Build-5875 TaxiPath.dbc directed edge and fare catalog.</summary>
public sealed class TaxiPathCatalog
{
    public const string Path = @"DBFilesClient\TaxiPath.dbc";
    private readonly Dictionary<(uint From, uint To), TaxiPathInfo> _byPair = [];
    private readonly Dictionary<uint, List<TaxiPathInfo>> _from = [];
    public int Count => _byPair.Count;

    public TaxiPathCatalog(IEnumerable<TaxiPathInfo> paths)
    {
        foreach (TaxiPathInfo path in paths)
        {
            if (path.Id == 0 || path.From == 0 || path.To == 0) continue;
            _byPair[(path.From, path.To)] = path;
            if (!_from.TryGetValue(path.From, out List<TaxiPathInfo>? outgoing))
                _from[path.From] = outgoing = [];
            outgoing.Add(path);
        }
    }

    public bool TryBetween(uint from, uint to, out TaxiPathInfo path) =>
        _byPair.TryGetValue((from, to), out path);

    public IReadOnlyList<TaxiPathInfo> From(uint node) =>
        _from.TryGetValue(node, out List<TaxiPathInfo>? paths) ? paths : [];

    public static TaxiPathCatalog? Load(MpqMount mpq)
    {
        byte[]? bytes = mpq.ReadFile(Path);
        DbcFile? dbc = bytes is null ? null : DbcFile.Parse(bytes);
        if (dbc is null || dbc.FieldCount < 4 || dbc.RecordSize < 16) return null;
        var rows = new List<TaxiPathInfo>(dbc.RecordCount);
        for (int row = 0; row < dbc.RecordCount; row++)
            rows.Add(new(dbc.GetUInt(row, 0), dbc.GetUInt(row, 1),
                dbc.GetUInt(row, 2), dbc.GetUInt(row, 3)));
        return new(rows);
    }
}

public readonly record struct TaxiContinentInfo(
    uint MapId, Vector2 TaxiMinimum, Vector2 TaxiMaximum)
{
    /// <summary>Byte-verified 1.12 taxi projection, including its cross-axis denominators.</summary>
    public Vector2 Project(Vector3 world)
    {
        float xSpan = TaxiMaximum.X - TaxiMinimum.X;
        float ySpan = TaxiMaximum.Y - TaxiMinimum.Y;
        if (MathF.Abs(xSpan) < float.Epsilon || MathF.Abs(ySpan) < float.Epsilon)
            return Vector2.Zero;
        return new((TaxiMaximum.Y - world.Y) / xSpan,
            (world.X - TaxiMinimum.X) / ySpan);
    }
}

/// <summary>WorldMapContinent.dbc taxi-map projection rows, keyed by MapID.</summary>
public sealed class TaxiContinentCatalog
{
    public const string Path = @"DBFilesClient\WorldMapContinent.dbc";
    private readonly Dictionary<uint, TaxiContinentInfo> _byMap = [];
    public bool TryGet(uint mapId, out TaxiContinentInfo continent) =>
        _byMap.TryGetValue(mapId, out continent);

    public static TaxiContinentCatalog? Load(MpqMount mpq)
    {
        byte[]? bytes = mpq.ReadFile(Path);
        DbcFile? dbc = bytes is null ? null : DbcFile.Parse(bytes);
        if (dbc is null || dbc.FieldCount < 13 || dbc.RecordSize < 52) return null;
        var result = new TaxiContinentCatalog();
        for (int row = 0; row < dbc.RecordCount; row++)
        {
            uint mapId = dbc.GetUInt(row, 1);
            result._byMap[mapId] = new(mapId,
                new Vector2(dbc.GetFloat(row, 9), dbc.GetFloat(row, 10)),
                new Vector2(dbc.GetFloat(row, 11), dbc.GetFloat(row, 12)));
        }
        return result;
    }
}

public readonly record struct TaxiResolvedRoute(uint[] Chain, uint Fare);

public readonly record struct TaxiRouteView(
    TaxiNodeInfo Node, Vector2 Position, bool Current, uint Fare,
    uint[] Chain, Vector4[] Segments);

/// <summary>Current 1.12 geographic-distance route search and visible-node builder.</summary>
public static class TaxiRoutePlanner
{
    public static TaxiResolvedRoute? ShortestRoute(
        IReadOnlySet<uint> known,
        Func<uint, IReadOnlyList<TaxiPathInfo>> edges,
        Func<uint, uint, float> distance,
        uint from,
        uint to)
    {
        if (from == to) return new([from], 0);
        var best = new Dictionary<uint, (float Distance, uint Hops)> { [from] = (0, 0) };
        var fares = new Dictionary<uint, uint> { [from] = 0 };
        var previous = new Dictionary<uint, uint>();
        var open = new HashSet<uint> { from };
        var visited = new HashSet<uint>();

        while (open.Count > 0)
        {
            uint node = open.MinBy(candidate =>
                (best[candidate].Distance, best[candidate].Hops, candidate));
            open.Remove(node);
            if (!visited.Add(node)) continue;
            if (node == to) break;
            (float currentDistance, uint currentHops) = best[node];
            foreach (TaxiPathInfo edge in edges(node))
            {
                uint next = edge.To;
                if (!known.Contains(next) || visited.Contains(next)) continue;
                float leg = Math.Max(0, distance(node, next));
                var candidate = (Distance: currentDistance + leg, Hops: currentHops + 1);
                if (best.TryGetValue(next, out var existing) &&
                    (candidate.Distance > existing.Distance ||
                     candidate.Distance == existing.Distance && candidate.Hops >= existing.Hops))
                    continue;
                best[next] = candidate;
                fares[next] = fares[node] + edge.Cost;
                previous[next] = node;
                open.Add(next);
            }
        }

        if (!best.ContainsKey(to) || !fares.TryGetValue(to, out uint fare)) return null;
        var chain = new List<uint> { to };
        uint cursor = to;
        while (cursor != from)
        {
            if (!previous.TryGetValue(cursor, out cursor)) return null;
            chain.Add(cursor);
        }
        chain.Reverse();
        return new(chain.ToArray(), fare);
    }

    public static TaxiRouteView[] BuildVisible(
        TaxiNodeCatalog nodes,
        TaxiPathCatalog paths,
        TaxiContinentInfo continent,
        IReadOnlySet<uint> known,
        uint currentNode)
    {
        if (!nodes.TryGet(currentNode, out TaxiNodeInfo current)) return [];
        float Distance(uint a, uint b)
        {
            if (!nodes.TryGet(a, out TaxiNodeInfo left) ||
                !nodes.TryGet(b, out TaxiNodeInfo right)) return float.MaxValue / 4;
            return Vector3.Distance(left.Position, right.Position);
        }

        var result = new List<TaxiRouteView>();
        foreach (TaxiNodeInfo node in nodes.Nodes
                     .Where(node => node.MapId == current.MapId && known.Contains(node.Id))
                     .OrderBy(node => node.Id))
        {
            Vector2 position = continent.Project(node.Position);
            if (node.Id == currentNode)
            {
                result.Add(new(node, position, true, 0, [node.Id], []));
                continue;
            }
            TaxiResolvedRoute? resolved = ShortestRoute(known, paths.From, Distance,
                currentNode, node.Id);
            if (resolved is not { } route) continue;
            Vector4[] segments = route.Chain.Zip(route.Chain.Skip(1), (from, to) =>
            {
                if (!nodes.TryGet(from, out TaxiNodeInfo a) ||
                    !nodes.TryGet(to, out TaxiNodeInfo b)) return Vector4.Zero;
                Vector2 source = continent.Project(a.Position);
                Vector2 destination = continent.Project(b.Position);
                return new Vector4(source.X, source.Y, destination.X, destination.Y);
            }).Where(segment => segment != Vector4.Zero).ToArray();
            result.Add(new(node, position, false, route.Fare, route.Chain, segments));
        }
        return result.ToArray();
    }
}

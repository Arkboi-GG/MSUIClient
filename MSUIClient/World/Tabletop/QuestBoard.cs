using System.Numerics;
using MSUIClient.Net;
using Q = MSUIClient.Net.QuestAtlasClient;

namespace MSUIClient.World.Tabletop;

/// <summary>
/// The zone bundle as a small graph (shared_docs/QUEST_TABLETOP.md §5) - a port of the web Quest Atlas model.js: quests,
/// the people who give and take them, where the work is, and the chains. Pure data: knows the board only through the
/// <c>onBoard</c> predicate it is built with. No GL, no game state.
/// </summary>
public sealed class QuestBoard
{
    public const int Alliance = 1 | 4 | 8 | 64;     // human, dwarf, night elf, gnome
    public const int Horde = 2 | 16 | 32 | 128;     // orc, undead, tauren, troll
    public const int MaxPiecesPerEntity = 4;        // a giver standing in 40 places is a pattern, not 40 pins
    public const int MaxSourcesDrawn = 6;

    public Q.ZoneBundle Bundle { get; }
    public IReadOnlyDictionary<uint, Q.Quest> Quests { get; }
    public IReadOnlyDictionary<uint, Q.Stub> Stubs { get; }
    /// <summary>"npc:123" → quest ids it gives / takes.</summary>
    public Dictionary<string, List<uint>> Gives { get; } = new();
    public Dictionary<string, List<uint>> Ends { get; } = new();
    public List<Edge> Edges { get; } = new();
    public List<Chain> Chains { get; } = new();
    public Dictionary<uint, Chain> ChainOf { get; } = new();
    public Dictionary<uint, int> Depth { get; } = new();
    public (int Lo, int Hi) LevelRange { get; }

    private readonly Func<int, float, float, bool> _onBoard;
    private readonly Dictionary<string, List<Vector3>> _spawnCache = new();
    private readonly Dictionary<uint, string> _search = new();

    public readonly record struct Edge(uint From, uint To, bool Breadcrumb);
    public sealed record Chain(List<uint> Ids, Vector3 Colour, bool Multi);
    public sealed record Target(string Kind, string Label, List<Vector3> Points, Q.Trigger? Trigger);

    public static string Key(string kind, uint entry) => $"{kind}:{entry}";
    public static string Key(Q.Ref r) => Key(r.Kind, r.Entry);

    public QuestBoard(Q.ZoneBundle bundle, Func<int, float, float, bool> onBoard)
    {
        Bundle = bundle;
        _onBoard = onBoard;
        Quests = bundle.Quests.GroupBy(q => q.Id).ToDictionary(g => g.Key, g => g.First());
        Stubs = bundle.Stubs.GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First());
        foreach (var q in Quests.Values)
        {
            foreach (var g in q.Givers) Push(Gives, Key(g), q.Id);
            foreach (var e in q.Enders) Push(Ends, Key(e), q.Id);
        }
        BuildChains();
        // QuestLevel <= 0 means "scales with the player" (holiday/class quests): no level of its own.
        var levels = Quests.Values.Select(q => q.Level).Where(l => l > 0).ToList();
        LevelRange = levels.Count > 0 ? (levels.Min(), levels.Max()) : (1, 60);
    }

    // ── entities ────────────────────────────────────────────────────────────

    public string EntityName(string kind, uint entry)
        => kind == "npc" ? Bundle.Npcs.GetValueOrDefault(entry.ToString())?.Name ?? $"npc #{entry}"
                         : Bundle.Objects.GetValueOrDefault(entry.ToString())?.Name ?? $"object #{entry}";

    public Q.Npc? Npc(uint entry) => Bundle.Npcs.GetValueOrDefault(entry.ToString());
    public Q.Item? Item(uint id) => Bundle.Items.GetValueOrDefault(id.ToString());
    public Q.Trigger? Trigger(uint id) => Bundle.Triggers.GetValueOrDefault(id.ToString());

    /// <summary>On-board spawns of an entity (world yards).</summary>
    public List<Vector3> SpawnsOnBoard(string kind, uint entry)
    {
        string k = Key(kind, entry);
        if (_spawnCache.TryGetValue(k, out var cached)) return cached;
        var spawns = kind == "npc" ? Bundle.Npcs.GetValueOrDefault(entry.ToString())?.Spawns
                                   : Bundle.Objects.GetValueOrDefault(entry.ToString())?.Spawns;
        var pts = (spawns ?? new()).Where(s => s.Length >= 4 && _onBoard((int)s[0], s[1], s[2]))
            .Select(s => new Vector3(s[1], s[2], s[3])).ToList();
        _spawnCache[k] = pts;
        return pts;
    }

    public Vector3? PlaceOf(Q.Ref r) => SpawnsOnBoard(r.Kind, r.Entry) is { Count: > 0 } p ? p[0] : null;
    public Vector3? GiverPlace(Q.Quest q) => q.Givers.Select(PlaceOf).FirstOrDefault(p => p is not null);
    public Vector3? EnderPlace(Q.Quest q) => q.Enders.Select(PlaceOf).FirstOrDefault(p => p is not null);
    /// <summary>Pieces to stand on the table for one entity (a few spawns at most).</summary>
    public List<Vector3> PiecePlaces(string kind, uint entry) => SpawnsOnBoard(kind, entry).Take(MaxPiecesPerEntity).ToList();

    /// <summary>What the quest sends you to do, with where.</summary>
    public List<Target> ObjectiveTargets(Q.Quest q)
    {
        var outList = new List<Target>();
        foreach (var o in q.Objectives)
        {
            switch (o.Kind)
            {
                case "kill":
                    outList.Add(new("kill", $"{Verb(o)} {o.Count}× {EntityName("npc", o.Id)}", SpawnsOnBoard("npc", o.Id), null));
                    break;
                case "object":
                    outList.Add(new("object", $"{Verb(o)} {EntityName("object", o.Id)}", SpawnsOnBoard("object", o.Id), null));
                    break;
                case "item":
                {
                    var item = Item(o.Id);
                    var pts = new List<Vector3>();
                    foreach (var s in (item?.Sources ?? new()).Take(MaxSourcesDrawn)) pts.AddRange(SpawnsOnBoard(s.Kind, s.Entry));
                    outList.Add(new("collect", $"Collect {o.Count}× {item?.Name ?? "item #" + o.Id}", pts, null));
                    break;
                }
                case "explore":
                {
                    var t = Trigger(o.Id);
                    string name = string.IsNullOrEmpty(t?.Name) ? $"area trigger {o.Id}" : t!.Name!;
                    outList.Add(t is not null && _onBoard(t.Map, t.X, t.Y)
                        ? new("explore", $"Explore {name}", new() { new(t.X, t.Y, t.Z) }, t)
                        : new("explore", $"Explore {name}", new(), null));
                    break;
                }
            }
        }
        return outList;
    }

    private static string Verb(Q.Objective o) => o.Spell != 0 ? "Use spell on" : o.Kind == "object" ? "Use" : "Slay";

    // ── classification ──────────────────────────────────────────────────────

    /// <summary>"alliance" | "horde" | "both", from RequiredRaces (0 = both).</summary>
    public static string Faction(Q.Quest q)
    {
        int r = q.Races;
        if (r == 0) return "both";
        bool a = (r & Alliance) != 0, h = (r & Horde) != 0;
        return a && !h ? "alliance" : h && !a ? "horde" : "both";
    }

    public static bool IsClassOrRace(Q.Quest q)
        => q.Classes != 0 || (q.Races != 0 && q.Races != Alliance && q.Races != Horde && q.Races != (Alliance | Horde));

    /// <summary>Lower-case text searched by the filter: title, id, NPC, target and item names.</summary>
    public string SearchText(Q.Quest q)
    {
        if (_search.TryGetValue(q.Id, out var s)) return s;
        var names = q.Givers.Concat(q.Enders).Select(r => EntityName(r.Kind, r.Entry));
        var objs = q.Objectives.Select(o => o.Kind switch
        {
            "item" => Item(o.Id)?.Name ?? "",
            "kill" => EntityName("npc", o.Id),
            "object" => EntityName("object", o.Id),
            _ => "",
        });
        s = string.Join(' ', new[] { q.Title, q.Id.ToString() }.Concat(names).Concat(objs)).ToLowerInvariant();
        _search[q.Id] = s;
        return s;
    }

    /// <summary>Quests on this board that send you after a creature: to kill it, or for something it drops.</summary>
    public List<(Q.Quest Quest, string Why)> QuestsWanting(uint entry)
    {
        var list = new List<(Q.Quest, string)>();
        foreach (var q in Quests.Values)
            foreach (var o in q.Objectives)
            {
                if (o.Kind == "kill" && o.Id == entry) { list.Add((q, $"slay {o.Count}")); break; }
                if (o.Kind == "item" && Item(o.Id) is { } it && it.Sources.Any(s => s.Kind == "npc" && s.Entry == entry))
                {
                    list.Add((q, $"drops {it.Name}"));
                    break;
                }
            }
        return list.OrderBy(x => x.Item1.Level).ToList();
    }

    // ── chains ──────────────────────────────────────────────────────────────

    /// <summary>Directed links of a quest (an end may be off the board - a stub).</summary>
    public static IEnumerable<Edge> Links(Q.Quest q)
    {
        static Edge? Add(int from, int to, bool crumb) => from != 0 && to != 0 && from != to ? new Edge((uint)from, (uint)to, crumb) : null;
        var list = new List<Edge?>
        {
            q.Prev != 0 ? Add(Math.Abs(q.Prev), (int)q.Id, false) : null,
            q.NextInChain != 0 ? Add((int)q.Id, q.NextInChain, false) : null,
            q.Next > 0 ? Add((int)q.Id, q.Next, false) : null,
            q.BreadcrumbFor != 0 ? Add((int)q.Id, q.BreadcrumbFor, true) : null,
        };
        return list.Where(e => e is not null).Select(e => e!.Value);
    }

    private void BuildChains()
    {
        // Undirected components (union-find) over chain links among the quests on this board.
        var parent = Quests.Keys.ToDictionary(id => id, id => id);
        uint Find(uint x)
        {
            while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
            return x;
        }
        var seen = new HashSet<(uint, uint, bool)>();
        foreach (var q in Quests.Values)
            foreach (var l in Links(q))
            {
                if (seen.Add((l.From, l.To, l.Breadcrumb))) Edges.Add(l);
                if (Quests.ContainsKey(l.From) && Quests.ContainsKey(l.To)) parent[Find(l.From)] = Find(l.To);
            }

        var groups = new Dictionary<uint, List<uint>>();
        foreach (var id in Quests.Keys) Push(groups, Find(id), id);

        var preds = new Dictionary<uint, List<uint>>();
        foreach (var e in Edges) if (Quests.ContainsKey(e.To)) Push(preds, e.To, e.From);
        int DepthOf(uint id, HashSet<uint> visiting)
        {
            if (Depth.TryGetValue(id, out int d)) return d;
            if (!visiting.Add(id)) return 0;
            var p = preds.GetValueOrDefault(id)?.Where(Quests.ContainsKey).ToList() ?? new();
            d = p.Count > 0 ? 1 + p.Max(x => DepthOf(x, visiting)) : 0;
            Depth[id] = d;
            return d;
        }

        int hueIndex = 0;
        foreach (var ids in groups.Values)
        {
            // Inside a chain: depth along the links, then level, then id.
            ids.Sort((a, b) =>
            {
                int c = DepthOf(a, new()).CompareTo(DepthOf(b, new()));
                if (c == 0) c = Quests[a].Level.CompareTo(Quests[b].Level);
                return c != 0 ? c : a.CompareTo(b);
            });
            var colour = ids.Count > 1 ? Hsl(hueIndex++ * 137.508f % 360f, 0.62f, 0.60f) : Hex(0xd9c7a0);
            var chain = new Chain(ids, colour, ids.Count > 1);
            Chains.Add(chain);
            foreach (var id in ids) ChainOf[id] = chain;
        }
        Chains.Sort((a, b) =>
        {
            int c = Quests[a.Ids[0]].Level.CompareTo(Quests[b.Ids[0]].Level);
            return c != 0 ? c : a.Ids[0].CompareTo(b.Ids[0]);
        });
        foreach (var id in Quests.Keys) DepthOf(id, new());
    }

    // ── geometry helpers ────────────────────────────────────────────────────

    /// <summary>Objective centroid: the mean of the densest third of the points (one stray spawn must not drag the stop away).</summary>
    public static Vector3? Centroid(IReadOnlyList<Vector3> pts)
    {
        if (pts.Count == 0) return null;
        float mx = pts.Average(p => p.X), my = pts.Average(p => p.Y);
        var near = pts.OrderBy(p => (p.X - mx) * (p.X - mx) + (p.Y - my) * (p.Y - my))
            .Take(Math.Max(1, (int)MathF.Ceiling(pts.Count / 3f))).ToList();
        return new Vector3(near.Average(p => p.X), near.Average(p => p.Y), near.Average(p => p.Z));
    }

    /// <summary>Quadratic Bézier point (the chain/journey arcs).</summary>
    public static Vector3 Bezier(Vector3 a, Vector3 mid, Vector3 b, float t)
    {
        float u = 1f - t;
        return u * u * a + 2f * u * t * mid + t * t * b;
    }

    public static Vector3 Hex(uint hex) => new(((hex >> 16) & 0xff) / 255f, ((hex >> 8) & 0xff) / 255f, (hex & 0xff) / 255f);

    /// <summary>HSL → sRGB 0..1 (the web's chain palette: golden-angle hues, s 62 %, l 60 %).</summary>
    public static Vector3 Hsl(float h, float s, float l)
    {
        float K(float n) => (n + h / 30f) % 12f;
        float a = s * MathF.Min(l, 1f - l);
        float F(float n) => l - a * MathF.Max(-1f, MathF.Min(K(n) - 3f, MathF.Min(9f - K(n), 1f)));
        return new Vector3(F(0), F(8), F(4));
    }

    private static void Push<TK>(Dictionary<TK, List<uint>> map, TK k, uint v) where TK : notnull
    {
        if (map.TryGetValue(k, out var list)) { if (!list.Contains(v)) list.Add(v); }
        else map[k] = new List<uint> { v };
    }
}

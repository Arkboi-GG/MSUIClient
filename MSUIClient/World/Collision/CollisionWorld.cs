using System.Numerics;

namespace MSUIClient.World.Collision;

/// <summary>
/// One hit from <see cref="CollisionWorld.Raycast"/>.
///
/// Normal always faces AGAINST the ray direction, regardless of how the source
/// triangle was wound. vmap meshes are not consistently wound — some WMO groups
/// are inside-out relative to others — so relying on winding here would make
/// floors intermittently unstandable. Facing the normal at the ray is the only
/// interpretation that is stable across the whole dataset.
/// </summary>
public readonly record struct RayHit(float Distance, Vector3 Point, Vector3 Normal, int Triangle);

/// <summary>
/// Static collision geometry in WoW world space (+X north, +Y west, +Z up),
/// stored as a triangle soup with a BVH over it.
///
/// WHAT GOES IN HERE
///   Only vmap geometry — buildings, trees, fences, bridges. Terrain does NOT
///   go in here: <see cref="TerrainRenderer.SampleHeight"/> is an O(1) bilinear
///   sample of the same height grid the server agreed with to 0.00, which beats
///   raycasting 65,000 triangles per tile in both speed and accuracy. The
///   character controller uses the grid for ground and this for everything else.
///
/// WHY WORLD-SPACE BAKING
///   Each spawn's triangles are transformed once at load and stored flat, rather
///   than keeping per-model meshes and transforming rays into model space. A
///   two-level BVH would use less memory (a tree model appearing 300 times is
///   stored 300 times here), but flat is roughly 50 MB for a 3x3 block of Elwynn
///   and the traversal has no per-instance transform in the inner loop. Revisit
///   if tile streaming makes the rebuild cost hurt.
///
/// Add geometry with <see cref="AddTriangle"/>, then call <see cref="Build"/>
/// once. Raycasting before Build simply finds nothing.
/// </summary>
public sealed class CollisionWorld
{
    private struct Tri
    {
        public Vector3 A, B, C;
    }

    /// <summary>
    /// A BVH node. Count > 0 means leaf; Count == 0 means interior, where the
    /// left child is always the next node in the array and Right is explicit.
    /// </summary>
    private struct Node
    {
        public Vector3 Min, Max;
        public int Start;
        public int Count;
        public int Right;
    }

    private const int LeafSize = 8;
    private const int MaxDepth = 48;

    /// <summary>
    /// Node bounds are inflated by this much. Beyond the usual float slop, it
    /// keeps a ray that lies exactly in a slab plane from producing 0 * infinity
    /// in the inverse-direction test, which is NaN and would silently drop the
    /// hit rather than reporting one.
    /// </summary>
    private const float BoundsEpsilon = 1e-3f;

    private readonly List<Tri> _pending = [];
    private readonly List<int> _pendingSource = [];

    /// <summary>
    /// Which source each triangle came from, parallel to _tris. Without this,
    /// "something is blocking me" can never become "WHAT is blocking me" — and
    /// that distinction is the difference between a five minute fix and an
    /// afternoon of guessing which building's mesh is in the wrong place.
    /// </summary>
    private int[] _source = [];
    private readonly List<string> _sourceNames = [];

    private Tri[] _tris = [];
    private int[] _index = [];
    private Node[] _nodes = [];

    public int TriangleCount => _tris.Length;
    public int NodeCount => _nodes.Length;
    public bool IsEmpty => _tris.Length == 0;

    public Vector3 BoundsMin { get; private set; }
    public Vector3 BoundsMax { get; private set; }

    /// <summary>
    /// World-space shift applied to the whole collision world at QUERY time.
    ///
    /// Applied to queries rather than to the baked triangles so it can be tuned
    /// live without a 1.2M-triangle rebuild: rays are moved into the mesh's
    /// frame on the way in and the hit point is moved back on the way out. Once
    /// the right value is known it belongs in the loader, baked into the
    /// vertices, and this goes back to zero.
    /// </summary>
    public Vector3 Offset { get; set; }

    /// <summary>Triangles rejected at add time for being degenerate.</summary>
    public int DegenerateSkipped { get; private set; }

    /// <summary>
    /// Queue one world-space triangle. Degenerate triangles are dropped here
    /// rather than at intersection time — a zero-area triangle produces a NaN
    /// normal, and one NaN in the ground normal makes the character fall
    /// through the floor for reasons that look nothing like the cause.
    /// </summary>
    public void AddTriangle(Vector3 a, Vector3 b, Vector3 c, int sourceId = -1)
    {
        var n = Vector3.Cross(b - a, c - a);
        if (n.LengthSquared() < 1e-12f) { DegenerateSkipped++; return; }

        _pending.Add(new Tri { A = a, B = b, C = c });
        _pendingSource.Add(sourceId);
    }

    /// <summary>
    /// Register a source and get its id. Self-registering so several producers
    /// can feed one world — WMO geometry from the renderer, doodads later, and
    /// the vmap loader when it is used as a cross-check.
    /// </summary>
    public int RegisterSource(string name)
    {
        _sourceNames.Add(name);
        return _sourceNames.Count - 1;
    }

    /// <summary>
    /// The three world-space corners of a triangle by index, Offset applied —
    /// the same numbers the raycast intersected. Drawing this is the only way
    /// to see WHERE the physics thinks a surface is, as opposed to where the
    /// bulk wireframe suggests it is.
    /// </summary>
    public bool TryGetTriangle(int index, out Vector3 a, out Vector3 b, out Vector3 c)
    {
        a = b = c = Vector3.Zero;
        if (index < 0 || index >= _tris.Length) return false;

        var t = _tris[index];
        a = t.A + Offset;
        b = t.B + Offset;
        c = t.C + Offset;
        return true;
    }

    /// <summary>
    /// Append built collision triangles that overlap a world-space box and are
    /// floor-like enough to receive a projected ground decal. The BVH keeps this
    /// proportional to nearby geometry; walls are rejected by their absolute
    /// normal Z because source winding is not consistent across WMO groups.
    /// </summary>
    public void GatherWalkableTriangles(float minX, float minY, float minZ,
        float maxX, float maxY, float maxZ,
        List<(Vector3 A, Vector3 B, Vector3 C)> output)
    {
        if (_nodes.Length == 0) return;

        Vector3 queryMin = Vector3.Min(new(minX, minY, minZ), new(maxX, maxY, maxZ)) - Offset;
        Vector3 queryMax = Vector3.Max(new(minX, minY, minZ), new(maxX, maxY, maxZ)) - Offset;
        Span<int> stack = stackalloc int[MaxDepth * 2 + 8];
        int sp = 0;
        stack[sp++] = 0;

        while (sp > 0)
        {
            int ni = stack[--sp];
            Node node = _nodes[ni];
            if (!BoundsOverlap(node.Min, node.Max, queryMin, queryMax)) continue;

            if (node.Count == 0)
            {
                if (sp + 2 <= stack.Length)
                {
                    stack[sp++] = node.Right;
                    stack[sp++] = ni + 1;
                }
                continue;
            }

            for (int i = node.Start; i < node.Start + node.Count; i++)
            {
                Tri tri = _tris[_index[i]];
                Vector3 triMin = Vector3.Min(tri.A, Vector3.Min(tri.B, tri.C));
                Vector3 triMax = Vector3.Max(tri.A, Vector3.Max(tri.B, tri.C));
                if (!BoundsOverlap(triMin, triMax, queryMin, queryMax)) continue;

                Vector3 normal = Vector3.Cross(tri.B - tri.A, tri.C - tri.A);
                float length = normal.Length();
                if (length < 1e-6f || MathF.Abs(normal.Z) / length < 0.5f) continue;
                output.Add((tri.A + Offset, tri.B + Offset, tri.C + Offset));
            }
        }
    }

    /// <summary>
    /// Rare recovery path: find the closest point on any floor-like collision
    /// triangle. This is intentionally a full scan; it runs only when the server
    /// supplies a position outside the active map and prevents an unrecoverable
    /// loading-screen/fall state when no authored arrival point is available.
    /// </summary>
    public bool TryFindNearestWalkablePoint(Vector3 worldPoint, out Vector3 result)
    {
        result = default;
        if (_tris.Length == 0) return false;

        Vector3 query = worldPoint - Offset;
        float bestSq = float.MaxValue;
        bool found = false;
        foreach (Tri tri in _tris)
        {
            Vector3 normal = Vector3.Cross(tri.B - tri.A, tri.C - tri.A);
            float length = normal.Length();
            if (length < 1e-6f || MathF.Abs(normal.Z) / length < 0.5f) continue;

            Vector3 candidate = ClosestPointOnTriangle(query, tri.A, tri.B, tri.C);
            float distanceSq = Vector3.DistanceSquared(query, candidate);
            if (distanceSq >= bestSq) continue;
            bestSq = distanceSq;
            result = candidate + Offset;
            found = true;
        }
        return found;
    }

    // Real-Time Collision Detection, Christer Ericson, closest-point region tests.
    private static Vector3 ClosestPointOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 ab = b - a, ac = c - a, ap = p - a;
        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0f && d2 <= 0f) return a;

        Vector3 bp = p - b;
        float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0f && d4 <= d3) return b;

        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0f && d1 >= 0f && d3 <= 0f)
            return a + (d1 / (d1 - d3)) * ab;

        Vector3 cp = p - c;
        float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0f && d5 <= d6) return c;

        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0f && d2 >= 0f && d6 <= 0f)
            return a + (d2 / (d2 - d6)) * ac;

        float va = d3 * d6 - d5 * d4;
        if (va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f)
            return b + ((d4 - d3) / ((d4 - d3) + (d5 - d6))) * (c - b);

        float denom = 1f / (va + vb + vc);
        return a + ab * (vb * denom) + ac * (vc * denom);
    }

    private static bool BoundsOverlap(in Vector3 aMin, in Vector3 aMax,
        in Vector3 bMin, in Vector3 bMax) =>
        aMax.X >= bMin.X && aMin.X <= bMax.X &&
        aMax.Y >= bMin.Y && aMin.Y <= bMax.Y &&
        aMax.Z >= bMin.Z && aMin.Z <= bMax.Z;

    /// <summary>Source id of a hit triangle, or -1. Used to isolate one model.</summary>
    public int SourceIdOf(int triangle)
        => triangle >= 0 && triangle < _source.Length ? _source[triangle] : -1;

    /// <summary>Name of the model a hit triangle came from, or "?" if unknown.</summary>
    public string SourceOf(int triangle)
    {
        if (triangle < 0 || triangle >= _source.Length) return "?";
        int id = _source[triangle];
        return id >= 0 && id < _sourceNames.Count ? _sourceNames[id] : "?";
    }

    /// <summary>
    /// Build the BVH over everything added so far. Cheap enough to call again
    /// after adding more geometry; it rebuilds from scratch.
    /// </summary>
    public void Build()
    {
        if (_pending.Count > 0)
        {
            if (_tris.Length == 0)
            {
                _tris = _pending.ToArray();
                _source = _pendingSource.ToArray();
            }
            else
            {
                var merged = new Tri[_tris.Length + _pending.Count];
                _tris.CopyTo(merged, 0);
                _pending.CopyTo(merged, _tris.Length);
                _tris = merged;

                var mergedSource = new int[_source.Length + _pendingSource.Count];
                _source.CopyTo(mergedSource, 0);
                _pendingSource.CopyTo(mergedSource, _source.Length);
                _source = mergedSource;
            }

            _pending.Clear();
            _pending.TrimExcess();
            _pendingSource.Clear();
            _pendingSource.TrimExcess();
        }

        if (_tris.Length == 0)
        {
            _index = [];
            _nodes = [];
            _source = [];
            BoundsMin = BoundsMax = Vector3.Zero;
            return;
        }

        _index = new int[_tris.Length];
        for (int i = 0; i < _index.Length; i++) _index[i] = i;

        var centroids = new Vector3[_tris.Length];
        for (int i = 0; i < _tris.Length; i++)
        {
            var t = _tris[i];
            centroids[i] = (t.A + t.B + t.C) / 3f;
        }

        var nodes = new List<Node>(Math.Max(16, _tris.Length / (LeafSize / 2)));
        BuildNode(nodes, 0, _tris.Length, centroids, 0);
        _nodes = nodes.ToArray();

        BoundsMin = _nodes[0].Min;
        BoundsMax = _nodes[0].Max;
    }

    private int BuildNode(List<Node> nodes, int start, int count, Vector3[] centroids, int depth)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        for (int i = start; i < start + count; i++)
        {
            var t = _tris[_index[i]];
            min = Vector3.Min(min, Vector3.Min(t.A, Vector3.Min(t.B, t.C)));
            max = Vector3.Max(max, Vector3.Max(t.A, Vector3.Max(t.B, t.C)));
        }

        var pad = new Vector3(BoundsEpsilon);
        int self = nodes.Count;
        nodes.Add(new Node
        {
            Min = min - pad,
            Max = max + pad,
            Start = start,
            Count = count,
            Right = -1,
        });

        if (count <= LeafSize || depth >= MaxDepth) return self;

        // Split on the widest axis of the CENTROID bounds, at its midpoint.
        // Object-median is the fallback because a midpoint split degenerates on
        // clustered geometry — and vmap doodads are extremely clustered.
        var cmin = new Vector3(float.MaxValue);
        var cmax = new Vector3(float.MinValue);
        for (int i = start; i < start + count; i++)
        {
            var c = centroids[_index[i]];
            cmin = Vector3.Min(cmin, c);
            cmax = Vector3.Max(cmax, c);
        }

        var extent = cmax - cmin;
        int axis = extent.X >= extent.Y && extent.X >= extent.Z ? 0 : extent.Y >= extent.Z ? 1 : 2;
        float split = 0.5f * (Axis(cmin, axis) + Axis(cmax, axis));

        int mid = start;
        for (int i = start; i < start + count; i++)
        {
            if (Axis(centroids[_index[i]], axis) < split)
            {
                (_index[i], _index[mid]) = (_index[mid], _index[i]);
                mid++;
            }
        }

        int leftCount = mid - start;

        if (leftCount == 0 || leftCount == count)
        {
            var slice = new int[count];
            Array.Copy(_index, start, slice, 0, count);
            int sortAxis = axis;
            Array.Sort(slice, (l, r) =>
                Axis(centroids[l], sortAxis).CompareTo(Axis(centroids[r], sortAxis)));
            Array.Copy(slice, 0, _index, start, count);
            leftCount = count / 2;
        }

        // The left child lands at self + 1 by construction: nothing is appended
        // between this node and its left subtree.
        BuildNode(nodes, start, leftCount, centroids, depth + 1);
        int right = BuildNode(nodes, start + leftCount, count - leftCount, centroids, depth + 1);

        var node = nodes[self];
        node.Count = 0;
        node.Right = right;
        nodes[self] = node;

        return self;
    }

    /// <summary>
    /// Closest hit along a ray, or null. Direction need not be normalised;
    /// it is normalised here and <see cref="RayHit.Distance"/> is in world units.
    /// </summary>
    public RayHit? Raycast(Vector3 origin, Vector3 direction, float maxDistance)
    {
        if (_nodes.Length == 0 || maxDistance <= 0) return null;

        // Into the mesh's frame. Undone on the hit point below.
        origin -= Offset;

        float dirLen = direction.Length();
        if (dirLen < 1e-9f) return null;
        var dir = direction / dirLen;

        var invDir = new Vector3(1f / dir.X, 1f / dir.Y, 1f / dir.Z);

        float best = maxDistance;
        int bestTri = -1;

        Span<int> stack = stackalloc int[MaxDepth * 2 + 8];
        int sp = 0;
        stack[sp++] = 0;

        while (sp > 0)
        {
            int ni = stack[--sp];
            var node = _nodes[ni];

            if (!SlabHit(node.Min, node.Max, origin, invDir, best)) continue;

            if (node.Count == 0)
            {
                // Interior. The left child is always the node immediately after
                // this one (see BuildNode); the right child is stored.
                if (sp + 2 <= stack.Length)
                {
                    stack[sp++] = node.Right;
                    stack[sp++] = ni + 1;
                }
                continue;
            }

            for (int i = node.Start; i < node.Start + node.Count; i++)
            {
                int ti = _index[i];
                if (!IntersectTriangle(_tris[ti], origin, dir, best, out float t)) continue;
                best = t;
                bestTri = ti;
            }
        }

        if (bestTri < 0) return null;

        var tri = _tris[bestTri];
        var normal = Vector3.Normalize(Vector3.Cross(tri.B - tri.A, tri.C - tri.A));
        if (Vector3.Dot(normal, dir) > 0) normal = -normal;

        return new RayHit(best, origin + dir * best + Offset, normal, bestTri);
    }

    /// <summary>True if anything blocks the segment. Cheaper intent, same cost for now.</summary>
    public bool AnyHit(Vector3 origin, Vector3 direction, float maxDistance)
        => Raycast(origin, direction, maxDistance) is not null;

    private static bool SlabHit(in Vector3 min, in Vector3 max, in Vector3 o, in Vector3 invDir, float maxDist)
    {
        float t1 = (min.X - o.X) * invDir.X;
        float t2 = (max.X - o.X) * invDir.X;
        float tmin = MathF.Min(t1, t2);
        float tmax = MathF.Max(t1, t2);

        t1 = (min.Y - o.Y) * invDir.Y;
        t2 = (max.Y - o.Y) * invDir.Y;
        tmin = MathF.Max(tmin, MathF.Min(t1, t2));
        tmax = MathF.Min(tmax, MathF.Max(t1, t2));

        t1 = (min.Z - o.Z) * invDir.Z;
        t2 = (max.Z - o.Z) * invDir.Z;
        tmin = MathF.Max(tmin, MathF.Min(t1, t2));
        tmax = MathF.Min(tmax, MathF.Max(t1, t2));

        if (tmin < 0) tmin = 0;
        return tmax >= tmin && tmin <= maxDist;
    }

    /// <summary>
    /// Moller-Trumbore, two-sided. Two-sided is deliberate: vmap winding is not
    /// consistent, so a one-sided test would make some walls and floors
    /// invisible to the sweep depending on which way the exporter happened to
    /// emit them.
    /// </summary>
    private static bool IntersectTriangle(in Tri tri, in Vector3 origin, in Vector3 dir, float best, out float t)
    {
        t = 0;

        var e1 = tri.B - tri.A;
        var e2 = tri.C - tri.A;
        var p = Vector3.Cross(dir, e2);
        float det = Vector3.Dot(e1, p);
        if (MathF.Abs(det) < 1e-8f) return false;

        float inv = 1f / det;
        var tv = origin - tri.A;

        float u = Vector3.Dot(tv, p) * inv;
        if (u < -1e-5f || u > 1f + 1e-5f) return false;

        var q = Vector3.Cross(tv, e1);
        float v = Vector3.Dot(dir, q) * inv;
        if (v < -1e-5f || u + v > 1f + 1e-5f) return false;

        float hit = Vector3.Dot(e2, q) * inv;
        // The 1e-4 floor stops a surface the character is already resting on
        // from registering as a zero-distance obstruction every frame.
        if (hit < 1e-4f || hit >= best) return false;

        t = hit;
        return true;
    }

    private static float Axis(in Vector3 v, int axis)
        => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

    public void Clear()
    {
        _pending.Clear();
        _pendingSource.Clear();
        _tris = [];
        _source = [];
        _index = [];
        _nodes = [];
        DegenerateSkipped = 0;
        BoundsMin = BoundsMax = Vector3.Zero;
    }

    /// <summary>
    /// Interleaved vertex data for the debug renderer: position (3 floats) plus
    /// the absolute Z of the triangle's normal (1 float), three vertices per
    /// triangle, flat-shaded.
    ///
    /// The normal Z is what makes the wireframe worth looking at rather than
    /// just pretty — it separates surfaces you can stand on from surfaces that
    /// stop you, using the same number the character controller tests against.
    /// If a floor draws in the wall colour, that is the collision bug, visible.
    ///
    /// Positions are duplicated per triangle rather than indexed on purpose:
    /// flat shading needs it, and this buffer is a diagnostic that gets built
    /// once, not a hot path.
    /// </summary>
    public float[] BuildDebugVertices()
    {
        var buffer = new float[_tris.Length * 3 * 5];
        int w = 0;

        for (int i = 0; i < _tris.Length; i++)
        {
            var t = _tris[i];
            var n = Vector3.Normalize(Vector3.Cross(t.B - t.A, t.C - t.A));
            float nz = float.IsNaN(n.Z) ? 0f : MathF.Abs(n.Z);
            float src = i < _source.Length ? _source[i] : -1f;

            buffer[w++] = t.A.X; buffer[w++] = t.A.Y; buffer[w++] = t.A.Z; buffer[w++] = nz; buffer[w++] = src;
            buffer[w++] = t.B.X; buffer[w++] = t.B.Y; buffer[w++] = t.B.Z; buffer[w++] = nz; buffer[w++] = src;
            buffer[w++] = t.C.X; buffer[w++] = t.C.Y; buffer[w++] = t.C.Z; buffer[w++] = nz; buffer[w++] = src;
        }

        return buffer;
    }
}

using System.Numerics;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Silk.NET.OpenGL;
using MSUIClient.Engine;
using MSUIClient.Formats;
using MSUIClient.World.Collision;
using Shader = MSUIClient.Engine.Shader;
using Texture = MSUIClient.Engine.Texture;

namespace MSUIClient.World.Doodads;

/// <summary>
/// Draws M2 doodads — trees, rocks, fences, barrels, the several hundred props
/// per tile that make terrain look like a place rather than a heightmap.
///
/// SAME SHAPE AS WmoRenderer, DELIBERATELY
///   MDDF placements out of the ADT, models out of the MPQs, one VAO per model,
///   one draw per batch. And the same principle: the collision hull comes from
///   the SAME parse as the render mesh, so a tree cannot be solid somewhere it
///   is not drawn.
///
/// AN M2 CARRIES ITS OWN COLLISION MESH, which is the thing that makes vmaps
/// unnecessary for a client. The render mesh of a tree is thousands of
/// triangles including every leaf; the collision hull is a few dozen around the
/// trunk. Many props have none at all and are meant to be walked through — that
/// is why the vmap extractor never produced a .vmo for Torch.m2 or
/// Innchandelier.m2, which looked like 237 missing files and was in fact 237
/// models with nothing to collide against.
///
/// THE COUNTS ARE THE DESIGN CONSTRAINT
///   785 placements on the Northshire tile alone, so roughly 7,000 across a 3x3
///   block, drawn from maybe 150 distinct models. Instances are therefore
///   grouped by model so each VAO binds once, frustum culled individually, and
///   dropped past a draw distance. Without all three this is tens of thousands
///   of draw calls a frame.
/// </summary>
public sealed class DoodadRenderer : IDisposable
{
    /// <summary>Position(3) + Normal(3) + UV(2).</summary>
    private const int FloatsPerVertex = 8;

    /// <summary>Map corner offset. Identical to VmapFormat.CoordShift.</summary>
    private const float CoordShift = 32f * 533.33333f;

    /// <summary>
    /// Bases for the M2 COLLISION hull. The render mesh needs none — an M2
    /// stores render vertices Y-up already — but the collision arrays in the
    /// same file are Z-up, so the hull alone gets converted.
    ///
    /// Index 0 is identity for completeness. Index 2 is the measured answer,
    /// (x, y, z) -> (x, z, -y), the same conversion WMO geometry needs. Index 1
    /// is the other proper rotation onto those axes; it differs by a half turn
    /// about X, invisible on a tree trunk and not on something asymmetric.
    ///
    /// Every entry has determinant +1, so none of them mirrors the geometry.
    /// </summary>
    private static readonly (string Name, Matrix4x4 M)[] Bases =
    [
        ("xyz", Matrix4x4.Identity),
        ("x-zy", new Matrix4x4(1, 0, 0, 0,  0, 0, 1, 0,  0, -1, 0, 0,  0, 0, 0, 1)),
        ("xz-y", new Matrix4x4(1, 0, 0, 0,  0, 0, -1, 0,  0, 1, 0, 0,  0, 0, 0, 1)),
        ("-zyx", new Matrix4x4(0, 0, 1, 0,  0, 1, 0, 0,  -1, 0, 0, 0,  0, 0, 0, 1)),
    ];

    public static string BasisName(int index)
        => index >= 0 && index < Bases.Length ? Bases[index].Name : "?";

    /// <summary>
    /// Which entry the COLLISION HULL uses, and it is not the same one.
    ///
    /// Measured, not assumed. Comparing every hull's local bounds against its
    /// own render mesh showed X identical and Y/Z swapped on 113 of 127 models:
    ///
    ///     elwynncliffrock02  render 17.7, 8.0, 19.0   hull 17.7, 19.0, 8.0
    ///     flagpole01         render  1.0, 10.0, 1.1   hull  1.0,  1.1, 10.0
    ///     smalldock          render 15.9, 26.5, 8.7   hull 16.0,  8.3, 26.3
    ///
    /// So an M2 stores its render vertices Y-up and its collision vertices
    /// Z-up. Two arrays, two conventions, one file. The hull therefore needs
    /// the basis the render mesh does not — and it turns out to be the same
    /// (x, y, z) -> (x, z, -y) that WMO geometry needs, which makes sense if
    /// the collision arrays share a lineage with the WMO tooling.
    ///
    /// Index 1 is the other proper rotation that maps the same axes; it differs
    /// by a half turn about X, which is invisible on a tree trunk and not on
    /// something asymmetric like a dock. If a hull looks flipped end for end,
    /// try it.
    /// </summary>
    public int CollisionBasisIndex { get; set; } = 2;

    /// <summary>ADT placement space to WoW world space: (x, y, z) -> (C - z, C - x, y).</summary>
    private static Matrix4x4 PlacementToWorld => new(
        0f, -1f, 0f, 0f,
        0f, 0f, 1f, 0f,
        -1f, 0f, 0f, 0f,
        CoordShift, CoordShift, 0f, 1f);

    private sealed class Batch
    {
        public uint IndexStart;
        public uint IndexCount;
        public Texture? Texture;
        public bool TwoSided;
    }

    private sealed class Model : IDisposable
    {
        public uint Vao, Vbo, Ebo, InstanceVbo;
        public List<Batch> Batches = [];
        public Vector3 LocalMin, LocalMax;
        public int TriangleCount;

        /// <summary>The M2's own collision hull, local space, three verts per triangle.</summary>
        public Vector3[] CollisionTriangles = [];

        /// <summary>Local bounds of that hull, for comparison against the render mesh.</summary>
        public Vector3 CollisionMin, CollisionMax;
        public string SourcePath = "";

        private GL? _gl;
        public void Attach(GL gl) => _gl = gl;

        public void Dispose()
        {
            if (_gl is null) return;
            _gl.DeleteVertexArray(Vao);
            _gl.DeleteBuffer(Vbo);
            _gl.DeleteBuffer(Ebo);
            if (InstanceVbo != 0) _gl.DeleteBuffer(InstanceVbo);
        }
    }

    private sealed class ModelPreloadJob
    {
        public required string Path;
        public required string CacheKey;
        public required Task<PreparedModel> Worker;
        public PreparedModel? Ready;
        public Task<UploadedModel>? Upload;
        public System.Diagnostics.Stopwatch Timer = System.Diagnostics.Stopwatch.StartNew();
    }

    private sealed class UploadedModel
    {
        public uint Vbo;
        public uint Ebo;
        public Dictionary<string, Texture?> Textures = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class PreparedModel
    {
        public M2Model? Parsed;
        public bool Missing;
        public List<PreparedTexture> Textures = [];
    }

    private sealed class PreparedTexture
    {
        public required string Path;
        public byte[]? Bgra;
        public int Width;
        public int Height;
    }

    private sealed class Instance
    {
        public Matrix4x4 Transform;
        public Vector3 WorldMin, WorldMax;
        public string Path = "";
    }

    private readonly GL _gl;
    private readonly GpuUploadWorker _uploads;
    private readonly AssetWorkerPool _workers;
    private readonly ClientConfig _config;

    private readonly Dictionary<string, Model?> _models = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Texture?> _textures = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Instances grouped by model, so each VAO binds once per frame.</summary>
    private readonly Dictionary<Model, List<Instance>> _byModel = [];
    private readonly List<Matrix4x4> _visibleTransforms = [];

    private readonly HashSet<string> _placed = [];
    private readonly HashSet<string> _missing = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _preloadQueue = new();
    private readonly HashSet<string> _preloadQueued = new(StringComparer.OrdinalIgnoreCase);
    private ModelPreloadJob? _preloadJob;

    private Shader _shader = null!;

    public int InstanceCount { get; private set; }
    public int ModelCount => _models.Count(m => m.Value is not null);
    public int TextureCount => _textures.Count(t => t.Value is not null);
    public int PendingPreloads => _preloadQueue.Count + (_preloadJob is null ? 0 : 1);
    public int TotalTriangles { get; private set; }
    public int CollisionModels { get; private set; }
    public int DrawnLastFrame { get; private set; }
    public int DrawCallsLastFrame { get; private set; }
    public long TrianglesLastFrame { get; private set; }

    /// <summary>Placed instances rejected this frame by the draw-distance test.</summary>
    public int DistanceCulledLastFrame { get; private set; }
    /// <summary>Placed instances rejected this frame by the frustum test.</summary>
    public int FrustumCulledLastFrame { get; private set; }
    /// <summary>Frame counter for the throttled [doodad-cull] diagnostic.</summary>
    private int _cullLogFrames;
    public double RenderMilliseconds { get; private set; }
    public bool Enabled { get; set; } = true;
    public bool FrustumCulling { get; set; } = true;
    /// <summary>
    /// When true, a placement whose model is not resident queues that model and
    /// returns immediately. Runtime placement refreshes will adopt it later.
    /// This keeps MPQ reads, parsing, texture decoding and uploads out of the
    /// render thread.
    /// </summary>
    public bool DemandStreaming { get; set; }
    public bool UseInstancing { get; set; } = true;

    public float DrawDistance { get; set; } = 300f;
    public float VisibilityDistance { get; set; } = float.PositiveInfinity;

    /// <summary>
    /// Alpha below which a doodad fragment is discarded.
    ///
    /// This MUST be set. DoodadRenderer owns its own Shader instance — a
    /// separate GL program built from the same source as the WMO one — so
    /// uniforms set over there do not apply here. Leaving uAlphaCutoff unset
    /// left it at zero, no alpha test ran, and every foliage card rendered as
    /// an opaque black rectangle. Tree leaves are alpha cutouts; without this
    /// the forest becomes a wall of dark panels.
    /// </summary>
    public float AlphaCutoff { get; set; } = 0.5f;

    public Vector3 SunDirection { get; set; } = Vector3.Normalize(new Vector3(0.45f, 0.35f, 0.82f));
    public Vector3 SunColor { get; set; } = new(1.00f, 0.95f, 0.85f);
    public float SunIntensity { get; set; } = 1.15f;
    public Vector3 AmbientColor { get; set; } = new(0.42f, 0.50f, 0.60f);
    public float AmbientIntensity { get; set; } = 0.85f;
    public Vector3 FogColor { get; set; } = new(0.56f, 0.71f, 0.85f);
    public float FogStart { get; set; } = 350f;
    public float FogEnd { get; set; } = 900f;

    public DoodadRenderer(
        GL gl, ClientConfig config, GpuUploadWorker uploads, AssetWorkerPool workers)
    {
        _gl = gl;
        _config = config;
        _uploads = uploads;
        _workers = workers;
    }

    /// <summary>Doodads reuse the WMO shader — same lighting, same fog, same alpha cut.</summary>
    public void LoadShaders(string shaderDir)
    {
        _shader = Shader.FromFiles(_gl,
            Path.Combine(shaderDir, "wmo.vert"),
            Path.Combine(shaderDir, "wmo.frag"));
    }

    public void LoadForTiles(
        IEnumerable<(int col, int row)> tiles,
        AdtCache adts,
        Vector2? streamCentre = null,
        float maxDistance = float.PositiveInfinity,
        bool reportDiagnostics = true)
    {
        var started = DateTime.UtcNow;
        float maxDistanceSq = maxDistance * maxDistance;

        foreach (var (col, row) in tiles)
        {
            var adt = adts.Get(col, row);
            if (adt?.Doodads is null) continue;

            foreach (var d in adt.Doodads)
            {
                if (string.IsNullOrWhiteSpace(d.ModelPath)) continue;

                var transform = BuildPlacement(d);
                if (streamCentre is Vector2 centre && !float.IsPositiveInfinity(maxDistance))
                {
                    var delta = new Vector2(transform.M41, transform.M42) - centre;
                    if (delta.LengthSquared() > maxDistanceSq) continue;
                }

                // A doodad straddling a tile edge is listed in both ADTs. Do
                // not reserve the key until its model is resident: demand
                // streaming must be able to retry this placement next refresh.
                string key = $"{d.ModelPath}|{d.PosX:F2}|{d.PosY:F2}|{d.PosZ:F2}";
                if (_placed.Contains(key)) continue;

                var model = ResolveModel(d.ModelPath);
                if (model is null) continue;
                _placed.Add(key);

                var (min, max) = TransformedBounds(model, transform);

                if (!_byModel.TryGetValue(model, out var list))
                {
                    list = [];
                    _byModel[model] = list;
                }

                list.Add(new Instance
                {
                    Transform = transform,
                    WorldMin = min,
                    WorldMax = max,
                    Path = d.ModelPath,
                });

                InstanceCount++;
                TotalTriangles += model.TriangleCount;
            }
        }

        var elapsed = DateTime.UtcNow - started;
        if (reportDiagnostics)
            Console.WriteLine(
                $"[doodad] {InstanceCount} placement(s), {ModelCount} model(s), " +
                $"{TextureCount} texture(s), {CollisionModels} with collision, " +
                $"{TotalTriangles:N0} triangles, {elapsed.TotalSeconds:F1}s");

        if (reportDiagnostics && _missing.Count > 0)
            Console.WriteLine($"[doodad] {_missing.Count} model(s) not found in the MPQs");

        if (reportDiagnostics) ReportHullAlignment();
    }

    /// <summary>
    /// Drop outdoor and interior placements while retaining model/texture GPU
    /// caches. Streaming rebuilds the small active placement set from ADTs.
    /// </summary>
    public void ResetPlacements()
    {
        _byModel.Clear();
        _placed.Clear();
        InstanceCount = 0;
        TotalTriangles = 0;
        DrawnLastFrame = 0;
    }

    /// <summary>Queue outdoor M2 assets referenced by an outer ADT ring.</summary>
    public void QueuePreloadForTiles(
        IEnumerable<(int col, int row)> tiles,
        AdtCache adts,
        Vector2? streamCentre = null,
        float maxDistance = float.PositiveInfinity)
    {
        float maxDistanceSq = maxDistance * maxDistance;
        var paths = new List<(string Path, float DistanceSq)>();
        foreach (var (col, row) in tiles)
        {
            var adt = adts.Get(col, row);
            if (adt?.Doodads is null) continue;

            foreach (var doodad in adt.Doodads)
            {
                float distanceSq = 0f;
                if (streamCentre is Vector2 centre && !float.IsPositiveInfinity(maxDistance))
                {
                    var transform = BuildPlacement(doodad);
                    var delta = new Vector2(transform.M41, transform.M42) - centre;
                    distanceSq = delta.LengthSquared();
                    if (distanceSq > maxDistanceSq) continue;
                }

                paths.Add((doodad.ModelPath, distanceSq));
            }
        }
        QueuePreloadModels(paths.OrderBy(p => p.DistanceSq).Select(p => p.Path));
    }

    /// <summary>Queue M2 paths without creating visible placements.</summary>
    public void QueuePreloadModels(IEnumerable<string> paths)
    {
        foreach (string path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            string key = ModelCacheKey(path);
            if (_models.ContainsKey(key) ||
                _preloadJob?.CacheKey.Equals(key, StringComparison.OrdinalIgnoreCase) == true ||
                !_preloadQueued.Add(key)) continue;
            _preloadQueue.Enqueue(path);
        }
    }

    /// <summary>
    /// Start CPU preparation on a worker, then finalize at most one texture or
    /// mesh on the render thread. MPQ extraction, M2 parsing and BLP decoding
    /// never consume a movement frame; only OpenGL work remains here.
    /// </summary>
    public bool WarmNextPreload(bool waitForWorker = false)
    {
        while (_preloadJob is null && _preloadQueue.Count > 0)
        {
            string path = _preloadQueue.Dequeue();
            string key = ModelCacheKey(path);
            _preloadQueued.Remove(key);
            if (_models.ContainsKey(key)) continue;
            _preloadJob = new ModelPreloadJob
            {
                Path = path,
                CacheKey = key,
                Worker = _workers.Run(() => PrepareModel(path)),
            };
        }

        if (_preloadJob is null) return false;
        var job = _preloadJob;
        if (waitForWorker && !job.Worker.IsCompleted)
            try { job.Worker.GetAwaiter().GetResult(); } catch { }
        if (!job.Worker.IsCompleted) return true;

        var stepTimer = System.Diagnostics.Stopwatch.StartNew();
        if (FinalizePreload(job, waitForWorker))
        {
            _preloadJob = null;
            if (job.Timer.Elapsed.TotalSeconds >= 0.05)
                Console.WriteLine($"[doodad-preload] {Path.GetFileName(job.Path)} prepared in " +
                                  $"{job.Timer.Elapsed.TotalSeconds:F2}s, {_preloadQueue.Count} queued");
        }
        if (stepTimer.Elapsed.TotalMilliseconds >= 8)
            Console.WriteLine($"[stream-budget] doodad finalize {Path.GetFileName(job.Path)} " +
                              $"took {stepTimer.Elapsed.TotalMilliseconds:F0}ms");
        return true;
    }

    public void DrainPreloads()
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        int steps = 0;
        while (WarmNextPreload(waitForWorker: true)) steps++;
        Console.WriteLine($"[doodad-preload] initial ring completed in {steps} staged step(s), " +
                          $"{timer.Elapsed.TotalSeconds:F1}s");
    }

    /// <summary>
    /// Check each collision hull against its own render mesh, in MODEL space.
    ///
    /// This is the question that matters and it needs no placement at all:
    /// render mesh and collision hull go through the SAME instance transform,
    /// so they cannot be misaligned in the world unless they already disagree
    /// inside the file. Comparing them locally removes every other variable.
    ///
    /// A hull that sits INSIDE its render bounds is correct and the geometry is
    /// simply coarser than what you see - a tree's hull is the trunk, never the
    /// canopy, so wireframe that ignores the leaves is right rather than
    /// broken. A hull that pokes OUTSIDE, or whose extents look like a
    /// permutation of the render extents, means the collision arrays use a
    /// different axis convention than the vertex array and need their own
    /// basis.
    /// </summary>
    private void ReportHullAlignment()
    {
        var withHull = _models.Values
            .Where(m => m is not null && m.CollisionTriangles.Length >= 3)
            .Select(m => m!)
            .ToList();

        if (withHull.Count == 0) return;

        int contained = 0;
        var offenders = new List<(string Path, Vector3 Leak, Vector3 RenderSize, Vector3 HullSize)>();

        foreach (var m in withHull)
        {
            // How far the hull escapes the render box on each axis, in yards.
            var low = m.LocalMin - m.CollisionMin;
            var high = m.CollisionMax - m.LocalMax;

            var leak = new Vector3(
                MathF.Max(0, MathF.Max(low.X, high.X)),
                MathF.Max(0, MathF.Max(low.Y, high.Y)),
                MathF.Max(0, MathF.Max(low.Z, high.Z)));

            // A little slop is normal; hulls are hand-made and can bulge.
            if (leak.X < 0.5f && leak.Y < 0.5f && leak.Z < 0.5f) { contained++; continue; }

            offenders.Add((
                Path.GetFileName(m.SourcePath),
                leak,
                m.LocalMax - m.LocalMin,
                m.CollisionMax - m.CollisionMin));
        }

        Console.WriteLine(
            $"[doodad] hull check: {contained}/{withHull.Count} collision hull(s) sit inside their " +
            $"render mesh (collision basis {CollisionBasisIndex} - {BasisName(CollisionBasisIndex)})");

        foreach (var o in offenders.OrderByDescending(o => o.Leak.Length()).Take(6))
        {
            Console.WriteLine(
                $"[doodad]   {o.Path,-34} escapes by ({o.Leak.X,6:F1},{o.Leak.Y,6:F1},{o.Leak.Z,6:F1})  " +
                $"render ({o.RenderSize.X,6:F1},{o.RenderSize.Y,6:F1},{o.RenderSize.Z,6:F1})  " +
                $"hull ({o.HullSize.X,6:F1},{o.HullSize.Y,6:F1},{o.HullSize.Z,6:F1})");
        }

        if (offenders.Count == 0)
        {
            Console.WriteLine(
                "[doodad] hulls agree with the render meshes - collision is COARSER than the " +
                "visual by design (a tree's hull is its trunk), not misplaced");
        }
        else
        {
            Console.WriteLine(
                $"[doodad] {offenders.Count} hull(s) still escape their render bounds. Permuted hull sizes " +
                "mean the collision basis is wrong - try another render.doodadCollisionBasis");
        }
    }

    /// <summary>
    /// Add a doodad whose transform someone else worked out — used for the
    /// furniture embedded in buildings, which is placed in WMO local space and
    /// rides that building's own transform out into the world.
    ///
    /// Everything downstream is shared with ADT doodads: the same model cache,
    /// the same VAO per model, the same batching, and the same collision hull
    /// treatment. A bed is solid for exactly the same reason a tree is.
    /// </summary>
    public bool AddPlaced(string modelPath, Matrix4x4 transform)
    {
        string key = $"wmo|{modelPath}|{transform.M41:F2}|{transform.M42:F2}|{transform.M43:F2}";
        if (_placed.Contains(key)) return true;

        var model = ResolveModel(modelPath);
        if (model is null) return false;
        _placed.Add(key);

        var (min, max) = TransformedBounds(model, transform);

        if (!_byModel.TryGetValue(model, out var list))
        {
            list = [];
            _byModel[model] = list;
        }

        list.Add(new Instance
        {
            Transform = transform,
            WorldMin = min,
            WorldMax = max,
            Path = modelPath,
        });

        InstanceCount++;
        TotalTriangles += model.TriangleCount;
        return true;
    }

    /// <summary>Report after a batch of AddPlaced calls.</summary>
    public void ReportInterior(int requested, int placed, double seconds)
        => Console.WriteLine(
            $"[doodad] building interiors: {placed}/{requested} placement(s) " +
            $"({requested - placed} model(s) unavailable) in {seconds:F1}s");

    // ── placement ────────────────────────────────────────────────────────────

    private static Matrix4x4 BuildPlacement(AdtTerrainReader.DoodadPlacement d)
    {
        const float deg = MathF.PI / 180f;

        // rotY - 270 + 180 = rotY - 90, the net heading the WMO calibration
        // arrived at once the hand-measured half turn was folded in.
        float heading = (d.RotY - 90f) * deg;

        float scale = d.Scale > 0.0001f ? d.Scale : 1f;

        // No basis: an M2's render vertices are already in placement space.
        return Matrix4x4.CreateScale(scale)
             * Matrix4x4.CreateRotationX(d.RotZ * deg)
             * Matrix4x4.CreateRotationZ(-d.RotX * deg)
             * Matrix4x4.CreateRotationY(heading)
             * Matrix4x4.CreateTranslation(d.PosX, d.PosY, d.PosZ)
             * PlacementToWorld;
    }

    private static (Vector3 min, Vector3 max) TransformedBounds(Model model, Matrix4x4 m)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        for (int c = 0; c < 8; c++)
        {
            var corner = new Vector3(
                (c & 1) == 0 ? model.LocalMin.X : model.LocalMax.X,
                (c & 2) == 0 ? model.LocalMin.Y : model.LocalMax.Y,
                (c & 4) == 0 ? model.LocalMin.Z : model.LocalMax.Z);

            var p = Vector3.Transform(corner, m);
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }

        return (min, max);
    }

    // ── loading ──────────────────────────────────────────────────────────────

    /// <summary>
    /// MDDF stores .mdx (and occasionally .mdl) names, while the files in the
    /// MPQ are .m2. Vanilla tooling has always done this swap; without it every
    /// single doodad silently fails to load.
    /// </summary>
    private static IEnumerable<string> PathCandidates(string modelPath)
    {
        yield return modelPath;

        int dot = modelPath.LastIndexOf('.');
        if (dot > 0)
        {
            string stem = modelPath[..dot];
            yield return stem + ".m2";
            yield return stem + ".M2";
        }
        else
        {
            yield return modelPath + ".m2";
        }
    }

    /// <summary>
    /// ADT MDDF records traditionally spell model files .mdx/.mdl while WMO
    /// interiors normally spell the same MPQ asset .m2. Cache by the physical
    /// M2 name so crossing from the outdoor pass to the interior pass cannot
    /// parse, texture and upload one model twice.
    /// </summary>
    private static string ModelCacheKey(string modelPath)
    {
        string path = modelPath.Replace('/', '\\');
        string extension = Path.GetExtension(path);
        if (extension.Equals(".mdx", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".mdl", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".m2", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^extension.Length] + ".m2";
        }

        return path;
    }

    private Model? ResolveModel(string modelPath)
    {
        string cacheKey = ModelCacheKey(modelPath);
        if (_models.TryGetValue(cacheKey, out var cached)) return cached;

        if (DemandStreaming)
            return null;

        if (_preloadJob?.CacheKey.Equals(cacheKey, StringComparison.OrdinalIgnoreCase) == true)
        {
            while (!FinalizePreloadBlocking(_preloadJob)) { }
            _preloadJob = null;
            return _models.GetValueOrDefault(cacheKey);
        }

        byte[]? bytes = null;
        foreach (var candidate in PathCandidates(modelPath))
        {
            bytes = AdtTerrainReader.ReadFileFromMpqs(_config.ClientDataPath, candidate);
            if (bytes is not null) break;
        }

        if (bytes is null)
        {
            _missing.Add(modelPath);
            _models[cacheKey] = null;
            return null;
        }

        var m2 = M2Reader.Parse(bytes);
        if (m2 is null || !m2.IsValid)
        {
            _models[cacheKey] = null;
            return null;
        }

        var model = BuildModel(m2);
        if (model is not null) model.SourcePath = modelPath;
        _models[cacheKey] = model;

        if (model is not null && model.CollisionTriangles.Length >= 3) CollisionModels++;

        return model;
    }

    private PreparedModel PrepareModel(string path)
    {
        byte[]? bytes = null;
        foreach (var candidate in PathCandidates(path))
        {
            bytes = AdtTerrainReader.ReadFileFromMpqs(_config.ClientDataPath, candidate);
            if (bytes is not null) break;
        }

        if (bytes is null) return new PreparedModel { Missing = true };

        var parsed = M2Reader.Parse(bytes);
        if (parsed is null || !parsed.IsValid) return new PreparedModel();

        var prepared = new PreparedModel { Parsed = parsed };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in parsed.Textures)
        {
            string texturePath = source.Filename;
            if (string.IsNullOrWhiteSpace(texturePath) || !seen.Add(texturePath)) continue;

            var decoded = AdtTerrainReader.ReadBlpPixels(_config.ClientDataPath, texturePath);
            if (decoded is null)
            {
                prepared.Textures.Add(new PreparedTexture { Path = texturePath });
                continue;
            }

            var (bgra, width, height) = decoded.Value;
            prepared.Textures.Add(new PreparedTexture
            {
                Path = texturePath,
                Bgra = bgra,
                Width = width,
                Height = height,
            });
        }

        return prepared;
    }

    private bool FinalizePreloadBlocking(ModelPreloadJob job)
    {
        if (!job.Worker.IsCompleted)
            try { job.Worker.GetAwaiter().GetResult(); } catch { }
        return FinalizePreload(job, waitForUpload: true);
    }

    private bool FinalizePreload(ModelPreloadJob job, bool waitForUpload)
    {
        try { job.Ready ??= job.Worker.GetAwaiter().GetResult(); }
        catch (Exception ex)
        {
            Console.WriteLine($"[doodad-preload] {Path.GetFileName(job.Path)} failed - {ex.Message}");
            _models[job.CacheKey] = null;
            return true;
        }
        var ready = job.Ready;

        if (ready.Missing)
        {
            _missing.Add(job.Path);
            _models[job.CacheKey] = null;
            return true;
        }

        if (ready.Parsed is null)
        {
            _models[job.CacheKey] = null;
            return true;
        }

        if (job.Upload is null)
        {
            var pendingTextures = ready.Textures
                .Where(t => !_textures.ContainsKey(t.Path))
                .ToList();
            job.Upload = _uploads.Enqueue(Path.GetFileName(job.Path), uploadGl =>
                UploadPreparedModel(uploadGl, ready.Parsed, pendingTextures));
        }
        if (waitForUpload && !job.Upload.IsCompleted)
            try { job.Upload.GetAwaiter().GetResult(); } catch { }
        if (!job.Upload.IsCompleted) return false;

        UploadedModel uploaded;
        try { uploaded = job.Upload.GetAwaiter().GetResult(); }
        catch (Exception ex)
        {
            Console.WriteLine($"[doodad-upload] {Path.GetFileName(job.Path)} failed - {ex.Message}");
            _models[job.CacheKey] = null;
            return true;
        }

        foreach (var (path, texture) in uploaded.Textures)
            if (!_textures.ContainsKey(path)) _textures[path] = texture;

        var model = BuildModel(ready.Parsed, uploaded);
        if (model is not null) model.SourcePath = job.Path;
        _models[job.CacheKey] = model;
        if (model is not null && model.CollisionTriangles.Length >= 3) CollisionModels++;
        return true;
    }

    private unsafe UploadedModel UploadPreparedModel(
        GL gl, M2Model m2, IReadOnlyList<PreparedTexture> textures)
    {
        var uploaded = new UploadedModel();
        foreach (var texture in textures)
        {
            uploaded.Textures[texture.Path] = texture.Bgra is null
                ? null
                : Texture.From2D(gl, texture.Bgra, texture.Width, texture.Height, ownerGl: _gl);
        }

        var vertices = BuildVertexArray(m2, out _, out _);
        var indices = m2.Indices.ToArray();

        uploaded.Vbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, uploaded.Vbo);
        fixed (float* p = vertices)
            gl.BufferData(BufferTargetARB.ArrayBuffer,
                (nuint)(vertices.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);

        uploaded.Ebo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, uploaded.Ebo);
        fixed (ushort* p = indices)
            gl.BufferData(BufferTargetARB.ElementArrayBuffer,
                (nuint)(indices.Length * sizeof(ushort)), p, BufferUsageARB.StaticDraw);

        return uploaded;
    }

    private unsafe Model? BuildModel(M2Model m2, UploadedModel? uploaded = null)
    {
        var vertices = BuildVertexArray(m2, out var min, out var max);

        var indices = m2.Indices.ToArray();
        if (indices.Length < 3) return null;

        uint vao = _gl.GenVertexArray();
        _gl.BindVertexArray(vao);

        uint vbo = uploaded?.Vbo ?? _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        if (uploaded is null)
        {
            fixed (float* p = vertices)
                _gl.BufferData(BufferTargetARB.ArrayBuffer,
                    (nuint)(vertices.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);
        }

        uint ebo = uploaded?.Ebo ?? _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, ebo);
        if (uploaded is null)
        {
            fixed (ushort* p = indices)
                _gl.BufferData(BufferTargetARB.ElementArrayBuffer,
                    (nuint)(indices.Length * sizeof(ushort)), p, BufferUsageARB.StaticDraw);
        }

        const uint stride = FloatsPerVertex * sizeof(float);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        _gl.EnableVertexAttribArray(2);
        _gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, (void*)(6 * sizeof(float)));

        uint instanceVbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, instanceVbo);
        const uint instanceStride = 16 * sizeof(float);
        for (uint row = 0; row < 4; row++)
        {
            uint location = 3 + row;
            _gl.EnableVertexAttribArray(location);
            _gl.VertexAttribPointer(location, 4, VertexAttribPointerType.Float, false,
                instanceStride, (void*)(row * 4 * sizeof(float)));
            _gl.VertexAttribDivisor(location, 1);
        }

        _gl.BindVertexArray(0);

        var model = new Model
        {
            Vao = vao,
            Vbo = vbo,
            Ebo = ebo,
            InstanceVbo = instanceVbo,
            LocalMin = min,
            LocalMax = max,
            TriangleCount = indices.Length / 3,
        };
        model.Attach(_gl);

        BuildBatches(m2, model, indices.Length);
        model.CollisionTriangles = BuildCollision(m2, CollisionBasisIndex);

        if (model.CollisionTriangles.Length >= 3)
        {
            var cmin = new Vector3(float.MaxValue);
            var cmax = new Vector3(float.MinValue);
            foreach (var v in model.CollisionTriangles)
            {
                cmin = Vector3.Min(cmin, v);
                cmax = Vector3.Max(cmax, v);
            }
            model.CollisionMin = cmin;
            model.CollisionMax = cmax;
        }

        return model;
    }

    private static float[] BuildVertexArray(M2Model m2, out Vector3 min, out Vector3 max)
    {
        var vertices = new float[m2.Vertices.Count * FloatsPerVertex];
        min = new Vector3(float.MaxValue);
        max = new Vector3(float.MinValue);

        for (int i = 0; i < m2.Vertices.Count; i++)
        {
            var v = m2.Vertices[i];
            int o = i * FloatsPerVertex;
            vertices[o + 0] = v.PosX;
            vertices[o + 1] = v.PosY;
            vertices[o + 2] = v.PosZ;
            vertices[o + 3] = v.NormX;
            vertices[o + 4] = v.NormY;
            vertices[o + 5] = v.NormZ;
            vertices[o + 6] = v.TexU;
            vertices[o + 7] = v.TexV;

            var p = new Vector3(v.PosX, v.PosY, v.PosZ);
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }

        return vertices;
    }

    /// <summary>
    /// One draw per batch. The texture chain is batch.TextureIndex ->
    /// TextureLookup -> Textures, which is the same indirection the item
    /// preview pipeline in SuperUI had to get right; going straight to
    /// Textures[TextureIndex] resolves the wrong image on most models.
    /// </summary>
    private void BuildBatches(M2Model m2, Model model, int indexCount)
    {
        foreach (var batch in m2.Batches)
        {
            if (batch.SubmeshIndex >= m2.Submeshes.Count) continue;

            var submesh = m2.Submeshes[batch.SubmeshIndex];
            if (submesh.IndexCount == 0) continue;
            if (submesh.IndexStart + submesh.IndexCount > indexCount) continue;

            Texture? texture = null;
            if (batch.TextureIndex < m2.TextureLookup.Count)
            {
                int texIdx = m2.TextureLookup[batch.TextureIndex];
                if (texIdx >= 0 && texIdx < m2.Textures.Count)
                    texture = ResolveTexture(m2.Textures[texIdx].Filename);
            }

            bool twoSided = batch.MaterialIndex < m2.RenderFlags.Count
                && m2.RenderFlags[batch.MaterialIndex].TwoSided;

            model.Batches.Add(new Batch
            {
                IndexStart = submesh.IndexStart,
                IndexCount = submesh.IndexCount,
                Texture = texture,
                // Foliage is nearly always two-sided; when in doubt, draw both
                // faces. A missing leaf reads as a bug, a doubled one does not.
                TwoSided = twoSided || texture is null,
            });
        }

        if (model.Batches.Count == 0)
        {
            model.Batches.Add(new Batch
            {
                IndexStart = 0,
                IndexCount = (uint)indexCount,
                Texture = null,
                TwoSided = true,
            });
        }
    }

    /// <summary>
    /// Pull out the collision hull, converting it into the render mesh's space
    /// on the way so both go through the same instance transform afterwards.
    /// </summary>
    private static Vector3[] BuildCollision(M2Model m2, int basisIndex)
    {
        if (!m2.HasCollision) return [];

        var basis = Bases[Math.Clamp(basisIndex, 0, Bases.Length - 1)].M;

        var triangles = new List<Vector3>(m2.CollisionIndices.Count);

        for (int i = 0; i + 2 < m2.CollisionIndices.Count; i += 3)
        {
            int i0 = m2.CollisionIndices[i];
            int i1 = m2.CollisionIndices[i + 1];
            int i2 = m2.CollisionIndices[i + 2];

            if (i0 >= m2.CollisionVertices.Count ||
                i1 >= m2.CollisionVertices.Count ||
                i2 >= m2.CollisionVertices.Count) continue;

            triangles.Add(Vector3.Transform(m2.CollisionVertices[i0], basis));
            triangles.Add(Vector3.Transform(m2.CollisionVertices[i1], basis));
            triangles.Add(Vector3.Transform(m2.CollisionVertices[i2], basis));
        }

        return [.. triangles];
    }

    private Texture? ResolveTexture(string blpPath)
    {
        if (string.IsNullOrWhiteSpace(blpPath)) return null;
        if (_textures.TryGetValue(blpPath, out var cached)) return cached;

        var decoded = AdtTerrainReader.ReadBlpPixels(_config.ClientDataPath, blpPath);
        if (decoded is null)
        {
            _textures[blpPath] = null;
            return null;
        }

        var (bgra, w, h) = decoded.Value;
        var texture = Texture.From2D(_gl, bgra, w, h);
        _textures[blpPath] = texture;
        return texture;
    }

    // ── collision ────────────────────────────────────────────────────────────

    /// <summary>
    /// Feed every doodad's own collision hull into the world, using the SAME
    /// transform the renderer draws with. Models without a hull contribute
    /// nothing, which is correct: you are meant to walk through a torch.
    /// </summary>
    public void AppendCollision(CollisionWorld world)
    {
        int solid = 0, triangles = 0;

        foreach (var (model, instances) in _byModel)
        {
            var tris = model.CollisionTriangles;
            if (tris.Length < 3) continue;

            foreach (var instance in instances)
            {
                int source = world.RegisterSource(Path.GetFileName(instance.Path));
                var m = instance.Transform;

                for (int i = 0; i + 2 < tris.Length; i += 3)
                {
                    world.AddTriangle(
                        Vector3.Transform(tris[i], m),
                        Vector3.Transform(tris[i + 1], m),
                        Vector3.Transform(tris[i + 2], m),
                        source);
                    triangles++;
                }

                solid++;
            }
        }

        Console.WriteLine(
            $"[collision] from doodads: {solid} solid instance(s) of {CollisionModels} model(s), " +
            $"{triangles:N0} triangles");
    }

    // ── drawing ──────────────────────────────────────────────────────────────

    public unsafe void Render(Camera camera)
    {
        long started = Stopwatch.GetTimestamp();
        DrawnLastFrame = 0;
        DrawCallsLastFrame = 0;
        TrianglesLastFrame = 0;
        DistanceCulledLastFrame = 0;
        FrustumCulledLastFrame = 0;
        if (!Enabled || _shader is null || _byModel.Count == 0)
        {
            RenderMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            return;
        }

        _shader.Use();
        _shader.Set("uViewProjection", camera.RelativeViewProjection);
        _shader.Set("uUseInstancing", UseInstancing ? 1 : 0);
        _shader.Set("uCameraPos", Vector3.Zero);
        _shader.Set("uSunDirection", SunDirection);
        _shader.Set("uSunColor", SunColor);
        _shader.Set("uSunIntensity", SunIntensity);
        _shader.Set("uAmbientColor", AmbientColor);
        _shader.Set("uAmbientIntensity", AmbientIntensity);
        _shader.Set("uFogStart", FogStart);
        _shader.Set("uFogEnd", FogEnd);
        _shader.Set("uFogColor", FogColor);
        _shader.Set("uTexture", 0);

        var viewProjection = camera.RelativeViewProjection;
        var eye = camera.Position;
        float effectiveDrawDistance = MathF.Min(DrawDistance, VisibilityDistance);
        float maxDistanceSq = effectiveDrawDistance * effectiveDrawDistance;
        bool cullingOn = true;

        if (UseInstancing)
        {
            RenderInstanced(viewProjection, eye, maxDistanceSq, ref cullingOn);
            if (!cullingOn) _gl.Enable(EnableCap.CullFace);
            _gl.BindVertexArray(0);
            MaybeLogCull(effectiveDrawDistance);
            RenderMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            return;
        }

        foreach (var (model, instances) in _byModel)
        {
            bool bound = false;

            foreach (var instance in instances)
            {
                // Distance first: it is a subtraction, the frustum test is six
                // dot products, and most doodads fail on distance.
                var centre = (instance.WorldMin + instance.WorldMax) * 0.5f;
                if (Vector3.DistanceSquared(centre, eye) > maxDistanceSq) { DistanceCulledLastFrame++; continue; }
                if (FrustumCulling &&
                    !Camera.BoxInFrustum(viewProjection,
                        instance.WorldMin - eye,
                        instance.WorldMax - eye)) { FrustumCulledLastFrame++; continue; }

                if (!bound)
                {
                    _gl.BindVertexArray(model.Vao);
                    bound = true;
                }

                var modelTransform = instance.Transform;
                modelTransform.M41 -= eye.X;
                modelTransform.M42 -= eye.Y;
                modelTransform.M43 -= eye.Z;

                _shader.Set("uModel", modelTransform);
                _shader.Set("uModelViewProjection", modelTransform * camera.RelativeViewProjection);

                foreach (var batch in model.Batches)
                {
                    if (batch.TwoSided && cullingOn)
                    {
                        _gl.Disable(EnableCap.CullFace);
                        cullingOn = false;
                    }
                    else if (!batch.TwoSided && !cullingOn)
                    {
                        _gl.Enable(EnableCap.CullFace);
                        cullingOn = true;
                    }

                    if (batch.Texture is not null)
                    {
                        batch.Texture.Bind(0);
                        _shader.Set("uHasTexture", 1);
                        _shader.Set("uAlphaCutoff", AlphaCutoff);
                    }
                    else
                    {
                        _shader.Set("uHasTexture", 0);
                        _shader.Set("uAlphaCutoff", 0f);
                    }

                    _gl.DrawElements(PrimitiveType.Triangles, batch.IndexCount,
                        DrawElementsType.UnsignedShort, (void*)(batch.IndexStart * sizeof(ushort)));
                    DrawCallsLastFrame++;
                    TrianglesLastFrame += batch.IndexCount / 3;
                }

                DrawnLastFrame++;
            }
        }

        if (!cullingOn) _gl.Enable(EnableCap.CullFace);
        _gl.BindVertexArray(0);
        MaybeLogCull(effectiveDrawDistance);
        RenderMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    /// <summary>
    /// Throttled per-cull diagnostic. Answers the "a doodad is well within draw
    /// distance but does not appear until I am close" question: if a nearby prop
    /// is missing from `placed` it is a streaming/placement gap, not a cull; if
    /// it shows under dist-culled or frustum-culled the render cull is eating it.
    /// Roughly one line every two seconds at 60 FPS.
    /// </summary>
    private void MaybeLogCull(float effectiveDrawDistance)
    {
        if (++_cullLogFrames < 120) return;
        _cullLogFrames = 0;
        Console.WriteLine(
            $"[doodad-cull] placed {InstanceCount}, drawn {DrawnLastFrame}, " +
            $"dist-culled {DistanceCulledLastFrame}, frustum-culled {FrustumCulledLastFrame}, " +
            $"drawDist {effectiveDrawDistance:F0} (slider {DrawDistance:F0}, visCap " +
            $"{(float.IsPositiveInfinity(VisibilityDistance) ? -1f : VisibilityDistance):F0})");
    }

    private unsafe void RenderInstanced(
        Matrix4x4 viewProjection, Vector3 eye, float maxDistanceSq, ref bool cullingOn)
    {
        foreach (var (model, instances) in _byModel)
        {
            _visibleTransforms.Clear();
            foreach (var instance in instances)
            {
                var centre = (instance.WorldMin + instance.WorldMax) * 0.5f;
                if (Vector3.DistanceSquared(centre, eye) > maxDistanceSq) { DistanceCulledLastFrame++; continue; }
                if (FrustumCulling &&
                    !Camera.BoxInFrustum(viewProjection,
                        instance.WorldMin - eye,
                        instance.WorldMax - eye)) { FrustumCulledLastFrame++; continue; }

                var transform = instance.Transform;
                transform.M41 -= eye.X;
                transform.M42 -= eye.Y;
                transform.M43 -= eye.Z;
                _visibleTransforms.Add(transform);
            }

            if (_visibleTransforms.Count == 0) continue;

            _gl.BindVertexArray(model.Vao);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, model.InstanceVbo);
            var transforms = CollectionsMarshal.AsSpan(_visibleTransforms);
            fixed (Matrix4x4* p = transforms)
            {
                _gl.BufferData(BufferTargetARB.ArrayBuffer,
                    (nuint)(transforms.Length * sizeof(Matrix4x4)), p, BufferUsageARB.StreamDraw);
            }

            uint instanceCount = (uint)_visibleTransforms.Count;
            foreach (var batch in model.Batches)
            {
                if (batch.TwoSided && cullingOn)
                {
                    _gl.Disable(EnableCap.CullFace);
                    cullingOn = false;
                }
                else if (!batch.TwoSided && !cullingOn)
                {
                    _gl.Enable(EnableCap.CullFace);
                    cullingOn = true;
                }

                if (batch.Texture is not null)
                {
                    batch.Texture.Bind(0);
                    _shader.Set("uHasTexture", 1);
                    _shader.Set("uAlphaCutoff", AlphaCutoff);
                }
                else
                {
                    _shader.Set("uHasTexture", 0);
                    _shader.Set("uAlphaCutoff", 0f);
                }

                _gl.DrawElementsInstanced(PrimitiveType.Triangles, batch.IndexCount,
                    DrawElementsType.UnsignedShort, (void*)(batch.IndexStart * sizeof(ushort)),
                    instanceCount);
                DrawCallsLastFrame++;
                TrianglesLastFrame += (long)(batch.IndexCount / 3) * instanceCount;
            }

            DrawnLastFrame += _visibleTransforms.Count;
        }
    }

    public void Dispose()
    {
        try { _preloadJob?.Worker.GetAwaiter().GetResult(); }
        catch { /* Shutdown must continue even if a background decode failed. */ }
        try
        {
            if (_preloadJob?.Upload is { } upload)
            {
                var orphan = upload.GetAwaiter().GetResult();
                foreach (var texture in orphan.Textures.Values) texture?.Dispose();
                if (orphan.Vbo != 0) _gl.DeleteBuffer(orphan.Vbo);
                if (orphan.Ebo != 0) _gl.DeleteBuffer(orphan.Ebo);
            }
        }
        catch { /* The upload worker may already be unwinding. */ }
        foreach (var model in _models.Values) model?.Dispose();
        foreach (var texture in _textures.Values) texture?.Dispose();
        _models.Clear();
        _textures.Clear();
        _byModel.Clear();
        _preloadQueue.Clear();
        _preloadQueued.Clear();
        _preloadJob = null;
        _shader?.Dispose();
    }
}

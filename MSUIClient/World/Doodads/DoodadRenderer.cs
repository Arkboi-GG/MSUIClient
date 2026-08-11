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
    /// "No baked light, full daylight" - the value every terrain doodad uses,
    /// and the value GL supplies for a disabled vertex attribute, so a doodad
    /// with no interior light renders exactly as it did before interior
    /// lighting existed.
    /// </summary>
    private static readonly Vector4 ExteriorLight = new(0f, 0f, 0f, 1f);

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

        /// <summary>
        /// M2 material flag 0x01. The batch is authored at full brightness and
        /// must ignore lighting entirely: lantern glows, fire, torch flames,
        /// the additive glow planes around a brazier. Without honouring it, a
        /// lantern goes out the moment interior lighting darkens the room it
        /// hangs in, which is the one thing a lantern must never do.
        /// </summary>
        public bool Unlit;

        /// <summary>
        /// This batch needs the fragment shader's alpha-test discard: M2
        /// blending mode 0 is opaque and does not.
        ///
        /// PARSED BUT NOT YET ACTED ON, deliberately. The obvious use — passing
        /// a cutoff of 0 for opaque batches — buys nothing: a driver disables
        /// early depth rejection on the STATIC presence of `discard` in the
        /// shader, not on the uniform's value, and doodad.frag discards
        /// unconditionally in two places. All that would change is that a mode-0
        /// batch whose texture happens to carry a cutout alpha would start
        /// rendering as a solid quad.
        ///
        /// The real win needs a second shader program with no discard in it,
        /// selected per batch off this flag. That is a bigger change than it
        /// looks (two programs, two uniform sets, a state sort) and belongs in
        /// its own pass. The data is here and correct when someone does it.
        /// </summary>
        public bool AlphaTest;
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

        /// <summary>
        /// The M2's particle emitters (PLAN_14). Kept on the shared Model
        /// because the emitter DEFINITION is per-model, while the live particle
        /// pool a stage-2 renderer spawns from it will be per-instance - two
        /// torches must not share one pool (PLAN_14 H5).
        /// </summary>
        public List<M2ParticleEmitter> Emitters = [];

        /// <summary>
        /// The BLP path for each emitter's texture, resolved once at load.
        /// Emitter.Texture indexes the M2's TEXTURES array directly - NOT
        /// through TextureLookup, which is the mesh's indirection and would
        /// pick the wrong file.
        /// </summary>
        public string[] EmitterTexturePaths = [];

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

        /// <summary>
        /// Baked interior light for THIS placement. rgb is MODD.color / 255,
        /// a is how much daylight to use instead (0 interior, 1 exterior).
        ///
        /// (0, 0, 0, 1) means "no baked light, full daylight", which is both
        /// the correct answer for every terrain doodad and the value GL hands
        /// a disabled vertex attribute - so the default costs nothing and
        /// behaves identically to having no interior lighting at all.
        /// </summary>
        public Vector4 Light = new(0f, 0f, 0f, 1f);

        /// <summary>Appear-fade spawn time in seconds; 0 = opaque/no fade. Set from
        /// the persistent per-key stamp so a re-placement on a tile crossing does
        /// not re-fade. See <see cref="ResolveAppearStart"/>.</summary>
        public float AppearStart;

        // Set only for MODD furniture. Terrain doodads remain unowned and are
        // never portal-culled.
        public int WmoInstanceId;
        public int[] OwnerGroups = [];
    }

    /// <summary>
    /// What actually goes in the instance VBO: the placement matrix, the baked
    /// light, then the appear-fade start. Sequential layout of 21 floats with no
    /// padding, which is what the stride arithmetic in BuildModel assumes.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct InstanceData
    {
        public Matrix4x4 Transform;
        public Vector4 Light;
        public float AppearStart;
    }

    private readonly GL _gl;
    private readonly GpuUploadWorker _uploads;
    private readonly AssetWorkerPool _workers;
    private readonly ClientConfig _config;

    private readonly Dictionary<string, Model?> _models = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Texture?> _textures = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Instances grouped by model, so each VAO binds once per frame.</summary>
    private readonly Dictionary<Model, List<Instance>> _byModel = [];

    /// <summary>
    /// Just the cull bounds, 24 bytes, contiguous. A value type on purpose.
    /// </summary>
    private readonly struct CullBounds(Vector3 min, Vector3 max)
    {
        public readonly Vector3 Min = min;
        public readonly Vector3 Max = max;
    }

    /// <summary>
    /// Bounds parallel to <see cref="_byModel"/>, same model key, same index.
    ///
    /// Measured 2026-07-25: the cull ran at **751-9,053 ns per instance** across
    /// two crossings, against roughly 50-100 ns for the arithmetic it actually
    /// performs. The loop was never the problem. `Instance` is a sealed CLASS,
    /// so `List&lt;Instance&gt;` is a list of pointers, and reading `WorldMin`
    /// and `WorldMax` for every placement means dereferencing thousands of
    /// scattered heap objects that PopulateDoodads allocated moments earlier in
    /// the same frame. The rate tracked model count (512 models -> 9,053 ns,
    /// 153 models -> 751 ns), which is locality, not workload: more models means
    /// more separate lists means a more scattered walk.
    ///
    /// So the cull reads THIS instead. 6,364 placements x 24 bytes is ~153 KB,
    /// contiguous, and it fits in L2. The heap object is touched only for the
    /// instances that survive - roughly 250 of 6,600, so about 96 percent of the
    /// pointer chases disappear.
    ///
    /// Kept parallel rather than folded into one type to hold the change down to
    /// three sites; the two Add paths and the two Clear paths are the only
    /// places it can drift, and DrainCullBounds self-heals if it ever does.
    /// </summary>
    private readonly Dictionary<Model, List<CullBounds>> _cullBounds = [];

    /// <summary>
    /// A/B switch for the flat-bounds cull, so the change can be proven in one
    /// session without a rebuild. PLAN_08 section 7 step 3: a change that does
    /// not move its own named field did nothing. The named field here is
    /// `cull` in the [hitch] doodad line. Toggle it, cross the same boundary,
    /// diff that number.
    /// </summary>
    public bool FlatCullBounds { get; set; } = true;

    private bool _cullBoundsDriftReported;
    private readonly List<InstanceData> _visibleInstances = [];

    private readonly HashSet<string> _placed = [];
    private readonly HashSet<string> _missing = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _preloadQueue = new();
    private readonly Queue<string> _newlyReadyModels = new();
    private readonly HashSet<string> _preloadQueued = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (float DistanceSq, string Queue)> _preloadTrace =
        new(StringComparer.OrdinalIgnoreCase);
    // A POOL of in-flight prepare jobs. Was a single _preloadJob, which prepared
    // exactly ONE model at a time - the ~0.10 s x N serial stall on zone load and
    // .tele (245 Stormwind doodads = ~25 s of one-at-a-time streaming). The
    // AssetWorkerPool caps real concurrency; keeping several jobs in flight
    // saturates it so many models decode in parallel.
    private readonly List<ModelPreloadJob> _preloadJobs = new();
    private const int MaxConcurrentPreloads = 12;

    private Shader _shader = null!;

    public int InstanceCount { get; private set; }

    /// <summary>
    /// How many placements carry a baked interior light (MODD.color) rather
    /// than falling back to daylight. Purely diagnostic: if this reads 0 while
    /// standing in a tavern, MODR or the interior gate is the thing that broke,
    /// not the shader.
    /// </summary>
    public int InteriorLitCount { get; private set; }
    public int ModelCount => _models.Count(m => m.Value is not null);

    /// <summary>
    /// Every loaded model that carries particle emitters, with its path.
    /// Read-only; PLAN_14 stage 1's panel is the only caller.
    /// </summary>
    public IEnumerable<(string Path, IReadOnlyList<M2ParticleEmitter> Emitters)> ModelsWithEmitters()
    {
        foreach (var (path, model) in _models)
            if (model is not null && model.Emitters.Count > 0)
                yield return (path, model.Emitters);
    }

    /// <summary>
    /// One entry per (placement, emitter) within <paramref name="radius"/> of a
    /// point, for the particle renderer to keep pools for.
    ///
    /// Distance is measured to the PLACEMENT, not the emitter, because the
    /// emitter's own offset needs the transform applied and the caller does that
    /// anyway. A generous radius here is cheap - the pool map is what bounds the
    /// work, not this walk.
    /// </summary>
    public IEnumerable<(string Path, Matrix4x4 Transform, M2ParticleEmitter Emitter,
                        int EmitterIndex, string TexturePath, double AnimationTime, int AnimationId,
                        Vector3? LocalOrigin, Quaternion? LocalRotation)>
        EmitterInstances(Vector3 near, float radius)
    {
        float r2 = radius * radius;
        foreach (var (model, instances) in _byModel)
        {
            if (model.Emitters.Count == 0) continue;
            foreach (var inst in instances)
            {
                var origin = new Vector3(inst.Transform.M41, inst.Transform.M42, inst.Transform.M43);
                if (Vector3.DistanceSquared(origin, near) > r2) continue;

                for (int e = 0; e < model.Emitters.Count; e++)
                {
                    string tex = e < model.EmitterTexturePaths.Length
                        ? model.EmitterTexturePaths[e] : "";
                    yield return (inst.Path, inst.Transform, model.Emitters[e], e, tex, double.NaN, 0,
                        null, null);
                }
            }
        }
    }

    /// <summary>Total emitters across every loaded model.</summary>
    public int EmitterCount
    {
        get
        {
            int n = 0;
            foreach (var m in _models.Values) n += m?.Emitters.Count ?? 0;
            return n;
        }
    }
    public int TextureCount => _textures.Count(t => t.Value is not null);
    public int PendingPreloads => _preloadQueue.Count + _preloadJobs.Count;
    public Action<string, string, float>? PreloadDequeued { get; set; }
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
    public void NoteNotRendered()
    {
        RenderMilliseconds = 0;
        CullMilliseconds = InstanceUploadMilliseconds = DrawMilliseconds = 0;
        UploadedModelsLastFrame = FirstTouchModelsLastFrame = 0;
        CullModelsLastFrame = CullInstancesLastFrame = 0;
    }

    // ── The 60 ms split (2026-07-25) ────────────────────────────────────────
    //
    // A crossing frame measured `doodad 60.3 ms` of CPU render while the GPU
    // drew the same pass in 0.1 ms, with no GC pause (1.7 of 91 ms), no upload
    // in flight, and the frame's last GL call returning in 0.1 ms. The cost is
    // inside RenderInstanced and it is not the GPU, not the collector and not
    // the swap. RenderInstanced does three unrelated things in one loop, and one
    // RenderMilliseconds cannot tell them apart:
    //
    //   Cull   - pure CPU over every placement (6,695 at the crossing)
    //   Upload - one glBufferData per visible model, StreamDraw
    //   Draw   - texture binds, uniform sets, DrawElementsInstanced
    //
    // They fail for different reasons. Cull is our own arithmetic and scales
    // with placement count. Upload and Draw are driver calls: on this Intel
    // driver the FIRST bind of a texture created on the shared upload context
    // can force a synchronization, and a crossing is exactly when dozens of
    // models are touched by the render context for the first time.
    //
    // Note the uploads counter added earlier CANNOT see that case - it reports
    // uploads in flight during the frame, and these completed frames earlier.
    // First-touch is a different failure from concurrent-upload, so it needs its
    // own count. That is what FirstTouchModelsLastFrame is for.

    /// <summary>Distance and frustum rejection over every placement. Pure CPU, ours.</summary>
    public double CullMilliseconds { get; private set; }

    /// <summary>The per-model glBufferData of visible instance data. Driver call.</summary>
    public double InstanceUploadMilliseconds { get; private set; }

    /// <summary>Binds, uniform sets and DrawElementsInstanced. Driver calls.</summary>
    public double DrawMilliseconds { get; private set; }

    /// <summary>
    /// Models drawn this frame that were not drawn last frame. A crossing spikes
    /// this; a steady walk holds it near zero. If the cost tracks THIS rather
    /// than the placement count, it is first-touch of shared-context objects and
    /// the fix is a warm-up pass, not a cheaper cull.
    /// </summary>
    public int FirstTouchModelsLastFrame { get; private set; }

    /// <summary>Models that issued a glBufferData this frame.</summary>
    public int UploadedModelsLastFrame { get; private set; }

    /// <summary>
    /// Entries of _byModel walked this frame, and instances examined across all
    /// of them. Without these, CullMilliseconds cannot be divided by anything
    /// and "55.8 ms" stays a number instead of a rate.
    ///
    /// The measured rate is what decides the next move. Roughly:
    ///   ~50-100 ns/instance  - normal; the cull is fine and the cost is elsewhere
    ///   ~1000+ ns/instance   - memory, not arithmetic. Instance is a sealed
    ///                          class, so this loop pointer-chases 6,000+
    ///                          scattered heap objects that PopulateDoodads
    ///                          allocated moments earlier in the same frame.
    ///                          The fix is flat contiguous bounds, not a cheaper
    ///                          test.
    /// High ns/model with low ns/instance would mean the opposite - per-model
    /// overhead over a dictionary with far more entries than expected.
    /// </summary>
    public int CullModelsLastFrame { get; private set; }

    /// <summary>Instances examined by the cull this frame, across all models.</summary>
    public int CullInstancesLastFrame { get; private set; }

    // Preallocated and reused - an instrument that allocates two sets per frame
    // would add to the allocation rate it exists to help explain.
    // Not readonly: these are swapped rather than copied at the end of each
    // instanced pass.
    private HashSet<Model> _drawnPreviousFrame = [];
    private HashSet<Model> _drawnThisFrame = [];

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

    /// <summary>
    /// Overbright factor for baked vertex light. MUST TRACK THE WMO RENDERER'S
    /// VALUE. Vanilla authored vertex colour in [0, 2] rather than [0, 1], and
    /// a prop lit on a different scale from the floor under it detaches from
    /// that floor in a way that is instantly visible and hard to name.
    /// </summary>
    public float VertexColorScale { get; set; } = 2.0f;

    /// <summary>Beyond-portal fill light, driven per frame by GameLoop (see
    /// WmoRenderer). Position is world-absolute; colour premultiplied by
    /// intensity; radius 0 disables it so exterior props are untouched.</summary>
    public Vector3 PortalLightWorldPos { get; set; }
    public Vector3 PortalLightColor { get; set; }
    public float PortalLightRadius { get; set; }

    /// <summary>
    /// Whether props inside buildings use their baked MODD.color instead of the
    /// outdoor sun. Off restores the pre-interior-lighting look exactly, which
    /// is what makes this a useful A/B toggle rather than a debug leftover.
    /// </summary>
    public bool InteriorLighting { get; set; } = true;

    /// <summary>Single portal-visibility authority supplied by WmoRenderer.
    /// Null keeps every doodad visible.</summary>
    public Func<int, int[], bool>? PortalVisibility { get; set; }

    // ── appear fade (benilla model_fade.rs) ─────────────────────────────────────

    /// <summary>Ease streamed-in doodads in over <see cref="AppearFadeSeconds"/>
    /// (alpha = t^3) instead of popping. Off restores the original hard pop-in.</summary>
    public bool AppearFade { get; set; }

    /// <summary>Appear-fade ramp length in seconds (benilla APPEAR_FADE_SECS = 2).</summary>
    public float AppearFadeSeconds { get; set; } = 2f;

    /// <summary>World clock in seconds, pushed each frame by GameLoop. Drives both
    /// the shader fade and the spawn-time stamp below.</summary>
    public float NowSeconds { get; set; }

    /// <summary>True once the loading curtain has lifted. While false (initial
    /// build behind the curtain) every placement is stamped opaque, so the
    /// curtain's own fade covers the first reveal and only models streamed in
    /// later ease in - benilla arms its appear fades on the same "world shown"
    /// signal (model_fade.rs arm_appear_fade / progress.focus_resident).</summary>
    public bool WorldShown { get; set; }

    /// <summary>
    /// Spawn time per placement KEY, surviving ResetPlacements. A tile crossing
    /// rebuilds every resident placement from scratch (Program.cs UpdateWorldResidency),
    /// so without a persistent stamp the whole visible world would re-fade on every
    /// crossing. A key already here reuses its (zero, or long-past) start and stays
    /// opaque; a genuinely new key seen after the world is shown is stamped NOW and
    /// eases in. Capped so a long roam can't grow it without bound.
    /// </summary>
    private readonly Dictionary<string, float> _appearStartByKey = new(StringComparer.Ordinal);
    private const int AppearKeyCap = 262144;

    /// <summary>Resolve (and remember) the appear-fade start for a placement key.
    /// 0 means "opaque, no fade". Only ever fades a genuinely new placement that
    /// first appears after the world is on screen.</summary>
    private float ResolveAppearStart(string key)
    {
        if (!AppearFade) return 0f;
        if (_appearStartByKey.TryGetValue(key, out float start)) return start;
        start = WorldShown ? NowSeconds : 0f;
        if (_appearStartByKey.Count < AppearKeyCap) _appearStartByKey[key] = start;
        return start;
    }

    public DoodadRenderer(
        GL gl, ClientConfig config, GpuUploadWorker uploads, AssetWorkerPool workers)
    {
        _gl = gl;
        _config = config;
        _uploads = uploads;
        _workers = workers;
    }

    /// <summary>
    /// Doodads have their OWN shader pair, forked from the WMO one.
    ///
    /// They used to load wmo.vert/wmo.frag directly. That was harmless while
    /// props and walls wanted identical lighting, and became a hazard the
    /// moment they did not: the baked MOCV lighting on building walls is
    /// correct and must not move, while props need per-instance interior light
    /// and the M2 unlit flag, neither of which a wall has any use for.
    /// Forking the files is what lets doodad lighting change without touching
    /// wall lighting.
    ///
    /// The exterior lighting and fog maths in the fork are character-for-
    /// character the WMO ones, so a barrel standing in a field lights exactly
    /// as it did before the fork.
    /// </summary>
    public void LoadShaders(string shaderDir)
    {
        _shader = Shader.FromFiles(_gl,
            Path.Combine(shaderDir, "doodad.vert"),
            Path.Combine(shaderDir, "doodad.frag"));
    }

    public void LoadForTiles(
        IEnumerable<(int col, int row)> tiles,
        AdtCache adts,
        Vector2? streamCentre = null,
        float maxDistance = float.PositiveInfinity,
        bool reportDiagnostics = true,
        IReadOnlySet<string>? modelFilter = null)
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
                if (modelFilter is not null &&
                    !modelFilter.Contains(ModelCacheKey(d.ModelPath))) continue;

                var transform = BuildPlacement(d);
                if (streamCentre is Vector2 centre && !float.IsPositiveInfinity(maxDistance))
                {
                    var delta = new Vector2(transform.M41, transform.M42) - centre;
                    if (delta.LengthSquared() > maxDistanceSq) continue;
                }

                // A doodad straddling a tile edge is listed in both ADTs. Do
                // not reserve the key until its model is resident: demand
                // streaming must be able to retry this placement next refresh.
                // Key on the WORLD position and an extension-less, lower-cased path so
                // this collapses with the WMO-embedded key below - the same doodad present
                // in BOTH the terrain (.mdx) and a WMO doodad set (.m2) is otherwise drawn
                // twice (the instance portal). See NormalizeModelKey.
                string key = $"{NormalizeModelKey(d.ModelPath)}|{transform.M41:F2}|{transform.M42:F2}|{transform.M43:F2}";
                if (_placed.Contains(key)) continue;

                var model = ResolveModel(d.ModelPath);
                if (model is null) continue;
                if (ExistingEmitterPlacementNear(model, transform))
                {
                    _placed.Add(key);
                    Console.WriteLine($"[doodad] deduped near-coincident effect: {d.ModelPath}");
                    continue;
                }
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
                    AppearStart = ResolveAppearStart(key),
                });
                CullBoundsFor(model).Add(new CullBounds(min, max));

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
        _cullBounds.Clear();
        _placed.Clear();
        InstanceCount = 0;
        InteriorLitCount = 0;
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
        QueuePreloadModels(
            paths.OrderBy(p => p.DistanceSq).Select(p => (p.Path, p.DistanceSq)),
            "outdoor-doodad");
    }

    /// <summary>Queue M2 paths without creating visible placements.</summary>
    public void QueuePreloadModels(IEnumerable<string> paths)
    {
        foreach (string path in paths)
            QueuePreloadModel(path, 0f, "doodad");
    }

    public void QueuePreloadModels(
        IEnumerable<(string Path, float DistanceSq)> paths, string queue)
    {
        foreach (var (path, distanceSq) in paths)
            QueuePreloadModel(path, distanceSq, queue);
    }

    private void QueuePreloadModel(string path, float distanceSq, string queue)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        string key = ModelCacheKey(path);
        if (_models.ContainsKey(key) ||
            _preloadJobs.Any(j => j.CacheKey.Equals(key, StringComparison.OrdinalIgnoreCase)) ||
            !_preloadQueued.Add(key)) return;
        _preloadQueue.Enqueue(path);
        _preloadTrace[key] = (distanceSq, queue);
    }

    /// <summary>
    /// Start CPU preparation on a worker, then finalize at most one texture or
    /// mesh on the render thread. MPQ extraction, M2 parsing and BLP decoding
    /// never consume a movement frame; only OpenGL work remains here.
    /// </summary>
    public bool WarmNextPreload(bool waitForWorker = false)
    {
        // Keep the worker pool saturated: start prepares until MaxConcurrentPreloads
        // are in flight. THIS is the parallelism - PrepareModel (MPQ read + M2 parse
        // + BLP decode) now runs on the pool for many models at once instead of one.
        while (_preloadJobs.Count < MaxConcurrentPreloads && _preloadQueue.Count > 0)
        {
            string path = _preloadQueue.Dequeue();
            string key = ModelCacheKey(path);
            _preloadQueued.Remove(key);
            var trace = _preloadTrace.GetValueOrDefault(key);
            _preloadTrace.Remove(key);
            PreloadDequeued?.Invoke(trace.Queue ?? "doodad", path, trace.DistanceSq);
            if (_models.ContainsKey(key)) continue;
            if (_preloadJobs.Any(j => j.CacheKey.Equals(key, StringComparison.OrdinalIgnoreCase))) continue;
            _preloadJobs.Add(new ModelPreloadJob
            {
                Path = path,
                CacheKey = key,
                Worker = _workers.Run(() => PrepareModel(path)),
            });
        }

        if (_preloadJobs.Count == 0) return false;

        // Advance every job whose CPU prepare is done. FinalizePreload enqueues the
        // GPU upload (cheap - keeps the upload thread fed) and, once the upload lands,
        // builds + caches the model. The GL build is the only main-thread cost, a few
        // ms per model; upload throughput bounds the rest.
        for (int i = _preloadJobs.Count - 1; i >= 0; i--)
        {
            var job = _preloadJobs[i];
            if (waitForWorker && !job.Worker.IsCompleted)
                try { job.Worker.GetAwaiter().GetResult(); } catch { }
            if (!job.Worker.IsCompleted) continue; // still preparing on a worker thread

            var stepTimer = System.Diagnostics.Stopwatch.StartNew();
            if (FinalizePreload(job, waitForWorker))
            {
                _preloadJobs.RemoveAt(i);
                if (job.Timer.Elapsed.TotalSeconds >= 0.05)
                    Console.WriteLine($"[doodad-preload] {Path.GetFileName(job.Path)} prepared in " +
                                      $"{job.Timer.Elapsed.TotalSeconds:F2}s, " +
                                      $"{_preloadQueue.Count + _preloadJobs.Count} in flight");
            }
            if (stepTimer.Elapsed.TotalMilliseconds >= 8)
                Console.WriteLine($"[stream-budget] doodad finalize {Path.GetFileName(job.Path)} " +
                                  $"took {stepTimer.Elapsed.TotalMilliseconds:F0}ms");
        }

        return _preloadQueue.Count > 0 || _preloadJobs.Count > 0;
    }

    /// <summary>Drain model paths that became resident since the previous demand pass.</summary>
    public void DrainNewlyReadyModelPaths(List<string> destination)
    {
        destination.Clear();
        while (_newlyReadyModels.Count > 0) destination.Add(_newlyReadyModels.Dequeue());
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
    /// <param name="light">
    /// Baked interior light for this placement: rgb = MODD.color / 255, a = how
    /// much daylight to use instead. Null keeps the exterior default, which is
    /// what an unlit-by-the-building placement wants.
    /// </param>
    /// <summary>Dedup-key path part: extension-less and lower-cased, so an MDDF
    /// ".mdx" placement and a WMO-embedded ".m2" placement of the SAME model share
    /// a key. Without it the two placement paths never dedup and a doodad present in
    /// both sources (the instance portal) is placed - and drawn - twice.</summary>
    private static string NormalizeModelKey(string path)
    {
        int dot = path.LastIndexOf('.');
        return (dot > 0 ? path[..dot] : path).ToLowerInvariant();
    }

    /// <summary>Console dump of every placement whose path contains <paramref name="filter"/>,
    /// with its world position - to catch a double-placed doodad like the portal.</summary>
    public void DumpEmitterPlacements(string filter)
    {
        int total = 0;
        Console.WriteLine($"[place-dump] placements matching '{filter}':");
        foreach (var (_, instances) in _byModel)
            foreach (var inst in instances)
            {
                if (filter.Length > 0 &&
                    inst.Path.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var t = inst.Transform;
                Console.WriteLine($"[place-dump]   {inst.Path}  @ ({t.M41:F2}, {t.M42:F2}, {t.M43:F2})");
                total++;
            }
        Console.WriteLine($"[place-dump] {total} placement(s)");
    }

    /// <summary>Two same-model placements within this radius (yards) of each other
    /// are treated as one - but ONLY for emitter-carrying models (portals/effects),
    /// so tightly-clustered props (rocks, crates) are never merged. The instance
    /// portal is authored in BOTH the terrain and the entrance WMO ~1.4 yd apart.</summary>
    private const float EmitterDedupRadius = 2.5f;

    private bool ExistingEmitterPlacementNear(Model model, Matrix4x4 transform)
    {
        if (model.Emitters.Count == 0) return false;
        if (!_byModel.TryGetValue(model, out var list)) return false;
        var p = new Vector3(transform.M41, transform.M42, transform.M43);
        foreach (var inst in list)
        {
            var q = new Vector3(inst.Transform.M41, inst.Transform.M42, inst.Transform.M43);
            if (Vector3.DistanceSquared(p, q) < EmitterDedupRadius * EmitterDedupRadius)
                return true;
        }
        return false;
    }

    /// <summary>Remove an existing near-coincident placement of this emitter model
    /// so a later WMO-embedded copy supersedes a terrain one (the portal is authored
    /// in both, and the WMO copy is the more centred/intended one). Keeps _cullBounds
    /// parallel to _byModel and the counters correct.</summary>
    private bool RemoveNearEmitterPlacement(Model model, Matrix4x4 transform)
    {
        if (model.Emitters.Count == 0) return false;
        if (!_byModel.TryGetValue(model, out var list)) return false;
        var p = new Vector3(transform.M41, transform.M42, transform.M43);
        for (int i = 0; i < list.Count; i++)
        {
            var q = new Vector3(list[i].Transform.M41, list[i].Transform.M42, list[i].Transform.M43);
            if (Vector3.DistanceSquared(p, q) >= EmitterDedupRadius * EmitterDedupRadius) continue;
            if (list[i].Light.W < 0.5f) InteriorLitCount--;
            InstanceCount--;
            TotalTriangles -= model.TriangleCount;
            list.RemoveAt(i);
            if (_cullBounds.TryGetValue(model, out var cb) && i < cb.Count) cb.RemoveAt(i);
            return true;
        }
        return false;
    }

    public bool AddPlaced(string modelPath, Matrix4x4 transform, Vector4? light = null,
        int wmoInstanceId = 0, int[]? ownerGroups = null)
    {
        string key = $"{NormalizeModelKey(modelPath)}|{transform.M41:F2}|{transform.M42:F2}|{transform.M43:F2}";
        if (_placed.Contains(key)) return true;

        var model = ResolveModel(modelPath);
        if (model is null) return false;
        // A WMO-embedded effect (the entrance portal) supersedes a near terrain copy -
        // it is the more centred, intended placement. Remove the terrain one, then add.
        if (RemoveNearEmitterPlacement(model, transform))
            Console.WriteLine($"[doodad] WMO effect supersedes a near terrain copy: {modelPath}");
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
            Light = light ?? new Vector4(0f, 0f, 0f, 1f),
            AppearStart = ResolveAppearStart(key),
            WmoInstanceId = wmoInstanceId,
            OwnerGroups = ownerGroups ?? [],
        });
        CullBoundsFor(model).Add(new CullBounds(min, max));

        InstanceCount++;
        if ((light ?? new Vector4(0f, 0f, 0f, 1f)).W < 0.5f) InteriorLitCount++;
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
        int dot = modelPath.LastIndexOf('.');
        if (dot > 0)
        {
            string stem = modelPath[..dot];
            yield return stem + ".m2";
            if (!modelPath.EndsWith(".m2", StringComparison.OrdinalIgnoreCase))
                yield return modelPath;
        }
        else
        {
            yield return modelPath + ".m2";
            yield return modelPath;
        }
    }

    /// <summary>
    /// ADT MDDF records traditionally spell model files .mdx/.mdl while WMO
    /// interiors normally spell the same MPQ asset .m2. Cache by the physical
    /// M2 name so crossing from the outdoor pass to the interior pass cannot
    /// parse, texture and upload one model twice.
    /// </summary>
    public static string ModelCacheKey(string modelPath)
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

        var inflight = _preloadJobs.FirstOrDefault(
            j => j.CacheKey.Equals(cacheKey, StringComparison.OrdinalIgnoreCase));
        if (inflight is not null)
        {
            while (!FinalizePreloadBlocking(inflight)) { }
            _preloadJobs.Remove(inflight);
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
        if (model is not null) _newlyReadyModels.Enqueue(job.Path);
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

        // 16 floats of placement matrix, then 4 of baked light, then 1 appear-fade
        // start: one InstanceData. Locations 3..6 are the matrix rows, 7 the light,
        // 8 the appear-fade start.
        const uint instanceStride = 21 * sizeof(float);
        for (uint row = 0; row < 4; row++)
        {
            uint location = 3 + row;
            _gl.EnableVertexAttribArray(location);
            _gl.VertexAttribPointer(location, 4, VertexAttribPointerType.Float, false,
                instanceStride, (void*)(row * 4 * sizeof(float)));
            _gl.VertexAttribDivisor(location, 1);
        }

        _gl.EnableVertexAttribArray(7);
        _gl.VertexAttribPointer(7, 4, VertexAttribPointerType.Float, false,
            instanceStride, (void*)(16 * sizeof(float)));
        _gl.VertexAttribDivisor(7, 1);

        _gl.EnableVertexAttribArray(8);
        _gl.VertexAttribPointer(8, 1, VertexAttribPointerType.Float, false,
            instanceStride, (void*)(20 * sizeof(float)));
        _gl.VertexAttribDivisor(8, 1);

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
        model.Emitters = m2.ParticleEmitters;
        model.EmitterTexturePaths = new string[m2.ParticleEmitters.Count];
        for (int i = 0; i < m2.ParticleEmitters.Count; i++)
        {
            int ti = m2.ParticleEmitters[i].Texture;
            model.EmitterTexturePaths[i] =
                ti >= 0 && ti < m2.Textures.Count ? m2.Textures[ti].Filename : "";
        }

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
            bool unlit = batch.MaterialIndex < m2.RenderFlags.Count
                && m2.RenderFlags[batch.MaterialIndex].Unlit;

            // Vanilla M2 blending modes: 0 opaque, 1 alpha-key, 2 alpha,
            // 3 additive, 4 mod, 5 mod2x. Only 0 is genuinely opaque. This pass
            // does not blend, so everything above 0 keeps the cutoff it had —
            // the change is that mode 0 now gets early-Z back.
            bool alphaTest = batch.MaterialIndex >= m2.RenderFlags.Count
                || m2.RenderFlags[batch.MaterialIndex].BlendingMode != 0;

            model.Batches.Add(new Batch
            {
                IndexStart = submesh.IndexStart,
                IndexCount = submesh.IndexCount,
                Texture = texture,
                // Foliage is nearly always two-sided; when in doubt, draw both
                // faces. A missing leaf reads as a bug, a doubled one does not.
                TwoSided = twoSided || texture is null,
                Unlit = unlit,
                AlphaTest = alphaTest,
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
    /// <summary>
    /// Record each solid doodad placement as a reference to its immutable
    /// collision geometry plus a transform. See CollisionBatch for why this
    /// does not expand triangles.
    /// </summary>
    public int SnapshotCollision(List<CollisionBatch> into)
    {
        int solid = 0;

        foreach (var (model, instances) in _byModel)
        {
            var tris = model.CollisionTriangles;
            if (tris.Length < 3) continue;

            foreach (var instance in instances)
            {
                into.Add(new CollisionBatch(tris, instance.Transform, instance.Path, 0));
                solid++;
            }
        }

        return solid;
    }

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
        _shader.Set("uVertexColorScale", VertexColorScale);
        _shader.Set("uStyleWeight", 0.42f);
        _shader.Set("uAppearFadeEnabled", AppearFade ? 1 : 0);
        _shader.Set("uNow", NowSeconds);
        _shader.Set("uAppearFadeSecs", MathF.Max(AppearFadeSeconds, 0.0001f));

        var viewProjection = camera.RelativeViewProjection;
        var eye = camera.Position;
        _shader.Set("uPortalLightPos",
            PortalLightRadius > 0f ? PortalLightWorldPos - eye : Vector3.Zero);
        _shader.Set("uPortalLightColor", PortalLightColor);
        _shader.Set("uPortalLightRadius", PortalLightRadius);
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

        // Appear fade needs straight-alpha blending while a doodad eases in; at
        // alpha 1 (every steady doodad) it composites identically to opaque, so
        // this is a no-op for the resident world. Depth-write stays on (benilla
        // wow_model.wgsl). Restored after the loop.
        if (AppearFade)
        {
            _gl.Enable(EnableCap.Blend);
            _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        }
        _shader.Set("uPreserveAlpha", AppearFade ? 1 : 0);

        foreach (var (model, instances) in _byModel)
        {
            bool bound = false;

            foreach (var instance in instances)
            {
                if (instance.WmoInstanceId != 0 &&
                    PortalVisibility is not null &&
                    !PortalVisibility(instance.WmoInstanceId, instance.OwnerGroups)) continue;
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
                _shader.Set("uInstanceLight",
                    InteriorLighting ? instance.Light : ExteriorLight);
                _shader.Set("uAppearStart", instance.AppearStart);

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

                    _shader.Set("uUnlit", batch.Unlit ? 1 : 0);

                    _gl.DrawElements(PrimitiveType.Triangles, batch.IndexCount,
                        DrawElementsType.UnsignedShort, (void*)(batch.IndexStart * sizeof(ushort)));
                    DrawCallsLastFrame++;
                    TrianglesLastFrame += batch.IndexCount / 3;
                }

                DrawnLastFrame++;
            }
        }

        if (AppearFade) _gl.Disable(EnableCap.Blend);
        if (!cullingOn) _gl.Enable(EnableCap.CullFace);
        _gl.BindVertexArray(0);
        MaybeLogCull(effectiveDrawDistance);
        RenderMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        // The non-instanced path does not produce the three-way split. Zero it
        // rather than leaving the last instanced frame's numbers standing - a
        // stale timer reporting a plausible value is worse than a missing one.
        CullMilliseconds = InstanceUploadMilliseconds = DrawMilliseconds = 0;
        UploadedModelsLastFrame = FirstTouchModelsLastFrame = 0;
        CullModelsLastFrame = CullInstancesLastFrame = 0;
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

    private List<CullBounds> CullBoundsFor(Model model)
    {
        if (!_cullBounds.TryGetValue(model, out var bounds))
        {
            bounds = [];
            _cullBounds[model] = bounds;
        }
        return bounds;
    }

    /// <summary>
    /// Bring the parallel bounds back in line if a placement path ever forgets
    /// to append. Self-healing on purpose: a cull reading a stale or short
    /// bounds list would silently draw the wrong props, and a wrong picture is
    /// far worse than a slow one. Reported once, not per frame, but reported -
    /// a silent repair is how a bug survives a whole session.
    /// </summary>
    private void RebuildCullBounds(List<Instance> instances, List<CullBounds> bounds)
    {
        bounds.Clear();
        foreach (var instance in instances)
            bounds.Add(new CullBounds(instance.WorldMin, instance.WorldMax));

        if (_cullBoundsDriftReported) return;
        _cullBoundsDriftReported = true;
        Console.WriteLine(
            "[doodad-cull] cull bounds drifted from placements and were rebuilt - " +
            "a placement path is adding to _byModel without updating _cullBounds");
    }

    private unsafe void RenderInstanced(
        Matrix4x4 viewProjection, Vector3 eye, float maxDistanceSq, ref bool cullingOn)
    {
        // Three accumulators rather than three brackets around the whole loop:
        // the phases interleave per model, so only a running total is honest.
        double cullTicks = 0, uploadTicks = 0, drawTicks = 0;
        int uploadedModels = 0;
        int firstTouch = 0;
        int cullModels = 0;
        int cullInstances = 0;
        _drawnThisFrame.Clear();

        // Appear fade needs straight-alpha blending while a doodad eases in.
        //
        // IT IS NOW ENABLED PER MODEL, NOT FOR THE WHOLE PASS. At alpha 1 the
        // blend composites identically to opaque, so it was described as a
        // no-op — but it is not free: every doodad fragment in the world paid a
        // colour-buffer read-modify-write for a fade that, in the steady state,
        // no doodad is doing. Blending is now switched on only for models that
        // actually have an instance mid-fade, which in practice is a handful for
        // two seconds after a tile streams in and none at all thereafter.
        bool blendOn = false;

        foreach (var (model, instances) in _byModel)
        {
            long cullStarted = Stopwatch.GetTimestamp();
            cullModels++;
            cullInstances += instances.Count;
            _visibleInstances.Clear();
            if (FlatCullBounds)
            {
                // The reject path never touches an Instance. About 96% of
                // placements are rejected (5,616 of 6,694 measured), so this is
                // where the pointer chases go: a linear walk over 24-byte
                // structs, dereferencing the heap object only for survivors.
                var bounds = CullBoundsFor(model);
                if (bounds.Count != instances.Count) RebuildCullBounds(instances, bounds);

                var boundsSpan = CollectionsMarshal.AsSpan(bounds);
                for (int i = 0; i < boundsSpan.Length; i++)
                {
                    ref readonly var b = ref boundsSpan[i];
                    var centre = (b.Min + b.Max) * 0.5f;
                    if (Vector3.DistanceSquared(centre, eye) > maxDistanceSq) { DistanceCulledLastFrame++; continue; }
                    if (FrustumCulling &&
                        !Camera.BoxInFrustum(viewProjection,
                            b.Min - eye,
                            b.Max - eye)) { FrustumCulledLastFrame++; continue; }

                    var instance = instances[i];
                    if (instance.WmoInstanceId != 0 &&
                        PortalVisibility is not null &&
                        !PortalVisibility(instance.WmoInstanceId, instance.OwnerGroups)) continue;
                    var transform = instance.Transform;
                    transform.M41 -= eye.X;
                    transform.M42 -= eye.Y;
                    transform.M43 -= eye.Z;
                    _visibleInstances.Add(new InstanceData
                    {
                        Transform = transform,
                        Light = InteriorLighting ? instance.Light : ExteriorLight,
                        AppearStart = instance.AppearStart,
                    });
                }
            }
            else
            {
                // The original array-of-pointers walk, kept verbatim as the A/B
                // baseline. Deleting it would make the toggle a lie.
                foreach (var instance in instances)
                {
                    if (instance.WmoInstanceId != 0 &&
                        PortalVisibility is not null &&
                        !PortalVisibility(instance.WmoInstanceId, instance.OwnerGroups)) continue;
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
                    _visibleInstances.Add(new InstanceData
                    {
                        Transform = transform,
                        Light = InteriorLighting ? instance.Light : ExteriorLight,
                        AppearStart = instance.AppearStart,
                    });
                }
            }

            cullTicks += Stopwatch.GetTimestamp() - cullStarted;

            if (_visibleInstances.Count == 0) continue;

            _drawnThisFrame.Add(model);
            if (!_drawnPreviousFrame.Contains(model)) firstTouch++;

            long uploadStarted = Stopwatch.GetTimestamp();
            _gl.BindVertexArray(model.Vao);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, model.InstanceVbo);
            var instanceData = CollectionsMarshal.AsSpan(_visibleInstances);
            fixed (InstanceData* p = instanceData)
            {
                _gl.BufferData(BufferTargetARB.ArrayBuffer,
                    (nuint)(instanceData.Length * sizeof(InstanceData)), p, BufferUsageARB.StreamDraw);
            }
            uploadTicks += Stopwatch.GetTimestamp() - uploadStarted;
            uploadedModels++;

            long drawStarted = Stopwatch.GetTimestamp();

            // Does anything we are about to draw actually need blending? Only
            // the survivors are scanned, and only until the first fader.
            bool wantBlend = false;
            if (AppearFade)
            {
                float fadeSecs = MathF.Max(AppearFadeSeconds, 0.0001f);
                foreach (var visible in _visibleInstances)
                {
                    if (visible.AppearStart > 0f && NowSeconds - visible.AppearStart < fadeSecs)
                    {
                        wantBlend = true;
                        break;
                    }
                }
            }

            if (wantBlend && !blendOn)
            {
                _gl.Enable(EnableCap.Blend);
                _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                blendOn = true;
            }
            else if (!wantBlend && blendOn)
            {
                _gl.Disable(EnableCap.Blend);
                blendOn = false;
            }

            _shader.Set("uPreserveAlpha", wantBlend ? 1 : 0);

            uint instanceCount = (uint)_visibleInstances.Count;
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

                _shader.Set("uUnlit", batch.Unlit ? 1 : 0);

                _gl.DrawElementsInstanced(PrimitiveType.Triangles, batch.IndexCount,
                    DrawElementsType.UnsignedShort, (void*)(batch.IndexStart * sizeof(ushort)),
                    instanceCount);
                DrawCallsLastFrame++;
                TrianglesLastFrame += (long)(batch.IndexCount / 3) * instanceCount;
            }

            drawTicks += Stopwatch.GetTimestamp() - drawStarted;
            DrawnLastFrame += _visibleInstances.Count;
        }

        if (blendOn) _gl.Disable(EnableCap.Blend);

        double perTick = 1000.0 / Stopwatch.Frequency;
        CullMilliseconds = cullTicks * perTick;
        InstanceUploadMilliseconds = uploadTicks * perTick;
        DrawMilliseconds = drawTicks * perTick;
        UploadedModelsLastFrame = uploadedModels;
        FirstTouchModelsLastFrame = firstTouch;
        CullModelsLastFrame = cullModels;
        CullInstancesLastFrame = cullInstances;

        // Swap, do not copy: this runs every frame and copying a set of live
        // models per frame is exactly the kind of quiet allocation that made
        // 40 MB/frame possible in the first place.
        (_drawnPreviousFrame, _drawnThisFrame) = (_drawnThisFrame, _drawnPreviousFrame);
    }

    public void Dispose()
    {
        foreach (var job in _preloadJobs)
        {
            try { job.Worker.GetAwaiter().GetResult(); }
            catch { /* Shutdown must continue even if a background decode failed. */ }
            try
            {
                if (job.Upload is { } upload)
                {
                    var orphan = upload.GetAwaiter().GetResult();
                    foreach (var texture in orphan.Textures.Values) texture?.Dispose();
                    if (orphan.Vbo != 0) _gl.DeleteBuffer(orphan.Vbo);
                    if (orphan.Ebo != 0) _gl.DeleteBuffer(orphan.Ebo);
                }
            }
            catch { /* The upload worker may already be unwinding. */ }
        }
        foreach (var model in _models.Values) model?.Dispose();
        foreach (var texture in _textures.Values) texture?.Dispose();
        _models.Clear();
        _textures.Clear();
        _byModel.Clear();
        _cullBounds.Clear();
        _preloadQueue.Clear();
        _preloadQueued.Clear();
        _preloadJobs.Clear();
        _shader?.Dispose();
    }
}

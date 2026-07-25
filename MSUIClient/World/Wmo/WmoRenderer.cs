using System.Numerics;
using System.Diagnostics;
using Silk.NET.OpenGL;
using MSUIClient.Engine;
using MSUIClient.Formats;
using MSUIClient.World.Collision;
using Shader = MSUIClient.Engine.Shader;
using Texture = MSUIClient.Engine.Texture;

namespace MSUIClient.World.Wmo;

/// <summary>
/// Why a WMO group drew or did not draw. One winning reason per group, decided by
/// WmoRenderer.ClassifyGroup, which both the render path and the read-only pick /
/// dump explanation call - so the reason reported can never drift from the reason
/// acted on. The last group (NotResident..Antiportal) is assigned at the build /
/// stream stage by the scene dump, not by ClassifyGroup. See PLAN_03_REASON_CODES.md.
/// </summary>
public enum WmoReasonCode
{
    Drawn,
    DrawnShellFar,
    ShellNearSuppressed,
    InteriorCull,
    DistanceCulled,
    FrustumCulled,
    OcclusionCulled,
    OverrideShow,
    OverrideHide,
    NotResident,
    MissingFile,
    NotBuilt,
    NoGeometry,
    Antiportal,
}

/// <summary>
/// Draws WMO buildings — the abbey, the farmhouses, Stormwind.
///
/// PIPELINE
///   ADT MODF placements (AdtTerrainReader.AdtResult.Wmos)
///     -> root .wmo out of the MPQs        (WmoReader.ParseRoot)
///     -> group files _000.wmo, _001.wmo   (WmoReader.ParseGroup)
///     -> one VAO per group, one draw per MOBA batch
///   Textures come from MOMT material names via AdtTerrainReader.ReadBlpPixels,
///   which is the same BLP -> BGRA -> GL path the terrain tileset already uses.
///
/// PLACEMENT — the coordinate part, which is where this could go wrong
///
///   MODF positions are NOT world coordinates. They live in ADT placement
///   space: a Y-up system whose origin is the map corner, with posX and posZ
///   running 0..34133 (= 64 * 533.33333). AdtTerrainReader's own diagnostics
///   pin the axes down:
///
///       posX / 533.33 -> ADT column, and column = 32 - worldY / 533.33
///       posZ / 533.33 -> ADT row,    and row    = 32 - worldX / 533.33
///       posY          -> world Z
///
///   Solving those gives the conversion in <see cref="PlacementToWorld"/>:
///
///       worldX = C - posZ        C = 32 * 533.33333 = 17066.67
///       worldY = C - posX
///       worldZ = posY
///
///   C is the SAME constant the vmap files use, which is a good sign rather
///   than a coincidence — both are measuring from the same map corner. And the
///   linear part has determinant +1, so it is a rotation, not a mirror:
///   triangle winding and normals come through unharmed.
///
///   The model's own vertices are already in this Y-up space, so the whole
///   placement transform is built there and converted to world once, at the
///   end. Same shape as the vmap loader, for the same reason.
///
/// THE ONE THING NOT YET VERIFIED is the ROTATION convention — whether MODF's
/// Euler triple composes as RotY(rotY - 270) * RotZ(-rotX) * RotX(rotZ), which
/// is what most ADT renderers use. So this self-checks: MODF also ships an
/// axis-aligned bounding box for the PLACED model, in the same space. Convert
/// that box to world, compare it against the bounds of the geometry we actually
/// transformed, and a wrong rotation shows up as a mismatch immediately —
/// printed at load, no walking around required.
/// </summary>
public sealed class WmoRenderer : IDisposable
{
    /// <summary>Position(3) + Normal(3) + UV(2) + MOCV colour(4).</summary>
    private const int FloatsPerVertex = 12;

    /// <summary>Map corner offset. Identical to VmapFormat.CoordShift.</summary>
    private const float CoordShift = 32f * 533.33333f;

    private sealed class Batch
    {
        public uint IndexStart;
        public uint IndexCount;
        public Texture? Texture;
        public bool TwoSided;

        /// <summary>
        /// Whether this batch's texture actually carries alpha. The cutoff
        /// itself is applied at DRAW time, not baked here — baking it made the
        /// HUD slider inert, which cost a debugging round trip.
        /// </summary>
        public bool AlphaTest;
        public bool Transparent;

        /// <summary>
        /// Which MOBA run this batch belongs to, which is what decides how its
        /// baked MOCV colour combines with the runtime sun:
        ///   1 transparent — fades from baked to daylight across a portal
        ///   2 interior    — baked only; no sun, no runtime ambient
        ///   3 exterior    — daylight only; MOCV ignored
        /// Also 3 for any group with no MOCV at all, which is how a vanilla
        /// exterior group is lit and matches the pre-MOCV behaviour exactly.
        /// </summary>
        public int Type = 3;
    }

    private sealed class GroupMesh : IDisposable
    {
        public uint Vao, Vbo, Ebo;
        public List<Batch> Batches = [];
        public Vector3 LocalMin, LocalMax;
        public bool IsInterior;
        public bool IsDistanceLod;

        // Identity, kept for the in-game group picker and for live (re-tunable)
        // shell classification at draw time.
        public int GroupIndex;
        public string GroupName = "";
        public uint GroupFlags;
        public int VertexCount;

        // The run of MOPR entries that belong to this group (MOGP +0x24/+0x26):
        // every doorway out of it. Retained here so the portal graph survives
        // the load job - PreparedWmo is transient and the raw WmoGroupData is
        // discarded once the mesh is uploaded. PLAN_10 D1/D2.
        public int PortalStart;
        public int PortalCount;

        // Local-space geometry retained on the CPU purely for the triangle-level
        // group picker (the GPU copy can't be read back cheaply). Positions only.
        public Vector3[] PickPositions = [];
        public int[] PickIndices = [];

        private GL? _gl;
        public void Attach(GL gl) => _gl = gl;

        public void Dispose()
        {
            if (_gl is null) return;
            _gl.DeleteVertexArray(Vao);
            _gl.DeleteBuffer(Vbo);
            _gl.DeleteBuffer(Ebo);
        }
    }

    private sealed class Model : IDisposable
    {
        public List<GroupMesh> Groups = [];
        public int TriangleCount;

        // ── Portal graph (PLAN_10) ──────────────────────────────────────────
        //
        // MOPV/MOPT/MOPR, retained from the load job. WmoReader has parsed these
        // since the WMO reader was written and the comment on WmoRootData.Portals
        // still says "no renderer reads these yet" - this is where that stops
        // being true. Kept in WMO LOCAL space, exactly as authored: an instance
        // transform turns them into world space when needed, and storing them
        // pre-transformed would break the moment a model is placed twice.
        public List<(float x, float y, float z)> PortalVertices = [];
        public List<WmoPortal> Portals = [];
        public List<WmoPortalRef> PortalRefs = [];

        /// <summary>MOHD's portal count, for the cross-check in PLAN_10 §7 step 2.</summary>
        public uint DeclaredPortalCount;

        /// <summary>
        /// The building's embedded doodads — its furniture. Beds, kitchen
        /// fittings, tables, barrels: none of these are ADT placements, they
        /// belong to the WMO itself via MODS/MODN/MODD and are placed in WMO
        /// LOCAL space, so they ride the building's own transform.
        /// </summary>
        public List<WmoDoodadSet> DoodadSets = [];
        public List<WmoDoodadDef> Doodads = [];

        /// <summary>
        /// Per-doodad baked interior light, index-parallel to <see cref="Doodads"/>.
        ///
        /// rgb = MODD.color / 255, a = daylight blend (0 = fully baked interior,
        /// 1 = fully outdoor sun+ambient). Doodads owned by an EXTERIOR or
        /// EXTERIOR_LIT group — lamp posts, chimneys, the crates stacked against
        /// an outside wall — get (0,0,0,1), i.e. lit exactly as they are today.
        ///
        /// See BuildDoodadLighting for why MODD.color is a baked light and not
        /// the "tint" the wiki calls it.
        /// </summary>
        public Vector4[] DoodadLight = [];

        /// <summary>
        /// Collidable triangles in WMO local space, three vertices each.
        ///
        /// This is the whole point of collecting collision here rather than
        /// from vmaps: it is the SAME vertex array the renderer uploads, so the
        /// wall you see and the wall you hit cannot be in different places. The
        /// vmap path loads a second copy of the same building through a second
        /// transform chain, and any disagreement between the two is a bug that
        /// simply cannot exist if there is only one chain.
        /// </summary>
        public Vector3[] CollisionTriangles = [];
        public int CollisionSkipped;

        public void Dispose()
        {
            foreach (var g in Groups) g.Dispose();
        }
    }

    /// <summary>
    /// Incremental WMO build state. A city WMO can contain hundreds of group
    /// files; treating the whole root as one frame-sized operation caused the
    /// multi-second freezes seen while walking into Stormwind.
    /// </summary>
    private sealed class ModelLoadJob
    {
        public required string RootPath;
        public required Task<PreparedWmo> Worker;
        public PreparedWmo? Ready;
        public Task<UploadedWmo>? Upload;
        public bool UploadAccepted;
        public Model Model = new();
        public List<Vector3> Collision = [];
        public int NextGroup;
        public int Skipped;
        public int MissingFiles, Unparsed, Empty, Antiportal, Unbuilt, BatchesDropped;
        public int IndicesCovered, IndicesTotal;
        public System.Diagnostics.Stopwatch Timer = System.Diagnostics.Stopwatch.StartNew();
    }

    private sealed class UploadedWmo
    {
        public Dictionary<string, Texture?> Textures = new(StringComparer.OrdinalIgnoreCase);
        public List<UploadedGroup?> Groups = [];
    }

    private sealed class UploadedGroup
    {
        public uint Vbo;
        public uint Ebo;
    }

    private sealed class PreparedWmo
    {
        public WmoRootData? Root;
        public bool MissingRoot;
        public List<WmoGroupData?> Groups = [];
        public List<PreparedTexture> Textures = [];
        public int MissingFiles, Unparsed, Empty, Antiportal;
    }

    private sealed class PreparedTexture
    {
        public required string Path;
        public byte[]? Bgra;
        public int Width;
        public int Height;
        public byte MaxAlpha;
    }

    private sealed class Instance
    {
        public Model Model = null!;
        public Matrix4x4 Transform;
        public Vector3 WorldMin, WorldMax;
        public Vector3 Origin;
        public string Path = "";

        /// <summary>Which MODS doodad set this placement asked for.</summary>
        public int DoodadSet;
    }

    /// <summary>Per-frame inputs the group classifier needs, gathered once per instance.</summary>
    private readonly struct FrameCullContext
    {
        public readonly Vector3 CameraPosition;
        public readonly bool CameraInside;
        public readonly float EffectiveDrawDistance;
        public readonly Matrix4x4 ViewProjection;

        public FrameCullContext(Vector3 cameraPosition, bool cameraInside,
            float effectiveDrawDistance, Matrix4x4 viewProjection)
        {
            CameraPosition = cameraPosition;
            CameraInside = cameraInside;
            EffectiveDrawDistance = effectiveDrawDistance;
            ViewProjection = viewProjection;
        }
    }

    private readonly GL _gl;
    private readonly GpuUploadWorker _uploads;
    private readonly AssetWorkerPool _workers;
    private readonly ClientConfig _config;

    private readonly Dictionary<string, Model?> _models = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Texture?> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Instance> _instances = [];
    private readonly HashSet<string> _placed = [];
    private readonly Queue<string> _preloadQueue = new();

    /// <summary>
    /// Preload-ring tiles whose ADT had not finished parsing when the ring was
    /// computed. Retried every frame from <see cref="WarmNextPreload"/> instead
    /// of being waited on - see the contract on QueuePreloadForTiles.
    /// </summary>
    private readonly HashSet<(int col, int row)> _deferredRingTiles = new();
    private AdtCache? _ringAdts;
    private readonly HashSet<string> _preloadQueued = new(StringComparer.OrdinalIgnoreCase);
    private ModelLoadJob? _preloadJob;
    private readonly Queue<string> _newDoodadModels = new();
    private readonly HashSet<string> _announcedDoodadModels = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Textures whose decoded alpha channel is entirely zero.</summary>
    private readonly HashSet<string> _opaqueTextures = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Textures whose 1-bit alpha came back as 0/1 and was rescaled to 0/255.</summary>
    private readonly HashSet<string> _rescaledAlpha = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>BLPs that were named by a material but could not be decoded.</summary>
    private readonly HashSet<string> _failedTextures = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Batches that ended up with no texture at all, from any slot.</summary>
    private int _texturelessBatches;

    /// <summary>Frame counter for the throttled [wmo-vis] visibility trace.</summary>
    private int _wmoVisLogFrames;

    /// <summary>Largest WMO group count seen so far this frame (for the HUD trace).</summary>
    private int _frameLargestWmoGroupCount;

    private Shader _shader = null!;

    public int InstanceCount => _instances.Count;

    /// <summary>
    /// Every placed building's world bounds, for cross-checking against the
    /// collision placement of the same model.
    /// </summary>
    public IEnumerable<(string Path, Vector3 Min, Vector3 Max, Vector3 Origin)> Placements
        => _instances.Select(i => (i.Path, i.WorldMin, i.WorldMax, i.Origin));
    public int ModelCount => _models.Count(m => m.Value is not null);
    public int TextureCount => _textures.Count(t => t.Value is not null);
    public int PendingPreloads => _preloadQueue.Count + (_preloadJob is null ? 0 : 1);

    /// <summary>Ring tiles still waiting on an ADT parse. Should trend to zero.</summary>
    public int DeferredRingTiles => _deferredRingTiles.Count;
    public int TotalTriangles { get; private set; }
    public int DrawnLastFrame { get; private set; }
    public int VisibleGroupsLastFrame { get; private set; }
    public int LodGroupsCulledLastFrame { get; private set; }
    public int DrawCallsLastFrame { get; private set; }
    public long TrianglesLastFrame { get; private set; }
    public double RenderMilliseconds { get; private set; }
    public bool Enabled { get; set; } = true;
    public bool FrustumCulling { get; set; } = true;
    public bool UseDistanceLodShells { get; set; } = true;

    /// <summary>
    /// Yards of slack on the per-group "is the camera inside this WMO" test that
    /// drives the distance-impostor swap (the Stormwind cathedral / grand-entrance
    /// stand-ins) and keeps a large WMO's own interior visible from within.
    /// Positive grows every group box (you count as inside sooner / from further
    /// out); negative shrinks it (you must be deeper in). Tune against the real
    /// client so the impostor drops right at the gate.
    /// </summary>
    public float InsideInstanceMargin { get; set; } = 0f;

    /// <summary>Dump the group table of large WMOs once at load (name/flags/LOD).
    /// Off by default now that it's captured; the HUD toggle re-enables it (then reload).</summary>
    public bool DumpLargeWmoGroups { get; set; } = false;

    /// <summary>Distance (yd) at which a big WMO's own interior cells are culled
    /// while the camera is OUTSIDE it. HUD-tunable. Was the hard-coded 120.</summary>
    public float InteriorCullDistance { get; set; } = 120f;

    // ════════════════════════════════════════════════════════════════════════
    // PLAN_10 D1 — which group is the camera in?
    //
    // Portal traversal starts from the camera's group, so this is the whole
    // problem before it is any of the problem: get it wrong and every later
    // symptom looks like a portal bug. It is also useful on its own - interior
    // lighting and MFOG both need it - which is why the plan says build it
    // alone and confirm it against walking through a door.
    //
    // Nothing here culls anything yet. This is the instrument.
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>Where the camera is, in WMO terms. Null means outdoors.</summary>
    public readonly record struct CameraCell(
        string InstancePath, int GroupIndex, string GroupName,
        bool IsInterior, float Volume, int PortalCount);

    /// <summary>
    /// The most specific group containing the camera, or null when outdoors.
    /// Recomputed once per frame; read by the HUD and, later, by traversal.
    /// </summary>
    public CameraCell? CameraGroup { get; private set; }

    /// <summary>How many groups contained the camera before the tie-break.</summary>
    public int CameraGroupCandidates { get; private set; }

    /// <summary>
    /// Find the group the camera is standing in.
    ///
    /// TWO RULES, both learned from CameraInsideInstance's comment above:
    ///
    /// 1. DISTANCE-LOD SHELLS ARE NOT CELLS. A shell's box is huge and being
    ///    inside it does not mean you are indoors - counting them made the
    ///    whole-instance box swallow Stormwind's approach bridge. Skipped, and
    ///    PLAN_10 D5 says the same thing for traversal.
    ///
    /// 2. SMALLEST VOLUME WINS. Group boxes NEST: a room sits inside a building
    ///    shell which sits inside a district cell. All three contain the camera
    ///    and only the smallest is the cell you are actually in. Picking the
    ///    first match instead would return whichever the file happened to list
    ///    first, which is stable, plausible, and wrong.
    ///
    /// No margin is applied, unlike CameraInsideInstance's InsideInstanceMargin:
    /// that margin exists to keep the impostor suppressed slightly outside a
    /// building, whereas this answers "which room", where a margin would make
    /// two adjacent rooms both claim the doorway.
    /// </summary>
    public void UpdateCameraCell(Vector3 cameraWorld)
    {
        CameraCell? best = null;
        float bestVolume = float.MaxValue;
        int candidates = 0;

        foreach (var instance in _instances)
        {
            // Cheap reject on the world box before inverting a matrix.
            if (cameraWorld.X < instance.WorldMin.X || cameraWorld.X > instance.WorldMax.X ||
                cameraWorld.Y < instance.WorldMin.Y || cameraWorld.Y > instance.WorldMax.Y ||
                cameraWorld.Z < instance.WorldMin.Z || cameraWorld.Z > instance.WorldMax.Z)
                continue;

            if (!Matrix4x4.Invert(instance.Transform, out var inv)) continue;
            var local = Vector3.Transform(cameraWorld, inv);

            foreach (var g in instance.Model.Groups)
            {
                if (g.IsDistanceLod) continue;               // rule 1
                if (local.X < g.LocalMin.X || local.X > g.LocalMax.X ||
                    local.Y < g.LocalMin.Y || local.Y > g.LocalMax.Y ||
                    local.Z < g.LocalMin.Z || local.Z > g.LocalMax.Z)
                    continue;

                candidates++;
                var size = g.LocalMax - g.LocalMin;
                float volume = MathF.Max(size.X, 0f) * MathF.Max(size.Y, 0f) * MathF.Max(size.Z, 0f);
                if (volume >= bestVolume) continue;          // rule 2

                bestVolume = volume;
                best = new CameraCell(instance.Path, g.GroupIndex, g.GroupName,
                                      g.IsInterior, volume, g.PortalCount);
            }
        }

        CameraGroup = best;
        CameraGroupCandidates = candidates;
    }

    /// <summary>
    /// Portal polygons as world-space triangles, for the debug draw.
    ///
    /// PLAN_10 §6 item 4: a portal whose plane or winding is wrong is obvious as
    /// a SHAPE and invisible as a number. Doorways should appear as flat quads
    /// standing in the door openings - one lying in a floor, or floating in a
    /// wall, says the transform or the vertex range is wrong before any
    /// traversal is written on top of it.
    ///
    /// Fan-triangulated from the polygon, which is correct because MOPT polygons
    /// are convex by construction. Reuses CollisionDebugRenderer.RenderHighlight
    /// rather than adding a GL path - handbook §10 records writing from scratch
    /// what already existed, twice.
    /// </summary>
    public List<Vector3> PortalDebugTriangles(bool onlyCameraWmo)
    {
        var tris = new List<Vector3>();
        string? only = onlyCameraWmo ? CameraGroup?.InstancePath : null;
        if (onlyCameraWmo && only is null) return tris;

        foreach (var instance in _instances)
        {
            if (only is not null &&
                !instance.Path.Equals(only, StringComparison.OrdinalIgnoreCase)) continue;

            var model = instance.Model;
            foreach (var portal in model.Portals)
            {
                int start = portal.StartVertex;
                int count = portal.VertexCount;
                if (count < 3 || start < 0 || start + count > model.PortalVertices.Count) continue;

                var v0 = ToWorld(model.PortalVertices[start], instance.Transform);
                for (int i = 1; i + 1 < count; i++)
                {
                    tris.Add(v0);
                    tris.Add(ToWorld(model.PortalVertices[start + i], instance.Transform));
                    tris.Add(ToWorld(model.PortalVertices[start + i + 1], instance.Transform));
                }
            }
        }
        return tris;

        static Vector3 ToWorld((float x, float y, float z) v, Matrix4x4 m)
            => Vector3.Transform(new Vector3(v.x, v.y, v.z), m);
    }

    /// <summary>
    /// Print the portal graph of every loaded WMO that has one.
    ///
    /// PLAN_10 §7 step 2: this is what turns "the traversal is wrong" into
    /// "portal 12 links group 4 to group 7 and it should not". It also runs the
    /// two integrity checks the reader only ever asserted in a comment -
    /// MOHD's NPortals against the parsed count, and every MOPR group index
    /// being in range.
    /// </summary>
    public void DumpPortalGraph()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int withPortals = 0;

        foreach (var instance in _instances)
        {
            if (!seen.Add(instance.Path)) continue;
            var model = instance.Model;
            if (model.Portals.Count == 0 && model.DeclaredPortalCount == 0) continue;
            withPortals++;

            string name = Path.GetFileName(instance.Path);
            Console.WriteLine($"[portals] {name}: {model.Portals.Count} portal(s), " +
                              $"{model.PortalRefs.Count} reference(s), " +
                              $"{model.PortalVertices.Count} vertex(es), " +
                              $"{model.Groups.Count} group(s)");

            if (model.DeclaredPortalCount != (uint)model.Portals.Count)
                Console.WriteLine($"[portals]   MISMATCH: MOHD declares {model.DeclaredPortalCount} " +
                                  $"but MOPT parsed {model.Portals.Count} - the reader's assumption " +
                                  "is broken and traversal must not be trusted");

            // MOPR group indices are FILE indices. Model.Groups is the list of
            // groups that produced a mesh, and a group that was empty, missing
            // or antiportal never got one - so the list is NOT index-aligned
            // with the file and must never be indexed by a MOPR value. Look up
            // by GroupMesh.GroupIndex, which is the file index.
            var byFileIndex = new Dictionary<int, GroupMesh>();
            foreach (var g in model.Groups) byFileIndex[g.GroupIndex] = g;

            int badRefs = 0, badPortals = 0;
            foreach (var r in model.PortalRefs)
            {
                if (!byFileIndex.ContainsKey(r.GroupIndex)) badRefs++;
                if (r.PortalIndex >= model.Portals.Count) badPortals++;
            }
            if (badRefs > 0 || badPortals > 0)
                Console.WriteLine($"[portals]   {badRefs} ref(s) point at a group with no mesh, " +
                                  $"{badPortals} portal ref(s) out of range " +
                                  "(refs with no mesh are usually fine - empty or antiportal groups)");

            foreach (var g in model.Groups)
            {
                if (g.PortalCount == 0) continue;
                var targets = new List<string>();
                for (int i = 0; i < g.PortalCount; i++)
                {
                    int idx = g.PortalStart + i;
                    if (idx < 0 || idx >= model.PortalRefs.Count) { targets.Add("?"); continue; }
                    var r = model.PortalRefs[idx];
                    string to = byFileIndex.TryGetValue(r.GroupIndex, out var target)
                        ? $"{r.GroupIndex}:{target.GroupName}"
                        : $"{r.GroupIndex}:no-mesh";
                    targets.Add($"p{r.PortalIndex}->{to}{(r.Side < 0 ? "-" : "+")}");
                }
                Console.WriteLine($"[portals]   [{g.GroupIndex,3}] '{g.GroupName}' " +
                                  $"{(g.IsInterior ? "INT" : "ext")}  {g.PortalCount} door(s): " +
                                  string.Join("  ", targets));
            }
        }

        if (withPortals == 0)
            Console.WriteLine("[portals] no loaded WMO declares any portals");
    }

    /// <summary>Distance (yd) below which an impostor shell hides even from
    /// outside (you are right on top of it). HUD-tunable. Was the hard-coded 196.</summary>
    public float ShellNearGuard { get; set; } = 196f;

    /// <summary>An ALWAYS_DRAW group under this many vertices is treated as a
    /// distance impostor regardless of its interior flag. HUD-tunable; retunes
    /// the whole city live because classification runs at draw time.</summary>
    public int ImpostorMaxVertices { get; set; } = 2000;

    /// <summary>Print the throttled [wmo-vis] line to the console. Off by default
    /// now that the same numbers live in the HUD.</summary>
    public bool VisTrace { get; set; }

    // Live visibility state for the biggest WMO in view, surfaced in the HUD so
    // the swap can be watched without console spam.
    public bool LastInsideCity { get; private set; }
    public int ShellsDrawnLastFrame { get; private set; }
    public int ShellsHiddenLastFrame { get; private set; }
    public string LargestWmoName { get; private set; } = "";
    public int LargestWmoGroupsDrawn { get; private set; }
    public int LargestWmoGroupCount { get; private set; }

    /// <summary>Collision BVH used for occlusion tests. Set by the game loop to the
    /// active world; when null, occlusion culling is skipped.</summary>
    public CollisionWorld? OcclusionWorld { get; set; }

    /// <summary>Hide exterior groups the collision BVH shows are blocked by nearer
    /// geometry (only while inside a large WMO). Off by default; tunable/toggleable.</summary>
    public bool OcclusionCulling { get; set; }

    /// <summary>Only occlusion-test exterior groups beyond this (yd) — nearby ones
    /// are cheap to just draw and shouldn't flicker.</summary>
    public float OcclusionMinDistance { get; set; } = 40f;

    /// <summary>Slack (yd) subtracted from the group distance before the occlusion
    /// ray, so grazing self-hits at the group's own face don't read as blocked.</summary>
    public float OcclusionMargin { get; set; } = 8f;

    public int OccludedGroupsLastFrame { get; private set; }

    public float DrawDistance { get; set; }
    public float VisibilityDistance { get; set; } = float.PositiveInfinity;

    /// <summary>
    /// Draw every batch two-sided, ignoring each material's IsNoCull flag.
    ///
    /// WMO winding is not consistent — that is why the collision raycast is
    /// two-sided as well. A group wound inward whose material does not set
    /// IsNoCull renders as nothing at all from outside, which reads as a
    /// building with missing walls rather than as a culling problem. Toggling
    /// this tells the two apart in one click: if the missing pieces reappear,
    /// it is winding, not lost geometry.
    /// </summary>
    public bool ForceTwoSided { get; set; } = true;

    /// <summary>Curated show/hide overrides consulted by ClassifyGroup before any
    /// heuristic. Null = none. The data ships and is honoured in release (PLAN_04).</summary>
    public VisibilityOverrides? Overrides { get; set; }

    /// <summary>
    /// Alpha below which a fragment is discarded, for textures that actually
    /// have an alpha channel. Railings and window tracery need it; a wall panel
    /// whose texture has no alpha must never be subject to it.
    /// </summary>
    public float AlphaCutoff { get; set; } = 0.35f;

    /// <summary>
    /// Light WMO interiors from their baked MOCV vertex colours instead of the
    /// outdoor sun. Off reverts to the old behaviour exactly (every batch takes
    /// the exterior path), which is the fastest way to see what MOCV is doing.
    /// </summary>
    public bool UseVertexColors { get; set; } = true;

    /// <summary>
    /// The overbright factor applied to MOCV. 2.0 is not a taste setting: the
    /// classic render path halves vertex colours at load and doubles them at
    /// draw, so the authored range is [0, 2] rather than [0, 1] and lanterns
    /// are meant to blow past white. Exposed because it is the single knob
    /// that says whether interiors are too dark or too flat.
    /// </summary>
    public float VertexColorScale { get; set; } = 2.0f;

    public Vector3 SunDirection { get; set; } = Vector3.Normalize(new Vector3(0.45f, 0.35f, 0.82f));
    public Vector3 SunColor { get; set; } = new(1.00f, 0.95f, 0.85f);
    public float SunIntensity { get; set; } = 1.15f;
    public Vector3 AmbientColor { get; set; } = new(0.42f, 0.50f, 0.60f);
    public float AmbientIntensity { get; set; } = 0.85f;
    public Vector3 FogColor { get; set; } = new(0.56f, 0.71f, 0.85f);
    public float FogStart { get; set; } = 350f;
    public float FogEnd { get; set; } = 900f;

    public WmoRenderer(
        GL gl, ClientConfig config, GpuUploadWorker uploads, AssetWorkerPool workers)
    {
        _gl = gl;
        _config = config;
        _uploads = uploads;
        _workers = workers;
        DrawDistance = Math.Max(100f, config.Render.WmoDistance);
        FogEnd = DrawDistance;
    }

    public void LoadShaders(string shaderDir)
    {
        _shader = Shader.FromFiles(_gl,
            Path.Combine(shaderDir, "wmo.vert"),
            Path.Combine(shaderDir, "wmo.frag"));
    }

    /// <summary>
    /// Read the ADTs for these tiles and place every WMO they reference.
    ///
    /// This re-reads each ADT rather than borrowing the terrain's parse. That
    /// is a real cost — startup already reads every tile twice — but it keeps
    /// buildings independent of terrain, and collapsing all the reads into one
    /// is a single obvious change once the shape settles.
    /// </summary>
    public void LoadForTiles(IEnumerable<(int col, int row)> tiles, AdtCache adts)
    {
        var started = DateTime.UtcNow;
        var pending = new List<(Model Model, AdtTerrainReader.WmoInstance Placement)>();

        foreach (var (col, row) in tiles)
        {
            var adt = adts.Get(col, row);
            if (adt?.Wmos is null) continue;

            foreach (var w in adt.Wmos)
            {
                if (string.IsNullOrWhiteSpace(w.ModelPath)) continue;
                if (w.ModelPath.StartsWith("Unknown_", StringComparison.Ordinal)) continue;

                // A building straddling a tile edge is listed in every ADT it
                // touches. Identity is the model plus its exact position.
                string key = $"{w.ModelPath}|{w.PosX:F2}|{w.PosY:F2}|{w.PosZ:F2}";
                if (!_placed.Add(key)) continue;

                var model = ResolveModel(w.ModelPath);
                if (model is null) continue;

                pending.Add((model, w));
            }
        }

        foreach (var (model, w) in pending)
        {
            var transform = BuildPlacement(w);
            var (min, max) = TransformedBounds(model, transform);

            _instances.Add(new Instance
            {
                Model = model,
                Transform = transform,
                WorldMin = min,
                WorldMax = max,
                Origin = Vector3.Transform(new Vector3(w.PosX, w.PosY, w.PosZ), PlacementToWorld),
                Path = w.ModelPath,
                DoodadSet = w.DoodadSet,
            });

            VerifyPlacement(w, min, max);
        }

        TotalTriangles = _instances.Sum(i => i.Model.TriangleCount);

        var elapsed = DateTime.UtcNow - started;
        Console.WriteLine(
            $"[wmo] {_instances.Count} placement(s), {ModelCount} model(s), {TextureCount} texture(s), " +
            $"{TotalTriangles:N0} triangles, {elapsed.TotalSeconds:F1}s");

        if (_texturelessBatches > 0)
            Console.WriteLine(
                $"[wmo] {_texturelessBatches} batch(es) have no texture in ANY material slot " +
                "- these draw as flat grey");

        if (_rescaledAlpha.Count > 0)
            Console.WriteLine(
                $"[wmo] {_rescaledAlpha.Count} texture(s) had 1-bit alpha decoded as 0/1 and were " +
                "rescaled to 0/255 — BlpDecoder should be doing this");

        if (_opaqueTextures.Count > 0)
            Console.WriteLine(
                $"[wmo] {_opaqueTextures.Count} texture(s) decoded with an all-zero alpha channel " +
                "- these are opaque and are now exempt from the alpha cut");

        if (_failedTextures.Count > 0)
        {
            Console.WriteLine($"[wmo] {_failedTextures.Count} named texture(s) failed to decode:");
            foreach (var name in _failedTextures.Take(10))
                Console.WriteLine($"[wmo]   {name}");
        }
    }

    /// <summary>
    /// Drop placed instances while retaining parsed models, textures and GPU
    /// buffers. A streaming ring change should rebuild cheap placement state,
    /// never pay the model-loading cost again.
    /// </summary>
    public void ResetPlacements()
    {
        _instances.Clear();
        _placed.Clear();
        TotalTriangles = 0;
    }

    /// <summary>
    /// Discover WMO assets in an outer tile ring without placing them. The
    /// parsed models, decoded textures and GPU buffers stay in the normal
    /// caches, so making the inner ring resident later is a cheap cache hit.
    /// </summary>
    /// <summary>
    /// Queue every WMO referenced by these tiles for background warming.
    ///
    /// NON-BLOCKING BY CONTRACT (PLAN_08 D1). This is speculative warming of the
    /// outer preload ring, so a tile whose ADT is not parsed yet is deferred and
    /// retried next frame rather than waited on.
    ///
    /// It used to call adts.Get(), which blocks on a pending parse. With 25 ring
    /// tiles and a worker pool busy with doodads, the hitch recorder measured
    /// that at 61 ms of a 187 ms tile-crossing freeze - the single largest item.
    /// Nothing warmed here is needed this frame: the buildings are two tiles
    /// away. Waiting for them was pure loss.
    /// </summary>
    public void QueuePreloadForTiles(IEnumerable<(int col, int row)> tiles, AdtCache adts)
    {
        _ringAdts = adts;
        foreach (var key in tiles) TryQueueRingTile(key, adts);
    }

    /// <summary>
    /// Enqueue one ring tile's WMOs if its ADT is already parsed; otherwise
    /// start the parse on the pool and remember the tile for a later retry.
    /// </summary>
    private void TryQueueRingTile((int col, int row) key, AdtCache adts)
    {
        if (!adts.TryPeek(key.col, key.row, out var adt))
        {
            // Kick the parse off-thread and come back to it. QueueLoad is
            // idempotent, so calling it again next frame costs nothing.
            adts.QueueLoad(key.col, key.row, _workers);
            _deferredRingTiles.Add(key);
            return;
        }

        // Authoritative answer, including "this tile has no ADT" - either way
        // the tile is finished and must not be retried forever.
        _deferredRingTiles.Remove(key);
        if (adt?.Wmos is null) return;

        foreach (var w in adt.Wmos)
        {
            string path = w.ModelPath;
            if (string.IsNullOrWhiteSpace(path) ||
                path.StartsWith("Unknown_", StringComparison.Ordinal) ||
                _models.ContainsKey(path) ||
                _preloadJob?.RootPath.Equals(path, StringComparison.OrdinalIgnoreCase) == true ||
                !_preloadQueued.Add(path)) continue;

            _preloadQueue.Enqueue(path);
        }
    }

    /// <summary>
    /// Retry ring tiles whose ADT has finished parsing since it was deferred.
    /// Cheap: a dictionary probe each, and the set empties as parses land.
    /// </summary>
    private void DrainDeferredRingTiles()
    {
        if (_ringAdts is null || _deferredRingTiles.Count == 0) return;

        // Snapshot: TryQueueRingTile mutates the set it is iterating.
        foreach (var key in _deferredRingTiles.ToArray())
            TryQueueRingTile(key, _ringAdts);
    }

    /// <summary>Warm one queued model. Runtime streaming calls this once per frame.</summary>
    public bool WarmNextPreload(bool waitForWorker = false)
    {
        // Ring tiles deferred by TryQueueRingTile get their retry here, because
        // this is already called once per frame by the streaming update.
        DrainDeferredRingTiles();

        while (_preloadJob is null && _preloadQueue.Count > 0)
        {
            string path = _preloadQueue.Dequeue();
            _preloadQueued.Remove(path);
            if (_models.ContainsKey(path)) continue;
            _preloadJob = StartModelLoad(path);
        }

        if (_preloadJob is null) return false;

        var job = _preloadJob;
        if (waitForWorker && !job.Worker.IsCompleted)
            try { job.Worker.GetAwaiter().GetResult(); } catch { }
        if (!job.Worker.IsCompleted) return true;

        var stepTimer = System.Diagnostics.Stopwatch.StartNew();
        if (StepModelLoad(job, waitForWorker))
        {
            _preloadJob = null;
            Console.WriteLine($"[wmo-preload] {Path.GetFileName(job.RootPath)} prepared over " +
                              $"{job.Ready?.Root?.NGroups ?? 0} group(s), {job.Timer.Elapsed.TotalSeconds:F2}s, " +
                              $"{_preloadQueue.Count} queued");
        }
        if (stepTimer.Elapsed.TotalMilliseconds >= 8)
            Console.WriteLine($"[stream-budget] WMO finalize {Path.GetFileName(job.RootPath)} " +
                              $"took {stepTimer.Elapsed.TotalMilliseconds:F0}ms");

        return true;
    }

    /// <summary>Warm the initial preload ring while the loading screen is expected.</summary>
    public void DrainPreloads()
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        int steps = 0;
        while (WarmNextPreload(waitForWorker: true)) steps++;
        Console.WriteLine($"[wmo-preload] initial ring completed in {steps} staged step(s), " +
                          $"{timer.Elapsed.TotalSeconds:F1}s");
    }

    /// <summary>
    /// Newly discovered embedded M2 assets from completed WMO roots. The
    /// doodad renderer consumes these into its own outer-ring preload queue;
    /// otherwise a city crossing first discovers thousands of furniture
    /// placements and pays all their model/texture costs at the boundary.
    /// </summary>
    public IEnumerable<string> TakeNewDoodadModelPaths()
    {
        while (_newDoodadModels.Count > 0)
            yield return _newDoodadModels.Dequeue();
    }

    // ── placement ────────────────────────────────────────────────────────────

    /// <summary>
    /// ADT placement space (Y-up, origin at the map corner) to WoW world space.
    /// Row-vector convention, matching System.Numerics and the way Camera
    /// uploads its matrices.
    ///
    ///     (x, y, z) -> (C - z, C - x, y)
    /// </summary>
    private static Matrix4x4 PlacementToWorld => new(
        0f, -1f, 0f, 0f,
        0f, 0f, 1f, 0f,
        -1f, 0f, 0f, 0f,
        CoordShift, CoordShift, 0f, 1f);

    /// <summary>
    /// Model vertex basis to placement basis: (x, y, z) -> (x, z, -y).
    ///
    /// Settled by calibration and then confirmed in game, so the 64-candidate
    /// search that found it is gone. Note that M2 does NOT use this — a doodad's
    /// render mesh is already Y-up and needs identity — while an M2's COLLISION
    /// hull does. Three arrays, two conventions; do not assume they agree.
    /// </summary>
    private static readonly Matrix4x4 Basis = new(
        1, 0, 0, 0,
        0, 0, -1, 0,
        0, 1, 0, 0,
        0, 0, 0, 1);

    public string ConventionName => "xz-y, heading rotY-90";

    /// <summary>
    /// Extra heading applied to every WMO, on top of whatever calibration picks.
    ///
    /// THIS IS NOT SOMETHING CALIBRATION CAN FIND, and the reason is worth
    /// keeping: an axis-aligned bounding box is INVARIANT under a 180 degree
    /// rotation about the vertical axis. Same extents, same size. The centre
    /// term barely moves either, because a WMO's local origin usually sits near
    /// the middle of its geometry. So the scorer is structurally blind to a
    /// half-turn — it can settle which axis is up, and it cannot settle which
    /// way a building faces.
    ///
    /// That part was determined by looking at Northshire: every building faced
    /// backwards, uniformly, which is the signature of a constant heading
    /// error rather than a per-model one.
    ///
    /// Do not "fix" the scoring function to catch this. It cannot, by
    /// construction.
    /// </summary>
    private const float HeadingCorrectionDegrees = 180f;

    private static Matrix4x4 BuildPlacement(AdtTerrainReader.WmoInstance w)
    {
        const float deg = MathF.PI / 180f;

        // rotY - 270 from calibration, plus the 180 that only eyes could find,
        // is rotY - 90. The bounding-box score could settle which axis is up
        // and never which way a building faces, because an AABB is invariant
        // under a half turn about the vertical.
        float heading = (w.RotY - 90f) * deg;

        // Column-vector intent: RotY(heading) * RotZ(-rotX) * RotX(rotZ).
        // System.Numerics is row-vector, so the order reverses.
        var rotation = Matrix4x4.CreateRotationX(w.RotZ * deg)
                     * Matrix4x4.CreateRotationZ(-w.RotX * deg)
                     * Matrix4x4.CreateRotationY(heading);

        return Basis
             * rotation
             * Matrix4x4.CreateTranslation(w.PosX, w.PosY, w.PosZ)
             * PlacementToWorld;
    }

    /// <summary>
    /// World bounds of a placed model, from the eight corners of each group's
    /// local box. Used for frustum culling and for the placement check.
    /// </summary>
    private static (Vector3 min, Vector3 max) TransformedBounds(Model model, Matrix4x4 m)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        foreach (var g in model.Groups)
        {
            for (int c = 0; c < 8; c++)
            {
                var corner = new Vector3(
                    (c & 1) == 0 ? g.LocalMin.X : g.LocalMax.X,
                    (c & 2) == 0 ? g.LocalMin.Y : g.LocalMax.Y,
                    (c & 4) == 0 ? g.LocalMin.Z : g.LocalMax.Z);

                var p = Vector3.Transform(corner, m);
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
        }

        return (min, max);
    }

    private static (Vector3 min, Vector3 max) TransformedBounds(GroupMesh group, Matrix4x4 m)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (int c = 0; c < 8; c++)
        {
            var corner = new Vector3(
                (c & 1) == 0 ? group.LocalMin.X : group.LocalMax.X,
                (c & 2) == 0 ? group.LocalMin.Y : group.LocalMax.Y,
                (c & 4) == 0 ? group.LocalMin.Z : group.LocalMax.Z);
            var p = Vector3.Transform(corner, m);
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        return (min, max);
    }

    private static float DistanceToBox(Vector3 p, Vector3 min, Vector3 max)
    {
        float dx = MathF.Max(MathF.Max(min.X - p.X, 0f), p.X - max.X);
        float dy = MathF.Max(MathF.Max(min.Y - p.Y, 0f), p.Y - max.Y);
        float dz = MathF.Max(MathF.Max(min.Z - p.Z, 0f), p.Z - max.Z);
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>
    /// Is the camera inside this placed WMO — inside one of its real spatial
    /// cells, not merely inside the whole model's world box? This is WoWee's
    /// findContainingGroup test: transform the camera into the model's local
    /// space and check it against each GROUP box. Distance-only shells are
    /// skipped because a shell's box is huge and does not mean you are indoors;
    /// counting it made the whole-instance box swallow the approach bridge and
    /// suppress the impostor everywhere. Streets, rooms and building shells give
    /// a boundary near the gate: on the bridge you are inside none of them, in
    /// the Trade District you are inside a street cell.
    /// </summary>
    private bool CameraInsideInstance(Instance instance, Vector3 cameraWorld)
    {
        if (!Matrix4x4.Invert(instance.Transform, out var inv)) return false;
        var local = Vector3.Transform(cameraWorld, inv);
        float m = InsideInstanceMargin;
        foreach (var g in instance.Model.Groups)
        {
            if (g.IsDistanceLod) continue;
            if (local.X >= g.LocalMin.X - m && local.X <= g.LocalMax.X + m &&
                local.Y >= g.LocalMin.Y - m && local.Y <= g.LocalMax.Y + m &&
                local.Z >= g.LocalMin.Z - m && local.Z <= g.LocalMax.Z + m)
                return true;
        }
        return false;
    }

    /// <summary>
    /// The single visibility decision for one WMO group: returns exactly one
    /// WmoReasonCode. Render() switches on it to decide drawing; the read-only
    /// explanation used by the picker and scene dump calls this SAME method, so the
    /// reason reported can never drift from the reason acted on. Toggle state is read
    /// from this renderer's own properties; only the per-frame/per-group varying data
    /// is passed in. See PLAN_03_REASON_CODES.md.
    /// </summary>
    private WmoReasonCode ClassifyGroup(
        Instance instance, GroupMesh group, Vector3 groupMin, Vector3 groupMax, in FrameCullContext ctx)
    {
        // Curated overrides win over every heuristic below (PLAN_04). A hide is
        // absolute; a show bypasses the heuristic culls but still respects residency
        // (only resident groups reach here) and frustum. This is the pragmatic fix
        // for cases the heuristics cannot get right - e.g. the thief01 entrance keep
        // that stays visible across the open courtyard.
        bool forceShow = false;
        if (Overrides is not null)
        {
            var ov = Overrides.Resolve(instance.Path, group.GroupIndex, ctx.CameraInside);
            if (ov == WmoReasonCode.OverrideHide) return WmoReasonCode.OverrideHide;
            if (ov == WmoReasonCode.OverrideShow) forceShow = true;
        }

        float groupDistance = DistanceToBox(ctx.CameraPosition, groupMin, groupMax);
        bool shell = false;

        if (!forceShow)
        {
            if (UseDistanceLodShells && group.IsDistanceLod)
            {
                var groupCentre = (groupMin + groupMax) * 0.5f;
                if (ctx.CameraInside ||
                    Vector3.DistanceSquared(ctx.CameraPosition, groupCentre) < ShellNearGuard * ShellNearGuard)
                    return WmoReasonCode.ShellNearSuppressed;
                shell = true;
            }
            else if (group.IsInterior)
            {
                if (!ctx.CameraInside && groupDistance > InteriorCullDistance)
                    return WmoReasonCode.InteriorCull;
            }

            if (groupDistance > ctx.EffectiveDrawDistance)
                return WmoReasonCode.DistanceCulled;
        }

        if (FrustumCulling &&
            !Camera.BoxInFrustum(ctx.ViewProjection,
                groupMin - ctx.CameraPosition, groupMax - ctx.CameraPosition))
            return WmoReasonCode.FrustumCulled;

        if (!forceShow && OcclusionCulling && OcclusionWorld is not null
            && !group.IsDistanceLod
            && groupDistance > OcclusionMinDistance
            && IsOccluded(ctx.CameraPosition, groupMin, groupMax))
            return WmoReasonCode.OcclusionCulled;

        if (forceShow) return WmoReasonCode.OverrideShow;
        return shell ? WmoReasonCode.DrawnShellFar : WmoReasonCode.Drawn;
    }

    /// <summary>
    /// Is the group's nearest point blocked from the camera by nearer solid
    /// geometry, per the collision BVH? One ray at the group's closest box point,
    /// stopped a margin short of it, so a hit means something else is in front.
    /// A coarse single-ray test — good enough to drop the entrance towers seen
    /// through a district, not a full occlusion pass.
    /// </summary>
    private bool IsOccluded(Vector3 camera, Vector3 groupMin, Vector3 groupMax)
    {
        // Cull only when EVERY corner of the group's box is blocked by nearer
        // solid geometry. A single clear corner (a roof edge against the sky, a
        // tower top over a building) keeps the whole group visible — so this
        // hides a structure only when it is genuinely fully behind something,
        // never when it is merely partly obscured.
        for (int c = 0; c < 8; c++)
        {
            var corner = new Vector3(
                (c & 1) == 0 ? groupMin.X : groupMax.X,
                (c & 2) == 0 ? groupMin.Y : groupMax.Y,
                (c & 4) == 0 ? groupMin.Z : groupMax.Z);

            var to = corner - camera;
            float dist = to.Length();
            if (dist <= OcclusionMargin) return false;
            if (!OcclusionWorld!.Raycast(camera, to, dist - OcclusionMargin).HasValue)
                return false;   // this corner is clear -> group is visible
        }
        return true;   // all eight corners blocked
    }

    /// <summary>
    /// MODF's own bounding box for the placed model, in world space. This is
    /// the target every candidate is scored against.
    /// </summary>
    private static (Vector3 min, Vector3 max) ModfBoxInWorld(AdtTerrainReader.WmoInstance w)
    {
        var a = Vector3.Transform(new Vector3(w.BbMinX, w.BbMinY, w.BbMinZ), PlacementToWorld);
        var b = Vector3.Transform(new Vector3(w.BbMaxX, w.BbMaxY, w.BbMaxZ), PlacementToWorld);
        return (Vector3.Min(a, b), Vector3.Max(a, b));
    }

    /// <summary>
    /// Per-placement sanity check. Compares centre and size
    /// against MODF's box for the placed model.
    ///
    /// Bounding boxes are padded, so a few yards of size disagreement is normal
    /// and only the centre is really diagnostic. This exists to catch the one
    /// building that lands somewhere else entirely while the rest are fine —
    /// which a single global convention cannot fix and is worth knowing about.
    /// </summary>
    private static void VerifyPlacement(AdtTerrainReader.WmoInstance w, Vector3 min, Vector3 max)
    {
        var (mMin, mMax) = ModfBoxInWorld(w);

        var centreError = ((min + max) * 0.5f) - ((mMin + mMax) * 0.5f);
        float error = centreError.Length();

        if (error < 15f) return;

        Console.WriteLine(
            $"[wmo] OFF-CENTRE {Path.GetFileName(w.ModelPath)}: geometry centre " +
            $"({(min.X + max.X) * 0.5f:F0},{(min.Y + max.Y) * 0.5f:F0},{(min.Z + max.Z) * 0.5f:F0}) " +
            $"vs MODF centre ({(mMin.X + mMax.X) * 0.5f:F0},{(mMin.Y + mMax.Y) * 0.5f:F0}," +
            $"{(mMin.Z + mMax.Z) * 0.5f:F0}), {error:F1} yd apart");
    }

    // ── loading ──────────────────────────────────────────────────────────────

    private Model? ResolveModel(string rootPath)
    {
        if (_models.TryGetValue(rootPath, out var cached)) return cached;

        // If the requested model is already being staged, finish that same job
        // rather than starting a duplicate. With the outer-ring lead this path
        // should be rare, but correctness wins if the player moves unusually
        // quickly or teleports.
        ModelLoadJob? job = _preloadJob?.RootPath.Equals(rootPath, StringComparison.OrdinalIgnoreCase) == true
            ? _preloadJob
            : StartModelLoad(rootPath);
        if (job is null) return null;

        if (!job.Worker.IsCompleted)
            try { job.Worker.GetAwaiter().GetResult(); } catch { }
        while (!StepModelLoad(job, waitForUpload: true)) { }
        if (ReferenceEquals(job, _preloadJob)) _preloadJob = null;
        return _models.GetValueOrDefault(rootPath);
    }

    private ModelLoadJob? StartModelLoad(string rootPath)
    {
        if (_models.ContainsKey(rootPath)) return null;
        return new ModelLoadJob
        {
            RootPath = rootPath,
            Worker = _workers.Run(() => PrepareWmo(rootPath)),
        };
    }

    private PreparedWmo PrepareWmo(string rootPath)
    {
        var prepared = new PreparedWmo();
        var rootBytes = AdtTerrainReader.ReadFileFromMpqs(_config.ClientDataPath, rootPath);
        if (rootBytes is null)
        {
            prepared.MissingRoot = true;
            return prepared;
        }

        var root = WmoReader.ParseRoot(rootBytes);
        if (root is null) return prepared;
        prepared.Root = root;

        string stem = rootPath[..^4];
        for (int g = 0; g < (int)root.NGroups; g++)
        {
            string groupPath = $"{stem}_{g:D3}.wmo";
            var groupBytes = AdtTerrainReader.ReadFileFromMpqs(_config.ClientDataPath, groupPath);
            if (groupBytes is null)
            {
                prepared.MissingFiles++;
                prepared.Groups.Add(null);
                continue;
            }

            var group = WmoReader.ParseGroup(groupBytes, root.Flags);
            if (group is null)
            {
                prepared.Unparsed++;
                prepared.Groups.Add(null);
                continue;
            }
            if (g < root.GroupInfos.Count)
                group.GroupName = root.GroupInfos[g].Name;
            if (group.IsAntiportal)
            {
                prepared.Antiportal++;
                prepared.Groups.Add(null);
                continue;
            }
            if (group.Vertices.Count == 0 || group.Indices.Count < 3)
            {
                prepared.Empty++;
                prepared.Groups.Add(null);
                continue;
            }
            prepared.Groups.Add(group);
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var material in root.Materials)
        foreach (string texturePath in new[]
                 { material.Texture0Name, material.Texture1Name, material.Texture2Name })
        {
            if (string.IsNullOrWhiteSpace(texturePath) || !seen.Add(texturePath)) continue;
            var decoded = AdtTerrainReader.ReadBlpPixels(_config.ClientDataPath, texturePath);
            if (decoded is null)
            {
                prepared.Textures.Add(new PreparedTexture { Path = texturePath });
                continue;
            }

            var (bgra, width, height) = decoded.Value;
            byte maxAlpha = 0;
            for (int i = 3; i < bgra.Length; i += 4)
            {
                if (bgra[i] > maxAlpha) maxAlpha = bgra[i];
                if (maxAlpha > 1) break;
            }
            if (maxAlpha == 1)
                for (int i = 3; i < bgra.Length; i += 4)
                    if (bgra[i] != 0) bgra[i] = 255;

            prepared.Textures.Add(new PreparedTexture
            {
                Path = texturePath,
                Bgra = bgra,
                Width = width,
                Height = height,
                MaxAlpha = maxAlpha,
            });
        }

        return prepared;
    }

    private unsafe UploadedWmo UploadPreparedWmo(
        GL gl, PreparedWmo prepared, IReadOnlyList<PreparedTexture> textures)
    {
        var uploaded = new UploadedWmo();

        foreach (var texture in textures)
        {
            uploaded.Textures[texture.Path] = texture.Bgra is null
                ? null
                : Texture.From2D(gl, texture.Bgra, texture.Width, texture.Height, ownerGl: _gl);
        }

        foreach (var group in prepared.Groups)
        {
            if (group is null)
            {
                uploaded.Groups.Add(null);
                continue;
            }

            var vertices = BuildGroupVertexArray(group, out _, out _);
            var indices = group.Indices.ToArray();
            var gpu = new UploadedGroup
            {
                Vbo = gl.GenBuffer(),
                Ebo = gl.GenBuffer(),
            };

            gl.BindBuffer(BufferTargetARB.ArrayBuffer, gpu.Vbo);
            fixed (float* p = vertices)
                gl.BufferData(BufferTargetARB.ArrayBuffer,
                    (nuint)(vertices.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);

            gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, gpu.Ebo);
            fixed (ushort* p = indices)
                gl.BufferData(BufferTargetARB.ElementArrayBuffer,
                    (nuint)(indices.Length * sizeof(ushort)), p, BufferUsageARB.StaticDraw);

            uploaded.Groups.Add(gpu);
        }

        return uploaded;
    }

    /// <summary>
    /// Build at most one WMO group. Returns true once the model is finalized.
    /// Group granularity keeps runtime preload work bounded while the asset is
    /// still beyond the fog boundary.
    /// </summary>
    private bool StepModelLoad(ModelLoadJob job, bool waitForUpload = false)
    {
        try { job.Ready ??= job.Worker.GetAwaiter().GetResult(); }
        catch (Exception ex)
        {
            Console.WriteLine($"[wmo-preload] {Path.GetFileName(job.RootPath)} failed - {ex.Message}");
            job.Model.Dispose();
            _models[job.RootPath] = null;
            return true;
        }
        var ready = job.Ready;
        if (ready.MissingRoot)
        {
            Console.WriteLine($"[wmo] not in MPQs: {job.RootPath}");
            _models[job.RootPath] = null;
            return true;
        }
        if (ready.Root is null)
        {
            Console.WriteLine($"[wmo] failed to parse root: {job.RootPath}");
            _models[job.RootPath] = null;
            return true;
        }

        job.MissingFiles = ready.MissingFiles;
        job.Unparsed = ready.Unparsed;
        job.Empty = ready.Empty;
        job.Antiportal = ready.Antiportal;

        // Carry the portal graph onto the Model before `ready` goes out of
        // scope. Copied by reference: the arrays are immutable after parse and
        // one WMO root is shared by every placement of it.
        job.Model.PortalVertices = ready.Root.PortalVertices;
        job.Model.Portals = ready.Root.Portals;
        job.Model.PortalRefs = ready.Root.PortalRefs;
        job.Model.DeclaredPortalCount = ready.Root.NPortals;

        if (job.Upload is null)
        {
            var pendingTextures = ready.Textures
                .Where(t => !_textures.ContainsKey(t.Path))
                .ToList();
            job.Upload = _uploads.Enqueue(Path.GetFileName(job.RootPath), uploadGl =>
                UploadPreparedWmo(uploadGl, ready, pendingTextures));
        }
        if (waitForUpload && !job.Upload.IsCompleted)
            try { job.Upload.GetAwaiter().GetResult(); } catch { }
        if (!job.Upload.IsCompleted) return false;

        UploadedWmo uploaded;
        try { uploaded = job.Upload.GetAwaiter().GetResult(); }
        catch (Exception ex)
        {
            Console.WriteLine($"[wmo-upload] {Path.GetFileName(job.RootPath)} failed - {ex.Message}");
            job.Model.Dispose();
            _models[job.RootPath] = null;
            return true;
        }

        if (!job.UploadAccepted)
        {
            foreach (var (path, texture) in uploaded.Textures)
            {
                if (!_textures.ContainsKey(path)) _textures[path] = texture;
                var prepared = ready.Textures.First(t =>
                    t.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
                if (texture is null) _failedTextures.Add(path);
                else if (prepared.MaxAlpha == 0) _opaqueTextures.Add(path);
                else if (prepared.MaxAlpha == 1) _rescaledAlpha.Add(path);
            }
            job.UploadAccepted = true;
        }

        if (job.NextGroup < ready.Groups.Count)
        {
            int groupIndex = job.NextGroup++;
            var group = ready.Groups[groupIndex];
            if (group is null) return false;

            bool isDistanceLod = IsDistanceOnlyLod(
                job.RootPath, ready.Root.NGroups, group.GroupFlags,
                group.Vertices.Count, group.GroupName);
            var mesh = BuildGroupMesh(
                group, ready.Root, isDistanceLod, groupIndex, uploaded.Groups[groupIndex]);
            if (mesh is null) { job.Unbuilt++; return false; }

            job.BatchesDropped += _lastBatchesDropped;
            job.IndicesCovered += _lastIndicesCovered;
            job.IndicesTotal += _lastIndicesTotal;

            job.Model.Groups.Add(mesh);
            job.Model.TriangleCount += group.Indices.Count / 3;

            CollectCollision(group, job.Collision, ref job.Skipped);
            return false;
        }

        var model = job.Model;
        model.CollisionTriangles = [.. job.Collision];
        model.CollisionSkipped = job.Skipped;

        if (job.IndicesTotal > 0 && job.IndicesCovered < job.IndicesTotal)
        {
            // Under 100% is EXPECTED, not a fault: MOPY marks collision-only
            // triangles (materialId 0xFF) that are solid and never drawn, and
            // they are not in any MOBA batch. Every model measured sits around
            // 90%, which is the invisible-wall geometry. Only a sharp outlier
            // would mean anything.
            double covered = 100.0 * job.IndicesCovered / job.IndicesTotal;
            if (covered < 70.0)
                Console.WriteLine(
                    $"[wmo] {Path.GetFileName(job.RootPath)}: MOBA batches draw only {covered:F1}% of " +
                    $"indices — unusually low, most models sit near 90%");
        }

        if (job.MissingFiles + job.Unparsed + job.Empty + job.Antiportal +
            job.Unbuilt + job.BatchesDropped > 0)
        {
            Console.WriteLine(
                $"[wmo] {Path.GetFileName(job.RootPath)}: {model.Groups.Count}/{ready.Root.NGroups} group(s) drawn" +
                (job.MissingFiles > 0 ? $", {job.MissingFiles} file(s) not in MPQ" : "") +
                (job.Unparsed > 0 ? $", {job.Unparsed} failed to parse" : "") +
                (job.Empty > 0 ? $", {job.Empty} empty" : "") +
                (job.Antiportal > 0 ? $", {job.Antiportal} antiportal skipped" : "") +
                (job.Unbuilt > 0 ? $", {job.Unbuilt} mesh build failed" : "") +
                (job.BatchesDropped > 0 ? $", {job.BatchesDropped} batch(es) dropped" : ""));
        }

        if (model.Groups.Count == 0)
        {
            Console.WriteLine($"[wmo] no drawable groups: {job.RootPath}");
            model.Dispose();
            _models[job.RootPath] = null;
            return true;
        }

        model.DoodadSets = ready.Root.DoodadSets;
        model.Doodads = ready.Root.Doodads;
        model.DoodadLight = BuildDoodadLighting(ready);

        int lodShells = model.Groups.Count(g => g.IsDistanceLod);
        if (lodShells > 0)
            Console.WriteLine($"[wmo-lod] {Path.GetFileName(job.RootPath)}: " +
                              $"{lodShells} distance-only shell group(s)");

        // One-time structural dump for large city WMOs. Identifies exactly which
        // group is that blue entrance roof / the cathedral shell so the right
        // ones can be classified as approach-only LOD instead of guessing. Local
        // centre lets a group be matched to what is on screen.
        if (DumpLargeWmoGroups && ready.Root.NGroups > 50)
        {
            Console.WriteLine($"[wmo-groups] {Path.GetFileName(job.RootPath)}: " +
                              $"{ready.Root.NGroups} group(s) [idx 'name' flags int/ext LOD verts localCentre]");
            for (int gi = 0; gi < ready.Groups.Count; gi++)
            {
                var g = ready.Groups[gi];
                if (g is null) continue;
                bool lod = IsDistanceOnlyLod(
                    job.RootPath, ready.Root.NGroups, g.GroupFlags,
                    g.Vertices.Count, g.GroupName);
                float cx = (g.BbMinX + g.BbMaxX) * 0.5f;
                float cy = (g.BbMinY + g.BbMaxY) * 0.5f;
                float cz = (g.BbMinZ + g.BbMaxZ) * 0.5f;
                Console.WriteLine(
                    $"[wmo-groups]  [{gi,3}] '{g.GroupName}' 0x{g.GroupFlags:X8} " +
                    $"{(g.IsInterior ? "INT" : "ext")}{(lod ? " LOD" : "")} " +
                    $"v={g.Vertices.Count} c=({cx:F0},{cy:F0},{cz:F0})");
            }
        }

        _models[job.RootPath] = model;
        return true;
    }

    /// <summary>
    /// Pull the collidable triangles out of a group.
    ///
    /// MOPY carries one (flags, materialId) pair per triangle. The pieces that
    /// matter here:
    ///
    ///   flags &amp; 0x04  F_DETAIL - decorative geometry the real client does not
    ///                  collide against. Excluding it is what stops you bumping
    ///                  into mouldings and window tracery.
    ///   materialId 0xFF - a collision-only triangle: solid but never drawn.
    ///                  These are how invisible walls and door blockers exist.
    ///
    /// So MOBA batches decide what gets DRAWN and MOPY decides what is SOLID,
    /// out of one vertex array. That is exactly how the 1.12 client does it,
    /// and why it never needed a separate collision file.
    /// </summary>
    private static void CollectCollision(WmoGroupData group, List<Vector3> into, ref int skipped)
    {
        int triangles = group.Indices.Count / 3;

        for (int t = 0; t < triangles; t++)
        {
            // A group without MOPY is treated as fully solid rather than
            // silently non-collidable: walking through a building is a much
            // worse failure than colliding with a moulding.
            if (t < group.TriMaterials.Count)
            {
                var (flags, _) = group.TriMaterials[t];
                if ((flags & 0x04) != 0) { skipped++; continue; }
            }

            int i0 = group.Indices[t * 3];
            int i1 = group.Indices[t * 3 + 1];
            int i2 = group.Indices[t * 3 + 2];

            if (i0 >= group.Vertices.Count || i1 >= group.Vertices.Count || i2 >= group.Vertices.Count)
                continue;

            var a = group.Vertices[i0];
            var b = group.Vertices[i1];
            var c = group.Vertices[i2];

            into.Add(new Vector3(a.x, a.y, a.z));
            into.Add(new Vector3(b.x, b.y, b.z));
            into.Add(new Vector3(c.x, c.y, c.z));
        }
    }

    /// <summary>
    /// Feed every placed building's collidable triangles into a collision
    /// world, using the SAME transform the renderer draws with.
    /// </summary>
    /// <summary>
    /// Record each placed building as a reference to its immutable collision
    /// geometry plus a transform. Does NOT expand triangles - that is the whole
    /// point (see CollisionBatch). Cheap enough to run on the render thread
    /// every time collision needs rebuilding.
    /// </summary>
    public int SnapshotCollision(List<CollisionBatch> into)
    {
        int placed = 0;

        foreach (var instance in _instances)
        {
            var tris = instance.Model.CollisionTriangles;
            if (tris.Length < 3) continue;

            into.Add(new CollisionBatch(
                tris, instance.Transform, instance.Path, instance.Model.CollisionSkipped));
            placed++;
        }

        return placed;
    }

    public void AppendCollision(CollisionWorld world)
    {
        int placed = 0, triangles = 0, skipped = 0;

        foreach (var instance in _instances)
        {
            var tris = instance.Model.CollisionTriangles;
            if (tris.Length < 3) continue;

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

            skipped += instance.Model.CollisionSkipped;
            placed++;
        }

        Console.WriteLine(
            $"[collision] from client geometry: {placed} building(s), " +
            $"{triangles:N0} solid triangles, {skipped:N0} detail triangles excluded");
    }

    /// <summary>Batches discarded by the most recent BuildGroupMesh call.</summary>
    private int _lastBatchesDropped;

    /// <summary>
    /// Indices the last group's batches actually draw, against how many it has.
    ///
    /// If MOBA does not cover the whole index buffer then some triangles are
    /// never submitted at all — which looks exactly like missing walls, loads
    /// without a single warning, and is invisible to any texture or culling
    /// theory. Worth measuring before guessing again.
    /// </summary>
    private int _lastIndicesCovered, _lastIndicesTotal;

    private unsafe GroupMesh? BuildGroupMesh(
        WmoGroupData group, WmoRootData root, bool isDistanceLod, int groupIndex,
        UploadedGroup? uploaded = null)
    {
        var vertices = BuildGroupVertexArray(group, out var min, out var max);

        var indices = group.Indices.ToArray();

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
        // Location 7, not 3: locations 3-6 are the four rows of the instancing
        // matrix and are claimed even when instancing is off for this draw.
        _gl.EnableVertexAttribArray(7);
        _gl.VertexAttribPointer(7, 4, VertexAttribPointerType.Float, false, stride, (void*)(8 * sizeof(float)));

        _gl.BindVertexArray(0);

        var mesh = new GroupMesh
        {
            Vao = vao,
            Vbo = vbo,
            Ebo = ebo,
            LocalMin = min,
            LocalMax = max,
            IsInterior = group.IsInterior,
            IsDistanceLod = isDistanceLod,
            GroupIndex = groupIndex,
            GroupName = group.GroupName,
            GroupFlags = group.GroupFlags,
            PortalStart = group.PortalStart,
            PortalCount = group.PortalCount,
            VertexCount = group.Vertices.Count,
            PickPositions = BuildPickPositions(group),
            PickIndices = [.. group.Indices.Select(i => (int)i)],
        };
        mesh.Attach(_gl);

        // MOBA batches: each is a run of indices sharing one material.
        _lastBatchesDropped = 0;
        _lastIndicesCovered = 0;
        _lastIndicesTotal = indices.Length;

        // MOBA is ordered transparent, then interior, then exterior, and the
        // three MOGP counts say where the boundaries are. When they do not add
        // up to the table we actually got, trust nothing and light the whole
        // group the old way rather than guessing a boundary.
        bool hasColors = group.VertexColors.Length >= group.Vertices.Count * 4;
        int transEnd = group.TransBatchCount;
        int intEnd = transEnd + group.IntBatchCount;
        bool countsUsable = hasColors
            && intEnd + group.ExtBatchCount == group.Batches.Count
            && group.Batches.Count > 0;

        for (int bi = 0; bi < group.Batches.Count; bi++)
        {
            var b = group.Batches[bi];
            if (b.IndexCount == 0) { _lastBatchesDropped++; continue; }
            if (b.IndexStart + b.IndexCount > (uint)indices.Length) { _lastBatchesDropped++; continue; }

            var material = b.MaterialId < root.Materials.Count ? root.Materials[b.MaterialId] : null;
            var texture = material is null ? null : ResolveMaterialTexture(material);

            if (texture is null) _texturelessBatches++;

            _lastIndicesCovered += (int)b.IndexCount;

            mesh.Batches.Add(new Batch
            {
                IndexStart = b.IndexStart,
                IndexCount = b.IndexCount,
                Texture = texture,
                TwoSided = material?.IsNoCull ?? false,
                // WoWee follows MOMT, rather than guessing from BLP contents:
                // mode 0 is opaque, mode 1 is alpha-key, modes 2+ blend.
                // Cutting every texture that merely contains alpha is what
                // turned ordinary walls and roofs into torn paper.
                AlphaTest = material?.BlendMode == 1 && MaterialHasAlpha(material),
                Transparent = material?.BlendMode >= 2,
                Type = !countsUsable ? 3 : bi < transEnd ? 1 : bi < intEnd ? 2 : 3,
            });
        }

        // No batches means nothing references this geometry — draw it whole
        // rather than dropping it, so a WMO with an odd MOBA table still shows.
        if (mesh.Batches.Count == 0)
        {
            mesh.Batches.Add(new Batch
            {
                IndexStart = 0,
                IndexCount = (uint)indices.Length,
                Texture = null,
                TwoSided = true,
            });
        }

        return mesh;
    }

    /// <summary>
    /// Whether a group is a distance-only impostor shell. Evaluated live at draw
    /// time (not baked at load) so the HUD's "Impostor max verts" slider retunes
    /// the whole city without a reload.
    ///
    /// The real distance impostors turned out to be flagged INTERIOR, which the
    /// old !indoor guards threw away. The Stormwind group dump proved it:
    ///   [302] 'Cathedral of Light' 0x00012841 INT v=96   (ALWAYS_DRAW set)
    ///   [303] 'Taventrance16'      0x00012841 INT v=48
    ///   [304] 'Old Town'           0x00012841 INT v=48
    /// These are the "double cathedral" / distant-district silhouettes Blizzard
    /// shows on approach. The signal is ALWAYS_DRAW (0x10000) + a low vertex
    /// count, NOT the interior/exterior bit: a genuine interior room is never
    /// ALWAYS_DRAW. So an ALWAYS_DRAW low-poly group is a shell regardless of
    /// the indoor flag; the detailed versions (e.g. 'Taventrance15' v=2368, no
    /// ALWAYS_DRAW) are untouched.
    /// </summary>
    private bool IsDistanceOnlyLod(
        string rootPath, uint nGroups, uint flags, int vertexCount, string groupName)
    {
        if (nGroups <= 50) return false;

        bool indoor = (flags & 0x2000) != 0;
        bool alwaysDraw = (flags & 0x00010000) != 0;
        string name = (groupName ?? "").ToLowerInvariant();
        bool facade = name.Contains("facade", StringComparison.Ordinal);
        bool cityShell = name.StartsWith("city", StringComparison.Ordinal) && name.Length <= 8;
        bool stormwind = Path.GetFileName(rootPath)
            .Equals("stormwind.wmo", StringComparison.OrdinalIgnoreCase);
        bool cathedralShell = stormwind && indoor && (flags & 0x80) != 0;
        bool alwaysDrawImpostor = alwaysDraw && vertexCount < ImpostorMaxVertices;

        return (vertexCount < 100 && !indoor)
            || (alwaysDraw && vertexCount < 5000 && !indoor)
            || alwaysDrawImpostor
            || (facade && !indoor)
            || (cityShell && !indoor)
            || cathedralShell;
    }

    private static float[] BuildGroupVertexArray(
        WmoGroupData group, out Vector3 min, out Vector3 max)
    {
        var vertices = new float[group.Vertices.Count * FloatsPerVertex];
        min = new Vector3(float.MaxValue);
        max = new Vector3(float.MinValue);

        for (int i = 0; i < group.Vertices.Count; i++)
        {
            var v = group.Vertices[i];
            var n = i < group.Normals.Count ? group.Normals[i] : (x: 0f, y: 0f, z: 1f);
            var uv = i < group.UVs.Count ? group.UVs[i] : (u: 0f, v: 0f);

            // No MOCV: 0.5 is neutral, because the render path doubles it.
            // Those groups are classified exterior anyway, so the shader never
            // reads this - it just must not be black if something slips through.
            int c = i * 4;
            bool hasColor = c + 3 < group.VertexColors.Length;
            float cr = hasColor ? group.VertexColors[c + 0] / 255f : 0.5f;
            float cg = hasColor ? group.VertexColors[c + 1] / 255f : 0.5f;
            float cb = hasColor ? group.VertexColors[c + 2] / 255f : 0.5f;
            float ca = hasColor ? group.VertexColors[c + 3] / 255f : 1.0f;

            int o = i * FloatsPerVertex;
            vertices[o + 0] = v.x;
            vertices[o + 1] = v.y;
            vertices[o + 2] = v.z;
            vertices[o + 3] = n.x;
            vertices[o + 4] = n.y;
            vertices[o + 5] = n.z;
            vertices[o + 6] = uv.u;
            vertices[o + 7] = uv.v;
            vertices[o + 8] = cr;
            vertices[o + 9] = cg;
            vertices[o + 10] = cb;
            vertices[o + 11] = ca;

            var p = new Vector3(v.x, v.y, v.z);
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }

        return vertices;
    }

    /// <summary>
    /// A material's diffuse texture, trying every slot it has.
    ///
    /// MOMT carries three texture offsets. Slot 0 is the diffuse on most
    /// materials, but not all — and reading only slot 0 meant any material that
    /// leaves it empty rendered with the shader's untextured fallback colour.
    /// That is what the pale patches all over the Goldshire inn were: not
    /// missing geometry, not missing files, just a texture nobody asked for.
    /// </summary>
    private Texture? ResolveMaterialTexture(WmoMaterial material)
        => ResolveTexture(material.Texture0Name)
        ?? ResolveTexture(material.Texture1Name)
        ?? ResolveTexture(material.Texture2Name);

    private Texture? ResolveTexture(string blpPath)
    {
        if (string.IsNullOrWhiteSpace(blpPath)) return null;
        if (_textures.TryGetValue(blpPath, out var cached)) return cached;

        var decoded = AdtTerrainReader.ReadBlpPixels(_config.ClientDataPath, blpPath);
        if (decoded is null)
        {
            // Collected rather than printed: one line per failure buries the
            // pattern, and the pattern is the useful part.
            _failedTextures.Add(blpPath);
            _textures[blpPath] = null;
            return null;
        }

        var (bgra, w, h) = decoded.Value;

        // ALPHA SANITY, and the reason the buildings were full of holes.
        //
        // The shader discards fragments below a cutoff, which is how railings
        // and window tracery work. But some BLPs come back from the decoder
        // with an alpha channel that is not on a 0..255 scale: a 1-bit alpha
        // texture decoded as 0 or 1 gives 0.004 in the shader, which fails a
        // 0.35 cut on EVERY texel. The wall loads, textures correctly, and
        // renders as nothing at all.
        //
        // So look at what actually came back:
        //   max == 0        no alpha channel — mark opaque, never cut it
        //   max == 1        1-bit alpha decoded to 0/1 — rescale to 0/255 so
        //                   the cutout works as intended instead of erasing
        //                   the surface
        //   otherwise       a real 8-bit alpha channel, use it as is
        //
        // The proper fix belongs in BlpDecoder, which should be emitting 0/255
        // for 1-bit alpha in the first place. This is a guard at the point of
        // use, not a substitute for that.
        byte maxAlpha = 0;
        for (int i = 3; i < bgra.Length; i += 4)
        {
            if (bgra[i] > maxAlpha) maxAlpha = bgra[i];
            if (maxAlpha > 1) break;
        }

        if (maxAlpha == 0)
        {
            _opaqueTextures.Add(blpPath);
        }
        else if (maxAlpha == 1)
        {
            for (int i = 3; i < bgra.Length; i += 4)
                if (bgra[i] != 0) bgra[i] = 255;
        }

        UploadPreparedTexture(new PreparedTexture
        {
            Path = blpPath,
            Bgra = bgra,
            Width = w,
            Height = h,
            MaxAlpha = maxAlpha,
        });
        return _textures.GetValueOrDefault(blpPath);
    }

    private void UploadPreparedTexture(PreparedTexture prepared)
    {
        if (_textures.ContainsKey(prepared.Path)) return;
        if (prepared.Bgra is null)
        {
            _failedTextures.Add(prepared.Path);
            _textures[prepared.Path] = null;
            return;
        }

        if (prepared.MaxAlpha == 0) _opaqueTextures.Add(prepared.Path);
        else if (prepared.MaxAlpha == 1) _rescaledAlpha.Add(prepared.Path);

        _textures[prepared.Path] = Texture.From2D(
            _gl, prepared.Bgra, prepared.Width, prepared.Height);
    }

    /// <summary>Does the material's resolved texture carry a real alpha channel?</summary>
    private bool MaterialHasAlpha(WmoMaterial? material)
    {
        if (material is null) return false;

        foreach (var name in new[] { material.Texture0Name, material.Texture1Name, material.Texture2Name })
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (!_textures.TryGetValue(name, out var t) || t is null) continue;
            return !_opaqueTextures.Contains(name);
        }

        return false;
    }

    /// <summary>
    /// Inverse of <see cref="Basis"/>: placement space back into WMO local space.
    ///
    /// An embedded doodad is an M2, and M2 render vertices are Y-up while WMO
    /// vertices are Z-up. MODD positions live in WMO space, so the M2 has to be
    /// rotated INTO that space before the MODD transform, and the building's
    /// own Basis then carries the whole assembly back out. Basis composed with
    /// this is the identity, which is the check that it is the right matrix.
    /// </summary>
    private static readonly Matrix4x4 M2ToWmo = new(
        1, 0, 0, 0,
        0, 0, 1, 0,
        0, -1, 0, 0,
        0, 0, 0, 1);

    /// <summary>
    /// "No baked light, full daylight" — the encoding a doodad gets when it is
    /// not owned by an interior group, and the value OpenGL itself supplies for
    /// a disabled vertex attribute. Terrain doodads therefore need no special
    /// case: they land on this by default and render exactly as before.
    /// </summary>
    private static readonly Vector4 ExteriorDoodadLight = new(0f, 0f, 0f, 1f);

    /// <summary>
    /// Resolve each doodad's baked interior light from MODD.color, gated by
    /// which group owns it (MODR).
    ///
    /// MODD.color is NOT a tint. The wiki calls it one; measuring says
    /// otherwise. Sampling the group's own MOCV floor directly beneath each
    /// doodad and correlating that against the shipped MODD.color gives
    /// r = 0.89 over 4929 channel samples, and the ratio sits at ~1.0 across
    /// every brightness bucket — MODD.color lives on the same scale as RAW
    /// MOCV. Two instances of the SAME model in the same building carry
    /// different colours (BARREL01 as (60,60,60) in one room and (114,113,110)
    /// in another), which a tint could not do. It is Blizzard's own pre-baked
    /// answer to "how lit is this prop", computed once at map-compile time.
    ///
    /// Scale: the retail client halves MOCV at load and doubles it at draw, so
    /// CMapObj::QueryLighting effectively returns 2x the raw stored value. Our
    /// wall path skips the halving and multiplies by VertexColorScale = 2.0 at
    /// draw instead, landing on the same place. Since MODD.color is on the raw
    /// MOCV scale, doodads feed color/255 into that SAME 2.0 — never
    /// pre-doubled, which would give 4x. This preserves the invariant that
    /// actually shows on screen: a barrel matches the floor it stands on.
    ///
    /// No MOHD.ambColor is added on top. The wall path does not add it either,
    /// and in classic-era data the ambient is already baked into MOCV.
    ///
    /// The gate matters. Measured across 335 WMO roots / 70,228 placements:
    /// interior-owned doodads average RGB (116,103,99) with 0.1% black, while
    /// exterior-owned ones average (51,51,63) with 12.7% pure black — because
    /// the client never reads those, so nobody ever checked them. Applying
    /// MODD.color ungated would black out every lamp post in the game. Zero
    /// doodads were unreferenced by MODR, so no fallback is needed.
    /// </summary>
    private static Vector4[] BuildDoodadLighting(PreparedWmo ready)
    {
        var root = ready.Root;
        if (root is null || root.Doodads.Count == 0) return [];

        var light = new Vector4[root.Doodads.Count];
        for (int i = 0; i < light.Length; i++) light[i] = ExteriorDoodadLight;

        foreach (var group in ready.Groups)
        {
            if (group is null || group.DoodadRefs.Count == 0) continue;
            if (!group.IsInterior) continue;

            // EXTERIOR (0x8) and EXTERIOR_LIT (0x40) both mean "use daylight".
            // CMapObj::QueryLighting rejects such a group outright and falls
            // back to the outdoor sun, so a doodad inside one must too.
            if ((group.GroupFlags & 0x48) != 0) continue;

            foreach (ushort index in group.DoodadRefs)
            {
                if (index >= light.Length) continue;
                var d = root.Doodads[index];
                light[index] = new Vector4(
                    d.ColorR / 255f, d.ColorG / 255f, d.ColorB / 255f, 0f);
            }
        }

        return light;
    }

    /// <summary>
    /// Every embedded doodad of every placed building, as a model path, a
    /// world transform and its baked light, ready to hand to the doodad
    /// renderer.
    ///
    /// Set 0 is "$DefaultGlobal" and is always present; a placement may name a
    /// second set on top of it, which is how one tavern model furnishes
    /// differently in different towns.
    /// </summary>
    public IEnumerable<(string ModelPath, Matrix4x4 Transform, Vector4 Light)> EnumerateDoodads()
        => EnumerateDoodads(Vector2.Zero, float.PositiveInfinity);

    /// <summary>
    /// Embedded doodads near a streaming centre. A huge WMO may intersect the
    /// resident ADT ring while most of its furniture is hundreds of yards away;
    /// filtering individual MODD transforms prevents that furniture from
    /// dominating startup.
    /// </summary>
    public IEnumerable<(string ModelPath, Matrix4x4 Transform, Vector4 Light)> EnumerateDoodads(
        Vector2 centre, float maxDistance)
    {
        float maxDistanceSq = maxDistance * maxDistance;

        foreach (var instance in _instances)
        {
            var model = instance.Model;
            if (model.Doodads.Count == 0) continue;

            foreach (int setIndex in DoodadSetsFor(model, instance.DoodadSet))
            {
                var set = model.DoodadSets[setIndex];

                for (uint i = 0; i < set.DoodadCount; i++)
                {
                    uint index = set.FirstInstanceIndex + i;
                    if (index >= model.Doodads.Count) break;

                    var d = model.Doodads[(int)index];
                    if (string.IsNullOrWhiteSpace(d.ModelPath)) continue;

                    var local =
                        M2ToWmo
                        * Matrix4x4.CreateScale(d.Scale > 0.0001f ? d.Scale : 1f)
                        * Matrix4x4.CreateFromQuaternion(new Quaternion(d.QuatX, d.QuatY, d.QuatZ, d.QuatW))
                        * Matrix4x4.CreateTranslation(d.PosX, d.PosY, d.PosZ);

                    var transform = local * instance.Transform;
                    if (!float.IsPositiveInfinity(maxDistance))
                    {
                        var delta = new Vector2(transform.M41, transform.M42) - centre;
                        if (delta.LengthSquared() > maxDistanceSq) continue;
                    }

                    var light = index < (uint)model.DoodadLight.Length
                        ? model.DoodadLight[(int)index]
                        : ExteriorDoodadLight;

                    yield return (d.ModelPath, transform, light);
                }
            }
        }
    }

    private static IEnumerable<int> DoodadSetsFor(Model model, int requested)
    {
        if (model.DoodadSets.Count == 0) yield break;

        yield return 0;

        if (requested > 0 && requested < model.DoodadSets.Count)
            yield return requested;
    }

    // ── drawing ──────────────────────────────────────────────────────────────

    public unsafe void Render(Camera camera)
    {
        long started = Stopwatch.GetTimestamp();
        DrawnLastFrame = 0;
        VisibleGroupsLastFrame = 0;
        LodGroupsCulledLastFrame = 0;
        DrawCallsLastFrame = 0;
        TrianglesLastFrame = 0;
        _frameLargestWmoGroupCount = 0;
        OccludedGroupsLastFrame = 0;
        if (!Enabled || _shader is null || _instances.Count == 0)
        {
            RenderMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            return;
        }

        _shader.Use();
        _shader.Set("uViewProjection", camera.RelativeViewProjection);
        _shader.Set("uUseInstancing", 0);
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

        var viewProjection = camera.RelativeViewProjection;
        var cameraPosition = camera.Position;
        float effectiveDrawDistance = MathF.Min(DrawDistance, VisibilityDistance);
        bool cullingOn = true;

        foreach (var instance in _instances)
        {
            if (FrustumCulling &&
                !Camera.BoxInFrustum(viewProjection,
                    instance.WorldMin - cameraPosition,
                    instance.WorldMax - cameraPosition)) continue;

            var modelTransform = instance.Transform;
            modelTransform.M41 -= cameraPosition.X;
            modelTransform.M42 -= cameraPosition.Y;
            modelTransform.M43 -= cameraPosition.Z;

            _shader.Set("uModel", modelTransform);
            _shader.Set("uModelViewProjection", modelTransform * camera.RelativeViewProjection);

            // A city is one WMO instance containing many spatial groups. The
            // impostor swap and interior visibility both key off whether the
            // camera is inside one of this WMO's real cells (CameraInsideInstance),
            // which is Blizzard's approach-LOD model: shells show from outside,
            // detailed geometry from within.
            bool cameraInside = CameraInsideInstance(instance, cameraPosition);

            var visibleGroups = new List<GroupMesh>();
            int shellsDrawn = 0, shellsHidden = 0;
            var cull = new FrameCullContext(cameraPosition, cameraInside, effectiveDrawDistance, viewProjection);
            foreach (var group in instance.Model.Groups)
            {
                var (groupMin, groupMax) = TransformedBounds(group, instance.Transform);

                // The decision lives in ClassifyGroup so the picker and dump report
                // the exact reason this loop acts on. The per-reason counters below
                // reproduce the previous behaviour (shells drawn/hidden, occluded,
                // LOD-culled); the switch draws for the two Drawn* reasons.
                switch (ClassifyGroup(instance, group, groupMin, groupMax, in cull))
                {
                    case WmoReasonCode.Drawn:
                        visibleGroups.Add(group);
                        break;
                    case WmoReasonCode.DrawnShellFar:
                        visibleGroups.Add(group);
                        shellsDrawn++;
                        break;
                    case WmoReasonCode.OverrideShow:
                        visibleGroups.Add(group);
                        break;
                    case WmoReasonCode.ShellNearSuppressed:
                        LodGroupsCulledLastFrame++;
                        shellsHidden++;
                        break;
                    case WmoReasonCode.OcclusionCulled:
                        OccludedGroupsLastFrame++;
                        break;
                    default:
                        // InteriorCull / DistanceCulled / FrustumCulled: skipped,
                        // counted only in the frame aggregates.
                        break;
                }
            }

            // Surface the biggest WMO's swap state to the HUD (and, if VisTrace
            // is on, the console). On the approach bridge inside should read
            // False and shells should draw; in the Trade District inside should
            // read True and shells hide.
            if (instance.Model.Groups.Count > _frameLargestWmoGroupCount)
            {
                _frameLargestWmoGroupCount = instance.Model.Groups.Count;
                LargestWmoGroupCount = instance.Model.Groups.Count;
                LargestWmoName = Path.GetFileName(instance.Path);
                LargestWmoGroupsDrawn = visibleGroups.Count;
                LastInsideCity = cameraInside;
                ShellsDrawnLastFrame = shellsDrawn;
                ShellsHiddenLastFrame = shellsHidden;

                if (VisTrace && ++_wmoVisLogFrames >= 120)
                {
                    _wmoVisLogFrames = 0;
                    Console.WriteLine(
                        $"[wmo-vis] {LargestWmoName} inside={cameraInside} " +
                        $"shellsDrawn={shellsDrawn} shellsHidden={shellsHidden} " +
                        $"groupsDrawn={visibleGroups.Count}/{instance.Model.Groups.Count}");
                }
            }

            if (visibleGroups.Count == 0)
                continue;

            DrawnLastFrame++;
            VisibleGroupsLastFrame += visibleGroups.Count;

            for (int pass = 0; pass < 2; pass++)
            {
                bool transparentPass = pass == 1;
                if (transparentPass)
                {
                    _gl.DepthMask(false);
                    _gl.Enable(EnableCap.Blend);
                    _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                }

                foreach (var group in visibleGroups)
                {
                    _gl.BindVertexArray(group.Vao);

                    foreach (var batch in group.Batches)
                    {
                        if (batch.Transparent != transparentPass) continue;
                        bool twoSided = batch.TwoSided || ForceTwoSided;

                        if (twoSided && cullingOn)
                        {
                            _gl.Disable(EnableCap.CullFace);
                            cullingOn = false;
                        }
                        else if (!twoSided && !cullingOn)
                        {
                            _gl.Enable(EnableCap.CullFace);
                            cullingOn = true;
                        }

                        _shader.Set("uBatchType", UseVertexColors ? batch.Type : 3);

                        if (batch.Texture is not null)
                        {
                            batch.Texture.Bind(0);
                            _shader.Set("uHasTexture", 1);
                            _shader.Set("uAlphaCutoff", batch.AlphaTest ? AlphaCutoff : 0f);
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
                }

                if (transparentPass)
                {
                    _gl.Disable(EnableCap.Blend);
                    _gl.DepthMask(true);
                }
            }
        }

        if (!cullingOn) _gl.Enable(EnableCap.CullFace);
        _gl.BindVertexArray(0);
        RenderMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    // ── in-game diagnostics ────────────────────────────────────────────────────

    /// <summary>
    /// Re-run shell classification on every loaded group with the current
    /// <see cref="ImpostorMaxVertices"/>. Cheap, and called from the HUD slider
    /// so the impostor set retunes live without a reload. Classification is baked
    /// (not per-frame) to keep the draw loop allocation-free.
    /// </summary>
    public void ReclassifyShells()
    {
        foreach (var instance in _instances)
        {
            uint n = (uint)instance.Model.Groups.Count;
            foreach (var g in instance.Model.Groups)
                g.IsDistanceLod = IsDistanceOnlyLod(
                    instance.Path, n, g.GroupFlags, g.VertexCount, g.GroupName);
        }
    }

    /// <summary>One WMO group a pick ray passed through.</summary>
    public readonly record struct GroupHit(
        string Path, string Root, int GroupIndex, string Name, uint Flags,
        int VertexCount, bool Interior, bool Shell, WmoReasonCode Reason, float Distance);

    /// <summary>One WMO group described for the scene dump: identity plus the exact
    /// WmoReasonCode ClassifyGroup assigned it this view.</summary>
    public readonly record struct GroupReport(
        int GroupIndex, string Name, uint Flags, int VertexCount,
        bool Interior, bool Shell, float Distance, WmoReasonCode Reason);

    /// <summary>One resident WMO instance summarized for the scene dump.</summary>
    public readonly record struct InstanceSummary(
        string Root, float Distance, int Groups, int Drawn, int Shells, bool CameraInside);

    private static Vector3[] BuildPickPositions(WmoGroupData group)
    {
        var arr = new Vector3[group.Vertices.Count];
        for (int i = 0; i < arr.Length; i++)
        {
            var v = group.Vertices[i];
            arr[i] = new Vector3(v.x, v.y, v.z);
        }
        return arr;
    }

    /// <summary>
    /// The WMO groups the ray actually PIERCES (triangle-level), nearest first —
    /// so the top entry is the surface under the cursor, not every box behind it.
    /// The ray is taken into each instance's local space (WMO placement is a pure
    /// rotation+translation, so distances are preserved) and tested against that
    /// group's retained triangles. A per-group local AABB prunes the misses.
    /// </summary>
    public List<GroupHit> PickGroups(Camera camera, Vector3 rayOrigin, Vector3 rayDir, int max = 14)
    {
        var cameraPosition = camera.Position;
        var viewProjection = camera.RelativeViewProjection;
        float effectiveDrawDistance = MathF.Min(DrawDistance, VisibilityDistance);

        var hits = new List<GroupHit>();
        foreach (var instance in _instances)
        {
            if (!Matrix4x4.Invert(instance.Transform, out var inv)) continue;
            var lo = Vector3.Transform(rayOrigin, inv);
            var ld = Vector3.TransformNormal(rayDir, inv);
            uint n = (uint)instance.Model.Groups.Count;

            var cull = new FrameCullContext(
                cameraPosition, CameraInsideInstance(instance, cameraPosition),
                effectiveDrawDistance, viewProjection);

            foreach (var g in instance.Model.Groups)
            {
                if (!RayHitsBox(lo, ld, g.LocalMin, g.LocalMax, out _)) continue;
                if (!NearestTriangle(lo, ld, g.PickPositions, g.PickIndices, out float t)) continue;

                var (groupMin, groupMax) = TransformedBounds(g, instance.Transform);
                var reason = ClassifyGroup(instance, g, groupMin, groupMax, in cull);
                bool shell = UseDistanceLodShells && IsDistanceOnlyLod(
                    instance.Path, n, g.GroupFlags, g.VertexCount, g.GroupName);
                hits.Add(new GroupHit(
                    Path.GetFileName(instance.Path), instance.Path, g.GroupIndex, g.GroupName,
                    g.GroupFlags, g.VertexCount, g.IsInterior, shell, reason, t));
            }
        }
        hits.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        if (hits.Count > max) hits.RemoveRange(max, hits.Count - max);
        return hits;
    }

    /// <summary>
    /// Every group of the WMO instance the crosshair ray hits, each with the
    /// WmoReasonCode it draws/skips under - the "why is this building doing that"
    /// block of the scene dump. Null when the ray hits no WMO. Shares ClassifyGroup
    /// with Render, so these are the real reasons. Dump-only (PLAN_02).
    /// </summary>
    public List<GroupReport>? DescribeInstanceUnderRay(Camera camera, Vector3 rayOrigin, Vector3 rayDir)
    {
        Instance? best = null;
        float bestT = float.MaxValue;
        foreach (var instance in _instances)
        {
            if (!Matrix4x4.Invert(instance.Transform, out var inv)) continue;
            var lo = Vector3.Transform(rayOrigin, inv);
            var ld = Vector3.TransformNormal(rayDir, inv);
            foreach (var g in instance.Model.Groups)
            {
                if (!RayHitsBox(lo, ld, g.LocalMin, g.LocalMax, out _)) continue;
                if (!NearestTriangle(lo, ld, g.PickPositions, g.PickIndices, out float t)) continue;
                if (t < bestT) { bestT = t; best = instance; }
            }
        }
        if (best is null) return null;

        var cull = new FrameCullContext(
            camera.Position, CameraInsideInstance(best, camera.Position),
            MathF.Min(DrawDistance, VisibilityDistance), camera.RelativeViewProjection);

        var report = new List<GroupReport>();
        foreach (var g in best.Model.Groups)
        {
            var (mn, mx) = TransformedBounds(g, best.Transform);
            var reason = ClassifyGroup(best, g, mn, mx, in cull);
            report.Add(new GroupReport(
                g.GroupIndex, g.GroupName, g.GroupFlags, g.VertexCount,
                g.IsInterior, g.IsDistanceLod, DistanceToBox(camera.Position, mn, mx), reason));
        }
        return report;
    }

    /// <summary>
    /// Every resident WMO instance summarized (nearest first): root, distance,
    /// group count, how many drew, how many were distance-shells, camera-inside.
    /// Classifies each group through ClassifyGroup - a one-shot cost paid only when
    /// a dump is taken. Dump-only (PLAN_02).
    /// </summary>
    public List<InstanceSummary> SummarizeInstances(Camera camera)
    {
        var cp = camera.Position;
        var vp = camera.RelativeViewProjection;
        float edd = MathF.Min(DrawDistance, VisibilityDistance);

        var summaries = new List<InstanceSummary>();
        foreach (var instance in _instances)
        {
            bool inside = CameraInsideInstance(instance, cp);
            var cull = new FrameCullContext(cp, inside, edd, vp);
            int drawn = 0, shells = 0;
            foreach (var g in instance.Model.Groups)
            {
                var (mn, mx) = TransformedBounds(g, instance.Transform);
                switch (ClassifyGroup(instance, g, mn, mx, in cull))
                {
                    case WmoReasonCode.Drawn: drawn++; break;
                    case WmoReasonCode.DrawnShellFar: drawn++; shells++; break;
                    case WmoReasonCode.OverrideShow: drawn++; break;
                }
            }
            summaries.Add(new InstanceSummary(
                Path.GetFileName(instance.Path),
                DistanceToBox(cp, instance.WorldMin, instance.WorldMax),
                instance.Model.Groups.Count, drawn, shells, inside));
        }
        summaries.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        return summaries;
    }

    /// <summary>Nearest ray-triangle hit distance over a group's triangles.</summary>
    private static bool NearestTriangle(
        Vector3 o, Vector3 d, Vector3[] verts, int[] indices, out float tMin)
    {
        tMin = float.MaxValue;
        bool hit = false;
        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            int a = indices[i], b = indices[i + 1], c = indices[i + 2];
            if (a >= verts.Length || b >= verts.Length || c >= verts.Length) continue;
            if (RayTriangle(o, d, verts[a], verts[b], verts[c], out float t) && t < tMin)
            {
                tMin = t;
                hit = true;
            }
        }
        return hit;
    }

    /// <summary>Two-sided Moller-Trumbore ray/triangle. t is distance along d.</summary>
    private static bool RayTriangle(
        Vector3 o, Vector3 d, Vector3 v0, Vector3 v1, Vector3 v2, out float t)
    {
        t = 0f;
        const float eps = 1e-7f;
        var e1 = v1 - v0;
        var e2 = v2 - v0;
        var p = Vector3.Cross(d, e2);
        float det = Vector3.Dot(e1, p);
        if (MathF.Abs(det) < eps) return false;
        float invDet = 1f / det;
        var tv = o - v0;
        float u = Vector3.Dot(tv, p) * invDet;
        if (u < 0f || u > 1f) return false;
        var q = Vector3.Cross(tv, e1);
        float v = Vector3.Dot(d, q) * invDet;
        if (v < 0f || u + v > 1f) return false;
        t = Vector3.Dot(e2, q) * invDet;
        return t > eps;
    }

    /// <summary>Slab ray/AABB test. Returns the near entry distance along dir.</summary>
    private static bool RayHitsBox(Vector3 o, Vector3 d, Vector3 mn, Vector3 mx, out float t)
    {
        t = 0f;
        float tmin = 0f, tmax = float.MaxValue;
        for (int a = 0; a < 3; a++)
        {
            float oa = a == 0 ? o.X : a == 1 ? o.Y : o.Z;
            float da = a == 0 ? d.X : a == 1 ? d.Y : d.Z;
            float lo = a == 0 ? mn.X : a == 1 ? mn.Y : mn.Z;
            float hi = a == 0 ? mx.X : a == 1 ? mx.Y : mx.Z;
            if (MathF.Abs(da) < 1e-8f)
            {
                if (oa < lo || oa > hi) return false;
                continue;
            }
            float inv = 1f / da;
            float t1 = (lo - oa) * inv;
            float t2 = (hi - oa) * inv;
            if (t1 > t2) (t1, t2) = (t2, t1);
            tmin = MathF.Max(tmin, t1);
            tmax = MathF.Min(tmax, t2);
            if (tmin > tmax) return false;
        }
        t = tmin;
        return true;
    }

    public void Dispose()
    {
        try { _preloadJob?.Worker.GetAwaiter().GetResult(); }
        catch { /* Shutdown must continue even if a background decode failed. */ }
        foreach (var model in _models.Values) model?.Dispose();
        foreach (var texture in _textures.Values) texture?.Dispose();
        _models.Clear();
        _textures.Clear();
        _instances.Clear();
        _preloadJob?.Model.Dispose();
        _preloadJob = null;
        _preloadQueue.Clear();
        _preloadQueued.Clear();
        _newDoodadModels.Clear();
        _announcedDoodadModels.Clear();
        _shader?.Dispose();
    }
}

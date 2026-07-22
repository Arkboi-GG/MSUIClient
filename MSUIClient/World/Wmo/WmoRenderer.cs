using System.Numerics;
using Silk.NET.OpenGL;
using MSUIClient.Engine;
using MSUIClient.Formats;
using MSUIClient.World.Collision;
using Shader = MSUIClient.Engine.Shader;
using Texture = MSUIClient.Engine.Texture;

namespace MSUIClient.World.Wmo;

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
    /// <summary>Position(3) + Normal(3) + UV(2).</summary>
    private const int FloatsPerVertex = 8;

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
        public bool TextureHasAlpha;
    }

    private sealed class GroupMesh : IDisposable
    {
        public uint Vao, Vbo, Ebo;
        public List<Batch> Batches = [];
        public Vector3 LocalMin, LocalMax;
        public bool IsInterior;

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

        /// <summary>
        /// The building's embedded doodads — its furniture. Beds, kitchen
        /// fittings, tables, barrels: none of these are ADT placements, they
        /// belong to the WMO itself via MODS/MODN/MODD and are placed in WMO
        /// LOCAL space, so they ride the building's own transform.
        /// </summary>
        public List<WmoDoodadSet> DoodadSets = [];
        public List<WmoDoodadDef> Doodads = [];

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

    private readonly GL _gl;
    private readonly ClientConfig _config;

    private readonly Dictionary<string, Model?> _models = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Texture?> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Instance> _instances = [];
    private readonly HashSet<string> _placed = [];

    /// <summary>Textures whose decoded alpha channel is entirely zero.</summary>
    private readonly HashSet<string> _opaqueTextures = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Textures whose 1-bit alpha came back as 0/1 and was rescaled to 0/255.</summary>
    private readonly HashSet<string> _rescaledAlpha = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>BLPs that were named by a material but could not be decoded.</summary>
    private readonly HashSet<string> _failedTextures = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Batches that ended up with no texture at all, from any slot.</summary>
    private int _texturelessBatches;

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
    public int TotalTriangles { get; private set; }
    public int DrawnLastFrame { get; private set; }
    public bool Enabled { get; set; } = true;

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

    /// <summary>
    /// Alpha below which a fragment is discarded, for textures that actually
    /// have an alpha channel. Railings and window tracery need it; a wall panel
    /// whose texture has no alpha must never be subject to it.
    /// </summary>
    public float AlphaCutoff { get; set; } = 0.35f;

    public Vector3 SunDirection { get; set; } = Vector3.Normalize(new Vector3(0.45f, 0.35f, 0.82f));
    public Vector3 FogColor { get; set; } = new(0.56f, 0.71f, 0.85f);
    public float FogStart { get; set; } = 350f;
    public float FogEnd { get; set; } = 900f;

    public WmoRenderer(GL gl, ClientConfig config)
    {
        _gl = gl;
        _config = config;
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

        var rootBytes = AdtTerrainReader.ReadFileFromMpqs(_config.ClientDataPath, rootPath);
        if (rootBytes is null)
        {
            Console.WriteLine($"[wmo] not in MPQs: {rootPath}");
            _models[rootPath] = null;
            return null;
        }

        var root = WmoReader.ParseRoot(rootBytes);
        if (root is null)
        {
            Console.WriteLine($"[wmo] failed to parse root: {rootPath}");
            _models[rootPath] = null;
            return null;
        }

        var model = new Model();
        var collision = new List<Vector3>();
        int skipped = 0;

        // Every way a group can vanish, counted. These were three silent
        // `continue`s, which is exactly how a building loses half its walls
        // without anything appearing in the log.
        int missingFiles = 0, unparsed = 0, empty = 0, unbuilt = 0, batchesDropped = 0;
        int indicesCovered = 0, indicesTotal = 0;

        string stem = rootPath[..^4];       // strip ".wmo"

        for (int g = 0; g < root.NGroups; g++)
        {
            string groupPath = $"{stem}_{g:D3}.wmo";
            var groupBytes = AdtTerrainReader.ReadFileFromMpqs(_config.ClientDataPath, groupPath);
            if (groupBytes is null) { missingFiles++; continue; }

            var group = WmoReader.ParseGroup(groupBytes);
            if (group is null) { unparsed++; continue; }
            if (group.Vertices.Count == 0 || group.Indices.Count < 3) { empty++; continue; }

            var mesh = BuildGroupMesh(group, root);
            if (mesh is null) { unbuilt++; continue; }

            batchesDropped += _lastBatchesDropped;
            indicesCovered += _lastIndicesCovered;
            indicesTotal += _lastIndicesTotal;

            model.Groups.Add(mesh);
            model.TriangleCount += group.Indices.Count / 3;

            CollectCollision(group, collision, ref skipped);
        }

        model.CollisionTriangles = [.. collision];
        model.CollisionSkipped = skipped;

        if (indicesTotal > 0 && indicesCovered < indicesTotal)
        {
            // Under 100% is EXPECTED, not a fault: MOPY marks collision-only
            // triangles (materialId 0xFF) that are solid and never drawn, and
            // they are not in any MOBA batch. Every model measured sits around
            // 90%, which is the invisible-wall geometry. Only a sharp outlier
            // would mean anything.
            double covered = 100.0 * indicesCovered / indicesTotal;
            if (covered < 70.0)
                Console.WriteLine(
                    $"[wmo] {Path.GetFileName(rootPath)}: MOBA batches draw only {covered:F1}% of " +
                    $"indices — unusually low, most models sit near 90%");
        }

        if (missingFiles + unparsed + empty + unbuilt + batchesDropped > 0)
        {
            Console.WriteLine(
                $"[wmo] {Path.GetFileName(rootPath)}: {model.Groups.Count}/{root.NGroups} group(s) drawn" +
                (missingFiles > 0 ? $", {missingFiles} file(s) not in MPQ" : "") +
                (unparsed > 0 ? $", {unparsed} failed to parse" : "") +
                (empty > 0 ? $", {empty} empty" : "") +
                (unbuilt > 0 ? $", {unbuilt} mesh build failed" : "") +
                (batchesDropped > 0 ? $", {batchesDropped} batch(es) dropped" : ""));
        }

        if (model.Groups.Count == 0)
        {
            Console.WriteLine($"[wmo] no drawable groups: {rootPath}");
            model.Dispose();
            _models[rootPath] = null;
            return null;
        }

        model.DoodadSets = root.DoodadSets;
        model.Doodads = root.Doodads;

        _models[rootPath] = model;
        return model;
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

    private unsafe GroupMesh? BuildGroupMesh(WmoGroupData group, WmoRootData root)
    {
        int vertexCount = group.Vertices.Count;
        var vertices = new float[vertexCount * FloatsPerVertex];

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        for (int i = 0; i < vertexCount; i++)
        {
            var v = group.Vertices[i];

            // The fallback tuples must name their elements. Without the names,
            // the ternary's common type is a plain (float, float, float) and
            // .x / .y / .z stop resolving on the whole expression.
            var n = i < group.Normals.Count ? group.Normals[i] : (x: 0f, y: 0f, z: 1f);
            var uv = i < group.UVs.Count ? group.UVs[i] : (u: 0f, v: 0f);

            int o = i * FloatsPerVertex;
            vertices[o + 0] = v.x;
            vertices[o + 1] = v.y;
            vertices[o + 2] = v.z;
            vertices[o + 3] = n.x;
            vertices[o + 4] = n.y;
            vertices[o + 5] = n.z;
            vertices[o + 6] = uv.u;
            vertices[o + 7] = uv.v;

            var p = new Vector3(v.x, v.y, v.z);
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }

        var indices = group.Indices.ToArray();

        uint vao = _gl.GenVertexArray();
        _gl.BindVertexArray(vao);

        uint vbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        fixed (float* p = vertices)
        {
            _gl.BufferData(BufferTargetARB.ArrayBuffer,
                (nuint)(vertices.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);
        }

        uint ebo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, ebo);
        fixed (ushort* p = indices)
        {
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

        _gl.BindVertexArray(0);

        var mesh = new GroupMesh
        {
            Vao = vao,
            Vbo = vbo,
            Ebo = ebo,
            LocalMin = min,
            LocalMax = max,
            IsInterior = group.IsInterior,
        };
        mesh.Attach(_gl);

        // MOBA batches: each is a run of indices sharing one material.
        _lastBatchesDropped = 0;
        _lastIndicesCovered = 0;
        _lastIndicesTotal = indices.Length;

        foreach (var b in group.Batches)
        {
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
                TextureHasAlpha = MaterialHasAlpha(material),
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

            _rescaledAlpha.Add(blpPath);
        }

        var texture = Texture.From2D(_gl, bgra, w, h);
        _textures[blpPath] = texture;
        return texture;
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
    /// Every embedded doodad of every placed building, as a model path and a
    /// world transform, ready to hand to the doodad renderer.
    ///
    /// Set 0 is "$DefaultGlobal" and is always present; a placement may name a
    /// second set on top of it, which is how one tavern model furnishes
    /// differently in different towns.
    /// </summary>
    public IEnumerable<(string ModelPath, Matrix4x4 Transform)> EnumerateDoodads()
    {
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

                    yield return (d.ModelPath, local * instance.Transform);
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
        DrawnLastFrame = 0;
        if (!Enabled || _shader is null || _instances.Count == 0) return;

        _shader.Use();
        _shader.Set("uViewProjection", camera.ViewProjection);
        _shader.Set("uCameraPos", camera.Position);
        _shader.Set("uSunDirection", SunDirection);
        _shader.Set("uFogStart", FogStart);
        _shader.Set("uFogEnd", FogEnd);
        _shader.Set("uFogColor", FogColor);
        _shader.Set("uTexture", 0);

        var planes = camera.FrustumPlanes();
        bool cullingOn = true;

        foreach (var instance in _instances)
        {
            if (!Camera.BoxInFrustum(planes, instance.WorldMin, instance.WorldMax)) continue;

            _shader.Set("uModel", instance.Transform);

            foreach (var group in instance.Model.Groups)
            {
                _gl.BindVertexArray(group.Vao);

                foreach (var batch in group.Batches)
                {
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

                    if (batch.Texture is not null)
                    {
                        batch.Texture.Bind(0);
                        _shader.Set("uHasTexture", 1);
                        _shader.Set("uAlphaCutoff", batch.TextureHasAlpha ? AlphaCutoff : 0f);
                    }
                    else
                    {
                        _shader.Set("uHasTexture", 0);
                        _shader.Set("uAlphaCutoff", 0f);
                    }

                    _gl.DrawElements(PrimitiveType.Triangles, batch.IndexCount,
                        DrawElementsType.UnsignedShort, (void*)(batch.IndexStart * sizeof(ushort)));
                }
            }

            DrawnLastFrame++;
        }

        if (!cullingOn) _gl.Enable(EnableCap.CullFace);
        _gl.BindVertexArray(0);
    }

    public void Dispose()
    {
        foreach (var model in _models.Values) model?.Dispose();
        foreach (var texture in _textures.Values) texture?.Dispose();
        _models.Clear();
        _textures.Clear();
        _instances.Clear();
        _shader?.Dispose();
    }
}

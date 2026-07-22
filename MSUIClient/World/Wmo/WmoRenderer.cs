using System.Numerics;
using Silk.NET.OpenGL;
using MSUIClient.Engine;
using MSUIClient.Formats;
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
    }

    private readonly GL _gl;
    private readonly ClientConfig _config;

    private readonly Dictionary<string, Model?> _models = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Texture?> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Instance> _instances = [];
    private readonly HashSet<string> _placed = [];

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
    public void LoadForTiles(IEnumerable<(int col, int row)> tiles)
    {
        var started = DateTime.UtcNow;
        var pending = new List<(Model Model, AdtTerrainReader.WmoInstance Placement)>();

        foreach (var (col, row) in tiles)
        {
            var adt = AdtTerrainReader.ReadFromMpq(_config.ClientDataPath, _config.Start.MapName, row, col);
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

        var convention = Calibrate(pending);
        ConventionName = convention.Name;

        foreach (var (model, w) in pending)
        {
            var transform = BuildPlacement(w, convention);
            var (min, max) = TransformedBounds(model, transform);

            _instances.Add(new Instance
            {
                Model = model,
                Transform = transform,
                WorldMin = min,
                WorldMax = max,
                Origin = Vector3.Transform(new Vector3(w.PosX, w.PosY, w.PosZ), PlacementToWorld),
                Path = w.ModelPath,
            });

            VerifyPlacement(w, min, max);
        }

        TotalTriangles = _instances.Sum(i => i.Model.TriangleCount);

        var elapsed = DateTime.UtcNow - started;
        Console.WriteLine(
            $"[wmo] {_instances.Count} placement(s), {ModelCount} model(s), {TextureCount} texture(s), " +
            $"{TotalTriangles:N0} triangles, {elapsed.TotalSeconds:F1}s");
    }

    /// <summary>
    /// Try every candidate reading of the placement data against MODF's own
    /// bounding boxes and keep the best.
    ///
    /// This is not elegant, and it is not meant to stay forever — once the
    /// winner is known and stable it can be hardcoded and the table deleted.
    /// But the alternative is guessing a convention, rebuilding, looking at a
    /// tilted building, and guessing again. The data already contains the
    /// answer; this just reads it.
    ///
    /// The printed table matters as much as the result: a clear winner means
    /// the convention is settled, while a near-tie means the score is not
    /// discriminating and the buildings still need eyes on them.
    /// </summary>
    private Convention Calibrate(List<(Model Model, AdtTerrainReader.WmoInstance Placement)> pending)
    {
        if (pending.Count == 0) return Candidates[0];

        var ranked = Candidates
            .Select(c => (Convention: c, Score: ScoreConvention(c, pending)))
            .OrderBy(r => r.Score)
            .ToList();

        Console.WriteLine($"[wmo] calibrating {Candidates.Length} conventions against {pending.Count} placement(s):");
        foreach (var (candidate, score) in ranked.Take(5))
            Console.WriteLine($"[wmo]   {candidate.Name,-24} mean error {score / pending.Count,8:F1} yd");

        var best = ranked[0];
        double runnerUp = ranked.Count > 1 ? ranked[1].Score : double.MaxValue;

        Console.WriteLine(
            $"[wmo] using {best.Convention.Name} " +
            $"(mean {best.Score / pending.Count:F1} yd, next best {runnerUp / pending.Count:F1} yd) " +
            $"+ {HeadingCorrectionDegrees:F0} deg heading correction");

        return best.Convention;
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
    /// One candidate reading of the placement data.
    ///
    /// Two things are genuinely ambiguous and the documentation disagrees with
    /// itself: which stored vertex axis is UP, and how MODF's Euler triple
    /// composes. Guessing costs a build cycle each time, so instead every
    /// plausible combination is scored against MODF's own bounding boxes and
    /// the winner is used. The boxes describe the placed model, so the right
    /// convention is the one whose geometry lands inside them.
    /// </summary>
    private sealed class Convention
    {
        public string Name = "";

        /// <summary>Model vertex basis -> placement basis. A pure axis permutation.</summary>
        public Matrix4x4 Basis = Matrix4x4.Identity;

        /// <summary>Heading offset applied to RotY, in degrees.</summary>
        public float HeadingOffset;

        /// <summary>Negate RotY before applying the offset.</summary>
        public bool FlipHeading;

        /// <summary>Apply the RotX / RotZ terms as well as the heading.</summary>
        public bool UseTilt;
    }

    // Axis permutations, row-vector. Each is a proper rotation (det +1), so
    // none of them mirrors the geometry.
    private static readonly (string Name, Matrix4x4 M)[] Bases =
    [
        // (x, y, z) unchanged - up is the stored Y.
        ("xyz", Matrix4x4.Identity),

        // (x, y, z) -> (x, -z, y). This is the reading implied by "MOVT is
        // stored X, Z, -Y": up is the negated stored Z.
        ("x-zy", new Matrix4x4(1, 0, 0, 0,  0, 0, 1, 0,  0, -1, 0, 0,  0, 0, 0, 1)),

        // (x, y, z) -> (x, z, -y). The same idea with the opposite sign.
        ("xz-y", new Matrix4x4(1, 0, 0, 0,  0, 0, -1, 0,  0, 1, 0, 0,  0, 0, 0, 1)),

        // (x, y, z) -> (-z, y, x). Up stays Y but the horizontals swap.
        ("-zyx", new Matrix4x4(0, 0, 1, 0,  0, 1, 0, 0,  -1, 0, 0, 0,  0, 0, 0, 1)),
    ];

    private static readonly Convention[] Candidates = BuildCandidates();

    private static Convention[] BuildCandidates()
    {
        var list = new List<Convention>();

        foreach (var (basisName, basis) in Bases)
        foreach (float offset in new[] { -270f, 0f, 90f, 180f })
        foreach (bool flip in new[] { false, true })
        foreach (bool tilt in new[] { true, false })
        {
            list.Add(new Convention
            {
                Name = $"{basisName}/{(flip ? "-" : "+")}rotY{offset:+0;-0;+0}{(tilt ? "/tilt" : "")}",
                Basis = basis,
                HeadingOffset = offset,
                FlipHeading = flip,
                UseTilt = tilt,
            });
        }

        return [.. list];
    }

    /// <summary>The convention in force. Set by calibration at load.</summary>
    public string ConventionName { get; private set; } = "(uncalibrated)";

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

    private static Matrix4x4 BuildPlacement(AdtTerrainReader.WmoInstance w, Convention c)
    {
        const float deg = MathF.PI / 180f;

        float heading =
            ((c.FlipHeading ? -w.RotY : w.RotY) + c.HeadingOffset + HeadingCorrectionDegrees) * deg;

        var rotation = Matrix4x4.CreateRotationY(heading);

        if (c.UseTilt)
        {
            // Column-vector intent: RotY(heading) * RotZ(-rotX) * RotX(rotZ).
            // System.Numerics is row-vector, so the order reverses.
            rotation = Matrix4x4.CreateRotationX(w.RotZ * deg)
                     * Matrix4x4.CreateRotationZ(-w.RotX * deg)
                     * rotation;
        }

        return c.Basis
             * rotation
             * Matrix4x4.CreateTranslation(w.PosX, w.PosY, w.PosZ)
             * PlacementToWorld;
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
    /// Score one candidate across every placement. Lower is better.
    ///
    /// Centre distance plus size difference, rather than corner distance:
    /// Blizzard's bounding boxes are padded, sometimes generously, so corners
    /// disagree even when a placement is perfect. A box that is in the right
    /// place and the right shape is the signal; exact extents are not.
    /// </summary>
    private static double ScoreConvention(
        Convention c, List<(Model Model, AdtTerrainReader.WmoInstance Placement)> pending)
    {
        double total = 0;

        foreach (var (model, w) in pending)
        {
            var (gMin, gMax) = TransformedBounds(model, BuildPlacement(w, c));
            var (mMin, mMax) = ModfBoxInWorld(w);

            var centreError = ((gMin + gMax) * 0.5f) - ((mMin + mMax) * 0.5f);
            var sizeError = (gMax - gMin) - (mMax - mMin);

            total += centreError.Length() + sizeError.Length();
        }

        return total;
    }

    private static (Vector3 min, Vector3 max) TransformedBounds(Model model, Matrix4x4 m)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        foreach (var g in model.Groups)
        {
            // Eight corners of the group's local box, transformed.
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
    /// Per-placement sanity check after calibration. Compares centre and size
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
        string stem = rootPath[..^4];       // strip ".wmo"

        for (int g = 0; g < root.NGroups; g++)
        {
            string groupPath = $"{stem}_{g:D3}.wmo";
            var groupBytes = AdtTerrainReader.ReadFileFromMpqs(_config.ClientDataPath, groupPath);
            if (groupBytes is null) continue;

            var group = WmoReader.ParseGroup(groupBytes);
            if (group is null || group.Vertices.Count == 0 || group.Indices.Count < 3) continue;

            var mesh = BuildGroupMesh(group, root);
            if (mesh is null) continue;

            model.Groups.Add(mesh);
            model.TriangleCount += group.Indices.Count / 3;
        }

        if (model.Groups.Count == 0)
        {
            Console.WriteLine($"[wmo] no drawable groups: {rootPath}");
            model.Dispose();
            _models[rootPath] = null;
            return null;
        }

        _models[rootPath] = model;
        return model;
    }

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
        foreach (var b in group.Batches)
        {
            if (b.IndexCount == 0) continue;
            if (b.IndexStart + b.IndexCount > (uint)indices.Length) continue;

            var material = b.MaterialId < root.Materials.Count ? root.Materials[b.MaterialId] : null;

            mesh.Batches.Add(new Batch
            {
                IndexStart = b.IndexStart,
                IndexCount = b.IndexCount,
                Texture = material is null ? null : ResolveTexture(material.Texture0Name),
                TwoSided = material?.IsNoCull ?? false,
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

    private Texture? ResolveTexture(string blpPath)
    {
        if (string.IsNullOrWhiteSpace(blpPath)) return null;
        if (_textures.TryGetValue(blpPath, out var cached)) return cached;

        var decoded = AdtTerrainReader.ReadBlpPixels(_config.ClientDataPath, blpPath);
        if (decoded is null)
        {
            Console.WriteLine($"[wmo] missing texture: {blpPath}");
            _textures[blpPath] = null;
            return null;
        }

        var (bgra, w, h) = decoded.Value;
        var texture = Texture.From2D(_gl, bgra, w, h);
        _textures[blpPath] = texture;
        return texture;
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
                    }
                    else
                    {
                        _shader.Set("uHasTexture", 0);
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

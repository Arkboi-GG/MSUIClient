using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.OpenGL;
using MSUIClient.Engine;
using MSUIClient.Formats;
using Shader = MSUIClient.Engine.Shader;
using Texture = MSUIClient.Engine.Texture;

namespace MSUIClient.World;

/// <summary>
/// Ground-effect foliage: the grass tufts, ferns and flowers vanilla scatters on
/// the terrain near the camera. Authentic chain:
///
///   MCLY.EffectId (per texture layer, per chunk)
///     -> GroundEffectTexture.dbc  (up to 4 doodad IDs + weights + density)
///        -> GroundEffectDoodad.dbc (the grass M2 model path)
///
/// For each terrain cell near the camera we find the dominant texture layer (the
/// one with the highest alpha), read its ground-effect id, and scatter density-
/// many little M2s at random position / yaw / scale on the terrain surface.
///
/// Rendering reuses the doodad pipeline exactly - one interleaved VBO (pos3 +
/// normal3 + uv2) per model, a per-instance mat4 as four vec4 attributes at
/// locations 3..6 (divisor 1), drawn with DrawElementsInstanced. Positions are
/// camera-relative for float precision. Grass gets its own shader (grass.vert/
/// frag) for wind sway, distance fade and alpha-cutout.
/// </summary>
public sealed class FoliageRenderer : IDisposable
{
    private const int FloatsPerVertex = 8;   // pos(3) + normal(3) + uv(2)

    private sealed class Batch
    {
        public int IndexStart;
        public int IndexCount;
        public Texture? Texture;
    }

    private sealed class GrassModel : IDisposable
    {
        public uint Vao, Vbo, Ebo, InstanceVbo;
        public List<Batch> Batches = [];
        public int TriangleCount;
        private GL? _gl;
        public void Attach(GL gl) => _gl = gl;
        public void Dispose()
        {
            if (_gl is null) return;
            _gl.DeleteVertexArray(Vao);
            _gl.DeleteBuffer(Vbo);
            _gl.DeleteBuffer(Ebo);
            _gl.DeleteBuffer(InstanceVbo);
        }
    }

    // Model-space (M2 is Y-up) -> WoW world (Z-up). Inverse of the M2 reader's
    // WoW->Y-up conversion: (x, y, z) -> (x, -z, y).
    private static readonly Matrix4x4 YUpToZUp = new(
        1, 0, 0, 0,
        0, 0, 1, 0,
        0, -1, 0, 0,
        0, 0, 0, 1);

    private readonly GL _gl;
    private readonly ClientConfig _config;
    private Shader? _shader;

    private readonly Dictionary<string, GrassModel?> _models = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Texture?> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<GrassModel, List<Matrix4x4>> _instances = [];
    private readonly List<Matrix4x4> _relBuffer = [];

    private GroundEffectDoodadTable? _doodads;
    private GroundEffectTextureTable? _recipes;

    private Vector2 _lastScatterXY;
    private bool _hasScattered;
    private int _missing;
    private readonly HashSet<string> _loggedMisses = new(StringComparer.OrdinalIgnoreCase);

    public bool Enabled { get; set; } = true;
    public int InstanceCount { get; private set; }
    public int ModelCount => _models.Count(m => m.Value is not null);
    public long TrianglesLastFrame { get; private set; }
    public bool DbcsReady => _recipes is not null && _recipes.Count > 0;
    public int EffectCount => _recipes?.Count ?? 0;

    /// <summary>Force the next frame to re-scatter (after a coverage knob changes).</summary>
    public void ForceRescatter() => _hasScattered = false;

    // ---------------- live tuning knobs ----------------
    public float Radius { get; set; } = 45f;          // scatter/draw radius (yards)
    public float DensityScale { get; set; } = 0.5f;   // multiplies the DBC density
    public int MaxPerCell { get; set; } = 6;          // cap doodads per ~4yd cell
    public int MaxInstances { get; set; } = 24000;    // hard cap
    public float RescatterDistance { get; set; } = 8f;// rescatter after moving this far
    public float Scale { get; set; } = 1.0f;          // global size multiplier
    public float ScaleJitter { get; set; } = 0.25f;   // +/- random size
    public float WindStrength { get; set; } = 0.06f;
    public float WindSpeed { get; set; } = 1.4f;
    public float FadeStart { get; set; } = 30f;
    public float FadeEnd { get; set; } = 45f;
    public float AlphaCutoff { get; set; } = 0.4f;
    public float Brightness { get; set; } = 1.0f;

    // atmosphere, pushed each frame
    public Vector3 SunDirection { get; set; } = Vector3.UnitZ;
    public Vector3 SunColor { get; set; } = Vector3.One;
    public float SunIntensity { get; set; } = 1f;
    public Vector3 AmbientColor { get; set; } = new(0.5f, 0.5f, 0.5f);
    public float AmbientIntensity { get; set; } = 0.6f;
    public Vector3 FogColor { get; set; } = new(0.56f, 0.71f, 0.85f);
    public float FogStart { get; set; } = 350f;
    public float FogEnd { get; set; } = 777f;
    public float Time { get; set; }

    public FoliageRenderer(GL gl, ClientConfig config)
    {
        _gl = gl;
        _config = config;
    }

    public void LoadShaders(string shaderDir)
    {
        _shader = Shader.FromFiles(_gl,
            Path.Combine(shaderDir, "grass.vert"),
            Path.Combine(shaderDir, "grass.frag"));
    }

    /// <summary>Load the two ground-effect DBCs from the client MPQs.</summary>
    public void LoadDbcs()
    {
        var dd = AdtTerrainReader.ReadFileFromMpqs(_config.ClientDataPath, GroundEffectDoodadTable.MpqPath);
        var dt = AdtTerrainReader.ReadFileFromMpqs(_config.ClientDataPath, GroundEffectTextureTable.MpqPath);
        if (dd is null || dt is null)
        {
            Console.WriteLine("[foliage] GroundEffect DBC(s) not found in the MPQs - foliage disabled");
            return;
        }
        _doodads = GroundEffectDoodadTable.Parse(dd);
        if (_doodads is null) { Console.WriteLine("[foliage] GroundEffectDoodad parse failed"); return; }
        _recipes = GroundEffectTextureTable.Parse(dt, _doodads);
        if (_recipes is null || _recipes.Count == 0)
            Console.WriteLine("[foliage] no usable ground-effect recipes - foliage will be empty");
    }

    /// <summary>
    /// Rebuild the resident grass instances around the camera. Throttled: only
    /// re-scatters after the camera has moved <see cref="RescatterDistance"/>.
    /// </summary>
    public void Scatter(Camera camera, AdtCache adts, IEnumerable<(int col, int row)> tiles, TerrainRenderer terrain)
    {
        if (!Enabled || _shader is null || _recipes is null || _recipes.Count == 0) return;

        var cam2 = new Vector2(camera.Position.X, camera.Position.Y);
        if (_hasScattered && Vector2.DistanceSquared(cam2, _lastScatterXY) < RescatterDistance * RescatterDistance)
            return;
        _lastScatterXY = cam2;
        _hasScattered = true;

        foreach (var list in _instances.Values) list.Clear();

        int total = 0;
        float radiusSq = Radius * Radius;
        const float cell = AdtTerrainReader.CELL_SIZE;
        float camX = camera.Position.X, camY = camera.Position.Y;

        foreach (var (col, row) in tiles)
        {
            var adt = adts.Get(col, row);
            if (adt?.Chunks is null) continue;

            double originX = (32 - row) * 533.33333;
            double originY = (32 - col) * 533.33333;

            foreach (var chunk in adt.Chunks)
            {
                if (chunk is null || chunk.Layers.Length == 0) continue;

                double chunkX = originX - chunk.IndexY * 8 * cell;
                double chunkY = originY - chunk.IndexX * 8 * cell;

                // Reject whole chunk if its centre is well outside the radius.
                double cxw = chunkX - 4 * cell, cyw = chunkY - 4 * cell;
                double ddx = cxw - camX, ddy = cyw - camY;
                float guard = Radius + 24f;
                if (ddx * ddx + ddy * ddy > guard * guard) continue;

                for (int cy = 0; cy < 8; cy++)
                for (int cx = 0; cx < 8; cx++)
                {
                    int dom = DominantLayer(chunk, cx, cy);
                    int effect = chunk.Layers[dom].EffectId;
                    if (effect < 0) continue;

                    var recipe = _recipes.Get(effect);
                    if (recipe is null || recipe.Doodads.Length == 0) continue;

                    int perCell = Math.Clamp((int)MathF.Round(recipe.Density * DensityScale), 0, MaxPerCell);
                    if (perCell <= 0) continue;

                    var rng = new Random(HashCode.Combine(col, row, chunk.IndexX, chunk.IndexY, cx, cy));
                    double cellX = chunkX - cy * cell;
                    double cellY = chunkY - cx * cell;

                    for (int i = 0; i < perCell; i++)
                    {
                        float px = (float)(cellX - rng.NextDouble() * cell);
                        float py = (float)(cellY - rng.NextDouble() * cell);

                        float dxp = px - camX, dyp = py - camY;
                        if (dxp * dxp + dyp * dyp > radiusSq) continue;

                        float? h = terrain.SampleHeight(px, py);
                        if (h is null) continue;

                        string modelPath = PickWeighted(recipe.Doodads, rng);
                        var gm = ResolveModel(modelPath);
                        if (gm is null) continue;

                        float yaw = (float)(rng.NextDouble() * Math.PI * 2.0);
                        float s = Scale * (1f - ScaleJitter + (float)rng.NextDouble() * ScaleJitter * 2f);

                        var m = YUpToZUp
                              * Matrix4x4.CreateScale(s)
                              * Matrix4x4.CreateRotationZ(yaw)
                              * Matrix4x4.CreateTranslation(px, py, h.Value);

                        if (!_instances.TryGetValue(gm, out var il)) { il = []; _instances[gm] = il; }
                        il.Add(m);

                        if (++total >= MaxInstances) goto done;
                    }
                }
            }
        }

    done:
        InstanceCount = total;
        Console.WriteLine($"[foliage] scattered {total} grass instance(s) over {_instances.Count(kv => kv.Value.Count > 0)} " +
            $"model(s); {ModelCount} model(s) loaded, {_missing} missing");
    }

    private static int DominantLayer(AdtTerrainReader.McnkChunk chunk, int cx, int cy)
    {
        int px = Math.Clamp(cx * 8 + 4, 0, 63);
        int py = Math.Clamp(cy * 8 + 4, 0, 63);

        int best = 0, bestA = 0, sum = 0;
        for (int li = 1; li < chunk.Layers.Length; li++)
        {
            var a = chunk.Layers[li].AlphaMap;
            if (a is null || a.Length < 64 * 64) continue;
            int val = a[py * 64 + px];
            sum += val;
            if (val > bestA) { bestA = val; best = li; }
        }
        int baseCoverage = 255 - Math.Min(sum, 255);   // layer 0 shows through the rest
        return bestA > baseCoverage ? best : 0;
    }

    private static string PickWeighted((string Model, int Weight)[] doodads, Random rng)
    {
        int total = 0;
        foreach (var d in doodads) total += d.Weight;
        if (total <= 0) return doodads[0].Model;
        int pick = rng.Next(total);
        foreach (var d in doodads)
        {
            if (pick < d.Weight) return d.Model;
            pick -= d.Weight;
        }
        return doodads[^1].Model;
    }

    private GrassModel? ResolveModel(string path)
    {
        if (_models.TryGetValue(path, out var cached)) return cached;

        byte[]? bytes = null;
        foreach (var cand in Candidates(path))
        {
            bytes = AdtTerrainReader.ReadFileFromMpqs(_config.ClientDataPath, cand);
            if (bytes is not null) break;
        }

        GrassModel? gm = null;
        string reason = "";
        if (bytes is null)
        {
            reason = "file not found in MPQs";
        }
        else
        {
            var m2 = M2Reader.Parse(bytes);
            if (m2 is null) reason = "M2Reader.Parse returned null (bad magic / version)";
            else if (!m2.IsValid) reason = $"M2 invalid (verts {m2.Vertices.Count}, idx {m2.Indices.Count})";
            else
            {
                gm = BuildModel(m2);
                if (gm is null)
                    reason = $"no drawable batch (batches {m2.Batches.Count}, submeshes {m2.Submeshes.Count}, " +
                             $"texRefs {m2.Textures.Count}, texLookup {m2.TextureLookup.Count})";
            }
        }

        _models[path] = gm;
        if (gm is null)
        {
            _missing++;
            if (_loggedMisses.Add(path))
                Console.WriteLine($"[foliage] model FAILED ('{path}'): {reason}");
        }
        return gm;
    }

    // GroundEffectDoodad stores BARE model filenames ("ElwGra01.mdl"), but the
    // models live under these folders in the MPQs and are .m2 there - not .mdl or
    // .mdx. The overwhelming majority are in World\NoDXT\Detail; a handful sit in
    // World\Detail. Without prepending a folder, every lookup reads from the
    // archive root and misses, so nothing scatters.
    private static readonly string[] FoliageDirs =
    {
        @"World\NoDXT\Detail\",
        @"World\Detail\",
    };

    private static IEnumerable<string> Candidates(string path)
    {
        // As-authored first, in case a DBC ever stores a full path.
        foreach (var p in ExtVariants(path)) yield return p;

        // Bare filename (the real case here): try it under each ground-effect
        // folder. ExtVariants also swaps .mdl/.mdx for the .m2 that is actually
        // in the archive.
        bool bare = !path.Contains('\\') && !path.Contains('/');
        if (bare)
            foreach (var dir in FoliageDirs)
                foreach (var p in ExtVariants(dir + path))
                    yield return p;
    }

    private static IEnumerable<string> ExtVariants(string path)
    {
        yield return path;
        if (path.EndsWith(".mdx", StringComparison.OrdinalIgnoreCase)) yield return path[..^4] + ".m2";
        else if (path.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase)) yield return path[..^4] + ".m2";
    }

    private unsafe GrassModel? BuildModel(M2Model m2)
    {
        int vcount = m2.Vertices.Count;
        if (vcount == 0 || m2.Indices.Count < 3) return null;

        var verts = new float[vcount * FloatsPerVertex];
        for (int i = 0; i < vcount; i++)
        {
            var v = m2.Vertices[i];
            int o = i * FloatsPerVertex;
            verts[o + 0] = v.PosX; verts[o + 1] = v.PosY; verts[o + 2] = v.PosZ;
            verts[o + 3] = v.NormX; verts[o + 4] = v.NormY; verts[o + 5] = v.NormZ;
            verts[o + 6] = v.TexU; verts[o + 7] = v.TexV;
        }
        var idx = m2.Indices.ToArray();

        var model = new GrassModel { TriangleCount = idx.Length / 3 };
        model.Attach(_gl);

        model.Vao = _gl.GenVertexArray();
        _gl.BindVertexArray(model.Vao);

        model.Vbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, model.Vbo);
        fixed (float* p = verts)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(verts.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);

        model.Ebo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, model.Ebo);
        fixed (ushort* p = idx)
            _gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(idx.Length * sizeof(ushort)), p, BufferUsageARB.StaticDraw);

        const uint stride = FloatsPerVertex * sizeof(float);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        _gl.EnableVertexAttribArray(2);
        _gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, (void*)(6 * sizeof(float)));

        model.InstanceVbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, model.InstanceVbo);
        const uint instanceStride = 16 * sizeof(float);
        for (uint r = 0; r < 4; r++)
        {
            uint loc = 3 + r;
            _gl.EnableVertexAttribArray(loc);
            _gl.VertexAttribPointer(loc, 4, VertexAttribPointerType.Float, false, instanceStride, (void*)(r * 4 * sizeof(float)));
            _gl.VertexAttribDivisor(loc, 1);
        }

        _gl.BindVertexArray(0);

        foreach (var b in m2.Batches)
        {
            if (b.SubmeshIndex >= m2.Submeshes.Count) continue;
            var sm = m2.Submeshes[b.SubmeshIndex];
            var tex = ResolveTexture(m2, b);
            if (tex is null) continue;   // no texture -> nothing to draw for grass
            model.Batches.Add(new Batch { IndexStart = sm.IndexStart, IndexCount = sm.IndexCount, Texture = tex });
        }

        if (model.Batches.Count == 0) { model.Dispose(); return null; }
        return model;
    }

    private Texture? ResolveTexture(M2Model m2, M2Batch b)
    {
        int ti = b.TextureIndex;
        if (ti < 0 || ti >= m2.TextureLookup.Count) return null;
        int real = m2.TextureLookup[ti];
        if (real < 0 || real >= m2.Textures.Count) return null;
        string path = m2.Textures[real].Filename;
        if (string.IsNullOrEmpty(path)) return null;

        if (_textures.TryGetValue(path, out var cached)) return cached;

        var px = AdtTerrainReader.ReadBlpPixels(_config.ClientDataPath, path);
        Texture? tex = px is null ? null : Texture.From2D(_gl, px.Value.bgra, px.Value.width, px.Value.height);
        _textures[path] = tex;
        return tex;
    }

    public unsafe void Render(Camera camera)
    {
        TrianglesLastFrame = 0;
        if (!Enabled || _shader is null || _instances.Count == 0) return;

        _shader.Use();
        _shader.Set("uViewProjection", camera.RelativeViewProjection);
        _shader.Set("uCameraOrigin", camera.Position);
        _shader.Set("uTime", Time);
        _shader.Set("uWindStrength", WindStrength);
        _shader.Set("uWindSpeed", WindSpeed);
        _shader.Set("uSunDirection", SunDirection);
        _shader.Set("uSunColor", SunColor);
        _shader.Set("uSunIntensity", SunIntensity);
        _shader.Set("uAmbientColor", AmbientColor);
        _shader.Set("uAmbientIntensity", AmbientIntensity);
        _shader.Set("uFogStart", FogStart);
        _shader.Set("uFogEnd", FogEnd);
        _shader.Set("uFogColor", FogColor);
        _shader.Set("uBrightness", Brightness);
        _shader.Set("uFadeStart", FadeStart);
        _shader.Set("uFadeEnd", FadeEnd);
        _shader.Set("uAlphaCutoff", AlphaCutoff);
        _shader.Set("uTexture", 0);

        // Opaque alpha-cutout: depth test + write, no blend. Grass cards are
        // two-sided, so face culling stays off.
        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);
        _gl.Disable(EnableCap.CullFace);

        var eye = camera.Position;
        foreach (var (model, list) in _instances)
        {
            if (list.Count == 0) continue;

            _relBuffer.Clear();
            foreach (var m in list)
            {
                var rm = m;
                rm.M41 -= eye.X; rm.M42 -= eye.Y; rm.M43 -= eye.Z;
                _relBuffer.Add(rm);
            }

            _gl.BindVertexArray(model.Vao);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, model.InstanceVbo);
            var span = CollectionsMarshal.AsSpan(_relBuffer);
            fixed (Matrix4x4* p = span)
                _gl.BufferData(BufferTargetARB.ArrayBuffer,
                    (nuint)(span.Length * sizeof(Matrix4x4)), p, BufferUsageARB.StreamDraw);

            uint ic = (uint)_relBuffer.Count;
            foreach (var b in model.Batches)
            {
                if (b.Texture is null) continue;
                b.Texture.Bind(0);
                _gl.DrawElementsInstanced(PrimitiveType.Triangles, (uint)b.IndexCount,
                    DrawElementsType.UnsignedShort, (void*)(b.IndexStart * sizeof(ushort)), ic);
                TrianglesLastFrame += (long)(b.IndexCount / 3) * ic;
            }
        }

        _gl.Enable(EnableCap.CullFace);
        _gl.BindVertexArray(0);
    }

    public void Dispose()
    {
        foreach (var m in _models.Values) m?.Dispose();
        _models.Clear();
        foreach (var t in _textures.Values) t?.Dispose();
        _textures.Clear();
        _instances.Clear();
    }
}

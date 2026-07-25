using System.Numerics;
using Silk.NET.OpenGL;
using MSUIClient.Engine;
using MSUIClient.Formats;
using Shader = MSUIClient.Engine.Shader;
using Texture = MSUIClient.Engine.Texture;

namespace MSUIClient.World;

/// <summary>
/// Renders open-world liquid (ADT MCLQ: lakes, rivers, ocean, slime, magma) as a
/// translucent, wave-displaced, depth-aware surface, plus a full-screen tint when
/// the camera is submerged.
///
/// Stage 2 ("look at wowee") - three things changed versus the flat stage-1 sheet:
///
///   1. DEPTH IS BAKED PER VERTEX. When the mesh is built, every water vertex
///      stores how deep the water is there: surfaceZ minus the terrain height at
///      the SAME grid position (the liquid 9x9 grid is index-aligned with the
///      chunk's MCVT outer vertices, so no spatial query is needed). The shader
///      uses that depth to fade the water transparent at the shoreline and darken
///      it in deep areas - the depth cue the flat version completely lacked.
///
///   2. THE SURFACE IS DISPLACED BY GERSTNER WAVES in the vertex shader, so it has
///      real, moving relief instead of reading as a painted line.
///
///   3. IT DRAWS AFTER THE CHARACTER (see GameLoop.Render). Water tests depth but
///      does not write it, so submerged parts of the character are correctly
///      covered by the surface in front of them, and a submerged camera gets the
///      underwater overlay this class also draws.
///
/// Placement still mirrors TerrainTile.Prepare exactly, so water aligns with the
/// ground:
///   originX = (32 - row) * 533.33333 ; originY = (32 - col) * 533.33333
///   worldX  = originX - (chunk.IndexY*8 + r) * CELL_SIZE
///   worldY  = originY - (chunk.IndexX*8 + c) * CELL_SIZE
///   worldZ  = MclqLayer.VertexHeights[r*9 + c]   (already absolute WoW Z)
///
/// WMO liquid (MLIQ - Stormwind canals), LiquidType.dbc colours, planar
/// reflection and screen-space refraction are still later stages.
/// </summary>
public sealed class LiquidRenderer : IDisposable
{
    private const int FloatsPerVertex = 5;   // position(3) + type(1) + depth(1)

    /// <summary>CPU-side copy of one water layer, kept so the camera/player can be
    /// tested for submersion without reading back from the GPU.</summary>
    private sealed class SurfaceLayer
    {
        public double OriginX, OriginY;
        public float Cell;
        public int GridRowBase, GridColBase;
        public required float[] Heights;   // 81, absolute WoW Z, row-major r*9+c
        public required bool[] Render;     // 64, r*8+c
        public byte Type;
    }

    private sealed class TileMesh : IDisposable
    {
        public uint Vao, Vbo, Ebo;
        public int IndexCount;
        public List<SurfaceLayer> Surfaces = [];
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

    private readonly GL _gl;
    private Shader? _shader;
    private Shader? _underwater;
    private uint _overlayVao;
    private readonly Dictionary<(int col, int row), TileMesh> _tiles = [];

    public bool Enabled { get; set; } = true;

    // --- Real vanilla animated liquid textures, loaded from the client MPQs ---
    // Vanilla stores each liquid as numbered BLP frames; we stack the frames of
    // one type into a single array texture and cross-fade between frames in the
    // shader. One array per basic type; a 1x1 dummy keeps every sampler bound.
    private Texture? _texWater, _texOcean, _texSlime, _texMagma, _dummyTex;
    private int _framesWater, _framesOcean, _framesSlime, _framesMagma;

    // Frame counts, exposed read-only for the Water Tuning HUD.
    public int WaterFrames  => _framesWater;
    public int OceanFrames  => _framesOcean;
    public int SlimeFrames  => _framesSlime;
    public int MagmaFrames  => _framesMagma;

    // ================= LIVE WATER TUNING KNOBS =================
    // Every hard-coded constant that used to live in water.frag/water.vert is now
    // one of these, pushed as a uniform each frame and driven by the Water Tuning
    // HUD (Program.WaterTuningWindow). Defaults reproduce the current look, so at
    // startup nothing changes until a slider is moved. Ranges are in the HUD.

    // Texture + animation
    public float TextureScale { get; set; } = 0.16f;   // world yards -> UV (smaller = bigger cells)
    public float AnimationFps { get; set; } = 12f;     // frames per second
    public float FrameBlend   { get; set; } = 0f;      // 0 = discrete swap, 1 = full cross-fade
    public float TexBrightness{ get; set; } = 1f;      // multiply texture colour
    public float TexContrast  { get; set; } = 1f;      // contrast around mid-grey
    public Vector3 TexTint    { get; set; } = Vector3.One;  // per-channel texture tint

    // Opacity
    public float Opacity      { get; set; } = 1.0f;    // deep-water alpha
    public float ShoreFade    { get; set; } = 0.85f;   // alpha fraction at the waterline
    public float ShoreWidth   { get; set; } = 1.2f;    // yards over which the shoreline softens

    // Depth
    public float DepthDarken  { get; set; } = 0.78f;   // deep-water brightness multiplier (higher = brighter)
    public float DepthRate    { get; set; } = 0.12f;   // how fast it darkens with depth

    // Lighting (flat - the texture carries the ripples). Tuned so the texture
    // shows near its own brightness at noon rather than being multiplied down.
    public float Brightness   { get; set; } = 0.90f;   // base surface brightness
    public float AmbientAmount{ get; set; } = 0.6f;    // ambient contribution
    public float SunAmount    { get; set; } = 0.30f;   // sun contribution
    public float SkySheen     { get; set; } = 0.14f;   // grazing-angle sky tint

    // Geometry waves (0 = flat still plane, the vanilla look)
    // ── Authored colours (PLAN_12) ──────────────────────────────────────────
    //
    // Defaults are water.frag's own invented constants, so these are inert until
    // WorldAtmosphere hands over something better. UseAuthoredColors AND
    // HasAuthoredColors must both be true before the shader looks at them, and
    // when either is false the shader arithmetic reduces to exactly what shipped.

    /// <summary>The A/B for PLAN_12. Off is bit-identical to the pre-PLAN_12 look.</summary>
    public bool UseAuthoredColors { get; set; } = true;

    /// <summary>Set from WorldAtmosphere.AuthoredWaterReady - see ApplyAtmosphere.</summary>
    public bool HasAuthoredColors { get; set; }

    public Vector3 OceanClose { get; set; } = new(0.06f, 0.20f, 0.28f);
    public Vector3 OceanFar   { get; set; } = new(0.02f, 0.09f, 0.16f);
    public Vector3 RiverClose { get; set; } = new(0.10f, 0.26f, 0.26f);
    public Vector3 RiverFar   { get; set; } = new(0.05f, 0.15f, 0.16f);

    public float OceanAlphaShallow { get; set; } = 0.85f;
    public float OceanAlphaDeep    { get; set; } = 1.00f;
    public float RiverAlphaShallow { get; set; } = 0.85f;
    public float RiverAlphaDeep    { get; set; } = 1.00f;

    /// <summary>True when the shader is actually being told to use the data.</summary>
    public bool AuthoredColorsActive => UseAuthoredColors && HasAuthoredColors;

    public float WaveAmplitude{ get; set; } = 0f;      // 0 = flat; >0 re-enables Gerstner displacement
    public float WaveSpeed    { get; set; } = 1.0f;    // wave scroll-speed multiplier

    /// <summary>Reset every knob to the startup default look.</summary>
    public void ResetTuning()
    {
        TextureScale = 0.16f; AnimationFps = 12f; FrameBlend = 0f;
        TexBrightness = 1f; TexContrast = 1f; TexTint = Vector3.One;
        Opacity = 1.0f; ShoreFade = 0.85f; ShoreWidth = 1.2f;
        DepthDarken = 0.78f; DepthRate = 0.12f;
        Brightness = 0.90f; AmbientAmount = 0.6f; SunAmount = 0.30f; SkySheen = 0.14f;
        WaveAmplitude = 0f; WaveSpeed = 1.0f;
    }

    // Shared time-of-day environment, pushed each frame from WorldAtmosphere.
    public Vector3 SunDirection { get; set; } = Vector3.UnitZ;
    public Vector3 SunColor { get; set; } = Vector3.One;
    public float SunIntensity { get; set; } = 1f;
    public Vector3 AmbientColor { get; set; } = new(0.5f, 0.5f, 0.5f);
    public float AmbientIntensity { get; set; } = 0.6f;
    public Vector3 FogColor { get; set; } = new(0.56f, 0.71f, 0.85f);
    public float FogStart { get; set; } = 350f;
    public float FogEnd { get; set; } = 777f;

    /// <summary>Seconds, for the wave animation. GameLoop accumulates it.</summary>
    public float Time { get; set; }

    public int TileCount => _tiles.Count;
    public int TrianglesLastFrame { get; private set; }

    public LiquidRenderer(GL gl) => _gl = gl;

    public void LoadShaders(string shaderDir)
    {
        _shader = Shader.FromFiles(_gl,
            Path.Combine(shaderDir, "water.vert"),
            Path.Combine(shaderDir, "water.frag"));
        _underwater = Shader.FromFiles(_gl,
            Path.Combine(shaderDir, "underwater.vert"),
            Path.Combine(shaderDir, "underwater.frag"));
        _overlayVao = _gl.GenVertexArray();
    }

    /// <summary>
    /// Load the real vanilla animated liquid textures from the client's MPQs.
    /// Vanilla stores each liquid as numbered BLP frames (e.g. river.1.blp,
    /// river.2.blp, ...); the exact folder/name varies, so we probe a small
    /// ordered candidate list per type, load every consecutive frame that
    /// resolves into one array texture, and LOG exactly which path won and how
    /// many frames it had. Nothing is assumed - the MPQ decides, and the console
    /// says what it found so a wrong path is obvious rather than silent.
    /// </summary>
    public void LoadLiquidTextures(string clientDataPath)
    {
        // Real vanilla paths per WoWMapViewer's liquid.cpp: the river/lake file is
        // "lake_a" inside the "river" folder (NOT "river"), ocean is "ocean_h".
        (_texWater, _framesWater) = LoadFrames(clientDataPath, "water", new[]
        {
            @"XTextures\river\lake_a.{0}.blp",   // vanilla river / lake / inland water
            @"XTextures\river\river.{0}.blp",
            @"XTextures\lake\lake.{0}.blp",
        });
        (_texOcean, _framesOcean) = LoadFrames(clientDataPath, "ocean", new[]
        {
            @"XTextures\ocean\ocean_h.{0}.blp",  // vanilla ocean
            @"XTextures\ocean\ocean.{0}.blp",
            @"XTextures\sea\sea.{0}.blp",
        });
        (_texSlime, _framesSlime) = LoadFrames(clientDataPath, "slime", new[]
        {
            @"XTextures\slime\slime.{0}.blp",
            @"XTextures\ooze\ooze.{0}.blp",
        });
        (_texMagma, _framesMagma) = LoadFrames(clientDataPath, "magma", new[]
        {
            @"XTextures\lava\lava.{0}.blp",
            @"XTextures\magma\magma.{0}.blp",
        });

        // 1x1 transparent array so every sampler unit has a valid binding even
        // when a type's textures were not found (the shader guards on the frame
        // count and never actually samples the dummy).
        _dummyTex = Texture.Array2D(_gl, new List<byte[]> { new byte[] { 0, 0, 0, 0 } },
            1, 1, mipmaps: false);

        if (_framesWater == 0)
            Console.WriteLine("[liquid-tex] water texture NOT found - the river falls back to the " +
                "procedural surface. Auto-discovering real paths from the MPQ (listfile) below.");

        // If anything failed to resolve, ask the MPQ what liquid textures it
        // actually contains so we can correct the candidate paths with certainty
        // instead of guessing. Harmless (and quiet) when every type loaded.
        if (_framesWater == 0 || _framesOcean == 0 || _framesSlime == 0 || _framesMagma == 0)
            DiscoverLiquidTexturePaths(clientDataPath);
    }

    /// <summary>
    /// Read the MPQ (listfile), if present, and print the liquid-texture entries
    /// so the real vanilla paths are known for certain. Many original Blizzard
    /// MPQs omit the listfile; if so, we say so rather than pretend.
    /// </summary>
    private static void DiscoverLiquidTexturePaths(string clientDataPath)
    {
        var lf = AdtTerrainReader.ReadFileFromMpqs(clientDataPath, "(listfile)");
        if (lf is null)
        {
            Console.WriteLine("[liquid-tex] no (listfile) in the MPQs - cannot auto-discover; " +
                "tell me the failures above and I'll correct the paths.");
            return;
        }

        var text = System.Text.Encoding.ASCII.GetString(lf);
        var hits = text.Split('\n', '\r')
            .Select(l => l.Trim())
            .Where(l => l.EndsWith(".blp", StringComparison.OrdinalIgnoreCase)
                     && (Has(l, "water") || Has(l, "river") || Has(l, "ocean")
                      || Has(l, "lava") || Has(l, "slime") || Has(l, "lake") || Has(l, "ooze")))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(l => l, StringComparer.OrdinalIgnoreCase)
            .Take(60)
            .ToList();

        Console.WriteLine($"[liquid-tex] (listfile) has {hits.Count} liquid-texture path(s):");
        foreach (var h in hits) Console.WriteLine("    " + h);

        static bool Has(string s, string sub) => s.IndexOf(sub, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// Probe the candidate templates in order; the first that yields at least one
    /// frame wins. Frames are read as consecutive numbers starting at 1 until one
    /// is missing (or the dimensions change). Returns the array texture + count,
    /// or (null, 0) if nothing resolved.
    /// </summary>
    private (Texture?, int) LoadFrames(string clientDataPath, string label, string[] templates)
    {
        const int maxFrames = 64;
        foreach (var template in templates)
        {
            var frames = new List<byte[]>();
            int w = 0, h = 0;
            for (int n = 1; n <= maxFrames; n++)
            {
                var px = AdtTerrainReader.ReadBlpPixels(clientDataPath, string.Format(template, n));
                if (px is null) break;
                if (frames.Count == 0) { w = px.Value.width; h = px.Value.height; }
                else if (px.Value.width != w || px.Value.height != h) break;
                frames.Add(px.Value.bgra);
            }

            if (frames.Count > 0)
            {
                Console.WriteLine($"[liquid-tex] {label}: {frames.Count} frame(s) {w}x{h} " +
                    $"from '{template.Replace("{0}", "N")}'");
                return (Texture.Array2D(_gl, frames, w, h), frames.Count);
            }
        }

        Console.WriteLine($"[liquid-tex] {label}: none of {templates.Length} candidate path(s) " +
            $"resolved - tried {string.Join("  ", templates.Select(t => t.Replace("{0}", "N")))}");
        return (null, 0);
    }

    /// <summary>Build/keep liquid meshes for exactly the resident tiles; dispose the rest.</summary>
    public void LoadForTiles(IEnumerable<(int col, int row)> tiles, AdtCache adts)
    {
        var want = new HashSet<(int col, int row)>(tiles);

        foreach (var key in _tiles.Keys.Where(k => !want.Contains(k)).ToArray())
        {
            _tiles[key].Dispose();
            _tiles.Remove(key);
        }

        int built = 0;
        foreach (var key in want)
        {
            if (_tiles.ContainsKey(key)) continue;
            var mesh = Build(adts.Get(key.col, key.row), key.col, key.row);
            if (mesh is not null) { _tiles[key] = mesh; built++; }
        }

        if (built > 0)
            Console.WriteLine($"[liquid] {built} tile(s) with water built, {_tiles.Count} resident");
    }

    private unsafe TileMesh? Build(AdtTerrainReader.AdtResult? adt, int col, int row)
    {
        if (adt?.Chunks is null) return null;

        double originX = (32 - row) * 533.33333;
        double originY = (32 - col) * 533.33333;
        const float cell = AdtTerrainReader.CELL_SIZE;

        var verts = new List<float>();
        var indices = new List<uint>();
        var surfaces = new List<SurfaceLayer>();

        foreach (var chunk in adt.Chunks)
        {
            if (chunk?.Liquid is null) continue;

            foreach (var layer in chunk.Liquid)
            {
                if (layer.VertexHeights.Length < 81 || layer.TileRender.Length < 64) continue;

                uint baseV = (uint)(verts.Count / FloatsPerVertex);
                float type = layer.LiquidType;

                for (int r = 0; r < 9; r++)
                for (int c = 0; c < 9; c++)
                {
                    double gridRow = chunk.IndexY * 8 + r;
                    double gridCol = chunk.IndexX * 8 + c;
                    float wx = (float)(originX - gridRow * cell);
                    float wy = (float)(originY - gridCol * cell);
                    float wz = layer.VertexHeights[r * 9 + c];

                    // Water depth here: surface minus the terrain height at the
                    // SAME grid vertex. The liquid grid and the chunk's MCVT outer
                    // grid are index-aligned, so this is a direct lookup, not a
                    // spatial query. Clamped to >= 0 (surface never below the bed).
                    float groundZ = chunk.BaseZ + chunk.OuterHeight(c, r);
                    float depth = MathF.Max(0f, wz - groundZ);

                    verts.Add(wx);
                    verts.Add(wy);
                    verts.Add(wz);
                    verts.Add(type);
                    verts.Add(depth);
                }

                bool any = false;
                for (int r = 0; r < 8; r++)
                for (int c = 0; c < 8; c++)
                {
                    if (!layer.TileRender[r * 8 + c]) continue;
                    any = true;
                    uint tl = baseV + (uint)(r * 9 + c);
                    uint tr = baseV + (uint)(r * 9 + c + 1);
                    uint bl = baseV + (uint)((r + 1) * 9 + c);
                    uint br = baseV + (uint)((r + 1) * 9 + c + 1);
                    indices.Add(tl); indices.Add(bl); indices.Add(tr);
                    indices.Add(tr); indices.Add(bl); indices.Add(br);
                }

                if (any)
                {
                    surfaces.Add(new SurfaceLayer
                    {
                        OriginX = originX,
                        OriginY = originY,
                        Cell = cell,
                        GridRowBase = chunk.IndexY * 8,
                        GridColBase = chunk.IndexX * 8,
                        Heights = (float[])layer.VertexHeights.Clone(),
                        Render = (bool[])layer.TileRender.Clone(),
                        Type = layer.LiquidType,
                    });
                }
            }
        }

        if (indices.Count == 0) return null;

        var va = verts.ToArray();
        var ia = indices.ToArray();

        var mesh = new TileMesh { IndexCount = ia.Length, Surfaces = surfaces };
        mesh.Attach(_gl);
        mesh.Vao = _gl.GenVertexArray();
        _gl.BindVertexArray(mesh.Vao);

        mesh.Vbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, mesh.Vbo);
        fixed (float* p = va)
            _gl.BufferData(BufferTargetARB.ArrayBuffer,
                (nuint)(va.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);

        mesh.Ebo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, mesh.Ebo);
        fixed (uint* p = ia)
            _gl.BufferData(BufferTargetARB.ElementArrayBuffer,
                (nuint)(ia.Length * sizeof(uint)), p, BufferUsageARB.StaticDraw);

        const uint stride = FloatsPerVertex * sizeof(float);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 1, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        _gl.EnableVertexAttribArray(2);
        _gl.VertexAttribPointer(2, 1, VertexAttribPointerType.Float, false, stride, (void*)(4 * sizeof(float)));
        _gl.BindVertexArray(0);

        return mesh;
    }

    public unsafe void Render(Camera camera)
    {
        TrianglesLastFrame = 0;
        if (!Enabled || _shader is null || _tiles.Count == 0) return;

        _shader.Use();
        _shader.Set("uViewProjection", camera.RelativeViewProjection);
        _shader.Set("uCameraOrigin", camera.Position);
        _shader.Set("uSunDirection", SunDirection);
        _shader.Set("uSunColor", SunColor);
        _shader.Set("uSunIntensity", SunIntensity);
        _shader.Set("uAmbientColor", AmbientColor);
        _shader.Set("uAmbientIntensity", AmbientIntensity);
        _shader.Set("uFogStart", FogStart);
        _shader.Set("uFogEnd", FogEnd);
        _shader.Set("uFogColor", FogColor);
        _shader.Set("uTime", Time);

        // Animated liquid textures: bind one array per basic type (dummy where a
        // type had none) and hand the shader the frame counts + animation rate so
        // it can cross-fade frames. World-space UV scale controls the tiling.
        _shader.Set("uTexScale", TextureScale);
        _shader.Set("uWaterFps", MathF.Max(AnimationFps, 0.01f));
        _shader.Set("uFrameBlend", FrameBlend);
        _shader.Set("uTexBright", TexBrightness);
        _shader.Set("uTexContrast", TexContrast);
        _shader.Set("uTexTint", TexTint);
        _shader.Set("uOpacity", Opacity);
        _shader.Set("uShoreFade", ShoreFade);
        _shader.Set("uShoreWidth", MathF.Max(ShoreWidth, 0.01f));
        _shader.Set("uDepthDarken", DepthDarken);
        _shader.Set("uDepthRate", DepthRate);
        _shader.Set("uBrightness", Brightness);
        _shader.Set("uAmbientAmt", AmbientAmount);
        _shader.Set("uSunAmt", SunAmount);
        _shader.Set("uSkySheen", SkySheen);
        // PLAN_12. One float decides it: 0 leaves every authored term mixed out
        // and the shader reduces to the constants it shipped with.
        _shader.Set("uAuthoredWater", AuthoredColorsActive ? 1f : 0f);
        _shader.Set("uOceanClose", OceanClose);
        _shader.Set("uOceanFar", OceanFar);
        _shader.Set("uRiverClose", RiverClose);
        _shader.Set("uRiverFar", RiverFar);
        _shader.Set("uOceanAlphaShallow", OceanAlphaShallow);
        _shader.Set("uOceanAlphaDeep", OceanAlphaDeep);
        _shader.Set("uRiverAlphaShallow", RiverAlphaShallow);
        _shader.Set("uRiverAlphaDeep", RiverAlphaDeep);

        _shader.Set("uWaveAmp", WaveAmplitude);
        _shader.Set("uWaveSpeed", WaveSpeed);
        BindLiquidTexture("uTexWater", _texWater, 0, "uFramesWater", _framesWater);
        BindLiquidTexture("uTexOcean", _texOcean, 1, "uFramesOcean", _framesOcean);
        BindLiquidTexture("uTexSlime", _texSlime, 2, "uFramesSlime", _framesSlime);
        BindLiquidTexture("uTexMagma", _texMagma, 3, "uFramesMagma", _framesMagma);
        _gl.ActiveTexture(TextureUnit.Texture0);

        // Transparent surface: blend, TEST depth so hills and the near side of the
        // character occlude it, but do NOT write depth so overlapping water and the
        // far side of a submerged character still blend. Both faces, so it reads
        // right from below when the camera dips under.
        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthMask(false);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _gl.Disable(EnableCap.CullFace);

        foreach (var mesh in _tiles.Values)
        {
            _gl.BindVertexArray(mesh.Vao);
            _gl.DrawElements(PrimitiveType.Triangles, (uint)mesh.IndexCount,
                DrawElementsType.UnsignedInt, (void*)0);
            TrianglesLastFrame += mesh.IndexCount / 3;
        }

        _gl.Enable(EnableCap.CullFace);
        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);
        _gl.BindVertexArray(0);
    }

    /// <summary>Bind one liquid array texture to a unit (the 1x1 dummy if the type
    /// had no textures) and set its sampler + frame-count uniforms.</summary>
    private void BindLiquidTexture(string sampler, Texture? tex, int unit, string framesName, int frames)
    {
        (tex ?? _dummyTex)?.Bind((uint)unit);
        _shader!.Set(sampler, unit);
        _shader.Set(framesName, frames);
    }

    /// <summary>
    /// Absolute surface height of the water directly over/under a world XY, if any
    /// resident water layer covers that point. Used to decide submersion.
    /// </summary>
    public bool TryGetSurface(float worldX, float worldY, out float height, out byte type)
    {
        foreach (var mesh in _tiles.Values)
        {
            foreach (var s in mesh.Surfaces)
            {
                double gr = (s.OriginX - worldX) / s.Cell - s.GridRowBase;
                double gc = (s.OriginY - worldY) / s.Cell - s.GridColBase;
                if (gr < 0 || gr > 8 || gc < 0 || gc > 8) continue;

                int cr = Math.Clamp((int)Math.Floor(gr), 0, 7);
                int cc = Math.Clamp((int)Math.Floor(gc), 0, 7);
                if (!s.Render[cr * 8 + cc]) continue;

                float fr = (float)(gr - cr);
                float fc = (float)(gc - cc);
                float h00 = s.Heights[cr * 9 + cc];
                float h01 = s.Heights[cr * 9 + cc + 1];
                float h10 = s.Heights[(cr + 1) * 9 + cc];
                float h11 = s.Heights[(cr + 1) * 9 + cc + 1];
                float top = h00 + (h01 - h00) * fc;
                float bot = h10 + (h11 - h10) * fc;
                height = top + (bot - top) * fr;
                type = s.Type;
                return true;
            }
        }

        height = 0f;
        type = 0;
        return false;
    }

    /// <summary>
    /// Draw the full-screen underwater tint. Call only when the camera eye is
    /// below a water surface. <paramref name="submersion"/> is how far below, in
    /// yards; deeper means a denser tint.
    /// </summary>
    public void RenderUnderwater(float submersion, byte type)
    {
        if (_underwater is null) return;

        _underwater.Use();
        _underwater.Set("uTint", UnderwaterTint(type));
        _underwater.Set("uSubmersion", MathF.Max(0f, submersion));
        _underwater.Set("uTime", Time);

        // Sits on top of everything: no depth test, no depth write, just blend.
        _gl.Disable(EnableCap.DepthTest);
        _gl.DepthMask(false);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _gl.Disable(EnableCap.CullFace);

        _gl.BindVertexArray(_overlayVao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        _gl.BindVertexArray(0);

        // Leave clean state for the debug/HUD passes that follow.
        _gl.Disable(EnableCap.Blend);
        _gl.Enable(EnableCap.CullFace);
        _gl.DepthMask(true);
        _gl.Enable(EnableCap.DepthTest);
    }

    /// <summary>Screen tint colour for being under a given liquid type.</summary>
    private static Vector3 UnderwaterTint(byte type) => type switch
    {
        3 => new Vector3(0.10f, 0.24f, 0.06f),   // slime - murky green
        6 => new Vector3(0.35f, 0.10f, 0.02f),   // magma - hot orange
        1 => new Vector3(0.05f, 0.16f, 0.26f),   // ocean - deep blue
        _ => new Vector3(0.09f, 0.20f, 0.28f),   // river/lake - blue-green
    };

    public void Dispose()
    {
        foreach (var m in _tiles.Values) m.Dispose();
        _tiles.Clear();
        _texWater?.Dispose(); _texOcean?.Dispose(); _texSlime?.Dispose(); _texMagma?.Dispose();
        _dummyTex?.Dispose();
        if (_overlayVao != 0) { _gl.DeleteVertexArray(_overlayVao); _overlayVao = 0; }
    }
}

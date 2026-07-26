using System.Numerics;
using Silk.NET.OpenGL;
using MSUIClient.Engine;
using MSUIClient.Formats;
using MSUIClient.World.Wmo;
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
/// WMO LIQUID (MLIQ) IS ALSO DRAWN HERE as of PLAN_15 — Stormwind's canals,
/// Ironforge's lava channels, Undercity's slime, fountains and indoor pools. It
/// deliberately shares this class's shader, uniforms, draw state and tuning knobs
/// rather than getting a pass of its own: a canal and the river outside the gate
/// are the same substance, and one pipeline is what keeps them looking like it.
/// The differences are that its vertices arrive already in world space from
/// WmoRenderer.EnumerateLiquid, and that its depth is a stand-in rather than a
/// measurement (see WmoDepth).
///
/// LiquidType.dbc colours, planar reflection and screen-space refraction are
/// still later stages.
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

    /// <summary>
    /// One placed MLIQ surface, kept CPU-side so submersion can be tested against
    /// canals and indoor pools as well as against open-world water (PLAN_15 D4).
    ///
    /// A WMO pool is a regular lattice carried through a RIGID transform, so the
    /// grid stays a lattice in world space and a point can be mapped back to
    /// (i, j) exactly with two dot products — no matrix inverse, no spatial index.
    /// That is why this stores basis vectors rather than an origin and a cell size
    /// the way <see cref="SurfaceLayer"/> does: the ADT grid is axis-aligned and a
    /// building is not.
    /// </summary>
    private sealed class WmoSurfaceLayer
    {
        public required Vector3 Origin;      // world position of grid vertex (0, 0)
        public required Vector2 UDir;        // unit XY direction of increasing i
        public required Vector2 VDir;        // unit XY direction of increasing j
        public required float Unit;          // yards per grid step
        public required int XVerts, YVerts;
        // Carried explicitly rather than derived as XVerts-1. That identity holds
        // in all 235 vanilla groups, but XTiles and XVerts are SEPARATE fields in
        // the file, and re-deriving one from the other is how a stride bug gets
        // in the day some model disagrees.
        public required int XTiles, YTiles;
        public required float[] Heights;     // world Z, row-major j*XVerts + i
        public required bool[] Render;       // row-major j*XTiles + i
        public required byte[] Types;        // shader-space type per tile
        public required Vector3 WorldMin, WorldMax;
        public string Owner = "";
    }

    private readonly GL _gl;
    private Shader? _shader;
    private Shader? _underwater;
    private uint _overlayVao;
    private readonly Dictionary<(int col, int row), TileMesh> _tiles = [];

    // ── WMO liquid: canals, fountains, indoor pools (PLAN_15) ───────────────
    private TileMesh? _wmoMesh;
    private readonly List<WmoSurfaceLayer> _wmoSurfaces = [];
    private int _wmoVersion = -1;

    /// <summary>PLAN_15 §7 step 6. Off must be bit-identical to the pre-PLAN_15 client.</summary>
    public bool DrawWmoLiquid { get; set; } = true;

    /// <summary>
    /// Assumed depth, in yards, of every WMO liquid vertex.
    ///
    /// **This is a stand-in and it is labelled as one (PLAN_15 D3).** The
    /// open-world mesh bakes real per-vertex depth by subtracting the terrain
    /// height at the same grid index — a free lookup, because the liquid grid and
    /// the MCVT grid are index-aligned. A WMO pool has no terrain under it; its
    /// floor is the building's own mesh, which needs a raycast per vertex against
    /// the collision BVH. That is the upgrade and it is not hard, it is just not
    /// stage 1.
    ///
    /// The default sits above <see cref="ShoreWidth"/> so a canal reads as open
    /// water rather than as one continuous shoreline. The visible cost of the
    /// shortcut is that a canal does not soften where it meets its wall.
    ///
    /// A tempting-looking alternative was rejected: MLIQ's CornerZ is NOT the pool
    /// floor. Measured, it equals the MINIMUM vertex height, so `height - CornerZ`
    /// is 0 across the 87% of surfaces that are flat — which would paint every
    /// pool entirely at shoreline alpha.
    /// </summary>
    public float WmoDepth { get; set; } = 3.0f;

    public int WmoSurfaceCount => _wmoSurfaces.Count;
    public int WmoTrianglesLastFrame { get; private set; }

    /// <summary>Type histogram of the resident WMO liquid, for PLAN_15 §7 step 2.</summary>
    public string WmoTypeSummary { get; private set; } = "none";

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
        WmoDepth = 3.0f;
        // NOTE: changing WmoDepth needs a rebuild, since it is baked per vertex.
        // The HUD slider must call UnloadWmoLiquid() so the next frame reloads.
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

    /// <summary>
    /// Drop every built liquid mesh. Needed on a map change for the same reason
    /// terrain needs it: LoadForTiles keeps any tile whose (col, row) key is
    /// still wanted, and those keys are shared across maps, so an overlapping
    /// range would leave Elwynn's river surface floating inside a dungeon.
    /// </summary>
    public void UnloadAll()
    {
        foreach (var mesh in _tiles.Values) mesh.Dispose();
        _tiles.Clear();
        UnloadWmoLiquid();
    }

    /// <summary>Drop WMO liquid and force the next LoadWmoLiquid to rebuild.</summary>
    public void UnloadWmoLiquid()
    {
        _wmoMesh?.Dispose();
        _wmoMesh = null;
        _wmoSurfaces.Clear();
        _wmoVersion = -1;
        WmoTypeSummary = "none";
    }

    /// <summary>
    /// Rebuild the WMO liquid mesh from the renderer's placed instances, but only
    /// when <paramref name="version"/> has moved (PLAN_15 D5).
    ///
    /// **Rebuild on the version, never on the tile-crossing event.** A WMO is
    /// placed the instant its ADT is read and its groups are adopted
    /// asynchronously over later frames, so a rebuild fired at the crossing runs
    /// before Model.Liquids exists and leaves a canal permanently dry. The bug
    /// does not throw and does not log; it just looks like the feature was never
    /// built. SYSTEM_INSTANCES.md records the identical race on async doors.
    ///
    /// Cost is irrelevant — 235 MLIQ groups is the entire game and a resident set
    /// is a handful — so this rebuilds wholesale rather than diffing.
    /// </summary>
    /// <returns>True when the mesh was actually rebuilt this call.</returns>
    public unsafe bool LoadWmoLiquid(int version, IEnumerable<WmoLiquidSurface> surfaces)
    {
        if (version == _wmoVersion) return false;
        _wmoVersion = version;

        _wmoMesh?.Dispose();
        _wmoMesh = null;
        _wmoSurfaces.Clear();

        var verts = new List<float>();
        var indices = new List<uint>();
        int hidden = 0, drawn = 0;
        var typeCount = new Dictionary<byte, int>();

        foreach (var s in surfaces)
        {
            if (s.XVerts < 2 || s.YVerts < 2) continue;

            uint baseV = (uint)(verts.Count / FloatsPerVertex);

            // Per-vertex type: a tile carries the type, a vertex does not, so take
            // the type of any drawn tile touching it. Mixed-substance surfaces do
            // not occur in 1.12 (every measured group is one substance), so this
            // is a formality rather than a blend.
            var vertType = new byte[s.XVerts * s.YVerts];
            var heights = new float[s.XVerts * s.YVerts];
            var render = new bool[s.XTiles * s.YTiles];
            var tileTypes = new byte[s.XTiles * s.YTiles];

            for (int j = 0; j < s.YTiles; j++)
            for (int i = 0; i < s.XTiles; i++)
            {
                bool vis = !s.IsHidden(i, j);
                render[j * s.XTiles + i] = vis;
                byte t = s.ShaderType(i, j);
                tileTypes[j * s.XTiles + i] = t;
                if (!vis) { hidden++; continue; }
                drawn++;
                typeCount[t] = typeCount.TryGetValue(t, out int c) ? c + 1 : 1;
                vertType[j * s.XVerts + i] = t;
                vertType[j * s.XVerts + i + 1] = t;
                vertType[(j + 1) * s.XVerts + i] = t;
                vertType[(j + 1) * s.XVerts + i + 1] = t;
            }

            var wMin = new Vector3(float.MaxValue);
            var wMax = new Vector3(float.MinValue);

            for (int j = 0; j < s.YVerts; j++)
            for (int i = 0; i < s.XVerts; i++)
            {
                var p = s.Vertices[j * s.XVerts + i];
                heights[j * s.XVerts + i] = p.Z;
                wMin = Vector3.Min(wMin, p);
                wMax = Vector3.Max(wMax, p);

                verts.Add(p.X);
                verts.Add(p.Y);
                verts.Add(p.Z);
                verts.Add(vertType[j * s.XVerts + i] == 0 ? 4f : vertType[j * s.XVerts + i]);
                verts.Add(WmoDepth);   // PLAN_15 D3 — a labelled stand-in, see WmoDepth
            }

            bool any = false;
            for (int j = 0; j < s.YTiles; j++)
            for (int i = 0; i < s.XTiles; i++)
            {
                if (!render[j * s.XTiles + i]) continue;
                any = true;
                uint tl = baseV + (uint)(j * s.XVerts + i);
                uint tr = baseV + (uint)(j * s.XVerts + i + 1);
                uint bl = baseV + (uint)((j + 1) * s.XVerts + i);
                uint br = baseV + (uint)((j + 1) * s.XVerts + i + 1);
                indices.Add(tl); indices.Add(bl); indices.Add(tr);
                indices.Add(tr); indices.Add(bl); indices.Add(br);
            }

            if (!any) continue;

            // Lattice basis for the point query. The instance transform is rigid,
            // so |v(1,0) - v(0,0)| is the authored tile unit and the two in-plane
            // directions stay orthonormal. Falls back to world axes on a
            // degenerate grid rather than producing NaNs downstream.
            var o = s.Vertices[0];
            var du3 = s.Vertices[1] - o;
            var dv3 = s.Vertices[s.XVerts] - o;
            float unit = du3.Length();
            var du = new Vector2(du3.X, du3.Y);
            var dv = new Vector2(dv3.X, dv3.Y);
            if (unit < 1e-4f || du.LengthSquared() < 1e-8f || dv.LengthSquared() < 1e-8f)
            {
                unit = 33.3333f / 8.0f;
                du = Vector2.UnitX;
                dv = Vector2.UnitY;
            }
            else
            {
                du = Vector2.Normalize(du);
                dv = Vector2.Normalize(dv);
            }

            _wmoSurfaces.Add(new WmoSurfaceLayer
            {
                Origin = o,
                UDir = du,
                VDir = dv,
                Unit = unit,
                XVerts = s.XVerts,
                YVerts = s.YVerts,
                XTiles = s.XTiles,
                YTiles = s.YTiles,
                Heights = heights,
                Render = render,
                Types = tileTypes,
                WorldMin = wMin,
                WorldMax = wMax,
                Owner = $"{Path.GetFileNameWithoutExtension(s.ModelPath)} [{s.GroupIndex}] '{s.GroupName}'",
            });
        }

        WmoTypeSummary = typeCount.Count == 0
            ? "none"
            : string.Join(" ", typeCount.OrderBy(kv => kv.Key)
                .Select(kv => $"{NameOfShaderType(kv.Key)}={kv.Value}"));

        if (indices.Count == 0)
        {
            if (_wmoSurfaces.Count == 0 && drawn == 0 && hidden == 0) return true;
            Console.WriteLine("[wmo-liquid] no drawable tiles");
            return true;
        }

        var va = verts.ToArray();
        var ia = indices.ToArray();

        var mesh = new TileMesh { IndexCount = ia.Length };
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

        _wmoMesh = mesh;

        Console.WriteLine(
            $"[wmo-liquid] {_wmoSurfaces.Count} surface(s), {drawn} tile(s) drawn, " +
            $"{hidden} hidden, {ia.Length / 3:N0} triangles");
        Console.WriteLine($"[wmo-liquid] types {WmoTypeSummary}");
        return true;
    }

    private static string NameOfShaderType(byte t) => t switch
    {
        1 => "ocean",
        3 => "slime",
        6 => "magma",
        _ => "water",
    };

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
        WmoTrianglesLastFrame = 0;
        bool anyWmo = DrawWmoLiquid && _wmoMesh is not null;
        if (!Enabled || _shader is null || (_tiles.Count == 0 && !anyWmo)) return;

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

        // WMO liquid rides the SAME shader, uniforms and draw state as the
        // open-world surface (PLAN_15 D1). That is the point: a canal and the
        // river outside the gate are the same substance and must stay looking
        // like it through every future tuning pass.
        if (anyWmo)
        {
            _gl.BindVertexArray(_wmoMesh!.Vao);
            _gl.DrawElements(PrimitiveType.Triangles, (uint)_wmoMesh.IndexCount,
                DrawElementsType.UnsignedInt, (void*)0);
            WmoTrianglesLastFrame = _wmoMesh.IndexCount / 3;
            TrianglesLastFrame += WmoTrianglesLastFrame;
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
        => TryGetSurface(worldX, worldY, float.NegativeInfinity, out height, out type);

    /// <summary>
    /// The liquid surface a point at <paramref name="worldZ"/> is submerged under:
    /// the LOWEST surface strictly above it, across both open-world water and WMO
    /// liquid. Pass <c>float.NegativeInfinity</c> for "the highest surface here",
    /// which is what the HUD read-out wants.
    ///
    /// PLAN_15 D4. Two things changed here and both were latent bugs, not new
    /// requirements:
    ///
    /// 1. **WMO surfaces are now in scope.** Without this you swim through
    ///    Stormwind's canal with a dry screen.
    /// 2. **It no longer returns the first hit it finds.** With overlapping
    ///    surfaces — a canal above a lake, an aqueduct above a river — the first
    ///    match was whichever tile happened to be first in a dictionary, which is
    ///    not a stable answer and not necessarily the one you are under.
    /// </summary>
    public bool TryGetSurface(float worldX, float worldY, float worldZ,
                              out float height, out byte type)
    {
        bool found = false;
        float best = 0f;
        byte bestType = 0;

        void Consider(float h, byte t)
        {
            if (h <= worldZ) return;                       // below the eye: not what we are in
            if (found && h >= best) return;                // keep the lowest qualifying surface
            found = true; best = h; bestType = t;
        }

        foreach (var mesh in _tiles.Values)
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
            Consider(top + (bot - top) * fr, s.Type);
        }

        foreach (var s in _wmoSurfaces)
        {
            // Cheap reject first: most of the map is not inside any pool.
            if (worldX < s.WorldMin.X - s.Unit || worldX > s.WorldMax.X + s.Unit) continue;
            if (worldY < s.WorldMin.Y - s.Unit || worldY > s.WorldMax.Y + s.Unit) continue;

            // Rigid lattice, so (i, j) comes back from two dot products.
            var d = new Vector2(worldX - s.Origin.X, worldY - s.Origin.Y);
            float fi = Vector2.Dot(d, s.UDir) / s.Unit;
            float fj = Vector2.Dot(d, s.VDir) / s.Unit;
            if (fi < 0 || fj < 0 || fi > s.XVerts - 1 || fj > s.YVerts - 1) continue;

            int i0 = Math.Clamp((int)MathF.Floor(fi), 0, s.XVerts - 2);
            int j0 = Math.Clamp((int)MathF.Floor(fj), 0, s.YVerts - 2);
            if (i0 >= s.XTiles || j0 >= s.YTiles) continue;
            int xt = s.XTiles;
            if (!s.Render[j0 * xt + i0]) continue;

            float ti = fi - i0;
            float tj = fj - j0;
            float a = s.Heights[j0 * s.XVerts + i0];
            float b = s.Heights[j0 * s.XVerts + i0 + 1];
            float c = s.Heights[(j0 + 1) * s.XVerts + i0];
            float e = s.Heights[(j0 + 1) * s.XVerts + i0 + 1];
            float lo = a + (b - a) * ti;
            float hi = c + (e - c) * ti;
            Consider(lo + (hi - lo) * tj, s.Types[j0 * xt + i0]);
        }

        height = best;
        type = bestType;
        return found;
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
        _wmoMesh?.Dispose();
        _wmoMesh = null;
        _wmoSurfaces.Clear();
        _texWater?.Dispose(); _texOcean?.Dispose(); _texSlime?.Dispose(); _texMagma?.Dispose();
        _dummyTex?.Dispose();
        if (_overlayVao != 0) { _gl.DeleteVertexArray(_overlayVao); _overlayVao = 0; }
    }
}

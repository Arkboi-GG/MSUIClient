using System.Numerics;
using Silk.NET.OpenGL;
using MSUIClient.Engine;
using MSUIClient.Formats;
using Shader = MSUIClient.Engine.Shader;

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
        if (_overlayVao != 0) { _gl.DeleteVertexArray(_overlayVao); _overlayVao = 0; }
    }
}

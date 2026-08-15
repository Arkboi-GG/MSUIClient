using System.Diagnostics;
using System.Numerics;
using Silk.NET.OpenGL;
using MSUIClient.Engine;
using MSUIClient.Formats;
using Shader = MSUIClient.Engine.Shader;
using Texture = MSUIClient.Engine.Texture;

namespace MSUIClient.World;

/// <summary>
/// The per-zone SKYBOX (PLAN_18 Phase 2). Some zones/states author a full sky
/// MODEL in LightSkybox.dbc (LightParams.lightSkyboxID -> an M2/MDX, e.g. the
/// ghost world's DeathClouds, the Caverns of Time sky): a dome mesh with the sky
/// painted on it, its cloud layers scrolled by texture transforms. This renders
/// that model camera-centred, over the sky gradient + clouds and BEFORE the world,
/// depth-write off, so it reads as an infinitely far sky the world draws over.
///
/// ADDITION, not a byte port: benilla renders the procedural sky + clouds but not
/// the LightSkybox zone models, so there is no reference render to transcribe. The
/// mesh + textures + texture-transform scroll come from the real M2 (M2Reader,
/// M2TrackSampling - the same verified UV-scroll path the doodads use); the
/// camera-centred, unlit, depth-off framing is the standard skybox recipe.
///
/// Owner night-lighting law untouched: the skybox draws into the sky only.
/// </summary>
public sealed class SkyboxRenderer : IDisposable
{
    private readonly GL _gl;
    private readonly ClientConfig _config;
    private Shader? _shader;
    private uint _vao, _vbo, _ebo;

    public bool Enabled { get; set; } = true;

    /// <summary>Dev override: force this skybox model path regardless of the zone. Null = follow the data.</summary>
    public string? ForceModelPath { get; set; }

    /// <summary>The model path currently loaded (for the probe / diagnostics), or null.</summary>
    public string? LoadedPath { get; private set; }

    /// <summary>True once a model is loaded and drawable.</summary>
    public bool HasModel => _batches.Count > 0 && _model is not null;

    private M2Model? _model;
    private readonly List<Batch> _batches = new();
    private readonly Dictionary<string, Texture?> _textures = new(StringComparer.OrdinalIgnoreCase);
    private float _modelScale = 1f;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastMs;
    private float _time;

    private struct Batch
    {
        public int IndexStart, IndexCount;
        public Texture? Tex;
        public int Blend;
        public bool TwoSided;
        public M2AnimTrack<Vector3>? UvTrack;
    }

    public SkyboxRenderer(GL gl, ClientConfig config)
    {
        _gl = gl;
        _config = config;
    }

    public void LoadShaders(string shaderDir)
    {
        _shader = Shader.FromFiles(_gl,
            Path.Combine(shaderDir, "skybox.vert"),
            Path.Combine(shaderDir, "skybox.frag"));
    }

    /// <summary>
    /// Load (or switch to) a skybox model by its MPQ path. A no-op if the same path
    /// is already loaded. Best-effort: a failure clears the model and the renderer
    /// simply draws nothing.
    /// </summary>
    public unsafe void SetModel(MpqMount mpq, string? path)
    {
        path = string.IsNullOrWhiteSpace(path) ? null : path;
        if (path == LoadedPath) return;

        ClearModel();
        LoadedPath = path;
        if (path is null) return;

        try
        {
            byte[]? bytes = mpq.ReadFile(path) ?? mpq.ReadFile(Path.ChangeExtension(path, ".m2"))
                ?? mpq.ReadFile(Path.ChangeExtension(path, ".mdx"));
            if (bytes is null) { Console.WriteLine($"[skybox] {path} not found in the MPQs"); LoadedPath = null; return; }

            M2Model? model = M2Reader.Parse(bytes);
            if (model is null || !model.IsValid) { Console.WriteLine($"[skybox] {path} parsed but has no geometry"); LoadedPath = null; return; }
            _model = model;

            // Interleave [pos.xyz, uv.xy]. Verts are M2Reader's glTF Y-up; the model
            // matrix rotates them back to the world's Z-up at draw time.
            float maxR = 0.01f;
            var verts = new float[model.Vertices.Count * 5];
            for (int i = 0; i < model.Vertices.Count; i++)
            {
                var v = model.Vertices[i];
                int o = i * 5;
                verts[o + 0] = v.PosX; verts[o + 1] = v.PosY; verts[o + 2] = v.PosZ;
                verts[o + 3] = v.TexU; verts[o + 4] = v.TexV;
                float r = MathF.Sqrt(v.PosX * v.PosX + v.PosY * v.PosY + v.PosZ * v.PosZ);
                if (r > maxR) maxR = r;
            }
            // Scale the dome to a fixed sky radius safely inside the far plane and
            // outside the near plane (it is camera-centred, so this is the only
            // constraint - depth is off, so it never clips the world).
            _modelScale = 900f / maxR;

            ushort[] indices = model.Indices.ToArray();

            _vao = _gl.GenVertexArray();
            _gl.BindVertexArray(_vao);
            _vbo = _gl.GenBuffer();
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
            fixed (float* p = verts)
                _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(verts.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);
            _ebo = _gl.GenBuffer();
            _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
            fixed (ushort* p = indices)
                _gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(indices.Length * sizeof(ushort)), p, BufferUsageARB.StaticDraw);
            _gl.EnableVertexAttribArray(0);
            _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 5 * sizeof(float), (void*)0);
            _gl.EnableVertexAttribArray(1);
            _gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 5 * sizeof(float), (void*)(3 * sizeof(float)));
            _gl.BindVertexArray(0);

            foreach (var b in model.Batches)
            {
                if (b.SubmeshIndex >= model.Submeshes.Count) continue;
                var sm = model.Submeshes[b.SubmeshIndex];

                string? texPath = null;
                if (b.TextureIndex < model.TextureLookup.Count)
                {
                    int t = model.TextureLookup[b.TextureIndex];
                    if (t >= 0 && t < model.Textures.Count) texPath = model.Textures[t].Filename;
                }
                int blend = b.MaterialIndex < model.RenderFlags.Count ? model.RenderFlags[b.MaterialIndex].BlendingMode : 0;
                bool twoSided = b.MaterialIndex < model.RenderFlags.Count && model.RenderFlags[b.MaterialIndex].TwoSided;

                int uvAnim = model.GetTextureTransformForBatch(b);
                M2AnimTrack<Vector3>? uvTrack = uvAnim >= 0 ? model.TextureTransforms[uvAnim].Translation : null;
                if (uvTrack is not null && uvTrack.Keys.Count == 0) uvTrack = null;

                _batches.Add(new Batch
                {
                    IndexStart = sm.IndexStart,
                    IndexCount = sm.IndexCount,
                    Tex = LoadTexture(mpq, texPath),
                    Blend = blend,
                    TwoSided = twoSided,
                    UvTrack = uvTrack,
                });
            }

            Console.WriteLine($"[skybox] {Path.GetFileName(path)} loaded: {_batches.Count} batch(es), " +
                              $"{model.Vertices.Count} vert(s), scale {_modelScale:F3}");
        }
        catch (Exception e)
        {
            Console.WriteLine($"[skybox] {path} unavailable: {e.Message}");
            ClearModel();
        }
    }

    private Texture? LoadTexture(MpqMount mpq, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (_textures.TryGetValue(path, out var cached)) return cached;
        Texture? tex = null;
        try
        {
            byte[]? blp = mpq.ReadFile(path);
            if (blp is not null)
            {
                byte[] bgra = BlpDecoder.GetPixels(blp, 0, out int w, out int h);
                tex = Texture.From2D(_gl, bgra, w, h, mipmaps: true, repeat: true);
            }
        }
        catch (Exception e) { Console.WriteLine($"[skybox] texture '{path}' failed: {e.Message}"); }
        _textures[path] = tex;
        return tex;
    }

    /// <summary>Draw the skybox. Must run AFTER the sky gradient/clouds and BEFORE the world.</summary>
    public unsafe void Render(Camera camera)
    {
        if (!Enabled || _shader is null || _model is null || _batches.Count == 0) return;

        double now = _clock.Elapsed.TotalMilliseconds;
        float dt = (float)((now - _lastMs) / 1000.0);
        _lastMs = now;
        if (dt > 0f && dt < 1f) _time += dt;

        // Camera-centred, Y-up model -> Z-up world, scaled to the sky radius.
        Matrix4x4 model = Matrix4x4.CreateRotationX(MathF.PI * 0.5f)
                        * Matrix4x4.CreateScale(_modelScale)
                        * Matrix4x4.CreateTranslation(camera.Position);
        Matrix4x4 mvp = model * camera.View * camera.Projection;

        _gl.Disable(EnableCap.DepthTest);
        _gl.DepthMask(false);
        _gl.Disable(EnableCap.CullFace);       // viewed from inside the dome
        _gl.Enable(EnableCap.Blend);

        _shader.Use();
        _shader.Set("uMVP", mvp);
        _shader.Set("uTex", 0);

        _gl.BindVertexArray(_vao);
        // Opaque/alpha-key first, then blended/additive over them (M2 transparency rule).
        for (int pass = 0; pass < 2; pass++)
        {
            bool transparentPass = pass == 1;
            foreach (var b in _batches)
            {
                bool additive = b.Blend is 3 or 4;
                bool blended = b.Blend >= 2;
                if (blended != transparentPass) continue;

                bool alphaKey = b.Blend == 1;
                if (additive) _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);
                else if (blended) _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                else _gl.BlendFunc(BlendingFactor.One, BlendingFactor.Zero);

                Vector2 uv = Vector2.Zero;
                if (b.UvTrack is not null)
                {
                    Vector3 t = M2TrackSampling.Vector(b.UvTrack, _model, 0, _time, Vector3.Zero);
                    uv = new Vector2(t.X, t.Y);
                }

                _shader.Set("uUvOffset", uv);
                _shader.Set("uColor", new Vector4(1f, 1f, 1f, 1f));
                _shader.Set("uAlphaCut", alphaKey ? 0.5f : 0.0f);

                b.Tex?.Bind(0);
                _gl.DrawElements(PrimitiveType.Triangles, (uint)b.IndexCount,
                    DrawElementsType.UnsignedShort, (void*)(b.IndexStart * sizeof(ushort)));
            }
        }
        _gl.BindVertexArray(0);

        // Restore the state the world pass expects.
        _gl.DepthMask(true);
        _gl.Enable(EnableCap.DepthTest);
        _gl.Enable(EnableCap.CullFace);
    }

    private void ClearModel()
    {
        foreach (var t in _textures.Values) t?.Dispose();
        _textures.Clear();
        if (_vbo != 0) { _gl.DeleteBuffer(_vbo); _vbo = 0; }
        if (_ebo != 0) { _gl.DeleteBuffer(_ebo); _ebo = 0; }
        if (_vao != 0) { _gl.DeleteVertexArray(_vao); _vao = 0; }
        _batches.Clear();
        _model = null;
        LoadedPath = null;
    }

    public void Dispose()
    {
        ClearModel();
        _shader?.Dispose();
    }
}

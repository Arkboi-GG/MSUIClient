using System.Numerics;
using Silk.NET.OpenGL;
using MSUIClient.Formats;

namespace MSUIClient.Engine;

// The 1.12 login-screen glue scene: Interface\Glues\Models\UI_MainMenu\UI_MainMenu.m2
// (AccountLogin.xml's ModelFFX — the burning gate), rendered fullscreen through the
// model's own authored camera with the authored orange fog, exactly like benilla's
// glue booth (benilla/src/portrait/glue_booth.rs + login/mod.rs).
//
// STEP 1 of the animated scene: static mesh + authored camera + fog. The fire
// PARTICLE EMITTERS (his ParticleRenderer), sequence-0 animation, and the Blizzard
// logo overlay layer on top of this next. Best-effort: any failure leaves _ok false
// and the scene simply doesn't draw (the caller keeps its plain backdrop).

public sealed class GlueScene : IDisposable
{
    private const string ModelPath = @"Interface\Glues\Models\UI_MainMenu\UI_MainMenu.m2";
    private const string ModelPathMdx = @"Interface\Glues\Models\UI_MainMenu\UI_MainMenu.mdx";

    // benilla glue_booth.rs: MainMenu fog = colour (0.25, 0.06, 0.015), far 1200.
    private static readonly Vector3 FogColor = new(0.25f, 0.06f, 0.015f);
    private const float FogNear = 30f;
    private const float FogFar = 1200f;

    private readonly GL _gl;
    private Shader? _shader;
    private uint _vao, _vbo, _ebo;
    private readonly List<Batch> _batches = new();
    private readonly Dictionary<string, Texture?> _textures = new(StringComparer.OrdinalIgnoreCase);

    private Vector3 _eye = new(0, 1, 3);
    private Vector3 _target = Vector3.Zero;
    private float _fovDiag = 0.9f;   // radians (diagonal); overwritten by the authored camera
    private float _near = 0.1f, _far = FogFar;

    public bool Ok { get; private set; }

    private struct Batch
    {
        public int IndexStart, IndexCount;
        public Texture? Tex;
        public int Blend;      // M2 blend mode (0 opaque, 1 alpha-key, 2 alpha, 3/4 additive, ...)
    }

    public GlueScene(GL gl, MpqMount mpq)
    {
        _gl = gl;
        try { Build(mpq); Ok = _batches.Count > 0; }
        catch (Exception e) { Console.WriteLine($"[glue] scene unavailable: {e.Message}"); Ok = false; }
        if (Ok) Console.WriteLine($"[glue] UI_MainMenu loaded: {_batches.Count} batch(es), eye {_eye}, target {_target}, fov {_fovDiag:F2}");
    }

    private unsafe void Build(MpqMount mpq)
    {
        byte[]? bytes = mpq.ReadFile(ModelPath) ?? mpq.ReadFile(ModelPathMdx);
        if (bytes is null) { Console.WriteLine("[glue] UI_MainMenu.m2 not found in the MPQs"); return; }

        M2Model model = M2Reader.Parse(bytes);
        if (!model.IsValid) { Console.WriteLine("[glue] UI_MainMenu.m2 parsed but has no geometry"); return; }

        // Interleave [pos.xyz, uv.xy]. Verts are already glTF Y-up (M2Reader converts at parse).
        var verts = new float[model.Vertices.Count * 5];
        for (int i = 0; i < model.Vertices.Count; i++)
        {
            var v = model.Vertices[i];
            int o = i * 5;
            verts[o + 0] = v.PosX; verts[o + 1] = v.PosY; verts[o + 2] = v.PosZ;
            verts[o + 3] = v.TexU; verts[o + 4] = v.TexV;
        }
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

        // One draw per batch: resolve its texture (batch.TextureIndex -> TextureLookup -> Textures)
        // and its blend mode (batch.MaterialIndex -> RenderFlags).
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

            _batches.Add(new Batch
            {
                IndexStart = sm.IndexStart,
                IndexCount = sm.IndexCount,
                Tex = LoadTexture(mpq, texPath),
                Blend = blend,
            });
        }

        // Authored camera (glue scenes carry one camera indexed directly — the lookup slot is 0xffff).
        if (TryParseCamera(bytes, out var eye, out var target, out float fov, out float near, out float far))
        {
            _eye = eye; _target = target; _fovDiag = fov;
            _near = near > 0.01f ? near : 0.1f;
            _far = far > _near ? far : FogFar;
        }
        else
        {
            FrameFromBounds(model);   // fallback: frame the whole model from its vertex bounds
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
        catch (Exception e) { Console.WriteLine($"[glue] texture '{path}' failed: {e.Message}"); }
        _textures[path] = tex;
        return tex;
    }

    /// <summary>Draw the glue scene fullscreen. viewport is the window size in pixels.</summary>
    public void Render(int viewportW, int viewportH)
    {
        if (!Ok || _shader is null && !CreateShader()) return;
        if (viewportH <= 0) return;

        float aspect = viewportW / (float)viewportH;
        // The M2 fov is DIAGONAL in the client's convention; convert to vertical for a standard projection.
        float t = MathF.Tan(_fovDiag * 0.5f) / MathF.Sqrt(1f + aspect * aspect);
        float fovy = 2f * MathF.Atan(t);
        fovy = Math.Clamp(fovy, 0.05f, 3.0f);

        Matrix4x4 view = Matrix4x4.CreateLookAt(_eye, _target, Vector3.UnitY);
        Matrix4x4 proj = Matrix4x4.CreatePerspectiveFieldOfView(fovy, aspect, _near, _far);
        Matrix4x4 mvp = view * proj;

        _gl.Viewport(0, 0, (uint)viewportW, (uint)viewportH);
        _gl.Clear((uint)ClearBufferMask.DepthBufferBit);
        _gl.Enable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.CullFace);           // glue meshes are two-sided; skip winding worries
        _gl.Enable(EnableCap.Blend);

        _shader!.Use();
        _shader.Set("uMVP", mvp);
        _shader.Set("uEye", _eye);
        _shader.Set("uFogColor", FogColor);
        _shader.Set("uFogNear", FogNear);
        _shader.Set("uFogFar", FogFar);
        _shader.Set("uTex", 0);

        _gl.BindVertexArray(_vao);
        foreach (var b in _batches)
        {
            bool additive = b.Blend is 3 or 4;
            bool alphaKey = b.Blend == 1;
            if (additive) { _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One); _gl.DepthMask(false); }
            else if (b.Blend >= 2) { _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha); _gl.DepthMask(false); }
            else { _gl.BlendFunc(BlendingFactor.One, BlendingFactor.Zero); _gl.DepthMask(true); }

            _shader.Set("uAdditive", additive ? 1 : 0);
            _shader.Set("uAlphaCut", alphaKey ? 0.5f : 0.0f);

            b.Tex?.Bind(0);
            DrawElements(b.IndexStart, b.IndexCount);
        }
        _gl.BindVertexArray(0);
        _gl.DepthMask(true);
    }

    private unsafe void DrawElements(int indexStart, int indexCount)
        => _gl.DrawElements(PrimitiveType.Triangles, (uint)indexCount, DrawElementsType.UnsignedShort, (void*)(indexStart * sizeof(ushort)));

    private bool CreateShader()
    {
        try { _shader = Shader.FromSource(_gl, "glue", VertSrc, FragSrc); return true; }
        catch (Exception e) { Console.WriteLine($"[glue] shader failed: {e.Message}"); Ok = false; return false; }
    }

    // ── M2 camera 0 (raw bytes; benilla-formats parse_m2_camera) ────────────────────────────────

    private static bool TryParseCamera(byte[] b, out Vector3 eye, out Vector3 target, out float fov, out float near, out float far)
    {
        eye = default; target = default; fov = 0; near = 0; far = 0;
        if (!U32(b, 0x124, out uint camCount) || camCount == 0) return false;
        if (!U32(b, 0x128, out uint camOfs)) return false;
        long rec = camOfs; // camera 0 (glue lookup slot is the 0xffff sentinel -> index the table directly)
        if (rec + 0x7c > b.Length) return false;

        if (!F32(b, rec + 0x04, out fov)) return false;
        F32(b, rec + 0x08, out far);
        F32(b, rec + 0x0c, out near);
        Vector3 pos = BasePlusKey(b, rec + 0x2c, rec + 0x10);
        Vector3 tgt = BasePlusKey(b, rec + 0x54, rec + 0x38);
        // Raw WoW model space (Z-up) -> the same glTF Y-up space M2Reader put the verts in: (x,y,z)->(x,z,-y).
        eye = ToYUp(pos);
        target = ToYUp(tgt);
        return true;
    }

    private static Vector3 ToYUp(Vector3 v) => new(v.X, v.Z, -v.Y);

    private static Vector3 BasePlusKey(byte[] b, long baseOfs, long trackOfs)
    {
        Vector3 basePos = new(F(b, baseOfs), F(b, baseOfs + 4), F(b, baseOfs + 8));
        // M2Track (v256): values M2Array is count@+0x14, ofs@+0x18; key 0's value triple is first.
        if (U32(b, trackOfs + 0x14, out uint n) && n > 0 && U32(b, trackOfs + 0x18, out uint vofs) && vofs + 12 <= b.Length)
            basePos += new Vector3(F(b, vofs), F(b, vofs + 4), F(b, vofs + 8));
        return basePos;
    }

    private void FrameFromBounds(M2Model model)
    {
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        foreach (var v in model.Vertices)
        {
            min = Vector3.Min(min, new Vector3(v.PosX, v.PosY, v.PosZ));
            max = Vector3.Max(max, new Vector3(v.PosX, v.PosY, v.PosZ));
        }
        Vector3 c = (min + max) * 0.5f;
        float r = (max - min).Length() * 0.5f;
        _target = c;
        _eye = c + new Vector3(0, r * 0.15f, r * 1.8f);
        _fovDiag = 0.9f;
        _near = MathF.Max(0.1f, r * 0.01f);
        _far = r * 6f;
        Console.WriteLine("[glue] no authored camera; framing UI_MainMenu from bounds");
    }

    private static bool U32(byte[] b, long o, out uint v)
    {
        v = 0;
        if (o < 0 || o + 4 > b.Length) return false;
        v = (uint)(b[o] | b[o + 1] << 8 | b[o + 2] << 16 | b[o + 3] << 24);
        return true;
    }
    private static bool F32(byte[] b, long o, out float v)
    {
        v = 0;
        if (!U32(b, o, out uint u)) return false;
        v = BitConverter.UInt32BitsToSingle(u);
        return true;
    }
    private static float F(byte[] b, long o) => F32(b, o, out float v) ? v : 0f;

    public void Dispose()
    {
        foreach (var t in _textures.Values) t?.Dispose();
        _textures.Clear();
        if (_vbo != 0) _gl.DeleteBuffer(_vbo);
        if (_ebo != 0) _gl.DeleteBuffer(_ebo);
        if (_vao != 0) _gl.DeleteVertexArray(_vao);
        _shader?.Dispose();
    }

    private const string VertSrc = @"#version 330 core
layout(location=0) in vec3 aPos;
layout(location=1) in vec2 aUv;
uniform mat4 uMVP;
uniform vec3 uEye;
out vec2 vUv;
out float vDist;
void main(){
    gl_Position = uMVP * vec4(aPos, 1.0);
    vUv = aUv;
    vDist = length(uEye - aPos);
}";

    private const string FragSrc = @"#version 330 core
in vec2 vUv;
in float vDist;
uniform sampler2D uTex;
uniform vec3 uFogColor;
uniform float uFogNear;
uniform float uFogFar;
uniform int uAdditive;
uniform float uAlphaCut;
out vec4 frag;
void main(){
    vec4 t = texture(uTex, vUv);
    if (t.a < uAlphaCut) discard;
    if (uAdditive == 1) { frag = vec4(t.rgb, t.a); return; }
    // Gentle atmospheric warmth only — full fog washed the far sky to flat brown. The real
    // orange sky comes from the model's own sky texture + its animated colour tracks (next increment).
    float fog = clamp((vDist - uFogNear) / (uFogFar - uFogNear), 0.0, 1.0) * 0.30;
    frag = vec4(mix(t.rgb, uFogColor, fog), t.a);
}";
}

using System.Diagnostics;
using System.IO;
using System.Numerics;
using Silk.NET.OpenGL;
using MSUIClient.Formats;
using MSUIClient.World.Particles;

namespace MSUIClient.Engine;

// The 1.12 login-screen glue scene: Interface\Glues\Models\UI_MainMenu\UI_MainMenu.m2
// (AccountLogin.xml's ModelFFX — the burning gate), rendered fullscreen through the
// model's own authored camera, exactly like benilla's glue booth (benilla/src/portrait/
// glue_booth.rs + login/mod.rs).
//
// COMES ALIVE, IS LIT, AND BURNS. UI_MainMenu.m2 carries (measured from the real file via
// tools/mpqpy): 4 M2Colour records, 3 texture transforms, 18 global sequences, 57 bones,
// 28 particle emitters, 4 lights, one 40 s sequence 0.
//   1. WRONG COLOURS — the warm tint lives entirely in an M2Colour RGB track (colour[2] =
//      ~(0.80, 0.18, 0.18) animated on global sequence 2, a 1.67 s pulse). A grey glow card
//      is warmed ONLY by that track; ignoring it renders the embers grey. We multiply it in.
//   2. NO ANIMATION — the 3 texture transforms scroll the cloud/sky layers, the colour pulse
//      breathes the embers, and the 28 PARTICLE EMITTERS are the brazier fires. Sampled /
//      simulated off a live clock.
//   3. FLAT / TOO BRIGHT - the mesh was drawn UNLIT. The real gate is lit by the model's own
//      dim warm rig, folded in under benilla's byte-verified law (glue_booth.rs scene_rig /
//      wow_model.wgsl). POINT lights (the brazier) use the engine's fixed falloff 1/(0.7d +
//      0.03d*d) with NO authored-radius clamp (dead code in the reference), so the warm light
//      reaches the whole gate. DIRECTIONAL lights (the green valley + blue sky fills) fold into
//      an order-2 SH probe whose closed form per normal is C*(4/17)*(0.375 + 2u + 1.875u*u),
//      u = N.L - softer than max(N.L,0), with no hard terminator, so they tint gently instead
//      of washing the stone cool. The light sum saturates to [0,1] before the texture modulate
//      (the FFP combine), all in gamma space (the reference's one sRGB decode is a frame post-step).
// The fires reuse the existing ParticleRenderer through a matrix (camera-free) draw path, so
// the login has its own particle system independent of the world one. Additive fog fades to
// BLACK (benilla FogPolicy::Add); opaque fog toward the ember colour is gentle so the sky
// stays vivid. The corners clear to a dark night-sky tone (the sky dome doesn't reach the
// screen corners at wide aspect ratios).
//
// STILL TO LAYER ON TOP (next): the full WoW glue-art 2D chrome (WowSkin: logo, gold FRIZQT
// boxes/buttons) and, if wanted, sequence-0 bone animation (waving banners).
//
// Best-effort: any failure leaves _ok false and the scene simply doesn't draw.

public sealed class GlueScene : IDisposable
{
    private const string ModelPath = @"Interface\Glues\Models\UI_MainMenu\UI_MainMenu.m2";
    private const string ModelPathMdx = @"Interface\Glues\Models\UI_MainMenu\UI_MainMenu.mdx";

    // benilla glue_booth.rs: MainMenu fog = colour (0.25, 0.06, 0.015), far 1200.
    private static readonly Vector3 FogColor = new(0.25f, 0.06f, 0.015f);
    private const float FogNear = 30f;
    private const float FogFar = 1200f;
    private const float OpaqueFogStrength = 0.30f;
    // A tiny NEUTRAL floor under the authored ambient so fully-shadowed stone isn't pitch black.
    // benilla uses no floor (ambient = sum of authored ambient tracks) and lets the point light
    // fill; keep this small and untinted. The old warm (0.12,0.10,0.10) floor washed the shadows
    // grey and flattened the contrast the OG has. Raise only if shadows read as pure black.
    private static readonly Vector3 AmbientFloor = new(0.03f, 0.03f, 0.03f);
    // The sky dome doesn't reach the screen corners at wide aspect; clear to a near-black warm
    // tone. The OG's corners are near-black, never blue (a blue fill read as cool contamination
    // in the corners). Keep it dark and slightly warm so it disappears into the burning sky.
    private static readonly Vector3 SkyFill = new(0.02f, 0.014f, 0.012f);

    private readonly GL _gl;
    private readonly ClientConfig _config;
    private Shader? _shader;
    private ParticleRenderer? _particles;
    private uint _vao, _vbo, _ebo;
    private readonly List<Batch> _batches = new();
    private readonly Dictionary<string, Texture?> _textures = new(StringComparer.OrdinalIgnoreCase);

    // Fire emitter instances, in the ParticleRenderer tuple shape. Transform is identity: the
    // emitters live in the same Y-up model space as the mesh and draw through the glue camera.
    private readonly List<(string Path, Matrix4x4 Transform, M2ParticleEmitter Emitter, int Index, string TexPath)> _emitters = new();

    private Vector3 _eye = new(0, 1, 3);
    private Vector3 _target = Vector3.Zero;
    private float _fovDiag = 0.9f;
    private float _near = 0.1f, _far = FogFar;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastMs;
    private float _time;

    private float[] _globalSeqDur = Array.Empty<float>();
    private ColorRec[] _colors = Array.Empty<ColorRec>();
    private Track[] _texAnims = Array.Empty<Track>();
    private ushort[] _texAnimLookup = Array.Empty<ushort>();

    private Vector3 _ambient = new(0.12f, 0.10f, 0.10f);
    private Light[] _lights = Array.Empty<Light>();

    public bool Ok { get; private set; }

    private struct Batch
    {
        public int IndexStart, IndexCount;
        public Texture? Tex;
        public int Blend;
        public int ColorIndex;
        public int TexAnim;
        public bool Unlit;
    }

    private struct Light
    {
        public int Type;         // 0 = directional, 1 = point
        public Vector3 PosDir;   // Y-up: position (point) or to-light direction (dir)
        public Vector3 Color;    // diffuse colour x intensity
        public float AttenStart, AttenEnd;
    }

    private sealed class ColorRec { public Track Rgb = new(); public Track Alpha = new(); }

    private sealed class Track
    {
        public int Interp;
        public int Gseq = -1;
        public float[] Times = Array.Empty<float>();
        public Vector3[] Vec = Array.Empty<Vector3>();
        public float[] Scalar = Array.Empty<float>();
    }

    public GlueScene(GL gl, MpqMount mpq, ClientConfig config)
    {
        _gl = gl;
        _config = config;
        try { Build(mpq); Ok = _batches.Count > 0; }
        catch (Exception e) { Console.WriteLine($"[glue] scene unavailable: {e.Message}"); Ok = false; }
        if (Ok)
        {
            Console.WriteLine($"[glue] UI_MainMenu loaded: {_batches.Count} batch(es), " +
                              $"{_colors.Length} colour + {_texAnims.Length} uv track(s), " +
                              $"{_lights.Length} light(s), {_emitters.Count} emitter(s), fov {_fovDiag:F2}");
            TryInitParticles();
        }
    }

    private unsafe void Build(MpqMount mpq)
    {
        byte[]? bytes = mpq.ReadFile(ModelPath) ?? mpq.ReadFile(ModelPathMdx);
        if (bytes is null) { Console.WriteLine("[glue] UI_MainMenu.m2 not found in the MPQs"); return; }

        M2Model model = M2Reader.Parse(bytes);
        if (!model.IsValid) { Console.WriteLine("[glue] UI_MainMenu.m2 parsed but has no geometry"); return; }

        // Interleave [pos.xyz, normal.xyz, uv.xy]. Verts + normals are glTF Y-up (M2Reader converts).
        var verts = new float[model.Vertices.Count * 8];
        for (int i = 0; i < model.Vertices.Count; i++)
        {
            var v = model.Vertices[i];
            int o = i * 8;
            verts[o + 0] = v.PosX; verts[o + 1] = v.PosY; verts[o + 2] = v.PosZ;
            verts[o + 3] = v.NormX; verts[o + 4] = v.NormY; verts[o + 5] = v.NormZ;
            verts[o + 6] = v.TexU; verts[o + 7] = v.TexV;
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
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 8 * sizeof(float), (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 8 * sizeof(float), (void*)(3 * sizeof(float)));
        _gl.EnableVertexAttribArray(2);
        _gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, 8 * sizeof(float), (void*)(6 * sizeof(float)));
        _gl.BindVertexArray(0);

        ParseAnimation(bytes);
        ParseLights(bytes);

        // Fire particle emitters (the braziers). Texture = model.Textures[emitter.Texture]
        // (direct index, NOT via TextureLookup — M2Reader documents this). Transform is identity.
        for (int i = 0; i < model.ParticleEmitters.Count; i++)
        {
            var em = model.ParticleEmitters[i];
            string tex = em.Texture < model.Textures.Count ? model.Textures[em.Texture].Filename : "";
            _emitters.Add(("glue-mainmenu", Matrix4x4.Identity, em, i, tex));
        }

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
            int mat = b.MaterialIndex;
            int blend = mat < model.RenderFlags.Count ? model.RenderFlags[mat].BlendingMode : 0;
            bool unlit = mat < model.RenderFlags.Count && model.RenderFlags[mat].Unlit;

            int colorIdx = b.ColorIndex >= 0 && b.ColorIndex < _colors.Length ? b.ColorIndex : -1;

            int texAnim = -1;
            if (_texAnimLookup.Length > 0 && b.TextureTransformIndex < _texAnimLookup.Length)
            {
                ushort la = _texAnimLookup[b.TextureTransformIndex];
                if (la != 0xFFFF && la < _texAnims.Length) texAnim = la;
            }
            else if (b.TextureTransformIndex < _texAnims.Length)
            {
                texAnim = b.TextureTransformIndex;
            }

            _batches.Add(new Batch
            {
                IndexStart = sm.IndexStart,
                IndexCount = sm.IndexCount,
                Tex = LoadTexture(mpq, texPath),
                Blend = blend,
                ColorIndex = colorIdx,
                TexAnim = texAnim,
                Unlit = unlit,
            });
        }

        if (TryParseCamera(bytes, out var eye, out var target, out float fov, out float near, out float far))
        {
            _eye = eye; _target = target; _fovDiag = fov;
            _near = near > 0.01f ? near : 0.1f;
            _far = far > _near ? far : FogFar;
        }
        else
        {
            FrameFromBounds(model);
        }
    }

    private void TryInitParticles()
    {
        if (_emitters.Count == 0) return;
        try
        {
            string shaderDir = Path.Combine(AppContext.BaseDirectory, "Shaders");
            if (!File.Exists(Path.Combine(shaderDir, "terrain.vert")))
                shaderDir = Path.Combine(_config.RepoRoot, "MSUIClient", "Shaders");
            _particles = new ParticleRenderer(_gl, _config);
            _particles.LoadShaders(shaderDir);
            // The glue braziers are world-space FIRE emitters, not the portal these
            // knobs were tuned for: the 4.74yd centre-hole fade would gut a flame that
            // lives half a yard from its emitter (every sprite * ~0.1 alpha), and the
            // 24-slot clock face is portal choreography - both off for the login fires.
            _particles.CentreHoleYards = 0f;
            _particles.SpawnArms = 0;
        }
        catch (Exception e)
        {
            Console.WriteLine($"[glue] particles unavailable: {e.Message}");
            _particles = null;
        }
    }

    // ── animation parsing (raw M2 bytes) ─────────────────────────────────────────────

    private void ParseAnimation(byte[] b)
    {
        if (U32(b, 0x14, out uint ng) && ng > 0 && ng < 4096 && U32(b, 0x18, out uint og))
        {
            _globalSeqDur = new float[ng];
            for (int i = 0; i < ng; i++)
                _globalSeqDur[i] = U32(b, og + i * 4, out uint d) ? d / 1000f : 0f;
        }

        if (U32(b, 0x54, out uint nc) && nc > 0 && nc < 4096 && U32(b, 0x58, out uint oc))
        {
            _colors = new ColorRec[nc];
            for (int i = 0; i < nc; i++)
            {
                long rec = oc + (long)i * 0x38;
                _colors[i] = new ColorRec
                {
                    Rgb = ReadTrack(b, rec + 0x00, vec3: true),
                    Alpha = ReadTrack(b, rec + 0x1c, vec3: false),
                };
            }
        }

        if (U32(b, 0x74, out uint nt) && nt > 0 && nt < 4096 && U32(b, 0x78, out uint ot))
        {
            _texAnims = new Track[nt];
            for (int i = 0; i < nt; i++)
                _texAnims[i] = ReadTrack(b, ot + (long)i * 0x54 + 0x00, vec3: true);
        }

        if (U32(b, 0xAC, out uint nl) && nl > 0 && nl < 4096 && U32(b, 0xB0, out uint ol) &&
            ol + nl * 2 <= (uint)b.Length)
        {
            _texAnimLookup = new ushort[nl];
            for (int i = 0; i < nl; i++)
                _texAnimLookup[i] = (ushort)(b[ol + i * 2] | b[ol + i * 2 + 1] << 8);
        }
    }

    /// <summary>The M2 light rig (header count@0x11C / ofs@0x120, stride 0xD4). Per light:
    /// type u16@0, bone i16@2, position C3@4, then M2Tracks — ambient colour@0x10, ambient
    /// intensity@0x2c, diffuse colour@0x48, diffuse intensity@0x64, atten start@0x80 / end@0x9c
    /// (first key of each). Ambient accumulates; diffuse lights become point (spatial +
    /// attenuated) or directional (to-light = normalized position) contributions.</summary>
    private void ParseLights(byte[] b)
    {
        const int STRIDE = 0xD4;
        if (!U32(b, 0x11C, out uint lc) || lc == 0 || lc > 64 || !U32(b, 0x120, out uint lo)) return;

        Vector3 ambient = Vector3.Zero;
        var lights = new List<Light>();
        for (int i = 0; i < lc; i++)
        {
            long rec = lo + (long)i * STRIDE;
            if (rec + STRIDE > b.Length) break;
            int type = b[rec] | b[rec + 1] << 8;
            Vector3 posRaw = new(F(b, rec + 4), F(b, rec + 8), F(b, rec + 12));
            Vector3 ambColor = TrackVec3(b, rec + 0x10, Vector3.One);
            float ambI = TrackFloat(b, rec + 0x2c, 0f);
            Vector3 difColor = TrackVec3(b, rec + 0x48, Vector3.One);
            float difI = TrackFloat(b, rec + 0x64, 0f);
            float atS = TrackFloat(b, rec + 0x80, 0f);
            float atE = TrackFloat(b, rec + 0x9c, 0f);

            ambient += ambColor * ambI;
            if (difI > 1e-4f)
            {
                Vector3 pd = ToYUp(posRaw);
                if (type == 0)
                    pd = pd.LengthSquared() > 1e-6f ? Vector3.Normalize(pd) : Vector3.UnitY;
                lights.Add(new Light
                {
                    Type = type,
                    PosDir = pd,
                    Color = difColor * difI,
                    AttenStart = atS,
                    AttenEnd = atE > atS ? atE : atS + 1f,
                });
            }
        }
        _ambient = Vector3.Max(ambient, AmbientFloor);
        _lights = lights.ToArray();
        Console.WriteLine($"[glue] light rig: ambient ({_ambient.X:F2},{_ambient.Y:F2},{_ambient.Z:F2}), " +
                          $"{_lights.Length} diffuse light(s)");
    }

    private static Vector3 TrackVec3(byte[] b, long trackOfs, Vector3 fallback)
    {
        if (U32(b, trackOfs + 0x14, out uint n) && n > 0 && U32(b, trackOfs + 0x18, out uint vo) && vo + 12 <= b.Length)
            return new Vector3(F(b, vo), F(b, vo + 4), F(b, vo + 8));
        return fallback;
    }

    private static float TrackFloat(byte[] b, long trackOfs, float fallback)
    {
        if (U32(b, trackOfs + 0x14, out uint n) && n > 0 && U32(b, trackOfs + 0x18, out uint vo) && vo + 4 <= b.Length)
            return F(b, vo);
        return fallback;
    }

    private static Track ReadTrack(byte[] b, long to, bool vec3)
    {
        var tr = new Track();
        if (to + 0x1c > b.Length) return tr;
        tr.Interp = b[to] | b[to + 1] << 8;
        tr.Gseq = (short)(b[to + 2] | b[to + 3] << 8);
        if (!U32(b, to + 0x0c, out uint nT) || !U32(b, to + 0x10, out uint oT)) return tr;
        if (!U32(b, to + 0x14, out uint nV) || !U32(b, to + 0x18, out uint oV)) return tr;
        int n = (int)Math.Min(nT, nV);
        if (n <= 0 || n > 65536) return tr;
        if (oT + (long)n * 4 > b.Length) return tr;

        tr.Times = new float[n];
        for (int i = 0; i < n; i++)
            tr.Times[i] = (U32(b, oT + i * 4, out uint ms) ? ms : 0) / 1000f;

        if (vec3)
        {
            if (oV + (long)n * 12 > b.Length) { tr.Times = Array.Empty<float>(); return tr; }
            tr.Vec = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                long o = oV + (long)i * 12;
                tr.Vec[i] = new Vector3(F(b, o), F(b, o + 4), F(b, o + 8));
            }
        }
        else
        {
            if (oV + (long)n * 2 > b.Length) { tr.Times = Array.Empty<float>(); return tr; }
            tr.Scalar = new float[n];
            for (int i = 0; i < n; i++)
                tr.Scalar[i] = (b[oV + i * 2] | b[oV + i * 2 + 1] << 8) / 32767f;
        }
        return tr;
    }

    private float TrackPeriod(Track t)
    {
        if (t.Gseq >= 0 && t.Gseq < _globalSeqDur.Length && _globalSeqDur[t.Gseq] > 0f)
            return _globalSeqDur[t.Gseq];
        return t.Times.Length > 0 ? t.Times[^1] : 0f;
    }

    private Vector3 SampleVec3(Track t, Vector3 fallback)
    {
        if (t.Vec.Length == 0) return fallback;
        if (t.Vec.Length == 1) return t.Vec[0];
        float period = TrackPeriod(t);
        float tt = period > 1e-4f ? _time % period : 0f;
        int hi = UpperSegment(t.Times, tt);
        if (hi <= 0) return t.Vec[0];
        if (hi >= t.Vec.Length) return t.Vec[^1];
        int lo = hi - 1;
        if (t.Interp == 0) return t.Vec[lo];
        float span = t.Times[hi] - t.Times[lo];
        float f = span > 1e-6f ? (tt - t.Times[lo]) / span : 0f;
        return Vector3.Lerp(t.Vec[lo], t.Vec[hi], f);
    }

    private float SampleScalar(Track t, float fallback)
    {
        if (t.Scalar.Length == 0) return fallback;
        if (t.Scalar.Length == 1) return t.Scalar[0];
        float period = TrackPeriod(t);
        float tt = period > 1e-4f ? _time % period : 0f;
        int hi = UpperSegment(t.Times, tt);
        if (hi <= 0) return t.Scalar[0];
        if (hi >= t.Scalar.Length) return t.Scalar[^1];
        int lo = hi - 1;
        if (t.Interp == 0) return t.Scalar[lo];
        float span = t.Times[hi] - t.Times[lo];
        float f = span > 1e-6f ? (tt - t.Times[lo]) / span : 0f;
        return t.Scalar[lo] + (t.Scalar[hi] - t.Scalar[lo]) * f;
    }

    private static int UpperSegment(float[] times, float t)
    {
        int lo = 0, hi = times.Length;
        while (lo < hi) { int mid = (lo + hi) / 2; if (times[mid] < t) lo = mid + 1; else hi = mid; }
        return lo;
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

        double now = _clock.Elapsed.TotalMilliseconds;
        float dt = (float)((now - _lastMs) / 1000.0);
        _lastMs = now;
        if (dt > 0f && dt < 1f) _time += dt;

        float aspect = viewportW / (float)viewportH;
        // benilla's glue framing (portrait/glue_booth.rs:668, portrait/framing.rs): the authored
        // M2 camera fov is a DIAGONAL angle, and the on-screen vertical opening is that angle
        // divided directly, fovy = fov / sqrt(aspect*aspect + 1) - NOT a tangent-based conversion.
        // The old 2*atan(tan(fov/2)/sqrt(1+aspect*aspect)) ran ~9% wider at this fov, zooming the
        // gate out (it filled ~71% of frame height vs the OG's ~80%). This linear division is exact
        // to the reference (at 4/3 it is the community's "fov * 0.6" factor, 1/sqrt((4/3)^2+1)=0.6).
        float fovy = _fovDiag / MathF.Sqrt(1f + aspect * aspect);
        fovy = Math.Clamp(fovy, 0.05f, 3.0f);

        Matrix4x4 view = Matrix4x4.CreateLookAt(_eye, _target, Vector3.UnitY);
        Matrix4x4 proj = Matrix4x4.CreatePerspectiveFieldOfView(fovy, aspect, _near, _far);
        Matrix4x4 mvp = view * proj;

        _gl.Viewport(0, 0, (uint)viewportW, (uint)viewportH);
        _gl.ClearColor(SkyFill.X, SkyFill.Y, SkyFill.Z, 1f);
        _gl.Clear((uint)(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit));
        _gl.Enable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.CullFace);
        _gl.Enable(EnableCap.Blend);

        _shader!.Use();
        _shader.Set("uMVP", mvp);
        _shader.Set("uEye", _eye);
        _shader.Set("uFogColor", FogColor);
        _shader.Set("uFogNear", FogNear);
        _shader.Set("uFogFar", FogFar);
        _shader.Set("uFogStrength", OpaqueFogStrength);
        _shader.Set("uTex", 0);

        _shader.Set("uAmbient", _ambient);
        int nl = Math.Min(_lights.Length, 4);
        _shader.Set("uLightCount", nl);
        for (int i = 0; i < nl; i++)
        {
            _shader.Set($"uLightType[{i}]", _lights[i].Type);
            _shader.Set($"uLightPos[{i}]", _lights[i].PosDir);
            _shader.Set($"uLightColor[{i}]", _lights[i].Color);
            _shader.Set($"uLightAtten[{i}]", new Vector2(_lights[i].AttenStart, _lights[i].AttenEnd));
        }

        _gl.BindVertexArray(_vao);
        foreach (var b in _batches)
        {
            bool additive = b.Blend is 3 or 4;
            bool alphaKey = b.Blend == 1;
            if (additive) { _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One); _gl.DepthMask(false); }
            else if (b.Blend >= 2) { _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha); _gl.DepthMask(false); }
            else { _gl.BlendFunc(BlendingFactor.One, BlendingFactor.Zero); _gl.DepthMask(true); }

            Vector3 tint = Vector3.One;
            float ca = 1f;
            if (b.ColorIndex >= 0)
            {
                tint = SampleVec3(_colors[b.ColorIndex].Rgb, Vector3.One);
                ca = Math.Clamp(SampleScalar(_colors[b.ColorIndex].Alpha, 1f), 0f, 1f);
            }
            Vector2 uv = Vector2.Zero;
            if (b.TexAnim >= 0)
            {
                Vector3 tr = SampleVec3(_texAnims[b.TexAnim], Vector3.Zero);
                uv = new Vector2(tr.X, tr.Y);
            }

            _shader.Set("uColor", tint);
            _shader.Set("uColorAlpha", ca);
            _shader.Set("uUvOffset", uv);
            _shader.Set("uAdditive", additive ? 1 : 0);
            _shader.Set("uEmissive", additive || b.Unlit ? 1 : 0);
            _shader.Set("uAlphaCut", alphaKey ? 0.5f : 0.0f);

            b.Tex?.Bind(0);
            DrawElements(b.IndexStart, b.IndexCount);
        }
        _gl.BindVertexArray(0);
        _gl.DepthMask(true);

        // The brazier fires. Same Y-up camera as the mesh, but ParticleRenderer wants a
        // camera-RELATIVE view-projection (sprite centres are uploaded absolute; the shader
        // subtracts uCameraOrigin) — so eye-subtracted view here. worldUp = Y (glue space).
        if (_particles is not null)
        {
            Vector3 forward = Vector3.Normalize(_target - _eye);
            Vector3 right = Vector3.Cross(forward, Vector3.UnitY);
            right = right.LengthSquared() > 1e-6f ? Vector3.Normalize(right) : Vector3.UnitX;
            Matrix4x4 relVp = Matrix4x4.CreateLookAt(Vector3.Zero, _target - _eye, Vector3.UnitY) * proj;
            _particles.Simulate(Math.Clamp(dt, 0f, 0.1f), _eye, _emitters);
            _particles.Render(relVp, _eye, forward, Vector3.UnitY, right);
        }
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
        long rec = camOfs;
        if (rec + 0x7c > b.Length) return false;

        if (!F32(b, rec + 0x04, out fov)) return false;
        F32(b, rec + 0x08, out far);
        F32(b, rec + 0x0c, out near);
        Vector3 pos = BasePlusKey(b, rec + 0x2c, rec + 0x10);
        Vector3 tgt = BasePlusKey(b, rec + 0x54, rec + 0x38);
        eye = ToYUp(pos);
        target = ToYUp(tgt);
        return true;
    }

    // Raw WoW model space (Z-up) -> the same glTF Y-up space M2Reader put the verts in: (x,y,z)->(x,z,-y).
    private static Vector3 ToYUp(Vector3 v) => new(v.X, v.Z, -v.Y);

    private static Vector3 BasePlusKey(byte[] b, long baseOfs, long trackOfs)
    {
        Vector3 basePos = new(F(b, baseOfs), F(b, baseOfs + 4), F(b, baseOfs + 8));
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
        _particles?.Dispose();
        foreach (var t in _textures.Values) t?.Dispose();
        _textures.Clear();
        if (_vbo != 0) _gl.DeleteBuffer(_vbo);
        if (_ebo != 0) _gl.DeleteBuffer(_ebo);
        if (_vao != 0) _gl.DeleteVertexArray(_vao);
        _shader?.Dispose();
    }

    private const string VertSrc = @"#version 330 core
layout(location=0) in vec3 aPos;
layout(location=1) in vec3 aNormal;
layout(location=2) in vec2 aUv;
uniform mat4 uMVP;
uniform vec3 uEye;
out vec3 vModelPos;
out vec3 vNormal;
out vec2 vUv;
out float vDist;
void main(){
    gl_Position = uMVP * vec4(aPos, 1.0);
    vModelPos = aPos;
    vNormal = aNormal;
    vUv = aUv;
    vDist = length(uEye - aPos);
}";

    private const string FragSrc = @"#version 330 core
in vec3 vModelPos;
in vec3 vNormal;
in vec2 vUv;
in float vDist;
uniform sampler2D uTex;
uniform vec3 uFogColor;
uniform float uFogNear;
uniform float uFogFar;
uniform float uFogStrength;
uniform vec3 uEye;
uniform vec3 uColor;
uniform float uColorAlpha;
uniform int uAdditive;
uniform int uEmissive;
uniform float uAlphaCut;
uniform vec2 uUvOffset;
uniform vec3 uAmbient;
uniform int uLightCount;
uniform int uLightType[4];
uniform vec3 uLightPos[4];
uniform vec3 uLightColor[4];
uniform vec2 uLightAtten[4];
out vec4 frag;
void main(){
    vec4 t = texture(uTex, vUv + uUvOffset);
    if (t.a < uAlphaCut) discard;
    vec3 rgb = t.rgb * uColor;
    float a = t.a * uColorAlpha;
    float fog = clamp((vDist - uFogNear) / (uFogFar - uFogNear), 0.0, 1.0);

    if (uEmissive == 1) {
        if (uAdditive == 1) { frag = vec4(rgb * (1.0 - fog), a); return; }
        frag = vec4(mix(rgb, uFogColor, fog * uFogStrength), a);
        return;
    }

    vec3 N = normalize(vNormal);
    vec3 V = normalize(uEye - vModelPos);
    if (dot(N, V) < 0.0) N = -N;               // two-sided: the gate has no back-face cull
    vec3 lit = uAmbient;                        // Sigma authored ambient tracks (light[*] ambient)
    for (int i = 0; i < uLightCount && i < 4; i++) {
        if (uLightType[i] == 1) {
            // Dynamic POINT light (benilla wow_model.wgsl point_light_sum): fixed engine falloff
            // 1/(0.7d + 0.03d^2), diffuse-only max(N.L,0), NO authored-radius clamp (uLightAtten
            // is the authored radii, dead code in the reference). This is what carries the warm
            // brazier across the whole gate instead of dying at ~6.5yd.
            vec3 d = uLightPos[i] - vModelPos;
            float dist = length(d);
            vec3 L = dist > 1e-4 ? d / dist : vec3(0.0, 1.0, 0.0);
            float atten = 1.0 / (0.7 * dist + 0.03 * dist * dist);
            lit += uLightColor[i] * (max(dot(N, L), 0.0) * atten);
        } else {
            // DIRECTIONAL light folded into an order-2 SH probe (benilla lighting/sh.rs): a single
            // lobe evaluated at a normal is EXACTLY E(mu) = C*(4/17)*(0.375 + 2mu + 1.875mu^2),
            // mu = N.u. Softer than max(N.L,0): no hard terminator (side-on 0.088C, back 0.059C,
            // the reference's authored wrap), so the green valley / blue sky fills tint gently
            // rather than washing the stone cool. uLightPos is the to-light unit dir for type 0.
            vec3 u = normalize(uLightPos[i]);
            float mu = dot(N, u);
            lit += uLightColor[i] * ((4.0 / 17.0) * (0.375 + 2.0 * mu + 1.875 * mu * mu));
        }
    }
    // FFP combine (benilla wow_model.wgsl:640): the light SUM saturates to [0,1] FIRST, THEN the
    // texture modulates it, so a surface never exceeds its own fully-lit texture (a brazier at
    // point-blank drives stone to tex*1, not past it into blown gold). Gamma space throughout.
    vec3 primary = clamp(lit, 0.0, 1.0);
    frag = vec4(mix(rgb * primary, uFogColor, fog * uFogStrength), a);
}";
}
using System.Diagnostics;
using System.IO;
using System.Numerics;
using Silk.NET.OpenGL;
using MSUIClient.Formats;
using MSUIClient.World.Particles;

namespace MSUIClient.Engine;

// The 1.12 login-screen glue scene: Interface\Glues\Models\UI_MainMenu\UI_MainMenu.m2
// (AccountLogin.xml's ModelFFX - the burning gate), rendered fullscreen through the
// model's own authored camera, exactly like benilla's glue booth (benilla/src/portrait/
// glue_booth.rs + login/mod.rs).
//
// GENERALIZED (2026-07-28) for character select: the same machinery renders ANY UI_* glue
// scene, not just UI_MainMenu. The per-race character-select backgrounds (UI_Human, UI_Orc,
// UI_Dwarf, ...) are the same kind of asset - mesh + authored camera 0 + fog + light rig +
// animated colour tracks + brazier/ember emitters - so only the model PATH and the fog policy
// vary. The login keeps its exact behaviour: the parameterless-tail constructor still loads
// UI_MainMenu with fog ON. The char-select booth (Engine/GlueBooth.cs) passes a per-race model
// with fog OFF (benilla renders the Race scene UNFOGGED at char-select; MainMenu is always
// fogged). Fog-off is a single uniform (uFogEnabled = 0) so the shared shader stays identical
// for the login (uFogEnabled = 1 is byte-for-byte the old behaviour).
//
// COMES ALIVE, IS LIT, AND BURNS. UI_MainMenu.m2 carries (measured from the real file via
// tools/mpqpy): 4 M2Colour records, 3 texture transforms, 18 global sequences, 57 bones,
// 28 particle emitters, 4 lights, one 40 s sequence 0.
//   1. WRONG COLOURS - the warm tint lives entirely in an M2Colour RGB track (colour[2] =
//      ~(0.80, 0.18, 0.18) animated on global sequence 2, a 1.67 s pulse). A grey glow card
//      is warmed ONLY by that track; ignoring it renders the embers grey. We multiply it in.
//   2. NO ANIMATION - the 3 texture transforms scroll the cloud/sky layers, the colour pulse
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
// Best-effort: any failure leaves _ok false and the scene simply doesn't draw.

public sealed class GlueScene : IDisposable
{
    // The login default. UI_MainMenu is the burning gate; other UI_* scenes come in via the
    // model-path constructor (character select's per-race backgrounds).
    private const string DefaultModelPath = @"Interface\Glues\Models\UI_MainMenu\UI_MainMenu.m2";

    // benilla glue_booth.rs: MainMenu fog = colour (0.25, 0.06, 0.015), far 1200. These stay the
    // login's fog; when fog is DISABLED (char-select Race scenes) the shader zeroes the fog factor
    // and these values are simply never reached.
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

    // Which model this scene loads, and whether the fog factor is applied. Set by the constructor;
    // the login uses (UI_MainMenu, fog on), a char-select Race booth uses (UI_<Race>, fog off).
    private readonly string _modelPath;
    private readonly string _modelPathMdx;
    private readonly bool _fogEnabled;

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

    // ── Read-only scene rig, exposed so a character can be posed and lit inside this booth
    //    (Engine/GlueBooth.cs, char-select phase 2). The eye/target/fov are the model's authored
    //    camera 0, already converted to the glTF Y-up space the mesh lives in; the ambient is the
    //    scene's accumulated authored ambient. These are read-only snapshots; the scene owns them.
    public Vector3 Eye => _eye;
    public Vector3 Target => _target;
    public float FovDiag => _fovDiag;
    public float NearPlane => _near;
    public float FarPlane => _far;
    public Vector3 Ambient => _ambient;

    // The scene's dominant DIRECTIONAL light, as a horizontal (Y-up, y=0) unit to-light direction -
    // i.e. the authored "sun" for this UI_<Race> scene. The char-select booth aims its supplemental
    // floor fill along THIS so the whole scene is lit from the same side as the real sun (which is
    // what the OG does), instead of an arbitrary guessed azimuth. Fallback if the scene has no
    // directional light.
    private Vector3 _primaryLightDir = new(-0.6f, 0f, 0.6f);
    public Vector3 PrimaryLightDir => _primaryLightDir;

    // The stage spot: attachment 0 of the scene model (the point the character stands on at
    // char-select, benilla framing.rs attachment_point(.,.,0)). Absolute glTF Y-up model-space
    // position (M2Attachment.Position is already model space). Null if the scene has no attachment 0.
    private Vector3? _stageSpot;
    public Vector3? StageSpot => _stageSpot;

    // A per-instance multiply on the LIT (non-emissive) geometry only, so a caller can brighten/warm
    // the scene without touching the emissive sky/glows. Default (1,1,1) = no change, so the login is
    // byte-identical; the char-select booth drives it (BoothTune) to match the OG warmth while the
    // faithful per-race light-rig fix is pending.
    public Vector3 SceneTint { get; set; } = Vector3.One;

    // A supplemental fill light (booth only). The per-race UI_<Race> scenes carry only GRAZING
    // directional lights and NO brazier point lights, so up-facing surfaces (the floor/pathway) get
    // nothing but flat ambient and read wrong. This adds one extra SH lobe to light them. Default
    // colour (0,0,0) = no contribution, so the login (which has its own brazier point lights) is
    // byte-identical; the booth aims it warm from above (BoothTune) to match the OG ground.
    public Vector3 SceneFillDir { get; set; } = new(0f, 1f, 0f);
    public Vector3 SceneFillColor { get; set; } = Vector3.Zero;

    private struct Batch
    {
        public int IndexStart, IndexCount;
        public Texture? Tex;
        public int Blend;
        public int ColorIndex;
        public int TexAnim;
        public bool Unlit;
        public bool TwoSided;
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

    /// <summary>The login glue scene: UI_MainMenu with fog ON. Unchanged behaviour.</summary>
    public GlueScene(GL gl, MpqMount mpq, ClientConfig config)
        : this(gl, mpq, config, DefaultModelPath, fogEnabled: true) { }

    /// <summary>
    /// A glue scene for an arbitrary UI_* model. modelPath is the .m2 (the .mdx sibling is tried
    /// as a fallback, exactly as the login does). fogEnabled = false renders the scene UNFOGGED
    /// (char-select Race backgrounds); the light rig, colour tracks, camera and particles are
    /// identical to the login's.
    /// </summary>
    public GlueScene(GL gl, MpqMount mpq, ClientConfig config, string modelPath, bool fogEnabled)
    {
        _gl = gl;
        _config = config;
        _modelPath = string.IsNullOrWhiteSpace(modelPath) ? DefaultModelPath : modelPath;
        _modelPathMdx = Path.ChangeExtension(_modelPath, ".mdx");
        _fogEnabled = fogEnabled;
        try { Build(mpq); Ok = _batches.Count > 0; }
        catch (Exception e) { Console.WriteLine($"[glue] scene unavailable: {e.Message}"); Ok = false; }
        if (Ok)
        {
            Console.WriteLine($"[glue] {Path.GetFileNameWithoutExtension(_modelPath)} loaded: {_batches.Count} batch(es), " +
                              $"{_colors.Length} colour + {_texAnims.Length} uv track(s), " +
                              $"{_lights.Length} light(s), {_emitters.Count} emitter(s), fov {_fovDiag:F2}, " +
                              $"fog {(_fogEnabled ? "on" : "off")}");
            TryInitParticles();
        }
    }

    private unsafe void Build(MpqMount mpq)
    {
        byte[]? bytes = mpq.ReadFile(_modelPath) ?? mpq.ReadFile(_modelPathMdx);
        if (bytes is null) { Console.WriteLine($"[glue] {_modelPath} not found in the MPQs"); return; }

        M2Model? model = M2Reader.Parse(bytes);
        if (model is null || !model.IsValid) { Console.WriteLine($"[glue] {_modelPath} parsed but has no geometry"); return; }

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
        // (direct index, NOT via TextureLookup - M2Reader documents this). Transform is identity.
        for (int i = 0; i < model.ParticleEmitters.Count; i++)
        {
            var em = model.ParticleEmitters[i];
            string tex = em.Texture < model.Textures.Count ? model.Textures[em.Texture].Filename : "";
            _emitters.Add(("glue-scene", Matrix4x4.Identity, em, i, tex));
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
            // Render-flag bit 0x04. Exactly ONE of UI_MainMenu's 22 materials sets it (rf19, the
            // ELWYNNGRASS card); every other batch - all the stone, and every backdrop sheet -
            // is single-sided and the reference back-face culls it. Kept per batch anyway: the
            // char-select Race scenes lean on two-sided foliage far more. See Render().
            bool twoSided = mat < model.RenderFlags.Count && model.RenderFlags[mat].TwoSided;

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

            // Diagnostic: surface the translucent / animated batches (the water, glass, glows) so a
            // "screwed-up" foreground can be identified by its blend/anim/texture without guessing.
            if (blend >= 2 || texAnim >= 0 || unlit)
                Console.WriteLine($"[glue]   batch sm{b.SubmeshIndex} blend{blend} " +
                                  $"{(unlit ? "unlit " : "lit   ")}texAnim{texAnim} tex '{texPath}'");

            _batches.Add(new Batch
            {
                IndexStart = sm.IndexStart,
                IndexCount = sm.IndexCount,
                Tex = LoadTexture(mpq, texPath),
                Blend = blend,
                ColorIndex = colorIdx,
                TexAnim = texAnim,
                Unlit = unlit,
                TwoSided = twoSided,
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

        // The stage spot for a booth character: attachment 0 (benilla seats the char-select body on
        // attachment 0 in every UI_* scene). M2Attachment.Position is already absolute model-space Y-up.
        foreach (var a in model.Attachments)
            if (a.Id == 0) { _stageSpot = a.Position; break; }
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
    /// type u16@0, bone i16@2, position C3@4, then M2Tracks - ambient colour@0x10, ambient
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
            int lightBone = (short)(b[rec + 2] | b[rec + 3] << 8);
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
                Vector3 pd;
                if (type == 0)
                {
                    // DIRECTIONAL: the to-light direction is the light BONE's model-space +Z axis
                    // (benilla-formats bone_z_axis; benilla_glue_booth.rs:250 folds THIS, and calls the
                    // def position a "decoy"). Position is only the point-light location. WoW Z-up -> Y-up.
                    pd = ToYUp(BoneZAxis(b, lightBone));
                    pd = pd.LengthSquared() > 1e-6f ? Vector3.Normalize(pd) : Vector3.UnitY;
                }
                else
                {
                    pd = ToYUp(posRaw);   // POINT: the light position
                }
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

        // The scene's sun = the brightest DIRECTIONAL light's horizontal to-light direction. The booth
        // fill rides this so the fill comes from the same side as the authored sun (per race).
        float bestLum = -1f;
        foreach (var L in _lights)
        {
            if (L.Type != 0) continue;   // directional only
            var h = new Vector3(L.PosDir.X, 0f, L.PosDir.Z);
            if (h.LengthSquared() < 1e-4f) continue;
            float lum = L.Color.X * 0.3f + L.Color.Y * 0.59f + L.Color.Z * 0.11f;
            if (lum > bestLum) { bestLum = lum; _primaryLightDir = Vector3.Normalize(h); }
        }

        Console.WriteLine($"[glue] light rig: ambient ({_ambient.X:F2},{_ambient.Y:F2},{_ambient.Z:F2}), " +
                          $"{_lights.Length} diffuse light(s), sun dir ({_primaryLightDir.X:F2},{_primaryLightDir.Y:F2},{_primaryLightDir.Z:F2})");
        // Per-light detail so a too-dark/too-cool scene can be diagnosed against benilla's rig without
        // guessing (the faithful fix may need directional to-light = the light bone's +Z axis, not its
        // position, which is what this dump will show is off for a given UI_<Race>).
        for (int i = 0; i < _lights.Length; i++)
        {
            var L = _lights[i];
            Console.WriteLine($"[glue]   light {i}: {(L.Type == 1 ? "point" : "dir  ")} " +
                              $"color ({L.Color.X:F2},{L.Color.Y:F2},{L.Color.Z:F2}) " +
                              $"posdir ({L.PosDir.X:F2},{L.PosDir.Y:F2},{L.PosDir.Z:F2})");
        }
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
        // BACK-FACE CULLING, per material. This used to be a blanket Disable with the note "the
        // gate has no back-face cull" - it does. Only render flag bit 0x04 is two-sided, and on
        // UI_MainMenu that is the ELWYNNGRASS card alone; every piece of stone is single-sided.
        //
        // Drawing the single-sided stone two-sided is not a harmless extra: the gate is an open
        // shell, not a closed solid, so a back face is not always hidden behind a front face. The
        // lintel's far edge showed the BACK of the arch's upper slabs as two flat grey panels
        // hanging below the stone - in screen area where the OG draws plain SKY, so nothing was
        // in front to depth-reject them, and they read as unblended patches.
        //
        // The winding convention is the one ClientWindow sets globally (CullFace Back +
        // FrontFace CCW) and every other M2 path here relies on. Verified for THIS mesh against
        // the authored camera: cross(P1-P0, P2-P0) agrees in sign with the authored vertex
        // normals on 99-100% of every submesh, so the front face is the normal side. And measured
        // from that camera, the backdrop this scene cannot afford to lose - sky dome, Aerie Peak
        // card, eyes, gradient, all three cloud sheets, grass - is 100% FRONT-facing, while the
        // solids run 13-58% back-facing. Culling can only take the second group.
        _gl.Enable(EnableCap.CullFace);
        bool cullingOn = true;
        _gl.Enable(EnableCap.Blend);

        _shader!.Use();
        _shader.Set("uMVP", mvp);
        _shader.Set("uEye", _eye);
        _shader.Set("uFogColor", FogColor);
        _shader.Set("uFogNear", FogNear);
        _shader.Set("uFogFar", FogFar);
        _shader.Set("uFogStrength", OpaqueFogStrength);
        _shader.Set("uFogEnabled", _fogEnabled ? 1 : 0);
        _shader.Set("uSceneTint", SceneTint);
        _shader.Set("uFillDir", SceneFillDir);
        _shader.Set("uFillColor", SceneFillColor);
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
        // Two passes, matching benilla / the standard transparency rule (model_render.rs:177-189 -
        // Opaque/AlphaTest ride the opaque pass, Blend/Mod/Mod2x/Add ride the transparent pass, which
        // Bevy draws second). MSUIClient used to draw batches in raw FILE order, so UI_Human's
        // alpha-blended STREET (early, no depth write) drew before the OPAQUE caustic water + buildings
        // (later), which then painted OVER it - the "screwed-up street". Pass 0 lays down every opaque/
        // alpha-key batch WITH depth write; pass 1 composites the blended/additive batches over them.
        for (int pass = 0; pass < 2; pass++)
        {
            bool transparentPass = pass == 1;
            foreach (var b in _batches)
            {
                bool additive = b.Blend is 3 or 4;
                bool blended = b.Blend >= 2;          // 2 = alpha, 3/4 = additive - the transparent pass
                if (blended != transparentPass) continue;

                if (b.TwoSided == cullingOn)
                {
                    cullingOn = !b.TwoSided;
                    if (cullingOn) _gl.Enable(EnableCap.CullFace);
                    else _gl.Disable(EnableCap.CullFace);
                }

                bool alphaKey = b.Blend == 1;
                if (additive) { _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One); _gl.DepthMask(false); }
                else if (blended) { _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha); _gl.DepthMask(false); }
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
        }
        _gl.BindVertexArray(0);
        _gl.DepthMask(true);
        // Leave culling ON: that is the global default ClientWindow sets, and GlueBooth's
        // character pass documents that it expects to start from it.
        if (!cullingOn) _gl.Enable(EnableCap.CullFace);

        // The brazier fires. Same Y-up camera as the mesh, but ParticleRenderer wants a
        // camera-RELATIVE view-projection (sprite centres are uploaded absolute; the shader
        // subtracts uCameraOrigin) - so eye-subtracted view here. worldUp = Y (glue space).
        if (_particles is not null)
        {
            Vector3 forward = Vector3.Normalize(_target - _eye);
            Vector3 right = Vector3.Cross(forward, Vector3.UnitY);
            right = right.LengthSquared() > 1e-6f ? Vector3.Normalize(right) : Vector3.UnitX;
            Matrix4x4 relVp = Matrix4x4.CreateLookAt(Vector3.Zero, _target - _eye, Vector3.UnitY) * proj;
            _particles.SpriteSizeScaleAll = MSUIClient.Engine.UI.GlueTune.ParticleSize;
            _particles.BrazierSizeScaleAll = MSUIClient.Engine.UI.GlueTune.BrazierSize;
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

    // ── Light-bone +Z axis (benilla-formats records.rs bone_z_axis) ─────────────────────────────
    // A directional light's to-light direction is its bone's model-space +Z axis at the track origin:
    // the FIRST rotation key of each bone composed up the parent chain (global = root o ... o local),
    // applied to (0,0,1). Rotationless chains yield (0,0,1) = up (vanilla's rest skeleton is pure
    // translations). Bone table count@0x34 / ofs@0x38, stride 0x6c; per bone: flags u32@4 (bit 0x04 =
    // keep the model root's orientation, stop inheriting), parent u16@8, rotation M2Track @0x28.
    private static Vector3 BoneZAxis(byte[] b, int bone)
    {
        if (!U32(b, 0x34, out uint count) || count == 0 || count > 4096 || !U32(b, 0x38, out uint ofs))
            return new Vector3(0f, 0f, 1f);
        const int STRIDE = 0x6c;
        var chain = new List<Vector4>();
        int idx = bone;
        for (int guard = 0; guard <= count; guard++)
        {
            if (idx < 0 || idx >= count) break;
            long rec = ofs + (long)idx * STRIDE;
            if (rec + STRIDE > b.Length) break;
            if (TrackFirstQuat(b, rec + 0x28, out Vector4 q)) chain.Add(q);
            U32(b, rec + 4, out uint flags);
            if ((flags & 0x4) != 0) break;                    // keeps the model root's orientation
            idx = (short)(b[rec + 8] | b[rec + 9] << 8);       // parent (i16)
        }
        Vector4 acc = new(0f, 0f, 0f, 1f);
        for (int i = chain.Count - 1; i >= 0; i--)             // root-first: global = root o ... o local
            acc = QuatMul(acc, chain[i]);
        return QuatRotateZ(acc);
    }

    // First value of a vanilla 28-byte M2Track whose values are [x,y,z,w] f32 quaternions (v256 bone
    // rotation key shape). count@0x14 / ofs@0x18 into the track.
    private static bool TrackFirstQuat(byte[] b, long track, out Vector4 q)
    {
        q = default;
        if (!U32(b, track + 0x14, out uint nval) || nval == 0 || !U32(b, track + 0x18, out uint vofs)) return false;
        if (vofs + 16 > b.Length) return false;
        q = new Vector4(F(b, vofs), F(b, vofs + 4), F(b, vofs + 8), F(b, vofs + 12));
        return true;
    }

    // Hamilton product a*b of two [x,y,z,w] quaternions.
    private static Vector4 QuatMul(Vector4 a, Vector4 c) => new(
        a.W * c.X + a.X * c.W + a.Y * c.Z - a.Z * c.Y,
        a.W * c.Y - a.X * c.Z + a.Y * c.W + a.Z * c.X,
        a.W * c.Z + a.X * c.Y - a.Y * c.X + a.Z * c.W,
        a.W * c.W - a.X * c.X - a.Y * c.Y - a.Z * c.Z);

    // q * (0,0,1) * q^-1 - the +Z axis rotated by q, closed form.
    private static Vector3 QuatRotateZ(Vector4 q) => new(
        2f * (q.X * q.Z + q.W * q.Y),
        2f * (q.Y * q.Z - q.W * q.X),
        1f - 2f * (q.X * q.X + q.Y * q.Y));

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
        Console.WriteLine($"[glue] no authored camera; framing {Path.GetFileNameWithoutExtension(_modelPath)} from bounds");
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
uniform int uLightCount;
uniform int uLightType[4];
uniform vec3 uLightPos[4];
uniform vec3 uLightColor[4];
out vec3 vModelPos;
out vec3 vNormal;
out vec2 vUv;
out float vDist;
out vec3 vPointLit;
void main(){
    gl_Position = uMVP * vec4(aPos, 1.0);
    vModelPos = aPos;
    vNormal = aNormal;
    vUv = aUv;
    vDist = length(uEye - aPos);

    // POINT lights are a PER-VERTEX term, interpolated - not a per-pixel one. The reference is
    // fixed-function GL T&L: it evaluates ambient + sun + the <=3 nearest point lights ONCE PER
    // VERTEX and interpolates the result (benilla wow_model.wgsl computes point_light_sum in its
    // VERTEX stage for exactly this reason; the clamp note in lighting/global_light.rs spells out
    // that what the eye reads as saturation is the per-vertex clamp, never the commit).
    //
    // Evaluating it per FRAGMENT instead - which is what this shader used to do - is not a
    // refinement, it is a different picture. UI_MainMenu's light 0 is a warm point light at
    // (-3.12, 0.43, 0.87) with diffuse (0.84,0.50,0.19) x intensity 2.0, sitting one to two
    // yards off the statues' faces. Per fragment, the 1/(0.7d + 0.03d^2) falloff and the
    // max(N.L,0) terminator resolve at pixel resolution and paint hard-edged dark bands across
    // the robes and shoulders that the OG's Gouraud gradient never shows. Per vertex, the same
    // rig reads as the smooth wash it does in 1.12.
    vec3 N = normalize(aNormal);
    vec3 V = normalize(uEye - aPos);
    if (dot(N, V) < 0.0) N = -N;               // two-sided, as the fragment stage does
    vec3 pl = vec3(0.0);
    for (int i = 0; i < uLightCount && i < 4; i++) {
        if (uLightType[i] != 1) continue;
        vec3 d = uLightPos[i] - aPos;
        float dist = length(d);
        vec3 L = dist > 1e-4 ? d / dist : vec3(0.0, 1.0, 0.0);
        // The engine's fixed falloff, NO authored-radius clamp: uLightAtten carries the authored
        // radii and the reference never reads them (benilla glue_booth.rs SCENE_POINT_RANGE).
        float atten = 1.0 / (0.7 * dist + 0.03 * dist * dist);
        pl += uLightColor[i] * (max(dot(N, L), 0.0) * atten);
    }
    vPointLit = pl;
}";

    private const string FragSrc = @"#version 330 core
in vec3 vModelPos;
in vec3 vNormal;
in vec2 vUv;
in float vDist;
in vec3 vPointLit;
uniform sampler2D uTex;
uniform vec3 uFogColor;
uniform float uFogNear;
uniform float uFogFar;
uniform float uFogStrength;
uniform int uFogEnabled;
uniform vec3 uSceneTint;
uniform vec3 uFillDir;
uniform vec3 uFillColor;
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
    if (uFogEnabled == 0) fog = 0.0;   // char-select Race scenes render UNFOGGED (benilla)

    if (uEmissive == 1) {
        if (uAdditive == 1) { frag = vec4(rgb * (1.0 - fog), a); return; }
        frag = vec4(mix(rgb, uFogColor, fog * uFogStrength), a);
        return;
    }

    vec3 N = normalize(vNormal);
    vec3 V = normalize(uEye - vModelPos);
    if (dot(N, V) < 0.0) N = -N;               // for the genuinely two-sided batches (flag 0x04)
    // The POINT term arrives INTERPOLATED from the vertex stage (see VertSrc): that is where
    // the reference evaluates it, and evaluating it per pixel is what put hard-edged shadow
    // bands on the gate statues.
    vec3 lit = uAmbient + vPointLit;            // Sigma authored ambient tracks (light[*] ambient)
    for (int i = 0; i < uLightCount && i < 4; i++) {
        if (uLightType[i] != 1) {
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
    // Supplemental fill (booth only; login uFillColor = 0 -> no contribution): one extra SH lobe that
    // lights up-facing surfaces (the floor/pathway) the per-race grazing rig leaves flat. Applied to
    // every LIT surface - the alpha-blended cobblestone STREET (blend 2) needs it as much as the opaque
    // stone; the unlit caustic water/sky/clouds take the EMISSIVE path above and never reach here, so
    // the boost cannot blow them out. uFillDir is always valid so normalize() never divides by zero.
    {
        vec3 fu = normalize(uFillDir);
        float fmu = dot(N, fu);
        lit += uFillColor * ((4.0 / 17.0) * (0.375 + 2.0 * fmu + 1.875 * fmu * fmu));
    }
    // FFP combine (benilla wow_model.wgsl:640): the light SUM saturates to [0,1] FIRST, THEN the
    // texture modulates it, so a surface never exceeds its own fully-lit texture (a brazier at
    // point-blank drives stone to tex*1, not past it into blown gold). Gamma space throughout.
    vec3 primary = clamp(lit, 0.0, 1.0);
    frag = vec4(mix(rgb * primary * uSceneTint, uFogColor, fog * uFogStrength), a);
}";
}

using System.Numerics;
using MSUIClient.Engine;
using MSUIClient.Formats;
using Silk.NET.OpenGL;
using Shader = MSUIClient.Engine.Shader;
using Texture = MSUIClient.Engine.Texture;

namespace MSUIClient.World.Units;

public readonly record struct SpellMeshDraw(
    long Id,
    string Path,
    M2Model Model,
    Matrix4x4 Transform,
    float Age,
    int SequenceIndex,
    bool GroundAnchor,
    string? CustomTexture,
    Vector3 Tint,
    float Opacity);

/// <summary>
/// Draws the mesh half of SpellVisualEffectName M2s. The particle half stays in
/// ParticleRenderer; both consume the same SpellEffectSource instances so a
/// cast, missile and impact remain spatially locked together.
/// </summary>
public sealed class SpellEffectMeshRenderer : IDisposable
{
    private const int FloatsPerVertex = 16;

    private sealed class GroundQuad
    {
        public int Bone;
        public M2Vertex[] Corners = [];
    }

    private sealed class Batch
    {
        public uint Start, Count;
        public Texture? Texture;
        public int Blend;
        public bool TwoSided;
        public bool NoZWrite;
        public bool NoZTest;
        public bool Unlit;
        public int FogPolicy;
        public GroundQuad? Ground;
        public bool AnimatedAlpha;
        public M2Batch Source = null!;
        public bool Transparent => Blend >= 2 || NoZWrite || AnimatedAlpha;
    }

    private sealed class Mesh : IDisposable
    {
        private readonly GL _gl;
        public uint Vao, Vbo, Ebo;
        public readonly List<Batch> Batches = [];
        public M2Animator? Animator;
        public int BoneCount;
        public Mesh(GL gl) => _gl = gl;
        public void Dispose()
        {
            if (Vao != 0) _gl.DeleteVertexArray(Vao);
            if (Vbo != 0) _gl.DeleteBuffer(Vbo);
            if (Ebo != 0) _gl.DeleteBuffer(Ebo);
        }
    }

    private readonly GL _gl;
    private readonly MpqMount _mpq;
    private readonly Dictionary<string, Mesh?> _meshes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Texture?> _textures = new(StringComparer.OrdinalIgnoreCase);
    private Shader? _shader;
    private Shader? _groundShader;
    private uint _groundVao, _groundVbo;
    private readonly Matrix4x4[] _skin = new Matrix4x4[M2Animator.MaxBones];
    private readonly float[] _packed = new float[M2Animator.MaxBones * 12];
    public int DrawnLastFrame { get; private set; }
    private readonly HashSet<string> _drawnPaths = new(StringComparer.OrdinalIgnoreCase);
    public bool WasDrawn(string path) => _drawnPaths.Contains(path);

    public Vector3 SunDirection { get; set; } = Vector3.UnitZ;
    public Vector3 SunColor { get; set; } = Vector3.One;
    public float SunIntensity { get; set; } = .35f;
    public Vector3 AmbientColor { get; set; } = Vector3.One;
    public float AmbientIntensity { get; set; } = 1f;
    public Vector3 FogColor { get; set; } = new(.56f, .71f, .85f);
    public float FogStart { get; set; } = 350f;
    public float FogEnd { get; set; } = 777f;
    public float FarClip { get; set; } = 777f;
    public bool FogEnabled { get; set; } = true;

    public SpellEffectMeshRenderer(GL gl, MpqMount mpq)
    {
        _gl = gl;
        _mpq = mpq;
    }

    public unsafe void LoadShaders(string shaderDir)
    {
        _shader = Shader.FromSource(_gl, "spell-effect-mesh",
            SpellMeshSkinningLaw.VertexShaderSource, FragmentSource);
        _groundShader = Shader.FromSource(_gl, "spell-ground-decal", GroundVertexSource,
            GroundFragmentSource);
        _groundVao = _gl.GenVertexArray();
        _groundVbo = _gl.GenBuffer();
        _gl.BindVertexArray(_groundVao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _groundVbo);
        const uint stride = 6 * sizeof(float);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride,
            (void*)(3 * sizeof(float)));
        _gl.EnableVertexAttribArray(2);
        _gl.VertexAttribPointer(2, 1, VertexAttribPointerType.Float, false, stride,
            (void*)(5 * sizeof(float)));
        _gl.BindVertexArray(0);
    }

    public unsafe void Render(Camera camera,
        IEnumerable<SpellMeshDraw> instances,
        Func<float, float, float, float?>? sampleGround = null)
    {
        DrawnLastFrame = 0;
        _drawnPaths.Clear();
        if (_shader is null) return;
        var ready = instances.Select(x => (Source: x, Mesh: Resolve(x.Path, x.Model)))
            .Where(x => x.Mesh is not null).ToArray();
        if (ready.Length == 0) return;

        _shader.Use();
        _shader.Set("uCameraPos", Vector3.Zero);
        _shader.Set("uView", camera.RelativeView);
        _shader.Set("uSunDirection", SunDirection);
        _shader.Set("uSunColor", SunColor);
        _shader.Set("uSunIntensity", SunIntensity);
        _shader.Set("uAmbientColor", AmbientColor);
        _shader.Set("uAmbientIntensity", AmbientIntensity);
        _shader.Set("uShadowWrap", 1f);
        _shader.Set("uFogEnabled", FogEnabled ? 1 : 0);
        _shader.Set("uFogStart", FogStart);
        _shader.Set("uFogEnd", FogEnd);
        _shader.Set("uFogColor", FogColor);
        _shader.Set("uFarClip", FarClip);
        _shader.Set("uTexture", 0);

        bool culling = true;
        bool hadDepthTest = _gl.IsEnabled(EnableCap.DepthTest);
        bool depthTest = hadDepthTest;
        var groundDraws = new List<(float[] Vertices, Texture? Texture, int Blend,
            Vector3 Tint, float Opacity, int FogPolicy)>();
        for (int pass = 0; pass < 2; pass++)
        {
            bool transparent = pass == 1;
            if (transparent) { _gl.Enable(EnableCap.Blend); _gl.DepthMask(false); }

            foreach (var item in ready)
            {
                Mesh mesh = item.Mesh!;
                Matrix4x4 model = SpellMeshSkinningLaw.CameraRelativeModel(
                    item.Source.Transform, camera.Position);
                _shader.Set("uModel", model);
                _shader.Set("uModelViewProjection", model * camera.RelativeViewProjection);
                int boneCount = 0;
                if (mesh.Animator is not null && mesh.BoneCount > 0)
                {
                    M2Animator.Clip? clip = mesh.Animator.FindSequenceOrBake(
                        item.Source.SequenceIndex);
                    boneCount = Math.Min(mesh.BoneCount, M2Animator.MaxBones);
                    mesh.Animator.Evaluate(clip, item.Source.Age, item.Source.Age, _skin);
                    SpellMeshSkinningLaw.ApplyBillboardBones(item.Source.Model, item.Source.Transform,
                        camera.Position, camera.Forward, boneCount, _skin);
                    M2Animator.Pack(_skin, boneCount, _packed);
                    _shader.SetVec4Array("uBones", _packed, boneCount * 3);
                }
                _shader.Set("uBoneCount", boneCount);
                _gl.BindVertexArray(mesh.Vao);

                foreach (Batch batch in mesh.Batches)
                {
                    if (batch.Transparent != transparent) continue;
                    if (batch.TwoSided && culling) { _gl.Disable(EnableCap.CullFace); culling = false; }
                    else if (!batch.TwoSided && !culling) { _gl.Enable(EnableCap.CullFace); culling = true; }
                    Texture? drawTexture = batch.Texture ??
                        (item.Source.CustomTexture is { Length: > 0 } custom ? ResolveTexture(custom) : null);
                    if (drawTexture is { } texture)
                    {
                        texture.Bind(0); _shader.Set("uHasTexture", 1);
                        _shader.Set("uAlphaCutoff", batch.Blend == 1 ? 224f / 255f : 0f);
                    }
                    else { _shader.Set("uHasTexture", 0); _shader.Set("uAlphaCutoff", 0f); }
                    int sequence = item.Source.SequenceIndex;
                    Vector3 tint = item.Source.Tint;
                    float opacity = item.Source.Opacity;
                    if (batch.Source.ColorIndex >= 0 && batch.Source.ColorIndex < item.Source.Model.Colors.Count)
                    {
                        M2ColorAnimation color = item.Source.Model.Colors[batch.Source.ColorIndex];
                        tint *= M2TrackSampling.Vector(color.Color, item.Source.Model, sequence,
                            item.Source.Age, Vector3.One);
                        opacity *= M2TrackSampling.Fixed16(color.Alpha, item.Source.Model, sequence,
                            item.Source.Age);
                    }
                    if (batch.Source.TextureWeightIndex < item.Source.Model.TransparencyLookup.Count)
                    {
                        int track = item.Source.Model.TransparencyLookup[batch.Source.TextureWeightIndex];
                        if (track >= 0 && track < item.Source.Model.TransparencyTracks.Count)
                            opacity *= M2TrackSampling.Fixed16(item.Source.Model.TransparencyTracks[track],
                                item.Source.Model, sequence, item.Source.Age);
                    }
                    // TEMP diagnostic (Blizzard area-mesh hunt): sample what this batch resolves to.
                    if (Environment.GetEnvironmentVariable("MSUI_FX_TRACE") is not null &&
                        item.Source.Path.Contains("Blizzard", StringComparison.OrdinalIgnoreCase) &&
                        Environment.TickCount64 % 1000 < 20)
                        Console.WriteLine($"[fx-trace] {Path.GetFileName(item.Source.Path)} " +
                            $"age={item.Source.Age:0.00} seq={sequence} " +
                            $"blend={batch.Blend} ground={batch.Ground is not null} " +
                            $"tint=({tint.X:0.00},{tint.Y:0.00},{tint.Z:0.00}) opacity={opacity:0.000}");
                    _shader.Set("uTint", tint);
                    _shader.Set("uOpacity", opacity);
                    _shader.Set("uUnlit", batch.Unlit ? 1 : 0);
                    _shader.Set("uFogPolicy", batch.FogPolicy);
                    if (item.Source.GroundAnchor && batch.Ground is { } ground)
                    {
                        // BuildGroundQuad needs the WORLD transform, not the camera-relative
                        // `model`: it samples terrain height in world space and subtracts the camera
                        // itself (once). Passing the already camera-relative `model` here double-
                        // subtracted the eye (~9000yd off) and fed world sampling garbage, which is
                        // why every ground-anchored ring (Frost Nova, Arcane Explosion) drew nothing.
                        float[] projected = BuildGroundQuad(ground, item.Source.Model,
                            item.Source.Transform, camera.Position, sampleGround);
                        if (projected.Length > 0)
                        {
                            groundDraws.Add((projected, drawTexture, batch.Blend, tint, opacity,
                                batch.FogPolicy));
                            _drawnPaths.Add(item.Source.Path);
                        }
                        continue;
                    }
                    if (transparent) ApplyBlend(batch.Blend);
                    bool wantsDepthTest = !batch.NoZTest;
                    if (wantsDepthTest != depthTest)
                    {
                        if (wantsDepthTest) _gl.Enable(EnableCap.DepthTest);
                        else _gl.Disable(EnableCap.DepthTest);
                        depthTest = wantsDepthTest;
                    }
                    _gl.DepthMask(!transparent && !batch.NoZWrite);
                    _gl.DrawElements(PrimitiveType.Triangles, batch.Count,
                        DrawElementsType.UnsignedShort, (void*)(batch.Start * sizeof(ushort)));
                    _drawnPaths.Add(item.Source.Path);
                }
                DrawnLastFrame++;
            }
            if (transparent) { _gl.Disable(EnableCap.Blend); _gl.DepthMask(true); }
        }
        if (!culling) _gl.Enable(EnableCap.CullFace);
        _gl.BindVertexArray(0);
        if (!depthTest) { _gl.Enable(EnableCap.DepthTest); depthTest = true; }
        RenderGroundQuads(camera, groundDraws);
        if (!hadDepthTest) _gl.Disable(EnableCap.DepthTest);
    }

    private unsafe Mesh? Resolve(string path, M2Model model)
    {
        if (_meshes.TryGetValue(path, out Mesh? cached)) return cached;
        if (!model.IsValid) return _meshes[path] = null;
        float[] vertices = new float[model.Vertices.Count * FloatsPerVertex];
        for (int i = 0; i < model.Vertices.Count; i++)
        {
            M2Vertex v = model.Vertices[i]; int o = i * FloatsPerVertex;
            vertices[o] = v.PosX; vertices[o + 1] = v.PosY; vertices[o + 2] = v.PosZ;
            vertices[o + 3] = v.NormX; vertices[o + 4] = v.NormY; vertices[o + 5] = v.NormZ;
            vertices[o + 6] = v.TexU; vertices[o + 7] = v.TexV;
            SpellMeshSkinningLaw.VertexSkin resolved = SpellMeshSkinningLaw.Resolve(v);
            vertices[o + 8] = resolved.Weights.X; vertices[o + 9] = resolved.Weights.Y;
            vertices[o + 10] = resolved.Weights.Z; vertices[o + 11] = resolved.Weights.W;
            vertices[o + 12] = resolved.Indices.X; vertices[o + 13] = resolved.Indices.Y;
            vertices[o + 14] = resolved.Indices.Z; vertices[o + 15] = resolved.Indices.W;
        }
        ushort[] indices = model.Indices.ToArray();
        var mesh = new Mesh(_gl) { Vao = _gl.GenVertexArray(), Vbo = _gl.GenBuffer(), Ebo = _gl.GenBuffer() };
        _gl.BindVertexArray(mesh.Vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, mesh.Vbo);
        fixed (float* p = vertices) _gl.BufferData(BufferTargetARB.ArrayBuffer,
            (nuint)(vertices.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, mesh.Ebo);
        fixed (ushort* p = indices) _gl.BufferData(BufferTargetARB.ElementArrayBuffer,
            (nuint)(indices.Length * sizeof(ushort)), p, BufferUsageARB.StaticDraw);
        const uint stride = FloatsPerVertex * sizeof(float);
        _gl.EnableVertexAttribArray(0); _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray(1); _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        _gl.EnableVertexAttribArray(2); _gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, (void*)(6 * sizeof(float)));
        _gl.EnableVertexAttribArray(3); _gl.VertexAttribPointer(3, 4, VertexAttribPointerType.Float, false, stride, (void*)(8 * sizeof(float)));
        _gl.EnableVertexAttribArray(4); _gl.VertexAttribPointer(4, 4, VertexAttribPointerType.Float, false, stride, (void*)(12 * sizeof(float)));
        _gl.BindVertexArray(0);

        mesh.Animator = M2Animator.Build(model, model.Sequences.Select(s => (int)s.AnimationId),
            includeStaticSequences: true);
        mesh.BoneCount = Math.Min(mesh.Animator?.BoneCount ?? 0, M2Animator.MaxBones);

        foreach (M2Batch source in model.Batches)
        {
            if (source.SubmeshIndex >= model.Submeshes.Count) continue;
            M2Submesh sub = model.Submeshes[source.SubmeshIndex];
            if (sub.IndexCount == 0 || sub.IndexStart + sub.IndexCount > indices.Length) continue;
            M2RenderFlag? flags = source.MaterialIndex < model.RenderFlags.Count ? model.RenderFlags[source.MaterialIndex] : null;
            mesh.Batches.Add(new Batch
            {
                Start = sub.IndexStart, Count = sub.IndexCount,
                Texture = ResolveBatchTexture(model, source),
                Blend = flags?.BlendingMode ?? 0,
                TwoSided = flags?.TwoSided ?? true,
                NoZWrite = flags?.NoZWrite ?? false,
                NoZTest = flags?.NoZTest ?? false,
                Unlit = flags?.Unlit ?? false,
                FogPolicy = flags?.Unfogged == true ? 4 : FogPolicyForBlend(flags?.BlendingMode ?? 0),
                Source = source,
                Ground = FindGroundQuad(model, sub),
                AnimatedAlpha = HasAnimatedAlpha(model, source),
            });
        }
        if (mesh.Batches.Count == 0)
            mesh.Batches.Add(new Batch { Count = (uint)indices.Length, TwoSided = true,
                Source = new M2Batch() });
        return _meshes[path] = mesh;
    }

    private static int FogPolicyForBlend(int blend) => blend switch
    {
        3 or 4 => 1, // additive -> black
        5 => 2,      // modulate -> white
        6 => 3,      // modulate-2x -> neutral grey
        _ => 0,      // ordinary scene fog
    };

    private Texture? ResolveBatchTexture(M2Model model, M2Batch batch)
    {
        if (batch.TextureIndex >= model.TextureLookup.Count) return null;
        int index = model.TextureLookup[batch.TextureIndex];
        if (index < 0 || index >= model.Textures.Count) return null;
        string path = model.Textures[index].Filename.Replace('/', '\\');
        if (path.Length == 0) return null;
        return ResolveTexture(path);
    }

    private Texture? ResolveTexture(string path)
    {
        path = path.Replace('/', Path.DirectorySeparatorChar);
        if (_textures.TryGetValue(path, out Texture? cached)) return cached;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        byte[]? bytes = _mpq.ReadFile(path);
        if (bytes is null) return _textures[path] = null;
        try
        {
            byte[] pixels = BlpDecoder.GetPixels(bytes, 0, out int width, out int height);
            Texture texture = Texture.From2D(_gl, pixels, width, height, mipmaps: true, repeat: false);
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 /
                System.Diagnostics.Stopwatch.Frequency;
            if (ms > 2) Console.WriteLine($"[fx-load] mesh-tex {Path.GetFileName(path)} {ms:0.0}ms");
            return _textures[path] = texture;
        }
        catch { return _textures[path] = null; }
    }

    private static GroundQuad? FindGroundQuad(M2Model model, M2Submesh sub)
    {
        var unique = new HashSet<ushort>();
        int end = Math.Min(model.Indices.Count, sub.IndexStart + sub.IndexCount);
        for (int i = sub.IndexStart; i < end; i++) unique.Add(model.Indices[i]);
        if (unique.Count != 4) return null;
        int bone = -1;
        var vertices = new List<M2Vertex>(4);
        foreach (ushort index in unique)
        {
            if (index >= model.Vertices.Count) return null;
            M2Vertex v = model.Vertices[index];
            // Original M2 z=0 is Y=0 after the reader's coordinate conversion.
            if (MathF.Abs(v.PosY) > .01f || v.BoneWeight0 < 254 ||
                v.BoneWeight1 + v.BoneWeight2 + v.BoneWeight3 != 0) return null;
            if (bone < 0) bone = v.BoneIndex0;
            else if (bone != v.BoneIndex0) return null;
            vertices.Add(v);
        }
        M2Vertex[] ordered = vertices.OrderBy(v => v.PosZ).ThenBy(v => v.PosX).ToArray();
        // Triangle-strip order: minZ/minX, minZ/maxX, maxZ/minX, maxZ/maxX.
        return new GroundQuad { Bone = bone, Corners = ordered };
    }

    private float[] BuildGroundQuad(GroundQuad quad, M2Model source, Matrix4x4 model,
        Vector3 camera, Func<float, float, float, float?>? sampleGround)
    {
        Matrix4x4 joint = quad.Bone >= 0 && quad.Bone < source.Bones.Count
            ? _skin[quad.Bone]
            : Matrix4x4.Identity;
        Matrix4x4 world = joint * model;

        // Corner order from FindGroundQuad: 0=BL(minZ,minX) 1=BR(minZ,maxX) 2=TL(maxZ,minX)
        // 3=TR(maxZ,maxX). The ring is the texture painted across this quad; interpolate positions
        // and UVs bilinearly across the four world-space corners.
        Span<Vector3> cp = stackalloc Vector3[4];
        Span<Vector2> cuv = stackalloc Vector2[4];
        for (int i = 0; i < 4; i++)
        {
            M2Vertex c = quad.Corners[i];
            cp[i] = Vector3.Transform(new Vector3(c.PosX, c.PosY, c.PosZ), world);
            cuv[i] = new Vector2(c.TexU, c.TexV);
        }

        // Handedness normalization (benilla ground_fx.rs:70-83): keep the frame's +y'
        // axis matched to the quad's authored +y edge so the UV bilerp never flips.
        {
            Vector3 ex = cp[1] - cp[0] + cp[3] - cp[2];
            Vector3 ey = cp[2] - cp[0] + cp[3] - cp[1];
            if (ex.X * ey.Y - ex.Y * ey.X < 0)
            {
                (cp[0], cp[2]) = (cp[2], cp[0]); (cp[1], cp[3]) = (cp[3], cp[1]);
                (cuv[0], cuv[2]) = (cuv[2], cuv[0]); (cuv[1], cuv[3]) = (cuv[3], cuv[1]);
            }
        }

        // True projector first: re-emit the terrain triangles under the posed quad.
        if (FitFrame(cp) is { } frame && ProjectDecal(frame, cuv, camera) is { } projected)
            return projected;

        // The reference hides projected ground effects when no projectable terrain exists.
        return [];
    }

    // â”€â”€ Ground-decal projector (benilla decal.rs/ground_fx.rs, trace Â§9) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    //
    // The decal is never drawn as its own quad: the REAL terrain triangles inside a
    // fitted frame box are re-emitted with the quad's UVs bilerped across the frame and
    // a vertical-fade alpha. The emitted geometry is exactly coplanar with the drawn
    // ground, so with a strong depth bias the marker/ring can never clip into a slope,
    // and the pattern is never split by terrain curvature.

    /// <summary>Yaw-rotated horizontal rectangle Ã— a vertical slab, centred on Center.</summary>
    private readonly record struct DecalFrame(Vector3 Center, float Sin, float Cos,
        float HalfX, float HalfY, float Vert)
    {
        public (float X, float Y) InFrame(in Vector3 p)
        {
            float dx = p.X - Center.X, dy = p.Y - Center.Y;
            return (dx * Cos - dy * Sin, dy * Cos + dx * Sin);
        }
    }

    /// <summary>Signature of TerrainRenderer.GatherGroundTriangles; assigned by the host.</summary>
    public Action<float, float, float, float, List<(Vector3 A, Vector3 B, Vector3 C)>>? GatherGround;

    private readonly List<(Vector3 A, Vector3 B, Vector3 C)> _gatherScratch = new(256);
    private List<Vector3> _clipFront = new(16);
    private List<Vector3> _clipBack = new(16);

    /// <summary>Fit the projection frame to 4 posed corners (benilla fit_frame). Null for a
    /// degenerate pose (scale-0 first frame, edge-on tilt).</summary>
    private static DecalFrame? FitFrame(ReadOnlySpan<Vector3> c)
    {
        Vector3 center = (c[0] + c[1] + c[2] + c[3]) * .25f;
        Vector3 ex = (c[1] - c[0] + c[3] - c[2]) * .5f;
        Vector3 ey = (c[2] - c[0] + c[3] - c[1]) * .5f;
        var exh = new Vector2(ex.X, ex.Y);
        float halfX = exh.Length() * .5f;
        float halfY = new Vector2(ey.X, ey.Y).Length() * .5f;
        if (halfX < 1e-3f || halfY < 1e-3f) return null;
        Vector2 d = exh / (halfX * 2f);
        return new DecalFrame(center, -d.Y, d.X, halfX, halfY, 2f * MathF.Max(halfX, halfY));
    }

    private static float PlaneDistance(in DecalFrame f, int plane, in Vector3 p)
    {
        (float x, float y) = f.InFrame(p);
        float z = p.Z - f.Center.Z;
        return plane switch
        {
            0 => x + f.HalfX, 1 => f.HalfX - x,
            2 => y + f.HalfY, 3 => f.HalfY - y,
            4 => z + f.Vert, _ => f.Vert - z,
        };
    }

    /// <summary>Sutherlandâ€“Hodgman clip of one triangle against the frame's 6 half-planes.
    /// Returns the surviving convex polygon (shared scratch list) or null.</summary>
    private List<Vector3>? ClipToFrame(in DecalFrame f, Vector3 a, Vector3 b, Vector3 c)
    {
        _clipFront.Clear(); _clipFront.Add(a); _clipFront.Add(b); _clipFront.Add(c);
        for (int plane = 0; plane < 6 && _clipFront.Count >= 3; plane++)
        {
            _clipBack.Clear();
            int n = _clipFront.Count;
            for (int i = 0; i < n; i++)
            {
                Vector3 p0 = _clipFront[i], p1 = _clipFront[(i + 1) % n];
                float d0 = PlaneDistance(f, plane, p0), d1 = PlaneDistance(f, plane, p1);
                if (d0 >= 0) _clipBack.Add(p0);
                if (d0 >= 0 != d1 >= 0) _clipBack.Add(Vector3.Lerp(p0, p1, d0 / (d0 - d1)));
            }
            (_clipFront, _clipBack) = (_clipBack, _clipFront);
        }
        return _clipFront.Count >= 3 ? _clipFront : null;
    }

    /// <summary>
    /// Project the frame onto the terrain: gather real ground triangles under it, clip
    /// each to the frame, and emit camera-relative vertices with bilerped corner UVs
    /// and the vertical-fade alpha trapezoid. Null when no ground is in the slab (the
    /// mid-air gate) or no gatherer is wired.
    /// </summary>
    private float[]? ProjectDecal(in DecalFrame frame, ReadOnlySpan<Vector2> uv, Vector3 camera)
    {
        if (GatherGround is null) return null;
        _gatherScratch.Clear();
        float ext = MathF.Sqrt(frame.HalfX * frame.HalfX + frame.HalfY * frame.HalfY);
        GatherGround(frame.Center.X - ext, frame.Center.Y - ext,
            frame.Center.X + ext, frame.Center.Y + ext, _gatherScratch);
        if (_gatherScratch.Count == 0) return null;
        var vertices = new List<float>(512);
        foreach ((Vector3 a, Vector3 b, Vector3 c) in _gatherScratch)
        {
            if (ClipToFrame(frame, a, b, c) is not { } poly) continue;
            for (int k = 1; k < poly.Count - 1; k++)
            {
                EmitDecalVert(vertices, frame, uv, poly[0], camera);
                EmitDecalVert(vertices, frame, uv, poly[k], camera);
                EmitDecalVert(vertices, frame, uv, poly[k + 1], camera);
            }
        }
        return vertices.Count >= 18 ? vertices.ToArray() : null;
    }

    private static void EmitDecalVert(List<float> dst, in DecalFrame f,
        ReadOnlySpan<Vector2> uv, Vector3 p, Vector3 camera)
    {
        (float x, float y) = f.InFrame(p);
        float s = (x + f.HalfX) / (2f * f.HalfX);
        float t = (y + f.HalfY) / (2f * f.HalfY);
        Vector2 u = Vector2.Lerp(Vector2.Lerp(uv[0], uv[1], s), Vector2.Lerp(uv[2], uv[3], s), t);
        float dz = MathF.Abs(p.Z - f.Center.Z);
        float alpha = Math.Clamp((f.Vert - dz) / (.75f * f.Vert), 0f, 1f);
        dst.Add(p.X - camera.X); dst.Add(p.Y - camera.Y); dst.Add(p.Z - camera.Z);
        dst.Add(u.X); dst.Add(u.Y); dst.Add(alpha);
    }

    /// <summary>
    /// The 1.12 area-targeting reticle: Blizzard's Spell-Shadow-Acceptable circle projected onto
    /// the real terrain triangles at the cursor's ground point, sized to the spell radius,
    /// preserving the texture's authored green and alpha. No ground under the cursor â†’ no marker
    /// (the 1.12 mid-air gate).
    /// </summary>
    public void RenderTargetingMarker(Camera camera, Vector3 centre, float radius)
    {
        if (_groundShader is null) return;
        Texture? rune = ResolveTexture(@"Interface\SpellShadow\Spell-Shadow-Acceptable.blp");
        if (rune is null) return;
        var frame = new DecalFrame(centre, 0f, 1f, radius, radius, 2f * radius);
        Span<Vector2> uv = [new(0, 0), new(1, 0), new(0, 1), new(1, 1)];
        float[]? vertices = ProjectDecal(frame, uv, camera.Position);
        if (vertices is null) return;
        // GxRs blend-mode 2: the texture's authored alpha preserves the fine rings and glyphs.
        // Additive blending blooms those details into the broad neon arcs seen in the old marker.
        RenderGroundQuads(camera, [(vertices, rune, 2, Vector3.One, 1f, 0)]);
    }

    private unsafe void RenderGroundQuads(Camera camera,
        List<(float[] Vertices, Texture? Texture, int Blend, Vector3 Tint, float Opacity,
            int FogPolicy)> draws)
    {
        if (_groundShader is null || draws.Count == 0) return;
        _groundShader.Use();
        _groundShader.Set("uViewProjection", camera.RelativeViewProjection);
        _groundShader.Set("uView", camera.RelativeView);
        _groundShader.Set("uTexture", 0);
        _groundShader.Set("uFogEnabled", FogEnabled ? 1 : 0);
        _groundShader.Set("uFogColor", FogColor);
        _groundShader.Set("uFogStart", FogStart);
        _groundShader.Set("uFogEnd", FogEnd);
        _groundShader.Set("uFarClip", FarClip);
        _gl.BindVertexArray(_groundVao);
        _gl.Enable(EnableCap.Blend);
        _gl.DepthMask(false);
        _gl.Disable(EnableCap.CullFace);
        _gl.Enable(EnableCap.PolygonOffsetFill);
        _gl.PolygonOffset(-1f, -8192f); // benilla GROUND_FX_DEPTH_BIAS: coplanar decal must beat the opaque ground
        foreach (var draw in draws)
        {
            if (draw.Vertices.Length == 0) continue;
            _groundShader.Set("uHasTexture", draw.Texture is null ? 0 : 1);
            _groundShader.Set("uTint", draw.Tint);
            _groundShader.Set("uOpacity", draw.Opacity);
            _groundShader.Set("uFogPolicy", draw.FogPolicy);
            draw.Texture?.Bind(0);
            ApplyBlend(draw.Blend);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _groundVbo);
            fixed (float* p = draw.Vertices)
                _gl.BufferData(BufferTargetARB.ArrayBuffer,
                    (nuint)(draw.Vertices.Length * sizeof(float)), p, BufferUsageARB.StreamDraw);
            _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)(draw.Vertices.Length / 6));
        }
        _gl.Disable(EnableCap.PolygonOffsetFill);
        _gl.Enable(EnableCap.CullFace);
        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);
        _gl.BindVertexArray(0);
    }

    private static bool HasAnimatedAlpha(M2Model model, M2Batch batch)
    {
        if (batch.ColorIndex >= 0 && batch.ColorIndex < model.Colors.Count &&
            model.Colors[batch.ColorIndex].Alpha.Keys.Any(a => a < 32767)) return true;
        if (batch.TextureWeightIndex < model.TransparencyLookup.Count)
        {
            int track = model.TransparencyLookup[batch.TextureWeightIndex];
            if (track >= 0 && track < model.TransparencyTracks.Count &&
                model.TransparencyTracks[track].Keys.Any(a => a < 32767)) return true;
        }
        return false;
    }

    private void ApplyBlend(int mode)
    {
        if (mode is 3 or 4) _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);
        else if (mode == 5) _gl.BlendFunc(BlendingFactor.DstColor, BlendingFactor.Zero);
        else if (mode == 6) _gl.BlendFunc(BlendingFactor.DstColor, BlendingFactor.SrcColor);
        else _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
    }

    public void Dispose()
    {
        foreach (Mesh? mesh in _meshes.Values) mesh?.Dispose();
        foreach (Texture? texture in _textures.Values) texture?.Dispose();
        if (_groundVbo != 0) _gl.DeleteBuffer(_groundVbo);
        if (_groundVao != 0) _gl.DeleteVertexArray(_groundVao);
        _meshes.Clear(); _textures.Clear(); _shader?.Dispose();
        _groundShader?.Dispose();
    }

    private const string FragmentSource = @"#version 330 core
in vec3 vWorldPos;
in vec3 vNormal;
in vec2 vUV;
in float vEyeDepth;
uniform sampler2D uTexture;
uniform int uHasTexture;
uniform float uAlphaCutoff;
uniform vec3 uTint;
uniform float uOpacity;
uniform int uUnlit;
uniform vec3 uSunDirection;
uniform vec3 uSunColor;
uniform float uSunIntensity;
uniform vec3 uAmbientColor;
uniform float uAmbientIntensity;
uniform float uFogStart;
uniform float uFogEnd;
uniform vec3 uFogColor;
uniform int uFogEnabled;
uniform int uFogPolicy;
uniform float uFarClip;
out vec4 FragColor;
void main() {
    if (uFarClip > 0.0 && vEyeDepth > uFarClip) discard;
    vec4 tex = uHasTexture != 0 ? texture(uTexture, vUV) : vec4(1.0);
    vec4 base = vec4(tex.rgb * uTint, tex.a * uOpacity);
    if (base.a < uAlphaCutoff || base.a <= 0.001) discard;
    vec3 light = vec3(1.0);
    if (uUnlit == 0) {
        float n = max(dot(normalize(vNormal), normalize(uSunDirection)), 0.0);
        light = uAmbientColor * uAmbientIntensity + uSunColor * uSunIntensity * n;
    }
    vec3 rgb = base.rgb * light;
    if (uFogEnabled != 0 && uFogPolicy != 4) {
        float visibility = clamp((uFogEnd-vEyeDepth) / max(0.001,uFogEnd-uFogStart), 0.0, 1.0);
        vec3 target = uFogColor;
        if (uFogPolicy == 1) target = vec3(0.0);
        else if (uFogPolicy == 2) target = vec3(1.0);
        else if (uFogPolicy == 3) target = vec3(0.50196078);
        rgb = mix(target, rgb, visibility);
    }
    FragColor = vec4(rgb, base.a);
}";

    private const string GroundVertexSource = @"#version 330 core
layout(location=0) in vec3 aPosition;
layout(location=1) in vec2 aUV;
layout(location=2) in float aAlpha;
uniform mat4 uViewProjection;
uniform mat4 uView;
out vec2 vUV;
out float vAlpha;
out float vEyeDepth;
void main(){vUV=aUV;vAlpha=aAlpha;vEyeDepth=-(uView*vec4(aPosition,1.0)).z;gl_Position=uViewProjection*vec4(aPosition,1.0);}";

    private const string GroundFragmentSource = @"#version 330 core
in vec2 vUV;
in float vAlpha;
in float vEyeDepth;
uniform sampler2D uTexture;
uniform int uHasTexture;
uniform vec3 uTint;
uniform float uOpacity;
uniform int uFogEnabled;
uniform int uFogPolicy;
uniform vec3 uFogColor;
uniform float uFogStart;
uniform float uFogEnd;
uniform float uFarClip;
out vec4 FragColor;
void main(){
    if(uFarClip>0.0 && vEyeDepth>uFarClip)discard;
    vec4 t=uHasTexture!=0?texture(uTexture,vUV):vec4(1.0);
    FragColor=vec4(t.rgb*uTint,t.a*uOpacity*vAlpha);
    if(FragColor.a<=0.001)discard;
    if(uFogEnabled!=0 && uFogPolicy!=4){
        float visibility=clamp((uFogEnd-vEyeDepth)/max(0.001,uFogEnd-uFogStart),0.0,1.0);
        vec3 target=uFogColor;
        if(uFogPolicy==1)target=vec3(0.0);
        else if(uFogPolicy==2)target=vec3(1.0);
        else if(uFogPolicy==3)target=vec3(0.50196078);
        FragColor.rgb=mix(target,FragColor.rgb,visibility);
    }
}";
}


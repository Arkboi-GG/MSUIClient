using System.Numerics;
using MSUIClient.Engine;
using MSUIClient.Formats;
using Silk.NET.OpenGL;
using Shader = MSUIClient.Engine.Shader;
using Texture = MSUIClient.Engine.Texture;

namespace MSUIClient.World.Units;

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
        public bool Unlit;
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

    public SpellEffectMeshRenderer(GL gl, MpqMount mpq)
    {
        _gl = gl;
        _mpq = mpq;
    }

    public unsafe void LoadShaders(string shaderDir)
    {
        _shader = Shader.FromSource(_gl, "spell-effect-mesh", VertexSource, FragmentSource);
        _groundShader = Shader.FromSource(_gl, "spell-ground-decal", GroundVertexSource,
            GroundFragmentSource);
        _groundVao = _gl.GenVertexArray();
        _groundVbo = _gl.GenBuffer();
        _gl.BindVertexArray(_groundVao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _groundVbo);
        const uint stride = 5 * sizeof(float);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride,
            (void*)(3 * sizeof(float)));
        _gl.BindVertexArray(0);
    }

    public unsafe void Render(Camera camera,
        IEnumerable<(long Id, string Path, M2Model Model, Matrix4x4 Transform,
            float Age, int AnimationId, bool GroundAnchor, string? CustomTexture)> instances,
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
        _shader.Set("uSunDirection", Vector3.UnitZ);
        _shader.Set("uSunColor", Vector3.One);
        _shader.Set("uSunIntensity", .35f);
        _shader.Set("uAmbientColor", Vector3.One);
        _shader.Set("uAmbientIntensity", 1f);
        _shader.Set("uShadowWrap", 1f);
        _shader.Set("uFogStart", 10000f);
        _shader.Set("uFogEnd", 20000f);
        _shader.Set("uFogColor", Vector3.Zero);
        _shader.Set("uTexture", 0);

        bool culling = true;
        var groundDraws = new List<(float[] Vertices, Texture? Texture, int Blend,
            Vector3 Tint, float Opacity)>();
        for (int pass = 0; pass < 2; pass++)
        {
            bool transparent = pass == 1;
            if (transparent) { _gl.Enable(EnableCap.Blend); _gl.DepthMask(false); }

            foreach (var item in ready)
            {
                Mesh mesh = item.Mesh!;
                Matrix4x4 model = item.Source.Transform;
                model.M41 -= camera.Position.X;
                model.M42 -= camera.Position.Y;
                model.M43 -= camera.Position.Z;
                _shader.Set("uModel", model);
                _shader.Set("uModelViewProjection", model * camera.RelativeViewProjection);
                int boneCount = 0;
                if (mesh.Animator is not null && mesh.BoneCount > 0)
                {
                    M2Animator.Clip? clip = mesh.Animator.Find(item.Source.AnimationId) ??
                        mesh.Animator.Clips.Values.FirstOrDefault();
                    boneCount = Math.Min(mesh.BoneCount, M2Animator.MaxBones);
                    mesh.Animator.Evaluate(clip, item.Source.Age, item.Source.Age, _skin);
                    ApplyBillboardBones(item.Source.Model, item.Source.Transform,
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
                        _shader.Set("uAlphaCutoff", batch.Blend == 1 ? .25f : 0f);
                    }
                    else { _shader.Set("uHasTexture", 0); _shader.Set("uAlphaCutoff", 0f); }
                    int sequence = item.Source.Model.TryFindSequenceIndexByAnimationId(item.Source.AnimationId);
                    Vector3 tint = Vector3.One;
                    float opacity = 1f;
                    if (batch.Source.ColorIndex >= 0 && batch.Source.ColorIndex < item.Source.Model.Colors.Count)
                    {
                        M2ColorAnimation color = item.Source.Model.Colors[batch.Source.ColorIndex];
                        tint = M2TrackSampling.Vector(color.Color, item.Source.Model, sequence,
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
                            $"age={item.Source.Age:0.00} anim={item.Source.AnimationId} seq={sequence} " +
                            $"blend={batch.Blend} ground={batch.Ground is not null} " +
                            $"tint=({tint.X:0.00},{tint.Y:0.00},{tint.Z:0.00}) opacity={opacity:0.000}");
                    _shader.Set("uTint", tint);
                    _shader.Set("uOpacity", opacity);
                    _shader.Set("uUnlit", batch.Unlit ? 1 : 0);
                    if (item.Source.GroundAnchor && batch.Ground is { } ground)
                    {
                        // BuildGroundQuad needs the WORLD transform, not the camera-relative
                        // `model`: it samples terrain height in world space and subtracts the camera
                        // itself (once). Passing the already camera-relative `model` here double-
                        // subtracted the eye (~9000yd off) and fed world sampling garbage, which is
                        // why every ground-anchored ring (Frost Nova, Arcane Explosion) drew nothing.
                        groundDraws.Add((BuildGroundQuad(ground, item.Source.Model, item.Source.Transform,
                            camera.Position, sampleGround), drawTexture, batch.Blend, tint, opacity));
                        _drawnPaths.Add(item.Source.Path);
                        continue;
                    }
                    if (transparent) ApplyBlend(batch.Blend);
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
        RenderGroundQuads(camera, groundDraws);
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
            float total = v.BoneWeight0 + v.BoneWeight1 + v.BoneWeight2 + v.BoneWeight3;
            if (total <= 0)
            {
                vertices[o + 8] = 1; vertices[o + 12] = 0;
            }
            else
            {
                vertices[o + 8] = v.BoneWeight0 / total; vertices[o + 9] = v.BoneWeight1 / total;
                vertices[o + 10] = v.BoneWeight2 / total; vertices[o + 11] = v.BoneWeight3 / total;
                vertices[o + 12] = v.BoneIndex0 < M2Animator.MaxBones ? v.BoneIndex0 : 0;
                vertices[o + 13] = v.BoneIndex1 < M2Animator.MaxBones ? v.BoneIndex1 : 0;
                vertices[o + 14] = v.BoneIndex2 < M2Animator.MaxBones ? v.BoneIndex2 : 0;
                vertices[o + 15] = v.BoneIndex3 < M2Animator.MaxBones ? v.BoneIndex3 : 0;
            }
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
                Unlit = flags?.Unlit ?? false,
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

    /// <summary>
    /// Grid resolution per side for the ground-ring decal. A single flat 4-corner quad drapes an
    /// expanding AoE ring (~19yd for Frost Nova) as one planar patch; over rolling terrain its far
    /// half sinks below the curved ground and depth-fails, so only a consistent world-half (a
    /// crescent) survives — the "half the ring renders" bug. Tessellating and snapping EVERY grid
    /// vertex to the ground makes the ring hug the terrain everywhere → a full 360-degree ring. This
    /// is a cheap stand-in for benilla's true terrain-triangle projection (ground_fx.rs project_decal).
    /// </summary>
    private const int GroundTessellation = 10;

    private float[] BuildGroundQuad(GroundQuad quad, M2Model source, Matrix4x4 model,
        Vector3 camera, Func<float, float, float, float?>? sampleGround)
    {
        Matrix4x4 joint = quad.Bone >= 0 && quad.Bone < source.Bones.Count
            ? Matrix4x4.CreateTranslation(source.Bones[quad.Bone].Pivot) * _skin[quad.Bone]
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

        int n = GroundTessellation;
        var grid = new (Vector3 P, Vector2 UV)[(n + 1) * (n + 1)];
        for (int gz = 0; gz <= n; gz++)
        {
            float v = gz / (float)n;
            for (int gx = 0; gx <= n; gx++)
            {
                float u = gx / (float)n;
                Vector3 p = Bilerp(cp[0], cp[1], cp[2], cp[3], u, v);
                if (sampleGround?.Invoke(p.X, p.Y, p.Z) is float height) p.Z = height + .015f;
                p -= camera;
                grid[gz * (n + 1) + gx] = (p, Bilerp(cuv[0], cuv[1], cuv[2], cuv[3], u, v));
            }
        }

        // Two triangles per cell, emitted as a triangle list (culling is off, so winding is moot).
        float[] vertices = new float[n * n * 6 * 5];
        int o = 0;
        for (int gz = 0; gz < n; gz++)
            for (int gx = 0; gx < n; gx++)
            {
                var a = grid[gz * (n + 1) + gx];
                var b = grid[gz * (n + 1) + gx + 1];
                var c = grid[(gz + 1) * (n + 1) + gx];
                var d = grid[(gz + 1) * (n + 1) + gx + 1];
                WriteGroundVert(vertices, ref o, a);
                WriteGroundVert(vertices, ref o, b);
                WriteGroundVert(vertices, ref o, c);
                WriteGroundVert(vertices, ref o, c);
                WriteGroundVert(vertices, ref o, b);
                WriteGroundVert(vertices, ref o, d);
            }
        return vertices;
    }

    private static void WriteGroundVert(float[] dst, ref int o, (Vector3 P, Vector2 UV) v)
    {
        dst[o++] = v.P.X; dst[o++] = v.P.Y; dst[o++] = v.P.Z;
        dst[o++] = v.UV.X; dst[o++] = v.UV.Y;
    }

    private static Vector3 Bilerp(Vector3 bl, Vector3 br, Vector3 tl, Vector3 tr, float u, float v)
        => (1 - u) * (1 - v) * bl + u * (1 - v) * br + (1 - u) * v * tl + u * v * tr;

    private static Vector2 Bilerp(Vector2 bl, Vector2 br, Vector2 tl, Vector2 tr, float u, float v)
        => (1 - u) * (1 - v) * bl + u * (1 - v) * br + (1 - u) * v * tl + u * v * tr;

    /// <summary>
    /// The 1.12 area-targeting reticle: the AURARUNE circle draped over terrain at the
    /// cursor's ground point, sized to the spell radius, tinted green and drawn additively.
    /// Called from the world render pass every frame while ground-targeting is armed.
    /// </summary>
    public void RenderTargetingMarker(Camera camera, Vector3 centre, float radius,
        Func<float, float, float, float?>? sampleGround)
    {
        if (_groundShader is null) return;
        Texture? rune = ResolveTexture(@"SPELLS\AURARUNE256.BLP");
        if (rune is null) return;
        int n = GroundTessellation;
        var grid = new (Vector3 P, Vector2 UV)[(n + 1) * (n + 1)];
        for (int gy = 0; gy <= n; gy++)
            for (int gx = 0; gx <= n; gx++)
            {
                float u = gx / (float)n, v = gy / (float)n;
                var p = new Vector3(
                    centre.X + (u - .5f) * 2f * radius,
                    centre.Y + (v - .5f) * 2f * radius, centre.Z);
                if (sampleGround?.Invoke(p.X, p.Y, centre.Z + 3f) is float height)
                    p.Z = height + .03f;
                p -= camera.Position;
                grid[gy * (n + 1) + gx] = (p, new Vector2(u, v));
            }
        float[] vertices = new float[n * n * 6 * 5];
        int o = 0;
        for (int gy = 0; gy < n; gy++)
            for (int gx = 0; gx < n; gx++)
            {
                var a = grid[gy * (n + 1) + gx];
                var b = grid[gy * (n + 1) + gx + 1];
                var c = grid[(gy + 1) * (n + 1) + gx];
                var d = grid[(gy + 1) * (n + 1) + gx + 1];
                WriteGroundVert(vertices, ref o, a);
                WriteGroundVert(vertices, ref o, b);
                WriteGroundVert(vertices, ref o, c);
                WriteGroundVert(vertices, ref o, c);
                WriteGroundVert(vertices, ref o, b);
                WriteGroundVert(vertices, ref o, d);
            }
        RenderGroundQuads(camera, [(vertices, rune, 4, new Vector3(.25f, 1f, .35f), 1f)]);
    }

    private unsafe void RenderGroundQuads(Camera camera,
        List<(float[] Vertices, Texture? Texture, int Blend, Vector3 Tint, float Opacity)> draws)
    {
        if (_groundShader is null || draws.Count == 0) return;
        _groundShader.Use();
        _groundShader.Set("uViewProjection", camera.RelativeViewProjection);
        _groundShader.Set("uTexture", 0);
        _gl.BindVertexArray(_groundVao);
        _gl.Enable(EnableCap.Blend);
        _gl.DepthMask(false);
        _gl.Disable(EnableCap.CullFace);
        _gl.Enable(EnableCap.PolygonOffsetFill);
        _gl.PolygonOffset(-1f, -1f);
        foreach (var draw in draws)
        {
            _groundShader.Set("uHasTexture", draw.Texture is null ? 0 : 1);
            _groundShader.Set("uTint", draw.Tint);
            _groundShader.Set("uOpacity", draw.Opacity);
            draw.Texture?.Bind(0);
            ApplyBlend(draw.Blend);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _groundVbo);
            fixed (float* p = draw.Vertices)
                _gl.BufferData(BufferTargetARB.ArrayBuffer,
                    (nuint)(draw.Vertices.Length * sizeof(float)), p, BufferUsageARB.StreamDraw);
            _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)(draw.Vertices.Length / 5));
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

    private static void ApplyBillboardBones(M2Model model, Matrix4x4 modelTransform,
        Vector3 cameraWorld, Vector3 cameraForwardWorld, int boneCount, Matrix4x4[] skin)
    {
        if (!Matrix4x4.Invert(modelTransform, out Matrix4x4 inverse)) return;
        Vector3 camera = Vector3.Transform(cameraWorld, inverse);
        Vector3 forward = NormalizeOr(Vector3.TransformNormal(cameraForwardWorld, inverse),
            -Vector3.UnitZ);
        Vector3 cameraRightWorld = NormalizeOr(Vector3.Cross(cameraForwardWorld, Vector3.UnitZ),
            Vector3.UnitY);
        Vector3 right = NormalizeOr(Vector3.TransformNormal(cameraRightWorld, inverse),
            Vector3.UnitX);
        Vector3 cameraUpWorld = NormalizeOr(Vector3.Cross(cameraRightWorld, cameraForwardWorld),
            Vector3.UnitZ);
        Vector3 up = NormalizeOr(Vector3.TransformNormal(cameraUpWorld, inverse),
            Vector3.UnitY);

        // Keep the original globals so a child local can be recovered, then
        // walk in bone order and compose it onto a billboard-rewritten parent.
        // Benilla applies the law to the joint palette rather than only to the
        // directly weighted vertices; frost sheets and similar child-skinned
        // geometry depend on that propagation.
        var original = new Matrix4x4[boneCount];
        var replaced = new bool[boneCount];
        for (int i = 0; i < boneCount; i++)
            original[i] = Matrix4x4.CreateTranslation(model.Bones[i].Pivot) * skin[i];

        for (int i = 0; i < boneCount && i < model.Bones.Count; i++)
        {
            M2Bone bone = model.Bones[i];
            int parent = bone.ParentBone;
            bool parentChanged = parent >= 0 && parent < i && replaced[parent];
            uint flags = bone.Flags;
            bool ignoreParentRotation = (flags & 0x04) != 0;
            uint billboard = flags & 0x78;
            if (!parentChanged && !ignoreParentRotation && billboard == 0) continue;

            Matrix4x4 global = original[i];
            if (parentChanged && Matrix4x4.Invert(original[parent], out Matrix4x4 parentInverse))
            {
                Matrix4x4 local = original[i] * parentInverse;
                Matrix4x4 newParent = Matrix4x4.CreateTranslation(model.Bones[parent].Pivot) *
                    skin[parent];
                global = local * newParent;
            }

            if (!Matrix4x4.Decompose(global, out Vector3 scale, out Quaternion kept,
                    out Vector3 position))
                continue;

            if (ignoreParentRotation)
            {
                global = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateTranslation(position);
            }
            else if (billboard != 0)
            {
                Vector3 bx, by, bz;
                if ((billboard & 0x08) != 0) // spherical
                {
                    bx = -forward;
                    by = right;
                    bz = up;
                }
                else if ((billboard & 0x40) != 0) // keep authored WoW Z
                {
                    bz = NormalizeOr(Vector3.Transform(Vector3.UnitY, kept), Vector3.UnitY);
                    by = NormalizeOr(Vector3.Cross(forward, bz), right);
                    bx = NormalizeOr(Vector3.Cross(by, bz), -forward);
                }
                else if ((billboard & 0x10) != 0) // keep authored WoW X
                {
                    bx = NormalizeOr(Vector3.Transform(Vector3.UnitX, kept), -forward);
                    bz = NormalizeOr(Vector3.Cross(forward, bx), up);
                    by = NormalizeOr(Vector3.Cross(bz, bx), right);
                }
                else // 0x20: keep authored WoW Y
                {
                    by = NormalizeOr(Vector3.Transform(-Vector3.UnitZ, kept), right);
                    bx = NormalizeOr(Vector3.Cross(forward, by), -forward);
                    bz = NormalizeOr(Vector3.Cross(bx, by), up);
                }

                // M2Reader stores model space as (x, z, -y) from WoW:
                // local X=WoW X, local Y=WoW Z, local Z=-WoW Y. (The previous
                // basis here was benilla/Bevy's local convention — wrong for
                // MSUI verts; it turned every billboard edge-on to the camera.)
                Matrix4x4 facing = new(
                    bx.X, bx.Y, bx.Z, 0,
                    bz.X, bz.Y, bz.Z, 0,
                    -by.X, -by.Y, -by.Z, 0,
                    0, 0, 0, 1);
                global = Matrix4x4.CreateScale(scale) * facing *
                    Matrix4x4.CreateTranslation(position);
            }

            skin[i] = Matrix4x4.CreateTranslation(-bone.Pivot) * global;
            replaced[i] = true;
        }
    }

    private static Vector3 NormalizeOr(Vector3 value, Vector3 fallback) =>
        value.LengthSquared() > 1e-8f ? Vector3.Normalize(value) : fallback;

    public void Dispose()
    {
        foreach (Mesh? mesh in _meshes.Values) mesh?.Dispose();
        foreach (Texture? texture in _textures.Values) texture?.Dispose();
        if (_groundVbo != 0) _gl.DeleteBuffer(_groundVbo);
        if (_groundVao != 0) _gl.DeleteVertexArray(_groundVao);
        _meshes.Clear(); _textures.Clear(); _shader?.Dispose();
        _groundShader?.Dispose();
    }

    private const string VertexSource = @"#version 330 core
layout(location=0) in vec3 aPosition;
layout(location=1) in vec3 aNormal;
layout(location=2) in vec2 aUV;
layout(location=3) in vec4 aBoneWeights;
layout(location=4) in vec4 aBoneIndices;
uniform mat4 uModel;
uniform mat4 uModelViewProjection;
const int MAX_BONES = 160;
uniform vec4 uBones[MAX_BONES * 3];
uniform int uBoneCount;
out vec3 vWorldPos;
out vec3 vNormal;
out vec2 vUV;
vec3 skinPoint(vec3 p, int b) {
    vec4 h=vec4(p,1.0);
    return vec3(dot(uBones[b*3],h),dot(uBones[b*3+1],h),dot(uBones[b*3+2],h));
}
vec3 skinVector(vec3 v, int b) {
    return vec3(dot(uBones[b*3].xyz,v),dot(uBones[b*3+1].xyz,v),dot(uBones[b*3+2].xyz,v));
}
void main() {
    vec3 p=aPosition; vec3 n=aNormal;
    if (uBoneCount > 0) {
        vec3 sp=vec3(0.0); vec3 sn=vec3(0.0); float sum=0.0;
        for (int i=0;i<4;i++) {
            float w=aBoneWeights[i]; int b=int(aBoneIndices[i]+0.5);
            if (w<=0.0 || b<0 || b>=uBoneCount) continue;
            sp += skinPoint(aPosition,b)*w; sn += skinVector(aNormal,b)*w; sum += w;
        }
        if (sum>0.0001) { p=sp/sum; n=sn/sum; }
    }
    vec4 world=uModel*vec4(p,1.0);
    vWorldPos=world.xyz; vNormal=normalize(mat3(uModel)*n); vUV=aUV;
    gl_Position=uModelViewProjection*vec4(p,1.0);
}";

    private const string FragmentSource = @"#version 330 core
in vec3 vWorldPos;
in vec3 vNormal;
in vec2 vUV;
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
out vec4 FragColor;
void main() {
    vec4 tex = uHasTexture != 0 ? texture(uTexture, vUV) : vec4(1.0);
    vec4 base = vec4(tex.rgb * uTint, tex.a * uOpacity);
    if (base.a < uAlphaCutoff || base.a <= 0.001) discard;
    vec3 light = vec3(1.0);
    if (uUnlit == 0) {
        float n = max(dot(normalize(vNormal), normalize(uSunDirection)), 0.0);
        light = uAmbientColor * uAmbientIntensity + uSunColor * uSunIntensity * n;
    }
    float fog = clamp((length(vWorldPos) - uFogStart) / max(0.001, uFogEnd-uFogStart), 0.0, 1.0);
    FragColor = vec4(mix(base.rgb * light, uFogColor, fog), base.a);
}";

    private const string GroundVertexSource = @"#version 330 core
layout(location=0) in vec3 aPosition;
layout(location=1) in vec2 aUV;
uniform mat4 uViewProjection;
out vec2 vUV;
void main(){vUV=aUV;gl_Position=uViewProjection*vec4(aPosition,1.0);}";

    private const string GroundFragmentSource = @"#version 330 core
in vec2 vUV;
uniform sampler2D uTexture;
uniform int uHasTexture;
uniform vec3 uTint;
uniform float uOpacity;
out vec4 FragColor;
void main(){vec4 t=uHasTexture!=0?texture(uTexture,vUV):vec4(1.0);FragColor=vec4(t.rgb*uTint,t.a*uOpacity);if(FragColor.a<=0.001)discard;}";
}

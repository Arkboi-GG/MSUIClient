using System.Numerics;
using System.Diagnostics;
using MSUIClient.Engine;
using MSUIClient.Formats;
using Silk.NET.OpenGL;
using Shader = MSUIClient.Engine.Shader;
using Texture = MSUIClient.Engine.Texture;

namespace MSUIClient.World.Units;

/// <summary>Dynamic M2 ribbon trails used by spell slashes and projectiles.</summary>
public sealed class SpellRibbonRenderer : IDisposable
{
    private sealed class Edge
    {
        public Vector3 Top, Bottom;
        public float Born;
    }

    private sealed class Trail
    {
        public readonly List<Edge> Edges = [];
        public float LastAge;
        public float Accumulator;
        public float CurrentAge;
        public float Lifetime;
        public float Gravity;
        public Texture? Texture;
        public int Blend;
        public Vector3 Color = Vector3.One;
        public float Alpha = 1f;
        public float U0, U1 = 1f, V0, V1 = 1f;
        public string Path = "";
    }

    private readonly GL _gl;
    private readonly MpqMount _mpq;
    private readonly Dictionary<(long Instance, int Ribbon), Trail> _trails = [];
    private readonly Dictionary<string, Texture?> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, M2Animator?> _animators = new(StringComparer.OrdinalIgnoreCase);
    private readonly Matrix4x4[] _skin = new Matrix4x4[M2Animator.MaxBones];
    private Shader? _shader;
    private uint _vao, _vbo;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastWall;
    public int DrawnLastFrame { get; private set; }
    private readonly HashSet<string> _drawnPaths = new(StringComparer.OrdinalIgnoreCase);
    public bool WasDrawn(string path) => _drawnPaths.Contains(path);

    public SpellRibbonRenderer(GL gl, MpqMount mpq) { _gl = gl; _mpq = mpq; }

    public unsafe void LoadShaders()
    {
        _shader = Shader.FromSource(_gl, "spell-ribbon", VertexSource, FragmentSource);
        _vao = _gl.GenVertexArray();
        _vbo = _gl.GenBuffer();
        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        const uint stride = 9 * sizeof(float);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        _gl.EnableVertexAttribArray(2);
        _gl.VertexAttribPointer(2, 4, VertexAttribPointerType.Float, false, stride, (void*)(5 * sizeof(float)));
        _gl.BindVertexArray(0);
    }

    public unsafe void Render(Camera camera,
        IEnumerable<(long Id, string Path, M2Model Model, Matrix4x4 Transform,
            float Age, int AnimationId)> instances)
    {
        DrawnLastFrame = 0;
        _drawnPaths.Clear();
        if (_shader is null) return;
        double wall = _clock.Elapsed.TotalSeconds;
        float drainDt = (float)Math.Clamp(wall - _lastWall, 0, .1);
        _lastWall = wall;
        var seen = new HashSet<(long, int)>();
        _shader.Use();
        _shader.Set("uViewProjection", camera.RelativeViewProjection);
        _shader.Set("uTexture", 0);
        _gl.BindVertexArray(_vao);
        _gl.Enable(EnableCap.Blend);
        _gl.DepthMask(false);
        _gl.Disable(EnableCap.CullFace);

        foreach (var instance in instances)
        {
            int sequence = instance.Model.TryFindSequenceIndexByAnimationId(instance.AnimationId);
            M2Animator? animator = ResolveAnimator(instance.Path, instance.Model);
            if (animator is not null)
            {
                M2Animator.Clip? clip = animator.Find(instance.AnimationId) ?? animator.Clips.Values.FirstOrDefault();
                animator.Evaluate(clip, instance.Age, instance.Age, _skin);
            }

            for (int r = 0; r < instance.Model.RibbonEmitters.Count; r++)
            {
                M2RibbonEmitter def = instance.Model.RibbonEmitters[r];
                if (M2TrackSampling.Byte(def.Visibility, instance.Model, sequence, instance.Age) == 0)
                    continue;
                var key = (instance.Id, r);
                seen.Add(key);
                if (!_trails.TryGetValue(key, out Trail? trail))
                    _trails[key] = trail = new Trail { LastAge = instance.Age,
                        CurrentAge = instance.Age };
                float dt = Math.Clamp(instance.Age - trail.LastAge, 0, .1f);
                trail.LastAge = instance.Age;
                trail.CurrentAge = instance.Age;
                trail.Lifetime = def.EdgeLifetime;
                trail.Gravity = def.Gravity;
                trail.Path = instance.Path;
                for (int e = trail.Edges.Count - 1; e >= 0; e--)
                {
                    if (instance.Age - trail.Edges[e].Born >= def.EdgeLifetime)
                    { trail.Edges.RemoveAt(e); continue; }
                    if (def.Gravity != 0)
                    {
                        float sag = 2f * def.Gravity * dt;
                        trail.Edges[e].Top.Z -= sag;
                        trail.Edges[e].Bottom.Z -= sag;
                    }
                }

                Matrix4x4 joint = Matrix4x4.Identity;
                if (animator is not null && def.Bone < instance.Model.Bones.Count)
                    joint = Matrix4x4.CreateTranslation(instance.Model.Bones[def.Bone].Pivot) * _skin[def.Bone];
                Matrix4x4 world = joint * instance.Transform;
                Vector3 head = Vector3.Transform(def.Position, world);
                Vector3 axis = Vector3.TransformNormal(Vector3.UnitY, world);
                axis = axis.LengthSquared() > 1e-8f ? Vector3.Normalize(axis) : Vector3.UnitZ;
                float above = Math.Max(0, M2TrackSampling.Float(def.HeightAbove,
                    instance.Model, sequence, instance.Age));
                float below = Math.Max(0, M2TrackSampling.Float(def.HeightBelow,
                    instance.Model, sequence, instance.Age));
                Vector3 liveTop = head + axis * above, liveBottom = head - axis * below;

                trail.Accumulator += Math.Max(0, def.EdgesPerSecond) * dt;
                if (trail.Accumulator >= 1f && trail.Edges.Count < 512)
                {
                    trail.Accumulator -= MathF.Floor(trail.Accumulator);
                    trail.Edges.Add(new Edge { Top = liveTop, Bottom = liveBottom, Born = instance.Age });
                }
                if (trail.Edges.Count == 0 || ResolveTexture(instance.Model, def) is not { } texture)
                    continue;

                Vector3 color = M2TrackSampling.Vector(def.Color, instance.Model, sequence,
                    instance.Age, Vector3.One);
                float alpha = M2TrackSampling.Fixed16(def.Alpha, instance.Model, sequence,
                    instance.Age);
                trail.Texture = texture;
                trail.Color = color;
                trail.Alpha = alpha;
                int rows = Math.Max(1, (int)def.TextureRows);
                int columns = Math.Max(1, (int)def.TextureColumns);
                int slot = Math.Clamp(M2TrackSampling.UShort(def.TextureSlot, instance.Model,
                    sequence, instance.Age), 0, rows * columns - 1);
                int column = slot % columns, row = slot / columns;
                trail.U0 = column / (float)columns;
                trail.U1 = (column + 1f) / columns;
                trail.V0 = row / (float)rows;
                trail.V1 = (row + 1f) / rows;
                int pairCount = trail.Edges.Count + 1;
                float[] vertices = new float[pairCount * 2 * 9];
                WritePair(vertices, 0, liveTop, liveBottom, camera.Position, 0, color, alpha,
                    trail.U0, trail.U1, trail.V0, trail.V1);
                for (int n = 0; n < trail.Edges.Count; n++)
                {
                    Edge edge = trail.Edges[trail.Edges.Count - 1 - n];
                    float u = Math.Clamp((instance.Age - edge.Born) / def.EdgeLifetime, 0, 1);
                    WritePair(vertices, n + 1, edge.Top, edge.Bottom, camera.Position, u, color,
                        alpha, trail.U0, trail.U1, trail.V0, trail.V1);
                }

                texture.Bind(0);
                int blend = def.Material < instance.Model.RenderFlags.Count
                    ? instance.Model.RenderFlags[def.Material].BlendingMode : 3;
                trail.Blend = blend;
                if (blend is 3 or 4) _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);
                else _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
                fixed (float* p = vertices)
                    _gl.BufferData(BufferTargetARB.ArrayBuffer,
                        (nuint)(vertices.Length * sizeof(float)), p, BufferUsageARB.StreamDraw);
                _gl.DrawArrays(PrimitiveType.TriangleStrip, 0, (uint)(pairCount * 2));
                _drawnPaths.Add(instance.Path);
                DrawnLastFrame++;
            }
        }

        // Owner disappeared: commit nothing new, but let authored trail edges
        // drain instead of hard-cutting at missile impact/effect reap.
        foreach (var key in _trails.Keys.Where(k => !seen.Contains(k)).ToArray())
        {
            Trail trail = _trails[key];
            trail.CurrentAge += drainDt;
            for (int e = trail.Edges.Count - 1; e >= 0; e--)
            {
                if (trail.CurrentAge - trail.Edges[e].Born >= trail.Lifetime)
                { trail.Edges.RemoveAt(e); continue; }
                if (trail.Gravity != 0)
                {
                    float sag = 2f * trail.Gravity * drainDt;
                    trail.Edges[e].Top.Z -= sag;
                    trail.Edges[e].Bottom.Z -= sag;
                }
            }
            if (trail.Edges.Count < 2 || trail.Texture is null)
            { if (trail.Edges.Count == 0) _trails.Remove(key); continue; }
            int pairCount = trail.Edges.Count;
            float[] vertices = new float[pairCount * 18];
            for (int n = 0; n < pairCount; n++)
            {
                Edge edge = trail.Edges[pairCount - 1 - n];
                float u = Math.Clamp((trail.CurrentAge - edge.Born) / trail.Lifetime, 0, 1);
                WritePair(vertices, n, edge.Top, edge.Bottom, camera.Position, u,
                    trail.Color, trail.Alpha, trail.U0, trail.U1, trail.V0, trail.V1);
            }
            trail.Texture.Bind(0);
            if (trail.Blend is 3 or 4)
                _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);
            else _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
            fixed (float* p = vertices)
                _gl.BufferData(BufferTargetARB.ArrayBuffer,
                    (nuint)(vertices.Length * sizeof(float)), p, BufferUsageARB.StreamDraw);
            _gl.DrawArrays(PrimitiveType.TriangleStrip, 0, (uint)(pairCount * 2));
            _drawnPaths.Add(trail.Path);
            DrawnLastFrame++;
        }
        _gl.Enable(EnableCap.CullFace);
        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);
        _gl.BindVertexArray(0);
    }

    private static void WritePair(float[] dst, int pair, Vector3 top, Vector3 bottom,
        Vector3 camera, float age01, Vector3 color, float alpha,
        float u0, float u1, float v0, float v1)
    {
        float u = u0 + (u1 - u0) * age01;
        Write(dst, pair * 18, top - camera, u, v0, color, alpha);
        Write(dst, pair * 18 + 9, bottom - camera, u, v1, color, alpha);
    }

    private static void Write(float[] d, int o, Vector3 p, float u, float v, Vector3 c, float a)
    {
        d[o] = p.X; d[o + 1] = p.Y; d[o + 2] = p.Z;
        d[o + 3] = u; d[o + 4] = v;
        d[o + 5] = c.X; d[o + 6] = c.Y; d[o + 7] = c.Z; d[o + 8] = a;
    }

    private M2Animator? ResolveAnimator(string path, M2Model model)
    {
        if (_animators.TryGetValue(path, out M2Animator? found)) return found;
        return _animators[path] = M2Animator.Build(model,
            model.Sequences.Select(s => (int)s.AnimationId), includeStaticSequences: true);
    }

    private Texture? ResolveTexture(M2Model model, M2RibbonEmitter ribbon)
    {
        if (ribbon.Texture >= model.Textures.Count) return null;
        string path = model.Textures[ribbon.Texture].Filename.Replace('/', Path.DirectorySeparatorChar);
        if (path.Length == 0) return null;
        if (_textures.TryGetValue(path, out Texture? cached)) return cached;
        byte[]? bytes = _mpq.ReadFile(path);
        if (bytes is null) return _textures[path] = null;
        try
        {
            byte[] pixels = BlpDecoder.GetPixels(bytes, 0, out int width, out int height);
            return _textures[path] = Texture.From2D(_gl, pixels, width, height,
                mipmaps: true, repeat: true);
        }
        catch { return _textures[path] = null; }
    }

    public void Dispose()
    {
        foreach (Texture? texture in _textures.Values) texture?.Dispose();
        if (_vbo != 0) _gl.DeleteBuffer(_vbo);
        if (_vao != 0) _gl.DeleteVertexArray(_vao);
        _shader?.Dispose();
    }

    private const string VertexSource = @"#version 330 core
layout(location=0) in vec3 aPosition;
layout(location=1) in vec2 aUV;
layout(location=2) in vec4 aColor;
uniform mat4 uViewProjection;
out vec2 vUV;
out vec4 vColor;
void main(){ vUV=aUV; vColor=aColor; gl_Position=uViewProjection*vec4(aPosition,1.0); }";

    private const string FragmentSource = @"#version 330 core
in vec2 vUV;
in vec4 vColor;
uniform sampler2D uTexture;
out vec4 FragColor;
void main(){ FragColor=texture(uTexture,vUV)*vColor; if(FragColor.a<=0.001) discard; }";
}

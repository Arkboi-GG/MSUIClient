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
    private sealed class Trail
    {
        public readonly SpellRibbonHistoryLaw.State History = new();
        public M2Model Model = null!;
        public M2RibbonEmitter Definition = null!;
        public int SequenceIndex;
        public Texture? Texture;
        public int Blend;
        public int FogPolicy = 1;
        public Vector3 Color = Vector3.One;
        public float Alpha = 1f;
        public float U0, U1 = 1f, V0, V1 = 1f;
        public string Path = "";
        public int EmitterIndex;
    }

    private readonly GL _gl;
    private readonly MpqMount _mpq;
    private readonly Dictionary<(long Instance, int Ribbon), Trail> _trails = [];
    private readonly Dictionary<string, Texture?> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, M2Animator?> _animators = new(StringComparer.OrdinalIgnoreCase);
    private readonly Matrix4x4[] _skin = new Matrix4x4[M2Animator.MaxBones];
    private Shader? _shader;
    private uint _vao, _vbo, _ebo;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastWall;
    public int DrawnLastFrame { get; private set; }
    public int CandidatesLastFrame { get; private set; }
    public int VisibilityRejectedLastFrame { get; private set; }
    public int TextureRejectedLastFrame { get; private set; }
    public bool Enabled { get; set; } = true;
    public bool ForceVisibility { get; set; }
    public bool DepthTest { get; set; } = true;
    public float WidthScale { get; set; } = 1f;
    public float AlphaScale { get; set; } = 1f;
    private readonly HashSet<string> _drawnPaths = new(StringComparer.OrdinalIgnoreCase);
    public bool WasDrawn(string path) => _drawnPaths.Contains(path);

    public Vector3 FogColor { get; set; } = new(.56f, .71f, .85f);
    public float FogStart { get; set; } = 350f;
    public float FogEnd { get; set; } = 777f;
    public float FarClip { get; set; } = 777f;
    public bool FogEnabled { get; set; } = true;

    public SpellRibbonRenderer(GL gl, MpqMount mpq) { _gl = gl; _mpq = mpq; }

    public unsafe void LoadShaders()
    {
        _shader = Shader.FromSource(_gl, "spell-ribbon", VertexSource, FragmentSource);
        _vao = _gl.GenVertexArray();
        _vbo = _gl.GenBuffer();
        _ebo = _gl.GenBuffer();
        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
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
            float Age, int SequenceIndex)> instances, bool billboardJointPoseB = false)
    {
        DrawnLastFrame = 0;
        CandidatesLastFrame = 0;
        VisibilityRejectedLastFrame = 0;
        TextureRejectedLastFrame = 0;
        _drawnPaths.Clear();
        if (!Enabled || _shader is null) return;
        double wall = _clock.Elapsed.TotalSeconds;
        float rawWallDelta = double.IsFinite(wall - _lastWall)
            ? (float)Math.Max(0, wall - _lastWall) : 0f;
        _lastWall = wall;
        var seen = new HashSet<(long, int)>();
        _shader.Use();
        _shader.Set("uViewProjection", camera.RelativeViewProjection);
        _shader.Set("uView", camera.RelativeView);
        _shader.Set("uTexture", 0);
        _shader.Set("uFogEnabled", FogEnabled ? 1 : 0);
        _shader.Set("uFogColor", FogColor);
        _shader.Set("uFogStart", FogStart);
        _shader.Set("uFogEnd", FogEnd);
        _shader.Set("uFarClip", FarClip);
        _gl.BindVertexArray(_vao);
        _gl.Enable(EnableCap.Blend);
        _gl.DepthMask(false);
        _gl.Disable(EnableCap.CullFace);
        bool hadDepthTest = _gl.IsEnabled(EnableCap.DepthTest);
        if (DepthTest) _gl.Enable(EnableCap.DepthTest);
        else _gl.Disable(EnableCap.DepthTest);

        foreach (var instance in instances)
        {
            int sequence = instance.SequenceIndex;
            int visibilitySequence = sequence;
            M2Animator? animator = ResolveAnimator(instance.Path, instance.Model);
            if (animator is not null)
            {
                M2Animator.Clip? clip = animator.FindSequenceOrBake(sequence);
                animator.Evaluate(clip, instance.Age, instance.Age, _skin);
                SpellMeshSkinningLaw.ApplyBillboardBonesIfEnabled(billboardJointPoseB,
                    instance.Model, instance.Transform, camera.Position, camera.Forward,
                    animator.BoneCount, _skin);
            }

            for (int r = 0; r < instance.Model.RibbonEmitters.Count; r++)
            {
                CandidatesLastFrame++;
                M2RibbonEmitter def = instance.Model.RibbonEmitters[r];
                // Nonglobal visibility is a per-sequence spawn gate sampled at that sequence's
                // band start. A global-sequence track is not such a gate and stays always-on.
                if (!ForceVisibility && def.Visibility.Keys.Count > 0 &&
                    def.Visibility.GlobalSequence < 0 &&
                    visibilitySequence >= 0 && M2TrackSampling.Byte(def.Visibility,
                        instance.Model, visibilitySequence, 0f) == 0)
                {
                    VisibilityRejectedLastFrame++;
                    continue;
                }
                var key = (instance.Id, r);
                seen.Add(key);
                if (!_trails.TryGetValue(key, out Trail? trail))
                    _trails[key] = trail = new Trail();
                trail.Model = instance.Model;
                trail.Definition = def;
                trail.SequenceIndex = sequence;
                trail.Path = instance.Path;
                trail.EmitterIndex = r;
                SpellRibbonHistoryLaw.Step step = SpellRibbonHistoryLaw.AdvanceLive(
                    trail.History, instance.Age, def.EdgesPerSecond,
                    def.EdgeLifetime, def.Gravity);

                Matrix4x4 joint = Matrix4x4.Identity;
                if (animator is not null && def.Bone < instance.Model.Bones.Count)
                    joint = _skin[def.Bone];
                Vector3 head = SpellRibbonHistoryLaw.NodeWorld(def.Position, joint,
                    instance.Transform);
                Vector3 axis = SpellRibbonHistoryLaw.CrossSectionAxis(joint,
                    instance.Transform);
                float above = WidthScale * Math.Max(0, M2TrackSampling.Float(def.HeightAbove,
                    instance.Model, sequence, trail.History.ClipAge));
                float below = WidthScale * Math.Max(0, M2TrackSampling.Float(def.HeightBelow,
                    instance.Model, sequence, trail.History.ClipAge));
                Vector3 liveTop = head + axis * above, liveBottom = head - axis * below;

                if (step.Commit)
                    SpellRibbonHistoryLaw.Commit(trail.History, liveTop, liveBottom);
                if (trail.History.Edges.Count == 0)
                    continue;
                if (ResolveTexture(instance.Model, def) is not { } texture)
                {
                    TextureRejectedLastFrame++;
                    continue;
                }

                Vector3 color = M2TrackSampling.Vector(def.Color, instance.Model, sequence,
                    trail.History.ClipAge, Vector3.One);
                float alpha = Math.Clamp(M2TrackSampling.Fixed16(def.Alpha, instance.Model,
                    sequence, trail.History.ClipAge) * AlphaScale, 0f, 8f);
                trail.Texture = texture;
                trail.Color = color;
                trail.Alpha = alpha;
                int rows = Math.Max(1, (int)def.TextureRows);
                int columns = Math.Max(1, (int)def.TextureColumns);
                int slot = Math.Clamp((int)def.TextureSlot.Keys.FirstOrDefault(),
                    0, Math.Max(0, rows * columns - 1));
                int column = slot % columns, row = slot / columns;
                trail.U0 = column / (float)columns;
                trail.U1 = (column + 1f) / columns;
                trail.V0 = row / (float)rows;
                trail.V1 = (row + 1f) / rows;
                int pairCount = trail.History.Edges.Count + 1;
                float[] vertices = new float[pairCount * 2 * 9];
                uint[] indices = BuildTriangleIndices(pairCount);
                Vector3 anchor = head;
                WritePair(vertices, 0, liveTop, liveBottom, anchor, 0, color, alpha,
                    trail.U0, trail.U1, trail.V0, trail.V1);
                for (int n = 0; n < trail.History.Edges.Count; n++)
                {
                    SpellRibbonHistoryLaw.Edge edge =
                        trail.History.Edges[trail.History.Edges.Count - 1 - n];
                    float u = SpellRibbonHistoryLaw.EdgeAge01(trail.History, edge,
                        def.EdgeLifetime);
                    WritePair(vertices, n + 1, edge.Top, edge.Bottom, anchor, u, color,
                        alpha, trail.U0, trail.U1, trail.V0, trail.V1);
                }

                texture.Bind(0);
                int blend = def.Material < instance.Model.RenderFlags.Count
                    ? instance.Model.RenderFlags[def.Material].BlendingMode : 3;
                M2RenderFlag? renderFlag = def.Material < instance.Model.RenderFlags.Count
                    ? instance.Model.RenderFlags[def.Material] : null;
                trail.Blend = blend;
                trail.FogPolicy = renderFlag?.Unfogged == true
                    ? 0 : blend is 3 or 4 ? 2 : 1;
                _shader.Set("uFogPolicy", trail.FogPolicy);
                _shader.Set("uAnchor", anchor - camera.Position);
                if (blend is 3 or 4) _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);
                else _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
                fixed (float* p = vertices)
                    _gl.BufferData(BufferTargetARB.ArrayBuffer,
                        (nuint)(vertices.Length * sizeof(float)), p, BufferUsageARB.StreamDraw);
                _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
                fixed (uint* p = indices)
                    _gl.BufferData(BufferTargetARB.ElementArrayBuffer,
                        (nuint)(indices.Length * sizeof(uint)), p, BufferUsageARB.StreamDraw);
                _gl.DrawElements(PrimitiveType.Triangles, (uint)indices.Length,
                    DrawElementsType.UnsignedInt, (void*)0);
                _drawnPaths.Add(instance.Path);
                DrawnLastFrame++;
            }
        }

        // Owner disappeared: commit nothing new, but let authored trail edges
        // drain instead of hard-cutting at missile impact/effect reap.
        foreach (var key in _trails.Keys.Where(k => !seen.Contains(k)).ToArray())
        {
            Trail trail = _trails[key];
            SpellRibbonHistoryLaw.AdvanceDrain(trail.History, rawWallDelta,
                trail.Definition.EdgeLifetime, trail.Definition.Gravity);
            trail.Color = M2TrackSampling.Vector(trail.Definition.Color, trail.Model,
                trail.SequenceIndex, trail.History.ClipAge, Vector3.One);
            trail.Alpha = Math.Clamp(M2TrackSampling.Fixed16(trail.Definition.Alpha, trail.Model,
                trail.SequenceIndex, trail.History.ClipAge) * AlphaScale, 0f, 8f);
            if (trail.History.Edges.Count < 2 || trail.Texture is null)
            {
                if (trail.History.Edges.Count == 0) _trails.Remove(key);
                continue;
            }
            int pairCount = trail.History.Edges.Count;
            float[] vertices = new float[pairCount * 18];
            uint[] indices = BuildTriangleIndices(pairCount);
            SpellRibbonHistoryLaw.Edge newest = trail.History.Edges[^1];
            Vector3 anchor = (newest.Top + newest.Bottom) * .5f;
            for (int n = 0; n < pairCount; n++)
            {
                SpellRibbonHistoryLaw.Edge edge = trail.History.Edges[pairCount - 1 - n];
                float u = SpellRibbonHistoryLaw.EdgeAge01(trail.History, edge,
                    trail.Definition.EdgeLifetime);
                WritePair(vertices, n, edge.Top, edge.Bottom, anchor, u,
                    trail.Color, trail.Alpha, trail.U0, trail.U1, trail.V0, trail.V1);
            }
            trail.Texture.Bind(0);
            _shader.Set("uFogPolicy", trail.FogPolicy);
            _shader.Set("uAnchor", anchor - camera.Position);
            if (trail.Blend is 3 or 4)
                _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);
            else _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
            fixed (float* p = vertices)
                _gl.BufferData(BufferTargetARB.ArrayBuffer,
                    (nuint)(vertices.Length * sizeof(float)), p, BufferUsageARB.StreamDraw);
            _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
            fixed (uint* p = indices)
                _gl.BufferData(BufferTargetARB.ElementArrayBuffer,
                    (nuint)(indices.Length * sizeof(uint)), p, BufferUsageARB.StreamDraw);
            _gl.DrawElements(PrimitiveType.Triangles, (uint)indices.Length,
                DrawElementsType.UnsignedInt, (void*)0);
            _drawnPaths.Add(trail.Path);
            DrawnLastFrame++;
        }
        _gl.Enable(EnableCap.CullFace);
        _gl.DepthMask(true);
        if (hadDepthTest) _gl.Enable(EnableCap.DepthTest);
        else _gl.Disable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.Blend);
        _gl.BindVertexArray(0);
    }

    public readonly record struct Diagnostic(string Model, int Emitter, int Edges,
        bool TextureReady, float Alpha, bool Submitted);

    public IReadOnlyList<Diagnostic> Diagnostics()
        => _trails.Values.OrderBy(t => t.Path).ThenBy(t => t.EmitterIndex)
            .Select(t => new Diagnostic(Path.GetFileName(t.Path), t.EmitterIndex,
                t.History.Edges.Count, t.Texture is not null, t.Alpha,
                _drawnPaths.Contains(t.Path))).ToArray();

    private static uint[] BuildTriangleIndices(int pairCount)
    {
        var indices = new uint[Math.Max(0, pairCount - 1) * 6];
        for (uint pair = 0; pair + 1 < (uint)pairCount; pair++)
        {
            uint b = pair * 2;
            int o = (int)pair * 6;
            indices[o] = b;
            indices[o + 1] = b + 1;
            indices[o + 2] = b + 2;
            indices[o + 3] = b + 1;
            indices[o + 4] = b + 3;
            indices[o + 5] = b + 2;
        }
        return indices;
    }

    private static void WritePair(float[] dst, int pair, Vector3 top, Vector3 bottom,
        Vector3 anchor, float age01, Vector3 color, float alpha,
        float u0, float u1, float v0, float v1)
    {
        float u = u0 + (u1 - u0) * age01;
        Write(dst, pair * 18, top - anchor, u, v0, color, alpha);
        Write(dst, pair * 18 + 9, bottom - anchor, u, v1, color, alpha);
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
        if (_ebo != 0) _gl.DeleteBuffer(_ebo);
        if (_vbo != 0) _gl.DeleteBuffer(_vbo);
        if (_vao != 0) _gl.DeleteVertexArray(_vao);
        _shader?.Dispose();
    }

    private const string VertexSource = @"#version 330 core
layout(location=0) in vec3 aPosition;
layout(location=1) in vec2 aUV;
layout(location=2) in vec4 aColor;
uniform mat4 uViewProjection;
uniform mat4 uView;
uniform vec3 uAnchor;
out vec2 vUV;
out vec4 vColor;
out float vEyeDepth;
void main(){ vec3 p=aPosition+uAnchor; vUV=aUV; vColor=aColor; vEyeDepth=-(uView*vec4(p,1.0)).z; gl_Position=uViewProjection*vec4(p,1.0); }";

    private const string FragmentSource = @"#version 330 core
in vec2 vUV;
in vec4 vColor;
in float vEyeDepth;
uniform sampler2D uTexture;
uniform int uFogEnabled;
uniform int uFogPolicy;
uniform vec3 uFogColor;
uniform float uFogStart;
uniform float uFogEnd;
uniform float uFarClip;
out vec4 FragColor;
void main(){
    if(uFarClip>0.0 && vEyeDepth>uFarClip) discard;
    FragColor=texture(uTexture,vUV)*vColor;
    if(uFogEnabled!=0 && uFogPolicy!=0){
        float visibility=clamp((uFogEnd-vEyeDepth)/max(0.001,uFogEnd-uFogStart),0.0,1.0);
        vec3 target=uFogPolicy==2?vec3(0.0):uFogColor;
        FragColor.rgb=mix(target,FragColor.rgb,visibility);
    }
}";
}

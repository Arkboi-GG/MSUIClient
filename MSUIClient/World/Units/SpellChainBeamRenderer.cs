using System.Numerics;
using MSUIClient.Engine;
using MSUIClient.Formats;
using Silk.NET.OpenGL;
using Shader = MSUIClient.Engine.Shader;
using Texture = MSUIClient.Engine.Texture;

namespace MSUIClient.World.Units;

/// <summary>
/// Depth-tested additive renderer for the reference client's LightningObject/CLightning lane.
/// Beam geometry is world-horizontal, spindle-tapered, re-jittered and advected every frame.
/// </summary>
public sealed class SpellChainBeamRenderer : IDisposable
{
    private sealed class Strand
    {
        public readonly List<Vector3> Points = [];
        public uint Random;
    }

    private sealed class BeamState
    {
        public readonly Dictionary<(int Hop, int Strand), Strand> Strands = [];
        public double LastNow;
        public float Phase;
    }

    private readonly GL _gl;
    private readonly MpqMount _mpq;
    private readonly Dictionary<long, BeamState> _states = [];
    private readonly Dictionary<string, Texture?> _textures =
        new(StringComparer.OrdinalIgnoreCase);
    private Shader? _shader;
    private uint _vao, _vbo, _ebo;
    private readonly List<Vector3> _fresh = [];

    public int BeamsDrawnLastFrame { get; private set; }
    public int StrandsDrawnLastFrame { get; private set; }
    public float FarClip { get; set; } = 777f;

    public SpellChainBeamRenderer(GL gl, MpqMount mpq)
    {
        _gl = gl;
        _mpq = mpq;
    }

    public unsafe void LoadShaders()
    {
        _shader = Shader.FromSource(_gl, "spell-chain-beam", VertexSource, FragmentSource);
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
        _gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride,
            (void*)(3 * sizeof(float)));
        _gl.EnableVertexAttribArray(2);
        _gl.VertexAttribPointer(2, 4, VertexAttribPointerType.Float, false, stride,
            (void*)(5 * sizeof(float)));
        _gl.BindVertexArray(0);
    }

    public unsafe void Render(Camera camera, double now,
        IReadOnlyList<SpellChainBeamInstance> beams,
        Func<ulong, SpellUnitPose> unitPose)
    {
        BeamsDrawnLastFrame = 0;
        StrandsDrawnLastFrame = 0;
        if (_shader is null) return;

        var live = new HashSet<long>(beams.Select(beam => beam.Id));
        foreach (long stale in _states.Keys.Where(id => !live.Contains(id)).ToArray())
            _states.Remove(stale);

        _shader.Use();
        _shader.Set("uViewProjection", camera.RelativeViewProjection);
        _shader.Set("uView", camera.RelativeView);
        _shader.Set("uTexture", 0);
        _shader.Set("uFarClip", FarClip);
        _gl.BindVertexArray(_vao);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);
        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthMask(false);
        _gl.Disable(EnableCap.CullFace);

        foreach (SpellChainBeamInstance beam in beams)
        {
            if (ResolveTexture(beam.Effect.Texture) is not { } texture) continue;
            if (!_states.TryGetValue(beam.Id, out BeamState? state))
                _states[beam.Id] = state = new BeamState { LastNow = now };
            float dt = (float)Math.Clamp(now - state.LastNow, 0, .1);
            state.LastNow = now;
            float period = beam.Effect.ScrollPeriodSeconds;
            if (float.IsFinite(period) && period != 0f)
                state.Phase = (state.Phase + dt) % period;
            else
                state.Phase = 0f;
            float uScroll = period != 0f && float.IsFinite(period)
                ? -(state.Phase / period) : 0f;

            var nodes = new ulong[beam.Targets.Count + 1];
            nodes[0] = beam.Caster;
            for (int i = 0; i < beam.Targets.Count; i++) nodes[i + 1] = beam.Targets[i];
            bool drewBeam = false;
            for (int hop = 0; hop < beam.Targets.Count; hop++)
            {
                if (!SpellChainBeamLaw.HopVisible(beam, hop, now))
                {
                    ClearHop(state, hop);
                    continue;
                }
                SpellUnitPose fromPose = unitPose(nodes[hop]);
                SpellUnitPose toPose = unitPose(nodes[hop + 1]);
                if (!fromPose.Found || !toPose.Found)
                {
                    ClearHop(state, hop);
                    continue;
                }
                Vector3 from = hop == 0 ? CasterPoint(fromPose) :
                    DestinationPoint(fromPose, beam.DestinationAttachment);
                Vector3 to = DestinationPoint(toPose, beam.DestinationAttachment);
                for (int strandIndex = 0; strandIndex < beam.StrandCount; strandIndex++)
                {
                    var key = (hop, strandIndex);
                    if (!state.Strands.TryGetValue(key, out Strand? strand))
                    {
                        strand = new Strand
                        {
                            Random = unchecked((uint)(beam.Id * 0x9E3779B9L) ^
                                (uint)(hop * 0x85EBCA6B) ^ (uint)(strandIndex * 0xC2B2AE35))
                        };
                        state.Strands[key] = strand;
                    }
                    SpellChainBeamLaw.FreshPolyline(from, to, beam.Effect.NoiseScale,
                        ref strand.Random, _fresh, beam.Effect.AverageSegmentLength);
                    SpellChainBeamLaw.Advect(strand.Points, _fresh);
                    DrawStrand(camera, texture, strand.Points,
                        Math.Max(0f, beam.Effect.HalfWidth), uScroll);
                    StrandsDrawnLastFrame++;
                    drewBeam = true;
                }
            }
            if (drewBeam) BeamsDrawnLastFrame++;
        }

        _gl.Enable(EnableCap.CullFace);
        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);
        _gl.BindVertexArray(0);
    }

    private static void ClearHop(BeamState state, int hop)
    {
        foreach (var pair in state.Strands)
            if (pair.Key.Hop == hop)
                pair.Value.Points.Clear();
    }

    private unsafe void DrawStrand(Camera camera, Texture texture,
        IReadOnlyList<Vector3> points, float halfWidth, float uScroll)
    {
        if (points.Count < 2) return;
        int pairs = points.Count;
        var perpendiculars = new Vector3[pairs - 1];
        for (int i = 0; i + 1 < pairs; i++)
        {
            Vector3 d = points[i + 1] - points[i];
            Vector3 p = new(-d.Y, d.X, 0f);
            perpendiculars[i] = p.Length() > SpellChainBeamLaw.PerpendicularEpsilon
                ? Vector3.Normalize(p) : p;
        }
        Vector3 anchor = (points[0] + points[^1]) * .5f;
        var vertices = new float[pairs * 18];
        for (int i = 0; i < pairs; i++)
        {
            Vector3 top, bottom;
            float vTop, vBottom;
            if (i == 0 || i == pairs - 1)
            {
                top = bottom = points[i];
                vTop = vBottom = .5f;
            }
            else
            {
                Vector3 axis = (perpendiculars[i - 1] + perpendiculars[i]) *
                    (.5f * halfWidth);
                top = points[i] + axis;
                bottom = points[i] - axis;
                vTop = 0f;
                vBottom = 1f;
            }
            float u = i / (float)(pairs - 1) + uScroll;
            WriteVertex(vertices, i * 18, top - anchor, u, vTop);
            WriteVertex(vertices, i * 18 + 9, bottom - anchor, u, vBottom);
        }
        uint[] indices = BuildIndices(pairs);
        texture.Bind(0);
        _shader!.Set("uAnchor", anchor - camera.Position);
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
    }

    private static Vector3 CasterPoint(in SpellUnitPose pose)
    {
        if (pose.Model?.Events.FirstOrDefault(e => e.Identifier == "$CSL") is { } marker)
            return ModelPoint(pose, marker.Bone, marker.Position);
        float height = pose.Model?.Sequences.FirstOrDefault(s => s.AnimationId == 0)
            ?.BoundsZExtent ?? pose.Model?.Sequences.FirstOrDefault()?.BoundsZExtent ?? 0f;
        float scale = new Vector3(pose.UnitTransform.M11, pose.UnitTransform.M12,
            pose.UnitTransform.M13).Length();
        return pose.Position + Vector3.UnitZ * (height * scale * .75f);
    }

    private static Vector3 DestinationPoint(in SpellUnitPose pose, ushort requested)
    {
        if (pose.Model is null) return pose.Position;
        SpellAttachment.Point? point = requested != SpellVisualCatalog.NoMissileAttachment
            ? SpellAttachment.ResolveExact(pose.Model, requested) : null;
        point ??= SpellAttachment.ResolveExact(pose.Model, 0x22);
        if (point is null) return pose.Position;
        Matrix4x4 world = SpellAttachment.World(pose.Model, point.Value,
            pose.UnitTransform, pose.BoneMatrix);
        return new Vector3(world.M41, world.M42, world.M43);
    }

    private static Vector3 ModelPoint(in SpellUnitPose pose, ushort bone, Vector3 local)
    {
        if (pose.BoneMatrix(bone) is not { } model) return pose.Position;
        Vector3 pivot = pose.Model is { } m && bone < m.Bones.Count
            ? m.Bones[bone].Pivot : Vector3.Zero;
        Matrix4x4 world = Matrix4x4.CreateTranslation(local - pivot) * model * pose.UnitTransform;
        return new Vector3(world.M41, world.M42, world.M43);
    }

    private Texture? ResolveTexture(string rawPath)
    {
        string path = rawPath.Replace('/', Path.DirectorySeparatorChar);
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
        catch
        {
            return _textures[path] = null;
        }
    }

    private static uint[] BuildIndices(int pairs)
    {
        var indices = new uint[(pairs - 1) * 6];
        for (uint pair = 0; pair + 1 < (uint)pairs; pair++)
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

    private static void WriteVertex(float[] vertices, int offset, Vector3 point,
        float u, float v)
    {
        vertices[offset] = point.X;
        vertices[offset + 1] = point.Y;
        vertices[offset + 2] = point.Z;
        vertices[offset + 3] = u;
        vertices[offset + 4] = v;
        vertices[offset + 5] = 1f;
        vertices[offset + 6] = 1f;
        vertices[offset + 7] = 1f;
        vertices[offset + 8] = 1f;
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
uniform float uFarClip;
out vec4 FragColor;
void main(){
    if(uFarClip>0.0 && vEyeDepth>uFarClip) discard;
    FragColor=texture(uTexture,vUV)*vColor;
    if(FragColor.a < (1.0/255.0)) discard;
}";
}

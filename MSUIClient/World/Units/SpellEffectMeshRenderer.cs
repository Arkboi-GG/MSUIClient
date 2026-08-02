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

    private sealed class Batch
    {
        public uint Start, Count;
        public Texture? Texture;
        public int Blend;
        public bool TwoSided;
        public bool NoZWrite;
        public bool Transparent => Blend >= 2 || NoZWrite;
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

    public void LoadShaders(string shaderDir)
        => _shader = Shader.FromSource(_gl, "spell-effect-mesh", VertexSource,
            File.ReadAllText(Path.Combine(shaderDir, "character.frag")));

    public unsafe void Render(Camera camera,
        IEnumerable<(string Path, M2Model Model, Matrix4x4 Transform, float Age)> instances)
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
                    M2Animator.Clip? clip = mesh.Animator.Find(0) ?? mesh.Animator.Clips.Values.FirstOrDefault();
                    boneCount = Math.Min(mesh.BoneCount, M2Animator.MaxBones);
                    mesh.Animator.Evaluate(clip, item.Source.Age, item.Source.Age, _skin);
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
                    if (batch.Texture is { } texture)
                    {
                        texture.Bind(0); _shader.Set("uHasTexture", 1);
                        _shader.Set("uAlphaCutoff", batch.Blend == 1 ? .25f : 0f);
                    }
                    else { _shader.Set("uHasTexture", 0); _shader.Set("uAlphaCutoff", 0f); }
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

        mesh.Animator = M2Animator.Build(model, model.Sequences.Select(s => (int)s.AnimationId));
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
            });
        }
        if (mesh.Batches.Count == 0)
            mesh.Batches.Add(new Batch { Count = (uint)indices.Length, TwoSided = true });
        return _meshes[path] = mesh;
    }

    private Texture? ResolveBatchTexture(M2Model model, M2Batch batch)
    {
        if (batch.TextureIndex >= model.TextureLookup.Count) return null;
        int index = model.TextureLookup[batch.TextureIndex];
        if (index < 0 || index >= model.Textures.Count) return null;
        string path = model.Textures[index].Filename.Replace('/', '\\');
        if (path.Length == 0) return null;
        if (_textures.TryGetValue(path, out Texture? cached)) return cached;
        byte[]? bytes = _mpq.ReadFile(path);
        if (bytes is null) return _textures[path] = null;
        try
        {
            byte[] pixels = BlpDecoder.GetPixels(bytes, 0, out int width, out int height);
            return _textures[path] = Texture.From2D(_gl, pixels, width, height, mipmaps: true, repeat: false);
        }
        catch { return _textures[path] = null; }
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
        _meshes.Clear(); _textures.Clear(); _shader?.Dispose();
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
}

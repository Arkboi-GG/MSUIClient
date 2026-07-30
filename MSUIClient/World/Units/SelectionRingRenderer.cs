using System.Numerics;
using Silk.NET.OpenGL;
using MSUIClient.Engine;
using MSUIClient.Formats;
using Shader = MSUIClient.Engine.Shader;
using Texture = MSUIClient.Engine.Texture;

namespace MSUIClient.World.Units;

/// <summary>The real UnitSelectTexture, drawn additive beneath the selected unit.</summary>
public sealed class SelectionRingRenderer : IDisposable
{
    private readonly GL _gl;
    private Shader? _shader;
    private Texture? _texture;
    private uint _vao, _vbo;

    public unsafe SelectionRingRenderer(GL gl, MpqMount mpq)
    {
        _gl = gl;
        byte[]? blp = mpq.ReadFile(@"Textures\UnitSelectTexture.blp");
        if (blp is null) throw new InvalidOperationException("Textures\\UnitSelectTexture.blp is missing");
        byte[] pixels = BlpDecoder.GetPixels(blp, 0, out int width, out int height);
        _texture = Texture.From2D(gl, pixels, width, height, mipmaps: true, repeat: false);
        _shader = Shader.FromSource(gl, "selection_ring", VertexSource, FragmentSource);

        float[] vertices =
        [
            -1, -1, 0, 0, 0,
             1, -1, 0, 1, 0,
            -1,  1, 0, 0, 1,
             1,  1, 0, 1, 1,
        ];
        _vao = gl.GenVertexArray();
        _vbo = gl.GenBuffer();
        gl.BindVertexArray(_vao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        fixed (float* ptr = vertices)
            gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(vertices.Length * sizeof(float)),
                ptr, BufferUsageARB.StaticDraw);
        const uint stride = 5 * sizeof(float);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        gl.BindVertexArray(0);
    }

    public void Render(Camera camera, Vector3 feet, float radius, Vector3 color)
    {
        if (_shader is null || _texture is null || radius <= 0) return;
        Vector3 relative = feet - camera.Position + new Vector3(0, 0, .025f);
        Matrix4x4 model = Matrix4x4.CreateScale(radius) *
                          Matrix4x4.CreateRotationZ(camera.ViewYaw) *
                          Matrix4x4.CreateTranslation(relative);
        _gl.Enable(EnableCap.DepthTest);
        _gl.Enable(EnableCap.Blend);
        _gl.Disable(EnableCap.CullFace);
        _gl.DepthMask(false);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);
        _gl.Enable(EnableCap.PolygonOffsetFill);
        _gl.PolygonOffset(-1f, -1f);
        _shader.Use();
        _shader.Set("uViewProj", camera.RelativeViewProjection);
        _shader.Set("uModel", model);
        _shader.Set("uColor", color);
        _shader.Set("uTex", 0);
        _texture.Bind(0);
        _gl.BindVertexArray(_vao);
        _gl.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
        _gl.BindVertexArray(0);
        _gl.Disable(EnableCap.PolygonOffsetFill);
        _gl.DepthMask(true);
        _gl.Enable(EnableCap.CullFace);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
    }

    public void Dispose()
    {
        _texture?.Dispose(); _texture = null;
        _shader?.Dispose(); _shader = null;
        if (_vbo != 0) { _gl.DeleteBuffer(_vbo); _vbo = 0; }
        if (_vao != 0) { _gl.DeleteVertexArray(_vao); _vao = 0; }
    }

    private const string VertexSource = @"#version 330 core
layout(location=0) in vec3 aPos;
layout(location=1) in vec2 aUv;
uniform mat4 uModel;
uniform mat4 uViewProj;
out vec2 vUv;
void main(){ gl_Position = uViewProj * uModel * vec4(aPos,1.0); vUv=aUv; }";

    private const string FragmentSource = @"#version 330 core
in vec2 vUv;
uniform sampler2D uTex;
uniform vec3 uColor;
out vec4 frag;
void main(){ vec4 t=texture(uTex,vUv); frag=vec4(t.rgb*uColor,t.a); }";
}

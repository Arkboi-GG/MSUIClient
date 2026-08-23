using System.Numerics;
using MSUIClient.Engine;
using Silk.NET.OpenGL;
using Shader = MSUIClient.Engine.Shader;

namespace MSUIClient.World.Units;

/// <summary>
/// The engine-drawn fishing line: an opaque, depth-tested, unlit line strip.
/// Geometry is rebuilt from the exact reference law each frame because both
/// endpoints ride streamed/animated scene objects.
/// </summary>
public sealed class FishingLineRenderer : IDisposable
{
    private readonly GL _gl;
    private Shader? _shader;
    private uint _vao;
    private uint _vbo;

    public int DrawnLastFrame { get; private set; }

    public unsafe FishingLineRenderer(GL gl)
    {
        _gl = gl;
        _shader = Shader.FromSource(gl, "fishing-line", VertexSource, FragmentSource);
        _vao = gl.GenVertexArray();
        _vbo = gl.GenBuffer();
        gl.BindVertexArray(_vao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        gl.BufferData(BufferTargetARB.ArrayBuffer,
            (nuint)(FishingLineLaw.VertexCount * 3 * sizeof(float)), null,
            BufferUsageARB.DynamicDraw);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false,
            3 * sizeof(float), (void*)0);
        gl.BindVertexArray(0);
    }

    public unsafe void Render(Camera camera, IReadOnlyList<FishingLineSpan> spans,
        Vector3 ambientColor)
    {
        DrawnLastFrame = 0;
        if (_shader is null || spans.Count == 0) return;

        bool blendWasOn = _gl.IsEnabled(EnableCap.Blend);
        bool cullWasOn = _gl.IsEnabled(EnableCap.CullFace);
        bool depthWasOn = _gl.IsEnabled(EnableCap.DepthTest);
        _gl.Disable(EnableCap.Blend);
        _gl.Disable(EnableCap.CullFace);
        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthMask(true);
        _gl.LineWidth(1f);

        _shader.Use();
        _shader.Set("uViewProjection", camera.RelativeViewProjection);
        _shader.Set("uColor", Vector3.Clamp(ambientColor, Vector3.Zero, Vector3.One));
        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);

        foreach (FishingLineSpan span in spans)
        {
            Vector3[] points = FishingLineLaw.Build(
                span.Near - camera.Position, span.Far - camera.Position);
            fixed (Vector3* ptr = points)
                _gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0,
                    (nuint)(points.Length * 3 * sizeof(float)), ptr);
            _gl.DrawArrays(PrimitiveType.LineStrip, 0, (uint)points.Length);
            DrawnLastFrame++;
        }

        _gl.BindVertexArray(0);
        if (blendWasOn) _gl.Enable(EnableCap.Blend);
        if (cullWasOn) _gl.Enable(EnableCap.CullFace);
        if (!depthWasOn) _gl.Disable(EnableCap.DepthTest);
    }

    public void Dispose()
    {
        _shader?.Dispose();
        _shader = null;
        if (_vbo != 0) { _gl.DeleteBuffer(_vbo); _vbo = 0; }
        if (_vao != 0) { _gl.DeleteVertexArray(_vao); _vao = 0; }
    }

    private const string VertexSource = @"#version 330 core
layout(location=0) in vec3 aPosition;
uniform mat4 uViewProjection;
void main(){ gl_Position = uViewProjection * vec4(aPosition, 1.0); }";

    private const string FragmentSource = @"#version 330 core
uniform vec3 uColor;
out vec4 frag;
void main(){ frag = vec4(uColor, 1.0); }";
}

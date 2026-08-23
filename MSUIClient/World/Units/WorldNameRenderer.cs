using System.Numerics;
using ImGuiNET;
using Silk.NET.OpenGL;
using MSUIClient.Engine;
using Shader = MSUIClient.Engine.Shader;

namespace MSUIClient.World.Units;

/// <summary>
/// Camera-facing unit identity text in the depth-tested world pass. The glyph source is the
/// already-live ImGui atlas, but the geometry is ordinary world geometry: walls occlude it and
/// it participates in the finished-world glow/style passes rather than the HUD overlay.
/// </summary>
public sealed class WorldNameRenderer : IDisposable
{
    public readonly record struct Label(
        Vector3 Anchor, IReadOnlyList<string> Lines, Vector4 Color, float LinePitch);

    private readonly GL _gl;
    private Shader? _shader;
    private uint _vao;
    private uint _vbo;
    private float[] _vertices = new float[9 * 6 * 256];
    private int _vertexFloats;

    public unsafe WorldNameRenderer(GL gl)
    {
        _gl = gl;
        _shader = Shader.FromSource(gl, "world_unit_names", VertexSource, FragmentSource);
        _vao = gl.GenVertexArray();
        _vbo = gl.GenBuffer();
        gl.BindVertexArray(_vao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        const uint stride = 9 * sizeof(float);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride,
            (void*)(3 * sizeof(float)));
        gl.EnableVertexAttribArray(2);
        gl.VertexAttribPointer(2, 4, VertexAttribPointerType.Float, false, stride,
            (void*)(5 * sizeof(float)));
        gl.BindVertexArray(0);
    }

    public unsafe void Render(Camera camera, IReadOnlyList<Label> labels)
    {
        if (_shader is null || labels.Count == 0) return;
        ImFontPtr font = ImGui.GetFont();
        uint atlas = (uint)ImGui.GetIO().Fonts.TexID.ToInt64();
        if (font.NativePtr == null || font.FontSize <= 0f || atlas == 0) return;

        Vector3 forward = Vector3.Normalize(camera.Forward);
        Vector3 right = Vector3.Cross(forward, Vector3.UnitZ);
        if (right.LengthSquared() < 1e-6f) right = Vector3.UnitX;
        else right = Vector3.Normalize(right);
        Vector3 up = Vector3.Normalize(Vector3.Cross(right, forward));

        _vertexFloats = 0;
        foreach (Label label in labels)
        {
            if (label.LinePitch <= 0f || label.Lines.Count == 0) continue;
            float outline = label.LinePitch * 0.055f;
            Vector4 black = new(0f, 0f, 0f, MathF.Max(0.9f, label.Color.W));
            for (int lineIndex = 0; lineIndex < label.Lines.Count; lineIndex++)
            {
                string text = label.Lines[lineIndex];
                if (text.Length == 0) continue;
                Vector3 baseline = label.Anchor +
                    up * ((label.Lines.Count - lineIndex) * label.LinePitch);
                for (int oy = -1; oy <= 1; oy++)
                    for (int ox = -1; ox <= 1; ox++)
                        if (ox != 0 || oy != 0)
                            AddLine(font, text, baseline + right * (ox * outline) +
                                up * (oy * outline), right, up, label.LinePitch, black,
                                camera.Position);
                AddLine(font, text, baseline, right, up, label.LinePitch, label.Color,
                    camera.Position);
            }
        }
        if (_vertexFloats == 0) return;

        int* iv = stackalloc int[1];
        _gl.GetInteger(GLEnum.CurrentProgram, iv); uint savedProgram = (uint)iv[0];
        _gl.GetInteger(GLEnum.VertexArrayBinding, iv); uint savedVao = (uint)iv[0];
        _gl.GetInteger(GLEnum.ArrayBufferBinding, iv); uint savedArrayBuffer = (uint)iv[0];
        _gl.GetInteger(GLEnum.ActiveTexture, iv); TextureUnit savedActiveTexture = (TextureUnit)iv[0];
        _gl.GetInteger(GLEnum.DepthWritemask, iv); bool savedDepthMask = iv[0] != 0;
        _gl.GetInteger(GLEnum.BlendSrcRgb, iv); var savedBlendSrcRgb = (BlendingFactor)iv[0];
        _gl.GetInteger(GLEnum.BlendDstRgb, iv); var savedBlendDstRgb = (BlendingFactor)iv[0];
        _gl.GetInteger(GLEnum.BlendSrcAlpha, iv); var savedBlendSrcAlpha = (BlendingFactor)iv[0];
        _gl.GetInteger(GLEnum.BlendDstAlpha, iv); var savedBlendDstAlpha = (BlendingFactor)iv[0];
        bool savedDepthTest = _gl.IsEnabled(EnableCap.DepthTest);
        bool savedCullFace = _gl.IsEnabled(EnableCap.CullFace);
        bool savedBlend = _gl.IsEnabled(EnableCap.Blend);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.GetInteger(GLEnum.TextureBinding2D, iv); uint savedTexture0 = (uint)iv[0];

        try
        {
            _gl.Enable(EnableCap.DepthTest);
            _gl.DepthMask(false);
            _gl.Enable(EnableCap.Blend);
            _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            _gl.Disable(EnableCap.CullFace);
            _shader.Use();
            _shader.Set("uViewProjection", camera.RelativeViewProjection);
            _shader.Set("uTex", 0);
            _gl.BindTexture(TextureTarget.Texture2D, atlas);
            _gl.BindVertexArray(_vao);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
            fixed (float* ptr = _vertices)
                _gl.BufferData(BufferTargetARB.ArrayBuffer,
                    (nuint)(_vertexFloats * sizeof(float)), ptr, BufferUsageARB.StreamDraw);
            _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)(_vertexFloats / 9));
        }
        finally
        {
            _gl.UseProgram(savedProgram);
            _gl.BindVertexArray(savedVao);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, savedArrayBuffer);
            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, savedTexture0);
            _gl.ActiveTexture(savedActiveTexture);
            _gl.DepthMask(savedDepthMask);
            _gl.BlendFuncSeparate(savedBlendSrcRgb, savedBlendDstRgb,
                savedBlendSrcAlpha, savedBlendDstAlpha);
            if (savedDepthTest) _gl.Enable(EnableCap.DepthTest); else _gl.Disable(EnableCap.DepthTest);
            if (savedCullFace) _gl.Enable(EnableCap.CullFace); else _gl.Disable(EnableCap.CullFace);
            if (savedBlend) _gl.Enable(EnableCap.Blend); else _gl.Disable(EnableCap.Blend);
        }
    }

    private void AddLine(ImFontPtr font, string text, Vector3 baseline, Vector3 right,
        Vector3 up, float pitch, Vector4 color, Vector3 cameraPosition)
    {
        float width = 0f;
        for (int i = 0; i < text.Length; i++) width += font.FindGlyph(text[i]).AdvanceX;
        float worldPerPixel = pitch / font.FontSize;
        float pen = -width * 0.5f;
        for (int i = 0; i < text.Length; i++)
        {
            ImFontGlyphPtr glyph = font.FindGlyph(text[i]);
            float x0 = pen + glyph.X0;
            float x1 = pen + glyph.X1;
            pen += glyph.AdvanceX;
            if (glyph.X1 <= glyph.X0 || glyph.Y1 <= glyph.Y0) continue;
            Vector3 p0 = baseline + right * (x0 * worldPerPixel) - up * (glyph.Y0 * worldPerPixel)
                - cameraPosition;
            Vector3 p1 = baseline + right * (x1 * worldPerPixel) - up * (glyph.Y0 * worldPerPixel)
                - cameraPosition;
            Vector3 p2 = baseline + right * (x1 * worldPerPixel) - up * (glyph.Y1 * worldPerPixel)
                - cameraPosition;
            Vector3 p3 = baseline + right * (x0 * worldPerPixel) - up * (glyph.Y1 * worldPerPixel)
                - cameraPosition;
            AddVertex(p0, glyph.U0, glyph.V0, color);
            AddVertex(p1, glyph.U1, glyph.V0, color);
            AddVertex(p2, glyph.U1, glyph.V1, color);
            AddVertex(p0, glyph.U0, glyph.V0, color);
            AddVertex(p2, glyph.U1, glyph.V1, color);
            AddVertex(p3, glyph.U0, glyph.V1, color);
        }
    }

    private void AddVertex(Vector3 position, float u, float v, Vector4 color)
    {
        Ensure(9);
        _vertices[_vertexFloats++] = position.X;
        _vertices[_vertexFloats++] = position.Y;
        _vertices[_vertexFloats++] = position.Z;
        _vertices[_vertexFloats++] = u;
        _vertices[_vertexFloats++] = v;
        _vertices[_vertexFloats++] = color.X;
        _vertices[_vertexFloats++] = color.Y;
        _vertices[_vertexFloats++] = color.Z;
        _vertices[_vertexFloats++] = color.W;
    }

    private void Ensure(int additional)
    {
        if (_vertexFloats + additional <= _vertices.Length) return;
        Array.Resize(ref _vertices, Math.Max(_vertices.Length * 2, _vertexFloats + additional));
    }

    public void Dispose()
    {
        _shader?.Dispose();
        _shader = null;
        if (_vbo != 0) { _gl.DeleteBuffer(_vbo); _vbo = 0; }
        if (_vao != 0) { _gl.DeleteVertexArray(_vao); _vao = 0; }
    }

    private const string VertexSource = @"#version 330 core
layout(location=0) in vec3 aPos;
layout(location=1) in vec2 aUv;
layout(location=2) in vec4 aColor;
uniform mat4 uViewProjection;
out vec2 vUv;
out vec4 vColor;
void main()
{
    gl_Position = uViewProjection * vec4(aPos, 1.0);
    vUv = aUv;
    vColor = aColor;
}";

    private const string FragmentSource = @"#version 330 core
in vec2 vUv;
in vec4 vColor;
uniform sampler2D uTex;
out vec4 frag;
void main()
{
    float coverage = texture(uTex, vUv).a;
    if (coverage < 0.01) discard;
    frag = vec4(vColor.rgb, vColor.a * coverage);
}";
}

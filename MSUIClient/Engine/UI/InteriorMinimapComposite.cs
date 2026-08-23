using System.Numerics;
using Silk.NET.OpenGL;
using Shader = MSUIClient.Engine.Shader;

namespace MSUIClient.Engine.UI;

/// <summary>
/// Fixed-resolution alpha-tested WMO minimap compositor. Vanilla deliberately
/// minifies the authored room tiles into 256x256 before the on-screen blit;
/// that pass closes filtered shared-wall seams and sub-texel bake gaps.
/// </summary>
public sealed class InteriorMinimapComposite : IDisposable
{
    public readonly record struct Tile(uint Texture, Vector2 P00, Vector2 P10,
        Vector2 P11, Vector2 P01);

    private readonly GL _gl;
    private Shader? _shader;
    private uint _framebuffer;
    private uint _texture;
    private uint _vao;
    private uint _vbo;
    private uint _ebo;

    public uint TextureHandle => _texture;

    public unsafe InteriorMinimapComposite(GL gl)
    {
        _gl = gl;
        _shader = Shader.FromSource(gl, "interior-minimap-composite",
            VertexSource, FragmentSource);

        _texture = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, _texture);
        gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8,
            WmoMinimapProjection.CompositeSize, WmoMinimapProjection.CompositeSize, 0,
            PixelFormat.Rgba, PixelType.UnsignedByte, null);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
            (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter,
            (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS,
            (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT,
            (int)GLEnum.ClampToEdge);

        _framebuffer = gl.GenFramebuffer();
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, _framebuffer);
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer,
            FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _texture, 0);
        GLEnum status = gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        gl.BindTexture(TextureTarget.Texture2D, 0);
        if (status != GLEnum.FramebufferComplete)
            throw new InvalidOperationException($"Interior minimap framebuffer is incomplete: {status}");

        _vao = gl.GenVertexArray();
        _vbo = gl.GenBuffer();
        _ebo = gl.GenBuffer();
        gl.BindVertexArray(_vao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(4 * 4 * sizeof(float)), null,
            BufferUsageARB.DynamicDraw);
        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
        ushort[] indices = [0, 1, 2, 0, 2, 3];
        fixed (ushort* ptr = indices)
            gl.BufferData(BufferTargetARB.ElementArrayBuffer,
                (nuint)(indices.Length * sizeof(ushort)), ptr, BufferUsageARB.StaticDraw);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false,
            4 * sizeof(float), (void*)0);
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false,
            4 * sizeof(float), (void*)(2 * sizeof(float)));
        gl.BindVertexArray(0);
    }

    public unsafe bool Render(IReadOnlyList<Tile> tiles)
    {
        if (_shader is null || _framebuffer == 0 || _texture == 0) return false;

        int* iv = stackalloc int[4];
        float* fv = stackalloc float[4];
        _gl.GetInteger(GLEnum.Viewport, iv);
        int vx = iv[0], vy = iv[1], vw = iv[2], vh = iv[3];
        _gl.GetInteger(GLEnum.DrawFramebufferBinding, iv); uint drawFbo = (uint)iv[0];
        _gl.GetInteger(GLEnum.ReadFramebufferBinding, iv); uint readFbo = (uint)iv[0];
        _gl.GetInteger(GLEnum.CurrentProgram, iv); uint program = (uint)iv[0];
        _gl.GetInteger(GLEnum.VertexArrayBinding, iv); uint vao = (uint)iv[0];
        _gl.GetInteger(GLEnum.ArrayBufferBinding, iv); uint arrayBuffer = (uint)iv[0];
        _gl.GetInteger(GLEnum.ActiveTexture, iv); TextureUnit activeTexture = (TextureUnit)iv[0];
        _gl.GetInteger(GLEnum.TextureBinding2D, iv); uint activeTexture2D = (uint)iv[0];
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.GetInteger(GLEnum.TextureBinding2D, iv); uint texture0 = (uint)iv[0];
        _gl.GetFloat(GLEnum.ColorClearValue, fv);
        Vector4 clear = new(fv[0], fv[1], fv[2], fv[3]);
        _gl.GetInteger(GLEnum.ColorWritemask, iv);
        bool colorR = iv[0] != 0, colorG = iv[1] != 0,
            colorB = iv[2] != 0, colorA = iv[3] != 0;
        bool blend = _gl.IsEnabled(EnableCap.Blend);
        bool depth = _gl.IsEnabled(EnableCap.DepthTest);
        bool cull = _gl.IsEnabled(EnableCap.CullFace);
        bool scissor = _gl.IsEnabled(EnableCap.ScissorTest);
        bool srgb = _gl.IsEnabled(EnableCap.FramebufferSrgb);

        try
        {
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _framebuffer);
            _gl.Viewport(0, 0, WmoMinimapProjection.CompositeSize,
                WmoMinimapProjection.CompositeSize);
            _gl.Disable(EnableCap.Blend);
            _gl.Disable(EnableCap.DepthTest);
            _gl.Disable(EnableCap.CullFace);
            _gl.Disable(EnableCap.ScissorTest);
            _gl.Disable(EnableCap.FramebufferSrgb);
            _gl.ColorMask(true, true, true, true);
            _gl.ClearColor(0f, 0f, 0f, 1f);
            _gl.Clear(ClearBufferMask.ColorBufferBit);

            _shader.Use();
            _shader.Set("uTexture", 0);
            _shader.Set("uAlphaReference", WmoMinimapProjection.InteriorAlphaReference);
            _gl.BindVertexArray(_vao);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
            Span<float> vertices = stackalloc float[16];

            foreach (Tile tile in tiles)
            {
                vertices[0] = tile.P00.X; vertices[1] = tile.P00.Y;
                vertices[2] = 0f; vertices[3] = 0f;
                vertices[4] = tile.P10.X; vertices[5] = tile.P10.Y;
                vertices[6] = 1f; vertices[7] = 0f;
                vertices[8] = tile.P11.X; vertices[9] = tile.P11.Y;
                vertices[10] = 1f; vertices[11] = 1f;
                vertices[12] = tile.P01.X; vertices[13] = tile.P01.Y;
                vertices[14] = 0f; vertices[15] = 1f;
                fixed (float* ptr = vertices)
                    _gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0,
                        (nuint)(vertices.Length * sizeof(float)), ptr);
                _gl.BindTexture(TextureTarget.Texture2D, tile.Texture);
                _gl.DrawElements(PrimitiveType.Triangles, 6,
                    DrawElementsType.UnsignedShort, null);
            }
            return true;
        }
        finally
        {
            _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, readFbo);
            _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, drawFbo);
            _gl.Viewport(vx, vy, (uint)vw, (uint)vh);
            _gl.UseProgram(program);
            _gl.BindVertexArray(vao);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, arrayBuffer);
            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, texture0);
            _gl.ActiveTexture(activeTexture);
            _gl.BindTexture(TextureTarget.Texture2D, activeTexture2D);
            _gl.ClearColor(clear.X, clear.Y, clear.Z, clear.W);
            _gl.ColorMask(colorR, colorG, colorB, colorA);
            Set(EnableCap.Blend, blend);
            Set(EnableCap.DepthTest, depth);
            Set(EnableCap.CullFace, cull);
            Set(EnableCap.ScissorTest, scissor);
            Set(EnableCap.FramebufferSrgb, srgb);
        }
    }

    private void Set(EnableCap cap, bool enabled)
    {
        if (enabled) _gl.Enable(cap); else _gl.Disable(cap);
    }

    public void Dispose()
    {
        _shader?.Dispose();
        _shader = null;
        if (_ebo != 0) { _gl.DeleteBuffer(_ebo); _ebo = 0; }
        if (_vbo != 0) { _gl.DeleteBuffer(_vbo); _vbo = 0; }
        if (_vao != 0) { _gl.DeleteVertexArray(_vao); _vao = 0; }
        if (_framebuffer != 0) { _gl.DeleteFramebuffer(_framebuffer); _framebuffer = 0; }
        if (_texture != 0) { _gl.DeleteTexture(_texture); _texture = 0; }
    }

    private const string VertexSource = @"#version 330 core
layout(location=0) in vec2 aPosition;
layout(location=1) in vec2 aUv;
out vec2 vUv;
void main(){ gl_Position=vec4(aPosition,0.0,1.0); vUv=aUv; }";

    private const string FragmentSource = @"#version 330 core
in vec2 vUv;
uniform sampler2D uTexture;
uniform float uAlphaReference;
out vec4 frag;
void main(){ vec4 t=texture(uTexture,vUv); if(t.a<uAlphaReference) discard; frag=vec4(t.rgb,1.0); }";
}

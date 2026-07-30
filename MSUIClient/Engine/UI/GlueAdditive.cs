using System.Numerics;
using Silk.NET.OpenGL;
using ImGuiNET;
using MSUIClient.Engine;

namespace MSUIClient.Engine.UI;

/// <summary>
/// True additive (alphaMode="ADD") textured-quad overlay for the glue screens - the blend the 1.12
/// char-select row highlight ACTUALLY uses. benilla glue/add_material.rs builds it as an
/// AddUiMaterial whose pipeline blends SrcAlpha/One (dst + src*srcAlpha); its own docs note it
/// REPLACED the alpha-encode approximation (a' = a*max(r,g,b), NORMAL blend) that "could only fake
/// the add over dark backgrounds" and "darkened the art instead of brightening it".
///
/// A DEDICATED GL pass, never an ImGui draw callback (that tore the render loop down). The char-select
/// build ENQUEUES its highlight quads; ClientWindow flushes them - under the ImGui HUD (OnOverlay) for
/// the "under" mode, or over it (OnOverlayTop) for "on top". The on-top mode then draws over the row
/// text, so the lit row's text is ALSO queued here (EnqueueText) and re-drawn as GL glyphs (ImGui font
/// atlas) right after the glow, in the SAME pass - so it stays crisp and in front WITHOUT a second
/// ImGui frame (a second frame breaks ImGui's cross-frame hover/click tracking).
/// </summary>
public sealed class GlueAdditive : IDisposable
{
    private readonly GL _gl;
    private readonly Shader _shader;      // additive glow
    private readonly Shader _textShader;  // alpha-blended atlas text
    private readonly uint _vao;
    private readonly uint _vbo;
    private readonly List<Quad> _queue = new();
    private readonly List<TextCmd> _text = new();
    private float[] _textBuf = new float[4096];
    private ImFontPtr _glueFont;   // captured in-frame (GetFont/TexID are only reliably valid there)
    private uint _glueAtlas;
    private bool _textDiag;

    private readonly record struct Quad(Vector2 Min, Vector2 Max, Vector2 Uv0, Vector2 Uv1, Vector4 Tint, float Gain, float Contrast, bool OnTop, bool Additive, uint Tex);
    private readonly record struct TextCmd(string Text, Vector2 Pos, float Size, Vector4 Color);

    public unsafe GlueAdditive(GL gl)
    {
        _gl = gl;
        _shader = Shader.FromSource(gl, "glue_additive", Vert, Frag);
        _textShader = Shader.FromSource(gl, "glue_text", Vert, TextFrag);

        _vao = gl.GenVertexArray();
        _vbo = gl.GenBuffer();
        gl.BindVertexArray(_vao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(6 * 4 * sizeof(float)), (void*)null, BufferUsageARB.StreamDraw);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), (void*)0);
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), (void*)(2 * sizeof(float)));
        gl.BindVertexArray(0);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);

        Console.WriteLine("[glue-add] additive overlay ready");
    }

    /// <summary>Queue one additive quad over screen-pixel rect [min,max]: whole texture, tinted, with a
    /// brightness <paramref name="gain"/>, a <paramref name="contrast"/> curve (crispness), and an
    /// <paramref name="onTop"/> flag (true = drawn OVER the HUD; false = under it).</summary>
    public void Enqueue(Vector2 min, Vector2 max, Vector4 tint, float gain, float contrast, bool onTop, uint texture)
        => _queue.Add(new Quad(min, max, Vector2.Zero, Vector2.One, tint, gain, contrast, onTop, true, texture));

    /// <summary>Queue one NORMAL-blended (SrcAlpha/OneMinusSrcAlpha) textured quad, tinted, with explicit
    /// UVs so a tiled sheet can be sliced. Every blend quad in a flush is drawn BEFORE every additive one,
    /// so this is how a patch of panel art gets laid down UNDER the glow: the char-select roster panel
    /// cuts its fill away behind the highlight card (so the ImGui pass cannot dim the ADD blend) and
    /// re-draws that exact band here instead. Result: panel -&gt; glow -&gt; text, benilla's order, in one
    /// ImGui frame.</summary>
    public void EnqueueBlend(Vector2 min, Vector2 max, Vector2 uv0, Vector2 uv1, Vector4 tint, bool onTop, uint texture)
        => _queue.Add(new Quad(min, max, uv0, uv1, tint, 1f, 1f, onTop, false, texture));

    /// <summary>Queue one left-aligned text string (screen pixels) to draw over the on-top glow, so it
    /// stays crisp and in front. Only the on-top Flush draws it.</summary>
    public void EnqueueText(string text, float x, float y, float size, Vector4 color)
        => _text.Add(new TextCmd(text, new Vector2(x, y), size, color));

    /// <summary>Capture the glue font + atlas from INSIDE the ImGui frame - GetFont()/Fonts.TexID are
    /// only reliably valid there, and the after-render text pass needs the exact font GlueText used.</summary>
    public void SetGlueFont(ImFontPtr font, uint atlas) { _glueFont = font; _glueAtlas = atlas; }

    /// <summary>True when a quad was queued this frame.</summary>
    public bool HasWork => _queue.Count > 0;

    /// <summary>
    /// Draw + clear every queued quad additively onto the current framebuffer. <paramref
    /// name="displaySize"/> is the ImGui display size the pixel coords live in; the live GL viewport
    /// (set by the world pass) supplies the pixel scale, so a window/framebuffer DPI split is handled.
    /// Every GL state this touches is saved and restored so the following ImGui pass is unaffected.
    /// On the on-top pass, the queued text is drawn (alpha-blended atlas glyphs) after the glow.
    /// </summary>
    public unsafe void Flush(Vector2 displaySize, bool onTop)
    {
        if (_queue.Count == 0 && _text.Count == 0) return;
        if (displaySize.X < 1f || displaySize.Y < 1f) { _queue.Clear(); _text.Clear(); return; }

        bool anyMatch = false;
        foreach (var qq in _queue) if (qq.OnTop == onTop) { anyMatch = true; break; }
        if (onTop && _text.Count > 0) anyMatch = true;
        if (!anyMatch) return;

        int* iv = stackalloc int[1];
        _gl.GetInteger(GLEnum.CurrentProgram, iv);     uint prevProg = (uint)iv[0];
        _gl.GetInteger(GLEnum.VertexArrayBinding, iv); uint prevVao  = (uint)iv[0];
        _gl.GetInteger(GLEnum.ArrayBufferBinding, iv); uint prevVbo  = (uint)iv[0];
        _gl.GetInteger(GLEnum.TextureBinding2D, iv);   uint prevTex  = (uint)iv[0];
        bool bBlend   = _gl.IsEnabled(EnableCap.Blend);
        bool bDepth   = _gl.IsEnabled(EnableCap.DepthTest);
        bool bScissor = _gl.IsEnabled(EnableCap.ScissorTest);
        bool bCull    = _gl.IsEnabled(EnableCap.CullFace);

        _gl.Disable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.ScissorTest);
        _gl.Disable(EnableCap.CullFace);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);   // alphaMode="ADD": dst + src*srcAlpha

        _shader.Use();
        _shader.Set("uViewport", displaySize);
        _shader.Set("uTex", 0);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);

        // Two sub-passes, in this order: NORMAL-blended quads (panel patches) first, then the ADDITIVE
        // ones over them. The whole point is that the glow lands on top of the art it is meant to light.
        for (int pass = 0; pass < 2; pass++)
        {
            bool additivePass = pass == 1;
            _gl.BlendFunc(BlendingFactor.SrcAlpha, additivePass ? BlendingFactor.One : BlendingFactor.OneMinusSrcAlpha);
            foreach (var q in _queue)
            {
                if (q.OnTop != onTop || q.Additive != additivePass) continue;
                _gl.BindTexture(TextureTarget.Texture2D, q.Tex);
                _shader.Set("uTint", q.Tint);
                _shader.Set("uGain", q.Gain);
                _shader.Set("uContrast", q.Contrast);

                float* v = stackalloc float[24];
                // tri 1: min, (max.x,min.y), max   |   tri 2: min, max, (min.x,max.y)
                v[0]  = q.Min.X; v[1]  = q.Min.Y; v[2]  = q.Uv0.X; v[3]  = q.Uv0.Y;
                v[4]  = q.Max.X; v[5]  = q.Min.Y; v[6]  = q.Uv1.X; v[7]  = q.Uv0.Y;
                v[8]  = q.Max.X; v[9]  = q.Max.Y; v[10] = q.Uv1.X; v[11] = q.Uv1.Y;
                v[12] = q.Min.X; v[13] = q.Min.Y; v[14] = q.Uv0.X; v[15] = q.Uv0.Y;
                v[16] = q.Max.X; v[17] = q.Max.Y; v[18] = q.Uv1.X; v[19] = q.Uv1.Y;
                v[20] = q.Min.X; v[21] = q.Max.Y; v[22] = q.Uv0.X; v[23] = q.Uv1.Y;
                _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(24 * sizeof(float)), v, BufferUsageARB.StreamDraw);
                _gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
            }
        }
        _queue.RemoveAll(q => q.OnTop == onTop);

        // The on-top glow just drew over the row text; re-draw the queued text as alpha-blended atlas
        // glyphs so it lands crisp IN FRONT of the glow (same pass, no second ImGui frame).
        if (onTop && _text.Count > 0)
            DrawQueuedText(displaySize);
        if (onTop)
            _text.Clear();

        _gl.BindTexture(TextureTarget.Texture2D, prevTex);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, prevVbo);
        _gl.BindVertexArray(prevVao);
        _gl.UseProgram(prevProg);
        if (!bBlend)  _gl.Disable(EnableCap.Blend);
        if (bDepth)   _gl.Enable(EnableCap.DepthTest);
        if (bScissor) _gl.Enable(EnableCap.ScissorTest);
        if (bCull)    _gl.Enable(EnableCap.CullFace);
    }

    // Draw the queued text with the ImGui font atlas: drop shadow + black outline + coloured main,
    // matching Program.GlueText, but as GL glyph quads (alpha blend) so it can sit over the ADD glow.
    private unsafe void DrawQueuedText(Vector2 displaySize)
    {
        var font = _glueFont;
        uint atlas = _glueAtlas;
        if (atlas == 0 || font.NativePtr == null || font.FontSize <= 0f) return;
        if (!_textDiag)
        {
            _textDiag = true;
            Console.WriteLine($"[glue-add] text pass live: atlas={atlas} fontSize={font.FontSize} cmds={_text.Count}");
        }

        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _textShader.Use();
        _textShader.Set("uViewport", displaySize);
        _textShader.Set("uTex", 0);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, atlas);

        var shadow = new Vector4(0f, 0f, 0f, GlueTune.ShadowAlpha);
        var outline = new Vector4(0f, 0f, 0f, 1f);
        float outlinePx = GlueTune.OutlinePx;

        foreach (var t in _text)
        {
            float scale = t.Size / font.FontSize;
            float so = MathF.Max(1f, MathF.Round(t.Size * GlueTune.ShadowOffsetRatio));
            DrawString(font, scale, t.Text, new Vector2(t.Pos.X + so, t.Pos.Y + so), shadow);
            if (outlinePx > 0.001f)
            {
                float ow = MathF.Max(1f, t.Size * outlinePx);
                for (int oy = -1; oy <= 1; oy++)
                    for (int ox = -1; ox <= 1; ox++)
                        if (ox != 0 || oy != 0)
                            DrawString(font, scale, t.Text, new Vector2(t.Pos.X + ox * ow, t.Pos.Y + oy * ow), outline);
            }
            DrawString(font, scale, t.Text, t.Pos, t.Color);
        }
    }

    private unsafe void DrawString(ImFontPtr font, float scale, string text, Vector2 pos, Vector4 color)
    {
        int fi = 0;
        float penX = pos.X, penY = pos.Y;
        for (int ci = 0; ci < text.Length; ci++)
        {
            var g = font.FindGlyph(text[ci]);
            float x0 = penX + g.X0 * scale, y0 = penY + g.Y0 * scale;
            float x1 = penX + g.X1 * scale, y1 = penY + g.Y1 * scale;
            penX += g.AdvanceX * scale;
            if (x1 <= x0 || y1 <= y0) continue;   // space / empty glyph
            if (fi + 24 > _textBuf.Length) Array.Resize(ref _textBuf, _textBuf.Length * 2);
            float u0 = g.U0, v0 = g.V0, u1 = g.U1, v1 = g.V1;
            _textBuf[fi++] = x0; _textBuf[fi++] = y0; _textBuf[fi++] = u0; _textBuf[fi++] = v0;
            _textBuf[fi++] = x1; _textBuf[fi++] = y0; _textBuf[fi++] = u1; _textBuf[fi++] = v0;
            _textBuf[fi++] = x1; _textBuf[fi++] = y1; _textBuf[fi++] = u1; _textBuf[fi++] = v1;
            _textBuf[fi++] = x0; _textBuf[fi++] = y0; _textBuf[fi++] = u0; _textBuf[fi++] = v0;
            _textBuf[fi++] = x1; _textBuf[fi++] = y1; _textBuf[fi++] = u1; _textBuf[fi++] = v1;
            _textBuf[fi++] = x0; _textBuf[fi++] = y1; _textBuf[fi++] = u0; _textBuf[fi++] = v1;
        }
        if (fi == 0) return;
        _textShader.Set("uColor", color);
        fixed (float* p = _textBuf)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(fi * sizeof(float)), p, BufferUsageARB.StreamDraw);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)(fi / 4));
    }

    public void Dispose()
    {
        _shader.Dispose();
        _textShader.Dispose();
        if (_vbo != 0) _gl.DeleteBuffer(_vbo);
        if (_vao != 0) _gl.DeleteVertexArray(_vao);
    }

    private const string Vert = @"#version 330 core
layout(location = 0) in vec2 aPos;   // screen pixels (ImGui display space, y down)
layout(location = 1) in vec2 aUv;
uniform vec2 uViewport;              // ImGui display size, same pixel space as aPos
out vec2 vUv;
void main()
{
    vUv = aUv;
    vec2 ndc = vec2(aPos.x / uViewport.x * 2.0 - 1.0, 1.0 - aPos.y / uViewport.y * 2.0);
    gl_Position = vec4(ndc, 0.0, 1.0);
}";

    private const string Frag = @"#version 330 core
in vec2 vUv;
uniform sampler2D uTex;
uniform vec4 uTint;
uniform float uGain;                     // brightness on the ADDED light; can exceed 1 (HDR-ish)
uniform float uContrast;                 // >1 drops the mid-tone fill; the bright border stays bright
out vec4 frag;
void main()
{
    vec4 tex = texture(uTex, vUv);
    // The highlight BLP (verified 256x64, opaque, black->yellow) is a bright rounded BORDER around a
    // MEDIUM olive interior (~53%). Drawn additively at high gain the interior saturates alongside the
    // border and the frame stops standing out - the washed-out look. uContrast (a power curve) drops
    // the mid fill far more than the bright border -> crisp frame + translucent interior; uGain then
    // sets overall brightness. ADD blend (SrcAlpha,One) adds frag.rgb * frag.a; alpha is coverage.
    vec3 shaped = pow(max(tex.rgb, vec3(0.0)), vec3(uContrast));
    frag = vec4(shaped * uTint.rgb * uGain, tex.a * uTint.a);
}";

    private const string TextFrag = @"#version 330 core
in vec2 vUv;
uniform sampler2D uTex;   // ImGui font atlas (RGBA32: white, alpha = glyph coverage)
uniform vec4 uColor;
out vec4 frag;
void main()
{
    frag = vec4(uColor.rgb, texture(uTex, vUv).a * uColor.a);
}";
}

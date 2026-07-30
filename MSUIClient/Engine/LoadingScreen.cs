using System.Numerics;
using Silk.NET.OpenGL;

namespace MSUIClient.Engine;

/// <summary>
/// A self-contained full-screen loading curtain: the map backdrop plus the
/// original build-5875 two-texture progress bar, drawn LAST so it covers the partially-streamed
/// world beneath it. No external assets and no shader files - the tiny quad
/// shader is inlined, and the quad is generated from gl_VertexID against an
/// empty VAO (SkyRenderer does the same for its fullscreen triangle).
///
/// This exists because MSUIClient used to build the whole zone synchronously
/// inside the GL Load callback, before the render loop ever presented a frame -
/// so the window was frozen for the entire multi-second build. benilla instead
/// shows a loading screen the instant the world is not resident and streams the
/// rest in behind it. This is our equivalent curtain; the budgeted per-frame
/// build that fills the world behind it lives in Program.Loading.cs.
/// </summary>
public sealed class LoadingScreen : IDisposable
{
    private readonly GL _gl;
    private readonly Shader _shader;
    private readonly Shader _texShader;
    private readonly uint _vao;

    // The map's real WoW loading-screen art (Map.dbc -> LoadingScreens.dbc -> BLP),
    // uploaded and owned by GameLoop and handed in via SetBackground. 0 = none, in
    // which case the plain dark curtain below is drawn (the original behaviour).
    private uint _bgTex;
    private uint _barBorderTex;
    private uint _barFillTex;

    private static readonly Vector3 Background = new(0.035f, 0.045f, 0.065f);

    public LoadingScreen(GL gl)
    {
        _gl = gl;
        _shader = Shader.FromSource(gl, "loading_screen", VertexSource, FragmentSource);
        _texShader = Shader.FromSource(gl, "loading_screen_bg", TexVertexSource, TexFragmentSource);

        // Core profile still needs a bound VAO for any draw, even one that reads
        // nothing but gl_VertexID.
        _vao = _gl.GenVertexArray();
    }

    /// <summary>
    /// Set the full-screen backdrop art (a GL texture handle owned by the caller,
    /// typically decoded from the map's LoadingScreens.dbc BLP). Pass 0 to clear
    /// it and fall back to the plain dark curtain.
    /// </summary>
    public void SetBackground(uint texHandle) => _bgTex = texHandle;

    /// <summary>Set the only two loading-bar layers drawn by the 1.12 client.</summary>
    public void SetBarArt(uint border, uint fill)
    {
        _barBorderTex = border;
        _barFillTex = fill;
    }

    /// <summary>
    /// Draw the curtain. <paramref name="progress"/> is 0..1 for the bar fill;
    /// <paramref name="alpha"/> is the whole curtain's opacity, driven from 1
    /// down to 0 during the fade-out so the finished world is revealed rather
    /// than snapping in (benilla fades every streamed object over 2 s; a curtain
    /// fade is the cheap, self-contained approximation).
    /// </summary>
    public unsafe void Render(float progress, float alpha)
    {
        progress = Math.Clamp(progress, 0f, 1f);
        alpha = Math.Clamp(alpha, 0f, 1f);
        if (alpha <= 0f) return;

        _gl.Disable(EnableCap.DepthTest);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        _gl.BindVertexArray(_vao);

        // Loading art is authored in a 4:3 canvas. Fit that canvas inside the
        // viewport and let the black curtain form the reference pillar/letterbox.
        int* viewport = stackalloc int[4];
        _gl.GetInteger(GLEnum.Viewport, viewport);
        float viewportAspect = viewport[3] > 0 ? viewport[2] / (float)viewport[3] : 4f / 3f;
        const float canvasAspect = 4f / 3f;
        float extentX = viewportAspect >= canvasAspect ? canvasAspect / viewportAspect : 1f;
        float extentY = viewportAspect >= canvasAspect ? 1f : viewportAspect / canvasAspect;

        _shader.Use();
        Rect(-1f, -1f, 1f, 1f, Vector3.Zero, alpha);
        if (_bgTex != 0)
            TextureRect(_bgTex, -extentX, -extentY, extentX, extentY, alpha);
        else
        {
            _shader.Use();
            Rect(-extentX, -extentY, extentX, extentY, Background, alpha);
        }

        // Byte-verified build-5875 fractions, relative to the 4:3 canvas and
        // measured from its bottom edge. Vanilla draws fill first, border last.
        if (_barFillTex != 0 && progress > 0f)
            TextureRect(_barFillTex,
                CanvasX(0.2375f, extentX), CanvasY(0.0625f, extentY),
                CanvasX(0.2375f + 0.525f * progress, extentX), CanvasY(0.0875f, extentY), alpha);
        if (_barBorderTex != 0)
            TextureRect(_barBorderTex,
                CanvasX(0.20f, extentX), CanvasY(0.05f, extentY),
                CanvasX(0.80f, extentX), CanvasY(0.10f, extentY), alpha);

        _gl.BindVertexArray(0);

        // Restore the opaque defaults the next frame's first pass expects.
        _gl.Disable(EnableCap.Blend);
        _gl.Enable(EnableCap.DepthTest);
    }

    private void Rect(float x0, float y0, float x1, float y1, Vector3 rgb, float alpha)
    {
        _shader.Set("uRect", new Vector4(x0, y0, x1, y1));
        _shader.Set("uColor", new Vector4(rgb.X, rgb.Y, rgb.Z, alpha));
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
    }

    private static float CanvasX(float fraction, float extent) => -extent + 2f * extent * fraction;
    private static float CanvasY(float fraction, float extent) => -extent + 2f * extent * fraction;

    private void TextureRect(uint texture, float x0, float y0, float x1, float y1, float alpha)
    {
        _texShader.Use();
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, texture);
        _texShader.Set("uTex", 0);
        _texShader.Set("uAlpha", alpha);
        _texShader.Set("uRect", new Vector4(x0, y0, x1, y1));
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
    }

    public void Dispose()
    {
        if (_vao != 0) _gl.DeleteVertexArray(_vao);
        _shader.Dispose();
        _texShader.Dispose();
    }

    private const string VertexSource = @"#version 330 core
uniform vec4 uRect; // (x0, y0, x1, y1) in NDC
const vec2 kQuad[6] = vec2[6](
    vec2(0.0, 0.0), vec2(1.0, 0.0), vec2(0.0, 1.0),
    vec2(0.0, 1.0), vec2(1.0, 0.0), vec2(1.0, 1.0));
void main()
{
    vec2 c = kQuad[gl_VertexID];
    vec2 ndc = mix(uRect.xy, uRect.zw, c);
    gl_Position = vec4(ndc, 0.0, 1.0);
}";

    private const string FragmentSource = @"#version 330 core
uniform vec4 uColor;
out vec4 frag;
void main() { frag = uColor; }";

    // Textured fullscreen quad for the backdrop art. Same empty-VAO / gl_VertexID
    // trick as above; V is flipped because a BLP stores its top row first while a
    // GL quad with y=-1 at the bottom would otherwise show the art upside down.
private const string TexVertexSource = @"#version 330 core
out vec2 vUv;
uniform vec4 uRect;
const vec2 kQuad[6] = vec2[6](
    vec2(0.0, 0.0), vec2(1.0, 0.0), vec2(0.0, 1.0),
    vec2(0.0, 1.0), vec2(1.0, 0.0), vec2(1.0, 1.0));
void main()
{
    vec2 c = kQuad[gl_VertexID];
    vUv = vec2(c.x, 1.0 - c.y);
    gl_Position = vec4(mix(uRect.xy, uRect.zw, c), 0.0, 1.0);
}";

    private const string TexFragmentSource = @"#version 330 core
in vec2 vUv;
uniform sampler2D uTex;
uniform float uAlpha;
out vec4 frag;
void main()
{
    vec4 sampleColor = texture(uTex, vUv);
    frag = vec4(sampleColor.rgb, sampleColor.a * uAlpha);
}";
}

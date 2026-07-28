using System.Numerics;
using Silk.NET.OpenGL;

namespace MSUIClient.Engine;

/// <summary>
/// A self-contained full-screen loading curtain: a dark background plus a
/// progress bar, drawn LAST in the frame so it covers the partially-streamed
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

    // The client's sky/fog accent (matches DoodadRenderer's FogColor 0.56/0.71/0.85),
    // so the bar reads as part of the same world it is loading.
    private static readonly Vector3 Accent = new(0.55f, 0.70f, 0.85f);
    private static readonly Vector3 Background = new(0.035f, 0.045f, 0.065f);
    private static readonly Vector3 Track = new(0.12f, 0.13f, 0.17f);

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

    /// <summary>
    /// Draw the curtain. <paramref name="progress"/> is 0..1 for the bar fill;
    /// <paramref name="alpha"/> is the whole curtain's opacity, driven from 1
    /// down to 0 during the fade-out so the finished world is revealed rather
    /// than snapping in (benilla fades every streamed object over 2 s; a curtain
    /// fade is the cheap, self-contained approximation).
    /// </summary>
    public void Render(float progress, float alpha)
    {
        progress = Math.Clamp(progress, 0f, 1f);
        alpha = Math.Clamp(alpha, 0f, 1f);
        if (alpha <= 0f) return;

        _gl.Disable(EnableCap.DepthTest);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        _gl.BindVertexArray(_vao);

        // Full-screen background: the map's real WoW loading-screen art when it
        // resolved (stretched to fill the window, as the reference client does),
        // otherwise the plain dark curtain. The curtain alpha drives both so the
        // fade-out reveals the world either way.
        if (_bgTex != 0)
        {
            _texShader.Use();
            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, _bgTex);
            _texShader.Set("uTex", 0);
            _texShader.Set("uAlpha", alpha);
            _gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
        }
        else
        {
            _shader.Use();
            Rect(-1f, -1f, 1f, 1f, Background, alpha);
        }

        // Progress bar, centred, lower third. Always the flat-colour shader.
        _shader.Use();
        const float x0 = -0.40f, x1 = 0.40f, y0 = -0.625f, y1 = -0.585f;
        Rect(x0, y0, x1, y1, Track, alpha);
        float fillX1 = x0 + (x1 - x0) * progress;
        if (fillX1 > x0) Rect(x0, y0, fillX1, y1, Accent, alpha);

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
const vec2 kQuad[6] = vec2[6](
    vec2(0.0, 0.0), vec2(1.0, 0.0), vec2(0.0, 1.0),
    vec2(0.0, 1.0), vec2(1.0, 0.0), vec2(1.0, 1.0));
void main()
{
    vec2 c = kQuad[gl_VertexID];
    vUv = vec2(c.x, 1.0 - c.y);
    gl_Position = vec4(c * 2.0 - 1.0, 0.0, 1.0);
}";

    private const string TexFragmentSource = @"#version 330 core
in vec2 vUv;
uniform sampler2D uTex;
uniform float uAlpha;
out vec4 frag;
void main() { frag = vec4(texture(uTex, vUv).rgb, uAlpha); }";
}

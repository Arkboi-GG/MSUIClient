using System.Numerics;
using Silk.NET.OpenGL;

namespace MSUIClient.Engine;

/// <summary>
/// FFXGlow — the reference client's full-screen glow (benilla's faithful
/// transcription of the shipped ARB programs Shaders\Pixel\FFXGlow.bls /
/// FFXGauss4.bls), as a self-contained OpenGL post pass.
///
/// The pipeline is byte-for-byte benilla (ffx_glow.wgsl / ffx_glow.rs):
///
///     blur = Gauss4(Gauss4(Box4(scene -> 1/4), horizontal), vertical)
///     out  = scene + w * blur^2                                  (FFXGlow.bls)
///
/// one Box4 downsample straight to quarter-res (taps at source-texel offsets
/// {-1.5, +0.5}^2), then a separable Gauss4 at +/-0.5 (weight 0.375) and +/-2.5
/// (weight 0.125) source texels — the shipped weights 1/8 3/8 3/8 1/8. `w` is
/// the per-zone LightParams.glow weight (default 0.5, ~0.647 in Elwynn); it is
/// the ONLY input. The blur^2 square-law is the whole character of the vanilla
/// glow: mid-tones (0.5 -> 0.25) barely bloom, highlights bloom fully — a bloom
/// a linear composite cannot express.
///
/// ADAPTED TO MSUI'S PIPELINE — the ONE deliberate difference from benilla.
/// benilla renders the whole frame in gamma bytes and its FFXGlow combine owns
/// the frame's single gamma->linear decode (its `srgb_to_linear`). MSUI does NOT
/// use that gamma lane: it presents its shaders' output straight to the default
/// framebuffer with no re-encode. Porting benilla's decode literally would shift
/// every exterior colour. So this pass reproduces benilla's glow MECHANISM but
/// composites it ADDITIVELY (glow term only, blend ONE,ONE) onto the frame the
/// world already drew: the base scene passes through untouched — its lighting,
/// fog and colour are exactly what they were — and only `w * blur^2` is added on
/// top, saturating in the 8-bit framebuffer (that saturation IS benilla's
/// min(.,1)). Set <see cref="Gain"/> to 0 to disable the pass entirely.
///
/// It runs after the world + particles and before the loading curtain and the
/// HUD, so — like benilla, where the UI is composited over an already-glowed
/// world and never blooms itself — neither the curtain nor the HUD glows.
/// </summary>
public sealed class FfxGlow : IDisposable
{
    private readonly GL _gl;
    private readonly uint _vao;
    private readonly Shader _downsample;
    private readonly Shader _gauss;
    private readonly Shader _combine;

    // Full-res single-sample resolve of the default framebuffer (also the MSAA
    // resolve when the window is multisampled), then two quarter-res ping-pong
    // targets for the separable blur.
    private uint _resolveFbo, _resolveTex;
    private uint _quarterFboA, _quarterTexA;
    private uint _quarterFboB, _quarterTexB;

    private int _width, _height; // full-res size the targets are allocated for
    private int _qw, _qh;        // quarter-res size

    /// <summary>Whole-pass off switch. When false, <see cref="Apply"/> is a no-op.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The per-zone glow weight `w` (benilla LightParams.glow: default 0.5, about
    /// 0.647 in Elwynn), multiplying blur^2 in the additive combine. 0 disables
    /// the pass — the exterior look is then exactly what it was before the glow.
    /// </summary>
    public float Gain { get; set; } = 0.5f;

    public FfxGlow(GL gl)
    {
        _gl = gl;

        // An empty VAO is still required in a core profile even for a draw that
        // reads nothing but gl_VertexID (same trick as SkyRenderer / LoadingScreen).
        _vao = _gl.GenVertexArray();

        _downsample = Shader.FromSource(gl, "ffx_glow_downsample", FullscreenVert, DownsampleFrag);
        _gauss      = Shader.FromSource(gl, "ffx_glow_gauss", FullscreenVert, GaussFrag);
        _combine    = Shader.FromSource(gl, "ffx_glow_combine", FullscreenVert, CombineFrag);
    }

    /// <summary>
    /// Add the glow to whatever is currently in the default framebuffer. Reads
    /// the live GL viewport for the pixel size, so it is robust to resizes and to
    /// any window/framebuffer DPI mismatch. Leaves the default framebuffer bound
    /// and the opaque render state (depth test on, blend off, cull on) restored.
    /// </summary>
    public unsafe void Apply()
    {
        if (!Enabled || Gain <= 0f) return;

        // Live viewport in pixels (GLEnum + pointer form, matching ClientWindow's
        // GetInteger idiom) - robust to resize and any window/framebuffer DPI split.
        int* vp = stackalloc int[4];
        _gl.GetInteger(GLEnum.Viewport, vp);
        int x = vp[0], y = vp[1], w = vp[2], h = vp[3];
        if (w <= 0 || h <= 0) return;

        EnsureTargets(w, h);

        // Post-process state: no depth, no cull, no blend for the filter passes.
        _gl.Disable(EnableCap.DepthTest);
        _gl.DepthMask(false);
        _gl.Disable(EnableCap.CullFace);
        _gl.Disable(EnableCap.Blend);
        _gl.BindVertexArray(_vao);

        // 1. Resolve the default framebuffer (possibly multisampled) into the
        //    full-res single-sample colour texture.
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
        _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _resolveFbo);
        _gl.BlitFramebuffer(0, 0, _width, _height, 0, 0, _width, _height,
            ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);

        // 2. Box4 downsample full -> quarterA (samples the FULL-res texture; the
        //    offsets are in full-res source texels, matching benilla).
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _quarterFboA);
        _gl.Viewport(0, 0, (uint)_qw, (uint)_qh);
        _downsample.Use();
        Bind(_resolveTex);
        _downsample.Set("uTex", 0);
        _downsample.Set("uTexel", new Vector2(1f / _width, 1f / _height));
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);

        // 3. Gauss4 horizontal quarterA -> quarterB.
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _quarterFboB);
        _gauss.Use();
        Bind(_quarterTexA);
        _gauss.Set("uTex", 0);
        _gauss.Set("uTexel", new Vector2(1f / _qw, 1f / _qh));
        _gauss.Set("uAxis", new Vector2(1f, 0f));
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);

        // 4. Gauss4 vertical quarterB -> quarterA.
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _quarterFboA);
        _gauss.Use();
        Bind(_quarterTexB);
        _gauss.Set("uTex", 0);
        _gauss.Set("uAxis", new Vector2(0f, 1f));
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);

        // 5. Combine: draw ONLY the glow term (gain * blur^2), additively blended
        //    onto the scene the world already left in the default framebuffer.
        //    ONE,ONE add + the 8-bit fixed-point clamp == benilla's scene + w*blur^2
        //    then min(.,1), without benilla's gamma decode (MSUI is not gamma-lane).
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _gl.Viewport(x, y, (uint)w, (uint)h);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.One, BlendingFactor.One);
        _combine.Use();
        Bind(_quarterTexA);
        _combine.Set("uBlur", 0);
        _combine.Set("uGain", Gain);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);

        // Restore the opaque defaults the next frame's first pass expects.
        _gl.Disable(EnableCap.Blend);
        _gl.Enable(EnableCap.CullFace);
        _gl.DepthMask(true);
        _gl.Enable(EnableCap.DepthTest);
        _gl.BindVertexArray(0);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
    }

    private void Bind(uint tex)
    {
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, tex);
    }

    private void EnsureTargets(int w, int h)
    {
        if (w == _width && h == _height && _resolveFbo != 0) return;

        DeleteTargets();

        _width = w;
        _height = h;
        // benilla floors the RT chain at 8 (ffx_compute_rt_dims); do the same so a
        // tiny window can never produce a zero-sized texture.
        _qw = Math.Max(8, w / 4);
        _qh = Math.Max(8, h / 4);

        _resolveTex = NewColorTexture(_width, _height);
        _resolveFbo = NewFbo(_resolveTex);
        _quarterTexA = NewColorTexture(_qw, _qh);
        _quarterFboA = NewFbo(_quarterTexA);
        _quarterTexB = NewColorTexture(_qw, _qh);
        _quarterFboB = NewFbo(_quarterTexB);
    }

    private unsafe uint NewColorTexture(int w, int h)
    {
        uint tex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, tex);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8,
            (uint)w, (uint)h, 0, PixelFormat.Rgba, PixelType.UnsignedByte, (void*)null);
        // Linear so the Box4/Gauss bilinear taps and the quarter->full upscale in
        // the combine are smooth; clamp so edge taps never wrap.
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
        return tex;
    }

    private uint NewFbo(uint tex)
    {
        uint fbo = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer,
            FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, tex, 0);

        var status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != GLEnum.FramebufferComplete)
            Console.WriteLine($"[glow] framebuffer incomplete: {status}");

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        return fbo;
    }

    private void DeleteTargets()
    {
        if (_resolveFbo != 0) { _gl.DeleteFramebuffer(_resolveFbo); _resolveFbo = 0; }
        if (_quarterFboA != 0) { _gl.DeleteFramebuffer(_quarterFboA); _quarterFboA = 0; }
        if (_quarterFboB != 0) { _gl.DeleteFramebuffer(_quarterFboB); _quarterFboB = 0; }
        if (_resolveTex != 0) { _gl.DeleteTexture(_resolveTex); _resolveTex = 0; }
        if (_quarterTexA != 0) { _gl.DeleteTexture(_quarterTexA); _quarterTexA = 0; }
        if (_quarterTexB != 0) { _gl.DeleteTexture(_quarterTexB); _quarterTexB = 0; }
    }

    public void Dispose()
    {
        DeleteTargets();
        if (_vao != 0) _gl.DeleteVertexArray(_vao);
        _downsample.Dispose();
        _gauss.Dispose();
        _combine.Dispose();
    }

    // ---------------------------------------------------------------- shaders

    // A fullscreen triangle generated from gl_VertexID, uv 0..1 over the screen.
    private const string FullscreenVert = @"#version 330 core
out vec2 vUv;
void main()
{
    vec2 uv = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2); // (0,0)(2,0)(0,2)
    vUv = uv;
    gl_Position = vec4(uv * 2.0 - 1.0, 0.0, 1.0);            // (-1,-1)(3,-1)(-1,3)
}";

    // Box4: one downsample full -> quarter, 4 bilinear taps at source-texel
    // offsets {-1.5,+0.5}^2 (benilla ffx_glow.wgsl fs_downsample).
    private const string DownsampleFrag = @"#version 330 core
in vec2 vUv;
uniform sampler2D uTex;
uniform vec2 uTexel; // 1 / full-res dimensions
out vec4 frag;
void main()
{
    frag = (texture(uTex, vUv + vec2(-1.5, -1.5) * uTexel)
          + texture(uTex, vUv + vec2( 0.5, -1.5) * uTexel)
          + texture(uTex, vUv + vec2( 0.5,  0.5) * uTexel)
          + texture(uTex, vUv + vec2(-1.5,  0.5) * uTexel)) * 0.25;
}";

    // Gauss4: taps at +/-0.5 (0.375) and +/-2.5 (0.125) source texels along uAxis,
    // shipped weights 1/8 3/8 3/8 1/8 (benilla ffx_glow.wgsl gauss4).
    private const string GaussFrag = @"#version 330 core
in vec2 vUv;
uniform sampler2D uTex;
uniform vec2 uTexel; // 1 / quarter-res dimensions
uniform vec2 uAxis;  // (1,0) horizontal, (0,1) vertical
out vec4 frag;
void main()
{
    vec2 t = uAxis * uTexel;
    frag = texture(uTex, vUv - 2.5 * t) * 0.125
         + texture(uTex, vUv - 0.5 * t) * 0.375
         + texture(uTex, vUv + 0.5 * t) * 0.375
         + texture(uTex, vUv + 2.5 * t) * 0.125;
}";

    // Combine: emit ONLY the glow term. Additive blend (ONE,ONE) adds it to the
    // scene already in the framebuffer; the 8-bit target saturates the sum. This
    // is benilla's `scene + w*blur^2` clamped, minus its gamma decode (MSUI is not
    // gamma-lane), so the base scene is left exactly as the world drew it.
    private const string CombineFrag = @"#version 330 core
in vec2 vUv;
uniform sampler2D uBlur;
uniform float uGain; // per-zone glow weight w
out vec4 frag;
void main()
{
    vec3 bg = max(texture(uBlur, vUv).rgb, vec3(0.0));
    frag = vec4(uGain * bg * bg, 0.0);
}";
}

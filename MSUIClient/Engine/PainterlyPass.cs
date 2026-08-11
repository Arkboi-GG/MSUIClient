using System.Numerics;
using Silk.NET.OpenGL;

namespace MSUIClient.Engine;

/// <summary>
/// Painterly mode — a whole-scene post pass that restyles the frame as a
/// hand-painted 2D RPG illustration (Baldur's Gate / Diablo 2 backdrop), NOT
/// as an oil painting.
///
/// The distinction is the whole design. An earlier version of this pass ran an
/// anisotropic Kuwahara filter, the standard real-time "oil painting" operator.
/// Kuwahara is a variance-minimizing AVERAGING kernel: its job is to destroy
/// high-frequency detail so flat regions merge into dabs. The illustrated look
/// wants the opposite — every leaf, shingle and cobble reads, and the frame
/// carries MORE apparent detail than the source, not less. No amount of radius
/// tuning gets one operator to the other place, so the operator was replaced.
///
/// What actually produces the look, in one post chain:
///  1. Split colour into low frequency (a small separable Gaussian) and an RGB
///     high-frequency residual.
///  2. Gently pull ONLY the low frequency toward <see cref="Bands"/> value
///     steps. <see cref="BandStrength"/> controls that pull, so flattening does
///     not have to mean full-frame posterization.
///  3. Add the RGB residual back with <see cref="Detail"/> as an absolute gain:
///     0 removes texture, 1 preserves the source, and 2 allows a guarded boost.
///     Strong edges are never over-sharpened, so extra detail cannot grow halos.
///  4. Ink: a Sobel on the BLURRED luma (blurred so texture noise does not ink)
///     darkens real boundaries, which is what separates a dark tree from a
///     dark hill behind it.
///  5. Grade: saturation lift plus a warm-light/cool-shadow split tone, the
///     sun-and-shade separation every painted backdrop has.
///
/// Mechanically a sibling of <see cref="FfxGlow"/>: resolve the default
/// framebuffer (also the MSAA resolve) into a texture, run fullscreen triangles
/// through the chain, write back to the presentation framebuffer. The game loop
/// runs it after FFXGlow and before the loading curtain and HUD. Consequently the
/// bloom belongs to the illustration while the interface stays crisp.
///
/// <see cref="CanvasHeight"/> can deliberately cap the world illustration's
/// working resolution while leaving the later HUD at the window's native
/// resolution. The default/MSAA framebuffer is always resolved at full size
/// first; only that single-sample colour result is cleanly reduced to the canvas.
/// Styling is then nearest-presented with a fullscreen draw, because OpenGL
/// forbids a scaled blit into a multisampled default framebuffer. At zero, the
/// original native resolution path is unchanged.
/// See memory doc project-painterly-mode.
/// </summary>
public sealed class PainterlyPass : IDisposable
{
    private readonly GL _gl;
    private readonly uint _vao;
    private readonly Shader _blur;
    private readonly Shader _style;
    private readonly Shader _present;

    private uint _sceneFbo, _sceneTex;   // single-sample resolve of the frame
    private uint _blurFboA, _blurTexA;   // horizontal pass at the active work size
    private uint _blurFboB, _blurTexB;   // vertical pass -> the low-frequency image
    private uint _depthFbo, _depthTex;   // single-sample resolve of the scene depth
    private int _width, _height;
    private int _blurWidth, _blurHeight;

    // Optional fixed-resolution painted canvas. Colour (including the material
    // importance stored in alpha) is downsampled here only after the mandatory
    // same-size default/MSAA resolve. The styled result is drawn back to the
    // default framebuffer through _present; a scaled blit would be illegal when
    // the window framebuffer is multisampled.
    private uint _canvasSceneFbo, _canvasSceneTex;
    private uint _canvasOutputFbo, _canvasOutputTex;
    private int _canvasWidth, _canvasHeight;

    // Depth resolve state. The default framebuffer's depth format is chosen by
    // the windowing system, and BlitFramebuffer refuses a depth blit whose
    // formats do not match - so the format is discovered by trying, not
    // assumed. -1 = give up (colour-only styling), 0 = untried, then the
    // candidate index that worked. See EnsureDepthTarget.
    private int _depthFormat;
    private bool _depthLive;

    /// <summary>
    /// Off switch for the WORLD pass. When false, <see cref="Apply"/> is a
    /// no-op and its full-resolution scratch targets are released.
    ///
    /// It deliberately does NOT gate <see cref="ApplyToTexture"/> or
    /// <see cref="StyleInto"/>. The HUD carries its own independent switch, and
    /// while this flag gated the off-screen entry points too, turning the world
    /// effect off silently disabled every styled icon and portrait as well -
    /// which made the advertised "HUD only" comparison impossible and cached a
    /// set of unstyled copies that looked like the styling had simply failed.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Target world-illustration canvas height in pixels. Zero keeps the native
    /// resolution path. When the viewport is taller than this value, colour and
    /// material importance are filtered to an aspect-correct canvas before
    /// blur/style, then nearest-presented to the window. When an integer window
    /// scale lands reasonably near this target it is preferred, giving every
    /// painted pixel an even, stable footprint. Depth remains full-size and is
    /// sampled in normalized screen coordinates. Off-screen portraits and UI art
    /// are intentionally unaffected.
    /// </summary>
    public int CanvasHeight { get; set; } = 1440;

    /// <summary>
    /// Painted value steps the low-frequency luminance is quantized into
    /// (3..24). Fewer bands reads as a bolder, flatter illustration; more bands
    /// approaches a continuous render. High-frequency detail is added back
    /// afterwards either way, so this never costs texture.
    /// </summary>
    public float Bands { get; set; } = 18f;

    /// <summary>
    /// Blend toward the quantized low-frequency value structure, 0..1. Zero
    /// leaves the continuous source values intact; one is the old fully
    /// posterized treatment. Restrained values flatten lighting without turning
    /// cobbles, plaster and foliage into broad paint pools.
    /// </summary>
    public float BandStrength { get; set; } = 0.30f;

    /// <summary>
    /// Absolute RGB residual gain, 0..2. 0 removes source texture from the
    /// painted value masses, 1 preserves source-strength texture, and 2 permits
    /// a guarded boost. Boost is suppressed at strong edges to prevent halos;
    /// Material hierarchy and distance calm intentionally do not attenuate this
    /// base residual: the retro target keeps authored texture crisp throughout
    /// the frame. They still quiet generated dither, grain and ink.
    /// </summary>
    public float Detail { get; set; } = 1f;

    /// <summary>
    /// Ink-line strength, 0..1 — how dark boundaries are drawn. This is the
    /// silhouette separation, not an outline shader: it follows luminance
    /// discontinuity in the blurred image, so it lands on object and material
    /// boundaries rather than on every blade of grass.
    /// </summary>
    public float Ink { get; set; } = 0.10f;

    /// <summary>
    /// Gradient magnitude an edge must reach before any ink is drawn (0..0.5).
    /// The noise gate: raise it if textured ground starts inking, lower it if
    /// silhouettes are not separating from their background.
    /// </summary>
    public float InkThreshold { get; set; } = 0.19f;

    /// <summary>Colour richness, 0..2 (1 = untouched source saturation).</summary>
    public float Saturation { get; set; } = 1.07f;

    /// <summary>
    /// Midtone lift, 0.5..2 (1 = untouched). A gamma curve, so pure black and
    /// pure white are fixed points and nothing can clip - it opens up the
    /// middle of the range without blowing the highlights the way a gain would.
    /// Exists because the styling deepens shadows and inks boundaries, both of
    /// which take light out of an already dim scene.
    /// </summary>
    public float Lift { get; set; } = 1.01f;

    /// <summary>
    /// Value structure, 0..1 — an S-curve on the low frequency, applied BEFORE
    /// quantization so the bands land on punched-up values. A painted backdrop
    /// reads because its lights and darks are separated into distinct shapes;
    /// a flat-lit game frame is one mid-tone soup until this pulls it apart.
    /// Non-clipping (a smoothstep, not a gain), so highlights survive.
    /// </summary>
    public float Contrast { get; set; } = 0.18f;

    /// <summary>
    /// Warm-light / cool-shadow split tone, 0..1. Painted backdrops separate
    /// sunlit and shaded surfaces by HUE, not just by value; this is that.
    /// </summary>
    public float Warmth { get; set; } = 0.08f;

    /// <summary>
    /// Silhouette ink strength, 0..1 — boundaries drawn where the DEPTH breaks,
    /// not where the colour does. This is the readability knob: colour ink
    /// cannot separate a dark figure from a dark hedge behind it because there
    /// is no colour edge to find, and that is exactly the case where a painted
    /// backdrop draws its firmest line. Silently inert if the depth resolve is
    /// unavailable on this driver (see <see cref="DepthAvailable"/>).
    /// </summary>
    public float Silhouette { get; set; } = 0.22f;

    /// <summary>
    /// How much the styling calms between <see cref="CalmStart"/> and
    /// <see cref="CalmEnd"/>, 0..1. Generated dither, both ink sources and grain
    /// scale down, so the background settles without blurring authored texture.
    /// At 0 the whole frame is treated identically.
    /// </summary>
    public float DepthFade { get; set; } = 0.35f;

    /// <summary>
    /// Eye-space distance in world units where distance calming begins. The
    /// near field receives the full treatment up to this point.
    /// </summary>
    public float CalmStart { get; set; } = 60f;

    /// <summary>
    /// Eye-space distance in world units where distance calming reaches the
    /// strength selected by <see cref="DepthFade"/>.
    /// </summary>
    public float CalmEnd { get; set; } = 240f;

    /// <summary>
    /// False when the driver refused the depth resolve, which turns
    /// <see cref="Silhouette"/>, <see cref="DepthFade"/>,
    /// <see cref="CalmStart"/> and <see cref="CalmEnd"/> into no-ops. The pass
    /// still runs; it just styles on colour alone.
    /// </summary>
    public bool DepthAvailable => _depthLive;

    /// <summary>
    /// Canvas grain, 0..1 — paper tooth over the finished image. Kept low by
    /// default: at 4K this is per-pixel noise and reads as sensor grain, not
    /// canvas, well before it reaches 0.5.
    /// </summary>
    public float Grain { get; set; } = 0f;

    /// <summary>
    /// Ordered band-dither strength, 0..1, independent of <see cref="Grain"/>.
    /// One is at most half a quantization interval; textured and distant areas
    /// attenuate it automatically so it only breaks contours in quiet regions.
    /// </summary>
    public float Dither { get; set; } = 0.04f;

    public PainterlyPass(GL gl)
    {
        _gl = gl;
        // Empty VAO for the gl_VertexID fullscreen triangle, same trick as FfxGlow.
        _vao = _gl.GenVertexArray();
        _blur = Shader.FromSource(gl, "painterly_blur", FullscreenVert, BlurFrag);
        _style = Shader.FromSource(gl, "painterly_style", FullscreenVert, StyleFrag);
        _present = Shader.FromSource(gl, "painterly_present", FullscreenVert, PresentFrag);
    }

    /// <summary>
    /// Restyle whatever is currently in the default framebuffer. Reads the live
    /// GL viewport for the pixel size (robust to resizes and DPI mismatch), then
    /// restores the caller's framebuffer, viewport, bindings and render state.
    /// </summary>
    public unsafe void Apply(float nearPlane, float farPlane)
    {
        if (!Enabled)
        {
            // Give the scratch back rather than holding it for a session. These
            // are full-viewport RGBA8 plus a depth copy - tens of megabytes at
            // 4K - and the HUD switch can legitimately keep painterly art alive
            // for hours with the world pass off. The OFF-SCREEN targets are not
            // touched here: they belong to the HUD path, which is gated
            // separately and would otherwise thrash its allocation every frame.
            if (_sceneFbo != 0) DeleteTargets();
            return;
        }

        int* vp = stackalloc int[4];
        _gl.GetInteger(GLEnum.Viewport, vp);
        int x = vp[0], y = vp[1], w = vp[2], h = vp[3];
        if (w <= 0 || h <= 0) return;

        int* iv = stackalloc int[1];
        _gl.GetInteger(GLEnum.DrawFramebufferBinding, iv); uint savedDrawFbo = (uint)iv[0];
        _gl.GetInteger(GLEnum.ReadFramebufferBinding, iv); uint savedReadFbo = (uint)iv[0];
        _gl.GetInteger(GLEnum.CurrentProgram, iv); uint savedProgram = (uint)iv[0];
        _gl.GetInteger(GLEnum.VertexArrayBinding, iv); uint savedVao = (uint)iv[0];
        _gl.GetInteger(GLEnum.ActiveTexture, iv); TextureUnit savedActiveTexture = (TextureUnit)iv[0];
        _gl.GetInteger(GLEnum.DepthWritemask, iv); bool savedDepthMask = iv[0] != 0;
        bool savedDepthTest = _gl.IsEnabled(EnableCap.DepthTest);
        bool savedCullFace = _gl.IsEnabled(EnableCap.CullFace);
        bool savedBlend = _gl.IsEnabled(EnableCap.Blend);
        bool savedScissorTest = _gl.IsEnabled(EnableCap.ScissorTest);
        bool savedFramebufferSrgb = _gl.IsEnabled(EnableCap.FramebufferSrgb);

        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.GetInteger(GLEnum.TextureBinding2D, iv); uint savedTexture0 = (uint)iv[0];
        _gl.ActiveTexture(TextureUnit.Texture1);
        _gl.GetInteger(GLEnum.TextureBinding2D, iv); uint savedTexture1 = (uint)iv[0];
        _gl.ActiveTexture(TextureUnit.Texture2);
        _gl.GetInteger(GLEnum.TextureBinding2D, iv); uint savedTexture2 = (uint)iv[0];
        _gl.ActiveTexture(TextureUnit.Texture0);

        try
        {
            EnsureTargets(w, h);

            int requestedCanvasHeight = Math.Max(CanvasHeight, 0);
            bool useCanvas = requestedCanvasHeight > 0 && h > requestedCanvasHeight;
            int workWidth = w;
            int workHeight = h;
            if (useCanvas)
            {
                // Prefer an integer enlargement when its resulting canvas is
                // reasonably close to the requested height. Common 3840x2160
                // and 3840x2400 viewports therefore become exact 2x canvases
                // (1920x1080 and 1920x1200) instead of uneven 1.5x/1.67x grids.
                // For a window only slightly above the target, retain the exact
                // requested height rather than dropping all the way to half-res.
                float desiredScale = h / (float)requestedCanvasHeight;
                int nearestIntegerScale = Math.Max(2, (int)MathF.Floor(desiredScale + 0.5f));
                float integerCanvasHeight = h / (float)nearestIntegerScale;
                bool integerCanvasIsNearTarget =
                    MathF.Abs(integerCanvasHeight - requestedCanvasHeight)
                    <= requestedCanvasHeight * 0.25f;

                if (integerCanvasIsNearTarget)
                {
                    workWidth = Math.Max(1, (int)MathF.Round(w / (float)nearestIntegerScale));
                    workHeight = Math.Max(1, (int)MathF.Round(h / (float)nearestIntegerScale));
                }
                else
                {
                    workHeight = requestedCanvasHeight;
                    workWidth = Math.Max(1, (int)MathF.Round(w * (workHeight / (float)h)));
                }
            }
            if (useCanvas) EnsureCanvasTargets(workWidth, workHeight);
            else if (_canvasSceneFbo != 0) DeleteCanvasTargets();
            EnsureBlurTargets(workWidth, workHeight);

            _gl.Disable(EnableCap.DepthTest);
            _gl.DepthMask(false);
            _gl.Disable(EnableCap.CullFace);
            _gl.Disable(EnableCap.Blend);
            _gl.Disable(EnableCap.ScissorTest);
            _gl.Disable(EnableCap.FramebufferSrgb);
            _gl.BindVertexArray(_vao);

            // Resolve the default framebuffer into a single-sample texture. The
            // default framebuffer can be MULTISAMPLED — and the driver may hand back
            // more samples than requested (this machine reports 2x for a 1x request)
            // — so this blit MUST stay same-size. A SCALING blit from a multisampled
            // read buffer is a silent GL_INVALID_OPERATION that leaves the target
            // uninitialized, which renders the whole world white. Canvas scaling,
            // when requested, therefore happens only in a second blit between two
            // single-sample targets below.
            _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
            _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _sceneFbo);
            _gl.BlitFramebuffer(x, y, x + w, y + h, 0, 0, _width, _height,
                ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);

            // Scene depth, same-size and NEAREST (the only filter a depth blit
            // accepts). Wrapped in the format probe because the default
            // framebuffer's depth format belongs to the windowing system.
            ResolveDepth(x, y, w, h);

            uint workSceneTex = _sceneTex;
            if (useCanvas)
            {
                // The source is single-sample after the mandatory same-size resolve,
                // so this scale is legal. Linear reduction keeps sub-pixel texture
                // stable in motion; the final NEAREST enlargement below supplies
                // the crisp stepped edge character without turning detail into
                // a shimmering point-sample.
                // Copy RGBA: alpha carries the renderers' material/category
                // importance into the style pass.
                _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _sceneFbo);
                _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _canvasSceneFbo);
                _gl.BlitFramebuffer(0, 0, _width, _height,
                    0, 0, workWidth, workHeight,
                    ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Linear);
                workSceneTex = _canvasSceneTex;
            }

            var texel = new Vector2(1f / workWidth, 1f / workHeight);

            // Every spatial constant below is authored against a 1080p frame and
            // scaled to the live resolution. Without this the whole style is
            // resolution-dependent in the worst way: a boundary spread over 3
            // texels at 1080p is spread over 7 at 4K, so its per-texel gradient
            // halves, the ink threshold stops being met and the frequency split
            // lands on a completely different feature size. Scaling the TAP
            // SPACING (not just the strengths) keeps the look identical whether
            // the window is 1280x720 or 3840x2400.
            float pixelScale = MathF.Max(1f, workHeight / 1080f);
            _gl.Viewport(0, 0, (uint)workWidth, (uint)workHeight);

            // Low-frequency image: separable Gaussian, sigma ~1.6 reference pixels.
            // Feeds BOTH the band quantization (which must not see texture) and the
            // ink Sobel (which must not see noise).
            _blur.Use();
            _blur.Set("uTex", 0);
            _blur.Set("uTexel", texel);
            _blur.Set("uScale", pixelScale);

            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _blurFboA);
            Bind(workSceneTex);
            _blur.Set("uAxis", new Vector2(1f, 0f));
            _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);

            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _blurFboB);
            Bind(_blurTexA);
            _blur.Set("uAxis", new Vector2(0f, 1f));
            _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);

            // Native styling writes straight to the presentation framebuffer. A
            // fixed canvas writes to its single-sample output texture first.
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer,
                useCanvas ? _canvasOutputFbo : 0);
            if (!useCanvas) _gl.Viewport(x, y, (uint)w, (uint)h);
            _style.Use();
            Bind(workSceneTex, TextureUnit.Texture0);
            Bind(_blurTexB, TextureUnit.Texture1);
            _style.Set("uScene", 0);
            _style.Set("uLow", 1);
            _style.Set("uTexel", texel);
            _style.Set("uScale", pixelScale);
            _style.Set("uBands", MathF.Round(Math.Clamp(Bands, 3f, 24f)));
            _style.Set("uBandStrength", Math.Clamp(BandStrength, 0f, 1f));
            _style.Set("uDetail", Math.Clamp(Detail, 0f, 2f));
            _style.Set("uInk", Math.Clamp(Ink, 0f, 1f));
            _style.Set("uInkThreshold", Math.Clamp(InkThreshold, 0.01f, 0.5f));
            _style.Set("uSaturation", Math.Clamp(Saturation, 0f, 2f));
            _style.Set("uContrast", Math.Clamp(Contrast, 0f, 1f));
            _style.Set("uLift", Math.Clamp(Lift, 0.5f, 2f));
            _style.Set("uHasDepth", _depthLive ? 1f : 0f);
            _style.Set("uSilhouette", Math.Clamp(Silhouette, 0f, 1f));
            _style.Set("uDepthFade", Math.Clamp(DepthFade, 0f, 1f));
            float calmStart = MathF.Max(CalmStart, 0f);
            _style.Set("uCalmStart", calmStart);
            _style.Set("uCalmEnd", MathF.Max(CalmEnd, calmStart + 1f));
            _style.Set("uNear", MathF.Max(nearPlane, 0.01f));
            _style.Set("uFar", MathF.Max(farPlane, nearPlane + 1f));
            if (_depthLive)
            {
                Bind(_depthTex, TextureUnit.Texture2);
                _style.Set("uDepth", 2);
            }
            _style.Set("uWarmth", Math.Clamp(Warmth, 0f, 1f));
            _style.Set("uGrain", Math.Clamp(Grain, 0f, 1f));
            _style.Set("uDither", Math.Clamp(Dither, 0f, 1f));
            _style.Set("uUseStyleWeights", 1f);
            _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);

            if (useCanvas)
            {
                // Do not use a scaled blit here: the default draw framebuffer may
                // be multisampled. A fullscreen draw performs the nearest upscale
                // legally and broadcasts each fragment to its destination samples.
                _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
                _gl.Viewport(x, y, (uint)w, (uint)h);
                _present.Use();
                Bind(_canvasOutputTex, TextureUnit.Texture0);
                _present.Set("uTex", 0);
                _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
            }

        }
        finally
        {
            _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, savedReadFbo);
            _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, savedDrawFbo);
            _gl.Viewport(x, y, (uint)w, (uint)h);
            _gl.UseProgram(savedProgram);
            _gl.BindVertexArray(savedVao);
            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, savedTexture0);
            _gl.ActiveTexture(TextureUnit.Texture1);
            _gl.BindTexture(TextureTarget.Texture2D, savedTexture1);
            _gl.ActiveTexture(TextureUnit.Texture2);
            _gl.BindTexture(TextureTarget.Texture2D, savedTexture2);
            _gl.ActiveTexture(savedActiveTexture);
            _gl.DepthMask(savedDepthMask);
            if (savedDepthTest) _gl.Enable(EnableCap.DepthTest); else _gl.Disable(EnableCap.DepthTest);
            if (savedCullFace) _gl.Enable(EnableCap.CullFace); else _gl.Disable(EnableCap.CullFace);
            if (savedBlend) _gl.Enable(EnableCap.Blend); else _gl.Disable(EnableCap.Blend);
            if (savedScissorTest) _gl.Enable(EnableCap.ScissorTest); else _gl.Disable(EnableCap.ScissorTest);
            if (savedFramebufferSrgb) _gl.Enable(EnableCap.FramebufferSrgb);
            else _gl.Disable(EnableCap.FramebufferSrgb);
        }
    }

    // Depth formats to try, best first. DEPTH24_STENCIL8 is what a normal
    // windowed GL context almost always hands out; the plain depth formats
    // cover contexts created without a stencil buffer.
    private static readonly (InternalFormat Format, PixelFormat Pixels, PixelType Type, ClearBufferMask Mask)[]
        DepthCandidates =
        [
            (InternalFormat.Depth24Stencil8, PixelFormat.DepthStencil, PixelType.UnsignedInt248,
                ClearBufferMask.DepthBufferBit | ClearBufferMask.StencilBufferBit),
            (InternalFormat.DepthComponent24, PixelFormat.DepthComponent, PixelType.UnsignedInt,
                ClearBufferMask.DepthBufferBit),
            (InternalFormat.DepthComponent32f, PixelFormat.DepthComponent, PixelType.Float,
                ClearBufferMask.DepthBufferBit),
        ];

    /// <summary>
    /// Copy the default framebuffer's depth into a sampleable texture, working
    /// out the format by trial.
    ///
    /// A depth blit is only legal when the read and draw depth formats match,
    /// and there is no portable way to ask the windowing system which format it
    /// gave the default framebuffer - so each candidate is tried once and the
    /// GL error decides. A silent failure here would be invisible (the shader
    /// would just sample garbage depth and ink the whole screen), which is why
    /// the outcome is logged and latched either way.
    /// </summary>
    private void ResolveDepth(int x, int y, int w, int h)
    {
        if (_depthFormat < 0) return;                       // already given up

        while (_gl.GetError() != GLEnum.NoError) { }        // start from a clean slate

        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
        _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _depthFbo);
        _gl.BlitFramebuffer(x, y, x + w, y + h, 0, 0, _width, _height,
            DepthCandidates[_depthFormat].Mask, BlitFramebufferFilter.Nearest);

        if (_gl.GetError() == GLEnum.NoError)
        {
            if (!_depthLive)
            {
                _depthLive = true;
                Console.WriteLine($"[painterly] depth resolve via {DepthCandidates[_depthFormat].Format} " +
                                  "- silhouette ink and distance fade active");
            }
            return;
        }

        // That format is not what the default framebuffer holds. Try the next.
        _depthLive = false;
        _depthFormat++;
        if (_depthFormat >= DepthCandidates.Length)
        {
            _depthFormat = -1;
            Console.WriteLine("[painterly] no depth format matched the default framebuffer - " +
                              "silhouette ink and distance fade disabled, styling on colour alone");
            return;
        }
        DeleteDepthTarget();
        EnsureDepthTarget();
    }

    /// <summary>
    /// Style an OFF-SCREEN texture in place — for anything rendered to its own
    /// framebuffer rather than to the screen, which the whole-frame pass can
    /// never reach.
    ///
    /// The unit portrait is the case that matters: it is baked into its own
    /// render target and then drawn as a UI quad, so with painterly on, the
    /// world was painted and the face in the corner of the screen was not. Same
    /// operator, same knobs, no depth (an off-screen bake has no scene depth to
    /// speak of), and source alpha is preserved so a portrait keeps its cut-out
    /// background.
    ///
    /// Gated by the CALLER, not by <see cref="Enabled"/>: the HUD owns the
    /// decision to paint its own art.
    /// </summary>
    public unsafe void ApplyToTexture(uint targetFbo, uint sourceTex, int w, int h) =>
        StyleOffscreen(targetFbo, sourceTex, w, h, inPlace: true);

    /// <summary>
    /// Style <paramref name="sourceTex"/> INTO a different target, leaving the
    /// source untouched. This is the variant UI art needs: an icon BLP is a
    /// shared cached texture that other things still draw unstyled, so it must
    /// not be restyled in place - the styled copy is a separate texture.
    /// </summary>
    public unsafe void StyleInto(uint destFbo, uint sourceTex, int w, int h) =>
        StyleOffscreen(destFbo, sourceTex, w, h, inPlace: false);

    private unsafe void StyleOffscreen(uint targetFbo, uint sourceTex, int w, int h, bool inPlace)
    {
        if (w <= 0 || h <= 0) return;

        // This path can run while a portrait bake or ImGui owns GL. Capture all
        // state the pass mutates BEFORE target allocation (allocation binds FBOs
        // and textures too), then restore it even if a shader or allocation fails.
        int* savedViewport = stackalloc int[4];
        int* iv = stackalloc int[1];
        _gl.GetInteger(GLEnum.Viewport, savedViewport);
        _gl.GetInteger(GLEnum.DrawFramebufferBinding, iv); uint savedDrawFbo = (uint)iv[0];
        _gl.GetInteger(GLEnum.ReadFramebufferBinding, iv); uint savedReadFbo = (uint)iv[0];
        _gl.GetInteger(GLEnum.CurrentProgram, iv); uint savedProgram = (uint)iv[0];
        _gl.GetInteger(GLEnum.VertexArrayBinding, iv); uint savedVao = (uint)iv[0];
        _gl.GetInteger(GLEnum.ActiveTexture, iv); TextureUnit savedActiveTexture = (TextureUnit)iv[0];
        _gl.GetInteger(GLEnum.DepthWritemask, iv); bool savedDepthMask = iv[0] != 0;
        bool savedDepthTest = _gl.IsEnabled(EnableCap.DepthTest);
        bool savedCullFace = _gl.IsEnabled(EnableCap.CullFace);
        bool savedBlend = _gl.IsEnabled(EnableCap.Blend);
        bool savedScissorTest = _gl.IsEnabled(EnableCap.ScissorTest);
        bool savedFramebufferSrgb = _gl.IsEnabled(EnableCap.FramebufferSrgb);

        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.GetInteger(GLEnum.TextureBinding2D, iv); uint savedTexture0 = (uint)iv[0];
        _gl.ActiveTexture(TextureUnit.Texture1);
        _gl.GetInteger(GLEnum.TextureBinding2D, iv); uint savedTexture1 = (uint)iv[0];
        _gl.ActiveTexture(TextureUnit.Texture0);

        try
        {
            EnsureOffscreenTargets(w, h);
            _gl.Disable(EnableCap.DepthTest);
            _gl.DepthMask(false);
            _gl.Disable(EnableCap.CullFace);
            _gl.Disable(EnableCap.Blend);
            _gl.Disable(EnableCap.ScissorTest);
            _gl.Disable(EnableCap.FramebufferSrgb);
            _gl.BindVertexArray(_vao);
            _gl.Viewport(0, 0, (uint)w, (uint)h);

            var texel = new Vector2(1f / w, 1f / h);
            // Portraits are small; scaling the 1080p-authored constants down by the
            // same rule as the screen pass would make the strokes vanish. Treat the
            // bake as if it were a 1080p-tall image so the style reads at its size.
            const float offscreenScale = 1f;

            // In place, the target framebuffer's colour attachment IS sourceTex, and
            // sampling a texture while rendering into it is undefined - so the style
            // pass has to read from somewhere else, and one same-size single-sample
            // blit is the cheapest way to get that somewhere. When the destination
            // is a different texture there is no aliasing and the copy is skipped.
            uint readTex = sourceTex;
            if (inPlace)
            {
                _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, targetFbo);
                _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _offCopyFbo);
                _gl.BlitFramebuffer(0, 0, w, h, 0, 0, w, h,
                    ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
                readTex = _offCopyTex;
            }

            _blur.Use();
            _blur.Set("uTex", 0);
            _blur.Set("uTexel", texel);
            _blur.Set("uScale", offscreenScale);

            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _offBlurFboA);
            Bind(readTex);
            _blur.Set("uAxis", new Vector2(1f, 0f));
            _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);

            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _offBlurFboB);
            Bind(_offBlurTexA);
            _blur.Set("uAxis", new Vector2(0f, 1f));
            _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);

            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, targetFbo);
            _style.Use();
            Bind(readTex, TextureUnit.Texture0);
            Bind(_offBlurTexB, TextureUnit.Texture1);
            _style.Set("uScene", 0);
            _style.Set("uLow", 1);
            _style.Set("uTexel", texel);
            _style.Set("uScale", offscreenScale);
            _style.Set("uBands", MathF.Round(Math.Clamp(Bands, 3f, 24f)));
            _style.Set("uBandStrength", Math.Clamp(BandStrength, 0f, 1f));
            _style.Set("uDetail", Math.Clamp(Detail, 0f, 2f));
            _style.Set("uInk", Math.Clamp(Ink, 0f, 1f));
            _style.Set("uInkThreshold", Math.Clamp(InkThreshold, 0.01f, 0.5f));
            _style.Set("uSaturation", Math.Clamp(Saturation, 0f, 2f));
            _style.Set("uContrast", Math.Clamp(Contrast, 0f, 1f));
            _style.Set("uLift", Math.Clamp(Lift, 0.5f, 2f));
            _style.Set("uWarmth", Math.Clamp(Warmth, 0f, 1f));
            _style.Set("uGrain", Math.Clamp(Grain, 0f, 1f));
            _style.Set("uDither", Math.Clamp(Dither, 0f, 1f));
            // Off-screen alpha is cut-out/coverage, not the world renderer's
            // material-importance channel. Never reinterpret it as a style weight.
            _style.Set("uUseStyleWeights", 0f);
            _style.Set("uHasDepth", 0f);
            _style.Set("uSilhouette", 0f);
            _style.Set("uDepthFade", 0f);
            _style.Set("uCalmStart", MathF.Max(CalmStart, 0f));
            _style.Set("uCalmEnd", MathF.Max(CalmEnd, MathF.Max(CalmStart, 0f) + 1f));
            _style.Set("uNear", 0.1f);
            _style.Set("uFar", 100f);
            _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        }
        finally
        {
            _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, savedReadFbo);
            _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, savedDrawFbo);
            _gl.Viewport(savedViewport[0], savedViewport[1],
                (uint)savedViewport[2], (uint)savedViewport[3]);
            _gl.UseProgram(savedProgram);
            _gl.BindVertexArray(savedVao);
            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, savedTexture0);
            _gl.ActiveTexture(TextureUnit.Texture1);
            _gl.BindTexture(TextureTarget.Texture2D, savedTexture1);
            _gl.ActiveTexture(savedActiveTexture);
            _gl.DepthMask(savedDepthMask);
            if (savedDepthTest) _gl.Enable(EnableCap.DepthTest); else _gl.Disable(EnableCap.DepthTest);
            if (savedCullFace) _gl.Enable(EnableCap.CullFace); else _gl.Disable(EnableCap.CullFace);
            if (savedBlend) _gl.Enable(EnableCap.Blend); else _gl.Disable(EnableCap.Blend);
            if (savedScissorTest) _gl.Enable(EnableCap.ScissorTest);
            else _gl.Disable(EnableCap.ScissorTest);
            if (savedFramebufferSrgb) _gl.Enable(EnableCap.FramebufferSrgb);
            else _gl.Disable(EnableCap.FramebufferSrgb);
        }
    }

    private uint _offCopyFbo, _offCopyTex;
    private uint _offBlurFboA, _offBlurTexA, _offBlurFboB, _offBlurTexB;
    private int _offWidth, _offHeight;

    private void EnsureOffscreenTargets(int w, int h)
    {
        if (w == _offWidth && h == _offHeight && _offBlurFboA != 0) return;
        DeleteOffscreenTargets();
        _offWidth = w;
        _offHeight = h;
        _offCopyTex = NewTexture(w, h);
        _offCopyFbo = NewFbo(_offCopyTex, "off-copy");
        _offBlurTexA = NewTexture(w, h);
        _offBlurFboA = NewFbo(_offBlurTexA, "off-blurA");
        _offBlurTexB = NewTexture(w, h);
        _offBlurFboB = NewFbo(_offBlurTexB, "off-blurB");
    }

    private void DeleteOffscreenTargets()
    {
        if (_offCopyFbo != 0) { _gl.DeleteFramebuffer(_offCopyFbo); _offCopyFbo = 0; }
        if (_offBlurFboA != 0) { _gl.DeleteFramebuffer(_offBlurFboA); _offBlurFboA = 0; }
        if (_offBlurFboB != 0) { _gl.DeleteFramebuffer(_offBlurFboB); _offBlurFboB = 0; }
        if (_offCopyTex != 0) { _gl.DeleteTexture(_offCopyTex); _offCopyTex = 0; }
        if (_offBlurTexA != 0) { _gl.DeleteTexture(_offBlurTexA); _offBlurTexA = 0; }
        if (_offBlurTexB != 0) { _gl.DeleteTexture(_offBlurTexB); _offBlurTexB = 0; }
        _offWidth = _offHeight = 0;
    }

    private void Bind(uint tex) => Bind(tex, TextureUnit.Texture0);

    private void Bind(uint tex, TextureUnit unit)
    {
        _gl.ActiveTexture(unit);
        _gl.BindTexture(TextureTarget.Texture2D, tex);
    }

    private void EnsureTargets(int w, int h)
    {
        if (w == _width && h == _height && _sceneFbo != 0) return;

        DeleteTargets();
        _width = w;
        _height = h;
        _sceneTex = NewTexture(_width, _height);
        _sceneFbo = NewFbo(_sceneTex, "scene");
        EnsureDepthTarget();
    }

    private void EnsureBlurTargets(int w, int h)
    {
        if (w == _blurWidth && h == _blurHeight && _blurFboA != 0) return;

        DeleteBlurTargets();
        _blurWidth = w;
        _blurHeight = h;
        // Linear, because the resolution-scaled tap offsets are fractional
        // texels above 1080p; at exactly 1 the taps land on texel centres and
        // linear degenerates to nearest anyway.
        _blurTexA = NewTexture(w, h);
        _blurFboA = NewFbo(_blurTexA, "blurA");
        _blurTexB = NewTexture(w, h);
        _blurFboB = NewFbo(_blurTexB, "blurB");
    }

    private void EnsureCanvasTargets(int w, int h)
    {
        if (w == _canvasWidth && h == _canvasHeight && _canvasSceneFbo != 0) return;

        DeleteCanvasTargets();
        _canvasWidth = w;
        _canvasHeight = h;
        _canvasSceneTex = NewTexture(w, h);
        _canvasSceneFbo = NewFbo(_canvasSceneTex, "canvas-scene");
        _canvasOutputTex = NewTexture(w, h, nearest: true);
        _canvasOutputFbo = NewFbo(_canvasOutputTex, "canvas-output");
    }

    private unsafe void EnsureDepthTarget()
    {
        if (_depthFormat < 0 || _depthFbo != 0) return;
        var (format, pixels, type, _) = DepthCandidates[_depthFormat];

        _depthTex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _depthTex);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, format,
            (uint)_width, (uint)_height, 0, pixels, type, (void*)null);
        // Nearest: a filtered depth value is a depth that exists nowhere in the
        // scene, and the silhouette test is built on exact neighbour depths.
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        // Depth textures default to comparison mode on some drivers; this pass
        // wants the raw value, not a shadow-map compare result.
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareMode, (int)GLEnum.None);
        _gl.BindTexture(TextureTarget.Texture2D, 0);

        var attachment = format == InternalFormat.Depth24Stencil8
            ? FramebufferAttachment.DepthStencilAttachment
            : FramebufferAttachment.DepthAttachment;
        _depthFbo = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _depthFbo);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, attachment,
            TextureTarget.Texture2D, _depthTex, 0);
        // A depth-only FBO draws no colour; say so or the completeness check fails.
        _gl.DrawBuffer(DrawBufferMode.None);
        _gl.ReadBuffer(ReadBufferMode.None);
        var status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

        if (status != GLEnum.FramebufferComplete)
        {
            Console.WriteLine($"[painterly] depth target ({format}) incomplete: {status}");
            DeleteDepthTarget();
            _depthFormat++;
            if (_depthFormat >= DepthCandidates.Length) _depthFormat = -1;
            else EnsureDepthTarget();
        }
    }

    private void DeleteDepthTarget()
    {
        if (_depthFbo != 0) { _gl.DeleteFramebuffer(_depthFbo); _depthFbo = 0; }
        if (_depthTex != 0) { _gl.DeleteTexture(_depthTex); _depthTex = 0; }
        _depthLive = false;
    }

    private unsafe uint NewTexture(int w, int h, bool nearest = false)
    {
        uint tex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, tex);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8,
            (uint)w, (uint)h, 0, PixelFormat.Rgba, PixelType.UnsignedByte, (void*)null);
        GLEnum filter = nearest ? GLEnum.Nearest : GLEnum.Linear;
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)filter);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)filter);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
        return tex;
    }

    private uint NewFbo(uint tex, string label)
    {
        uint fbo = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer,
            FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, tex, 0);
        var status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != GLEnum.FramebufferComplete)
            Console.WriteLine($"[painterly] {label} framebuffer incomplete: {status}");
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        return fbo;
    }

    private void DeleteTargets()
    {
        if (_sceneFbo != 0) { _gl.DeleteFramebuffer(_sceneFbo); _sceneFbo = 0; }
        if (_sceneTex != 0) { _gl.DeleteTexture(_sceneTex); _sceneTex = 0; }
        _width = _height = 0;
        DeleteBlurTargets();
        DeleteCanvasTargets();
        DeleteDepthTarget();
    }

    private void DeleteBlurTargets()
    {
        if (_blurFboA != 0) { _gl.DeleteFramebuffer(_blurFboA); _blurFboA = 0; }
        if (_blurFboB != 0) { _gl.DeleteFramebuffer(_blurFboB); _blurFboB = 0; }
        if (_blurTexA != 0) { _gl.DeleteTexture(_blurTexA); _blurTexA = 0; }
        if (_blurTexB != 0) { _gl.DeleteTexture(_blurTexB); _blurTexB = 0; }
        _blurWidth = _blurHeight = 0;
    }

    private void DeleteCanvasTargets()
    {
        if (_canvasSceneFbo != 0) { _gl.DeleteFramebuffer(_canvasSceneFbo); _canvasSceneFbo = 0; }
        if (_canvasOutputFbo != 0) { _gl.DeleteFramebuffer(_canvasOutputFbo); _canvasOutputFbo = 0; }
        if (_canvasSceneTex != 0) { _gl.DeleteTexture(_canvasSceneTex); _canvasSceneTex = 0; }
        if (_canvasOutputTex != 0) { _gl.DeleteTexture(_canvasOutputTex); _canvasOutputTex = 0; }
        _canvasWidth = _canvasHeight = 0;
    }

    public void Dispose()
    {
        DeleteTargets();
        DeleteOffscreenTargets();
        if (_vao != 0) _gl.DeleteVertexArray(_vao);
        _blur.Dispose();
        _style.Dispose();
        _present.Dispose();
    }

    // ---------------------------------------------------------------- shaders

    private const string FullscreenVert = @"#version 330 core
out vec2 vUv;
void main()
{
    vec2 uv = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2); // (0,0)(2,0)(0,2)
    vUv = uv;
    gl_Position = vec4(uv * 2.0 - 1.0, 0.0, 1.0);            // (-1,-1)(3,-1)(-1,3)
}";

    // Separable Gaussian, sigma 1.6 over +/-3 texels. Deliberately SMALL: this
    // is the frequency split line, and a wide blur would push medium-scale
    // structure (a roof, a shield) into the "detail" band where quantization
    // can no longer band it.
    private const string BlurFrag = @"#version 330 core
in vec2 vUv;
uniform sampler2D uTex;
uniform vec2 uTexel;
uniform vec2 uAxis;
uniform float uScale;   // reference pixels -> live pixels
out vec4 frag;

void main()
{
    vec2 t = uAxis * uTexel * uScale;
    vec3 acc = vec3(0.0);
    float norm = 0.0;
    for (int i = -3; i <= 3; i++)
    {
        float w = exp(-float(i * i) / 5.12); // sigma = 1.6
        acc += texture(uTex, vUv + float(i) * t).rgb * w;
        norm += w;
    }
    frag = vec4(acc / norm, 1.0);
}";

    // Shader presentation is required for the optional fixed canvas. The
    // window framebuffer may be multisampled, so a scaled framebuffer blit is
    // not legal; a nearest-filtered fullscreen draw performs the upscale instead.
    private const string PresentFrag = @"#version 330 core
in vec2 vUv;
uniform sampler2D uTex;
out vec4 frag;

void main()
{
    frag = texture(uTex, vUv);
}";

    // The illustrated restyle. Reads the source frame and its low-frequency
    // version; everything below is a luminance operation with the source chroma
    // carried along, so hues stay where the artists put them.
    private const string StyleFrag = @"#version 330 core
in vec2 vUv;
uniform sampler2D uScene;
uniform sampler2D uLow;
uniform vec2 uTexel;
uniform float uScale;   // reference pixels -> live pixels
uniform float uBands;
uniform float uBandStrength;
uniform float uDetail;
uniform float uInk;
uniform float uInkThreshold;
uniform float uSaturation;
uniform float uContrast;
uniform float uLift;
uniform float uWarmth;
uniform float uGrain;
uniform float uDither;
uniform float uUseStyleWeights;
uniform sampler2D uDepth;
uniform float uHasDepth;
uniform float uSilhouette;
uniform float uDepthFade;
uniform float uCalmStart;
uniform float uCalmEnd;
uniform float uNear;
uniform float uFar;
out vec4 frag;

const vec3 LUMA = vec3(0.2126, 0.7152, 0.0722);

// Window depth -> eye-space distance in world units. Camera.Projection is
// System.Numerics' 0..1 projection. OpenGL maps that NDC interval into window
// depth 0.5..1.0, so the first line recovers the original 0..1 NDC value and
// the second line uses the matching 0..1 inverse (not the OpenGL -1..1 one).
float eyeDepth(vec2 uv)
{
    float d = texture(uDepth, uv).r * 2.0 - 1.0;
    return (uNear * uFar) / (uFar - d * (uFar - uNear));
}

// Cheap hash noise for the canvas grain. Band dither uses the ordered sequence
// below so threshold crossings do not gather into random clumps.
float hash12(vec2 p)
{
    vec3 p3 = fract(vec3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return fract((p3.x + p3.y) * p3.z);
}

// Stable interleaved-gradient dither. It is evaluated on reference-pixel cells,
// so its apparent size does not change with output resolution, and unlike white
// hash noise it distributes thresholds evenly without large random clumps.
float orderedNoise(vec2 p)
{
    return fract(52.9829189 * fract(dot(p, vec2(0.06711056, 0.00583715))));
}

// Put a candidate colour at an exact target luminance, then compress only its
// chroma until every channel fits. This avoids per-channel clipping, which both
// shifts hue and creates bright/dark sharpening halos at saturated edges.
vec3 gamutMapAtLuma(vec3 candidate, float targetLum)
{
    float y = clamp(targetLum, 0.0, 1.0);
    candidate += vec3(y - dot(candidate, LUMA));
    vec3 chroma = candidate - vec3(y);
    float scale = 1.0;
    for (int i = 0; i < 3; i++)
    {
        if (chroma[i] > 0.0)
            scale = min(scale, (1.0 - y) / chroma[i]);
        else if (chroma[i] < 0.0)
            scale = min(scale, y / -chroma[i]);
    }
    return clamp(vec3(y) + chroma * clamp(scale, 0.0, 1.0), 0.0, 1.0);
}

void main()
{
    vec4 scene = texture(uScene, vUv);
    vec3 src = scene.rgb;
    vec3 low = texture(uLow, vUv).rgb;

    // The world renderers store style importance in framebuffer alpha: quiet
    // terrain/foliage receive less micro-detail and much less ink, while units
    // remain fully articulated. Off-screen alpha means coverage instead, so
    // uUseStyleWeights selects neutral factors there. Broad value grading and
    // authored texture are deliberately not weighted; the hierarchy affects
    // only generated dither, grain and ink.
    float materialAlpha = clamp(scene.a, 0.0, 1.0);
    float markImportance = mix(1.0, mix(0.20, 1.0, materialAlpha), uUseStyleWeights);
    float lineImportance = mix(1.0, mix(0.08, 1.0, materialAlpha), uUseStyleWeights);

    float lowLum = dot(low, LUMA);
    float lo = lowLum;
    vec3 residual = src - low;       // RGB texture: leaves, shingles, cobbles
    float residualLum = dot(residual, LUMA);
    float residualEnergy = max(max(abs(residual.r), abs(residual.g)), abs(residual.b));

    vec2 e1 = uTexel * uScale;      // one reference pixel, whatever the res

    // Aerial perspective in explicit world units. Treating every generated mark
    // identically is noisy: dither, ink and grain on distant foliage compete
    // with the foreground. Calm is 1 in
    // the near field and reaches (1-uDepthFade) at uCalmEnd. Authored source
    // texture is deliberately excluded: the target remains crisp at distance;
    // only generated marks become quieter.
    float calm = 1.0;
    float zc = 0.0;
    if (uHasDepth > 0.5)
    {
        zc = eyeDepth(vUv);
        float distanceFade = smoothstep(uCalmStart, max(uCalmEnd, uCalmStart + 1.0), zc);
        calm = 1.0 - uDepthFade * distanceFade;
    }

    // Value structure first: an S-curve deepens the shadow side and lifts the
    // lit side, so the scene separates into readable light and dark SHAPES
    // instead of one mid-tone field. Done before quantization so the bands land
    // on the separated values, and non-clipping so nothing blows out.
    //
    // The curve is pivoted at PIVOT, not at 0.5. Game exteriors sit well below
    // half luminance (this scene's grass is around 0.3), so a 0.5-pivoted
    // smoothstep pushes the ENTIRE frame down and just reads as murk. The
    // remap sends PIVOT to 0.5, S-curves there, and sends it back, which holds
    // the scene's own mid-tone still while spreading everything around it.
    const float PIVOT = 0.38;
    float xs = (lo < PIVOT) ? (lo / PIVOT) * 0.5
                            : 0.5 + (lo - PIVOT) / (1.0 - PIVOT) * 0.5;
    float ss = xs * xs * (3.0 - 2.0 * xs);
    float curved = (ss < 0.5) ? ss * 2.0 * PIVOT
                              : PIVOT + (ss - 0.5) * 2.0 * (1.0 - PIVOT);
    lo = mix(lo, curved, uContrast);

    // Painted value steps, on the low frequency only. Bands is rounded on the
    // CPU and again here defensively. N bands means N endpoint-preserving levels
    // over [0,1], unlike midpoint bins which lifted black and lowered white.
    // Ordered dither is independent of canvas grain, fades with distance, and
    // switches itself off where source texture already breaks the contour.
    vec2 cell = floor(gl_FragCoord.xy / uScale);
    float bandCount = floor(uBands + 0.5);
    float intervals = max(bandCount - 1.0, 1.0);
    float step = 1.0 / intervals;
    float textureQuiet = 1.0 - smoothstep(0.02, 0.12, residualEnergy);
    float dither = (orderedNoise(cell) - 0.5) * step * uDither
                 * calm * markImportance * textureQuiet;
    float quantizedInput = clamp(lo + dither, 0.0, 1.0);
    float quantized = floor(quantizedInput * intervals + 0.5) / intervals;
    if (lo <= 0.0) quantized = 0.0;
    if (lo >= 1.0) quantized = 1.0;
    float banded = mix(lo, quantized, uBandStrength);

    // Detail is an ABSOLUTE residual gain and is not secretly multiplied by
    // material class or distance. Only the gain above 1 is suppressed on strong
    // edges: source texture stays intact at 1, while 2 cannot grow dark/bright
    // unsharp-mask halos.
    float requestedGain = uDetail;
    float boostGuard = 1.0 - smoothstep(0.06, 0.22, residualEnergy);
    float detailGain = min(requestedGain, 1.0)
                     + max(requestedGain - 1.0, 0.0) * boostGuard;
    float targetLum = clamp(banded + residualLum * detailGain, 0.0, 1.0);
    vec3 paintedLow = low + vec3(banded - lowLum);
    vec3 c = gamutMapAtLuma(paintedLow + residual * detailGain, targetLum);

    // Colour richness, about the restyled luminance.
    float lum = dot(c, LUMA);
    c = gamutMapAtLuma(mix(vec3(lum), c, uSaturation), lum);

    // Sun/shade split tone: lights toward warm, shadows toward a cool blue.
    vec3 cool = vec3(0.86, 0.94, 1.14);
    vec3 warm = vec3(1.08, 1.01, 0.88);
    vec3 toned = c * mix(cool, warm, smoothstep(0.15, 0.75, lum));
    c = clamp(mix(c, toned, uWarmth), 0.0, 1.0);

    // Midtone lift. A gamma, so 0 and 1 are fixed and nothing clips; it opens
    // the middle of the range back up after the S-curve and the ink have taken
    // light out. Before the ink so the lines still bite into the lifted image.
    if (uLift != 1.0) c = pow(c, vec3(1.0 / uLift));

    // Ink. Sobel over the BLURRED luminance so ground texture does not draw
    // lines; the threshold gates what is left. Darkening toward a fraction of
    // the pixel's own colour keeps ink from reading as a black sticker on
    // saturated surfaces the way a mix toward pure black does.
    float ink = 0.0;
    if (uInk > 0.0)
    {
        float l00 = dot(texture(uLow, vUv + vec2(-1.0, -1.0) * e1).rgb, LUMA);
        float l10 = dot(texture(uLow, vUv + vec2( 0.0, -1.0) * e1).rgb, LUMA);
        float l20 = dot(texture(uLow, vUv + vec2( 1.0, -1.0) * e1).rgb, LUMA);
        float l01 = dot(texture(uLow, vUv + vec2(-1.0,  0.0) * e1).rgb, LUMA);
        float l21 = dot(texture(uLow, vUv + vec2( 1.0,  0.0) * e1).rgb, LUMA);
        float l02 = dot(texture(uLow, vUv + vec2(-1.0,  1.0) * e1).rgb, LUMA);
        float l12 = dot(texture(uLow, vUv + vec2( 0.0,  1.0) * e1).rgb, LUMA);
        float l22 = dot(texture(uLow, vUv + vec2( 1.0,  1.0) * e1).rgb, LUMA);

        float gx = (l20 + 2.0 * l21 + l22) - (l00 + 2.0 * l01 + l02);
        float gy = (l02 + 2.0 * l12 + l22) - (l00 + 2.0 * l10 + l20);
        float edge = length(vec2(gx, gy)) * 0.25;
        ink = smoothstep(uInkThreshold, uInkThreshold + 0.14, edge)
            * uInk * calm * lineImportance;
    }

    // Silhouette ink: boundaries the COLOUR ink cannot see. A dark figure in
    // front of a dark hedge has no colour edge at all, yet that is precisely
    // where an illustration draws its firmest line - so the line comes from
    // the depth buffer instead.
    //
    // The test keeps the SECOND derivative of eye depth, so a smooth ground
    // ramp cancels, but samples only 0.65 reference pixels away for a narrower
    // line. A foreground-side gate prevents the same discontinuity from being
    // painted once on the subject and again on the background/sky side.
    if (uHasDepth > 0.5 && uSilhouette > 0.0)
    {
        vec2 de = e1 * 0.65;
        float zl = eyeDepth(vUv - vec2(de.x, 0.0));
        float zr = eyeDepth(vUv + vec2(de.x, 0.0));
        float zu = eyeDepth(vUv - vec2(0.0, de.y));
        float zd = eyeDepth(vUv + vec2(0.0, de.y));
        float curve = abs(zl + zr - 2.0 * zc) + abs(zu + zd - 2.0 * zc);

        float farther = max(max(zl, zr), max(zu, zd));
        float nearer = min(min(zl, zr), min(zu, zd));
        float foregroundStep = max(farther - zc, 0.0);
        float backgroundStep = max(zc - nearer, 0.0);
        float sideDelta = (foregroundStep - backgroundStep) / max(zc, 1.0);
        float foregroundSide = smoothstep(0.001, 0.008, sideDelta);

        // Two gates, both of which a real silhouette clears.
        //   relative: scale-free, so one setting works near and far.
        //   absolute: the step must be a genuine gap in WORLD units. Without
        //     it every clutter billboard - each blade of grass is a quad
        //     standing a few inches off the ground - outlines itself, and the
        //     field turns into a carpet of dark specks. That is the same
        //     busy-ness the distance fade exists to remove, so it must not be
        //     reintroduced here.
        float relEdge = smoothstep(0.004, 0.020, curve / max(zc, 1.0));
        float absEdge = smoothstep(0.30, 1.20, curve);
        ink = max(ink, relEdge * absEdge * foregroundSide
                       * uSilhouette * calm * lineImportance);
    }

    // Toward a fraction of the pixel's own colour, not toward black - black
    // reads as a sticker on saturated surfaces.
    if (ink > 0.0) c = mix(c, c * 0.30, ink);

    // Canvas grain, weighted toward the lights so shadows stay clean.
    if (uGrain > 0.0)
    {
        float g = (hash12(cell * 1.7 + 11.0) - 0.5) * uGrain * 0.10
                * calm * markImportance;
        float grainLum = dot(c, LUMA);
        c = clamp(c + g * (0.35 + 0.65 * grainLum), 0.0, 1.0);
    }

    // Source alpha, not 1.0. Irrelevant for the screen pass (the default
    // framebuffer's alpha goes nowhere) but load-bearing off-screen: a portrait
    // bake is a cut-out, and writing opaque alpha would fill its transparent
    // background with a styled black square.
    frag = vec4(c, scene.a);
}";
}

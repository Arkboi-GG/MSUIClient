using Silk.NET.OpenGL;
using SkiaSharp;

namespace MSUIClient.Engine;

/// <summary>
/// A small, depth-backed framebuffer used to bake a real unit model into an
/// ImGui texture. Portraits are intentionally cached by the caller: the model
/// is rendered once when its appearance changes, not once per UI frame.
/// </summary>
public sealed class PortraitRenderTarget : IDisposable
{
    public readonly record struct ReadbackStats(int SubjectPixels, byte MinRgb, byte MaxRgb,
        byte MinAlpha, byte MaxAlpha)
    {
        public bool HasSubject => SubjectPixels >= 128;
        public override string ToString() =>
            $"subject={SubjectPixels}, rgb={MinRgb}..{MaxRgb}, alpha={MinAlpha}..{MaxAlpha}";
    }

    private readonly GL _gl;
    private uint _framebuffer;
    private uint _depth;

    public uint TextureHandle { get; private set; }
    public int Width { get; }
    public int Height { get; }
    public int Size => Width;

    public PortraitRenderTarget(GL gl, int size = 256) : this(gl, size, size) { }

    public unsafe PortraitRenderTarget(GL gl, int width, int height)
    {
        _gl = gl;
        Width = Math.Max(64, width);
        Height = Math.Max(64, height);

        TextureHandle = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, TextureHandle);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8,
            (uint)Width, (uint)Height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, (void*)null);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);

        _depth = _gl.GenRenderbuffer();
        _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _depth);
        _gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.DepthComponent24,
            (uint)Width, (uint)Height);

        _framebuffer = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _framebuffer);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, TextureHandle, 0);
        _gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
            RenderbufferTarget.Renderbuffer, _depth);

        GLEnum status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, 0);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
        if (status != GLEnum.FramebufferComplete)
            throw new InvalidOperationException($"Portrait framebuffer is incomplete: {status}");
    }

    public unsafe void Bake(Action draw, bool transparent = false)
    {
        int* viewport = stackalloc int[4];
        int* previousFramebuffer = stackalloc int[1];
        _gl.GetInteger(GLEnum.Viewport, viewport);
        _gl.GetInteger(GLEnum.FramebufferBinding, previousFramebuffer);
        bool scissorEnabled = _gl.IsEnabled(EnableCap.ScissorTest);
        bool depthEnabled = _gl.IsEnabled(EnableCap.DepthTest);
        bool cullEnabled = _gl.IsEnabled(EnableCap.CullFace);
        bool blendEnabled = _gl.IsEnabled(EnableCap.Blend);

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _framebuffer);
        _gl.Viewport(0, 0, (uint)Width, (uint)Height);
        // ImGui leaves its last window's clip rectangle in GL_SCISSOR_BOX. Applying that
        // screen-space rectangle to this 256x256 FBO clips both the clear and the model,
        // yielding the valid-but-black portrait seen in the unit frame.
        _gl.Disable(EnableCap.ScissorTest);
        // A booth is an isolated opaque scene. World particles/materials can leave blending
        // enabled with multiply/additive factors, and a false depth write mask also prevents
        // glClear from resetting the depth attachment. Establish the whole raster baseline before
        // clearing; doing DepthMask(true) after Clear is too late.
        _gl.Disable(EnableCap.Blend);
        _gl.ColorMask(true, true, true, true);
        _gl.DepthMask(true);
        _gl.DepthFunc(DepthFunction.Lequal);
        _gl.CullFace(TriangleFace.Back);
        _gl.FrontFace(FrontFaceDirection.Ccw);
        // Benilla booth parity: an opaque near-black backdrop (0.055, 0.045, 0.04) so the
        // world can never show through the portrait circle; only body panes clear transparent.
        _gl.ClearColor(transparent ? 0f : 0.055f, transparent ? 0f : 0.045f,
            transparent ? 0f : 0.04f, transparent ? 0f : 1f);
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        _gl.Enable(EnableCap.DepthTest);
        _gl.Enable(EnableCap.CullFace);
        draw();

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)previousFramebuffer[0]);
        _gl.Viewport(viewport[0], viewport[1], (uint)viewport[2], (uint)viewport[3]);
        if (scissorEnabled) _gl.Enable(EnableCap.ScissorTest);
        if (blendEnabled) _gl.Enable(EnableCap.Blend);
        if (!depthEnabled) _gl.Disable(EnableCap.DepthTest);
        if (!cullEnabled) _gl.Disable(EnableCap.CullFace);
    }

    /// <summary>
    /// Synchronous diagnostic readback. Portraits bake only on appearance changes, so this is
    /// intentionally correctness-first: it distinguishes a real subject from a framebuffer that
    /// contains only the booth clear colour instead of guessing from camera metadata.
    /// </summary>
    public ReadbackStats Analyze(bool transparent = false)
    {
        byte[] rgba = ReadRgba();
        byte clearR = transparent ? (byte)0 : (byte)14;
        byte clearG = transparent ? (byte)0 : (byte)11;
        byte clearB = transparent ? (byte)0 : (byte)10;
        byte clearA = transparent ? (byte)0 : (byte)255;
        int subject = 0;
        byte minRgb = 255, maxRgb = 0, minAlpha = 255, maxAlpha = 0;
        for (int i = 0; i < rgba.Length; i += 4)
        {
            byte r = rgba[i], g = rgba[i + 1], b = rgba[i + 2], a = rgba[i + 3];
            minRgb = Math.Min(minRgb, Math.Min(r, Math.Min(g, b)));
            maxRgb = Math.Max(maxRgb, Math.Max(r, Math.Max(g, b)));
            minAlpha = Math.Min(minAlpha, a);
            maxAlpha = Math.Max(maxAlpha, a);
            int colourDelta = Math.Abs(r - clearR) + Math.Abs(g - clearG) + Math.Abs(b - clearB);
            if (colourDelta > 18 || Math.Abs(a - clearA) > 12) subject++;
        }
        return new ReadbackStats(subject, minRgb, maxRgb, minAlpha, maxAlpha);
    }

    /// <summary>
    /// Zero the alpha of every texel outside the inscribed circle. The reference client masks
    /// the round unit-frame portrait in its UI shader ("circular" path of ui_quad); the ring
    /// chrome is a thin band with TRANSPARENT corners, so nothing else hides the square. ImGui
    /// has no per-image mask (AddImageRounded emitted a single fan triangle on this backend),
    /// so the disc is cut into the baked texture itself instead. Call after a successful bake
    /// (and after <see cref="Analyze"/>, whose subject count assumes an unmasked surface).
    /// </summary>
    public unsafe void ApplyCircularMask()
    {
        byte[] rgba = ReadRgba();
        float cx = (Width - 1) * 0.5f, cy = (Height - 1) * 0.5f;
        float radius = MathF.Min(Width, Height) * 0.5f;
        float r2 = radius * radius;
        for (int y = 0; y < Height; y++)
        {
            float dy = y - cy;
            for (int x = 0; x < Width; x++)
            {
                float dx = x - cx;
                if (dx * dx + dy * dy > r2) rgba[(y * Width + x) * 4 + 3] = 0;
            }
        }
        _gl.BindTexture(TextureTarget.Texture2D, TextureHandle);
        fixed (byte* pixels = rgba)
            _gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, (uint)Width, (uint)Height,
                PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
    }

    /// <summary>Write the actual FBO pixels for a failed-bake investigation.</summary>
    public void SavePng(string path)
    {
        byte[] rgba = ReadRgba();
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        using var bitmap = new SKBitmap(Width, Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            int i = (y * Width + x) * 4;
            bitmap.SetPixel(x, Height - 1 - y,
                new SKColor(rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]));
        }
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        using FileStream stream = File.Create(path);
        data.SaveTo(stream);
    }

    private unsafe byte[] ReadRgba()
    {
        int* previousFramebuffer = stackalloc int[1];
        _gl.GetInteger(GLEnum.FramebufferBinding, previousFramebuffer);
        byte[] rgba = new byte[Width * Height * 4];
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _framebuffer);
        fixed (byte* pixels = rgba)
            _gl.ReadPixels(0, 0, (uint)Width, (uint)Height,
                PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)previousFramebuffer[0]);
        return rgba;
    }

    public void Dispose()
    {
        if (_depth != 0) { _gl.DeleteRenderbuffer(_depth); _depth = 0; }
        if (_framebuffer != 0) { _gl.DeleteFramebuffer(_framebuffer); _framebuffer = 0; }
        if (TextureHandle != 0) { _gl.DeleteTexture(TextureHandle); TextureHandle = 0; }
    }
}

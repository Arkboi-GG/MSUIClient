using Silk.NET.OpenGL;

namespace MSUIClient.World.Portals;

/// <summary>
/// Owns a copy of the last complete portal-preview frame. The copy deliberately
/// does not retain the preview target's texture handle: world teardown may
/// invalidate, resize, reuse, or dispose that double-buffered target while the
/// main destination is still loading.
/// </summary>
public sealed class PortalHandoffSnapshot : IDisposable
{
    private readonly GL _gl;
    private readonly int _ownerThread;
    private uint _readFramebuffer;
    private uint _drawFramebuffer;
    private uint _texture;
    private int _width;
    private int _height;
    private bool _disposed;

    public PortalHandoffSnapshot(GL gl)
    {
        _gl = gl;
        _ownerThread = Environment.CurrentManagedThreadId;
        _readFramebuffer = gl.GenFramebuffer();
        _drawFramebuffer = gl.GenFramebuffer();
    }

    public uint Texture => HasFrame ? _texture : 0;
    public bool HasFrame { get; private set; }
    public int Width => HasFrame ? _width : 0;
    public int Height => HasFrame ? _height : 0;

    /// <summary>
    /// Copy an already completed single-sample RGBA preview texture. OpenGL
    /// command ordering makes the copied texture safe to sample on subsequent
    /// draws; the source is detached before returning so deleting the preview
    /// target cannot leave an attachment relationship behind.
    /// </summary>
    public unsafe void Capture(uint sourceTexture, int width, int height)
    {
        EnsureOwnerThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (sourceTexture == 0) throw new ArgumentOutOfRangeException(nameof(sourceTexture));
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Portal snapshot size must be positive");

        int* state = stackalloc int[1];
        _gl.GetInteger(GLEnum.ReadFramebufferBinding, state);
        uint previousReadFramebuffer = (uint)state[0];
        _gl.GetInteger(GLEnum.DrawFramebufferBinding, state);
        uint previousDrawFramebuffer = (uint)state[0];
        _gl.GetInteger(GLEnum.ActiveTexture, state);
        TextureUnit previousActiveTexture = (TextureUnit)state[0];
        _gl.GetInteger(GLEnum.TextureBinding2D, state);
        uint previousActiveTexture2D = (uint)state[0];
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.GetInteger(GLEnum.TextureBinding2D, state);
        uint previousTexture0 = (uint)state[0];

        try
        {
            EnsureTexture(width, height);

            _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _readFramebuffer);
            _gl.FramebufferTexture2D(FramebufferTarget.ReadFramebuffer,
                FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, sourceTexture, 0);
            RequireComplete(FramebufferTarget.ReadFramebuffer, "source");

            _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _drawFramebuffer);
            _gl.FramebufferTexture2D(FramebufferTarget.DrawFramebuffer,
                FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _texture, 0);
            RequireComplete(FramebufferTarget.DrawFramebuffer, "snapshot");

            _gl.BlitFramebuffer(0, 0, width, height, 0, 0, width, height,
                ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
            _gl.Flush();
            HasFrame = true;
        }
        catch
        {
            HasFrame = false;
            throw;
        }
        finally
        {
            // Detach while our FBOs are bound, then restore every binding this
            // small transfer touched. The owned snapshot texture stays alive.
            _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _readFramebuffer);
            _gl.FramebufferTexture2D(FramebufferTarget.ReadFramebuffer,
                FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, 0, 0);
            _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _drawFramebuffer);
            _gl.FramebufferTexture2D(FramebufferTarget.DrawFramebuffer,
                FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, 0, 0);
            _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, previousReadFramebuffer);
            _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, previousDrawFramebuffer);
            // EnsureTexture bound the copy target on unit 0. Restore that unit
            // first, then return to whichever unit the caller had active.
            _gl.BindTexture(TextureTarget.Texture2D, previousTexture0);
            _gl.ActiveTexture(previousActiveTexture);
            _gl.BindTexture(TextureTarget.Texture2D, previousActiveTexture2D);
        }
    }

    public void Clear()
    {
        EnsureOwnerThread();
        if (_disposed) return;
        if (_texture != 0) _gl.DeleteTexture(_texture);
        _texture = 0;
        _width = _height = 0;
        HasFrame = false;
    }

    private unsafe void EnsureTexture(int width, int height)
    {
        if (_texture != 0 && _width == width && _height == height) return;
        if (_texture != 0) _gl.DeleteTexture(_texture);

        _texture = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _texture);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8,
            (uint)width, (uint)height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, null);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
            (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter,
            (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS,
            (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT,
            (int)GLEnum.ClampToEdge);
        _width = width;
        _height = height;
    }

    private void RequireComplete(FramebufferTarget target, string role)
    {
        GLEnum status = _gl.CheckFramebufferStatus(target);
        if (status != GLEnum.FramebufferComplete)
            throw new InvalidOperationException(
                $"Portal handoff {role} framebuffer is incomplete: {status}");
    }

    public void Dispose()
    {
        EnsureOwnerThread();
        if (_disposed) return;
        if (_texture != 0) _gl.DeleteTexture(_texture);
        if (_readFramebuffer != 0) _gl.DeleteFramebuffer(_readFramebuffer);
        if (_drawFramebuffer != 0) _gl.DeleteFramebuffer(_drawFramebuffer);
        _texture = _readFramebuffer = _drawFramebuffer = 0;
        _width = _height = 0;
        HasFrame = false;
        _disposed = true;
    }

    private void EnsureOwnerThread()
    {
        if (Environment.CurrentManagedThreadId != _ownerThread)
            throw new InvalidOperationException(
                "Portal handoff snapshots must be used on their owning GL thread");
    }
}

using System.Numerics;
using Silk.NET.OpenGL;

namespace MSUIClient.World.Portals;

/// <summary>
/// Double-buffered portal preview target. A rendered back texture is fenced and
/// becomes public only after a later non-blocking poll observes the whole frame
/// complete; callers therefore never sample a texture that is still being drawn.
/// </summary>
public sealed class PortalRenderTarget : IDisposable
{
    private sealed class Slot
    {
        public uint Framebuffer;
        public uint Texture;
        public uint Depth;
        public int Width;
        public int Height;
    }

    private readonly struct GlState
    {
        public readonly int[] Viewport;
        public readonly int[] ScissorBox;
        public readonly bool[] ColorMask;
        public readonly Vector4 ClearColor;
        public readonly uint DrawFramebuffer;
        public readonly uint ReadFramebuffer;
        public readonly uint Program;
        public readonly uint VertexArray;
        public readonly uint ArrayBuffer;
        public readonly TextureUnit ActiveTexture;
        public readonly uint ActiveTexture2D;
        public readonly uint ActiveTexture2DArray;
        public readonly uint[] Texture2D;
        public readonly uint[] Texture2DArray;
        public readonly bool DepthMask;
        public readonly DepthFunction DepthFunction;
        public readonly TriangleFace CullFaceMode;
        public readonly FrontFaceDirection FrontFace;
        public readonly bool DepthTest;
        public readonly bool CullFace;
        public readonly bool Blend;
        public readonly bool ScissorTest;
        public readonly bool FramebufferSrgb;
        public readonly bool ClipDistance0;
        public readonly BlendingFactor BlendSrcRgb;
        public readonly BlendingFactor BlendDstRgb;
        public readonly BlendingFactor BlendSrcAlpha;
        public readonly BlendingFactor BlendDstAlpha;

        public unsafe GlState(GL gl, int textureUnits)
        {
            int* values = stackalloc int[4];
            float* floats = stackalloc float[4];
            gl.GetInteger(GLEnum.Viewport, values);
            Viewport = [values[0], values[1], values[2], values[3]];
            gl.GetInteger(GLEnum.ScissorBox, values);
            ScissorBox = [values[0], values[1], values[2], values[3]];
            gl.GetInteger(GLEnum.ColorWritemask, values);
            ColorMask = [values[0] != 0, values[1] != 0, values[2] != 0, values[3] != 0];
            gl.GetFloat(GLEnum.ColorClearValue, floats);
            ClearColor = new Vector4(floats[0], floats[1], floats[2], floats[3]);
            gl.GetInteger(GLEnum.DrawFramebufferBinding, values); DrawFramebuffer = (uint)values[0];
            gl.GetInteger(GLEnum.ReadFramebufferBinding, values); ReadFramebuffer = (uint)values[0];
            gl.GetInteger(GLEnum.CurrentProgram, values); Program = (uint)values[0];
            gl.GetInteger(GLEnum.VertexArrayBinding, values); VertexArray = (uint)values[0];
            gl.GetInteger(GLEnum.ArrayBufferBinding, values); ArrayBuffer = (uint)values[0];
            gl.GetInteger(GLEnum.ActiveTexture, values); ActiveTexture = (TextureUnit)values[0];
            gl.GetInteger(GLEnum.TextureBinding2D, values); ActiveTexture2D = (uint)values[0];
            gl.GetInteger(GLEnum.TextureBinding2DArray, values); ActiveTexture2DArray = (uint)values[0];
            gl.GetInteger(GLEnum.DepthWritemask, values); DepthMask = values[0] != 0;
            gl.GetInteger(GLEnum.DepthFunc, values); DepthFunction = (DepthFunction)values[0];
            gl.GetInteger(GLEnum.CullFaceMode, values); CullFaceMode = (TriangleFace)values[0];
            gl.GetInteger(GLEnum.FrontFace, values); FrontFace = (FrontFaceDirection)values[0];
            gl.GetInteger(GLEnum.BlendSrcRgb, values); BlendSrcRgb = (BlendingFactor)values[0];
            gl.GetInteger(GLEnum.BlendDstRgb, values); BlendDstRgb = (BlendingFactor)values[0];
            gl.GetInteger(GLEnum.BlendSrcAlpha, values); BlendSrcAlpha = (BlendingFactor)values[0];
            gl.GetInteger(GLEnum.BlendDstAlpha, values); BlendDstAlpha = (BlendingFactor)values[0];

            DepthTest = gl.IsEnabled(EnableCap.DepthTest);
            CullFace = gl.IsEnabled(EnableCap.CullFace);
            Blend = gl.IsEnabled(EnableCap.Blend);
            ScissorTest = gl.IsEnabled(EnableCap.ScissorTest);
            FramebufferSrgb = gl.IsEnabled(EnableCap.FramebufferSrgb);
            ClipDistance0 = gl.IsEnabled(EnableCap.ClipDistance0);

            Texture2D = new uint[textureUnits];
            Texture2DArray = new uint[textureUnits];
            for (int unit = 0; unit < textureUnits; unit++)
            {
                gl.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + unit));
                gl.GetInteger(GLEnum.TextureBinding2D, values); Texture2D[unit] = (uint)values[0];
                gl.GetInteger(GLEnum.TextureBinding2DArray, values); Texture2DArray[unit] = (uint)values[0];
            }
            gl.ActiveTexture(ActiveTexture);
        }

        public void Restore(GL gl)
        {
            gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, ReadFramebuffer);
            gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, DrawFramebuffer);
            gl.Viewport(Viewport[0], Viewport[1], (uint)Viewport[2], (uint)Viewport[3]);
            gl.Scissor(ScissorBox[0], ScissorBox[1], (uint)ScissorBox[2], (uint)ScissorBox[3]);
            gl.UseProgram(Program);
            gl.BindVertexArray(VertexArray);
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, ArrayBuffer);
            for (int unit = 0; unit < Texture2D.Length; unit++)
            {
                gl.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + unit));
                gl.BindTexture(TextureTarget.Texture2D, Texture2D[unit]);
                gl.BindTexture(TextureTarget.Texture2DArray, Texture2DArray[unit]);
            }
            gl.ActiveTexture(ActiveTexture);
            gl.BindTexture(TextureTarget.Texture2D, ActiveTexture2D);
            gl.BindTexture(TextureTarget.Texture2DArray, ActiveTexture2DArray);
            gl.ColorMask(ColorMask[0], ColorMask[1], ColorMask[2], ColorMask[3]);
            gl.ClearColor(ClearColor.X, ClearColor.Y, ClearColor.Z, ClearColor.W);
            gl.DepthMask(DepthMask);
            gl.DepthFunc(DepthFunction);
            gl.CullFace(CullFaceMode);
            gl.FrontFace(FrontFace);
            gl.BlendFuncSeparate(BlendSrcRgb, BlendDstRgb, BlendSrcAlpha, BlendDstAlpha);
            Set(gl, EnableCap.DepthTest, DepthTest);
            Set(gl, EnableCap.CullFace, CullFace);
            Set(gl, EnableCap.Blend, Blend);
            Set(gl, EnableCap.ScissorTest, ScissorTest);
            Set(gl, EnableCap.FramebufferSrgb, FramebufferSrgb);
            Set(gl, EnableCap.ClipDistance0, ClipDistance0);
        }

        private static void Set(GL gl, EnableCap cap, bool enabled)
        {
            if (enabled) gl.Enable(cap); else gl.Disable(cap);
        }
    }

    // Terrain uses 0..2 and water uses 0..4. Preserve every texture binding the
    // candidate renderers are allowed to mutate, plus the previously active unit.
    private const int TouchedTextureUnits = 5;
    private readonly GL _gl;
    private readonly int _ownerThread;
    private Slot? _front;
    private Slot? _back;
    private nint _pendingFence;
    private GlState _savedState;
    private bool _rendering;
    private bool _disposed;
    private int _desiredWidth;
    private int _desiredHeight;

    public PortalRenderTarget(GL gl, int width = 768, int height = 768)
    {
        _gl = gl;
        _ownerThread = Environment.CurrentManagedThreadId;
        _desiredWidth = Math.Max(64, width);
        _desiredHeight = Math.Max(64, height);
        try
        {
            _front = CreateSlot(_desiredWidth, _desiredHeight);
            _back = CreateSlot(_desiredWidth, _desiredHeight);
        }
        catch
        {
            // If the second slot fails, the constructor never publishes this
            // object to its caller, so it must retire the first slot itself.
            DeleteSlot(_front);
            DeleteSlot(_back);
            _front = _back = null;
            throw;
        }
    }

    public uint Texture => HasCompleteFrame ? _front!.Texture : 0;
    public bool HasCompleteFrame { get; private set; }
    public bool HasPendingFrame => _pendingFence != 0;
    public bool CanRetire => !_rendering && _pendingFence == 0;
    public int Width => HasCompleteFrame ? _front!.Width : _desiredWidth;
    public int Height => HasCompleteFrame ? _front!.Height : _desiredHeight;
    public int DesiredWidth => _desiredWidth;
    public int DesiredHeight => _desiredHeight;

    public void Resize(int width, int height)
    {
        EnsureOwnerThread();
        ThrowIfDisposed();
        int desiredWidth = Math.Max(64, width);
        int desiredHeight = Math.Max(64, height);
        if (_desiredWidth == desiredWidth && _desiredHeight == desiredHeight) return;
        _desiredWidth = desiredWidth;
        _desiredHeight = desiredHeight;
        // Never stretch a completed frame from a different aspect/size. A
        // pending old-size frame may still retire normally, but PollComplete
        // will not publish it as current below.
        HasCompleteFrame = false;
    }

    public void Invalidate()
    {
        EnsureOwnerThread();
        HasCompleteFrame = false;
    }

    /// <summary>Poll the GPU fence without waiting and publish a complete back frame.</summary>
    public bool PollComplete()
    {
        EnsureOwnerThread();
        if (_pendingFence == 0) return false;
        GLEnum status = _gl.ClientWaitSync(_pendingFence, (SyncObjectMask)0, 0);
        if (status == GLEnum.WaitFailed)
        {
            _gl.DeleteSync(_pendingFence);
            _pendingFence = 0;
            throw new InvalidOperationException("OpenGL portal-preview fence wait failed");
        }
        if (status is not (GLEnum.AlreadySignaled or GLEnum.ConditionSatisfied)) return false;

        _gl.DeleteSync(_pendingFence);
        _pendingFence = 0;
        (_front, _back) = (_back, _front);
        HasCompleteFrame = _front!.Width == _desiredWidth && _front.Height == _desiredHeight;
        return true;
    }

    /// <summary>
    /// Bind a private back target and establish the opaque-world raster baseline.
    /// Returns false while the prior frame is still fenced.
    /// </summary>
    public void Begin(Vector4 clearColor)
    {
        EnsureOwnerThread();
        ThrowIfDisposed();
        if (_rendering) throw new InvalidOperationException("Portal render target is already active");
        if (_pendingFence != 0)
            throw new InvalidOperationException("Portal render target back buffer is still in flight");

        EnsureBackSize();
        _savedState = new GlState(_gl, TouchedTextureUnits);
        _rendering = true;
        try
        {
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _back!.Framebuffer);
            _gl.Viewport(0, 0, (uint)_back.Width, (uint)_back.Height);
            _gl.Disable(EnableCap.ScissorTest);
            _gl.Disable(EnableCap.Blend);
            _gl.Disable(EnableCap.FramebufferSrgb);
            _gl.Disable(EnableCap.ClipDistance0);
            _gl.Enable(EnableCap.DepthTest);
            _gl.Enable(EnableCap.CullFace);
            _gl.ColorMask(true, true, true, true);
            _gl.DepthMask(true);
            _gl.DepthFunc(DepthFunction.Lequal);
            _gl.CullFace(TriangleFace.Back);
            _gl.FrontFace(FrontFaceDirection.Ccw);
            _gl.ClearColor(clearColor.X, clearColor.Y, clearColor.Z, clearColor.W);
            _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        }
        catch
        {
            _rendering = false;
            _savedState.Restore(_gl);
            throw;
        }
    }

    /// <summary>Fence a successful frame, then restore the caller's GL context state.</summary>
    public void End(bool publish)
    {
        EnsureOwnerThread();
        if (!_rendering) return;
        try
        {
            if (publish)
            {
                _pendingFence = _gl.FenceSync(
                    SyncCondition.SyncGpuCommandsComplete, (SyncBehaviorFlags)0);
                _gl.Flush();
            }
        }
        finally
        {
            _savedState.Restore(_gl);
            _rendering = false;
        }
    }

    private unsafe Slot CreateSlot(int width, int height)
    {
        int* previous = stackalloc int[4];
        _gl.GetInteger(GLEnum.DrawFramebufferBinding, previous); uint previousDraw = (uint)previous[0];
        _gl.GetInteger(GLEnum.ReadFramebufferBinding, previous); uint previousRead = (uint)previous[0];
        _gl.GetInteger(GLEnum.RenderbufferBinding, previous); uint previousRenderbuffer = (uint)previous[0];
        _gl.GetInteger(GLEnum.TextureBinding2D, previous); uint previousTexture2D = (uint)previous[0];
        var slot = new Slot { Width = width, Height = height };
        try
        {
            slot.Texture = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, slot.Texture);
            _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8,
                (uint)width, (uint)height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, null);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);

            slot.Depth = _gl.GenRenderbuffer();
            _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, slot.Depth);
            _gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.DepthComponent24,
                (uint)width, (uint)height);

            slot.Framebuffer = _gl.GenFramebuffer();
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, slot.Framebuffer);
            _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer,
                FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, slot.Texture, 0);
            _gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer,
                FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, slot.Depth);
            GLEnum status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
            if (status != GLEnum.FramebufferComplete)
                throw new InvalidOperationException($"Portal framebuffer is incomplete: {status}");
            return slot;
        }
        catch
        {
            DeleteSlot(slot);
            throw;
        }
        finally
        {
            _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, previousRead);
            _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, previousDraw);
            _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, previousRenderbuffer);
            _gl.BindTexture(TextureTarget.Texture2D, previousTexture2D);
        }
    }

    private void EnsureBackSize()
    {
        if (_back is not null && _back.Width == _desiredWidth && _back.Height == _desiredHeight)
            return;
        DeleteSlot(_back);
        _back = CreateSlot(_desiredWidth, _desiredHeight);
    }

    private void DeleteSlot(Slot? slot)
    {
        if (slot is null) return;
        if (slot.Depth != 0) _gl.DeleteRenderbuffer(slot.Depth);
        if (slot.Framebuffer != 0) _gl.DeleteFramebuffer(slot.Framebuffer);
        if (slot.Texture != 0) _gl.DeleteTexture(slot.Texture);
    }

    public void Dispose()
    {
        EnsureOwnerThread();
        if (_disposed) return;
        if (_rendering) End(publish: false);
        if (_pendingFence != 0)
        {
            _gl.DeleteSync(_pendingFence);
            _pendingFence = 0;
        }
        DeleteSlot(_front);
        DeleteSlot(_back);
        _front = _back = null;
        HasCompleteFrame = false;
        _disposed = true;
    }

    private void EnsureOwnerThread()
    {
        if (Environment.CurrentManagedThreadId != _ownerThread)
            throw new InvalidOperationException("Portal render targets must be used on their owning GL thread");
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

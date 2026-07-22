using System.Numerics;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ImGui;
using Silk.NET.Windowing;
using ImGuiNET;

namespace MSUIClient.Engine;

/// <summary>
/// Window, GL context, main loop, input and the debug HUD.
///
/// Deliberately thin: Silk.NET is a binding layer, not an engine, so this owns
/// the loop rather than plugging into someone else's. That is what makes the
/// painterly render mode a shader variant instead of a fight with a framework.
///
/// MOUSE LOOK IS POLLED, NOT PURELY EVENT DRIVEN
///   The original version drove capture entirely from MouseDown and MouseUp and
///   flipped the cursor to CursorMode.Raw on the way in. Three things can each
///   kill that outright, and none of them announces itself:
///
///     1. Raw cursor mode is not universally supported. Setting it can throw, or
///        silently fail, and on some drivers it stops the position callback
///        reporting usable coordinates at all - so MouseMove fires and every
///        delta is zero.
///     2. Switching to Raw or Disabled makes the reported cursor position jump
///        into a different coordinate space. The first delta after capture is
///        then nonsense, large enough to spin the view somewhere arbitrary.
///     3. A MouseUp delivered while ImGui owned the mouse, or with the pointer
///        outside the window, is simply never seen, so capture sticks on or off.
///
///   So capture is now derived from POLLING the button state every frame, with
///   the events kept only for the motion delta. The first delta after capture is
///   discarded, oversized deltas are dropped, and the cursor mode falls back
///   from Raw to Hidden if the platform refuses it.
///
///   Every one of those states is published for the HUD - see the mouse
///   diagnostics below. "The mouse does nothing" should be a number, not a
///   theory.
///
/// LEFT AND RIGHT DRAG MEAN DIFFERENT THINGS
///   Left swings the camera around the character without turning him. Right
///   turns him and takes the camera along. See Camera.OrbitYaw - the separation
///   lives there, and this class only decides which of the two a given drag
///   feeds.
/// </summary>
public sealed class ClientWindow : IDisposable
{
    private readonly ClientConfig _config;

    private IWindow _window = null!;
    private GL _gl = null!;
    private IInputContext _input = null!;
    private ImGuiController _imgui = null!;
    private IMouse? _mouse;

    public GL Gl => _gl;
    public Camera Camera { get; } = new();

    /// <summary>Raised once after the GL context exists — build GPU resources here.</summary>
    public event Action<GL>? OnLoad;

    /// <summary>Raised every frame before rendering. Argument is delta seconds.</summary>
    public event Action<float>? OnUpdate;

    /// <summary>Raised every frame to draw the world.</summary>
    public event Action<float>? OnRender;

    /// <summary>Raised inside the ImGui frame — draw HUD windows here.</summary>
    public event Action? OnGui;

    // Input state
    private readonly HashSet<Key> _held = [];
    private Vector2 _lastMouse;
    private bool _mouseCaptured;
    private bool _skipNextDelta;
    private float _pendingYaw, _pendingOrbitYaw, _pendingPitch, _pendingZoom;
    private bool _previousRightDown;

    /// <summary>
    /// Which button owns the current drag, and therefore what horizontal motion
    /// means.
    ///
    ///   LEFT  swings the camera around the character. He keeps facing where he
    ///         was, so you can walk north and look at your own face.
    ///   RIGHT turns the character, and the camera comes with him.
    ///
    /// Right wins when both are held, because turning is the stronger intent.
    /// </summary>
    private bool _lookTurnsCharacter;

    /// <summary>
    /// A single mouse-move event larger than this is discarded rather than
    /// applied. It is not a real hand movement; it is the cursor changing
    /// coordinate space, and applying it throws the view somewhere random.
    /// </summary>
    private const float MaxDeltaPixels = 300f;

    // Frame timing
    private double _fpsAccumulator;
    private int _framesSinceSample;
    public float Fps { get; private set; }
    public double FrameMs { get; private set; }

    // ── mouse diagnostics, all published for the HUD ─────────────────────────

    /// <summary>True while the mouse is captured for camera look.</summary>
    public bool MouseCaptured => _mouseCaptured;

    public bool MouseLeftDown { get; private set; }
    public bool MouseRightDown { get; private set; }

    /// <summary>Motion events seen since start. Frozen means no events arrive at all.</summary>
    public int MouseMoveEvents { get; private set; }

    /// <summary>Motion events actually applied to the camera. The gap is what was rejected.</summary>
    public int MouseLookEvents { get; private set; }

    /// <summary>Last accepted delta in pixels. Zero while moving means the delta is the problem.</summary>
    public Vector2 LastMouseDelta { get; private set; }

    /// <summary>What the cursor mode actually ended up as, which is not always what was asked for.</summary>
    public string CursorModeName { get; private set; } = "Normal";

    /// <summary>
    /// Raw cursor mode: unbounded look, cursor hidden and locked. Turn it OFF if
    /// look is dead - Hidden keeps the cursor in normal screen coordinates, which
    /// works everywhere but stops at the screen edge.
    /// </summary>
    public bool RawCursor { get; set; } = true;

    /// <summary>Multiplier on camera.mouseSensitivity, so a too-slow look is one drag away from fixed.</summary>
    public float MouseSensitivity { get; set; } = 1f;

    public ClientWindow(ClientConfig config) => _config = config;

    public void Run()
    {
        var options = WindowOptions.Default with
        {
            Size = new Vector2D<int>(_config.Window.Width, _config.Window.Height),
            Title = _config.Window.Title,
            VSync = _config.Window.VSync,
            // 3.3 core is the floor; anything modern exceeds it, and it keeps the
            // client runnable on older hardware and inside VMs.
            API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.Default,
                                  new APIVersion(3, 3)),
        };

        _window = Window.Create(options);

        _window.Load += HandleLoad;
        _window.Update += dt => HandleUpdate((float)dt);
        _window.Render += dt => HandleRender((float)dt);
        _window.FramebufferResize += HandleResize;
        _window.Closing += HandleClosing;

        _window.Run();
    }

    private void HandleLoad()
    {
        _gl = _window.CreateOpenGL();
        _input = _window.CreateInput();
        _imgui = new ImGuiController(_gl, _window, _input);

        // ImGui's default font is unreadably small on a high-DPI panel. Scale
        // the font and every widget metric together so the HUD stays legible.
        var scale = Math.Clamp(_config.Window.UiScale, 0.5f, 4f);
        if (Math.Abs(scale - 1f) > 0.01f)
        {
            ImGui.GetIO().FontGlobalScale = scale;
            ImGui.GetStyle().ScaleAllSizes(scale);
            Console.WriteLine($"[ui] scale {scale:F2}");
        }

        Console.WriteLine($"[gl] {_gl.GetStringS(StringName.Renderer)}");
        Console.WriteLine($"[gl] {_gl.GetStringS(StringName.Version)}");

        foreach (var kb in _input.Keyboards)
        {
            kb.KeyDown += (_, key, _) => _held.Add(key);
            kb.KeyUp += (_, key, _) => _held.Remove(key);
        }

        _mouse = _input.Mice.Count > 0 ? _input.Mice[0] : null;
        Console.WriteLine($"[input] {_input.Keyboards.Count} keyboard(s), {_input.Mice.Count} mouse/mice");

        foreach (var mouse in _input.Mice)
        {
            // Down and up are kept only so a press registers on the same frame it
            // happens. The polling pass in HandleUpdate is what actually decides
            // whether look is engaged, so a missed event cannot strand it.
            mouse.MouseDown += (m, btn) =>
            {
                if (ImGui.GetIO().WantCaptureMouse) return;
                if (btn is MouseButton.Right or MouseButton.Left) BeginLook(m);
            };

            mouse.MouseUp += (m, btn) =>
            {
                if (btn is MouseButton.Right or MouseButton.Left) EndLook(m);
            };

            mouse.MouseMove += (_, pos) =>
            {
                MouseMoveEvents++;

                if (!_mouseCaptured) { _lastMouse = pos; return; }

                var delta = pos - _lastMouse;
                _lastMouse = pos;

                // The frame capture begins, the cursor changes mode and the
                // reported position moves with it. That first delta describes
                // the mode change, not the hand.
                if (_skipNextDelta) { _skipNextDelta = false; return; }

                if (MathF.Abs(delta.X) > MaxDeltaPixels || MathF.Abs(delta.Y) > MaxDeltaPixels) return;
                if (delta.X == 0f && delta.Y == 0f) return;

                LastMouseDelta = delta;
                MouseLookEvents++;

                float sensitivity = _config.Camera.MouseSensitivity * MouseSensitivity;

                // The one line that separates the two drag modes. Pitch is
                // shared - looking up and down is a camera thing either way.
                if (_lookTurnsCharacter) _pendingYaw -= delta.X * sensitivity;
                else _pendingOrbitYaw -= delta.X * sensitivity;

                // Screen Y grows DOWNWARD, and Camera.Pitch is elevation ABOVE
                // the target. So standard behaviour — push the mouse up, look up
                // — needs the delta ADDED: looking up drops the camera below the
                // target, which is a smaller pitch. Subtracting here inverts the
                // vertical axis, which is what this used to do.
                float pitchSign = _config.Camera.InvertPitch ? -1f : 1f;
                _pendingPitch += delta.Y * sensitivity * pitchSign;
            };

            mouse.Scroll += (_, wheel) =>
            {
                if (ImGui.GetIO().WantCaptureMouse) return;
                _pendingZoom += wheel.Y;
            };
        }

        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthFunc(DepthFunction.Lequal);
        _gl.Enable(EnableCap.CullFace);
        _gl.CullFace(TriangleFace.Back);
        // WoW is Z-up and its terrain winds counter-clockwise seen from above.
        _gl.FrontFace(FrontFaceDirection.Ccw);

        Camera.FieldOfViewDegrees = _config.Render.FieldOfView;
        Camera.NearPlane = _config.Render.NearPlane;
        Camera.FarPlane = _config.Render.FarPlane;
        Camera.AspectRatio = (float)_config.Window.Width / _config.Window.Height;

        OnLoad?.Invoke(_gl);
    }

    // ── mouse capture ────────────────────────────────────────────────────────

    private void BeginLook(IMouse mouse)
    {
        if (_mouseCaptured) return;

        _mouseCaptured = true;
        _lastMouse = mouse.Position;
        _skipNextDelta = true;

        ApplyCursorMode(mouse);
    }

    private void EndLook(IMouse mouse)
    {
        if (!_mouseCaptured) return;

        _mouseCaptured = false;
        SetCursorMode(mouse, CursorMode.Normal);
    }

    /// <summary>
    /// Raw is the mode a game wants: the cursor is locked and motion is
    /// unbounded, so you can keep turning past the edge of the screen. It is
    /// also the mode most likely to be refused, and a refusal that is swallowed
    /// looks exactly like broken camera code. Fall back and say so.
    /// </summary>
    private void ApplyCursorMode(IMouse mouse)
    {
        if (RawCursor && SetCursorMode(mouse, CursorMode.Raw)) return;

        if (RawCursor)
        {
            RawCursor = false;
            Console.WriteLine("[input] raw cursor refused by the platform - falling back to Hidden. " +
                              "Look will stop at the edge of the screen.");
        }

        if (!SetCursorMode(mouse, CursorMode.Hidden))
            SetCursorMode(mouse, CursorMode.Normal);
    }

    private bool SetCursorMode(IMouse mouse, CursorMode mode)
    {
        try
        {
            mouse.Cursor.CursorMode = mode;
            CursorModeName = mode.ToString();
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[input] cursor mode {mode} failed: {ex.Message}");
            return false;
        }
    }

    private void PollMouse()
    {
        if (_mouse is null) return;

        MouseLeftDown = _mouse.IsButtonPressed(MouseButton.Left);
        MouseRightDown = _mouse.IsButtonPressed(MouseButton.Right);

        // Pressing the right button turns the character to wherever the camera
        // has been swung, without the view moving. Done on the TRANSITION, so a
        // held right button does not keep re-folding a zero offset.
        if (MouseRightDown && !_previousRightDown) Camera.FoldOrbitIntoFacing();
        _previousRightDown = MouseRightDown;

        _lookTurnsCharacter = MouseRightDown;

        bool anyButton = MouseLeftDown || MouseRightDown;

        // Engage from polling as well as from the event. If MouseDown was
        // swallowed - ImGui claiming the mouse on the wrong frame is the usual
        // culprit - this still starts the look.
        if (anyButton && !_mouseCaptured && !ImGui.GetIO().WantCaptureMouse)
            BeginLook(_mouse);

        // And release from polling, so a MouseUp that never arrived cannot leave
        // the cursor hidden and the view spinning.
        if (!anyButton && _mouseCaptured)
            EndLook(_mouse);
    }

    private void HandleUpdate(float dt)
    {
        // Clamp so an alt-tab or a breakpoint doesn't teleport anything.
        dt = MathF.Min(dt, 0.05f);

        PollMouse();

        Camera.Rotate(_pendingYaw, _pendingPitch);
        Camera.RotateView(_pendingOrbitYaw);
        if (_pendingZoom != 0) Camera.Zoom(_pendingZoom);
        _pendingYaw = _pendingOrbitYaw = _pendingPitch = _pendingZoom = 0;

        OnUpdate?.Invoke(dt);
    }

    private void HandleRender(float dt)
    {
        _fpsAccumulator += dt;
        _framesSinceSample++;
        if (_fpsAccumulator >= 0.5)
        {
            Fps = (float)(_framesSinceSample / _fpsAccumulator);
            FrameMs = _fpsAccumulator * 1000.0 / _framesSinceSample;
            _fpsAccumulator = 0;
            _framesSinceSample = 0;
        }

        _imgui.Update(dt);

        // Sky colour; the painterly pass will replace this with a gradient.
        _gl.ClearColor(0.56f, 0.71f, 0.85f, 1f);
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        OnRender?.Invoke(dt);

        OnGui?.Invoke();
        _imgui.Render();
    }

    private void HandleResize(Vector2D<int> size)
    {
        if (size.X <= 0 || size.Y <= 0) return;
        _gl.Viewport(size);
        Camera.AspectRatio = (float)size.X / size.Y;
    }

    private void HandleClosing()
    {
        _imgui.Dispose();
        _input.Dispose();
        _gl.Dispose();
    }

    // ── input queries ─────────────────────────────────────────────────────────

    public bool IsDown(Key key) => _held.Contains(key);

    public float Axis(Key positive, Key negative)
        => (_held.Contains(positive) ? 1f : 0f) - (_held.Contains(negative) ? 1f : 0f);

    public void Close() => _window.Close();

    public void Dispose() => _window?.Dispose();
}

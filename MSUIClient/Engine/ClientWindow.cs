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
/// </summary>
public sealed class ClientWindow : IDisposable
{
    private readonly ClientConfig _config;

    private IWindow _window = null!;
    private GL _gl = null!;
    private IInputContext _input = null!;
    private ImGuiController _imgui = null!;

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
    private float _pendingYaw, _pendingPitch, _pendingZoom;

    // Frame timing
    private double _fpsAccumulator;
    private int _framesSinceSample;
    public float Fps { get; private set; }
    public double FrameMs { get; private set; }

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

        foreach (var mouse in _input.Mice)
        {
            mouse.MouseDown += (m, btn) =>
            {
                if (ImGui.GetIO().WantCaptureMouse) return;
                if (btn is MouseButton.Right or MouseButton.Left)
                {
                    _mouseCaptured = true;
                    _lastMouse = m.Position;
                    m.Cursor.CursorMode = CursorMode.Raw;
                }
            };

            mouse.MouseUp += (m, btn) =>
            {
                if (btn is MouseButton.Right or MouseButton.Left)
                {
                    _mouseCaptured = false;
                    m.Cursor.CursorMode = CursorMode.Normal;
                }
            };

            mouse.MouseMove += (_, pos) =>
            {
                if (!_mouseCaptured) { _lastMouse = pos; return; }
                var delta = pos - _lastMouse;
                _lastMouse = pos;

                float sensitivity = _config.Camera.MouseSensitivity;

                _pendingYaw -= delta.X * sensitivity;

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

    private void HandleUpdate(float dt)
    {
        // Clamp so an alt-tab or a breakpoint doesn't teleport anything.
        dt = MathF.Min(dt, 0.05f);

        Camera.Rotate(_pendingYaw, _pendingPitch);
        if (_pendingZoom != 0) Camera.Zoom(_pendingZoom);
        _pendingYaw = _pendingPitch = _pendingZoom = 0;

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

    /// <summary>True while the mouse is captured for camera look.</summary>
    public bool MouseCaptured => _mouseCaptured;

    public void Close() => _window.Close();

    public void Dispose() => _window?.Dispose();
}

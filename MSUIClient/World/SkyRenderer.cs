using System.Numerics;
using Silk.NET.OpenGL;
using MSUIClient.Engine;
using Shader = MSUIClient.Engine.Shader;

namespace MSUIClient.World;

/// <summary>
/// The sky (PLAN_09 D2). Five authored colour bands from LightIntBand 2..6,
/// blended by view elevation across a fullscreen triangle.
///
/// WHY THIS EXISTS AT ALL: until now the "sky" was glClearColor set to the fog
/// colour - one flat tone with no gradient, no horizon and no zenith. That was a
/// deliberate trade (ClientWindow: "sky colour matches the far fog so the
/// visibility boundary disappears into aerial perspective"), and it is the
/// reason Nico could not judge fog: vanilla's fog colour IS a sky band, and fog
/// dissolves distant terrain INTO the sky. Against a flat sky, correct and
/// incorrect fog look identical.
///
/// No dome mesh. The sky is a function of direction, not of position, so a
/// screen-space pass gets it exactly right at any FOV and any orientation, with
/// no vertices to build, cull, or get wrong at the poles.
/// </summary>
public sealed class SkyRenderer : IDisposable
{
    private readonly GL _gl;
    private Shader? _shader;
    private uint _vao;

    public bool Enabled { get; set; } = true;

    // Where each band sits, as a view-elevation 0..1 (0 horizon, 1 zenith).
    //
    // ONLY THE COLOURS ARE AUTHORED. LightIntBand gives five colours and says
    // nothing about the heights they sit at, so these are ours and they are
    // sliders, not constants pretending to be data. Defaults chosen so the
    // gradient reads like vanilla: most of the change happens near the horizon.
    public float StopMiddle { get; set; } = 0.45f;
    public float StopBand1 { get; set; } = 0.18f;
    public float StopBand2 { get; set; } = 0.06f;

    public SkyRenderer(GL gl) => _gl = gl;

    public void LoadShaders(string shaderDir)
    {
        _shader = Shader.FromFiles(_gl,
            Path.Combine(shaderDir, "sky.vert"),
            Path.Combine(shaderDir, "sky.frag"));

        // An empty VAO is still required in a core profile, even when the
        // vertex shader reads nothing but gl_VertexID.
        _vao = _gl.GenVertexArray();
    }

    /// <summary>
    /// Draw the sky. Must run BEFORE the world, with depth writes off so it
    /// never occludes anything.
    /// </summary>
    public void Render(Camera camera, WorldAtmosphere atmosphere)
    {
        if (!Enabled || _shader is null) return;

        // Camera basis, built here rather than inverting a matrix in the shader:
        // Camera already exposes Forward, and the other two follow from it. One
        // less place for a transpose convention to go wrong.
        var forward = camera.Forward;
        var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitZ));
        var up = Vector3.Cross(right, forward);

        _gl.Disable(EnableCap.DepthTest);
        _gl.DepthMask(false);

        _shader.Use();
        _shader.Set("uForward", forward);
        _shader.Set("uRight", right);
        _shader.Set("uUp", up);
        _shader.Set("uTanHalfFov",
            MathF.Tan(camera.FieldOfViewDegrees * MathF.PI / 180f * 0.5f));
        _shader.Set("uAspect", camera.AspectRatio);

        _shader.Set("uSkyTop", atmosphere.SkyTop);
        _shader.Set("uSkyMiddle", atmosphere.SkyMiddle);
        _shader.Set("uSkyBand1", atmosphere.SkyBand1);
        _shader.Set("uSkyBand2", atmosphere.SkyBand2);
        _shader.Set("uSkySmog", atmosphere.SkySmog);

        // Kept ordered, so dragging one slider past another cannot invert a
        // band and produce a stripe nobody can explain.
        float band2 = Math.Clamp(StopBand2, 0.001f, 0.9f);
        float band1 = Math.Clamp(StopBand1, band2 + 0.001f, 0.95f);
        float middle = Math.Clamp(StopMiddle, band1 + 0.001f, 0.99f);
        _shader.Set("uStopMiddle", middle);
        _shader.Set("uStopBand1", band1);
        _shader.Set("uStopBand2", band2);

        _gl.BindVertexArray(_vao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        _gl.BindVertexArray(0);

        _gl.DepthMask(true);
        _gl.Enable(EnableCap.DepthTest);
    }

    public void Dispose()
    {
        if (_vao != 0) _gl.DeleteVertexArray(_vao);
        _shader?.Dispose();
    }
}

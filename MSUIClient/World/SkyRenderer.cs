using System.Diagnostics;
using System.Numerics;
using Silk.NET.OpenGL;
using MSUIClient.Engine;
using Shader = MSUIClient.Engine.Shader;

namespace MSUIClient.World;

/// <summary>
/// The sky (PLAN_09 D2 + PLAN_18 clouds). Five authored colour bands from
/// LightIntBand 2..6, blended by view elevation across a fullscreen triangle,
/// with the procedural cloud layer (<see cref="CloudField"/>) composited over it.
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
/// no vertices to build, cull, or get wrong at the poles. The clouds ride the
/// SAME pass: the CloudField kernel colours a 128x128 coverage tile CPU-side
/// (the reference's 0x6cfb00 byte math), and sky.frag samples it by the
/// azimuthal projection of the view ray (CloudField.ProjectCells, ported to
/// GLSL) - benilla renders the identical tile on a camera-centred dome, this
/// samples it in screen space so the no-dome property holds for the clouds too.
/// </summary>
public sealed class SkyRenderer : IDisposable
{
    private readonly GL _gl;
    private Shader? _shader;
    private uint _vao;

    public bool Enabled { get; set; } = true;

    /// <summary>Draw the authored cloud layer over the gradient. Dev toggle (light probe).</summary>
    public bool CloudsEnabled { get; set; } = true;

    /// <summary>
    /// Dev override for the authored cloud density C (light probe slider). Null =
    /// use the resolved band. 0 clears the sky, 1 fills it - both are legal.
    /// </summary>
    public float? CloudDensityOverride { get; set; }

    /// <summary>The cloud coverage field, exposed so the probe can read its state.</summary>
    public CloudField? Clouds { get; private set; }

    // Where each band sits, as a view-elevation 0..1 (0 horizon, 1 zenith).
    //
    // ONLY THE COLOURS ARE AUTHORED. LightIntBand gives five colours and says
    // nothing about the heights they sit at, so these are ours and they are
    // sliders, not constants pretending to be data. Defaults chosen so the
    // gradient reads like vanilla: most of the change happens near the horizon.
    public float StopMiddle { get; set; } = 0.45f;
    public float StopBand1 { get; set; } = 0.18f;
    public float StopBand2 { get; set; } = 0.06f;

    // ── Cloud layer state ──────────────────────────────────────────────────────
    private uint _cloudTex;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastMs;

    public SkyRenderer(GL gl) => _gl = gl;

    public void LoadShaders(string shaderDir)
    {
        _shader = Shader.FromFiles(_gl,
            Path.Combine(shaderDir, "sky.vert"),
            Path.Combine(shaderDir, "sky.frag"));

        // An empty VAO is still required in a core profile, even when the
        // vertex shader reads nothing but gl_VertexID.
        _vao = _gl.GenVertexArray();

        Clouds = new CloudField();
        CreateCloudTexture();
    }

    private unsafe void CreateCloudTexture()
    {
        _cloudTex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _cloudTex);
        // NON-sRGB: the CloudField texels are the reference's gamma bytes, sampled raw
        // and composited over the (gamma) sky bands, so no sRGB decode on the way in.
        _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8,
            CloudField.Cols, CloudField.Cols, 0, PixelFormat.Rgba, PixelType.UnsignedByte, null);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        // Toroidal tile - the projection can land uv just past [0,1] at the rim.
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.Repeat);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.Repeat);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
    }

    /// <summary>
    /// Draw the sky. Must run BEFORE the world, with depth writes off so it
    /// never occludes anything.
    /// </summary>
    public void Render(Camera camera, WorldAtmosphere atmosphere, float stormBlend = 0f)
    {
        if (!Enabled || _shader is null) return;

        double now = _clock.Elapsed.TotalMilliseconds;
        float dt = (float)((now - _lastMs) / 1000.0);
        _lastMs = now;
        if (dt is <= 0f or >= 1f) dt = 0f;   // hitch / first frame: advance no cloud time

        bool drawClouds = CloudsEnabled && Clouds is not null && atmosphere.AuthoredCloudsReady;
        if (drawClouds) UpdateCloudField(dt, atmosphere, stormBlend);

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

        _shader.Set("uCloudEnabled", drawClouds ? 1 : 0);
        if (drawClouds)
        {
            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, _cloudTex);
            _shader.Set("uCloudTex", 0);
        }

        _gl.BindVertexArray(_vao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        _gl.BindVertexArray(0);

        _gl.DepthMask(true);
        _gl.Enable(EnableCap.DepthTest);
    }

    // Tick the coverage field and re-upload the colored tile when it changed. The
    // frame's colour inputs are the resolved cloud bands; the glow points at the
    // sun (day only - the moon glow is a later addition), converted from world
    // Z-up to the kernel's tile frame (+Y up): tile = (x, z, y).
    private unsafe void UpdateCloudField(float dt, WorldAtmosphere atmosphere, float stormBlend)
    {
        var field = Clouds!;
        float density = Math.Clamp(CloudDensityOverride ?? atmosphere.CloudDensity, 0f, 1f);

        float hours = atmosphere.TimeOfDayHours;
        bool sunGlow = CloudField.GlowIsSun(hours);
        Vector3 sun = atmosphere.SunDirection;
        var frame = new CloudField.CloudFrame
        {
            Sun = atmosphere.CloudSunGlow,
            Slope = atmosphere.CloudSlope,
            GBase = atmosphere.CloudBase,
            Bcc = Math.Clamp(stormBlend, 0f, 1f),
            GlowDir = Vector3.Normalize(new Vector3(sun.X, sun.Z, sun.Y)),
            GlowTrack = sunGlow ? CloudField.GlowTrack(hours) : 0f,
        };

        bool changed = field.Primed
            ? field.Tick(dt, density, frame)
            : Prime(field, density, frame);

        if (!changed) return;

        _gl.BindTexture(TextureTarget.Texture2D, _cloudTex);
        fixed (byte* p = field.Rgba)
            _gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0,
                CloudField.Cols, CloudField.Cols, PixelFormat.Rgba, PixelType.UnsignedByte, p);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
    }

    private static bool Prime(CloudField field, float density, in CloudField.CloudFrame frame)
    {
        field.Rebuild(density, frame);
        return true;
    }

    public void Dispose()
    {
        if (_cloudTex != 0) _gl.DeleteTexture(_cloudTex);
        if (_vao != 0) _gl.DeleteVertexArray(_vao);
        _shader?.Dispose();
    }
}

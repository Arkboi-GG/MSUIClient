using System.Numerics;
using Silk.NET.OpenGL;
using MSUIClient.Engine;
using Shader = MSUIClient.Engine.Shader;

namespace MSUIClient.World.Collision;

/// <summary>
/// Draws the collision world as wireframe, over the top of everything else.
///
/// WHY THIS EXISTS
///   Collision geometry is invisible by definition, so every question about it
///   — is it in the right place, is it the right shape, is the surface I just
///   walked onto the one I can see — turns into inference from symptoms. That
///   inference is slow and frequently wrong. Drawing the triangles answers all
///   of it at a glance: stand at the abbey steps, switch this on, and either
///   the wireframe lies on the steps or it does not.
///
///   Green is standable (normal Z above the controller's slope limit), red is
///   wall. Those are the same numbers ResolveGround and MoveHorizontal test
///   against, so what you see is literally what the character is deciding on.
///
/// COST
///   One vertex per triangle corner, positions duplicated because flat shading
///   needs them: about 58 MB for a 3x3 block of Elwynn. Built once, drawn with
///   polygon mode LINE. Off by default; this is a diagnostic, not a feature.
/// </summary>
public sealed class CollisionDebugRenderer : IDisposable
{
    /// <summary>Position(3) + absolute normal Z(1) + source id(1).</summary>
    private const int FloatsPerVertex = 5;

    private readonly GL _gl;

    private Shader? _shader;
    private uint _vao, _vbo;
    private int _vertexCount;

    // Small dynamic buffer for highlighted triangles, rebuilt per frame.
    private uint _hiVao, _hiVbo;
    private bool _hiReady;

    public bool Enabled { get; set; }

    /// <summary>Draw filled instead of wireframe. Filled reads better at distance.</summary>
    public bool Solid { get; set; }

    /// <summary>Triangles beyond this from the camera are not worth the fill rate.</summary>
    public float FadeStart { get; set; } = 60f;
    public float FadeEnd { get; set; } = 250f;

    /// <summary>
    /// Draw only the triangles belonging to this source, or -1 for all.
    ///
    /// A million triangles of wireframe is not a diagnostic, it is a mess. One
    /// building's shell on its own, next to that same building rendered, is a
    /// single glance.
    /// </summary>
    public int SourceFilter { get; set; } = -1;

    public int TriangleCount => _vertexCount / 3;

    /// <summary>Discard an upload whose collision world is no longer resident.</summary>
    public void Clear() => Release();

    public CollisionDebugRenderer(GL gl) => _gl = gl;

    public void LoadShaders(string shaderDir)
    {
        _shader = Shader.FromFiles(_gl,
            Path.Combine(shaderDir, "collision.vert"),
            Path.Combine(shaderDir, "collision.frag"));
    }

    /// <summary>Upload the whole collision world. Call once, after Build().</summary>
    public unsafe void Build(CollisionWorld world)
    {
        Release();

        if (world.IsEmpty) return;

        var vertices = world.BuildDebugVertices();
        _vertexCount = vertices.Length / FloatsPerVertex;

        _vao = _gl.GenVertexArray();
        _gl.BindVertexArray(_vao);

        _vbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        fixed (float* p = vertices)
        {
            _gl.BufferData(BufferTargetARB.ArrayBuffer,
                (nuint)(vertices.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);
        }

        const uint stride = FloatsPerVertex * sizeof(float);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 1, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        _gl.EnableVertexAttribArray(2);
        _gl.VertexAttribPointer(2, 1, VertexAttribPointerType.Float, false, stride, (void*)(4 * sizeof(float)));

        _gl.BindVertexArray(0);

        Console.WriteLine(
            $"[collision] debug mesh {TriangleCount:N0} triangles, " +
            $"{vertices.Length * sizeof(float) / 1024 / 1024} MB");
    }

    public unsafe void Render(Camera camera, float slopeLimitZ, Vector3 offset)
    {
        if (!Enabled || _shader is null || _vertexCount == 0) return;

        _shader.Use();
        _shader.Set("uViewProjection", camera.ViewProjection);
        _shader.Set("uCameraPos", camera.Position);
        _shader.Set("uSlopeLimit", slopeLimitZ);
        _shader.Set("uFadeStart", FadeStart);
        _shader.Set("uFadeEnd", FadeEnd);
        _shader.Set("uSourceFilter", (float)SourceFilter);
        _shader.Set("uOffset", offset);
        _shader.Set("uHighlight", 0);

        // Collision meshes are not consistently wound, so back-face culling
        // would hide roughly half of them at random.
        _gl.Disable(EnableCap.CullFace);

        if (!Solid)
        {
            _gl.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Line);
            // Pull the lines toward the viewer so they sit ON the surface they
            // describe instead of z-fighting with it.
            _gl.Enable(EnableCap.PolygonOffsetLine);
            _gl.PolygonOffset(-1f, -1f);
        }

        _gl.BindVertexArray(_vao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)_vertexCount);
        _gl.BindVertexArray(0);

        if (!Solid)
        {
            _gl.Disable(EnableCap.PolygonOffsetLine);
            _gl.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Fill);
        }

        _gl.Enable(EnableCap.CullFace);
    }

    /// <summary>
    /// Draw specific triangles in bright yellow, ignoring depth, from the same
    /// vertex data the raycast intersected. If one of these lands somewhere the
    /// bulk wireframe does not, the drawing is lying and the physics is not.
    /// </summary>
    public unsafe void RenderHighlight(Camera camera, IReadOnlyList<Vector3> corners, int mode = 1)
    {
        if (_shader is null || corners.Count < 3) return;

        var data = new float[corners.Count * FloatsPerVertex];
        for (int i = 0; i < corners.Count; i++)
        {
            int o = i * FloatsPerVertex;
            data[o + 0] = corners[i].X;
            data[o + 1] = corners[i].Y;
            data[o + 2] = corners[i].Z;
            data[o + 3] = 1f;
            data[o + 4] = -1f;
        }

        if (!_hiReady)
        {
            _hiVao = _gl.GenVertexArray();
            _hiVbo = _gl.GenBuffer();
            _hiReady = true;
        }

        _gl.BindVertexArray(_hiVao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _hiVbo);
        fixed (float* p = data)
        {
            _gl.BufferData(BufferTargetARB.ArrayBuffer,
                (nuint)(data.Length * sizeof(float)), p, BufferUsageARB.DynamicDraw);
        }

        const uint stride = FloatsPerVertex * sizeof(float);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 1, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        _gl.EnableVertexAttribArray(2);
        _gl.VertexAttribPointer(2, 1, VertexAttribPointerType.Float, false, stride, (void*)(4 * sizeof(float)));

        _shader.Use();
        _shader.Set("uViewProjection", camera.ViewProjection);
        _shader.Set("uCameraPos", camera.Position);
        _shader.Set("uSlopeLimit", 0f);
        _shader.Set("uFadeStart", 10000f);
        _shader.Set("uFadeEnd", 20000f);
        _shader.Set("uSourceFilter", -1f);
        _shader.Set("uOffset", Vector3.Zero);
        _shader.Set("uHighlight", mode);

        _gl.Disable(EnableCap.CullFace);
        _gl.Disable(EnableCap.DepthTest);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)corners.Count);
        _gl.Enable(EnableCap.DepthTest);
        _gl.Enable(EnableCap.CullFace);

        _shader.Set("uHighlight", 0);
        _gl.BindVertexArray(0);
    }

    /// <summary>
    /// Draw the character's capsule where the controller actually thinks it is.
    ///
    /// There is no character model yet, so nothing is drawn at the player's
    /// position — and a third-person camera nine yards behind and three below
    /// the head makes the foreground read as "where I am standing" when it is
    /// not. That illusion cost real time: movement that was working correctly
    /// looked like a two yard collision offset. An explicit marker removes the
    /// ambiguity for good.
    ///
    /// An octagonal prism at the controller's real radius and height, plus a
    /// spike showing facing.
    /// </summary>
    public void RenderPlayerMarker(Camera camera, Vector3 feet, float radius, float height, float yaw)
    {
        const int sides = 8;
        var corners = new List<Vector3>(sides * 6 + 3);

        var top = feet + new Vector3(0, 0, height);

        for (int i = 0; i < sides; i++)
        {
            float a0 = i * MathF.PI * 2f / sides;
            float a1 = (i + 1) * MathF.PI * 2f / sides;

            var p0 = new Vector3(MathF.Cos(a0), MathF.Sin(a0), 0) * radius;
            var p1 = new Vector3(MathF.Cos(a1), MathF.Sin(a1), 0) * radius;

            // Side quad as two triangles.
            corners.Add(feet + p0); corners.Add(feet + p1); corners.Add(top + p1);
            corners.Add(feet + p0); corners.Add(top + p1); corners.Add(top + p0);
        }

        // Facing spike at chest height, so the marker reads directionally.
        var forward = new Vector3(MathF.Cos(yaw), MathF.Sin(yaw), 0);
        var right = new Vector3(MathF.Sin(yaw), -MathF.Cos(yaw), 0);
        var chest = feet + new Vector3(0, 0, height * 0.5f);

        corners.Add(chest + right * radius * 0.5f);
        corners.Add(chest - right * radius * 0.5f);
        corners.Add(chest + forward * radius * 2.5f);

        RenderHighlight(camera, corners, mode: 2);
    }

    private void Release()
    {
        if (_vertexCount == 0) return;
        _gl.DeleteVertexArray(_vao);
        _gl.DeleteBuffer(_vbo);
        _vertexCount = 0;
    }

    public void Dispose()
    {
        if (_hiReady) { _gl.DeleteVertexArray(_hiVao); _gl.DeleteBuffer(_hiVbo); _hiReady = false; }
        Release();
        _shader?.Dispose();
    }
}

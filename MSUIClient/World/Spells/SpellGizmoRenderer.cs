using System.Numerics;
using MSUIClient.Engine;
using Silk.NET.OpenGL;

namespace MSUIClient.World.Spells;

/// <summary>
/// One coloured line segment in absolute WoW world space. Built by
/// <see cref="SpellEmitterGizmoLaw"/> (pure), drawn by <see cref="SpellGizmoRenderer"/>.
/// </summary>
public readonly record struct GizmoLine(Vector3 A, Vector3 B, Vector4 Color);

/// <summary>
/// The creator's line pass: the reference grid around the acting body and one
/// gizmo per live spell emitter (origin, frame, birth shape, reach). See
/// shared_docs/SPELL_CREATOR_IDE.md §2.3.
///
/// WHY A RENDERER OF ITS OWN
///   CollisionDebugRenderer draws a baked triangle mesh with a fixed palette;
///   this pass is rebuilt EVERY frame from the particle system's live pool
///   frames and needs a colour per vertex (texture-slot identity colours, the
///   bold/dim highlight). A tiny dynamic GL_LINES buffer is the honest fit.
///   No GameLoop reference in here - GameLoop hands it the lines and the camera.
/// </summary>
public sealed class SpellGizmoRenderer : IDisposable
{
    /// <summary>Position(3) + colour(4).</summary>
    private const int FloatsPerVertex = 7;

    private readonly GL _gl;
    private Engine.Shader? _shader;
    private uint _vao, _vbo;
    private bool _ready;
    private float[] _staging = new float[FloatsPerVertex * 2 * 1024];

    /// <summary>Depth-test against the world so the character occludes the grid
    /// ("the character model is the priority"). Off = draw through everything.</summary>
    public bool DepthTest { get; set; } = true;

    public int LinesLastFrame { get; private set; }

    public SpellGizmoRenderer(GL gl) => _gl = gl;

    public void LoadShaders(string shaderDir)
    {
        _shader = Engine.Shader.FromFiles(_gl,
            Path.Combine(shaderDir, "gizmo.vert"),
            Path.Combine(shaderDir, "gizmo.frag"));
    }

    public unsafe void Render(Camera camera, IReadOnlyList<GizmoLine> lines)
    {
        LinesLastFrame = 0;
        if (_shader is null || lines.Count == 0) return;

        int floats = lines.Count * 2 * FloatsPerVertex;
        if (_staging.Length < floats) _staging = new float[floats * 2];
        int o = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            GizmoLine line = lines[i];
            _staging[o++] = line.A.X; _staging[o++] = line.A.Y; _staging[o++] = line.A.Z;
            _staging[o++] = line.Color.X; _staging[o++] = line.Color.Y;
            _staging[o++] = line.Color.Z; _staging[o++] = line.Color.W;
            _staging[o++] = line.B.X; _staging[o++] = line.B.Y; _staging[o++] = line.B.Z;
            _staging[o++] = line.Color.X; _staging[o++] = line.Color.Y;
            _staging[o++] = line.Color.Z; _staging[o++] = line.Color.W;
        }

        if (!_ready)
        {
            _vao = _gl.GenVertexArray();
            _vbo = _gl.GenBuffer();
            _ready = true;
        }

        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        fixed (float* p = _staging)
        {
            _gl.BufferData(BufferTargetARB.ArrayBuffer,
                (nuint)(floats * sizeof(float)), p, BufferUsageARB.DynamicDraw);
        }
        const uint stride = FloatsPerVertex * sizeof(float);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 4, VertexAttribPointerType.Float, false, stride,
            (void*)(3 * sizeof(float)));

        _shader.Use();
        _shader.Set("uViewProjection", camera.ViewProjection);

        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _gl.DepthMask(false);
        if (!DepthTest) _gl.Disable(EnableCap.DepthTest);
        _gl.DrawArrays(PrimitiveType.Lines, 0, (uint)(lines.Count * 2));
        if (!DepthTest) _gl.Enable(EnableCap.DepthTest);
        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);

        _gl.BindVertexArray(0);
        LinesLastFrame = lines.Count;
    }

    public void Dispose()
    {
        if (_ready)
        {
            _gl.DeleteVertexArray(_vao);
            _gl.DeleteBuffer(_vbo);
            _ready = false;
        }
        _shader?.Dispose();
        _shader = null;
    }
}

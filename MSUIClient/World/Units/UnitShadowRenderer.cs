using System.Numerics;
using Silk.NET.OpenGL;
using MSUIClient.Engine;
using MSUIClient.Formats;
using Shader = MSUIClient.Engine.Shader;
using Texture = MSUIClient.Engine.Texture;

namespace MSUIClient.World.Units;

/// <summary>A unit's ground contact, in world coordinates.</summary>
public readonly record struct UnitShadowCaster(Vector3 Feet, float Radius, float Opacity = 1f);

/// <summary>
/// Draws the vanilla unit blob as one instanced, depth-tested ground pass.
/// This is deliberately not a shadow map: the 1.12 presentation uses a small contact blob to
/// separate feet from the ground, and the real archive asset keeps that edge language authentic.
/// </summary>
public sealed class UnitShadowRenderer : IDisposable
{
    private const string VanillaTexturePath = @"textures\ShadowBlob.blp";
    private const int FloatsPerInstance = 5; // camera-relative centre(3), radius, opacity
    private const float GroundBias = 0.025f;

    private readonly GL _gl;
    private Shader? _shader;
    private Texture? _texture;
    private uint _vao;
    private uint _quadVbo;
    private uint _instanceVbo;
    private float[] _instanceData = [];
    private int _instanceCapacity;

    public float FogStart { get; set; } = 350f;
    public float FogEnd { get; set; } = 900f;
    public float Opacity { get; set; } = 0.42f;
    public int DrawnLastFrame { get; private set; }
    public string TextureSource { get; } = "unresolved";

    public unsafe UnitShadowRenderer(GL gl, MpqMount mpq)
    {
        _gl = gl;
        _shader = Shader.FromSource(gl, "unit_shadow", VertexSource, FragmentSource);

        try
        {
            if (mpq.ReadFile(VanillaTexturePath) is { } blp)
            {
                byte[] bgra = BlpDecoder.GetPixels(blp, 0, out int width, out int height);
                _texture = Texture.From2D(gl, bgra, width, height, mipmaps: true, repeat: false);
                TextureSource = VanillaTexturePath;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[unit-shadow] vanilla texture decode failed - {ex.Message}");
        }

        if (_texture is null)
        {
            const int size = 64;
            _texture = Texture.From2D(gl, BuildSoftBlob(size), size, size,
                mipmaps: true, repeat: false);
            TextureSource = "procedural-soft-blob";
        }

        float[] quad =
        [
            -1f, -1f, 0f, 0f,
             1f, -1f, 1f, 0f,
            -1f,  1f, 0f, 1f,
             1f,  1f, 1f, 1f,
        ];

        _vao = gl.GenVertexArray();
        gl.BindVertexArray(_vao);

        _quadVbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _quadVbo);
        fixed (float* p = quad)
            gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(quad.Length * sizeof(float)),
                p, BufferUsageARB.StaticDraw);
        const uint vertexStride = 4 * sizeof(float);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false,
            vertexStride, (void*)0);
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false,
            vertexStride, (void*)(2 * sizeof(float)));

        _instanceVbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _instanceVbo);
        const uint instanceStride = FloatsPerInstance * sizeof(float);
        gl.EnableVertexAttribArray(2);
        gl.VertexAttribPointer(2, 4, VertexAttribPointerType.Float, false,
            instanceStride, (void*)0);
        gl.VertexAttribDivisor(2, 1);
        gl.EnableVertexAttribArray(3);
        gl.VertexAttribPointer(3, 1, VertexAttribPointerType.Float, false,
            instanceStride, (void*)(4 * sizeof(float)));
        gl.VertexAttribDivisor(3, 1);

        gl.BindVertexArray(0);
        Console.WriteLine($"[unit-shadow] ready texture={TextureSource}");
    }

    /// <summary>
    /// Draw the local body plus streamed units. Positions become camera-relative on the CPU,
    /// matching every other world pass and avoiding precision loss at large WoW coordinates.
    /// </summary>
    public unsafe void Render(Camera camera, UnitShadowCaster? local,
        IReadOnlyList<UnitShadowCaster>? streamed)
    {
        DrawnLastFrame = 0;
        if (_shader is null || _texture is null || Opacity <= 0f) return;

        int requested = (local.HasValue ? 1 : 0) + (streamed?.Count ?? 0);
        if (requested == 0) return;
        EnsureCapacity(requested);

        Matrix4x4 viewProjection = camera.RelativeViewProjection;
        Vector3 eye = camera.Position;
        int count = 0;

        void Append(in UnitShadowCaster caster)
        {
            float radius = caster.Radius;
            float alpha = caster.Opacity;
            if (!float.IsFinite(radius) || !float.IsFinite(alpha) || radius <= 0f || alpha <= 0f)
                return;

            Vector3 relative = caster.Feet - eye + new Vector3(0f, 0f, GroundBias);
            if (!Camera.BoxInFrustum(viewProjection,
                    relative - new Vector3(radius, radius, 0.15f),
                    relative + new Vector3(radius, radius, 0.15f)))
                return;

            float distance = relative.Length();
            if (distance - radius >= FogEnd) return;

            int offset = count * FloatsPerInstance;
            _instanceData[offset] = relative.X;
            _instanceData[offset + 1] = relative.Y;
            _instanceData[offset + 2] = relative.Z;
            _instanceData[offset + 3] = radius;
            _instanceData[offset + 4] = Math.Clamp(alpha, 0f, 1f);
            count++;
        }

        if (local is { } localCaster) Append(localCaster);
        if (streamed is not null)
            for (int i = 0; i < streamed.Count; i++) Append(streamed[i]);
        if (count == 0) return;

        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _instanceVbo);
        fixed (float* p = _instanceData)
            _gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0,
                (nuint)(count * FloatsPerInstance * sizeof(float)), p);

        _gl.Enable(EnableCap.DepthTest);
        _gl.Enable(EnableCap.Blend);
        _gl.Disable(EnableCap.CullFace);
        _gl.DepthMask(false);
        // Preserve destination alpha. The scene's alpha can be consumed as a downstream
        // category/importance channel; a cosmetic dark decal must only change covered RGB.
        _gl.BlendFuncSeparate(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha,
            BlendingFactor.Zero, BlendingFactor.One);
        _gl.Enable(EnableCap.PolygonOffsetFill);
        _gl.PolygonOffset(-1f, -1f);

        _shader.Use();
        _shader.Set("uViewProjection", viewProjection);
        _shader.Set("uFogStart", FogStart);
        _shader.Set("uFogEnd", FogEnd);
        _shader.Set("uOpacity", Opacity);
        _shader.Set("uTexture", 0);
        _texture.Bind(0);
        _gl.BindVertexArray(_vao);
        _gl.DrawArraysInstanced(PrimitiveType.TriangleStrip, 0, 4, (uint)count);
        _gl.BindVertexArray(0);

        _gl.Disable(EnableCap.PolygonOffsetFill);
        _gl.DepthMask(true);
        _gl.Enable(EnableCap.DepthTest);
        _gl.Enable(EnableCap.CullFace);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _gl.Disable(EnableCap.Blend);
        DrawnLastFrame = count;
    }

    private unsafe void EnsureCapacity(int requested)
    {
        if (requested <= _instanceCapacity) return;
        int capacity = Math.Max(32, _instanceCapacity);
        while (capacity < requested) capacity *= 2;
        _instanceCapacity = capacity;
        Array.Resize(ref _instanceData, capacity * FloatsPerInstance);

        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _instanceVbo);
        fixed (float* p = _instanceData)
            _gl.BufferData(BufferTargetARB.ArrayBuffer,
                (nuint)(_instanceData.Length * sizeof(float)), p, BufferUsageARB.StreamDraw);
    }

    private static byte[] BuildSoftBlob(int size)
    {
        var bgra = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float dx = (x + 0.5f) / size * 2f - 1f;
            float dy = (y + 0.5f) / size * 2f - 1f;
            float distanceSq = dx * dx + dy * dy;
            float coverage = Math.Clamp(1f - distanceSq, 0f, 1f);
            coverage *= coverage;
            bgra[(y * size + x) * 4 + 3] = (byte)MathF.Round(coverage * 255f);
        }
        return bgra;
    }

    public void Dispose()
    {
        _texture?.Dispose();
        _texture = null;
        _shader?.Dispose();
        _shader = null;
        if (_instanceVbo != 0) { _gl.DeleteBuffer(_instanceVbo); _instanceVbo = 0; }
        if (_quadVbo != 0) { _gl.DeleteBuffer(_quadVbo); _quadVbo = 0; }
        if (_vao != 0) { _gl.DeleteVertexArray(_vao); _vao = 0; }
    }

    private const string VertexSource = @"#version 330 core
layout(location=0) in vec2 aCorner;
layout(location=1) in vec2 aUv;
layout(location=2) in vec4 iCentreRadius;
layout(location=3) in float iOpacity;
uniform mat4 uViewProjection;
out vec2 vUv;
out float vOpacity;
out float vDistance;
void main(){
    vec3 position = iCentreRadius.xyz + vec3(aCorner * iCentreRadius.w, 0.0);
    gl_Position = uViewProjection * vec4(position, 1.0);
    vUv = aUv;
    vOpacity = iOpacity;
    vDistance = length(iCentreRadius.xyz);
}";

    private const string FragmentSource = @"#version 330 core
in vec2 vUv;
in float vOpacity;
in float vDistance;
uniform sampler2D uTexture;
uniform float uFogStart;
uniform float uFogEnd;
uniform float uOpacity;
out vec4 frag;
void main(){
    float mask = texture(uTexture, vUv).a;
    if (mask < 0.004) discard;
    float fog = clamp((vDistance - uFogStart) / max(uFogEnd - uFogStart, 1.0), 0.0, 1.0);
    float alpha = mask * vOpacity * uOpacity * (1.0 - fog);
    frag = vec4(0.035, 0.028, 0.022, alpha);
}";
}

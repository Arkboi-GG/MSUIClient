using System.Numerics;
using Silk.NET.OpenGL;
using MSUIClient.Engine;
using MSUIClient.Formats;
using Shader = MSUIClient.Engine.Shader;
using Texture = MSUIClient.Engine.Texture;
using Camera = MSUIClient.Engine.Camera;

namespace MSUIClient.World;

/// <summary>
/// Rain/snow presentation behind <see cref="WeatherVisualLaw"/>. Geometry is
/// world-space and collision-aware; UI code never participates in its layout.
/// Sand remains state/lighting-only because the current Benilla reference has
/// not implemented its deferred sand particle slice either.
/// </summary>
public sealed class WeatherPrecipitationRenderer : IDisposable
{
    private struct Drop
    {
        public Vector3 Position;
        public Vector3 Velocity;
        public float Ground;
        public float Age;
    }

    private struct GroundParticle
    {
        public Vector3 Position;
        public float Age;
        public byte Variant;
    }

    private sealed class MistNode
    {
        public Vector3 Position;
        public Vector3 Direction;
        public float Tail;
        public float Age;
        public float Life;
        public float PlanarSpeed;
        public float[] Path = [];
    }

    private readonly GL _gl;
    private readonly ClientConfig _config;
    private Shader? _shader;
    private Texture? _rainTexture, _splashTexture, _snowTexture, _mistTexture;
    private uint _vao, _vbo;
    private readonly List<Drop> _rain = [];
    private readonly List<Drop> _snow = [];
    private readonly List<GroundParticle> _patters = [];
    private readonly List<GroundParticle> _settledSnow = [];
    private readonly List<MistNode> _mist = [];
    private readonly List<float> _vertices = [];
    private readonly Queue<(float Dt, Vector3 Delta)> _windWindow = [];
    private Vector3? _lastPlayerPosition;
    private Vector3 _wind;
    private Vector3 _heading = Vector3.UnitX;
    private Quaternion _slabTilt = Quaternion.Identity;
    private Quaternion _streakTilt = Quaternion.Identity;
    private float _mistBudget;
    private uint _rng = 0x9E3779B9u;
    private uint _cutSequence;

    public int RainDrops => _rain.Count;
    public int SnowFlakes => _snow.Count;
    public int GroundParticles => _patters.Count + _settledSnow.Count;
    public int MistPuffs => _mist.Count;
    public bool FrozenIndoors { get; private set; }

    public WeatherPrecipitationRenderer(GL gl, ClientConfig config)
    {
        _gl = gl;
        _config = config;
    }

    public void LoadShaders(string shaderDir)
    {
        _shader = Shader.FromFiles(_gl,
            Path.Combine(shaderDir, "weather_precip.vert"),
            Path.Combine(shaderDir, "weather_precip.frag"));
        BuildBuffers();
        _rainTexture = LoadTexture(@"textures\weather\raindrop01.blp", mipmaps: true);
        _splashTexture = LoadTexture(@"textures\weather\raindropsplash01.blp", mipmaps: false);
        _snowTexture = LoadTexture(@"textures\weather\snowflake01.blp", mipmaps: true);
        _mistTexture = LoadTexture(@"textures\weather\snowmist01.blp", mipmaps: false);
    }

    public void Update(float dt, WeatherVisualLaw weather, Vector3 cameraPosition,
        Vector3 playerPosition, Vector3 playerFacing, float livePlanarSpeed,
        bool indoorBlocked, Func<float, float, float, float?> groundHeight)
    {
        if (dt <= 0f || !float.IsFinite(dt)) return;
        FrozenIndoors = indoorBlocked;
        if (indoorBlocked) return; // reference freezes simulation and draw together

        UpdateWind(dt, playerPosition, playerFacing, livePlanarSpeed);
        if (_cutSequence != weather.CutSequence)
        {
            _cutSequence = weather.CutSequence;
            _mistBudget = 0f; // scheduled-but-unborn nodes retire at a type cut
        }

        IntegrateDrops(_rain, _patters, WeatherVisualLaw.Kind.Rain, dt, cameraPosition,
            groundHeight);
        IntegrateDrops(_snow, _settledSnow, WeatherVisualLaw.Kind.Snow, dt, cameraPosition,
            groundHeight);
        AgeGround(_patters, WeatherPrecipitationLaw.RainPatterLife, dt);
        AgeGround(_settledSnow, WeatherPrecipitationLaw.SnowSettleLife, dt);

        if (weather.EffectKind is WeatherVisualLaw.Kind.Rain or WeatherVisualLaw.Kind.Snow &&
            weather.EffectDensity > 0f)
        {
            List<Drop> pool = weather.EffectKind == WeatherVisualLaw.Kind.Rain ? _rain : _snow;
            int count = WeatherPrecipitationLaw.FrameSpawnCount(weather.EffectKind,
                weather.EffectDensity, dt, WeatherPrecipitationLaw.DropCapacity - pool.Count);
            float castZ = cameraPosition.Z +
                WeatherPrecipitationLaw.SpawnBox(weather.EffectKind).Height;
            for (int i = 0; i < count; i++)
            {
                var birth = WeatherPrecipitationLaw.SpawnParticle(weather.EffectKind,
                    weather.EffectDensity, cameraPosition, _wind, _slabTilt,
                    Rand(), Rand(), Rand(), Rand(), Rand());
                float ground = groundHeight(birth.Position.X, birth.Position.Y, castZ) ??
                               castZ - WeatherPrecipitationLaw.RetireDistance;
                if (ground >= birth.Position.Z) continue;
                pool.Add(new Drop
                {
                    Position = birth.Position,
                    Velocity = birth.Velocity,
                    Ground = ground,
                });
            }
        }

        UpdateMist(dt, weather, cameraPosition, groundHeight);
    }

    private void IntegrateDrops(List<Drop> drops, List<GroundParticle> ground,
        WeatherVisualLaw.Kind kind, float dt, Vector3 camera,
        Func<float, float, float, float?> groundHeight)
    {
        float castZ = camera.Z + WeatherPrecipitationLaw.SpawnBox(kind).Height;
        float retireSq = MathF.Pow(WeatherPrecipitationLaw.RetireDistance +
                                   WeatherPrecipitationLaw.DropRetireSlack, 2f);
        for (int i = drops.Count - 1; i >= 0; i--)
        {
            Drop drop = drops[i];
            drop.Age += dt;
            drop.Position += drop.Velocity * dt;
            if (Vector2.DistanceSquared(new Vector2(drop.Position.X, drop.Position.Y),
                    new Vector2(camera.X, camera.Y)) > retireSq)
            {
                drops.RemoveAt(i);
                continue;
            }

            float? refreshed = groundHeight(drop.Position.X, drop.Position.Y, castZ);
            if (refreshed is float surface) drop.Ground = surface;
            if (drop.Position.Z <= drop.Ground)
            {
                if (ground.Count < WeatherPrecipitationLaw.GroundCapacity)
                    ground.Add(new GroundParticle
                    {
                        Position = new Vector3(drop.Position.X, drop.Position.Y, drop.Ground + .02f),
                        Variant = (byte)Math.Min(3, (int)(Rand() * 4f)),
                    });
                drops.RemoveAt(i);
            }
            else drops[i] = drop;
        }
    }

    private static void AgeGround(List<GroundParticle> particles, float life, float dt)
    {
        for (int i = particles.Count - 1; i >= 0; i--)
        {
            GroundParticle p = particles[i];
            p.Age += dt;
            if (p.Age >= life) particles.RemoveAt(i);
            else particles[i] = p;
        }
    }

    private void UpdateWind(float dt, Vector3 position, Vector3 facing, float liveSpeed)
    {
        Vector3 delta = Vector3.Zero;
        if (_lastPlayerPosition is Vector3 previous)
        {
            delta = position - previous;
            delta.Z = 0f;
            if (delta.LengthSquared() > MathF.Max(MathF.Pow(100f * dt, 2f), 100f))
            {
                _windWindow.Clear();
                delta = Vector3.Zero;
            }
        }
        _lastPlayerPosition = position;
        _windWindow.Enqueue((dt, delta));
        float total = _windWindow.Sum(sample => sample.Dt);
        while (_windWindow.Count > 1 && total - _windWindow.Peek().Dt >= .149f)
            total -= _windWindow.Dequeue().Dt;
        Vector3 displacement = Vector3.Zero;
        foreach (var sample in _windWindow) displacement += sample.Delta;
        _wind = displacement / (total + .001f);
        _wind.Z = 0f;

        Vector3 flatFacing = new(facing.X, facing.Y, 0f);
        if (_wind.LengthSquared() >= 1f) _heading = Vector3.Normalize(_wind);
        else if (flatFacing.LengthSquared() > 1e-6f) _heading = Vector3.Normalize(flatFacing);
        _slabTilt = Lean(_heading, liveSpeed, 18f, 65f);
        _streakTilt = _wind.LengthSquared() < .001f
            ? Quaternion.Identity : Lean(_wind, _wind.Length(), 30f, 45f);
    }

    private static Quaternion Lean(Vector3 direction, float speed, float divisor, float maxDegrees)
    {
        Vector3 axis = new(direction.Y, -direction.X, 0f);
        if (axis.LengthSquared() <= 1e-6f) return Quaternion.Identity;
        float radians = Math.Clamp(speed / divisor, 0f, 1f) * maxDegrees * MathF.PI / 180f;
        return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), radians);
    }

    private void UpdateMist(float dt, WeatherVisualLaw weather, Vector3 camera,
        Func<float, float, float, float?> groundHeight)
    {
        if (weather.EffectKind is WeatherVisualLaw.Kind.Rain or WeatherVisualLaw.Kind.Snow)
            _mistBudget = MathF.Min(WeatherPrecipitationLaw.MistCapacity,
                _mistBudget + WeatherPrecipitationLaw.MistRate(weather.EffectKind,
                    weather.EffectDensity) * dt);

        float castZ = camera.Z + WeatherPrecipitationLaw.SpawnBox(weather.EffectKind).Height;
        while (_mistBudget >= 1f && _mist.Count < WeatherPrecipitationLaw.MistCapacity)
        {
            _mistBudget -= 1f;
            bool snow = weather.EffectKind == WeatherVisualLaw.Kind.Snow;
            float radius = (snow ? 9f : 5f) + (Rand() - .5f) * (snow ? 3f : 1.2f);
            float azimuth = -1.57f + (Rand() - .5f) * .349f;
            Vector3 antiHeading = -_heading;
            float ca = MathF.Cos(azimuth + MathF.PI * .5f);
            float sa = MathF.Sin(azimuth + MathF.PI * .5f);
            Vector3 side = new(-antiHeading.Y, antiHeading.X, 0f);
            Vector3 direction = antiHeading * (ca * radius) + side * (sa * radius);
            direction.Z = .33333334f + (Rand() - .5f) * .033333335f;
            Vector3 scatter = new((Rand() - .5f) * 44f, (Rand() - .5f) * 44f,
                                  (Rand() - .5f) * 25f);
            Vector3 pos = camera - direction * 1.5f + scatter;
            float ground = groundHeight(pos.X, pos.Y, castZ) ??
                           castZ - WeatherPrecipitationLaw.RetireDistance;
            pos.Z = MathF.Max(pos.Z, ground) + WeatherPrecipitationLaw.MistFloor;

            float planar = new Vector2(direction.X, direction.Y).Length();
            int samples = Math.Clamp((int)MathF.Round(planar * .96f / 1.0416666f), 1, 64);
            var path = new float[samples];
            Vector2 step = planar > 1e-6f
                ? Vector2.Normalize(new Vector2(direction.X, direction.Y)) * 1.0416666f
                : Vector2.Zero;
            for (int i = 0; i < samples; i++)
                path[i] = groundHeight(pos.X + step.X * i, pos.Y + step.Y * i, castZ) ?? ground;
            float life = 2.7f + (Rand() - .5f) * .3f;
            int invalid = WeatherPrecipitationLaw.FirstInvalidMistPath(path);
            if (invalid == 0) continue;
            if (invalid > 0) life *= (invalid + 1f) / path.Length;
            _mist.Add(new MistNode
            {
                Position = pos,
                Direction = direction,
                Tail = (Rand() - .5f) * 3.3333333f,
                Life = life,
                PlanarSpeed = planar,
                Path = path,
            });
        }

        for (int i = _mist.Count - 1; i >= 0; i--)
        {
            MistNode node = _mist[i];
            node.Age += dt;
            if (node.Age >= node.Life) { _mist.RemoveAt(i); continue; }
            node.Position += node.Direction * dt + Vector3.One * (.5f * dt * dt * node.Tail);
            float cursor = MathF.Max(0f, node.PlanarSpeed * node.Age / 1.0416666f);
            int lo = Math.Min(node.Path.Length - 1, (int)MathF.Floor(cursor));
            int hi = Math.Min(node.Path.Length - 1, (int)MathF.Ceiling(cursor));
            float target = node.Path[lo] + (node.Path[hi] - node.Path[lo]) * (cursor - MathF.Floor(cursor)) +
                           WeatherPrecipitationLaw.MistFloor;
            if (node.Position.Z < target)
            {
                node.Tail += 5f / 3f;
                node.Position = node.Position with { Z = MathF.Min(target, node.Position.Z + 3f) };
            }
        }
    }

    public void Render(Camera camera, Vector3 fogColor, float fogStart, float fogEnd)
    {
        if (FrozenIndoors || _shader is null) return;
        Vector3 eye = camera.Position;
        Vector3 forward = camera.Forward;
        Vector3 right = Vector3.Cross(forward, Vector3.UnitZ);
        right = right.LengthSquared() > 1e-6f ? Vector3.Normalize(right) : camera.FlatRight;
        Vector3 up = Vector3.Normalize(Vector3.Cross(right, forward));

        bool depth = _gl.IsEnabled(EnableCap.DepthTest);
        bool cull = _gl.IsEnabled(EnableCap.CullFace);
        _gl.Enable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.CullFace);
        _gl.DepthMask(false);
        _gl.Enable(EnableCap.Blend);
        _gl.BindVertexArray(_vao);
        _shader.Use();
        _shader.Set("uViewProjection", camera.RelativeViewProjection);
        _shader.Set("uTexture", 0);
        _shader.Set("uFogColor", fogColor);
        _shader.Set("uFogStart", fogStart);
        _shader.Set("uFogEnd", fogEnd);

        if (_rainTexture is not null && _rain.Count > 0)
        {
            _vertices.Clear();
            foreach (Drop d in _rain)
            {
                Vector3 anti = Vector3.Normalize(-d.Velocity);
                Vector3 toCamera = Vector3.Normalize(eye - d.Position);
                Vector3 width = Vector3.Cross(toCamera, anti);
                width = (width.LengthSquared() > 1e-8f ? Vector3.Normalize(width) : right) * .05f;
                Vector3 apex = d.Position + Vector3.Transform(anti * 2f, _streakTilt);
                Vertex(d.Position - width - eye, 0f, 1f, Vector4.One);
                Vertex(d.Position + width - eye, 1f, 1f, Vector4.One);
                Vertex(apex - eye, .5f, 0f, Vector4.One);
            }
            Draw(_rainTexture, mod2x: true, fog: true);
        }

        if (_splashTexture is not null && _patters.Count > 0)
        {
            _vertices.Clear();
            Vector3 halfRight = right / 12f;
            Vector3 top = up / 6f;
            foreach (GroundParticle p in _patters)
            {
                float frame = Math.Min(3, (int)(p.Age / WeatherPrecipitationLaw.RainPatterLife * 4f));
                float u = frame * .25f, v = p.Variant * .25f;
                Vertex(p.Position - halfRight - eye, u, v + .25f, Vector4.One);
                Vertex(p.Position + top - eye, u + .125f, v + .043f, Vector4.One);
                Vertex(p.Position + halfRight - eye, u + .25f, v + .25f, Vector4.One);
            }
            Draw(_splashTexture, mod2x: true, fog: true);
        }

        if (_snowTexture is not null && (_snow.Count > 0 || _settledSnow.Count > 0))
        {
            _vertices.Clear();
            float fovy = camera.AuthoredVerticalFieldOfViewRadians ??
                         camera.FieldOfViewDegrees * MathF.PI / 180f;
            float worldPerPixel = MathF.Tan(fovy * .5f) /
                                  WeatherPrecipitationLaw.SnowReferenceHeight;
            foreach (Drop d in _snow)
                SnowQuad(d.Position, Math.Clamp(d.Age / WeatherPrecipitationLaw.SnowFadeIn, 0f, 1f));
            foreach (GroundParticle p in _settledSnow)
                SnowQuad(p.Position, 1f - Math.Clamp(p.Age /
                    WeatherPrecipitationLaw.SnowSettleLife, 0f, 1f));
            Draw(_snowTexture, mod2x: false, fog: false);

            void SnowQuad(Vector3 center, float alpha)
            {
                Vector3 relative = center - eye;
                float viewDepth = Vector3.Dot(relative, forward);
                if (viewDepth <= 0f) return;
                float pixels = WeatherPrecipitationLaw.SnowPixelSize(relative.Length());
                float half = pixels * viewDepth * worldPerPixel;
                Quad(relative, right * half, up * half, new Vector4(1f, 1f, 1f, alpha));
            }
        }

        if (_mistTexture is not null && _mist.Count > 0)
        {
            _vertices.Clear();
            Vector3 mr = right * WeatherPrecipitationLaw.MistHalfSize;
            Vector3 mu = up * WeatherPrecipitationLaw.MistHalfSize;
            foreach (MistNode node in _mist)
            {
                float lifeAlpha = WeatherPrecipitationLaw.MistLifeAlpha(node.Age, node.Life);
                Vector3 relative = node.Position - eye;
                MistCorner(relative - mr - mu, 0f, 1f);
                MistCorner(relative + mr - mu, 1f, 1f);
                MistCorner(relative + mr + mu, 1f, 0f);
                MistCorner(relative - mr - mu, 0f, 1f);
                MistCorner(relative + mr + mu, 1f, 0f);
                MistCorner(relative - mr + mu, 0f, 0f);

                void MistCorner(Vector3 p, float u, float v)
                {
                    float a = WeatherPrecipitationLaw.MistDistanceAlpha(p.Length()) * lifeAlpha;
                    Vertex(p, u, v, new Vector4(fogColor, a));
                }
            }
            Draw(_mistTexture, mod2x: false, fog: false);
        }

        _gl.BindVertexArray(0);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
        _gl.DepthMask(true);
        if (depth) _gl.Enable(EnableCap.DepthTest); else _gl.Disable(EnableCap.DepthTest);
        if (cull) _gl.Enable(EnableCap.CullFace); else _gl.Disable(EnableCap.CullFace);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _gl.Disable(EnableCap.Blend);
    }

    private void Quad(Vector3 c, Vector3 r, Vector3 u, Vector4 color)
    {
        Vertex(c - r - u, 0f, 1f, color);
        Vertex(c + r - u, 1f, 1f, color);
        Vertex(c + r + u, 1f, 0f, color);
        Vertex(c - r - u, 0f, 1f, color);
        Vertex(c + r + u, 1f, 0f, color);
        Vertex(c - r + u, 0f, 0f, color);
    }

    private void Vertex(Vector3 position, float u, float v, Vector4 color)
    {
        _vertices.Add(position.X); _vertices.Add(position.Y); _vertices.Add(position.Z);
        _vertices.Add(u); _vertices.Add(v);
        _vertices.Add(color.X); _vertices.Add(color.Y); _vertices.Add(color.Z); _vertices.Add(color.W);
    }

    private unsafe void Draw(Texture texture, bool mod2x, bool fog)
    {
        if (_vertices.Count == 0 || _shader is null) return;
        _shader.Set("uFogEnabled", fog ? 1 : 0);
        _gl.BlendFunc(mod2x ? BlendingFactor.DstColor : BlendingFactor.SrcAlpha,
            mod2x ? BlendingFactor.SrcColor : BlendingFactor.OneMinusSrcAlpha);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, texture.Handle);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        float[] data = _vertices.ToArray();
        fixed (float* p = data)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(data.Length * sizeof(float)), p,
                BufferUsageARB.StreamDraw);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)(data.Length / 9));
    }

    private unsafe void BuildBuffers()
    {
        _vao = _gl.GenVertexArray();
        _vbo = _gl.GenBuffer();
        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        const uint stride = 9 * sizeof(float);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride,
            (void*)(3 * sizeof(float)));
        _gl.EnableVertexAttribArray(2);
        _gl.VertexAttribPointer(2, 4, VertexAttribPointerType.Float, false, stride,
            (void*)(5 * sizeof(float)));
        _gl.BindVertexArray(0);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
    }

    private Texture? LoadTexture(string path, bool mipmaps)
    {
        try
        {
            var decoded = AdtTerrainReader.ReadBlpPixels(_config.ClientDataPath, path);
            return decoded is { } d
                ? Texture.From2D(_gl, d.bgra, d.width, d.height, mipmaps, repeat: false)
                : null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[weather] texture {path} unavailable: {ex.Message}");
            return null;
        }
    }

    private float Rand()
    {
        _rng ^= _rng << 13;
        _rng ^= _rng >> 17;
        _rng ^= _rng << 5;
        return (_rng >> 8) / 16777216f;
    }

    public void Dispose()
    {
        _rainTexture?.Dispose(); _splashTexture?.Dispose();
        _snowTexture?.Dispose(); _mistTexture?.Dispose();
        _shader?.Dispose();
        if (_vbo != 0) _gl.DeleteBuffer(_vbo);
        if (_vao != 0) _gl.DeleteVertexArray(_vao);
    }
}

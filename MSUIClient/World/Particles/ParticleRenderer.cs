using System.Numerics;
using Silk.NET.OpenGL;
using MSUIClient.Engine;
using MSUIClient.Formats;

// Silk.NET.OpenGL ships its own Shader and Texture types, so both names are
// ambiguous the moment that namespace is imported. Every other renderer in this
// project aliases them the same way - LiquidRenderer, TerrainRenderer,
// FoliageRenderer and DoodadRenderer all carry these two lines.
using Shader = MSUIClient.Engine.Shader;
using Texture = MSUIClient.Engine.Texture;

namespace MSUIClient.World.Particles;

/// <summary>
/// M2 particle emitters, simulated on the CPU and drawn as camera-facing
/// billboards. PLAN_14 stage 2.
///
/// WHAT THIS MAKES VISIBLE. 18% of the archives' 15,214 models carry emitters,
/// and for some of them the emitters ARE the model: InstancePortal's mesh is
/// thirty vertices and everything you see is spawned sprites. Until this
/// existed, a dungeon portal was an invisible stub, a torch was an unlit
/// stick, and a waterfall did not fall.
///
/// THE FOUR THINGS THAT MAKE IT LOOK RIGHT, all measured (PLAN_14 §3):
///
///   1. NEGATIVE EMISSION SPEED. InstancePortal emits at -3.333 and -2.778 -
///      the particles travel TOWARD the emitter. That inward pull is the whole
///      character of a portal, and clamping speed to positive turns it into a
///      fountain. Do not clamp.
///   2. THE BLEND MODE IS NOT DECORATION. ADD for anything that is light -
///      flames, glows, embers, the portal. Alpha for anything that is matter -
///      waterfalls, smoke, steam, dust. The measured split is exactly along
///      that line.
///   3. THE THREE-KEY RAMP with MidPoint. Colour and size both run
///      start -> middle -> end, and the middle key does NOT sit at half life:
///      the portal's is at 0.20 and 0.30, so the flash happens early.
///   4. PER-INSTANCE POOLS. Two torches must not share particles (H5). The
///      model is shared; the pool is keyed by the placement.
///
/// NOT DONE HERE, deliberately: the 4x4 sprite-sheet flipbook that flames use
/// (headCellTrack lives in the part of the struct §3.3 has not cracked), bone
/// animation of the emitter origin, spline and sphere emitter types, tails,
/// and ribbons. A flame will glow but not lick.
/// </summary>
public sealed class ParticleRenderer : IDisposable
{
    private readonly GL _gl;
    private readonly ClientConfig _config;

    private Shader? _shader;
    private uint _vao, _quadVbo, _instanceVbo;
    private int _instanceCapacity;

    private readonly Dictionary<string, Texture?> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<PoolKey, Pool> _pools = [];
    private readonly List<GpuParticle> _scratch = [];

    public bool Enabled { get; set; } = true;

    /// <summary>Beyond this, emitters are not simulated at all.</summary>
    public float SimulationDistance { get; set; } = 120f;

    /// <summary>Global multiplier on every emitter's rate. 0 stops new spawns.</summary>
    public float DensityScale { get; set; } = 1f;

    /// <summary>
    /// How a plane emitter turns `verticalRange` into a direction. **This is an
    /// open question, deliberately exposed rather than decided.**
    ///
    /// The measured facts: 212 of 910 sampled emitters use verticalRange = pi
    /// exactly, so it is a common authored value and not an anomaly; and the
    /// emitter flags do NOT separate the wide ones from the narrow ones -
    /// 0x29 appears in both the pi group and among torches at 0.087.
    ///
    /// Read as a cone half-angle, pi is the whole sphere, and a sphere of
    /// particles converging through its own centre is what this renderer draws
    /// today: a bright volumetric plume. The real 1.12 client draws a broad,
    /// flat, low-contrast sheet with a sparse centre - Nico's words, comparing
    /// the two side by side, were that ours "looks like a spinning 3D emitter
    /// spitting stuff out" where the live one "feels more like an animated 2D
    /// plane pulling things in a circular pattern".
    ///
    /// So the cone reading is wrong for wide emitters, right for narrow ones,
    /// and one run with this switch settles which alternative is right rather
    /// than a third round of reasoning about it.
    /// </summary>
    public enum PlaneDirection
    {
        /// <summary>Cone about the plane normal, half-angle = verticalRange. Today's behaviour.</summary>
        Cone = 0,

        /// <summary>
        /// Direction is radial WITHIN the plane, from the emitter origin out
        /// through the spawn point. With a negative speed every particle then
        /// travels straight back toward the centre and stays in the plane -
        /// which is the shape the live client appears to draw.
        /// </summary>
        InPlaneRadial = 1,

        /// <summary>
        /// Radial in the plane, blended toward the normal by verticalRange, so a
        /// torch at 0.087 is still essentially a narrow upward jet and the
        /// portal at pi is fully in-plane. The compromise reading, and the one
        /// to prefer if BOTH the portal and the torches have to look right.
        /// </summary>
        Blended = 2,
    }

    public PlaneDirection DirectionModel { get; set; } = PlaneDirection.Blended;

    /// <summary>Hard ceiling on live particles, so one bad emitter cannot eat the frame.</summary>
    public int MaxParticles { get; set; } = 40000;

    /// <summary>Seconds since start, driving every emitter's bone spin.</summary>
    public double Time { get; private set; }

    public int LiveParticles { get; private set; }
    public int ActivePools { get; private set; }
    public int DrawnLastFrame { get; private set; }
    public double SimulateMilliseconds { get; private set; }
    public double DrawMilliseconds { get; private set; }

    public ParticleRenderer(GL gl, ClientConfig config)
    {
        _gl = gl;
        _config = config;
    }

    public void LoadShaders(string shaderDir)
    {
        _shader = Shader.FromFiles(_gl,
            Path.Combine(shaderDir, "particle.vert"),
            Path.Combine(shaderDir, "particle.frag"));
        BuildBuffers();
    }

    // ── Pools ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Identity of one emitter on one placement. Position is rounded to a
    /// tenth of a yard: placements do not move, and a float key would risk a
    /// pool being orphaned and rebuilt by a re-placement that rounded
    /// differently, which reads as the effect blinking.
    /// </summary>
    private readonly record struct PoolKey(string Path, int X, int Y, int Z, int Emitter, int Rot);

    private sealed class Pool
    {
        public M2ParticleEmitter Emitter = null!;
        public Matrix4x4 Transform;
        public string TexturePath = "";
        public float SpawnAccumulator;
        public readonly List<Particle> Particles = [];
        public bool TouchedThisFrame;
        public uint Seed = 0x9E3779B9;

        /// <summary>Uniform scale of the placement, applied to speed and sprite size.</summary>
        public float Scale = 1f;

        /// <summary>xorshift, so each pool is independent and nothing shares Random.</summary>
        public float Rand()
        {
            Seed ^= Seed << 13; Seed ^= Seed >> 17; Seed ^= Seed << 5;
            return (Seed & 0xFFFFFF) / 16777215f;
        }

        public float Symmetric() => Rand() * 2f - 1f;
    }

    /// <summary>
    /// The M2's Z-up to the render pipeline's Y-up. Identical to what
    /// ParseVertices does to every vertex; kept here so the emission geometry
    /// can be written in the terms the FILE uses and converted once.
    /// </summary>
    private static Vector3 Swap(Vector3 v) => new(v.X, v.Z, -v.Y);

    private struct Particle
    {
        public Vector3 Position;
        public Vector3 Velocity;
        public float Age;
        public float Life;
    }

    private struct GpuParticle
    {
        public Vector3 Centre;
        public float Size;
        public Vector4 Colour;
    }

    // ── Frame ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Advance every pool near the camera. `emitters` yields one entry per
    /// (placement, emitter) and is expected to be already distance-filtered by
    /// the caller; anything further than SimulationDistance is dropped here too.
    /// </summary>
    public void Simulate(
        float dt,
        Vector3 cameraPosition,
        IEnumerable<(string Path, Matrix4x4 Transform, M2ParticleEmitter Emitter,
                     int EmitterIndex, string TexturePath)> emitters)
    {
        SimulateMilliseconds = 0.0;
        if (!Enabled || _shader is null) return;

        Time += dt;
        _time = Time;

        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        foreach (var pool in _pools.Values) pool.TouchedThisFrame = false;

        float simSq = SimulationDistance * SimulationDistance;

        foreach (var (path, transform, emitter, index, texPath) in emitters)
        {
            var origin = Vector3.Transform(
                new Vector3(emitter.PosX, emitter.PosY, emitter.PosZ), transform);
            if (Vector3.DistanceSquared(origin, cameraPosition) > simSq) continue;

            // Rotation is in the key as well as position: two placements of the
            // same model in the same tenth-of-a-yard cell would otherwise share
            // one pool, which is precisely the per-instance invariant H5 asks
            // this key to enforce.
            var key = new PoolKey(path,
                (int)MathF.Round(transform.M41 * 10f),
                (int)MathF.Round(transform.M42 * 10f),
                (int)MathF.Round(transform.M43 * 10f), index,
                (int)MathF.Round((transform.M11 + transform.M21 * 3f + transform.M12 * 7f) * 100f));

            if (!_pools.TryGetValue(key, out var pool))
            {
                pool = new Pool
                {
                    Emitter = emitter,
                    Transform = transform,
                    TexturePath = texPath,
                    Seed = (uint)(key.GetHashCode() | 1),
                };
                _pools[key] = pool;
            }

            // Refreshed every frame, not frozen at creation. PoolKey only
            // carries the rounded translation, so a placement rebuilt with a new
            // rotation - or a model reloaded - would otherwise keep emitting
            // along an orientation that no longer exists.
            pool.Transform = transform;
            pool.Emitter = emitter;
            pool.TexturePath = texPath;
            pool.Scale = MathF.Sqrt(
                transform.M11 * transform.M11 +
                transform.M12 * transform.M12 +
                transform.M13 * transform.M13);
            if (pool.Scale <= 0f || float.IsNaN(pool.Scale)) pool.Scale = 1f;
            pool.TouchedThisFrame = true;
            Advance(pool, dt, origin);
        }

        // Pools nobody touched are out of range or gone with their placement.
        // Dropped rather than kept, because holding them would leak one pool per
        // doodad ever walked past.
        foreach (var key in _pools.Keys.ToArray())
            if (!_pools[key].TouchedThisFrame) _pools.Remove(key);

        int live = 0;
        foreach (var pool in _pools.Values) live += pool.Particles.Count;
        LiveParticles = live;
        ActivePools = _pools.Count;

        SimulateMilliseconds = System.Diagnostics.Stopwatch
            .GetElapsedTime(started).TotalMilliseconds;
    }

    private void Advance(Pool pool, float dt, Vector3 origin)
    {
        var e = pool.Emitter;
        var list = pool.Particles;

        // Age and integrate. Gravity is a plain -Z acceleration in world space:
        // WoW is Z-up and the value is yards per second squared.
        for (int i = list.Count - 1; i >= 0; i--)
        {
            var p = list[i];
            p.Age += dt;
            if (p.Age >= p.Life) { list.RemoveAt(i); continue; }
            p.Velocity.Z -= e.Gravity * dt;
            p.Position += p.Velocity * dt;
            list[i] = p;
        }

        // An emitter can be authored inert - dustwestfall's rate is 0.0 - so
        // this is a real case and not a guard against nothing.
        float rate = e.EmissionRate * DensityScale;
        if (rate <= 0f || e.Lifespan <= 0f) return;
        if (LiveParticles >= MaxParticles) return;

        pool.SpawnAccumulator += rate * dt;
        int spawn = (int)pool.SpawnAccumulator;
        pool.SpawnAccumulator -= spawn;

        // One frame's worth at most. A long frame - a map change, a breakpoint -
        // would otherwise dump a whole second of particles into one instant.
        int cap = (int)MathF.Ceiling(rate * 0.1f) + 1;
        if (spawn > cap) spawn = cap;

        for (int i = 0; i < spawn && list.Count < 4096; i++)
            list.Add(Spawn(pool, origin));
    }

    /// <summary>Set once per Simulate so Spawn does not have to be threaded a time.</summary>
    private double _time;

    private Particle Spawn(Pool pool, Vector3 origin)
    {
        var e = pool.Emitter;

        // PLANE emitter: born on a rectangle in the emitter's local XY, sized
        // by emissionAreaLength/Width, and thrown along local +Z spread by
        // verticalRange. The portal's range is pi - a full hemisphere.
        float lx = e.EmissionAreaLength * 0.5f * pool.Symmetric();
        float ly = e.EmissionAreaWidth * 0.5f * pool.Symmetric();

        float theta = e.VerticalRange * pool.Rand();

        // HorizontalRange narrows the sweep around local +X. Zero means "no
        // constraint" rather than "a degenerate line" - an emitter authored with
        // a narrow fan would otherwise emit a full ring, which is the difference
        // between a jet and a fountain.
        float sweep = e.HorizontalRange > 0f ? e.HorizontalRange : MathF.Tau;
        float phi = sweep * pool.Symmetric();

        // Everything below is written in the M2's own Z-up terms - the spawn
        // plane is local XY and the normal is +Z - and swapped once at the end
        // into the Y-up space the placement matrix expects, the same
        // `(x, y, z) -> (x, z, -y)` the vertices and the emitter position get.
        var cone = new Vector3(
            MathF.Sin(theta) * MathF.Cos(phi),
            MathF.Sin(theta) * MathF.Sin(phi),
            MathF.Cos(theta));

        // Radial in the plane, from the origin out through the spawn point. A
        // particle born at the exact centre has no radial direction, so it
        // falls back to the cone rather than to a zero vector.
        var planar = new Vector3(lx, ly, 0f);
        planar = planar.LengthSquared() > 1e-6f ? Vector3.Normalize(planar) : cone;

        var dirRaw = DirectionModel switch
        {
            PlaneDirection.Cone => cone,
            PlaneDirection.InPlaneRadial => planar,

            // Blended: verticalRange drives how far from the normal the
            // direction leans, so 0.087 (a torch) is almost the normal and pi
            // (the portal) is fully in-plane.
            _ => Vector3.Normalize(Vector3.Lerp(
                    new Vector3(0f, 0f, 1f), planar,
                    Math.Clamp(e.VerticalRange / MathF.PI, 0f, 1f))
                 + cone * 0.0001f),
        };

        var dirLocal = Swap(dirRaw);

        // THE BONE SPIN, and it is what makes this a portal rather than a haze.
        // The emitter rides a bone whose only animation is a full revolution
        // about its local X every 3.33 s, so the emission plane sweeps through
        // every orientation and throws the particles out into a disc. Applied
        // BEFORE the placement transform, because it is in model space.
        var spin = e.SampleBoneRotation(_time);
        var spawnLocal = Vector3.Transform(Swap(new Vector3(lx, ly, 0f)), spin);
        dirLocal = Vector3.Transform(dirLocal, spin);

        // Direction only - the placement's rotation must apply, its translation
        // must not, or every particle would be born at the world origin offset.
        var rotation = pool.Transform;
        rotation.M41 = rotation.M42 = rotation.M43 = 0f;

        var offsetWorld = Vector3.Transform(spawnLocal, rotation);
        var dirWorld = Vector3.Normalize(Vector3.TransformNormal(dirLocal, rotation));

        // The placement's scale reaches the spawn rectangle for free, because
        // the matrix carries it - but Normalize drops it from the direction and
        // nothing carried it into the sprite size. A doodad placed at scale 2
        // would get a correctly doubled emission area, half-size sprites and
        // unscaled speed: three different worlds in one effect.
        float speed = e.EmissionSpeed * (1f + e.SpeedVariation * pool.Symmetric()) * pool.Scale;

        return new Particle
        {
            Position = origin + offsetWorld,
            Velocity = dirWorld * speed,      // NEGATIVE speed pulls inward. H4.
            Age = 0f,
            Life = e.Lifespan,
        };
    }

    // ── Draw ─────────────────────────────────────────────────────────────────

    public unsafe void Render(Camera camera)
    {
        DrawnLastFrame = 0;
        DrawMilliseconds = 0.0;
        if (!Enabled || _shader is null || _pools.Count == 0) return;

        long started = System.Diagnostics.Stopwatch.GetTimestamp();

        // Group by texture AND blend mode: one draw per combination, and the
        // blend state changes between groups.
        var groups = new Dictionary<(string Tex, byte Blend), List<Pool>>();
        foreach (var pool in _pools.Values)
        {
            if (pool.Particles.Count == 0) continue;
            var key = (pool.TexturePath, pool.Emitter.BlendingType);
            if (!groups.TryGetValue(key, out var list)) groups[key] = list = [];
            list.Add(pool);
        }
        if (groups.Count == 0) return;


        _shader.Use();
        _shader.Set("uViewProjection", camera.RelativeViewProjection);
        _shader.Set("uCameraOrigin", camera.Position);
        // Screen-facing basis. Cross(forward, worldUp) degenerates when the
        // camera looks straight down - which is exactly how you look at a portal
        // on the ground - so fall back to the camera's flat right vector, which
        // is always defined.
        var forward = camera.Forward;
        var right = Vector3.Cross(forward, Vector3.UnitZ);
        right = right.LengthSquared() > 1e-6f ? Vector3.Normalize(right) : camera.FlatRight;
        var up = Vector3.Normalize(Vector3.Cross(right, forward));

        _shader.Set("uRight", right);
        _shader.Set("uUp", up);
        _shader.Set("uTexture", 0);

        _gl.Enable(EnableCap.Blend);

        // Depth TEST on, depth WRITE off. Particles must be hidden by the world
        // but must not hide each other - additive sprites that write depth
        // punch black holes in the ones behind them.
        //
        // The test is RESTORED, not just enabled: whatever draws after this must
        // get the state it had, and a silent z-fail in someone else's pass is a
        // miserable thing to track back to here.
        bool hadDepthTest = _gl.IsEnabled(EnableCap.DepthTest);
        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthMask(false);

        _gl.BindVertexArray(_vao);

        foreach (var ((texPath, blend), pools) in groups)
        {
            _scratch.Clear();
            foreach (var pool in pools) Fill(pool);
            if (_scratch.Count == 0) continue;

            SetBlend(blend);

            // A missing texture must SKIP, not bind name 0. Sampling an
            // incomplete texture returns opaque black, and black at the ramp's
            // alpha survives the discard - so an alpha-blended group would paint
            // black squares over the scene rather than drawing nothing.
            var tex = ResolveTexture(texPath);
            if (tex is null) continue;

            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, tex.Handle);

            UploadInstances();
            _gl.DrawArraysInstanced(PrimitiveType.TriangleStrip, 0, 4, (uint)_scratch.Count);
            DrawnLastFrame += _scratch.Count;
        }

        _gl.BindVertexArray(0);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
        _gl.DepthMask(true);
        if (!hadDepthTest) _gl.Disable(EnableCap.DepthTest);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _gl.Disable(EnableCap.Blend);

        DrawMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    private void Fill(Pool pool)
    {
        var e = pool.Emitter;
        foreach (var p in pool.Particles)
        {
            float t = p.Life > 0f ? p.Age / p.Life : 1f;
            e.SampleRamp(t, out var rgba, out float scale);
            if (scale <= 0f || rgba.W <= 0.002f) continue;

            _scratch.Add(new GpuParticle
            {
                Centre = p.Position,
                Size = scale * pool.Scale,
                Colour = rgba,
            });
        }
    }

    private void SetBlend(byte blend)
    {
        // Measured on 1086 emitters: ADD for light, alpha for matter. The other
        // modes appear rarely and fall back to alpha rather than to opaque,
        // because an opaque particle is a solid square and reads as a bug.
        switch (blend)
        {
            case 4:                                     // ADD
            case 3:                                     // no-alpha-add
                _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);
                break;
            case 5:                                     // mod
                _gl.BlendFunc(BlendingFactor.DstColor, BlendingFactor.Zero);
                break;
            case 6:                                     // mod2x
                _gl.BlendFunc(BlendingFactor.DstColor, BlendingFactor.SrcColor);
                break;
            default:
                _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                break;
        }
    }

    private Texture? ResolveTexture(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (_textures.TryGetValue(path, out var cached)) return cached;

        Texture? tex = null;
        try
        {
            var decoded = AdtTerrainReader.ReadBlpPixels(_config.ClientDataPath, path);
            if (decoded is { } d)
            {
                // ReadBlpPixels hands back BGRA; the sampler wants RGBA.
                var rgba = new byte[d.bgra.Length];
                for (int i = 0; i + 3 < d.bgra.Length; i += 4)
                {
                    rgba[i] = d.bgra[i + 2];
                    rgba[i + 1] = d.bgra[i + 1];
                    rgba[i + 2] = d.bgra[i];
                    rgba[i + 3] = d.bgra[i + 3];
                }
                tex = Texture.FromRgbaNoMips(_gl, rgba, d.width, d.height);
            }
            else
            {
                Console.WriteLine($"[particles] texture not found: {path}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[particles] texture {path} failed - {ex.Message}");
        }

        _textures[path] = tex;
        return tex;
    }

    // ── GL plumbing ──────────────────────────────────────────────────────────

    private unsafe void BuildBuffers()
    {
        // A unit quad as a triangle strip, expanded to face the camera in the
        // vertex shader. Four vertices, drawn instanced.
        float[] quad = { -0.5f, -0.5f, 0.5f, -0.5f, -0.5f, 0.5f, 0.5f, 0.5f };

        _vao = _gl.GenVertexArray();
        _gl.BindVertexArray(_vao);

        _quadVbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _quadVbo);
        fixed (float* p = quad)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(quad.Length * sizeof(float)),
                p, BufferUsageARB.StaticDraw);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), (void*)0);

        _instanceVbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _instanceVbo);

        const uint stride = 8 * sizeof(float);          // centre(3) size(1) colour(4)
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.VertexAttribDivisor(1, 1);

        _gl.EnableVertexAttribArray(2);
        _gl.VertexAttribPointer(2, 1, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        _gl.VertexAttribDivisor(2, 1);

        _gl.EnableVertexAttribArray(3);
        _gl.VertexAttribPointer(3, 4, VertexAttribPointerType.Float, false, stride, (void*)(4 * sizeof(float)));
        _gl.VertexAttribDivisor(3, 1);

        _gl.BindVertexArray(0);
    }

    private unsafe void UploadInstances()
    {
        int count = _scratch.Count;
        var data = new float[count * 8];
        for (int i = 0; i < count; i++)
        {
            var g = _scratch[i];
            int o = i * 8;
            data[o] = g.Centre.X; data[o + 1] = g.Centre.Y; data[o + 2] = g.Centre.Z;
            data[o + 3] = g.Size;
            data[o + 4] = g.Colour.X; data[o + 5] = g.Colour.Y;
            data[o + 6] = g.Colour.Z; data[o + 7] = g.Colour.W;
        }

        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _instanceVbo);
        fixed (float* p = data)
        {
            if (count > _instanceCapacity)
            {
                _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(data.Length * sizeof(float)),
                    p, BufferUsageARB.StreamDraw);
                _instanceCapacity = count;
            }
            else
            {
                _gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0,
                    (nuint)(data.Length * sizeof(float)), p);
            }
        }
    }

    public void Dispose()
    {
        foreach (var t in _textures.Values) t?.Dispose();
        _textures.Clear();
        _pools.Clear();
        if (_vao != 0) _gl.DeleteVertexArray(_vao);
        if (_quadVbo != 0) _gl.DeleteBuffer(_quadVbo);
        if (_instanceVbo != 0) _gl.DeleteBuffer(_instanceVbo);
        _shader?.Dispose();
    }

    /// <summary>Drop every pool, for a map change.</summary>
    public void Clear()
    {
        _pools.Clear();
        LiveParticles = 0;
        ActivePools = 0;
    }
}

using System.Numerics;
using Silk.NET.OpenGL;
using MSUIClient.Engine;
using MSUIClient.Formats;
using Shader = MSUIClient.Engine.Shader;
using Texture = MSUIClient.Engine.Texture;
using Camera = MSUIClient.Engine.Camera;

namespace MSUIClient.World.Spells;

/// <summary>
/// Isolated spell-effect particle simulation + renderer. Shares NO code and NO
/// tuning with the portal/doodad <see cref="MSUIClient.World.Particles.ParticleRenderer"/>.
/// It is a faithful port of benilla's particle simulation (crates/benilla/src/particles/
/// sim.rs, quads.rs, particles.rs) with ALL portal-specific knobs removed
/// (no centre-hole, no SpriteSizeScale, no reverse-converging, no portal spin).
///
/// Coordinate handling mirrors MSUI's proven model-space path: a model-space
/// emitter (M2 flag 0x10) stores each particle in the emitter's local Y-up frame and
/// is re-projected through the live bone/attach transform every frame; a world-space
/// emitter bakes the placement in at birth. Physics is benilla's exact integrator.
/// </summary>
public sealed class SpellParticleSystem : IDisposable
{
    private readonly GL _gl;
    private readonly ClientConfig _config;
    private Shader? _shader;
    private uint _vao, _quadVbo, _instanceVbo;
    private int _instanceCapacity;

    private readonly Dictionary<string, Texture?> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<PoolKey, Pool> _pools = new();
    private readonly List<GpuParticle> _scratch = new();
    private readonly List<PoolKey> _dead = new();
    private readonly HashSet<string> _loggedPaths = new();   // one placement log per emitter path (diagnostic)

    private double _time;

    /// <summary>Beyond this the emitter is not simulated (benilla drops with a distance LOD; we cull hard).</summary>
    public float SimulationDistance { get; set; } = 250f;

    public int LiveParticles { get; private set; }
    public int ActivePools { get; private set; }

    private const int MaxParticlesPerPool = 1024;   // benilla MAX_PARTICLES (particles.rs:43)

    public SpellParticleSystem(GL gl, ClientConfig config)
    {
        _gl = gl;
        _config = config;
    }

    public void LoadShaders(string shaderDir)
    {
        _shader = Shader.FromFiles(_gl,
            Path.Combine(shaderDir, "spell_particle.vert"),
            Path.Combine(shaderDir, "particle.frag"));
        BuildBuffers();
    }

    // ── Data ─────────────────────────────────────────────────────────────────

    private struct Particle
    {
        public Vector3 Position;   // stored frame: model-local (Y-up) or world
        public Vector3 Velocity;
        public float Age;
        public float Life;
        public uint Phase;
        public bool Fresh;         // benilla follow-delta skip on first integrate
    }

    private struct GpuParticle
    {
        public Vector3 Centre;
        public Vector3 AxisRight;
        public Vector3 AxisUp;
        public Vector4 Colour;
        public Vector4 CellRect;
        public float UvMode;
    }

    private readonly record struct PoolKey(string Path, int Emitter);

    private sealed class Pool
    {
        public M2ParticleEmitter Emitter = null!;
        public Matrix4x4 Transform;
        public string TexturePath = "";
        public int EmitterIndex;
        public bool ModelSpace;
        // Unit-attached effects (precast/cast/impact/channel) follow the attach point
        // every frame (benilla attach=Some(root)); missile trails are world-frozen
        // (attach=None). Attached world-space particles are stored RELATIVE to Origin.
        public bool Attached;
        public Vector3 Origin;              // emitter world position this frame
        public Quaternion? BoneRotationOverride;
        public double AnimationTime;
        public int AnimationId;
        public float Scale = 1f;            // instance scale (transform X-axis length)
        public float SpawnAccumulator;
        public bool GatePrev;               // benilla accumulate_emission rising-edge latch (burst + gate reset)
        public bool TouchedThisFrame;
        public int DrawnLastFrame;
        public uint Seed = 0x9E3779B9;
        public bool HasPreviousOrigin;
        public Vector3 FollowDelta;
        public float InheritAccumulator;
        public Vector3 InheritDelta;
        public Vector3 InheritVelocity;
        public readonly float[] Scalars = new float[10];
        public readonly List<Particle> Particles = new();

        public float Rand()
        {
            Seed ^= Seed << 13; Seed ^= Seed >> 17; Seed ^= Seed << 5;
            return (Seed & 0xFFFFFF) / 16777215f;
        }
        public float Symmetric() => Rand() * 2f - 1f;
        public uint NextPhase() { Seed ^= Seed << 13; Seed ^= Seed >> 17; Seed ^= Seed << 5; return Seed; }
    }

    // ── Simulate ─────────────────────────────────────────────────────────────

    public void Simulate(float dt, Vector3 cameraPosition,
        IEnumerable<(string Path, Matrix4x4 Transform, M2ParticleEmitter Emitter,
            int EmitterIndex, string TexturePath, double AnimationTime, int AnimationId,
            Vector3? LocalOrigin, Quaternion? LocalRotation, bool Attached)> emitters,
        Func<float, float, float, float?>? groundHeight = null)
    {
        _time += dt;
        foreach (Pool p in _pools.Values) p.TouchedThisFrame = false;

        float simSq = SimulationDistance * SimulationDistance;

        foreach (var (path, transform, emitter, index, texPath, suppliedAnimationTime, animationId,
            localOrigin, localRotation, attached) in emitters)
        {
            double animationTime = double.IsNaN(suppliedAnimationTime) ? _time : suppliedAnimationTime;

            // The emitter bone composes each particle's BIRTH; an animated bone
            // leaves a trail. LocalOrigin is the bone-evaluated emitter position.
            Vector3 emitterLocal = localOrigin ?? emitter.SampleBonePosition(
                animationTime, new Vector3(emitter.PosX, emitter.PosY, emitter.PosZ));
            Vector3 origin = Vector3.Transform(emitterLocal, transform);

            var key = new PoolKey(path, index);
            if (Vector3.DistanceSquared(origin, cameraPosition) > simSq)
            {
                // The reference freezes an owner-gated pool with no catch-up. Keep its origin
                // current so returning to range cannot manufacture a giant follow/inherit delta.
                if (_pools.TryGetValue(key, out Pool? frozen))
                {
                    frozen.TouchedThisFrame = true;
                    frozen.Origin = origin;
                    frozen.HasPreviousOrigin = true;
                    frozen.FollowDelta = Vector3.Zero;
                }
                continue;
            }

            if (_loggedPaths.Add($"{path}#{index}"))
                Console.WriteLine($"[spell-fx-place] {path} e{index} bone={emitter.Bone} origin=({origin.X:0.##},{origin.Y:0.##},{origin.Z:0.##}) " +
                    $"transformT=({transform.M41:0.##},{transform.M42:0.##},{transform.M43:0.##}) " +
                    $"localOrigin={(localOrigin.HasValue ? $"({localOrigin.Value.X:0.##},{localOrigin.Value.Y:0.##},{localOrigin.Value.Z:0.##})" : "null")}");

            if (!_pools.TryGetValue(key, out Pool? pool))
            {
                pool = new Pool { Seed = (uint)(key.GetHashCode() | 1) };
                _pools[key] = pool;
            }

            Vector3 emitterDelta = pool.HasPreviousOrigin ? origin - pool.Origin : Vector3.Zero;
            pool.TouchedThisFrame = true;
            pool.Transform = transform;
            pool.Emitter = emitter;
            pool.TexturePath = texPath;
            pool.EmitterIndex = index;
            pool.ModelSpace = (emitter.Flags & 0x10) != 0;
            pool.Attached = attached;
            pool.AnimationTime = animationTime;
            pool.AnimationId = animationId;
            pool.BoneRotationOverride = localRotation;
            pool.Origin = origin;
            pool.FollowDelta = FollowDelta(pool, emitterDelta, dt);
            pool.HasPreviousOrigin = true;
            UpdateInheritedMotion(pool, emitterDelta, dt);

            float[] defaults =
            [
                emitter.EmissionSpeed, emitter.SpeedVariation, emitter.VerticalRange,
                emitter.HorizontalRange, emitter.Gravity, emitter.Lifespan, emitter.EmissionRate,
                emitter.EmissionAreaLength, emitter.EmissionAreaWidth, emitter.ZSource,
            ];
            for (int s = 0; s < pool.Scalars.Length; s++)
                pool.Scalars[s] = emitter.SampleScalar(s, animationTime, animationId, defaults[s]);

            pool.Scale = MathF.Sqrt(
                transform.M11 * transform.M11 + transform.M12 * transform.M12 + transform.M13 * transform.M13);
            if (pool.Scale <= 0f || float.IsNaN(pool.Scale)) pool.Scale = 1f;

            Advance(pool, dt, cameraPosition, emit: true, groundHeight);
        }

        // Orphaned pools: drain their particles, then drop when empty.
        _dead.Clear();
        foreach (var (key, pool) in _pools)
        {
            if (pool.TouchedThisFrame) continue;
            Advance(pool, dt, cameraPosition, emit: false, groundHeight);
            if (pool.Particles.Count == 0) _dead.Add(key);
        }
        foreach (PoolKey key in _dead) _pools.Remove(key);

        LiveParticles = 0;
        foreach (Pool p in _pools.Values) LiveParticles += p.Particles.Count;
        ActivePools = _pools.Count;
    }

    private static Vector3 FollowDelta(Pool pool, Vector3 emitterDelta, float dt)
    {
        M2ParticleEmitter e = pool.Emitter;
        if ((e.Flags & 0x4000) == 0 || dt <= 0 || emitterDelta == Vector3.Zero ||
            MathF.Abs(e.FollowSpeed2 - e.FollowSpeed1) < 1e-6f)
            return Vector3.Zero;
        float slope = (e.FollowScale2 - e.FollowScale1) / (e.FollowSpeed2 - e.FollowSpeed1);
        float intercept = e.FollowScale1 - slope * e.FollowSpeed1;
        float fraction = Math.Clamp(slope * emitterDelta.Length() / dt + intercept, 0f, 1f);
        // Model-space and attached storage already rides the owner by the full delta. The
        // correction retains only the authored fraction. A detached world trail has no implicit
        // ride, so its correction is the fraction itself.
        Vector3 world = (pool.ModelSpace || pool.Attached ? fraction - 1f : fraction) * emitterDelta;
        return ToStoredVector(pool, world);
    }

    private static void UpdateInheritedMotion(Pool pool, Vector3 emitterDelta, float dt)
    {
        M2ParticleEmitter e = pool.Emitter;
        if ((e.Flags & 0x40) == 0 || dt <= 0)
        {
            pool.InheritAccumulator = 0;
            pool.InheritDelta = Vector3.Zero;
            pool.InheritVelocity = Vector3.Zero;
            return;
        }
        const float interval = 1f / 30f;
        pool.InheritAccumulator += dt;
        pool.InheritDelta += emitterDelta;
        if (pool.InheritAccumulator < interval) return;
        pool.InheritVelocity = pool.InheritDelta *
            (interval / pool.InheritAccumulator) * e.InheritScale;
        pool.InheritAccumulator = 0;
        pool.InheritDelta = Vector3.Zero;
    }

    private static Vector3 ToStoredVector(Pool pool, Vector3 world)
    {
        if (!pool.ModelSpace) return world;
        Matrix4x4 localToWorld = pool.Transform;
        localToWorld.M41 = localToWorld.M42 = localToWorld.M43 = 0;
        return Matrix4x4.Invert(localToWorld, out Matrix4x4 worldToLocal)
            ? Vector3.TransformNormal(world, worldToLocal)
            : world;
    }

    private void Advance(Pool pool, float dt, Vector3 cameraPosition, bool emit,
        Func<float, float, float, float?>? groundHeight)
    {
        M2ParticleEmitter e = pool.Emitter;
        List<Particle> list = pool.Particles;
        float sdt = MathF.Min(dt, 0.1f);   // benilla dt clamp (sim.rs:226)

        // Kill-outbound (benilla sim.rs:38-78): a Sphere emitter flagged 0x80 kills
        // any particle whose velocity points away from the emitter origin — the inward
        // stream stops at the centre instead of spraying out the far side. The origin
        // is the emitter's sphere centre: local 0 (model space) or pool.Origin (world).
        bool killOutbound = e.Shape == ParticleShape.Sphere && (e.Flags & 0x80) != 0;
        // Particles stored relative to the emitter (model-space, or attached world-space)
        // have their sphere centre at local zero; frozen missile particles at pool.Origin.
        Vector3 killOrigin = pool.ModelSpace || pool.Attached ? Vector3.Zero : pool.Origin;
        float gravity = pool.Scalars[4];

        for (int i = list.Count - 1; i >= 0; i--)
        {
            Particle p = list[i];
            p.Age += sdt;
            if (p.Age >= p.Life) { list.RemoveAt(i); continue; }
            if (p.Fresh) p.Fresh = false;
            else p.Position += pool.FollowDelta;

            Vector3 stepVel = p.Velocity;
            p.Position += p.Velocity * sdt;
            // Gravity as a closed-form half-step on the up axis: model-space local is
            // Y-up (MSUI Swap), world space is WoW Z-up.
            if (gravity != 0f)
            {
                if (pool.ModelSpace) { p.Position.Y -= 0.5f * gravity * sdt * sdt; p.Velocity.Y -= gravity * sdt; }
                else { p.Position.Z -= 0.5f * gravity * sdt * sdt; p.Velocity.Z -= gravity * sdt; }
            }
            if (e.Drag != 0f)
            {
                float f = MathF.Min(sdt * e.Drag, 1f);
                p.Velocity -= f * p.Velocity;
            }
            if (killOutbound && Vector3.Dot(stepVel, p.Position - killOrigin) > 0f)
            {
                list.RemoveAt(i); continue;
            }
            list[i] = p;
        }

        if (!emit) { pool.GatePrev = false; return; }

        // Emission (benilla accumulate_emission, particles.rs:747). Two gates ride the emitter's
        // clip clock, both load-bearing for one-shot effects (the fireball impact):
        //   • RATE track (+0xdc / Scalars[6]) — the authored spawn rate.
        //   • ENABLED track (+0x1dc, SampleEnabled) — the ON/OFF window; the reference forces the
        //     spawn rate to 0 while it is off (an impact's spray/lava/dust pour only 0.2-0.5s, not
        //     the whole 1.6s sequence). Turning off resets the accumulator (no carried-over pour).
        // And the BURST flag (0x8000): the emitter puffs ONCE on the rising edge of (enabled &&
        // rate>0) — trunc(rate) particles as a single burst (rate read as a COUNT), not a pour.
        // The impact's plume is a burst; without this it poured continuously and lingered.
        if (pool.Scalars[5] <= 0f) { pool.GatePrev = false; return; }

        bool emitting = pool.Emitter.SampleEnabled(pool.AnimationTime, pool.AnimationId);
        float rate = emitting ? MathF.Max(0f, pool.Scalars[6]) : 0f;
        bool gate = rate > 0f;

        // Distance LOD (benilla sim.rs:585): full rate inside 50yd, 25% floor from 87.5yd.
        float dist = Vector3.Distance(pool.Origin, cameraPosition);
        float distLod = Math.Clamp(1f - (dist - 50f) * 0.02f, 0.25f, 1f);

        int spawn;
        if ((e.Flags & 0x8000) != 0)
        {
            spawn = gate && !pool.GatePrev ? (int)(rate * distLod) : 0;   // one rising-edge puff
        }
        else
        {
            if (!emitting) pool.SpawnAccumulator = 0f;
            if (gate) pool.SpawnAccumulator += rate * distLod * sdt;
            spawn = (int)pool.SpawnAccumulator;
            pool.SpawnAccumulator -= spawn;
        }
        pool.GatePrev = gate;

        for (int i = 0; i < spawn && list.Count < MaxParticlesPerPool; i++)
            list.Add(Spawn(pool, groundHeight));
    }

    // ── Emission kernel (benilla emit_local, particles.rs:272-358) ────────────

    private static Vector3 Swap(Vector3 v) => new(v.X, v.Z, -v.Y);
    private static Vector3 Rot90Z(Vector3 v) => new(-v.Y, v.X, v.Z);

    private Particle Spawn(Pool pool, Func<float, float, float, float?>? groundHeight)
    {
        M2ParticleEmitter e = pool.Emitter;

        // Birth position + unit direction in the emitter's Z-up local frame.
        Vector3 posZ, dirZ;
        if (e.Shape == ParticleShape.Spline && e.SampleSpline(
                Math.Clamp(pool.Scalars[7], 0f, 1f) + pool.Rand() *
                (Math.Clamp(pool.Scalars[8], 0f, 1f) - Math.Clamp(pool.Scalars[7], 0f, 1f)),
                out posZ, out Vector3 tangent))
        {
            if (pool.Scalars[9] != 0f)
            {
                dirZ = posZ - new Vector3(0f, 0f, pool.Scalars[9]);
                dirZ = dirZ.LengthSquared() > 1e-12f ? Vector3.Normalize(dirZ) : Vector3.UnitZ;
            }
            else if (pool.Scalars[2] != 0f)
            {
                dirZ = RotateAround(Vector3.UnitZ, tangent, pool.Symmetric() * pool.Scalars[2]);
                if (pool.Scalars[3] != 0f)
                    posZ += pool.Rand() * pool.Scalars[3] * dirZ;
            }
            else dirZ = Vector3.Zero;
        }
        else if (e.Shape == ParticleShape.Sphere)
        {
            float r = pool.Scalars[7] + pool.Rand() * MathF.Max(0f, pool.Scalars[8] - pool.Scalars[7]);
            float lat = pool.Symmetric() * pool.Scalars[2];
            float lon = pool.Symmetric() * pool.Scalars[3];
            float clat = MathF.Cos(lat), slat = MathF.Sin(lat);
            float clon = MathF.Cos(lon), slon = MathF.Sin(lon);
            Vector3 shell = new(clat * clon, clat * slon, slat);
            posZ = r * shell;
            if (pool.Scalars[9] != 0f)
            {
                dirZ = posZ - new Vector3(0f, 0f, pool.Scalars[9]);
                dirZ = dirZ.LengthSquared() > 1e-12f ? Vector3.Normalize(dirZ) : Vector3.UnitZ;
            }
            else if ((e.Flags & 0x100) != 0) dirZ = Vector3.UnitZ;   // sphere_up
            else dirZ = shell;                                       // radial (negative speed => inward)
        }
        else
        {
            posZ = new Vector3(pool.Scalars[8] * 0.5f * pool.Symmetric(),
                               pool.Scalars[7] * 0.5f * pool.Symmetric(), 0f);
            if (pool.Scalars[9] != 0f)
            {
                dirZ = posZ - new Vector3(0f, 0f, pool.Scalars[9]);
                dirZ = dirZ.LengthSquared() > 1e-12f ? Vector3.Normalize(dirZ) : Vector3.UnitZ;
            }
            else
            {
                float theta = pool.Symmetric() * pool.Scalars[2];
                float phi = pool.Symmetric() * pool.Scalars[3];
                float st = MathF.Sin(theta), ct = MathF.Cos(theta);
                float sp = MathF.Sin(phi), cp = MathF.Cos(phi);
                dirZ = new Vector3(st * cp, st * sp, ct);
            }
        }

        float speed = pool.Scalars[0] * (1f + pool.Scalars[1] * pool.Symmetric());

        Vector3 localPos = Swap(Rot90Z(posZ));
        Vector3 localDir = Swap(Rot90Z(dirZ));

        if (pool.ModelSpace)
        {
            Vector3 velocity = localDir * speed;
            if ((e.Flags & 0x40) != 0 && pool.InheritVelocity != Vector3.Zero)
                velocity += (1f + pool.Scalars[1] * pool.Symmetric()) *
                    ToStoredVector(pool, pool.InheritVelocity);
            // Stored raw local (Y-up); re-projected through the live transform at draw.
            return new Particle
            {
                Position = localPos,
                Velocity = velocity,
                Age = 0f, Life = pool.Scalars[5], Phase = pool.NextPhase(), Fresh = true,
            };
        }

        // World space: bake the emitter placement's rotation/scale in at birth.
        // Attached effects store RELATIVE to Origin (re-anchored each frame in Fill so
        // the cloud rides the moving hand); missile trails store ABSOLUTE (world-frozen).
        Matrix4x4 rot = pool.Transform; rot.M41 = rot.M42 = rot.M43 = 0f;
        Vector3 offset = Vector3.Transform(localPos, rot);
        Vector3 worldDir = Vector3.TransformNormal(localDir, rot);
        worldDir = worldDir.LengthSquared() > 1e-12f ? Vector3.Normalize(worldDir) : Vector3.Zero;
        Vector3 velocityWorld = worldDir * speed * pool.Scale;
        if ((e.Flags & 0x40) != 0 && pool.InheritVelocity != Vector3.Zero)
            velocityWorld += (1f + pool.Scalars[1] * pool.Symmetric()) * pool.InheritVelocity;
        Vector3 worldPosition = pool.Origin + offset;
        if ((e.Flags & 0x2000) != 0 && groundHeight is not null &&
            groundHeight(worldPosition.X, worldPosition.Y, worldPosition.Z) is float ground &&
            worldPosition.Z - ground is >= 0f and <= 20f)
        {
            e.SampleRamp(0f, out _, out float birthSize);
            float instanceSize = (e.Flags & 0x20) != 0 ? pool.Scale : 1f;
            worldPosition.Z = ground + birthSize * instanceSize;
        }
        return new Particle
        {
            Position = pool.Attached ? worldPosition - pool.Origin : worldPosition,
            Velocity = velocityWorld,
            Age = 0f, Life = pool.Scalars[5], Phase = pool.NextPhase(), Fresh = true,
        };
    }

    private static Vector3 RotateAround(Vector3 vector, Vector3 axis, float angle)
    {
        if (axis.LengthSquared() <= 1e-12f) return vector;
        axis = Vector3.Normalize(axis);
        float c = MathF.Cos(angle), s = MathF.Sin(angle);
        return vector * c + Vector3.Cross(axis, vector) * s +
            axis * Vector3.Dot(axis, vector) * (1f - c);
    }

    // ── Render ───────────────────────────────────────────────────────────────

    public void Render(Camera camera)
    {
        if (_shader is null || _pools.Count == 0) return;

        // Group by (texture, blend); one draw per combination.
        var groups = new Dictionary<(string Tex, byte Blend), List<Pool>>();
        foreach (Pool pool in _pools.Values)
        {
            if (pool.Particles.Count == 0) continue;
            var key = (pool.TexturePath, pool.Emitter.BlendingType);
            if (!groups.TryGetValue(key, out List<Pool>? list)) groups[key] = list = new();
            list.Add(pool);
        }
        if (groups.Count == 0) return;

        Vector3 eye = camera.Position;
        Vector3 forward = camera.Forward;
        Vector3 worldUp = Vector3.UnitZ;
        Vector3 right = Vector3.Cross(forward, worldUp);
        right = right.LengthSquared() > 1e-6f ? Vector3.Normalize(right) : camera.FlatRight;
        Vector3 up = Vector3.Normalize(Vector3.Cross(right, forward));

        _shader.Use();
        _shader.Set("uViewProjection", camera.RelativeViewProjection);
        _shader.Set("uCameraOrigin", eye);
        _shader.Set("uTexture", 0);
        _shader.Set("uMipBias", 0f);

        _gl.Enable(EnableCap.Blend);
        bool hadDepthTest = _gl.IsEnabled(EnableCap.DepthTest);
        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthMask(false);
        _gl.BindVertexArray(_vao);

        foreach (var ((texPath, blend), pools) in groups)
        {
            _scratch.Clear();
            foreach (Pool pool in pools) Fill(pool, right, up);
            if (_scratch.Count == 0) continue;

            SetBlend(blend);
            Texture? tex = ResolveTexture(texPath);
            if (tex is null) continue;
            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, tex.Handle);

            UploadInstances();
            _gl.DrawArraysInstanced(PrimitiveType.TriangleStrip, 0, 4, (uint)_scratch.Count);
            foreach (Pool pool in pools) pool.DrawnLastFrame = pool.Particles.Count;
        }

        _gl.BindVertexArray(0);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
        _gl.DepthMask(true);
        if (!hadDepthTest) _gl.Disable(EnableCap.DepthTest);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _gl.Disable(EnableCap.Blend);
    }

    private void Fill(Pool pool, Vector3 cameraRight, Vector3 cameraUp)
    {
        M2ParticleEmitter e = pool.Emitter;
        Quaternion boneRot = pool.ModelSpace
            ? pool.BoneRotationOverride ?? Quaternion.Identity
            : Quaternion.Identity;
        Matrix4x4 root = pool.Transform;
        root.M41 = root.M42 = root.M43 = 0f;
        foreach (Particle p in pool.Particles)
        {
            float t = p.Life > 0f ? p.Age / p.Life : 1f;
            e.SampleRamp(t, out Vector4 rgba, out float size);
            float noise = TwinkleNoise(e.TwinkleSpeed, p.Age, p.Phase);
            if (e.TwinklePercent < 1f && noise > e.TwinklePercent) continue;
            float instanceScale = (e.Flags & 0x20) != 0 ? pool.Scale : 1f;
            float half = size * e.Twinkle(noise) * instanceScale;
            if (half <= 0f || rgba.W <= 0.002f) continue;

            Vector3 centre;
            Vector3 velocity;
            if (pool.ModelSpace)
            {
                centre = pool.Origin + Vector3.Transform(Vector3.Transform(p.Position, boneRot), root);
                velocity = Vector3.TransformNormal(Vector3.Transform(p.Velocity, boneRot), root);
            }
            else
            {
                centre = pool.Attached ? pool.Origin + p.Position : p.Position;
                velocity = p.Velocity;
            }

            bool drawHead = e.HeadOrTail is 0 or 2;
            bool drawTail = e.HeadOrTail >= 1;
            if (drawHead)
            {
                Vector3 baseRight = cameraRight;
                Vector3 baseUp = cameraUp;
                if ((e.Flags & 0x1000) != 0)
                {
                    baseRight = Vector3.TransformNormal(Vector3.Transform(-Vector3.UnitZ, boneRot), root);
                    baseUp = Vector3.TransformNormal(Vector3.Transform(-Vector3.UnitX, boneRot), root);
                    baseRight = NormalizeOr(baseRight, cameraRight) * pool.Scale;
                    baseUp = NormalizeOr(baseUp, cameraUp) * pool.Scale;
                }
                float angle = e.Spin * p.Age;
                if (angle < 0f && (p.Phase & 0x20) != 0) angle = -angle;
                float sine = MathF.Sin(angle), cosine = MathF.Cos(angle);
                Vector3 axisRight = (baseRight * cosine + baseUp * sine) * half;
                Vector3 axisUp = (baseUp * cosine - baseRight * sine) * half;
                AddQuad(centre, axisRight, axisUp, rgba, e.SampleHeadCellRect(t), 0f);
            }
            if (drawTail)
            {
                float effectiveTime = (e.Flags & 0x400) != 0
                    ? MathF.Min(e.TailTime, p.Age) : e.TailTime;
                Vector3 tail = -velocity * effectiveTime;
                float tr = Vector3.Dot(tail, cameraRight);
                float tu = Vector3.Dot(tail, cameraUp);
                float projectedLengthSquared = tr * tr + tu * tu;
                if (projectedLengthSquared < 7.7e-4f)
                    AddQuad(centre, cameraRight * half, cameraUp * half, rgba,
                        e.SampleTailCellRect(t), 0f);
                else
                {
                    Vector3 perpendicular = (cameraUp * tr - cameraRight * tu) *
                        (half / MathF.Sqrt(projectedLengthSquared));
                    AddQuad(centre + tail * .5f, perpendicular, tail * .5f, rgba,
                        e.SampleTailCellRect(t), 1f);
                }
            }
        }
    }

    private void AddQuad(Vector3 centre, Vector3 axisRight, Vector3 axisUp,
        Vector4 colour, Vector4 cellRect, float uvMode)
        => _scratch.Add(new GpuParticle
        {
            Centre = centre,
            AxisRight = axisRight,
            AxisUp = axisUp,
            Colour = colour,
            CellRect = cellRect,
            UvMode = uvMode,
        });

    private static Vector3 NormalizeOr(Vector3 value, Vector3 fallback)
        => value.LengthSquared() > 1e-12f ? Vector3.Normalize(value) : fallback;

    private void SetBlend(byte blend)
    {
        switch (blend)
        {
            case 3:
            case 4: _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One); break;              // additive
            case 5: _gl.BlendFunc(BlendingFactor.DstColor, BlendingFactor.Zero); break;             // mod
            case 6: _gl.BlendFunc(BlendingFactor.DstColor, BlendingFactor.SrcColor); break;         // mod2x
            default: _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha); break; // 0/1/2 alpha
        }
    }

    private Texture? ResolveTexture(string path)
    {
        if (path.Length == 0) return null;
        if (_textures.TryGetValue(path, out Texture? tex)) return tex;
        try
        {
            var decoded = AdtTerrainReader.ReadBlpPixels(_config.ClientDataPath, path);
            tex = decoded is { } d ? Texture.From2D(_gl, d.bgra, d.width, d.height,
                mipmaps: true, repeat: true) : null;
        }
        catch { tex = null; }
        _textures[path] = tex;
        return tex;
    }

    // ── Twinkle LUT (benilla quads.rs) ────────────────────────────────────────

    private static readonly float[] TwinkleLut = BuildTwinkleLut();
    private static float[] BuildTwinkleLut()
    {
        var t = new float[128];
        uint s = 0xC0FFEE11u;
        for (int i = 0; i < t.Length; i++) { s ^= s << 13; s ^= s >> 17; s ^= s << 5; t[i] = (s & 0xFFFFFF) / 16777215f; }
        return t;
    }
    private static float TwinkleNoise(float speed, float age, uint phase)
    {
        float w = speed * age;
        uint i = float.IsFinite(w) ? (uint)Math.Clamp(w, 0f, 255f) : 0u;
        return TwinkleLut[(int)((i + phase) & 0x7Fu)];
    }

    // ── GL buffers (mirror of ParticleRenderer.BuildBuffers) ──────────────────

    private unsafe void BuildBuffers()
    {
        float[] quad = { -1f, -1f, 1f, -1f, -1f, 1f, 1f, 1f };
        _vao = _gl.GenVertexArray();
        _gl.BindVertexArray(_vao);

        _quadVbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _quadVbo);
        fixed (float* p = quad)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(quad.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), (void*)0);

        _instanceVbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _instanceVbo);
        const uint stride = 18 * sizeof(float);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.VertexAttribDivisor(1, 1);
        _gl.EnableVertexAttribArray(2);
        _gl.VertexAttribPointer(2, 3, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        _gl.VertexAttribDivisor(2, 1);
        _gl.EnableVertexAttribArray(3);
        _gl.VertexAttribPointer(3, 3, VertexAttribPointerType.Float, false, stride, (void*)(6 * sizeof(float)));
        _gl.VertexAttribDivisor(3, 1);
        _gl.EnableVertexAttribArray(4);
        _gl.VertexAttribPointer(4, 4, VertexAttribPointerType.Float, false, stride, (void*)(9 * sizeof(float)));
        _gl.VertexAttribDivisor(4, 1);
        _gl.EnableVertexAttribArray(5);
        _gl.VertexAttribPointer(5, 4, VertexAttribPointerType.Float, false, stride, (void*)(13 * sizeof(float)));
        _gl.VertexAttribDivisor(5, 1);
        _gl.EnableVertexAttribArray(6);
        _gl.VertexAttribPointer(6, 1, VertexAttribPointerType.Float, false, stride, (void*)(17 * sizeof(float)));
        _gl.VertexAttribDivisor(6, 1);

        _gl.BindVertexArray(0);
    }

    private unsafe void UploadInstances()
    {
        int count = _scratch.Count;
        var data = new float[count * 18];
        for (int i = 0; i < count; i++)
        {
            GpuParticle g = _scratch[i];
            int o = i * 18;
            data[o] = g.Centre.X; data[o + 1] = g.Centre.Y; data[o + 2] = g.Centre.Z;
            data[o + 3] = g.AxisRight.X; data[o + 4] = g.AxisRight.Y; data[o + 5] = g.AxisRight.Z;
            data[o + 6] = g.AxisUp.X; data[o + 7] = g.AxisUp.Y; data[o + 8] = g.AxisUp.Z;
            data[o + 9] = g.Colour.X; data[o + 10] = g.Colour.Y; data[o + 11] = g.Colour.Z; data[o + 12] = g.Colour.W;
            data[o + 13] = g.CellRect.X; data[o + 14] = g.CellRect.Y; data[o + 15] = g.CellRect.Z; data[o + 16] = g.CellRect.W;
            data[o + 17] = g.UvMode;
        }
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _instanceVbo);
        fixed (float* p = data)
        {
            if (count > _instanceCapacity)
            {
                _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(data.Length * sizeof(float)), p, BufferUsageARB.StreamDraw);
                _instanceCapacity = count;
            }
            else _gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0, (nuint)(data.Length * sizeof(float)), p);
        }
    }

    /// <summary>Per-emitter census for diagnostics (mirrors ParticleRenderer.CensusReport).</summary>
    public string CensusReport()
    {
        if (_pools.Count == 0) return "spell-particles: NO POOLS";
        var sb = new System.Text.StringBuilder($"spell-particles: {_pools.Count} pools");
        foreach (Pool p in _pools.Values.OrderBy(p => p.TexturePath).ThenBy(p => p.EmitterIndex))
            sb.Append($" | e{p.EmitterIndex}[{(p.ModelSpace ? "model" : "world")}] blend={p.Emitter.BlendingType} " +
                $"live={p.Particles.Count} drawn={p.DrawnLastFrame} origin=({p.Origin.X:0.##},{p.Origin.Y:0.##},{p.Origin.Z:0.##}) tex={Path.GetFileName(p.TexturePath)}");
        return sb.ToString();
    }

    public void Dispose()
    {
        foreach (Texture? t in _textures.Values) t?.Dispose();
        _textures.Clear();
        if (_vao != 0) _gl.DeleteVertexArray(_vao);
        if (_quadVbo != 0) _gl.DeleteBuffer(_quadVbo);
        if (_instanceVbo != 0) _gl.DeleteBuffer(_instanceVbo);
        _shader?.Dispose();
    }
}

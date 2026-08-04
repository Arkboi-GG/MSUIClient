using System.Numerics;
using Silk.NET.OpenGL;
using MSUIClient.Engine;
using MSUIClient.Formats;
using Shader = MSUIClient.Engine.Shader;
using Texture = MSUIClient.Engine.Texture;
using Camera = MSUIClient.Engine.Camera;
using MSUIClient.World.Units;

namespace MSUIClient.World.Spells;

/// <summary>
/// Isolated spell-effect particle simulation + renderer. Shares NO code and NO
/// tuning with the portal/doodad <see cref="MSUIClient.World.Particles.ParticleRenderer"/>.
/// It is a faithful port of benilla's particle simulation (crates/benilla/src/particles/
/// sim.rs, quads.rs, particles.rs) with ALL portal-specific knobs removed
/// (no centre-hole, no SpriteSizeScale, no reverse-converging, no portal spin).
///
/// Coordinate handling keeps the two authored lanes distinct: a model-space emitter
/// (M2 flag 0x10) intentionally reprojects through the live model/bone transform, while an
/// ordinary emitter bakes the evaluated bone into each birth and stores it relative to the
/// effect/model root cloud anchor. Physics follows benilla's integrator where scoped.
/// </summary>
public sealed class SpellParticleSystem : IDisposable
{
    private readonly GL _gl;
    private readonly ClientConfig _config;
    private readonly MpqMount _mpq;
    private Shader? _shader;
    private uint _vao, _quadVbo, _instanceVbo;
    private int _instanceCapacity;

    private readonly Dictionary<string, Texture?> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, M2Model?> _geometryModels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<PoolKey, Pool> _pools = new();
    private readonly List<GpuParticle> _scratch = new();
    private readonly List<PoolKey> _dead = new();
    private readonly HashSet<string> _loggedPaths = new();   // one placement log per emitter path (diagnostic)

    private double _time;

    /// <summary>
    /// Optional diagnostic simulation wall. The reference has no particle-side hard cull inside
    /// the owner's draw set, so normal spell rendering leaves this infinite and relies on emission
    /// LOD plus the shared fragment far clip.
    /// </summary>
    public float SimulationDistance { get; set; } = float.PositiveInfinity;

    public Vector3 FogColor { get; set; } = new(.56f, .71f, .85f);
    public float FogStart { get; set; } = 350f;
    public float FogEnd { get; set; } = 777f;
    public float FarClip { get; set; } = 777f;
    public bool FogEnabled { get; set; } = true;
    public float DensityScale { get; set; } = 1f;

    public int LiveParticles { get; private set; }
    public int ActivePools { get; private set; }

    private const int MaxParticlesPerPool = 1024;   // benilla MAX_PARTICLES (particles.rs:43)

    public SpellParticleSystem(GL gl, ClientConfig config, MpqMount mpq)
    {
        _gl = gl;
        _config = config;
        _mpq = mpq;
        DensityScale = Math.Clamp(config.Render.ParticleDensity, 0.25f, 1f);
    }

    public void LoadShaders(string shaderDir)
    {
        _shader = Shader.FromFiles(_gl,
            Path.Combine(shaderDir, "spell_particle.vert"),
            Path.Combine(shaderDir, "spell_particle.frag"));
        BuildBuffers();
    }

    // ── Data ─────────────────────────────────────────────────────────────────

    private struct Particle
    {
        public Vector3 Position;   // S_model, root/attachment-relative S_anchor, or explicit S_world
        public Vector3 Velocity;
        public float Age;
        public float Life;
        public uint Phase;
        public bool Fresh;         // benilla follow-delta skip on first integrate
        public Quaternion Orientation;
        public Vector3 AngularVelocity;
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
        // These are independent decisions. RootCarriesCloud controls live translation of an
        // ordinary anchored cloud. HostAttachmentRotatesCloud controls A(t0)^-1 at birth and
        // A(t) at draw. Neither field says which bone composes births.
        public bool RootCarriesCloud;
        public bool HostAttachmentRotatesCloud;
        public Quaternion HostAttachmentRotation = Quaternion.Identity;
        public Vector3 RootCloudAnchorWorld; // effect/model instance root R(t), never emitter B_i(t)
        public Vector3 EmitterWorld;         // evaluated emitter bone origin B_i(t), births/motion only
        public Matrix4x4 EmitterModelFrame = Matrix4x4.Identity;
        public double AnimationTime;
        public int SequenceIndex;
        public float Scale = 1f;            // instance scale (transform X-axis length)
        public float SpawnAccumulator;
        public bool GatePrev;               // benilla accumulate_emission rising-edge latch (burst + gate reset)
        public bool TouchedThisFrame;
        public int DrawnLastFrame;
        public uint Seed = 0x9E3779B9;
        public bool HasPreviousEmitterWorld;
        public Vector3 FollowDelta;
        public float InheritAccumulator;
        public Vector3 InheritVelocity;
        public bool IsChild;
        public bool RecursionWired;
        public string RecursionPath = "";
        public readonly float[] Scalars = new float[10];
        public readonly List<Particle> Particles = new();
        public readonly List<Pool> Children = new();

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
            int EmitterIndex, string TexturePath, double AnimationTime, int SequenceIndex,
            Vector3? LocalOrigin, Matrix4x4? LocalFrame, bool RootCarriesCloud,
            bool HostAttachmentRotatesCloud)> emitters,
        Func<float, float, float, float?>? groundHeight = null)
    {
        _time += dt;
        float sdt = SpellParticleFrameLaw.SimulationStep(dt);
        foreach (Pool p in _pools.Values) p.TouchedThisFrame = false;

        float simSq = SimulationDistance * SimulationDistance;

        foreach (var (path, transform, emitter, index, texPath, suppliedAnimationTime, sequenceIndex,
            localOrigin, localFrame, rootCarriesCloud, hostAttachmentRotatesCloud) in emitters)
        {
            double animationTime = double.IsNaN(suppliedAnimationTime) ? _time : suppliedAnimationTime;

            // The emitter bone composes each particle's BIRTH; an animated bone
            // leaves a trail. LocalOrigin is the bone-evaluated emitter position.
            Vector3 emitterLocal = localOrigin ?? emitter.SampleBonePosition(
                animationTime, new Vector3(emitter.PosX, emitter.PosY, emitter.PosZ));
            Vector3 emitterWorld = Vector3.Transform(emitterLocal, transform);
            Vector3 rootCloudAnchorWorld = new(transform.M41, transform.M42, transform.M43);

            var key = new PoolKey(path, index);
            if (Vector3.DistanceSquared(emitterWorld, cameraPosition) > simSq)
            {
                // The reference freezes an owner-gated pool with no catch-up. Keep its origin
                // current so returning to range cannot manufacture a giant follow/inherit delta.
                if (_pools.TryGetValue(key, out Pool? frozen))
                {
                    frozen.TouchedThisFrame = true;
                    frozen.EmitterWorld = emitterWorld;
                    frozen.RootCloudAnchorWorld = rootCloudAnchorWorld;
                    frozen.HasPreviousEmitterWorld = true;
                    frozen.FollowDelta = Vector3.Zero;
                }
                continue;
            }

            if (_loggedPaths.Add($"{path}#{index}"))
                Console.WriteLine($"[spell-fx-place] {path} e{index} bone={emitter.Bone} " +
                    $"emitter=({emitterWorld.X:0.##},{emitterWorld.Y:0.##},{emitterWorld.Z:0.##}) " +
                    $"root=({rootCloudAnchorWorld.X:0.##},{rootCloudAnchorWorld.Y:0.##},{rootCloudAnchorWorld.Z:0.##}) " +
                    $"transformT=({transform.M41:0.##},{transform.M42:0.##},{transform.M43:0.##}) " +
                    $"localOrigin={(localOrigin.HasValue ? $"({localOrigin.Value.X:0.##},{localOrigin.Value.Y:0.##},{localOrigin.Value.Z:0.##})" : "null")}");

            if (!_pools.TryGetValue(key, out Pool? pool))
            {
                pool = new Pool { Seed = (uint)(key.GetHashCode() | 1) };
                _pools[key] = pool;
            }

            Vector3 emitterDelta = pool.HasPreviousEmitterWorld
                ? emitterWorld - pool.EmitterWorld : Vector3.Zero;
            pool.TouchedThisFrame = true;
            pool.Transform = transform;
            pool.Emitter = emitter;
            pool.TexturePath = texPath;
            pool.EmitterIndex = index;
            pool.ModelSpace = (emitter.Flags & 0x10) != 0;
            pool.RootCarriesCloud = rootCarriesCloud;
            pool.HostAttachmentRotatesCloud = hostAttachmentRotatesCloud;
            pool.AnimationTime = animationTime;
            pool.SequenceIndex = sequenceIndex;
            pool.EmitterModelFrame = localFrame ?? Matrix4x4.CreateFromQuaternion(
                emitter.SampleBoneRotation(animationTime));
            pool.HostAttachmentRotation = hostAttachmentRotatesCloud
                ? RotationOf(transform) : Quaternion.Identity;
            pool.RootCloudAnchorWorld = rootCloudAnchorWorld;
            pool.EmitterWorld = emitterWorld;
            pool.FollowDelta = FollowDelta(pool, emitterDelta, sdt);
            pool.HasPreviousEmitterWorld = true;
            UpdateInheritedMotion(pool, emitterDelta, sdt);

            // The original runtime animates only emission rate and enabled. All other scalar
            // tracks are baked from value zero when the emitter definition is loaded.
            SetStaticScalars(pool, emitter);
            pool.Scalars[6] = emitter.SampleScalarBySequence(6, animationTime, sequenceIndex,
                emitter.EmissionRate);

            pool.Scale = SpellParticleFrameLaw.XScale(EmitterLinearFrame(pool));

            WireChildren(pool);
            Advance(pool, sdt, cameraPosition, emit: true, groundHeight);
        }

        // Orphaned pools: drain their particles, then drop when empty.
        _dead.Clear();
        foreach (var (key, pool) in _pools)
        {
            if (pool.TouchedThisFrame) continue;
            Advance(pool, sdt, cameraPosition, emit: false, groundHeight);
            if (pool.Particles.Count == 0 && pool.Children.All(c => c.Particles.Count == 0))
                _dead.Add(key);
        }
        foreach (PoolKey key in _dead) _pools.Remove(key);

        LiveParticles = 0;
        foreach (Pool p in _pools.Values)
        {
            LiveParticles += p.Particles.Count;
            foreach (Pool child in p.Children) LiveParticles += child.Particles.Count;
        }
        ActivePools = _pools.Count + _pools.Values.Sum(p => p.Children.Count);
    }

    /// <summary>
    /// Resolve one recursion model into the parent's private child emitters. The vanilla client
    /// inspects only the first four records, then rejects records without a texture, a positive
    /// lifespan, or any positive rate key. Children are deliberately not inserted in <see cref="_pools"/>:
    /// their lifetime and drain are owned by the parent emitter.
    /// </summary>
    private void WireChildren(Pool parent)
    {
        if (parent.RecursionWired) return;
        parent.RecursionWired = true;
        if (parent.Emitter.RecursionModel.Length == 0) return;

        string path = SpellVisualCatalog.ModelPath(parent.Emitter.RecursionModel);
        parent.RecursionPath = path;
        M2Model? model = ResolveGeometryModel(path);
        if (model is null) return;

        uint seed = BitOperations.RotateLeft(parent.Seed, 7) | 1u;
        int inspected = Math.Min(4, model.ParticleEmitters.Count);
        for (int i = 0; i < inspected; i++)
        {
            M2ParticleEmitter emitter = model.ParticleEmitters[i];
            string texture = emitter.Texture < model.Textures.Count
                ? model.Textures[emitter.Texture].Filename : "";
            bool hasPositiveRate = emitter.ScalarTracks[6].Keys.Any(v => v > 0f);
            if (texture.Length == 0 || emitter.Lifespan <= 0f || !hasPositiveRate) continue;

            seed = unchecked(seed * 0x9E3779B9u) | 1u;
            var child = new Pool
            {
                Emitter = emitter,
                TexturePath = texture,
                EmitterIndex = i,
                SequenceIndex = model.Sequences.Count > 0 ? 0 : -1,
                Seed = seed,
                IsChild = true,
                RecursionWired = true,
            };
            SetStaticScalars(child, emitter);
            parent.Children.Add(child);
        }

        Console.WriteLine($"[spell-fx-child] {Path.GetFileName(path)} parent=e{parent.EmitterIndex} " +
            $"children={parent.Children.Count}/{inspected}");
    }

    private static void SetStaticScalars(Pool pool, M2ParticleEmitter emitter)
    {
        pool.Scalars[0] = emitter.EmissionSpeed;
        pool.Scalars[1] = emitter.SpeedVariation;
        pool.Scalars[2] = emitter.VerticalRange;
        pool.Scalars[3] = emitter.HorizontalRange;
        pool.Scalars[4] = emitter.Gravity;
        pool.Scalars[5] = emitter.Lifespan;
        pool.Scalars[6] = emitter.EmissionRate;
        pool.Scalars[7] = emitter.EmissionAreaLength;
        pool.Scalars[8] = emitter.EmissionAreaWidth;
        pool.Scalars[9] = emitter.ZSource;
    }

    private static Vector3 FollowDelta(Pool pool, Vector3 emitterDelta, float dt)
    {
        M2ParticleEmitter e = pool.Emitter;
        Vector3 world = SpellParticleFrameLaw.FollowCorrectionWorld((e.Flags & 0x4000) != 0,
            emitterDelta, dt,
            e.FollowSpeed1, e.FollowScale1, e.FollowSpeed2, e.FollowScale2,
            pool.ModelSpace || pool.RootCarriesCloud);
        return ToStoredVector(pool, world);
    }

    private static void UpdateInheritedMotion(Pool pool, Vector3 emitterDelta, float dt)
    {
        M2ParticleEmitter e = pool.Emitter;
        if ((e.Flags & 0x40) == 0)
        {
            pool.InheritAccumulator = 0;
            pool.InheritVelocity = Vector3.Zero;
            return;
        }
        SpellParticleFrameLaw.UpdateInheritedMotion(dt, emitterDelta, e.InheritScale,
            pool.Particles.Count > 0, ref pool.InheritAccumulator, ref pool.InheritVelocity);
    }

    private static Matrix4x4 EmitterLinearFrame(Pool pool)
        => SpellParticleFrameLaw.ComposeEmitterLinearFrame(pool.EmitterModelFrame, pool.Transform);

    private static Vector3 ToStoredVector(Pool pool, Vector3 world)
    {
        if (pool.ModelSpace)
            return SpellParticleFrameLaw.StoreModelVector(world, EmitterLinearFrame(pool));
        return pool.HostAttachmentRotatesCloud
            ? SpellParticleFrameLaw.StoreVector(world, pool.HostAttachmentRotation)
            : world;
    }

    private void Advance(Pool pool, float dt, Vector3 cameraPosition, bool emit,
        Func<float, float, float, float?>? groundHeight)
    {
        // The parent integrates and emits first. Its private children must observe that exact
        // post-integration, post-birth pool, then integrate their own fresh births this frame.
        AdvanceParent(pool, dt, cameraPosition, emit, groundHeight);
        float sdt = MathF.Min(dt, 0.1f);
        float dist = Vector3.Distance(pool.EmitterWorld, cameraPosition);
        float emissionScale = DensityScale *
            Math.Clamp(1f - (dist - 50f) * 0.02f, 0.25f, 1f);
        DriveChildren(pool, sdt, emissionScale, emit);
    }

    private static void AccumulateEmission(bool burst, float rate, bool emitting, float scale,
        float dt, ref float accumulator, ref bool gatePrev)
    {
        if (!emitting) accumulator = 0f;
        rate = emitting ? MathF.Max(rate, 0f) : 0f;
        bool gate = rate > 0f;
        if (burst)
        {
            if (gate && !gatePrev) accumulator = MathF.Truncate(rate * scale);
        }
        else if (gate) accumulator += rate * scale * dt;
        gatePrev = gate;
    }

    private void DriveChildren(Pool parent, float sdt, float distLod, bool drive)
    {
        foreach (Pool child in parent.Children)
        {
            // The child uses its parent's storage lane and live birth fold. The parent particle
            // replaces the child record's context translation; the child record position itself
            // therefore never composes into the final birth position.
            child.Transform = parent.Transform;
            child.ModelSpace = parent.ModelSpace;
            child.RootCarriesCloud = parent.RootCarriesCloud;
            child.HostAttachmentRotatesCloud = parent.HostAttachmentRotatesCloud;
            child.HostAttachmentRotation = parent.HostAttachmentRotation;
            child.RootCloudAnchorWorld = parent.RootCloudAnchorWorld;
            child.EmitterWorld = parent.EmitterWorld;
            child.EmitterModelFrame = parent.EmitterModelFrame;
            child.Scale = parent.Scale;
            child.AnimationTime = parent.AnimationTime;
            child.FollowDelta = Vector3.Zero;
            child.InheritVelocity = Vector3.Zero;

            if (drive)
            {
                bool emitting = child.Emitter.SampleEnabledBySequence(parent.AnimationTime,
                    child.SequenceIndex);
                float rate = child.Emitter.SampleScalarBySequence(6, parent.AnimationTime,
                    child.SequenceIndex, child.Scalars[6]);
                foreach (Particle parentParticle in parent.Particles)
                {
                    AccumulateEmission((child.Emitter.Flags & 0x8000) != 0, rate, emitting,
                        distLod, sdt, ref child.SpawnAccumulator, ref child.GatePrev);
                    while (child.SpawnAccumulator >= 1f &&
                        child.Particles.Count < MaxParticlesPerPool)
                    {
                        child.SpawnAccumulator -= 1f;
                        Particle born = Spawn(child, groundHeight: null);
                        // A world-history parent stores absolute positions; convert the child's
                        // ordinary birth back to an offset before composing the parent particle.
                        if (!child.ModelSpace && !child.RootCarriesCloud)
                            born.Position -= child.EmitterWorld;
                        born.Position += parentParticle.Position;
                        if ((child.Emitter.Flags & 0x40) != 0)
                            born.Velocity += (1f + child.Scalars[1] * child.Symmetric()) *
                                parentParticle.Velocity;
                        born.Phase = child.NextPhase();
                        born.Orientation = Quaternion.Identity;
                        born.AngularVelocity = Vector3.Zero;
                        child.Particles.Add(born);
                    }
                }
            }
            else
            {
                child.SpawnAccumulator = 0f;
                child.GatePrev = false;
            }

            IntegrateChildParticles(child, sdt);
        }
    }

    private static void IntegrateChildParticles(Pool child, float sdt)
    {
        List<Particle> list = child.Particles;
        float gravity = child.Scalars[4];
        float drag = child.Emitter.Drag;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            Particle p = list[i];
            p.Age += sdt;
            if (p.Age >= p.Life) { list.RemoveAt(i); continue; }
            p.Fresh = false;
            p.Position += p.Velocity * sdt;
            if (gravity != 0f)
            {
                if (child.ModelSpace)
                {
                    p.Position.Y -= .5f * gravity * sdt * sdt;
                    p.Velocity.Y -= gravity * sdt;
                }
                else if (child.HostAttachmentRotatesCloud)
                {
                    Vector3 g = Vector3.Transform(-Vector3.UnitZ * gravity,
                        Quaternion.Inverse(child.HostAttachmentRotation));
                    p.Position += .5f * g * sdt * sdt;
                    p.Velocity += g * sdt;
                }
                else
                {
                    p.Position.Z -= .5f * gravity * sdt * sdt;
                    p.Velocity.Z -= gravity * sdt;
                }
            }
            if (drag != 0f) p.Velocity -= MathF.Min(sdt * drag, 1f) * p.Velocity;
            list[i] = p;
        }
    }

    private void AdvanceParent(Pool pool, float dt, Vector3 cameraPosition, bool emit,
        Func<float, float, float, float?>? groundHeight)
    {
        M2ParticleEmitter e = pool.Emitter;
        List<Particle> list = pool.Particles;
        float sdt = MathF.Min(dt, 0.1f);   // benilla dt clamp (sim.rs:226)

        // Kill-outbound (benilla sim.rs:38-78): a Sphere emitter flagged 0x80 kills
        // any particle whose velocity points away from the emitter origin — the inward
        // stream stops at the centre instead of spraying out the far side. The origin
        // is the emitter's sphere centre in the particle's persistent storage frame.
        bool killOutbound = e.Shape == ParticleShape.Sphere && (e.Flags & 0x80) != 0;
        Vector3 killOrigin = pool.ModelSpace
            ? Vector3.Zero
            : pool.RootCarriesCloud
                ? SpellParticleFrameLaw.StoreAtBirth(pool.EmitterWorld,
                    pool.RootCloudAnchorWorld, pool.HostAttachmentRotation)
                : pool.EmitterWorld;
        float gravity = pool.Scalars[4];

        for (int i = list.Count - 1; i >= 0; i--)
        {
            Particle p = list[i];
            p.Age += sdt;
            if (p.Age >= p.Life) { list.RemoveAt(i); continue; }
            if (p.Fresh) p.Fresh = false;
            else p.Position += pool.FollowDelta;

            if (p.AngularVelocity.LengthSquared() > 1e-8f)
            {
                float angularSpeed = p.AngularVelocity.Length();
                Quaternion delta = Quaternion.CreateFromAxisAngle(
                    p.AngularVelocity / angularSpeed, angularSpeed * sdt);
                p.Orientation = Quaternion.Normalize(p.Orientation * delta);
            }

            Vector3 stepVel = p.Velocity;
            p.Position += p.Velocity * sdt;
            // Gravity as a closed-form half-step on the up axis: model-space local is
            // Y-up (MSUI Swap), world space is WoW Z-up.
            if (gravity != 0f)
            {
                if (pool.ModelSpace) { p.Position.Y -= 0.5f * gravity * sdt * sdt; p.Velocity.Y -= gravity * sdt; }
                else if (pool.HostAttachmentRotatesCloud)
                {
                    Vector3 g = Vector3.Transform(-Vector3.UnitZ * gravity,
                        Quaternion.Inverse(pool.HostAttachmentRotation));
                    p.Position += .5f * g * sdt * sdt;
                    p.Velocity += g * sdt;
                }
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

        bool emitting = pool.Emitter.SampleEnabledBySequence(pool.AnimationTime,
            pool.SequenceIndex);
        float rate = emitting ? MathF.Max(0f, pool.Scalars[6]) : 0f;
        bool gate = rate > 0f;

        // Distance LOD (benilla sim.rs:585): full rate inside 50yd, 25% floor from 87.5yd.
        float dist = Vector3.Distance(pool.EmitterWorld, cameraPosition);
        float distLod = DensityScale *
            Math.Clamp(1f - (dist - 50f) * 0.02f, 0.25f, 1f);

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
        (Quaternion orientation, Vector3 angularVelocity) = ModelParticleSpin(pool);

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
                Age = 0f, Life = pool.Scalars[5],
                Phase = pool.IsChild ? 0u : pool.NextPhase(), Fresh = true,
                Orientation = orientation, AngularVelocity = angularVelocity,
            };
        }

        // Ordinary anchored space: the evaluated emitter bone composes this birth, but persistent
        // storage is relative to the effect/model ROOT. Draw never folds the emitter bone again.
        // The currently reference-limited missile lane can still request absolute world storage.
        Matrix4x4 emitterFrame = EmitterLinearFrame(pool);
        Vector3 offset = SpellParticleFrameLaw.DrawModelVector(localPos, emitterFrame);
        Vector3 velocityWorld = SpellParticleFrameLaw.DrawModelVector(localDir * speed, emitterFrame);
        if ((e.Flags & 0x40) != 0 && pool.InheritVelocity != Vector3.Zero)
            velocityWorld += (1f + pool.Scalars[1] * pool.Symmetric()) * pool.InheritVelocity;
        Vector3 worldPosition = pool.EmitterWorld + offset;
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
            Position = pool.RootCarriesCloud
                ? (pool.IsChild
                    ? SpellParticleFrameLaw.StoreVector(worldPosition - pool.EmitterWorld,
                        pool.HostAttachmentRotation)
                    : SpellParticleFrameLaw.StoreAtBirth(worldPosition,
                        pool.RootCloudAnchorWorld, pool.HostAttachmentRotation))
                : worldPosition,
            Velocity = pool.RootCarriesCloud ? ToStoredVector(pool, velocityWorld) : velocityWorld,
            Age = 0f, Life = pool.Scalars[5],
            Phase = pool.IsChild ? 0u : pool.NextPhase(), Fresh = true,
            Orientation = pool.HostAttachmentRotatesCloud
                ? AnchoredParticleOrientation(pool, orientation)
                : WorldParticleOrientation(pool, orientation),
            AngularVelocity = angularVelocity,
        };
    }

    private static (Quaternion Orientation, Vector3 AngularVelocity) ModelParticleSpin(Pool pool)
    {
        M2ParticleEmitter e = pool.Emitter;
        if (pool.IsChild || e.GeometryModel.Length == 0)
            return (Quaternion.Identity, Vector3.Zero);

        Vector3 min = e.AngularVelocityMin;
        Vector3 range = e.AngularVelocityMax - min;
        Vector3 wow = new(
            min.X + pool.Rand() * range.X,
            (1f + pool.Rand()) * range.Y,
            (1f + pool.Rand()) * range.Z);
        if ((e.Flags & 0x200) != 0)
        {
            if ((pool.NextPhase() & 1) == 0) wow.X = -wow.X;
            if ((pool.NextPhase() & 1) == 0) wow.Y = -wow.Y;
            if ((pool.NextPhase() & 1) == 0) wow.Z = -wow.Z;
        }
        Quaternion r90 = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f);
        return (r90, Swap(wow));
    }

    private static Quaternion WorldParticleOrientation(Pool pool, Quaternion local)
    {
        Matrix4x4 composed = Matrix4x4.CreateFromQuaternion(local) *
            EmitterLinearFrame(pool);
        return Matrix4x4.Decompose(composed, out _, out Quaternion rotation, out _)
            ? Quaternion.Normalize(rotation) : local;
    }

    private static Quaternion AnchoredParticleOrientation(Pool pool, Quaternion local)
    {
        Matrix4x4 composed = Matrix4x4.CreateFromQuaternion(local) *
            pool.EmitterModelFrame;
        return Matrix4x4.Decompose(composed, out _, out Quaternion rotation, out _)
            ? Quaternion.Normalize(rotation) : local;
    }

    private static Quaternion RotationOf(Matrix4x4 transform)
        => Matrix4x4.Decompose(transform, out _, out Quaternion rotation, out _)
            ? Quaternion.Normalize(rotation) : Quaternion.Identity;

    private static Vector3 RotateAround(Vector3 vector, Vector3 axis, float angle)
    {
        if (axis.LengthSquared() <= 1e-12f) return vector;
        axis = Vector3.Normalize(axis);
        float c = MathF.Cos(angle), s = MathF.Sin(angle);
        return vector * c + Vector3.Cross(axis, vector) * s +
            axis * Vector3.Dot(axis, vector) * (1f - c);
    }

    // ── Render ───────────────────────────────────────────────────────────────

    private IEnumerable<Pool> PoolsWithChildren()
    {
        foreach (Pool parent in _pools.Values)
        {
            yield return parent;
            foreach (Pool child in parent.Children) yield return child;
        }
    }

    public void Render(Camera camera)
    {
        if (_shader is null || _pools.Count == 0) return;

        // Group by (texture, blend); one draw per combination.
        var groups = new Dictionary<(string Tex, byte Blend, int FogPolicy), List<Pool>>();
        foreach (Pool pool in PoolsWithChildren())
        {
            if (pool.Particles.Count == 0 ||
                (!pool.IsChild && pool.Emitter.GeometryModel.Length > 0)) continue;
            int fogPolicy = (pool.Emitter.Flags & 0x08) != 0
                ? 0 : pool.Emitter.BlendingType is 3 or 4 ? 2 : 1;
            var key = (pool.TexturePath, pool.Emitter.BlendingType, fogPolicy);
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
        _shader.Set("uView", camera.RelativeView);
        _shader.Set("uCameraOrigin", eye);
        _shader.Set("uTexture", 0);
        _shader.Set("uMipBias", 0f);
        _shader.Set("uFogEnabled", FogEnabled ? 1 : 0);
        _shader.Set("uFogColor", FogColor);
        _shader.Set("uFogStart", FogStart);
        _shader.Set("uFogEnd", FogEnd);
        _shader.Set("uFarClip", FarClip);

        bool hadDepthTest = _gl.IsEnabled(EnableCap.DepthTest);
        _gl.Enable(EnableCap.DepthTest);
        _gl.BindVertexArray(_vao);

        foreach (var ((texPath, blend, fogPolicy), pools) in
            groups.OrderBy(g => g.Key.Blend is 0 or 1 ? 0 : 1))
        {
            _scratch.Clear();
            foreach (Pool pool in pools) Fill(pool, right, up);
            if (_scratch.Count == 0) continue;

            SetBlend(blend);
            _shader.Set("uFogPolicy", fogPolicy);
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

    /// <summary>
    /// Expand geometry-model emitters into ordinary spell-mesh draws. Benilla routes these tiny
    /// instances through the generic M2 material path: no billboard atlas, a 128-instance draw
    /// cap per emitter, over-life tint/alpha/size, and the particle's integrated quaternion.
    /// </summary>
    public IEnumerable<SpellMeshDraw> GeometryInstances()
    {
        foreach (var (key, pool) in _pools)
        {
            M2ParticleEmitter emitter = pool.Emitter;
            if (emitter.GeometryModel.Length == 0 || pool.Particles.Count == 0) continue;
            string path = SpellVisualCatalog.ModelPath(emitter.GeometryModel);
            M2Model? model = ResolveGeometryModel(path);
            if (model is null || !model.IsValid) continue;

            Matrix4x4 emitterFrame = EmitterLinearFrame(pool);
            float instanceScale = (emitter.Flags & 0x20) != 0 ? pool.Scale : 1f;
            int sequenceIndex = model.Sequences.Count > 0 ? 0 : -1;
            int count = Math.Min(pool.Particles.Count, 128);
            for (int i = 0; i < count; i++)
            {
                Particle particle = pool.Particles[i];
                float t = particle.Life > 0f ? particle.Age / particle.Life : 1f;
                emitter.SampleRamp(t, out Vector4 rgba, out float size);
                if (size <= 0f || rgba.W <= 0f) continue;

                Vector3 centre;
                Quaternion rotation = particle.Orientation;
                if (pool.ModelSpace)
                {
                    centre = SpellParticleFrameLaw.DrawModelPoint(particle.Position,
                        pool.EmitterWorld, emitterFrame);
                    Matrix4x4 composed = Matrix4x4.CreateFromQuaternion(rotation) *
                        emitterFrame;
                    if (Matrix4x4.Decompose(composed, out _, out Quaternion posed, out _))
                        rotation = Quaternion.Normalize(posed);
                }
                else
                {
                    if (pool.RootCarriesCloud)
                    {
                        centre = SpellParticleFrameLaw.DrawWorld(particle.Position,
                            pool.RootCloudAnchorWorld, pool.HostAttachmentRotation);
                        if (pool.HostAttachmentRotatesCloud)
                        {
                            Matrix4x4 composed = Matrix4x4.CreateFromQuaternion(rotation) *
                                Matrix4x4.CreateFromQuaternion(pool.HostAttachmentRotation);
                            if (Matrix4x4.Decompose(composed, out _, out Quaternion posed, out _))
                                rotation = Quaternion.Normalize(posed);
                        }
                    }
                    else centre = particle.Position;
                }

                Matrix4x4 transform = Matrix4x4.CreateScale(size * instanceScale) *
                    Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(centre);
                long id = ((long)key.GetHashCode() << 32) ^ (uint)i;
                yield return new SpellMeshDraw(id, path, model, transform, 0f, sequenceIndex,
                    false, null, new Vector3(rgba.X, rgba.Y, rgba.Z), rgba.W);
            }
        }
    }

    private M2Model? ResolveGeometryModel(string path)
    {
        if (_geometryModels.TryGetValue(path, out M2Model? cached)) return cached;
        try
        {
            byte[]? bytes = _mpq.ReadFile(path);
            return _geometryModels[path] = bytes is null ? null : M2Reader.Parse(bytes);
        }
        catch
        {
            return _geometryModels[path] = null;
        }
    }

    private void Fill(Pool pool, Vector3 cameraRight, Vector3 cameraUp)
    {
        M2ParticleEmitter e = pool.Emitter;
        Matrix4x4 emitterFrame = EmitterLinearFrame(pool);
        foreach (Particle p in pool.Particles)
        {
            float t = p.Life > 0f ? p.Age / p.Life : 1f;
            e.SampleRamp(t, out Vector4 rgba, out float size);
            float noise = TwinkleNoise(e.TwinkleSpeed, p.Age, p.Phase);
            if (e.TwinklePercent < 1f && noise > e.TwinklePercent) continue;
            float instanceScale = (e.Flags & 0x20) != 0 ? pool.Scale : 1f;
            float half = size * e.Twinkle(noise) * instanceScale;
            if (half <= 0f) continue;

            Vector3 centre;
            Vector3 velocity;
            if (pool.ModelSpace)
            {
                centre = SpellParticleFrameLaw.DrawModelPoint(p.Position,
                    pool.EmitterWorld, emitterFrame);
                velocity = SpellParticleFrameLaw.DrawModelVector(p.Velocity, emitterFrame);
            }
            else
            {
                centre = pool.RootCarriesCloud
                    ? SpellParticleFrameLaw.DrawWorld(p.Position, pool.RootCloudAnchorWorld,
                        pool.HostAttachmentRotation)
                    : p.Position;
                velocity = pool.RootCarriesCloud
                    ? SpellParticleFrameLaw.DrawVector(p.Velocity, pool.HostAttachmentRotation)
                    : p.Velocity;
            }

            bool drawHead = e.HeadOrTail is 0 or 2;
            bool drawTail = e.HeadOrTail >= 1;
            if (drawHead)
            {
                Vector3 baseRight = cameraRight;
                Vector3 baseUp = cameraUp;
                if ((e.Flags & 0x1000) != 0)
                {
                    baseRight = SpellParticleFrameLaw.DrawModelVector(-Vector3.UnitZ, emitterFrame);
                    baseUp = SpellParticleFrameLaw.DrawModelVector(-Vector3.UnitX, emitterFrame);
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
        if (blend is 0 or 1)
        {
            _gl.Disable(EnableCap.Blend);
            _gl.DepthMask(true);
            return;
        }

        _gl.Enable(EnableCap.Blend);
        _gl.DepthMask(false);
        switch (blend)
        {
            case 3:
            case 4: _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One); break;              // additive
            default: _gl.BlendFunc(BlendingFactor.SrcAlpha,
                BlendingFactor.OneMinusSrcAlpha); break; // 2/5/6 alpha
        }
    }

    private Texture? ResolveTexture(string path)
    {
        if (path.Length == 0) return null;
        if (_textures.TryGetValue(path, out Texture? tex)) return tex;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            var decoded = AdtTerrainReader.ReadBlpPixels(_config.ClientDataPath, path);
            tex = decoded is { } d ? Texture.From2D(_gl, d.bgra, d.width, d.height,
                mipmaps: true, repeat: true) : null;
        }
        catch { tex = null; }
        double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 /
            System.Diagnostics.Stopwatch.Frequency;
        if (ms > 2) Console.WriteLine($"[fx-load] particle-tex {Path.GetFileName(path)} {ms:0.0}ms");
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
        {
            sb.Append($" | e{p.EmitterIndex}[{(p.ModelSpace ? "model" : "world")}] blend={p.Emitter.BlendingType} " +
                $"live={p.Particles.Count} drawn={p.DrawnLastFrame} " +
                $"root=({p.RootCloudAnchorWorld.X:0.##},{p.RootCloudAnchorWorld.Y:0.##},{p.RootCloudAnchorWorld.Z:0.##}) " +
                $"emitter=({p.EmitterWorld.X:0.##},{p.EmitterWorld.Y:0.##},{p.EmitterWorld.Z:0.##}) " +
                $"{CloudTrace(p)} " +
                $"tex={Path.GetFileName(p.TexturePath)}");
            for (int i = 0; i < p.Children.Count; i++)
            {
                Pool child = p.Children[i];
                sb.Append($" child{i}[e{child.EmitterIndex}] blend={child.Emitter.BlendingType} " +
                    $"live={child.Particles.Count} drawn={child.DrawnLastFrame} " +
                    $"tex={Path.GetFileName(child.TexturePath)}");
            }
        }
        return sb.ToString();
    }

    private static string CloudTrace(Pool pool)
    {
        if (pool.Particles.Count == 0) return "cloud=empty";
        Matrix4x4 emitterFrame = EmitterLinearFrame(pool);

        Vector3 World(in Particle particle)
            => pool.ModelSpace
                ? SpellParticleFrameLaw.DrawModelPoint(particle.Position,
                    pool.EmitterWorld, emitterFrame)
                : pool.RootCarriesCloud
                    ? SpellParticleFrameLaw.DrawWorld(particle.Position,
                        pool.RootCloudAnchorWorld, pool.HostAttachmentRotation)
                    : particle.Position;

        Vector3 mean = Vector3.Zero;
        foreach (Particle particle in pool.Particles) mean += World(particle);
        mean /= pool.Particles.Count;

        float xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
        foreach (Particle particle in pool.Particles)
        {
            Vector3 d = World(particle) - mean;
            xx += d.X * d.X; xy += d.X * d.Y; xz += d.X * d.Z;
            yy += d.Y * d.Y; yz += d.Y * d.Z; zz += d.Z * d.Z;
        }
        Vector3 axis = Vector3.UnitX;
        for (int i = 0; i < 16; i++)
        {
            Vector3 next = new(
                xx * axis.X + xy * axis.Y + xz * axis.Z,
                xy * axis.X + yy * axis.Y + yz * axis.Z,
                xz * axis.X + yz * axis.Y + zz * axis.Z);
            if (next.LengthSquared() <= 1e-12f) break;
            axis = Vector3.Normalize(next);
        }
        float min = float.PositiveInfinity, max = float.NegativeInfinity;
        foreach (Particle particle in pool.Particles)
        {
            float projection = Vector3.Dot(World(particle) - mean, axis);
            min = MathF.Min(min, projection);
            max = MathF.Max(max, projection);
        }
        Vector3 boneOffset = pool.EmitterWorld - pool.RootCloudAnchorWorld;
        return $"boneOffset=({boneOffset.X:0.##},{boneOffset.Y:0.##},{boneOffset.Z:0.##}) " +
            $"cloudAxis=({axis.X:0.##},{axis.Y:0.##},{axis.Z:0.##}) span={max - min:0.##}";
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

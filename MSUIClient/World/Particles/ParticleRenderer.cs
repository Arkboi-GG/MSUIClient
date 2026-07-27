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
public enum ParticleSpaceMode { FromFlag, ForceModel, ForceWorld }

public sealed class ParticleRenderer : IDisposable
{
    private readonly GL _gl;
    private readonly ClientConfig _config;

    private Shader? _shader;
    private uint _vao, _quadVbo, _instanceVbo;
    private Shader? _surfaceShader;
    private uint _surfaceVao;
    private readonly HashSet<(int, int, int)> _surfaceSeen = new();

    /// <summary>Draw the flat translucent "looking glass" film across an instance
    /// portal's opening - the 1.12 portal SURFACE, a separate flat plane from the
    /// swirling emitters. False = particles only.</summary>
    public bool PortalSurface { get; set; } = true;

    /// <summary>Peak opacity of that film (alpha-blended frost). Tunable.</summary>
    public float PortalSurfaceAlpha { get; set; } = 0.106f;

    /// <summary>Film radius as a multiple of the emitter ring radius (>1 reaches
    /// past the ring toward the archway edge).</summary>
    public float PortalSurfaceSize { get; set; } = 1.2f;

    /// <summary>Hue of the surface film (0..1 around the wheel). ~0.583 is the authored
    /// blue; lower toward ~0.40-0.45 pushes it green.</summary>
    public float PortalSurfaceHue { get; set; } = 0.424f;

    /// <summary>Surface film colour saturation and brightness (with the hue above).</summary>
    public float PortalSurfaceSat { get; set; } = 1.0f;
    public float PortalSurfaceVal { get; set; } = 1.06f;

    /// <summary>Uniform scale of the whole portal disc (spawn ring, convergence,
    /// sprites AND the surface film) about its fixed centre. >1 pushes the dense
    /// outer ring outward - the knob to test whether vanilla hides it behind the
    /// archway walls, leaving a sparse open core in view. Model-space only.</summary>
    public float PortalScale { get; set; } = 1.01f;

    /// <summary>Per-sprite size multiplier for the PORTAL (model-space) sprites only,
    /// on top of the authored scale ramp. Below 1 shrinks each speck so the converging
    /// sprites stop overlapping into a solid cloud and read as distinct specks.</summary>
    public float SpriteSizeScale { get; set; } = 1.77f;

    /// <summary>Mip LOD bias for portal sprites (handed to the frag shader). 0 = full
    /// trilinear (soft; blurs a shrinking speck into vapour); negative sharpens toward
    /// the base level so specks stay crisp. Portal-scoped; other effects keep 0.</summary>
    public float SpriteSharpness { get; set; } = -4.0f;

    /// <summary>Radius (yards) of the see-through centre: portal particles inside this
    /// fade out so you look through the glass to the interior. 0 = off.</summary>
    public float PortalCentreHole { get; set; } = 4.33f;

    /// <summary>Debug isolation: -1 draws all emitters; 0/1/... draws only that emitter
    /// index per model. Tells the portal's two emitters apart and spots a double-placed
    /// portal (solo ONE and still see two rings = the portal is placed twice).</summary>
    public int SoloEmitter { get; set; } = -1;
    private int _instanceCapacity;

    private readonly Dictionary<string, Texture?> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<PoolKey, Pool> _pools = [];
    private readonly List<GpuParticle> _scratch = [];

    public bool Enabled { get; set; } = true;

    /// <summary>Beyond this, emitters are not simulated at all.</summary>
    public float SimulationDistance { get; set; } = 120f;

    /// <summary>Global multiplier on every emitter's rate. 0 stops new spawns.</summary>
    public float DensityScale { get; set; } = 0.89f;

    /// <summary>Portal particle colour tuning (MODEL-space emitters only, so it does
    /// not touch torches etc). Hue shift around the wheel, plus saturation and
    /// brightness multipliers on the authored colour - to push toward ocean blue.</summary>
    public float ParticleHueShift { get; set; } = 0f;
    public float ParticleSaturation { get; set; } = 1.15f;
    public float ParticleValue { get; set; } = 1f;

    /// <summary>
    /// How a particle's motion is composed (benilla parity - see BENILLA_VS_MSUI_PORTAL.md).
    ///   FromFlag   - model space when the emitter's file flag 0x10 is set (the
    ///                InstancePortal case), world space otherwise. What benilla does.
    ///   ForceModel / ForceWorld - override, for A/B against the old look.
    ///
    /// MODEL SPACE is the fix for the "flat wrong" portal: a particle stores raw
    /// LOCAL coordinates and is RE-PROJECTED through the live spinning bone every
    /// frame at draw, instead of the spin being baked in once at birth. The swirl -
    /// inward spiral, empty centre, overlapping arms - emerges from that, so in
    /// model space ReverseConverging / SpawnArms / CentreHoleYards / SpinRateScale
    /// are all ignored.
    /// </summary>
    public ParticleSpaceMode SpaceMode { get; set; } = ParticleSpaceMode.FromFlag;

    /// <summary>Model-space bone-spin rate multiplier. 1.0 = the authored rate (benilla
    /// plays it at 1x; the old 1.86 was a world-space compensation). Tuning knob only.</summary>
    public float ModelSpinScale { get; set; } = 0.86f;

    // Beyond-portal fill light. Tunable values only; GameLoop reads them each
    // frame, finds the nearest portal via TryGetNearestPortal, and pushes a
    // world-space light onto the WMO and doodad renderers. Kept here so every
    // portal knob lives on one instrument.
    public bool  PortalLight { get; set; } = true;
    public float PortalLightIntensity { get; set; } = 0.85f;
    public float PortalLightRadius { get; set; } = 34f;
    public float PortalLightOffset { get; set; } = 10f;
    public float PortalLightHue { get; set; } = 0.09f;
    public float PortalLightSat { get; set; } = 0.16f;
    public float PortalLightVal { get; set; } = 0.67f;

    /// <summary>
    /// Play a converging emitter's motion BACKWARDS: the particle starts where
    /// it would have finished and travels back to the emitter.
    ///
    /// Nico's idea, and it is a better one than three rounds of arguing about
    /// what the direction field means. **A time-reversed outward spiral is an
    /// inward spiral** - the same authored direction, speed, lifetime and bone
    /// sweep, run the other way. Nothing has to be reinterpreted, and the sweep
    /// still supplies the curve, so the path is a spiral rather than a fall.
    ///
    /// Implemented as a spawn-time transform, not a simulation mode: displace
    /// the particle by one full lifetime of travel and negate the velocity.
    /// Everything downstream - gravity, the ramp, culling - is untouched.
    /// </summary>
    public bool ReverseConverging { get; set; } = true;

    /// <summary>
    /// Sample the colour/scale ramp at `1 - t`, so the END of the animation owns
    /// the density instead of the beginning.
    ///
    /// The second half of Nico's idea, and it is what empties the middle.
    /// InstancePortal's ramp peaks at MidPoint **0.20** - very early - so
    /// particles are brightest just after birth. Whichever end of the path birth
    /// happens to be, that end is the bright one. Flipping the sample moves the
    /// peak to t = 0.80, so the bright band sits at the FAR end and the centre
    /// goes dark on its own.
    ///
    /// §16 got this exactly backwards by assuming the ramp already did it: the
    /// ramp is a function of a particle's own age, and age only maps to distance
    /// if every particle starts from the same place. This makes that true.
    /// </summary>
    public bool ReverseRamp { get; set; } = false;

    /// <summary>
    /// Multiplier on the emitter bone's animation speed. **Nico's observation,
    /// and he has verified it against 1.12: the real rotation is much faster.**
    ///
    /// It also explains the "double swirl". WoWee's direction is
    /// `1 + U(-pi, pi)` on Z, positive about two thirds of the time and negative
    /// the rest - so every spawn instant fires TWO opposite arms. A particle
    /// lives 1.05 s while the emitter sweeps 1.05 / 3.334 = **113 degrees**, so
    /// each arm is a 113-degree arc with a gap after it. Spin fast enough that
    /// an arm exceeds 180 degrees and the two arcs meet; faster still and they
    /// overlap into one continuous band. So "rotate faster" and "more starts"
    /// are the same fix, not two.
    /// </summary>
    public float SpinRateScale { get; set; } = 1.86f;

    /// <summary>
    /// Number of evenly spaced spawn slots around the spin axis.
    ///
    /// **This is the shape Nico described and random jitter cannot make it:**
    /// *"many real origin points that let their animation start, go for a bit
    /// till getting close to center, interrupt, and between that and a restart
    /// another one starts, and another, but all staggered, and as if each owns a
    /// position on a 24 hour round clock."*
    ///
    /// Random phase scatters births uniformly and reads as a mess, because two
    /// neighbouring particles can land a degree apart or a hundred. Quantising
    /// to N slots and issuing them **round-robin** gives every stream its own
    /// angle, keeps them evenly separated forever, and staggers them in time for
    /// free - the next particle always belongs to the next slot, so the streams
    /// are born at different points in their own cycle.
    ///
    /// 0 falls back to continuous phase. 24 is the clock face he named.
    /// </summary>
    public int SpawnArms { get; set; } = 24;

    /// <summary>
    /// Random phase on TOP of the slot, as a fraction of one slot's width.
    /// Softens the spokes without dissolving them; 1 would smear a slot into its
    /// neighbours and give back the mess.
    /// </summary>
    public float SpawnPhaseJitter { get; set; } = 0.25f;

    /// <summary>
    /// Radius in yards around the emitter inside which particles fade out.
    /// **Nico's #2: "there is NOTHING in the center in 1.12 - so something fades
    /// it or the endpoint sits short of the center."**
    ///
    /// This is the "something fades it" reading, and it is a knob rather than a
    /// claim: nothing measured so far says the format carries an inner cutoff.
    /// If a value here makes the portal correct, that is evidence worth chasing
    /// back into the flags - `0x400` STYLE:Pinned and `0x20000` STYLE:Outward
    /// are both unread and both describe where a particle's quad lives relative
    /// to its origin.
    /// </summary>
    public float CentreHoleYards { get; set; } = 4.74f;


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

        // The flat portal-surface film (self-contained, inline GLSL like SkyRenderer).
        _surfaceShader = Shader.FromSource(_gl, "portal_surface", SurfaceVert, SurfaceFrag);
        _surfaceVao = _gl.GenVertexArray();
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

        /// <summary>World position of the emitter this frame (= the bone pivot in world space).</summary>
        public Vector3 Origin;

        /// <summary>Model space (flag 0x10) vs world space. Refreshed each frame from the emitter.</summary>
        public bool ModelSpace;

        /// <summary>Emitter index within its model, for the solo/isolation debug.</summary>
        public int EmitterIndex;

        /// <summary>Round-robin cursor over the spawn slots. See SpawnArms.</summary>
        public int NextArm;

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

    /// <summary>+90 degrees about the emitter's local +Z, in the M2's Z-up frame:
    /// (x, y, z) -> (-y, x, z). benilla prepends this to EVERY emitter's kernel output
    /// (particles.rs:357) - it stands the emission ring perpendicular to the rotor's
    /// local-X spin so the disc faces out instead of tumbling edge-on.</summary>
    private static Vector3 Rot90Z(Vector3 v) => new(-v.Y, v.X, v.Z);

    /// <summary>Whether this emitter is simulated in model space (re-projected through the
    /// live bone each frame) vs world space (spin baked at birth).</summary>
    private bool IsModelSpace(M2ParticleEmitter e) => SpaceMode switch
    {
        ParticleSpaceMode.ForceModel => true,
        ParticleSpaceMode.ForceWorld => false,
        _ => (e.Flags & 0x10) != 0,
    };

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
            pool.ModelSpace = IsModelSpace(emitter);
            pool.EmitterIndex = index;
            pool.Scale = MathF.Sqrt(
                transform.M11 * transform.M11 +
                transform.M12 * transform.M12 +
                transform.M13 * transform.M13);
            if (pool.Scale <= 0f || float.IsNaN(pool.Scale)) pool.Scale = 1f;
            pool.Origin = origin;
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
            if (pool.ModelSpace)
            {
                // benilla integrate order (sim.rs:56-71), dt clamped to 0.1
                // (sim.rs:226): position advances FIRST on the pre-decay velocity,
                // THEN gravity as a closed-form half-step on local up (+Y = WoW +Z),
                // THEN drag as a CLAMPED-LINEAR decay. Portal has gravity=drag=0, so
                // this is pos += vel*dt for the portal; it matters for other emitters.
                float sdt = MathF.Min(dt, 0.1f);
                p.Position += p.Velocity * sdt;
                if (e.Gravity != 0f)
                {
                    p.Position.Y -= 0.5f * e.Gravity * sdt * sdt;
                    p.Velocity.Y -= e.Gravity * sdt;
                }
                if (e.Drag != 0f)
                {
                    float fdrag = MathF.Min(sdt * e.Drag, 1f);
                    p.Velocity -= fdrag * p.Velocity;
                }
            }
            else
            {
                p.Velocity.Z -= e.Gravity * dt;
                p.Position += p.Velocity * dt;
            }
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

        // ── MODEL SPACE (flag 0x10, the InstancePortal case) ─────
        //
        // Store the particle in the emitter's LOCAL frame (relative to the bone
        // pivot), UN-spun. The live bone rotation is applied every frame at draw
        // (see Fill), so the particle spirals as the frame it lives in turns - that
        // IS the swirl. benilla parity: emit_local's plane kernel + the R(+Z,90)
        // prepend, stored raw (particles.rs:314-357, sim.rs:610-622 else branch).
        if (pool.ModelSpace)
        {
            // ── EMISSION KERNEL (benilla emit_local, particles.rs:314-347) ────
            // The portal is a SPHERE emitter: born on a RING of radius ~areaLength,
            // moving radially INWARD (negative speed) - the "outer ring, high density,
            // coming in". A PLANE emitter is born in a flat rectangle near the centre.
            // MSUI used to force plane, which is why the portal emanated from the middle.
            Vector3 posZ, dirZ;
            if (e.Shape == ParticleShape.Sphere)
            {
                // areaLength/areaWidth are the min/max radius for a sphere emitter.
                float r = e.EmissionAreaLength
                        + pool.Rand() * MathF.Max(0f, e.EmissionAreaWidth - e.EmissionAreaLength);
                float lat = pool.Symmetric() * e.VerticalRange;   // latitude
                float lon = pool.Symmetric() * e.HorizontalRange; // longitude
                float clat = MathF.Cos(lat), slat = MathF.Sin(lat);
                float clon = MathF.Cos(lon), slon = MathF.Sin(lon);
                var shell = new Vector3(clat * clon, clat * slon, slat);  // unit
                posZ = r * shell;                                         // birth on the shell (the ring)
                if (e.ZSource != 0f)
                {
                    dirZ = posZ - new Vector3(0f, 0f, e.ZSource);
                    dirZ = dirZ.LengthSquared() > 1e-12f ? Vector3.Normalize(dirZ) : Vector3.UnitZ;
                }
                else if ((e.Flags & 0x100) != 0)
                {
                    dirZ = Vector3.UnitZ;                                  // sphere_up (flag 0x100)
                }
                else
                {
                    dirZ = shell;                                         // radial (x negative speed => inward)
                }
            }
            else
            {
                // Plane (spline falls back to plane): born across the area rectangle,
                // direction a symmetric spherical cone about +Z.
                posZ = new Vector3(
                    e.EmissionAreaLength * 0.5f * pool.Symmetric(),
                    e.EmissionAreaWidth  * 0.5f * pool.Symmetric(),
                    0f);
                if (e.ZSource != 0f)
                {
                    dirZ = posZ - new Vector3(0f, 0f, e.ZSource);
                    dirZ = dirZ.LengthSquared() > 1e-12f ? Vector3.Normalize(dirZ) : Vector3.UnitZ;
                }
                else
                {
                    float theta = pool.Symmetric() * e.VerticalRange;
                    float phi   = pool.Symmetric() * e.HorizontalRange;
                    float st = MathF.Sin(theta), ct = MathF.Cos(theta);
                    float sp = MathF.Sin(phi),   cp = MathF.Cos(phi);
                    dirZ = new Vector3(st * cp, st * sp, ct);
                }
            }

            // R(+Z,90) prepend in Z-up, then swap to the Y-up local frame.
            var localPos = Swap(Rot90Z(posZ));
            var localDir = Swap(Rot90Z(dirZ));

            float mspeed = e.EmissionSpeed * (1f + e.SpeedVariation * pool.Symmetric()) * pool.Scale;

            return new Particle
            {
                Position = localPos,           // LOCAL (relative to pivot), re-projected at draw
                Velocity = localDir * mspeed,  // negative speed => inward; no time-reversal needed
                Age = 0f,
                Life = e.Lifespan,
            };
        }

        // ── DIRECTION: the format spec, not an approximation of it ───────────
        //
        // wowdev.wiki/M2, verbatim, for a PLANE generator:
        //
        //   verticalRange   "the maximum POLAR angle of the initial velocity;
        //                    0 makes the velocity straight up (+z)"
        //   horizontalRange "the maximum AZIMUTH angle of the initial velocity;
        //                    0 makes the velocity have NO SIDEWAYS (y-axis)
        //                    component"
        //   emissionAreaLength / Width  "the width of the plane in the x-axis /
        //                    y-axis"
        //
        // So they are ordinary spherical angles about +z, and every model this
        // renderer has worn was wrong about them in a different way:
        //
        //   * the first cone sampled azimuth over the FULL circle regardless of
        //     horizontalRange, which at vRange = pi is an isotropic sphere - the
        //     volumetric plume;
        //   * WoWee adds both ranges as componentwise jitter and normalises,
        //     which is a cheap approximation, not the spec. It is why following
        //     it got close and never right.
        //
        // The decisive clause is the second one. InstancePortal's horizontalRange
        // is ZERO, so its velocity has NO y component at all: the direction is
        // confined to the model's XZ plane. It is a FLAT FAN, and the bone's
        // full revolution sweeps that fan. Nothing here is 3D.
        //
        // Both angles are sampled symmetrically about the axis - "drifting away
        // vertically... they can do it horizontally too" describes a spread
        // either side, and a one-sided [0, range] sample would throw every
        // particle to the same side of the emitter.
        float lx = e.EmissionAreaLength * 0.5f * pool.Symmetric();
        float ly = e.EmissionAreaWidth * 0.5f * pool.Symmetric();

        // WoWee's formula, kept - and MEASURED to be the right shape, which the
        // spec-literal reading was not. See PLAN_14 §19: after the axis swap and
        // the bone spin, this produces directions whose component along the ring
        // mesh's NORMAL is exactly 0.000 across 4000 samples. Every particle
        // stays in the plane of the disc the model is built from. The polar/
        // azimuth reading put a mean 0.636 out-of-plane component in, which is
        // strictly worse and is why it looked like nothing changed.
        var dirRaw = new Vector3(
            pool.Symmetric() * e.HorizontalRange,
            pool.Symmetric() * e.HorizontalRange,
            1f + pool.Symmetric() * e.VerticalRange);

        float lenSq = dirRaw.LengthSquared();
        dirRaw = lenSq > 1e-6f ? dirRaw * (1f / MathF.Sqrt(lenSq)) : new Vector3(0f, 0f, 1f);

        // Swapped once into the Y-up space the placement matrix expects, the
        // same `(x, y, z) -> (x, z, -y)` the vertices and the emitter position
        // get.
        var dirLocal = Swap(dirRaw);

        // THE BONE SPIN. WoWee multiplies the direction by the bone matrix, and
        // for InstancePortal that bone turns a full revolution every 3.33 s - so
        // a direction fixed along one axis sweeps a complete circle, and the
        // particles trace a flat rotating disc. Direction and spin together are
        // what make the shape; neither does it alone.
        var spin = e.SampleBoneRotation(_time * SpinRateScale);

        // Put this particle on the next slot of the clock face. Round-robin
        // rather than random: it is what keeps the streams evenly separated and
        // staggered instead of clumping. The axis is the bone's own rotation
        // axis, which for InstancePortal is local X.
        float phase = 0f;
        if (SpawnArms > 0)
        {
            phase = MathF.Tau * (pool.NextArm % SpawnArms) / SpawnArms;
            pool.NextArm++;
            if (SpawnPhaseJitter > 0f)
                phase += MathF.Tau / SpawnArms * SpawnPhaseJitter * pool.Symmetric() * 0.5f;
        }
        else if (SpawnPhaseJitter > 0f)
        {
            phase = MathF.Tau * SpawnPhaseJitter * pool.Symmetric();
        }

        if (phase != 0f)
            spin = Quaternion.Concatenate(
                spin, Quaternion.CreateFromAxisAngle(Vector3.UnitX, phase));
        dirLocal = Vector3.Transform(dirLocal, spin);

        // ── SPAWN ACROSS THE EMISSION AREA ───────────────────────────────────
        //
        // WoWee spawns every particle at the emitter point and never reads
        // emissionAreaLength/Width. I followed that for one commit and it was a
        // mistake: **WoWee's particle path never runs for this model at all.**
        // `m2_renderer_particles.cpp` opens its spawn loop with
        // `if (gpu.isInstancePortal) return;` and substitutes two hand-authored
        // glow sprites in the renderer instead. Its omission of the area is
        // therefore not evidence about portals - it is untested code for this
        // case, and harmless for the rest only because every other emitter's
        // area is 0.007..0.5 where the difference does not show.
        //
        // The portal's area is 4.167 and the waterfall's is 18.0. Born at a
        // single point with a direction that sweeps, particles trace one thin
        // coherent ribbon - the sharp arcs. Born across the authored rectangle,
        // the same sweep smears into a soft sheet, which is what the real client
        // draws.
        //
        // The rectangle is carried by the bone spin too, so the plane turns with
        // the direction rather than staying flat while the direction rotates
        // through it - and for a converging emitter that turn is what makes the
        // inward path a SPIRAL rather than a straight fall to the centre.
        var spawnLocal = Vector3.Transform(Swap(new Vector3(lx, ly, 0f)), spin);

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

        var velocity = dirWorld * speed;      // NEGATIVE speed pulls inward. H4.
        var position = origin + offsetWorld;

        // TIME REVERSAL. Start where this particle would have ENDED and travel
        // back. Applied only to converging emitters - the ones the author gave a
        // negative speed - so torches, campfires and waterfalls are untouched.
        if (ReverseConverging && e.EmissionSpeed < 0f)
        {
            position += velocity * e.Lifespan;
            velocity = -velocity;
        }

        return new Particle
        {
            Position = position,
            Velocity = velocity,
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

        // The flat portal-surface film, drawn BEFORE the sprites so they sit over
        // it (interior geometry -> frost film -> additive particles).
        if (PortalSurface) DrawPortalSurfaces(camera);

        // Group by texture AND blend mode: one draw per combination, and the
        // blend state changes between groups.
        var groups = new Dictionary<(string Tex, byte Blend), List<Pool>>();
        foreach (var pool in _pools.Values)
        {
            if (pool.Particles.Count == 0) continue;
            if (SoloEmitter >= 0 && pool.EmitterIndex != SoloEmitter) continue;
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

            // Portal (model-space) sprites take the sharpness knob; other effects
            // keep full trilinear mips (bias 0) so nothing else changes.
            _shader.Set("uMipBias",
                pools.Count > 0 && pools[0].ModelSpace ? SpriteSharpness : 0f);

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

        // MODEL SPACE: re-project every particle through the LIVE bone THIS frame.
        // This is the fix. `p.Position` is a local offset from the bone pivot; the
        // current bone rotation is applied to it, then the doodad placement, so the
        // particle rides the spin as the disc turns (benilla quads.rs:151-155).
        if (pool.ModelSpace)
        {
            var boneRot = e.SampleBoneRotation(_time * ModelSpinScale);
            var rot = pool.Transform;
            rot.M41 = rot.M42 = rot.M43 = 0f;   // rotation+scale only; the pivot is pool.Origin

            foreach (var p in pool.Particles)
            {
                float t = p.Life > 0f ? p.Age / p.Life : 1f;
                e.SampleRamp(t, out var rgba, out float scale);
                if (ParticleHueShift != 0f || ParticleSaturation != 1f || ParticleValue != 1f)
                    rgba = AdjustColor(rgba, ParticleHueShift, ParticleSaturation, ParticleValue);
                if (PortalCentreHole > 0f)
                {
                    float rc = p.Position.Length() * PortalScale;
                    if (rc < PortalCentreHole) rgba.W *= rc / PortalCentreHole;
                }
                if (scale <= 0f || rgba.W <= 0.002f) continue;

                var spun = Vector3.Transform(p.Position * PortalScale, boneRot); // rotate about the pivot
                var centre = pool.Origin + Vector3.Transform(spun, rot);         // pivot + doodad transform

                _scratch.Add(new GpuParticle
                {
                    Centre = centre,
                    Size = scale * pool.Scale * PortalScale * SpriteSizeScale,
                    Colour = rgba,
                });
            }
            return;
        }

        // WORLD SPACE (legacy): the spin was baked at birth; positions are world.
        foreach (var p in pool.Particles)
        {
            float t = p.Life > 0f ? p.Age / p.Life : 1f;

            // Flip which end of the life owns the density. See ReverseRamp.
            if (ReverseRamp && e.EmissionSpeed < 0f) t = 1f - t;

            e.SampleRamp(t, out var rgba, out float scale);

            // Empty the middle. Linear fade from the hole's edge inwards, so the
            // boundary is not a visible ring of its own.
            if (CentreHoleYards > 0f)
            {
                float r = Vector3.Distance(p.Position, pool.Origin);
                if (r < CentreHoleYards) rgba.W *= r / CentreHoleYards;
            }
            if (scale <= 0f || rgba.W <= 0.002f) continue;

            _scratch.Add(new GpuParticle
            {
                Centre = p.Position,
                Size = scale * pool.Scale,
                Colour = rgba,
            });
        }
    }

    /// <summary>
    /// Draw the flat translucent "looking glass" film across each instance
    /// portal's opening - the 1.12 portal SURFACE, a separate flat plane on the
    /// disc, NOT the swirling emitters (a converging ring never covers the opening
    /// evenly). The InstancePortal model has no render mesh, so the real client
    /// draws this surface itself; this recreates it.
    ///
    /// One film per portal placement. A portal is a MODEL-SPACE SPHERE emitter (its
    /// signature); its two emitters share the disc plane, so dedupe by rounded world
    /// origin. The disc lies in the emitter's local Y-Z plane (normal = local X), so
    /// the in-plane basis is the placement's transformed local Y and Z; the centre is
    /// the bone-pivot world position (pool.Origin).
    /// </summary>
    private void DrawPortalSurfaces(Camera camera)
    {
        if (_surfaceShader is null) return;

        _surfaceSeen.Clear();
        bool began = false;

        foreach (var pool in _pools.Values)
        {
            if (!pool.ModelSpace || pool.Emitter.Shape != ParticleShape.Sphere) continue;
            if (SoloEmitter >= 0 && pool.EmitterIndex != SoloEmitter) continue;
            if (pool.Particles.Count == 0) continue;

            var key = ((int)MathF.Round(pool.Origin.X * 4f),
                       (int)MathF.Round(pool.Origin.Y * 4f),
                       (int)MathF.Round(pool.Origin.Z * 4f));
            if (!_surfaceSeen.Add(key)) continue;

            if (!began)
            {
                began = true;
                _surfaceShader.Use();
                _surfaceShader.Set("uViewProjection", camera.RelativeViewProjection);
                _surfaceShader.Set("uCameraOrigin", camera.Position);
                _surfaceShader.Set("uTime", (float)_time);
                _surfaceShader.Set("uTint", HsvToRgb(PortalSurfaceHue, PortalSurfaceSat, PortalSurfaceVal));
                _gl.Enable(EnableCap.Blend);
                _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                _gl.Enable(EnableCap.DepthTest);
                _gl.DepthMask(false);
                _gl.Disable(EnableCap.CullFace);
                _gl.BindVertexArray(_surfaceVao);
            }

            var rot = pool.Transform;
            rot.M41 = rot.M42 = rot.M43 = 0f;
            var u = Vector3.TransformNormal(Vector3.UnitY, rot);
            var v = Vector3.TransformNormal(Vector3.UnitZ, rot);
            u = u.LengthSquared() > 1e-8f ? Vector3.Normalize(u) : Vector3.UnitY;
            v = v.LengthSquared() > 1e-8f ? Vector3.Normalize(v) : Vector3.UnitZ;

            float half = MathF.Max(0.1f, pool.Emitter.EmissionAreaLength)
                       * pool.Scale * MathF.Max(0.1f, PortalSurfaceSize) * PortalScale;

            _surfaceShader.Set("uCenter", pool.Origin);
            _surfaceShader.Set("uRight", u * half);
            _surfaceShader.Set("uUp", v * half);
            _surfaceShader.Set("uAlpha", PortalSurfaceAlpha);
            _gl.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
        }

        if (began)
        {
            _gl.BindVertexArray(0);
            _gl.DepthMask(true);
            _gl.Enable(EnableCap.CullFace);
            _gl.Disable(EnableCap.Blend);
        }
    }

    /// <summary>Shift hue and scale saturation/brightness of an RGBA colour, alpha kept.</summary>
    private static Vector4 AdjustColor(Vector4 c, float hueShift, float satMul, float valMul)
    {
        float r = c.X, g = c.Y, b = c.Z;
        float max = MathF.Max(r, MathF.Max(g, b));
        float min = MathF.Min(r, MathF.Min(g, b));
        float v = max, d = max - min;
        float s = max <= 0f ? 0f : d / max;
        float h = 0f;
        if (d > 1e-6f)
        {
            if (max == r) h = ((g - b) / d) % 6f;
            else if (max == g) h = (b - r) / d + 2f;
            else h = (r - g) / d + 4f;
            h /= 6f;
            if (h < 0f) h += 1f;
        }
        h += hueShift; h -= MathF.Floor(h);
        s = Math.Clamp(s * satMul, 0f, 1f);
        v = MathF.Max(0f, v * valMul);
        return new Vector4(HsvToRgb(h, s, v), c.W);
    }

    private static Vector3 HsvToRgb(float h, float s, float v)
    {
        h = (h - MathF.Floor(h)) * 6f;
        float c = v * s;
        float x = c * (1f - MathF.Abs(h % 2f - 1f));
        float m = v - c;
        Vector3 rgb = (int)h switch
        {
            0 => new Vector3(c, x, 0f),
            1 => new Vector3(x, c, 0f),
            2 => new Vector3(0f, c, x),
            3 => new Vector3(0f, x, c),
            4 => new Vector3(x, 0f, c),
            _ => new Vector3(c, 0f, x),
        };
        return new Vector3(rgb.X + m, rgb.Y + m, rgb.Z + m);
    }

    // The portal-surface film: a flat quad on the disc plane, camera-relative like
    // the sprites. Corners from gl_VertexID (triangle strip 0..3).
    private const string SurfaceVert = @"#version 330 core
uniform mat4 uViewProjection;
uniform vec3 uCameraOrigin;
uniform vec3 uCenter;
uniform vec3 uRight;
uniform vec3 uUp;
out vec2 vUv;
void main()
{
    vec2 c = vec2((gl_VertexID & 1) == 0 ? -1.0 : 1.0,
                  (gl_VertexID & 2) == 0 ? -1.0 : 1.0);
    vUv = c;
    vec3 world = uCenter + uRight * c.x + uUp * c.y;
    gl_Position = uViewProjection * vec4(world - uCameraOrigin, 1.0);
}";

    // Soft round frost with a gentle drifting ripple (the looking-glass shimmer),
    // alpha-blended so it lightly frosts the interior seen through it.
    private const string SurfaceFrag = @"#version 330 core
in vec2 vUv;
uniform float uTime;
uniform float uAlpha;
uniform vec3 uTint;
out vec4 FragColor;
void main()
{
    float r = length(vUv);
    float edge = smoothstep(1.05, 0.60, r);
    float ripple = 0.80
        + 0.10 * sin(vUv.x * 6.0 + uTime * 0.7)
        + 0.10 * sin((vUv.x + vUv.y) * 5.0 - uTime * 0.5);
    float a = uAlpha * edge * ripple;
    if (a <= 0.002) discard;
    FragColor = vec4(uTint * ripple, a);
}";

    /// <summary>Fill-light colour (HSV knobs premultiplied by intensity), for
    /// GameLoop to hand to the world renderers.</summary>
    public Vector3 PortalLightRgb() =>
        HsvToRgb(PortalLightHue, PortalLightSat, PortalLightVal) * PortalLightIntensity;

    /// <summary>World centre of the nearest instance portal (a model-space SPHERE
    /// pool origin) to the camera, for the beyond-portal fill light. False if none
    /// within <paramref name="maxDistance"/>.</summary>
    public bool TryGetNearestPortal(Vector3 camera, float maxDistance, out Vector3 centre)
    {
        centre = default;
        float best = maxDistance * maxDistance;
        bool found = false;
        foreach (var pool in _pools.Values)
        {
            if (!pool.ModelSpace || pool.Emitter.Shape != ParticleShape.Sphere) continue;
            float d = Vector3.DistanceSquared(pool.Origin, camera);
            if (d > best) continue;
            best = d; centre = pool.Origin; found = true;
        }
        return found;
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
                // MIPMAPPED + trilinear + clamped. A converging portal particle
                // shrinks to a few pixels (the glowball emitter's authored size is
                // 0.04), and sampling a full-size sprite with NO mips aliases it into
                // hard chips - the "bundled squares" instead of a smooth soft dot,
                // and harsh faint specks near the centre read as a filled core. From2D
                // uploads BGRA directly, so the manual channel swap is gone.
                tex = Texture.From2D(_gl, d.bgra, d.width, d.height, mipmaps: true, repeat: false);
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
        // Corners at +/-1 (NOT +/-0.5): a vertex sits at centre +/- aSize so the
        // sprite edge spans 2*aSize - aSize is the half-extent, matching the real
        // 1.12 client / benilla (quads.rs:156, byte-verified). At +/-0.5 every
        // sprite was HALF-width / quarter-area, which is why the portal never
        // accumulated into a bright cloud. UV remap is in particle.vert.
        float[] quad = { -1f, -1f, 1f, -1f, -1f, 1f, 1f, 1f };

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
        _surfaceShader?.Dispose();
        if (_surfaceVao != 0) _gl.DeleteVertexArray(_surfaceVao);
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

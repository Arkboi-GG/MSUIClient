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
/// Sprite-sheet flipbooks, animated emitter bones, plane/sphere kernels, and
/// instance-local spell clocks are handled here. M2 ribbons are a sibling
/// dynamic renderer because they are edge trails rather than particle quads.
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
    private readonly List<Pool> _legacySurfacePools = [];

    /// <summary>
    /// Explicit summoned-Mage-portal apertures, keyed by the server GameObject
    /// GUID. Unlike the legacy instance-portal film these do not depend on a
    /// loaded M2, a live emitter pool, or a rounded world-position guess.
    /// </summary>
    private readonly Dictionary<ulong, MagePortalAperture> _magePortalApertures = [];

    private readonly record struct MagePortalAperture(
        Vector3 Center,
        Vector3 Right,
        Vector3 Up,
        float HalfWidth,
        float HalfHeight,
        float SealProgress,
        float SealAlpha,
        uint LiveTexture,
        float LiveBlend);

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

    /// <summary>Number of explicit GUID-owned Mage apertures currently registered.</summary>
    public int MagePortalApertureCount => _magePortalApertures.Count;

    /// <summary>
    /// Create or update one summoned Mage portal's procedural opening.
    ///
    /// <paramref name="right"/> and <paramref name="up"/> are world-space
    /// aperture axes; they are orthonormalised here. The default half extents
    /// produce a six-yard-wide by eight-yard-tall doorway. SealProgress is the
    /// caller-owned pre-animation phase (0 hidden, 1 fully formed). A nonzero
    /// OpenGL 2D texture may be cross-faded in with liveBlend once it contains a
    /// complete destination frame. The texture is sampled in framebuffer screen
    /// space, not stretched across portal-local UVs.
    /// </summary>
    public bool UpsertMagePortalAperture(
        ulong guid,
        Vector3 center,
        Vector3 right,
        Vector3 up,
        float halfWidth = 3f,
        float halfHeight = 4f,
        float sealProgress = 1f,
        float sealAlpha = 0.82f,
        uint liveTexture = 0,
        float liveBlend = 0f)
    {
        if (guid == 0 || !Finite(center)) return false;

        up = NormalizedOr(up, Vector3.UnitZ);
        right -= up * Vector3.Dot(right, up);
        if (!Finite(right) || right.LengthSquared() <= 1e-8f)
        {
            Vector3 seed = MathF.Abs(up.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitY;
            right = Vector3.Cross(seed, up);
        }
        right = Vector3.Normalize(right);

        halfWidth = float.IsFinite(halfWidth) ? MathF.Max(0.1f, halfWidth) : 3f;
        halfHeight = float.IsFinite(halfHeight) ? MathF.Max(0.1f, halfHeight) : 4f;
        sealProgress = float.IsFinite(sealProgress) ? Math.Clamp(sealProgress, 0f, 1f) : 0f;
        sealAlpha = float.IsFinite(sealAlpha) ? Math.Clamp(sealAlpha, 0f, 1f) : 0.82f;
        liveBlend = liveTexture != 0 && float.IsFinite(liveBlend)
            ? Math.Clamp(liveBlend, 0f, 1f)
            : 0f;

        _magePortalApertures[guid] = new MagePortalAperture(
            center, right, up, halfWidth, halfHeight, sealProgress, sealAlpha,
            liveTexture, liveBlend);
        return true;
    }

    /// <summary>Remove one despawned/out-of-range summoned portal visual.</summary>
    public bool RemoveMagePortalAperture(ulong guid) => _magePortalApertures.Remove(guid);

    /// <summary>Drop every explicit Mage aperture at a map/session boundary.</summary>
    public void ClearMagePortalApertures() => _magePortalApertures.Clear();

    /// <summary>
    /// Immediately retire every legacy M2 emitter pool owned by one dynamic
    /// GameObject. This is intentionally GUID-specific: static instance portals
    /// and every unrelated dynamic effect keep their normal lifetime.
    /// </summary>
    public int RemoveOwnedEmitterPools(ulong ownerGuid)
    {
        if (ownerGuid == 0 || _pools.Count == 0) return 0;
        PoolKey[] owned = _pools.Keys.Where(key => key.OwnerGuid == ownerGuid).ToArray();
        foreach (PoolKey key in owned) _pools.Remove(key);
        return owned.Length;
    }

    private static bool Finite(Vector3 v) =>
        float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    private static Vector3 NormalizedOr(Vector3 value, Vector3 fallback)
    {
        if (!Finite(value) || value.LengthSquared() <= 1e-8f) value = fallback;
        return Vector3.Normalize(value);
    }

    /// <summary>Uniform scale of the whole portal disc (spawn ring, convergence,
    /// sprites AND the surface film) about its fixed centre. >1 pushes the dense
    /// outer ring outward - the knob to test whether vanilla hides it behind the
    /// archway walls, leaving a sparse open core in view. Model-space only.</summary>
    public float PortalScale { get; set; } = 1.01f;

    /// <summary>Per-sprite size multiplier for the PORTAL (model-space) sprites only,
    /// on top of the authored scale ramp. Below 1 shrinks each speck so the converging
    /// sprites stop overlapping into a solid cloud and read as distinct specks.</summary>
    public float SpriteSizeScale { get; set; } = 1.77f;

    /// <summary>A global per-sprite size multiplier applied to EVERY sprite (both the model-space
    /// portal path and the world-space path), on top of the authored ramp. The glue login drives
    /// this from its tuning slider to grow or shrink the swirling embers. 1 = authored size.</summary>
    public float SpriteSizeScaleAll { get; set; } = 1f;

    /// <summary>The world-space (flame / brazier) counterpart of SpriteSizeScaleAll: a size
    /// multiplier for the world-space sprites only - the glue's brazier FLAMES - kept independent
    /// of the model-space swirls so the login can size the two groups separately. 1 = authored.</summary>
    public float BrazierSizeScaleAll { get; set; } = 1f;

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
    public bool PortalLight { get; set; } = true;
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

    public (int Pools, int LiveParticles, int DrawnParticles) VisualState(string pathPrefix)
    {
        Pool[] pools = _pools.Where(pair => pair.Key.Path.StartsWith(pathPrefix,
            StringComparison.OrdinalIgnoreCase)).Select(pair => pair.Value).ToArray();
        return (pools.Length, pools.Sum(pool => pool.Particles.Count), pools.Sum(pool => pool.DrawnLastFrame));
    }

    /// <summary>Per-emitter particle census for a path prefix: which emitters are
    /// alive and actually drawing, split by model/world space. Used to localize
    /// "thin fire" — a model-space glow with live &gt; 0 but drawn == 0 is dropping
    /// out at draw, not at spawn.</summary>
    public string CensusReport(string pathPrefix)
    {
        Pool[] pools = _pools.Where(pair => pair.Key.Path.StartsWith(pathPrefix,
            StringComparison.OrdinalIgnoreCase)).Select(pair => pair.Value)
            .OrderBy(p => p.TexturePath).ThenBy(p => p.EmitterIndex).ToArray();
        if (pools.Length == 0) return $"census '{pathPrefix}': NO POOLS";
        var sb = new System.Text.StringBuilder($"census '{pathPrefix}': {pools.Length} pools");
        foreach (Pool p in pools)
            sb.Append($" | e{p.EmitterIndex}[{(p.ModelSpace ? "model" : "world")}] blend={p.Emitter.BlendingType} " +
                $"live={p.Particles.Count} drawn={p.DrawnLastFrame} scale={p.Scale:0.##} tex={System.IO.Path.GetFileName(p.TexturePath)}");
        return sb.ToString();
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
    /// Identity of one emitter on one placement. Static placement positions are
    /// rounded to a tenth of a yard; dynamic GameObjects instead use OwnerGuid
    /// as their exact identity and leave the rounded transform fields at zero.
    /// </summary>
    private readonly record struct PoolKey(
        string Path, ulong OwnerGuid, int X, int Y, int Z, int Emitter, int Rot);

    private sealed class Pool
    {
        public M2ParticleEmitter Emitter = null!;
        public Matrix4x4 Transform;
        public string TexturePath = "";
        public float SpawnAccumulator;
        public readonly List<Particle> Particles = [];
        public bool TouchedThisFrame;
        public int DrawnLastFrame;
        // True for spell-effect pools (path "spell:..."). Portal-only presentation
        // knobs (the centre-hole hollow) must NOT apply to these: a spell orb is
        // ~0.8yd, far inside the 4.33yd portal hole, so it would fade the bright
        // converging core to near-zero. This is why precast/impact read as thin.
        public bool Spell;
        /// <summary>Exact dynamic GameObject owner; zero for static scenery.</summary>
        public ulong OwnerGuid;
        public uint Seed = 0x9E3779B9;

        /// <summary>Uniform scale of the placement, applied to speed and sprite size.</summary>
        public float Scale = 1f;

        /// <summary>World position of the emitter this frame (= the bone pivot in world space).</summary>
        public Vector3 Origin;

        /// <summary>Model space (flag 0x10) vs world space. Refreshed each frame from the emitter.</summary>
        public bool ModelSpace;

        /// <summary>Emitter index within its model, for the solo/isolation debug.</summary>
        public int EmitterIndex;
        /// <summary>Placement-local M2 clock; NaN inputs use the global doodad clock.</summary>
        public double AnimationTime;
        public int AnimationId;
        public readonly float[] Scalars = new float[10];
        public Quaternion? BoneRotationOverride;


        /// <summary>xorshift, so each pool is independent and nothing shares Random.</summary>
        public float Rand()
        {
            Seed ^= Seed << 13; Seed ^= Seed >> 17; Seed ^= Seed << 5;
            return (Seed & 0xFFFFFF) / 16777215f;
        }

        public float Symmetric() => Rand() * 2f - 1f;

        /// <summary>A spawn-time random phase for the twinkle LUT. Without a per-particle
        /// phase a whole flame flickers in lockstep.</summary>
        public uint NextPhase()
        {
            Seed ^= Seed << 13; Seed ^= Seed >> 17; Seed ^= Seed << 5;
            return Seed;
        }
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

    // ── Twinkle ──────────────────────────────────────────────────────────────
    //
    // The 128-entry noise table the reference seeds with uniform-random f32 at startup
    // (benilla quads.rs TWINKLE_LUT). We mirror the distribution with a fixed seed - the
    // sequence is not observable, only its statistics are.
    private static readonly float[] TwinkleLut = BuildTwinkleLut();

    private static float[] BuildTwinkleLut()
    {
        var t = new float[128];
        uint s = 0xC0FFEE11u;
        for (int i = 0; i < t.Length; i++)
        {
            s ^= s << 13; s ^= s >> 17; s ^= s << 5;
            t[i] = (s & 0xFFFFFF) / 16777215f;
        }
        return t;
    }

    /// <summary>The twinkle noise sample for a particle: byte-verified index (0x7b2a86)
    /// `(floor(clamp(twinkleSpeed * age, 0, 255)) + phase) &amp; 0x7f`.</summary>
    private static float TwinkleNoise(float speed, float age, uint phase)
    {
        float w = speed * age;
        uint i = float.IsFinite(w) ? (uint)Math.Clamp(w, 0f, 255f) : 0u;
        return TwinkleLut[(int)((i + phase) & 0x7Fu)];
    }

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

        /// <summary>Spawn-time random, the twinkle LUT's index offset. The reference hashes the
        /// pool-slot POINTER; a pool INDEX would not survive our RemoveAt compaction, so this is
        /// carried per particle (benilla Particle::phase).</summary>
        public uint Phase;
    }

    private struct GpuParticle
    {
        public Vector3 Centre;
        public float Size;
        public Vector4 Colour;
        // Sprite-sheet cell (offset.xy, scale.xy); (0,0,1,1) = whole texture. Chosen PER PARTICLE
        // by its own age (Fill -> M2ParticleEmitter.SampleHeadCellRect), so the flame flipbook is
        // no longer a single global-clock cell for the whole draw group (that was the flicker).
        public Vector4 CellRect;
    }

    // ── Frame ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Compatibility lane for non-world callers such as the login glue scene.
    /// Those emitters have no server GameObject owner.
    /// </summary>
    public void Simulate(
        float dt,
        Vector3 cameraPosition,
        IEnumerable<(string Path, Matrix4x4 Transform, M2ParticleEmitter Emitter,
                     int EmitterIndex, string TexturePath, double AnimationTime,
                     int AnimationId, Vector3? LocalOrigin,
                     Quaternion? LocalRotation)> emitters)
        => Simulate(dt, cameraPosition, WithoutOwnerGuids(emitters));

    private static IEnumerable<(string Path, Matrix4x4 Transform, M2ParticleEmitter Emitter,
                                int EmitterIndex, string TexturePath, double AnimationTime,
                                int AnimationId, Vector3? LocalOrigin,
                                Quaternion? LocalRotation, ulong OwnerGuid)>
        WithoutOwnerGuids(
            IEnumerable<(string Path, Matrix4x4 Transform, M2ParticleEmitter Emitter,
                         int EmitterIndex, string TexturePath, double AnimationTime,
                         int AnimationId, Vector3? LocalOrigin,
                         Quaternion? LocalRotation)> emitters)
    {
        foreach (var emitter in emitters)
            yield return (emitter.Path, emitter.Transform, emitter.Emitter,
                emitter.EmitterIndex, emitter.TexturePath, emitter.AnimationTime,
                emitter.AnimationId, emitter.LocalOrigin, emitter.LocalRotation, 0UL);
    }

    /// <summary>
    /// World lane. OwnerGuid is zero for static ADT/WMO placements and the exact
    /// server GameObject GUID for dynamic placements.
    /// </summary>
    public void Simulate(
        float dt,
        Vector3 cameraPosition,
        IEnumerable<(string Path, Matrix4x4 Transform, M2ParticleEmitter Emitter,
                     int EmitterIndex, string TexturePath, double AnimationTime,
                     int AnimationId, Vector3? LocalOrigin,
                     Quaternion? LocalRotation, ulong OwnerGuid)> emitters)
    {
        SimulateMilliseconds = 0.0;
        if (!Enabled || _shader is null) return;

        Time += dt;
        _time = Time;

        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        foreach (var pool in _pools.Values) pool.TouchedThisFrame = false;

        float simSq = SimulationDistance * SimulationDistance;

        foreach (var (path, transform, emitter, index, texPath, suppliedAnimationTime, animationId,
            localOrigin, localRotation, ownerGuid) in emitters)
        {
            double animationTime = double.IsNaN(suppliedAnimationTime) ? _time : suppliedAnimationTime;
            // The emitter's BONE composes each particle's BIRTH (benilla particles.rs:10-11),
            // so an emitter riding an animated bone leaves a TRAIL rather than dragging its
            // cloud. Sampling only the bone's ROTATION - which is all this did - froze every
            // TRANSLATION-driven emitter in place: UI_MainMenu's 16 GLOWBALL emitters author
            // EmissionSpeed 0 and keep all their motion in bones 32..47, so the login screen
            // grew 16 motionless flares (~90 additive sprites stacked on one point) where the
            // OG has drifting motes. Static emitters return the bind position unchanged.
            Vector3 emitterLocal = localOrigin ?? emitter.SampleBonePosition(
                animationTime, new Vector3(emitter.PosX, emitter.PosY, emitter.PosZ));
            var origin = Vector3.Transform(emitterLocal, transform);
            if (Vector3.DistanceSquared(origin, cameraPosition) > simSq) continue;

            // Rotation is in the key as well as position: two placements of the
            // same model in the same tenth-of-a-yard cell would otherwise share
            // one pool, which is precisely the per-instance invariant H5 asks
            // this key to enforce.
            // Spell paths already contain the effect-instance id. Keying those
            // pools by their moving position rebuilt the pool every tenth of a
            // yard, deleting the projectile's cloud and making its trail blink.
            bool movingInstance = path.StartsWith("spell:", StringComparison.OrdinalIgnoreCase);
            bool exactOwner = ownerGuid != 0;
            var key = new PoolKey(path, ownerGuid,
                movingInstance || exactOwner ? 0 : (int)MathF.Round(transform.M41 * 10f),
                movingInstance || exactOwner ? 0 : (int)MathF.Round(transform.M42 * 10f),
                movingInstance || exactOwner ? 0 : (int)MathF.Round(transform.M43 * 10f), index,
                movingInstance || exactOwner ? 0 : (int)MathF.Round(
                    (transform.M11 + transform.M21 * 3f + transform.M12 * 7f) * 100f));

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

            // Refreshed every frame, not frozen at creation. Static PoolKeys
            // carry only a rounded transform and dynamic keys only their exact
            // owner GUID, so neither identity records the live orientation.
            pool.Transform = transform;
            pool.Emitter = emitter;
            pool.TexturePath = texPath;
            pool.Spell = movingInstance;
            pool.OwnerGuid = ownerGuid;
            pool.ModelSpace = IsModelSpace(emitter);
            pool.EmitterIndex = index;
            pool.AnimationTime = animationTime;
            pool.AnimationId = animationId;
            pool.BoneRotationOverride = localRotation;
            float[] defaults = [emitter.EmissionSpeed, emitter.SpeedVariation, emitter.VerticalRange,
                emitter.HorizontalRange, emitter.Gravity, emitter.Lifespan, emitter.EmissionRate,
                emitter.EmissionAreaLength, emitter.EmissionAreaWidth, emitter.ZSource];
            for (int scalar = 0; scalar < pool.Scalars.Length; scalar++)
                pool.Scalars[scalar] = emitter.SampleScalar(scalar, animationTime, animationId,
                    defaults[scalar]);
            pool.Scale = MathF.Sqrt(
                transform.M11 * transform.M11 +
                transform.M12 * transform.M12 +
                transform.M13 * transform.M13);
            if (pool.Scale <= 0f || float.IsNaN(pool.Scale)) pool.Scale = 1f;
            pool.Origin = origin;
            pool.TouchedThisFrame = true;

            // The +0x1dc enable track is distinct from the rate track. Interactive
            // doodads use it to keep one-shot emitters dormant during Stand: for
            // example DarkIronNode's fast spray is enabled only at the beginning
            // of animation 150. Ignoring the gate turns that burst into a permanent
            // fountain. Reset the fractional carry while gated off, matching the
            // spell-particle lane, so re-enabling cannot release stale emission.
            bool emitting = emitter.SampleEnabled(animationTime, animationId);
            if (!emitting) pool.SpawnAccumulator = 0f;
            Advance(pool, dt, origin, emit: emitting);
        }

        // Pools nobody touched are out of range or gone with their placement.
        // Dropped rather than kept, because holding them would leak one pool per
        // doodad ever walked past.
        foreach (var key in _pools.Keys.ToArray())
        {
            Pool pool = _pools[key];
            if (pool.TouchedThisFrame) continue;
            Advance(pool, dt, pool.Origin, emit: false);
            if (pool.Particles.Count == 0) _pools.Remove(key);
        }

        int live = 0;
        foreach (var pool in _pools.Values) live += pool.Particles.Count;
        LiveParticles = live;
        ActivePools = _pools.Count;

        SimulateMilliseconds = System.Diagnostics.Stopwatch
            .GetElapsedTime(started).TotalMilliseconds;
    }

    private void Advance(Pool pool, float dt, Vector3 origin, bool emit)
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
                if (pool.Scalars[4] != 0f)
                {
                    p.Position.Y -= 0.5f * pool.Scalars[4] * sdt * sdt;
                    p.Velocity.Y -= pool.Scalars[4] * sdt;
                }
                if (e.Drag != 0f)
                {
                    float fdrag = MathF.Min(sdt * e.Drag, 1f);
                    p.Velocity -= fdrag * p.Velocity;
                }
            }
            else
            {
                p.Velocity.Z -= pool.Scalars[4] * dt;
                p.Position += p.Velocity * dt;
            }
            list[i] = p;
        }

        if (!emit) return; // orphaned effect: drain already-born particles

        // An emitter can be authored inert - dustwestfall's rate is 0.0 - so
        // this is a real case and not a guard against nothing.
        float rate = pool.Scalars[6] * DensityScale;
        if (rate <= 0f || pool.Scalars[5] <= 0f) return;
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
                float r = pool.Scalars[7]
                        + pool.Rand() * MathF.Max(0f, pool.Scalars[8] - pool.Scalars[7]);
                float lat = pool.Symmetric() * pool.Scalars[2];   // latitude
                float lon = pool.Symmetric() * pool.Scalars[3]; // longitude
                float clat = MathF.Cos(lat), slat = MathF.Sin(lat);
                float clon = MathF.Cos(lon), slon = MathF.Sin(lon);
                var shell = new Vector3(clat * clon, clat * slon, slat);  // unit
                posZ = r * shell;                                         // birth on the shell (the ring)
                if (pool.Scalars[9] != 0f)
                {
                    dirZ = posZ - new Vector3(0f, 0f, pool.Scalars[9]);
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
                    pool.Scalars[7] * 0.5f * pool.Symmetric(),
                    pool.Scalars[8] * 0.5f * pool.Symmetric(),
                    0f);
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

            // R(+Z,90) prepend in Z-up, then swap to the Y-up local frame.
            var localPos = Swap(Rot90Z(posZ));
            var localDir = Swap(Rot90Z(dirZ));

            float mspeed = pool.Scalars[0] * (1f + pool.Scalars[1] * pool.Symmetric()) * pool.Scale;

            return new Particle
            {
                Position = localPos,           // LOCAL (relative to pivot), re-projected at draw
                Velocity = localDir * mspeed,  // negative speed => inward; no time-reversal needed
                Age = 0f,
                Life = pool.Scalars[5],
                Phase = pool.NextPhase(),
            };
        }

        // ── WORLD SPACE (benilla "anchored" birth, sim.rs:601-622) ───────────
        //
        // The SAME emission kernel as the model-space branch above (benilla
        // emit_local, particles.rs:314-347): born across the area rectangle with a
        // NARROW spherical cone about the emitter's local +Z - polar
        // theta = S11*verticalRange off straight-up, azimuth phi = S11*horizontalRange
        // around it. The ONLY difference from model space is the transform: the
        // emitter/bone placement is baked in at birth and the particle stored in
        // world space (model space re-projects through the live bone each frame).
        //
        // WHY THE FLAME USED TO SPRAY EVERY WAY. The old path read horizontalRange
        // as a raw +/-range JITTER added straight onto X and Y:
        //     dir = (S11*hRange, S11*hRange, 1 + S11*vRange)
        // For the InstancePortal (hRange 0) that collapses to +/-Z, and the bone
        // sweep drew the disc - so it measured right on the one model it was tuned
        // against. But a brazier authors hRange = 2*pi: the +/-6.28 on X and Y swamp
        // the 1.0 on Z, normalise to almost pure horizontal, and the flame fires in
        // every direction at once - the "omnidirectional, not rising" bug. benilla
        // never does this; horizontalRange is an AZIMUTH ANGLE, so 2*pi just means
        // "any way around" while verticalRange holds the cone a few degrees off
        // straight up. The cone then rides the emitter's up axis and the fire climbs
        // from the top of the brazier. The portal is now a model-space emitter and
        // never reaches this branch, so nothing it needs is lost - and gone with the
        // jitter are the 24-slot clock face and the fast direction spin, both portal
        // choreography with no place in a flame.
        Vector3 wposZ, wdirZ;
        if (e.Shape == ParticleShape.Sphere)
        {
            // areaLength/areaWidth are min/max radius for a sphere emitter.
            float r = pool.Scalars[7]
                    + pool.Rand() * MathF.Max(0f, pool.Scalars[8] - pool.Scalars[7]);
            float lat = pool.Symmetric() * pool.Scalars[2];
            float lon = pool.Symmetric() * pool.Scalars[3];
            float clat = MathF.Cos(lat), slat = MathF.Sin(lat);
            float clon = MathF.Cos(lon), slon = MathF.Sin(lon);
            var shell = new Vector3(clat * clon, clat * slon, slat);
            wposZ = r * shell;
            if (pool.Scalars[9] != 0f)
            {
                wdirZ = wposZ - new Vector3(0f, 0f, pool.Scalars[9]);
                wdirZ = wdirZ.LengthSquared() > 1e-12f ? Vector3.Normalize(wdirZ) : Vector3.UnitZ;
            }
            else if ((e.Flags & 0x100) != 0) wdirZ = Vector3.UnitZ;   // sphere_up
            else wdirZ = shell;                                       // radial
        }
        else
        {
            // Plane (spline falls back to plane): born across the area rectangle,
            // direction a symmetric spherical cone about +Z.
            wposZ = new Vector3(
                pool.Scalars[7] * 0.5f * pool.Symmetric(),
                pool.Scalars[8] * 0.5f * pool.Symmetric(),
                0f);
            if (pool.Scalars[9] != 0f)
            {
                wdirZ = wposZ - new Vector3(0f, 0f, pool.Scalars[9]);
                wdirZ = wdirZ.LengthSquared() > 1e-12f ? Vector3.Normalize(wdirZ) : Vector3.UnitZ;
            }
            else
            {
                float theta = pool.Symmetric() * pool.Scalars[2];
                float phi = pool.Symmetric() * pool.Scalars[3];
                float st = MathF.Sin(theta), ct = MathF.Cos(theta);
                float sp = MathF.Sin(phi), cp = MathF.Cos(phi);
                wdirZ = new Vector3(st * cp, st * sp, ct);
            }
        }

        // R(+Z,90) prepend, then Swap into the Y-up frame the placement matrix
        // expects - identical to the model-space path and emit_local's rot90 tail.
        var wlocalPos = Swap(Rot90Z(wposZ));
        var wlocalDir = Swap(Rot90Z(wdirZ));

        // Bake the emitter bone's orientation at birth (benilla's placement.rotation
        // carries the bone). A static brazier bone is Identity, so local +Z stays
        // world up and the cone rises straight from the brazier; an animated emitter
        // bone rides its live pose. No SpinRateScale here - that was the portal sweep.
        var boneRot = pool.BoneRotationOverride ?? e.SampleBoneRotation(pool.AnimationTime);
        wlocalPos = Vector3.Transform(wlocalPos, boneRot);
        wlocalDir = Vector3.Transform(wlocalDir, boneRot);

        // Then the placement's rotation + scale (its translation is the emitter
        // origin, added below): benilla's placement.rotation * (scale * dir).
        var rotation = pool.Transform;
        rotation.M41 = rotation.M42 = rotation.M43 = 0f;

        var offsetWorld = Vector3.Transform(wlocalPos, rotation);
        var dirWorld = Vector3.TransformNormal(wlocalDir, rotation);
        dirWorld = dirWorld.LengthSquared() > 1e-12f ? Vector3.Normalize(dirWorld) : Vector3.UnitY;

        float speed = pool.Scalars[0] * (1f + pool.Scalars[1] * pool.Symmetric()) * pool.Scale;

        var velocity = dirWorld * speed;
        var position = origin + offsetWorld;

        // TIME REVERSAL. Start where the particle would have ENDED and travel back -
        // converging (negative-speed) emitters only, so a waterfall spirals inward
        // while fire, fountaining outward at positive speed, is untouched.
        if (ReverseConverging && pool.Scalars[0] < 0f)
        {
            position += velocity * pool.Scalars[5];
            velocity = -velocity;
        }

        return new Particle
        {
            Position = position,
            Velocity = velocity,
            Age = 0f,
            Life = pool.Scalars[5],
            Phase = pool.NextPhase(),
        };
    }

    // ── Draw ─────────────────────────────────────────────────────────────────

    public unsafe void Render(Camera camera)
        => RenderInternal(camera.RelativeViewProjection, camera.Position, camera.Forward,
                          Vector3.UnitZ, camera.FlatRight, camera);

    // Off-camera path (the login glue scene): draw the live pools through an arbitrary
    // view. worldUp orients the billboard basis (Y-up for the glue scene); fallbackRight
    // covers the degenerate look-along-up case. No portal-surface film (a world-only effect).
    public unsafe void Render(Matrix4x4 relativeViewProjection, Vector3 eye, Vector3 forward,
                              Vector3 worldUp, Vector3 fallbackRight)
        => RenderInternal(relativeViewProjection, eye, forward, worldUp, fallbackRight, null);

    private unsafe void RenderInternal(Matrix4x4 relativeViewProjection, Vector3 eye, Vector3 forward,
                                       Vector3 worldUp, Vector3 fallbackRight, Camera? portalCamera)
    {
        DrawnLastFrame = 0;
        DrawMilliseconds = 0.0;
        foreach (Pool pool in _pools.Values) pool.DrawnLastFrame = 0;
        // PortalSurface is the cosmetic toggle for legacy inferred instance
        // films. Explicit Mage apertures carry readiness/interaction state and
        // must remain visible independently of that tuning checkbox.
        bool canDrawSurfaces = portalCamera is not null && _surfaceShader is not null &&
            (_magePortalApertures.Count != 0 || (PortalSurface && _pools.Count != 0));
        bool canDrawParticles = Enabled && _shader is not null && _pools.Count != 0;
        if (!canDrawSurfaces && !canDrawParticles) return;

        long started = System.Diagnostics.Stopwatch.GetTimestamp();

        // The flat portal-surface film, drawn BEFORE the sprites so they sit over
        // it (interior geometry -> frost film -> additive particles).
        if (canDrawSurfaces) DrawPortalSurfaces(portalCamera!);

        // The procedural doorway is interaction/readiness presentation, not an
        // M2 particle. Keep its sealed/live surface available when the user has
        // disabled motes; only the cosmetic rim sprites follow Enabled.
        if (!canDrawParticles)
        {
            DrawMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            return;
        }

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
        if (groups.Count == 0)
        {
            DrawMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            return;
        }


        _shader!.Use();
        _shader.Set("uViewProjection", relativeViewProjection);
        _shader.Set("uCameraOrigin", eye);
        // Screen-facing basis. Cross(forward, worldUp) degenerates when the
        // camera looks straight down - which is exactly how you look at a portal
        // on the ground - so fall back to the camera's flat right vector, which
        // is always defined.
        var right = Vector3.Cross(forward, worldUp);
        right = right.LengthSquared() > 1e-6f ? Vector3.Normalize(right) : fallbackRight;
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

            // Sprite-sheet flipbook (e.g. FlameLick's 4x4 flame sheet): the cell is now chosen
            // PER PARTICLE by its own age in Fill (GpuParticle.CellRect via SampleHeadCellRect)
            // and uploaded per instance as aCellRect — NOT one global-clock cell for the whole
            // draw group, which flipped every sprite in lockstep 24x/s and read as flicker. A
            // rows==cols==1 emitter uploads (0,0,1,1), so the model-space swirls stay byte-identical.
            // Here we only still detect a flipbook group to bias its mips toward the sharp base
            // level (a sub-cell minified through the whole sheet washes out to vapour).
            var fem = pools.Count > 0 ? pools[0].Emitter : null;
            int fcols = fem is not null ? Math.Max(1, (int)fem.TextureCols) : 1;
            int frows = fem is not null ? Math.Max(1, (int)fem.TextureRows) : 1;
            if (frows * fcols > 1)
                _shader.Set("uMipBias", -4f);

            UploadInstances();
            _gl.DrawArraysInstanced(PrimitiveType.TriangleStrip, 0, 4, (uint)_scratch.Count);
            foreach (Pool pool in pools) pool.DrawnLastFrame = pool.Particles.Count;
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
            var boneRot = pool.BoneRotationOverride ?? e.SampleBoneRotation(pool.AnimationTime * ModelSpinScale);
            var rot = pool.Transform;
            rot.M41 = rot.M42 = rot.M43 = 0f;   // rotation+scale only; the pivot is pool.Origin

            foreach (var p in pool.Particles)
            {
                float t = p.Life > 0f ? p.Age / p.Life : 1f;
                e.SampleRamp(t, out var rgba, out float scale);
                if (ParticleHueShift != 0f || ParticleSaturation != 1f || ParticleValue != 1f)
                    rgba = AdjustColor(rgba, ParticleHueShift, ParticleSaturation, ParticleValue);
                if (PortalCentreHole > 0f && !pool.Spell)
                {
                    float rc = p.Position.Length() * PortalScale;
                    if (rc < PortalCentreHole) rgba.W *= rc / PortalCentreHole;
                }
                float mnoise = TwinkleNoise(e.TwinkleSpeed, p.Age, p.Phase);
                // The twinklePercent DRAW GATE (benilla quads.rs, byte-verified 0x7b2adc):
                // below 1, a frame whose sample exceeds it emits no quad at all.
                if (e.TwinklePercent < 1f && mnoise > e.TwinklePercent) continue;
                scale *= e.Twinkle(mnoise);

                if (scale <= 0f || rgba.W <= 0.002f) continue;

                var spun = Vector3.Transform(p.Position * PortalScale, boneRot); // rotate about the pivot
                var centre = pool.Origin + Vector3.Transform(spun, rot);         // pivot + doodad transform

                _scratch.Add(new GpuParticle
                {
                    Centre = centre,
                    // benilla sizes a particle by its authored half-extent alone (quads.rs:156;
                    // instance scale only under emitter flag 0x200). PortalScale/SpriteSizeScale
                    // are portal-disc tuning (SpriteSizeScale=1.77 oversized the converging
                    // glow ~1.8x — the "too big circumference"); they must not touch spell pools.
                    Size = pool.Spell
                        ? scale * pool.Scale
                        : scale * pool.Scale * PortalScale * SpriteSizeScale * SpriteSizeScaleAll,
                    Colour = rgba,
                    CellRect = e.SampleHeadCellRect(t),   // (0,0,1,1) for the 1x1 swirls: unchanged
                });
            }
            return;
        }

        // WORLD SPACE (legacy): the spin was baked at birth; positions are world.
        foreach (var p in pool.Particles)
        {
            float t = p.Life > 0f ? p.Age / p.Life : 1f;

            // Flip which end of the life owns the density. See ReverseRamp.
            if (ReverseRamp && pool.Scalars[0] < 0f) t = 1f - t;

            e.SampleRamp(t, out var rgba, out float scale);

            // Empty the middle. Linear fade from the hole's edge inwards, so the
            // boundary is not a visible ring of its own.
            if (CentreHoleYards > 0f)
            {
                float r = Vector3.Distance(p.Position, pool.Origin);
                if (r < CentreHoleYards) rgba.W *= r / CentreHoleYards;
            }
            // Gated twinkle: the size ramp x the flicker, and the hard percent draw gate.
            // UI_MainMenu's brazier glows (emitters 25 and 27) author min 0 / max 1, so without
            // this they burn as a steady disc rather than pulsing.
            float wnoise = TwinkleNoise(e.TwinkleSpeed, p.Age, p.Phase);
            if (e.TwinklePercent < 1f && wnoise > e.TwinklePercent) continue;
            scale *= e.Twinkle(wnoise);

            if (scale <= 0f || rgba.W <= 0.002f) continue;

            _scratch.Add(new GpuParticle
            {
                Centre = p.Position,
                Size = scale * pool.Scale * BrazierSizeScaleAll,
                Colour = rgba,
                CellRect = e.SampleHeadCellRect(t),   // per-particle flame cell; (0,0,1,1) if 1x1
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
    /// One film per InstancePortal model placement. Its two MODEL-SPACE SPHERE
    /// emitters share the disc plane, so dedupe by rounded world origin. The
    /// model filename is part of the identity because ordinary props use the
    /// same emitter flags and shape. The disc lies in the emitter's local Y-Z
    /// plane (normal = local X), so the in-plane basis is the placement's
    /// transformed local Y and Z; the centre is the bone-pivot world position
    /// (pool.Origin).
    /// </summary>
    private unsafe void DrawPortalSurfaces(Camera camera)
    {
        if (_surfaceShader is null) return;

        // Keep the old instance-entrance inference for static scenery only.
        // A dynamic GameObject must use the explicit GUID registry below: a
        // model-space sphere emitter is not proof that a GO is a Mage portal.
        _surfaceSeen.Clear();
        _legacySurfacePools.Clear();
        if (PortalSurface)
        {
            foreach ((PoolKey poolKey, Pool pool) in _pools)
            {
                if (pool.OwnerGuid != 0) continue;
                // Model-space sphere is an emitter-space description, not a
                // portal identity. Candles, lamps and other ordinary props use
                // that same combination. Treating all of them as portals drew
                // green looking-glass quads through nearby walls (usually only
                // a triangular wedge remained visible). The authored static
                // entrance is identified by its model name, just as the portal
                // particle tuning and the reference renderer identify it.
                if (!IsInstancePortalPath(poolKey.Path)) continue;
                if (!pool.ModelSpace || pool.Emitter.Shape != ParticleShape.Sphere) continue;
                if (SoloEmitter >= 0 && pool.EmitterIndex != SoloEmitter) continue;
                if (pool.Particles.Count == 0) continue;

                var key = ((int)MathF.Round(pool.Origin.X * 4f),
                           (int)MathF.Round(pool.Origin.Y * 4f),
                           (int)MathF.Round(pool.Origin.Z * 4f));
                if (_surfaceSeen.Add(key)) _legacySurfacePools.Add(pool);
            }
        }

        if (_legacySurfacePools.Count == 0 && _magePortalApertures.Count == 0) return;

        // This pass sits between opaque world geometry and portal sprites. It
        // must not leak the state it establishes into either the particle pass
        // or source water. Save every state we mutate, including blend factors
        // and texture-unit zero rather than restoring assumed defaults.
        int* iv = stackalloc int[4];
        _gl.GetInteger(GLEnum.Viewport, iv);
        var framebufferOrigin = new Vector2(iv[0], iv[1]);
        var framebufferSize = new Vector2(Math.Max(1, iv[2]), Math.Max(1, iv[3]));

        _gl.GetInteger(GLEnum.CurrentProgram, iv); uint savedProgram = (uint)iv[0];
        _gl.GetInteger(GLEnum.VertexArrayBinding, iv); uint savedVao = (uint)iv[0];
        _gl.GetInteger(GLEnum.ActiveTexture, iv); TextureUnit savedActiveTexture = (TextureUnit)iv[0];
        _gl.GetInteger(GLEnum.DepthWritemask, iv); bool savedDepthMask = iv[0] != 0;
        _gl.GetInteger(GLEnum.BlendSrcRgb, iv); var savedBlendSrcRgb = (BlendingFactor)iv[0];
        _gl.GetInteger(GLEnum.BlendDstRgb, iv); var savedBlendDstRgb = (BlendingFactor)iv[0];
        _gl.GetInteger(GLEnum.BlendSrcAlpha, iv); var savedBlendSrcAlpha = (BlendingFactor)iv[0];
        _gl.GetInteger(GLEnum.BlendDstAlpha, iv); var savedBlendDstAlpha = (BlendingFactor)iv[0];
        bool savedDepthTest = _gl.IsEnabled(EnableCap.DepthTest);
        bool savedCullFace = _gl.IsEnabled(EnableCap.CullFace);
        bool savedBlend = _gl.IsEnabled(EnableCap.Blend);

        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.GetInteger(GLEnum.TextureBinding2D, iv); uint savedTexture0 = (uint)iv[0];

        try
        {
            _surfaceShader.Use();
            _surfaceShader.Set("uViewProjection", camera.RelativeViewProjection);
            _surfaceShader.Set("uCameraOrigin", camera.Position);
            _surfaceShader.Set("uTime", (float)_time);
            _surfaceShader.Set("uTint", HsvToRgb(PortalSurfaceHue, PortalSurfaceSat, PortalSurfaceVal));
            _surfaceShader.Set("uPortalView", 0);
            _surfaceShader.Set("uFramebufferOrigin", framebufferOrigin);
            _surfaceShader.Set("uMainFramebufferSize", framebufferSize);

            _gl.Enable(EnableCap.Blend);
            _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            _gl.Enable(EnableCap.DepthTest);
            _gl.DepthMask(false);
            _gl.Disable(EnableCap.CullFace);
            _gl.BindVertexArray(_surfaceVao);

            // Static instance entrances retain their original circular, faint
            // looking-glass film and never sample a destination texture.
            _surfaceShader.Set("uExplicit", 0);
            _surfaceShader.Set("uPortalViewAvailable", 0);
            _surfaceShader.Set("uLiveBlend", 0f);
            _surfaceShader.Set("uSealProgress", 1f);
            _gl.BindTexture(TextureTarget.Texture2D, 0);
            foreach (Pool pool in _legacySurfacePools)
            {
                var rot = pool.Transform;
                rot.M41 = rot.M42 = rot.M43 = 0f;
                var u = NormalizedOr(Vector3.TransformNormal(Vector3.UnitY, rot), Vector3.UnitY);
                var v = NormalizedOr(Vector3.TransformNormal(Vector3.UnitZ, rot), Vector3.UnitZ);

                float half = MathF.Max(0.1f, pool.Emitter.EmissionAreaLength)
                           * pool.Scale * MathF.Max(0.1f, PortalSurfaceSize) * PortalScale;

                _surfaceShader.Set("uCenter", pool.Origin);
                _surfaceShader.Set("uRight", u * half);
                _surfaceShader.Set("uUp", v * half);
                _surfaceShader.Set("uAlpha", PortalSurfaceAlpha);
                _gl.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
            }

            // Summoned Mage portals are explicit, independently-sized oval
            // doorways. Their seal exists independently of the legacy cosmetic
            // M2. The inline shader borrows InstancePortal's visual grammar --
            // broken lanes spiralling inward from the rim -- but owns the motion
            // across the full aperture and therefore never needs a small nested
            // copy of that model. Gold also keeps the preload state visually
            // distinct from the cool destination view that replaces it.
            _surfaceShader.Set("uExplicit", 1);
            _surfaceShader.Set("uTint", new Vector3(0.96f, 0.63f, 0.12f));
            foreach ((ulong guid, MagePortalAperture portal) in _magePortalApertures)
            {
                bool live = portal.LiveTexture != 0 && portal.LiveBlend > 0f;
                // The stock rim/model can sit almost exactly on the procedural
                // plane. Nudge the film toward the current camera so the same
                // opening wins the depth test from either approach side without
                // disabling ordinary foreground occlusion.
                Vector3 normal = NormalizedOr(
                    Vector3.Cross(portal.Right, portal.Up), Vector3.UnitX);
                float cameraSide = Vector3.Dot(camera.Position - portal.Center, normal) >= 0f
                    ? 1f
                    : -1f;
                Vector3 drawCenter = portal.Center + normal * (cameraSide * 0.075f);
                _surfaceShader.Set("uCenter", drawCenter);
                _surfaceShader.Set("uRight", portal.Right * portal.HalfWidth);
                _surfaceShader.Set("uUp", portal.Up * portal.HalfHeight);
                _surfaceShader.Set("uAlpha", portal.SealAlpha);
                _surfaceShader.Set("uSealProgress", portal.SealProgress);
                // Stable per-portal phase: two nearby Mage portals should not
                // look like one screen-space animation copied in lockstep.
                uint phaseBits = (uint)(guid ^ (guid >> 32));
                _surfaceShader.Set("uPortalPhase",
                    (phaseBits & 0xFFFFu) * (MathF.Tau / 65536f));
                _surfaceShader.Set("uPortalViewAvailable", live ? 1 : 0);
                _surfaceShader.Set("uLiveBlend", live ? portal.LiveBlend : 0f);
                _gl.BindTexture(TextureTarget.Texture2D, live ? portal.LiveTexture : 0);
                _gl.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
            }
        }
        finally
        {
            _gl.UseProgram(savedProgram);
            _gl.BindVertexArray(savedVao);
            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, savedTexture0);
            _gl.ActiveTexture(savedActiveTexture);
            _gl.DepthMask(savedDepthMask);
            _gl.BlendFuncSeparate(savedBlendSrcRgb, savedBlendDstRgb,
                savedBlendSrcAlpha, savedBlendDstAlpha);
            if (savedDepthTest) _gl.Enable(EnableCap.DepthTest); else _gl.Disable(EnableCap.DepthTest);
            if (savedCullFace) _gl.Enable(EnableCap.CullFace); else _gl.Disable(EnableCap.CullFace);
            if (savedBlend) _gl.Enable(EnableCap.Blend); else _gl.Disable(EnableCap.Blend);
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

    // The static lane keeps its original circular frost. The explicit lane is a
    // tall rounded doorway: an animated sealed film forms from the centre, then
    // optionally yields to a complete live destination texture sampled in main-
    // framebuffer screen space. Portal-local UV sampling would stretch the view
    // like a television and destroy perspective parallax.
    private const string SurfaceFrag = @"#version 330 core
in vec2 vUv;
uniform float uTime;
uniform float uAlpha;
uniform vec3 uTint;
uniform int uExplicit;
uniform float uSealProgress;
uniform float uPortalPhase;
uniform sampler2D uPortalView;
uniform int uPortalViewAvailable;
uniform float uLiveBlend;
uniform vec2 uFramebufferOrigin;
uniform vec2 uMainFramebufferSize;
out vec4 FragColor;

void main()
{
    if (uExplicit == 0)
    {
        float r = length(vUv);
        float edge = smoothstep(1.05, 0.60, r);
        float ripple = 0.80
            + 0.10 * sin(vUv.x * 6.0 + uTime * 0.7)
            + 0.10 * sin((vUv.x + vUv.y) * 5.0 - uTime * 0.5);
        float a = uAlpha * edge * ripple;
        if (a <= 0.002) discard;
        FragColor = vec4(uTint * ripple, a);
        return;
    }

    // A 6x8 world-space ellipse reads as a large portal disc rather than a
    // luminous UI rectangle. During preload it grows from the centre, then
    // keeps the InstancePortal character: bright broken streams born around
    // the rim spiral inward instead of sitting on the opening as a wavy sheet.
    // This is procedural so it remains aligned to the large GUID-owned aperture
    // from either side and does not resurrect the small legacy M2 inside it.
    float radius = length(vUv);
    float shape = 1.0 - smoothstep(0.975, 1.015, radius);
    float progress = smoothstep(0.0, 1.0, uSealProgress);
    float frontier = mix(0.015, 1.035, progress);
    float formed = 1.0 - smoothstep(frontier - 0.045, frontier + 0.015, radius);
    float building = step(0.001, progress) * (1.0 - smoothstep(0.94, 1.0, progress));
    float buildBand = exp(-abs(radius - frontier) * 34.0) * building;
    float aperture = shape * clamp(formed + buildBand * 0.35, 0.0, 1.0);
    if (aperture <= 0.002) discard;

    float angle = atan(vUv.y, vUv.x);
    float outerRim = smoothstep(0.80, 0.94, radius)
        * (1.0 - smoothstep(0.965, 1.01, radius));

    // The +time term makes constant-phase points move toward smaller radius.
    // Six curled lanes and the faster counter-lane echo InstancePortal's live
    // bone sweep, while high powers break them down into bright narrow threads.
    float laneWave = 0.5 + 0.5 * cos(
        angle * 6.0 + radius * 18.0 + uTime * 2.55 + uPortalPhase);
    float counterWave = 0.5 + 0.5 * cos(
        angle * -5.0 + radius * 23.0 + uTime * 3.15 - uPortalPhase * 0.7);
    float lane = pow(laneWave, 13.0);
    float counterLane = pow(counterWave, 17.0);

    // Segment the lanes into converging sparks rather than drawing smooth neon
    // curves. Their radial packet phase also travels inward as time advances.
    float beadWave = 0.5 + 0.5 * cos(
        angle * 31.0 + radius * 11.0 - uTime * 4.1 + uPortalPhase * 1.7);
    float packetWave = 0.5 + 0.5 * cos(
        radius * 34.0 + uTime * 4.8 + sin(angle * 6.0 + uPortalPhase) * 2.2);
    float beads = pow(beadWave, 11.0);
    float packets = pow(packetWave, 15.0);
    float streams = lane * (0.22 + beads * 0.78)
        + counterLane * (0.12 + packets * 0.60);

    // Keep the rim strongest and let the inward trails thin toward the centre,
    // matching the authored negative-speed emitter without filling the oval
    // with an opaque gold fog. A small arrival glint sells convergence.
    float streamEnvelope = smoothstep(0.08, 0.24, radius)
        * (1.0 - smoothstep(0.91, 1.0, radius));
    streams *= streamEnvelope;
    float centreGlint = exp(-radius * 13.0)
        * (0.45 + 0.55 * sin(uTime * 3.4 + uPortalPhase) * sin(uTime * 3.4 + uPortalPhase));
    float rimPulse = 0.88 + 0.12 * sin(uTime * 1.65 + uPortalPhase);

    vec3 deepViolet = vec3(0.022, 0.017, 0.050);
    vec3 paleGold = vec3(1.0, 0.88, 0.48);
    vec3 filmRgb = deepViolet
        + uTint * (0.035 + outerRim * 0.54 * rimPulse)
        + uTint * streams * 0.72
        + paleGold * (streams * packets * 0.34 + centreGlint * 0.42)
        + paleGold * buildBand * 0.68;
    float veil = 0.48 + outerRim * 0.28
        + clamp(streams, 0.0, 1.0) * 0.20 + centreGlint * 0.12;
    float filmAlpha = uAlpha * aperture * veil;

    float live = uPortalViewAvailable != 0
        ? clamp(uLiveBlend, 0.0, 1.0) * smoothstep(0.88, 1.0, progress)
        : 0.0;
    // At live==1 no gold preload contribution survives: only the destination
    // framebuffer remains. The animation is a preparation state, not a glaze.
    vec3 rgb = filmRgb;
    if (live > 0.0)
    {
        vec2 screenUv = (gl_FragCoord.xy - uFramebufferOrigin)
            / max(uMainFramebufferSize, vec2(1.0));
        vec3 destination = texture(uPortalView, clamp(screenUv, vec2(0.0), vec2(1.0))).rgb;
        rgb = mix(filmRgb, destination, live);
    }

    float a = mix(filmAlpha, aperture, live);
    if (a <= 0.002) discard;
    FragColor = vec4(rgb, a);
}";

    /// <summary>Fill-light colour (HSV knobs premultiplied by intensity), for
    /// GameLoop to hand to the world renderers.</summary>
    public Vector3 PortalLightRgb() =>
        HsvToRgb(PortalLightHue, PortalLightSat, PortalLightVal) * PortalLightIntensity;

    /// <summary>World centre of the nearest explicit Mage aperture or static
    /// instance portal, for the beyond-portal fill light. Dynamic GameObjects
    /// never enter through legacy emitter-shape inference.</summary>
    public bool TryGetNearestPortal(Vector3 camera, float maxDistance, out Vector3 centre)
    {
        centre = default;
        float best = maxDistance * maxDistance;
        bool found = false;
        foreach (MagePortalAperture portal in _magePortalApertures.Values)
        {
            float d = Vector3.DistanceSquared(portal.Center, camera);
            if (d > best) continue;
            best = d; centre = portal.Center; found = true;
        }
        foreach ((PoolKey poolKey, Pool pool) in _pools)
        {
            if (pool.OwnerGuid != 0) continue;
            if (!IsInstancePortalPath(poolKey.Path)) continue;
            if (!pool.ModelSpace || pool.Emitter.Shape != ParticleShape.Sphere) continue;
            float d = Vector3.DistanceSquared(pool.Origin, camera);
            if (d > best) continue;
            best = d; centre = pool.Origin; found = true;
        }
        return found;
    }

    private static bool IsInstancePortalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string normalized = path.Replace('/', '\\');
        int slash = normalized.LastIndexOf('\\');
        string file = slash >= 0 ? normalized[(slash + 1)..] : normalized;
        int dot = file.LastIndexOf('.');
        if (dot >= 0) file = file[..dot];
        return file.Equals("InstancePortal", StringComparison.OrdinalIgnoreCase);
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

        const uint stride = 12 * sizeof(float);         // centre(3) size(1) colour(4) cellRect(4)
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.VertexAttribDivisor(1, 1);

        _gl.EnableVertexAttribArray(2);
        _gl.VertexAttribPointer(2, 1, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        _gl.VertexAttribDivisor(2, 1);

        _gl.EnableVertexAttribArray(3);
        _gl.VertexAttribPointer(3, 4, VertexAttribPointerType.Float, false, stride, (void*)(4 * sizeof(float)));
        _gl.VertexAttribDivisor(3, 1);

        // Per-particle flipbook cell rect (offset.xy, scale.xy) — location 4, one per instance.
        // Non-flipbook pools upload (0,0,1,1), so the model-space swirls sample the whole texture
        // exactly as before. This is the buffer the flame flipbook now rides, per particle.
        _gl.EnableVertexAttribArray(4);
        _gl.VertexAttribPointer(4, 4, VertexAttribPointerType.Float, false, stride, (void*)(8 * sizeof(float)));
        _gl.VertexAttribDivisor(4, 1);

        _gl.BindVertexArray(0);
    }

    private unsafe void UploadInstances()
    {
        int count = _scratch.Count;
        var data = new float[count * 12];
        for (int i = 0; i < count; i++)
        {
            var g = _scratch[i];
            int o = i * 12;
            data[o] = g.Centre.X; data[o + 1] = g.Centre.Y; data[o + 2] = g.Centre.Z;
            data[o + 3] = g.Size;
            data[o + 4] = g.Colour.X; data[o + 5] = g.Colour.Y;
            data[o + 6] = g.Colour.Z; data[o + 7] = g.Colour.W;
            data[o + 8] = g.CellRect.X; data[o + 9] = g.CellRect.Y;
            data[o + 10] = g.CellRect.Z; data[o + 11] = g.CellRect.W;
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
        _magePortalApertures.Clear();
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
        _magePortalApertures.Clear();
        LiveParticles = 0;
        ActivePools = 0;
    }
}

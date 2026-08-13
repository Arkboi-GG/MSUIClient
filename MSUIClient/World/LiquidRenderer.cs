using System.Numerics;
using Silk.NET.OpenGL;
using MSUIClient.Engine;
using MSUIClient.Formats;
using MSUIClient.World.Wmo;
using Shader = MSUIClient.Engine.Shader;
using Texture = MSUIClient.Engine.Texture;

namespace MSUIClient.World;

/// <summary>
/// Renders open-world liquid (ADT MCLQ: lakes, rivers, ocean, slime, magma) as a
/// translucent, wave-displaced, depth-aware surface, plus a full-screen tint when
/// the camera is submerged.
///
/// Stage 2 ("look at wowee") - three things changed versus the flat stage-1 sheet:
///
///   1. DEPTH IS BAKED PER VERTEX. When the mesh is built, every water vertex
///      stores how deep the water is there: surfaceZ minus the terrain height at
///      the SAME grid position (the liquid 9x9 grid is index-aligned with the
///      chunk's MCVT outer vertices, so no spatial query is needed). The shader
///      uses that depth to fade the water transparent at the shoreline and darken
///      it in deep areas - the depth cue the flat version completely lacked.
///
///   2. THE SURFACE IS DISPLACED BY GERSTNER WAVES in the vertex shader, so it has
///      real, moving relief instead of reading as a painted line.
///
///   3. IT DRAWS AFTER THE CHARACTER (see GameLoop.Render). Water tests depth but
///      does not write it, so submerged parts of the character are correctly
///      covered by the surface in front of them, and a submerged camera gets the
///      underwater overlay this class also draws.
///
/// Placement still mirrors TerrainTile.Prepare exactly, so water aligns with the
/// ground:
///   originX = (32 - row) * 533.33333 ; originY = (32 - col) * 533.33333
///   worldX  = originX - (chunk.IndexY*8 + r) * CELL_SIZE
///   worldY  = originY - (chunk.IndexX*8 + c) * CELL_SIZE
///   worldZ  = MclqLayer.VertexHeights[r*9 + c]   (already absolute WoW Z)
///
/// WMO liquid (MLIQ - Stormwind canals), LiquidType.dbc colours, planar
/// reflection and screen-space refraction are still later stages.
/// </summary>
public sealed class LiquidRenderer : IDisposable
{
    private const int FloatsPerVertex = 5;   // ADT path: position(3) + type(1) + depth(1)

    /// <summary>
    /// WMO (MLIQ) vertex format only: position(3) + type(1) + depth(1) + authored
    /// UV(2). The two extra floats carry the MLIQ per-vertex s/t — Blizzard's
    /// hand-authored magma flow mapping (the big swirls dragged around
    /// Blackrock's central spire). The ADT path keeps its 5-float format and its
    /// planar mapping BIT-IDENTICALLY: water.vert reads the UV from attribute 3,
    /// which ADT VAOs never enable, and the shader only looks at it when the
    /// uWmoAuthoredUv uniform is raised for the WMO draw loop below (and the
    /// vertex is magma — for WMO water/slime the same MLIQ bytes are flow data,
    /// not UVs, so those keep planar mapping too).
    /// </summary>
    private const int WmoFloatsPerVertex = 7;

    /// <summary>
    /// MSUI_WMO_LIQUID_TRACE=1: log every WMO liquid surface as it is meshed —
    /// world Z range of the visible region, tile substances, authored-UV range.
    /// The instrument that verified Blackrock's lava height against the MLIQ
    /// bytes (SYSTEM_WATER.md §7.4b).
    /// </summary>
    private static readonly bool WmoLiquidTrace =
        Environment.GetEnvironmentVariable("MSUI_WMO_LIQUID_TRACE") == "1";

    /// <summary>CPU-side copy of one water layer, kept so the camera/player can be
    /// tested for submersion without reading back from the GPU.</summary>
    private sealed class SurfaceLayer
    {
        public double OriginX, OriginY;
        public float Cell;
        public int GridRowBase, GridColBase;
        public required float[] Heights;   // 81, absolute WoW Z, row-major r*9+c
        public required bool[] Render;     // 64, r*8+c
        public byte Type;
    }

    private sealed class TileMesh : IDisposable
    {
        public uint Vao, Vbo, Ebo;
        public int IndexCount;
        public List<SurfaceLayer> Surfaces = [];

        /// <summary>
        /// World-space bounds, for the frustum test the draw loop did not have.
        ///
        /// This renderer was the only one in the client submitting its entire
        /// resident set every frame with no visibility test of any kind — an
        /// ocean tile behind the camera still paid full vertex cost, and water
        /// is double-sided blended fill with depth-write off, so it is the
        /// most expensive thing per pixel we draw.
        /// </summary>
        public Vector3 BoundsMin = new(float.MaxValue, float.MaxValue, float.MaxValue);
        public Vector3 BoundsMax = new(float.MinValue, float.MinValue, float.MinValue);
        private GL? _gl;
        public void Attach(GL gl) => _gl = gl;
        public void Dispose()
        {
            if (_gl is null) return;
            _gl.DeleteVertexArray(Vao);
            _gl.DeleteBuffer(Vbo);
            _gl.DeleteBuffer(Ebo);
        }
    }

    private readonly GL _gl;
    private Shader? _shader;
    private Shader? _underwater;
    private uint _overlayVao;
    private readonly Dictionary<(int col, int row), TileMesh> _tiles = [];

    // ── WMO liquid (MLIQ) — a second, fully separate mesh set ───────────────
    //
    // Built from WmoRenderer.EnumerateLiquid() and rebuilt whenever
    // WmoRenderer.LiquidVersion moves (groups are adopted asynchronously, so a
    // tile-crossing trigger would fire before Model.Liquids is populated).
    //
    // DRAW-ONLY, on purpose: these meshes are NOT added to TryGetSurface, so
    // WMO liquid contributes nothing to submersion, the underwater tint or the
    // walking wake. That is the SYSTEM_WATER.md §7 warning — the first PLAN_15
    // build rewrote the shared surface query and broke open-world water; this
    // one cannot, because the shared path never sees these meshes.
    private readonly List<TileMesh> _wmoMeshes = [];
    private int _wmoLiquidVersionSeen = int.MinValue;

    public bool Enabled { get; set; } = true;

    /// <summary>Draw the WMO (MLIQ) liquid set — Blackrock's lava lake, the
    /// Stormwind canals. Off skips only the WMO draw loop; ADT water is
    /// untouched either way.</summary>
    public bool WmoLiquidEnabled { get; set; } = true;

    /// <summary>WMO liquid surfaces currently meshed (surfaces whose tiles were
    /// all hidden build nothing and are not counted).</summary>
    public int WmoSurfaceCount => _wmoMeshes.Count;

    /// <summary>WMO liquid surfaces that survived the frustum test this frame.</summary>
    public int WmoSurfacesDrawnLastFrame { get; private set; }

    // --- Real vanilla animated liquid textures, loaded from the client MPQs ---
    // Vanilla stores each liquid as numbered BLP frames; we stack the frames of
    // one type into a single array texture and cross-fade between frames in the
    // shader. One array per basic type; a 1x1 dummy keeps every sampler bound.
    private Texture? _texWater, _texOcean, _texSlime, _texMagma, _dummyTex;
    private int _framesWater, _framesOcean, _framesSlime, _framesMagma;

    // Frame counts, exposed read-only for the Water Tuning HUD.
    public int WaterFrames  => _framesWater;
    public int OceanFrames  => _framesOcean;
    public int SlimeFrames  => _framesSlime;
    public int MagmaFrames  => _framesMagma;

    // ================= LIVE WATER TUNING KNOBS =================
    // Every hard-coded constant that used to live in water.frag/water.vert is now
    // one of these, pushed as a uniform each frame and driven by the Water Tuning
    // HUD (Program.WaterTuningWindow). Defaults reproduce the current look, so at
    // startup nothing changes until a slider is moved. Ranges are in the HUD.

    // Texture + animation
    public float TextureScale { get; set; } = 0.16f;   // world yards -> UV (smaller = bigger cells)
    public float AnimationFps { get; set; } = 12f;     // frames per second
    public float FrameBlend   { get; set; } = 0f;      // 0 = discrete swap, 1 = full cross-fade
    public float TexBrightness{ get; set; } = 1f;      // multiply texture colour
    public float TexContrast  { get; set; } = 1f;      // contrast around mid-grey
    public Vector3 TexTint    { get; set; } = Vector3.One;  // per-channel texture tint

    // Opacity
    public float Opacity      { get; set; } = 1.0f;    // deep-water alpha
    public float ShoreFade    { get; set; } = 0.85f;   // alpha fraction at the waterline
    public float ShoreWidth   { get; set; } = 1.2f;    // yards over which the shoreline softens

    // Depth
    public float DepthDarken  { get; set; } = 0.78f;   // deep-water brightness multiplier (higher = brighter)
    public float DepthRate    { get; set; } = 0.12f;   // how fast it darkens with depth

    // Lighting (flat - the texture carries the ripples). Tuned so the texture
    // shows near its own brightness at noon rather than being multiplied down.
    public float Brightness   { get; set; } = 0.90f;   // base surface brightness
    public float AmbientAmount{ get; set; } = 0.6f;    // ambient contribution
    public float SunAmount    { get; set; } = 0.30f;   // sun contribution
    public float SkySheen     { get; set; } = 0.14f;   // grazing-angle sky tint

    // Geometry waves (0 = flat still plane, the vanilla look)
    // ── Authored colours (PLAN_12) ──────────────────────────────────────────
    //
    // Defaults are water.frag's own invented constants, so these are inert until
    // WorldAtmosphere hands over something better. UseAuthoredColors AND
    // HasAuthoredColors must both be true before the shader looks at them, and
    // when either is false the shader arithmetic reduces to exactly what shipped.

    /// <summary>
    /// The A/B for PLAN_12. Off is bit-identical to the pre-PLAN_12 look.
    ///
    /// **DEFAULTS OFF as of 2026-07-26, and should stay off.** On, this ruins the
    /// river: water.frag MULTIPLIES the animated liquid texture by the band
    /// colour, and Azeroth's authored river-close is (0.000, 0.114, 0.161) -- red
    /// exactly zero. Vanilla's lake_a.N.blp frames ARE the bright animated
    /// highlight layer, so multiplying them by near-black leaves a dark,
    /// monocolour, apparently-static surface.
    ///
    /// The band indexing is CORRECT and the values are REAL -- the interpretation
    /// is what is wrong. These bands are not a texture tint. WoWee loads all 18
    /// colour bands, consumes seven, and hardcodes water colour per liquid type
    /// instead. SYSTEM_WATER.md section 5 has the full evidence.
    /// </summary>
    public bool UseAuthoredColors { get; set; }

    /// <summary>Set from WorldAtmosphere.AuthoredWaterReady - see ApplyAtmosphere.</summary>
    public bool HasAuthoredColors { get; set; }

    // ── The AUTHORED inbox ──────────────────────────────────────────────────
    // Program.cs overwrites these from WorldAtmosphere every frame with the
    // Light.dbc bands. They only reach the shader when AuthoredColorsActive.
    public Vector3 OceanClose { get; set; } = new(0.06f, 0.20f, 0.28f);
    public Vector3 OceanFar   { get; set; } = new(0.02f, 0.09f, 0.16f);
    public Vector3 RiverClose { get; set; } = new(0.10f, 0.26f, 0.26f);
    public Vector3 RiverFar   { get; set; } = new(0.05f, 0.15f, 0.16f);

    // ── The TUNED body colours — the shipping look ──────────────────────────
    //
    // THE WATER TEXTURE CARRIES NO COLOUR. Measured 2026-07-26: lake_a.1.blp is
    // mean RGB (0.014, 0.014, 0.014), ocean_h.1.blp (0.016, 0.016, 0.016) — both
    // near-black GREYSCALE highlight masks. (Controls decoded with the same code:
    // lava.1.blp (0.688, 0.089, 0.000), slime.1.blp (0.268, 0.517, 0.074),
    // ElwynnGrassBase (0.365, 0.412, 0.009) — so the decoder is sound and those
    // three really are coloured.)
    //
    // That is why magma and slime have always looked right and water has always
    // looked black: the shader was using the mask AS the colour. These two are
    // where the river and the sea actually get their colour from now.
    //
    // Starting values are WoWee's, which is the same conclusion reached
    // independently after reading the same DBCs — WaterRenderer::getLiquidColor,
    // "inland: richer blue" and "ocean: deep blue".
    // RIVERS ARE ESSENTIALLY COLOURLESS in 1.12 - Nico, 2026-07-26, after seeing
    // the blue version in game. The blue below was WoWee's "inland: richer blue",
    // which turned out to be WoWee's own look rather than vanilla's. A river reads
    // as the riverbed seen through a faint cool wash, not as a blue sheet.
    // The OCEAN keeps its blue - that part was never wrong.
    public Vector3 RiverBody { get; set; } = new(0.13f, 0.16f, 0.17f);
    public Vector3 OceanBody { get; set; } = new(0.04f, 0.16f, 0.38f);

    // Shallow/deep are derived from the body rather than authored separately, so
    // there is ONE colour to dial per liquid instead of four. Factors are WoWee's
    // (shallowColor = base * 1.2, deepColor = base * vec3(0.3, 0.5, 0.7)) — deep
    // water gets darker and shifts blue, which is what absorption does.
    private static Vector3 Shallow(Vector3 body) => body * 1.2f;
    private static Vector3 Deep(Vector3 body) => body * new Vector3(0.3f, 0.5f, 0.7f);

    /// <summary>
    /// How hard the animated liquid texture is added on top of the body colour.
    ///
    /// The mask peaks at 0.158 luminance, so this lifts it into a visible
    /// sparkle. 0 gives a completely still surface — useful for seeing the body
    /// colour on its own while tuning.
    /// </summary>
    public float HighlightGain { get; set; } = 4.0f;

    // -- The walking wake (PLAN_16) ------------------------------------------
    //
    // A single V-shaped bow wave anchored at the player and extending backward.
    //
    // The first version kept a ring of eight recent positions and stamped the
    // mask at each. That was wrong, and the reason is worth keeping: decoding
    // XTextures\splash\wake.blp shows its alpha channel IS ALREADY A V - a narrow
    // wedge that splits into two diverging arms. Stamping it eight times drew
    // eight overlapping Vs, which read as a chain of blobs. THE TEXTURE IS THE
    // TRAIL; it only needs placing once.
    //
    // The lesson, which this project keeps paying for: LOOK AT THE ASSET before
    // deciding how to use it. One alpha dump would have settled the shape before
    // any of the trail machinery was written.
    private Texture? _texWakeMask, _dummy2D;
    private bool _hasWakeTex;

    private Vector2 _wakePos;
    private Vector2 _wakeDir = new(1f, 0f);
    private float _wakeAmount;
    private Vector3 _lastWakeSample;
    private bool _haveLastSample;
    private float _wakeScroll;

    /// <summary>Master switch. Off is a bit-identical image to pre-PLAN_16 water.</summary>
    public bool WakeEnabled { get; set; } = true;

    /// <summary>Overall visibility. 0 is the kill switch.</summary>
    public float WakeStrength { get; set; } = 0.9f;

    /// <summary>Yards the V extends behind you.</summary>
    public float WakeLength { get; set; } = 4.5f;

    /// <summary>Yards across at its widest.</summary>
    public float WakeWidth { get; set; } = 2.6f;

    /// <summary>Yards the apex sits ahead of the feet, so the churn starts at the body.</summary>
    public float WakeAhead { get; set; } = 0.6f;

    /// <summary>Speed, in yards/sec, at which the wake reaches full strength.</summary>
    public float WakeFullSpeed { get; set; } = 2.5f;

    /// <summary>Seconds the wake takes to fade out once you stop.</summary>
    public float WakeFade { get; set; } = 0.45f;

    /// <summary>How many wavefronts fit along the length. 1 = a single chevron.</summary>
    public float WakeRepeat { get; set; } = 2.5f;

    /// <summary>
    /// How world-locked the crests are. 1.0 = exactly locked: the wake sits still
    /// in the river and you move through it, which is what the real client does.
    /// Below 1 the pattern drags along with you; above 1 it streams backward
    /// faster than you move.
    /// </summary>
    public float WakeWorldLock { get; set; } = 1.0f;

    /// <summary>Colour ADDED where the water is churned. Added, so keep it modest.</summary>
    public Vector3 WakeColor { get; set; } = new(0.30f, 0.36f, 0.42f);

    /// <summary>
    /// How much the wake also lifts surface alpha. Not decoration: a wake happens
    /// in SHALLOW water, which is exactly where ShoreFade has already pulled alpha
    /// down, so colour alone barely shows where the effect lives.
    /// </summary>
    public float WakeOpacity { get; set; } = 0.40f;

    /// <summary>Current wake intensity 0..1, for the HUD.</summary>
    public float WakeAmount => _wakeAmount;

    /// <summary>True when wake.blp resolved out of the MPQs.</summary>
    public bool HasWakeTexture => _hasWakeTex;

    /// <summary>
    /// Advance the wake. Call once per frame BEFORE <see cref="Render"/>.
    ///
    /// inWater is the caller's gate - it is the only place that knows where the
    /// feet are relative to the surface.
    ///
    /// Direction comes from actual TRAVEL, not body yaw, so strafing or backing
    /// up lays the V along the way you are really moving. Body yaw only seeds it.
    /// The direction is HELD when you stop rather than zeroed, so the V fades out
    /// pointing the way it was instead of snapping to an axis on the last frame.
    /// </summary>
    public void UpdateWake(Vector3 playerPos, float bodyYawRadians, float dt, bool inWater)
    {
        if (!WakeEnabled) { _wakeAmount = 0f; _haveLastSample = false; return; }
        if (dt <= 0f) return;

        _wakePos = new Vector2(playerPos.X, playerPos.Y);

        float speed = 0f;
        if (_haveLastSample)
        {
            var delta = new Vector2(playerPos.X - _lastWakeSample.X, playerPos.Y - _lastWakeSample.Y);
            float moved = delta.Length();
            speed = moved / dt;
            // Ignore sub-millimetre jitter, which would otherwise spin the
            // direction vector wildly while standing still.
            if (moved > 0.001f) _wakeDir = Vector2.Normalize(delta);
        }
        else
        {
            _wakeDir = new Vector2(MathF.Cos(bodyYawRadians), MathF.Sin(bodyYawRadians));
        }
        // Advance the propagation phase by DISTANCE, not time. That is what makes
        // the crests stay put in the water while the character moves through
        // them; a time-based scroll would slide the whole V along with you and
        // look exactly as stuck as the first version did.
        if (_haveLastSample && WakeLength > 0.01f)
        {
            var mv = new Vector2(playerPos.X - _lastWakeSample.X, playerPos.Y - _lastWakeSample.Y);
            _wakeScroll -= mv.Length() / WakeLength * WakeRepeat * WakeWorldLock;
            // Keep it in 0..1 so float precision does not drift after a long walk.
            _wakeScroll -= MathF.Floor(_wakeScroll);
        }

        _lastWakeSample = playerPos;
        _haveLastSample = true;

        float target = 0f;
        if (inWater && WakeFullSpeed > 0.01f)
            target = Math.Clamp(speed / WakeFullSpeed, 0f, 1f);

        // Attack fast, release slow: the churn appears the moment you move and
        // lingers a beat after you stop, which is what disturbed water does.
        float rate = target > _wakeAmount ? (dt / 0.08f) : (dt / MathF.Max(WakeFade, 0.01f));
        _wakeAmount += Math.Clamp(target - _wakeAmount, -rate, rate);
        _wakeAmount = Math.Clamp(_wakeAmount, 0f, 1f);
    }

    /// <summary>Drop the wake - on teleport, map change or a settings change.</summary>
    public void ClearWake()
    {
        _wakeAmount = 0f;
        _haveLastSample = false;
        _wakeScroll = 0f;
    }

    public float OceanAlphaShallow { get; set; } = 0.85f;
    public float OceanAlphaDeep    { get; set; } = 1.00f;
    public float RiverAlphaShallow { get; set; } = 0.85f;
    public float RiverAlphaDeep    { get; set; } = 1.00f;

    /// <summary>True when the shader is actually being told to use the data.</summary>
    public bool AuthoredColorsActive => UseAuthoredColors && HasAuthoredColors;

    public float WaveAmplitude{ get; set; } = 0f;      // 0 = flat; >0 re-enables Gerstner displacement
    public float WaveSpeed    { get; set; } = 1.0f;    // wave scroll-speed multiplier

    /// <summary>Reset every knob to the startup default look.</summary>
    public void ResetTuning()
    {
        TextureScale = 0.16f; AnimationFps = 12f; FrameBlend = 0f;
        TexBrightness = 1f; TexContrast = 1f; TexTint = Vector3.One;
        Opacity = 1.0f; ShoreFade = 0.85f; ShoreWidth = 1.2f;
        DepthDarken = 0.78f; DepthRate = 0.12f;
        Brightness = 0.90f; AmbientAmount = 0.6f; SunAmount = 0.30f; SkySheen = 0.14f;
        WaveAmplitude = 0f; WaveSpeed = 1.0f;
        RiverBody = new Vector3(0.13f, 0.16f, 0.17f);
        OceanBody = new Vector3(0.04f, 0.16f, 0.38f);
        HighlightGain = 4.0f;
        WakeEnabled = true; WakeStrength = 0.9f;
        WakeLength = 4.5f; WakeWidth = 2.6f; WakeAhead = 0.6f;
        WakeFullSpeed = 2.5f; WakeFade = 0.45f; WakeOpacity = 0.40f;
        WakeRepeat = 2.5f; WakeWorldLock = 1.0f;
        WakeColor = new Vector3(0.30f, 0.36f, 0.42f);
        ClearWake();
    }

    // Shared time-of-day environment, pushed each frame from WorldAtmosphere.
    public Vector3 SunDirection { get; set; } = Vector3.UnitZ;
    public Vector3 SunColor { get; set; } = Vector3.One;
    public float SunIntensity { get; set; } = 1f;
    public Vector3 AmbientColor { get; set; } = new(0.5f, 0.5f, 0.5f);
    public float AmbientIntensity { get; set; } = 0.6f;
    public Vector3 FogColor { get; set; } = new(0.56f, 0.71f, 0.85f);
    public float FogStart { get; set; } = 350f;
    public float FogEnd { get; set; } = 777f;

    /// <summary>Seconds, for the wave animation. GameLoop accumulates it.</summary>
    public float Time { get; set; }

    public int TileCount => _tiles.Count;

    /// <summary>Liquid tiles that survived the frustum test this frame.</summary>
    public int TilesDrawnLastFrame { get; private set; }

    /// <summary>Frustum-cull liquid tiles. Off draws the whole resident set, as before.</summary>
    public bool FrustumCulling { get; set; } = true;
    public int TrianglesLastFrame { get; private set; }

    public LiquidRenderer(GL gl) => _gl = gl;

    public void LoadShaders(string shaderDir)
    {
        _shader = Shader.FromFiles(_gl,
            Path.Combine(shaderDir, "water.vert"),
            Path.Combine(shaderDir, "water.frag"));
        _underwater = Shader.FromFiles(_gl,
            Path.Combine(shaderDir, "underwater.vert"),
            Path.Combine(shaderDir, "underwater.frag"));
        _overlayVao = _gl.GenVertexArray();
    }

    /// <summary>
    /// Load the real vanilla animated liquid textures from the client's MPQs.
    /// Vanilla stores each liquid as numbered BLP frames (e.g. river.1.blp,
    /// river.2.blp, ...); the exact folder/name varies, so we probe a small
    /// ordered candidate list per type, load every consecutive frame that
    /// resolves into one array texture, and LOG exactly which path won and how
    /// many frames it had. Nothing is assumed - the MPQ decides, and the console
    /// says what it found so a wrong path is obvious rather than silent.
    /// </summary>
    public void LoadLiquidTextures(string clientDataPath)
    {
        // Real vanilla paths per WoWMapViewer's liquid.cpp: the river/lake file is
        // "lake_a" inside the "river" folder (NOT "river"), ocean is "ocean_h".
        (_texWater, _framesWater) = LoadFrames(clientDataPath, "water", new[]
        {
            @"XTextures\river\lake_a.{0}.blp",   // vanilla river / lake / inland water
            @"XTextures\river\river.{0}.blp",
            @"XTextures\lake\lake.{0}.blp",
        });
        (_texOcean, _framesOcean) = LoadFrames(clientDataPath, "ocean", new[]
        {
            @"XTextures\ocean\ocean_h.{0}.blp",  // vanilla ocean
            @"XTextures\ocean\ocean.{0}.blp",
            @"XTextures\sea\sea.{0}.blp",
        });
        (_texSlime, _framesSlime) = LoadFrames(clientDataPath, "slime", new[]
        {
            @"XTextures\slime\slime.{0}.blp",
            @"XTextures\ooze\ooze.{0}.blp",
        });
        (_texMagma, _framesMagma) = LoadFrames(clientDataPath, "magma", new[]
        {
            @"XTextures\lava\lava.{0}.blp",
            @"XTextures\magma\magma.{0}.blp",
        });

        // 1x1 transparent array so every sampler unit has a valid binding even
        // when a type's textures were not found (the shader guards on the frame
        // count and never actually samples the dummy).
        _dummyTex = Texture.Array2D(_gl, new List<byte[]> { new byte[] { 0, 0, 0, 0 } },
            1, 1, mipmaps: false);

        // The walking wake mask (PLAN_16). XTextures\splash\ holds exactly two
        // files, wake.blp and splash.blp - Blizzard authored this effect and we
        // are wiring it, not inventing it.
        //
        // CLAMPED, not repeated: it is stamped once per trail sample, and a
        // wrapping mask would tile the disturbance across the whole river.
        LoadWakeTexture(clientDataPath);

        // A 1x1 2D dummy so the wake sampler always has something bound even
        // when wake.blp is missing. Different target from _dummyTex above -
        // sampler2D and sampler2DArray cannot share a binding.
        _dummy2D = Texture.From2D(_gl, new byte[] { 0, 0, 0, 0 }, 1, 1,
            mipmaps: false, repeat: false);

        if (_framesWater == 0)
            Console.WriteLine("[liquid-tex] water texture NOT found - the river falls back to the " +
                "procedural surface. Auto-discovering real paths from the MPQ (listfile) below.");

        // If anything failed to resolve, ask the MPQ what liquid textures it
        // actually contains so we can correct the candidate paths with certainty
        // instead of guessing. Harmless (and quiet) when every type loaded.
        if (_framesWater == 0 || _framesOcean == 0 || _framesSlime == 0 || _framesMagma == 0)
            DiscoverLiquidTexturePaths(clientDataPath);
    }

    /// <summary>
    /// Load XTextures\splash\wake.blp — the mask the walking wake is stamped
    /// with. Logs what it found, including the MAX ALPHA, because that number is
    /// the one that decides whether this works at all.
    ///
    /// Only the ALPHA channel is used: wake.blp is a near-black greyscale mask
    /// (measured mean RGB 0.024, mean alpha 0.451), exactly like the liquid
    /// frames. If the decoded max alpha comes back as 1 rather than 255 this is
    /// the 1-bit-alpha trap from handbook section 3.19 and the wake will be
    /// invisible — the log says so rather than leaving it a mystery.
    /// </summary>
    private void LoadWakeTexture(string clientDataPath)
    {
        const string path = @"XTextures\splash\wake.blp";
        var px = AdtTerrainReader.ReadBlpPixels(clientDataPath, path);
        if (px is null)
        {
            _hasWakeTex = false;
            Console.WriteLine($"[wake] '{path}' not found - falling back to a procedural ring. " +
                              "The trail will still show, but it is not Blizzard's shape.");
            return;
        }

        var (bgra, w, h) = (px.Value.bgra, px.Value.width, px.Value.height);
        byte maxAlpha = 0;
        for (int i = 3; i < bgra.Length; i += 4) if (bgra[i] > maxAlpha) maxAlpha = bgra[i];

        _texWakeMask = Texture.From2D(_gl, bgra, w, h, mipmaps: true, repeat: false);
        _hasWakeTex = maxAlpha > 1;

        Console.WriteLine($"[wake] {path} {w}x{h}, max alpha {maxAlpha}");
        if (maxAlpha == 0)
            Console.WriteLine("[wake] max alpha 0 - the mask is empty, using the procedural ring instead.");
        else if (maxAlpha == 1)
            Console.WriteLine("[wake] max alpha 1 - 1-bit alpha decoded as 0/1 (handbook 3.19). " +
                              "Using the procedural ring; the proper fix belongs in BlpDecoder.");
    }

    /// <summary>
    /// Read the MPQ (listfile), if present, and print the liquid-texture entries
    /// so the real vanilla paths are known for certain. Many original Blizzard
    /// MPQs omit the listfile; if so, we say so rather than pretend.
    /// </summary>
    private static void DiscoverLiquidTexturePaths(string clientDataPath)
    {
        var lf = AdtTerrainReader.ReadFileFromMpqs(clientDataPath, "(listfile)");
        if (lf is null)
        {
            Console.WriteLine("[liquid-tex] no (listfile) in the MPQs - cannot auto-discover; " +
                "tell me the failures above and I'll correct the paths.");
            return;
        }

        var text = System.Text.Encoding.ASCII.GetString(lf);
        var hits = text.Split('\n', '\r')
            .Select(l => l.Trim())
            .Where(l => l.EndsWith(".blp", StringComparison.OrdinalIgnoreCase)
                     && (Has(l, "water") || Has(l, "river") || Has(l, "ocean")
                      || Has(l, "lava") || Has(l, "slime") || Has(l, "lake") || Has(l, "ooze")))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(l => l, StringComparer.OrdinalIgnoreCase)
            .Take(60)
            .ToList();

        Console.WriteLine($"[liquid-tex] (listfile) has {hits.Count} liquid-texture path(s):");
        foreach (var h in hits) Console.WriteLine("    " + h);

        static bool Has(string s, string sub) => s.IndexOf(sub, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// Probe the candidate templates in order; the first that yields at least one
    /// frame wins. Frames are read as consecutive numbers starting at 1 until one
    /// is missing (or the dimensions change). Returns the array texture + count,
    /// or (null, 0) if nothing resolved.
    /// </summary>
    private (Texture?, int) LoadFrames(string clientDataPath, string label, string[] templates)
    {
        const int maxFrames = 64;
        foreach (var template in templates)
        {
            var frames = new List<byte[]>();
            int w = 0, h = 0;
            for (int n = 1; n <= maxFrames; n++)
            {
                var px = AdtTerrainReader.ReadBlpPixels(clientDataPath, string.Format(template, n));
                if (px is null) break;
                if (frames.Count == 0) { w = px.Value.width; h = px.Value.height; }
                else if (px.Value.width != w || px.Value.height != h) break;
                frames.Add(px.Value.bgra);
            }

            if (frames.Count > 0)
            {
                Console.WriteLine($"[liquid-tex] {label}: {frames.Count} frame(s) {w}x{h} " +
                    $"from '{template.Replace("{0}", "N")}'");
                return (Texture.Array2D(_gl, frames, w, h), frames.Count);
            }
        }

        Console.WriteLine($"[liquid-tex] {label}: none of {templates.Length} candidate path(s) " +
            $"resolved - tried {string.Join("  ", templates.Select(t => t.Replace("{0}", "N")))}");
        return (null, 0);
    }

    /// <summary>
    /// Drop every built liquid mesh. Needed on a map change for the same reason
    /// terrain needs it: LoadForTiles keeps any tile whose (col, row) key is
    /// still wanted, and those keys are shared across maps, so an overlapping
    /// range would leave Elwynn's river surface floating inside a dungeon.
    /// </summary>
    public void UnloadAll()
    {
        foreach (var mesh in _tiles.Values) mesh.Dispose();
        _tiles.Clear();
        // The WMO set goes too, and the version sentinel resets so the next
        // UpdateWmoLiquid call rebuilds even if LiquidVersion happens to
        // repeat the last value seen on the previous map.
        foreach (var mesh in _wmoMeshes) mesh.Dispose();
        _wmoMeshes.Clear();
        _wmoLiquidVersionSeen = int.MinValue;
        ClearWake();   // a trail must not survive a map change
    }

    /// <summary>Build/keep liquid meshes for exactly the resident tiles; dispose the rest.</summary>
    public void LoadForTiles(IEnumerable<(int col, int row)> tiles, AdtCache adts)
    {
        var want = new HashSet<(int col, int row)>(tiles);

        foreach (var key in _tiles.Keys.Where(k => !want.Contains(k)).ToArray())
        {
            _tiles[key].Dispose();
            _tiles.Remove(key);
        }

        int built = 0;
        foreach (var key in want)
        {
            if (_tiles.ContainsKey(key)) continue;
            var mesh = Build(adts.Get(key.col, key.row), key.col, key.row);
            if (mesh is not null) { _tiles[key] = mesh; built++; }
        }

        if (built > 0)
            Console.WriteLine($"[liquid] {built} tile(s) with water built, {_tiles.Count} resident");
    }

    private unsafe TileMesh? Build(AdtTerrainReader.AdtResult? adt, int col, int row)
    {
        if (adt?.Chunks is null) return null;

        double originX = (32 - row) * 533.33333;
        double originY = (32 - col) * 533.33333;
        const float cell = AdtTerrainReader.CELL_SIZE;

        var verts = new List<float>();
        var indices = new List<uint>();
        var surfaces = new List<SurfaceLayer>();

        foreach (var chunk in adt.Chunks)
        {
            if (chunk?.Liquid is null) continue;

            foreach (var layer in chunk.Liquid)
            {
                if (layer.VertexHeights.Length < 81 || layer.TileRender.Length < 64) continue;

                uint baseV = (uint)(verts.Count / FloatsPerVertex);
                float type = layer.LiquidType;

                for (int r = 0; r < 9; r++)
                for (int c = 0; c < 9; c++)
                {
                    double gridRow = chunk.IndexY * 8 + r;
                    double gridCol = chunk.IndexX * 8 + c;
                    float wx = (float)(originX - gridRow * cell);
                    float wy = (float)(originY - gridCol * cell);
                    float wz = layer.VertexHeights[r * 9 + c];

                    // Water depth here: surface minus the terrain height at the
                    // SAME grid vertex. The liquid grid and the chunk's MCVT outer
                    // grid are index-aligned, so this is a direct lookup, not a
                    // spatial query. Clamped to >= 0 (surface never below the bed).
                    float groundZ = chunk.BaseZ + chunk.OuterHeight(c, r);
                    float depth = MathF.Max(0f, wz - groundZ);

                    verts.Add(wx);
                    verts.Add(wy);
                    verts.Add(wz);
                    verts.Add(type);
                    verts.Add(depth);
                }

                bool any = false;
                for (int r = 0; r < 8; r++)
                for (int c = 0; c < 8; c++)
                {
                    if (!layer.TileRender[r * 8 + c]) continue;
                    any = true;
                    uint tl = baseV + (uint)(r * 9 + c);
                    uint tr = baseV + (uint)(r * 9 + c + 1);
                    uint bl = baseV + (uint)((r + 1) * 9 + c);
                    uint br = baseV + (uint)((r + 1) * 9 + c + 1);
                    indices.Add(tl); indices.Add(bl); indices.Add(tr);
                    indices.Add(tr); indices.Add(bl); indices.Add(br);
                }

                if (any)
                {
                    surfaces.Add(new SurfaceLayer
                    {
                        OriginX = originX,
                        OriginY = originY,
                        Cell = cell,
                        GridRowBase = chunk.IndexY * 8,
                        GridColBase = chunk.IndexX * 8,
                        Heights = (float[])layer.VertexHeights.Clone(),
                        Render = (bool[])layer.TileRender.Clone(),
                        Type = layer.LiquidType,
                    });
                }
            }
        }

        if (indices.Count == 0) return null;

        var va = verts.ToArray();
        var ia = indices.ToArray();

        var mesh = new TileMesh { IndexCount = ia.Length, Surfaces = surfaces };

        // Bounds from the packed vertex array rather than a second pass over the
        // source data: whatever ended up in the buffer is what gets drawn.
        for (int v = 0; v < va.Length; v += FloatsPerVertex)
        {
            var p = new Vector3(va[v], va[v + 1], va[v + 2]);
            mesh.BoundsMin = Vector3.Min(mesh.BoundsMin, p);
            mesh.BoundsMax = Vector3.Max(mesh.BoundsMax, p);
        }

        mesh.Attach(_gl);
        mesh.Vao = _gl.GenVertexArray();
        _gl.BindVertexArray(mesh.Vao);

        mesh.Vbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, mesh.Vbo);
        fixed (float* p = va)
            _gl.BufferData(BufferTargetARB.ArrayBuffer,
                (nuint)(va.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);

        mesh.Ebo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, mesh.Ebo);
        fixed (uint* p = ia)
            _gl.BufferData(BufferTargetARB.ElementArrayBuffer,
                (nuint)(ia.Length * sizeof(uint)), p, BufferUsageARB.StaticDraw);

        const uint stride = FloatsPerVertex * sizeof(float);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 1, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        _gl.EnableVertexAttribArray(2);
        _gl.VertexAttribPointer(2, 1, VertexAttribPointerType.Float, false, stride, (void*)(4 * sizeof(float)));
        _gl.BindVertexArray(0);

        return mesh;
    }

    /// <summary>
    /// Rebuild the WMO liquid mesh set if <paramref name="version"/> has moved
    /// since the last call. Call every frame with WmoRenderer.LiquidVersion —
    /// the int compare is the whole cost when nothing changed. Version-driven,
    /// NOT tile-crossing-driven: groups are adopted several frames after their
    /// instance is placed (SYSTEM_WATER.md §7.6), so an event fired on the
    /// crossing would enumerate before the liquid exists and never retry.
    /// </summary>
    public void UpdateWmoLiquid(int version, IEnumerable<WmoLiquidSurface> surfaces)
    {
        if (version == _wmoLiquidVersionSeen) return;
        _wmoLiquidVersionSeen = version;

        foreach (var mesh in _wmoMeshes) mesh.Dispose();
        _wmoMeshes.Clear();

        int hiddenOnly = 0;
        foreach (var surface in surfaces)
        {
            var mesh = BuildWmoSurface(surface);
            if (mesh is not null) _wmoMeshes.Add(mesh);
            else hiddenOnly++;
        }

        if (_wmoMeshes.Count > 0 || hiddenOnly > 0)
            Console.WriteLine($"[liquid] WMO liquid v{version}: {_wmoMeshes.Count} surface(s) " +
                $"meshed, {hiddenOnly} fully hidden");
    }

    /// <summary>
    /// One MLIQ surface -> one mesh in the existing 5-float vertex format, so
    /// the ADT water shader, uniforms and tuning HUD apply unchanged.
    ///
    /// Two details carry the whole feature:
    ///
    ///   TYPE comes from WmoLiquidSurface.ShaderType — the MLIQ→MCLQ code
    ///   translation. The raw MLIQ magma code is 2, which water.frag routes as
    ///   ocean: skip the translation and Blackrock's lava lake renders blue.
    ///
    ///   INDICES are emitted only for tiles where !IsHidden. Roughly 40% of all
    ///   MLIQ tiles are hidden — the grid is a bounding rectangle with the pool
    ///   cut out of it, and ignoring the mask draws a lava slab across the
    ///   whole mountain. The VERTEX grid stays complete so indices keep lining
    ///   up; only the index list is cut.
    ///
    /// DEPTH is a per-surface constant 3.0: deep enough that the shoreline fade
    /// never bites. MLIQ has no per-vertex floor to measure against — CornerZ
    /// is NOT the floor, it equals the minimum vertex height (rejected in
    /// SYSTEM_WATER.md §7.4).
    /// </summary>
    private unsafe TileMesh? BuildWmoSurface(WmoLiquidSurface surface)
    {
        int xv = surface.XVerts, yv = surface.YVerts;
        int xt = surface.XTiles, yt = surface.YTiles;
        if (surface.Vertices.Length < xv * yv) return null;

        var indices = new List<uint>();
        for (int j = 0; j < yt; j++)
        for (int i = 0; i < xt; i++)
        {
            if (surface.IsHidden(i, j)) continue;
            uint tl = (uint)(j * xv + i);
            uint tr = tl + 1;
            uint bl = (uint)((j + 1) * xv + i);
            uint br = bl + 1;
            indices.Add(tl); indices.Add(bl); indices.Add(tr);
            indices.Add(tr); indices.Add(bl); indices.Add(br);
        }
        if (indices.Count == 0) return null;   // every tile hidden - nothing to draw

        // Fallback type for vertices whose adjacent tiles are all hidden: the
        // first visible tile's type. A surface is almost always one substance.
        byte defaultType = 4;
        bool foundDefault = false;
        for (int j = 0; j < yt && !foundDefault; j++)
        for (int i = 0; i < xt; i++)
            if (!surface.IsHidden(i, j))
            {
                defaultType = surface.ShaderType(i, j);
                foundDefault = true;
                break;
            }

        const float wmoLiquidDepth = 3.0f;

        var verts = new float[xv * yv * WmoFloatsPerVertex];
        int v = 0;
        for (int j = 0; j < yv; j++)
        for (int i = 0; i < xv; i++)
        {
            var p = surface.Vertices[j * xv + i];

            // Per-vertex type from the first VISIBLE tile touching this vertex.
            // Hidden tiles carry flag value 0x0F, whose low bits would decode as
            // slime; asking them for a type would tint pool edges green.
            byte type = defaultType;
            for (int tj = Math.Max(j - 1, 0); tj <= Math.Min(j, yt - 1); tj++)
            {
                for (int ti = Math.Max(i - 1, 0); ti <= Math.Min(i, xt - 1); ti++)
                {
                    if (surface.IsHidden(ti, tj)) continue;
                    type = surface.ShaderType(ti, tj);
                    tj = yt; break;   // found - leave both loops
                }
            }

            // The authored MLIQ s/t, in repeats. Written for every vertex;
            // water.frag only reads it for magma with uWmoAuthoredUv raised,
            // so the flow-byte garbage a water surface would decode to here is
            // never sampled.
            var uv = surface.AuthoredUv(i, j);

            verts[v++] = p.X;
            verts[v++] = p.Y;
            verts[v++] = p.Z;
            verts[v++] = type;
            verts[v++] = wmoLiquidDepth;
            verts[v++] = uv.X;
            verts[v++] = uv.Y;
        }

        if (WmoLiquidTrace) TraceWmoSurface(surface);

        var ia = indices.ToArray();
        var mesh = new TileMesh { IndexCount = ia.Length };   // Surfaces stays
        // empty ON PURPOSE: TryGetSurface iterates _tiles only, and WMO liquid
        // is deliberately excluded from submersion (SYSTEM_WATER.md §7).

        for (int f = 0; f < verts.Length; f += WmoFloatsPerVertex)
        {
            var p = new Vector3(verts[f], verts[f + 1], verts[f + 2]);
            mesh.BoundsMin = Vector3.Min(mesh.BoundsMin, p);
            mesh.BoundsMax = Vector3.Max(mesh.BoundsMax, p);
        }

        mesh.Attach(_gl);
        mesh.Vao = _gl.GenVertexArray();
        _gl.BindVertexArray(mesh.Vao);

        mesh.Vbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, mesh.Vbo);
        fixed (float* p = verts)
            _gl.BufferData(BufferTargetARB.ArrayBuffer,
                (nuint)(verts.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);

        mesh.Ebo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, mesh.Ebo);
        fixed (uint* p = ia)
            _gl.BufferData(BufferTargetARB.ElementArrayBuffer,
                (nuint)(ia.Length * sizeof(uint)), p, BufferUsageARB.StaticDraw);

        const uint stride = WmoFloatsPerVertex * sizeof(float);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 1, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        _gl.EnableVertexAttribArray(2);
        _gl.VertexAttribPointer(2, 1, VertexAttribPointerType.Float, false, stride, (void*)(4 * sizeof(float)));
        // Attribute 3: the authored MLIQ UV. ADT tile VAOs never enable this
        // attribute, so their shader invocations read the GL default (0,0) and
        // the uniform gate keeps them on planar mapping regardless.
        _gl.EnableVertexAttribArray(3);
        _gl.VertexAttribPointer(3, 2, VertexAttribPointerType.Float, false, stride, (void*)(5 * sizeof(float)));
        _gl.BindVertexArray(0);

        return mesh;
    }

    /// <summary>
    /// MSUI_WMO_LIQUID_TRACE=1 instrument: one line per meshed WMO surface with
    /// the numbers that settle a "wrong height / wrong mapping" report — the
    /// world Z range of vertices the visible tiles actually use, the substance
    /// histogram, and the authored UV range in repeats.
    /// </summary>
    private static void TraceWmoSurface(WmoLiquidSurface surface)
    {
        int xv = surface.XVerts, yv = surface.YVerts;
        int xt = surface.XTiles, yt = surface.YTiles;
        float zMin = float.MaxValue, zMax = float.MinValue;
        float uMin = float.MaxValue, uMax = float.MinValue;
        float vMin = float.MaxValue, vMax = float.MinValue;
        int visible = 0;
        var typeCounts = new Dictionary<byte, int>();
        for (int j = 0; j < yt; j++)
        for (int i = 0; i < xt; i++)
        {
            if (surface.IsHidden(i, j)) continue;
            visible++;
            byte t = surface.ShaderType(i, j);
            typeCounts[t] = typeCounts.GetValueOrDefault(t) + 1;
            for (int c = 0; c < 4; c++)
            {
                int ci = i + (c & 1), cj = j + (c >> 1);
                var p = surface.Vertices[cj * xv + ci];
                zMin = MathF.Min(zMin, p.Z); zMax = MathF.Max(zMax, p.Z);
                var uv = surface.AuthoredUv(ci, cj);
                uMin = MathF.Min(uMin, uv.X); uMax = MathF.Max(uMax, uv.X);
                vMin = MathF.Min(vMin, uv.Y); vMax = MathF.Max(vMax, uv.Y);
            }
        }
        string types = string.Join(",", typeCounts.Select(kv => $"{kv.Key}:{kv.Value}"));
        Console.WriteLine(
            $"[wmo-liquid-trace] {Path.GetFileName(surface.ModelPath)}[{surface.GroupIndex}] " +
            $"'{surface.GroupName}' grid {xv}x{yv} visible {visible}/{xt * yt} types {types} " +
            $"worldZ {zMin:F2}..{zMax:F2} uv U {uMin:F2}..{uMax:F2} V {vMin:F2}..{vMax:F2}");
    }

    public unsafe void Render(Camera camera)
    {
        TrianglesLastFrame = 0;
        WmoSurfacesDrawnLastFrame = 0;
        // On global-WMO maps (Blackrock Depths/Spire, ...) there is no terrain,
        // so _tiles is empty FOREVER — the WMO set must be able to draw alone.
        bool anyWmo = WmoLiquidEnabled && _wmoMeshes.Count > 0;
        if (!Enabled || _shader is null || (_tiles.Count == 0 && !anyWmo)) return;

        _shader.Use();
        _shader.Set("uViewProjection", camera.RelativeViewProjection);
        _shader.Set("uCameraOrigin", camera.Position);
        _shader.Set("uSunDirection", SunDirection);
        _shader.Set("uSunColor", SunColor);
        _shader.Set("uSunIntensity", SunIntensity);
        _shader.Set("uAmbientColor", AmbientColor);
        _shader.Set("uAmbientIntensity", AmbientIntensity);
        _shader.Set("uFogStart", FogStart);
        _shader.Set("uFogEnd", FogEnd);
        _shader.Set("uFogColor", FogColor);
        _shader.Set("uTime", Time);

        // Animated liquid textures: bind one array per basic type (dummy where a
        // type had none) and hand the shader the frame counts + animation rate so
        // it can cross-fade frames. World-space UV scale controls the tiling.
        _shader.Set("uTexScale", TextureScale);
        _shader.Set("uWaterFps", MathF.Max(AnimationFps, 0.01f));
        _shader.Set("uFrameBlend", FrameBlend);
        _shader.Set("uTexBright", TexBrightness);
        _shader.Set("uTexContrast", TexContrast);
        _shader.Set("uTexTint", TexTint);
        _shader.Set("uOpacity", Opacity);
        _shader.Set("uShoreFade", ShoreFade);
        _shader.Set("uShoreWidth", MathF.Max(ShoreWidth, 0.01f));
        _shader.Set("uDepthDarken", DepthDarken);
        _shader.Set("uDepthRate", DepthRate);
        _shader.Set("uBrightness", Brightness);
        _shader.Set("uAmbientAmt", AmbientAmount);
        _shader.Set("uSunAmt", SunAmount);
        _shader.Set("uSkySheen", SkySheen);
        // PLAN_12. One float decides it: 0 leaves every authored term mixed out
        // and the shader reduces to the constants it shipped with.
        // uAuthoredWater now only selects the ALPHA source. The COLOUR is always
        // the body uniforms below; this class decides what goes in them, so the
        // shader never has to branch on it.
        bool authored = AuthoredColorsActive;
        _shader.Set("uAuthoredWater", authored ? 1f : 0f);
        _shader.Set("uOceanClose", authored ? OceanClose : Shallow(OceanBody));
        _shader.Set("uOceanFar",   authored ? OceanFar   : Deep(OceanBody));
        _shader.Set("uRiverClose", authored ? RiverClose : Shallow(RiverBody));
        _shader.Set("uRiverFar",   authored ? RiverFar   : Deep(RiverBody));
        _shader.Set("uHighlightGain", MathF.Max(HighlightGain, 0f));

        // The walking wake (PLAN_16). One float is the kill switch: at 0 the
        // shader's wakeAt() returns immediately and the image is bit-identical
        // to the pre-PLAN_16 water.
        float wakeAmt = WakeEnabled ? _wakeAmount * WakeStrength : 0f;
        _shader.Set("uWakePos", _wakePos);
        _shader.Set("uWakeDir", _wakeDir);
        _shader.Set("uWakeAmount", Math.Clamp(wakeAmt, 0f, 1f));
        _shader.Set("uWakeLength", MathF.Max(WakeLength, 0.1f));
        _shader.Set("uWakeWidth", MathF.Max(WakeWidth, 0.1f));
        _shader.Set("uWakeAhead", WakeAhead);
        _shader.Set("uWakeRepeat", MathF.Max(WakeRepeat, 0.1f));
        _shader.Set("uWakeScroll", _wakeScroll);
        _shader.Set("uWakeColor", WakeColor);
        _shader.Set("uWakeOpacity", WakeOpacity);
        _shader.Set("uHasWakeTex", _hasWakeTex ? 1 : 0);

        _shader.Set("uOceanAlphaShallow", OceanAlphaShallow);
        _shader.Set("uOceanAlphaDeep", OceanAlphaDeep);
        _shader.Set("uRiverAlphaShallow", RiverAlphaShallow);
        _shader.Set("uRiverAlphaDeep", RiverAlphaDeep);

        _shader.Set("uWaveAmp", WaveAmplitude);
        _shader.Set("uWaveSpeed", WaveSpeed);
        BindLiquidTexture("uTexWater", _texWater, 0, "uFramesWater", _framesWater);
        BindLiquidTexture("uTexOcean", _texOcean, 1, "uFramesOcean", _framesOcean);
        BindLiquidTexture("uTexSlime", _texSlime, 2, "uFramesSlime", _framesSlime);
        BindLiquidTexture("uTexMagma", _texMagma, 3, "uFramesMagma", _framesMagma);
        // Unit 4: the wake mask. Always bound - a sampler2D left dangling is
        // undefined behaviour even when the shader never reads it.
        (_texWakeMask ?? _dummy2D)?.Bind(4);
        _shader.Set("uTexWake", 4);
        _gl.ActiveTexture(TextureUnit.Texture0);

        // Transparent surface: blend, TEST depth so hills and the near side of the
        // character occlude it, but do NOT write depth so overlapping water and the
        // far side of a submerged character still blend. Both faces, so it reads
        // right from below when the camera dips under.
        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthMask(false);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _gl.Disable(EnableCap.CullFace);

        var viewProjection = camera.RelativeViewProjection;
        var cameraPosition = camera.Position;
        TilesDrawnLastFrame = 0;

        // ADT pass: planar magma mapping, exactly as before. The uniform is the
        // gate that keeps this path bit-identical — attribute 3 is not even
        // enabled on these VAOs.
        _shader.Set("uWmoAuthoredUv", 0f);

        foreach (var mesh in _tiles.Values)
        {
            // The one visibility test this pass never had. Water surfaces are
            // near-planar, so their boxes are thin and the frustum rejects an
            // off-screen lake outright.
            if (FrustumCulling &&
                !Camera.BoxInFrustum(viewProjection,
                    mesh.BoundsMin - cameraPosition,
                    mesh.BoundsMax - cameraPosition)) continue;

            _gl.BindVertexArray(mesh.Vao);
            _gl.DrawElements(PrimitiveType.Triangles, (uint)mesh.IndexCount,
                DrawElementsType.UnsignedInt, (void*)0);
            TrianglesLastFrame += mesh.IndexCount / 3;
            TilesDrawnLastFrame++;
        }

        // The WMO (MLIQ) set: same shader, same uniforms, same GL state — the
        // meshes bake the correct MCLQ-coded type per vertex, so the existing
        // magma/slime/ocean routing in water.frag just works.
        if (anyWmo)
        {
            // WMO magma switches to the authored MLIQ per-vertex UVs carried in
            // attribute 3; WMO water/ocean/slime stay planar (the shader gates
            // on the vertex type as well as this uniform).
            _shader.Set("uWmoAuthoredUv", 1f);

            foreach (var mesh in _wmoMeshes)
            {
                if (FrustumCulling &&
                    !Camera.BoxInFrustum(viewProjection,
                        mesh.BoundsMin - cameraPosition,
                        mesh.BoundsMax - cameraPosition)) continue;

                _gl.BindVertexArray(mesh.Vao);
                _gl.DrawElements(PrimitiveType.Triangles, (uint)mesh.IndexCount,
                    DrawElementsType.UnsignedInt, (void*)0);
                TrianglesLastFrame += mesh.IndexCount / 3;
                WmoSurfacesDrawnLastFrame++;
            }
        }

        _gl.Enable(EnableCap.CullFace);
        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);
        _gl.BindVertexArray(0);
    }

    /// <summary>Bind one liquid array texture to a unit (the 1x1 dummy if the type
    /// had no textures) and set its sampler + frame-count uniforms.</summary>
    private void BindLiquidTexture(string sampler, Texture? tex, int unit, string framesName, int frames)
    {
        (tex ?? _dummyTex)?.Bind((uint)unit);
        _shader!.Set(sampler, unit);
        _shader.Set(framesName, frames);
    }

    /// <summary>
    /// Absolute surface height of the water directly over/under a world XY, if any
    /// resident water layer covers that point. Used to decide submersion.
    /// </summary>
    public bool TryGetSurface(float worldX, float worldY, out float height, out byte type)
    {
        foreach (var mesh in _tiles.Values)
        {
            foreach (var s in mesh.Surfaces)
            {
                double gr = (s.OriginX - worldX) / s.Cell - s.GridRowBase;
                double gc = (s.OriginY - worldY) / s.Cell - s.GridColBase;
                if (gr < 0 || gr > 8 || gc < 0 || gc > 8) continue;

                int cr = Math.Clamp((int)Math.Floor(gr), 0, 7);
                int cc = Math.Clamp((int)Math.Floor(gc), 0, 7);
                if (!s.Render[cr * 8 + cc]) continue;

                float fr = (float)(gr - cr);
                float fc = (float)(gc - cc);
                float h00 = s.Heights[cr * 9 + cc];
                float h01 = s.Heights[cr * 9 + cc + 1];
                float h10 = s.Heights[(cr + 1) * 9 + cc];
                float h11 = s.Heights[(cr + 1) * 9 + cc + 1];
                float top = h00 + (h01 - h00) * fc;
                float bot = h10 + (h11 - h10) * fc;
                height = top + (bot - top) * fr;
                type = s.Type;
                return true;
            }
        }

        height = 0f;
        type = 0;
        return false;
    }

    /// <summary>
    /// Draw the full-screen underwater tint. Call only when the camera eye is
    /// below a water surface. <paramref name="submersion"/> is how far below, in
    /// yards; deeper means a denser tint.
    /// </summary>
    public void RenderUnderwater(float submersion, byte type)
    {
        if (_underwater is null) return;

        _underwater.Use();
        _underwater.Set("uTint", UnderwaterTint(type));
        _underwater.Set("uSubmersion", MathF.Max(0f, submersion));
        _underwater.Set("uTime", Time);

        // Sits on top of everything: no depth test, no depth write, just blend.
        _gl.Disable(EnableCap.DepthTest);
        _gl.DepthMask(false);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _gl.Disable(EnableCap.CullFace);

        _gl.BindVertexArray(_overlayVao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        _gl.BindVertexArray(0);

        // Leave clean state for the debug/HUD passes that follow.
        _gl.Disable(EnableCap.Blend);
        _gl.Enable(EnableCap.CullFace);
        _gl.DepthMask(true);
        _gl.Enable(EnableCap.DepthTest);
    }

    /// <summary>Screen tint colour for being under a given liquid type.</summary>
    private static Vector3 UnderwaterTint(byte type) => type switch
    {
        3 => new Vector3(0.10f, 0.24f, 0.06f),   // slime - murky green
        6 => new Vector3(0.35f, 0.10f, 0.02f),   // magma - hot orange
        1 => new Vector3(0.05f, 0.16f, 0.26f),   // ocean - deep blue
        _ => new Vector3(0.09f, 0.20f, 0.28f),   // river/lake - blue-green
    };

    public void Dispose()
    {
        foreach (var m in _tiles.Values) m.Dispose();
        _tiles.Clear();
        foreach (var m in _wmoMeshes) m.Dispose();
        _wmoMeshes.Clear();
        _texWater?.Dispose(); _texOcean?.Dispose(); _texSlime?.Dispose(); _texMagma?.Dispose();
        _dummyTex?.Dispose();
        _texWakeMask?.Dispose(); _dummy2D?.Dispose();
        if (_overlayVao != 0) { _gl.DeleteVertexArray(_overlayVao); _overlayVao = 0; }
    }
}

using System.Numerics;
using MSUIClient.Engine;

namespace MSUIClient.World;

/// <summary>
/// Live world-lighting and aerial-perspective controls. This is deliberately
/// data-only: renderers copy the evaluated values before drawing, so terrain,
/// buildings, doodads and units cannot drift into different times of day.
///
/// LIGHTING TUNE (2026-07-23). The earlier noon values lit flat ground at a
/// combined multiplier around (1.42, 1.43, 1.41) - above white, so cobblestone
/// and grass washed out - and the daytime ambient was BLUE-biased
/// (0.42, 0.50, 0.60), which fought the sun's warmth and left the world reading
/// cool. Vanilla 1.12 Elwynn/Stormwind daylight is warm and golden, and the
/// ground keeps its colour rather than blowing to white.
///
/// Two levers do the work, and both are here rather than in the shader so the
/// HUD SunStrength/AmbientStrength sliders still scale them:
///   1. AMBIENT HUE. DayAmbient is now warm (R >= G > B). This is the single
///      biggest reason the world now reads yellow instead of blue, because
///      ambient colours every shadowed and downward face.
///   2. BRIGHTNESS. Sun and ambient intensities are lower, so sunlit flat
///      ground now lands near (1.15, 1.04, 0.84) - still bright and warm, no
///      longer clipped to white.
/// A golden noon sun colour (1.00, 0.90, 0.72) finishes the warm cast.
/// </summary>
public sealed class WorldAtmosphere
{
    private static readonly Vector3 DayFog = new(0.56f, 0.71f, 0.85f);
    private static readonly Vector3 NightFog = new(0.035f, 0.055f, 0.11f);

    // Warm daytime ambient (R >= G > B). Was blue-biased (0.42, 0.50, 0.60),
    // which is what made the world look cool and the shadows grey-blue.
    private static readonly Vector3 DayAmbient = new(0.50f, 0.46f, 0.38f);
    private static readonly Vector3 NightAmbient = new(0.08f, 0.10f, 0.18f);

    // Warm, near-golden midday sun. Was (1.00, 0.95, 0.85).
    private static readonly Vector3 NoonSun = new(1.00f, 0.90f, 0.72f);
    // Low, orange horizon sun for sunrise/sunset.
    private static readonly Vector3 HorizonSun = new(1.00f, 0.48f, 0.24f);

    public bool DynamicLighting { get; set; } = true;
    public bool FogEnabled { get; set; } = true;
    public bool CullAtFogEnd { get; set; } = true;
    public float TimeOfDayHours { get; set; } = 12f;
    public float FogStart { get; set; } = 350f;
    public float FogEnd { get; set; } = 777f;
    public float SunStrength { get; set; } = 1f;
    public float AmbientStrength { get; set; } = 1f;

    // ── Authored data (PLAN_09) ─────────────────────────────────────────────
    //
    // This class used to BE the source of truth: every colour below started as a
    // constant somebody tuned by eye, most recently on 2026-07-23. Vanilla ships
    // the real answers in Light.dbc, and ExteriorLighting now resolves them for
    // the player's position and time. So this becomes the EVALUATOR, and the
    // constants survive as the fallback for when the data is missing.
    //
    // The 2026-07-23 note above is worth keeping honest about: it rejected a
    // blue-biased ambient of (0.42, 0.50, 0.60) as "what made the world look
    // cool". The authored value at noon is (0.408, 0.510, 0.604). The tune was
    // fighting the data almost exactly.

    /// <summary>
    /// Use resolved DBC values when available. The A/B for all of PLAN_09.
    /// SINCE 2026-08-12 THIS IS A DEV INSTRUMENT, NOT A SETTING: both lighting
    /// modes consume the authored data, so this is no longer persisted and no
    /// player surface writes it. The light probe keeps it as the transient
    /// data-vs-constants A/B; off routes everything to the hand-tuned fallback
    /// constants above.
    /// </summary>
    public bool UseAuthoredData { get; set; } = true;

    /// <summary>
    /// How the authored values are interpreted (SYSTEM_EXTERIOR_LIGHTING.md
    /// "Lighting modes"). Msui applies them exactly as before v6; Parity112
    /// additionally scales the diffuse by the vanilla client's own day/night
    /// intensity curve (World\dnc.db, see ParityDaylightIntensity).
    /// </summary>
    public LightingMode Mode { get; set; } = LightingMode.Parity112;

    /// <summary>
    /// hours -> intensity, wired by GameLoop from <see cref="DayNightCycle"/>
    /// (max of the dnc.db day/night curves) when dnc.db loaded. Only Parity112
    /// consumes it; null (no data) means no curve, which is Msui's behaviour.
    ///
    /// OWNER-SETTLED 2026-08-15: this intensity-only law - band 0 applied RAW
    /// (its authored cyan-blue night value IS the moonlight colour) with the
    /// never-setting sun - is the version Nico judged SPOT ON against the real
    /// client. The dnc COLOUR-ramp theory that briefly replaced it (diffuse and
    /// then ambient multiplied by the Day/Night/Ambient RGB columns) made
    /// moonlight pure-blue and night near-black, and is retired. The colour
    /// ramps almost certainly drive only the fixed-function TWO-light setup's
    /// second light in the real exe; with our single light, raw band colours x
    /// this curve is the match the owner's eyes confirmed.
    /// </summary>
    public Func<float, float>? ParityDaylightIntensity { get; set; }

    /// <summary>True once a resolved sample has been handed over.</summary>
    public bool HasAuthored { get; private set; }

    private Vector3 _authoredAmbient, _authoredDiffuse, _authoredFog;
    private Vector3 _authoredSkyTop, _authoredSkyMiddle, _authoredSkyBand1;
    private Vector3 _authoredSkyBand2, _authoredSkySmog;
    private float _authoredFogStart, _authoredFogEnd;

    /// <summary>
    /// Hand over what the data says here and now. Called once per frame from the
    /// probe; does not itself decide anything, so turning UseAuthoredData off
    /// restores the constants without a reload.
    /// </summary>
    public void SetAuthored(
        Vector3 ambient, Vector3 diffuse, Vector3 fog,
        Vector3 skyTop, Vector3 skyMiddle, Vector3 skyBand1,
        Vector3 skyBand2, Vector3 skySmog,
        float fogStart, float fogEnd)
    {
        _authoredAmbient = ambient;
        _authoredDiffuse = diffuse;
        _authoredFog = fog;
        _authoredSkyTop = skyTop;
        _authoredSkyMiddle = skyMiddle;
        _authoredSkyBand1 = skyBand1;
        _authoredSkyBand2 = skyBand2;
        _authoredSkySmog = skySmog;

        // Guard rather than trust: a zero fog end would collapse the world to a
        // point, and VisibilityDistance feeds doodad draw distance and the
        // residency radius. An unauthored band must not be able to empty the
        // world (PLAN_09 D7).
        if (fogEnd > 10f)
        {
            _authoredFogEnd = fogEnd;
            _authoredFogStart = Math.Clamp(fogStart, 0f, fogEnd - 1f);
        }

        HasAuthored = true;
    }

    private bool Authored => UseAuthoredData && HasAuthored;
    public bool AuthoredLightingReady => Authored;

    // ── Authored water colours (PLAN_12) ────────────────────────────────────
    //
    // LightIntBand 13-16 are ocean close/far and river close/far; LightParams
    // carries the matching shallow/deep alphas. Both were resolved, blended and
    // printed by the light probe from 2026-07-25 and consumed by nothing, while
    // water.frag held six invented numbers for exactly the same quantities.
    //
    // They ride SetAuthored's gate deliberately (PLAN_12 H1): one switch decides
    // whether the client believes the data, and water must not be able to
    // disagree with the sky about it.

    private Vector3 _authoredOceanClose, _authoredOceanFar;
    private Vector3 _authoredRiverClose, _authoredRiverFar;
    // Seeded with water.frag's own textured-path values (uShoreFade 0.85,
    // uOpacity 1.0) so that a rejected alpha pair renders like today rather
    // than like alpha zero.
    private float _authoredOceanAlphaShallow = 0.85f, _authoredOceanAlphaDeep = 1f;
    private float _authoredRiverAlphaShallow = 0.85f, _authoredRiverAlphaDeep = 1f;
    private bool _hasAuthoredWater;

    /// <summary>True once resolved water colours have been handed over.</summary>
    public bool HasAuthoredWater => _hasAuthoredWater;

    /// <summary>
    /// The single question LiquidRenderer asks: should it use the data? False
    /// leaves water.frag on the constants it shipped with, byte for byte.
    /// </summary>
    public bool AuthoredWaterReady => Authored && _hasAuthoredWater;

    public Vector3 OceanCloseColor => _authoredOceanClose;
    public Vector3 OceanFarColor => _authoredOceanFar;
    public Vector3 RiverCloseColor => _authoredRiverClose;
    public Vector3 RiverFarColor => _authoredRiverFar;
    public float OceanShallowAlpha => _authoredOceanAlphaShallow;
    public float OceanDeepAlpha => _authoredOceanAlphaDeep;
    public float RiverShallowAlpha => _authoredRiverAlphaShallow;
    public float RiverDeepAlpha => _authoredRiverAlphaDeep;

    /// <summary>
    /// Hand over the resolved water colours. Colours are taken as given - an
    /// unauthored band resolves to black and black IS a legal water colour, so
    /// there is nothing honest to guard against.
    ///
    /// The ALPHAS are guarded, because they are not: a deep alpha of zero would
    /// make every lake in the world invisible, and an unauthored LightParams row
    /// reads as zero rather than as absent. A deep alpha at or below 0.01 is
    /// therefore treated as "not authored" and the whole alpha pair is rejected,
    /// leaving the shader's own constants. Same shape as SetAuthored's fog-end
    /// guard and for the same reason (PLAN_09 D7): data may change the look, it
    /// may not delete the world.
    /// </summary>
    public void SetAuthoredWater(
        Vector3 oceanClose, Vector3 oceanFar, Vector3 riverClose, Vector3 riverFar,
        float oceanShallowAlpha, float oceanDeepAlpha,
        float riverShallowAlpha, float riverDeepAlpha)
    {
        _authoredOceanClose = oceanClose;
        _authoredOceanFar = oceanFar;
        _authoredRiverClose = riverClose;
        _authoredRiverFar = riverFar;

        if (oceanDeepAlpha > 0.01f)
        {
            _authoredOceanAlphaDeep = Math.Clamp(oceanDeepAlpha, 0.05f, 1f);
            _authoredOceanAlphaShallow = Math.Clamp(oceanShallowAlpha, 0.02f, 1f);
        }

        if (riverDeepAlpha > 0.01f)
        {
            _authoredRiverAlphaDeep = Math.Clamp(riverDeepAlpha, 0.05f, 1f);
            _authoredRiverAlphaShallow = Math.Clamp(riverShallowAlpha, 0.02f, 1f);
        }

        _hasAuthoredWater = true;
    }

    // ── Authored cloud palette (PLAN_18) ────────────────────────────────────
    //
    // The three IntBand cloud rows the CloudField kernel colours the coverage
    // tile with (sun-glow 10, slope 11, base 12) plus the FloatBand density C
    // (band 3). Like the water colours they ride SetAuthored's gate: one switch
    // decides whether the client believes the data, and the clouds must not
    // disagree with the sky about the time of day.
    //
    // Density is the only value that can empty or fill the sky, and BOTH ends are
    // legal - C=0 is a clear sky, C=1 an overcast one - so it is taken as given
    // (clamped to [0,1]); an unauthored band reads 0, which is a clear sky, the
    // right default for a zone with no cloud data.
    private Vector3 _authoredCloudSunGlow, _authoredCloudSlope, _authoredCloudBase;
    private float _authoredCloudDensity;
    private bool _hasAuthoredClouds;

    /// <summary>True once resolved cloud bands have been handed over.</summary>
    public bool HasAuthoredClouds => _hasAuthoredClouds;

    /// <summary>Should the sky pass draw the authored cloud layer?</summary>
    public bool AuthoredCloudsReady => Authored && _hasAuthoredClouds;

    public Vector3 CloudSunGlow => _authoredCloudSunGlow;
    public Vector3 CloudSlope => _authoredCloudSlope;
    public Vector3 CloudBase => _authoredCloudBase;
    public float CloudDensity => _authoredCloudDensity;

    /// <summary>Hand over the resolved cloud palette + density for this place and time.</summary>
    public void SetAuthoredClouds(Vector3 sunGlow, Vector3 slope, Vector3 baseColor, float density)
    {
        _authoredCloudSunGlow = sunGlow;
        _authoredCloudSlope = slope;
        _authoredCloudBase = baseColor;
        _authoredCloudDensity = Math.Clamp(density, 0f, 1f);
        _hasAuthoredClouds = true;
    }

    // The five sky bands the SkyRenderer draws. Fall back to the flat
    // fog-coloured sky when there is no data, which is exactly the old
    // behaviour - so a missing DBC degrades to what shipped before, not to
    // black.
    public Vector3 SkyTop => Authored ? _authoredSkyTop : FogColor;
    public Vector3 SkyMiddle => Authored ? _authoredSkyMiddle : FogColor;
    public Vector3 SkyBand1 => Authored ? _authoredSkyBand1 : FogColor;
    public Vector3 SkyBand2 => Authored ? _authoredSkyBand2 : FogColor;
    public Vector3 SkySmog => Authored ? _authoredSkySmog : FogColor;

    public Vector3 SunDirection { get; private set; }
        = Vector3.Normalize(new Vector3(0.45f, 0.35f, 0.82f));
    public Vector3 SunColor { get; private set; } = NoonSun;
    public Vector3 AmbientColor { get; private set; } = DayAmbient;
    public Vector3 FogColor { get; private set; } = DayFog;
    public Vector3 SkyColor => FogColor;
    public float SunIntensity { get; private set; } = 0.90f;
    public float AmbientIntensity { get; private set; } = 0.64f;

    public float ShaderFogStart => FogEnabled ? MathF.Min(FogStart, FogEnd - 1f) : 100_000f;
    public float ShaderFogEnd => FogEnabled ? MathF.Max(FogEnd, FogStart + 1f) : 100_001f;
    public float VisibilityDistance => CullAtFogEnd ? MathF.Max(100f, FogEnd) : float.PositiveInfinity;

    public void Evaluate()
    {
        TimeOfDayHours = WrapHours(TimeOfDayHours);

        // Authored path. The sun DIRECTION is still computed, because Light.dbc
        // holds colours and distances but no sun position - that is genuinely
        // not in the data, and inventing it here is honest where inventing a
        // colour was not.
        if (Authored)
        {
            FogStart = _authoredFogStart;
            FogEnd = _authoredFogEnd;

            SunDirection = SunDirectionFor(Mode, TimeOfDayHours);
            SunColor = _authoredDiffuse;
            AmbientColor = _authoredAmbient;
            FogColor = _authoredFog;

            // The sliders become multipliers on authored values rather than on
            // invented ones (PLAN_09 §10). At 1.0 the data is used exactly,
            // which is what makes the probe's data-vs-applied deltas read zero.
            //
            // MODE SEAM (owner-settled 2026-08-15). Msui applies band 0 raw -
            // the pre-v6 look, preserved bit for bit. Parity112 = the SPOT-ON
            // law: authored colours RAW (band 0's cyan-blue night value is the
            // moonlight), the WoW.exe never-setting sun direction, and the
            // dnc.db intensity curve (0.8 day, 1.0 deep night) as the one
            // scalar. Two later "more faithful" revisions multiplied the dnc
            // COLOUR ramps into diffuse and ambient; both read wrong against
            // the real client and were reverted on the owner's call.
            float modeScale = Mode == LightingMode.Parity112
                ? ParityDaylightIntensity?.Invoke(TimeOfDayHours) ?? 1f
                : 1f;
            SunIntensity = SunStrength * modeScale;
            AmbientIntensity = AmbientStrength;
            return;
        }

        FogStart = Math.Clamp(FogStart, 0f, 1999f);
        FogEnd = Math.Clamp(FogEnd, FogStart + 1f, 2000f);

        if (!DynamicLighting)
        {
            SunDirection = Vector3.Normalize(new Vector3(0.45f, 0.35f, 0.82f));
            SunColor = NoonSun;
            AmbientColor = DayAmbient;
            FogColor = DayFog;
            // Warm and no longer clipped: flat ground ~ (1.15, 1.04, 0.84).
            SunIntensity = 0.90f * SunStrength;
            AmbientIntensity = 0.64f * AmbientStrength;
            return;
        }

        // Six is sunrise, twelve is solar noon, eighteen is sunset. The
        // The horizontal component rotates too, so directional model shading
        // follows the sun instead of only becoming brighter and darker. There
        // is no cast-shadow pass yet.
        float phase = (TimeOfDayHours - 6f) / 24f * MathF.Tau;
        float altitude = MathF.Sin(phase);
        SunDirection = SunDirectionAt(TimeOfDayHours);

        float daylight = SmoothStep(-0.12f, 0.22f, altitude);
        float horizon = 1f - SmoothStep(0.05f, 0.55f, MathF.Abs(altitude));
        float warm = horizon * daylight;

        SunColor = Vector3.Lerp(
            HorizonSun,
            NoonSun,
            SmoothStep(0.02f, 0.65f, altitude));
        AmbientColor = Vector3.Lerp(NightAmbient, DayAmbient, daylight);
        FogColor = Vector3.Lerp(NightFog, DayFog, daylight);
        FogColor = Vector3.Lerp(FogColor, new Vector3(0.74f, 0.38f, 0.25f), warm * 0.42f);

        // Dimmer than the old 1.15 / (0.42 + 0.43) so sunlit ground stops
        // clipping to white. Warmth now comes from colour, not from raw power.
        SunIntensity = daylight * 0.90f * SunStrength;
        AmbientIntensity = (0.30f + daylight * 0.34f) * AmbientStrength;
    }

    public void SetDay() => TimeOfDayHours = 12f;
    public void SetSunset() => TimeOfDayHours = 18.25f;
    public void SetNight() => TimeOfDayHours = 0f;

    /// <summary>
    /// The per-mode seam for the sun's day arc. Msui keeps the invented
    /// sunrise-to-sunset arc. Parity112 uses the REAL 1.12 law, transcribed
    /// from WoW.exe via the benilla reference (lighting/daynight.rs
    /// DayNight::SetDirection, verified 2026-08-14): the lighting sun NEVER
    /// SETS. Azimuth is a constant 225 degrees all day; only the elevation
    /// wobbles, +37 deg at noon AND midnight, +20 deg at 06:00/18:00. At
    /// midnight the directional light is still up there and its colour is
    /// simply band 0's night value - the blue moonlight that paints the
    /// vanilla night. Our old arc sent the sun below the horizon at night,
    /// which zeroed N.L on every upward face and reduced night to
    /// ambient-only: dark, but a dimmed day instead of a moonlit one.
    /// </summary>
    private static Vector3 SunDirectionFor(LightingMode mode, float hours)
        => mode == LightingMode.Parity112 ? VanillaSunDirectionAt(hours) : SunDirectionAt(hours);

    // WoW.exe's polar-angle table (radians from +Z, benilla PHI_TABLE): day
    // fraction -> polar angle, linear between entries, wrapping midnight.
    // 2.2165682 rad = 127 deg polar = 37 deg above the horizon (the travel
    // direction points DOWN; the table is for the travel vector's polar angle).
    private static readonly (float DayFrac, float Phi)[] VanillaSunPhi =
    [
        (0.00f, 2.2165682f),   // midnight
        (0.25f, 1.9198623f),   // 06:00
        (0.50f, 2.2165682f),   // noon
        (0.75f, 1.9198623f),   // 18:00
        (1.00f, 2.2165682f),   // wrap
    ];

    /// <summary>Constant lighting azimuth, WoW.exe: 225 degrees.</summary>
    private const float VanillaSunTheta = 3.926991f;

    /// <summary>
    /// The 1.12 lighting-sun direction as a TO-SUN vector (shaders dot the
    /// surface normal against it). WoW.exe builds the travel direction
    /// (sin phi cos theta, sin phi sin theta, cos phi); this returns its
    /// negation in the same world frame.
    /// </summary>
    private static Vector3 VanillaSunDirectionAt(float hours)
    {
        float dayFrac = WrapHours(hours) / 24f;
        float phi = VanillaSunPhi[^1].Phi;
        for (int i = 0; i + 1 < VanillaSunPhi.Length; i++)
        {
            if (dayFrac > VanillaSunPhi[i + 1].DayFrac) continue;
            float span = VanillaSunPhi[i + 1].DayFrac - VanillaSunPhi[i].DayFrac;
            float t = span <= 0f ? 0f : (dayFrac - VanillaSunPhi[i].DayFrac) / span;
            phi = VanillaSunPhi[i].Phi + (VanillaSunPhi[i + 1].Phi - VanillaSunPhi[i].Phi) * t;
            break;
        }

        float sinPhi = MathF.Sin(phi);
        return Vector3.Normalize(new Vector3(
            -sinPhi * MathF.Cos(VanillaSunTheta),
            -sinPhi * MathF.Sin(VanillaSunTheta),
            -MathF.Cos(phi)));
    }

    /// <summary>
    /// Six is sunrise, twelve solar noon, eighteen sunset. Shared by both paths
    /// because Light.dbc carries no sun position - the one thing here that is
    /// still ours to invent (but see SunDirectionFor: dnc.db does carry one).
    /// </summary>
    private static Vector3 SunDirectionAt(float hours)
    {
        float phase = (hours - 6f) / 24f * MathF.Tau;
        return Vector3.Normalize(new Vector3(
            MathF.Cos(phase) * 0.72f,
            MathF.Sin(phase) * 0.42f,
            MathF.Sin(phase)));
    }

    private static float WrapHours(float hours)
    {
        hours %= 24f;
        return hours < 0f ? hours + 24f : hours;
    }

    private static float SmoothStep(float edge0, float edge1, float value)
    {
        float t = Math.Clamp((value - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}

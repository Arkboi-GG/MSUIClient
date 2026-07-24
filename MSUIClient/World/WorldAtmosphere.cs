using System.Numerics;

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
        FogStart = Math.Clamp(FogStart, 0f, 1999f);
        FogEnd = Math.Clamp(FogEnd, FogStart + 1f, 2000f);
        TimeOfDayHours = WrapHours(TimeOfDayHours);

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
        SunDirection = Vector3.Normalize(new Vector3(
            MathF.Cos(phase) * 0.72f,
            MathF.Sin(phase) * 0.42f,
            altitude));

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

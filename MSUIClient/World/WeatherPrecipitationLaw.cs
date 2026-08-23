using System.Numerics;

namespace MSUIClient.World;

/// <summary>
/// Pure 1.12 precipitation geometry and motion law. Runtime pools and OpenGL
/// ownership live in <see cref="WeatherPrecipitationRenderer"/>; this class is
/// deliberately deterministic and independently checkable.
/// </summary>
public static class WeatherPrecipitationLaw
{
    public const int DropCapacity = 0x1800;
    public const int GroundCapacity = 0x1800;
    public const int MistCapacity = 128;
    public const float RetireDistance = 200f;
    public const float DropRetireSlack = 50f;
    public const float RainPopulation = 35000f;
    public const float SnowPopulation = 14000f;
    public const float ReferenceFpsGain = .5f;
    public const float FrameDtCap = 1f / 60f;
    public const float WindLead = 1.75f;
    public const float RainHalfExtent = 65f;
    public const float RainHeight = 37.5f;
    public const float SnowHalfExtent = 45f;
    public const float SnowHeight = 30f;
    public const float RainPatterLife = .25f;
    public const float SnowSettleLife = .25f;
    public const float SnowFadeIn = 1f;
    public const float MistFloor = 6f;
    public const float MistHalfSize = 6f;
    public const float MistFadeSeconds = .4f;
    public const float MistAlphaNear = 6f;
    public const float MistAlphaFar = 18f;
    public const float SnowReferenceHeight = 800f;

    private const float DriftAzimuth = -1.57f;
    private const float RainSpread = .20943952f;
    private const float RainSpreadBias = .05235988f;
    private const float SnowSpreadWeight = 5.9341197f;

    public readonly record struct Spawn(Vector3 Position, Vector3 Velocity);

    public static bool IndoorBlocked(bool cameraInInterior, bool exteriorPortalVisible) =>
        cameraInInterior && !exteriorPortalVisible;

    public static (float HalfExtent, float Height) SpawnBox(WeatherVisualLaw.Kind kind) =>
        kind == WeatherVisualLaw.Kind.Rain
            ? (RainHalfExtent, RainHeight)
            : (SnowHalfExtent, SnowHeight);

    /// <summary>Reference frame-local drop budget. The unused fraction is discarded.</summary>
    public static int FrameSpawnCount(WeatherVisualLaw.Kind kind, float density, float dt,
        int freeSlots, byte weatherDensity = 3)
    {
        float population = kind == WeatherVisualLaw.Kind.Rain
            ? RainPopulation : SnowPopulation;
        float quota = WeatherVisualLaw.DensityGain(weatherDensity) * population *
                      Math.Clamp(density, 0f, 1f) * ReferenceFpsGain *
                      MathF.Min(MathF.Max(dt, 0f), FrameDtCap);
        if (quota <= 1f || freeSlots <= 0) return 0;
        float answer = MathF.Round(MathF.Min(freeSlots, quota) - .5f,
            MidpointRounding.ToEven);
        return Math.Clamp((int)answer, 0, freeSlots);
    }

    /// <summary>
    /// Spawn a drop/flake in MSUI's native WoW coordinates (Z up). The five
    /// randoms are uniform [0,1), matching the five-draw reference kernel.
    /// </summary>
    public static Spawn SpawnParticle(WeatherVisualLaw.Kind kind, float density,
        Vector3 camera, Vector3 averagedWind, Quaternion slabTilt,
        float r0, float r1, float r2, float r3, float r4)
    {
        float w = Math.Clamp(density, 0f, 1f);
        var (half, height) = SpawnBox(kind);
        Vector3 scatter = new((r0 - .5f) * 2f * half,
                              (r1 - .5f) * 2f * half, 0f);
        float vz, drift, spread;
        if (kind == WeatherVisualLaw.Kind.Rain)
        {
            vz = -(28f + 4f * w + 2f * w * r2);
            drift = ((2f * r3 - 1f) + 9.49f) * w + .01f;
            spread = RainSpread * w + RainSpreadBias;
        }
        else
        {
            vz = -(2f + 3.5f * w + w * r2);
            drift = ((r3 - .5f) + 5.985f) * w + .015f;
            spread = MathF.Tau - SnowSpreadWeight * w;
        }

        float azimuth = DriftAzimuth + (r4 - .5f) * spread;
        Vector3 velocity = new(MathF.Sin(azimuth) * drift,
                               MathF.Cos(azimuth) * drift, vz);
        Vector3 anchor = camera + new Vector3(averagedWind.X, averagedWind.Y, 0f) * WindLead;
        anchor.Z = camera.Z;
        Vector3 local = scatter - velocity * (height / -vz);
        return new Spawn(anchor + Vector3.Transform(local, slabTilt), velocity);
    }

    public static float SnowPixelSize(float radialDistance) =>
        MathF.Max(1f, 14f * Math.Clamp(1f - .02f * radialDistance, 0f, 1f));

    public static float MistRate(WeatherVisualLaw.Kind kind, float density,
        byte weatherDensity = 3)
    {
        float q = kind == WeatherVisualLaw.Kind.Snow ? 48f : 38f;
        return 2f * MathF.Max(density - .5f, 0f) *
               WeatherVisualLaw.DensityGain(weatherDensity) * q;
    }

    public static float MistDistanceAlpha(float distance) =>
        Math.Clamp((distance - MistAlphaNear) / (MistAlphaFar - MistAlphaNear), 0f, 1f);

    public static float MistLifeAlpha(float age, float life) =>
        Math.Clamp(age / MistFadeSeconds, 0f, 1f) *
        Math.Clamp((life - age) / MistFadeSeconds, 0f, 1f);

    public static int FirstInvalidMistPath(ReadOnlySpan<float> path)
    {
        for (int i = 0; i < path.Length; i++)
        {
            bool jump1 = i + 1 < path.Length && path[i + 1] - path[i] > .5f;
            bool jump2 = i + 2 < path.Length && path[i + 2] - path[i] > .75f;
            bool jump3 = i + 3 < path.Length && path[i + 3] - path[i] > 1f;
            if (jump1 || jump2 || jump3) return i;
        }
        return -1;
    }
}

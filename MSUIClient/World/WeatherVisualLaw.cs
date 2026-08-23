namespace MSUIClient.World;

/// <summary>
/// The client-side visual state behind SMSG_WEATHER. This is the two-channel
/// CMapWeather ramp, kept independent of rendering so loading screens do not
/// pause it and every visual consumer reads the same answer.
/// </summary>
public sealed class WeatherVisualLaw
{
    public enum Kind : uint
    {
        Fine = 0,
        Rain = 1,
        Snow = 2,
        Sand = 3,
    }

    private struct Channel(float spanScale)
    {
        public float From;
        public float To;
        public double StartedAt;
        public readonly float SpanScale = spanScale;

        public readonly float Value(double now)
        {
            // WoW.exe 0x67bc70: the epsilon is inside the absolute value.
            float duration = MathF.Abs((To - From) * SpanScale + .001f) * 10f;
            float t = Math.Clamp((float)(now - StartedAt) / duration, 0f, 1f);
            return From + (To - From) * t;
        }

        public void Retarget(float target, double now)
        {
            // SetWeather starts from the old TARGET, not the in-flight value.
            From = To;
            To = target;
            StartedAt = now;
        }

        public void Snap(float target)
        {
            From = target;
            To = target;
        }
    }

    private Channel _effect = new(1f);
    private Channel _sky = new(4f);

    public Kind WeatherKind { get; private set; }
    public Kind EffectKind { get; private set; }
    public float IntensityA { get; private set; }
    public float EffectDensity { get; private set; }
    public float SkyDensity { get; private set; }
    public float StormBlend => MathF.Min(1f, SkyDensity * 4f);
    public uint CutSequence { get; private set; }

    /// <summary>The vanilla weatherDensity 0..3 spawn-rate table.</summary>
    public static float DensityGain(byte weatherDensity) =>
        weatherDensity switch
        {
            0 => .1f,
            1 => .33f,
            2 => .66f,
            _ => 1f,
        };

    public void Apply(uint wireKind, float grade, bool instant, double now)
    {
        Kind kind = wireKind switch
        {
            1 => Kind.Rain,
            2 => Kind.Snow,
            3 => Kind.Sand,
            _ => Kind.Fine,
        };
        float target = kind == Kind.Fine ? 0f : Math.Clamp(grade, 0f, 1f);

        if (kind != WeatherKind)
        {
            CutSequence++;
            EffectKind = kind;
        }
        WeatherKind = kind;

        float skyTarget = MathF.Min(target, .25f);
        if (instant)
        {
            _effect.Snap(target);
            _sky.Snap(skyTarget);
        }
        else
        {
            _effect.Retarget(target, now);
            _sky.Retarget(skyTarget, now);
        }

        Resolve(now);
    }

    public void Resolve(double now)
    {
        IntensityA = _effect.Value(now);
        EffectDensity = MathF.Max(0f, (IntensityA - .25f) * (4f / 3f));
        SkyDensity = _sky.Value(now);
    }
}

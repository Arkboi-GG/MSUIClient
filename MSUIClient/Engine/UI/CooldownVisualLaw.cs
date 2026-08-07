using System.Numerics;

namespace MSUIClient.Engine.UI;

/// <summary>
/// Build-5875 UI-Cooldown-Indicator geometry. The dark sweep is a square-contained clockwise
/// wipe; the finish star is the authored one-second scale/alpha pulse.
/// </summary>
public static class CooldownVisualLaw
{
    public const float WipeAlpha = 153f / 255f;

    public readonly record struct Quad(Vector2 A, Vector2 B, Vector2 C, Vector2 D);

    private static readonly (float Time, float Scale)[] StarScaleKeys =
    [
        (0f, 1f),
        (0.333f, 1.853f),
        (0.666f, 1.305f),
        (0.833f, 1.605f),
        (1f, 1.155f),
    ];

    /// <summary>
    /// Returns at most four dark quads. Coverage shrinks clockwise from 12 o'clock and every
    /// vertex is inside <paramref name="min"/>..<paramref name="max"/>.
    /// </summary>
    public static IReadOnlyList<Quad> BuildWipe(Vector2 min, Vector2 max, float elapsedFraction)
    {
        float fraction = Math.Clamp(elapsedFraction, 0f, 1f);
        if (fraction >= 1f || max.X <= min.X || max.Y <= min.Y) return [];

        Vector2 c = (min + max) * 0.5f;
        float theta = fraction * MathF.Tau;
        int active = Math.Min((int)(theta / (MathF.PI * 0.5f)), 3);

        (Vector2 Min, Vector2 Max) Quadrant(int k) => k switch
        {
            0 => (new Vector2(c.X, min.Y), new Vector2(max.X, c.Y)),
            1 => (c, max),
            2 => (new Vector2(min.X, c.Y), new Vector2(c.X, max.Y)),
            _ => (min, c),
        };

        static Quad Rect(Vector2 a, Vector2 b) => new(
            a, new Vector2(b.X, a.Y), b, new Vector2(a.X, b.Y));

        var result = new List<Quad>(4);
        for (int k = active + 1; k < 4; k++)
        {
            var q = Quadrant(k);
            result.Add(Rect(q.Min, q.Max));
        }

        // Clockwise from 12 o'clock in screen space (Y grows downward).
        Vector2 d = new(MathF.Sin(theta), -MathF.Cos(theta));
        float tFirst, tSecond;
        Vector2 outerCorner, endMidpoint;
        switch (active)
        {
            case 0:
                tFirst = (min.Y - c.Y) / d.Y;
                tSecond = (max.X - c.X) / d.X;
                outerCorner = new Vector2(max.X, min.Y);
                endMidpoint = new Vector2(max.X, c.Y);
                break;
            case 1:
                tFirst = (max.X - c.X) / d.X;
                tSecond = (max.Y - c.Y) / d.Y;
                outerCorner = max;
                endMidpoint = new Vector2(c.X, max.Y);
                break;
            case 2:
                tFirst = (max.Y - c.Y) / d.Y;
                tSecond = (min.X - c.X) / d.X;
                outerCorner = new Vector2(min.X, max.Y);
                endMidpoint = new Vector2(min.X, c.Y);
                break;
            default:
                tFirst = (min.X - c.X) / d.X;
                tSecond = (min.Y - c.Y) / d.Y;
                outerCorner = min;
                endMidpoint = new Vector2(c.X, min.Y);
                break;
        }

        Vector2 rayExit = c + d * MathF.Min(tFirst, tSecond);
        result.Add(tFirst <= tSecond
            ? new Quad(c, rayExit, outerCorner, endMidpoint)
            : new Quad(c, rayExit, endMidpoint, endMidpoint));
        return result;
    }

    public static float FlashAlpha(float progress)
    {
        float t = Math.Clamp(progress, 0f, 1f);
        return t < 1f / 3f ? t * 3f : t < 0.5f ? 1f : (1f - t) * 2f;
    }

    public static float FlashScale(float progress)
    {
        float t = Math.Clamp(progress, 0f, 1f);
        for (int i = 0; i < StarScaleKeys.Length - 1; i++)
        {
            var (t0, v0) = StarScaleKeys[i];
            var (t1, v1) = StarScaleKeys[i + 1];
            if (t <= t1) return v0 + (v1 - v0) * ((t - t0) / (t1 - t0));
        }
        return StarScaleKeys[^1].Scale;
    }
}

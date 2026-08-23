using System.Numerics;

namespace MSUIClient.Engine.UI;

/// <summary>Frozen build-5875 ComboFrame geometry, target gate, and three-leg fade law.</summary>
public static class ComboFrameUiLaw
{
    public const int MaximumPoints = 5;
    public const string Texture = @"Interface\ComboFrame\ComboPoint";
    public const float FrameFadeInSeconds = 0.3f;
    public const float HighlightFadeInSeconds = 0.4f;
    public const float ShineFadeInSeconds = 0.3f;
    public const float ShineFadeOutSeconds = 0.4f;
    public static readonly Vector2 SocketSize = new(12, 16);
    public static readonly Vector2 HighlightOffset = new(2, 0);
    public static readonly Vector2 HighlightSize = new(8, 16);
    public static readonly Vector2 ShineOffset = new(0, -4);
    public static readonly Vector2 ShineSize = new(14, 16);
    public static readonly Vector2 SocketUvMin = Vector2.Zero;
    public static readonly Vector2 SocketUvMax = new(.375f, 1);
    public static readonly Vector2 HighlightUvMin = new(.375f, 0);
    public static readonly Vector2 HighlightUvMax = new(.5625f, 1);
    public static readonly Vector2 ShineUvMin = new(.5625f, 0);
    public static readonly Vector2 ShineUvMax = Vector2.One;

    private static readonly Vector2[] PointFrameSizes =
    [
        new(12, 12), new(12, 12), new(12, 12), new(12, 12), new(15, 18),
    ];
    private static readonly Vector2[] NextAnchorOffsets =
    [
        new(7, 4), new(5, 2), new(2, 1), new(0, 1),
    ];

    /// <summary>
    /// GetComboPoints' real gates: only rogue/druid and only when the banked owner is the current
    /// target. The raw byte itself is intentionally not class-filtered by the server.
    /// </summary>
    public static byte VisiblePoints(byte classId, byte rawPoints, ulong comboTarget,
        ulong currentTarget) =>
        classId is 4 or 11 && comboTarget != 0 && comboTarget == currentTarget
            ? (byte)Math.Min(MaximumPoints, (int)rawPoints)
            : (byte)0;

    /// <summary>Point frame top-left in screen-logical units, relative to TargetFrame top-left.</summary>
    public static Vector2 PointMinimum(int zeroBasedIndex)
    {
        int index = Math.Clamp(zeroBasedIndex, 0, MaximumPoints - 1);
        // ComboFrame TOPRIGHT sits at TargetFrame TOPRIGHT(-44,-9); point 1 is TOPRIGHT of it.
        Vector2 minimum = new(232f - 44f - PointFrameSizes[0].X, 9f);
        for (int i = 1; i <= index; i++)
        {
            Vector2 priorSize = PointFrameSizes[i - 1];
            Vector2 currentSize = PointFrameSizes[i];
            Vector2 offset = NextAnchorOffsets[i - 1];
            float priorBottomCenterX = minimum.X + priorSize.X * 0.5f;
            float priorBottomY = minimum.Y + priorSize.Y;
            // Authored positive y points upward; screen y grows downward.
            minimum = new Vector2(priorBottomCenterX + offset.X - currentSize.X * 0.5f,
                priorBottomY - offset.Y);
        }
        return minimum;
    }

    public static float FrameAlpha(double now, double shownAt) =>
        (float)Math.Clamp((now - shownAt) / FrameFadeInSeconds, 0.0, 1.0);

    public static float HighlightAlpha(double now, double earnedAt) =>
        (float)Math.Clamp((now - earnedAt) / HighlightFadeInSeconds, 0.0, 1.0);

    public static float ShineAlpha(double now, double earnedAt)
    {
        double phase = now - earnedAt - HighlightFadeInSeconds;
        if (phase <= 0) return 0;
        if (phase < ShineFadeInSeconds) return (float)(phase / ShineFadeInSeconds);
        phase -= ShineFadeInSeconds;
        return phase < ShineFadeOutSeconds
            ? 1f - (float)(phase / ShineFadeOutSeconds)
            : 0f;
    }
}

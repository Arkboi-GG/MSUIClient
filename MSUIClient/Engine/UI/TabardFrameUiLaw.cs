using System.Numerics;

namespace MSUIClient.Engine.UI;

/// <summary>
/// Rule-owned geometry for MSUI's desktop guild-tabard designer. Benilla currently has no
/// authored TabardFrame surface, so these seats preserve the established MSUI presentation.
/// </summary>
public static class TabardFrameUiLaw
{
    public readonly record struct LogicalRect(float X, float Y, float Width, float Height)
    {
        public Vector2 Min => new(X, Y);
        public Vector2 Size => new(Width, Height);
    }

    public readonly record struct SelectorLayout(
        Vector2 LabelCenter, LogicalRect Previous, LogicalRect Next, Vector2 ValueCenter);

    public static readonly LogicalRect Frame = new(0, 104, 384, 512);
    public static readonly LogicalRect Background = new(23, 76, 316, 325);
    public static readonly Vector2 TitleCenter = new(192, 18);
    public static readonly Vector2 SubtitleCenter = new(192, 44);

    public static SelectorLayout Selector(int index)
    {
        float y = 172 + Math.Clamp(index, 0, 4) * 43;
        return new(new Vector2(274, y), new(218, y + 8, 32, 32),
            new(306, y + 8, 32, 32), new Vector2(278, y + 24));
    }

    public static readonly Vector2 Cost = new(55, 399);
    public static readonly LogicalRect Accept = new(184, 409, 80, 22);
    public static readonly LogicalRect Cancel = new(265, 409, 80, 22);
    public static readonly LogicalRect Close = new(323, 8, 32, 32);
}

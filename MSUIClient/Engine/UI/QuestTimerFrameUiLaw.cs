using System.Numerics;

namespace MSUIClient.Engine.UI;

/// <summary>Frozen 1.12 QuestTimerFrame geometry and managed-HUD stacking laws.</summary>
public static class QuestTimerFrameUiLaw
{
    public const string TitleFont = "GameFontNormal";
    public const string RowFont = "GameFontHighlightSmall";
    public readonly record struct ScreenRect(Vector2 Min, Vector2 Size);
    public const int MaxTimers = 20;
    public const float Width = 160f;
    public const float EmptyHeight = 72f;
    public const float HeightBase = 45f;
    public const float RowWidth = 140f;
    public const float RowHeight = 16f;
    public const float FirstRowTop = 30f;
    public const float MinimapClusterHeight = 192f;
    public static readonly Vector2 HeaderMin = new(-48f, -12f);
    public static readonly Vector2 HeaderSize = new(256f, 64f);
    public static readonly Vector2 TitleCenter = new(80f, 7f);

    public static float FrameHeight(int timerCount) =>
        HeightBase + RowHeight * Math.Clamp(timerCount, 0, MaxTimers);

    public static float ManagedHeight(int timerCount) => timerCount > 0
        ? FrameHeight(timerCount) : 0f;

    public static Vector2 FrameOrigin(Vector2 display, float scale) =>
        new(display.X - Width * scale, MinimapClusterHeight * scale);

    public static QuestLogicalRect RowRect(int timerIndex) =>
        new((Width - RowWidth) * .5f,
            FirstRowTop + Math.Clamp(timerIndex, 0, MaxTimers - 1) * RowHeight,
            RowWidth, RowHeight);

    public static Vector2 FrameSize(float height, float scale) =>
        new(Width * scale, Math.Max(0, height) * scale);

    public static ScreenRect RowScreen(Vector2 origin, int timerIndex, float scale)
    {
        QuestLogicalRect row = RowRect(timerIndex);
        return new(new(origin.X + row.X * scale, origin.Y + row.Y * scale),
            new(row.Width * scale, row.Height * scale));
    }

    public static Vector2 RowTextCenter(in ScreenRect row, float textEm) =>
        new(row.Min.X + row.Size.X * .5f, row.Min.Y + textEm * .5f);
}

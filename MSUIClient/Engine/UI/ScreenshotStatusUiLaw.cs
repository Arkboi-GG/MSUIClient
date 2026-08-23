using System.Numerics;

namespace MSUIClient.Engine.UI;

/// <summary>Build-5875 ScreenshotStatus text, fade, center seat, and file naming.</summary>
public static class ScreenshotStatusUiLaw
{
    public const string SuccessText = "Screen Captured";
    public const string FailureText = "Screen Capture Failed";
    public const float FadeSeconds = 1.5f;
    public const string FileStem = "WoWScrnShot";

    public static Vector2 TextCenter(Vector2 display) => display * .5f;
    public static Vector2 TextMinimum(Vector2 display, float measuredWidth,
        float measuredHeight) => TextCenter(display) - new Vector2(measuredWidth, measuredHeight) * .5f;
    public static float Alpha(double elapsedSeconds) =>
        1f - Math.Clamp((float)elapsedSeconds / FadeSeconds, 0f, 1f);

    public static string FileName(DateTime localTime, int withinSecond = 0)
    {
        string baseName = $"{FileStem}_{localTime:MMddyy_HHmmss}";
        return withinSecond <= 0 ? $"{baseName}.png" : $"{baseName}_{withinSecond + 1}.png";
    }
}

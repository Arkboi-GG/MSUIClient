using System.Numerics;
using MSUIClient.Net;

namespace MSUIClient.Engine.UI;

/// <summary>MirrorTimer.xml's authored top-center stack and visual vocabulary.</summary>
public static class MirrorTimerUiLaw
{
    public const float FrameWidth = 206f;
    public const float FrameHeight = 26f;
    public const float TopOffset = 96f;
    public const float BarWidth = 195f;
    public const float BarHeight = 13f;
    public const float BorderWidth = 256f;
    public const float BorderHeight = 64f;
    public const string StatusTexture = @"Interface\TargetingFrame\UI-StatusBar";
    public const string BorderTexture = @"Interface\CastingBar\UI-CastingBar-Border";
    public static readonly Vector2 BarMin = new((FrameWidth - BarWidth) * .5f, 2f);
    public static readonly Vector2 BorderMin = new((FrameWidth - BorderWidth) * .5f, -25f);
    public static readonly Vector2 TextCenter = new(FrameWidth * .5f, 6f);

    public readonly record struct ScreenRect(Vector2 Min, Vector2 Size);

    public static readonly Vector2 BorderSize = new(BorderWidth, BorderHeight);

    public static ScreenRect BarRect(ScreenRect frame, float scale) =>
        new(frame.Min + BarMin * scale, new(BarWidth * scale, BarHeight * scale));

    public static ScreenRect BorderRect(ScreenRect frame, float scale) =>
        new(frame.Min + BorderMin * scale, BorderSize * scale);

    public static Vector2 FillSize(Vector2 barSize, float fraction) =>
        new(barSize.X * Math.Clamp(fraction, 0, 1), barSize.Y);

    public static Vector2 FillUvMax(float fraction) =>
        new(Math.Clamp(fraction, 0, 1), 1);

    public static ScreenRect FrameRect(Vector2 displayPixels, float scale, int frameIndex)
    {
        Vector2 size = new Vector2(FrameWidth, FrameHeight) * scale;
        return new(new Vector2((displayPixels.X - size.X) * .5f,
            (TopOffset + frameIndex * FrameHeight) * scale), size);
    }

    public static Vector4 Color(MirrorTimerKind kind) => kind switch
    {
        MirrorTimerKind.Fatigue => new(1f, .9f, 0f, 1f),
        MirrorTimerKind.Breath => new(0f, .5f, 1f, 1f),
        _ => new(1f, .7f, 0f, 1f),
    };

    public static string ScriptName(MirrorTimerKind kind) => kind switch
    {
        MirrorTimerKind.Fatigue => "EXHAUSTION",
        MirrorTimerKind.Breath => "BREATH",
        _ => "FEIGNDEATH",
    };

    public static string FallbackCaption(MirrorTimerKind kind) => kind switch
    {
        MirrorTimerKind.Fatigue => "Fatigue",
        MirrorTimerKind.Breath => "Breath",
        _ => "",
    };
}

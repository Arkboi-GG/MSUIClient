using System.Numerics;

namespace MSUIClient.Engine.UI;

/// <summary>Current AccountLogin GlueDialog geometry in 1024x768 glue units.</summary>
public static class LoginUiLaw
{
    public readonly record struct ScreenRect(Vector2 Min, Vector2 Size)
    {
        public Vector2 Max => Min + Size;
    }

    public readonly record struct DialogLayout(
        ScreenRect Frame, ScreenRect Message, ScreenRect Button);

    public readonly record struct LaunchOptionsLayout(
        ScreenRect Frame,
        Vector2 PromptCenter,
        ScreenRect ClientButton,
        Vector2 ClientActiveLabel,
        ScreenRect CreatorButton,
        Vector2 CreatorActiveLabel,
        ScreenRect OkayButton);

    public const float DialogWidth = 512f;
    public const float MessageWidth = 440f;
    public const float MessageTop = 16f;
    public const float MessageFontSize = 18f;
    public const float MessageButtonGap = 13f;
    public const float ButtonWidth = 200f;
    public const float ButtonHeight = 40f;
    public const float ButtonBottom = 16f;
    public const float MinimumHeight = 108f;
    public const float LaunchWidth = 420f;
    public const float LaunchHeight = 250f;
    public const float LaunchPromptTop = 44f;
    public const float LaunchModeButtonWidth = 250f;
    public const float LaunchModeButtonHeight = 40f;
    public const float LaunchClientTop = 74f;
    public const float LaunchCreatorTop = 124f;
    public const float LaunchActiveGap = 8f;
    public const float LaunchActiveBaselineLift = 6f;
    public const float LaunchOkayWidth = 120f;
    public const float LaunchOkayHeight = 34f;
    public const float LaunchOkayBottomSeat = 46f;

    public static ScreenRect Host(Vector2 displaySize) =>
        new(Vector2.Zero, displaySize);

    public static ScreenRect TuningWindow =>
        new(new Vector2(48f, 48f), new Vector2(380f, 0f));

    public static float DialogHeight(float messageHeight) => MathF.Max(MinimumHeight,
        MessageTop + MathF.Max(0f, messageHeight) + MessageButtonGap +
        ButtonHeight + ButtonBottom);

    public static DialogLayout Dialog(Vector2 display, float scale, float messageHeight)
    {
        float s = MathF.Max(scale, 0f);
        float logicalHeight = DialogHeight(messageHeight);
        Vector2 frameSize = new(DialogWidth * s, logicalHeight * s);
        Vector2 origin = (display - frameSize) * .5f;
        return new(
            new(origin, frameSize),
            new(origin + new Vector2((DialogWidth - MessageWidth) * .5f, MessageTop) * s,
                new Vector2(MessageWidth, MathF.Max(0f, messageHeight)) * s),
            new(origin + new Vector2((DialogWidth - ButtonWidth) * .5f,
                    logicalHeight - ButtonBottom - ButtonHeight) * s,
                new Vector2(ButtonWidth, ButtonHeight) * s));
    }

    public static string FailureText(string status)
    {
        const string prefix = "failed:";
        string clean = status?.Trim() ?? "";
        if (clean.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            clean = clean[prefix.Length..].TrimStart();
        return clean.Length == 0 ? "Unable to connect" : clean;
    }

    public static LaunchOptionsLayout LaunchOptions(Vector2 display, float scale)
    {
        float s = MathF.Max(scale, 0f);
        Vector2 frameSize = new Vector2(LaunchWidth, LaunchHeight) * s;
        Vector2 origin = (display - frameSize) * .5f;
        float buttonX = (LaunchWidth - LaunchModeButtonWidth) * .5f;
        ScreenRect LocalRect(Vector2 min, Vector2 size) => new(origin + min * s, size * s);
        ScreenRect client = LocalRect(new(buttonX, LaunchClientTop),
            new(LaunchModeButtonWidth, LaunchModeButtonHeight));
        ScreenRect creator = LocalRect(new(buttonX, LaunchCreatorTop),
            new(LaunchModeButtonWidth, LaunchModeButtonHeight));
        Vector2 activeOffset = new Vector2(LaunchActiveGap,
            LaunchModeButtonHeight * .5f - LaunchActiveBaselineLift) * s;
        return new(
            new(origin, frameSize),
            origin + new Vector2(LaunchWidth * .5f, LaunchPromptTop) * s,
            client,
            new Vector2(client.Max.X, client.Min.Y) + activeOffset,
            creator,
            new Vector2(creator.Max.X, creator.Min.Y) + activeOffset,
            LocalRect(new((LaunchWidth - LaunchOkayWidth) * .5f,
                    LaunchHeight - LaunchOkayBottomSeat),
                new(LaunchOkayWidth, LaunchOkayHeight)));
    }
}

namespace MSUIClient.Engine.UI;

/// <summary>
/// Build-5875 overhead speech-bubble rules.  This is deliberately renderer-free:
/// the HUD supplies projection, font measurement and Blizzard texture handles,
/// while this type owns the kind gate, plain-text conversion, timing and sizing
/// constants transcribed from Benilla's byte-pinned chat_bubble module.
/// </summary>
public static class ChatBubbleUiLaw
{
    public const float RangeYards = 20f;
    public const float RangeSquared = RangeYards * RangeYards;
    public const float FadeSeconds = 0.25f;
    public const float WorldLift = 0.7f;
    public const float TextHeightGx = 0.01f;
    public const float MarginGx = 0.01f;
    public const float WrapWidthGx = 0.2f;
    public const float BorderScreenWidthFraction = 16f / 1024f;

    public const string BackgroundTexture = @"Interface\Tooltips\ChatBubble-Background";
    public const string BackdropTexture = @"Interface\Tooltips\ChatBubble-Backdrop";
    public const string TailTexture = @"Interface\Tooltips\ChatBubble-Tail";

    /// <summary>Benilla's uncontested v1 set. Party has its own CVar.</summary>
    public static bool Enabled(ChatFrameLaw.MsgType type, bool all, bool party) => type switch
    {
        ChatFrameLaw.MsgType.Party => party,
        ChatFrameLaw.MsgType.Say or ChatFrameLaw.MsgType.Yell or
        ChatFrameLaw.MsgType.MonsterSay or ChatFrameLaw.MsgType.MonsterYell => all,
        _ => false,
    };

    /// <summary>Maximal runs separated only by spaces or tabs.</summary>
    public static int WordCount(string text)
    {
        int words = 0;
        bool inWord = false;
        foreach (char c in text)
        {
            bool separator = c is ' ' or '\t';
            if (!separator && !inWord) words++;
            inWord = !separator;
        }
        return words;
    }

    /// <summary>Others: 2750+750(n-1) ms; self: 1500+500(n-1) ms.</summary>
    public static double DurationSeconds(int words, bool self)
    {
        if (words <= 0) return 0;
        int milliseconds = self
            ? 1500 + 500 * (words - 1)
            : 2750 + 750 * (words - 1);
        return milliseconds / 1000.0;
    }

    /// <summary>
    /// Bubble text is plain: escaped pipes become one pipe, color wrappers are
    /// removed, and hyperlink metadata/wrappers disappear while display text remains.
    /// Unknown or dangling escapes render as a literal pipe.
    /// </summary>
    public static string Sanitize(string text)
    {
        if (text.Length == 0) return "";
        var output = new System.Text.StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] != '|')
            {
                output.Append(text[i++]);
                continue;
            }

            if (i + 1 >= text.Length)
            {
                output.Append('|');
                i++;
                continue;
            }

            char code = text[i + 1];
            if (code == '|')
            {
                output.Append('|');
                i += 2;
            }
            else if ((code is 'c' or 'C') && i + 10 <= text.Length)
            {
                i += 10;
            }
            else if (code is 'r' or 'R' or 'h')
            {
                i += 2;
            }
            else if (code == 'H')
            {
                i += 2;
                while (i + 1 < text.Length && !(text[i] == '|' && text[i + 1] == 'h')) i++;
                i = Math.Min(text.Length, i + 2);
            }
            else
            {
                output.Append('|');
                i++;
            }
        }
        return output.ToString();
    }

    /// <summary>
    /// Stateful 250 ms ramp. The lifetime fade is permanent; the range fade is
    /// recoverable until that point.
    /// </summary>
    public static float StepAlpha(float alpha, double age, double duration,
        float deltaSeconds, bool inRange)
    {
        float step = Math.Max(0f, deltaSeconds) / FadeSeconds;
        bool permanentFade = age >= duration + FadeSeconds;
        return permanentFade || !inRange
            ? Math.Max(0f, alpha - step)
            : Math.Min(1f, alpha + step);
    }

    public static bool Recyclable(float alpha, double age, double duration) =>
        age >= duration + FadeSeconds && alpha <= 0f;

    /// <summary>Benilla's damped diagonal basis shared with MSUI nameplates.</summary>
    public static float PlateBasis(float width, float height)
    {
        float diagonal = MathF.Sqrt(width * width + height * height);
        return diagonal <= 1280f ? diagonal : 1280f + (diagonal - 1280f) * 0.5f;
    }

    public static float BorderPixels(float width, float height)
    {
        float diagonal = MathF.Sqrt(width * width + height * height);
        if (diagonal <= 0f) return 1f;
        return MathF.Max(1f, width / diagonal * PlateBasis(width, height) *
            BorderScreenWidthFraction);
    }
}

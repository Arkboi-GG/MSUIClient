using System.Numerics;

namespace MSUIClient.Engine.UI;

/// <summary>
/// Build-5875 overhead speech-bubble rules.  This is deliberately renderer-free:
/// the HUD supplies projection, font measurement and Blizzard texture handles,
/// while this type owns the kind gate, plain-text conversion, timing and sizing
/// constants transcribed from Benilla's byte-pinned chat_bubble module.
/// </summary>
public static class ChatBubbleUiLaw
{
    public readonly record struct Bounds(float Left, float Top, float Right, float Bottom)
    {
        public float Width => Right - Left;
        public float Height => Bottom - Top;
    }

    public readonly record struct ImageRect(Vector2 Min, Vector2 Max);

    public readonly record struct ImageQuad(Vector2 TopLeft, Vector2 TopRight,
        Vector2 BottomRight, Vector2 BottomLeft, Vector2 UvTopLeft, Vector2 UvTopRight,
        Vector2 UvBottomRight, Vector2 UvBottomLeft);

    public const float RangeYards = 20f;
    public const float RangeSquared = RangeYards * RangeYards;
    public const float FadeSeconds = 0.25f;
    public const float WorldLift = 0.7f;
    public const float TextHeightGx = 0.01f;
    public const float MarginGx = 0.01f;
    public const float WrapWidthGx = 0.2f;
    public const float BorderScreenWidthFraction = 16f / 1024f;
    public const float BackdropSlice = .125f;
    public const float BackdropHalfU = .5f / 256f;
    public const float BackdropHalfV = .5f / 32f;

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

    public static Bounds Frame(Vector2 seat, float width, float height, float deviceScale)
    {
        float scale = MathF.Max(1f, deviceScale);
        float Snap(float value) => MathF.Round(value * scale) / scale;
        float left = Snap(seat.X - width * .5f);
        float bottom = Snap(seat.Y);
        return new(left, bottom - height, left + width, bottom);
    }

    public static ImageRect Inner(Bounds frame, float border) =>
        new(new Vector2(frame.Left + border, frame.Top + border),
            new Vector2(frame.Right - border, frame.Bottom - border));

    public static ImageRect Tail(Bounds frame, float border)
    {
        float center = (frame.Left + frame.Right) * .5f;
        float top = frame.Bottom - border * .25f;
        return new(new Vector2(center - border, top),
            new Vector2(center, top + border));
    }

    public static Vector2 LinePosition(Bounds frame, float lineWidth, float y) =>
        new((frame.Left + frame.Right - lineWidth) * .5f, y);

    public static IReadOnlyList<ImageQuad> Backdrop(Bounds frame, float edge)
    {
        if (edge <= 0f) return [];
        float widthRun = MathF.Max(0f, frame.Width / edge - 2f);
        float heightRun = MathF.Max(0f, frame.Height / edge - 2f);
        float InsetStart(float run) => run <= 1f ? MathF.Min(BackdropHalfV, run * .5f) : 0f;
        float InsetEnd(float run) => run <= 1f
            ? MathF.Max(run * .5f, run - BackdropHalfV) : run;
        float h0 = InsetStart(heightRun), h1 = InsetEnd(heightRun);
        float w0 = InsetStart(widthRun), w1 = InsetEnd(widthRun);

        static ImageQuad Quad(float left, float top, float right, float bottom,
            Vector2 uvTl, Vector2 uvTr, Vector2 uvBr, Vector2 uvBl) =>
            new(new Vector2(left, top), new Vector2(right, top),
                new Vector2(right, bottom), new Vector2(left, bottom),
                uvTl, uvTr, uvBr, uvBl);

        ImageQuad Corner(float left, float top, int cell)
        {
            float u0 = cell * BackdropSlice + BackdropHalfU;
            float u1 = (cell + 1) * BackdropSlice - BackdropHalfU;
            return Quad(left, top, left + edge, top + edge,
                new(u0, BackdropHalfV), new(u1, BackdropHalfV),
                new(u1, 1f - BackdropHalfV), new(u0, 1f - BackdropHalfV));
        }

        return
        [
            Quad(frame.Left, frame.Top + edge, frame.Left + edge, frame.Bottom - edge,
                new(BackdropHalfU, h0), new(BackdropSlice - BackdropHalfU, h0),
                new(BackdropSlice - BackdropHalfU, h1), new(BackdropHalfU, h1)),
            Quad(frame.Right - edge, frame.Top + edge, frame.Right, frame.Bottom - edge,
                new(BackdropSlice + BackdropHalfU, h0),
                new(2f * BackdropSlice - BackdropHalfU, h0),
                new(2f * BackdropSlice - BackdropHalfU, h1),
                new(BackdropSlice + BackdropHalfU, h1)),
            Quad(frame.Left + edge, frame.Top, frame.Right - edge, frame.Top + edge,
                new(2f * BackdropSlice + BackdropHalfU, w1),
                new(2f * BackdropSlice + BackdropHalfU, w0),
                new(3f * BackdropSlice - BackdropHalfU, w0),
                new(3f * BackdropSlice - BackdropHalfU, w1)),
            Quad(frame.Left + edge, frame.Bottom - edge, frame.Right - edge, frame.Bottom,
                new(3f * BackdropSlice + BackdropHalfU, w1),
                new(3f * BackdropSlice + BackdropHalfU, w0),
                new(4f * BackdropSlice - BackdropHalfU, w0),
                new(4f * BackdropSlice - BackdropHalfU, w1)),
            Corner(frame.Left, frame.Top, 4),
            Corner(frame.Right - edge, frame.Top, 5),
            Corner(frame.Left, frame.Bottom - edge, 6),
            Corner(frame.Right - edge, frame.Bottom - edge, 7),
        ];
    }
}

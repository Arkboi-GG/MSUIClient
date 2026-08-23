namespace MSUIClient.Engine.UI;

/// <summary>
/// Build-5875 FontString fixed-box overflow law. The box first wraps with the client's greedy
/// whitespace opportunities (force-breaking an overlong word at the last fitting Unicode scalar),
/// then the height gate backs off one scalar at a time and appends three ASCII dots until the
/// candidate fits. This belongs only at FontString call sites with an authored width and height.
/// </summary>
public static class FontStringOverflowLaw
{
    public const string Ellipsis = "...";
    public const float HeightEpsilon = .25f;
    public const float WidthEpsilon = .25f;
    public const float MinimumBoxExtent = 1f;

    public static int LinesAllowed(float boxHeight, float pitch) =>
        pitch > 0 ? Math.Max(1, (int)MathF.Ceiling((boxHeight - HeightEpsilon) / pitch)) : 1;

    public static int LinesFitting(float boxHeight, float pitch) =>
        pitch > 0 ? Math.Max(0, (int)MathF.Floor((boxHeight + HeightEpsilon) / pitch)) : 0;

    /// <summary>Returns the raw string when it fits or the longest fitting prefix plus "...".</summary>
    public static string Ellipsize(string text, float boxWidth, float boxHeight, float pitch,
        Func<string, float> measure)
    {
        if (boxWidth <= MinimumBoxExtent || boxHeight <= MinimumBoxExtent || pitch <= 0)
            return text;

        int allowed = LinesFitting(MathF.Max(boxHeight, pitch), pitch);
        int cap = allowed + 1;
        return Ellipsize(text, allowed,
            candidate => WrappedRows(candidate, boxWidth, measure, cap));
    }

    /// <summary>The pure height-gated backoff loop, with row measurement supplied by the caller.</summary>
    public static string Ellipsize(string text, int allowed, Func<string, int> rows)
    {
        if (rows(text) <= allowed) return text;

        for (int end = PreviousScalarBoundary(text, text.Length); end >= 0;
             end = PreviousScalarBoundary(text, end))
        {
            string candidate = text[..end] + Ellipsis;
            if (rows(candidate) <= allowed) return candidate;
            if (end == 0) break;
        }
        return Ellipsis;
    }

    /// <summary>
    /// Number of rows produced by the plain-text half of Benilla's FontString wrapper. A blank
    /// source line occupies one row; inter-word whitespace is measured verbatim and is discarded
    /// only at a line break. The cap is an early-out used by the ellipsis fit test.
    /// </summary>
    public static int WrappedRows(string text, float boxWidth, Func<string, float> measure,
        int cap = int.MaxValue)
    {
        if (cap <= 0) return 0;
        float width = boxWidth + WidthEpsilon;
        int rows = 0;
        foreach (string line in NormalizeLineBreaks(text).Split('\n'))
        {
            rows += WrappedSourceLineRows(line, width, measure, cap - rows);
            if (rows >= cap) return cap;
        }
        return Math.Max(1, rows);
    }

    private static int WrappedSourceLineRows(string line, float width,
        Func<string, float> measure, int cap)
    {
        if (cap <= 0) return 0;
        List<Word> words = Tokenize(line);
        if (words.Count == 0) return 1;

        int rows = 0;
        bool hasCurrent = false;
        float currentWidth = 0;
        foreach (Word source in words)
        {
            string word = source.Text;
            float wordWidth = measure(word);
            if (hasCurrent)
            {
                float candidate = currentWidth + measure(source.Lead) + wordWidth;
                if (candidate <= width)
                {
                    currentWidth = candidate;
                    continue;
                }
                if (++rows >= cap) return cap;
                hasCurrent = false;
            }

            while (wordWidth > width)
            {
                int split = LastFittingScalarBoundary(word, width, measure);
                if (split == 0)
                    return Math.Max(1, rows); // client's no-progress builder bail
                if (++rows >= cap) return cap;
                word = word[split..];
                wordWidth = measure(word);
            }
            currentWidth = wordWidth;
            hasCurrent = true;
        }

        if (hasCurrent) rows++;
        return Math.Max(1, rows);
    }

    private static List<Word> Tokenize(string line)
    {
        var words = new List<Word>();
        var lead = new System.Text.StringBuilder();
        var word = new System.Text.StringBuilder();
        foreach (char ch in line)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (word.Length > 0)
                {
                    words.Add(new Word(word.ToString(), lead.ToString()));
                    word.Clear();
                    lead.Clear();
                }
                lead.Append(ch);
            }
            else
            {
                word.Append(ch);
            }
        }
        if (word.Length > 0) words.Add(new Word(word.ToString(), lead.ToString()));
        return words;
    }

    private static int LastFittingScalarBoundary(string text, float width,
        Func<string, float> measure)
    {
        int fit = 0;
        for (int end = NextScalarBoundary(text, 0); end <= text.Length && end > 0;
             end = NextScalarBoundary(text, end))
        {
            if (measure(text[..end]) > width) break;
            fit = end;
            if (end == text.Length) break;
        }
        return fit;
    }

    private static int NextScalarBoundary(string text, int start)
    {
        if (start >= text.Length) return text.Length;
        return start + (char.IsHighSurrogate(text[start]) && start + 1 < text.Length &&
            char.IsLowSurrogate(text[start + 1]) ? 2 : 1);
    }

    private static int PreviousScalarBoundary(string text, int end)
    {
        if (end <= 0) return -1;
        int prior = end - 1;
        if (prior > 0 && char.IsLowSurrogate(text[prior]) && char.IsHighSurrogate(text[prior - 1]))
            prior--;
        return prior;
    }

    private static string NormalizeLineBreaks(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private readonly record struct Word(string Text, string Lead);
}

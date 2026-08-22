using System.Globalization;
using System.Numerics;
using System.Text;

namespace MSUIClient.Engine.UI;

public sealed class UiTextLinkInfo
{
    public required string Payload { get; init; }
    public string Markup { get; set; } = "";
}

public readonly record struct UiTextColorRun(string Text, Vector4 Color, UiTextLinkInfo? Link);
public readonly record struct UiTextMarkupLine(IReadOnlyList<UiTextColorRun> Runs)
{
    public string VisibleText => string.Concat(Runs.Select(run => run.Text));
}

/// <summary>Build-5875 FontString inline-color, line-break, escaped-pipe and hyperlink grammar.</summary>
public static class UiTextMarkupLaw
{
    public static IReadOnlyList<UiTextMarkupLine> Parse(string input, Vector4 baseColor)
    {
        var lines = new List<UiTextMarkupLine>();
        var runs = new List<UiTextColorRun>();
        var text = new StringBuilder();
        Vector4 color = baseColor;
        UiTextLinkInfo? link = null;
        StringBuilder? linkVisible = null;

        void Flush()
        {
            if (text.Length == 0) return;
            runs.Add(new(text.ToString(), color, link));
            text.Clear();
        }
        void BreakLine()
        {
            Flush();
            lines.Add(new(runs.ToArray()));
            runs.Clear();
        }
        void Push(char c)
        {
            text.Append(c);
            linkVisible?.Append(c);
        }

        input ??= "";
        for (int i = 0; i < input.Length;)
        {
            char c = input[i];
            if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < input.Length && input[i + 1] == '\n') i++;
                BreakLine();
                i++;
                continue;
            }
            if (c != '|' || i + 1 >= input.Length)
            {
                Push(c);
                i++;
                continue;
            }

            char token = input[i + 1];
            if (token == '|')
            {
                Push('|');
                i += 2;
            }
            else if (token is 'n' or 'N')
            {
                BreakLine();
                i += 2;
            }
            else if (token is 'r' or 'R')
            {
                Flush();
                color = baseColor;
                i += 2;
            }
            else if ((token is 'c' or 'C') && i + 10 <= input.Length &&
                     uint.TryParse(input.AsSpan(i + 2, 8), NumberStyles.HexNumber,
                         CultureInfo.InvariantCulture, out uint argb))
            {
                Flush();
                color = new((argb >> 16 & 0xff) / 255f, (argb >> 8 & 0xff) / 255f,
                    (argb & 0xff) / 255f, baseColor.W);
                i += 10;
            }
            else if (token == 'H')
            {
                int end = input.IndexOf("|h", i + 2, StringComparison.OrdinalIgnoreCase);
                if (end < 0) { Push('|'); i++; continue; }
                Flush();
                link = new UiTextLinkInfo { Payload = input[(i + 2)..end] };
                linkVisible = new StringBuilder();
                i = end + 2;
            }
            else if (token == 'h' && link is not null)
            {
                Flush();
                link.Markup = $"|H{link.Payload}|h{linkVisible}|h";
                link = null;
                linkVisible = null;
                i += 2;
            }
            else
            {
                // Build 5875 has no |T texture token. Unknown escapes are literal.
                Push('|');
                i++;
            }
        }
        Flush();
        lines.Add(new(runs.ToArray()));
        return lines;
    }

    public static IReadOnlyList<UiTextMarkupLine> Wrap(string input, Vector4 baseColor,
        Func<string, float> measure, float maximumWidth)
    {
        ArgumentNullException.ThrowIfNull(measure);
        var output = new List<UiTextMarkupLine>();
        foreach (UiTextMarkupLine parsed in Parse(input, baseColor))
        {
            var glyphs = new List<(char Character, Vector4 Color, UiTextLinkInfo? Link)>();
            foreach (UiTextColorRun run in parsed.Runs)
                foreach (char c in run.Text) glyphs.Add((c, run.Color, run.Link));
            if (glyphs.Count == 0) { output.Add(new([])); continue; }

            int start = 0;
            while (start < glyphs.Count)
            {
                while (start < glyphs.Count && glyphs[start].Character == ' ') start++;
                if (start >= glyphs.Count) break;
                float width = 0;
                int lastSpace = -1;
                int end = start;
                for (; end < glyphs.Count; end++)
                {
                    char ch = glyphs[end].Character;
                    float next = width + measure(ch.ToString());
                    if (end > start && next > maximumWidth) break;
                    width = next;
                    if (ch == ' ') lastSpace = end;
                }
                int takeEnd = end;
                if (end < glyphs.Count && lastSpace >= start) takeEnd = lastSpace;
                while (takeEnd > start && glyphs[takeEnd - 1].Character == ' ') takeEnd--;
                output.Add(new(BuildRuns(glyphs, start, Math.Max(start + 1, takeEnd))));
                start = end < glyphs.Count && lastSpace >= start ? lastSpace + 1 : end;
            }
        }
        return output;
    }

    private static IReadOnlyList<UiTextColorRun> BuildRuns(
        List<(char Character, Vector4 Color, UiTextLinkInfo? Link)> glyphs, int start, int end)
    {
        var result = new List<UiTextColorRun>();
        var text = new StringBuilder();
        Vector4 color = glyphs[start].Color;
        UiTextLinkInfo? link = glyphs[start].Link;
        for (int i = start; i < end; i++)
        {
            var glyph = glyphs[i];
            if (text.Length > 0 && (glyph.Color != color || !ReferenceEquals(glyph.Link, link)))
            {
                result.Add(new(text.ToString(), color, link));
                text.Clear();
                color = glyph.Color;
                link = glyph.Link;
            }
            text.Append(glyph.Character);
        }
        if (text.Length > 0) result.Add(new(text.ToString(), color, link));
        return result;
    }
}

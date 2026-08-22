using System.Globalization;
using System.Text;

namespace MSUIClient.Engine.UI;

/// <summary>
/// Build-5875 NPC/quest text substitution. The accepted token set and failure behavior are
/// transcribed from the frozen Benilla QuestTextParser closure; this is deliberately separate
/// from spell-description substitution.
/// </summary>
public static class QuestTextMacroLaw
{
    public readonly record struct Subject(string Name, string Race, string Class, byte Gender);

    public readonly record struct Expansion(string Text, bool Clean);

    public static string Expand(string text, Subject? subject,
        IReadOnlyDictionary<uint, uint>? worldStates = null) =>
        ExpandChecked(text, subject, worldStates).Text;

    /// <summary>Expand text and retain the reference driver's success bit. Chat uses the bit to
    /// defer or drop an unresolved server-authored macro rather than showing a literal '$'.</summary>
    public static Expansion ExpandChecked(string text, Subject? subject,
        IReadOnlyDictionary<uint, uint>? worldStates = null)
    {
        if (string.IsNullOrEmpty(text)) return new Expansion(text, true);
        var output = new StringBuilder(text.Length);
        bool clean = true;
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] != '$')
            {
                output.Append(text[i++]);
                continue;
            }

            int token = i + 1;
            while (token < text.Length && char.IsAsciiDigit(text[token])) token++;
            char code = token < text.Length ? text[token] : '\0';
            bool failed = false;
            switch (code)
            {
                case 'B': case 'b':
                    output.Append('\n');
                    i = token + 1;
                    break;
                case 'W': case 'w': case 'E': case 'e':
                {
                    uint id = 0;
                    if (token > i + 1)
                        uint.TryParse(text.AsSpan(i + 1, token - i - 1),
                            NumberStyles.None, CultureInfo.InvariantCulture, out id);
                    uint key = code is 'E' or 'e' ? unchecked(0u - id) : id;
                    uint raw = worldStates is not null && worldStates.TryGetValue(key, out uint value)
                        ? value : 0;
                    output.Append(unchecked((int)raw).ToString(CultureInfo.InvariantCulture));
                    i = token + 1;
                    break;
                }
                case 'N': case 'n':
                    if (subject is { } named)
                    {
                        output.Append(named.Name);
                        i = token + 1;
                    }
                    else failed = true;
                    break;
                case 'R': case 'r': case 'C': case 'c':
                    if (subject is { } typed)
                    {
                        string value = code is 'R' or 'r' ? typed.Race : typed.Class;
                        output.Append(char.IsAsciiLetterLower(code)
                            ? value.ToLowerInvariant() : value);
                        i = token + 1;
                    }
                    else failed = true;
                    break;
                case 'G': case 'g': case 'T': case 't':
                    if (subject is { } gendered)
                    {
                        int branch = token + 1;
                        while (branch < text.Length && text[branch] == ' ') branch++;
                        int colon = text.IndexOf(':', branch);
                        int earlySemi = text.IndexOf(';', branch);
                        if (colon < 0 || earlySemi >= 0 && earlySemi < colon)
                        {
                            // The reference consumes the marker and following spaces, then leaves
                            // a malformed argument as ordinary literal text.
                            i = branch;
                            break;
                        }
                        int semi = text.IndexOf(';', colon + 1);
                        if (semi < 0)
                        {
                            i = branch;
                            break;
                        }
                        ReadOnlySpan<char> first = TrimSpaces(text.AsSpan(branch, colon - branch));
                        ReadOnlySpan<char> second = TrimSpaces(text.AsSpan(colon + 1, semi - colon - 1));
                        output.Append(gendered.Gender == 0 ? first : second);
                        i = semi + 1;
                    }
                    else failed = true;
                    break;
                default:
                    failed = true;
                    break;
            }

            if (failed)
            {
                // Decimal prefixes are consumed even for an unknown/unresolved token. The dollar
                // is restored and the token letter is copied by the ordinary path next.
                output.Append('$');
                i = token;
                clean = false;
            }
        }
        return new Expansion(output.ToString(), clean);
    }

    private static ReadOnlySpan<char> TrimSpaces(ReadOnlySpan<char> value)
    {
        int start = 0, end = value.Length;
        while (start < end && value[start] == ' ') start++;
        while (end > start && value[end - 1] == ' ') end--;
        return value[start..end];
    }
}

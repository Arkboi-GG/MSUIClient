using System.Text;

namespace MSUIClient.Formats;

// Interface\GlueXML\GlueStrings.lua - the 1.12 glue text table: faction/race/class description
// paragraphs (FACTION_INFO_*, RACE_INFO_*, ABILITY_INFO_*, CLASS_*), dial labels, button captions.
// Ported byte-faithfully from benilla glue_strings.rs parse_glue_strings: plain `KEY = "value";`
// Lua assignments, one per line; \n \t \" \\ unescaped, \r dropped; every other line skipped.
// Runtime-read off the MPQ (Blizzard content, never embedded).
//
// GROUND TRUTH verified against Nico's patch.MPQ (2026-07-28): keys at column 0, single space around
// '=', double-quoted value, trailing ';'; values single-line; the info paragraphs open with 8 literal
// spaces (the ref's first-line inset past the header icon). Keys used: FACTION_INFO_ALLIANCE/_HORDE,
// RACE_INFO_{HUMAN,ORC,DWARF,NIGHTELF,SCOURGE,TAUREN,GNOME,TROLL}, ABILITY_INFO_<FILE><n> (1..),
// CLASS_{WARRIOR,PALADIN,HUNTER,ROGUE,PRIEST,SHAMAN,MAGE,WARLOCK,DRUID}.
public sealed class GlueStrings
{
    public const string MpqPath = @"Interface\GlueXML\GlueStrings.lua";

    private readonly Dictionary<string, string> _map = new(StringComparer.Ordinal);
    public int Count => _map.Count;

    /// <summary>The string for a key, or null (unknown key).</summary>
    public string? Get(string key) => _map.TryGetValue(key, out var v) ? v : null;

    /// <summary>The string for a key, falling back to a built-in caption.</summary>
    public string Text(string key, string fallback) => _map.TryGetValue(key, out var v) ? v : fallback;

    public static GlueStrings? Load(string clientDataPath)
    {
        byte[]? data = AdtTerrainReader.ReadFileFromMpqs(clientDataPath, MpqPath);
        if (data is null)
        {
            Console.WriteLine("[glue] GlueStrings.lua not found - built-in captions only");
            return null;
        }
        var gs = Parse(data);
        Console.WriteLine($"[glue] GlueStrings: {gs.Count} entries");
        return gs;
    }

    public static GlueStrings Parse(byte[] data)
    {
        var gs = new GlueStrings();
        string src = Encoding.UTF8.GetString(data);
        foreach (string line in src.Split('\n'))
        {
            int eq = line.IndexOf('=');
            if (eq < 0) continue;
            string key = line.Substring(0, eq).Trim();
            if (key.Length == 0) continue;
            bool alnum = true;
            foreach (char c in key) if (!(char.IsAsciiLetterOrDigit(c) || c == '_')) { alnum = false; break; }
            if (!alnum) continue;

            string rest = line.Substring(eq + 1).TrimStart();
            if (rest.Length == 0 || rest[0] != '"') continue;

            var sb = new StringBuilder();
            bool closed = false;
            for (int i = 1; i < rest.Length; i++)
            {
                char c = rest[i];
                if (c == '"') { closed = true; break; }
                if (c == '\\')
                {
                    if (i + 1 >= rest.Length) break;
                    char n = rest[++i];
                    if (n == 'n') sb.Append('\n');
                    else if (n == 't') sb.Append('\t');
                    else if (n == 'r') { /* dropped */ }
                    else sb.Append(n);
                }
                else sb.Append(c);
            }
            if (closed) gs._map[key] = Normalize(sb.ToString());
        }
        return gs;
    }

    // Fold the few non-ASCII typographic marks (curly quotes/apostrophes, en/em dash, ellipsis) to
    // ASCII so the ImGui glue font - which has no glyphs for them - renders clean text, not boxes.
    private static string Normalize(string s) => s
        .Replace('\u2019', '\'').Replace('\u2018', '\'')
        .Replace('\u201C', '"').Replace('\u201D', '"')
        .Replace('\u2013', '-').Replace('\u2014', '-')
        .Replace("\u2026", "...");
}

using System.Globalization;
using System.Numerics;
using System.Text;
using MSUIClient.Formats;

namespace MSUIClient.Engine.UI;

/// <summary>Resolved spell tooltip content. Rendering owns fonts/colours/geometry, not DBC math.</summary>
public readonly record struct SpellTooltipView(string Name, string Rank, string? Cost,
    string? Range, string? CastTime, string? Cooldown, string Description);

/// <summary>
/// Build-5875 spell-description token and core tooltip-line law. Unknown tokens remain visible so
/// missing coverage is diagnosable instead of silently corrupting player-facing text.
/// </summary>
public static class SpellTooltipLaw
{
    // The GameTooltip line-stack law reconstructed from build 5875: header/text font objects,
    // the engine-created line inset/gap, double-line separation, and wrapped-line ceiling.
    // Width is still content-measured; WrapWidth is a ceiling, never an unconditional frame width.
    public const float HeaderFontHeight = 14f;
    public const float TextFontHeight = 12f;
    public const float Pad = 10f;
    public const float LineGap = 2f;
    public const float DoubleGap = 40f;
    public const float WrapWidth = 260f;
    public const uint RankColor = 0xff808080; // byte-verified RGB(128,128,128), ImGui ABGR.

    public static Vector2 FrameSize(float contentWidth, float rowStackHeight, float scale) => new(
        MathF.Round(contentWidth + Pad * 2f * scale),
        MathF.Round(rowStackHeight + Pad * 2f * scale));

    public static Vector2 DefaultBottomRightOrigin(Vector2 displaySize, Vector2 frameSize,
        float scale) => new(
            displaySize.X - 13f * scale - frameSize.X,
            displaySize.Y - 70f * scale - frameSize.Y);

    public static Vector2 OwnerRightOrigin(Vector2 ownerMin, Vector2 ownerMax,
        Vector2 frameSize, Vector2 displaySize, float scale)
    {
        Vector2 position = new(ownerMax.X + 4f * scale, ownerMin.Y);
        if (position.X + frameSize.X > displaySize.X - 4f)
            position.X = ownerMin.X - frameSize.X - 4f * scale;
        return ClampOrigin(position, frameSize, displaySize);
    }

    public static Vector2 ClampOrigin(Vector2 position, Vector2 frameSize, Vector2 displaySize)
    {
        position.X = Math.Clamp(position.X, 4f, Math.Max(4f, displaySize.X - frameSize.X - 4f));
        position.Y = Math.Clamp(position.Y, 4f, Math.Max(4f, displaySize.Y - frameSize.Y - 4f));
        return position;
    }

    public static Vector2 LeftTextPosition(Vector2 origin, float y, float scale) =>
        new(origin.X + Pad * scale, y);

    public static Vector2 RightTextPosition(Vector2 origin, Vector2 frameSize, float y,
        float scale) => new(origin.X + frameSize.X - Pad * scale, y);

    public static SpellTooltipView Build(in SpellInfo spell, SpellCatalog catalog, uint casterLevel = 0)
    {
        string? cost = Cost(spell);
        string? range = Range(spell, catalog);
        bool omitCast = spell.Passive || spell.EffectIds?.FirstOrDefault() is 47 or 78;
        string? cast = omitCast ? null : spell.CastTimeMs > 0
            ? $"{Trim(spell.CastTimeMs / 1000f)} sec cast"
            : cost is null ? "Instant" : "Instant cast";
        uint recovery = Math.Max(spell.RecoveryMs, spell.CategoryRecoveryMs);
        string? cooldown = recovery == 0 ? null : recovery >= 60_000
            ? $"{Trim(recovery / 60_000f)} min cooldown"
            : $"{Trim(recovery / 1000f)} sec cooldown";
        return new SpellTooltipView(spell.Name, spell.Rank, cost, range, cast, cooldown,
            Substitute(spell.Description, spell, catalog, casterLevel));
    }

    public static string Substitute(string text, in SpellInfo spell, SpellCatalog catalog,
        uint casterLevel = 0)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains('$')) return text;
        var output = new StringBuilder(text.Length + 24);
        double lastValue = 0;
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] != '$') { output.Append(text[i++]); continue; }
            int start = i++;
            double scale = 1;
            if (i < text.Length && text[i] is '/' or '*')
            {
                char op = text[i++];
                int numberStart = i;
                while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.')) i++;
                if (double.TryParse(text[numberStart..i], NumberStyles.Float,
                        CultureInfo.InvariantCulture, out double n) && n != 0)
                {
                    if (i < text.Length && text[i] == ';') i++;
                    scale = op == '/' ? 1 / n : n;
                }
            }

            int idStart = i;
            while (i < text.Length && char.IsDigit(text[i])) i++;
            uint reference = 0;
            if (i > idStart) uint.TryParse(text[idStart..i], out reference);

            if (i < text.Length && char.ToLowerInvariant(text[i]) is 'l' or 'g')
            {
                char selector = char.ToLowerInvariant(text[i]);
                int end = text.IndexOf(';', i + 1);
                int split = end < 0 ? -1 : text.IndexOf(':', i + 1, end - i - 1);
                if (split >= 0)
                {
                    string first = text[(i + 1)..split], second = text[(split + 1)..end];
                    output.Append(selector == 'l' && Math.Abs(lastValue - 1) >= 1e-9 ? second : first);
                    i = end + 1;
                    continue;
                }
            }

            if (i >= text.Length || !char.IsLetter(text[i]))
            { output.Append(text[start..i]); continue; }
            char token = text[i++];
            int slot = 0;
            if (i < text.Length && text[i] is >= '1' and <= '3') slot = text[i++] - '1';

            SpellInfo target = spell;
            if (reference != 0 && !catalog.TryGet(reference, out target))
            { output.Append(text[start..i]); continue; }
            if (TryValue(token, slot, target, catalog, casterLevel, scale,
                    out string value, out double numeric))
            { output.Append(value); lastValue = numeric; }
            else output.Append(text[start..i]);
        }
        return output.ToString();
    }

    private static bool TryValue(char token, int slot, SpellInfo spell, SpellCatalog catalog,
        uint casterLevel, double scale, out string value, out double numeric)
    {
        value = ""; numeric = 0;
        int Scale(long v) => (int)Math.Round(v * scale, MidpointRounding.AwayFromZero);
        (long min, long max) Bounds()
        {
            long basePoints = At(spell.EffectBasePoints, slot);
            long dice = At(spell.EffectBaseDice, slot);
            long sides = At(spell.EffectDieSides, slot);
            // 0x6e3800's level leg: low ranks scale only from their SpellLevel through MaxLevel.
            // Both float products truncate before entering the integer dice calculation. This is
            // the neutral-stat level contribution; live spell-damage modifiers are a separate leg.
            uint effective = casterLevel;
            if (spell.MaxLevel != 0) effective = Math.Min(effective, spell.MaxLevel);
            long levels = Math.Max(0L, (long)effective - spell.SpellLevel);
            basePoints += (long)(At(spell.EffectRealPointsPerLevel, slot) * levels);
            sides += (long)(At(spell.EffectDicePerLevel, slot) * levels);
            return (basePoints + dice, basePoints + sides * dice);
        }
        switch (token)
        {
            case 's': case 'S':
            {
                var (lo, hi) = Bounds(); int a = Math.Abs(Scale(lo)), b = Math.Abs(Scale(hi));
                value = a == b ? a.ToString() : $"{a} to {b}"; numeric = b; return true;
            }
            case 'm':
            {
                var (lo, _) = Bounds(); int v = Math.Abs(Scale(lo));
                value = v.ToString(); numeric = v; return true;
            }
            case 'M':
            {
                var (_, hi) = Bounds(); int v = Math.Abs(Scale(hi));
                value = v.ToString(); numeric = v; return true;
            }
            case 'o': case 'O':
            {
                var (lo, hi) = Bounds(); long period = Math.Max(1, At(spell.EffectAmplitudes, slot));
                if (period == 1 && At(spell.EffectAmplitudes, slot) == 0) period = 5000;
                long duration = Math.Max(0, spell.DurationMs);
                int a = Math.Abs(Scale(Math.Abs(lo) * duration / period));
                int b = Math.Abs(Scale(Math.Abs(hi) * duration / period));
                value = a == b ? a.ToString() : $"{a} to {b}"; numeric = b; return true;
            }
            case 'd': case 'D':
                value = Duration(spell.DurationMs); numeric = Math.Max(0, spell.DurationMs / 1000d); return true;
            case 't': case 'T':
            {
                uint ms = At(spell.EffectAmplitudes, slot); if (ms == 0) ms = 5000;
                numeric = ms / 1000d; value = Trim(numeric); return true;
            }
            case 'a': case 'A':
            {
                uint index = At(spell.EffectRadiusIndices, slot);
                // The 1.12 formatter emits a numeric zero for an empty/missing radius row. Keeping
                // "$a1" visible is a useful authoring diagnostic, but it is not player-facing law.
                if (!catalog.TryGetRadius(index, out SpellRadiusRow radius))
                { numeric = 0; value = "0"; return true; }
                numeric = radius.Radius; value = Trim(numeric); return true;
            }
            case 'h': case 'H':
                numeric = spell.ProcChance; value = spell.ProcChance.ToString(); return true;
            case 'x': case 'X':
                numeric = At(spell.EffectChainTargets, slot); value = numeric.ToString("0", CultureInfo.InvariantCulture); return true;
            case 'e': case 'E':
                numeric = At(spell.EffectMultipleValues, slot); value = Trim(numeric); return true;
            default: return false;
        }
    }

    private static string? Cost(in SpellInfo spell)
    {
        if (spell.OnNextSwing) return "Next melee";
        if (spell.ManaCost > 0) return spell.PowerType switch
        {
            1 => $"{spell.ManaCost / 10} Rage",
            3 => $"{spell.ManaCost} Energy",
            _ => $"{spell.ManaCost} Mana",
        };
        return spell.ManaCostPercent > 0 ? $"{spell.ManaCostPercent}% of base mana" : null;
    }

    private static string? Range(in SpellInfo spell, SpellCatalog catalog)
    {
        if (!catalog.TryGetRange(spell.RangeIndex, out SpellRangeRow range)) return null;
        if (range.Melee) return "Melee Range";
        if (range.Max <= 0) return null;
        return range.Min > 0 ? $"{Trim(range.Min)}-{Trim(range.Max)} yd range"
            : $"{Trim(range.Max)} yd range";
    }

    private static string Duration(int milliseconds)
    {
        if (milliseconds < 0) return "until cancelled";
        int seconds = milliseconds / 1000;
        return seconds < 60 ? $"{seconds} sec" : seconds < 3600
            ? $"{seconds / 60} min" : $"{seconds / 3600} hours";
    }

    private static string Trim(double value) => Math.Abs(value - Math.Round(value)) < 1e-7
        ? Math.Round(value).ToString(CultureInfo.InvariantCulture)
        : value.ToString("0.#", CultureInfo.InvariantCulture);
    private static int At(int[]? values, int index) => values is not null && index < values.Length ? values[index] : 0;
    private static uint At(uint[]? values, int index) => values is not null && index < values.Length ? values[index] : 0;
    private static float At(float[]? values, int index) => values is not null && index < values.Length ? values[index] : 0;
}

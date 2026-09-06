using System.Globalization;
using System.Numerics;
using System.Text;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient.Engine.UI;

public delegate SpellModifierTotals SpellTooltipModifierResolver(SpellInfo spell, byte operation);

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

    public static SpellTooltipView Build(in SpellInfo spell, SpellCatalog catalog, uint casterLevel = 0,
        float castSpeedMultiplier = 1f, SpellRangeRow? rangeOverride = null, SpellTooltipModifierResolver? modifiers = null)
    {
        string? cost = Cost(spell);
        string? range = Range(spell, catalog, rangeOverride);
        bool omitCast = spell.Passive || spell.EffectIds?.FirstOrDefault() is 47 or 78;
        int castTime = SpellTimingLaw.CastTimeMilliseconds(spell, castSpeedMultiplier);
        string? cast = omitCast ? null : castTime > 0
            ? $"{Trim(castTime / 1000f)} sec cast"
            : cost is null ? "Instant" : "Instant cast";
        uint recovery = Math.Max(spell.RecoveryMs, spell.CategoryRecoveryMs);
        string? cooldown = recovery == 0 ? null : recovery >= 60_000
            ? $"{Trim(recovery / 60_000f)} min cooldown"
            : $"{Trim(recovery / 1000f)} sec cooldown";
        return new SpellTooltipView(spell.Name, spell.Rank, cost, range, cast, cooldown,
            Substitute(spell.Description, spell, catalog, casterLevel, modifiers));
    }

    public static string Substitute(string text, in SpellInfo spell, SpellCatalog catalog,
        uint casterLevel = 0, SpellTooltipModifierResolver? modifiers = null)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains('$')) return text;
        var output = new StringBuilder(text.Length + 24);
        double lastValue = 0;
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] != '$') { output.Append(text[i++]); continue; }
            int start = i++;
            double scale = ReadScale(text, ref i);

            int idStart = i;
            while (i < text.Length && char.IsDigit(text[i])) i++;
            uint reference = 0;
            if (i > idStart) uint.TryParse(text[idStart..i], out reference);
            // Both $/5;123s1 and $123/5;s1 occur in authored 5875 strings.
            if (reference != 0) scale *= ReadScale(text, ref i);

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
            if (TryValue(token, slot, target, catalog, casterLevel, scale, modifiers,
                    out string value, out double numeric))
            { output.Append(value); lastValue = numeric; }
            else output.Append(text[start..i]);
        }
        return output.ToString();
    }

    private static double ReadScale(string text, ref int index)
    {
        if (index >= text.Length || text[index] is not ('/' or '*')) return 1;
        int start = index, cursor = index + 1;
        while (cursor < text.Length && (char.IsDigit(text[cursor]) || text[cursor] == '.')) cursor++;
        if (cursor >= text.Length || text[cursor] != ';' ||
            !double.TryParse(text[(start + 1)..cursor], NumberStyles.Float,
                CultureInfo.InvariantCulture, out double factor) || !double.IsFinite(factor) ||
            (text[start] == '/' && factor == 0)) return 1;
        index = cursor + 1;
        return text[start] == '/' ? 1 / factor : factor;
    }

    private static bool TryValue(char token, int slot, SpellInfo spell, SpellCatalog catalog,
        uint casterLevel, double scale, SpellTooltipModifierResolver? modifiers, out string value, out double numeric)
    {
        value = ""; numeric = 0;
        SpellModifierTotals Mod(byte operation) => modifiers?.Invoke(spell, operation) ?? default;
        int ModifiedDuration() => spell.DurationMs < 0 ? spell.DurationMs :
            (int)Math.Clamp(Mod(1).ApplyInteger(spell.DurationMs), 0, int.MaxValue);
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
                long duration = Math.Max(0, ModifiedDuration());
                int a = Math.Abs(Scale(Math.Abs(lo) * duration / period));
                int b = Math.Abs(Scale(Math.Abs(hi) * duration / period));
                value = a == b ? a.ToString() : $"{a} to {b}"; numeric = b; return true;
            }
            case 'd': case 'D':
                value = Duration(ModifiedDuration()); numeric = Math.Max(0, ModifiedDuration() / 1000d); return true;
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
                numeric = Math.Max(0, Mod(SpellModifierStore.Radius).ApplyFloat(radius.Radius)); value = Trim(numeric); return true;
            }
            case 'h': case 'H':
                numeric = spell.ProcChance; value = spell.ProcChance.ToString(); return true;
            case 'x': case 'X':
                numeric = Math.Clamp(Mod(17).ApplyInteger(At(spell.EffectChainTargets, slot)), 0, uint.MaxValue); value = numeric.ToString("0", CultureInfo.InvariantCulture); return true;
            case 'e': case 'E':
                numeric = Mod(27).ApplyFloat(At(spell.EffectMultipleValues, slot)); value = Trim(numeric); return true;
            // Build-5875 authored descriptions use these independent fields: charges are not
            // aura stacks, target count is not chain count, and damage multiplier is not the
            // effect's multiple-value field. Cross-spell references share the same decoder.
            case 'n': case 'N':
                numeric = Math.Clamp(Mod(4).ApplyInteger(spell.ProcCharges), 0, uint.MaxValue) * scale; value = Trim(numeric); return true;
            case 'u': case 'U':
                numeric = spell.StackAmount * scale; value = Trim(numeric); return true;
            case 'v': case 'V':
                numeric = spell.MaxTargetLevel * scale; value = Trim(numeric); return true;
            case 'i': case 'I':
                numeric = spell.MaxAffectedTargets * scale; value = Trim(numeric); return true;
            case 'q': case 'Q':
                numeric = At(spell.EffectMiscValues, slot) * scale; value = Trim(numeric); return true;
            case 'b': case 'B':
                numeric = At(spell.EffectPointsPerComboPoint, slot) * scale; value = Trim(numeric); return true;
            case 'f': case 'F':
                numeric = At(spell.EffectDamageMultipliers, slot) * scale; value = Trim(numeric); return true;
            case 'r': case 'R':
                if (!catalog.TryGetRange(spell.RangeIndex, out SpellRangeRow range)) return false;
                numeric = (range.Melee || range.Max <= 0 ? range.Max : Math.Max(0, Mod(SpellModifierStore.Range).ApplyFloat(range.Max))) * scale; value = Trim(numeric); return true;
            default: return false;
        }
    }

    private static string? Cost(in SpellInfo spell)
    {
        if (spell.OnNextSwing) return "Next melee";
        if (spell.UsesAllPower) return spell.PowerType switch
        {
            0 => "Uses 100% mana", 1 => "All Rage", 2 => "All Focus", 3 => "All Energy",
            4 => "All Happiness", SpellResourceLaw.HealthPower => "All Health", _ => null,
        };
        if (spell.ManaCost > 0) return spell.PowerType switch
        {
            1 => $"{spell.ManaCost / 10} Rage",
            3 => $"{spell.ManaCost} Energy",
            SpellResourceLaw.HealthPower => $"{spell.ManaCost} Health",
            _ => $"{spell.ManaCost} Mana",
        };
        return spell.ManaCostPercent > 0
            ? $"{spell.ManaCostPercent}% of base {(spell.PowerType == SpellResourceLaw.HealthPower ? "health" : "mana")}"
            : null;
    }

    private static string? Range(in SpellInfo spell, SpellCatalog catalog, SpellRangeRow? rangeOverride)
    {
        SpellRangeRow range;
        if (rangeOverride is { } overridden) range = overridden;
        else if (!catalog.TryGetRange(spell.RangeIndex, out range)) return null;
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

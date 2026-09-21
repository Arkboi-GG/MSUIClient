namespace MSUIClient.Formats;

// ─────────────────────────────────────────────────────────────────────────────
// Palette-swap math for spell textures: rotate every pixel's hue toward a
// target color while preserving its luminance, saturation spread and alpha.
// This recolors BLPs whose look lives in the TEXTURE (where the emitter-color
// hue dials do nothing, e.g. Blizzard's authored icy art). Shared by the live
// preview (particle + mesh renderers tint on decode) and the creator export
// (recolored BLPs written into the patch MPQ).
// ─────────────────────────────────────────────────────────────────────────────
public static class BlpRecolor
{
    /// <summary>Below this HSL saturation a pixel is "grey" (white, grey, black) and is
    /// tinted by MULTIPLICATION rather than by hue-mapping. Hue-mapping keeps lightness, and
    /// at lightness 1 there is no room for a hue at all - HSL(any hue, any saturation, 1) is
    /// white - so a pure-white glow, and every sketch image (white shape, alpha edge), came
    /// out of "Tint" exactly as white as it went in. The "+0.25 saturation" the old law added
    /// only ever showed on mid greys. Multiplying by the target (normalised so its brightest
    /// channel is 1) keeps the pixel's brightness and gives it the target's full hue: white
    /// becomes the target, mid grey becomes a darker target, black stays black - which is what
    /// "tint" means everywhere else.</summary>
    private const float GreySaturation = 0.08f;

    /// <summary>Hue-map a decoded BGRA pixel buffer in place toward
    /// <paramref name="targetRgb"/> (0x00RRGGBB).</summary>
    public static void HueMapBgra(byte[] bgra, uint targetRgb)
    {
        RgbToHsl((byte)(targetRgb >> 16), (byte)(targetRgb >> 8), (byte)targetRgb,
            out float targetHue, out _, out _);
        (float mr, float mg, float mb) = MultiplyTarget(targetRgb);
        for (int i = 0; i + 3 < bgra.Length; i += 4)
        {
            byte b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
            RgbToHsl(r, g, b, out _, out float s, out float l);
            byte nr, ng, nb;
            if (s < GreySaturation)
            {
                // Grey (incl. white and black): take the target's hue at this brightness.
                nr = (byte)Math.Clamp(r * mr + 0.5f, 0f, 255f);
                ng = (byte)Math.Clamp(g * mg + 0.5f, 0f, 255f);
                nb = (byte)Math.Clamp(b * mb + 0.5f, 0f, 255f);
            }
            else
            {
                // Coloured pixels keep their own saturation and lightness (bright cores
                // stay bright, dark swirls stay dark); only the hue turns.
                HslToRgb(targetHue, s, l, out nr, out ng, out nb);
            }
            bgra[i] = nb; bgra[i + 1] = ng; bgra[i + 2] = nr;   // alpha untouched
        }
    }

    /// <summary>Hue-map a single 0..1 RGB color (e.g. an authored ribbon color
    /// track sample) toward the target, preserving its luminance. Same law as
    /// <see cref="HueMapBgra"/>: a grey sample is multiplied, a coloured one turns.</summary>
    public static System.Numerics.Vector3 HueMapColor(System.Numerics.Vector3 rgb, uint targetRgb)
    {
        byte r = (byte)Math.Clamp(rgb.X * 255f + 0.5f, 0f, 255f);
        byte g = (byte)Math.Clamp(rgb.Y * 255f + 0.5f, 0f, 255f);
        byte b = (byte)Math.Clamp(rgb.Z * 255f + 0.5f, 0f, 255f);
        RgbToHsl(r, g, b, out _, out float s, out float l);
        if (s < GreySaturation)
        {
            (float mr, float mg, float mb) = MultiplyTarget(targetRgb);
            return new System.Numerics.Vector3(
                Math.Clamp(rgb.X * mr, 0f, 1f), Math.Clamp(rgb.Y * mg, 0f, 1f), Math.Clamp(rgb.Z * mb, 0f, 1f));
        }
        RgbToHsl((byte)(targetRgb >> 16), (byte)(targetRgb >> 8), (byte)targetRgb,
            out float targetHue, out _, out _);
        HslToRgb(targetHue, s, l, out byte nr, out byte ng, out byte nb);
        return new System.Numerics.Vector3(nr / 255f, ng / 255f, nb / 255f);
    }

    /// <summary>The target as a per-channel multiplier whose brightest channel is exactly 1,
    /// so multiplying a pixel by it never darkens the pixel's brightest channel. A black
    /// target has no hue to give and leaves the pixel alone (multiplier 1,1,1).</summary>
    private static (float R, float G, float B) MultiplyTarget(uint targetRgb)
    {
        float r = ((targetRgb >> 16) & 0xFF) / 255f;
        float g = ((targetRgb >> 8) & 0xFF) / 255f;
        float b = (targetRgb & 0xFF) / 255f;
        float max = MathF.Max(r, MathF.Max(g, b));
        return max < 0.001f ? (1f, 1f, 1f) : (r / max, g / max, b / max);
    }

    private static void RgbToHsl(byte r, byte g, byte b, out float h, out float s, out float l)
    {
        float rf = r / 255f, gf = g / 255f, bf = b / 255f;
        float max = MathF.Max(rf, MathF.Max(gf, bf));
        float min = MathF.Min(rf, MathF.Min(gf, bf));
        float delta = max - min;
        l = (max + min) / 2f;
        if (delta < 0.0001f) { h = 0f; s = 0f; return; }
        s = l > 0.5f ? delta / (2f - max - min) : delta / (max + min);
        if (max == rf) h = ((gf - bf) / delta + (gf < bf ? 6f : 0f)) / 6f;
        else if (max == gf) h = ((bf - rf) / delta + 2f) / 6f;
        else h = ((rf - gf) / delta + 4f) / 6f;
    }

    private static void HslToRgb(float h, float s, float l, out byte r, out byte g, out byte b)
    {
        if (s < 0.0001f)
        {
            byte v = (byte)Math.Clamp(l * 255f + 0.5f, 0f, 255f);
            r = g = b = v;
            return;
        }
        float q = l < 0.5f ? l * (1f + s) : l + s - l * s;
        float p = 2f * l - q;
        r = (byte)Math.Clamp(Channel(p, q, h + 1f / 3f) * 255f + 0.5f, 0f, 255f);
        g = (byte)Math.Clamp(Channel(p, q, h) * 255f + 0.5f, 0f, 255f);
        b = (byte)Math.Clamp(Channel(p, q, h - 1f / 3f) * 255f + 0.5f, 0f, 255f);
    }

    private static float Channel(float p, float q, float t)
    {
        if (t < 0f) t += 1f;
        if (t > 1f) t -= 1f;
        if (t < 1f / 6f) return p + (q - p) * 6f * t;
        if (t < 1f / 2f) return q;
        if (t < 2f / 3f) return p + (q - p) * (2f / 3f - t) * 6f;
        return p;
    }
}

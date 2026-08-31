using System.Runtime.InteropServices;
using SkiaSharp;

namespace MSUIClient.Engine.UI;

/// <summary>
/// Rasterises quest-helper punctuation from the caller's actual FRIZQT font bytes.
///
/// The helper is deliberately stateless. Each call owns every Skia object it creates, so two
/// worker threads may render independently and there is no cached typeface that can be disposed
/// while another call is using it. <see cref="SKData.CreateCopy(byte[])"/> also keeps
/// the native font source independent of the MPQ byte-array's later lifetime or mutation.
/// </summary>
public static class QuestHelperFontGlyphRasterizer
{
    public const int Width = 64;
    public const int Height = 64;

    private const float ReferenceEm = 64f;
    // The stroke is centred on the glyph edge and the fill redraw covers its inner half, so a
    // 12px stroke produces the quest-marker's approximately 6px visible black border per side.
    private const float OutlineWidth = 12f;
    private const float TransparentPadding = 2f;

    private static readonly SKColor Outline = new(35, 31, 32, 255);
    private static readonly SKColor GoldTop = new(255, 242, 57, 255);
    private static readonly SKColor GoldMiddle = new(255, 218, 21, 255);
    private static readonly SKColor GoldBottom = new(249, 161, 26, 255);

    /// <summary>
    /// Return a new top-left-origin, unpremultiplied 64x64 BGRA marker for <c>!</c> or <c>?</c>.
    /// The supplied bytes are expected to be <c>Fonts\FRIZQT__.TTF</c> read from the mounted MPQs.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="punctuation"/> is not <c>!</c> or <c>?</c>.
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// <paramref name="fontBytes"/> is empty, invalid, or does not contain the requested glyph.
    /// </exception>
    public static byte[] Rasterize(byte[] fontBytes, char punctuation)
    {
        ArgumentNullException.ThrowIfNull(fontBytes);
        if (punctuation is not ('!' or '?'))
            throw new ArgumentOutOfRangeException(nameof(punctuation), punctuation,
                "Quest-helper font art supports only '!' and '?'.");
        if (fontBytes.Length == 0)
            throw new InvalidDataException("The quest-helper font data is empty.");

        // FromStream transfers ownership and must not be paired with a caller-owned using stream.
        // FromData plus an explicit copy has simpler ownership: keep both objects alive together,
        // then dispose the typeface before its backing SKData.
        using SKData data = SKData.CreateCopy(fontBytes);
        using SKTypeface? typeface = SKTypeface.FromData(data, 0);
        if (typeface is null)
            throw new InvalidDataException("The quest-helper font data is not a valid typeface.");

        using var font = new SKFont(typeface, ReferenceEm, 1f, 0f)
        {
            Edging = SKFontEdging.Antialias,
            Hinting = SKFontHinting.None,
            LinearMetrics = true,
            Subpixel = false,
        };

        ushort glyph = font.GetGlyph(punctuation);
        if (glyph == 0)
            throw new InvalidDataException(
                $"The quest-helper font does not contain the '{punctuation}' glyph.");

        using SKPath? glyphPath = font.GetGlyphPath(glyph);
        if (glyphPath is null || !glyphPath.GetTightBounds(out SKRect glyphBounds) ||
            glyphBounds.Width <= 0f || glyphBounds.Height <= 0f)
            throw new InvalidDataException(
                $"The quest-helper font has no drawable '{punctuation}' outline.");

        // Leave enough room for half of the centred stroke plus two fully transparent pixels.
        // Uniform fitting preserves FRIZQT's authored punctuation proportions.
        float inset = TransparentPadding + OutlineWidth * .5f;
        float availableWidth = Width - inset * 2f;
        float availableHeight = Height - inset * 2f;
        float scale = MathF.Min(
            availableWidth / glyphBounds.Width,
            availableHeight / glyphBounds.Height);
        // Questie's painted exclamation uses FRIZQT's contour with a faux-bold horizontal
        // expansion. Preserve the authentic path but match that heavier map-marker silhouette.
        float horizontalScale = punctuation == '!' ? 1.4f : 1f;
        float scaledWidth = glyphBounds.Width * scale * horizontalScale;
        float left = (Width - scaledWidth) * .5f;
        float top = (Height - glyphBounds.Height * scale) * .5f;
        var transform = SKMatrix.CreateScaleTranslation(
            scale * horizontalScale, scale,
            left - glyphBounds.Left * scale * horizontalScale,
            top - glyphBounds.Top * scale);
        glyphPath.Transform(transform);

        using var bitmap = new SKBitmap(
            new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);

        using var outline = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.StrokeAndFill,
            StrokeWidth = OutlineWidth,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
            Color = Outline,
        };
        using var fill = new SKPaint
        {
            IsAntialias = true,
            // A small centred stroke is the clean-room faux-bold pass: 4px on ! and 3px on ?
            // expands the gold by 2/1.5px per side without eating the charcoal rim.
            Style = SKPaintStyle.StrokeAndFill,
            StrokeWidth = punctuation == '!' ? 4f : 3f,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(0f, inset), new SKPoint(0f, Height - inset),
                [GoldTop, GoldMiddle, GoldBottom],
                [0f, .48f, 1f], SKShaderTileMode.Clamp),
        };
        using var brightDot = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.StrokeAndFill,
            StrokeWidth = fill.StrokeWidth,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
            // The reference dot is its own little painted bead: pale through the centre, with
            // warm orange along its lower edge. A separate short ramp keeps that treatment from
            // inheriting the much taller stem/hook gradient.
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(0f, 47f), new SKPoint(0f, 61f),
                [GoldTop, GoldMiddle, GoldBottom],
                [0f, .52f, 1f], SKShaderTileMode.Clamp),
        };

        canvas.DrawPath(glyphPath, outline);
        canvas.DrawPath(glyphPath, fill);
        // FRIZQT's dot is a separate bottom contour. Repaint only that band so the dot keeps the
        // reference marker's bright centre and orange lower rim instead of inheriting the stem's
        // much longer gradient.
        canvas.Save();
        canvas.ClipRect(new SKRect(0f, 46f, Width, Height));
        canvas.DrawPath(glyphPath, brightDot);
        canvas.Restore();
        canvas.Flush();

        byte[] pixels = new byte[checked(Width * Height * 4)];
        Marshal.Copy(bitmap.GetPixels(), pixels, 0, pixels.Length);
        return pixels;
    }
}

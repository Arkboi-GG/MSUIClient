using System.Runtime.InteropServices;
using SkiaSharp;

namespace MSUIClient.Engine.UI;

/// <summary>The three pieces of procedural art used by the native quest helper.</summary>
public enum QuestHelperMarkerArtKind
{
    Sack,
    Exclamation,
    Question,
}

/// <summary>
/// Produces original quest-helper marker art. Objective sacks are clean-room painted paths;
/// punctuation is rasterised from the caller's actual client FRIZQT bytes rather than borrowed
/// from an addon, so it is both Warcraft-authentic and deterministic.
/// </summary>
public static class QuestHelperMarkerArt
{
    public const int Size = 64;
    public const int Width = 64;
    public const int Height = 64;

    // Quest-helper punctuation is painted art, not ordinary UI text. A charcoal outline and
    // warm vertical gold ramp reproduce that visual language while keeping these shapes original.
    private static readonly SKColor Outline = new(35, 31, 32, 255);
    private static readonly SKColor DeepBrown = new(62, 52, 37, 255);
    private static readonly SKColor SackGold = new(145, 109, 67, 255);
    private static readonly SKColor SackLight = new(224, 180, 108, 255);
    private static readonly SKColor BrightGold = new(255, 215, 20, 255);
    private static readonly SKColor WarmGold = new(238, 154, 5, 255);
    private static readonly SKColor Highlight = new(255, 238, 108, 235);

    /// <summary>Return a new top-left-origin, unpremultiplied 64x64 BGRA buffer.</summary>
    public static byte[] Rasterize(QuestHelperMarkerArtKind kind, byte[]? frizQt = null)
    {
        if (frizQt is { Length: > 0 } &&
            kind is QuestHelperMarkerArtKind.Exclamation or QuestHelperMarkerArtKind.Question)
            return QuestHelperFontGlyphRasterizer.Rasterize(frizQt,
                kind == QuestHelperMarkerArtKind.Exclamation ? '!' : '?');

        using var bitmap = new SKBitmap(
            new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);

        switch (kind)
        {
            case QuestHelperMarkerArtKind.Sack:
                DrawSack(canvas);
                break;
            case QuestHelperMarkerArtKind.Exclamation:
                DrawExclamation(canvas);
                break;
            case QuestHelperMarkerArtKind.Question:
                DrawQuestion(canvas);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }

        canvas.Flush();
        byte[] pixels = new byte[checked(Width * Height * 4)];
        Marshal.Copy(bitmap.GetPixels(), pixels, 0, pixels.Length);
        return pixels;
    }

    /// <summary>Map the five gameplay pin states onto the three visual markers.</summary>
    public static byte[] RenderBgra(QuestHelperPinKind kind, byte[]? frizQt = null) =>
        Rasterize(kind switch
    {
        QuestHelperPinKind.Available => QuestHelperMarkerArtKind.Exclamation,
        QuestHelperPinKind.TurnIn => QuestHelperMarkerArtKind.Question,
        _ => QuestHelperMarkerArtKind.Sack,
    }, frizQt);

    public static byte[] Rasterize(QuestHelperPinKind kind, byte[]? frizQt = null) =>
        RenderBgra(kind, frizQt);

    private static void DrawSack(SKCanvas canvas)
    {
        // A narrow, rumpled pouch with an off-centre neck. Broad symmetry reads as a pumpkin at
        // map size; the diagonal tie, pale left fold, and weighted dark side make this one sack.
        using var body = new SKPath();
        body.MoveTo(24f, 24f);
        body.CubicTo(18f, 28f, 13f, 36f, 12.5f, 44f);
        body.CubicTo(12f, 51f, 16f, 55f, 23f, 55f);
        body.LineTo(38f, 55f);
        body.CubicTo(44f, 54f, 47f, 51f, 48.5f, 47f);
        body.CubicTo(50f, 39f, 45f, 31f, 38f, 25f);
        body.CubicTo(33f, 27f, 28f, 27f, 24f, 24f);
        body.Close();

        using var sackFill = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(11f, 34f), new SKPoint(51f, 45f),
                [SackLight, SackGold, DeepBrown],
                [0f, .34f, 1f], SKShaderTileMode.Clamp),
        };
        using var outerStroke = RoundedStroke(Outline, 5f);
        canvas.DrawPath(body, outerStroke);
        canvas.DrawPath(body, sackFill);

        // Weight the far side and floor contact so the pouch remains a brown sack after being
        // reduced to 10-13px, rather than collapsing into a pale speck.
        using var bodyShadow = new SKPath();
        bodyShadow.MoveTo(38f, 27f);
        bodyShadow.CubicTo(47f, 34f, 51f, 43f, 47f, 49f);
        bodyShadow.CubicTo(44f, 54f, 37f, 55f, 29f, 55f);
        bodyShadow.CubicTo(36f, 49f, 39f, 39f, 38f, 27f);
        bodyShadow.Close();
        using var shadowPaint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
            Shader = SKShader.CreateLinearGradient(new SKPoint(32f, 32f),
                new SKPoint(47f, 52f),
                [new SKColor(74, 60, 41, 40), new SKColor(43, 38, 29, 220)],
                SKShaderTileMode.Clamp),
        };
        canvas.DrawPath(bodyShadow, shadowPaint);

        // Muted red cloth caught in the knot, offset to the dark side of the pouch.
        using var ribbon = new SKPath();
        ribbon.MoveTo(32f, 14f);
        ribbon.CubicTo(39f, 13f, 46f, 17f, 48f, 22f);
        ribbon.LineTo(40f, 26f);
        ribbon.CubicTo(37f, 22f, 34f, 20f, 30f, 19f);
        ribbon.Close();
        using var ribbonStroke = RoundedStroke(Outline, 4f);
        using var ribbonFill = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
            Shader = SKShader.CreateLinearGradient(new SKPoint(31f, 15f),
                new SKPoint(47f, 24f),
                [new SKColor(143, 56, 41), new SKColor(84, 34, 29)],
                SKShaderTileMode.Clamp),
        };
        canvas.DrawPath(ribbon, ribbonStroke);
        canvas.DrawPath(ribbon, ribbonFill);

        // The gathered mouth leans left instead of forming a centred cartoon bow.
        using var neck = new SKPath();
        neck.MoveTo(23f, 24f);
        neck.CubicTo(26f, 20f, 23f, 17f, 24f, 14f);
        neck.CubicTo(25f, 12f, 29f, 10f, 31f, 9f);
        neck.LineTo(37f, 12f);
        neck.CubicTo(34f, 15f, 34f, 18f, 39f, 21f);
        neck.CubicTo(34f, 24f, 28f, 25f, 23f, 24f);
        neck.Close();
        using var neckFill = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
            Shader = SKShader.CreateLinearGradient(new SKPoint(25f, 6f),
                new SKPoint(38f, 24f), [new SKColor(249, 221, 157), SackGold],
                SKShaderTileMode.Clamp),
        };
        canvas.DrawPath(neck, outerStroke);
        canvas.DrawPath(neck, neckFill);

        using var cordOutline = RoundedStroke(Outline, 5f);
        using var cord = RoundedStroke(new SKColor(218, 175, 101, 255), 2f);
        canvas.DrawLine(21.5f, 27f, 40f, 19.5f, cordOutline);
        canvas.DrawLine(21.5f, 27f, 40f, 19.5f, cord);

        // A broad cream fold and two restrained creases carry the painted, old-world material.
        using var paleFold = new SKPath();
        paleFold.MoveTo(22f, 29f);
        paleFold.CubicTo(15f, 35f, 14f, 46f, 20f, 53f);
        paleFold.CubicTo(19f, 44f, 23f, 34f, 29f, 28f);
        paleFold.Close();
        using var palePaint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
            Color = new SKColor(238, 196, 126, 165),
        };
        canvas.DrawPath(paleFold, palePaint);

        using var crease = RoundedStroke(new SKColor(64, 54, 38, 205), 2f);
        using var gleam = RoundedStroke(new SKColor(241, 200, 128, 190), 1.5f);
        DrawCubic(canvas, crease,
            p0: new SKPoint(31f, 30f), p1: new SKPoint(27f, 38f),
            p2: new SKPoint(28f, 47f), p3: new SKPoint(32f, 53f));
        DrawCubic(canvas, crease,
            p0: new SKPoint(42f, 31f), p1: new SKPoint(47f, 39f),
            p2: new SKPoint(45f, 49f), p3: new SKPoint(39f, 54f));
        DrawCubic(canvas, gleam,
            p0: new SKPoint(23f, 30f), p1: new SKPoint(20f, 37f),
            p2: new SKPoint(21f, 43f), p3: new SKPoint(24f, 46f));
    }

    private static void DrawExclamation(SKCanvas canvas)
    {
        // Broad, round-shouldered and sharply tapered: at map size this reads like the familiar
        // painted quest marker rather than a typeset punctuation glyph.
        using var body = new SKPath();
        body.MoveTo(23f, 4f);
        body.CubicTo(28f, 1.5f, 36f, 1.5f, 41f, 4f);
        body.CubicTo(45f, 6f, 47f, 10.5f, 46f, 16.5f);
        body.CubicTo(44.5f, 25f, 41.5f, 35f, 39f, 43f);
        body.CubicTo(37.5f, 48f, 26.5f, 48f, 25f, 43f);
        body.CubicTo(22.5f, 35f, 19.5f, 25f, 18f, 16.5f);
        body.CubicTo(17f, 10.5f, 19f, 6f, 23f, 4f);
        body.Close();

        using var outline = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
            Color = Outline,
        };
        canvas.DrawPath(body, outline);

        using var inner = new SKPath();
        inner.MoveTo(26f, 8f);
        inner.CubicTo(29.5f, 6.5f, 34.5f, 6.5f, 38f, 8f);
        inner.CubicTo(40.5f, 9.5f, 41.5f, 12f, 41f, 16f);
        inner.CubicTo(39.5f, 25f, 37.5f, 33.5f, 35.5f, 40.5f);
        inner.CubicTo(35f, 42.5f, 29f, 42.5f, 28.5f, 40.5f);
        inner.CubicTo(26.5f, 33.5f, 24.5f, 25f, 23f, 16f);
        inner.CubicTo(22.5f, 12f, 23.5f, 9.5f, 26f, 8f);
        inner.Close();
        using var goldFill = GoldFill(9f, 42f);
        canvas.DrawPath(inner, goldFill);

        using var dotOutline = new SKPaint { IsAntialias = true, Color = Outline };
        using var dotFill = new SKPaint { IsAntialias = true, Color = BrightGold };
        canvas.DrawCircle(32f, 54f, 8f, dotOutline);
        canvas.DrawCircle(32f, 53.5f, 5f, dotFill);

        using var glint = RoundedStroke(Highlight, 1.4f);
        canvas.DrawLine(27.2f, 11f, 26.5f, 24f, glint);
    }

    private static void DrawQuestion(SKCanvas canvas)
    {
        // A filled hook with a small counter is intentionally much heavier than a font stroke.
        // That mass is what keeps the mark recognisable after shrinking to minimap dimensions.
        using var outerHook = new SKPath();
        outerHook.MoveTo(12f, 22f);
        outerHook.CubicTo(12.5f, 9.5f, 21.5f, 2.5f, 33f, 2.5f);
        outerHook.CubicTo(45.5f, 2.5f, 53f, 10f, 52.5f, 21.5f);
        outerHook.CubicTo(52f, 31f, 46f, 35f, 40.5f, 38.5f);
        outerHook.CubicTo(36.5f, 41f, 35f, 43.5f, 35f, 48.5f);
        outerHook.LineTo(23f, 48.5f);
        outerHook.CubicTo(22.5f, 39f, 25.5f, 34.5f, 32f, 30.5f);
        outerHook.CubicTo(38f, 26.8f, 41.5f, 24.8f, 41.5f, 20.5f);
        outerHook.CubicTo(41.5f, 16f, 38.5f, 13.5f, 33.5f, 13.5f);
        outerHook.CubicTo(28.5f, 13.5f, 25.5f, 16.5f, 24.5f, 22.5f);
        outerHook.Close();
        using var outerPaint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
            Color = Outline,
        };
        canvas.DrawPath(outerHook, outerPaint);

        using var goldHook = new SKPath();
        goldHook.MoveTo(18f, 21.5f);
        goldHook.CubicTo(18.5f, 13f, 24.5f, 7.5f, 33f, 7.5f);
        goldHook.CubicTo(42f, 7.5f, 47.5f, 12.5f, 47.2f, 21f);
        goldHook.CubicTo(47f, 27.5f, 42.5f, 30.8f, 37.5f, 34f);
        goldHook.CubicTo(31.7f, 37.7f, 29f, 41f, 29f, 44f);
        goldHook.LineTo(30.5f, 44f);
        goldHook.CubicTo(31f, 38f, 33.5f, 35.5f, 38f, 32.5f);
        goldHook.CubicTo(43.5f, 28.8f, 45f, 25.5f, 45f, 20.5f);
        goldHook.CubicTo(45f, 13.5f, 40.5f, 9.5f, 33.5f, 9.5f);
        goldHook.CubicTo(26.5f, 9.5f, 22f, 14f, 21f, 21.8f);
        goldHook.Close();
        using var goldFill = GoldFill(7f, 45f);
        canvas.DrawPath(goldHook, goldFill);

        using var dotOutline = new SKPaint { IsAntialias = true, Color = Outline };
        using var dotFill = new SKPaint { IsAntialias = true, Color = BrightGold };
        canvas.DrawCircle(30f, 55f, 8f, dotOutline);
        canvas.DrawCircle(30f, 54.5f, 5f, dotFill);
    }

    private static SKPaint RoundedStroke(SKColor color, float width) => new()
    {
        IsAntialias = true,
        Style = SKPaintStyle.Stroke,
        StrokeWidth = width,
        StrokeCap = SKStrokeCap.Round,
        StrokeJoin = SKStrokeJoin.Round,
        Color = color,
    };

    private static SKPaint GoldFill(float top, float bottom) => new()
    {
        IsAntialias = true,
        Style = SKPaintStyle.Fill,
        Shader = SKShader.CreateLinearGradient(
            new SKPoint(32f, top), new SKPoint(32f, bottom),
            [Highlight, BrightGold, WarmGold],
            [0f, .45f, 1f], SKShaderTileMode.Clamp),
    };

    private static void DrawCubic(SKCanvas canvas, SKPaint paint,
        SKPoint p0, SKPoint p1, SKPoint p2, SKPoint p3)
    {
        using var path = new SKPath();
        path.MoveTo(p0);
        path.CubicTo(p1, p2, p3);
        canvas.DrawPath(path, paint);
    }
}

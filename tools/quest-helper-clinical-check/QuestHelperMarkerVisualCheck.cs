using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using SkiaSharp;

internal static class QuestHelperMarkerVisualCheck
{
    // Approved only after rendering the contact sheet at the exact client sizes below. This turns
    // a future accidental shape/antialias change into an explicit visual-review decision.
    private static readonly IReadOnlyDictionary<QuestHelperPinKind, string> ApprovedSourceHashes =
        new Dictionary<QuestHelperPinKind, string>
        {
            [QuestHelperPinKind.Loot] =
                "B4B7BFC84F794BFB8F14172440E6ED63B59530689DDC7B652FF94F98C6B25CC2",
            [QuestHelperPinKind.Available] =
                "A0BE6879248DA2499D239FC78287D922DF4EE23D6F1528550A31A968676CAD82",
            [QuestHelperPinKind.TurnIn] =
                "7B0F107BEBDD8F2F0846D51B52E1BF56E078D95515EC510CB341C4CC4C6F46BF",
        };

    private readonly record struct PixelBounds(int MinX, int MinY, int MaxX, int MaxY)
    {
        public int Width => MaxX - MinX + 1;
        public int Height => MaxY - MinY + 1;
        public bool Encloses(PixelBounds inner, int margin) =>
            MinX <= inner.MinX - margin && MinY <= inner.MinY - margin &&
            MaxX >= inner.MaxX + margin && MaxY >= inner.MaxY + margin;
    }

    private readonly record struct MarkerMetrics(
        string Marker, string Sha256, PixelBounds Alpha, PixelBounds Dark,
        PixelBounds Fill, int OpaquePixels, int DarkPixels, int FillPixels,
        int AlphaComponents);

    public static (string ProofPath, string MetricsPath) Run(
        string root, Action<bool, string> check)
    {
        check(MathF.Abs(QuestHelperUiLaw.WorldMapMarkerSize(QuestHelperPinKind.Loot) - 9.6f) < .001f &&
              MathF.Abs(QuestHelperUiLaw.WorldMapMarkerSize(QuestHelperPinKind.Available) - 11.52f) < .001f &&
              MathF.Abs(QuestHelperUiLaw.MinimapMarkerSize(QuestHelperPinKind.Loot) - 11.2f) < .001f &&
              MathF.Abs(QuestHelperUiLaw.MinimapMarkerSize(QuestHelperPinKind.TurnIn) - 13.44f) < .001f,
            "quest-helper marker sizes must stay at the reviewed Questie-compatible defaults");

        byte[] frizQt = LoadFrizQt(root);
        check(frizQt.Length > 10_000,
            "mounted client FRIZQT font is absent or unexpectedly small");
        QuestHelperPinKind[] kinds =
            [QuestHelperPinKind.Loot, QuestHelperPinKind.Available, QuestHelperPinKind.TurnIn];
        var metrics = new List<MarkerMetrics>();
        foreach (QuestHelperPinKind kind in kinds)
        {
            byte[] pixels = QuestHelperMarkerArt.RenderBgra(kind, frizQt);
            MarkerMetrics measured = Measure(kind, pixels);
            metrics.Add(measured);

            check(measured.Alpha.MinX >= 2 && measured.Alpha.MinY >= 2 &&
                  measured.Alpha.MaxX <= QuestHelperMarkerArt.Width - 3 &&
                  measured.Alpha.MaxY <= QuestHelperMarkerArt.Height - 3,
                $"{kind} marker has lost its transparent clipping margin: {measured.Alpha}");
            check(measured.OpaquePixels >= 180 && measured.DarkPixels >= 75 &&
                  measured.FillPixels >= 45,
                $"{kind} marker lost substantive outlined/fill artwork");
            check(measured.Dark.Encloses(measured.Fill, 1),
                $"{kind} marker fill is no longer enclosed by a dark outline");
            if (kind == QuestHelperPinKind.Loot)
                check(measured.AlphaComponents == 1,
                    "loot marker is no longer one complete sack silhouette");

            VerifyResampling(kind, pixels,
                QuestHelperUiLaw.WorldMapMarkerSize(kind), "world map", check);
            VerifyResampling(kind, pixels,
                QuestHelperUiLaw.WorldMapMarkerSize(kind) * 1.5f,
                "world map at 1.5x UI scale", check);
            VerifyResampling(kind, pixels,
                QuestHelperUiLaw.MinimapMarkerSize(kind), "minimap", check);
            VerifyResampling(kind, pixels,
                QuestHelperUiLaw.MinimapMarkerSize(kind) * 1.5f,
                "minimap at 1.5x UI scale", check);
        }

        string proofPath = Path.Combine(Path.GetTempPath(),
            "MSUIClient-quest-helper-marker-proof.png");
        string metricsPath = Path.Combine(Path.GetTempPath(),
            "MSUIClient-quest-helper-marker-proof.json");
        CreateProofSheet(root, kinds, frizQt, proofPath);
        File.WriteAllText(metricsPath, JsonSerializer.Serialize(metrics,
            new JsonSerializerOptions { WriteIndented = true }));
        for (int index = 0; index < kinds.Length; index++)
        {
            QuestHelperPinKind kind = kinds[index];
            check(metrics[index].Sha256 == ApprovedSourceHashes[kind],
                $"{kind} marker pixels changed without updating the reviewed contact sheet " +
                $"(expected {ApprovedSourceHashes[kind]}, got {metrics[index].Sha256})");
        }
        return (proofPath, metricsPath);
    }

    private static MarkerMetrics Measure(QuestHelperPinKind kind, byte[] pixels)
    {
        static bool Alpha(byte b, byte g, byte r, byte a) => a >= 16;
        static bool Dark(byte b, byte g, byte r, byte a) =>
            a >= 128 && (299 * r + 587 * g + 114 * b) / 1000 <= 80;
        bool Fill(byte b, byte g, byte r, byte a) => kind == QuestHelperPinKind.Loot
            ? a >= 128 && r >= 85 && g >= 35 && b <= 100 && r > g
            : a >= 128 && r >= 160 && g >= 90 && b <= 125;

        PixelBounds alpha = Bounds(pixels, Alpha, out int opaque);
        PixelBounds dark = Bounds(pixels, Dark, out int darkCount);
        PixelBounds fill = Bounds(pixels, Fill, out int fillCount);
        int components = CountAlphaComponents(pixels, 32);
        return new(kind.ToString(), Convert.ToHexString(SHA256.HashData(pixels)),
            alpha, dark, fill, opaque, darkCount, fillCount, components);
    }

    private static PixelBounds Bounds(byte[] pixels,
        Func<byte, byte, byte, byte, bool> include, out int count)
    {
        int minX = QuestHelperMarkerArt.Width, minY = QuestHelperMarkerArt.Height;
        int maxX = -1, maxY = -1;
        count = 0;
        for (int y = 0; y < QuestHelperMarkerArt.Height; y++)
        for (int x = 0; x < QuestHelperMarkerArt.Width; x++)
        {
            int p = (y * QuestHelperMarkerArt.Width + x) * 4;
            if (!include(pixels[p], pixels[p + 1], pixels[p + 2], pixels[p + 3]))
                continue;
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
            count++;
        }
        if (count == 0) throw new InvalidDataException("marker predicate selected no pixels");
        return new(minX, minY, maxX, maxY);
    }

    private static int CountAlphaComponents(byte[] pixels, byte threshold)
    {
        int width = QuestHelperMarkerArt.Width;
        int height = QuestHelperMarkerArt.Height;
        var visited = new bool[width * height];
        int components = 0;
        for (int start = 0; start < visited.Length; start++)
        {
            if (visited[start] || pixels[start * 4 + 3] < threshold) continue;
            components++;
            var queue = new Queue<int>();
            queue.Enqueue(start);
            visited[start] = true;
            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                int x = current % width;
                int y = current / width;
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || nx >= width || ny < 0 || ny >= height) continue;
                    int next = ny * width + nx;
                    if (visited[next] || pixels[next * 4 + 3] < threshold) continue;
                    visited[next] = true;
                    queue.Enqueue(next);
                }
            }
        }
        return components;
    }

    private static void VerifyResampling(QuestHelperPinKind kind, byte[] pixels,
        float destinationSize, string surface, Action<bool, string> check)
    {
        using SKBitmap source = BitmapFromBgra(pixels,
            QuestHelperMarkerArt.Width, QuestHelperMarkerArt.Height);
        using SKImage image = SKImage.FromBitmap(source);
        foreach (float phase in new[] { 0f, .25f, .5f, .75f })
        {
            const int canvasSize = 48;
            using var target = new SKBitmap(new SKImageInfo(canvasSize, canvasSize,
                SKColorType.Bgra8888, SKAlphaType.Unpremul));
            using var canvas = new SKCanvas(target);
            canvas.Clear(SKColors.Transparent);
            float left = (canvasSize - destinationSize) * .5f + phase;
            var destination = new SKRect(left, left,
                left + destinationSize, left + destinationSize);
            canvas.DrawImage(image,
                new SKRect(0, 0, source.Width, source.Height), destination,
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), null);
            canvas.Flush();

            byte[] rendered = CopyPixels(target);
            PixelBounds alpha = BoundsAtSize(rendered, canvasSize, 24, out int alphaCount);
            PixelBounds dark = BoundsAtSize(rendered, canvasSize, 96, out int darkCount,
                darkOnly: true);
            int outside = CountAlphaOutside(rendered, canvasSize, destination, 32);
            check(outside == 0,
                $"{kind} bleeds outside its {surface} quad at phase {phase:0.00}");
            check(alphaCount >= 8 && alpha.Width >= 3 && alpha.Height >= 7 && darkCount >= 2,
                $"{kind} becomes illegible at {surface} size {destinationSize:0.0}, phase {phase:0.00}");
            check(dark.Width >= 2 && dark.Height >= 4,
                $"{kind} loses its dark outline at {surface} phase {phase:0.00}");
        }
    }

    private static PixelBounds BoundsAtSize(byte[] pixels, int size, byte alphaThreshold,
        out int count, bool darkOnly = false)
    {
        int minX = size, minY = size, maxX = -1, maxY = -1;
        count = 0;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            int p = (y * size + x) * 4;
            byte b = pixels[p], g = pixels[p + 1], r = pixels[p + 2], a = pixels[p + 3];
            if (a < alphaThreshold || darkOnly &&
                (299 * r + 587 * g + 114 * b) / 1000 > 95) continue;
            minX = Math.Min(minX, x); minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y); count++;
        }
        if (count == 0) throw new InvalidDataException("resampled marker selected no pixels");
        return new(minX, minY, maxX, maxY);
    }

    private static int CountAlphaOutside(byte[] pixels, int size, SKRect quad, byte threshold)
    {
        int outside = 0;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            if (pixels[(y * size + x) * 4 + 3] < threshold) continue;
            if (x + .5f < quad.Left || x + .5f > quad.Right ||
                y + .5f < quad.Top || y + .5f > quad.Bottom) outside++;
        }
        return outside;
    }

    private static void CreateProofSheet(string root, QuestHelperPinKind[] kinds,
        byte[] frizQt, string outputPath)
    {
        using SKBitmap map = LoadMapBackground(root);
        const int cellWidth = 240, rowHeight = 165, header = 48, footer = 34;
        const int sheetWidth = cellWidth * 3;
        const int sheetHeight = header + rowHeight * 2 + footer;
        using var sheet = new SKBitmap(new SKImageInfo(sheetWidth, sheetHeight,
            SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(sheet);
        canvas.Clear(new SKColor(24, 20, 16));
        using var titleFont = new SKFont(
            SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold), 19);
        using var labelFont = new SKFont(
            SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold), 14);
        using var smallFont = new SKFont(
            SKTypeface.FromFamilyName("Arial"), 12);
        using var white = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var muted = new SKPaint { Color = new SKColor(225, 214, 190), IsAntialias = true };
        using var shade = new SKPaint { Color = new SKColor(0, 0, 0, 92) };
        using var border = new SKPaint
        {
            Color = new SKColor(25, 20, 15, 230), Style = SKPaintStyle.Stroke,
            StrokeWidth = 2, IsAntialias = true,
        };

        canvas.DrawText("Quest helper markers — exact client draw sizes", 16, 31,
            SKTextAlign.Left, titleFont, white);
        for (int row = 0; row < 2; row++)
        for (int col = 0; col < kinds.Length; col++)
        {
            QuestHelperPinKind kind = kinds[col];
            float x = col * cellWidth;
            float y = header + row * rowHeight;
            var cell = new SKRect(x, y, x + cellWidth, y + rowHeight);
            var src = new SKRect(0, 0, map.Width, map.Height);
            canvas.DrawBitmap(map, src, cell);
            canvas.DrawRect(new SKRect(x, y, x + cellWidth, y + 27), shade);
            canvas.DrawRect(cell, border);

            float markerSize = row == 0
                ? QuestHelperUiLaw.WorldMapMarkerSize(kind)
                : QuestHelperUiLaw.MinimapMarkerSize(kind);
            string surface = row == 0 ? "M map" : "Minimap";
            canvas.DrawText($"{surface}  |  {kind}  |  {markerSize:0.0}px",
                x + 9, y + 19, SKTextAlign.Left, labelFont, white);

            byte[] pixels = QuestHelperMarkerArt.RenderBgra(kind, frizQt);
            using SKBitmap marker = BitmapFromBgra(pixels,
                QuestHelperMarkerArt.Width, QuestHelperMarkerArt.Height);
            using SKImage markerImage = SKImage.FromBitmap(marker);
            float actualX = x + 58f - markerSize * .5f;
            float actualY = y + 89f - markerSize * .5f;
            canvas.DrawImage(markerImage,
                new SKRect(0, 0, marker.Width, marker.Height),
                new SKRect(actualX, actualY, actualX + markerSize, actualY + markerSize),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), null);
            canvas.DrawText("actual", x + 37, y + 130,
                SKTextAlign.Left, smallFont, muted);

            const float sourceSize = 64f;
            float sourceX = x + 147f - sourceSize * .5f;
            float sourceY = y + 89f - sourceSize * .5f;
            canvas.DrawImage(markerImage,
                new SKRect(0, 0, marker.Width, marker.Height),
                new SKRect(sourceX, sourceY, sourceX + sourceSize, sourceY + sourceSize),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), null);
            canvas.DrawText("64px source", x + 112, y + 137,
                SKTextAlign.Left, smallFont, muted);
        }
        canvas.DrawText("Full UVs • transparent margins • four subpixel phases checked • no addon art copied",
            16, sheetHeight - 11, SKTextAlign.Left, smallFont, muted);
        canvas.Flush();
        using SKImage image = SKImage.FromBitmap(sheet);
        using SKData png = image.Encode(SKEncodedImageFormat.Png, 100);
        using FileStream stream = File.Create(outputPath);
        png.SaveTo(stream);
    }

    private static SKBitmap LoadMapBackground(string root)
    {
        using var mpq = new MpqMount(Path.Combine(root, "GameData", "Data"));
        byte[] bytes = mpq.ReadFile(@"Interface\WorldMap\Azeroth\Azeroth1.blp")
            ?? throw new FileNotFoundException("world-map proof background was not found in MPQs");
        byte[] bgra = BlpDecoder.GetPixels(bytes, 0, out int width, out int height);
        return BitmapFromBgra(bgra, width, height);
    }

    private static byte[] LoadFrizQt(string root)
    {
        using var mpq = new MpqMount(Path.Combine(root, "GameData", "Data"));
        return mpq.ReadFile(FontFace.FrizQt)
            ?? throw new FileNotFoundException("Fonts\\FRIZQT__.TTF was not found in MPQs");
    }

    private static SKBitmap BitmapFromBgra(byte[] bgra, int width, int height)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height,
            SKColorType.Bgra8888, SKAlphaType.Unpremul));
        Marshal.Copy(bgra, 0, bitmap.GetPixels(), bgra.Length);
        return bitmap;
    }

    private static byte[] CopyPixels(SKBitmap bitmap)
    {
        byte[] bytes = new byte[checked(bitmap.Width * bitmap.Height * 4)];
        Marshal.Copy(bitmap.GetPixels(), bytes, 0, bytes.Length);
        return bytes;
    }
}

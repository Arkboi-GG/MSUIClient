namespace MSUIClient.Engine.UI;

public readonly record struct HardwareCursorImage(byte[] Rgba, int Width, int Height);

/// <summary>CPU preparation for Silk's non-premultiplied little-endian RGBA cursor contract.</summary>
public static class HardwareCursorLaw
{
    public static HardwareCursorImage FromBgra(byte[] bgra, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        if (width <= 0 || height <= 0 || bgra.Length != checked(width * height * 4))
            throw new ArgumentException("BGRA cursor dimensions do not match the pixel buffer.",
                nameof(bgra));

        var rgba = new byte[bgra.Length];
        for (int i = 0; i < bgra.Length; i += 4)
        {
            rgba[i] = bgra[i + 2];
            rgba[i + 1] = bgra[i + 1];
            rgba[i + 2] = bgra[i];
            rgba[i + 3] = bgra[i + 3];
        }
        return new(rgba, width, height);
    }

    /// <summary>
    /// Scale a prepared cursor without asking the GPU or window backend to resample it. Cursor
    /// art is authored as crisp UI pixels, so nearest-neighbour preserves its silhouettes and
    /// alpha edge instead of introducing a grey fringe around the OS cursor.
    /// </summary>
    public static HardwareCursorImage ResizeNearest(in HardwareCursorImage source,
        int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (source.Width == width && source.Height == height) return source;
        if (source.Width <= 0 || source.Height <= 0 ||
            source.Rgba.Length != checked(source.Width * source.Height * 4))
            throw new ArgumentException("Source cursor dimensions do not match the pixel buffer.",
                nameof(source));

        var rgba = new byte[checked(width * height * 4)];
        for (int y = 0; y < height; y++)
        {
            int sy = Math.Min(source.Height - 1, y * source.Height / height);
            for (int x = 0; x < width; x++)
            {
                int sx = Math.Min(source.Width - 1, x * source.Width / width);
                int src = (sy * source.Width + sx) * 4;
                int dst = (y * width + x) * 4;
                rgba[dst] = source.Rgba[src];
                rgba[dst + 1] = source.Rgba[src + 1];
                rgba[dst + 2] = source.Rgba[src + 2];
                rgba[dst + 3] = source.Rgba[src + 3];
            }
        }
        return new(rgba, width, height);
    }
}

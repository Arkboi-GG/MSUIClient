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
}

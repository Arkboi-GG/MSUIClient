namespace MSUIClient.Engine.UI;

/// <summary>
/// CPU-side alpha mask for icons drawn beneath circular Blizzard button chrome.
/// The original cached icon remains square; callers upload this derived copy only for
/// controls whose authored aperture is round.
/// </summary>
public static class IconApertureMask
{
    public static void ApplyCircularBgra(byte[] bgra, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        if (width <= 0 || height <= 0 || bgra.Length != checked(width * height * 4))
            throw new ArgumentException("BGRA dimensions do not match the supplied pixel buffer.", nameof(bgra));

        float centerX = (width - 1) * .5f;
        float centerY = (height - 1) * .5f;
        // Keep one transparent source texel between the feather and the authored aperture edge.
        // Without that guard, linear sampling can pull a low-alpha corner fringe outside the
        // mathematical circle even though the source texture's corner texels are transparent.
        float radius = MathF.Max(0f, MathF.Min(width, height) * .5f - 1.5f);
        for (int y = 0; y < height; y++)
        {
            float dy = y - centerY;
            for (int x = 0; x < width; x++)
            {
                float dx = x - centerX;
                float coverage = Math.Clamp(radius + .5f - MathF.Sqrt(dx * dx + dy * dy), 0f, 1f);
                int alpha = (y * width + x) * 4 + 3;
                bgra[alpha] = (byte)MathF.Round(bgra[alpha] * coverage);
            }
        }
    }
}

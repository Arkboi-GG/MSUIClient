namespace MSUIClient.Engine.UI;

/// <summary>
/// Proportional gameplay-UI scaling. The player's preference describes the appearance at MSUI's
/// 1600x900 reference window; the live framebuffer scales that appearance with the window.
/// </summary>
public static class InterfaceScaleLaw
{
    public const float Minimum = 0.5f;
    public const float Maximum = 4f;
    public const float ReferenceFramebufferWidth = 1600f;
    public const float ReferenceFramebufferHeight = 900f;
    public static float Resolve(float preference) =>
        Math.Clamp(preference, Minimum, Maximum);

    /// <summary>
    /// Preserve aspect ratio by following the limiting framebuffer dimension. Thus 1.30x remains
    /// 1.30x at 1600x900 and becomes 2.08x at 2560x1440, with no settings change.
    /// </summary>
    public static float ResolveForFramebuffer(float width, float height, float preference)
    {
        float resolvedPreference = Resolve(preference);
        if (!float.IsFinite(width) || !float.IsFinite(height) || width <= 0f || height <= 0f)
            return resolvedPreference;

        float windowRatio = MathF.Min(
            width / ReferenceFramebufferWidth,
            height / ReferenceFramebufferHeight);
        return Math.Clamp(resolvedPreference * windowRatio, Minimum, Maximum);
    }
}

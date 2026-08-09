namespace MSUIClient.Engine.UI;

/// <summary>Capture geometry around the two round bag-icon apertures.</summary>
public static class BagIconContainmentLaw
{
    public readonly record struct Geometry(float CaptureSize, float ApertureOffset, float ApertureSize);

    // UI-Quickslot2 is 66 square around a 36 square icon; the circular alpha mask is inscribed
    // in that icon, leaving 15 pixels of ring/corner context on every side of the capture.
    public static Geometry BagBar => new(66f, 15f, 36f);

    // ContainerFrame's 40px portrait sits inside a 64px proof crop with 12px of unchanged
    // title/chrome context on every side.
    public static Geometry HeaderPortrait => new(64f, 12f, 40f);
}

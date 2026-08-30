namespace MSUIClient.World.Sound;

/// <summary>Build-5875 SoundEntries per-shot pitch variation.</summary>
public static class SoundVariationLaw
{
    /// <summary>Scale one raw 32-bit draw uniformly into the reference's 0..30 range.</summary>
    public static int Draw(uint raw) => (int)(((ulong)raw * 31) >> 32);

    /// <summary>FMOD's authored absolute playback frequency: 18,742..25,357 Hz,
    /// with draw 15 producing the identity 22,050 Hz used by these SFX.</summary>
    public static uint PitchFrequency(int draw)
    {
        draw = Math.Clamp(draw, 0, 30);
        return (uint)(22_050 * (draw + 0x55) / 100);
    }

    public static uint NextPitchFrequency()
    {
        uint raw = (uint)Random.Shared.NextInt64(1L << 32);
        return PitchFrequency(Draw(raw));
    }
}

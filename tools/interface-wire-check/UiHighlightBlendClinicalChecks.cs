using MSUIClient;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;

/// <summary>
/// The 1.12 ADD-highlight encode. Every number below was measured off the shipped archives with
/// tools/mpqpeek before it was asserted here - decode the real BLP, composite it over the real
/// button art under ImGui's SRC_ALPHA blend, and compare against what alphaMode="ADD" would have
/// produced. Mean luma, un-hovered button = base:
///
///   button         base   TRUE ADD | drawn raw   old encode   this encode
///   checkbox        5.9      13.8  |       7.9          8.5         13.8
///   minimap +      34.0      48.6  |      14.6         30.3         39.3
///   scrollbar up   17.6      19.5  |       2.4         16.3         17.5
///   bag slot        3.8      25.1  |      21.3         10.0         25.1
///
/// Two things that report is worth keeping: drawn raw, the minimap and scrollbar hovers came out
/// DARKER THAN THE UN-HOVERED BUTTON (34.0 -> 14.6, 17.6 -> 2.4) - the black square as a number -
/// and the old encode still under-lit every case, delivering a third of the intended light on the
/// checkbox. Where the art sits on near-black chrome the new encode is exact.
/// </summary>
internal static class UiHighlightBlendClinicalChecks
{
    public static void Run()
    {
        CheckAddArtIsNormalised();
        CheckAlphaArtIsUntouched();
        CheckNeverDarkerThanTheOldEncode();
        CheckHeaderIsTheAuthority();
        CheckPerRegionAddArt();
    }

    /// <summary>
    /// The case the header CANNOT settle. UI-StateIcon is one 64x64 sheet holding four quadrants:
    /// the top half is the Zzz / crossed-swords icons on ordinary alpha (measured 7-8% opaque),
    /// the bottom half is their glow, which PlayerFrame.xml declares alphaMode="ADD" on
    /// PlayerRestGlow/PlayerAttackGlow and which measures 100% opaque with 71% of it flat black.
    /// The FILE says alphaDepth 8 because of the icons, so HasAlphaChannel rightly answers "not
    /// ADD art" for the sheet while being wrong about half of it. Drawn straight, that half was a
    /// hard black 32x32 square centred on (53,66) - the player frame's level number. Composited
    /// over the frame plate, mean luma went 76.5 -> 18.5 on hover-free REST: a 76% darkening.
    /// </summary>
    private static void CheckPerRegionAddArt()
    {
        string root = ClientConfig.FindRepoRoot();
        string art = SourceText.Read(Path.Combine(root, "MSUIClient", "Engine", "UI",
            "GameplayArt.cs"));
        Check(art.Contains("public uint AdditiveRegionHandle(string path, Vector2 uvMin, Vector2 uvMax)",
                  StringComparison.Ordinal) &&
              art.Contains("UiHighlightBlendLaw.EncodeAdditive(", StringComparison.Ordinal),
            "the per-region ADD encode is gone - a sheet that is ADD in only one half cannot be drawn");

        string frames = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Hud",
            "GameLoop.UnitFrames.cs"));
        Check(frames.Contains("AdditiveRegionHandle(", StringComparison.Ordinal) &&
              frames.Contains("new Vector2(0f, 0.5f), Vector2.One", StringComparison.Ordinal),
            "UI-StateIcon's ADD glow half is being drawn straight again - the black square is back");

        // The region encode must leave everything OUTSIDE the rect byte-identical, or the icon
        // half of that same sheet shifts along with the glow half.
        byte[] sheet = new byte[4 * 4 * 4];
        for (int i = 0; i < sheet.Length; i += 4)
        {
            sheet[i] = 20; sheet[i + 1] = 40; sheet[i + 2] = 60; sheet[i + 3] = 200;
        }
        byte[] expected = (byte[])sheet.Clone();
        for (int y = 2; y < 4; y++)
            UiHighlightBlendLaw.EncodeAdditive(expected.AsSpan((y * 4) * 4, 4 * 4), addArt: true);
        for (int y = 0; y < 2; y++)
        for (int x = 0; x < 4; x++)
        {
            int i = (y * 4 + x) * 4;
            Check(expected[i] == 20 && expected[i + 1] == 40 && expected[i + 2] == 60 &&
                  expected[i + 3] == 200,
                "the region encode reached outside its rectangle");
        }
        Check(expected[(2 * 4) * 4 + 2] == 255 && expected[(2 * 4) * 4 + 3] == 47,
            "the region encode did not apply inside its rectangle");
    }

    /// <summary>
    /// ADD art: colour goes to full brightness with its HUE INTACT, magnitude moves into alpha.
    /// UI-CheckBox-Highlight's own peak texel (24, 40, 99) is the case the report named.
    /// </summary>
    private static void CheckAddArtIsNormalised()
    {
        byte[] bgra = [24, 40, 99, 255, 0, 0, 0, 255, 255, 255, 255, 255];
        UiHighlightBlendLaw.EncodeAdditive(bgra, addArt: true);

        Check(bgra[0] == 61 && bgra[1] == 103 && bgra[2] == 255 && bgra[3] == 99,
            "ADD art no longer normalises to full brightness - dim glows will darken again");
        Check(bgra[2] == 255 && bgra[0] < bgra[1] && bgra[1] < bgra[2],
            "the normalised texel lost its hue - that is the white-mask encode, not this one");
        Check(bgra[7] == 0,
            "a pure black ADD texel is no longer transparent - the surround paints a black square");
        Check(bgra[8] == 255 && bgra[9] == 255 && bgra[10] == 255 && bgra[11] == 255,
            "an already-full-brightness texel moved - the correction must scale with the defect");
    }

    /// <summary>
    /// Art WITH an alpha channel is ordinary alpha/alphakey art carrying its own coverage mask.
    /// It must come out of the encode exactly as the pre-fix client produced it, or every glow
    /// that was already correct shifts.
    /// </summary>
    private static void CheckAlphaArtIsUntouched()
    {
        byte[] bgra = [24, 40, 99, 200, 10, 20, 30, 255];
        byte[] expected = [24, 40, 99, (byte)(200 * 99 / 255), 10, 20, 30, (byte)(255 * 30 / 255)];
        UiHighlightBlendLaw.EncodeAdditive(bgra, addArt: false);
        Check(bgra.SequenceEqual(expected),
            "alpha-channel art is being normalised - only headerless ADD art may be");
    }

    /// <summary>
    /// The property that actually fixes the bug: under ImGui's SRC_ALPHA blend, over any
    /// destination, the new encode delivers at least as much light as the old one for every
    /// texel. Swept across the whole colour cube, not spot-checked.
    /// </summary>
    private static void CheckNeverDarkerThanTheOldEncode()
    {
        foreach (int dst in new[] { 0, 32, 96, 160, 255 })
        for (int r = 0; r <= 255; r += 15)
        for (int g = 0; g <= 255; g += 15)
        for (int b = 0; b <= 255; b += 15)
        {
            byte[] now = [(byte)b, (byte)g, (byte)r, 255];
            byte[] old = [(byte)b, (byte)g, (byte)r, 255];
            UiHighlightBlendLaw.EncodeAdditive(now, addArt: true);
            OldEncode(old);
            Check(Blend(now, dst) >= Blend(old, dst) - 1,
                $"the ADD encode darkened ({r},{g},{b}) over {dst} - the black square is back");
        }

        // ...and the darkest real highlight in the client gains most. UI-PlusButton-Hilight
        // peaks at (99, 0, 0): 99/255 of one channel is all the light the old encode could add.
        byte[] fixedUp = [0, 0, 99, 255];
        byte[] before = [0, 0, 99, 255];
        UiHighlightBlendLaw.EncodeAdditive(fixedUp, addArt: true);
        OldEncode(before);
        Check(Blend(fixedUp, 0) > Blend(before, 0) * 2,
            "the dimmest shipped highlight stopped gaining its light back");

        static double Blend(byte[] p, int dst)
        {
            double a = p[3] / 255.0;
            double sum = 0;
            for (int c = 0; c < 3; c++) sum += p[c] * a + dst * (1 - a);
            return sum;
        }

        static void OldEncode(byte[] p)
        {
            int m = Math.Max(p[0], Math.Max(p[1], p[2]));
            p[3] = (byte)(p[3] * m / 255);
        }
    }

    /// <summary>
    /// Which branch runs is read off the BLP header, never inferred from the pixels - a dim
    /// grayscale photo and a dim ADD glow look identical texel by texel.
    /// </summary>
    private static void CheckHeaderIsTheAuthority()
    {
        byte[] add = [(byte)'B', (byte)'L', (byte)'P', (byte)'2', 1, 0, 0, 0, 2, 0, 0, 0];
        byte[] alphaKey = [(byte)'B', (byte)'L', (byte)'P', (byte)'2', 1, 0, 0, 0, 2, 8, 1, 0];
        Check(!BlpDecoder.HasAlphaChannel(add) && BlpDecoder.HasAlphaChannel(alphaKey),
            "alphaDepth is no longer what decides ADD art from alphakey art");
        Check(!BlpDecoder.HasAlphaChannel([1, 2, 3]) &&
              !BlpDecoder.HasAlphaChannel([(byte)'N', (byte)'O', (byte)'P', (byte)'E', 0,0,0,0,0,8,0,0]),
            "HasAlphaChannel accepts a truncated or non-BLP2 buffer");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

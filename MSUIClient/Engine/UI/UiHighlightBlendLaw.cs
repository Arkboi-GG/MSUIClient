namespace MSUIClient.Engine.UI;

/// <summary>
/// How 1.12's ADD-mode highlight art is re-encoded so it can be drawn by an ImGui draw list.
///
/// THE PROBLEM. Blizzard's hover/glow art is authored for `alphaMode="ADD"`: black background,
/// coloured glow, NO alpha channel at all (BLP header alphaDepth 0 - every texel opaque). The
/// GPU is meant to compute `dst + src`, so the black surround contributes nothing and the glow
/// only ever adds light. An ImGui draw list has ONE blend state for the whole frame - ordinary
/// non-premultiplied `SRC_ALPHA / ONE_MINUS_SRC_ALPHA` - so that art cannot be drawn as authored:
///
///   drawn raw           out = src               -> the black background REPLACES the button.
///                                                  A solid black square, which is what the
///                                                  minimap zoom buttons showed.
///   alpha-encoded       out = src*m + dst*(1-m)  where m = max(r,g,b)
///                                               -> adds src*m but SUBTRACTS dst*m. Whenever the
///                                                  art is darker than what is under it the pixel
///                                                  gets darker. The dimmer the art, the worse:
///                                                  UI-CheckBox-Highlight peaks at only 99/255,
///                                                  so its "glow" darkened everything it touched.
///
/// The second one is the approximation this client shipped, and the glue screens already learned
/// it is wrong - see <see cref="GlueAdditive"/>, whose header records benilla replacing the same
/// `a' = a*max(r,g,b)` trick because it "darkened the art instead of brightening it". The glue
/// fix was a real SrcAlpha/One GL pass; the gameplay UI cannot have one without giving up ImGui's
/// z-order, so it needs a better ENCODE instead.
///
/// THE FIX. Normalise the colour to full brightness and carry the lost magnitude in alpha:
///
///   rgb' = rgb / m,  a' = a * m        out = rgb + dst*(1-m)
///
/// against a true add of `rgb + dst`. The added light is now exactly right; all that remains of
/// the error is `-dst*m`, which vanishes at the glow's edge (m -> 0) and is smallest where UI
/// chrome actually lives - dark stone and metal. Hue is preserved, which a white mask cannot do,
/// and the correction SCALES WITH THE BUG: art that already peaks near 255 barely moves (m ~ 1),
/// while the dim art that was darkening the screen gains up to 2.5x its light back.
/// </summary>
public static class UiHighlightBlendLaw
{
    /// <summary>
    /// Re-encode one BGRA buffer in place.
    ///
    /// <paramref name="addArt"/> comes from the BLP header, not from the look of the pixels: an
    /// absent alpha channel IS the declaration that the file is ADD art, because that is the only
    /// reason Blizzard would ship a UI texture without one. Art that carries an alpha channel is
    /// ordinary alpha/alphakey art that already blends correctly, so it keeps the plain
    /// luminance encode and its own coverage mask.
    /// </summary>
    public static void EncodeAdditive(Span<byte> bgra, bool addArt)
    {
        for (int i = 0; i + 3 < bgra.Length; i += 4)
        {
            int intensity = Math.Max(bgra[i], Math.Max(bgra[i + 1], bgra[i + 2]));
            if (addArt && intensity > 0)
            {
                // Every channel is <= intensity, so this saturates the brightest one at 255 and
                // cannot overflow. Pure black stays black and is about to become fully
                // transparent anyway.
                bgra[i] = (byte)(bgra[i] * 255 / intensity);
                bgra[i + 1] = (byte)(bgra[i + 1] * 255 / intensity);
                bgra[i + 2] = (byte)(bgra[i + 2] * 255 / intensity);
            }
            bgra[i + 3] = (byte)(bgra[i + 3] * intensity / 255);
        }
    }

    /// <summary>
    /// The stronger, hue-destroying variant: force the colour to white and carry luminance in
    /// alpha, so the result is `white*m + dst*(1-m)` and can only ever BRIGHTEN. Use it where a
    /// pure "lit up" read matters more than the art's hue; <see cref="EncodeAdditive"/> is closer
    /// to the true add everywhere else.
    /// </summary>
    public static void EncodeBrightMask(Span<byte> bgra)
    {
        for (int i = 0; i + 3 < bgra.Length; i += 4)
        {
            int intensity = Math.Max(bgra[i], Math.Max(bgra[i + 1], bgra[i + 2]));
            bgra[i] = bgra[i + 1] = bgra[i + 2] = 255;
            bgra[i + 3] = (byte)(bgra[i + 3] * intensity / 255);
        }
    }
}

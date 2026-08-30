// BlpDecoder.cs
//
// Managed BLP2 decoder (WoW 1.12), replacing War3Net.Drawing.Blp.
//
// GetPixels() is a drop-in for War3Net's BlpFile.GetPixels: it returns BGRA
// bytes (b, g, r, a per pixel) for the requested mip level — exactly what the
// call sites copy into an SKBitmap(Bgra8888). Supports the encodings vanilla
// 1.12 uses: palettized (RAW1), DXT1/DXT3/DXT5, and uncompressed BGRA (RAW3).
// JPEG-compressed BLP2 (type 0, not used by WoW) throws NotSupportedException.
//
// The DXT block math (565 expansion, 2-bit color indices, DXT3 alpha nibbles,
// DXT5 alpha ramp + 3-bit indices) was validated in a Python prototype before
// this was written.

using System.Buffers;
using System.Buffers.Binary;

namespace MSUIClient.Formats;

public static class BlpDecoder
{
    private const byte EncPalette = 1;   // RAW1
    private const byte EncDxt     = 2;
    private const byte EncArgb    = 3;   // RAW3 / BGRA8888

    private const int PaletteOffset = 148;   // 4+4+4 + 8 + 64 + 64

    /// <summary>
    /// Does this BLP carry an alpha channel at all? Header byte 9 (alphaDepth), read without
    /// decoding a single texel.
    ///
    /// For UI art this is the file saying WHICH BLEND IT WAS AUTHORED FOR. A hover/glow texture
    /// shipped with alphaDepth 0 is ADD art - black background, coloured glow, every texel
    /// opaque - because an add needs no coverage mask. Anything carrying an alpha channel is
    /// ordinary alpha/alphakey art. See MSUIClient.Engine.UI.UiHighlightBlendLaw, which has to
    /// tell the two apart to draw either one through an ImGui draw list.
    /// </summary>
    public static bool HasAlphaChannel(byte[] blp)
    {
        ArgumentNullException.ThrowIfNull(blp);
        return blp.Length > 9 && blp[0] == 'B' && blp[1] == 'L' && blp[2] == 'P' &&
               blp[3] == '2' && blp[9] != 0;
    }

    /// <summary>
    /// Decode a BLP2 mip level to BGRA bytes (w*h*4). Drop-in replacement for
    /// War3Net's BlpFile.GetPixels(mip, out w, out h).
    /// </summary>
    public static byte[] GetPixels(byte[] blp, int mipLevel, out int width, out int height,
        bool rentFromPool = false)
    {
        if (blp.Length < PaletteOffset ||
            blp[0] != 'B' || blp[1] != 'L' || blp[2] != 'P' || blp[3] != '2')
            throw new NotSupportedException("Not a BLP2 file (managed decoder supports WoW 1.12 BLP2 only).");

        uint type      = BinaryPrimitives.ReadUInt32LittleEndian(blp.AsSpan(4));
        byte encoding  = blp[8];
        byte alphaDepth = blp[9];
        byte alphaType = blp[10];
        int w0 = (int)BinaryPrimitives.ReadUInt32LittleEndian(blp.AsSpan(12));
        int h0 = (int)BinaryPrimitives.ReadUInt32LittleEndian(blp.AsSpan(16));

        if (type != 1)
            throw new NotSupportedException("JPEG-compressed BLP2 (type 0) is not supported by the managed decoder.");

        var mipOffsets = new uint[16];
        var mipSizes   = new uint[16];
        for (int i = 0; i < 16; i++) mipOffsets[i] = BinaryPrimitives.ReadUInt32LittleEndian(blp.AsSpan(20 + i * 4));
        for (int i = 0; i < 16; i++) mipSizes[i]   = BinaryPrimitives.ReadUInt32LittleEndian(blp.AsSpan(84 + i * 4));

        if (mipLevel < 0 || mipLevel > 15 || mipOffsets[mipLevel] == 0 || mipSizes[mipLevel] == 0)
            mipLevel = 0;   // fall back to the base mip

        int w = Math.Max(1, w0 >> mipLevel);
        int h = Math.Max(1, h0 >> mipLevel);
        width = w; height = h;

        var mip = blp.AsSpan((int)mipOffsets[mipLevel], (int)mipSizes[mipLevel]);
        int pixelBytes = checked(w * h * 4);
        var bgra = rentFromPool
            ? ArrayPool<byte>.Shared.Rent(pixelBytes)
            : new byte[pixelBytes];
        if (rentFromPool) Array.Clear(bgra, 0, pixelBytes);

        switch (encoding)
        {
            case EncPalette: DecodePalettized(blp, mip, w, h, alphaDepth, bgra); break;
            case EncDxt:     DecodeDxt(mip, w, h, alphaType, bgra); break;
            case EncArgb:    mip.Slice(0, Math.Min(pixelBytes, mip.Length)).CopyTo(bgra); break; // already BGRA
            default:         throw new NotSupportedException($"BLP2 encoding {encoding} is not supported.");
        }
        return bgra;
    }

    // ── Palettized (RAW1): 1 byte/pixel index into the 256-entry BGRA palette,
    //    followed by an alpha block sized by alphaDepth. ──
    private static void DecodePalettized(byte[] blp, ReadOnlySpan<byte> mip, int w, int h,
                                         int alphaDepth, byte[] bgra)
    {
        int px = w * h;
        for (int i = 0; i < px; i++)
        {
            int p = PaletteOffset + mip[i] * 4;
            bgra[i * 4 + 0] = blp[p + 0];   // B
            bgra[i * 4 + 1] = blp[p + 1];   // G
            bgra[i * 4 + 2] = blp[p + 2];   // R
            bgra[i * 4 + 3] = 255;
        }

        int aoff = px;
        switch (alphaDepth)
        {
            case 1:
                for (int i = 0; i < px; i++)
                {
                    int bit = (mip[aoff + (i >> 3)] >> (i & 7)) & 1;
                    bgra[i * 4 + 3] = (byte)(bit != 0 ? 255 : 0);
                }
                break;
            case 4:
                for (int i = 0; i < px; i++)
                {
                    int n = (mip[aoff + (i >> 1)] >> (4 * (i & 1))) & 0xF;
                    bgra[i * 4 + 3] = (byte)(n * 17);
                }
                break;
            case 8:
                for (int i = 0; i < px; i++)
                    bgra[i * 4 + 3] = mip[aoff + i];
                break;
            // alphaDepth 0 -> already opaque
        }
    }

    // ── DXT1 (alphaType 0) / DXT3 (1) / DXT5 (7) ──
    private static void DecodeDxt(ReadOnlySpan<byte> mip, int w, int h, int alphaType, byte[] bgra)
    {
        bool dxt1 = alphaType == 0;
        int blockBytes = dxt1 ? 8 : 16;
        int bx = (w + 3) / 4, by = (h + 3) / 4;

        Span<byte> col = stackalloc byte[16];    // 4 colors * (b,g,r,a)
        Span<byte> alpha = stackalloc byte[16];
        int o = 0;

        for (int cy = 0; cy < by; cy++)
        for (int cx = 0; cx < bx; cx++, o += blockBytes)
        {
            int colorOff = dxt1 ? o : o + 8;
            ColorPalette(mip, colorOff, dxt1, col);

            if (!dxt1)
            {
                if (alphaType == 1) Dxt3Alpha(mip, o, alpha);
                else                Dxt5Alpha(mip, o, alpha);
            }

            uint idx = BinaryPrimitives.ReadUInt32LittleEndian(mip.Slice(colorOff + 4));
            for (int py = 0; py < 4; py++)
            for (int px = 0; px < 4; px++)
            {
                int gx = cx * 4 + px, gy = cy * 4 + py;
                if (gx >= w || gy >= h) continue;

                int pi = py * 4 + px;
                int ci = (int)((idx >> (2 * pi)) & 3);
                int dst = (gy * w + gx) * 4;

                bgra[dst + 0] = col[ci * 4 + 0];
                bgra[dst + 1] = col[ci * 4 + 1];
                bgra[dst + 2] = col[ci * 4 + 2];
                bgra[dst + 3] = dxt1 ? col[ci * 4 + 3] : alpha[pi];
            }
        }
    }

    private static void ColorPalette(ReadOnlySpan<byte> mip, int off, bool dxt1, Span<byte> col)
    {
        ushort c0 = BinaryPrimitives.ReadUInt16LittleEndian(mip.Slice(off));
        ushort c1 = BinaryPrimitives.ReadUInt16LittleEndian(mip.Slice(off + 2));
        Expand565(c0, out int r0, out int g0, out int b0);
        Expand565(c1, out int r1, out int g1, out int b1);

        col[0] = (byte)b0; col[1] = (byte)g0; col[2] = (byte)r0; col[3] = 255;
        col[4] = (byte)b1; col[5] = (byte)g1; col[6] = (byte)r1; col[7] = 255;

        if (dxt1 && c0 <= c1)
        {
            col[8]  = (byte)((b0 + b1) / 2); col[9]  = (byte)((g0 + g1) / 2); col[10] = (byte)((r0 + r1) / 2); col[11] = 255;
            col[12] = 0; col[13] = 0; col[14] = 0; col[15] = 0;   // transparent
        }
        else
        {
            col[8]  = (byte)((2 * b0 + b1) / 3); col[9]  = (byte)((2 * g0 + g1) / 3); col[10] = (byte)((2 * r0 + r1) / 3); col[11] = 255;
            col[12] = (byte)((b0 + 2 * b1) / 3); col[13] = (byte)((g0 + 2 * g1) / 3); col[14] = (byte)((r0 + 2 * r1) / 3); col[15] = 255;
        }
    }

    private static void Dxt3Alpha(ReadOnlySpan<byte> mip, int off, Span<byte> alpha)
    {
        for (int i = 0; i < 16; i++)
        {
            int n = (mip[off + (i >> 1)] >> (4 * (i & 1))) & 0xF;
            alpha[i] = (byte)(n * 17);
        }
    }

    private static void Dxt5Alpha(ReadOnlySpan<byte> mip, int off, Span<byte> alpha)
    {
        int a0 = mip[off], a1 = mip[off + 1];
        Span<int> a = stackalloc int[8];
        a[0] = a0; a[1] = a1;
        if (a0 > a1)
            for (int i = 1; i < 7; i++) a[1 + i] = ((7 - i) * a0 + i * a1) / 7;
        else
        {
            for (int i = 1; i < 5; i++) a[1 + i] = ((5 - i) * a0 + i * a1) / 5;
            a[6] = 0; a[7] = 255;
        }

        long bits = 0;
        for (int k = 0; k < 6; k++) bits |= (long)mip[off + 2 + k] << (8 * k);
        for (int i = 0; i < 16; i++) alpha[i] = (byte)a[(int)((bits >> (3 * i)) & 7)];
    }

    private static void Expand565(ushort c, out int r, out int g, out int b)
    {
        int r5 = (c >> 11) & 0x1F, g6 = (c >> 5) & 0x3F, b5 = c & 0x1F;
        r = (r5 << 3) | (r5 >> 2);
        g = (g6 << 2) | (g6 >> 4);
        b = (b5 << 3) | (b5 >> 2);
    }
}

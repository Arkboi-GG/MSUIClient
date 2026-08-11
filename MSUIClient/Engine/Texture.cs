using Silk.NET.OpenGL;
using MSUIClient.Formats;

namespace MSUIClient.Engine;

/// <summary>
/// GL textures, uploaded straight from BLP.
///
/// <see cref="BlpDecoder"/> hands back BGRA bytes, which is exactly what GL
/// wants as an external format — so a WoW texture goes MPQ -> BLP -> GPU with
/// no intermediate PNG, no Skia, and no disk round trip. That path is already
/// proven: it is what renders every item icon in SuperUI.
/// </summary>
public sealed class Texture : IDisposable
{
    private const int TextureMaxAnisotropyExt = 0x84FE;
    private const int MaxTextureMaxAnisotropyExt = 0x84FF;
    private static float _anisotropy = 1f;

    private readonly GL _gl;
    public uint Handle { get; }
    public int Width { get; }
    public int Height { get; }
    public int Layers { get; }
    public TextureTarget Target { get; }

    private Texture(GL gl, uint handle, TextureTarget target, int w, int h, int layers)
    {
        _gl = gl;
        Handle = handle;
        Target = target;
        Width = w;
        Height = h;
        Layers = layers;
    }

    /// <summary>
    /// Detect and select EXT_texture_filter_anisotropic without making it a
    /// hard requirement. Unsupported GL implementations return InvalidEnum and
    /// quietly stay at ordinary trilinear filtering.
    /// </summary>
    public static unsafe float ConfigureAnisotropy(GL gl, float requested)
    {
        while (gl.GetError() != GLEnum.NoError) { }

        float hardwareMax = 1f;
        gl.GetFloat((GLEnum)MaxTextureMaxAnisotropyExt, &hardwareMax);

        if (gl.GetError() != GLEnum.NoError || !float.IsFinite(hardwareMax) || hardwareMax < 1f)
        {
            _anisotropy = 1f;
            return _anisotropy;
        }

        _anisotropy = Math.Clamp(requested, 1f, hardwareMax);
        return _anisotropy;
    }

    /// <summary>Single 2D texture from raw BGRA bytes.</summary>
    public static unsafe Texture From2D(
        GL gl, byte[] bgra, int width, int height, bool mipmaps = true, bool repeat = true,
        GL? ownerGl = null)
    {
        uint handle = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, handle);

        fixed (byte* p = bgra)
        {
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8,
                (uint)width, (uint)height, 0, PixelFormat.Bgra, PixelType.UnsignedByte, p);
        }

        ApplyParameters(gl, TextureTarget.Texture2D, mipmaps, repeat);
        if (mipmaps) gl.GenerateMipmap(TextureTarget.Texture2D);

        gl.BindTexture(TextureTarget.Texture2D, 0);
        return new Texture(ownerGl ?? gl, handle, TextureTarget.Texture2D, width, height, 1);
    }

    /// <summary>
    /// 2D array texture. Terrain uses this so a whole tile's tileset can be
    /// bound once and indexed per-vertex, instead of one draw call per chunk
    /// (256 chunks x 9 tiles would be 2300 draw calls a frame).
    ///
    /// Every layer must share dimensions — vanilla tileset BLPs are 256x256,
    /// and anything else is rejected by the caller rather than stretched.
    /// </summary>
    public static unsafe Texture Array2D(
        GL gl, IReadOnlyList<byte[]> layersBgra, int width, int height, bool mipmaps = true,
        GL? ownerGl = null)
    {
        uint handle = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2DArray, handle);

        gl.TexImage3D(TextureTarget.Texture2DArray, 0, InternalFormat.Rgba8,
            (uint)width, (uint)height, (uint)layersBgra.Count, 0,
            PixelFormat.Bgra, PixelType.UnsignedByte, null);

        for (int i = 0; i < layersBgra.Count; i++)
        {
            fixed (byte* p = layersBgra[i])
            {
                gl.TexSubImage3D(TextureTarget.Texture2DArray, 0,
                    0, 0, i, (uint)width, (uint)height, 1,
                    PixelFormat.Bgra, PixelType.UnsignedByte, p);
            }
        }

        ApplyParameters(gl, TextureTarget.Texture2DArray, mipmaps, repeat: true);
        if (mipmaps) gl.GenerateMipmap(TextureTarget.Texture2DArray);

        gl.BindTexture(TextureTarget.Texture2DArray, 0);
        return new Texture(ownerGl ?? gl, handle, TextureTarget.Texture2DArray, width, height, layersBgra.Count);
    }

    /// <summary>
    /// 2D array texture from ONE contiguous RGBA buffer holding every layer back
    /// to back, clamped and unmipmapped. The terrain alpha masks.
    ///
    /// WHY AN ARRAY AND NOT AN ATLAS. The masks used to be packed edge to edge
    /// into one 1024x1024 image, which meant a bilinear tap on a chunk boundary
    /// straddled two chunks and returned a 50/50 blend of two unrelated sets of
    /// blend weights — about a yard of wrong-texture smear along every chunk
    /// edge. No sampler state can fix that, because inside an atlas the
    /// neighbours are genuinely adjacent. Array layers cannot bleed into each
    /// other at all, so CLAMP_TO_EDGE now means what it says.
    ///
    /// RGBA order, not BGRA: these are three independent blend weights
    /// (R = layer 1, G = layer 2, B = layer 3), not a colour, so there is no
    /// channel convention to honour and the shader reads them in order.
    ///
    /// Unmipmapped on purpose. A mip chain would average across the chunk's own
    /// edge into whatever the reduction picks up, and reintroduce at distance
    /// exactly the bleeding the array layout removes up close.
    /// </summary>
    public static unsafe Texture ArrayRgbaNoMips(
        GL gl, byte[] rgba, int width, int height, int layers, GL? ownerGl = null)
    {
        uint handle = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2DArray, handle);

        fixed (byte* p = rgba)
        {
            gl.TexImage3D(TextureTarget.Texture2DArray, 0, InternalFormat.Rgba8,
                (uint)width, (uint)height, (uint)layers, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, p);
        }

        gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);

        // A texture with no mip chain must say so, or it is INCOMPLETE and every
        // sample returns opaque black. The min filter above already avoids that,
        // but stating the level range costs nothing and survives someone later
        // changing the filter.
        gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureBaseLevel, 0);
        gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMaxLevel, 0);

        gl.BindTexture(TextureTarget.Texture2DArray, 0);
        return new Texture(ownerGl ?? gl, handle, TextureTarget.Texture2DArray, width, height, layers);
    }

    /// <summary>
    /// 2D array texture from one contiguous single-channel buffer, clamped and
    /// unmipmapped. Terrain uses this for the 64x64-per-chunk MCSH masks: an R8
    /// allocation keeps the authored shadow independent of the RGBA MCAL blend
    /// masks and costs exactly one byte per texel.
    /// </summary>
    public static unsafe Texture ArrayR8NoMips(
        GL gl, byte[] red, int width, int height, int layers, GL? ownerGl = null)
    {
        uint handle = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2DArray, handle);

        fixed (byte* p = red)
        {
            gl.TexImage3D(TextureTarget.Texture2DArray, 0, InternalFormat.R8,
                (uint)width, (uint)height, (uint)layers, 0,
                PixelFormat.Red, PixelType.UnsignedByte, p);
        }

        gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureBaseLevel, 0);
        gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMaxLevel, 0);

        gl.BindTexture(TextureTarget.Texture2DArray, 0);
        return new Texture(ownerGl ?? gl, handle, TextureTarget.Texture2DArray, width, height, layers);
    }

    /// <summary>
    /// Single 2D texture from raw RGBA bytes, clamped and unmipmapped.
    /// </summary>
    public static unsafe Texture FromRgbaNoMips(
        GL gl, byte[] rgba, int width, int height, GL? ownerGl = null)
    {
        uint handle = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, handle);

        fixed (byte* p = rgba)
        {
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8,
                (uint)width, (uint)height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, p);
        }

        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);

        gl.BindTexture(TextureTarget.Texture2D, 0);
        return new Texture(ownerGl ?? gl, handle, TextureTarget.Texture2D, width, height, 1);
    }

    private static void ApplyParameters(GL gl, TextureTarget target, bool mipmaps, bool repeat)
    {
        gl.TexParameter(target, TextureParameterName.TextureMinFilter,
            (int)(mipmaps ? GLEnum.LinearMipmapLinear : GLEnum.Linear));
        gl.TexParameter(target, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);

        if (mipmaps && _anisotropy > 1f)
            gl.TexParameter(target, (TextureParameterName)TextureMaxAnisotropyExt, _anisotropy);

        var wrap = repeat ? GLEnum.Repeat : GLEnum.ClampToEdge;
        gl.TexParameter(target, TextureParameterName.TextureWrapS, (int)wrap);
        gl.TexParameter(target, TextureParameterName.TextureWrapT, (int)wrap);
    }

    public void Bind(uint unit = 0)
    {
        _gl.ActiveTexture(TextureUnit.Texture0 + (int)unit);
        _gl.BindTexture(Target, Handle);
    }

    public void Dispose() => _gl.DeleteTexture(Handle);
}

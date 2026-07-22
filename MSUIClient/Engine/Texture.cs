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

    /// <summary>Single 2D texture from raw BGRA bytes.</summary>
    public static unsafe Texture From2D(
        GL gl, byte[] bgra, int width, int height, bool mipmaps = true, bool repeat = true)
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
        return new Texture(gl, handle, TextureTarget.Texture2D, width, height, 1);
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
        GL gl, IReadOnlyList<byte[]> layersBgra, int width, int height, bool mipmaps = true)
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
        return new Texture(gl, handle, TextureTarget.Texture2DArray, width, height, layersBgra.Count);
    }

    /// <summary>
    /// Single-channel-per-layer alpha atlas, uploaded as RGBA8.
    /// Clamped and unmipmapped: mipmapping a splat mask bleeds neighbouring
    /// chunks into each other at distance, which shows up as seams.
    /// </summary>
    public static unsafe Texture FromRgbaNoMips(GL gl, byte[] rgba, int width, int height)
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
        return new Texture(gl, handle, TextureTarget.Texture2D, width, height, 1);
    }

    private static void ApplyParameters(GL gl, TextureTarget target, bool mipmaps, bool repeat)
    {
        gl.TexParameter(target, TextureParameterName.TextureMinFilter,
            (int)(mipmaps ? GLEnum.LinearMipmapLinear : GLEnum.Linear));
        gl.TexParameter(target, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);

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

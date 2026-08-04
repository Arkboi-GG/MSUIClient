using Silk.NET.OpenGL;
using MSUIClient.Formats;

namespace MSUIClient.Engine.UI;

/// <summary>Lazy MPQ-backed cache for authored FrameXML art and DBC-resolved icons.</summary>
public sealed class GameplayArt : IDisposable
{
    private readonly GL _gl;
    private readonly MpqMount _mpq;
    private readonly Dictionary<string, Texture?> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Texture?> _additiveTextures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Texture?> _brightHighlightTextures = new(StringComparer.OrdinalIgnoreCase);

    public GameplayArt(GL gl, MpqMount mpq) { _gl = gl; _mpq = mpq; }

    public Texture? Get(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) path = @"Interface\Icons\INV_Misc_QuestionMark.blp";
        if (!path.EndsWith(".blp", StringComparison.OrdinalIgnoreCase)) path += ".blp";
        if (_textures.TryGetValue(path, out Texture? cached)) return cached;
        try
        {
            byte[]? bytes = _mpq.ReadFile(path);
            if (bytes is null) return _textures[path] = null;
            byte[] bgra = BlpDecoder.GetPixels(bytes, 0, out int width, out int height);
            return _textures[path] = Texture.From2D(_gl, bgra, width, height, mipmaps: false, repeat: false);
        }
        catch { return _textures[path] = null; }
    }

    public uint Handle(string path) => Get(path)?.Handle ?? 0;

    /// <summary>
    /// Builds an alpha-safe copy of ADD-authored art for ImGui's regular alpha draw list.
    /// Black additive texels become transparent instead of painting an opaque black rectangle.
    /// </summary>
    public uint AdditiveHandle(string path)
    {
        if (!path.EndsWith(".blp", StringComparison.OrdinalIgnoreCase)) path += ".blp";
        if (_additiveTextures.TryGetValue(path, out Texture? cached)) return cached?.Handle ?? 0;
        try
        {
            byte[]? bytes = _mpq.ReadFile(path);
            if (bytes is null) { _additiveTextures[path] = null; return 0; }
            byte[] bgra = BlpDecoder.GetPixels(bytes, 0, out int width, out int height);
            for (int i = 0; i + 3 < bgra.Length; i += 4)
            {
                int intensity = Math.Max(bgra[i], Math.Max(bgra[i + 1], bgra[i + 2]));
                bgra[i + 3] = (byte)(bgra[i + 3] * intensity / 255);
            }
            Texture texture = Texture.From2D(_gl, bgra, width, height, mipmaps: false, repeat: false);
            _additiveTextures[path] = texture;
            return texture.Handle;
        }
        catch { _additiveTextures[path] = null; return 0; }
    }

    /// <summary>
    /// Converts grayscale/near-grayscale ADD hover art into a white alpha mask. Ordinary alpha
    /// blending of the source RGB inevitably subtracts some destination light; a white mask can
    /// only brighten, matching ButtonHilight-Square's 1.12 hover appearance in an ImGui draw list.
    /// Keep colored state/equipped glows on <see cref="AdditiveHandle"/> so their hue survives.
    /// </summary>
    public uint BrightHighlightHandle(string path)
    {
        if (!path.EndsWith(".blp", StringComparison.OrdinalIgnoreCase)) path += ".blp";
        if (_brightHighlightTextures.TryGetValue(path, out Texture? cached)) return cached?.Handle ?? 0;
        try
        {
            byte[]? bytes = _mpq.ReadFile(path);
            if (bytes is null) { _brightHighlightTextures[path] = null; return 0; }
            byte[] bgra = BlpDecoder.GetPixels(bytes, 0, out int width, out int height);
            for (int i = 0; i + 3 < bgra.Length; i += 4)
            {
                int intensity = Math.Max(bgra[i], Math.Max(bgra[i + 1], bgra[i + 2]));
                bgra[i] = bgra[i + 1] = bgra[i + 2] = 255;
                bgra[i + 3] = (byte)(bgra[i + 3] * intensity / 255);
            }
            Texture texture = Texture.From2D(_gl, bgra, width, height, mipmaps: false, repeat: false);
            _brightHighlightTextures[path] = texture;
            return texture.Handle;
        }
        catch { _brightHighlightTextures[path] = null; return 0; }
    }

    public void Dispose()
    {
        foreach (Texture texture in _textures.Values.Where(t => t is not null).Distinct()!) texture.Dispose();
        foreach (Texture texture in _additiveTextures.Values.Where(t => t is not null).Distinct()!) texture.Dispose();
        foreach (Texture texture in _brightHighlightTextures.Values.Where(t => t is not null).Distinct()!) texture.Dispose();
        _textures.Clear();
        _additiveTextures.Clear();
        _brightHighlightTextures.Clear();
    }
}

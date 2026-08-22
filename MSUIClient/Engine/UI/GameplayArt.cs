using Silk.NET.OpenGL;
using MSUIClient.Formats;

namespace MSUIClient.Engine.UI;

/// <summary>Lazy MPQ-backed cache for authored FrameXML art and DBC-resolved icons.</summary>
public sealed class GameplayArt : IDisposable
{
    public readonly record struct PreparedTexture(
        string Path, byte[] Pixels, int Width, int Height);

    private readonly GL _gl;
    private readonly MpqMount _mpq;
    private readonly Dictionary<string, Texture?> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Texture?> _repeatTextures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Texture?> _additiveTextures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Texture?> _brightHighlightTextures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Texture?> _circularTextures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Texture?> _painterlyTextures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Texture?> _painterlyCircularTextures = new(StringComparer.OrdinalIgnoreCase);
    private int _painterlyEpoch = -1;

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

    /// <summary>Resolve UI art with repeat addressing (Backdrop edge strips).</summary>
    public uint RepeatHandle(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return 0;
        if (!path.EndsWith(".blp", StringComparison.OrdinalIgnoreCase)) path += ".blp";
        if (_repeatTextures.TryGetValue(path, out Texture? cached)) return cached?.Handle ?? 0;
        try
        {
            byte[]? bytes = _mpq.ReadFile(path);
            if (bytes is null) { _repeatTextures[path] = null; return 0; }
            byte[] bgra = BlpDecoder.GetPixels(bytes, 0, out int width, out int height);
            Texture texture = Texture.From2D(_gl, bgra, width, height,
                mipmaps: false, repeat: true);
            _repeatTextures[path] = texture;
            return texture.Handle;
        }
        catch { _repeatTextures[path] = null; return 0; }
    }

    /// <summary>Return only an already-resolved UI texture; never reads MPQ data.</summary>
    public bool TryHandle(string path, out uint handle)
    {
        handle = 0;
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (!path.EndsWith(".blp", StringComparison.OrdinalIgnoreCase)) path += ".blp";
        if (!_textures.TryGetValue(path, out Texture? texture)) return false;
        handle = texture?.Handle ?? 0;
        return true;
    }

    /// <summary>CPU-only MPQ read/BLP decode; safe on the bounded asset pool.</summary>
    public PreparedTexture? Prepare(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (!path.EndsWith(".blp", StringComparison.OrdinalIgnoreCase)) path += ".blp";
        byte[]? bytes = _mpq.ReadFile(path);
        if (bytes is null) return null;
        byte[] pixels = BlpDecoder.GetPixels(bytes, 0, out int width, out int height);
        return new PreparedTexture(path, pixels, width, height);
    }

    /// <summary>Publish one CPU-prepared texture on the owning GL thread.</summary>
    public uint Adopt(PreparedTexture prepared)
    {
        if (_textures.TryGetValue(prepared.Path, out Texture? cached))
            return cached?.Handle ?? 0;
        Texture texture = Texture.From2D(
            _gl, prepared.Pixels, prepared.Width, prepared.Height,
            mipmaps: false, repeat: false);
        _textures[prepared.Path] = texture;
        return texture.Handle;
    }

    /// <summary>True after a path has either loaded or been cached as absent.</summary>
    public bool IsResolved(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (!path.EndsWith(".blp", StringComparison.OrdinalIgnoreCase)) path += ".blp";
        return _textures.ContainsKey(path);
    }

    public void MarkMissing(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (!path.EndsWith(".blp", StringComparison.OrdinalIgnoreCase)) path += ".blp";
        _textures.TryAdd(path, null);
    }

    /// <summary>
    /// Painterly-styled copy of a piece of art, for painterly mode.
    ///
    /// Spell icons, item icons and the stand-in portraits are BLPs drawn
    /// straight onto the UI. The painterly pass runs over the default
    /// framebuffer and an off-screen bake, so it never touches them: a painted
    /// world carried a bar of untouched Blizzard icons, which is the single
    /// loudest way the interface announced it was not part of the picture.
    ///
    /// A COPY, never the original: the same texture is drawn unstyled elsewhere
    /// (tooltips, the spellbook, the cursor payload), so restyling the shared
    /// handle in place would be visible everywhere at once. <paramref name="style"/>
    /// receives (destination framebuffer, source texture, width, height).
    ///
    /// <paramref name="epoch"/> invalidates the whole cache when the painterly
    /// knobs move - without it a slider drag would leave every already-styled
    /// icon frozen at the settings it was first baked with.
    /// </summary>
    /// <summary>
    /// Release every styled copy now.
    ///
    /// <see cref="PainterlyHandle"/> drops them lazily on an epoch change, which
    /// is enough while the mode is ON but never fires when it goes OFF - the
    /// next styled lookup is the trigger, and there is no next one. That left a
    /// full set of styled copies resident for the rest of the session.
    /// </summary>
    public void ClearPainterlyCache()
    {
        foreach (Texture texture in _painterlyTextures.Values.Where(t => t is not null).Distinct()!)
            texture.Dispose();
        foreach (Texture texture in _painterlyCircularTextures.Values.Where(t => t is not null).Distinct()!)
            texture.Dispose();
        _painterlyTextures.Clear();
        _painterlyCircularTextures.Clear();
    }

    public uint PainterlyHandle(string path, int epoch, Action<uint, uint, int, int> style)
    {
        if (epoch != _painterlyEpoch)
        {
            ClearPainterlyCache();
            _painterlyEpoch = epoch;
        }

        if (string.IsNullOrWhiteSpace(path)) path = @"Interface\Icons\INV_Misc_QuestionMark.blp";
        if (!path.EndsWith(".blp", StringComparison.OrdinalIgnoreCase)) path += ".blp";
        if (_painterlyTextures.TryGetValue(path, out Texture? cached)) return cached?.Handle ?? Handle(path);

        Texture? source = Get(path);
        if (source is null) { _painterlyTextures[path] = null; return 0; }

        try
        {
            byte[]? bytes = _mpq.ReadFile(path);
            if (bytes is null) { _painterlyTextures[path] = null; return source.Handle; }
            // Decoded again only to size and seed the destination; the style
            // pass overwrites every texel from the source texture.
            byte[] bgra = BlpDecoder.GetPixels(bytes, 0, out int width, out int height);
            Texture destination = Texture.From2D(_gl, bgra, width, height, mipmaps: false, repeat: false);

            uint fbo = _gl.GenFramebuffer();
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer,
                FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, destination.Handle, 0);
            if (_gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer) == GLEnum.FramebufferComplete)
                style(fbo, source.Handle, width, height);
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            _gl.DeleteFramebuffer(fbo);

            _painterlyTextures[path] = destination;
            return destination.Handle;
        }
        catch { _painterlyTextures[path] = null; return source.Handle; }
    }

    /// <summary>
    /// Painterly-styled copy of art drawn inside a ROUND aperture - the party
    /// portraits are the case: stand-in character art, so they belong to the
    /// painted set, but they sit in ring chrome that cannot hide square corners.
    ///
    /// The disc is cut BEFORE the style pass and carried through it, which works
    /// because the pass writes the source alpha back out untouched. Styling
    /// first and masking after would need a readback for no gain.
    /// </summary>
    public uint PainterlyCircularHandle(string path, int epoch, Action<uint, uint, int, int> style)
    {
        if (epoch != _painterlyEpoch)
        {
            ClearPainterlyCache();
            _painterlyEpoch = epoch;
        }

        if (string.IsNullOrWhiteSpace(path)) path = @"Interface\Icons\INV_Misc_QuestionMark.blp";
        if (!path.EndsWith(".blp", StringComparison.OrdinalIgnoreCase)) path += ".blp";
        if (_painterlyCircularTextures.TryGetValue(path, out Texture? cached))
            return cached?.Handle ?? CircularHandle(path);

        uint source = CircularHandle(path);
        if (source == 0) { _painterlyCircularTextures[path] = null; return 0; }

        try
        {
            byte[]? bytes = _mpq.ReadFile(path);
            if (bytes is null) { _painterlyCircularTextures[path] = null; return source; }
            // Decoded again only to size and seed the destination; the style
            // pass overwrites every texel from the masked source texture.
            byte[] bgra = BlpDecoder.GetPixels(bytes, 0, out int width, out int height);
            IconApertureMask.ApplyCircularBgra(bgra, width, height);
            Texture destination = Texture.From2D(_gl, bgra, width, height, mipmaps: false, repeat: false);

            uint fbo = _gl.GenFramebuffer();
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer,
                FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, destination.Handle, 0);
            if (_gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer) == GLEnum.FramebufferComplete)
                style(fbo, source, width, height);
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            _gl.DeleteFramebuffer(fbo);

            _painterlyCircularTextures[path] = destination;
            return destination.Handle;
        }
        catch { _painterlyCircularTextures[path] = null; return source; }
    }

    /// <summary>
    /// Alpha-masked copy for an icon drawn inside circular button chrome. A ring texture cannot
    /// mask its transparent corners, so painting the ordinary square handle beneath it leaks four
    /// square corners. This cache keeps that repair local to controls with a round aperture.
    /// </summary>
    public uint CircularHandle(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) path = @"Interface\Icons\INV_Misc_QuestionMark.blp";
        if (!path.EndsWith(".blp", StringComparison.OrdinalIgnoreCase)) path += ".blp";
        if (_circularTextures.TryGetValue(path, out Texture? cached)) return cached?.Handle ?? 0;
        try
        {
            byte[]? bytes = _mpq.ReadFile(path);
            if (bytes is null) { _circularTextures[path] = null; return 0; }
            byte[] bgra = BlpDecoder.GetPixels(bytes, 0, out int width, out int height);
            IconApertureMask.ApplyCircularBgra(bgra, width, height);
            Texture texture = Texture.From2D(_gl, bgra, width, height, mipmaps: false, repeat: false);
            _circularTextures[path] = texture;
            return texture.Handle;
        }
        catch { _circularTextures[path] = null; return 0; }
    }

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
        foreach (Texture texture in _repeatTextures.Values.Where(t => t is not null).Distinct()!) texture.Dispose();
        foreach (Texture texture in _additiveTextures.Values.Where(t => t is not null).Distinct()!) texture.Dispose();
        foreach (Texture texture in _brightHighlightTextures.Values.Where(t => t is not null).Distinct()!) texture.Dispose();
        foreach (Texture texture in _circularTextures.Values.Where(t => t is not null).Distinct()!) texture.Dispose();
        // Styled copies are owned solely by this cache - nothing else holds them.
        ClearPainterlyCache();
        _textures.Clear();
        _repeatTextures.Clear();
        _additiveTextures.Clear();
        _brightHighlightTextures.Clear();
        _circularTextures.Clear();
    }
}

using System.Numerics;
using ImGuiNET;

namespace MSUIClient.Engine.UI;

/// <summary>
/// The 1.12 client's UI text rendering law, reproduced on ImGui.
///
/// WHY THIS EXISTS
///   FrameXML font heights are not ImGui draw sizes, and the difference is not a taste knob.
///   Same-resolution captures showed spellbook names 23-29% too narrow while their slot chrome
///   agreed within 3-4%: the panel geometry conversion was right and the text conversion was a
///   different, wrong unit system. The empirical 1.25 calibration factor was approximating a
///   measurable font property. This class replaces it with the derived laws.
///
/// THE THREE LAWS (byte-verified in the 5875 client, via wow-re / Benilla's transcription)
///   1. RASTER SIZE (0x5ca030): the client rasterises at
///      clamp(round(FontHeight/768 * deviceHeight), 2, 32) pixels and FreeType makes that the EM.
///      With our gameplay scale s = deviceHeight/768 * preference, the em is round(height * s).
///   2. ADVANCE (0x5d1120): per glyph, the pen advances floor(FT_advance) + 1 pixels, plus one
///      more for a THICK-outlined font only (GlyphStepBase 0x5ca2b0). The +1 is real tracking
///      the client adds to every glyph; it is why reference text reads wider and denser than
///      raw font metrics, and no size factor can reproduce it. It is baked into the glyph
///      advances after the atlas builds - THICK fonts bake as separate instances so the extra
///      tracking never leaks into unoutlined text sharing the same face and size.
///   3. LINE PITCH (0x5cdc20): lineStep = em + spacing, and FrameXML spacing defaults to 0 -
///      the pitch IS the font height. No ascent+descent line-height convention applies.
///
/// THE STB/FREETYPE SIZE SEAM
///   FreeType at pixel size N sets the em to N. stb_truetype (ImGui's rasteriser) scales so the
///   requested SizePixels equals hhea ascent-descent. For FRIZQT that span is 1215 per
///   1000-unit em, so reproducing a FreeType em of N requires requesting N * 1.215 from ImGui.
///   The factor is read from each actual TTF at startup (head.unitsPerEm, hhea
///   ascender/descender), never hardcoded, so a patch-replaced font stays correct.
///
/// WHAT REMAINS DIFFERENT
///   The client runs 2004-era FreeType with hinting; stb_truetype does not hint. Stems can read
///   very slightly slimmer at small sizes. Benilla accepts the same divergence. Everything else
///   - em size, advances, pitch, shadow, outline, pixel alignment - follows the laws above.
///
/// LIFECYCLE
///   Program.Main calls <see cref="Configure"/> with the extracted faces and
///   <see cref="SetBakeRequests"/> with the (face, height, thick) set the font-object registry
///   says gameplay panels use (FontObjectLaw). ClientWindow seeds <see cref="Retarget"/> from
///   the real framebuffer, bakes inside the ImGui controller's onConfigureIO callback
///   (<see cref="BakeInto"/>), applies the advance law right after construction, and rebuilds
///   the atlas between frames whenever Retarget reports the em targets moved. If any step
///   fails, draws fall back to the supersampled atlas with the fallback factor - a missing font
///   degrades to the previous behavior rather than to nothing.
///
///   Panels do not call this class directly - they draw through <see cref="GameText"/> with a
///   FrameXML font-object NAME, which resolves face/height/color/shadow/outline from
///   FontObjectLaw. Hand-picked properties at call sites are the bug class that motivated the
///   registry; see docs/current/ui/UI_TEXT_PARITY_PLAYBOOK.md.
/// </summary>
public static class GameTextLaw
{
    /// <summary>
    /// FRIZQT's measured (hhea.ascent - hhea.descent) / head.unitsPerEm = (965+250)/1000. Used
    /// only when a face's TTF cannot be parsed or the bake failed entirely.
    /// </summary>
    public const float FallbackEmFactor = 1.215f;

    /// <summary>The 1.12 one-to-one raster cap: any FontHeight above 32 draws 32 (0x5ca030).</summary>
    private const float EmCap = 32f;

    /// <summary>Native ImFontGlyph stride. cimgui 1.89 packs Colored:1|Visible:1|Codepoint:30
    /// into one uint, then AdvanceX and 8 floats: 40 bytes. ImGui.NET's managed mirror flattens
    /// the bitfields (48 bytes), so its Glyphs accessor mis-strides - all glyph access here is
    /// raw-pointer at the native layout, validated before any write. Never iterate
    /// ImFontPtr.Glyphs anywhere else.</summary>
    private const int NativeGlyphStride = 40;

    /// <summary>One baked atlas font: a face at a device-pixel em, with or without the THICK
    /// extra-tracking law.</summary>
    private readonly record struct BakeKey(string Face, int Em, bool Thick);

    private readonly record struct FaceInfo(string ExtractedPath, float EmFactor);

    private static readonly Dictionary<string, FaceInfo> _faces = [];
    private static (string Face, float Height, bool Thick)[] _bakePairs = [];
    private static BakeKey[] _requested = [];
    private static readonly Dictionary<BakeKey, ImFontPtr> _fonts = [];
    private static readonly Dictionary<BakeKey, float[]> _stbAdvances = [];
    private static readonly HashSet<BakeKey> _missingLogged = [];
    private static bool _advanceLaw;

    /// <summary>Glyph ranges baked into every atlas font — the exact-size gameplay faces here AND
    /// the supersampled UI face in ClientWindow, which reads <see cref="GlyphRangesPtr"/> so the
    /// two never drift. ImGui's default range stops at U+00FF (Latin-1), which left the em-dash,
    /// en-dash, bullet, ellipsis and curly quotes the codebase uses as separators outside it —
    /// they rasterised as the '?' fallback even though FRIZQT__.TTF carries them. Adding the
    /// General Punctuation block (plus the Latin-Extended/Greek/math the face has) bakes the
    /// glyphs present and silently skips the absent ones (e.g. the → arrow, which the source
    /// spells "->" instead). ImGui keeps only the POINTER until the atlas builds, so it is pinned
    /// for the process lifetime.</summary>
    public static readonly ushort[] GlyphRanges =
    [
        0x0020, 0x00FF,   // Basic Latin + Latin-1 Supplement
        0x0100, 0x024F,   // Latin Extended-A/B (Œ, Š, Ž, ƒ) for accented data names
        0x02C6, 0x02DD,   // spacing modifier letters the face carries
        0x0391, 0x03C9,   // Greek (Δ, Ω, µ, π)
        0x2000, 0x25FF,   // punctuation (en/em dash, bullet, ellipsis, curly quotes) + math + misc
        0,
    ];
    private static System.Runtime.InteropServices.GCHandle _glyphRangesHandle;

    /// <summary>Pinned pointer to <see cref="GlyphRanges"/>, allocated once on first use.</summary>
    public static nint GlyphRangesPtr
    {
        get
        {
            if (!_glyphRangesHandle.IsAllocated)
                _glyphRangesHandle = System.Runtime.InteropServices.GCHandle.Alloc(
                    GlyphRanges, System.Runtime.InteropServices.GCHandleType.Pinned);
            return _glyphRangesHandle.AddrOfPinnedObject();
        }
    }

    /// <summary>True once at least one exact-size gameplay font is baked.</summary>
    public static bool Ready => _fonts.Count > 0;

    /// <summary>Whether the floor(advance)+1 law is currently applied (F6 A/B toggle).</summary>
    public static bool AdvanceLawEnabled => _advanceLaw;

    /// <summary>Register the extracted typefaces. Face keys are the MPQ-internal paths
    /// (FontFace constants); each face's stb conversion factor is measured from its TTF.</summary>
    public static void Configure(IEnumerable<(string Face, string Path)> faces)
    {
        foreach ((string face, string path) in faces)
        {
            float factor = ReadEmFactor(path) ?? FallbackEmFactor;
            _faces[face] = new FaceInfo(path, factor);
            Console.WriteLine($"[game-text] {Path.GetFileName(path)} em factor {factor:F4}");
        }
    }

    /// <summary>The logical (face, height, thick) set to keep rasterised - from
    /// FontObjectLaw.DefaultBakePairs(). Growing gameplay text coverage is a change THERE.</summary>
    public static void SetBakeRequests(IEnumerable<(string Face, float Height, bool Thick)> pairs)
        => _bakePairs = pairs.Where(p => _faces.ContainsKey(p.Face)).Distinct().ToArray();

    /// <summary>
    /// Recompute the wanted em sizes for a (possibly new) gameplay scale. Returns true when
    /// they differ from what is baked - the caller then rebuilds the atlas (ClientWindow owns
    /// that; fonts can only join an atlas before it bakes). This is what keeps a maximised
    /// window or a changed UI-scale preference on exact-size rasters instead of silently
    /// upscaling the nearest bake - the exact blur this class exists to remove.
    /// </summary>
    public static bool Retarget(float uiScale)
    {
        if (_faces.Count == 0 || _bakePairs.Length == 0) return false;
        BakeKey[] wanted = _bakePairs
            .Select(p => new BakeKey(p.Face, EmPixels(p.Height, uiScale), p.Thick))
            .Distinct().OrderBy(k => k.Face).ThenBy(k => k.Em).ThenBy(k => k.Thick).ToArray();
        if (wanted.SequenceEqual(_fonts.Keys.OrderBy(k => k.Face).ThenBy(k => k.Em)
                .ThenBy(k => k.Thick).ToArray()))
            return false;
        _requested = wanted;
        return true;
    }

    /// <summary>Law 1: the device-pixel em for a FrameXML FontHeight at gameplay scale.</summary>
    public static int EmPixels(float fontHeight, float uiScale)
        => Math.Max(2, (int)MathF.Round(MathF.Min(fontHeight, EmCap) * uiScale));

    /// <summary>Law 3: the line pitch equals the em. Named so call sites read as intent.</summary>
    public static float LinePitch(float fontHeight, float uiScale) => EmPixels(fontHeight, uiScale);

    /// <summary>A face's measured stb factor, for the calibration panel.</summary>
    public static float FaceEmFactor(string face)
        => _faces.TryGetValue(face, out FaceInfo info) ? info.EmFactor : FallbackEmFactor;

    /// <summary>"FRIZQT__ 11/13/15px, ARIALN 13/15px" - for the calibration panel and logs.</summary>
    public static string DescribeBake()
        => string.Join(", ", _fonts.Keys.GroupBy(k => k.Face).OrderBy(g => g.Key)
            .Select(g => Path.GetFileNameWithoutExtension(g.Key.Replace('\\', '/')) + " " +
                string.Join("/", g.Select(k => k.Em + (k.Thick ? "T" : ""))
                    .Distinct().Order())));

    /// <summary>
    /// Add the exact-size gameplay fonts to the atlas. Runs inside the ImGui controller's
    /// onConfigureIO callback and again on every ClientWindow atlas rebuild. Oversampling stays
    /// off: the client rasterises at the final size, and a downscaled or
    /// horizontally-oversampled glyph is precisely the softness this class exists to remove.
    /// PixelSnapH stays OFF on purpose - it would ROUND advances at bake time, and the client's
    /// law is floor(raw)+1; rounding first shifts every above-half fraction one pixel wide
    /// (measured on FRIZQT before this was turned off). The law itself produces integer
    /// advances, and Draw snaps origins, so the pen still lands on whole pixels.
    /// </summary>
    public static unsafe void BakeInto(ImGuiIOPtr io)
    {
        if (_requested.Length == 0) return;
        // Rebuild path: stale font pointers die with the old atlas.
        _fonts.Clear();
        _stbAdvances.Clear();
        _missingLogged.Clear();
        _advanceLaw = false;
        var cfg = new ImFontConfigPtr(ImGuiNative.ImFontConfig_ImFontConfig());
        try
        {
            cfg.OversampleH = 1;
            cfg.OversampleV = 1;
            cfg.GlyphRanges = GlyphRangesPtr;   // same punctuation coverage as the UI atlas
            foreach (BakeKey key in _requested)
            {
                if (!_faces.TryGetValue(key.Face, out FaceInfo face) ||
                    !File.Exists(face.ExtractedPath)) continue;
                _fonts[key] = io.Fonts.AddFontFromFileTTF(
                    face.ExtractedPath, key.Em * face.EmFactor, cfg);
            }
        }
        catch (Exception ex)
        {
            _fonts.Clear();
            Console.WriteLine($"[game-text] bake failed - {ex.Message}; using the fallback path");
        }
        finally
        {
            cfg.Destroy();
        }
    }

    /// <summary>
    /// Law 2, applied after the atlas is built: every glyph advance becomes floor(advance)+1
    /// device pixels (+1 more on THICK-outline bakes). Original stb advances are kept so the
    /// F6 panel can A/B the law live.
    /// </summary>
    public static unsafe void ApplyAdvanceLaw()
    {
        if (_fonts.Count == 0) return;
        foreach ((BakeKey key, ImFontPtr font) in _fonts)
        {
            ImVector glyphs = font.NativePtr->Glyphs;
            if (glyphs.Size <= 0 || !GlyphLayoutLooksSane(font))
            {
                Console.WriteLine("[game-text] native glyph layout mismatch - advance law skipped");
                return;
            }
            byte* data = (byte*)glyphs.Data;
            var original = new float[glyphs.Size];
            for (int i = 0; i < glyphs.Size; i++)
                original[i] = *(float*)(data + i * NativeGlyphStride + 4);
            _stbAdvances[key] = original;
        }
        SetAdvanceLaw(true);
    }

    /// <summary>Toggle law 2 on the baked fonts (calibration A/B; on is the shipped state).</summary>
    public static unsafe void SetAdvanceLaw(bool enabled)
    {
        if (_stbAdvances.Count == 0) return;
        foreach ((BakeKey key, ImFontPtr font) in _fonts)
        {
            if (!_stbAdvances.TryGetValue(key, out float[]? original)) continue;
            float extra = key.Thick ? 2f : 1f;
            ImVector glyphs = font.NativePtr->Glyphs;
            byte* data = (byte*)glyphs.Data;
            int count = Math.Min(glyphs.Size, original.Length);
            for (int i = 0; i < count; i++)
            {
                float* advance = (float*)(data + i * NativeGlyphStride + 4);
                *advance = enabled ? MathF.Floor(original[i]) + extra : original[i];
            }
            RefreshIndexAdvances(font);
        }
        _advanceLaw = enabled;
    }

    /// <summary>
    /// Sanity-check the native stride before writing through it. Every decoded codepoint must
    /// be in the BMP, and known codepoints must round-trip IndexLookup -> glyph -> codepoint.
    /// (Codepoint ORDER is deliberately not checked: ImGui appends synthetic glyphs - the tab -
    /// out of order at the tail.) Garbage here means the cimgui layout moved.
    /// </summary>
    private static unsafe bool GlyphLayoutLooksSane(ImFontPtr font)
    {
        ImVector glyphs = font.NativePtr->Glyphs;
        byte* data = (byte*)glyphs.Data;
        for (int i = 0; i < glyphs.Size; i++)
            if (*(uint*)(data + i * NativeGlyphStride) >> 2 > 0xFFFF) return false;
        ImVector lookup = font.NativePtr->IndexLookup;
        ushort* lookupData = (ushort*)lookup.Data;
        foreach (char probe in " Aa0")
        {
            if (probe >= lookup.Size) return false;
            ushort glyph = lookupData[probe];
            if (glyph == ushort.MaxValue || glyph >= glyphs.Size) return false;
            if (*(uint*)(data + glyph * NativeGlyphStride) >> 2 != probe) return false;
        }
        return true;
    }

    /// <summary>
    /// Rebuild the per-codepoint advance cache after mutating glyph advances. Rendering reads
    /// the glyph, but CalcTextSize-style layout reads this index; both must agree.
    /// </summary>
    private static unsafe void RefreshIndexAdvances(ImFontPtr font)
    {
        ImVector glyphs = font.NativePtr->Glyphs;
        byte* data = (byte*)glyphs.Data;

        ImFontGlyph* fallback = font.NativePtr->FallbackGlyph;
        if (fallback is not null)
            font.NativePtr->FallbackAdvanceX = *(float*)((byte*)fallback + 4);

        ImVector lookup = font.NativePtr->IndexLookup;
        ImVector index = font.NativePtr->IndexAdvanceX;
        ushort* lookupData = (ushort*)lookup.Data;
        float* indexData = (float*)index.Data;
        int count = Math.Min(lookup.Size, index.Size);
        for (int cp = 0; cp < count; cp++)
        {
            ushort glyph = lookupData[cp];
            indexData[cp] = glyph == ushort.MaxValue || glyph >= glyphs.Size
                ? font.NativePtr->FallbackAdvanceX
                : *(float*)(data + glyph * NativeGlyphStride + 4);
        }
    }

    /// <summary>
    /// Pick the baked font for a (face, em, thick). An exact hit draws 1:1; a miss (a font
    /// object not yet in FontObjectLaw.BakedByDefault, or the one frame before a rebuild
    /// lands) scales the nearest same-face bake and says so once - visibly softer, never
    /// silent.
    /// </summary>
    private static bool TryResolve(string face, int em, bool thick,
        out ImFontPtr font, out float drawSize)
    {
        var key = new BakeKey(face, em, thick);
        if (_fonts.TryGetValue(key, out font))
        {
            drawSize = font.FontSize;
            return true;
        }
        BakeKey[] sameFace = _fonts.Keys.Where(k => k.Face == face && k.Thick == thick).ToArray();
        if (sameFace.Length == 0)
        {
            font = default;
            drawSize = 0f;
            return false;
        }
        BakeKey nearest = sameFace.MinBy(k => Math.Abs(k.Em - em));
        font = _fonts[nearest];
        drawSize = font.FontSize * em / (float)nearest.Em;
        if (_missingLogged.Add(key))
            Console.WriteLine($"[game-text] no baked font for {key.Face} em {em}px" +
                (thick ? " (thick)" : "") + $" - scaling the {nearest.Em}px bake (softer than " +
                "1:1; add the font object to FontObjectLaw.BakedByDefault)");
        return true;
    }

    /// <summary>
    /// Public accessor for world-space / GL text renderers (nameplates, floating combat text,
    /// world unit names) that need the baked <see cref="ImFontPtr"/> and its draw size directly
    /// rather than a 2D <c>AddText</c> - so they read glyph geometry from a baked FRIZQT face in
    /// the shared atlas, never the ImGui default. Same nearest-bake fallback as <see cref="Draw"/>;
    /// returns false only when the face has no bake at all.
    /// </summary>
    public static bool TryGetFont(string face, int em, bool thick,
        out ImFontPtr font, out float drawSize)
        => TryResolve(face, em, thick, out font, out drawSize);

    /// <summary>
    /// Measure a string's advance width in device pixels - the client's own measure is the sum
    /// of glyph advances (GetStringWidth 0x772890), which after ApplyAdvanceLaw this is.
    /// </summary>
    public static unsafe float MeasureWidth(string face, string text, float fontHeight,
        float uiScale, int outline = 0)
    {
        if (text.Length == 0) return 0f;
        int em = EmPixels(fontHeight, uiScale);
        if (!TryResolve(face, em, outline >= 2, out ImFontPtr font, out float drawSize))
            return ImGui.CalcTextSize(text).X *
                (em * FallbackEmFactor / MathF.Max(1f, ImGui.GetFontSize()));
        ImVector index = font.NativePtr->IndexAdvanceX;
        float* indexData = (float*)index.Data;
        float fallback = font.NativePtr->FallbackAdvanceX;
        float width = 0f;
        foreach (char c in text)
            width += c < index.Size ? indexData[c] : fallback;
        return width * (drawSize / font.FontSize);
    }

    /// <summary>
    /// Draw one line. <paramref name="pos"/> is the line's top-left in device pixels.
    /// <paramref name="shadowColor"/> is the font object's shadow (FrameXML (1,-1) = one
    /// logical pixel right/down on screen, at least one device pixel), null for none.
    /// <paramref name="outline"/> is the classic bitmap outline: the glyph coverage stamped in
    /// black over the r-neighbourhood behind the fill (1 = NORMAL, 2 = THICK).
    /// </summary>
    public static void Draw(ImDrawListPtr dl, string face, string text, float fontHeight,
        float uiScale, Vector2 pos, uint color, uint? shadowColor = null, int outline = 0,
        bool snap = true)
    {
        // ImGui.NET's sized AddText overload throws ArgumentNullException (chars) for an
        // empty managed string. Empty tooltip paragraphs are intentional vertical spacer rows:
        // their height is accounted for by the caller, but there is no glyph submission to make.
        if (text.Length == 0) return;
        if (snap) pos = new Vector2(MathF.Round(pos.X), MathF.Round(pos.Y));
        int em = EmPixels(fontHeight, uiScale);
        if (!TryResolve(face, em, outline >= 2, out ImFontPtr font, out float drawSize))
        {
            font = ImGui.GetFont();
            drawSize = em * FallbackEmFactor;
        }
        if (outline > 0)
        {
            // Outline pixels are raster pixels at the em size - 1 or 2 device px, not scaled.
            for (int dy = -outline; dy <= outline; dy++)
                for (int dx = -outline; dx <= outline; dx++)
                    if (dx != 0 || dy != 0)
                        dl.AddText(font, drawSize, pos + new Vector2(dx, dy), 0xff000000, text);
        }
        if (shadowColor is uint shadow)
        {
            // FrameXML shadow offset is 1 LOGICAL px (MasterFont (1,-1)); round(uiScale) over-
            // scaled it to 2px at typical gameplay scales, reading as a heavy shadow. floor keeps
            // it a 1px hairline until the UI is genuinely 2x+, matching 1.12's subtle drop shadow.
            float offset = MathF.Max(1f, MathF.Floor(uiScale));
            dl.AddText(font, drawSize, pos + new Vector2(offset, offset), shadow, text);
        }
        dl.AddText(font, drawSize, pos, color, text);
    }

    /// <summary>
    /// (hhea.ascender - hhea.descender) / head.unitsPerEm from the TTF's own tables - the exact
    /// ratio between a FreeType pixel-size em and stb_truetype's ascent-to-descent sizing.
    /// Null (and the documented fallback) if the file is not a parseable TTF.
    /// </summary>
    private static float? ReadEmFactor(string path)
    {
        try
        {
            byte[] data = File.ReadAllBytes(path);
            if (data.Length < 12) return null;
            int tableCount = (data[4] << 8) | data[5];
            int headOffset = -1, hheaOffset = -1;
            for (int i = 0; i < tableCount; i++)
            {
                int entry = 12 + i * 16;
                if (entry + 16 > data.Length) return null;
                string tag = System.Text.Encoding.ASCII.GetString(data, entry, 4);
                int offset = (data[entry + 8] << 24) | (data[entry + 9] << 16) |
                             (data[entry + 10] << 8) | data[entry + 11];
                if (tag == "head") headOffset = offset;
                if (tag == "hhea") hheaOffset = offset;
            }
            if (headOffset < 0 || hheaOffset < 0) return null;
            if (headOffset + 20 > data.Length || hheaOffset + 8 > data.Length) return null;
            int unitsPerEm = (data[headOffset + 18] << 8) | data[headOffset + 19];
            short ascent = (short)((data[hheaOffset + 4] << 8) | data[hheaOffset + 5]);
            short descent = (short)((data[hheaOffset + 6] << 8) | data[hheaOffset + 7]);
            if (unitsPerEm <= 0 || ascent <= descent) return null;
            return (ascent - descent) / (float)unitsPerEm;
        }
        catch
        {
            return null;
        }
    }
}

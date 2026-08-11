using System.Numerics;
using ImGuiNET;

namespace MSUIClient;

/// <summary>
/// The interface half of painterly mode.
///
/// Painterly is not only a world shader. The reference look Nico is aiming at
/// is a painted 2D-RPG screen, and its interface is BOXY - square framed map,
/// square portrait, square skill slots, hard rectangular panels. Vanilla's
/// chrome is the opposite: round minimap, circle-cut portraits, gilded rings
/// and beveled art. Painting the world and leaving that chrome on top reads as
/// two different games stacked on each other.
///
/// So every painterly UI variant hangs off the single <see cref="PainterlyUi"/>
/// gate, and every one of them is a VARIANT, never a replacement: with the mode
/// off, the authored Blizzard art path runs exactly as before. Nothing here
/// loads new BLPs - the square chrome is drawn from primitives, which is also
/// why it costs nothing and cannot fail on a missing asset.
/// </summary>
public sealed partial class GameLoop
{
    /// <summary>
    /// The HUD style is deliberately independent from the world pass so clean
    /// world-only and UI-only comparisons do not require editing code.
    /// </summary>
    private bool PainterlyUi => Settings.Display.PainterlyUi;

    /// <summary>
    /// Pack a colour for ImGui, which stores U32 as 0xAABBGGRR - ALPHA, BLUE,
    /// GREEN, RED. Written out as a helper because hand-packing the literal is
    /// a trap: the first version of this palette was authored as 0xAARRGGBB and
    /// every "warm gold" frame came out a cool blue-grey, which is exactly the
    /// wrong direction for a painted-illustration look and is invisible in code
    /// review because the constant still looks plausible.
    /// </summary>
    private static uint Rgba(byte r, byte g, byte b, byte a = 255) =>
        ((uint)a << 24) | ((uint)b << 16) | ((uint)g << 8) | r;

    // The painted-chrome palette. A frame in the reference is a piece of carved
    // stone with a gilt inlay, lit from above - so the set is a stone body that
    // graduates top-to-bottom, a two-tone gold rule that catches that light on
    // its upper edge and loses it on the lower, and a near-black ground the
    // whole thing sits on.
    private static readonly uint PainterlyFrameOuter = Rgba(0x07, 0x06, 0x04);
    private static readonly uint PainterlyStoneTop   = Rgba(0x51, 0x44, 0x30);
    private static readonly uint PainterlyStoneMid   = Rgba(0x3A, 0x2F, 0x20);
    private static readonly uint PainterlyStoneLow   = Rgba(0x23, 0x1C, 0x12);
    private static readonly uint PainterlyFrameRule  = Rgba(0xC6, 0x9E, 0x4E);   // gilt
    private static readonly uint PainterlyGoldLit    = Rgba(0xEA, 0xC9, 0x7C);   // top-lit edge
    private static readonly uint PainterlyGoldShade  = Rgba(0x7A, 0x5F, 0x2C);   // shaded edge
    private static readonly uint PainterlyFrameInner = Rgba(0x14, 0x10, 0x0A);
    private static readonly uint PainterlyFrameFill  = Rgba(0x14, 0x10, 0x0A, 0xD0);
    private static readonly uint PainterlyFrameTitle = Rgba(0x0F, 0x0C, 0x08, 0xE8);

    /// <summary>
    /// Fill the MARGIN between two rects with a top-to-bottom stone gradient,
    /// leaving the content rect untouched.
    ///
    /// Four bands rather than one rect with a hole, because the content is
    /// drawn BEFORE the chrome (FrameXML layers NormalTexture over the icon) -
    /// anything that covers the middle paints over every spell icon on the bar,
    /// which is exactly the bug this codebase already shipped once.
    /// </summary>
    private static void FillStoneMargin(ImDrawListPtr dl, Vector2 outerMin, Vector2 outerMax,
        Vector2 contentMin, Vector2 contentMax)
    {
        // Top band: brightest, catching the light.
        dl.AddRectFilledMultiColor(outerMin, new Vector2(outerMax.X, contentMin.Y),
            PainterlyStoneTop, PainterlyStoneTop, PainterlyStoneMid, PainterlyStoneMid);
        // Bottom band: falling into shadow.
        dl.AddRectFilledMultiColor(new Vector2(outerMin.X, contentMax.Y), outerMax,
            PainterlyStoneMid, PainterlyStoneMid, PainterlyStoneLow, PainterlyStoneLow);
        // Sides carry the full run so the four bands read as one piece.
        dl.AddRectFilledMultiColor(new Vector2(outerMin.X, contentMin.Y),
            new Vector2(contentMin.X, contentMax.Y),
            PainterlyStoneTop, PainterlyStoneTop, PainterlyStoneLow, PainterlyStoneLow);
        dl.AddRectFilledMultiColor(new Vector2(contentMax.X, contentMin.Y),
            new Vector2(outerMax.X, contentMax.Y),
            PainterlyStoneTop, PainterlyStoneTop, PainterlyStoneLow, PainterlyStoneLow);
    }

    /// <summary>
    /// The bevel that makes a flat rectangle read as carved: a lit line along
    /// the top and left, a shadowed one along the bottom and right. One
    /// consistent light direction (above-left) across every panel in the set is
    /// what stops the chrome looking like coloured boxes.
    /// </summary>
    private static void DrawBevel(ImDrawListPtr dl, Vector2 min, Vector2 max, float thickness,
        uint lit, uint shade)
    {
        dl.AddLine(min, new Vector2(max.X, min.Y), lit, thickness);
        dl.AddLine(min, new Vector2(min.X, max.Y), lit, thickness);
        dl.AddLine(new Vector2(min.X, max.Y), max, shade, thickness);
        dl.AddLine(new Vector2(max.X, min.Y), max, shade, thickness);
    }

    /// <summary>
    /// The corner blocks. Small square studs at the four corners of a frame -
    /// the cheapest piece of ornament that reads as "carved" rather than
    /// "drawn", and the detail the reference's frames all share.
    /// </summary>
    private static void DrawCornerStuds(ImDrawListPtr dl, Vector2 outerMin, Vector2 outerMax, float s)
    {
        float d = MathF.Max(3f, 4f * s);
        Span<Vector2> corners =
        [
            outerMin,
            new Vector2(outerMax.X - d, outerMin.Y),
            new Vector2(outerMin.X, outerMax.Y - d),
            new Vector2(outerMax.X - d, outerMax.Y - d),
        ];
        foreach (Vector2 c in corners)
        {
            Vector2 max = c + new Vector2(d);
            dl.AddRectFilledMultiColor(c, max,
                PainterlyGoldLit, PainterlyFrameRule, PainterlyGoldShade, PainterlyFrameRule);
            dl.AddRect(c, max, PainterlyFrameOuter, 0f, ImDrawFlags.None, MathF.Max(1f, s * 0.75f));
        }
    }

    /// <summary>
    /// A square frame around <paramref name="contentMin"/>/<paramref name="contentSize"/>.
    /// Layered dark-to-warm outward, which is how the reference's stone frames
    /// read, and drawn with square corners on purpose - rounding it would undo
    /// the whole point.
    /// </summary>
    /// <param name="fill">
    /// Backing for an EMPTY content rect. Only ever pass true when there is
    /// nothing underneath - the content is drawn before the chrome, so this
    /// paints over anything that is.
    /// </param>
    /// <param name="studs">Corner blocks. Off for small repeated panels (bar
    /// slots), where four studs per slot reads as clutter rather than carving.</param>
    private static void DrawSquarePanel(ImDrawListPtr dl, Vector2 contentMin, Vector2 contentSize,
        float s, bool fill = false, uint ruleColor = 0, bool studs = true)
    {
        if (ruleColor == 0) ruleColor = PainterlyFrameRule;
        float rule = MathF.Max(1f, s);
        float pad = MathF.Max(2f, 3f * s);
        Vector2 contentMax = contentMin + contentSize;
        var outerMin = contentMin - new Vector2(pad);
        var outerMax = contentMax + new Vector2(pad);

        // Ground the whole thing sits on, one pixel proud so the stone has an
        // edge against the world rather than bleeding into it.
        dl.AddRect(outerMin - new Vector2(rule), outerMax + new Vector2(rule),
            PainterlyFrameOuter, 0f, ImDrawFlags.None, rule * 2f);

        // Carved stone body. Margin only - the content rect belongs to whatever
        // was drawn underneath (a spell icon, a portrait bake) and covering it
        // is the bug this codebase already shipped once.
        FillStoneMargin(dl, outerMin, outerMax, contentMin, contentMax);

        // Outer bevel: lit above-left, shadowed below-right, same light as
        // every other panel in the set.
        DrawBevel(dl, outerMin, outerMax, rule, PainterlyStoneTop, PainterlyFrameOuter);

        // The gilt inlay, itself bevelled so it reads as inlaid metal rather
        // than a drawn line - lit on top, shaded underneath.
        Vector2 ruleMin = contentMin - new Vector2(rule * 1.5f);
        Vector2 ruleMax = contentMax + new Vector2(rule * 1.5f);
        dl.AddRect(ruleMin, ruleMax, ruleColor, 0f, ImDrawFlags.None, rule);
        DrawBevel(dl, ruleMin, ruleMax, MathF.Max(1f, rule * 0.5f),
            PainterlyGoldLit, PainterlyGoldShade);

        // Inner cut: the dark lip where the frame drops away to the content.
        dl.AddRect(contentMin, contentMax, PainterlyFrameInner, 0f, ImDrawFlags.None, rule);

        if (studs) DrawCornerStuds(dl, outerMin, outerMax, s);

        // `fill` is now only the backing for an EMPTY content rect (a slot with
        // no icon in it); the stone above already covers the margin.
        if (fill) dl.AddRectFilled(contentMin, contentMax, PainterlyFrameFill);
    }

    /// <summary>
    /// Bumped whenever the painterly knobs are applied, so cached styled art is
    /// rebuilt instead of staying frozen at whatever the sliders said when it
    /// was first baked.
    /// </summary>
    private int _painterlyArtEpoch;

    /// <summary>
    /// The last state cached art was baked under. Only the knobs the OFF-SCREEN
    /// style pass actually reads are in it: the world-only controls (silhouette,
    /// distance calm, canvas height) cannot change an icon or a portrait, and
    /// including them would rebake every one of them on a slider that does not
    /// affect any of them.
    /// </summary>
    private (bool Ui, float Bands, float BandStrength, float Detail, float Ink, float InkThreshold,
        float Saturation, float Contrast, float Lift, float Warmth, float Grain, float Dither)?
        _painterlyArtSignature;

    private bool _painterlyArtStale;

    /// <summary>
    /// Note that cached painterly art no longer matches the knobs, when the
    /// knobs were moved somewhere this cannot see a signature for (the dev
    /// panel writes onto the pass directly, not through settings).
    /// </summary>
    private void MarkPainterlyArtStale() => _painterlyArtStale = true;

    /// <summary>
    /// Mark cached art stale only when something it was baked from actually
    /// moved. ApplySettings runs on EVERY settings widget change, so an
    /// unconditional invalidation here threw away every styled icon - and now
    /// every portrait bake - on each frame of a mouse-sensitivity drag.
    /// </summary>
    private void RefreshPainterlyArt()
    {
        var d = Settings.Display;
        var signature = (d.PainterlyUi, d.PainterlyBands, d.PainterlyBandStrength, d.PainterlyDetail,
            d.PainterlyInk, d.PainterlyInkThreshold, d.PainterlySaturation, d.PainterlyContrast,
            d.PainterlyLift, d.PainterlyWarmth, d.PainterlyGrain, d.PainterlyDither);
        if (_painterlyArtSignature == signature) return;
        _painterlyArtSignature = signature;
        MarkPainterlyArtStale();
    }

    /// <summary>
    /// Rebuild stale painterly art once the player stops moving the control.
    ///
    /// Rebaking is not free - every styled icon re-decodes its BLP, and each
    /// portrait costs a model render plus three synchronous readbacks - so
    /// doing it on every frame of a slider drag stutters the whole client for
    /// the length of the drag. An active ImGui item means the drag is still in
    /// progress; the result of it lands one frame after release.
    /// </summary>
    private void FlushPainterlyArt()
    {
        if (!_painterlyArtStale || ImGui.IsAnyItemActive()) return;
        _painterlyArtStale = false;
        InvalidatePainterlyArt();
    }

    /// <summary>
    /// Drop every piece of art painterly has baked. Both caches, because both
    /// are snapshots of the knobs: the styled icon copies in GameplayArt and
    /// the styled pixels inside the portrait render targets.
    /// </summary>
    private void InvalidatePainterlyArt()
    {
        _painterlyArtEpoch++;
        // Eager, so turning the HUD style OFF actually gives the styled copies
        // back instead of holding them for the session - the epoch alone only
        // frees them on the NEXT styled lookup, which never comes once off.
        _gameplayArt?.ClearPainterlyCache();
        InvalidatePortraitStyling();
    }

    /// <summary>
    /// The portrait handle for a frame whose aperture is ROUND - the character
    /// sheet, the talent frame, the micro-menu button. Always the round copy,
    /// in either mode: painterly squares the unit frames only, and those frames
    /// still draw authored art with a circular hole in it.
    /// </summary>
    private static uint RoundAperturePortrait(Engine.PortraitRenderTarget? portrait, bool usable) =>
        usable ? portrait?.CircularTextureHandle ?? 0 : 0;

    /// <summary>
    /// The portrait handle for the unit frames, whose aperture follows the
    /// chrome they draw: painterly's square painted panel, or the authored
    /// round ring whose transparent corners hide nothing.
    /// </summary>
    private uint UnitFramePortrait(Engine.PortraitRenderTarget? portrait, bool usable) =>
        !usable ? 0u
        : PainterlyUi ? portrait?.TextureHandle ?? 0
        : portrait?.CircularTextureHandle ?? 0;

    /// <summary>
    /// Handle for a piece of UI art, styled when painterly is on and plain
    /// otherwise. Every icon the HUD draws should come through here - an
    /// unstyled Blizzard icon on a painted screen is the loudest thing in it.
    /// </summary>
    private uint PainterlyArt(string path)
    {
        if (_gameplayArt is null) return 0;
        if (!PainterlyUi || _painterly is null) return _gameplayArt.Handle(path);
        return _gameplayArt.PainterlyHandle(path, _painterlyArtEpoch,
            (fbo, tex, w, h) => _painterly.StyleInto(fbo, tex, w, h));
    }

    /// <summary>
    /// Handle for UI art drawn inside a ROUND aperture - the party portraits.
    /// The painterly variant of <see cref="Engine.UI.GameplayArt.CircularHandle"/>,
    /// and like <see cref="PainterlyArt"/> it falls back to the plain masked
    /// copy with the mode off, so the normal HUD is byte-identical to before.
    /// </summary>
    private uint PainterlyRoundArt(string path)
    {
        if (_gameplayArt is null) return 0;
        if (!PainterlyUi || _painterly is null) return _gameplayArt.CircularHandle(path);
        return _gameplayArt.PainterlyCircularHandle(path, _painterlyArtEpoch,
            (fbo, tex, w, h) => _painterly.StyleInto(fbo, tex, w, h));
    }

    /// <summary>
    /// Run a freshly baked portrait through the world's style pass. Called at
    /// bake time, not per frame, so the cost lands where the bake already is.
    /// The flip side is that the style is then FROZEN into the texture until the
    /// next bake, which is why changing it has to force one - see
    /// InvalidatePortraitStyling.
    /// </summary>
    private void StylePortrait(Engine.PortraitRenderTarget portrait) =>
        // Deliberately silent. The paper doll rebakes on EVERY frame of a
        // rotation drag, so a log line here is a per-frame console write in a
        // gameplay path; the bake sites already report their own outcome.
        _painterly?.ApplyToTexture(portrait.FramebufferHandle, portrait.TextureHandle,
            portrait.Width, portrait.Height);

    /// <summary>
    /// The flat backing the skill bar sits on, replacing the authored dwarf
    /// plate and its two sculpted end caps. The reference's bar is a plain
    /// framed strip of square slots, so the ornament goes.
    /// </summary>
    private static void DrawPainterlyBarBacking(ImDrawListPtr dl, Vector2 min, Vector2 size, float s)
    {
        float rule = MathF.Max(1f, s);
        Vector2 max = min + size;

        // Carved stone plinth: full-width gradient, lit above.
        dl.AddRectFilledMultiColor(min, max,
            PainterlyStoneTop, PainterlyStoneTop, PainterlyStoneLow, PainterlyStoneLow);
        dl.AddRect(min, max, PainterlyFrameOuter, 0f, ImDrawFlags.None, rule * 2f);
        DrawBevel(dl, min, max, rule, PainterlyStoneTop, PainterlyFrameOuter);

        // Gilt rail along the top edge, bevelled like the panel inlays.
        var railMin = new Vector2(min.X, min.Y + rule * 2f);
        var railMax = new Vector2(max.X, min.Y + rule * 4f);
        dl.AddRectFilled(railMin, railMax, PainterlyFrameRule);
        dl.AddLine(railMin, new Vector2(railMax.X, railMin.Y), PainterlyGoldLit, rule * 0.75f);
        dl.AddLine(new Vector2(railMin.X, railMax.Y), railMax, PainterlyGoldShade, rule * 0.75f);

        // Sculpted ends: a heavier block capping each end of the plinth, which
        // is what stops a long strip reading as a plain bar.
        float capW = MathF.Max(10f, 14f * s);
        foreach (float x in new[] { min.X, max.X - capW })
        {
            var capMin = new Vector2(x, min.Y);
            var capMax = new Vector2(x + capW, max.Y);
            dl.AddRectFilledMultiColor(capMin, capMax,
                PainterlyStoneTop, PainterlyStoneMid, PainterlyStoneLow, PainterlyStoneMid);
            dl.AddRect(capMin, capMax, PainterlyFrameOuter, 0f, ImDrawFlags.None, rule);
            DrawBevel(dl, capMin, capMax, rule, PainterlyGoldLit, PainterlyGoldShade);
        }
    }
}

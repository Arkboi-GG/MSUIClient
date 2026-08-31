using System.Numerics;
using ImGuiNET;

namespace MSUIClient.Engine.UI;

/// <summary>
/// The panel-facing gameplay text API: draw by FrameXML font-object NAME, exactly as FrameXML
/// itself does. Face, height, default color, shadow, and outline all come from
/// <see cref="FontObjectLaw"/> (the transcribed Fonts.xml); rendering follows the derived 1.12
/// laws in <see cref="GameTextLaw"/>.
///
/// RULES FOR PANEL CODE
///   - Name the font object the panel's FrameXML names. Never hand-pick a height, shadow, or
///     outline at a call site - that is the "title drawn 14px white when the XML says 12px gold
///     shadowed" bug class.
///   - The color override exists for runtime Lua recolors only (SetTextColor - e.g. the
///     passive-spell yellow, a disabled gray). The font object's own color is the default.
///   - Positions are FontString BOX positions in device pixels. A fixed-height box centers its
///     text vertically (justifyV MIDDLE is the FrameXML default) - use
///     <see cref="BoxCenteredTop"/> for that; an auto-height box is lines x LinePitch tall.
///   - New font object on a migrating panel? Add its name to FontObjectLaw.BakedByDefault so
///     it rasterises exact-size. Drawing unbaked logs and rescales the nearest bake - visible,
///     never silent.
/// </summary>
public static class GameText
{
    /// <summary>Draw one line; <paramref name="pos"/> is the line's top-left (box top for a
    /// single-line auto box). <paramref name="color"/> only for runtime Lua recolors.</summary>
    public static void Draw(ImDrawListPtr dl, string fontObject, string text, Vector2 pos,
        float uiScale, uint? color = null, bool snap = true)
    {
        FontObjectSpec spec = FontObjectLaw.Get(fontObject);
        GameTextLaw.Draw(dl, spec.Face, text, spec.Height, uiScale, pos, color ?? spec.Color,
            spec.ShadowColor, spec.Outline, snap);
    }

    /// <summary>Draw centered on a point (both axes; the em box centers vertically).</summary>
    public static void DrawCentered(ImDrawListPtr dl, string fontObject, string text,
        Vector2 center, float uiScale, uint? color = null)
    {
        FontObjectSpec spec = FontObjectLaw.Get(fontObject);
        float width = MeasureWidth(fontObject, text, uiScale);
        int em = GameTextLaw.EmPixels(spec.Height, uiScale);
        Draw(dl, fontObject, text, new Vector2(center.X - width * 0.5f, center.Y - em * 0.5f),
            uiScale, color);
    }

    /// <summary>
    /// Draw centered while multiplying the complete FontString alpha. Unlike a SetTextColor-style
    /// override this fades both the glyph and its inherited shadow, matching Frame:SetAlpha.
    /// </summary>
    public static void DrawCenteredWithAlpha(ImDrawListPtr dl, string fontObject, string text,
        Vector2 center, float uiScale, float alpha)
    {
        FontObjectSpec spec = FontObjectLaw.Get(fontObject);
        float width = MeasureWidth(fontObject, text, uiScale);
        int em = GameTextLaw.EmPixels(spec.Height, uiScale);
        Vector2 position = new(center.X - width * .5f, center.Y - em * .5f);
        GameTextLaw.Draw(dl, spec.Face, text, spec.Height, uiScale, position,
            MultiplyAlpha(spec.Color, alpha),
            spec.ShadowColor is uint shadow ? MultiplyAlpha(shadow, alpha) : null,
            spec.Outline, snap: true);
    }

    /// <summary>
    /// Custom / RTS / HUD text that has no vanilla FontObject to name: exact-size FRIZQT at a
    /// logical height, drawn from the baked atlas - never the ImGui default font (Nico's rule).
    /// <paramref name="pos"/> is the line's top-left in device pixels. Pass uiScale 1f when the
    /// caller only has a device-pixel size (the nearest FRIZQT bake is scaled to it).
    /// </summary>
    public static void DrawPlain(ImDrawListPtr dl, string text, Vector2 pos, float logicalHeight,
        float uiScale, uint color, uint? shadow = null, int outline = 0)
        => GameTextLaw.Draw(dl, FontFace.FrizQt, text, logicalHeight, uiScale, pos, color,
            shadow, outline);

    /// <summary>DrawPlain centered on a point (both axes; the em box centers vertically).</summary>
    public static void DrawPlainCentered(ImDrawListPtr dl, string text, Vector2 center,
        float logicalHeight, float uiScale, uint color, uint? shadow = null)
    {
        float width = GameTextLaw.MeasureWidth(FontFace.FrizQt, text, logicalHeight, uiScale);
        int em = GameTextLaw.EmPixels(logicalHeight, uiScale);
        GameTextLaw.Draw(dl, FontFace.FrizQt, text,
            logicalHeight, uiScale, new Vector2(center.X - width * .5f, center.Y - em * .5f),
            color, shadow);
    }

    /// <summary>DrawPlain right-aligned: the text's RIGHT edge lands on <paramref name="rightX"/>.
    /// <paramref name="top"/> is the line top in device pixels.</summary>
    public static void DrawPlainRightAligned(ImDrawListPtr dl, string text, float rightX,
        float top, float logicalHeight, float uiScale, uint color, uint? shadow = null)
    {
        float width = GameTextLaw.MeasureWidth(FontFace.FrizQt, text, logicalHeight, uiScale);
        GameTextLaw.Draw(dl, FontFace.FrizQt, text, logicalHeight, uiScale,
            new Vector2(rightX - width, top), color, shadow);
    }

    /// <summary>FRIZQT advance width in device pixels for a logical height (custom text layout).
    /// </summary>
    public static float MeasurePlain(string text, float logicalHeight, float uiScale)
        => GameTextLaw.MeasureWidth(FontFace.FrizQt, text, logicalHeight, uiScale);

    /// <summary>Draw with the text's RIGHT edge at <paramref name="rightEdge"/>.X (justifyH
    /// RIGHT columns - tooltip right cells, money numbers).</summary>
    public static void DrawRightAligned(ImDrawListPtr dl, string fontObject, string text,
        Vector2 rightEdge, float uiScale, uint? color = null, bool snap = true)
    {
        float width = MeasureWidth(fontObject, text, uiScale);
        Draw(dl, fontObject, text, rightEdge with { X = rightEdge.X - width }, uiScale,
            color, snap);
    }

    /// <summary>Advance width in device pixels (the client's GetStringWidth).</summary>
    public static float MeasureWidth(string fontObject, string text, float uiScale)
    {
        FontObjectSpec spec = FontObjectLaw.Get(fontObject);
        return GameTextLaw.MeasureWidth(spec.Face, text, spec.Height, uiScale, spec.Outline);
    }

    /// <summary>Line pitch in device pixels: the em (law 3), for stacking wrapped lines.</summary>
    public static float LinePitch(string fontObject, float uiScale)
        => GameTextLaw.LinePitch(FontObjectLaw.Get(fontObject).Height, uiScale);

    /// <summary>
    /// Resolve the display text for an authored fixed-size FontString box. Width and height are
    /// logical FrameXML units; measurement, wrapping, and the line-pitch fit test all occur in the
    /// same scaled device-pixel space used by the draw.
    /// </summary>
    public static string EllipsizeToBox(string fontObject, string text, float boxWidthLogical,
        float boxHeightLogical, float uiScale)
    {
        float width = boxWidthLogical * uiScale;
        float height = boxHeightLogical * uiScale;
        return FontStringOverflowLaw.Ellipsize(text, width, height,
            LinePitch(fontObject, uiScale), candidate => MeasureWidth(fontObject, candidate, uiScale));
    }

    /// <summary>The device-pixel em (law 1) - the single-line text height.</summary>
    public static int EmPixels(string fontObject, float uiScale)
        => GameTextLaw.EmPixels(FontObjectLaw.Get(fontObject).Height, uiScale);

    private static uint MultiplyAlpha(uint color, float alpha)
    {
        byte source = checked((byte)(color >> 24));
        byte result = checked((byte)MathF.Round(source * Math.Clamp(alpha, 0f, 1f)));
        return (color & 0x00ff_ffffu) | ((uint)result << 24);
    }

    /// <summary>
    /// The text top for a FIXED-height FontString box (justifyV MIDDLE default): the box's
    /// screen top plus half the slack between the scaled box and the em. The spellbook's
    /// SubSpellName (79x18 box, 10px text) is the canonical case - this slack is the visible
    /// air 1.12 shows between a name and its rank line.
    /// </summary>
    public static float BoxCenteredTop(string fontObject, float boxTopY, float boxHeightLogical,
        float uiScale)
        => boxTopY + (boxHeightLogical * uiScale - EmPixels(fontObject, uiScale)) * 0.5f;
}

using System.Numerics;

namespace MSUIClient.Engine.UI;

/// <summary>
/// Rule-owned geometry for MSUI's preserved standalone Key Bindings shell, with current
/// Benilla's collapsed-category and binding-search behavior hosted inside that shell.
/// </summary>
public static class KeyBindingsUiLaw
{
    public const string TitleFont = "GameFontNormal";
    public const string ColumnHeaderFont = "GameFontNormalSmall";
    public const string CategoryFont = "GameFontNormal";
    public const string CategoryHighlightFont = "GameFontHighlight";
    public const string CommandFont = "GameFontNormalSmall";
    public const string KeyNormalFont = "GameFontHighlightSmall";
    public const string KeyHighlightFont = "GameFontHighlightSmall";
    public const string KeyDisabledFont = "GameFontDisableSmall";
    public readonly record struct Rect(float X, float Y, float Width, float Height)
    {
        public Vector2 Min => new(X, Y);
        public Vector2 Size => new(Width, Height);
    }

    // -- measured, not remembered --------------------------------------------
    // Every number below comes from Blizzard's own data rather than from the look of it:
    //   Interface\FrameXML\KeyBindingFrame.xml   frame 640x512, anchored TOP (0,-100)
    //   Interface\FrameXML\UIPanelTemplates.xml  scroll bar 16 wide, TOPRIGHT +(6,-16)
    //   Interface\KeyBindingFrame\UI-KeyBindingFrame-*.blp, decoded alpha:
    //       left   solid border x 6..17,  interior fill from x 18
    //       top    solid border y 7..52,  interior fill from y 53
    //       right  interior fill ends x 561, border to x 593, LAST OPAQUE PIXEL x 597
    //       bottom last opaque pixel y 500
    // The art therefore does NOT fill its 640x512 canvas - x 598..639 is empty padding. That is
    // what put the old scroll bar (584..616) and Cancel button (490..620) outside the visible
    // window: inside the canvas, but past the artwork. Reported 2026-08-26.
    public const float FrameTop = 100f;              // KeyBindingFrame.xml TOP offset y=-100
    public const float VisibleRightEdge = 597f;      // last opaque pixel, measured
    public static Vector2 FrameSize => new(640, 512);

    /// <summary>
    /// Vanilla anchors this frame TOP to UIParent, so it is CENTRED horizontally - it does not
    /// sit against the left edge. Needs the live display width, hence a method.
    /// </summary>
    public static Vector2 WindowOrigin(float logicalDisplayWidth) =>
        WindowOrigin(logicalDisplayWidth, float.PositiveInfinity);

    /// <summary>
    /// Centred like the TOP anchor, and kept ON SCREEN vertically.
    ///
    /// The fit ceiling in InterfaceScaleLaw is width-only by design - the main menu bar runs out
    /// of room sideways, never downward - so on a wide-but-short display the derived scale can
    /// put this frame's 512 logical units past the bottom of the framebuffer. At 3440x1440 with
    /// the shipped preference the footer row landed below the screen entirely, which no amount
    /// of dragging recovers because the seat is only applied on first use. Clamp the seat
    /// instead of trusting the scale. Found by audit, 2026-08-26.
    /// </summary>
    public static Vector2 WindowOrigin(float logicalDisplayWidth, float logicalDisplayHeight)
        => WindowOrigin(logicalDisplayWidth, logicalDisplayHeight, 0f);

    /// <summary>Centred on the WIDENED frame (see <see cref="ClampExtraWidth"/>).</summary>
    public static Vector2 WindowOrigin(float logicalDisplayWidth, float logicalDisplayHeight, float extra)
    {
        float x = MathF.Max(0f, (logicalDisplayWidth - FrameSizeWith(extra).X) * 0.5f);
        float y = float.IsFinite(logicalDisplayHeight)
            ? Math.Clamp(FrameTop, 0f, MathF.Max(0f, logicalDisplayHeight - FrameSize.Y))
            : FrameTop;
        return new Vector2(x, y);
    }

    // -- MSUI: a wider frame ---------------------------------------------------------------
    // Vanilla's 640-wide frame gives the command column 175 px, which MSUI's longer commander
    // rows outgrow (owner, 2026-09-03: "needs to be wider / resizeable"). The frame may be
    // widened by dragging its right border; EVERY extra pixel goes to the command column, so
    // the key columns, the scroll bar, the right-hand art slices and the right-anchored footer
    // buttons all shift right by the same amount and the art's carved scroll slot stays under
    // the bar. The gap between the Top/Bot slices and the TopRight/BotRight slices is filled
    // by the middle half of Top/Bot (their interior stone), stretched.
    public const float MaxExtraWidth = 640f;

    public static float ClampExtraWidth(float requested, float logicalDisplayWidth)
    {
        float room = MathF.Max(0f, logicalDisplayWidth - FrameSize.X);
        float ceiling = MathF.Min(MaxExtraWidth, room);
        return float.IsFinite(requested) ? Math.Clamp(requested, 0f, ceiling) : 0f;
    }

    public static Vector2 FrameSizeWith(float extra) => FrameSize + new Vector2(extra, 0f);
    public static Vector2 RightShift(float extra) => new(extra, 0f);
    /// <summary>Width the command label may use before it is ellipsized (a 4 px gutter).</summary>
    public static float CommandColumnWidth(float extra) => PrimaryKey.X - 4f + extra;
    /// <summary>The drag handle: the frame's right border band, full interior height.</summary>
    public static Rect ResizeGrip(float extra) => new(VisibleRightEdge - 14f + extra, 53f, 14f, 390f);
    /// <summary>The hover rule drawn along the grip: 4 px inside its right edge, 8 px shy of
    /// both ends (screen pixels in, screen pixels out).</summary>
    public static (Vector2 A, Vector2 B) ResizeGripRule(Vector2 gripMin, Vector2 gripMax, float scale)
    {
        float x = gripMax.X - 4f * scale;
        return (new Vector2(x, gripMin.Y + 8f * scale), new Vector2(x, gripMax.Y - 8f * scale));
    }
    /// <summary>The stretched interior strips that fill the widened gap (logical rects; empty at 0).</summary>
    public static Rect StretchTop(float extra) => new(512f, 0f, extra, 256f);
    public static Rect StretchBottom(float extra) => new(512f, 256f, extra, 256f);
    public static Vector2 StretchUv0 => new(0.25f, 0f);
    public static Vector2 StretchUv1 => new(0.75f, 1f);
    /// <summary>Art slices anchored to the frame's RIGHT edge (they move with the extra width).</summary>
    public static bool ArtIsRightAnchored(ArtSlice slice) => slice.Offset.X >= 512f;

    public static Vector2 TitleCenter => new(320, 26);
    // MSUI adds a search box and a character-specific toggle that vanilla has no room for, so
    // both live in the INTERIOR (y >= 53) and the row band starts below them. In the top band
    // they were drawn on the frame's own border decoration.
    public static Rect Search => new(26, 58, 180, 22);
    public static Vector2 SearchPlaceholderOffset => new(7, 5);
    public static Vector2 CommandTitle => new(26, 88);
    public static Vector2 KeyOneCenter => new(292, 92);
    public static Vector2 KeyTwoCenter => new(472, 92);
    public static Rect Rows => new(27, 104, 535, 345);
    public const float RowPitch = 23f;               // template 25 tall, anchored -2 = 23 pitch
    public const int VisibleRows = 15;               // 104 + 15*23 = 449, clear of the buttons
    public static Vector2 RowMinimum(int visibleIndex) =>
        Rows.Min + new Vector2(0, Math.Max(0, visibleIndex) * RowPitch);
    public static Vector2 RowHitSize => new(Rows.Width, RowPitch);
    public static Rect HeaderGlyph => new(2, 3.5f, 16, 16);
    public static Vector2 HeaderTextOffset => new(24, 5.5f);
    public static Vector2 CommandTextOffset => new(0, 6.5f);
    public static Rect PrimaryKey => new(175, 1, 180, 22);
    public static Rect SecondaryKey => new(355, 1, 180, 22);
    // KeyBindingFrame.xml: KeyBindingFrameScrollFrame (FauxScrollFrameTemplate) is TOPLEFT
    // (2,-53), 560x390, so its right edge is 562 and it spans y 53..443. UIPanelScrollFrameTemplate
    // hangs the bar at TOPRIGHT +(6,-16) / BOTTOMRIGHT +(6,16), width 16, with the up button
    // anchored BOTTOM to the bar's TOP and the down button TOP to its BOTTOM: so the up button
    // sits flush at y 53, the down button ends at 443, x 568..584 - the carved slot in the
    // TopRight/BotRight art. It had been seated on MSUI's row band (104, 345 tall) instead,
    // which left the arrows floating mid-slot (owner, 2026-09-03: "not inside its slots").
    public static Vector2 ScrollMinimum => new(568, 53);
    public const float ScrollHeight = 390f;
    public static Vector2 FeedbackCenter => new(320, 458);
    public static Vector2 CharacterSpecificMinimum => new(360, 60);
    // BOTTOMLEFT (10,21) and BOTTOMRIGHT (-50,21) with 130x22 buttons: y = 512-21-22 = 469,
    // Cancel 460..590, Okay abuts its LEFT at 330..460. Unbind is MSUI's, abutting Okay.
    public static Rect Defaults => new(10, 469, 130, 22);
    public static Rect Unbind => new(200, 469, 130, 22);
    public static Rect Okay => new(330, 469, 130, 22);
    public static Rect Cancel => new(460, 469, 130, 22);

    public readonly record struct ArtSlice(string Path, Vector2 Offset, Vector2 Size);

    public static IReadOnlyList<ArtSlice> Art =>
    [
        new(@"Interface\KeyBindingFrame\UI-KeyBindingFrame-TopLeft", new(0, 0), new(256)),
        new(@"Interface\KeyBindingFrame\UI-KeyBindingFrame-Top", new(256, 0), new(256)),
        new(@"Interface\KeyBindingFrame\UI-KeyBindingFrame-TopRight", new(512, 0), new(128, 256)),
        new(@"Interface\KeyBindingFrame\UI-KeyBindingFrame-BotLeft", new(0, 256), new(256)),
        new(@"Interface\KeyBindingFrame\UI-KeyBindingFrame-Bot", new(256, 256), new(256)),
        new(@"Interface\KeyBindingFrame\UI-KeyBindingFrame-BotRight", new(512, 256), new(128, 256)),
    ];

    public static int MaximumScroll(int rowCount) =>
        Math.Max(0, rowCount - VisibleRows);

    public static int ClampScroll(int requested, int rowCount) =>
        Math.Clamp(requested, 0, MaximumScroll(rowCount));

    public static bool MatchesSearch(string category, string label, string query)
    {
        string needle = query.Trim();
        return needle.Length == 0 ||
            category.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
            label.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }
}

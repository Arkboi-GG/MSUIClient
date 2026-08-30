using System.Numerics;
using ImGuiNET;
using Silk.NET.OpenGL;
using MSUIClient.Formats;

namespace MSUIClient.Engine.UI;

/// <summary>
/// The 1.12 frame look, built from Blizzard's own art and Blizzard's own layout
/// numbers.
///
/// NOTHING IN THIS FILE IS A GUESS ANY MORE
///   The first version of this class guessed the texture paths and the edge-file
///   layout, and got both close and neither right. Everything below was read out
///   of interface.MPQ instead:
///
///   * `Interface\FrameXML\GameMenuFrame.xml`, `OptionsFrame.xml` and
///     `BasicControls.xml` gave the backdrop definitions, the edge and tile
///     sizes, the background insets, the frame sizes, the button size and the
///     button tex-coords. Those files ship in the archive - the real UI is data,
///     not a description of data, and it was sitting there the whole time.
///   * The paths needed `.blp`. `MpqMount.ReadFile` takes a literal internal
///     path and the archive stores the extension; FrameXML omits it because the
///     engine appends it. That single missing suffix was 14/14 misses.
///   * The edge layout was settled by DECODING `UI-DialogBox-Border.blp` and
///     looking at it: 256x32, eight 32x32 cells in a horizontal strip, in the
///     order LEFT, RIGHT, TOP, BOTTOM, TOPLEFT, TOPRIGHT, BOTTOMLEFT,
///     BOTTOMRIGHT. The TOP and BOTTOM cells are stored STANDING UP - their bar
///     runs down the left and right of the cell respectively - so they are drawn
///     rotated a quarter turn CLOCKWISE, which puts the left column at the top
///     and the right column at the bottom. The first version rotated the other
///     way.
///
/// THE THREE BACKDROPS ARE BLIZZARD'S, VERBATIM
///   Dialog, Tooltip and SliderTrack below are transcriptions of the three
///   distinct `&lt;Backdrop&gt;` blocks in OptionsFrame.xml. Do not tune them by eye;
///   if one looks wrong, the drawing code is wrong.
/// </summary>
public sealed class WowSkin : IDisposable
{
    // ── Blizzard's backdrops, transcribed ────────────────────────────────────

    /// <summary>
    /// A `&lt;Backdrop&gt;`: a tiled background inset inside a nine-sliced edge.
    /// Sizes are in Blizzard's own UI pixels and get multiplied by <see cref="Scale"/>.
    /// </summary>
    public readonly record struct Backdrop(
        string Bg, string Edge, float EdgeSize, float TileSize,
        float InsetL, float InsetT, float InsetR, float InsetB);

    /// <summary>The exact variables used by one rendered UIPanelButton state.</summary>
    public readonly record struct PanelButtonDrawState(
        Vector2 Min,
        Vector2 Size,
        bool Enabled,
        bool Held,
        bool Hovered,
        string InteractionState,
        string StateTextureRole,
        string StateTexturePath,
        Vector2 StateUvMin,
        Vector2 StateUvMax,
        Vector2 TextMin,
        Vector2 TextSize,
        uint TextColor,
        bool HighlightVisible,
        string HighlightTexturePath);

    /// <summary>GameMenuFrame / OptionsFrame. The heavy riveted metal frame.</summary>
    public static readonly Backdrop Dialog =
        new("dialog.bg", "dialog.border", 32f, 32f, 11f, 12f, 12f, 11f);

    /// <summary>The bind-on-pickup GroupLootFrame gold dialog variant.</summary>
    public static readonly Backdrop DialogGold =
        new("dialog.bg", "dialog.gold.border", 32f, 32f, 11f, 12f, 12f, 11f);

    /// <summary>The inner group boxes - "Display", "World Appearance".</summary>
    public static readonly Backdrop Tooltip =
        new("tooltip.bg", "tooltip.border", 16f, 16f, 5f, 5f, 5f, 5f);

    /// <summary>A slider's track.</summary>
    public static readonly Backdrop SliderTrack =
        new("slider.bg", "slider.border", 8f, 8f, 3f, 6f, 3f, 6f);

    /// <summary>
    /// The AccountLogin account/password edit boxes (AccountLogin.xml Backdrop): UI-Tooltip-Background
    /// tiled inside a Glue-Tooltip-Border edge, edge/tile 16, BackgroundInsets 10/4/5/9. This is the
    /// recessed frame the inputs share with the buttons' look - drawn, not a flat dark rectangle.
    /// </summary>
    public static readonly Backdrop GlueEditBox =
        new("tooltip.bg", "glue.tt.border", 16f, 16f, 10f, 4f, 5f, 9f);

    // ── the texture set ──────────────────────────────────────────────────────
    //
    // Verified present in interface.MPQ's (listfile). The .blp matters.

    // Backgrounds repeat; EDGE files deliberately do not.
    //
    // The edges are drawn one quad per tile with v inside [0,1] (see
    // VerticalEdge), so they never need the wrap mode - and CLAMP is actively
    // better for them, because REPEAT lets linear filtering at v=0 blend in the
    // texture's bottom row and put a seam across every rivet.
    //
    // The three background files are each a single flat colour (measured:
    // UI-DialogBox-Background is RGBA 0,0,0,153 throughout), so their uv range of
    // 0..n samples the same texel whatever the wrap mode does. Repeat is set for
    // correctness, not because anything depends on it.
    public static readonly (string Key, string Path, bool Repeat)[] Paths =
    [
        ("dialog.bg",      @"Interface\DialogFrame\UI-DialogBox-Background.blp", true),
        ("dialog.border",  @"Interface\DialogFrame\UI-DialogBox-Border.blp",     false),
        ("dialog.gold.border", @"Interface\DialogFrame\UI-DialogBox-Gold-Border.blp", false),
        ("dialog.header",  @"Interface\DialogFrame\UI-DialogBox-Header.blp",     false),
        ("tooltip.bg",     @"Interface\Tooltips\UI-Tooltip-Background.blp",      true),
        ("tooltip.border", @"Interface\Tooltips\UI-Tooltip-Border.blp",          false),
        ("button.up",      @"Interface\Buttons\UI-Panel-Button-Up.blp",          false),
        ("button.down",    @"Interface\Buttons\UI-Panel-Button-Down.blp",        false),
        ("button.hi",      @"Interface\Buttons\UI-Panel-Button-Highlight.blp",   false),
        ("button.off",     @"Interface\Buttons\UI-Panel-Button-Disabled.blp",    false),
        ("dialog.button.up",   @"Interface\Buttons\UI-DialogBox-Button-Up.blp", false),
        ("dialog.button.down", @"Interface\Buttons\UI-DialogBox-Button-Down.blp", false),
        ("dialog.button.off",  @"Interface\Buttons\UI-DialogBox-Button-Disabled.blp", false),
        ("dialog.button.hi",   @"Interface\Buttons\UI-DialogBox-Button-Highlight.blp", false),
        ("dialog.alert",       @"Interface\DialogFrame\DialogAlertIcon.blp", false),
        ("chat.input.left",    @"Interface\ChatFrame\UI-ChatInputBorder-Left.blp", false),
        ("chat.input.right",   @"Interface\ChatFrame\UI-ChatInputBorder-Right.blp", false),
        ("check.box",      @"Interface\Buttons\UI-CheckBox-Up.blp",              false),
        ("check.down",     @"Interface\Buttons\UI-CheckBox-Down.blp",            false),
        ("check.mark",     @"Interface\Buttons\UI-CheckBox-Check.blp",           false),
        ("check.hi",       @"Interface\Buttons\UI-CheckBox-Highlight.blp",       false),
        ("slider.bg",      @"Interface\Buttons\UI-SliderBar-Background.blp",     true),
        ("slider.border",  @"Interface\Buttons\UI-SliderBar-Border.blp",         false),
        ("slider.knob",    @"Interface\Buttons\UI-SliderBar-Button-Horizontal.blp", false),

        // The GLUE art set (the login/main-menu screen). Paths + texcoords transcribed from
        // benilla's glue/art.rs, which read them out of GlueButtons.xml / AccountLogin.xml.
        // The login checkbox reuses check.* (UI-CheckBox-*) and the realm modal reuses dialog.*
        // (UI-DialogBox-*), both already loaded above - only the glue-specific art is new here.
        ("glue.logo",      @"Interface\Glues\Common\Glues-WoW-Logo.blp",          false),
        ("glue.btn.up",    @"Interface\Glues\Common\Glue-Panel-Button-Up.blp",    false),
        ("glue.btn.down",  @"Interface\Glues\Common\Glue-Panel-Button-Down.blp",  false),
        ("glue.btn.off",   @"Interface\Glues\Common\Glue-Panel-Button-Disabled.blp", false),
        ("glue.btn.hi",    @"Interface\Glues\Common\Glue-Panel-Button-Highlight.blp", false),

        // The DIAL SPINNER arrows - the yellow-on-stone 32x32 pair the char-create appearance rows
        // use, NOT text "<" / ">". benilla glue/art.rs `fn arrow()` builds them from
        // Interface\Glues\Common\Glue-{Left,Right}Arrow-Button-{Up,Down,Highlight}, and
        // char_create/screen.rs dial_row places them 32x32 at the row's right. The Highlight sheet is
        // an ADD-blend overlay in the reference, so it gets the same luma->alpha rebuild as
        // glue.btn.hi (dark field -> transparent, bright rim -> tint) and can be drawn straight.
        ("glue.arrow.l",    @"Interface\Glues\Common\Glue-LeftArrow-Button-Up.blp",         false),
        ("glue.arrow.l.dn", @"Interface\Glues\Common\Glue-LeftArrow-Button-Down.blp",       false),
        ("glue.arrow.l.hi", @"Interface\Glues\Common\Glue-LeftArrow-Button-Highlight.blp",  false),
        ("glue.arrow.r",    @"Interface\Glues\Common\Glue-RightArrow-Button-Up.blp",        false),
        ("glue.arrow.r.dn", @"Interface\Glues\Common\Glue-RightArrow-Button-Down.blp",      false),
        ("glue.arrow.r.hi", @"Interface\Glues\Common\Glue-RightArrow-Button-Highlight.blp", false),

        // The info panels' SCROLLBAR (benilla glue/art.rs `fn scroll_btn` + char_create/panels.rs).
        // The buttons and the knob are 32x32 sheets whose CENTRE QUARTER is the 16x16 control -
        // GlueScrollBarButton's texcoords, benilla SCROLL_BTN_TC 0.25..0.75. The two track pieces are
        // decorative art behind the slider, shown only while the panel actually scrolls.
        ("scroll.up",    @"Interface\Buttons\UI-ScrollBar-ScrollUpButton-Up.blp",     false),
        ("scroll.up.dn", @"Interface\Buttons\UI-ScrollBar-ScrollUpButton-Down.blp",   false),
        ("scroll.dn",    @"Interface\Buttons\UI-ScrollBar-ScrollDownButton-Up.blp",   false),
        ("scroll.dn.dn", @"Interface\Buttons\UI-ScrollBar-ScrollDownButton-Down.blp", false),
        ("scroll.knob",  @"Interface\Buttons\UI-ScrollBar-Knob.blp",                  false),

        // The race/class/gender check-button highlight (benilla glue/widgets.rs icon_button):
        // ButtonHilight-Square, lit on hover AND HELD while selected - the ref's LockHighlight. The
        // 1.12 template's CheckedTexture is commented out in the shipped GlueXML, so this square IS
        // the whole selected visual: there is no border of any colour around a picked race or class.
        // Authored dark-field + bright-rim for ADD, so it takes the luma->alpha rebuild below.
        ("btn.hilight.sq", @"Interface\Buttons\ButtonHilight-Square.blp", false),
        ("cc.scrolltop", @"Interface\Glues\CharacterCreate\UI-CharacterCreate-ScrollBar-Top.blp", false),
        ("cc.scrollbot", @"Interface\ClassTrainerFrame\UI-ClassTrainer-ScrollBar.blp",             false),
        ("glue.blizz",     @"Interface\Glues\Mainmenu\Glues-BlizzardLogo.blp",    false),
        // The account/password edit-box edge (AccountLogin.xml Backdrop edgeFile). 128x16, eight
        // 16x16 cells - the same nine-slice layout as the other borders, so DrawBackdrop serves it.
        ("glue.tt.border", @"Interface\Glues\Common\Glue-Tooltip-Border.blp",     false),
        // The character-select roster row highlight (benilla glue/art.rs: Glue-CharacterSelect-
        // Highlight, ADD blend). A yellow glow authored on black; luma->alpha below so an alpha draw
        // only brightens, same path as glue.btn.hi. Shown for hover AND the selected (locked) row.
        ("glue.select.hi", @"Interface\Glues\CharacterSelect\Glue-CharacterSelect-Highlight.blp", false),
        // RAW copy (NO luma rebuild) for the TRUE additive overlay (GlueAdditive), drawn SrcAlpha/One
        // so black adds nothing and only the bright rim adds light - exactly benilla's AddUiMaterial.
        ("glue.select.hi.raw", @"Interface\Glues\CharacterSelect\Glue-CharacterSelect-Highlight.blp", false),

        // The character-create icon sheets (benilla glue/art.rs; texcoords live in Program.CharCreate.cs).
        ("cc.races",    @"Interface\Glues\CharacterCreate\UI-CharacterCreate-Races.blp",    false),
        ("cc.classes",  @"Interface\Glues\CharacterCreate\UI-CharacterCreate-Classes.blp",  false),
        ("cc.gender",   @"Interface\Glues\CharacterCreate\UI-CharacterCreate-Gender.blp",   false),
        ("cc.factions", @"Interface\Glues\CharacterCreate\UI-CharacterCreate-Factions.blp", false),
        ("cc.banners",  @"Interface\Glues\CharacterCreate\UI-CharacterCreate-Banners.blp",  false),
        ("cc.bg",         @"Interface\Glues\CharacterCreate\UI-CharacterCreate-Background.blp",  false),
        ("cc.border",     @"Interface\Glues\CharacterCreate\UI-CharacterCreate-OuterBorder.blp", false),
        ("cc.labelframe", @"Interface\Glues\CharacterCreate\CharacterCreate-LabelFrame.blp",     false),
        ("cc.iconshadow", @"Interface\Glues\CharacterCreate\UI-CharacterCreate-IconShadow.blp",  false),
        ("cc.rotate.up",  @"Interface\Glues\CharacterCreate\UI-RotationRight-Big-Up.blp",        false),
        ("cc.rotate.down",@"Interface\Glues\CharacterCreate\UI-RotationRight-Big-Down.blp",      false),
    ];

    public sealed class Piece
    {
        public string Key = "";
        public string Path = "";
        public Texture? Texture;
        public int Width;
        public int Height;
        public string Note = "";
        public bool Found => Texture is not null;
    }

    private readonly List<Piece> _pieces = [];
    private readonly Dictionary<string, Piece> _byKey = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<Piece> Pieces => _pieces;
    public int FoundCount { get; private set; }

    /// <summary>
    /// Off draws the plain styled panel and touches no texture. Kept because a
    /// broken skin should never be able to make the settings unreachable.
    /// </summary>
    public bool Textured { get; set; } = true;

    /// <summary>Blizzard UI pixels -> screen pixels. Tracks the interface scale.</summary>
    public float Scale { get; set; } = 1f;

    /// <summary>Blizzard's button art: 128x32 texture, art in the top-left 0.625 x 0.6875.</summary>
    private static readonly Vector2 ButtonUv1 = new(0f, 0f);
    private static readonly Vector2 ButtonUv2 = new(0.625f, 0.6875f);

    /// <summary>
    /// One DIAL SPINNER arrow button (benilla glue/widgets.rs `dial_arrow` + glue/art.rs `arrow`):
    /// the `Glue-{Left,Right}Arrow-Button-Up` sprite, swapped for `-Down` while held and overlaid
    /// with `-Highlight` on hover. Draws the WHOLE sheet into the given square, which is what the
    /// reference does (the art is authored as a finished 32x32 button, not a sheet cell).
    ///
    /// Returns false and draws NOTHING when the art is missing - the caller falls back to its own
    /// text button, so a stripped MPQ still leaves the dials usable.
    /// </summary>
    public bool GlueArrowButton(ImDrawListPtr dl, string id, bool left, Vector2 pos, Vector2 size,
                                out bool clicked)
    {
        clicked = false;
        string baseKey = left ? "glue.arrow.l" : "glue.arrow.r";
        var up = Get(baseKey);
        if (up is null) return false;

        ImGui.SetCursorScreenPos(pos);
        clicked = ImGui.InvisibleButton(id, size);
        bool held = ImGui.IsItemActive();
        bool hovered = ImGui.IsItemHovered();

        var art = held ? (Get(baseKey + ".dn") ?? up) : up;
        var drawPos = held ? pos + new Vector2(1f, 1f) * Scale : pos;   // the pushed offset, as GlueButton
        dl.AddImage(Id(art), drawPos, drawPos + size, Vector2.Zero, Vector2.One, White);

        if (hovered && !held && GlueTune.HoverGlow > 0.001f && Get(baseKey + ".hi") is Piece hi)
            for (float g = GlueTune.HoverGlow; g > 0.001f; g -= 1f)
                dl.AddImage(Id(hi), pos, pos + size, Vector2.Zero, Vector2.One,
                    ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, MathF.Min(1f, g))));

        return true;
    }

    /// <summary>UI-Panel-Button-Up is 128x32; the used region is 80x22.</summary>
    public static readonly Vector2 ButtonArt = new(80f, 22f);

    /// <summary>GameMenuFrame's own button size.</summary>
    public static readonly Vector2 MenuButton = new(144f, 21f);

    /// <summary>
    /// The glue button art region (GlueButtons.xml TexCoords, via benilla glue/art.rs): Up / Down /
    /// Disabled share [0, 0.578125] x [0, 0.75] of the sheet; the Highlight has its own sub-rect.
    /// Drawn as one stretched quad across the button rect, exactly like the in-game PanelButton.
    /// </summary>
    // benilla BUTTON_TC: the sheet's top-left [0,0.578125] x [0,0.75] = [0,148] x [0,48] of 256x64.
    // The outer ~4-6px of that region is a soft ~38%-alpha black GLOW ring - the button's baked-in
    // 1.12 shadow. The whole region is drawn as authored (no crop, no de-halo) so that shadow lands
    // on the scene the way the OG login does.
    private static readonly Vector2 GlueButtonUv1 = new(0f, 0f);
    private static readonly Vector2 GlueButtonUv2 = new(0.578125f, 0.75f);
    private static readonly Vector2 GlueButtonHiUv1 = new(0f, 0f);
    private static readonly Vector2 GlueButtonHiUv2 = new(0.625f, 0.6875f);

    // Edge-strip order, measured off the decoded texture.
    private const int EdgeLeft = 0, EdgeRight = 1, EdgeTop = 2, EdgeBottom = 3;
    private const int CornerTL = 4, CornerTR = 5, CornerBL = 6, CornerBR = 7;

    // ── the procedural fallback palette ──────────────────────────────────────

    public static readonly Vector4 Fill      = new(0.055f, 0.043f, 0.031f, 0.94f);
    public static readonly Vector4 FillLight = new(0.098f, 0.082f, 0.062f, 0.94f);
    public static readonly Vector4 Gold      = new(1.000f, 0.820f, 0.000f, 1.00f);
    // GlueFontNormal's gold (GlueFonts.xml: 1.0, 0.78, 0) - the login screen's label/caption colour.
    public static readonly Vector4 GlueGold  = new(1.000f, 0.780f, 0.000f, 1.00f);
    // AccountLogin's DEFAULT_TOOLTIP_COLOR (benilla login/screen.rs BOX_FILL / BOX_BORDER): the tint
    // the edit-box backdrop is drawn with. UI-Tooltip-Background is a LIGHT sheet; at full White it
    // reads whitish-grey over the bright login valley. Tinted near-black (0.09) for the recessed well
    // and light-grey (0.8) for the border, it matches the OG's dark input field. This is a TINT
    // (multiply on the texture), not a flat fill - the tile's own texture/rivets still show through.
    public static readonly Vector4 GlueBoxFill   = new(0.090f, 0.090f, 0.090f, 1.00f);
    public static readonly Vector4 GlueBoxBorder = new(0.800f, 0.800f, 0.800f, 1.00f);
    public static readonly Vector4 GoldDim   = new(0.478f, 0.400f, 0.251f, 1.00f);
    public static readonly Vector4 Parchment = new(1.000f, 0.824f, 0.000f, 1.00f);
    public static readonly Vector4 Normal    = new(1.000f, 1.000f, 1.000f, 1.00f);
    public static readonly Vector4 Muted     = new(0.612f, 0.573f, 0.494f, 1.00f);
    public static readonly Vector4 Highlight = new(1.000f, 1.000f, 1.000f, 1.00f);
    public static readonly Vector4 Disabled  = new(0.500f, 0.500f, 0.500f, 1.00f);
    public static readonly Vector4 Shadow    = new(0.000f, 0.000f, 0.000f, 0.75f);
    // The 1.12 MasterFont drop shadow: opaque black, offset (1,-1) UI px (down-right on screen).
    // Every glue label/caption carries it - it is what gives gold-on-bright text its edge. Opaque,
    // unlike the in-game Shadow above, because that is what the reference bakes onto the glue fonts.
    public static readonly Vector4 GlueShadow = new(0.000f, 0.000f, 0.000f, 1.00f);

    private WowSkin() { }

    // ── load ─────────────────────────────────────────────────────────────────

    public static WowSkin Load(GL gl, MpqMount? mpq)
    {
        var skin = new WowSkin();

        foreach (var (key, path, repeat) in Paths)
        {
            var piece = new Piece { Key = key, Path = path };
            skin._pieces.Add(piece);
            skin._byKey[key] = piece;

            if (mpq is null) { piece.Note = "no MPQ mount"; continue; }

            byte[]? blp;
            try { blp = mpq.ReadFile(path); }
            catch (Exception ex) { piece.Note = ex.Message; continue; }

            if (blp is null) { piece.Note = "not in any archive"; continue; }

            try
            {
                var bgra = BlpDecoder.GetPixels(blp, 0, out int w, out int h);

                // The glue button sprites (up / down / off) are uploaded AS AUTHORED. The soft black
                // glow ring baked around them IS the 1.12 button's shadow - it grounds the button on
                // the scene (see the OG reference). Earlier this was de-haloed away to kill a supposed
                // "black box", but that stripped the shadow, rendered the metal border transparent and
                // washed the darker pressed pill out. So: no de-halo on the button faces.
                // The HIGHLIGHT is the one exception: it is a black field + bright rim meant to be
                // ADD-blended. The red glue button is rendered through ImGui's normal alpha blend,
                // so keeping the highlight's coloured RGB can still replace bright red with darker
                // midtones. Make that overlay a white light mask: hover can then only brighten the
                // authored button face, matching the 1.12 ADD-blend result.
                if (key == "button.hi" || key == "dialog.button.hi" ||
                    key == "glue.btn.hi")
                    WhiteGlowFromLuma(bgra);
                else if (key == "glue.select.hi"
                    || key == "glue.arrow.l.hi" || key == "glue.arrow.r.hi")
                    HighlightAlphaFromLuma(bgra);
                // The icon check-button square goes further: RGB is forced WHITE so the overlay can
                // only ever LIGHTEN what is under it. See WhiteGlowFromLuma for why alpha alone was
                // not enough.
                else if (key == "btn.hilight.sq")
                    WhiteGlowFromLuma(bgra);
                // check.hi WAS MISSING FROM EVERY BRANCH ABOVE and so went up AS AUTHORED - and it
                // is ADD art like the rest: alphaDepth 0, peak (24,40,99), and 76% of its texels a
                // near-black field, measured. Opaque black over a straight-alpha draw does not add
                // nothing, it REPLACES: the settings checkboxes hovered as a hard-edged square film
                // covering the whole 26x26 cell, while the box art itself only occupies the middle
                // ~17px of that - the "overflowing square" it was reported as. This is the same
                // defect the minimap zoom buttons had, in a third copy of the encode.
                //
                // UiHighlightBlendLaw rather than WhiteGlowFromLuma: the law normalises the colour
                // to full brightness instead of discarding it, so the checkbox keeps the blue tint
                // 1.12 gives it. Measured, it lands exactly on what alphaMode="ADD" would produce.
                else if (key == "check.hi")
                    UiHighlightBlendLaw.EncodeAdditive(bgra, addArt: !BlpDecoder.HasAlphaChannel(blp));

                piece.Texture = Texture.From2D(gl, bgra, w, h, mipmaps: false, repeat: repeat);
                piece.Width = w;
                piece.Height = h;
                skin.FoundCount++;
            }
            catch (Exception ex)
            {
                piece.Note = $"decode failed: {ex.Message}";
            }
        }

        Console.WriteLine($"[ui-skin] {skin.FoundCount}/{skin._pieces.Count} resolved from the MPQs");
        foreach (var p in skin._pieces)
            if (!p.Found) Console.WriteLine($"[ui-skin]   MISSING {p.Path}  ({p.Note})");

        if (skin.FoundCount == 0)
            Console.WriteLine("[ui-skin] nothing resolved - drawing the procedural frame instead.");

        return skin;
    }

    /// <summary>
    /// Turn an ADDITIVE glow sheet into a straight-alpha approximation.
    ///
    /// Glue-Panel-Button-Highlight is a black field with a bright rim, authored for ADD blending: the
    /// black adds nothing, the rim adds light. ImGui's draw lists blend straight, so drawing it as-is
    /// paints the black field as a ~50% dark veil over the pill - the "interior shadow on hover". This
    /// rewrites each texel's alpha to its own brightness (max r,g,b): the black field goes fully
    /// transparent (adds nothing, exactly like ADD would), and only the bright rim tints through.
    /// RGB is left as-is, so this is only appropriate where retaining the authored highlight colour
    /// matters more than a strict light-only guarantee. BGRA in place; channel order is irrelevant
    /// to max().
    /// </summary>
    private static void HighlightAlphaFromLuma(byte[] bgra)
    {
        for (int i = 0; i + 3 < bgra.Length; i += 4)
        {
            float a = Math.Max(bgra[i], Math.Max(bgra[i + 1], bgra[i + 2])) / 255f;
            // Gamma-push the alpha so the dark field goes fully transparent - the glow reads cleanly
            // without a dark veil ("shadow creeping through"). (Straight-alpha stand-in for ADD.)
            a *= a;
            bgra[i + 3] = (byte)(a * 255f + 0.5f);
        }
    }

    /// <summary>
    /// Turn an ADDITIVE glow sheet into a pure LIGHT MASK: white RGB, alpha = the texel's own
    /// brightness. Drawn with straight alpha this lerps whatever is underneath TOWARDS WHITE, so it
    /// can only ever brighten - stack it and the highlight keeps climbing instead of saturating into
    /// a coloured film.
    ///
    /// Why this and not <see cref="HighlightAlphaFromLuma"/>: that one rewrites alpha but KEEPS the
    /// sheet's own RGB. Under normal alpha blending, any retained RGB darker than the destination
    /// still darkens it. That made the red GlueButton and the fairly dark ButtonHilight-Square read
    /// as inward shadows instead of 1.12-style ADD highlights. Forcing RGB to white keeps the same
    /// mask shape - the bright parts still carry the most alpha - while guaranteeing the direction
    /// of the effect. Tint at draw time to colour it.
    /// </summary>
    private static void WhiteGlowFromLuma(byte[] bgra)
    {
        for (int i = 0; i + 3 < bgra.Length; i += 4)
        {
            float a = Math.Max(bgra[i], Math.Max(bgra[i + 1], bgra[i + 2])) / 255f;
            bgra[i] = 255; bgra[i + 1] = 255; bgra[i + 2] = 255;
            bgra[i + 3] = (byte)(a * 255f + 0.5f);
        }
    }

    private Piece? Get(string key)
    {
        if (!Textured) return null;
        return _byKey.TryGetValue(key, out var p) && p.Found ? p : null;
    }

    private static IntPtr Id(Piece p) => (IntPtr)p.Texture!.Handle;
    private static uint White => ImGui.ColorConvertFloat4ToU32(Vector4.One);
    private static uint U32(Vector4 c) => ImGui.ColorConvertFloat4ToU32(c);

    /// <summary>The 1.12 outlined glue font: a thin black border, emulated with 8 offset copies of the
    /// glyph (ImGui's loaded font has no outline). Draw it BETWEEN the drop shadow and the main text.
    /// Off when GlueTune.OutlinePx is 0. Applies to gold, white, and grey glue text alike.</summary>
    public static void OutlineText(ImDrawListPtr dl, ImFontPtr font, float sizePx, Vector2 pos, string text)
    {
        if (GlueTune.OutlinePx <= 0.001f) return;
        float ow = MathF.Max(1f, sizePx * GlueTune.OutlinePx);
        uint black = ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 1f));
        for (int oy = -1; oy <= 1; oy++)
            for (int ox = -1; ox <= 1; ox++)
                if (ox != 0 || oy != 0)
                    dl.AddText(font, sizePx, pos + new Vector2(ox * ow, oy * ow), black, text);
    }

    // ── backdrops ────────────────────────────────────────────────────────────

    /// <summary>
    /// Blizzard's `&lt;Backdrop&gt;`, drawn: the tiled background inset by the
    /// backdrop's insets, then the nine-sliced edge over the full rect.
    /// </summary>
    public void DrawBackdrop(ImDrawListPtr dl, Vector2 min, Vector2 max, in Backdrop style)
        => DrawBackdrop(dl, min, max, style, Vector4.One, Vector4.One);

    /// <summary>
    /// The same backdrop, but with the fill and the edge each MULTIPLIED by a tint - the mechanism
    /// AccountLogin uses to turn the light UI-Tooltip-Background sheet into a dark recessed field
    /// (fill tinted near-black, border tinted light-grey). The texture's own detail still shows
    /// through; this is a multiply, not a repaint. Untinted callers get the White,White overload.
    /// </summary>
    public void DrawBackdrop(ImDrawListPtr dl, Vector2 min, Vector2 max, in Backdrop style,
                             Vector4 fillTint, Vector4 edgeTint)
    {
        DrawBackdropFill(dl, min, max, style, U32(fillTint));
        DrawBackdropEdge(dl, min, max, style, U32(edgeTint));
    }

    /// <summary>
    /// The tinted backdrop with horizontal BANDS of the tiled FILL left out. The nine-sliced EDGE is
    /// always drawn whole, so the frame border is never broken - only the translucent interior opens up.
    ///
    /// This is what lets the char-select row highlight sit BEHIND the row text without being dimmed:
    /// the ADD glow is composited under the whole ImGui pass (so the text draws in front, benilla's
    /// panel -&gt; glow -&gt; text order), and the band the glow occupies is simply not covered by the panel
    /// fill. Every strip re-draws the SAME full-rect fill quad under a clip rect, so the tiling stays
    /// aligned across the gaps - it is a mask, not a re-layout.
    ///
    /// <paramref name="fillHoleBands"/> holds screen-space (top, bottom) Y pairs; they are merged, so
    /// overlapping cards (the selected row and a hovered row) collapse into one band.
    /// </summary>
    public void DrawBackdrop(ImDrawListPtr dl, Vector2 min, Vector2 max, in Backdrop style,
                             Vector4 fillTint, Vector4 edgeTint, IReadOnlyList<Vector2>? fillHoleBands)
    {
        uint fill = U32(fillTint);
        if (fillHoleBands is null || fillHoleBands.Count == 0)
            DrawBackdropFill(dl, min, max, style, fill);
        else
        {
            var bands = new List<Vector2>(fillHoleBands);
            bands.Sort(static (a, b) => a.X.CompareTo(b.X));
            float y = min.Y;
            foreach (var raw in bands)
            {
                float top = MathF.Max(raw.X, min.Y), bottom = MathF.Min(raw.Y, max.Y);
                if (bottom <= top) continue;
                if (top > y) FillStrip(dl, min, max, style, fill, y, top);
                if (bottom > y) y = bottom;
            }
            if (y < max.Y) FillStrip(dl, min, max, style, fill, y, max.Y);
        }
        DrawBackdropEdge(dl, min, max, style, U32(edgeTint));
    }

    // One clipped copy of the full-rect fill. Same geometry + same UVs every call (the clip rect is the
    // only difference), which is why the tiled background does not shift between strips.
    private void FillStrip(ImDrawListPtr dl, Vector2 min, Vector2 max, in Backdrop style, uint tint,
                           float y0, float y1)
    {
        if (y1 <= y0) return;
        dl.PushClipRect(new Vector2(min.X, y0), new Vector2(max.X, y1), true);
        DrawBackdropFill(dl, min, max, style, tint);
        dl.PopClipRect();
    }

    private void DrawBackdropFill(ImDrawListPtr dl, Vector2 min, Vector2 max, in Backdrop style, uint tint)
    {
        var inner = new Vector2(min.X + style.InsetL * Scale, min.Y + style.InsetT * Scale);
        var outer = new Vector2(max.X - style.InsetR * Scale, max.Y - style.InsetB * Scale);
        if (outer.X <= inner.X || outer.Y <= inner.Y) return;

        var bg = Get(style.Bg);
        if (bg is null)
        {
            dl.AddRectFilled(inner, outer, U32(Fill));
            return;
        }

        // tile="true" with TileSize: one texture repeat every TileSize UI pixels.
        float tile = MathF.Max(style.TileSize * Scale, 1f);
        var uv = new Vector2((outer.X - inner.X) / tile, (outer.Y - inner.Y) / tile);
        dl.AddImage(Id(bg), inner, outer, Vector2.Zero, uv, tint);
    }

    /// <summary>
    /// The geometry + UVs of a horizontal SLICE of this backdrop's tiled fill, so the exact same band of
    /// panel art can be re-drawn OUTSIDE the ImGui pass (the GL overlay) and land pixel-identical.
    ///
    /// This is the other half of the fill-hole mechanism above: the char-select roster panel skips the
    /// band behind the additive row highlight (so the ImGui pass cannot dim the ADD blend), and that band
    /// is laid back down in the GL pass UNDER the glow instead. The panel therefore looks untouched -
    /// only the layering moved. Returns false when there is no tiled sheet or the slice is empty.
    /// </summary>
    public bool BackdropFillSlice(Vector2 min, Vector2 max, in Backdrop style, float y0, float y1,
                                  out uint texture, out Vector2 qMin, out Vector2 qMax,
                                  out Vector2 uv0, out Vector2 uv1)
    {
        texture = 0; qMin = default; qMax = default; uv0 = default; uv1 = default;

        var inner = new Vector2(min.X + style.InsetL * Scale, min.Y + style.InsetT * Scale);
        var outer = new Vector2(max.X - style.InsetR * Scale, max.Y - style.InsetB * Scale);
        if (outer.X <= inner.X || outer.Y <= inner.Y) return false;

        var bg = Get(style.Bg);
        if (bg is null) return false;

        float top = MathF.Max(y0, inner.Y), bottom = MathF.Min(y1, outer.Y);
        if (bottom <= top) return false;

        // Same tiling as DrawBackdropFill - one repeat every TileSize UI pixels, measured from inner.
        float tile = MathF.Max(style.TileSize * Scale, 1f);
        texture = bg.Texture!.Handle;
        qMin = new Vector2(inner.X, top);
        qMax = new Vector2(outer.X, bottom);
        uv0 = new Vector2(0f, (top - inner.Y) / tile);
        uv1 = new Vector2((outer.X - inner.X) / tile, (bottom - inner.Y) / tile);
        return true;
    }

    private void DrawBackdropEdge(ImDrawListPtr dl, Vector2 min, Vector2 max, in Backdrop style, uint tint)
    {
        var border = Get(style.Edge);
        float e = MathF.Max(style.EdgeSize * Scale, 2f);

        if (border is null)
        {
            uint gold = U32(GoldDim);
            var one = new Vector2(1f, 1f);
            dl.AddRect(min, max, gold);
            dl.AddRect(min + one, max - one, gold);
            dl.AddRect(min + one * 2f, max - one * 2f, U32(Shadow));
            return;
        }

        IntPtr id = Id(border);

        // Corners first: they are drawn at exactly EdgeSize and the edges run
        // between them. The bolt in the corner cell overhangs on purpose.
        Corner(dl, id, CornerTL, min, new Vector2(min.X + e, min.Y + e), tint);
        Corner(dl, id, CornerTR, new Vector2(max.X - e, min.Y), new Vector2(max.X, min.Y + e), tint);
        Corner(dl, id, CornerBL, new Vector2(min.X, max.Y - e), new Vector2(min.X + e, max.Y), tint);
        Corner(dl, id, CornerBR, new Vector2(max.X - e, max.Y - e), max, tint);

        float top = min.Y + e, bottom = max.Y - e;
        float left = min.X + e, right = max.X - e;

        if (bottom > top)
        {
            VerticalEdge(dl, id, EdgeLeft, min.X, min.X + e, top, bottom, e, tint);
            VerticalEdge(dl, id, EdgeRight, max.X - e, max.X, top, bottom, e, tint);
        }

        if (right > left)
        {
            HorizontalEdge(dl, id, EdgeTop, left, right, min.Y, min.Y + e, e, tint);
            HorizontalEdge(dl, id, EdgeBottom, left, right, max.Y - e, max.Y, e, tint);
        }
    }

    private static void Slice(int index, out float u0, out float u1)
    {
        u0 = index / 8f;
        u1 = (index + 1) / 8f;
    }

    private static void Corner(ImDrawListPtr dl, IntPtr id, int slice, Vector2 a, Vector2 b, uint tint)
    {
        Slice(slice, out float u0, out float u1);
        dl.AddImage(id, a, b, new Vector2(u0, 0f), new Vector2(u1, 1f), tint);
    }

    /// <summary>
    /// The maximum number of tiles one edge will emit. A guard, not a budget: a
    /// frame edge needs 5-20 and a slider track about 55. If something ever asks
    /// for thousands, the size arithmetic is wrong and silently drawing them
    /// would hide it.
    /// </summary>
    private const int MaxTilesPerEdge = 256;

    /// <summary>
    /// Drawn the way it is stored - standing up, repeated down its length.
    ///
    /// ONE QUAD PER TILE, NOT ONE QUAD WITH A UV RANGE OF 0..n.
    ///   The obvious implementation is a single AddImage whose v runs past 1 and
    ///   lets GL_REPEAT do the tiling. That is what run 3 did, and it drew a thin
    ///   smear instead of a riveted bar: the wrap mode set at texture creation was
    ///   not what the sampler actually used by the time ImGui issued the draw.
    ///   Rather than chase whose sampler state wins, this keeps v inside [0,1] on
    ///   every quad, which cannot be wrong under any wrap mode. Twenty extra quads
    ///   for a frame edge is nothing; being at the mercy of global sampler state
    ///   for the whole look of the UI is not.
    ///
    ///   The last tile is usually partial, so its rect AND its v are both cut to
    ///   the same fraction - shortening one without the other stretches it.
    /// </summary>
    private static void VerticalEdge(
        ImDrawListPtr dl, IntPtr id, int slice, float x0, float x1, float y0, float y1, float edge, uint tint)
    {
        Slice(slice, out float u0, out float u1);
        if (edge < 1f || y1 <= y0) return;

        int tiles = 0;
        for (float y = y0; y < y1 && tiles < MaxTilesPerEdge; y += edge, tiles++)
        {
            float yEnd = MathF.Min(y + edge, y1);
            float frac = (yEnd - y) / edge;
            dl.AddImage(id, new Vector2(x0, y), new Vector2(x1, yEnd),
                new Vector2(u0, 0f), new Vector2(u1, frac), tint);
        }
    }

    /// <summary>
    /// The same strip laid on its side by a quarter turn CLOCKWISE.
    ///
    /// Measured, not assumed: the TOP cell's bar runs down the LEFT of its cell
    /// and has to end up along the TOP of the strip; the BOTTOM cell's runs down
    /// the RIGHT and has to end up along the BOTTOM. One rotation satisfies both,
    /// which is the check that it is the right one.
    ///
    /// So display-Y maps to texture-U (the top of the strip is u0), and display-X
    /// maps to texture-V running backwards - which is also the axis it repeats on,
    /// hence one quad per tile with v going 1 -> 1-frac.
    /// </summary>
    private static void HorizontalEdge(
        ImDrawListPtr dl, IntPtr id, int slice, float x0, float x1, float y0, float y1, float edge, uint tint)
    {
        Slice(slice, out float u0, out float u1);
        if (edge < 1f || x1 <= x0) return;

        int tiles = 0;
        for (float x = x0; x < x1 && tiles < MaxTilesPerEdge; x += edge, tiles++)
        {
            float xEnd = MathF.Min(x + edge, x1);
            float frac = (xEnd - x) / edge;

            dl.AddImageQuad(id,
                new Vector2(x, y0), new Vector2(xEnd, y0), new Vector2(xEnd, y1), new Vector2(x, y1),
                new Vector2(u0, 1f), new Vector2(u0, 1f - frac),
                new Vector2(u1, 1f - frac), new Vector2(u1, 1f),
                tint);
        }
    }

    // ── the header plaque ────────────────────────────────────────────────────

    /// <summary>
    /// `UI-DialogBox-Header`, 256x64, anchored to the frame's TOP and hanging 12
    /// UI pixels ABOVE it, with the caption 14 pixels down from the plaque's top.
    /// Those three numbers are GameMenuFrame.xml's, not taste.
    /// </summary>
    public void HeaderPlaque(ImDrawListPtr dl, Vector2 frameMin, float frameWidth, string caption)
    {
        float w = 256f * Scale, h = 64f * Scale;
        float cx = frameMin.X + frameWidth * 0.5f;
        var min = new Vector2(cx - w * 0.5f, frameMin.Y - 12f * Scale);
        var max = min + new Vector2(w, h);

        var art = Get("dialog.header");
        if (art is not null)
            dl.AddImage(Id(art), min, max, Vector2.Zero, Vector2.One, White);

        var size = ImGui.CalcTextSize(caption);
        var pos = new Vector2(cx - size.X * 0.5f, min.Y + 14f * Scale);
        dl.AddText(pos + new Vector2(1f, 1f), U32(Shadow), caption);
        dl.AddText(pos, U32(Gold), caption);
    }

    // ── widgets ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A UIPanelButton. Blizzard draws it as one stretched quad of the top-left
    /// 0.625 x 0.6875 of a 128x32 texture - there is no nine-slice on a button,
    /// which is why they all look slightly squashed at odd widths in the real
    /// client too.
    /// </summary>
    public bool PanelButton(string label, Vector2 size, bool enabled = true)
        => PanelButton(label, size, enabled, out _);

    public bool PanelButton(string label, Vector2 size, bool enabled,
        out PanelButtonDrawState drawState)
    {
        string caption = Caption(label);
        Vector2 pos = ImGui.GetCursorScreenPos();

        var up = Get("button.up");
        if (up is null)
        {
            bool clicked;
            if (!enabled)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, Disabled);
                ImGui.Button(label, size);
                ImGui.PopStyleColor();
                clicked = false;
            }
            else clicked = ImGui.Button(label, size);
            bool heldFallback = enabled && ImGui.IsItemActive();
            bool hoveredFallback = enabled && ImGui.IsItemHovered();
            Vector2 textSizeFallback = ImGui.CalcTextSize(caption);
            Vector2 textMinFallback = pos + (size - textSizeFallback) * .5f;
            if (heldFallback) textMinFallback += new Vector2(1f, 1f) * Scale;
            drawState = new(pos, size, enabled, heldFallback, hoveredFallback,
                !enabled ? "disabled-fallback" : heldFallback ? "pushed-fallback" :
                hoveredFallback ? "highlighted-fallback" : "normal-fallback",
                "", "", ButtonUv1, ButtonUv2, textMinFallback, textSizeFallback,
                U32(!enabled ? Disabled : hoveredFallback ? Highlight : Normal), false, "");
            return clicked;
        }

        var dl = ImGui.GetWindowDrawList();

        bool pressed = ImGui.InvisibleButton(label, size) && enabled;
        bool held = enabled && ImGui.IsItemActive();
        bool hovered = enabled && ImGui.IsItemHovered();

        string stateRole;
        Piece art;
        if (!enabled)
        {
            Piece? disabled = Get("button.off");
            art = disabled ?? up;
            stateRole = disabled is null ? "NormalTexture" : "DisabledTexture";
        }
        else if (held)
        {
            Piece? down = Get("button.down");
            art = down ?? up;
            stateRole = down is null ? "NormalTexture" : "PushedTexture";
        }
        else
        {
            art = up;
            stateRole = "NormalTexture";
        }

        dl.AddImage(Id(art), pos, pos + size, ButtonUv1, ButtonUv2, White);

        // The frozen reference uses ADD. The texture was rebuilt as a white light mask at load,
        // so normal ImGui blending can only brighten the face and its black source field cannot
        // leak beyond the visible button as a rectangular veil.
        Piece? highlight = hovered && !held ? Get("button.hi") : null;
        if (highlight is Piece hi)
            dl.AddImage(Id(hi), pos, pos + size, ButtonUv1, ButtonUv2,
                ImGui.ColorConvertFloat4ToU32(
                    new Vector4(1f, 1f, 1f, GameMenuUiLaw.HighlightAlpha)));

        var textSize = ImGui.CalcTextSize(caption);
        var textPos = pos + (size - textSize) * 0.5f;
        if (held) textPos += new Vector2(1f, 1f) * Scale;

        // WoW panel buttons use the normal gold font and turn white only while
        // highlighted. Keeping Normal here made this shared custom-modal path the
        // lone white-at-rest family beside VanillaButton and dialog buttons.
        var colour = !enabled ? Disabled : hovered ? Highlight : Gold;
        dl.AddText(textPos + new Vector2(1f, 1f), U32(Shadow), caption);
        dl.AddText(textPos, U32(colour), caption);

        drawState = new(pos, size, enabled, held, hovered,
            !enabled ? "disabled" : held ? "pushed" : hovered ? "highlighted" : "normal",
            stateRole, art.Path, ButtonUv1, ButtonUv2, textPos, textSize, U32(colour),
            highlight is not null, highlight?.Path ?? "");

        return pressed;
    }

    // ── glue widgets (the login / main-menu screen) ──────────────────────────

    /// <summary>
    /// A GlueButtonTemplate - the red login/main-menu button. One stretched quad of the sheet's
    /// top-left [0,0.578125]x[0,0.75] region (glue/art.rs BUTTON_TC), a gold FRIZQT caption, and the
    /// additive Highlight overlay on hover. Placed at the current cursor screen pos, same contract
    /// as PanelButton - the caller does ImGui.SetCursorScreenPos(pos) first. Falls back to the
    /// in-game skinned button (then a plain ImGui button) if the glue art didn't load, so a missing
    /// texture can never make login unreachable.
    /// </summary>
    public bool GlueButton(string label, Vector2 size, bool enabled = true, float captionPx = 0f)
    {
        var up = Get("glue.btn.up");
        if (up is null) return PanelButton(label, size, enabled);

        string caption = Caption(label);
        var dl = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();

        bool pressed = ImGui.InvisibleButton(label, size) && enabled;
        bool held = enabled && ImGui.IsItemActive();
        bool hovered = enabled && ImGui.IsItemHovered();

        var art = !enabled ? (Get("glue.btn.off") ?? up)
                : held ? (Get("glue.btn.down") ?? up)
                : up;
        // Pressed = the DOWN sprite (a darker-red pill - NOT black; the real 1.12 texture has no
        // black), nudged down-right one UI px so it "drops" into the frame (WoW's pushed offset).
        // The earlier black fill was wrong; the fix that makes the pressed pill show at all is the
        // neutral-only de-halo above, which stopped eating the down sprite's dark red.
        var drawPos = held ? pos + new Vector2(1f, 1f) * Scale : pos;
        dl.AddImage(Id(art), drawPos, drawPos + size, GlueButtonUv1, GlueButtonUv2, White);

        if (hovered && !held && GlueTune.HoverGlow > 0.001f && Get("glue.btn.hi") is Piece hi)
        {
            // Keep the light inside the button's own rectangle. The authored highlight sits a touch
            // low relative to the Up face, so enlarge it vertically and bias it upward by one small
            // optical step; clipping prevents either end from escaping the decorative frame.
            float growY = size.Y * 0.04f;
            float liftY = size.Y * 0.02f;
            Vector2 highlightMin = pos + new Vector2(0f, -growY - liftY);
            Vector2 highlightMax = pos + size + new Vector2(0f, growY - liftY);
            dl.PushClipRect(pos, pos + size, true);
            for (float g = GlueTune.HoverGlow; g > 0.001f; g -= 1f)
                dl.AddImage(Id(hi), highlightMin, highlightMax,
                    GlueButtonHiUv1, GlueButtonHiUv2,
                    ImGui.ColorConvertFloat4ToU32(
                        new Vector4(1f, 1f, 1f, MathF.Min(1f, g))));
            dl.PopClipRect();
        }

        // The caption is sized to the button, not to the fixed ImGui font: FRIZQT on the real glue
        // buttons nearly fills their height. capSize = 42% of button height, then shrunk to fit if a
        // long label (MANAGE ACCOUNT) would otherwise run past ~86% of the width. The width is the
        // base-font measurement scaled by capSize/baseFs (the same trick GlueText uses) - ImFontPtr
        // has no CalcTextSizeA in this binding, and scaling the base measure is binding-independent.
        var font = ImGui.GetFont();
        float baseFs = ImGui.GetFontSize();
        var baseSize = ImGui.CalcTextSize(caption);
        // captionPx > 0 pins the caption to an EXPLICIT size instead of deriving it from the button
        // height. Without it, resizing a button silently resized its text (Nico on Enter World: "I can
        // only change the height and width and that auto sizes font"). The fit-to-width shrink below
        // still applies either way, so an over-long label can never run off the pill.
        float capSize = captionPx > 0.01f
            ? MathF.Max(captionPx, 1f)
            : MathF.Max(size.Y * GlueTune.CaptionSizeRatio, baseFs);
        float maxW = size.X * 0.86f;
        float widthAtCap = baseFs > 0f ? baseSize.X * (capSize / baseFs) : baseSize.X;
        if (widthAtCap > maxW && widthAtCap > 0f)
            capSize *= maxW / widthAtCap;
        float capScale = baseFs > 0f ? capSize / baseFs : 1f;
        var textSize = baseSize * capScale;

        var textPos = pos + (size - textSize) * 0.5f;
        // Optical centre: capitals sit in the upper em-box and descenders bias the visual mass down,
        // so a plain box-centre reads as sitting slightly low on the pill. Lift it (tunable).
        textPos.Y -= textSize.Y * GlueTune.CaptionLift;
        if (held) textPos += new Vector2(1f, 1f) * Scale;

        var colour = !enabled ? Disabled : hovered ? Highlight : GlueGold;
        // The caption's OWN shadow, a SMOOTH offset proportional to the caption size (no rounding or
        // 1px floor - those quantised the slider so its lower half all snapped to 1px and 0 could not
        // turn the shadow off). The caption is large, so a sub-pixel offset reads fine; 0 = no shadow.
        float capShadow = capSize * GlueTune.CaptionShadowRatio;
        if (capShadow > 0.01f)
            dl.AddText(font, capSize, textPos + new Vector2(capShadow, capShadow), U32(GlueTune.ShadowColor), caption);
        OutlineText(dl, font, capSize, textPos, caption);
        dl.AddText(font, capSize, textPos, U32(colour), caption);

        return pressed;
    }

    /// <summary>Draw a loaded skin texture (by key) as one quad over [min,max]. No-op if absent.</summary>
    public void GlueImage(ImDrawListPtr dl, string key, Vector2 min, Vector2 max)
    {
        if (Get(key) is Piece p)
            dl.AddImage(Id(p), min, max, Vector2.Zero, Vector2.One, White);
    }

    /// <summary>Draw a loaded sheet over [min,max] with a colour/alpha tint (e.g. a warm, translucent
    /// glow for the char-select row highlight so the backdrop shows through, like the OG ADD blend).</summary>
    public void GlueImage(ImDrawListPtr dl, string key, Vector2 min, Vector2 max, Vector4 tint)
    {
        if (Get(key) is Piece p)
            dl.AddImage(Id(p), min, max, Vector2.Zero, Vector2.One, U32(tint));
    }

    /// <summary>Draw a sub-rect (uv0..uv1) of a loaded sheet over [min,max]. Returns false if the
    /// sheet is absent (so the caller can fall back to text). Used by the character-create icon grids.</summary>
    public bool GlueImageUv(ImDrawListPtr dl, string key, Vector2 min, Vector2 max, Vector2 uv0, Vector2 uv1)
    {
        if (Get(key) is not Piece p) return false;
        dl.AddImage(Id(p), min, max, uv0, uv1, White);
        return true;
    }

    /// <summary>True if a skin piece is loaded (the login layout asks before it relies on art).</summary>
    public bool Has(string key) => Get(key) is not null;

    /// <summary>The GL texture handle for a loaded piece, or 0 if absent - lets a raw GL pass (the
    /// additive glue overlay) bind glue art directly, outside the ImGui draw list.</summary>
    public uint TextureHandle(string key) => Get(key) is Piece p && p.Texture is { } t ? t.Handle : 0u;

    /// <summary>
    /// UICheckButtonTemplate: 32x32 box, check mark over it at the same rect. When <paramref
    /// name="labelPx"/> &gt; 0 the caption is drawn at that explicit pixel size (the glue login needs
    /// a glue-sized label; the ambient ImGui font is tiny next to the s-scaled box); 0 keeps the
    /// in-game behaviour of drawing at the current font size.
    /// </summary>
    public bool CheckBox(string label, ref bool value, float boxSize = 26f, float labelPx = 0f)
    {
        var box = Get("check.box");
        if (box is null) return ImGui.Checkbox(label, ref value);

        string caption = Caption(label);
        float s = boxSize * Scale;

        var dl = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        var font = ImGui.GetFont();
        float baseFs = ImGui.GetFontSize();
        float lblScale = labelPx > 0f && baseFs > 0f ? labelPx / baseFs : 1f;
        var textSize = ImGui.CalcTextSize(caption) * lblScale;

        var hit = new Vector2(s + 6f * Scale + textSize.X, MathF.Max(s, textSize.Y));
        bool pressed = ImGui.InvisibleButton(label, hit);
        bool hovered = ImGui.IsItemHovered();
        bool held = ImGui.IsItemActive();

        if (pressed) value = !value;

        var boxMin = new Vector2(pos.X, pos.Y + (hit.Y - s) * 0.5f);
        var boxMax = boxMin + new Vector2(s, s);

        var art = held ? (Get("check.down") ?? box) : box;
        dl.AddImage(Id(art), boxMin, boxMax, Vector2.Zero, Vector2.One, White);

        // FULL strength, not the half-alpha this carried. The 0.5 was holding back the dark veil
        // that check.hi's un-rebuilt black field painted over the box; with the field now properly
        // transparent it was only halving the glow itself.
        if (hovered && Get("check.hi") is Piece hi)
            dl.AddImage(Id(hi), boxMin, boxMax, Vector2.Zero, Vector2.One, White);

        if (value && Get("check.mark") is Piece mark)
            dl.AddImage(Id(mark), boxMin, boxMax, Vector2.Zero, Vector2.One, White);

        // Gold, not white: every checkbox label in the real Video Options frame
        // is GameFontNormal, which is the yellow face. White is what the BUTTONS
        // use.
        var textPos = new Vector2(boxMax.X + 6f * Scale, pos.Y + (hit.Y - textSize.Y) * 0.5f);
        // The 1.12 drop shadow (down-right): every glue label carries it, and the Remember-checkbox
        // caption is a glue label like the rest. At an explicit label size the shadow scales with it.
        var lblColour = hovered ? Highlight : Gold;
        if (labelPx > 0f)
        {
            float lblShadow = MathF.Max(1f, MathF.Round(labelPx * GlueTune.ShadowOffsetRatio));
            dl.AddText(font, labelPx, textPos + new Vector2(lblShadow, lblShadow), U32(GlueTune.ShadowColor), caption);
            dl.AddText(font, labelPx, textPos, U32(lblColour), caption);
        }
        else
        {
            dl.AddText(textPos + new Vector2(1f, 1f) * Scale, U32(GlueTune.ShadowColor), caption);
            dl.AddText(textPos, U32(lblColour), caption);
        }

        return pressed;
    }

    // Drag anchor for SliderFloat. The value must be computed against the track
    // geometry captured when the drag STARTED, never the current frame's: a
    // scale slider changes the very window that hosts it, so live geometry
    // makes value and track a feedback loop (value moves track, track re-derives
    // value) that runs away from the cursor or oscillates between the clamps.
    private string? _sliderDragId;
    private float _sliderDragOriginX;
    private float _sliderDragUsable;
    private float _sliderDragKnobHalf;

    /// <summary>True while any WowSkin slider knob is held.</summary>
    public bool SliderDragActive => _sliderDragId is not null;

    /// <summary>
    /// A 1.12 options slider: caption above, value on the right, the SliderTrack
    /// backdrop as the groove and `UI-SliderBar-Button-Horizontal` as the knob.
    /// </summary>
    public bool SliderFloat(
        string id, string caption, ref float value, float lo, float hi,
        string valueText, float width, float trackHeight = 17f, float knobSize = 32f)
    {
        var knobArt = Get("slider.knob");
        if (knobArt is null)
        {
            ImGui.SetNextItemWidth(width);
            return ImGui.SliderFloat(caption + "##" + id, ref value, lo, hi, valueText);
        }

        var dl = ImGui.GetWindowDrawList();
        float th = trackHeight * Scale;
        float knob = knobSize * Scale;
        float rowH = ImGui.GetTextLineHeight();

        // Caption row: name on the left, current value on the right. Both carry the same 1px
        // drop shadow every other label in this menu has (CheckBox, GlueButton) - sliders are
        // most of a Video/Sound/Interface Options page, and shadowless text was the main
        // contributor to it reading as barely legible over the group-box backdrop.
        var top = ImGui.GetCursorScreenPos();
        var shadow = new Vector2(1f, 1f) * Scale;
        dl.AddText(top + shadow, U32(GlueTune.ShadowColor), caption);
        dl.AddText(top, U32(Gold), caption);
        var vs = ImGui.CalcTextSize(valueText);
        var valuePos = new Vector2(top.X + width - vs.X, top.Y);
        dl.AddText(valuePos + shadow, U32(GlueTune.ShadowColor), valueText);
        dl.AddText(valuePos, U32(Normal), valueText);
        ImGui.Dummy(new Vector2(width, rowH));

        // Track row. The hit area is knob-tall so the knob is easy to grab.
        var pos = ImGui.GetCursorScreenPos();
        var hit = new Vector2(width, knob);
        ImGui.InvisibleButton("##slider" + id, hit);

        // Anchor the drag to the geometry at mouse-down; see _sliderDragId.
        if (ImGui.IsItemActivated())
        {
            _sliderDragId = id;
            _sliderDragOriginX = pos.X;
            _sliderDragUsable = MathF.Max(width - knob, 1f);
            _sliderDragKnobHalf = knob * 0.5f;
        }
        bool active = ImGui.IsItemActive() && _sliderDragId == id;
        if (ImGui.IsItemDeactivated() && _sliderDragId == id) _sliderDragId = null;

        float range = hi - lo;
        float t = range > 1e-6f ? Math.Clamp((value - lo) / range, 0f, 1f) : 0f;

        bool changed = false;
        if (active)
        {
            float mouseX = ImGui.GetIO().MousePos.X - _sliderDragOriginX - _sliderDragKnobHalf;
            float nt = Math.Clamp(mouseX / _sliderDragUsable, 0f, 1f);
            float nv = lo + nt * range;
            if (MathF.Abs(nv - value) > 1e-6f) { value = nv; t = nt; changed = true; }
        }

        float mid = pos.Y + knob * 0.5f;
        var trackMin = new Vector2(pos.X + knob * 0.5f, mid - th * 0.5f);
        var trackMax = new Vector2(pos.X + width - knob * 0.5f, mid + th * 0.5f);
        if (trackMax.X > trackMin.X) DrawBackdrop(dl, trackMin, trackMax, SliderTrack);

        float cx = trackMin.X + (trackMax.X - trackMin.X) * t;
        var knobMin = new Vector2(cx - knob * 0.5f, mid - knob * 0.5f);
        dl.AddImage(Id(knobArt), knobMin, knobMin + new Vector2(knob, knob),
            Vector2.Zero, Vector2.One, White);

        return changed;
    }

    private static string Caption(string label)
    {
        int i = label.IndexOf("##", StringComparison.Ordinal);
        return i >= 0 ? label[..i] : label;
    }

    // ── ImGui style ──────────────────────────────────────────────────────────

    private int _colorsPushed;
    private int _varsPushed;

    /// <summary>
    /// Colours and metrics for the ImGui widgets this class does not draw itself
    /// (trees, combos, scrollbars, text fields), so they sit inside a Blizzard
    /// frame without looking pasted in.
    /// </summary>
    public void PushStyle()
    {
        _colorsPushed = 0;
        _varsPushed = 0;

        void Col(ImGuiCol which, Vector4 c) { ImGui.PushStyleColor(which, c); _colorsPushed++; }
        void Var(ImGuiStyleVar which, float v) { ImGui.PushStyleVar(which, v); _varsPushed++; }
        void Var2(ImGuiStyleVar which, Vector2 v) { ImGui.PushStyleVar(which, v); _varsPushed++; }

        // The frame paints its own background, so ImGui's must not show through.
        Col(ImGuiCol.WindowBg, new Vector4(0f, 0f, 0f, 0f));
        Col(ImGuiCol.ChildBg, new Vector4(0f, 0f, 0f, 0f));
        // Stays OPAQUE on purpose - combo dropdowns and tooltips need it. The
        // settings frame pushes a transparent PopupBg around its own Begin, so
        // the world shows through the frame; see DrawSettingsModal.
        Col(ImGuiCol.PopupBg, new Vector4(Fill.X, Fill.Y, Fill.Z, 0.98f));
        Col(ImGuiCol.Border, GoldDim);
        Col(ImGuiCol.BorderShadow, new Vector4(0f, 0f, 0f, 0f));

        // NO DIM. UI-DialogBox-Background is flat black at 60% alpha - the
        // "stone" you see inside a real 1.12 dialog is THE WORLD, darkened by
        // that one texture and nothing else. ImGui's modal dim on top of it took
        // the panel to 78% black, which reads as solid, and killed the single
        // most characteristic thing about the frame.
        Col(ImGuiCol.ModalWindowDimBg, new Vector4(0f, 0f, 0f, 0f));

        Col(ImGuiCol.Text, Normal);
        Col(ImGuiCol.TextDisabled, Muted);

        Col(ImGuiCol.FrameBg, new Vector4(0.02f, 0.02f, 0.02f, 0.85f));
        Col(ImGuiCol.FrameBgHovered, new Vector4(0.16f, 0.13f, 0.08f, 0.90f));
        Col(ImGuiCol.FrameBgActive, new Vector4(0.24f, 0.19f, 0.11f, 0.95f));

        Col(ImGuiCol.Header, new Vector4(0.20f, 0.16f, 0.09f, 0.75f));
        Col(ImGuiCol.HeaderHovered, new Vector4(0.31f, 0.25f, 0.14f, 0.85f));
        Col(ImGuiCol.HeaderActive, new Vector4(0.39f, 0.32f, 0.18f, 0.95f));

        Col(ImGuiCol.Button, new Vector4(0.16f, 0.13f, 0.08f, 0.90f));
        Col(ImGuiCol.ButtonHovered, new Vector4(0.28f, 0.23f, 0.13f, 0.95f));
        Col(ImGuiCol.ButtonActive, new Vector4(0.39f, 0.32f, 0.18f, 1f));

        Col(ImGuiCol.SliderGrab, GoldDim);
        Col(ImGuiCol.SliderGrabActive, Gold);
        Col(ImGuiCol.CheckMark, Gold);

        Col(ImGuiCol.ScrollbarBg, new Vector4(0f, 0f, 0f, 0.35f));
        Col(ImGuiCol.ScrollbarGrab, GoldDim);
        Col(ImGuiCol.ScrollbarGrabHovered, Gold);
        Col(ImGuiCol.ScrollbarGrabActive, Gold);

        Col(ImGuiCol.Separator, GoldDim);
        Col(ImGuiCol.SeparatorHovered, Gold);
        Col(ImGuiCol.SeparatorActive, Gold);

        // Vanilla's panels are square. Rounding is the loudest tell that a frame
        // was not drawn by Blizzard, so every radius goes to zero.
        Var(ImGuiStyleVar.WindowRounding, 0f);
        Var(ImGuiStyleVar.ChildRounding, 0f);
        Var(ImGuiStyleVar.FrameRounding, 0f);
        Var(ImGuiStyleVar.PopupRounding, 0f);
        Var(ImGuiStyleVar.ScrollbarRounding, 0f);
        Var(ImGuiStyleVar.GrabRounding, 0f);
        Var(ImGuiStyleVar.TabRounding, 0f);
        Var(ImGuiStyleVar.WindowBorderSize, 0f);
        Var(ImGuiStyleVar.FrameBorderSize, 0f);
        Var(ImGuiStyleVar.ScrollbarSize, 11f * Scale);

        Var2(ImGuiStyleVar.FramePadding, new Vector2(6f, 4f) * Scale);
        Var2(ImGuiStyleVar.ItemSpacing, new Vector2(8f, 6f) * Scale);
        Var2(ImGuiStyleVar.WindowPadding, new Vector2(24f, 24f) * Scale);
    }

    public void PopStyle()
    {
        if (_varsPushed > 0) { ImGui.PopStyleVar(_varsPushed); _varsPushed = 0; }
        if (_colorsPushed > 0) { ImGui.PopStyleColor(_colorsPushed); _colorsPushed = 0; }
    }

    public void Dispose()
    {
        foreach (var p in _pieces) { p.Texture?.Dispose(); p.Texture = null; }
        FoundCount = 0;
    }
}

/// <summary>
/// Live-tunable knobs for the glue login screen, edited by the in-client tuning modal and read by
/// the glue widgets (GlueButton, CheckBox) and the login layout every frame. Static because the
/// login screen is a singleton and there is exactly one set of values on screen at a time; the
/// defaults are the values these had when they were hard-coded, so nothing changes until a slider
/// moves. Values that are "UI units" get multiplied by the login scale s (= height/768) at use.
/// </summary>
public static class GlueTune
{
    // Buttons (the red GlueButtons). Defaults are the values dialed in via the tuning modal.
    public static float ButtonHeightMul  = 1.086f;  // multiplies every glue button's height
    public static float CaptionSizeRatio = 0.389f;  // gold caption height as a fraction of button height
    public static float CaptionLift      = 0.177f;  // upward optical nudge, as a fraction of caption height
    public static float HoverGlow        = 0.775f;  // strength of the hover highlight (Nico baked; 0 = off, stacks past 1)

    // The 1.12 drop shadow. Two ratios on purpose: the small labels bottom out at the 1px floor, so
    // ShadowOffsetRatio can be pushed up for them without inflating anything; the red-button caption
    // is large enough to clear that floor, so it gets its OWN (tighter) ratio - both stay proportional
    // to their text, they just no longer share one knob (which is what made the button shadow bloat).
    public static float ShadowAlpha        = 1.00f;  // 1 = opaque black (the reference); lower softens it
    public static float ShadowOffsetRatio  = 0.08f;  // label / field / checkbox / version-text shadow ratio
    public static float OutlinePx          = 0.038f; // black glyph OUTLINE thickness (fraction of font px); 0 = off
    public static float CaptionShadowRatio = 0.05f;  // the red-button caption's own shadow ratio

    // Edit fields.
    public static float FieldLabelUnits = 17f;      // "Account Name" / "Account Password" label size
    public static float TypedTextUnits  = 18f;      // the typed account/password line (benilla ARIALN 18)

    // The Remember-Account-Name checkbox.
    public static float CheckBoxUnits   = 24.8f;    // the box itself
    public static float CheckLabelUnits = 13.1f;    // the caption beside it

    // The 3D glue scene behind the chrome (the burning-gate model). Two independent size knobs: the
    // model-space swirling embers, and the world-space brazier flames.
    public static float ParticleSize    = 0.50f;    // ember / swirl size (model-space sprites)
    public static float BrazierSize     = 1.00f;    // brazier flame size (world-space sprites)

    // The char-select selected-row highlight tint (a warm, translucent glow over the brick like the OG
    // ADD blend, vs a flat opaque yellow). RGBA; tuned from the char-select tuning modal.
    public static Vector4 SelectHi = new(0.776f, 1.0f, 0.0f, 0.749f); // R198 G255 B0 A191 (Nico baked); additive tint, A = coverage
    public static float SelectHiGain = 3.08f;  // ADDITIVE brightness: multiplies the added light (Nico baked; >1 brighter, too high washes)
    public static float SelectHiContrast = 2.2f;// crispness: >1 drops the mid-tone fill (translucent interior) so the bright border pops
    public static bool  SelectHiOnTop = false; // draw the glow OVER the HUD (covers the row TEXT) vs UNDER it (text in front)
    // With the glow UNDER the HUD the row text is naturally in front (benilla order: panel -> glow -> text)
    // but the translucent roster panel would dim the ADD card. This cuts the panel FILL away in a band
    // behind each lit card - the nine-sliced EDGE still draws whole - so the glow reads at full strength.
    public static bool  SelectHiPanelHole = true;
    public static float RosterAlpha  = 0.8f;   // char-select right-panel opacity (Nico baked); the ADD glow is no longer dimmed by it, so this is purely how much cobblestone reads through
    // Character-select chrome sizes (1024x768 units, scaled by s). These were hardcoded: Enter World
    // 200x60 at 30 off the bottom, Change Realm 30 tall inset 12 at 32 down the roster panel.
    public static float EnterWorldW = 187.3f, EnterWorldH = 60.6f, EnterWorldBottom = 30f;
    /// <summary>Enter World's caption size in 1024x768 units. 0 = derive it from the button height
    /// (the shared CaptionSizeRatio); anything else pins it, so resizing the pill leaves the text be.</summary>
    public static float EnterWorldTextPx = 18.2f;
    public static float ChangeRealmH = 39.8f, ChangeRealmTop = 32f, ChangeRealmInset = 60f;
    public static float CreateCharH = 48.6f, CreateCharBottom = 12f, CreateCharInset = 27.7f;
    /// <summary>Create New Character's caption size, same contract as EnterWorldTextPx: 0 = derive it
    /// from the button height, anything else pins it so resizing the button leaves the text be.</summary>
    public static float CreateCharTextPx = 14.3f;
    // The logon progress GlueDialog (the riveted DialogFrame box shown while connecting).
    public static float LogonBoxW = 380f, LogonBoxH = 150f, LogonBoxDY = 0f;
    public static float LogonTitlePx = 18f, LogonStatusPx = 12f, LogonBtnW = 140f, LogonBtnH = 34f;
    // The stylized model-rotate pair, centred UNDER Enter World (the 1.12 placement).
    public static float RotateSize = 54.4f, RotateGap = -12.7f, RotateDX = 1.5f, RotateTop = -18.1f;
    public static float SelectHiInsetX = 1.472f;// row-highlight card: horizontal inset from the frame edges
    public static float SelectHiTop    = -9.3f;// row-highlight card: top offset from the row top
    public static float SelectHiHeight = 76.9f;// row-highlight card: height

    /// <summary>The shadow colour built from <see cref="ShadowAlpha"/> - opaque black by default.</summary>
    public static Vector4 ShadowColor => new(0f, 0f, 0f, ShadowAlpha);

    public static void Reset()
    {
        ButtonHeightMul = 1.086f; CaptionSizeRatio = 0.389f; CaptionLift = 0.177f; HoverGlow = 0.775f;
        ShadowAlpha = 1.00f; ShadowOffsetRatio = 0.08f; OutlinePx = 0.038f; CaptionShadowRatio = 0.05f;
        FieldLabelUnits = 17f; TypedTextUnits = 18f;
        CheckBoxUnits = 24.8f; CheckLabelUnits = 13.1f;
        ParticleSize = 0.50f; BrazierSize = 1.00f;
        SelectHi = new(0.776f, 1.0f, 0.0f, 0.749f); SelectHiGain = 3.08f; SelectHiContrast = 2.2f; SelectHiOnTop = false; SelectHiPanelHole = true; RosterAlpha = 0.8f;
        SelectHiInsetX = 1.472f; SelectHiTop = -9.3f; SelectHiHeight = 76.9f;
        EnterWorldW = 187.3f; EnterWorldH = 60.6f; EnterWorldBottom = 30f; EnterWorldTextPx = 18.2f;
        ChangeRealmH = 39.8f; ChangeRealmTop = 32f; ChangeRealmInset = 60f;
        CreateCharH = 48.6f; CreateCharBottom = 12f; CreateCharInset = 27.7f; CreateCharTextPx = 14.3f;
        LogonBoxW = 380f; LogonBoxH = 150f; LogonBoxDY = 0f;
        LogonTitlePx = 18f; LogonStatusPx = 12f; LogonBtnW = 140f; LogonBtnH = 34f;
        RotateSize = 54.4f; RotateGap = -12.7f; RotateDX = 1.5f; RotateTop = -18.1f;
    }

    /// <summary>Dump the current values to the console in a copy-pasteable form, so a dialed-in look
    /// can be read off and baked back into the defaults.</summary>
    public static void LogValues()
    {
        Console.WriteLine(
            "[glue-tune] ButtonHeightMul=" + ButtonHeightMul.ToString("0.###") +
            " CaptionSizeRatio=" + CaptionSizeRatio.ToString("0.###") +
            " CaptionLift=" + CaptionLift.ToString("0.###") +
            " HoverGlow=" + HoverGlow.ToString("0.###") +
            " OutlinePx=" + OutlinePx.ToString("0.###") +
            " ShadowAlpha=" + ShadowAlpha.ToString("0.###") +
            " ShadowOffsetRatio=" + ShadowOffsetRatio.ToString("0.###") +
            " CaptionShadowRatio=" + CaptionShadowRatio.ToString("0.###") +
            " FieldLabelUnits=" + FieldLabelUnits.ToString("0.#") +
            " TypedTextUnits=" + TypedTextUnits.ToString("0.#") +
            " CheckBoxUnits=" + CheckBoxUnits.ToString("0.#") +
            " CheckLabelUnits=" + CheckLabelUnits.ToString("0.#") +
            " ParticleSize=" + ParticleSize.ToString("0.###") +
            " BrazierSize=" + BrazierSize.ToString("0.###") +
            " SelectHi=" + SelectHi.X.ToString("0.##") + "/" + SelectHi.Y.ToString("0.##") + "/" + SelectHi.Z.ToString("0.##") + "/" + SelectHi.W.ToString("0.##") +
            " SelectHiGain=" + SelectHiGain.ToString("0.###") +
            " SelectHiContrast=" + SelectHiContrast.ToString("0.###") +
            " SelectHiOnTop=" + SelectHiOnTop +
            " SelectHiPanelHole=" + SelectHiPanelHole +
            " RosterAlpha=" + RosterAlpha.ToString("0.###") +
            " SelectHiInsetX=" + SelectHiInsetX.ToString("0.###") +
            " SelectHiTop=" + SelectHiTop.ToString("0.###") +
            " SelectHiHeight=" + SelectHiHeight.ToString("0.###") +
            " EnterWorld=" + EnterWorldW.ToString("0.#") + "x" + EnterWorldH.ToString("0.#") +
            "@" + EnterWorldBottom.ToString("0.#") + " text" + EnterWorldTextPx.ToString("0.#") +
            " ChangeRealm=h" + ChangeRealmH.ToString("0.#") + " top" + ChangeRealmTop.ToString("0.#") +
            " inset" + ChangeRealmInset.ToString("0.#") +
            " CreateChar=h" + CreateCharH.ToString("0.#") + " bottom" + CreateCharBottom.ToString("0.#") +
            " inset" + CreateCharInset.ToString("0.#") + " text" + CreateCharTextPx.ToString("0.#") +
            " Rotate=" + RotateSize.ToString("0.#") + " gap" + RotateGap.ToString("0.#") +
            " dx" + RotateDX.ToString("0.#") + " top" + RotateTop.ToString("0.#"));
    }
}

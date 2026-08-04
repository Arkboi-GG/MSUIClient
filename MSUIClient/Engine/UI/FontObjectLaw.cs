namespace MSUIClient.Engine.UI;

/// <summary>
/// One resolved FrameXML font object: face path, height, default color, shadow, outline.
/// Inheritance is already flattened - consumers never chase an inherits= chain.
/// </summary>
/// <param name="Face">MPQ-internal TTF path (the FontFace constants).</param>
/// <param name="Height">FrameXML FontHeight - the em, per the raster law.</param>
/// <param name="Color">Default text color, ImGui ABGR packing.</param>
/// <param name="ShadowColor">Shadow color (ABGR) at FrameXML (1,-1) = screen right/down, or
/// null for no shadow. 1.12 declares no other shadow offset, so only the color varies.</param>
/// <param name="Outline">0 none, 1 NORMAL (1px), 2 THICK (2px, +1 extra advance).</param>
public readonly record struct FontObjectSpec(
    string Face, float Height, uint Color, uint? ShadowColor, int Outline);

/// <summary>The four typeface paths 1.12's Fonts.xml names, normalised to one casing.</summary>
public static class FontFace
{
    public const string FrizQt = @"Fonts\FRIZQT__.TTF";
    public const string ArialN = @"Fonts\ARIALN.TTF";
    public const string Morpheus = @"Fonts\MORPHEUS.TTF";
    public const string Skurri = @"Fonts\SKURRI.TTF";
}

/// <summary>
/// The complete build-5875 `Interface\FrameXML\Fonts.xml` font-object registry, transcribed
/// 2026-08-04 directly from the MPQ (patch chain) and cross-checked against Benilla's annotated
/// copy. Inheritance is resolved at transcription time: the `MasterFont` chain contributes the
/// (1,-1) black shadow and nothing else; fonts outside the chain are shadowless unless they
/// declare their own.
///
/// WHY THIS EXISTS
///   Call sites that hand-pick a height/color/shadow reproduce the "Spellbook title drawn 14px
///   white when the XML says GameFontNormal = 12px gold shadowed" class of bug. Panels name a
///   font object - exactly what FrameXML does - and every property rides along. A runtime color
///   override (Lua SetTextColor, e.g. the passive-spell yellow) is the caller's one legitimate
///   knob; heights and shadows are never overridden ad hoc.
///
/// TRANSCRIPTION NOTES (all from the MPQ copy)
///   - NORMAL_FONT_COLOR is {1.0, 0.82, 0} = (255,209,0). The engine's separate byte constant
///     for tooltip default gold is (255,210,0) - one step apart, both kept where each applies.
///   - GameFontHighlightSmallOutline declares a SHADOW, not an outline, despite its name.
///   - ZoneTextFont/WorldMapTextFont carry a bare empty Shadow element (zero offset - no
///     visible pixels): transcribed as no shadow.
///   - SubZoneTextFont declares no Color: FontString default white.
///   - NumberFontNormalSmall is monochrome="true" in the XML (aliased raster); not reproduced -
///     it draws antialiased like the rest until a capture proves it matters.
/// </summary>
public static class FontObjectLaw
{
    private const uint Gold = 0xff00d1ff;        // (255,209,0)  {1.0, 0.82, 0}
    private const uint White = 0xffffffff;
    private const uint Black = 0xff000000;
    private const uint Gray = 0xff808080;        // {0.5, 0.5, 0.5}
    private const uint DarkGray = 0xff595959;    // {0.35, 0.35, 0.35}
    private const uint LightGray = 0xff999999;   // {0.6, 0.6, 0.6}
    private const uint Green = 0xff1aff1a;       // {0.1, 1.0, 0.1}
    private const uint Red = 0xff1a1aff;         // {1.0, 0.1, 0.1}
    private const uint SystemYellow = 0xff00ffff;    // {1.0, 1.0, 0}
    private const uint QuestBrown = 0xff002e4d;      // {0.30, 0.18, 0}
    private const uint ParchmentBrown = 0xff0f1f2e;  // {0.18, 0.12, 0.06}
    private const uint SubSpellBrown = 0xff003359;   // {0.35, 0.2, 0}
    private const uint ZoneCream = 0xffc2edff;       // {1.0, 0.9294, 0.7607}
    private const uint QuestTitleShadowBrown = 0xff0d597d; // {0.49, 0.35, 0.05}

    public static readonly IReadOnlyDictionary<string, FontObjectSpec> Objects =
        new Dictionary<string, FontObjectSpec>
    {
        // FRIZQT body family (MasterFont chain: black shadow).
        ["SystemFont"] = new(FontFace.FrizQt, 15f, SystemYellow, Black, 0),
        ["GameFontNormal"] = new(FontFace.FrizQt, 12f, Gold, Black, 0),
        ["GameFontHighlight"] = new(FontFace.FrizQt, 12f, White, Black, 0),
        ["GameFontDisable"] = new(FontFace.FrizQt, 12f, Gray, Black, 0),
        ["GameFontGreen"] = new(FontFace.FrizQt, 12f, Green, Black, 0),
        ["GameFontRed"] = new(FontFace.FrizQt, 12f, Red, Black, 0),
        // GameFontBlack/White: 12px FRIZQT with NO MasterFont chain - shadowless.
        ["GameFontBlack"] = new(FontFace.FrizQt, 12f, Black, null, 0),
        ["GameFontWhite"] = new(FontFace.FrizQt, 12f, White, null, 0),
        // Small family (shadowed).
        ["GameFontNormalSmall"] = new(FontFace.FrizQt, 10f, Gold, Black, 0),
        ["GameFontHighlightSmall"] = new(FontFace.FrizQt, 10f, White, Black, 0),
        ["GameFontDisableSmall"] = new(FontFace.FrizQt, 10f, Gray, Black, 0),
        ["GameFontDarkGraySmall"] = new(FontFace.FrizQt, 10f, DarkGray, Black, 0),
        ["GameFontGreenSmall"] = new(FontFace.FrizQt, 10f, Green, Black, 0),
        ["GameFontRedSmall"] = new(FontFace.FrizQt, 10f, Red, Black, 0),
        ["GameFontHighlightSmallOutline"] = new(FontFace.FrizQt, 10f, White, Black, 0),
        // Large/huge family (shadowed).
        ["GameFontNormalLarge"] = new(FontFace.FrizQt, 16f, Gold, Black, 0),
        ["GameFontHighlightLarge"] = new(FontFace.FrizQt, 16f, White, Black, 0),
        ["GameFontDisableLarge"] = new(FontFace.FrizQt, 16f, Gray, Black, 0),
        ["GameFontGreenLarge"] = new(FontFace.FrizQt, 16f, Green, Black, 0),
        ["GameFontRedLarge"] = new(FontFace.FrizQt, 16f, Red, Black, 0),
        ["GameFontNormalHuge"] = new(FontFace.FrizQt, 20f, Gold, Black, 0),
        // ARIALN number family: outlined, white, NO shadow.
        ["NumberFontNormal"] = new(FontFace.ArialN, 14f, White, null, 1),
        ["NumberFontNormalYellow"] = new(FontFace.ArialN, 14f, Gold, null, 1),
        ["NumberFontNormalSmall"] = new(FontFace.ArialN, 12f, White, null, 2),
        ["NumberFontNormalSmallGray"] = new(FontFace.ArialN, 12f, LightGray, null, 2),
        ["NumberFontNormalLarge"] = new(FontFace.ArialN, 16f, White, null, 1),
        ["NumberFontNormalHuge"] = new(FontFace.Skurri, 30f, White, null, 2),
        // Chat: ARIALN with its own declared shadow.
        ["ChatFontNormal"] = new(FontFace.ArialN, 14f, White, Black, 0),
        // Quest/parchment: dark text on parchment is SHADOWLESS except QuestTitleFont,
        // whose shadow is brown, not black.
        ["QuestTitleFont"] = new(FontFace.Morpheus, 18f, Black, QuestTitleShadowBrown, 0),
        ["QuestFont"] = new(FontFace.FrizQt, 13f, Black, null, 0),
        ["QuestFontNormalSmall"] = new(FontFace.FrizQt, 12f, QuestBrown, null, 0),
        ["QuestFontHighlight"] = new(FontFace.FrizQt, 14f, Black, null, 0),
        ["ItemTextFontNormal"] = new(FontFace.Morpheus, 15f, ParchmentBrown, null, 0),
        ["MailTextFontNormal"] = new(FontFace.Morpheus, 15f, ParchmentBrown, null, 0),
        ["InvoiceTextFontNormal"] = new(FontFace.FrizQt, 12f, ParchmentBrown, null, 0),
        ["InvoiceTextFontSmall"] = new(FontFace.FrizQt, 10f, ParchmentBrown, null, 0),
        // Spellbook rank/category line.
        ["SubSpellFont"] = new(FontFace.FrizQt, 10f, SubSpellBrown, null, 0),
        // Dialog (StaticPopup) buttons.
        ["DialogButtonNormalText"] = new(FontFace.FrizQt, 16f, Gold, null, 0),
        ["DialogButtonHighlightText"] = new(FontFace.FrizQt, 16f, White, null, 0),
        // Zone splash / world map (the 102 rides the em cap to 32).
        ["ZoneTextFont"] = new(FontFace.FrizQt, 102f, ZoneCream, null, 2),
        ["SubZoneTextFont"] = new(FontFace.FrizQt, 26f, White, null, 2),
        ["WorldMapTextFont"] = new(FontFace.FrizQt, 102f, ZoneCream, null, 2),
        // Aliases the XML declares by inheritance alone.
        ["ErrorFont"] = new(FontFace.FrizQt, 16f, Gold, Black, 0),
        ["TextStatusBarText"] = new(FontFace.ArialN, 14f, White, null, 1),
        ["TextStatusBarTextSmall"] = new(FontFace.ArialN, 12f, White, null, 1),
        ["CombatLogFont"] = new(FontFace.FrizQt, 12f, White, Black, 0),
        // Tooltip family: shadowless white.
        ["GameTooltipText"] = new(FontFace.FrizQt, 12f, White, null, 0),
        ["GameTooltipTextSmall"] = new(FontFace.FrizQt, 10f, White, null, 0),
        ["GameTooltipHeaderText"] = new(FontFace.FrizQt, 14f, White, null, 0),
        // World combat text (MasterFont chain).
        ["CombatTextFont"] = new(FontFace.FrizQt, 25f, Gold, Black, 0),
    };

    public static FontObjectSpec Get(string name) => Objects.TryGetValue(name, out var spec)
        ? spec
        : throw new KeyNotFoundException(
            $"font object '{name}' is not in Fonts.xml - check the name against FontObjectLaw");

    /// <summary>
    /// The font objects gameplay panels currently draw with - the set the exact-size atlas is
    /// baked for. Migrating a panel that uses a NEW font object means adding its name here
    /// (a data change); drawing an unbaked (face,height) pair falls back to the nearest bake
    /// and logs, so the omission is visible, never silent.
    /// </summary>
    public static readonly string[] BakedByDefault =
    [
        "GameFontNormal", "GameFontHighlight", "GameFontDisable",
        "GameFontNormalSmall", "GameFontHighlightSmall",
        "GameFontNormalLarge",
        "SubSpellFont",
        "GameTooltipText", "GameTooltipTextSmall", "GameTooltipHeaderText",
        "NumberFontNormal", "NumberFontNormalSmall",
        "ChatFontNormal",
    ];

    /// <summary>Distinct (face, height, thick) triples to rasterise for
    /// <see cref="BakedByDefault"/>. THICK-outlined objects bake as separate font instances
    /// because their advance law differs (+1 extra tracking per glyph).</summary>
    public static IEnumerable<(string Face, float Height, bool Thick)> DefaultBakePairs()
        => BakedByDefault.Select(name => Objects[name])
            .Select(spec => (spec.Face, spec.Height, spec.Outline >= 2)).Distinct();
}

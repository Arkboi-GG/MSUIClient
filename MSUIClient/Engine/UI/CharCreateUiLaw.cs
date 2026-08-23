using System.Numerics;
using MSUIClient.Formats;

namespace MSUIClient.Engine.UI;

/// <summary>Data-derived CharacterCreate captions; rendering remains owned by the glue screen.</summary>
public static class CharCreateUiLaw
{
    public readonly record struct ScreenRect(Vector2 Min, Vector2 Size);

    public readonly record struct ActionButtons(ScreenRect Accept, ScreenRect Back);

    public const string ClassChoiceSound = "gsCharacterCreationClass";
    public const string LookChoiceSound = "gsCharacterCreationLook";
    public const string CancelSound = "gsCharacterCreationCancel";
    public const string CreateSound = "gsCharacterCreationCreateChar";
    public const string SoundCategory = "ui.glue.char-create";

    public static ScreenRect Host(Vector2 displaySize) =>
        new(Vector2.Zero, displaySize);

    public static ActionButtons Actions(Vector2 displaySize, float scale,
        float buttonHeightMultiplier)
    {
        Vector2 acceptSize = new(160f * scale, 35f * scale * buttonHeightMultiplier);
        Vector2 backSize = new(120f * scale, 30f * scale * buttonHeightMultiplier);
        float right = displaySize.X - 50f * scale;
        float acceptY = displaySize.Y - 20f * scale - backSize.Y - 5f * scale - acceptSize.Y;
        return new(
            new(new Vector2(right - acceptSize.X, acceptY), acceptSize),
            new(new Vector2(right - backSize.X, acceptY + acceptSize.Y + 5f * scale), backSize));
    }

    public static ScreenRect TuningWindow =>
        new(new Vector2(48f, 48f), new Vector2(400f, 0f));

    public static string[] DialLabels(GlueStrings? strings, string hairToken,
        string facialHairToken) =>
    [
        strings?.Text("CHAR_CUSTOMIZATION1_DESC", "Skin Color") ?? "Skin Color",
        strings?.Text("CHAR_CUSTOMIZATION2_DESC", "Face") ?? "Face",
        strings?.Text($"HAIR_{Normalize(hairToken)}_STYLE", "Hair Style") ?? "Hair Style",
        strings?.Text($"HAIR_{Normalize(hairToken)}_COLOR", "Hair Color") ?? "Hair Color",
        strings?.Text($"FACIAL_HAIR_{Normalize(facialHairToken)}", "Facial Hair") ?? "Facial Hair",
    ];

    private static string Normalize(string token) =>
        string.IsNullOrWhiteSpace(token) ? "NORMAL" : token.Trim().ToUpperInvariant();
}

/// <summary>
/// Character-create layout values in the 1024x768 glue coordinate space. The renderer consumes
/// these values but does not own player-facing placement. The developer tuning window can adjust
/// them live and bake the resulting values back here without changing the rendering mechanism.
/// </summary>
internal static class CreateTune
{
    // Baked 2026-07-29 from Nico's `[cc-tune]` line - the signed-off character-create layout.
    public static float TowerX = 28f, TowerTop = 49f, TowerW = 194f, TowerH = 617f;
    public static float LogoW = 271f, LogoDX = 0f, LogoTop = 0f, ContentTop = 116f, HeaderPx = 15f;
    public static float PanelW = 240f, PanelTop = 28f, PanelRight = 20f, PanelGap = 10f;
    public static float PanelMinH = 66f, PanelMaxH = 320f, TitlePx = 15f, BodyPx = 13f;
    // benilla's FIXED panel heights (char_create/panels.rs right_stack). Auto-size is the old
    // grow-to-fit behaviour, kept behind a toggle - with it off, more text scrolls instead of resizing.
    public static bool PanelAutoSize = false;
    public static float PanelFactionH = 160f, PanelRaceH = 260f, PanelClassH = 210f;
    // The scroll frame inside a panel (the ref's GlueScrollFrame: 190 wide at (17,10), bottom 10) and
    // the title/body stack inside it. PanelLineH is the wrapped-text line height as a multiple of the
    // font size - raise it if outlined body text reads cramped.
    public static float PanelTitleTop = 8f, PanelBodyGap = 13.2f, PanelLineH = 1.28f;
    public static float PanelScrollLeft = 17f, PanelScrollRight = 36f, PanelScrollBottom = 10f;
    // The scrollbar column: inset from the panel's right edge, and the 16x16 button/knob size.
    public static float PanelBarRight = 27f, PanelBarW = 16f;
    // The faction/race/class badge on each info panel's top-left corner, relative to the panel's own
    // top-left. Negative lifts it above / left of the corner (the OG overhang).
    public static float PanelIconDX = -12.4f, PanelIconTop = -6f, PanelIconSize = 42f;
    public static float IconSize = 44f, DialRowH = 27f, NameBoxW = 215f, DialLabelPx = 13f;
    public static float IconGap = 6f, RacePairGap = 26f;
    // The Alliance / Horde banners behind the race columns, one per column (the sheet's two halves).
    // BannerH 0 auto-fits the grid; BannerSpread pushes the pair apart, BannerDX slides both together.
    public static float BannerW = 126.9f, BannerH = 261.9f, BannerTop = -2f, BannerDX = -2.5f, BannerSpread = 17.8f;
    // The male/female pair: its own size and spacing, centred on the tower (it used to borrow the
    // race columns, which flung the two icons to opposite sides of the panel).
    public static float GenderSize = 44f, GenderGap = 3.3f, GenderDX = 0f, GenderTop = 15.3f;
    // The gold name over an icon's bottom edge (the ref's HighlightText). Shown for the selected and
    // hovered icons - the same pair the highlight square lights, as the 1.12 shot shows (three labels
    // lit simultaneously, so it cannot be hover-only). IconLabelsAlways names every icon instead.
    // IconGlow is the ButtonHilight-Square intensity, shared by the hovered AND the selected state
    // (in 1.12 the selected one is simply the same square, held lit).
    public static float HoverLabelPx = 12f, HoverLabelBottom = 1f, IconGlow = 0.71f;
    public static bool IconLabelsAlways = false;
    // What the glow lerps the icon towards - 1.12's lit square reads blue-white, not neutral.
    public static Vector4 IconGlowColor = new(0.81f, 0.89f, 1f, 1f);
    // How far the glow spills PAST the icon (fraction of icon size) so it lights the shadow ring too,
    // and how far that shadow ring extends. Keep bleed >= pad or the ring stays dark when selected.
    public static float IconGlowBleed = 0f, IconShadowPad = 0.16f;
    public static float DialArrowW = 30.9f, DialArrowH = 28f;
    // The spinner pair's placement: DialArrowRight insets the RIGHT arrow from the tower's right edge,
    // DialArrowGap is the space BETWEEN the two, DialArrowDY nudges both off the row's vertical centre.
    // All in the same units as DialArrowW/H, so scaling the pair proportionally means scaling together.
    public static float DialArrowGap = -3.5f, DialArrowRight = 0.3f, DialArrowDY = -0.9f;
    // The appearance-dial label plates (skin / face / hair style / hair colour / facial hair).
    // PadY grows the box above and below the row; Left and Gap are its two horizontal edges.
    public static float DialPlatePadY = 20.3f, DialPlateLeft = -8.1f, DialPlateGap = -10f;
    // The gold "1/10" on each plate: right-aligned DialValueInset inside the plate's right edge, with
    // DialValueZone reserved so the centred label never overlaps it.
    public static float DialValueInset = 20.4f, DialValueZone = 22.1f;
    // Randomize, centred under the dials (benilla's 146x30 Small glue button).
    public static float RandomW = 163f, RandomH = 41f, RandomTop = 0f;
    // The bottom rotate pair, centred on the tower. Gap is signed (the OG pair overlaps slightly).
    public static float RotSize = 50f, RotGap = -8f, RotDX = 164.7f, RotBottom = 20f;

    public static void Reset()
    {
        TowerX = 28f; TowerTop = 49f; TowerW = 194f; TowerH = 617f;
        LogoW = 271f; LogoDX = 0f; LogoTop = 0f; ContentTop = 116f; HeaderPx = 15f;
        PanelW = 240f; PanelTop = 28f; PanelRight = 20f; PanelGap = 10f;
        PanelMinH = 66f; PanelMaxH = 320f; TitlePx = 15f; BodyPx = 13f;
        PanelAutoSize = false; PanelFactionH = 160f; PanelRaceH = 260f; PanelClassH = 210f;
        PanelTitleTop = 8f; PanelBodyGap = 13.2f; PanelLineH = 1.28f;
        PanelScrollLeft = 17f; PanelScrollRight = 36f; PanelScrollBottom = 10f;
        PanelBarRight = 27f; PanelBarW = 16f;
        PanelIconDX = -12.4f; PanelIconTop = -6f; PanelIconSize = 42f;
        IconSize = 44f; DialRowH = 27f; NameBoxW = 215f; DialLabelPx = 13f;
        IconGap = 6f; RacePairGap = 26f;
        BannerW = 126.9f; BannerH = 261.9f; BannerTop = -2f; BannerDX = -2.5f; BannerSpread = 17.8f;
        GenderSize = 44f; GenderGap = 3.3f; GenderDX = 0f; GenderTop = 15.3f;
        HoverLabelPx = 12f; HoverLabelBottom = 1f; IconGlow = 0.71f; IconLabelsAlways = false;
        IconGlowColor = new(0.81f, 0.89f, 1f, 1f);
        IconGlowBleed = 0f; IconShadowPad = 0.16f;
        DialArrowW = 30.9f; DialArrowH = 28f;
        DialArrowGap = -3.5f; DialArrowRight = 0.3f; DialArrowDY = -0.9f;
        DialPlatePadY = 20.3f; DialPlateLeft = -8.1f; DialPlateGap = -10f;
        DialValueInset = 20.4f; DialValueZone = 22.1f;
        RandomW = 163f; RandomH = 41f; RandomTop = 0f;
        RotSize = 50f; RotGap = -8f; RotDX = 164.7f; RotBottom = 20f;
    }

    public static void Log() => Console.WriteLine(
        $"[cc-tune] tower X{TowerX:F0} top{TowerTop:F0} w{TowerW:F0} h{TowerH:F0} | logo w{LogoW:F0} dx{LogoDX:F0} top{LogoTop:F0} " +
        $"content{ContentTop:F0} header{HeaderPx:F0} | panel w{PanelW:F0} top{PanelTop:F0} right{PanelRight:F0} gap{PanelGap:F0} " +
        $"auto{(PanelAutoSize ? 1 : 0)} h{PanelFactionH:F0}/{PanelRaceH:F0}/{PanelClassH:F0} " +
        $"ttop{PanelTitleTop:F1} bgap{PanelBodyGap:F1} line{PanelLineH:F2} " +
        $"ins{PanelScrollLeft:F1}/{PanelScrollRight:F1}/{PanelScrollBottom:F1} bar{PanelBarRight:F1}x{PanelBarW:F1} " +
        $"min{PanelMinH:F0} max{PanelMaxH:F0} title{TitlePx:F0} body{BodyPx:F0} " +
        $"picon dx{PanelIconDX:F1} top{PanelIconTop:F1} size{PanelIconSize:F1} | icon{IconSize:F0} igap{IconGap:F1} rgap{RacePairGap:F1} " +
        $"banner w{BannerW:F1} h{BannerH:F1} top{BannerTop:F1} dx{BannerDX:F1} spread{BannerSpread:F1} " +
        $"gender {GenderSize:F1}/{GenderGap:F1}/{GenderDX:F1}/{GenderTop:F1} hlabel{HoverLabelPx:F1}+{HoverLabelBottom:F1} always{(IconLabelsAlways ? 1 : 0)} iglow{IconGlow:F2}/{IconGlowColor.X:F2},{IconGlowColor.Y:F2},{IconGlowColor.Z:F2},{IconGlowColor.W:F2} bleed{IconGlowBleed:F2} ishadow{IconShadowPad:F2} " +
        $"dial{DialRowH:F0} name{NameBoxW:F0} dlabel{DialLabelPx:F0} arrow{DialArrowW:F1}x{DialArrowH:F1} gap{DialArrowGap:F1} right{DialArrowRight:F1} dy{DialArrowDY:F1} " +
        $"dbox pad{DialPlatePadY:F1} left{DialPlateLeft:F1} gap{DialPlateGap:F1} " +
        $"dval inset{DialValueInset:F1} zone{DialValueZone:F1} " +
        $"rnd {RandomW:F0}x{RandomH:F0} top{RandomTop:F0} | " +
        $"rot size{RotSize:F1} gap{RotGap:F1} dx{RotDX:F1} bottom{RotBottom:F1}");
}

using MSUIClient;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using System.Numerics;

internal static class SpellbookVisualClinicalChecks
{
    public static void Run()
    {
        Check(SpellbookLaw.FrameSize == new Vector2(384, 512) &&
              SpellbookLaw.HostSize == new Vector2(416, 512) &&
              SpellbookLaw.CloseButton == new SpellbookLogicalRect(324, 9, 32, 32) &&
              SpellbookLaw.PreviousPageButton == new SpellbookLogicalRect(34, 391, 32, 32) &&
              SpellbookLaw.NextPageButton == new SpellbookLogicalRect(298, 391, 32, 32) &&
              SpellbookLaw.TitleCenter == new Vector2(198, 26) &&
              SpellbookLaw.PageTextCenter == new Vector2(178, 416),
            "SpellBook frame/title/page/close geometry drift");
        Check(SpellbookLaw.MaxClassTabs == 8 && SpellbookLaw.SpellsPerPage == 12 &&
              SpellbookLaw.SpellButtonSeat(0) ==
                  new SpellbookLogicalRect(34, 85, 37, 37) &&
              SpellbookLaw.SpellButtonSeat(5) ==
                  new SpellbookLogicalRect(34, 340, 37, 37) &&
              SpellbookLaw.SpellButtonSeat(6) ==
                  new SpellbookLogicalRect(191, 85, 37, 37) &&
              SpellbookLaw.SpellButtonSeat(11) ==
                  new SpellbookLogicalRect(191, 340, 37, 37) &&
              SpellbookLaw.SkillLineTabSeat(0) ==
                  new SpellbookLogicalRect(352, 65, 32, 32) &&
              SpellbookLaw.SkillLineTabSeat(7) ==
                  new SpellbookLogicalRect(352, 408, 32, 32),
            "SpellBook 2x6 spell grid or eight-seat skill rail drift");
        Check(SpellbookLaw.DragPreviewMin(new Vector2(100, 200), 1.5f) ==
                  new Vector2(115, 215) &&
              SpellbookLaw.DragPreviewMax(new Vector2(100, 200), 1.5f) ==
                  new Vector2(163, 263) &&
              SpellbookLaw.ButtonScaledSize(2) == new Vector2(74) &&
              SpellbookLaw.SkillLineTabScaledSize(1.5f) == new Vector2(48) &&
              SpellbookLaw.FontCalibrationPosition(new Vector2(20, 30), 2) ==
                  new Vector2(860, 80) &&
              SpellbookLaw.FontCalibrationSize == new Vector2(430, 0),
            "SpellBook cursor/button/tab/calibration geometry drift");
        Vector2 namePosition = SpellbookLaw.SpellNamePosition(new Vector2(34, 85), 1, 4, 24);
        Check(namePosition == new Vector2(75, 87.5f) &&
              SpellbookLaw.SpellNameLinePosition(namePosition, 2, 12) ==
                  new Vector2(75, 111.5f) &&
              SpellbookLaw.SpellRankPosition(namePosition, 24, 1, 10) ==
                  new Vector2(75, 111.5f),
            "SpellButton wrapped-name/rank placement drift");
        Check(SpellbookLaw.PanelArt.Length == 5 &&
              SpellbookLaw.PanelArt[0] == new SpellbookArtSeat(
                  @"Interface\Spellbook\Spellbook-Icon",
                  new SpellbookLogicalRect(10, 8, 58, 58)) &&
              SpellbookLaw.PanelArt[4].Rect ==
                  new SpellbookLogicalRect(256, 256, 128, 256) &&
              SpellbookLaw.SpellButtonBackground ==
                  new SpellbookLogicalRect(-3, -3, 64, 64) &&
              SpellbookLaw.SpellButtonNormalRing ==
                  new SpellbookLogicalRect(-13.5f, -13.5f, 64, 64) &&
              SpellbookLaw.SkillLineTabBackdrop ==
                  new SpellbookLogicalRect(-3, -11, 64, 64),
            "SpellBook authored art-seat geometry drift");

        SpellInfo baseSpell = new(133, "Fireball", "Rank 1", @"Interface\Icons\Spell_Fire_FlameBolt",
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, "", 0);
        SpellInfo ghostWolf = baseSpell with
        {
            Id = 2645,
            Name = "Ghost Wolf",
            AuraIds = [SpellbookLaw.ModShapeshiftAura, 0, 0],
            EffectMiscValues = [16, 0, 0]
        };
        Check(SpellbookLaw.ShapeshiftForm(ghostWolf) == 16 &&
              SpellbookLaw.Checked(ghostWolf, 16, false) &&
              !SpellbookLaw.Checked(ghostWolf, 1, false) &&
              !SpellbookLaw.Checked(baseSpell, 0, false) &&
              SpellbookLaw.Checked(baseSpell, 0, true),
            "SpellBook IsCurrentCast form/profession predicate drift");
        Check(SpellbookLaw.MacroCastLine(baseSpell) == "/cast Fireball(Rank 1)" &&
              SpellbookLaw.MacroCastLine(baseSpell with { Rank = "" }) == "/cast Fireball" &&
              SpellbookLaw.MacroCastLine(baseSpell with { Attributes = 0x40 }) is null,
            "SpellBook shifted MacroFrame cast-line payload drift");

        Vector2 button = new(34, 85);
        Check(SpellbookLaw.CooldownMin(button, 1) == new Vector2(34.5f, 86.5f) &&
              SpellbookLaw.CooldownMax(button, 1) == new Vector2(70.5f, 122.5f) &&
              SpellbookLaw.CooldownMax(button, 2) - SpellbookLaw.CooldownMin(button, 2) ==
                  new Vector2(72),
            "SpellButton centered 36x36 cooldown geometry drift");

        Check(MathF.Abs(SpellbookLaw.AutocastOverlaySize - 71.53f) < .0001f &&
              SpellbookLaw.SpellButtonAutocastOverlay ==
                  new SpellbookLogicalRect(-17.265f, -17.265f, 71.53f, 71.53f) &&
              MathF.Abs(SpellbookLaw.AutocastPathSide - 37.888f) < .0001f &&
              SpellbookLaw.AutocastEmitterCount == 4 &&
              SpellbookLaw.AutocastParticlesPerEmitter == 300,
            "current-Benilla spellbook autocast overlay/model population drift");
        Vector2 bottomLeft = SpellbookLaw.AutocastPoint(0, 0, 0);
        Vector2 bottomRight = SpellbookLaw.AutocastPoint(0, 1, 0);
        Vector2 leftMid = SpellbookLaw.AutocastPoint(.125f, 0, 0);
        Check(Vector2.Distance(bottomLeft, new Vector2(0, 37)) < .0001f &&
              Vector2.Distance(bottomRight, new Vector2(37.888f, 37)) < .0001f &&
              Vector2.Distance(leftMid, new Vector2(0, 18.056f)) < .001f &&
              MathF.Abs(SpellbookLaw.AutocastParticleAge(299) - 299f / 300f) < .0001f &&
              MathF.Abs(SpellbookLaw.AutocastStarHalfExtent(0, 1280) - 6.3776f) < .001f,
            "spellbook autocast M2 path/star projection drift");
        Check(Vector2.Distance(
                  SpellbookLaw.AutocastStarCorner(new Vector2(10, 20), 2, 3, 0),
                  new Vector2(12, 23)) < .0001f &&
              Vector2.Distance(
                  SpellbookLaw.AutocastStarCorner(new Vector2(10, 20), 2, 3, MathF.PI / 2),
                  new Vector2(7, 22)) < .0001f,
            "spellbook autocast star-corner rotation drift");

        Check(PetSpellBookUiLaw.PlayerTab.ScaledMin(new Vector2(10, 20), 2) ==
                  new Vector2(40, 858) &&
              PetSpellBookUiLaw.PlayerTab.ScaledSize(2) == new Vector2(256, 128),
            "pet/player spellbook type-tab geometry drift");

        Vector2 tooltipSize = SpellTooltipLaw.FrameSize(100.4f, 50.4f, 1);
        Vector2 display = new(800, 600);
        Check(tooltipSize == new Vector2(120, 70) &&
              SpellTooltipLaw.DefaultBottomRightOrigin(display, tooltipSize, 1) ==
                  new Vector2(667, 460) &&
              SpellTooltipLaw.OwnerRightOrigin(new Vector2(700, 100), new Vector2(750, 150),
                  tooltipSize, display, 1) == new Vector2(576, 100) &&
              SpellTooltipLaw.LeftTextPosition(new Vector2(20, 30), 40, 2) ==
                  new Vector2(40, 40) &&
              SpellTooltipLaw.RightTextPosition(new Vector2(20, 30), tooltipSize, 40, 2) ==
                  new Vector2(120, 40),
            "spell tooltip frame/anchor/text geometry drift");

        string root = ClientConfig.FindRepoRoot();
        string spellbook = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Spellbook.cs"));
        Check(spellbook.Contains("SpellbookLaw.Checked(spell", StringComparison.Ordinal) &&
              spellbook.Contains("Fields.ShapeshiftForm", StringComparison.Ordinal) &&
              spellbook.Contains("SpellbookLaw.CheckedTexture", StringComparison.Ordinal) &&
              spellbook.Contains("SpellbookLaw.CooldownMin", StringComparison.Ordinal) &&
              spellbook.Contains("PlayerActions cooldownStore = petBook ? _petCooldowns : _actions",
                  StringComparison.Ordinal) &&
              spellbook.Contains("cooldownStore.TryCooldownDisplay(id", StringComparison.Ordinal) &&
              spellbook.Contains("DrawCooldownSwipe(dl, cooldownMin, cooldownMax", StringComparison.Ordinal) &&
              spellbook.Contains("DrawCooldownFlash(dl, cooldownMin, cooldownMax", StringComparison.Ordinal),
            "SpellBook checked overlay or CooldownFrame render wiring drift");
        Check(spellbook.Contains("ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight",
                  StringComparison.Ordinal) &&
              spellbook.Contains("SpellbookLaw.ButtonScaledSize(s)",
                  StringComparison.Ordinal) &&
              !spellbook.Contains("new Vector2(145, 37)", StringComparison.Ordinal) &&
              spellbook.Contains("TryAppendSpellToOpenMacro(spell)", StringComparison.Ordinal) &&
              spellbook.Contains("PickupSpellToCursor(id)", StringComparison.Ordinal) &&
              spellbook.Contains("bool receiveDrag =", StringComparison.Ordinal) &&
              spellbook.Contains("HasActionBarCursor", StringComparison.Ordinal),
            "SpellButton click/shift-pickup/receive-drag contract wiring drift");
        Check(spellbook.Contains("SpellbookLaw.SpellButtonAutocastOverlay",
                  StringComparison.Ordinal) &&
              !spellbook.Contains("PetActionBarUiLaw.AutoCastOverlaySize",
                  StringComparison.Ordinal) &&
              spellbook.Contains("DrawSpellbookAutocastSparkles", StringComparison.Ordinal) &&
              spellbook.Contains("SpellbookLaw.AutocastParticlesPerEmitter",
                  StringComparison.Ordinal) &&
              spellbook.Contains("draw.PushClipRect(buttonMin, buttonMax, true)",
                  StringComparison.Ordinal),
            "spellbook-owned autocast overlay/shine/scissor wiring drift");
        Check(!spellbook.Contains("new Vector2(", StringComparison.Ordinal) &&
              spellbook.Contains("SpellbookLaw.SpellNamePosition", StringComparison.Ordinal) &&
              spellbook.Contains("SpellTooltipLaw.OwnerRightOrigin", StringComparison.Ordinal) &&
              spellbook.Contains("authored.ScaledMin(frameOrigin, s)", StringComparison.Ordinal) &&
              spellbook.Contains("SpellbookLaw.AutocastStarCorner", StringComparison.Ordinal),
            "SpellBook renderer regained authored Vector2 geometry");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}

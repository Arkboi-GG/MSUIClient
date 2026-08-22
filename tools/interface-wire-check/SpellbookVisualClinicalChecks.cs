using MSUIClient;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using System.Numerics;

internal static class SpellbookVisualClinicalChecks
{
    public static void Run()
    {
        Check(SpellbookLaw.MaxClassTabs == 8,
            "SpellBook must retain all eight frozen skill-line tab seats");

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

        string root = ClientConfig.FindRepoRoot();
        string spellbook = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Spellbook.cs"));
        Check(spellbook.Contains("SpellbookLaw.Checked(spell", StringComparison.Ordinal) &&
              spellbook.Contains("Fields.ShapeshiftForm", StringComparison.Ordinal) &&
              spellbook.Contains("SpellbookLaw.CheckedTexture", StringComparison.Ordinal) &&
              spellbook.Contains("SpellbookLaw.CooldownMin", StringComparison.Ordinal) &&
              spellbook.Contains("_actions.TryCooldownDisplay(id", StringComparison.Ordinal) &&
              spellbook.Contains("DrawCooldownSwipe(dl, cooldownMin, cooldownMax", StringComparison.Ordinal) &&
              spellbook.Contains("DrawCooldownFlash(dl, cooldownMin, cooldownMax", StringComparison.Ordinal),
            "SpellBook checked overlay or CooldownFrame render wiring drift");
        Check(spellbook.Contains("ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight",
                  StringComparison.Ordinal) &&
              spellbook.Contains("TryAppendSpellToOpenMacro(spell)", StringComparison.Ordinal) &&
              spellbook.Contains("PickupSpellToCursor(id)", StringComparison.Ordinal) &&
              spellbook.Contains("bool receiveDrag =", StringComparison.Ordinal) &&
              spellbook.Contains("HasActionBarCursor", StringComparison.Ordinal),
            "SpellButton click/shift-pickup/receive-drag contract wiring drift");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}

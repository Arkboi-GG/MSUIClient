using System.Numerics;
using MSUIClient;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

internal static class PetPaperDollClinicalChecks
{
    public static void Run()
    {
        Check(PetPaperDollUiLaw.Model ==
                  new PetPaperDollUiLaw.LogicalRect(25, 78, 318, 224) &&
              PetPaperDollUiLaw.RotateLeft ==
                  new PetPaperDollUiLaw.LogicalRect(23, 76, 35, 35) &&
              PetPaperDollUiLaw.RotateRight.X == 58 &&
              PetPaperDollUiLaw.Experience ==
                  new PetPaperDollUiLaw.LogicalRect(23, 396, 319, 11) &&
              PetPaperDollUiLaw.Attributes ==
                  new PetPaperDollUiLaw.LogicalRect(67, 300, 230, 78) &&
              PetPaperDollUiLaw.Resistance ==
                  new PetPaperDollUiLaw.LogicalRect(315, 77, 32, 160) &&
              PetPaperDollUiLaw.Close ==
                  new PetPaperDollUiLaw.LogicalRect(265, 411, 80, 22),
            "PetPaperDollFrame authored geometry drift");
        Check(PetPaperDollUiLaw.StatRow(false, 0) ==
                  new PetPaperDollUiLaw.LogicalRect(73, 305, 104, 13) &&
              PetPaperDollUiLaw.StatRow(true, 4) ==
                  new PetPaperDollUiLaw.LogicalRect(189, 361, 104, 13) &&
              PetPaperDollUiLaw.ResistanceRow(4) ==
                  new PetPaperDollUiLaw.LogicalRect(315, 193, 32, 29) &&
              PetPaperDollUiLaw.ExperienceFraction(4200, 8000) == .525f &&
              PetPaperDollUiLaw.ExperienceFraction(1, 0) == 0 &&
              PetPaperDollUiLaw.TrainingPoints((170u << 16) | 130) == (170, 130) &&
              PetPaperDollUiLaw.LoyaltyName(6) == "Best Friend" &&
              PetPaperDollUiLaw.ResistanceTooltip("Fire Resistance", 15, 0, 0) ==
                  "Fire Resistance" &&
              PetPaperDollUiLaw.ResistanceTooltip("Fire Resistance", 15, 5, -2) ==
                  "Fire Resistance ( 12 +5 -2 )",
            "pet stat/XP/training/loyalty projection drift");
        Check(ObjectFields.UNIT_FIELD_PETEXPERIENCE == 141 &&
              ObjectFields.UNIT_FIELD_PETNEXTLEVELEXP == 142 &&
              ObjectFields.UNIT_FIELD_TRAINING_POINTS == 149,
            "pet descriptor-field indices drift");

        string root = ClientConfig.FindRepoRoot();
        string page = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.CharacterPage.cs"));
        string portraits = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Hud",
            "GameLoop.Portraits.cs"));
        string query = SourceText.Read(Path.Combine(root, "MSUIClient", "Net",
            "CreatureQuery.cs"));
        string catalog = SourceText.Read(Path.Combine(root, "MSUIClient", "Formats",
            "CreatureFamilyCatalog.cs"));
        Check(page.Contains("DrawPetPaperDollBackground", StringComparison.Ordinal) &&
              page.Contains("DrawPetPaperDollPage", StringComparison.Ordinal) &&
              page.Contains("PetPaperDollUiLaw.Model", StringComparison.Ordinal) &&
              page.Contains("PetPaperDollUiLaw.Experience", StringComparison.Ordinal) &&
              page.Contains("PetPaperDollUiLaw.Attributes", StringComparison.Ordinal) &&
              page.Contains("PetPaperDollUiLaw.Close", StringComparison.Ordinal) &&
              page.Contains("PetPaperDollUiLaw.ResistanceRow", StringComparison.Ordinal) &&
              page.Contains("PetStatFrame{i + 1}", StringComparison.Ordinal) &&
              page.Contains("PetAttackPowerFrame", StringComparison.Ordinal) &&
              page.Contains("PetMagicResFrame{i + 1}", StringComparison.Ordinal) &&
              page.Contains("PetPaperDollUiLaw.ResistanceTooltip(",
                  StringComparison.Ordinal) &&
              portraits.Contains("new PortraitRenderTarget(gl, 636, 448)",
                  StringComparison.Ordinal) &&
              portraits.Contains("_petPortrait.UpdateCircularCopy()",
                  StringComparison.Ordinal) &&
              portraits.Contains("private uint PetPortraitHandle", StringComparison.Ordinal) &&
              portraits.Contains("_characterTab == 1", StringComparison.Ordinal) &&
              portraits.Contains("_creatures.RenderPortrait(camera, pet)",
                  StringComparison.Ordinal) &&
              query.Contains("uint PetFamily", StringComparison.Ordinal) &&
              catalog.Contains("CreatureFamily.dbc", StringComparison.Ordinal) &&
              catalog.Contains("ItemPetFood.dbc", StringComparison.Ordinal),
            "Pet tab is blank or bypasses its rule-owned geometry/model/DBC law");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}

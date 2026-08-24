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
              PetPaperDollUiLaw.BackgroundArt.Length == 4 &&
              PetPaperDollUiLaw.BackgroundArt[3].Rect ==
                  new PetPaperDollUiLaw.LogicalRect(258, 257, 128, 256) &&
              PetPaperDollUiLaw.AttributePlate(false) ==
                  new PetPaperDollUiLaw.LogicalRect(67, 300, 114, 78) &&
              PetPaperDollUiLaw.AttributePlate(true) ==
                  new PetPaperDollUiLaw.LogicalRect(182, 300, 114, 78) &&
              PetPaperDollUiLaw.Close ==
                  new PetPaperDollUiLaw.LogicalRect(265, 411, 80, 22),
            "PetPaperDollFrame authored geometry drift");
        Check(PetPaperDollUiLaw.StatRow(false, 0) ==
                  new PetPaperDollUiLaw.LogicalRect(73, 305, 104, 13) &&
              PetPaperDollUiLaw.StatRow(true, 4) ==
                  new PetPaperDollUiLaw.LogicalRect(189, 361, 104, 13) &&
              PetPaperDollUiLaw.ResistanceRow(4) ==
                  new PetPaperDollUiLaw.LogicalRect(315, 193, 32, 29) &&
              PetPaperDollUiLaw.ResistanceSchoolIds.SequenceEqual(new[] { 6, 2, 3, 4, 5 }) &&
              PetPaperDollUiLaw.ResistanceUvMin(0) == new Vector2(0, .2265625f) &&
              PetPaperDollUiLaw.ResistanceUvMax(4) == new Vector2(1, .56640625f) &&
              PetPaperDollUiLaw.ResistanceTextCenter(new Vector2(315, 77), 2) ==
                  new Vector2(347, 119) &&
              PetPaperDollUiLaw.RightTooltipSeat(new Vector2(100, 200),
                  new Vector2(104, 13)) ==
                  new PetPaperDollUiLaw.TooltipSeat(new Vector2(204, 200), Vector2.UnitY) &&
              PetPaperDollUiLaw.ExperienceDwarfPiece(false) ==
                  new PetPaperDollUiLaw.LogicalRect(0, 0, 160, 13) &&
              PetPaperDollUiLaw.ExperienceDwarfPiece(true) ==
                  new PetPaperDollUiLaw.LogicalRect(160, 0, 159, 13) &&
              PetPaperDollUiLaw.ExperienceFraction(4200, 8000) == .525f &&
              PetPaperDollUiLaw.ExperienceFraction(1, 0) == 0 &&
              PetPaperDollUiLaw.TrainingPoints((170u << 16) | 130) == (170, 130) &&
              PetPaperDollUiLaw.LiveAnimationStep(10.01, 10.0) > .009f &&
              PetPaperDollUiLaw.LiveAnimationStep(11.0, 10.0) ==
                  PetPaperDollUiLaw.LiveAnimationMaxStep &&
              PetPaperDollUiLaw.LiveAnimationStep(10.0, 0) == 0f &&
              PetPaperDollUiLaw.CreatureSkill(0) == 0 &&
              PetPaperDollUiLaw.CreatureSkill(60) == 300 &&
              PetPaperDollUiLaw.LevelText(58, "Boar") == "Level 58 Boar" &&
              PetPaperDollUiLaw.LevelText(58, null) == "" &&
              PetPaperDollUiLaw.LevelText(58, "  ") == "" &&
              PetPaperDollUiLaw.LoyaltyName(6) == "Best Friend" &&
              PetPaperDollUiLaw.ResistanceTooltip("Fire Resistance", 15, 0, 0) ==
                  "Fire Resistance" &&
              PetPaperDollUiLaw.ResistanceTooltip("Fire Resistance", 15, 5, -2) ==
                  "Fire Resistance ( 12 +5 -2 )",
            "pet stat/XP/training/loyalty projection drift");
        Check(PetPaperDollUiLaw.PetNameFont == "GameFontHighlight" &&
              PetPaperDollUiLaw.PetLevelFont == "GameFontNormalSmall" &&
              PetPaperDollUiLaw.PetLoyaltyFont == "GameFontNormalSmall" &&
              PetPaperDollUiLaw.StatLabelFont == "GameFontNormalSmall" &&
              PetPaperDollUiLaw.StatValueFont == "GameFontHighlightSmall" &&
              PetPaperDollUiLaw.PetNameCenter(new Vector2(100, 200), 2) ==
                  new Vector2(496, 248) &&
              PetPaperDollUiLaw.PetLevelCenter(new Vector2(100, 200), 2) ==
                  new Vector2(496, 286) &&
              PetPaperDollUiLaw.PetLoyaltyCenter(new Vector2(100, 200), 2) ==
                  new Vector2(496, 308) &&
              PetPaperDollUiLaw.StatLabelMin(new Vector2(146, 610), 2) ==
                  new Vector2(146, 613) &&
              PetPaperDollUiLaw.StatValueRightTop(new Vector2(146, 610), 2) ==
                  new Vector2(354, 613) &&
              PetPaperDollUiLaw.TrainingValueTopRight(new Vector2(100, 200), 2) ==
                  new Vector2(598, 1032) &&
              PetPaperDollUiLaw.TrainingLabelRightTop(new Vector2(100, 200), 2, 40) ==
                  new Vector2(548, 1032),
            "pet fixed FontString anchor-chain drift");
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
        int petStart = page.IndexOf("private void DrawPetPaperDollPage", StringComparison.Ordinal);
        int playerStart = page.IndexOf("private void DrawPaperDollPage", petStart,
            StringComparison.Ordinal);
        string petRenderer = page[petStart..playerStart];
        Check(page.Contains("DrawPetPaperDollBackground", StringComparison.Ordinal) &&
              page.Contains("DrawPetPaperDollPage", StringComparison.Ordinal) &&
              page.Contains("PetPaperDollUiLaw.Model", StringComparison.Ordinal) &&
              page.Contains("PetPaperDollUiLaw.Experience", StringComparison.Ordinal) &&
              page.Contains("PetPaperDollUiLaw.AttributePlate", StringComparison.Ordinal) &&
              page.Contains("PetPaperDollUiLaw.Close", StringComparison.Ordinal) &&
              page.Contains("PetPaperDollUiLaw.ResistanceRow", StringComparison.Ordinal) &&
              page.Contains("PetStatFrame{i + 1}", StringComparison.Ordinal) &&
              page.Contains("PetAttackPowerFrame", StringComparison.Ordinal) &&
              page.Contains("PetPaperDollUiLaw.CreatureSkill(pet.Level)",
                  StringComparison.Ordinal) &&
              page.Contains("PetMagicResFrame{i + 1}", StringComparison.Ordinal) &&
              page.Contains("PetPaperDollUiLaw.ResistanceTooltip(",
                  StringComparison.Ordinal) &&
              page.Contains("PetPaperDollUiLaw.RightTooltipSeat", StringComparison.Ordinal) &&
              page.Contains("parent.StartsWith(\"Pet\"", StringComparison.Ordinal) &&
              page.Contains("PetPaperDollUiLaw.BackgroundArt", StringComparison.Ordinal) &&
              page.Contains("PetPaperDollUiLaw.PetNameCenter", StringComparison.Ordinal) &&
              page.Contains("PetPaperDollUiLaw.PetLevelCenter", StringComparison.Ordinal) &&
              page.Contains("PetPaperDollUiLaw.LevelText(pet!.Level, PetFamilyName(pet))",
                  StringComparison.Ordinal) &&
              !page.Contains("PetFamilySuffix", StringComparison.Ordinal) &&
              petRenderer.Contains("PetPaperDollUiLaw.PetLoyaltyCenter", StringComparison.Ordinal) &&
              petRenderer.Contains("PetPaperDollUiLaw.TrainingLabelRightTop", StringComparison.Ordinal) &&
              petRenderer.Contains("PetPaperDollUiLaw.StatLabelMin", StringComparison.Ordinal) &&
              petRenderer.Contains("PetPaperDollUiLaw.StatValueRightTop", StringComparison.Ordinal) &&
              petRenderer.Contains("PetPaperDollUiLaw.ExperienceDwarfPiece", StringComparison.Ordinal) &&
              !petRenderer.Contains("new Vector2", StringComparison.Ordinal) &&
              !petRenderer.Contains("new Vector4", StringComparison.Ordinal) &&
              portraits.Contains("new PortraitRenderTarget(gl, 636, 448)",
                  StringComparison.Ordinal) &&
              portraits.Contains("_petPortrait.UpdateCircularCopy()",
                  StringComparison.Ordinal) &&
              portraits.Contains("private uint PetPortraitHandle", StringComparison.Ordinal) &&
              portraits.Contains("_characterTab == 1", StringComparison.Ordinal) &&
              portraits.Contains("_petPaperDollAnimationTime += PetPaperDollUiLaw.LiveAnimationStep",
                  StringComparison.Ordinal) &&
              portraits.Contains("camera, pet, _petPaperDollAnimationTime)",
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

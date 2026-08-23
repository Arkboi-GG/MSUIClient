using System.Numerics;
using MSUIClient;
using MSUIClient.Engine.UI;

internal static class ProfessionFrameClinicalChecks
{
    public static void Run()
    {
        Check(ProfessionFrameUiLaw.FrameOrigin(1.5f) == new Vector2(0, 156) &&
              ProfessionFrameUiLaw.FrameSize(1.5f) == new Vector2(576, 768) &&
              ProfessionFrameUiLaw.Rank == new ProfessionFrameUiLaw.LogicalRect(73, 37, 268, 15) &&
              ProfessionFrameUiLaw.PortraitOffset == new Vector2(7, 6) &&
              ProfessionFrameUiLaw.PortraitSize == 60 &&
              ProfessionFrameUiLaw.TitleFont == "GameFontNormal" &&
              ProfessionFrameUiLaw.TitleCenter == new Vector2(192, 17) &&
              ProfessionFrameUiLaw.SkillBorderLeft ==
                  new ProfessionFrameUiLaw.LogicalRect(63, 50, 256, 8) &&
              ProfessionFrameUiLaw.SkillBorderRightUvMax == new Vector2(.109375f, .5f) &&
              ProfessionFrameUiLaw.List == new ProfessionFrameUiLaw.LogicalRect(22, 96, 293, 128) &&
              ProfessionFrameUiLaw.Row(7).Y == 208 &&
              ProfessionFrameUiLaw.ScrollUp == new ProfessionFrameUiLaw.LogicalRect(321, 96, 16, 16) &&
              ProfessionFrameUiLaw.ScrollDown == new ProfessionFrameUiLaw.LogicalRect(321, 208, 16, 16) &&
              ProfessionFrameUiLaw.HeaderMarkerOffset == new Vector2(1, 1) &&
              ProfessionFrameUiLaw.ReagentIconSize == new Vector2(28) &&
              ProfessionFrameUiLaw.Reagent(1, tradeSkill: false, 304) ==
                  new ProfessionFrameUiLaw.LogicalRect(165, 317, 140, 32) &&
              ProfessionFrameUiLaw.Reagent(2, tradeSkill: true, 281) ==
                  new ProfessionFrameUiLaw.LogicalRect(23, 328, 140, 32) &&
              ProfessionFrameUiLaw.Reagent(6, tradeSkill: false, 304) ==
                  new ProfessionFrameUiLaw.LogicalRect(165, 419, 140, 32) &&
              ProfessionFrameUiLaw.Reagent(7, tradeSkill: true, 281) ==
                  new ProfessionFrameUiLaw.LogicalRect(303, 396, 140, 32) &&
              ProfessionFrameUiLaw.DetailHeaderLeft ==
                  new ProfessionFrameUiLaw.LogicalRect(20, 231, 256, 64) &&
              ProfessionFrameUiLaw.DetailEmptySlot ==
                  new ProfessionFrameUiLaw.LogicalRect(15, 224, 64, 64) &&
              ProfessionFrameUiLaw.FilterMenuRow(2, 128) ==
                  new ProfessionFrameUiLaw.LogicalRect(2, 38, 124, 18) &&
              !ProfessionFrameUiLaw.RowHoverHighlight(tradeSkill: false) &&
              ProfessionFrameUiLaw.RowHoverHighlight(tradeSkill: true) &&
              !ProfessionFrameUiLaw.DetailIconVisible(0) &&
              ProfessionFrameUiLaw.DetailIconVisible(1) &&
              ProfessionFrameUiLaw.RecipeNameOffset(tradeSkill: false) == new Vector2(4, 0) &&
              ProfessionFrameUiLaw.RecipeNameOffset(tradeSkill: true) == new Vector2(22, 0) &&
              ProfessionFrameUiLaw.CraftDetailTooltipSeat(
                  new Vector2(100, 200), new Vector2(137, 237)) ==
                  new ProfessionFrameUiLaw.TooltipSeat(
                      new Vector2(137, 200), new Vector2(0, 1)) &&
              ProfessionFrameUiLaw.RightTooltipSeat(
                  new Vector2(22, 96), new Vector2(315, 112)) ==
                  new ProfessionFrameUiLaw.TooltipSeat(
                      new Vector2(315, 96), new Vector2(0, 1)) &&
              ProfessionFrameUiLaw.CraftReagentTooltipSeat(new Vector2(165, 317)) ==
                  new ProfessionFrameUiLaw.TooltipSeat(
                      new Vector2(165, 317), new Vector2(0, 1)) &&
              ProfessionFrameUiLaw.RequirementFont == "GameFontHighlightSmall" &&
              ProfessionFrameUiLaw.RequiresLabel == "Requires:" &&
              ProfessionFrameUiLaw.TradeSkillRequirementLabel == new Vector2(70, 253) &&
              ProfessionFrameUiLaw.TradeSkillRequirementTextAt(40, 2) ==
                  new Vector2(94, 253) &&
              ProfessionFrameUiLaw.CollapseAll ==
                  new ProfessionFrameUiLaw.LogicalRect(23, 73, 40, 22) &&
              ProfessionFrameUiLaw.CollapseAllIcon ==
                  new ProfessionFrameUiLaw.LogicalRect(23, 76, 16, 16) &&
              ProfessionFrameUiLaw.CollapseAllLabelCenter == new Vector2(52, 84) &&
              ProfessionFrameUiLaw.CollapseAllTabArt.Select(piece => piece.Element)
                  .SequenceEqual(new[]
                  {
                      "TradeSkillExpandTabLeft", "TradeSkillExpandTabMiddle",
                      "TradeSkillExpandTabRight",
                  }) &&
              ProfessionFrameUiLaw.CollapseAllTabArt[1].Rect ==
                  new ProfessionFrameUiLaw.LogicalRect(23, 65, 38, 32) &&
              ProfessionFrameUiLaw.CollapseAllFont == "GameFontNormalSmall" &&
              ProfessionFrameUiLaw.CollapseAllMinusPath ==
                  @"Interface\Buttons\UI-MinusButton-Up" &&
              ProfessionFrameUiLaw.CollapseAllPlusPath ==
                  @"Interface\Buttons\UI-PlusButton-Up" &&
              ProfessionFrameUiLaw.CollapseAllHighlightPath ==
                  @"Interface\Buttons\UI-PlusButton-Hilight",
            "shared Craft/TradeSkill geometry drift");

        Check(ProfessionFrameUiLaw.BottomLeftArtFor(tradeSkill: false) ==
                  @"Interface\ClassTrainerFrame\UI-ClassTrainer-BotLeft" &&
              ProfessionFrameUiLaw.BottomLeftArtFor(tradeSkill: true) ==
                  @"Interface\TradeSkillFrame\UI-TradeSkill-BotLeft",
            "CraftFrame/TradeSkillFrame bottom-left shell art fork drift");

        Check(ProfessionFrameUiLaw.MaximumScroll(7) == 0 &&
              ProfessionFrameUiLaw.MaximumScroll(10) == 2 &&
              ProfessionFrameUiLaw.ClampScroll(9, 10) == 2 &&
              ProfessionFrameUiLaw.RefreshedSelection(20, [10u, 20u, 30u]) == 1 &&
              ProfessionFrameUiLaw.RefreshedSelection(99, [10u, 20u, 30u]) == 0 &&
              Math.Abs(ProfessionFrameUiLaw.RankFraction(75, 150) - .5f) < .001f &&
              ProfessionFrameUiLaw.ScrollThumbY(2, 2) == 192 &&
              ProfessionFrameUiLaw.RankBackgroundColor == 0x80bf0000 &&
              ProfessionFrameUiLaw.RankFillColor == 0x80ff0000 &&
              ProfessionFrameUiLaw.RankValueText(75, 150) == "75/150",
            "profession rank/list scroll law drift");

        Check(ProfessionFrameUiLaw.CraftableCount([(12u, 3u), (5u, 2u)]) == 2 &&
              ProfessionFrameUiLaw.CraftableCount([]) == 0 &&
              ProfessionFrameUiLaw.ClampCreateCount(8, 3) == 3 &&
              ProfessionFrameUiLaw.RowLabel("Linen Bandage", 4) == " Linen Bandage [4]" &&
              ProfessionFrameUiLaw.CraftSubText(" Journeyman ") == "(Journeyman)",
            "profession craft-count/subtext law drift");

        Check(ProfessionFrameUiLaw.WrapDescription("one two three", 7,
                  value => value.Length).SequenceEqual(new[] { "one two", "three" }) &&
              ProfessionFrameUiLaw.WrapDescription("one\ntwo", 20,
                  value => value.Length).SequenceEqual(new[] { "one", "two" }) &&
              ProfessionFrameUiLaw.WrapDescription(" ", 20,
                  value => value.Length).Count == 0,
            "CraftFrame GameFontHighlightSmall description wrapping drift");

        Check(ProfessionFrameUiLaw.CraftRequirementsText([]) == "" &&
              ProfessionFrameUiLaw.CraftRequirementsText(
                  [("Runed Copper Rod", false), ("Anvil", true)]) ==
                  "Requires: |cffff2020Runed Copper Rod|r, Anvil" &&
              ProfessionFrameUiLaw.RequirementNamesMarkup(
                  [("Blacksmith Hammer", false), ("Anvil", true)]) ==
                  "|cffff2020Blacksmith Hammer|r, Anvil",
            "CraftFrame colored requirements copy drift");

        ProfessionFrameUiLaw.CraftTooltipTarget enchantTooltip =
            ProfessionFrameUiLaw.CraftTooltip(7418, [53u, 0u, 0u], [0u, 0u, 0u],
                [0u, 0u, 0u]);
        ProfessionFrameUiLaw.CraftTooltipTarget rodTooltip =
            ProfessionFrameUiLaw.CraftTooltip(7421, [24u, 0u, 0u], [6218u, 0u, 0u],
                [0u, 0u, 0u]);
        Check(ProfessionFrameUiLaw.CraftRecipeEligible(3, 0x10000, 3) &&
              !ProfessionFrameUiLaw.CraftRecipeEligible(3, 0x10020, 3) &&
              !ProfessionFrameUiLaw.CraftRecipeEligible(1, 0x10000, 3) &&
              ProfessionFrameUiLaw.OpenForSnapshot(true, 0) &&
              !ProfessionFrameUiLaw.OpenForSnapshot(false, 0) &&
              ProfessionFrameUiLaw.OpenForSnapshot(false, 1) &&
              ProfessionFrameUiLaw.TradeSkillRecipeEligible(0x20) &&
              ProfessionFrameUiLaw.EffectiveSkill(75, -10) == 65 &&
              ProfessionFrameUiLaw.DifficultyName(50, 0, 75) == "yellow" &&
              ProfessionFrameUiLaw.DifficultyColor(0) == new Vector4(1f, .5f, .25f, 1f) &&
              ProfessionFrameUiLaw.DifficultyColor(1) == new Vector4(1f, 1f, 0f, 1f) &&
              ProfessionFrameUiLaw.DifficultyColor(2) == new Vector4(.25f, .75f, .25f, 1f) &&
              ProfessionFrameUiLaw.DifficultyColor(3) == new Vector4(.5f, .5f, .5f, 1f) &&
              ProfessionFrameUiLaw.ReagentHaveText(100) == "*" &&
              !ProfessionFrameUiLaw.ReagentTemplateVisible(false,
                  @"Interface\Icons\INV_Enchant_DustStrange") &&
              !ProfessionFrameUiLaw.ReagentTemplateVisible(true, "") &&
              ProfessionFrameUiLaw.ReagentTemplateVisible(true,
                  @"Interface\Icons\INV_Enchant_DustStrange") &&
              ProfessionFrameUiLaw.ReagentAllowsCreate(false, 0, 3) &&
              !ProfessionFrameUiLaw.ReagentAllowsCreate(true, 2, 3) &&
              ProfessionFrameUiLaw.ReagentAllowsCreate(true, 3, 3) &&
              ProfessionFrameUiLaw.ResolvedRequirementName(null) is null &&
              ProfessionFrameUiLaw.ResolvedRequirementName(" ") is null &&
              ProfessionFrameUiLaw.ResolvedRequirementName("Blacksmith Hammer") ==
                  "Blacksmith Hammer" &&
              enchantTooltip == new ProfessionFrameUiLaw.CraftTooltipTarget(
                  ProfessionFrameUiLaw.CraftTooltipKind.Spell, 7418) &&
              rodTooltip == new ProfessionFrameUiLaw.CraftTooltipTarget(
                  ProfessionFrameUiLaw.CraftTooltipKind.Item, 6218) &&
              ProfessionFrameUiLaw.CompareCraftRecipes(0, "Zeta", 0, 2,
                  1, "Alpha", 0, 1, 3) < 0,
            "Enchanting CraftFrame admission/order/tooltip law drift");

        var cloth = new ProfessionFrameUiLaw.TradeSkillNode(0, 4, 1, "Cloth", 5, 12, 1,
            "Brown Linen Robe");
        var sword = new ProfessionFrameUiLaw.TradeSkillNode(1, 2, 7, "One-Handed Swords", 13,
            20, 0, "Copper Shortsword");
        IReadOnlyList<ProfessionFrameUiLaw.TradeSkillRow> tree =
            ProfessionFrameUiLaw.BuildTradeSkillTree([cloth, sword], new HashSet<ulong>(),
                subclassFilter: null, inventorySlotFilter: null);
        Check(tree.Select(row => row.Text).SequenceEqual(new[]
              {
                  "One-Handed Swords", "Copper Shortsword", "Cloth", "Brown Linen Robe",
              }) &&
              ProfessionFrameUiLaw.BuildTradeSkillTree([cloth, sword],
                  new HashSet<ulong> { ProfessionFrameUiLaw.GroupKey(2, 7) }, null, 15)
                  .Select(row => row.Text).SequenceEqual(new[] { "One-Handed Swords" }) &&
              ProfessionFrameUiLaw.InventorySlotMask(13) == 0x18000 &&
              ProfessionFrameUiLaw.InventorySlotName(23) == "Not equippable.",
            "TradeSkill group/fold/inventory-filter law drift");

        string root = ClientConfig.FindRepoRoot();
        string runtime = SourceText.Read(Path.Combine(root, "MSUIClient", "GameLoop", "Panels",
            "GameLoop.Professions.cs"));
        Check(runtime.Contains(
                  "UiPanelFrameLogicalOrigin(UiPanelOwnershipRegistry[panelIndex])",
                  StringComparison.Ordinal) &&
              runtime.Contains(
                  "_professionPanelKind == ProfessionPanelKind.Craft ? 17 : 16",
                  StringComparison.Ordinal) &&
              runtime.Contains("DrawProfessionRankBar", StringComparison.Ordinal) &&
              runtime.Contains("ProfessionFrameUiLaw.RankBackgroundColor",
                  StringComparison.Ordinal) &&
              runtime.Contains("ProfessionFrameUiLaw.RankValueText(value, maximum)",
                  StringComparison.Ordinal) &&
              runtime.Contains("DrawUnitPortraitImage", StringComparison.Ordinal) &&
              runtime.Contains("DrawProfessionScrollBar", StringComparison.Ordinal) &&
              runtime.Contains("DrawProfessionCountSpinner", StringComparison.Ordinal) &&
              runtime.Contains("BuildTradeSkillTree", StringComparison.Ordinal) &&
              runtime.Contains("DrawProfessionTradeSkillControls", StringComparison.Ordinal) &&
              runtime.Contains("ProfessionFrameUiLaw.CollapseAllTabArt",
                  StringComparison.Ordinal) &&
              runtime.Contains("VanillaCollapseAllButton", StringComparison.Ordinal) &&
              runtime.Contains("groupKeys.All(_professionCollapsedGroups.Contains)",
                  StringComparison.Ordinal) &&
              !runtime.Contains("anyCollapsed", StringComparison.Ordinal) &&
              runtime.Contains("ProfessionFrameUiLaw.BottomLeftArtFor(tradeSkill)",
                  StringComparison.Ordinal) &&
              runtime.Contains("GameText.DrawCentered(dl, ProfessionFrameUiLaw.TitleFont",
                  StringComparison.Ordinal) &&
              runtime.Contains("ProfessionFrameUiLaw.RowHoverHighlight(tradeSkill)",
                  StringComparison.Ordinal) &&
              runtime.Contains("ProfessionFrameUiLaw.DetailIconVisible(_professionRecipes.Count)",
                  StringComparison.Ordinal) &&
              runtime.Contains("ProfessionFrameUiLaw.RecipeNameOffset(tradeSkill)",
                  StringComparison.Ordinal) &&
              runtime.Contains("GameText.Draw(dl, \"GameFontNormal\", rowLabel",
                  StringComparison.Ordinal) &&
              runtime.Contains("ProfessionFrameUiLaw.WrapDescription(",
                  StringComparison.Ordinal) &&
              runtime.Contains("GameText.Draw(dl, \"GameFontHighlightSmall\"",
                  StringComparison.Ordinal) &&
              runtime.Contains("detailSpell.IconPath", StringComparison.Ordinal) &&
              runtime.Contains("SkillBorderLeftUvMax", StringComparison.Ordinal) &&
              runtime.Contains("selectedColor: color", StringComparison.Ordinal) &&
              runtime.Contains("ProfessionFrameUiLaw.DifficultyColor(difficultyTier)",
                  StringComparison.Ordinal) &&
              runtime.Contains("PlayUiSound(ProfessionFrameUiLaw.OpenSound",
                  StringComparison.Ordinal) &&
              runtime.Contains("PlayUiSound(ProfessionFrameUiLaw.CloseSound",
                  StringComparison.Ordinal) &&
              runtime.Contains("CloseProfessionFrame()", StringComparison.Ordinal) &&
              runtime.Contains("FallbackIconPath", StringComparison.Ordinal) &&
              runtime.Contains("SpellTooltipLaw.Substitute", StringComparison.Ordinal) &&
              runtime.Contains("CraftRequirements", StringComparison.Ordinal) &&
              runtime.Contains("CraftRequirementsText(requirements)",
                  StringComparison.Ordinal) &&
              runtime.Contains("ProfessionFrameUiLaw.CraftDetailTooltipSeat(productHit,",
                  StringComparison.Ordinal) &&
              runtime.Contains("ProfessionFrameUiLaw.RightTooltipSeat(rowMin,",
                  StringComparison.Ordinal) &&
              runtime.Contains("ProfessionFrameUiLaw.RightTooltipSeat(productHit,",
                  StringComparison.Ordinal) &&
              runtime.Contains("ProfessionFrameUiLaw.CraftReagentTooltipSeat(row)",
                  StringComparison.Ordinal) &&
              runtime.Contains("ProfessionFrameUiLaw.ReagentTemplateVisible(",
                  StringComparison.Ordinal) &&
              runtime.Contains("ProfessionFrameUiLaw.ReagentAllowsCreate(",
                  StringComparison.Ordinal) &&
              runtime.Contains("ProfessionFrameUiLaw.ResolvedRequirementName(",
                  StringComparison.Ordinal) &&
              runtime.Contains("resolvedCraftReagentsReady", StringComparison.Ordinal) &&
              !runtime.Contains("$\"Item {reagent.ItemId}\"", StringComparison.Ordinal) &&
              !runtime.Contains("$\"Item {tool}\"", StringComparison.Ordinal) &&
              !runtime.Contains("requirements.Add((SpellFocusName", StringComparison.Ordinal) &&
              runtime.Contains("nextWindowPivot: tooltipSeat.Pivot",
                  StringComparison.Ordinal) &&
              runtime.Contains("ProfessionFrameUiLaw.TradeSkillRequirementLabel",
                  StringComparison.Ordinal) &&
              runtime.Contains("ProfessionFrameUiLaw.TradeSkillRequirementTextAt(",
                  StringComparison.Ordinal) &&
              runtime.Contains("ProfessionFrameUiLaw.RequirementNamesMarkup(requirements)",
                  StringComparison.Ordinal) &&
              !runtime.Contains("$\"Tool: {name}\"", StringComparison.Ordinal) &&
              runtime.Contains("preserveSelection: true", StringComparison.Ordinal) &&
              runtime.Contains("ProfessionFrameUiLaw.RefreshedSelection",
                  StringComparison.Ordinal) &&
              runtime.Contains("_actions.KnownSpells.Select(spellId =>",
                  StringComparison.Ordinal) &&
              runtime.Contains("ProfessionFrameUiLaw.OpenForSnapshot(",
                  StringComparison.Ordinal) &&
              runtime.Contains("CraftReagentLabelAt(0, false)",
                  StringComparison.Ordinal) &&
              runtime.Contains("selectedRecipeReady", StringComparison.Ordinal) &&
              runtime.Contains("uint count = BackpackCount(entry);",
                  StringComparison.Ordinal) &&
              runtime.Contains("counts deliberately use BackpackCount",
                  StringComparison.Ordinal) &&
              runtime.Contains("UiTextMarkupLaw.Parse(markup, Vector4.One)",
                  StringComparison.Ordinal) &&
              runtime.Contains("ReagentHaveText", StringComparison.Ordinal) &&
              runtime.Contains("recipe.Description", StringComparison.Ordinal) &&
              runtime.Contains("PrepareItemTooltipBodySnapshot(product", StringComparison.Ordinal) &&
              runtime.Contains("PrepareItemTooltipBodySnapshot(item, reagent.Count)",
                  StringComparison.Ordinal) &&
              !runtime.Contains("new Vector2", StringComparison.Ordinal) &&
              !runtime.Contains("BeginVanillaWindow(\"##profession\", new Vector2",
                  StringComparison.Ordinal),
            "profession production frame bypasses shared geometry/content law");

        string catalog = SourceText.Read(Path.Combine(root, "MSUIClient", "Formats",
            "ItemSubClassCatalog.cs"));
        Check(catalog.Contains("dbc.GetString(row, 19)", StringComparison.Ordinal) &&
              catalog.Contains("dbc.GetString(row, 10)", StringComparison.Ordinal),
            "TradeSkill header vocabulary no longer resolves verbose-first");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}

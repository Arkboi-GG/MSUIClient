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
              ProfessionFrameUiLaw.List == new ProfessionFrameUiLaw.LogicalRect(22, 96, 293, 128) &&
              ProfessionFrameUiLaw.Row(7).Y == 208 &&
              ProfessionFrameUiLaw.ScrollUp == new ProfessionFrameUiLaw.LogicalRect(321, 96, 16, 16) &&
              ProfessionFrameUiLaw.ScrollDown == new ProfessionFrameUiLaw.LogicalRect(321, 208, 16, 16),
            "shared Craft/TradeSkill geometry drift");

        Check(ProfessionFrameUiLaw.MaximumScroll(7) == 0 &&
              ProfessionFrameUiLaw.MaximumScroll(10) == 2 &&
              ProfessionFrameUiLaw.ClampScroll(9, 10) == 2 &&
              Math.Abs(ProfessionFrameUiLaw.RankFraction(75, 150) - .5f) < .001f &&
              ProfessionFrameUiLaw.ScrollThumbY(2, 2) == 192,
            "profession rank/list scroll law drift");

        Check(ProfessionFrameUiLaw.CraftableCount([(12u, 3u), (5u, 2u)]) == 2 &&
              ProfessionFrameUiLaw.CraftableCount([]) == 1 &&
              ProfessionFrameUiLaw.ClampCreateCount(8, 3) == 3 &&
              ProfessionFrameUiLaw.RowLabel("Linen Bandage", 4) == " Linen Bandage [4]" &&
              ProfessionFrameUiLaw.CraftSubText(" Journeyman ") == "(Journeyman)",
            "profession craft-count/subtext law drift");

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
              runtime.Contains("DrawUnitPortraitImage", StringComparison.Ordinal) &&
              runtime.Contains("DrawProfessionScrollBar", StringComparison.Ordinal) &&
              runtime.Contains("DrawProfessionCountSpinner", StringComparison.Ordinal) &&
              runtime.Contains("BuildTradeSkillTree", StringComparison.Ordinal) &&
              runtime.Contains("DrawProfessionTradeSkillControls", StringComparison.Ordinal) &&
              runtime.Contains("recipe.Description", StringComparison.Ordinal) &&
              runtime.Contains("PrepareItemTooltipBodySnapshot(product", StringComparison.Ordinal) &&
              runtime.Contains("PrepareItemTooltipBodySnapshot(item, reagent.Count)",
                  StringComparison.Ordinal) &&
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

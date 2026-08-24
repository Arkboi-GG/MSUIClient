using System.Numerics;

namespace MSUIClient.Engine.UI;

/// <summary>
/// Shared build-5875 CraftFrame/TradeSkillFrame geometry and bounded interaction law.
/// Both panels use the same 384x512 ClassTrainer/TradeSkill shell and list/detail layout;
/// only TradeSkill exposes live group/filter/count controls.
/// </summary>
public static class ProfessionFrameUiLaw
{
    public enum CraftTooltipKind
    {
        Spell,
        Item,
    }

    public readonly record struct CraftTooltipTarget(CraftTooltipKind Kind, uint Id);

    public readonly record struct LogicalRect(float X, float Y, float Width, float Height)
    {
        public Vector2 Min => new(X, Y);
        public Vector2 Size => new(Width, Height);
    }

    public readonly record struct TooltipSeat(Vector2 Anchor, Vector2 Pivot);
    public readonly record struct ArtPiece(string Element, string Path, LogicalRect Rect);

    public const int VisibleRows = 8;
    public const float Width = 384f;
    public const float Height = 512f;
    public const float Top = 104f;
    public const float RowHeight = 16f;
    public const float DescriptionWidth = 290f;
    public const string RequirementFont = "GameFontHighlightSmall";
    public const string RequiresLabel = "Requires:";
    public const string TitleFont = "GameFontNormal";
    public const uint CraftTypeBeastTraining = 1;
    public const uint SpellAttributeTradeSkill = 0x20;
    public const uint EffectCreateItem = 24;
    public const uint EffectLearnSpell = 36;
    public const string SkillBorderPath = @"Interface\TradeSkillFrame\UI-TradeSkill-SkillBorder";
    public const string TopLeftArt = @"Interface\ClassTrainerFrame\UI-ClassTrainer-TopLeft";
    public const string TopRightArt = @"Interface\ClassTrainerFrame\UI-ClassTrainer-TopRight";
    public const string CraftBottomLeftArt = @"Interface\ClassTrainerFrame\UI-ClassTrainer-BotLeft";
    public const string TradeSkillBottomLeftArt = @"Interface\TradeSkillFrame\UI-TradeSkill-BotLeft";
    public const string BottomRightArt = @"Interface\ClassTrainerFrame\UI-ClassTrainer-BotRight";
    public const string FallbackIconPath = @"Interface\Icons\INV_Misc_QuestionMark";
    public const string RankFillPath = @"Interface\PaperDollInfoFrame\UI-Character-Skills-Bar";
    public const string RankBorderPath = @"Interface\PaperDollInfoFrame\UI-Character-Skills-BarBorder";
    public const string ScrollKnobPath = @"Interface\Buttons\UI-ScrollBar-Knob";
    public const string CollapseAllLabel = "All";
    public const string CollapseAllFont = "GameFontNormalSmall";
    public const string CollapseAllDisabledFont = "GameFontDisableSmall";
    public const string CollapseAllMinusPath = @"Interface\Buttons\UI-MinusButton-Up";
    public const string CollapseAllPlusPath = @"Interface\Buttons\UI-PlusButton-Up";
    public const string CollapseAllHighlightPath = @"Interface\Buttons\UI-PlusButton-Hilight";
    public const string OpenSound = "igCharacterInfoOpen";
    public const string CloseSound = "igCharacterInfoClose";
    public const string SoundCategory = "ui.profession";
    // CraftFrame_Update overrides the XML defaults: fill (0,0,1,.5), background (0,0,.75,.5).
    public const uint RankBackgroundColor = 0x80bf0000;
    public const uint RankFillColor = 0x80ff0000;
    public static readonly Vector2 PortraitOffset = new(7, 6);
    public const float PortraitSize = 60f;
    public static readonly Vector2 HeaderMarkerOffset = new(1, 1);
    public static readonly Vector2 HeaderTextOffset = new(18, 1);
    public static readonly Vector2 CraftRecipeNameOffset = new(4, 0);
    public static readonly Vector2 TradeSkillRecipeNameOffset = new(22, 0);
    public static readonly Vector2 CraftSubTextOffset = new(190, 1);
    public static readonly Vector2 HorizontalBarSize = new(331, 16);
    public static readonly Vector2 ReagentIconSize = new(28);
    public static readonly Vector2 ReagentIconOffset = new(0, 2);
    public static readonly Vector2 ReagentNameOffset = new(32, 2);
    public static readonly Vector2 ReagentCountOffset = new(32, 17);
    public static readonly Vector2 CloseSize = new(32);
    public static readonly Vector2 RankTextOffset = new(6, 1);
    public static readonly Vector2 ScrollButtonUvMin = new(.25f);
    public static readonly Vector2 ScrollButtonUvMax = new(.75f);
    public static readonly Vector2 SkillBorderLeftUvMin = Vector2.Zero;
    public static readonly Vector2 SkillBorderLeftUvMax = new(1, .25f);
    public static readonly Vector2 SkillBorderRightUvMin = new(0, .25f);
    public static readonly Vector2 SkillBorderRightUvMax = new(.109375f, .5f);

    public static readonly LogicalRect Rank = new(73, 37, 268, 15);
    public static readonly LogicalRect RankBorder = new(68, 28.5f, 281, 32);
    public static readonly LogicalRect SkillBorderLeft = new(63, 50, 256, 8);
    public static readonly LogicalRect SkillBorderRight = new(319, 50, 28, 8);
    public static readonly LogicalRect List = new(22, 96, 293, 128);
    public static readonly LogicalRect ScrollUp = new(321, 96, 16, 16);
    public static readonly LogicalRect ScrollSlider = new(321, 112, 16, 96);
    public static readonly LogicalRect ScrollDown = new(321, 208, 16, 16);
    public static readonly LogicalRect DetailProduct = new(28, 237, 37, 37);
    public static readonly LogicalRect DetailHeaderLeft = new(20, 231, 256, 64);
    public static readonly LogicalRect DetailHeaderRight = new(276, 231, 64, 64);
    public static readonly LogicalRect DetailEmptySlot = new(15, 224, 64, 64);
    public static readonly LogicalRect CreateAll = new(18, 411, 80, 22);
    public static readonly LogicalRect Create = new(184, 411, 80, 22);
    public static readonly LogicalRect Exit = new(265, 411, 80, 22);
    public static readonly LogicalRect CountDecrement = new(101, 411, 23, 22);
    public static readonly LogicalRect CountInput = new(128, 412, 30, 20);
    public static readonly LogicalRect CountIncrement = new(161, 411, 23, 22);
    public static readonly LogicalRect CollapseAll = new(23, 73, 40, 22);
    public static readonly LogicalRect CollapseAllIcon = new(23, 76, 16, 16);
    public static readonly Vector2 CollapseAllLabelCenter = new(52, 84);
    public static readonly ArtPiece[] CollapseAllTabArt =
    [
        new("TradeSkillExpandTabLeft",
            @"Interface\ClassTrainerFrame\UI-ClassTrainer-ExpandTab-Left",
            new(15, 71, 8, 32)),
        new("TradeSkillExpandTabMiddle",
            @"Interface\QuestFrame\UI-QuestLogSortTab-Middle",
            new(23, 65, 38, 32)),
        new("TradeSkillExpandTabRight",
            @"Interface\QuestFrame\UI-QuestLogSortTab-Right",
            new(61, 65, 8, 32)),
    ];
    public static readonly DropdownCapsuleUiLaw.Layout InvSlotDropDown =
        DropdownCapsuleUiLaw.TopRight(Width, 25, 66, 120);
    public static readonly DropdownCapsuleUiLaw.Layout SubClassDropDown =
        DropdownCapsuleUiLaw.LeftOf(InvSlotDropDown, 35, 120);
    public static readonly Vector2 TitleCenter = new(192, 17);
    public static readonly Vector2 HorizontalBar = new(15, 221);
    public static readonly Vector2 ProductName = new(70, 239);
    public static readonly Vector2 CraftRequirements = new(70, 253);
    public static readonly Vector2 TradeSkillRequirementLabel = new(70, 253);
    public static readonly Vector2 Description = new(25, 284);
    public static readonly Vector2 TradeSkillReagentLabel = new(28, 281);
    public static readonly Vector2 Close = new(323, 8);

    public static Vector2 FrameOrigin(float scale) => new(0, Top * scale);
    public static Vector2 FrameSize(float scale) => new(Width * scale, Height * scale);
    // Craft's row template has no HighlightTexture/OnEnter highlight. TradeSkill's row template
    // does, but only for its fold marker; the persistent full-row glow remains selection-owned.
    public static bool RowHoverHighlight(bool tradeSkill) => tradeSkill;
    // UI-EmptySlot is a layer of the recipe icon button, so HideDetails hides both together.
    public static bool DetailIconVisible(int recipeCount) => recipeCount > 0;
    public static Vector2 RecipeNameOffset(bool tradeSkill) =>
        tradeSkill ? TradeSkillRecipeNameOffset : CraftRecipeNameOffset;
    // ANCHOR_RIGHT: GameTooltip BOTTOMLEFT to the owner's TOPRIGHT. CraftIcon and both
    // TradeSkill product owners (recipe row and detail icon) use this exact shared seat.
    public static TooltipSeat RightTooltipSeat(Vector2 ownerMin, Vector2 ownerMax) =>
        new(new Vector2(ownerMax.X, ownerMin.Y), new Vector2(0, 1));
    public static TooltipSeat CraftDetailTooltipSeat(Vector2 ownerMin, Vector2 ownerMax) =>
        RightTooltipSeat(ownerMin, ownerMax);
    // CraftReagent uses ANCHOR_TOPLEFT: GameTooltip BOTTOMLEFT to the reagent's TOPLEFT.
    public static TooltipSeat CraftReagentTooltipSeat(Vector2 ownerMin) =>
        new(ownerMin, new Vector2(0, 1));
    public static LogicalRect Row(int visible) =>
        new(List.X, List.Y + Math.Clamp(visible, 0, VisibleRows - 1) * RowHeight,
            List.Width, RowHeight);
    public static string BottomLeftArtFor(bool tradeSkill) =>
        tradeSkill ? TradeSkillBottomLeftArt : CraftBottomLeftArt;

    public static LogicalRect Reagent(int index, bool tradeSkill, float labelY)
    {
        int bounded = Math.Clamp(index, 0, 7);
        float baseX = tradeSkill ? 23 : 25;
        // Both reference XML files carry the original anchor-chain quirk: reagent 7 hangs from
        // reagent 6 (the right column), then reagent 8 hangs to its right.
        int column = bounded < 6 ? bounded % 2 : bounded - 5;
        int row = bounded < 6 ? bounded / 2 : 3;
        return new(baseX + column * 140, labelY + 13 + row * 34, 140, 32);
    }

    public static Vector2 CraftReagentLabelAt(float descriptionHeight, bool hasDescription) =>
        Description + new Vector2(0, hasDescription ? Math.Max(0, descriptionHeight) + 10 : 0);

    public static Vector2 TradeSkillRequirementTextAt(float labelWidth, float scale) =>
        TradeSkillRequirementLabel + new Vector2(labelWidth / Math.Max(float.Epsilon, scale) + 4, 0);

    public static Vector2 RankFillSize(Vector2 rankSize, float fraction) =>
        new(rankSize.X * Math.Clamp(fraction, 0, 1), rankSize.Y);

    public static Vector2 RankFillUvMax(float fraction) =>
        new(Math.Clamp(fraction, 0, 1), 1);

    public static Vector2 RankValueTextAt(Vector2 rankMin, float nameWidth, float scale) =>
        rankMin + RankTextOffset * scale + new Vector2(nameWidth + 13 * scale, 0);

    public static string RankValueText(uint value, uint maximum) => $"{value}/{maximum}";

    public static string CraftRequirementsText(
        IEnumerable<(string Name, bool Met)> requirements)
    {
        string names = RequirementNamesMarkup(requirements);
        return names.Length == 0 ? "" : RequiresLabel + " " + names;
    }

    public static string RequirementNamesMarkup(
        IEnumerable<(string Name, bool Met)> requirements) =>
        string.Join(", ", requirements
            .Where(row => !string.IsNullOrWhiteSpace(row.Name))
            .Select(row => row.Met ? row.Name : $"|cffff2020{row.Name}|r"));

    public static Vector2 ScrollThumbMin(Vector2 origin, float thumbY, float scale) =>
        origin + new Vector2(ScrollSlider.X, thumbY) * scale;

    public static int MaximumScroll(int rowCount) => Math.Max(0, rowCount - VisibleRows);
    public static int ClampScroll(int value, int rowCount) =>
        Math.Clamp(value, 0, MaximumScroll(rowCount));
    public static int RefreshedSelection(uint selectedSpellId,
        IReadOnlyList<uint> orderedSpellIds)
    {
        if (selectedSpellId != 0)
        {
            for (int index = 0; index < orderedSpellIds.Count; index++)
                if (orderedSpellIds[index] == selectedSpellId) return index;
        }
        return 0;
    }
    public static float RankFraction(uint value, uint maximum) =>
        maximum == 0 ? 0 : Math.Clamp((float)value / maximum, 0f, 1f);
    public static float ScrollThumbY(int value, int maximum) => ScrollSlider.Y +
        (maximum <= 0 ? 0 : Math.Clamp((float)value / maximum, 0f, 1f) *
            (ScrollSlider.Height - ScrollUp.Height));
    public static int ScrollFromThumb(float logicalY, int maximum) => maximum <= 0 ? 0 :
        Math.Clamp((int)MathF.Round((logicalY - ScrollSlider.Y - ScrollUp.Height * .5f) /
            (ScrollSlider.Height - ScrollUp.Height) * maximum), 0, maximum);

    public static int CraftableCount(IEnumerable<(uint Have, uint Need)> reagents)
    {
        (uint Have, uint Need)[] material = reagents.Where(x => x.Need > 0).ToArray();
        return material.Length == 0 ? 0 : Math.Clamp(material.Min(x => (int)(x.Have / x.Need)), 0, 999);
    }

    public static int ClampCreateCount(int requested, int craftable) =>
        Math.Clamp(requested, 1, Math.Max(1, craftable));

    public static string RowLabel(string name, int craftable) =>
        craftable > 0 ? $" {name} [{craftable}]" : $" {name}";

    public static string CraftSubText(string? rank) =>
        string.IsNullOrWhiteSpace(rank) ? "" : $"({rank.Trim()})";

    public static IReadOnlyList<string> WrapDescription(string? text, float width,
        Func<string, float> measure)
    {
        if (string.IsNullOrWhiteSpace(text) || width <= 0) return [];
        var lines = new List<string>();
        foreach (string paragraph in text.Replace("\r", "").Split('\n'))
        {
            string current = "";
            foreach (string word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = current.Length == 0 ? word : current + " " + word;
                if (current.Length > 0 && measure(candidate) > width)
                {
                    lines.Add(current);
                    current = word;
                }
                else current = candidate;
            }
            if (current.Length > 0) lines.Add(current);
        }
        return lines;
    }

    public static string ReagentHaveText(uint have) => have >= 100 ? "*" : have.ToString();

    // GetCraftReagentInfo yields nil name/icon while the ask-once item query is unresolved.
    // CraftFrame hides that slot, and only visible resolved slots participate in its local gate.
    public static bool ReagentTemplateVisible(bool hasTemplate, string? iconPath) =>
        hasTemplate && !string.IsNullOrWhiteSpace(iconPath);

    public static bool ReagentAllowsCreate(bool visible, uint have, uint need) =>
        !visible || have >= need;

    // The feeds append tools and spell foci only after their display names resolve. Pending
    // catalog rows do not become synthetic Item/Spell Focus labels in the Requires line.
    public static string? ResolvedRequirementName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : name;

    public static uint EffectiveSkill(uint raw, int bonus) =>
        (uint)Math.Max(0, (long)raw + bonus);

    public static int DifficultyTier(uint rank, uint trivialLow, uint trivialHigh)
    {
        uint low = trivialLow == 0 ? trivialHigh >= 25 ? trivialHigh - 25 : 0 : trivialLow;
        if (rank >= trivialHigh) return 3;
        if (rank >= ((ulong)low + trivialHigh) / 2) return 2;
        return rank >= low ? 1 : 0;
    }

    public static string DifficultyName(uint rank, uint trivialLow, uint trivialHigh) =>
        DifficultyTier(rank, trivialLow, trivialHigh) switch
        {
            0 => "orange",
            1 => "yellow",
            2 => "green",
            _ => "gray",
        };

    public static Vector4 DifficultyColor(int tier) => Math.Clamp(tier, 0, 3) switch
    {
        0 => new(1f, .5f, .25f, 1f),
        1 => new(1f, 1f, 0f, 1f),
        2 => new(.25f, .75f, .25f, 1f),
        _ => new(.5f, .5f, .5f, 1f),
    };

    public static bool CraftRecipeEligible(uint castUi, uint attributes, uint craftType) =>
        craftType != 0 && castUi == craftType && (attributes & SpellAttributeTradeSkill) == 0;

    // An explicit Craft/TradeSkill opener owns a live panel snapshot even when its filtered recipe
    // list is empty. The current CraftFrame still paints its shell and disabled action row in that
    // state; recipe-count gating applies only to the legacy panel-kind-less diagnostic opener.
    public static bool OpenForSnapshot(bool hasExplicitPanelKind, int recipeCount) =>
        hasExplicitPanelKind || recipeCount > 0;

    public static bool TradeSkillRecipeEligible(uint attributes) =>
        (attributes & SpellAttributeTradeSkill) != 0;

    public static int CompareCraftRecipes(int tierA, string nameA, uint spellLevelA, uint spellIdA,
        int tierB, string nameB, uint spellLevelB, uint spellIdB, uint craftType)
    {
        int order = tierA.CompareTo(tierB);
        if (order != 0) return order;
        order = StringComparer.OrdinalIgnoreCase.Compare(nameA, nameB);
        if (order != 0) return order;
        order = StringComparer.Ordinal.Compare(nameA, nameB);
        if (order != 0) return order;
        if (craftType == CraftTypeBeastTraining)
        {
            order = spellLevelA.CompareTo(spellLevelB);
            if (order != 0) return order;
        }
        return spellIdA.CompareTo(spellIdB);
    }

    public static CraftTooltipTarget CraftTooltip(uint recipeSpellId,
        IReadOnlyList<uint>? effects, IReadOnlyList<uint>? itemTypes,
        IReadOnlyList<uint>? triggerSpells)
    {
        for (int lane = 0; lane < 3; lane++)
        {
            uint effect = effects is { Count: > 0 } && lane < effects.Count ? effects[lane] : 0;
            if (effect == EffectLearnSpell)
                return new(CraftTooltipKind.Spell,
                    triggerSpells is { Count: > 0 } && lane < triggerSpells.Count
                        ? triggerSpells[lane] : 0);
            if (effect == EffectCreateItem)
                return new(CraftTooltipKind.Item,
                    itemTypes is { Count: > 0 } && lane < itemTypes.Count ? itemTypes[lane] : 0);
        }
        return new(CraftTooltipKind.Spell, recipeSpellId);
    }

    public static ulong GroupKey(uint itemClass, uint subclass) =>
        ((ulong)itemClass << 32) | subclass;

    /// <summary>The build-5875 29-entry InventoryType table after its live overrides.</summary>
    public static uint InventorySlotMask(uint inventoryType) => inventoryType switch
    {
        1 => 1u << 0,
        2 => 1u << 1,
        3 => 1u << 2,
        4 => 1u << 3,
        5 or 20 => 1u << 4,
        6 => 1u << 5,
        7 => 1u << 6,
        8 => 1u << 7,
        9 => 1u << 8,
        10 => 1u << 9,
        11 => 1u << 10,
        12 => 1u << 12,
        13 => (1u << 15) | (1u << 16),
        14 or 22 or 23 => 1u << 16,
        15 or 25 or 26 or 28 => 1u << 17,
        16 => 1u << 14,
        17 or 21 => 1u << 15,
        18 => 1u << 19,
        19 => 1u << 18,
        _ => 1u << 23,
    };

    public static string InventorySlotName(int bit) => bit switch
    {
        0 => "Head", 1 => "Neck", 2 => "Shoulders", 3 => "Shirt", 4 => "Chest",
        5 => "Waist", 6 => "Legs", 7 => "Feet", 8 => "Wrist", 9 => "Hands",
        10 or 11 => "Finger", 12 or 13 => "Trinket", 14 => "Back",
        15 => "Main Hand", 16 => "Off Hand", 17 => "Ranged", 18 => "Tabard",
        >= 19 and <= 22 => "Bag", 23 => "Not equippable.", _ => "",
    };

    public static IReadOnlyList<int> PresentInventorySlots(IEnumerable<uint> inventoryTypes)
    {
        uint mask = inventoryTypes.Aggregate(0u, (current, type) => current | InventorySlotMask(type));
        return Enumerable.Range(0, 24).Where(bit => (mask & (1u << bit)) != 0).ToArray();
    }

    public readonly record struct TradeSkillNode(int RecipeIndex, uint ItemClass, uint Subclass,
        string GroupName, uint InventoryType, uint ItemLevel, int DifficultyTier, string Name);
    public readonly record struct TradeSkillRow(bool Header, ulong GroupKey, string Text,
        int RecipeIndex, bool Expanded);

    public static IReadOnlyList<TradeSkillRow> BuildTradeSkillTree(
        IEnumerable<TradeSkillNode> source, IReadOnlySet<ulong> collapsed,
        ulong? subclassFilter, int? inventorySlotFilter)
    {
        var nodes = source.Where(node =>
            (!subclassFilter.HasValue || GroupKey(node.ItemClass, node.Subclass) == subclassFilter) &&
            (!inventorySlotFilter.HasValue ||
             (InventorySlotMask(node.InventoryType) & (1u << inventorySlotFilter.Value)) != 0));
        var groups = nodes.GroupBy(node => new
            {
                Key = GroupKey(node.ItemClass, node.Subclass),
                node.ItemClass,
                node.GroupName,
            })
            .Select(group => new
            {
                group.Key.Key,
                group.Key.ItemClass,
                Name = group.Key.GroupName,
                Entries = group.OrderBy(node => node.DifficultyTier)
                    .ThenBy(node => node.ItemLevel)
                    .ThenBy(node => node.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(node => node.Name, StringComparer.Ordinal).ToArray(),
            })
            .OrderBy(group => group.ItemClass)
            .ThenBy(group => group.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.Name, StringComparer.Ordinal);
        var rows = new List<TradeSkillRow>();
        foreach (var group in groups)
        {
            bool expanded = !collapsed.Contains(group.Key);
            rows.Add(new(true, group.Key, group.Name, -1, expanded));
            if (!expanded) continue;
            rows.AddRange(group.Entries.Select(node =>
                new TradeSkillRow(false, group.Key, node.Name, node.RecipeIndex, false)));
        }
        return rows;
    }
}

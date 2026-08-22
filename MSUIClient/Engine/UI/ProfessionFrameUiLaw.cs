using System.Numerics;

namespace MSUIClient.Engine.UI;

/// <summary>
/// Shared build-5875 CraftFrame/TradeSkillFrame geometry and bounded interaction law.
/// Both panels use the same 384x512 ClassTrainer/TradeSkill shell and list/detail layout;
/// only TradeSkill exposes live group/filter/count controls.
/// </summary>
public static class ProfessionFrameUiLaw
{
    public readonly record struct LogicalRect(float X, float Y, float Width, float Height)
    {
        public Vector2 Min => new(X, Y);
        public Vector2 Size => new(Width, Height);
    }

    public const int VisibleRows = 8;
    public const float Width = 384f;
    public const float Height = 512f;
    public const float Top = 104f;
    public const float RowHeight = 16f;
    public const string RankFillPath = @"Interface\PaperDollInfoFrame\UI-Character-Skills-Bar";
    public const string RankBorderPath = @"Interface\PaperDollInfoFrame\UI-Character-Skills-BarBorder";
    public const string ScrollKnobPath = @"Interface\Buttons\UI-ScrollBar-Knob";
    public static readonly Vector2 PortraitOffset = new(7, 6);
    public const float PortraitSize = 60f;

    public static readonly LogicalRect Rank = new(73, 37, 268, 15);
    public static readonly LogicalRect RankBorder = new(68, 28.5f, 281, 32);
    public static readonly LogicalRect List = new(22, 96, 293, 128);
    public static readonly LogicalRect ScrollUp = new(321, 96, 16, 16);
    public static readonly LogicalRect ScrollSlider = new(321, 112, 16, 96);
    public static readonly LogicalRect ScrollDown = new(321, 208, 16, 16);
    public static readonly LogicalRect DetailProduct = new(28, 237, 37, 37);
    public static readonly LogicalRect ProductTexture = new(24, 240, 32, 32);
    public static readonly LogicalRect CreateAll = new(18, 411, 80, 22);
    public static readonly LogicalRect Create = new(184, 411, 80, 22);
    public static readonly LogicalRect Exit = new(265, 411, 80, 22);
    public static readonly LogicalRect CountDecrement = new(101, 411, 23, 22);
    public static readonly LogicalRect CountInput = new(128, 412, 30, 20);
    public static readonly LogicalRect CountIncrement = new(161, 411, 23, 22);
    public static readonly LogicalRect CollapseAll = new(23, 74, 40, 22);
    public static readonly LogicalRect SubClassFilter = new(71, 66, 128, 22);
    public static readonly LogicalRect InvSlotFilter = new(203, 66, 136, 22);
    public static readonly Vector2 FilterMenu = new(71, 89);
    public static readonly Vector2 InventoryFilterMenu = new(203, 89);
    public static readonly Vector2 TitleCenter = new(190, 18);
    public static readonly Vector2 HorizontalBar = new(15, 221);
    public static readonly Vector2 ProductName = new(70, 239);
    public static readonly Vector2 Description = new(25, 284);
    public static readonly Vector2 ReagentLabel = new(25, 292);
    public static readonly Vector2 ReagentGrid = new(25, 305);
    public static readonly Vector2 Close = new(323, 8);

    public static Vector2 FrameOrigin(float scale) => new(0, Top * scale);
    public static Vector2 FrameSize(float scale) => new(Width * scale, Height * scale);
    public static LogicalRect Row(int visible) =>
        new(List.X, List.Y + Math.Clamp(visible, 0, VisibleRows - 1) * RowHeight,
            List.Width, RowHeight);
    public static LogicalRect Reagent(int index, float descriptionHeight = 0) =>
        new(ReagentGrid.X + Math.Clamp(index, 0, 7) % 2 * 145,
            ReagentGrid.Y + descriptionHeight + Math.Clamp(index, 0, 7) / 2 * 32,
            140, 32);

    public static int MaximumScroll(int rowCount) => Math.Max(0, rowCount - VisibleRows);
    public static int ClampScroll(int value, int rowCount) =>
        Math.Clamp(value, 0, MaximumScroll(rowCount));
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
        return material.Length == 0 ? 1 : Math.Clamp(material.Min(x => (int)(x.Have / x.Need)), 0, 999);
    }

    public static int ClampCreateCount(int requested, int craftable) =>
        Math.Clamp(requested, 1, Math.Max(1, craftable));

    public static string RowLabel(string name, int craftable) =>
        craftable > 0 ? $" {name} [{craftable}]" : $" {name}";

    public static string CraftSubText(string? rank) =>
        string.IsNullOrWhiteSpace(rank) ? "" : $"({rank.Trim()})";

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

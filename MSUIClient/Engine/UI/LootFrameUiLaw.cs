using System.Numerics;

namespace MSUIClient.Engine.UI;

/// <summary>LootFrame_OnShow's Lua-owned open fork. Normal non-empty loot is C-side and silent here.</summary>
public static class LootFrameUiLaw
{
    public enum RowClickAction { None, DressUp, InsertChat, Loot }

    public const byte FishingLootType = 3;
    public const string EmptyOpenSound = "LOOTWINDOWOPENEMPTY";
    public const string FishingOpenSound = "FISHING REEL IN";
    public const string CorpseOverlay = @"Interface\TargetingFrame\TargetDead";
    public const string FishingOverlay = @"Interface\LootFrame\FishingLoot-Icon";
    public const string SoundCategory = "ui.loot";
    public const string PanelPath = @"Interface\LootFrame\UI-LootPanel";
    public const string NamePlatePath = @"Interface\QuestFrame\UI-QuestItemNameFrame";
    public const string CloseUpPath = @"Interface\Buttons\UI-Panel-MinimizeButton-Up";
    public const string CloseHighlightPath =
        @"Interface\Buttons\UI-Panel-MinimizeButton-Highlight";
    public const string TitleFont = "GameFontNormal";
    public const string NameFont = "GameFontNormal";
    public const string CountFont = "NumberFontNormal";
    public const float RowPitch = 41f;

    public readonly record struct LogicalRect(float X, float Y, float Width, float Height)
    {
        public Vector2 Min => new(X, Y);
        public Vector2 Size => new(Width, Height);
        public Vector2 ScaledMin(Vector2 origin, float scale) => origin + Min * scale;
        public Vector2 ScaledSize(float scale) => Size * scale;
    }

    public readonly record struct TooltipSeat(Vector2 Anchor, Vector2 Pivot);

    public static readonly Vector2 FrameOffset = new(16, 12);
    public static readonly Vector2 TraceAbsoluteOffset = new(16, 116);
    public static readonly LogicalRect Frame = new(0, 0, 256, 256);
    public static readonly LogicalRect PortraitOverlay = new(10, 8, 58, 58);
    public static readonly Vector2 TitleCenter = new(116, 26);
    public static readonly LogicalRect PagerUp = new(25, 208, 32, 32);
    public static readonly LogicalRect PagerDown = new(111, 208, 32, 32);
    public static readonly LogicalRect CloseArt = new(159, 10, 32, 32);
    public static readonly LogicalRect CloseHit = new(165, 16, 20, 20);
    public static readonly Vector2 RowIconSize = new(37, 37);
    public static readonly Vector2 RowHitSize = new(160, 37);
    public static readonly LogicalRect RowNamePlate = new(30, -12.5f, 130, 62);
    public static readonly LogicalRect RowNameBox = new(45, -.5f, 93, 38);
    public static readonly LogicalRect StackCountBox = new(4, 22.5f, 34, 12);

    public static LogicalRect Row(int visibleIndex) =>
        new(24, 80 + Math.Max(0, visibleIndex) * RowPitch, 160, 37);

    // ANCHOR_RIGHT with x=-123 on the 160x37 row: tooltip BOTTOMLEFT lands at the
    // 37px icon's TOPRIGHT instead of the row's far edge.
    public static TooltipSeat RowTooltipSeat(Vector2 rowMin, float scale) =>
        new(rowMin + new Vector2(RowIconSize.X * scale, 0), new Vector2(0, 1));

    public static IReadOnlyList<string> WrapName(string? text, float width,
        int maximumLines, Func<string, float> measure)
    {
        if (string.IsNullOrWhiteSpace(text) || width <= 0 || maximumLines <= 0) return [];
        var lines = new List<string>();
        string current = "";
        foreach (string word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = current.Length == 0 ? word : current + " " + word;
            if (current.Length > 0 && measure(candidate) > width)
            {
                lines.Add(current);
                if (lines.Count == maximumLines) return lines;
                current = word;
            }
            else current = candidate;
        }
        if (current.Length > 0 && lines.Count < maximumLines) lines.Add(current);
        return lines;
    }

    public static Vector2 NameLineMin(Vector2 rowMin, float scale, int line,
        int lineCount, float pitch) => new(
        rowMin.X + RowNameBox.X * scale,
        rowMin.Y + RowNameBox.Y * scale +
            Math.Max(0, RowNameBox.Height * scale - Math.Max(0, lineCount) * pitch) * .5f +
            Math.Max(0, line) * pitch);

    public static Vector2 CountRightTop(Vector2 rowMin, float scale) => new(
        rowMin.X + (StackCountBox.X + StackCountBox.Width) * scale,
        GameText.BoxCenteredTop(CountFont,
            rowMin.Y + StackCountBox.Y * scale, StackCountBox.Height, scale));

    public static string PagerPath(bool up, bool enabled) =>
        $@"Interface\ChatFrame\UI-ChatIcon-{(up ? "ScrollUp" : "ScrollDown")}-" +
        (enabled ? "Up" : "Disabled");

    public static string ItemLink(uint itemId, string name, uint quality) =>
        UiTextMarkupLaw.ItemLink(itemId, name, quality);

    public static RowClickAction ClickAction(bool clicked, bool control, bool shift,
        bool alt, bool chatOpen, bool isItem, bool linkAvailable)
    {
        if (!clicked) return RowClickAction.None;
        if (control)
            return isItem && linkAvailable ? RowClickAction.DressUp : RowClickAction.None;
        if (shift)
            return isItem && chatOpen && linkAvailable
                ? RowClickAction.InsertChat
                : RowClickAction.None;
        if (alt) return RowClickAction.None;
        return RowClickAction.Loot;
    }

    public readonly record struct OpenPresentation(string OverlayPath, string? SoundCue);

    public static OpenPresentation OnShow(byte lootType, int itemCount, uint gold)
    {
        int visibleRows = Math.Max(0, itemCount) + (gold > 0 ? 1 : 0);
        if (visibleRows == 0) return new(CorpseOverlay, EmptyOpenSound);
        if (lootType == FishingLootType) return new(FishingOverlay, FishingOpenSound);
        return new(CorpseOverlay, null);
    }
}

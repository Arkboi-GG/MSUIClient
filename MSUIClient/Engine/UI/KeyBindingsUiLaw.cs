using System.Numerics;

namespace MSUIClient.Engine.UI;

/// <summary>
/// Rule-owned geometry for MSUI's preserved standalone Key Bindings shell, with current
/// Benilla's collapsed-category and binding-search behavior hosted inside that shell.
/// </summary>
public static class KeyBindingsUiLaw
{
    public const string TitleFont = "GameFontNormal";
    public const string ColumnHeaderFont = "GameFontNormalSmall";
    public const string CategoryFont = "GameFontNormal";
    public const string CategoryHighlightFont = "GameFontHighlight";
    public const string CommandFont = "GameFontNormalSmall";
    public const string KeyNormalFont = "GameFontHighlightSmall";
    public const string KeyHighlightFont = "GameFontHighlightSmall";
    public const string KeyDisabledFont = "GameFontDisableSmall";
    public readonly record struct Rect(float X, float Y, float Width, float Height)
    {
        public Vector2 Min => new(X, Y);
        public Vector2 Size => new(Width, Height);
    }

    public const float FrameTop = 104f;
    public static Vector2 WindowMinimum => new(0, FrameTop);
    public static Vector2 FrameSize => new(640, 512);
    public static Vector2 TitleCenter => new(290, 24);
    public static Vector2 CommandTitle => new(26, 35);
    public static Vector2 KeyOneCenter => new(290, 41);
    public static Vector2 KeyTwoCenter => new(470, 41);
    public static Rect Search => new(18, 8, 180, 22);
    public static Vector2 SearchPlaceholderOffset => new(7, 5);
    public static Rect Rows => new(27, 53, 535, 390);
    public const float RowPitch = 23f;
    public const int VisibleRows = 17;
    public static Vector2 RowMinimum(int visibleIndex) =>
        Rows.Min + new Vector2(0, Math.Max(0, visibleIndex) * RowPitch);
    public static Vector2 RowHitSize => new(Rows.Width, RowPitch);
    public static Rect HeaderGlyph => new(2, 3.5f, 16, 16);
    public static Vector2 HeaderTextOffset => new(24, 5.5f);
    public static Vector2 CommandTextOffset => new(0, 6.5f);
    public static Rect PrimaryKey => new(175, 1, 180, 22);
    public static Rect SecondaryKey => new(355, 1, 180, 22);
    public static Vector2 ScrollMinimum => new(584, 52);
    public const float ScrollHeight = 390f;
    public static Vector2 FeedbackCenter => new(320, 455);
    public static Vector2 CharacterSpecificMinimum => new(395, 10);
    public static Rect Defaults => new(10, 469, 130, 22);
    public static Rect Unbind => new(230, 469, 130, 22);
    public static Rect Okay => new(360, 469, 130, 22);
    public static Rect Cancel => new(490, 469, 130, 22);

    public readonly record struct ArtSlice(string Path, Vector2 Offset, Vector2 Size);

    public static IReadOnlyList<ArtSlice> Art =>
    [
        new(@"Interface\KeyBindingFrame\UI-KeyBindingFrame-TopLeft", new(0, 0), new(256)),
        new(@"Interface\KeyBindingFrame\UI-KeyBindingFrame-Top", new(256, 0), new(256)),
        new(@"Interface\KeyBindingFrame\UI-KeyBindingFrame-TopRight", new(512, 0), new(128, 256)),
        new(@"Interface\KeyBindingFrame\UI-KeyBindingFrame-BotLeft", new(0, 256), new(256)),
        new(@"Interface\KeyBindingFrame\UI-KeyBindingFrame-Bot", new(256, 256), new(256)),
        new(@"Interface\KeyBindingFrame\UI-KeyBindingFrame-BotRight", new(512, 256), new(128, 256)),
    ];

    public static int MaximumScroll(int rowCount) =>
        Math.Max(0, rowCount - VisibleRows);

    public static int ClampScroll(int requested, int rowCount) =>
        Math.Clamp(requested, 0, MaximumScroll(rowCount));

    public static bool MatchesSearch(string category, string label, string query)
    {
        string needle = query.Trim();
        return needle.Length == 0 ||
            category.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
            label.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }
}

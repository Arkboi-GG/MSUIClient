using System.Numerics;

namespace MSUIClient.Engine.UI;

/// <summary>
/// Frozen Macro Book geometry. Authored in FrameXML units (1 unit = 1 px at UI scale 1, +y
/// down as everywhere in this client's laws) on a 640x512 DialogFrame-style panel: the vanilla
/// dialog backdrop and header plaque, HelpFrame inset tabs for the two books, a UIPanelButton
/// row, Common-Input-Border edit boxes, UIPanelScrollBar bars and the QuestLog plus/minus for
/// sections. Nothing here is a 1.12 window's measurement - the Macro Book is ours - but every
/// piece is a 1.12 template so it reads as one of theirs.
///
/// Left column: search, the section/macro list, New / New Section / Delete.
/// Right column: name + icon, section, the editor, the linter strip, the reference shelf
/// (templates or the Core's command tree), Run and Exit.
/// </summary>
public static class MacroBookUiLaw
{
    public readonly record struct Rect(float X, float Y, float Width, float Height)
    {
        public Vector2 Min => new(X, Y);
        public Vector2 LogicalSize => new(Width, Height);
        public Vector2 Minimum(Vector2 origin, float scale) =>
            origin + new Vector2(X, Y) * scale;
        public Vector2 Size(float scale) => new Vector2(Width, Height) * scale;
        public bool Contains(Vector2 origin, float scale, Vector2 point)
        {
            Vector2 min = Minimum(origin, scale);
            Vector2 max = min + Size(scale);
            return point.X >= min.X && point.Y >= min.Y && point.X < max.X && point.Y < max.Y;
        }
    }

    public const float FrameWidth = 640f;
    public const float FrameHeight = 512f;
    public static Vector2 FrameSize => new(FrameWidth, FrameHeight);
    public const string Title = "Macro Book";
    public const string TitleFont = "GameFontNormal";
    /// <summary>UI-DialogBox-Header is 256x64 hung 12 px above the frame; the caption sits 14 px
    /// below the plaque's top (GameMenuFrame.xml's numbers).</summary>
    public static Rect HeaderPlaque => new(192, -12, 256, 64);
    public static Vector2 TitleCenter => new(320, 8);
    public const string HeaderArt = @"Interface\DialogFrame\UI-DialogBox-Header";
    public static Rect CloseButton => new(604, 4, 32, 32);

    // Two books, HelpFrame inset tabs (TabButtonTemplate, 16 px caps, padding -15).
    public static Rect GeneralTab => new(24, 34, 0, 32);
    public const string GeneralTabText = "General Macros";
    public const string CharacterTabFormat = "{0} Specific Macros";
    public const string TabFont = "GameFontNormalSmall";
    public const float TabSideWidth = 16f;
    public const float TabPadding = -15f;
    public const float CharacterTabMaxWidth = 170f;
    public static float TabWidth(float measuredTextWidth) =>
        PanelTabLaw.Resize(measuredTextWidth, TabSideWidth, padding: TabPadding).TabWidth;
    public static float CharacterTabWidth(float measuredTextWidth) =>
        PanelTabLaw.Resize(measuredTextWidth, TabSideWidth, padding: TabPadding,
            maxWidth: CharacterTabMaxWidth).TabWidth;
    public static Vector2 CharacterTabOffset(float generalTabWidth) => new(generalTabWidth, 0);

    // ── left column ──────────────────────────────────────────────────────────────────────
    public static Rect SearchBox => new(20, 76, 224, 20);
    public const string SearchHint = "Search macros";
    public static Rect List => new(20, 100, 206, 340);
    public const float ListRowHeight = 20f;
    public static int VisibleListRows => (int)(List.Height / ListRowHeight);
    public static Rect ListScrollBar => new(228, 100, 16, 340);
    public static Rect ListRow(int visibleIndex) =>
        new(List.X, List.Y + visibleIndex * ListRowHeight, List.Width, ListRowHeight);
    public const float SectionToggleSize = 16f;
    public static Vector2 SectionToggleOffset => new(2, 2);
    public static Vector2 SectionLabelOffset => new(22, 0);
    public const string SectionFont = "GameFontNormal";
    public const string SectionCountFont = "GameFontDisableSmall";
    public static Vector2 MacroIconOffset(bool indented) => new(indented ? 22 : 4, 2);
    public const float MacroRowIconSize = 16f;
    public static Vector2 MacroLabelOffset(bool indented) => new(indented ? 42 : 24, 0);
    public const string MacroFont = "GameFontHighlightSmall";
    public const string MacroSelectedFont = "GameFontNormalSmall";
    public const string PlusPath = @"Interface\Buttons\UI-PlusButton-Up";
    public const string MinusPath = @"Interface\Buttons\UI-MinusButton-Up";
    public const string ToggleHighlightPath = @"Interface\Buttons\UI-PlusButton-Hilight";
    public const string RowHighlightPath = @"Interface\Buttons\UI-Listbox-Highlight2";
    public static Rect NewButton => new(20, 448, 70, 22);
    public static Rect NewSectionButton => new(94, 448, 76, 22);
    public static Rect DeleteButton => new(174, 448, 70, 22);
    public const string NewText = "New";
    public const string NewSectionText = "Section";
    public const string DeleteText = "Delete";
    public static Rect Divider => new(248, 76, 1, 394);
    public const uint DividerColor = 0x40ffffffu;

    // ── right column: identity ───────────────────────────────────────────────────────────
    public static Vector2 NameLabel => new(256, 80);
    public const string NameLabelText = "Name:";
    public const string LabelFont = "GameFontNormalSmall";
    public static Rect NameField => new(300, 76, 200, 20);
    public static Rect IconButton => new(588, 72, 36, 36);
    /// <summary>MacroFrameButtonTemplate's 64x64 UI-EmptySlot-Disabled socket under a 36x36
    /// button: CENTER (0,-1), one pixel down.</summary>
    public static Rect IconSocket => new(IconButton.X - 14, IconButton.Y - 13, 64, 64);
    public static Vector2 IconOffset => new(0, 1);
    public static Vector2 SectionLabel => new(256, 106);
    public const string SectionLabelText = "Section:";
    public static Rect SectionButton => new(300, 102, 200, 22);
    public const string NoSectionText = "(no section)";
    public const string NewSectionMenuText = "New section...";
    public static Rect SectionMenu(int rowCount) =>
        new(300, 126, 200, rowCount * SectionMenuRowHeight + 6);
    public const float SectionMenuRowHeight = 18f;
    public const int SectionMenuMaxRows = 10;
    public static Rect SectionMenuRow(int index) =>
        new(303, 129 + index * SectionMenuRowHeight, 194, SectionMenuRowHeight);

    // ── right column: editor ─────────────────────────────────────────────────────────────
    public static Vector2 BodyLabel => new(256, 132);
    public const string BodyLabelText = "Enter Macro Commands:";
    public static Rect BodyBackground => new(256, 146, 368, 156);
    public static Rect BodyEditor => new(263, 151, 338, 146);
    public static Rect BodyScrollBar => new(605, 151, 16, 146);
    public static Vector2 CounterRight => new(620, 305);
    public const string CounterFont = "GameFontHighlightSmall";
    public const float BodyLineHeight = 14f;
    public const int BodyCharactersPerLine = 52;
    public const float BodyScrollStep = 28f;

    /// <summary>Explicit lines plus a conservative wrap allowance, never shorter than the
    /// viewport - the ImGui input is sized to this and the child clips it.</summary>
    public static float BodyContentHeight(string text)
    {
        int explicitLines = 1;
        foreach (char c in text)
            if (c == '\n') explicitLines++;
        int wrapAllowance = (text.Length + BodyCharactersPerLine - 1) / BodyCharactersPerLine;
        return MathF.Max(BodyEditor.Height, (explicitLines + wrapAllowance) * BodyLineHeight);
    }

    public static float MaximumBodyScroll(string text) =>
        MathF.Max(0, BodyContentHeight(text) - BodyEditor.Height);

    public static float ClampBodyScroll(float requested, string text) =>
        Math.Clamp(requested, 0, MaximumBodyScroll(text));

    public static float WheelBodyScroll(float current, string text, float wheel) =>
        ClampBodyScroll(current - wheel * BodyScrollStep, text);

    /// <summary>The body scroll bar is a UIPanelScrollBar over integer "steps".</summary>
    public static int BodyScrollSteps(string text) =>
        (int)MathF.Ceiling(MaximumBodyScroll(text) / BodyScrollStep);

    public static int BodyScrollStepOf(float scroll, string text) =>
        (int)MathF.Round(ClampBodyScroll(scroll, text) / BodyScrollStep);

    public static float BodyScrollFromStep(int step, string text) =>
        ClampBodyScroll(step * BodyScrollStep, text);

    /// <summary>When the editor is hidden (a section is selected), what the space says.</summary>
    public static Vector2 SectionSummaryCenter => new(440, 224);
    public const string SectionSummaryFont = "GameFontNormal";
    public static string SectionSummary(string section, int count) => count == 1
        ? $"{section}: 1 macro"
        : $"{section}: {count} macros";
    public const string SectionSummaryHint = "Select a macro to edit it, or press New.";
    public const string SectionSummaryHintFont = "GameFontHighlightSmall";
    public static Vector2 SectionSummaryHintCenter => new(440, 246);

    // ── right column: linter strip ───────────────────────────────────────────────────────
    public static Rect Diagnostics => new(256, 308, 368, 42);
    public const float DiagnosticPitch = 14f;
    public const int DiagnosticRows = 3;
    public const string DiagnosticFont = "GameFontHighlightSmall";
    public const uint ErrorColor = 0xff2020ffu;
    public const uint WarningColor = 0xff00d1ffu;
    public const uint InfoColor = 0xffb0b0b0u;
    public const string CleanText = "No problems found.";
    public const uint CleanColor = 0xff20d020u;
    public static uint DiagnosticColor(MacroLintLaw.Severity severity) => severity switch
    {
        MacroLintLaw.Severity.Error => ErrorColor,
        MacroLintLaw.Severity.Warning => WarningColor,
        _ => InfoColor,
    };
    public static string DiagnosticText(MacroLintLaw.Diagnostic diagnostic) =>
        $"Line {diagnostic.Line}: {diagnostic.Message}";
    public static string OverflowText(int hidden) => $"... and {hidden} more";

    // ── right column: reference shelf ────────────────────────────────────────────────────
    public static Rect TemplatesTab => new(256, 354, 84, 20);
    public static Rect CommandsTab => new(344, 354, 84, 20);
    public const string TemplatesText = "Templates";
    public const string CommandsText = "Commands";
    public static Rect ShelfFilter => new(432, 354, 192, 20);
    public const string ShelfFilterHint = "Filter";
    public static Rect Shelf => new(256, 378, 352, 90);
    public const float ShelfRowHeight = 18f;
    public static int VisibleShelfRows => (int)(Shelf.Height / ShelfRowHeight);
    public static Rect ShelfScrollBar => new(608, 378, 16, 90);
    public static Rect ShelfRow(int visibleIndex) =>
        new(Shelf.X, Shelf.Y + visibleIndex * ShelfRowHeight, Shelf.Width, ShelfRowHeight);
    public const string ShelfFont = "GameFontHighlightSmall";
    public const string ShelfHintFont = "GameFontDisableSmall";
    public const float ShelfHintRightInset = 6f;
    public const int ShelfSearchLimit = 400;
    public const string ShelfEmptyText = "Nothing matches.";

    // ── bottom row ───────────────────────────────────────────────────────────────────────
    public static Rect RunButton => new(256, 478, 110, 22);
    public const string RunText = "Run Macro";
    public static Rect ExitButton => new(514, 478, 110, 22);
    public const string ExitText = "Exit";
    public static Vector2 StatusLeft => new(376, 483);
    public const string StatusFont = "GameFontDisableSmall";
    public static string CountStatus(int count, int capacity) => $"{count} / {capacity} macros";

    // ── icon picker (replaces the editor + shelf while open) ─────────────────────────────
    public static Rect IconFilter => new(256, 132, 368, 20);
    public const string IconFilterHint = "Filter icons";
    public const int IconColumns = 7;
    public const int IconRows = 6;
    public const int VisibleIcons = IconColumns * IconRows;
    public const float IconSize = 36f;
    public const float IconColumnPitch = 46f;
    public const float IconRowPitch = 44f;
    public static Rect IconCell(int visibleIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(visibleIndex);
        if (visibleIndex >= VisibleIcons)
            throw new ArgumentOutOfRangeException(nameof(visibleIndex));
        int column = visibleIndex % IconColumns;
        int row = visibleIndex / IconColumns;
        return new Rect(270f + column * IconColumnPitch, 160f + row * IconRowPitch,
            IconSize, IconSize);
    }
    public static Rect IconCellSocket(Rect cell) => new(cell.X - 14, cell.Y - 13, 64, 64);
    public static Rect IconScrollBar => new(600, 160, 16, 256);
    public static Rect IconOkayButton => new(256, 432, 80, 22);
    public static Rect IconCancelButton => new(344, 432, 80, 22);
    public const string OkayText = "Okay";
    public const string CancelText = "Cancel";
    public static int MaximumIconRowOffset(int iconCount) =>
        Math.Max(0, (Math.Max(0, iconCount) + IconColumns - 1) / IconColumns - IconRows);
    public static int ClampIconRowOffset(int requested, int iconCount) =>
        Math.Clamp(requested, 0, MaximumIconRowOffset(iconCount));
    public static int IconCatalogIndex(int rowOffset, int visibleIndex, int iconCount)
    {
        _ = IconCell(visibleIndex);
        int index = Math.Max(0, rowOffset) * IconColumns + visibleIndex;
        return index < Math.Max(0, iconCount) ? index : -1;
    }
    /// <summary>Icon filtering matches the file stem, case-insensitively.</summary>
    public static IReadOnlyList<string> FilterIcons(IReadOnlyList<string> icons, string filter)
    {
        filter = filter.Trim();
        if (filter.Length == 0) return icons;
        return icons.Where(path => Path.GetFileName(path)
            .Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    // ── drag and drop ────────────────────────────────────────────────────────────────────
    public const float DropLineThickness = 2f;
    public const uint DropLineColor = 0xff00d1ffu;
    /// <summary>The lower half of a row means "after it".</summary>
    public static bool DropAfter(float rowTopY, float mouseY, float scale) =>
        mouseY >= rowTopY + ListRowHeight * scale * .5f;
    public const string SectionDragFont = "GameFontNormal";
    public static readonly Vector2 SectionDragPreviewOffset = new(12, 8);

    // ── the hotbar name label ────────────────────────────────────────────────────────────
    /// <summary>ActionButtonTemplate's $parentName: a 36x10 GameFontHighlightSmallOutline box at
    /// BOTTOM (0,2). 1.12 clips the name to the box; so do we (no ellipsis), after a hard cap so
    /// a long name never costs a measure per character (owner 2026-09-05: "the first X
    /// characters of the macro name").</summary>
    public const string HotbarNameFont = "GameFontHighlightSmallOutline";
    public const float HotbarNameWidth = 36f;
    public const float HotbarNameHeight = 10f;
    public const float HotbarNameBottomInset = 2f;
    public const float ActionButtonSize = 36f;
    public const int HotbarNameMaxCharacters = 8;
    public static float HotbarNameBoxTop(float buttonTop, float scale) =>
        buttonTop + (ActionButtonSize - HotbarNameBottomInset - HotbarNameHeight) * scale;
    public static string HotbarLabel(string name, Func<string, float> logicalWidth)
    {
        string label = name.Trim();
        if (label.Length > HotbarNameMaxCharacters) label = label[..HotbarNameMaxCharacters];
        while (label.Length > 0 && logicalWidth(label) > HotbarNameWidth) label = label[..^1];
        return label.TrimEnd();
    }

    // shared art
    public const string SocketPath = @"Interface\Buttons\UI-EmptySlot-Disabled";
    public const string SelectedSocketPath = @"Interface\Buttons\UI-EmptySlot";
    public const string HoverSquarePath = @"Interface\Buttons\ButtonHilight-Square";
    public const string CheckedSquarePath = @"Interface\Buttons\CheckButtonHilight";
    public static readonly Vector2 DragPreviewOffset = new(10);
    public static readonly Vector2 DragPreviewSize = new(32);
    public const float DragThreshold = 6f;
    public const string OpenSound = "igCharacterInfoOpen";
    public const string CloseSound = "igCharacterInfoClose";
    public const string ClickSound = "igMainMenuOptionCheckBoxOn";
    public const string ScrollSound = "UChatScrollButton";
    public const string RunSound = "igMainMenuOption";

    /// <summary>Drag begins once the press has travelled the threshold (squared, scaled).</summary>
    public static bool DragStarted(Vector2 pressPosition, Vector2 mouse, float scale) =>
        Vector2.DistanceSquared(pressPosition, mouse) > DragThreshold * DragThreshold * scale * scale;
}

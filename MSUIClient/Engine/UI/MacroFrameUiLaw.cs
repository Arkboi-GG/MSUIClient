using System.Numerics;

namespace MSUIClient.Engine.UI;

public enum MacroPopupMode
{
    New,
    Edit,
}

/// <summary>
/// Frozen MacroFrame / MacroPopupFrame geometry and state rules. Re-measured 2026-09-03 against
/// the 1.12.1 client's OWN layout - Interface\AddOns\Blizzard_MacroUI\Blizzard_MacroUI.xml in
/// patch.MPQ (the FrameXML\MacroFrame.xml copy is the older 1.12.0 one and is shadowed by a
/// zero-byte entry) plus the templates it inherits: MacroFrameButtonTemplate (36x36 button, a
/// 64x64 UI-EmptySlot-Disabled socket and a 36x36 icon both CENTER (0,-1), the name a 36x10
/// GameFontHighlightSmallOutline box at BOTTOM (0,2)); TabButtonTemplate (HelpFrameTab art,
/// 16 px caps, text BOTTOM (0,5), the selected art 3 px lower, resized with padding -15 and the
/// character tab capped at 150); ClassTrainerListScrollFrameTemplate (a UIPanel scroll bar with
/// the UI-ClassTrainer-ScrollBar track art hung off its two buttons).
/// </summary>
public static class MacroFrameUiLaw
{
    public const string StoredIconPrefix = @"Interface\Icons\";
    public const float FrameWidth = 384f;
    public const float FrameHeight = 512f;
    public const float FrameTop = 104f;
    public const int MacrosPerSet = 18;
    public const int MacroSetCount = 2;
    public const int TotalMacros = MacrosPerSet * MacroSetCount;
    public const float PopupWidth = 297f;
    public const float PopupHeight = 298f;
    public const int IconsPerRow = 5;
    public const int VisibleRows = 4;
    public const int VisibleIcons = IconsPerRow * VisibleRows;
    public const float IconSize = 36f;
    public const float IconColumnPitch = 46f;
    public const float IconRowPitch = 44f;
    public const int NameCapacity = 16;
    public const float BodyLineHeight = 14f;
    public const int BodyCharactersPerLine = 48;
    public const float BodyScrollStep = 20f;
    public const string OpenSound = "igCharacterInfoOpen";
    public const string CloseSound = "igCharacterInfoClose";
    public const string AcceptSound = "gsTitleOptionOK";

    public readonly record struct Rect(float X, float Y, float Width, float Height)
    {
        public Vector2 Min => new(X, Y);
        public Vector2 LogicalSize => new(Width, Height);
        public Vector2 Minimum(Vector2 origin, float scale) =>
            origin + new Vector2(X, Y) * scale;
        public Vector2 Size(float scale) => new Vector2(Width, Height) * scale;
    }

    public readonly record struct ArtPiece(string Path, Rect Rect);
    public readonly record struct TextureSlice(Rect Rect, Vector2 UvMin, Vector2 UvMax);

    public readonly record struct StoredMacro(string Name, string Body, string IconPath);

    /// <summary>TOPLEFT on MacroFrame TOPRIGHT with the authored (-40,-40) offset.</summary>
    public static Vector2 PopupMinimum(Vector2 macroMinimum, float scale) =>
        macroMinimum + new Vector2(384f - 40f, 40f) * scale;

    public static Vector2 FrameSize => new(FrameWidth, FrameHeight);
    public static Vector2 PopupSize => new(PopupWidth, PopupHeight);
    public static IReadOnlyList<ArtPiece> FrameArt =>
    [
        new(@"Interface\PaperDollInfoFrame\UI-Character-General-TopLeft",
            new(0, 0, 256, 256)),
        new(@"Interface\PaperDollInfoFrame\UI-Character-General-TopRight",
            new(256, 0, 128, 256)),
        new(@"Interface\MacroFrame\MacroFrame-BotLeft", new(0, 256, 256, 256)),
        new(@"Interface\MacroFrame\MacroFrame-BotRight", new(256, 256, 128, 256)),
    ];
    public static IReadOnlyList<ArtPiece> PopupArt =>
    [
        new(@"Interface\MacroFrame\MacroPopup-TopLeft", new(0, 0, 256, 256)),
        new(@"Interface\MacroFrame\MacroPopup-TopRight", new(256, 0, 64, 256)),
        new(@"Interface\MacroFrame\MacroPopup-BotLeft", new(0, 256, 256, 64)),
        new(@"Interface\MacroFrame\MacroPopup-BotRight", new(256, 256, 64, 64)),
    ];

    public static Rect NameEdit => new(29f, 35f, 200f, 20f);
    public static Rect CancelButton => new(208f, 263f, 78f, 22f);
    public static Rect OkayButton => new(128f, 263f, 78f, 22f);
    public static Rect ScrollUp => new(264f, 67f, 16f, 16f);
    public static Rect ScrollDown => new(264f, 246f, 16f, 16f);
    public static Rect ScrollTrack => new(264f, 83f, 16f, 163f);
    public static Rect Portrait => new(7, 6, 60, 60);
    public const string TitleFont = "GameFontNormal";
    public static Vector2 TitleCenter => new(192, 23);
    public static Rect GeneralTab => new(65, 39, 0, 32);
    public static Rect DividerLeft => new(15, 220, 256, 16);
    public static Rect DividerRight => new(271, 220, 75, 16);
    public static Rect SelectedBackground => new(16, 228, 64, 64);
    public static Rect SelectedButton => new(30, 242, 36, 36);
    public static Rect SelectedName => new(76, 243, 256, 16);
    public static Rect EnterMacroLabel => new(24, 292, 180, 12);
    public static Rect BodyBackground => new(18, 305, 322, 95);
    public static Rect BodyEditor => new(27, 310, 286, 85);
    public static Rect BodyScrollUp => new(319, 310, 16, 16);
    public static Rect BodyScrollTrack => new(319, 326, 16, 53);
    public static Rect BodyScrollDown => new(319, 379, 16, 16);
    public static Rect ChangeButton => new(67, 258, 170, 22);
    public static Rect DeleteButton => new(17, 411, 80, 22);
    public static Rect NewButton => new(182, 411, 80, 22);
    public static Rect ExitButton => new(263, 411, 80, 22);
    public static Rect CloseButton => new(323, 8, 32, 32);
    /// <summary>MacroFrameCharLimitText: BOTTOM (-15,105) of the frame, GameFontHighlightSmall
    /// (10 px), so its centre is 5 px above y 407.</summary>
    public static Vector2 CharacterLimitCenter => new(177, 402);
    public static Vector2 CharacterTabOffset(float generalTabWidth) =>
        new(generalTabWidth, 0);
    /// <summary>The 64x64 UI-EmptySlot-Disabled socket under a 36x36 button: CENTER (0,-1),
    /// i.e. one pixel DOWN (FrameXML +y is up).</summary>
    public static Rect MacroSocket => new(-14, -13, 64, 64);
    /// <summary>$parentIcon is CENTER (0,-1) too: the icon sits one pixel below the button.</summary>
    public static Vector2 IconOffset => new(0, 1);
    /// <summary>$parentName: a 36x10 box at BOTTOM (0,2) - bottom edge 2 px up from the button's,
    /// centre at y 29, and never wider than the button.</summary>
    public static Vector2 MacroNameCenter => new(18, 29);
    public const float MacroNameWidth = 36f;
    public const float MacroNameHeight = 10f;
    public static readonly Vector2 DividerLeftUvMin = Vector2.Zero;
    public static readonly Vector2 DividerLeftUvMax = new(1, .25f);
    public static readonly Vector2 DividerRightUvMin = new(0, .25f);
    public static readonly Vector2 DividerRightUvMax = new(.29296875f, .5f);
    public static Vector2 BodyInputSize(float contentHeight) =>
        new(BodyEditor.Width, MathF.Max(0, contentHeight));
    public static readonly Vector2 DragPreviewOffset = new(10);
    public static readonly Vector2 DragPreviewSize = new(32);
    public static readonly Vector2 ScrollUvMin = new(.25f);
    public static readonly Vector2 ScrollUvMax = new(.75f);
    public static Rect BodyScrollKnob(float scroll, string text) =>
        new(BodyScrollTrack.X, BodyThumbY(scroll, text), 16, 16);
    public static readonly Vector2 PopupNameLabel = new(24, 21);
    public static readonly Vector2 PopupIconLabel = new(24, 69);
    /// <summary>GlobalStrings MACRO_POPUP_TEXT / MACRO_POPUP_CHOOSE_ICON, verbatim.</summary>
    public const string PopupNameText = "Enter Macro Name (Max 16 Characters):";
    public const string PopupIconText = "Choose an Icon:";
    /// <summary>ClassTrainerListScrollFrameTemplate's track art (UI-ClassTrainer-ScrollBar,
    /// 64x128): a 30x120 piece TOP-anchored to the up button's TOP at (-2,+2) and a 30x123
    /// piece BOTTOM-anchored to the down button's BOTTOM at (-2,-1). Drawn under the buttons.</summary>
    public static Rect PopupScrollTrackTop => new(255, 65, 30, 120);
    public static Rect PopupScrollTrackBottom => new(255, 140, 30, 123);
    public static readonly Vector2 PopupScrollTrackTopUvMin = new(0f, .0234375f);
    public static readonly Vector2 PopupScrollTrackTopUvMax = new(.46875f, .9609375f);
    public static readonly Vector2 PopupScrollTrackBottomUvMin = new(.53125f, .03125f);
    public static readonly Vector2 PopupScrollTrackBottomUvMax = new(1f, 1f);
    public static Rect NameInput => new(NameEdit.X + 3, NameEdit.Y,
        NameEdit.Width - 6, NameEdit.Height);
    public static IReadOnlyList<TextureSlice> NameBorderSlices =>
    [
        new(new(18, 35, 12, 29), Vector2.Zero, new(.09375f, 1)),
        new(new(30, 35, 175, 29), new(.09375f, 0), new(.90625f, 1)),
        new(new(205, 35, 12, 29), new(.90625f, 0), Vector2.One),
    ];
    public static Rect PopupIconSocket(Rect icon) =>
        new(icon.X + MacroSocket.X, icon.Y + MacroSocket.Y,
            MacroSocket.Width, MacroSocket.Height);
    /// <summary>UIPanelScrollBarTemplate's thumb is 16x16 (UI-ScrollBar-Knob, centre half of a
    /// 32x32 sheet - see ScrollUvMin/Max); it travels the bar between the two buttons.</summary>
    public static Rect PopupScrollKnob(int rowOffset, int maximum)
    {
        float fraction = maximum <= 0 ? 0 : Math.Clamp((float)rowOffset / maximum, 0, 1);
        return new(ScrollTrack.X,
            ScrollTrack.Y + fraction * (ScrollTrack.Height - 16), 16, 16);
    }

    // TabButtonTemplate: 16 px caps (HelpFrameTab-Inactive / -Active), text BOTTOM (0,5).
    // MacroFrameTab1 OnLoad: PanelTemplates_TabResize(-15); MacroFrameTab2 OnLoad:
    // SetText(format(CHARACTER_SPECIFIC_MACROS, name)); PanelTemplates_TabResize(-15, nil, nil, 150).
    public const float TabSideWidth = 16f;
    public const float TabPadding = -15f;
    public const float CharacterTabMaxWidth = 150f;
    public const string GeneralTabText = "General Macros";
    public const string CharacterTabFormat = "{0} Specific Macros";
    public const string TabFont = "GameFontNormalSmall";

    public static float GeneralTabWidth(float measuredTextWidth) =>
        PanelTabLaw.Resize(measuredTextWidth, TabSideWidth, padding: TabPadding).TabWidth;

    public static float CharacterTabWidth(float measuredTextWidth, float generalTabWidth) =>
        PanelTabLaw.Resize(measuredTextWidth, TabSideWidth, padding: TabPadding,
            maxWidth: CharacterTabMaxWidth).TabWidth;

    public static Rect MacroButton(int visibleIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(visibleIndex);
        if (visibleIndex >= MacrosPerSet)
            throw new ArgumentOutOfRangeException(nameof(visibleIndex));
        int column = visibleIndex % 6;
        int row = visibleIndex / 6;
        return new Rect(42f + column * 49f, 83f + row * 46f, 36f, 36f);
    }

    public static int SetBase(bool characterSpecific) =>
        characterSpecific ? MacrosPerSet : 0;

    public static int AbsoluteIndex(bool characterSpecific, int visibleIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(visibleIndex);
        if (visibleIndex >= MacrosPerSet)
            throw new ArgumentOutOfRangeException(nameof(visibleIndex));
        return SetBase(characterSpecific) + visibleIndex;
    }

    public static bool InSet(bool characterSpecific, int absoluteIndex) =>
        absoluteIndex >= SetBase(characterSpecific) &&
        absoluteIndex < SetBase(characterSpecific) + MacrosPerSet;

    /// <summary>
    /// Mirrors BenillaMacroFrameText_Resize: explicit lines plus a deliberately conservative
    /// 48-character wrap allowance, never shorter than the authored 286x85 scroll viewport.
    /// </summary>
    public static float BodyContentHeight(string text)
    {
        int explicitLines = 1;
        foreach (char c in text)
            if (c == '\n') explicitLines++;
        int wrapAllowance = (text.Length + BodyCharactersPerLine - 1) /
            BodyCharactersPerLine;
        return MathF.Max(BodyEditor.Height,
            (explicitLines + wrapAllowance) * BodyLineHeight);
    }

    public static float MaximumBodyScroll(string text) =>
        MathF.Max(0, BodyContentHeight(text) - BodyEditor.Height);

    public static float ClampBodyScroll(float requested, string text) =>
        Math.Clamp(requested, 0, MaximumBodyScroll(text));

    public static float WheelBodyScroll(float current, string text, float wheel) =>
        ClampBodyScroll(current - wheel * BodyScrollStep, text);

    public static float BodyThumbY(float scroll, string text)
    {
        float maximum = MaximumBodyScroll(text);
        float travel = BodyScrollTrack.Height - 16f;
        return BodyScrollTrack.Y + (maximum <= 0 ? 0 :
            ClampBodyScroll(scroll, text) / maximum * travel);
    }

    public static Rect IconButton(int visibleIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(visibleIndex);
        if (visibleIndex >= VisibleIcons)
            throw new ArgumentOutOfRangeException(nameof(visibleIndex));
        int column = visibleIndex % IconsPerRow;
        int row = visibleIndex / IconsPerRow;
        return new Rect(24f + column * IconColumnPitch, 85f + row * IconRowPitch,
            IconSize, IconSize);
    }

    public static int MaximumRowOffset(int iconCount) =>
        Math.Max(0, (Math.Max(0, iconCount) + IconsPerRow - 1) / IconsPerRow - VisibleRows);

    public static int ClampRowOffset(int requested, int iconCount) =>
        Math.Clamp(requested, 0, MaximumRowOffset(iconCount));

    public static int CatalogIndex(int rowOffset, int visibleIndex, int iconCount)
    {
        _ = IconButton(visibleIndex);
        int index = Math.Max(0, rowOffset) * IconsPerRow + visibleIndex;
        return index < Math.Max(0, iconCount) ? index : -1;
    }

    public static bool OkayEnabled(MacroPopupMode mode, string name,
        int selectedIcon, bool existingIcon)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        return selectedIcon >= 0 || mode == MacroPopupMode.Edit && existingIcon;
    }

    /// <summary>
    /// The reference tokenizes on either CR or LF and executes each non-empty line through the
    /// same ChatFrame route as typed input. Trimming is the current Benilla-friendly divergence.
    /// </summary>
    public static IReadOnlyList<string> RunnableLines(string body) => body
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.Trim())
        .Where(line => line.Length > 0)
        .ToArray();

    /// <summary>Current Benilla's vanilla-compatible MACRO/END text reader.</summary>
    public static IReadOnlyList<StoredMacro> ParseStore(string text)
    {
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n').Split('\n');
        int lineCount = lines.Length;
        if (lineCount > 0 && lines[^1].Length == 0) lineCount--;
        var records = new List<(uint Index, StoredMacro Macro)>();
        int cursor = 0;
        while (cursor < lineCount)
        {
            string line = lines[cursor++];
            string trimmed = line.TrimStart();
            if (!trimmed.StartsWith("MACRO ", StringComparison.Ordinal)) continue;
            string header = trimmed[6..];
            int firstQuote = header.IndexOf('"');
            int lastQuote = header.LastIndexOf('"');
            if (firstQuote < 0 || lastQuote <= firstQuote ||
                !uint.TryParse(header[..firstQuote].Trim(), out uint index))
                continue;
            string name = header[(firstQuote + 1)..lastQuote];
            string iconToken = header[(lastQuote + 1)..].Trim();
            var body = new List<string>();
            while (cursor < lineCount)
            {
                string bodyLine = lines[cursor];
                if (bodyLine.Trim() == "END")
                {
                    cursor++;
                    break;
                }
                if (bodyLine.TrimStart().StartsWith("MACRO ", StringComparison.Ordinal)) break;
                body.Add(bodyLine);
                cursor++;
            }
            string iconPath = iconToken.Length == 0 ? "" :
                iconToken.Contains('\\') || iconToken.Contains('/') ? iconToken :
                StoredIconPrefix + iconToken;
            records.Add((index, new StoredMacro(name, string.Join('\n', body), iconPath)));
        }
        return records.OrderBy(record => record.Index)
            .Take(MacrosPerSet).Select(record => record.Macro).ToArray();
    }

    /// <summary>Writes one dense 18-entry scope in the reference MACRO/END shape.</summary>
    public static string WriteStore(IEnumerable<StoredMacro> macros)
    {
        var output = new System.Text.StringBuilder();
        int index = 1;
        foreach (StoredMacro macro in macros.Take(MacrosPerSet))
        {
            string icon = macro.IconPath.StartsWith(StoredIconPrefix, StringComparison.Ordinal)
                ? macro.IconPath[StoredIconPrefix.Length..] : macro.IconPath;
            output.Append("MACRO ").Append(index++).Append(" \"").Append(macro.Name)
                .Append("\" ").Append(icon).Append('\n');
            string body = macro.Body.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n');
            if (body.Length > 0)
            {
                output.Append(body);
                if (body[^1] != '\n') output.Append('\n');
            }
            output.Append("END\n");
        }
        return output.ToString();
    }

    /// <summary>Benilla's path-component law: only ASCII letters and digits survive.</summary>
    public static string StoreFileToken(string value)
    {
        string token = new(value.Select(character => char.IsAsciiLetterOrDigit(character)
            ? character : '_').ToArray());
        return token.Length == 0 ? "unknown" : token;
    }
}

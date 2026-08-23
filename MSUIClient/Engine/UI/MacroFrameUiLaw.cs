using System.Numerics;

namespace MSUIClient.Engine.UI;

public enum MacroPopupMode
{
    New,
    Edit,
}

/// <summary>Frozen MacroPopupFrame geometry and state rules from current Benilla MacroFrame.xml.</summary>
public static class MacroFrameUiLaw
{
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
        public Vector2 Minimum(Vector2 origin, float scale) =>
            origin + new Vector2(X, Y) * scale;
        public Vector2 Size(float scale) => new Vector2(Width, Height) * scale;
    }

    /// <summary>TOPLEFT on MacroFrame TOPRIGHT with the authored (-40,-40) offset.</summary>
    public static Vector2 PopupMinimum(Vector2 macroMinimum, float scale) =>
        macroMinimum + new Vector2(384f - 40f, 40f) * scale;

    public static Rect NameEdit => new(29f, 35f, 200f, 20f);
    public static Rect CancelButton => new(208f, 263f, 78f, 22f);
    public static Rect OkayButton => new(128f, 263f, 78f, 22f);
    public static Rect ScrollUp => new(264f, 67f, 16f, 16f);
    public static Rect ScrollDown => new(264f, 246f, 16f, 16f);
    public static Rect ScrollTrack => new(264f, 83f, 16f, 163f);
    public static Rect Portrait => new(7, 6, 60, 60);
    public static Vector2 TitleCenter => new(192, 24);
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
    public static Vector2 CharacterLimitCenter => new(177, 407);

    public static float GeneralTabWidth(float measuredTextWidth) =>
        PanelTabLaw.Resize(measuredTextWidth, 20f, padding: -15f).TabWidth;

    public static float CharacterTabWidth(float measuredTextWidth, float generalTabWidth) =>
        PanelTabLaw.FitWithinParent(measuredTextWidth, 20f,
            GeneralTab.X + generalTabWidth, FrameWidth, rightInset: 40f,
            padding: -15f, maxWidth: 150f).TabWidth;

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
}

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
    public const float PopupWidth = 297f;
    public const float PopupHeight = 298f;
    public const int IconsPerRow = 5;
    public const int VisibleRows = 4;
    public const int VisibleIcons = IconsPerRow * VisibleRows;
    public const float IconSize = 36f;
    public const float IconColumnPitch = 46f;
    public const float IconRowPitch = 44f;
    public const int NameCapacity = 16;
    public const string OpenSound = "igCharacterInfoOpen";
    public const string CloseSound = "igCharacterInfoClose";
    public const string AcceptSound = "gsTitleOptionOK";

    public readonly record struct Rect(float X, float Y, float Width, float Height)
    {
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
}

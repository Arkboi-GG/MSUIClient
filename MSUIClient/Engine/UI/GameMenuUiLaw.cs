using System.Numerics;

namespace MSUIClient.Engine.UI;

public enum GameMenuEscapeLayer
{
    Popup,
    Options,
    GameMenu,
    StackSplit,
    WorldMap,
    OpenMail,
    SpellCast,
    SpellTargeting,
    PlayerPanel,
    Target,
    OpenGameMenu,
}

public enum GameMenuToggleAction { Open, Close }

public readonly record struct GameMenuEscapeState(
    bool HasCarriedCursor,
    bool PopupOpen,
    bool OptionsOpen,
    bool GameMenuOpen,
    bool StackSplitOpen,
    bool WorldMapOpen,
    bool OpenMailOpen,
    bool CancelableSpellCast,
    bool SpellTargeting,
    bool PlayerPanelOpen,
    bool TargetSelected);

public readonly record struct GameMenuEscapePlan(
    bool ClearCarriedCursor, GameMenuEscapeLayer Layer);

/// <summary>
/// Deterministic GameMenuFrame behavior shared by production input and the parity checks.
/// Geometry constants intentionally describe MSUI's already-working build-5875 menu; the frozen
/// Benilla Era ladder is a reference difference, not authority to normalize this surface.
/// </summary>
public static class GameMenuUiLaw
{
    public const float FrameWidth = 195f;
    public const float FrameHeight = 246f;
    public const float ButtonWidth = 144f;
    public const float ButtonHeight = 21f;
    public const float HeaderWidth = 256f;
    public const float HeaderHeight = 64f;
    public const float HeaderTop = -12f;
    public const float HeaderTitleTop = 14f;
    public const float HighlightAlpha = .55f;
    public const float OptionsDefaultWidth = 450f;
    public const float OptionsDefaultHeight = 575f;
    public const float OptionsMinWidth = 450f;
    public const float OptionsMinHeight = 360f;
    public const float OptionsViewportWidth = .96f;
    public const float OptionsViewportHeight = .92f;
    public const string OpenSound = "igMainMenuOpen";
    public const string EscapeCloseSound = "igMainMenuQuit";
    public const string PopupOpenSound = "igMainMenuOpen";
    public const string PopupCloseSound = "igMainMenuClose";

    private static readonly float[] RowTops =
        [26.5f, 48.5f, 70.5f, 92.5f, 114.5f, 136.5f, 158.5f, 195.5f];

    public static float ButtonTop(int row) => RowTops[Math.Clamp(row, 0, RowTops.Length - 1)];

    /// <summary>
    /// Resolves a remembered logical option-window size into safe framebuffer pixels.
    /// The minimum never exceeds the current viewport, which keeps a saved desktop-sized
    /// window usable after moving to a smaller display.
    /// </summary>
    public static Vector2 ResolveOptionsSize(Vector2 logicalSize, float scale, Vector2 display)
    {
        scale = float.IsFinite(scale) ? MathF.Max(scale, .001f) : 1f;
        var maximum = new Vector2(
            MathF.Max(1f, display.X * OptionsViewportWidth),
            MathF.Max(1f, display.Y * OptionsViewportHeight));
        var minimum = Vector2.Min(
            new Vector2(OptionsMinWidth, OptionsMinHeight) * scale,
            maximum);

        float width = float.IsFinite(logicalSize.X) && logicalSize.X > 0f
            ? logicalSize.X : OptionsDefaultWidth;
        float height = float.IsFinite(logicalSize.Y) && logicalSize.Y > 0f
            ? logicalSize.Y : OptionsDefaultHeight;
        return Vector2.Clamp(new Vector2(width, height) * scale, minimum, maximum);
    }

    /// <summary>Converts a live pixel size back to scale-independent persisted units.</summary>
    public static Vector2 ToLogicalOptionsSize(Vector2 physicalSize, float scale)
    {
        scale = float.IsFinite(scale) ? MathF.Max(scale, .001f) : 1f;
        return physicalSize / scale;
    }

    /// <summary>
    /// A live UI-scale or host-viewport change must re-resolve and re-center the
    /// physical window from its remembered logical size. Sub-pixel noise does not.
    /// </summary>
    public static bool OptionsEnvironmentChanged(
        Vector2 previousDisplay, Vector2 display, float previousScale, float scale)
    {
        if (!float.IsFinite(previousDisplay.X) || !float.IsFinite(previousDisplay.Y) ||
            !float.IsFinite(display.X) || !float.IsFinite(display.Y) ||
            !float.IsFinite(previousScale) || !float.IsFinite(scale))
            return true;

        return Vector2.DistanceSquared(previousDisplay, display) >= .25f ||
               MathF.Abs(previousScale - scale) >= .0001f;
    }

    /// <summary>
    /// Cursor payload is cleared before routing but deliberately does not eat Escape. The returned
    /// layer is the sole consumer, which prevents one press from closing two UI layers.
    /// </summary>
    public static GameMenuEscapePlan ResolveEscape(in GameMenuEscapeState state)
    {
        GameMenuEscapeLayer layer = state.PopupOpen ? GameMenuEscapeLayer.Popup
            : state.OptionsOpen ? GameMenuEscapeLayer.Options
            : state.GameMenuOpen ? GameMenuEscapeLayer.GameMenu
            : state.StackSplitOpen ? GameMenuEscapeLayer.StackSplit
            : state.WorldMapOpen ? GameMenuEscapeLayer.WorldMap
            : state.OpenMailOpen ? GameMenuEscapeLayer.OpenMail
            : state.CancelableSpellCast ? GameMenuEscapeLayer.SpellCast
            : state.SpellTargeting ? GameMenuEscapeLayer.SpellTargeting
            : state.PlayerPanelOpen ? GameMenuEscapeLayer.PlayerPanel
            : state.TargetSelected ? GameMenuEscapeLayer.Target
            : GameMenuEscapeLayer.OpenGameMenu;
        return new GameMenuEscapePlan(state.HasCarriedCursor, layer);
    }

    public static GameMenuToggleAction MicroToggle(bool menuOpen) =>
        menuOpen ? GameMenuToggleAction.Close : GameMenuToggleAction.Open;

    public static bool PlayerPanelMayOpen(bool menuOpen) => !menuOpen;

    /// <summary>Exactly one cue per real visibility edge; repeated assignments are silent.</summary>
    public static string PopupVisibilitySound(bool wasVisible, bool willBeVisible) =>
        wasVisible == willBeVisible ? "" : willBeVisible ? PopupOpenSound : PopupCloseSound;
}

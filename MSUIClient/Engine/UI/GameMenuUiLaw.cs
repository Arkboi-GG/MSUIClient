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
    public const float MenuScaleMinimum = .65f;
    public const float MenuScaleMaximum = 2.5f;
    public const float LayoutPopupWidth = 200f;
    public const float LayoutPopupHeight = 205f;
    public const string OpenSound = "igMainMenuOpen";
    public const string EscapeCloseSound = "igMainMenuQuit";
    public const string PopupOpenSound = "igMainMenuOpen";
    public const string PopupCloseSound = "igMainMenuClose";

    private static readonly float[] RowTops =
        [26.5f, 48.5f, 70.5f, 92.5f, 114.5f, 136.5f, 158.5f, 195.5f];

    public static float ButtonTop(int row) => RowTops[Math.Clamp(row, 0, RowTops.Length - 1)];

    public static float ResolveMenuScale(float scale) =>
        float.IsFinite(scale)
            ? Math.Clamp(scale, MenuScaleMinimum, MenuScaleMaximum)
            : 1f;

    public static Vector2 ResolveGameMenuSize(
        Vector2 logicalSize, float scale, Vector2 display) =>
        ResolveWindowSize(logicalSize, new(FrameWidth, FrameHeight),
            true, scale, display);

    /// <summary>
    /// Resolves a remembered logical option-window size into safe framebuffer pixels.
    /// The minimum never exceeds the current viewport, which keeps a saved desktop-sized
    /// window usable after moving to a smaller display.
    /// </summary>
    public static Vector2 ResolveOptionsSize(Vector2 logicalSize, float scale, Vector2 display)
    {
        return ResolveWindowSize(logicalSize,
            new(OptionsDefaultWidth, OptionsDefaultHeight),
            false, scale, display);
    }

    public static (Vector2 Minimum, Vector2 Maximum) WindowSizeLimits(
        bool gameMenu, float scale, Vector2 display)
    {
        scale = ResolveMenuScale(scale);
        var maximum = new Vector2(
            MathF.Max(1f, display.X * OptionsViewportWidth),
            MathF.Max(1f, display.Y * OptionsViewportHeight));
        Vector2 authoredMinimum = gameMenu
            ? new(FrameWidth, FrameHeight)
            : new(OptionsMinWidth, OptionsMinHeight);
        return (Vector2.Min(authoredMinimum * scale, maximum), maximum);
    }

    private static Vector2 ResolveWindowSize(
        Vector2 logicalSize,
        Vector2 authoredDefault,
        bool gameMenu,
        float scale,
        Vector2 display)
    {
        scale = ResolveMenuScale(scale);
        (Vector2 minimum, Vector2 maximum) = WindowSizeLimits(gameMenu, scale, display);

        float width = float.IsFinite(logicalSize.X) && logicalSize.X > 0f
            ? logicalSize.X : authoredDefault.X;
        float height = float.IsFinite(logicalSize.Y) && logicalSize.Y > 0f
            ? logicalSize.Y : authoredDefault.Y;
        return Vector2.Clamp(new Vector2(width, height) * scale, minimum, maximum);
    }

    /// <summary>Converts a live pixel size back to scale-independent persisted units.</summary>
    public static Vector2 ToLogicalOptionsSize(Vector2 physicalSize, float scale)
    {
        scale = float.IsFinite(scale) ? MathF.Max(scale, .001f) : 1f;
        return physicalSize / scale;
    }

    /// <summary>Law-owned top-left seat for both the fixed GameMenu and resizable option pages.</summary>
    public static Vector2 CenteredOrigin(Vector2 display, Vector2 physicalSize) =>
        Vector2.Max(Vector2.Zero, (display - physicalSize) * .5f);

    /// <summary>Top-right header seat for the always-reachable menu-only layout gear.</summary>
    /// <summary>
    /// The frame's nine-sliced border band, in logical units. GameMenuFrame.xml declares
    /// &lt;EdgeSize&gt;32&lt;/EdgeSize&gt; on its Backdrop and WowSkin.Dialog carries the same 32,
    /// drawn at EdgeSize * Scale - so the outer 32 logical units on every side are decorative
    /// border, not usable surface. Anything seated inside that band lands on the corner
    /// ornament.
    /// </summary>
    public const float BackdropEdgeSize = 32f;

    /// <summary>Breathing room between the border band and whatever sits next to it.</summary>
    public const float GearGap = 6f;

    /// <summary>
    /// Seat the layout gear INSIDE the frame's interior, clear of the border art.
    ///
    /// It used to be inset 8 from the top-right corner - a quarter of the 32-unit border band -
    /// so it sat on the corner ornament with roughly half of it lost against the decoration.
    /// Reported 2026-08-26. The inset is now taken from the backdrop's own edge size, so it
    /// stays correct if the frame's border art ever changes.
    /// </summary>
    public static Vector2 LayoutGearMinimum(Vector2 frameMinimum, Vector2 frameSize, float scale)
    {
        scale = ResolveMenuScale(scale);
        float side = LayoutGearSide(scale);
        float inset = (BackdropEdgeSize + GearGap) * scale;
        // Never let a very narrow frame push the gear off its own left edge.
        float x = MathF.Max(inset, frameSize.X - side - inset);
        return frameMinimum + new Vector2(x, inset);
    }

    public static float LayoutGearSide(float scale) =>
        Math.Clamp(14f * ResolveMenuScale(scale), 12f, 24f);

    /// <summary>Law-owned popup seat: right of the menu when possible, left otherwise, clamped.</summary>
    public static Vector2 LayoutPopupOrigin(
        Vector2 frameMinimum, Vector2 frameSize, Vector2 popupSize, Vector2 display)
    {
        Vector2 proposed = new(frameMinimum.X + frameSize.X + 8f, frameMinimum.Y);
        if (proposed.X + popupSize.X > display.X)
            proposed.X = frameMinimum.X - popupSize.X - 8f;
        Vector2 maximum = Vector2.Max(Vector2.Zero, display - popupSize);
        return Vector2.Clamp(proposed, Vector2.Zero, maximum);
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

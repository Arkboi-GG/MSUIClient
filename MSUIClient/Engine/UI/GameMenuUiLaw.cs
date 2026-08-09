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
    public const string OpenSound = "igMainMenuOpen";
    public const string EscapeCloseSound = "igMainMenuQuit";
    public const string PopupOpenSound = "igMainMenuOpen";
    public const string PopupCloseSound = "igMainMenuClose";

    private static readonly float[] RowTops =
        [26.5f, 48.5f, 70.5f, 92.5f, 114.5f, 136.5f, 158.5f, 195.5f];

    public static float ButtonTop(int row) => RowTops[Math.Clamp(row, 0, RowTops.Length - 1)];

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

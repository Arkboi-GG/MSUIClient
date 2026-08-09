namespace MSUIClient.Engine.UI;

/// <summary>
/// The small, testable laws added by the frozen Benilla SkillFrame slice. Existing MSUI
/// character-page presentation remains outside this type; these constants describe only the
/// newly ported controls and the defects whose behavior was independently reproduced.
/// </summary>
public static class SkillFrameUiLaw
{
    public enum ToggleAction { None, OpenSkills, CloseSkills, SwitchToSkills }
    public enum BarPresentation { Barless, Progress, Proficiency }

    public readonly record struct LogicalRect(float X, float Y, float Width, float Height);

    public const int SkillsTab = 3;
    public const int VisibleRows = 12;
    public const float RowPitch = 18f;
    public const float SkillBarWidth = 271f;
    public const float SkillBarHeight = 15f;
    public const float SkillRowHitWidth = 281f;
    public const float SkillRowHitHeight = 32f;
    public const float FauxScrollItemHeight = 15f;
    public const int UnlearnOpcode = 0x0202;
    public const float UnlearnTimeoutSeconds = 60f;

    public const string BindingCommand = "TOGGLECHARACTER1";
    public const string BindingLabel = "Toggle Skill Pane";
    public const string UnlearnQuestionFormat = "Do you want to unlearn {0}?";
    public const string UnlearnButtonText = "Unlearn";
    public const string CancelButtonText = "Cancel";
    public const string UnlearnTooltip = "Unlearn this profession";
    public const string PopupOpenSound = "igMainMenuOpen";
    public const string PopupCloseSound = "igMainMenuClose";
    public const string DirectTabSound = "igCharacterInfoTab";
    public const string ScrollButtonSound = "UChatScrollButton";

    // Frozen SkillFrame.xml geometry. Positive XML Y offsets are converted to top-origin screen
    // coordinates here, hence the collapse-tab art sitting six logical pixels above its frame.
    public static readonly LogicalRect ListRect = new(22, 79, 296, 216);
    // The separate mouse-wheel catcher deliberately overhangs the list by three pixels on each
    // side and vertically: it is not the FauxScrollFrame itself.
    public static readonly LogicalRect WheelCatcherRect = new(20, 76, 290, 222);
    public static readonly LogicalRect CollapseFrameRect = new(70, 49, 54, 32);
    public static readonly LogicalRect CollapseLeftRect = new(70, 43, 8, 32);
    public static readonly LogicalRect CollapseMiddleRect = new(78, 43, 38, 32);
    public static readonly LogicalRect CollapseRightRect = new(116, 43, 8, 32);
    public static readonly LogicalRect CollapseButtonRect = new(78, 45, 40, 22);
    public static readonly LogicalRect CollapseIconRect = new(78, 48, 16, 16);

    // FauxScrollFrame -> UIPanelScrollFrameTemplate anchor closure: the 296x216 scroll frame's
    // 16-wide slider begins six pixels right, inset sixteen at each vertical end. The arrow
    // buttons sit immediately outside the slider; the 16x16 thumb travels within it.
    public static readonly LogicalRect ScrollSliderRect = new(324, 95, 16, 184);
    public static readonly LogicalRect ScrollUpRect = new(324, 79, 16, 16);
    public static readonly LogicalRect ScrollDownRect = new(324, 279, 16, 16);
    public const float ScrollThumbTravel = 168f;
    public const int ScrollArrowRows = 6; // round((184 / 2) / the authored 15px item height)

    public static readonly LogicalRect DividerLeftRect = new(15, 305, 256, 16);
    public static readonly LogicalRect DividerRightRect = new(271, 305, 75, 16);

    // Shared StaticPopup template (showAlert/exclusive are accepted but inert in frozen Benilla).
    public static readonly LogicalRect PopupRect = new(0, 128, 320, 72);
    public static readonly LogicalRect PopupTextRect = new(15, 16, 290, 12);
    public static readonly LogicalRect PopupAcceptRect = new(26, 36, 128, 20);
    public static readonly LogicalRect PopupCancelRect = new(167, 36, 128, 20);

    public static ToggleAction ResolveDirectToggle(bool characterOpen, int characterTab) =>
        !characterOpen ? ToggleAction.OpenSkills :
        characterTab == SkillsTab ? ToggleAction.CloseSkills : ToggleAction.SwitchToSkills;

    /// <summary>
    /// Benilla's Edge dispatch: exact bare chord, one key-down edge, no repeat, and no firing
    /// while a text field, modal capture, or the keybinding-capture capsule owns keyboard input.
    /// The caller still updates its latch when this returns false so releasing modifiers while K
    /// remains held cannot synthesize a second key-down event.
    /// </summary>
    public static bool FiresDirectBinding(bool down, bool wasDown, bool alt, bool control,
        bool shift, bool super, bool captureBlocked, bool inWorld) =>
        down && !wasDown && !alt && !control && !shift && !super && !captureBlocked && inWorld;

    public static BarPresentation BarFor(ushort maximum, bool proficiencyCategory) =>
        maximum == 0 ? BarPresentation.Barless :
        maximum == 1 || proficiencyCategory ? BarPresentation.Proficiency :
        BarPresentation.Progress;

    public static int MaximumScroll(int rowCount) => Math.Max(0, rowCount - VisibleRows);
    public static int ClampScroll(int value, int rowCount) =>
        Math.Clamp(value, 0, MaximumScroll(rowCount));
    public static int WheelScroll(int value, int rowCount, float wheel) =>
        ClampScroll(value - Math.Sign(wheel), rowCount);
    public static int ArrowScroll(int value, int rowCount, bool upward) =>
        ClampScroll(value + (upward ? -ScrollArrowRows : ScrollArrowRows), rowCount);
    public static float ScrollThumbY(int value, int maximum) =>
        ScrollSliderRect.Y + (maximum <= 0 ? 0f :
            Math.Clamp((float)value / maximum, 0f, 1f) * ScrollThumbTravel);

    public static LogicalRect SkillRowHitRect(int visibleIndex) =>
        new(33, 70.5f + visibleIndex * RowPitch, SkillRowHitWidth, SkillRowHitHeight);

    /// <summary>HitRectInsets left=9,right=7,top=-7,bottom=10.</summary>
    public static LogicalRect InsetUnlearnHitRect(in LogicalRect visual) =>
        new(visual.X + 9, visual.Y - 7, visual.Width - 16, visual.Height - 3);
}

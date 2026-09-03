using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
using Silk.NET.Input;

namespace MSUIClient;

/// <summary>
/// HUD frame registry (PLAN_21). Every movable HUD frame costs its draw site ONE call:
/// <see cref="HudFrame"/> resolves authored placement -> user override -> on-screen clamp,
/// records the rect for the Edit Mode overlay (GameLoop.HudLayoutEditor.cs) and returns where
/// to draw. The registry is rebuilt every frame from the draw sites, so a frame that is not
/// drawn this frame does not exist for the editor (action bars in the Command View, everything
/// under the commander map). Edit Mode lifecycle - enter / exit / Escape / the binding gate -
/// lives here too; the overlay itself is the editor file.
/// </summary>
public sealed partial class GameLoop
{
    private bool _hudEditMode;
    private HudEditSession? _hudEdit;
    /// <summary>The overlay window takes focus on the frame Edit Mode is entered, so it is
    /// display-front over every HUD window it edits (none of which may rise over it).</summary>
    private bool _hudEditFocusPending;
    private readonly List<HudFrameRecord> _hudFrames = [];
    private readonly Dictionary<string, int> _hudFrameIndex = new(StringComparer.Ordinal);

    /// <summary>One registered frame, this frame. Placement is the EFFECTIVE one (override or
    /// authored); LogicalOrigin is already clamped. Hidden is the layout's flag for this
    /// context; Hideable is whether the editor offers the Hide button at all.</summary>
    private readonly record struct HudFrameRecord(string Id, string Label, HudPlacement Placement,
        Vector2 LogicalSize, Vector2 LogicalOrigin, string? Parent, bool Overridden,
        bool Hidden, bool Hideable);

    /// <summary>
    /// Where a frame draws this frame. Hidden is true when the draw site must NOT draw: the
    /// player hid the frame in the active layout and Edit Mode is off. In Edit Mode a hidden
    /// frame still draws (its mover says so) so it can be found and shown again; the site
    /// keeps its one registry call and checks this one flag.
    /// </summary>
    private readonly record struct HudFrameResult(Vector2 LogicalOrigin, Vector2 ScreenMin,
        Vector2 ScreenSize, float Scale, Vector2 LogicalDisplay, bool Hidden)
    {
        public Vector2 ScreenMax => ScreenMin + ScreenSize;
    }

    /// <summary>Which of the two layouts is live: the Command View vantage or body play.</summary>
    private HudLayoutContext HudContext =>
        _freeView ? HudLayoutContext.Command : HudLayoutContext.Body;

    private HudLayoutSettings HudLayoutState => Settings.HudLayout ??= new HudLayoutSettings();

    /// <summary>Start of DrawCombatHud: the registry is only ever this frame's draw sites.</summary>
    private void BeginHudFrameRegistry()
    {
        _hudFrames.Clear();
        _hudFrameIndex.Clear();
    }

    /// <summary>
    /// Register a HUD frame and resolve where it draws. <paramref name="authored"/> is the
    /// body-play default; <paramref name="authoredCommand"/> the Command View default when it
    /// differs. A <paramref name="parent"/> makes this a child whose offset is measured from
    /// the parent's resolved rect (it moves with the parent and is not separately draggable in
    /// phase 1). Sizes and offsets are logical pixels; the result carries both logical and
    /// screen rects so the site can keep authoring in whichever it already used. A frame with
    /// <paramref name="hideable"/> false (a panel the player opens on purpose) never gets the
    /// editor's Hide button; every other site honours <see cref="HudFrameResult.Hidden"/>.
    /// </summary>
    private HudFrameResult HudFrame(string id, string label, HudPlacement authored, Vector2 logicalSize,
        HudPlacement? authoredCommand = null, string? parent = null, bool hideable = true)
    {
        float scale = GameplayUiScale();
        Vector2 display = ImGui.GetIO().DisplaySize;
        Vector2 logicalDisplay = display / MathF.Max(.01f, scale);
        HudLayoutContext context = HudContext;
        HudPlacement placement = context == HudLayoutContext.Command && authoredCommand is not null
            ? authoredCommand : authored;
        bool overridden = false;
        if (parent is null && HudLayoutLaw.Override(HudLayoutState, context, id) is { } over)
        {
            placement = over;
            overridden = true;
        }
        Vector2 containerMin = Vector2.Zero, containerSize = logicalDisplay;
        if (parent is not null && _hudFrameIndex.TryGetValue(parent, out int parentIndex))
        {
            containerMin = _hudFrames[parentIndex].LogicalOrigin;
            containerSize = _hudFrames[parentIndex].LogicalSize;
        }
        Vector2 origin = HudLayoutLaw.Clamp(
            HudLayoutLaw.Resolve(placement, containerMin, containerSize, logicalSize),
            logicalSize, logicalDisplay);

        // A child follows its parent's visibility; a non-hideable frame is never hidden.
        bool hidden = hideable && (parent is null
            ? HudLayoutLaw.IsHidden(HudLayoutState, context, id)
            : _hudFrameIndex.TryGetValue(parent, out int parentRecord) && _hudFrames[parentRecord].Hidden);
        var record = new HudFrameRecord(id, label, placement, logicalSize, origin, parent, overridden,
            hidden, hideable);
        if (_hudFrameIndex.TryGetValue(id, out int existing)) _hudFrames[existing] = record;
        else
        {
            _hudFrameIndex[id] = _hudFrames.Count;
            _hudFrames.Add(record);
        }
        // Whole device pixels: HUD art is authored pixel-exact and a half-pixel origin blurs it.
        Vector2 screenMin = new(MathF.Round(origin.X * scale), MathF.Round(origin.Y * scale));
        return new HudFrameResult(origin, screenMin, logicalSize * scale, scale, logicalDisplay,
            hidden && !_hudEditMode);
    }

    // ── Edit Mode lifecycle ──────────────────────────────────────────────────────────────

    /// <summary>Open Edit Mode, optionally with a frame pre-selected (chat's "Move chat").</summary>
    private void EnterHudEditMode(string? select = null)
    {
        if (_hudEditMode)
        {
            if (select is not null && _hudEdit is not null) _hudEdit.Selected = select;
            return;
        }
        _hudEdit = HudLayoutEditLaw.Begin(HudLayoutState, HudContext, select);
        _hudEditMode = true;
        _hudEditFocusPending = true;
        _chatEditOpen = false;
        AddChatMessage("HUD layout: drag a frame to move it, arrow keys nudge, H hides or shows it, Escape saves and exits.");
    }

    /// <summary>Save writes settings once; Revert restores the snapshot taken on entry.</summary>
    private void ExitHudEditMode(bool save)
    {
        if (!_hudEditMode) return;
        if (save) CommitSettings();
        else if (_hudEdit is not null) Settings.HudLayout = _hudEdit.Snapshot;
        _hudEditMode = false;
        _hudEdit = null;
        AddChatMessage(save ? "HUD layout saved." : "HUD layout changes discarded.");
    }

    private void ToggleHudEditMode()
    {
        if (_hudEditMode) ExitHudEditMode(save: true);
        else EnterHudEditMode();
    }

    /// <summary>The binding row (unbound by default) - same edge idiom as the free-view toggle.</summary>
    private void UpdateHudEditInput(bool typing)
    {
        if (BindingPressedEdge(GameBinding.ToggleHudEditMode, typing) &&
            (_net is { IsInWorld: true } || CreatorInWorld || HudPreview))
            ToggleHudEditMode();
    }

    /// <summary>Escape = Save &amp; Exit. Runs ahead of the game-menu Escape ladder.</summary>
    private bool ConsumeHudEditEscape()
    {
        if (!_hudEditMode) return false;
        ExitHudEditMode(save: true);
        return true;
    }

    // ── binding gate ─────────────────────────────────────────────────────────────────────
    // Gameplay bindings are off while editing, except the camera (so the view can still be
    // flown to check a layout), the Command View toggle (which switches the edited context)
    // and Edit Mode's own toggle. Arrow keys are the editor's nudge keys while a frame is
    // selected, so a movement binding spelled with an arrow yields to the nudge.

    private static readonly HashSet<GameBinding> HudEditPassthroughBindings =
    [
        GameBinding.MoveForward, GameBinding.MoveBackward,
        GameBinding.StrafeLeft, GameBinding.StrafeRight,
        GameBinding.TurnLeft, GameBinding.TurnRight,
        GameBinding.CameraZoomIn, GameBinding.CameraZoomOut,
        GameBinding.RtsMoveForward, GameBinding.RtsMoveBackward,
        GameBinding.RtsStrafeLeft, GameBinding.RtsStrafeRight,
        GameBinding.RtsTurnLeft, GameBinding.RtsTurnRight,
        GameBinding.RtsRigForward, GameBinding.RtsRigBackward,
        GameBinding.RtsBoomZoomIn, GameBinding.RtsBoomZoomOut,
        GameBinding.RtsToggleFreeView, GameBinding.ToggleHudEditMode,
    ];

    private bool HudEditBlocksBinding(GameBinding binding) =>
        _hudEditMode && !HudEditPassthroughBindings.Contains(binding);

    private bool HudEditOwnsKey(Key key) =>
        _hudEditMode && _hudEdit?.Selected is not null &&
        key is Key.Left or Key.Right or Key.Up or Key.Down;
}

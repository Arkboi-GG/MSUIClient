using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;

namespace MSUIClient;

/// <summary>
/// HUD layout Edit Mode overlay (PLAN_21, phase 1). One full-screen transparent ImGui window
/// begun AFTER every frame it edits: because it is display-front, the furniture underneath
/// never sees hover - that is the click swallow, with no per-site edit flag. Inside it, in
/// hit-test order (ImGui gives hover to the FIRST item submitted under the pointer): the
/// toolbar's buttons, the selection card, one mover per registered frame smallest-area first
/// (so a frame nested in a larger one stays grabbable), then a backdrop that clears the
/// selection. Everything visible is painted on the foreground draw list with the carved-stone
/// chrome, GameText and VanillaButton - no ImGui widgets (GameplayImguiPolicyLaw).
///
/// Drags update the live override immediately, so the real frame follows; entering snapshots
/// the layout block and Save / Revert decide once on exit (GameLoop.HudFrames.cs).
/// </summary>
public sealed partial class GameLoop
{
    private const float HudEditToolbarHeight = 40f;   // logical; y 0-60 is free in the Command View
    /// <summary>The toolbar's height this frame: one row, or two when the tool run and the
    /// exits would collide on a narrow logical width (a big UI scale on a small screen).</summary>
    private float _hudEditToolbarHeight = HudEditToolbarHeight;
    private const float HudEditCardWidth = 236f;
    private const float HudEditCardHeight = 214f;
    private const float HudEditButtonHeight = 20f;
    private const uint HudEditGuideColor = 0xffff00ffu;   // magenta, the layout-tool convention

    /// <summary>Warm gold at the given alpha (ImGui packs ABGR).</summary>
    private static uint HudEditTint(int alpha) => ((uint)alpha << 24) | 0x0040c8ffu;

    private void DrawHudLayoutEditor()
    {
        if (!_hudEditMode || _hudEdit is null || _gameplayArt is null) return;
        HudEditSession session = _hudEdit;
        HudLayoutSettings hl = HudLayoutState;
        float scale = GameplayUiScale();
        Vector2 display = ImGui.GetIO().DisplaySize;
        Vector2 logicalDisplay = display / MathF.Max(.01f, scale);

        // Ctrl+F while editing switches the edited context; the mover list follows the draw
        // sites automatically because the registry is rebuilt from them every frame.
        HudLayoutContext context = HudContext;
        if (session.Context != context)
        {
            session.Context = context;
            session.Selected = null;
            session.Dragging = null;
            session.FrameListOpen = false;
        }
        if (session.Selected is not null && !_hudFrameIndex.ContainsKey(session.Selected))
            session.Selected = null;

        if (hl.GridVisible) DrawHudEditGrid(ImGui.GetBackgroundDrawList(), display, scale, hl.GridSize);

        ImGui.SetNextWindowPos(Vector2.Zero, ImGuiCond.Always);
        ImGui.SetNextWindowSize(display, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);
        if (_hudEditFocusPending)
        {
            ImGui.SetNextWindowFocus();
            _hudEditFocusPending = false;
        }
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        // Deliberately NOT NoBringToFrontOnFocus: the overlay must be the topmost window.
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        bool begun = ImGui.Begin("##hud-layout-editor", flags);
        ImGui.PopStyleVar(2);
        if (!begun) { ImGui.End(); return; }
        // Two draw lists on purpose: movers paint on the overlay WINDOW's list (display-front,
        // so over every HUD window) while the toolbar, card, list and guides paint on the
        // FOREGROUND list, which sits over the movers whatever the submission order - the
        // hit-test order below (toolbar, card, movers, backdrop) stays a separate concern.
        ImDrawListPtr fg = ImGui.GetForegroundDrawList();
        ImDrawListPtr movers = ImGui.GetWindowDrawList();

        DrawHudEditToolbar(fg, scale, display, hl, session);
        if (!_hudEditMode) { ImGui.End(); return; }   // Save / Revert fired this frame
        if (session.Selected is not null)
            DrawHudEditCard(fg, scale, display, logicalDisplay, hl, session);

        int[] order = Enumerable.Range(0, _hudFrames.Count)
            .OrderBy(i => _hudFrames[i].LogicalSize.X * _hudFrames[i].LogicalSize.Y)
            .ToArray();
        foreach (int i in order)
            DrawHudEditMover(movers, fg, _hudFrames[i], scale, display, logicalDisplay, hl, session);

        ImGui.SetCursorScreenPos(Vector2.Zero);
        if (ImGui.InvisibleButton("##hud-edit-backdrop", Vector2.Max(display, new Vector2(1f))))
        {
            session.Selected = null;
            session.FrameListOpen = false;
        }

        HandleHudEditKeys(hl, session);
        ImGui.End();
    }

    // ── grid + guides ────────────────────────────────────────────────────────────────────

    private static void DrawHudEditGrid(ImDrawListPtr bg, Vector2 display, float scale, int gridSize)
    {
        float step = MathF.Max(4f, gridSize * scale);
        for (float x = 0f; x <= display.X; x += step)
            bg.AddLine(new Vector2(x, 0f), new Vector2(x, display.Y), 0x22ffffffu, 1f);
        for (float y = 0f; y <= display.Y; y += step)
            bg.AddLine(new Vector2(0f, y), new Vector2(display.X, y), 0x22ffffffu, 1f);
        // Brighter centre lines: the two axes a symmetric layout is built around.
        bg.AddLine(new Vector2(display.X * .5f, 0f), new Vector2(display.X * .5f, display.Y), 0x66ffffffu, 1f);
        bg.AddLine(new Vector2(0f, display.Y * .5f), new Vector2(display.X, display.Y * .5f), 0x66ffffffu, 1f);
    }

    private static void DrawHudEditGuides(ImDrawListPtr fg, IReadOnlyList<HudLayoutLaw.GuideLine> guides,
        float scale, Vector2 display)
    {
        foreach (HudLayoutLaw.GuideLine g in guides)
        {
            float at = MathF.Round(g.At * scale);
            if (g.Vertical)
                fg.AddLine(new Vector2(at, 0f), new Vector2(at, display.Y), HudEditGuideColor, 1f);
            else
                fg.AddLine(new Vector2(0f, at), new Vector2(display.X, at), HudEditGuideColor, 1f);
        }
    }

    // ── toolbar ──────────────────────────────────────────────────────────────────────────

    private void DrawHudEditToolbar(ImDrawListPtr fg, float scale, Vector2 display,
        HudLayoutSettings hl, HudEditSession session)
    {
        string title = session.Context == HudLayoutContext.Command
            ? "HUD LAYOUT  ·  COMMAND VIEW" : "HUD LAYOUT  ·  BODY";
        float x = 14f + GameText.MeasureWidth("GameFontNormal", title, scale) / scale + 18f;
        // Widths of the tool run (nine buttons + gaps) and the two exits, so the strip can
        // grow a second row instead of letting Reset all disappear under Save & Exit.
        const float runWidth = 150f + 72f + 54f + 106f + 122f + 68f + 54f + 54f + 80f + 9f * 6f;
        const float exitsWidth = 104f + 6f + 96f + 14f;
        bool twoRows = x + runWidth + exitsWidth > display.X / scale;
        _hudEditToolbarHeight = twoRows ? HudEditToolbarHeight + 30f : HudEditToolbarHeight;
        float exitY = twoRows ? HudEditToolbarHeight : 10f;

        Vector2 min = Vector2.Zero;
        Vector2 max = new(display.X, _hudEditToolbarHeight * scale);
        DrawRtsConsoleBackdrop(fg, min, max, scale);
        GameText.Draw(fg, "GameFontNormal", title, new Vector2(14f, 12f) * scale, scale, PainterlyGoldLit);
        const float y = 10f;
        bool Button(string id, string caption, float width, bool enabled = true)
        {
            bool clicked = VanillaButton(fg, id, caption, new Vector2(x, y) * scale,
                new Vector2(width, HudEditButtonHeight), scale, enabled,
                "GameFontNormalSmall", "GameFontHighlightSmall", "GameFontDisableSmall");
            x += width + 6f;
            return clicked;
        }

        if (Button("##hud-edit-layout", $"Layout: {hl.ActiveLayout}", 150f))
            hl.ActiveLayout = HudLayoutLaw.NextLayoutName(hl);
        if (Button("##hud-edit-grid", hl.GridVisible ? "Grid: On" : "Grid: Off", 72f))
            hl.GridVisible = !hl.GridVisible;
        if (Button("##hud-edit-gridsize", $"{hl.GridSize} px", 54f))
            hl.GridSize = HudLayoutLaw.NextGridSize(hl.GridSize);
        if (Button("##hud-edit-snapgrid", hl.SnapToGrid ? "Snap grid: On" : "Snap grid: Off", 106f))
            hl.SnapToGrid = !hl.SnapToGrid;
        if (Button("##hud-edit-snapframes", hl.SnapToFrames ? "Snap frames: On" : "Snap frames: Off", 122f))
            hl.SnapToFrames = !hl.SnapToFrames;
        float framesX = x;
        if (Button("##hud-edit-frames", "Frames", 68f))
            session.FrameListOpen = !session.FrameListOpen;
        if (Button("##hud-edit-undo", "Undo", 54f, session.CanUndo)) HudEditUndo(hl, session, undo: true);
        if (Button("##hud-edit-redo", "Redo", 54f, session.CanRedo)) HudEditUndo(hl, session, undo: false);
        if (Button("##hud-edit-resetall", "Reset all", 80f))
        {
            HudEditChange? change = HudLayoutEditLaw.ResetAll(hl, session.Context);
            if (change is not null) session.Push(change);
        }

        // The two exits, right-aligned so they never collide with the tool run on a narrow screen.
        float right = display.X / scale - 14f;
        right -= 104f;
        if (VanillaButton(fg, "##hud-edit-revert", "Revert & Exit", new Vector2(right, exitY) * scale,
                new Vector2(104f, HudEditButtonHeight), scale, true,
                "GameFontNormalSmall", "GameFontHighlightSmall", "GameFontDisableSmall"))
        {
            ExitHudEditMode(save: false);
            return;
        }
        right -= 96f + 6f;
        if (VanillaButton(fg, "##hud-edit-save", "Save & Exit", new Vector2(right, exitY) * scale,
                new Vector2(96f, HudEditButtonHeight), scale, true,
                "GameFontNormalSmall", "GameFontHighlightSmall", "GameFontDisableSmall"))
        {
            ExitHudEditMode(save: true);
            return;
        }

        if (session.FrameListOpen)
            DrawHudEditFrameList(fg, scale, framesX, _hudEditToolbarHeight + 2f, session);

        // Block the strip so a drag that starts on it never grabs a frame underneath.
        ImGui.SetCursorScreenPos(min);
        ImGui.InvisibleButton("##hud-edit-toolbar", Vector2.Max(max - min, new Vector2(1f)));
    }

    /// <summary>Select-by-name: the way to reach a frame that is small, hidden behind another,
    /// or parked somewhere awkward (FFXIV's element list).</summary>
    private void DrawHudEditFrameList(ImDrawListPtr fg, float scale, float x, float y, HudEditSession session)
    {
        const float rowHeight = 20f, width = 210f;
        int rows = 0;
        foreach (HudFrameRecord f in _hudFrames) if (f.Parent is null) rows++;
        Vector2 min = new Vector2(x, y) * scale;
        Vector2 max = min + new Vector2(width, 8f + rowHeight * Math.Max(1, rows)) * scale;
        DrawRtsConsoleBackdrop(fg, min, max, scale);

        float rowY = y + 4f;
        foreach (HudFrameRecord f in _hudFrames)
        {
            if (f.Parent is not null) continue;
            Vector2 rowMin = new Vector2(x + 6f, rowY) * scale;
            Vector2 rowSize = new Vector2(width - 12f, rowHeight) * scale;
            ImGui.SetCursorScreenPos(rowMin);
            ImGui.InvisibleButton("##hud-edit-list-" + f.Id, rowSize);
            bool hot = ImGui.IsItemHovered();
            bool selected = session.Selected == f.Id;
            if (hot || selected) fg.AddRectFilled(rowMin, rowMin + rowSize, HudEditTint(selected ? 0x50 : 0x30));
            GameText.Draw(fg, "GameFontNormalSmall",
                GameText.EllipsizeToBox("GameFontNormalSmall", f.Label, width - 20f, rowHeight, scale),
                rowMin + new Vector2(4f, 4f) * scale, scale,
                selected ? PainterlyGoldLit : hot ? 0xffffffffu : 0xffd8e0e6u);
            if (ImGui.IsItemClicked())
            {
                session.Selected = f.Id;
                session.FrameListOpen = false;
            }
            rowY += rowHeight;
        }
        if (rows == 0)
            GameText.Draw(fg, "GameFontDisableSmall", "No frames drawn in this view.",
                new Vector2(x + 10f, rowY + 4f) * scale, scale);

        ImGui.SetCursorScreenPos(min);
        ImGui.InvisibleButton("##hud-edit-list", Vector2.Max(max - min, new Vector2(1f)));
    }

    // ── selection card ───────────────────────────────────────────────────────────────────

    private void DrawHudEditCard(ImDrawListPtr fg, float scale, Vector2 display, Vector2 logicalDisplay,
        HudLayoutSettings hl, HudEditSession session)
    {
        if (session.Selected is null || !_hudFrameIndex.TryGetValue(session.Selected, out int index)) return;
        HudFrameRecord rec = _hudFrames[index];

        // At the screen edge farthest from the selection, level with it, clear of the toolbar -
        // unless the player has dragged the card somewhere, which then sticks for the session.
        Vector2 cardSize = new(HudEditCardWidth, HudEditCardHeight);
        Vector2 centre = (rec.LogicalOrigin + rec.LogicalSize * .5f) * scale;
        float top = _hudEditToolbarHeight + 8f;
        float bottom = MathF.Max(top, logicalDisplay.Y - HudEditCardHeight - 8f);
        Vector2 cardOrigin = session.CardOrigin ?? new Vector2(
            HudLayoutEditLaw.CardOnLeft(centre, display) ? 12f : logicalDisplay.X - 12f - HudEditCardWidth,
            Math.Clamp(centre.Y / scale - HudEditCardHeight * .5f, top, bottom));
        cardOrigin = HudLayoutLaw.Clamp(cardOrigin, cardSize, logicalDisplay);
        Vector2 min = cardOrigin * scale;
        Vector2 max = min + cardSize * scale;
        DrawRtsConsoleBackdrop(fg, min, max, scale);

        GameText.Draw(fg, "GameFontNormal",
            GameText.EllipsizeToBox("GameFontNormal", rec.Label, HudEditCardWidth - 28f, 16f, scale),
            min + new Vector2(14f, 10f) * scale, scale, PainterlyGoldLit);
        GameText.Draw(fg, "GameFontNormalSmall",
            rec.Overridden ? "Custom position" : "Authored position",
            min + new Vector2(14f, 28f) * scale, scale, 0xff9aa4abu);

        HudPlacement p = rec.Placement;

        // 9-dot anchor picker: this is also the one-click "put it in that corner".
        GameText.Draw(fg, "GameFontNormalSmall", "Anchor", min + new Vector2(14f, 52f) * scale, scale);
        GameText.Draw(fg, "GameFontNormalSmall", HudLayoutLaw.Label(p.Anchor),
            min + new Vector2(14f, 70f) * scale, scale, 0xffd8e0e6u);
        for (int i = 0; i < 9; i++)
        {
            var anchor = (HudAnchor)i;
            int col = i % 3, row = i / 3;
            Vector2 pipMin = min + new Vector2(150f + col * 24f, 48f + row * 24f) * scale;
            Vector2 pipSize = new Vector2(20f) * scale;
            ImGui.SetCursorScreenPos(pipMin);
            ImGui.InvisibleButton("##hud-edit-anchor-" + i, pipSize);
            bool hot = ImGui.IsItemHovered();
            bool on = p.Anchor == anchor;
            Vector2 c = pipMin + pipSize * .5f;
            fg.AddCircleFilled(c, 6f * scale,
                on ? PainterlyGoldLit : hot ? PainterlyGoldShade : PainterlyFrameInner);
            fg.AddCircle(c, 6f * scale, on ? PainterlyFrameRule : PainterlyFrameOuter, 0, MathF.Max(1f, scale));
            if (ImGui.IsItemClicked())
                HudEditApply(hl, session, rec.Id, HudPlacement.At(anchor, 0f, 0f));
        }

        DrawHudEditNudgeRow(fg, scale, min, 130f, "X", p.X,
            delta => HudEditApply(hl, session, rec.Id, p with { X = p.X + delta }));
        DrawHudEditNudgeRow(fg, scale, min, 156f, "Y", p.Y,
            delta => HudEditApply(hl, session, rec.Id, p with { Y = p.Y + delta }));

        if (VanillaButton(fg, "##hud-edit-reset-frame", "Reset frame",
                min + new Vector2(14f, 186f) * scale, new Vector2(100f, HudEditButtonHeight), scale,
                rec.Overridden, "GameFontNormalSmall", "GameFontHighlightSmall", "GameFontDisableSmall"))
            HudEditApply(hl, session, rec.Id, null);
        if (VanillaButton(fg, "##hud-edit-done", "Done",
                min + new Vector2(HudEditCardWidth - 14f - 70f, 186f) * scale,
                new Vector2(70f, HudEditButtonHeight), scale, true,
                "GameFontNormalSmall", "GameFontHighlightSmall", "GameFontDisableSmall"))
            session.Selected = null;

        // The card's own body, submitted after its controls so they keep the hover: blocks
        // the movers underneath and drags the card (it covers things, so it has to move).
        ImGui.SetCursorScreenPos(min);
        ImGui.InvisibleButton("##hud-edit-card", Vector2.Max(max - min, new Vector2(1f)));
        if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left, 2f))
        {
            session.CardOrigin = HudLayoutLaw.Clamp(
                cardOrigin + ImGui.GetIO().MouseDelta / MathF.Max(.01f, scale), cardSize, logicalDisplay);
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll);
        }
        else if (ImGui.IsItemHovered())
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll);
    }

    /// <summary>ElvUI's nudge row: -10 / -1 / value / +1 / +10, in logical pixels.</summary>
    private void DrawHudEditNudgeRow(ImDrawListPtr fg, float scale, Vector2 cardMin, float y,
        string axis, float value, Action<float> nudge)
    {
        GameText.Draw(fg, "GameFontNormalSmall", axis, cardMin + new Vector2(14f, y + 4f) * scale, scale);
        float x = 30f;
        void Step(string caption, float delta)
        {
            if (VanillaButton(fg, $"##hud-edit-{axis}{caption}", caption,
                    cardMin + new Vector2(x, y) * scale, new Vector2(34f, HudEditButtonHeight), scale,
                    true, "GameFontNormalSmall", "GameFontHighlightSmall", "GameFontDisableSmall"))
                nudge(delta);
            x += 36f;
        }
        Step("-10", -10f);
        Step("-1", -1f);
        GameText.DrawCentered(fg, "GameFontNormalSmall", value.ToString("0"),
            cardMin + new Vector2(x + 24f, y + 10f) * scale, scale, PainterlyGoldLit);
        x += 48f;
        Step("+1", 1f);
        Step("+10", 10f);
    }

    // ── movers ───────────────────────────────────────────────────────────────────────────

    private void DrawHudEditMover(ImDrawListPtr dl, ImDrawListPtr fg, in HudFrameRecord rec, float scale,
        Vector2 display, Vector2 logicalDisplay, HudLayoutSettings hl, HudEditSession session)
    {
        Vector2 min = new(MathF.Round(rec.LogicalOrigin.X * scale), MathF.Round(rec.LogicalOrigin.Y * scale));
        Vector2 size = Vector2.Max(rec.LogicalSize * scale, new Vector2(4f));
        Vector2 max = min + size;
        bool child = rec.Parent is not null;
        bool selected = session.Selected == rec.Id;

        ImGui.SetCursorScreenPos(min);
        ImGui.InvisibleButton("##hud-mover-" + rec.Id, size);
        bool hovered = ImGui.IsItemHovered();

        if (child)
        {
            // Children ride their parent in phase 1: clicking one selects the parent.
            if (ImGui.IsItemClicked()) session.Selected = rec.Parent;
        }
        else
        {
            if (ImGui.IsItemActivated())
            {
                session.Selected = rec.Id;
                session.FrameListOpen = false;
                session.Dragging = rec.Id;
                session.DragStartOrigin = rec.LogicalOrigin;
                session.DragStartMouse = ImGui.GetIO().MousePos;
                session.DragBefore = HudLayoutLaw.Override(hl, session.Context, rec.Id);
            }
            if (session.Dragging == rec.Id && ImGui.IsItemActive() &&
                ImGui.IsMouseDragging(ImGuiMouseButton.Left, 2f))
            {
                Vector2 proposed = HudLayoutEditLaw.DragOrigin(session, ImGui.GetIO().MousePos, scale);
                HudLayoutLaw.SnapResult snap;
                if (ImGui.GetIO().KeyAlt)
                    snap = new HudLayoutLaw.SnapResult(proposed, []);
                else
                {
                    var others = new List<HudLayoutLaw.SnapBox>(_hudFrames.Count);
                    foreach (HudFrameRecord f in _hudFrames)
                        if (f.Id != rec.Id && f.Parent is null)
                            others.Add(new HudLayoutLaw.SnapBox(f.LogicalOrigin, f.LogicalSize));
                    snap = HudLayoutLaw.Snap(proposed, rec.LogicalSize, logicalDisplay, others,
                        hl.SnapToFrames, hl.SnapToGrid, hl.GridSize);
                }
                Vector2 origin = HudLayoutLaw.Clamp(snap.Origin, rec.LogicalSize, logicalDisplay);
                // Live apply: the real frame follows next draw. Re-anchoring on every step (not
                // only on drop) keeps the card's anchor readout truthful mid-drag.
                HudLayoutLaw.EnsureEditable(hl).For(session.Context)[rec.Id] =
                    HudLayoutLaw.Reanchor(origin, rec.LogicalSize, logicalDisplay);
                DrawHudEditGuides(fg, snap.Guides, scale, display);
                ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll);
            }
            if (session.Dragging == rec.Id && ImGui.IsItemDeactivated())
            {
                HudPlacement? after = HudLayoutLaw.Override(hl, session.Context, rec.Id);
                if (!Equals(after, session.DragBefore))
                    session.Push(new HudEditChange(
                        [new HudEditEntry(rec.Id, session.Context, session.DragBefore, after)]));
                session.Dragging = null;
            }
        }

        float rule = MathF.Max(1f, scale);
        if (child)
        {
            dl.AddRect(min, max, 0x80ffffffu, 0f, ImDrawFlags.None, rule);
        }
        else
        {
            dl.AddRectFilled(min, max, HudEditTint(selected ? 0x58 : hovered ? 0x44 : 0x2c));
            dl.AddRect(min, max, selected ? PainterlyGoldLit : PainterlyFrameRule, 0f, ImDrawFlags.None,
                selected ? rule * 2f : rule);
        }
        string label = GameText.EllipsizeToBox("GameFontNormalSmall", rec.Label,
            MathF.Max(8f, rec.LogicalSize.X - 6f), 14f, scale);
        GameText.Draw(dl, "GameFontNormalSmall", label, min + new Vector2(3f, 2f) * scale, scale,
            selected ? PainterlyGoldLit : child ? 0xffb8c0c6u : 0xffffffffu);
        if (selected && session.Dragging == rec.Id)
        {
            HudPlacement p = HudLayoutLaw.Override(hl, session.Context, rec.Id) ?? rec.Placement;
            GameText.Draw(dl, "GameFontNormalSmall",
                $"X {p.X:0}  Y {p.Y:0}  {HudLayoutLaw.Label(p.Anchor)}",
                min + new Vector2(3f, 16f) * scale, scale, 0xffffffffu);
        }
    }

    // ── edits, undo, keys ────────────────────────────────────────────────────────────────

    private static void HudEditApply(HudLayoutSettings hl, HudEditSession session, string id,
        HudPlacement? placement)
        => session.Push(HudLayoutEditLaw.SetPlacement(hl, session.Context, id, placement));

    private static void HudEditUndo(HudLayoutSettings hl, HudEditSession session, bool undo)
    {
        HudEditChange? change = undo ? session.Undo() : session.Redo();
        if (change is not null) HudLayoutEditLaw.Apply(hl, change, undo);
    }

    /// <summary>Arrows nudge 1 (Shift: 10), Ctrl+Z / Ctrl+Y, Delete resets the selected frame.
    /// Escape is spent by the game-menu ladder (ConsumeHudEditEscape), not here.</summary>
    private void HandleHudEditKeys(HudLayoutSettings hl, HudEditSession session)
    {
        ImGuiIOPtr io = ImGui.GetIO();
        if (io.WantTextInput) return;
        if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.Z, false)) { HudEditUndo(hl, session, undo: true); return; }
        if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.Y, false)) { HudEditUndo(hl, session, undo: false); return; }
        if (session.Selected is null || !_hudFrameIndex.TryGetValue(session.Selected, out int index)) return;
        HudFrameRecord rec = _hudFrames[index];
        if (ImGui.IsKeyPressed(ImGuiKey.Delete, false))
        {
            if (rec.Overridden) HudEditApply(hl, session, rec.Id, null);
            return;
        }
        int dx = (ImGui.IsKeyPressed(ImGuiKey.RightArrow, true) ? 1 : 0) -
                 (ImGui.IsKeyPressed(ImGuiKey.LeftArrow, true) ? 1 : 0);
        int dy = (ImGui.IsKeyPressed(ImGuiKey.DownArrow, true) ? 1 : 0) -
                 (ImGui.IsKeyPressed(ImGuiKey.UpArrow, true) ? 1 : 0);
        if (dx == 0 && dy == 0) return;
        Vector2 nudged = HudLayoutLaw.Nudge(new Vector2(rec.Placement.X, rec.Placement.Y), dx, dy, io.KeyShift);
        HudEditApply(hl, session, rec.Id, rec.Placement with { X = nudged.X, Y = nudged.Y });
    }
}

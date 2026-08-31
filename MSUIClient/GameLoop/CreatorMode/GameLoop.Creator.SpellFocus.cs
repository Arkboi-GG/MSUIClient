using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// Spell Workshop FOCUS LAYOUT (2026-08-31) — the creator's third layout.
//
// The docked workspace lays a panel's sections side by side as fixed-width
// columns in a bottom deck. That works for a panel with two or three sections;
// the Spell Workshop registers one per PHASE MODEL — precast, cast, missile,
// impact, every geometry child — and five-to-seven columns in a deck capped at
// 55% of the screen means every one of them is clipped mid-widget.
//
// So the workshop moves into ONE full-height right sidebar, stacked: the spell,
// then its phases, then the selected phase's dials and images. Everything to the
// left of it is world, because the point of the workshop is watching the spell
// play on the model.
//
// Two things this deliberately does NOT do, both learned the hard way:
//   * It does not take both sidebars. That read as "thicker than the deck it
//     replaced", which defeats the purpose.
//   * It does not offer the section pop-out corners. A grid of little squares
//     down the phase list looks exactly like a column of checkboxes, and every
//     mis-click spawns a floating window over the model. In focus mode the
//     sidebar IS the home for every section, and DrawPoppedCreatorSections skips
//     this panel entirely so nothing can double-draw.
//
// The rows are still the SAME registered sections the deck and the classic
// floating panel draw, in the SAME persisted order, with the same labels and the
// same ' *' dirty marker. RegisterCreatorSpellsSections does not know this
// layout exists.
// ─────────────────────────────────────────────────────────────────────────────

public sealed partial class GameLoop
{
    /// <summary>Session-only opt-out, written by Escape and the header's "deck"
    /// button. NEVER persisted: a reflexive Escape must not rewrite a preference -
    /// the permanent switch is the checkbox in the Creator UI dials.</summary>
    private bool _spellFocusSuppressed;

    /// <summary>The selected row, as a SECTION ID ("ws-loop", "ws-audio",
    /// "ws-session", or "ws-{model path}" with its backslashes intact). A plain
    /// field on purpose: SetSectionOpen writes settings.json on every call, and
    /// Save() is a whole-file rewrite.</summary>
    private string? _spellFocusRow;

    /// <summary>
    /// The single source of truth for "the focus layout owns the screen this
    /// frame", read by the workspace entry point, the right-inset helper, the
    /// popped-section pass and the Escape ladder. The Root-view and customizer
    /// terms are what keep the rails and the deck intact for the Encounter Lab and
    /// the Character Customizer - never re-derive this condition inline.
    /// </summary>
    private bool SpellFocusActive =>
        CreatorWorkspaceActive && Settings.Creator.SpellFocus && !_spellFocusSuppressed &&
        _creatorPanel == CreatorPanel.Spells && _workspaceView == WorkspaceView.Root &&
        _encounterPlayerSetupKey is null;

    /// <summary>The sidebar's width. Deliberately takes the RAW clamped UiScale
    /// setting rather than <see cref="CreatorUiScale"/>: the latter folds in
    /// ActivePanelTune.Widget and _creatorScaleBoost, so the geometry would change
    /// depending on WHEN it was read.</summary>
    private float SpellFocusPaneWidth => SpellFocusLayoutLaw.SidebarWidth(
        ImGui.GetIO().DisplaySize.X, ImGui.GetIO().DisplaySize.Y,
        Math.Clamp(Settings.Creator.UiScale, 0.6f, 2.5f), Settings.Creator.SpellFocusFraction);

    /// <summary>A row that edits a MODEL (a phase, the missile, a geometry child)
    /// rather than one of the workshop's fixed sections.</summary>
    private static bool IsSpellPhaseSection(string id)
        => id.StartsWith("ws-", StringComparison.Ordinal) &&
           id is not ("ws-spell" or "ws-loop" or "ws-audio" or "ws-session");

    private void DrawCreatorSpellFocus()
    {
        var io = ImGui.GetIO();
        float s = MathF.Max(io.DisplaySize.Y / GlueCanvasH, 0.5f) * CreatorBarScale;
        float paneW = SpellFocusPaneWidth;

        // INVARIANT: every early return happens ABOVE this line. _creatorScaleBoost
        // multiplies BOTH CreatorUiScale and CreatorTextScale, so leaking it renders
        // every later creator window this frame up to 2.2x oversized. _activePanelTune
        // is assigned first because PushCreatorStyle reads ActivePanelTune.Spacing.
        // The id is "Spells" verbatim - it keys the per-window dials, the widget
        // offsets and the layout-edit gate, all shared with the other two layouts.
        _activePanelTune = "Spells";
        _creatorScaleBoost = WorkspaceDeckBoost;

        DrawSpellFocusPane(paneW, s);

        _creatorScaleBoost = 1f;
        _activePanelTune = null;

        DrawSpellFocusSplitter(paneW);
        if (_workspaceHelpOpen) DrawWorkspaceHelp();
    }

    /// <summary>The full-height right sidebar. Deliberately NOT BeginWorkspaceRail:
    /// that pushes a translucent wash whose premise ("the world reads THROUGH them")
    /// is true of a 64px strip and false of a sidebar of dense dials.</summary>
    private bool BeginSpellFocusPane(string id, float paneW)
    {
        var io = ImGui.GetIO();
        ImGui.SetNextWindowPos(new Vector2(io.DisplaySize.X - paneW, 0f), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(paneW, io.DisplaySize.Y), ImGuiCond.Always);
        PushCreatorStyle();
        // The deck's compact rhythm: air scales with the display ONCE; the boost
        // stays on glyphs and widgets, or padding reads as enormous empty rows.
        float air = MathF.Max(io.DisplaySize.Y / GlueCanvasH, 0.5f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(14f, 7f) * air);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8f, 4.5f) * air);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(7f, 3.5f) * air);
        ImGui.PushStyleColor(ImGuiCol.WindowBg,
            new Vector4(0.055f, 0.05f, 0.045f, Math.Clamp(Settings.Creator.PanelAlpha, 0.15f, 1f)));
        return ImGui.Begin(id, CreatorChromeFlags | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoFocusOnAppearing);
    }

    /// <summary>Runs even when Begin returned false - the pushes happened either way.</summary>
    private void EndSpellFocusPane()
    {
        ImGui.End();
        ImGui.PopStyleColor();
        ImGui.PopStyleVar(3);
        PopCreatorStyle();
    }

    private void DrawSpellFocusPane(float paneW, float s)
    {
        if (BeginSpellFocusPane("##spell-focus", paneW))
        {
            float cs = CreatorUiScale;
            // Reserve the footer at the scale its buttons will actually DRAW at, not
            // the base font - CreatorTextScale carries the deck boost (up to 2.2x on
            // a tall display) and a base-font reserve clips them.
            float footerH = ImGui.GetTextLineHeight() * CreatorTextScale + 8f * cs + 14f * s;

            DrawSpellFocusHeader(s);

            // 1. The spell itself, INLINE. The result list is capped hard: this is a
            //    full-height pane, and the default 45% share would push the phase
            //    list and the whole editor below the fold.
            int pickerAt = _creatorSectionDefs.FindIndex(d => d.Panel == "Spells" && d.Id == "ws-spell");
            if (pickerAt >= 0)
            {
                ImGui.SetWindowFontScale(CreatorTextScale);
                _creatorResultsFractionOverride = 0.22f;
                ImGui.PushID("ws-spell");
                _creatorSectionDefs[pickerAt].Body();
                ImGui.PopID();
                _creatorResultsFractionOverride = null;
                ImGui.SetWindowFontScale(1f);
                ImGui.Separator();
            }

            // 2. The phase list, in a child of its own so a long one scrolls instead
            //    of squeezing the editor out.
            var rows = new List<int>();
            foreach (string id in OrderedSectionIds("Spells"))
            {
                if (id == "ws-spell") continue;
                int at = _creatorSectionDefs.FindIndex(d => d.Panel == "Spells" && d.Id == id);
                if (at >= 0) rows.Add(at);
            }

            if (rows.Count > 0)
            {
                float stride = CreatorResultRowHeight + ImGui.GetStyle().ItemSpacing.Y;
                float want = rows.Count * stride + 8f * cs;
                float room = MathF.Max(ImGui.GetContentRegionAvail().Y - footerH, stride * 3f);
                float listH = MathF.Min(want, room * 0.42f);

                ImGui.BeginChild("##focus-rows", new Vector2(0f, listH));
                ImGui.SetWindowFontScale(CreatorTextScale);

                // Snapshot the selection BEFORE the loop and apply a click AFTER it.
                // Reading the live field while the click writes it made every row
                // after the clicked one compare against the NEW id, so the "did the
                // selection survive?" test failed and the heal below overwrote the
                // click - clicking upward silently snapped to the first phase.
                string? pre = _spellFocusRow;
                string? picked = null;
                bool resolved = false;
                string? firstPhase = null, firstRow = null;
                bool? previousWasPhase = null;
                foreach (int at in rows)
                {
                    var def = _creatorSectionDefs[at];
                    bool phase = IsSpellPhaseSection(def.Id);
                    if (previousWasPhase is { } was && was != phase) ImGui.Separator();
                    previousWasPhase = phase;

                    firstRow ??= def.Id;
                    if (phase) firstPhase ??= def.Id;
                    if (def.Id == pre) resolved = true;

                    // A plain full-width row. No pop-out corner: a column of little
                    // squares reads as checkboxes, and mis-clicking one threw a
                    // floating window over the model.
                    ImGui.PushID(def.Id);
                    if (ImGui.Selectable(def.Label, def.Id == pre,
                            ImGuiSelectableFlags.None, new Vector2(0f, CreatorResultRowHeight)))
                        picked = def.Id;
                    ImGui.PopID();
                }
                // A click always wins. Otherwise heal a selection this frame's
                // registry no longer contains - the ws-{path} ids change wholesale
                // when a different spell is picked.
                if (picked is not null) _spellFocusRow = picked;
                else if (!resolved) _spellFocusRow = firstPhase ?? firstRow;
                ImGui.SetWindowFontScale(1f);
                ImGui.EndChild();
                ImGui.Separator();
            }
            else _spellFocusRow = null;

            // 3. The selected section, filling whatever is left above the footer.
            int detailAt = _spellFocusRow is { } row
                ? _creatorSectionDefs.FindIndex(d => d.Panel == "Spells" && d.Id == row)
                : -1;

            ImGui.SetWindowFontScale(CreatorTextScale);
            if (detailAt >= 0)
            {
                var def = _creatorSectionDefs[detailAt];
                ImGui.TextColored(new Vector4(1f, 0.82f, 0.28f, 1f), def.Label);
                if (IsSpellPhaseSection(def.Id))
                {
                    ImGui.SameLine();
                    if (ImGui.SmallButton("solo")) SoloCreatorSpellPhase(def.Id);
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Play only this phase, on repeat.");
                }
                ImGui.Separator();
            }
            ImGui.SetWindowFontScale(1f);

            // HorizontalScrollbar is load-bearing, not cosmetic: a texture row is an
            // unbreakable SameLine chain (~500*cs closed, ~850*cs with both colour
            // pickers open) and ImGui CLIPS rather than wraps - without it the Swap
            // and +Em buttons are invisible AND unclickable, with no error.
            ImGui.BeginChild("##focus-detail", new Vector2(0f, -footerH),
                false, ImGuiWindowFlags.HorizontalScrollbar);
            ImGui.SetWindowFontScale(CreatorTextScale);
            if (detailAt < 0)
            {
                ImGui.TextWrapped("Search a spell above, then pick a phase - precast, cast, " +
                                  "impact, missile or a geometry child - and its model dials " +
                                  "and images fill the rest of this sidebar.");
            }
            else
            {
                var def = _creatorSectionDefs[detailAt];
                ImGui.PushID(def.Id);
                def.Body();
                ImGui.PopID();
            }
            ImGui.SetWindowFontScale(1f);
            ImGui.EndChild();

            // The right rail's quick actions, kept reachable - at the pane's own text
            // scale, or they read at a third the size of everything above them.
            ImGui.SetWindowFontScale(CreatorTextScale);
            if (ImGui.SmallButton("Fly")) ToggleFreeView();
            ImGui.SameLine();
            if (ImGui.SmallButton("Wins"))
            {
                Settings.Creator.Workspace = false;
                SettingsFile?.Save();
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("Help")) _workspaceHelpOpen = !_workspaceHelpOpen;
            ImGui.SetWindowFontScale(1f);
        }
        EndSpellFocusPane();
    }

    /// <summary>The lone rail icon plus the workshop's own controls.</summary>
    private void DrawSpellFocusHeader(float s)
    {
        // The icon is handed the ORDINARY rail width, not the pane width:
        // WorkspaceRailButton derives its square as railW - 12*s and centres itself
        // in railW, so passing the pane width would draw one enormous block.
        float savedScale = _skin?.Scale ?? 1f;
        if (_skin is not null) _skin.Scale = s;
        if (WorkspaceRailButton("spell-focus", "Spell", "INV_Misc_Book_09",
                WorkspaceRailWidth, s, active: true,
                "Close the Spell Workshop (back to the rails)"))
            _creatorPanel = CreatorPanel.None;
        if (_skin is not null) _skin.Scale = savedScale;

        ImGui.SetWindowFontScale(CreatorTextScale);
        ImGui.TextColored(new Vector4(1f, 0.82f, 0.28f, 1f), "SPELL WORKSHOP");
        string status = CreatorPanelStatus("Spells");
        if (status.Length > 0) ImGui.TextDisabled(status);
        if (ImGui.SmallButton("deck"))
            _spellFocusSuppressed = true;                    // session only, no Save
        ImGui.SameLine();
        if (ImGui.SmallButton("dials"))
            _openPanelTuneId = _openPanelTuneId is null ? "Spells" : null;
        ImGui.Separator();
        ImGui.SetWindowFontScale(1f);   // children apply the scale themselves
    }

    // ── the width dial ───────────────────────────────────────────────────────

    /// <summary>A grab strip along the sidebar's left edge. Its OWN window rather
    /// than a widget inside the pane: the pane's full-width content children would
    /// steal the hover, and an inner strip would sit over their scrollbars.</summary>
    private void DrawSpellFocusSplitter(float paneW)
    {
        var io = ImGui.GetIO();
        const float grab = 7f;
        ImGui.SetNextWindowPos(new Vector2(io.DisplaySize.X - paneW - grab, 0f), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(grab, io.DisplaySize.Y), ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        // Without this the window is clamped to style.WindowMinSize (32px, and MORE
        // once ScaleAllSizes has run), so a "7px" grab strip would silently swallow
        // camera-look and click-select across 32px of world that looks like empty
        // ground. Same reason StanceBar/Pet push it for their small bars.
        ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, Vector2.Zero);
        if (ImGui.Begin("##spell-focus-splitter", ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoBackground))
        {
            ImGui.InvisibleButton("##grab", new Vector2(grab, io.DisplaySize.Y));
            if (ImGui.IsItemHovered() || ImGui.IsItemActive())
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEW);
                ImGui.GetWindowDrawList().AddRectFilled(ImGui.GetItemRectMin(),
                    ImGui.GetItemRectMax(), ImGui.ColorConvertFloat4ToU32(
                        new Vector4(1f, 0.82f, 0.28f, ImGui.IsItemActive() ? 0.8f : 0.4f)));
            }
            // Absolute mouse->fraction so there is no geometry feedback loop; write
            // live, save ONCE on release (Save() is a whole-file rewrite).
            if (ImGui.IsItemActive() && io.DisplaySize.X > 1f)
                Settings.Creator.SpellFocusFraction = SpellFocusLayoutLaw.FractionFromDragX(
                    io.DisplaySize.X, io.DisplaySize.Y,
                    Math.Clamp(Settings.Creator.UiScale, 0.6f, 2.5f), io.MousePos.X);
            if (ImGui.IsItemDeactivated()) SettingsFile?.Save();
        }
        ImGui.End();
        ImGui.PopStyleVar(2);
    }
}

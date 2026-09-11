using System.Numerics;
using ImGuiNET;
using MSUIClient.World.Spells;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// Creator void stage (shared_docs/SPELL_CREATOR_IDE.md §2.1)
//
// A sibling of the X-Ray layer switch: buildings, doodads, foliage, water and
// sky go off, the clear colour is black, and the ground is kept ONLY inside a
// disc around the acting body (radius Settings.Creator.StageRadius, a +-2 yd
// height band). Terrain and WMO floors both honour the disc through the
// uStage* uniforms; the outer quarter fades to black so the plinth reads as a
// plinth. The stage and X-Ray are mutually exclusive and neither persists.
//
// Also the home of the "Stage" panel section, which carries the grid and gizmo
// switches (GameLoop.Creator.Gizmos.cs draws them).
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class GameLoop
{
    private const float CreatorStageHalfHeight = 2f;
    private const float CreatorStageDefaultRadius = 6f;

    private bool _creatorStageActive;

    /// <summary>Renderer enable flags as they were when the stage switched on.</summary>
    private (bool Wmo, bool Doodads, bool Sky, bool Foliage, bool Liquid)? _creatorStageSaved;

    private void SetCreatorStageActive(bool on)
    {
        if (on == _creatorStageActive) return;
        if (on && _xrayActive) SetXrayActive(false);
        _creatorStageActive = on;

        if (on)
        {
            _creatorStageSaved = (
                _wmo?.Enabled ?? true,
                _doodads?.Enabled ?? true,
                _sky?.Enabled ?? true,
                _foliage?.Enabled ?? true,
                _liquid?.Enabled ?? true);
            return;
        }

        if (_creatorStageSaved is { } saved)
        {
            if (_wmo is not null) _wmo.Enabled = saved.Wmo;
            if (_doodads is not null) _doodads.Enabled = saved.Doodads;
            if (_sky is not null) _sky.Enabled = saved.Sky;
            if (_foliage is not null) _foliage.Enabled = saved.Foliage;
            if (_liquid is not null) _liquid.Enabled = saved.Liquid;
        }
        _creatorStageSaved = null;
        if (_terrain is not null)
        {
            _terrain.Stage = null;
            _terrain.Enabled = true;
        }
        if (_wmo is not null) _wmo.Stage = null;
    }

    /// <summary>
    /// Asserted every frame from Render, after ApplyAtmosphere (idempotent writes), so the
    /// atmosphere's fog and sky colour cannot re-light the void mid-inspection. X-Ray
    /// switching on underneath us wins: the stage stands down.
    /// </summary>
    private void ApplyCreatorStageLayers()
    {
        if (!_creatorStageActive) return;
        if (_xrayActive || !_creatorWorldRequested)
        {
            SetCreatorStageActive(false);
            return;
        }

        Vector3 centre = _controller?.Position ?? _window.Camera.Target;
        float radius = Math.Clamp(Settings.Creator.StageRadius, 2f, 30f);
        var disc = (centre, radius, CreatorStageHalfHeight);

        if (_terrain is not null)
        {
            _terrain.Enabled = true;
            _terrain.Stage = disc;
            _terrain.FogColor = Vector3.Zero;
        }
        if (_wmo is not null)
        {
            _wmo.Enabled = true;
            _wmo.Stage = disc;
            _wmo.FogColor = Vector3.Zero;
        }
        if (_doodads is not null) _doodads.Enabled = false;
        if (_sky is not null) _sky.Enabled = false;
        if (_foliage is not null) _foliage.Enabled = false;
        if (_liquid is not null) _liquid.Enabled = false;

        // The sky pass is off, so the window clear IS the background.
        _window.SkyColor = Vector3.Zero;
    }

    // ── panel section ────────────────────────────────────────────────────────

    private void DrawCreatorStageBody()
    {
        var settings = Settings.Creator;

        bool stage = _creatorStageActive;
        if (ImGui.Checkbox("Void stage (black world, ground disc)", ref stage))
            SetCreatorStageActive(stage);
        CreatorHelp("Everything but the ground around you goes black: buildings, props, " +
            "foliage, water and sky off, terrain and building floors kept only inside a disc " +
            "at your feet (2 yd up and down). Switching it off restores the world exactly. " +
            "Turning the stage on turns Collision X-Ray off, and vice versa.");
        if (_creatorStageActive)
        {
            float radius = settings.StageRadius;
            ImGui.SetNextItemWidth(CreatorControlWidth);
            if (ImGui.SliderFloat("Disc radius", ref radius, 2f, 30f, "%.0f yd"))
                settings.StageRadius = radius;
            if (ImGui.IsItemDeactivatedAfterEdit()) SettingsFile?.Save();
            if (CreatorResetKnob("stageradius"))
            {
                settings.StageRadius = CreatorStageDefaultRadius;
                SettingsFile?.Save();
            }
            CreatorHelp("How much ground stays around you. The outer quarter fades to black.");
        }

        ImGui.Spacing();
        ImGui.TextDisabled("REFERENCE GRID");
        bool grid = settings.SpellGrid;
        if (ImGui.Checkbox("Reference grid (cube lattice at your feet)", ref grid))
        {
            settings.SpellGrid = grid;
            SettingsFile?.Save();
        }
        CreatorHelp("A reference grid in your facing frame: by default a full cube lattice " +
            "around you (minor cells, a brighter cage every yard) with a clear pocket carved " +
            "out around your body so the model stays readable; or three planes through your " +
            "feet (floor, side, front). Facing axes: forward red, left green, up blue. " +
            "Depth-tested, so your model stands in front of it.");
        bool lattice = settings.GridLattice;
        if (ImGui.Checkbox("Full cube lattice (off = three planes)", ref lattice))
        {
            settings.GridLattice = lattice;
            SettingsFile?.Save();
        }
        CreatorHelp("The 3D cube matrix: every cell corner in all directions, forward/left " +
            "to the extent and up from the floor. Off draws only the floor, side and front planes.");
        float extent = settings.GridExtent;
        ImGui.SetNextItemWidth(CreatorControlWidth);
        if (ImGui.SliderFloat("Grid extent", ref extent, 1f, 8f, "%.0f yd")) settings.GridExtent = extent;
        if (ImGui.IsItemDeactivatedAfterEdit()) SettingsFile?.Save();
        if (CreatorResetKnob("gridextent"))
        {
            settings.GridExtent = 3f;
            SettingsFile?.Save();
        }
        CreatorHelp("How far the grid reaches from your feet, in yards, in every direction " +
            "(and up). A big extent with half-foot cells is a LOT of lines - dial it down " +
            "if the frame rate drops.");
        bool sixth = settings.GridMinorSixthYard;
        if (ImGui.Checkbox("Half-foot minor lines (1/6 yd)", ref sixth))
        {
            settings.GridMinorSixthYard = sixth;
            SettingsFile?.Save();
        }
        CreatorHelp("A finer grid: one minor line per half foot (a sixth of a yard) instead of " +
            "a quarter yard. Major lines stay every yard.");

        ImGui.Spacing();
        ImGui.TextDisabled("EMITTER GIZMOS");
        bool gizmos = settings.SpellGizmos;
        if (ImGui.Checkbox("Show emitter gizmos", ref gizmos))
        {
            settings.SpellGizmos = gizmos;
            SettingsFile?.Save();
        }
        CreatorHelp("One gizmo per live emitter of the selected spell, in the emitter's texture " +
            "identity colour (the swatch on its row):\n\n" +
            "Small cross - the emitter's ORIGIN this instant (its bone, in the effect, on the " +
            "caster's attachment).\n" +
            "Triad - the birth frame: WHITE arrow = the emission axis (which way particles go " +
            "at zero spread, sign of speed applied), red = the plane's length axis, green = " +
            "its width axis.\n" +
            "Rectangle / rings - the birth shape (plane area or sphere shell).\n" +
            "Thin line - back to the effect root, i.e. the caster attachment the effect hangs " +
            "from. An emitter behind you shows a line from behind you to your hand.\n\n" +
            "BOLD = the emitter whose row is open or hovered in the phase editor. DIM = an " +
            "emitter not emitting this instant (rate 0 - a gated burst between pours, or an " +
            "animated rate at its zero first key like Cleave's).");
        bool reach = settings.GizmoReach;
        if (ImGui.Checkbox("Reach rays (speed x lifespan, gravity arc)", ref reach))
        {
            settings.GizmoReach = reach;
            SettingsFile?.Save();
        }
        CreatorHelp("Where the first particles are when they die: rays along the edge of the " +
            "vertical/horizontal spread to t = lifespan, a loop across their tips, and the " +
            "mean ray drawn as the arc gravity bends it into. Start and end of the effect, " +
            "drawn. Drag is ignored.");
        bool through = settings.GizmoThroughWalls;
        if (ImGui.Checkbox("Draw through the character and walls", ref through))
        {
            settings.GizmoThroughWalls = through;
            SettingsFile?.Save();
        }
        CreatorHelp("Off: the grid and gizmos are depth-tested, so the model occludes them. " +
            "On: they draw over everything.");

        ImGui.TextDisabled($"{_spellGizmos?.LinesLastFrame ?? 0} lines, " +
                           $"{_gizmoLabels.Count} emitter{(_gizmoLabels.Count == 1 ? "" : "s")} " +
                           "in view (gizmos draw while the Spell Workshop is open).");
        if (_spellGizmos is null)
            ImGui.TextDisabled("Gizmo renderer unavailable - check the console for the shader error.");
    }
}

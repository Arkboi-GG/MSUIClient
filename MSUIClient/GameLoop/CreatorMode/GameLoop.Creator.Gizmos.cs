using System.Numerics;
using ImGuiNET;
using MSUIClient.Formats;
using MSUIClient.World.Spells;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// Creator grid + emitter gizmos (shared_docs/SPELL_CREATOR_IDE.md §2.3)
//
// The read-only half of the spatial editor: a reference grid around the acting
// body and one gizmo per live emitter of the selected spell, built by the pure
// SpellEmitterGizmoLaw from the particle system's live pool frames and drawn by
// SpellGizmoRenderer in the debug pass. Labels ride the ImGui background draw
// list (creator mode is a dev surface under the ImGui policy).
//
// The emitter whose panel row is open, or whose header is hovered, draws bold;
// that is the panel <-> world link until drag handles exist.
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class GameLoop
{
    /// <summary>Built in Program.cs init beside the collision debug renderers.</summary>
    private SpellGizmoRenderer? _spellGizmos;

    private readonly List<GizmoLine> _gizmoLines = new();
    private readonly List<(Vector3 World, string Text, uint Color, string ModelPath, int Emitter)> _gizmoLabels = new();

    /// <summary>Written by CreatorCategory for the header it just drew, read by the
    /// model editor to find the hovered emitter row.</summary>
    private bool _creatorCategoryHovered;

    /// <summary>The emitter whose row header is hovered this GUI pass (model path, index).</summary>
    private (string Path, int Index)? _creatorGizmoHover;

    private bool CreatorGizmosVisible =>
        _creatorWorldRequested && !_worldLoading &&
        _creatorPanel == CreatorPanel.Spells && _creatorSpell is not null;

    /// <summary>Debug pass, after the world and the particles so depth is populated.</summary>
    private void RenderCreatorGizmos()
    {
        _gizmoLines.Clear();
        _gizmoLabels.Clear();
        if (_spellGizmos is null || !CreatorGizmosVisible) return;

        var settings = Settings.Creator;
        if (settings.SpellGrid && _controller is not null)
        {
            float minor = settings.GridMinorSixthYard
                ? SpellEmitterGizmoLaw.SixthYardMinor
                : SpellEmitterGizmoLaw.DefaultMinorYards;
            float extent = Math.Clamp(settings.GridExtent, 1f, 8f);
            if (settings.GridLattice)
                // The character stands in a clear pocket: the controller's capsule plus a margin.
                SpellEmitterGizmoLaw.Lattice(_gizmoLines, _controller.Position, _controller.Yaw,
                    minor, SpellEmitterGizmoLaw.MajorYards, extent,
                    MathF.Max(_config.Movement.Radius, 0.45f) + 0.2f, _config.Movement.Height + 0.25f);
            else
                SpellEmitterGizmoLaw.Grid(_gizmoLines, _controller.Position, _controller.Yaw,
                    minor, SpellEmitterGizmoLaw.MajorYards, extent,
                    floor: true, side: true, front: true);
        }

        if (settings.SpellGizmos && _spellParticles is not null && _creatorSpell is { } doc)
        {
            foreach (SpellEmitterFrame frame in _spellParticles.EmitterFrames())
            {
                if (!TryResolveGizmoModel(doc, frame.Path, out CreatorModelDoc model)) continue;
                int slot = -1;
                foreach (var tex in model.Textures)
                    if (tex.ReferencedByEmitters.Contains(frame.EmitterIndex))
                    {
                        slot = tex.Index;
                        break;
                    }
                Vector4 color = slot >= 0 ? CreatorSlotColor(slot) : new Vector4(1f, 1f, 1f, 1f);
                bool highlighted = IsGizmoEmitterHighlighted(model, frame.EmitterIndex);
                SpellEmitterGizmoLaw.Emitter(_gizmoLines, frame, color, highlighted, settings.GizmoReach);
                _gizmoLabels.Add((frame.Origin, $"e{frame.EmitterIndex}",
                    ImGui.ColorConvertFloat4ToU32(color with { W = highlighted ? 1f : 0.7f }),
                    model.Path, frame.EmitterIndex));
            }
        }

        if (_gizmoLines.Count == 0) return;
        _spellGizmos.DepthTest = !settings.GizmoThroughWalls;
        _spellGizmos.Render(_window.Camera, _gizmoLines);
    }

    /// <summary>A pool path is "spell:{resolved model path}#{instance id}"; match it to the
    /// selected spell's model docs by resolved path so only THIS spell's emitters draw.</summary>
    private static bool TryResolveGizmoModel(CreatorSpellDoc doc, string poolPath,
        out CreatorModelDoc model)
    {
        model = null!;
        const string prefix = "spell:";
        if (!poolPath.StartsWith(prefix, StringComparison.Ordinal)) return false;
        int hash = poolPath.IndexOf('#');
        string path = hash > prefix.Length ? poolPath[prefix.Length..hash] : poolPath[prefix.Length..];
        foreach (CreatorModelDoc candidate in doc.Models.Values)
        {
            if (string.Equals(SpellVisualCatalog.ModelPath(candidate.Path), path,
                    StringComparison.OrdinalIgnoreCase))
            {
                model = candidate;
                return true;
            }
        }
        return false;
    }

    private bool IsGizmoEmitterHighlighted(CreatorModelDoc model, int emitterIndex)
    {
        // The IDE's selection and its pinned inspectors draw bold; the hovered row and
        // an open classic-layout category do too.
        if (_spellIdeSelection.IsEmitterOf(model.Path, emitterIndex)) return true;
        foreach (SpellIdeSelection pin in _spellIdePins)
            if (pin.IsEmitterOf(model.Path, emitterIndex)) return true;
        if (_creatorGizmoHover is { } hover &&
            string.Equals(hover.Path, model.Path, StringComparison.OrdinalIgnoreCase) &&
            hover.Index == emitterIndex)
            return true;
        return GetSectionOpen($"ws-{model.Path}-em{emitterIndex}", false);
    }

    /// <summary>GUI pass: the e&lt;index&gt; tags at the emitter origins, under every window.</summary>
    private void DrawCreatorGizmoLabels()
    {
        if (_gizmoLabels.Count == 0 || !CreatorGizmosVisible) return;
        ImDrawListPtr draw = ImGui.GetBackgroundDrawList();
        Vector2 display = ImGui.GetIO().DisplaySize;
        foreach (var (world, text, color, _, _) in _gizmoLabels)
        {
            if (!_window.Camera.TryWorldToScreen(world, display, out Vector2 pixel)) continue;
            Vector2 at = pixel + new Vector2(7f, -9f);
            draw.AddText(at + Vector2.One, 0xC0000000, text);
            draw.AddText(at, color, text);
        }
    }
}

using System.Numerics;
using ImGuiNET;
using MSUIClient.Formats;

namespace MSUIClient;

// ============================================================================
// The particle instrument (PLAN_14_PARTICLES.md §5, stage 1).
//
// Same rule as Program.DevTools.cs, Program.Hitch.cs, Program.LightProbe.cs,
// Program.Portals.cs and Program.Instances.cs: developer TOOLING. It reads what
// M2Reader parsed and what ParticleRenderer is doing with it, and prints both.
// The renderer itself is core and runs in every build; this panel only watches
// it, and its switches are the usual DevTools affordances.
//
// WHAT THIS PANEL IS FOR. §3 makes a set of claims about a binary layout that
// was DERIVED rather than looked up - and the number every reference gives for
// the emitter stride, 476, is wrong; it is 504. This panel is where those
// claims meet Nico's machine. If InstancePortal lists two ADD-blended plane
// emitters with the numbers in §3.2, the derivation held. If it lists garbage,
// the stride is wrong and §3 is the reference.
// ============================================================================
public sealed partial class GameLoop
{
    private string _particleFilter = "";
    private bool _particlesOnlyAnimated;

    private void DrawParticlesPanel()
    {
        if (!ImGui.CollapsingHeader("Particles (PLAN_14)")) return;

        if (_doodads is null)
        {
            ImGui.TextDisabled("no doodad renderer");
            return;
        }

        var models = _doodads.ModelsWithEmitters().ToList();
        int emitters = _doodads.EmitterCount;

        ImGui.Text($"{models.Count} loaded model(s) with emitters, {emitters} emitter(s) total");
        ImGui.TextDisabled($"of {_doodads.ModelCount} model(s) loaded   " +
                           "(18% of the archives' 15,214 M2s carry emitters)");
        if (_particles is not null)
        {
            ImGui.Separator();
            ImGui.Text($"LIVE: {_particles.LiveParticles:N0} particle(s) in " +
                       $"{_particles.ActivePools} pool(s), {_particles.DrawnLastFrame:N0} drawn");
            ImGui.TextDisabled($"simulate {_particles.SimulateMilliseconds:F2} ms   " +
                               $"draw {_particles.DrawMilliseconds:F2} ms");

            bool on = _particles.Enabled;
            if (ImGui.Checkbox("Draw particles", ref on)) _particles.Enabled = on;

            float density = _particles.DensityScale;
            ImGui.SetNextItemWidth(160f);
            if (ImGui.SliderFloat("Density", ref density, 0f, 2f, "%.2f"))
                _particles.DensityScale = density;

            float dist = _particles.SimulationDistance;
            ImGui.SetNextItemWidth(160f);
            if (ImGui.SliderFloat("Simulate within (yd)", ref dist, 10f, 400f, "%.0f"))
                _particles.SimulationDistance = dist;
            ImGui.Separator();
        }

        ImGui.SetNextItemWidth(160f);
        ImGui.InputText("filter##pfx", ref _particleFilter, 64u);
        ImGui.SameLine();
        ImGui.Checkbox("animated tracks only", ref _particlesOnlyAnimated);
        ImGui.SameLine();
        if (ImGui.Button("Dump to console")) DumpEmitters();

        ImGui.Separator();

        foreach (var (path, list) in models.OrderBy(m => m.Path, StringComparer.OrdinalIgnoreCase))
        {
            if (_particleFilter.Length > 0 &&
                path.IndexOf(_particleFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;

            bool anyAnimated = list.Any(e => e.AnyTrackAnimated);
            if (_particlesOnlyAnimated && !anyAnimated) continue;

            string leaf = Path.GetFileName(path);
            if (!ImGui.TreeNodeEx($"{leaf}   [{list.Count} emitter(s)]##pe_{path}",
                                  ImGuiTreeNodeFlags.SpanAvailWidth)) continue;

            ImGui.TextDisabled(path);

            for (int i = 0; i < list.Count; i++) DrawEmitter(i, list[i]);

            ImGui.TreePop();
        }
    }

    private void DrawEmitter(int index, M2ParticleEmitter e)
    {
        ImGui.Text($"[{index}] bone {e.Bone}  texture {e.Texture}  " +
                   $"{e.BlendName}  {e.TypeName}  {e.TextureRows}x{e.TextureCols} cell(s)");
        ImGui.TextDisabled($"     pos ({e.PosX:F2}, {e.PosY:F2}, {e.PosZ:F2})   " +
                           $"flags 0x{e.Flags:X8}   headOrTail {e.HeadOrTail}");

        // Negative emission speed is the whole character of a portal: the
        // particles travel TOWARD the emitter. Called out rather than left to
        // be read off a minus sign, because an implementation that clamps it
        // produces a fountain and the bug is hard to name afterwards (H4).
        if (e.EmissionSpeed < 0f)
            ImGui.TextColored(new Vector4(0.6f, 0.85f, 1f, 1f),
                $"     speed {e.EmissionSpeed:F3} - NEGATIVE, particles pull inward");
        else
            ImGui.Text($"     speed {e.EmissionSpeed:F3}");

        ImGui.SameLine();
        ImGui.Text($"  var {e.SpeedVariation:F3}   gravity {e.Gravity:F3}");

        ImGui.Text($"     lifespan {e.Lifespan:F3}s   rate {e.EmissionRate:F0}/s   " +
                   $"-> ~{e.SteadyStatePopulation:F0} live sprite(s)");
        ImGui.Text($"     range v {e.VerticalRange:F3} h {e.HorizontalRange:F3} rad   " +
                   $"area {e.EmissionAreaLength:F2} x {e.EmissionAreaWidth:F2}   z {e.ZSource:F2}");

        if (e.AnyTrackAnimated)
        {
            string[] names =
            {
                "speed", "speedVar", "vRange", "hRange", "gravity",
                "lifespan", "rate", "areaLen", "areaWid", "zSource",
            };
            var animated = new List<string>();
            for (int t = 0; t < e.TrackKeyCounts.Length && t < names.Length; t++)
                if (e.TrackKeyCounts[t] > 1) animated.Add($"{names[t]}({e.TrackKeyCounts[t]})");

            ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f),
                $"     ANIMATED tracks, static value shown: {string.Join(", ", animated)}");
        }
    }

    /// <summary>
    /// The whole set in one console block, to be diffed against PLAN_14 §3.2.
    /// That comparison is the test for stage 1.
    /// </summary>
    private void DumpEmitters()
    {
        if (_doodads is null) return;

        Console.WriteLine("[particles] model / emitter / bone tex blend type / speed var life rate");
        foreach (var (path, list) in _doodads.ModelsWithEmitters()
                                             .OrderBy(m => m.Path, StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine($"[particles] {Path.GetFileName(path)}  ({list.Count} emitter(s))");
            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                Console.WriteLine(
                    $"[particles]   [{i}] bone {e.Bone,3} tex {e.Texture,2} " +
                    $"{e.BlendName,-12} {e.TypeName,-7} {e.TextureRows}x{e.TextureCols}  " +
                    $"speed {e.EmissionSpeed,8:F3} var {e.SpeedVariation,6:F3} " +
                    $"life {e.Lifespan,6:F3} rate {e.EmissionRate,7:F1} " +
                    $"vRange {e.VerticalRange,6:F3} area {e.EmissionAreaLength,6:F3}");
            }
        }
    }
}

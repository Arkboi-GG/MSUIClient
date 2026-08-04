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

    /// <summary>
    /// Beyond-portal fill light. Real 1.12 shows a lit room through an instance
    /// portal; our interiors read as pitch black. Each frame we find the nearest
    /// instance portal, drop a soft point light a little way PAST it (into the
    /// room, along the camera->portal ray), and hand that world-space light to
    /// the WMO and doodad renderers. When no portal is near, radius goes to 0 and
    /// the light is off - exterior lighting is never touched. Knobs on the panel.
    /// </summary>
    private void UpdatePortalFillLight()
    {
        if (_particles is null) return;

        // Pre-declared so it is definitely assigned even when the && short-circuits
        // before TryGetNearestPortal runs (CS0165). Behaviour is unchanged: centre
        // is only read below when havePortal is true, i.e. after the call ran.
        Vector3 centre = default;
        bool havePortal =
            _particles.PortalLight &&
            _particles.PortalLightIntensity > 0f &&
            _particles.PortalLightRadius > 0f &&
            _particles.TryGetNearestPortal(
                _window.Camera.Position, 150f, out centre);

        if (!havePortal)
        {
            if (_wmo is not null) _wmo.PortalLightRadius = 0f;
            if (_doodads is not null) _doodads.PortalLightRadius = 0f;
            return;
        }

        var eye = _window.Camera.Position;
        var toPortal = centre - eye;
        var dir = toPortal.LengthSquared() > 1e-4f
            ? Vector3.Normalize(toPortal)
            : _window.Camera.Forward;
        var lightPos = centre + dir * _particles.PortalLightOffset;
        var colour = _particles.PortalLightRgb();

        if (_wmo is not null)
        {
            _wmo.PortalLightWorldPos = lightPos;
            _wmo.PortalLightColor = colour;
            _wmo.PortalLightRadius = _particles.PortalLightRadius;
        }
        if (_doodads is not null)
        {
            _doodads.PortalLightWorldPos = lightPos;
            _doodads.PortalLightColor = colour;
            _doodads.PortalLightRadius = _particles.PortalLightRadius;
        }
    }

    private void DrawParticlesPanel()
    {
        if (!ImGui.CollapsingHeader("Particles (PLAN_14)", ImGuiTreeNodeFlags.DefaultOpen)) return;

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
            if (ImGui.SliderFloat("Density", ref density, 0.25f, 1f, "%.2f"))
            {
                _particles.DensityScale = density;
                if (_spellParticles is not null) _spellParticles.DensityScale = density;
            }

            float spriteSize = _particles.SpriteSizeScale;
            ImGui.SetNextItemWidth(160f);
            if (ImGui.SliderFloat("Sprite size x", ref spriteSize, 0.2f, 3f, "%.2f"))
                _particles.SpriteSizeScale = spriteSize;
            ImGui.TextDisabled("   shrink to separate the converging specks (less overlap = less 'cloud')");

            float sharp = _particles.SpriteSharpness;
            ImGui.SetNextItemWidth(160f);
            if (ImGui.SliderFloat("Sprite sharpness (mip bias)", ref sharp, -4f, 2f, "%.2f"))
                _particles.SpriteSharpness = sharp;
            ImGui.TextDisabled("   negative = crisper specks; 0 = soft trilinear (vapour). Portal only.");

            float pHue = _particles.ParticleHueShift;
            ImGui.SetNextItemWidth(160f);
            if (ImGui.SliderFloat("Particle hue shift", ref pHue, -0.5f, 0.5f, "%.3f"))
                _particles.ParticleHueShift = pHue;
            float pSat = _particles.ParticleSaturation;
            ImGui.SetNextItemWidth(160f);
            if (ImGui.SliderFloat("Particle saturation", ref pSat, 0f, 2f, "%.2f"))
                _particles.ParticleSaturation = pSat;
            float pVal = _particles.ParticleValue;
            ImGui.SetNextItemWidth(160f);
            if (ImGui.SliderFloat("Particle brightness", ref pVal, 0f, 2f, "%.2f"))
                _particles.ParticleValue = pVal;

            // FFXGlow (whole-scene bloom) - the glaze. Owned by the game loop.
            if (_glow is not null)
            {
                bool glowOn = _glow.Enabled;
                if (ImGui.Checkbox("Glow (FFXGlow bloom)", ref glowOn)) _glow.Enabled = glowOn;
                float gain = _glow.Gain;
                ImGui.SetNextItemWidth(160f);
                if (ImGui.SliderFloat("Glow gain", ref gain, 0f, 1f, "%.2f"))
                    _glow.Gain = gain;
                ImGui.TextDisabled("   whole-scene; lower for dark interiors (~0.25)");
            }

            // Portal "looking glass" surface film - a flat plane, not the sprites.
            bool surf = _particles.PortalSurface;
            if (ImGui.Checkbox("Portal surface film", ref surf)) _particles.PortalSurface = surf;

            float surfA = _particles.PortalSurfaceAlpha;
            ImGui.SetNextItemWidth(160f);
            if (ImGui.SliderFloat("  surface opacity", ref surfA, 0f, 0.6f, "%.3f"))
                _particles.PortalSurfaceAlpha = surfA;

            float surfSize = _particles.PortalSurfaceSize;
            ImGui.SetNextItemWidth(160f);
            if (ImGui.SliderFloat("  surface reach x", ref surfSize, 0.5f, 2.5f, "%.2f"))
                _particles.PortalSurfaceSize = surfSize;

            float surfHue = _particles.PortalSurfaceHue;
            ImGui.SetNextItemWidth(160f);
            if (ImGui.SliderFloat("  film hue (0.58 blue -> 0.42 green)", ref surfHue, 0f, 1f, "%.3f"))
                _particles.PortalSurfaceHue = surfHue;
            float surfSat = _particles.PortalSurfaceSat;
            ImGui.SetNextItemWidth(160f);
            if (ImGui.SliderFloat("  film saturation", ref surfSat, 0f, 1f, "%.2f"))
                _particles.PortalSurfaceSat = surfSat;
            float surfVal = _particles.PortalSurfaceVal;
            ImGui.SetNextItemWidth(160f);
            if (ImGui.SliderFloat("  film brightness", ref surfVal, 0f, 2f, "%.2f"))
                _particles.PortalSurfaceVal = surfVal;

            float centreHole = _particles.PortalCentreHole;
            ImGui.SetNextItemWidth(160f);
            if (ImGui.SliderFloat("Portal see-through centre (yd)", ref centreHole, 0f, 6f, "%.2f"))
                _particles.PortalCentreHole = centreHole;

            float portalScale = _particles.PortalScale;
            ImGui.SetNextItemWidth(160f);
            if (ImGui.SliderFloat("Portal circle size x", ref portalScale, 0.25f, 4f, "%.2f"))
                _particles.PortalScale = portalScale;
            ImGui.TextDisabled("   scales the whole disc about its centre");

            int solo = _particles.SoloEmitter;
            ImGui.SetNextItemWidth(160f);
            if (ImGui.SliderInt("Solo emitter (-1=all)", ref solo, -1, 3))
                _particles.SoloEmitter = solo;
            ImGui.TextDisabled("   0/1 = one emitter; if one still shows 2 rings, portal is placed twice");
            if (ImGui.Button("Dump portal placements to console"))
                _doodads?.DumpEmitterPlacements("Portal");

            float spin = _particles.ModelSpinScale;
            ImGui.SetNextItemWidth(160f);
            if (ImGui.SliderFloat("Spin rate x", ref spin, 0.1f, 6f, "%.2f"))
                _particles.ModelSpinScale = spin;
            ImGui.TextDisabled("   model-space portal spin (1.0 = authored). The legacy world-space" +
                               " SpinRateScale did nothing here - this now drives the actual spin.");

            // Beyond-portal fill light - lifts the too-dark instance interior seen
            // through the portal WITHOUT touching exterior/daylight lighting.
            ImGui.Separator();
            bool plOn = _particles.PortalLight;
            if (ImGui.Checkbox("Beyond-portal fill light", ref plOn))
                _particles.PortalLight = plOn;

            float plInt = _particles.PortalLightIntensity;
            ImGui.SetNextItemWidth(160f);
            if (ImGui.SliderFloat("  fill intensity", ref plInt, 0f, 3f, "%.2f"))
                _particles.PortalLightIntensity = plInt;

            float plRad = _particles.PortalLightRadius;
            ImGui.SetNextItemWidth(160f);
            if (ImGui.SliderFloat("  fill radius (yd)", ref plRad, 2f, 120f, "%.0f"))
                _particles.PortalLightRadius = plRad;

            float plOff = _particles.PortalLightOffset;
            ImGui.SetNextItemWidth(160f);
            if (ImGui.SliderFloat("  reach past portal (yd)", ref plOff, -20f, 60f, "%.0f"))
                _particles.PortalLightOffset = plOff;

            float plHue = _particles.PortalLightHue;
            ImGui.SetNextItemWidth(160f);
            if (ImGui.SliderFloat("  light hue", ref plHue, 0f, 1f, "%.3f"))
                _particles.PortalLightHue = plHue;
            float plSat = _particles.PortalLightSat;
            ImGui.SetNextItemWidth(160f);
            if (ImGui.SliderFloat("  light saturation", ref plSat, 0f, 1f, "%.2f"))
                _particles.PortalLightSat = plSat;
            float plVal = _particles.PortalLightVal;
            ImGui.SetNextItemWidth(160f);
            if (ImGui.SliderFloat("  light brightness", ref plVal, 0f, 2f, "%.2f"))
                _particles.PortalLightVal = plVal;
            ImGui.TextDisabled("   active only near instance portals; exterior lighting untouched");
            ImGui.Separator();

            int arms = _particles.SpawnArms;
            ImGui.SetNextItemWidth(160f);
            if (ImGui.SliderInt("Spawn arms (clock face)", ref arms, 0, 48))
                _particles.SpawnArms = arms;
            ImGui.TextDisabled(arms > 0
                ? $"   {arms} evenly spaced origins, issued round-robin - {360f / arms:F0} deg apart"
                : "   continuous phase (random)");

            float jitter = _particles.SpawnPhaseJitter;
            ImGui.SetNextItemWidth(160f);
            if (ImGui.SliderFloat("Phase jitter (of one slot)", ref jitter, 0f, 1f, "%.2f"))
                _particles.SpawnPhaseJitter = jitter;

            float hole = _particles.CentreHoleYards;
            ImGui.SetNextItemWidth(160f);
            if (ImGui.SliderFloat("Centre hole (yd)", ref hole, 0f, 5f, "%.2f"))
                _particles.CentreHoleYards = hole;

            bool rev = _particles.ReverseConverging;
            if (ImGui.Checkbox("Reverse converging emitters (time)", ref rev))
                _particles.ReverseConverging = rev;

            bool ramp = _particles.ReverseRamp;
            if (ImGui.Checkbox("Density at the far end (flip ramp)", ref ramp))
                _particles.ReverseRamp = ramp;
            ImGui.TextDisabled("   both apply only where emissionSpeed is negative");

            float dist = _particles.SimulationDistance;
            ImGui.SetNextItemWidth(160f);
            if (ImGui.SliderFloat("Simulate within (yd)", ref dist, 10f, 400f, "%.0f"))
                _particles.SimulationDistance = dist;
            ImGui.Separator();
        }

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
                   $"area {e.EmissionAreaLength:F2} x {e.EmissionAreaWidth:F2}   zsrc {e.ZSource:F2} drag {e.Drag:F2}");
        ImGui.Text($"     ramp mid {e.MidPoint:F2}   scale {e.ScaleKeys[0]:F3} -> " +
                   $"{e.ScaleKeys[1]:F3} -> {e.ScaleKeys[2]:F3}");

        if (e.HasBoneSpin)
            ImGui.TextColored(new Vector4(0.6f, 1f, 0.7f, 1f),
                $"     BONE SPIN: {e.BoneRotationKeys.Length} key(s) over " +
                $"{e.SequenceEnd - e.SequenceStart} ms - this is what sweeps the disc");
        else
            ImGui.TextDisabled("     no bone spin - emitter is static");

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
                    $"vRange {e.VerticalRange,6:F3} area {e.EmissionAreaLength,6:F3}  " +
                    (e.HasBoneSpin
                        ? $"SPIN {e.BoneRotationKeys.Length}k/{e.SequenceEnd - e.SequenceStart}ms"
                        : "no-spin") +
                    $"  pos({e.PosX:F2},{e.PosY:F2},{e.PosZ:F2})");
            }
        }
    }
}

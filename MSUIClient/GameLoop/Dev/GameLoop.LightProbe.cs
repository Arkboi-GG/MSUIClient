using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine;
using MSUIClient.Formats;
using MSUIClient.World;

namespace MSUIClient;

// ============================================================================
// The light probe (PLAN_09_EXTERIOR_LIGHTING.md §6).
//
// Same rule as Program.DevTools.cs and Program.Hitch.cs: this is developer
// TOOLING. It reads core state and the authored DBC data; core never depends
// on it, and this file applies nothing.
//
// What it is FOR, stated plainly: until now the client could say what it was
// DRAWING but had no way to say what vanilla INTENDS at a spot. So "the sky and
// overall exterior colour seem off" was unanswerable - there was nothing to
// compare against, and the 2026-07-23 tuning pass in WorldAtmosphere had to be
// done by eye for exactly that reason.
//
// Two columns, `data` and `applied`, on one line each. That is the whole design.
// ============================================================================
public sealed partial class GameLoop
{
    private readonly ExteriorLighting _exteriorLight = new();
    private readonly DayNightCycle _dayNightCycle = new();
    private ExteriorLighting.Sample? _lightSample;

    private static readonly string[] _lightingModeLabels = ["MSUI Lighting", "1.12 Parity"];
    private static readonly string[] _timeSourceLabels = ["Server", "Fixed", "Cycle"];

    /// <summary>
    /// Time the probe reports at. Follows the world clock unless pinned, so the
    /// 24-hour curve can be inspected without waiting for it or disturbing the
    /// scene.
    /// </summary>
    private bool _probePinTime;
    private float _probeHours = 12f;

    // Cloud density override (PLAN_18 probe A/B). The value the slider edits; only
    // pushed to the sky renderer while the override checkbox is ticked.
    private float _cloudDensityOverride = 0.6f;

    // Skybox force override (PLAN_18 Phase 2 probe A/B). 0 = follow the zone; else
    // one of the six LightSkybox.dbc models, so a skybox can be seen anywhere.
    private int _skyboxForceIndex;
    private static readonly string[] _skyboxForceLabels =
        ["(follow zone)", "Stratholme", "PortalWorldLegion", "DeathClouds", "Stars", "CavernsOfTime", "DireMaul"];
    private static readonly string[] _skyboxForcePaths =
    [
        @"Environments\Stars\StratholmeSkybox.mdx",
        @"Environments\Stars\PortalWorldLegionSky.mdx",
        @"Environments\Stars\DeathClouds.mdx",
        @"Environments\Stars\Stars.mdx",
        @"Environments\Stars\CavernsOfTimeSky.mdx",
        @"Environments\Stars\DireMaulSkyBox.mdx",
    ];

    private static readonly string[] _conventionLabels =
        ExteriorLighting.Conventions.Select(c => c.Name).ToArray();

    private void InitLightProbe()
    {
        _exteriorLight.Load(_config.ClientDataPath);

        // The vanilla day/night cycle table (World\dnc.db) - colour ramps,
        // intensities and light directions. Only the 1.12 Parity lighting mode
        // consumes it - WorldAtmosphere ignores the delegate in Msui mode, and
        // a missing table degrades Parity to "no modulation", which is Msui's
        // behaviour plus the never-setting sun law.
        _dayNightCycle.Load(_config.ClientDataPath);
        if (_dayNightCycle.Ready)
            _atmosphere.ParityDaylightIntensity = _dayNightCycle.SunIntensityAt;
    }

    // ── The world clock (2026-08-12) ─────────────────────────────────────────
    //
    // THIS IS CORE, like UpdateExteriorLighting below: it decides what hour the
    // atmosphere lights, in every build. It lives beside the lighting resolve
    // because the two are one pipeline - clock, then resolve at that clock.

    /// <summary>
    /// Advance <see cref="WorldAtmosphere.TimeOfDayHours"/> from the configured
    /// source. Server follows <see cref="WorldClock"/> (server game time, local
    /// wall clock until one arrives); Cycle is the accelerated debug day/night;
    /// Fixed leaves the value alone - the settings slider owns it. A dev pin
    /// (HUD slider, vantage restore) freezes the clock without touching the
    /// persisted preference.
    /// </summary>
    private void UpdateWorldClock(float dt)
    {
        if (_devTimePin) return;
        switch (_timeSource)
        {
            case TimeSource.Server:
                _atmosphere.TimeOfDayHours = _worldClock.CurrentHours;
                break;
            case TimeSource.Cycle:
                _atmosphere.TimeOfDayHours += dt * _gameHoursPerMinute / 60f;
                break;
        }
    }

    /// <summary>
    /// Dev override from the HUD: set the hour, and freeze the clock there if a
    /// tracking source would otherwise overwrite it next frame. In Fixed mode
    /// this is just the slider writing the value it owns.
    /// </summary>
    private void PinWorldClockAt(float hours)
    {
        _atmosphere.TimeOfDayHours = hours;
        if (_timeSource != TimeSource.Fixed) _devTimePin = true;
    }

    /// <summary>One-line clock status shared by the HUD, probe and settings page.</summary>
    private string WorldClockDescription()
    {
        if (_devTimePin)
            return $"pinned at {_atmosphere.TimeOfDayHours:F2} h (dev override)";
        return _timeSource switch
        {
            TimeSource.Fixed => $"fixed at {_atmosphere.TimeOfDayHours:F2} h",
            TimeSource.Cycle => $"cycling at {_atmosphere.TimeOfDayHours:F2} h " +
                                $"(x{_gameHoursPerMinute:F1} game h/min)",
            _ => _worldClock.HasServerTime
                ? $"server game time {_atmosphere.TimeOfDayHours:F2} h " +
                  $"(timescale {_worldClock.Timescale:F5})"
                : $"local clock {_atmosphere.TimeOfDayHours:F2} h (no server time yet)",
        };
    }

    /// <summary>
    /// Try every candidate dbc->world mapping against the player's position and
    /// print what each would produce. The one that yields containment is the
    /// convention; there is no judgement call in reading this.
    /// </summary>
    private void PrintConventionScores()
    {
        var p = _controller?.Position ?? Vector3.Zero;
        Console.WriteLine($"[light] coordinate convention scores at " +
                          $"({p.X:F0},{p.Y:F0},{p.Z:F0}) on map {_config.Start.Map}");
        Console.WriteLine($"[light]   {_exteriorLight.DescribeZoneExtent((uint)_config.Start.Map)}");

        foreach (var (name, containing, nearest, nearestId) in
                 _exteriorLight.ScoreConventions((uint)_config.Start.Map, p))
            Console.WriteLine($"[light]   {name,-34} containing {containing,3}   " +
                              $"nearest {nearest,9:F0} yd (light {nearestId})");

        Console.WriteLine("[light]   the convention with a non-zero 'containing' is the right one");
    }

    /// <summary>
    /// Refresh the resolved sample once per frame. Cheap - a handful of band
    /// lookups and lerps - but not free, so it is skipped when dev tooling is
    /// off, and it never runs inside the render pass.
    /// </summary>
    /// <summary>
    /// Resolve the authored exterior lighting for where the player is and what
    /// the world clock says, and hand it to the atmosphere.
    ///
    /// THIS IS CORE. IT RUNS IN EVERY BUILD.
    ///   It used to be the front half of UpdateLightProbe, which returned early
    ///   on `!_config.DevTools` - and it held the ONLY call to SetAuthored. So a
    ///   DevTools-off build silently reverted to the hand-invented constants
    ///   that SYSTEM_EXTERIOR_LIGHTING.md replaced with data, and nothing said
    ///   so. A whole shipped system was reachable only from developer tooling.
    ///
    ///   The call site's comment made it worse by asserting the opposite -
    ///   "Read-only: it feeds the probe panel and nothing else" - which is how a
    ///   seam violation survives a reading. FOUNDATION_PLAN section 12: the
    ///   DevTools flag may gate what you can SEE, never what the renderer does.
    ///
    ///   The settings modal is what made this load-bearing: its Lighting page
    ///   offers "Use authored lighting data (Light.dbc)" in exactly the build
    ///   where the resolve was not running.
    ///
    /// ALWAYS THE WORLD CLOCK, NEVER THE PINNED PROBE TIME.
    ///   Scrubbing the probe to inspect a curve must not relight the scene. The
    ///   probe's own (possibly pinned) sample is taken separately below, and
    ///   only when there is a panel to show it in.
    /// </summary>
    private string _lastLightState = "";

    private void UpdateExteriorLighting()
    {
        if (!_exteriorLight.Ready) return;

        var p = _controller?.Position ?? Vector3.Zero;

        // Settle the dbc->world mapping on the first frame with a real position.
        // Idempotent after that; the HUD can force a re-detect from elsewhere.
        _exteriorLight.DetectConvention((uint)_config.Start.Map, p);

        var applied = _exteriorLight.Resolve(
            (uint)_config.Start.Map, p, _atmosphere.TimeOfDayHours,
            _weatherVisual.StormBlend);

        // WHAT THE SCENE IS ACTUALLY LIT BY, printed when it changes. "It went dark
        // and I changed nothing" is otherwise unanswerable without a build to
        // compare against: this says whether the hour moved, the mode moved, the
        // strengths moved, or the authored bands themselves resolved differently.
        // Rate-limited to a real change, so it is a line at a transition, not spam.
        string lightState = $"{_atmosphere.TimeOfDayHours:F2}h mode={_atmosphere.Mode} " +
            $"src={_timeSource}{(_devTimePin ? "+pinned" : "")} " +
            $"sun={Settings.Lighting.SunStrength:F2} amb={Settings.Lighting.AmbientStrength:F2} " +
            $"authored={_atmosphere.UseAuthoredData} data={applied is { HasData: true }} " +
            $"map={_config.Start.Map}";
        if (lightState != _lastLightState)
        {
            _lastLightState = lightState;
            Console.WriteLine($"[light] {lightState}");
        }

        // WorldAtmosphere.UseAuthoredData is the switch that decides whether the
        // renderer consumes this. That switch is a SETTING, on the Lighting page;
        // it is not the DevTools flag and must never become it again.
        if (applied is { HasData: true })
        {
            _atmosphere.SetAuthored(
                applied.Ambient, applied.Diffuse, applied.FogColor,
                applied.SkyTop, applied.SkyMiddle, applied.SkyBand1,
                applied.SkyBand2, applied.SkySmog,
                applied.FogStart, applied.FogEnd);

            // PLAN_18. The cloud palette + density are bands like any other and are
            // already blended across every contributing zone by Resolve. The sky
            // pass owns the CloudField kernel that turns them into the drawn layer.
            _atmosphere.SetAuthoredClouds(
                applied.CloudSunGlow, applied.CloudSlope, applied.CloudBase,
                applied.CloudDensity);

            // PLAN_12. The four water COLOURS are bands like any other and are
            // already blended across every contributing zone by Resolve.
            //
            // The four ALPHAS are not. They live on LightParams, which is a row
            // and not a band table, so there is nothing to interpolate and the
            // NEAREST zone's row is used - Contributors is ordered nearest-last,
            // which is the same row the probe panel calls dominant. That is a
            // real approximation at a zone boundary and it is written down here
            // rather than discovered later from a seam in a lake.
            var dominant = applied.Contributors.Count > 0
                ? _exteriorLight.Params(applied.Contributors[^1].ParamsId)
                : null;

            // PLAN_18 Phase 2: the zone skybox is the dominant params' lightSkyboxID.
            // The render step resolves it to a model path and draws it.
            _activeSkyboxId = dominant?.SkyboxId ?? 0;

            // FFXGlow's sole authored input is the active LightParams row's glow weight.
            // Keep the configured value only as the no-data fallback.
            if (_glow is not null && _atmosphere.UseAuthoredData && dominant is not null)
                _glow.Gain = dominant.Glow;

            _atmosphere.SetAuthoredWater(
                applied.Colors[LightIntBandTable.OceanCloseBand],
                applied.Colors[LightIntBandTable.OceanFarBand],
                applied.Colors[LightIntBandTable.RiverCloseBand],
                applied.Colors[LightIntBandTable.RiverFarBand],
                dominant?.OceanShallowAlpha ?? 0f, dominant?.OceanDeepAlpha ?? 0f,
                dominant?.WaterShallowAlpha ?? 0f, dominant?.WaterDeepAlpha ?? 0f);
        }

        // ── Everything below is the PANEL's sample, and is tooling ───────────
        //
        // _lightSample feeds DrawLightProbePanel and PrintLightProbe and nothing
        // else, so it is the half that legitimately belongs behind the flag.
        if (!_config.DevTools) { _lightSample = null; return; }

        // Pinned, this is a SECOND resolve at the probe's time - deliberately
        // not the one that was applied. Unpinned the two are the same answer and
        // it is reused rather than resolved twice, which the old code did.
        _lightSample = _probePinTime
            ? _exteriorLight.Resolve((uint)_config.Start.Map, p, _probeHours)
            : applied;
    }

    private void DrawLightProbePanel()
    {
        if (!ImGui.CollapsingHeader("Light probe - what the DBCs say (PLAN_09)")) return;

        if (!_exteriorLight.Ready)
        {
            ImGui.TextDisabled($"unavailable: {_exteriorLight.Status}");
            ImGui.TextWrapped(
                "Exterior lighting is running on the hand-tuned constants in " +
                "WorldAtmosphere. That is the state PLAN_09 exists to end.");
            return;
        }

        ImGui.TextDisabled(_exteriorLight.Status);

        bool pin = _probePinTime;
        if (ImGui.Checkbox("Pin probe time", ref pin)) _probePinTime = pin;
        if (_probePinTime)
        {
            ImGui.SameLine();
            float h = _probeHours;
            if (ImGui.SliderFloat("##probehours", ref h, 0f, 24f, "%.2f h")) _probeHours = h;
        }
        else
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"following world clock: {WorldClockDescription()}");
        }

        // The channel-order check, made one click instead of an argument. The
        // schema pages do not agree on whether the packed colour is RGB or BGR,
        // and it cannot be settled by reading - but the sky top at noon MUST be
        // strongly blue. If it reads red-dominant, this is the wrong way round.
        bool swap = LightIntBandTable.SwapRedBlue;
        if (ImGui.Checkbox("Swap R/B (sky top at noon must be BLUE)", ref swap))
            LightIntBandTable.SwapRedBlue = swap;

        // Coordinate convention, unresolved and therefore a switch. The score
        // table below picks it: exactly one candidate should put the player
        // inside a zone, and the rest miss by tens of thousands of yards.
        int convention = _exteriorLight.ConventionIndex;
        if (ImGui.Combo("Coord convention", ref convention,
                        _conventionLabels, _conventionLabels.Length))
            _exteriorLight.ConventionIndex = convention;

        ImGui.TextDisabled($"  detected: {_exteriorLight.ConventionReport}");
        if (ImGui.Button("Re-detect from here"))
            _exteriorLight.DetectConvention((uint)_config.Start.Map,
                _controller?.Position ?? Vector3.Zero, force: true);
        ImGui.SameLine();
        if (ImGui.Button("Score all conventions")) PrintConventionScores();

        var sample = _lightSample;
        if (sample is null || !sample.HasData)
        {
            ImGui.TextDisabled("no light zone resolved at this position");
            return;
        }

        ImGui.Separator();
        ImGui.Text("Contributing zones (nearest applies last)");
        foreach (var c in sample.Contributors)
        {
            if (c.IsDefault)
            {
                ImGui.Text($"  light {c.LightId,5}  params {c.ParamsId,5}   MAP DEFAULT   weight 1.00");
                continue;
            }
            ImGui.Text($"  light {c.LightId,5}  params {c.ParamsId,5}   " +
                       $"{c.DistanceYards,7:F0} yd   falloff {c.FalloffStart:F0}..{c.FalloffEnd:F0}   " +
                       $"weight {c.Weight:F2}");
        }

        // §7 step 1: only the map default inside a named zone is suspicious.
        // But "no zone reaches us" and "our position is in the wrong coordinate
        // space" look identical from a list that hides zero-weight zones, so the
        // nearest few are shown unconditionally with their real distances. If
        // the closest named zone is tens of thousands of yards away, the
        // positions are not in our world space and no amount of falloff tuning
        // will help.
        if (sample.Contributors.Count == 1 && sample.Contributors[0].IsDefault)
        {
            ImGui.TextDisabled("  only the map default applies here");
            var p = _controller?.Position ?? Vector3.Zero;
            ImGui.TextDisabled($"  zone extent on this map: " +
                               $"{_exteriorLight.DescribeZoneExtent((uint)_config.Start.Map)}");
            ImGui.TextDisabled($"  player at ({p.X:F0},{p.Y:F0},{p.Z:F0}) - if that is outside the " +
                               "extent above, the coordinate convention is wrong");
            foreach (var (zone, distance) in _exteriorLight.NearestZones(
                         (uint)_config.Start.Map, p, 5))
                ImGui.TextDisabled($"    nearest: light {zone.Id,5} at " +
                                   $"({zone.Position.X:F0},{zone.Position.Y:F0},{zone.Position.Z:F0})  " +
                                   $"{distance:F0} yd  reach {zone.FalloffEnd:F0} yd");
        }

        ImGui.Separator();
        ImGui.Text("Colours (LightIntBand)");
        // The last contributor is the nearest zone, which the blend applied
        // last and which therefore dominates. "Is this band authored" is asked
        // of that one, since it is the one whose absence would show.
        uint dominantParams = sample.Contributors[^1].ParamsId;
        for (int b = 0; b < LightIntBandTable.BandsPerParams; b++)
        {
            var c = sample.Colors[b];
            bool authored = _exteriorLight.ColorBandAuthored(dominantParams, b);
            var col = new Vector4(c.X, c.Y, c.Z, 1f);

            ImGui.ColorButton($"##band{b}", col,
                ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoPicker,
                new Vector2(16, 16));
            ImGui.SameLine();
            ImGui.Text($"{b,2} {LightIntBandTable.BandNames[b],-18} " +
                       $"{c.X:F3} {c.Y:F3} {c.Z:F3}" + (authored ? "" : "   (unauthored)"));
        }

        ImGui.Separator();
        ImGui.Text("Scalars (LightFloatBand)");
        for (int b = 0; b < LightFloatBandTable.BandsPerParams; b++)
        {
            bool authored = _exteriorLight.FloatBandAuthored(dominantParams, b);
            string unit = b == LightFloatBandTable.FogEndBand ? " yd" : "";
            ImGui.Text($"  {b} {LightFloatBandTable.BandNames[b],-24} " +
                       $"{sample.Floats[b],10:F3}{unit}" + (authored ? "" : "   (unauthored)"));
        }

        // ── The comparison this whole panel exists for ──────────────────────
        ImGui.Separator();
        ImGui.Text("data  vs  applied");

        // The MODE is the setting (Video Options -> Lighting and sky); this
        // combo is the same value through the same path, so the two surfaces
        // cannot disagree. ApplyLightingModeDefaults pushes the mode's
        // recommended doorway spill exactly like the settings page does.
        int lightingMode = (int)Settings.Lighting.Mode;
        ImGui.SetNextItemWidth(160f);
        if (ImGui.Combo("Lighting mode", ref lightingMode,
                        _lightingModeLabels, _lightingModeLabels.Length))
        {
            Settings.Lighting.ApplyLightingModeDefaults((Engine.LightingMode)lightingMode);
            ApplySettings(Settings);
        }
        ImGui.TextDisabled(Settings.Lighting.Mode == Engine.LightingMode.Parity112
            ? $"  parity: raw bands x dnc.db intensity, never-setting sun " +
              $"({(_dayNightCycle.Ready ? $"{_dayNightCycle.SunIntensityAt(_atmosphere.TimeOfDayHours):F2} now" : "dnc NOT LOADED")})"
            : "  msui: authored colours applied raw (pre-v6 look)");

        // Transient dev A/B, deliberately NOT persisted since v6: off routes
        // everything to the hand-tuned fallback constants for comparison.
        bool useAuthored = _atmosphere.UseAuthoredData;
        if (ImGui.Checkbox("Use authored lighting (dev A/B, not saved)", ref useAuthored))
            _atmosphere.UseAuthoredData = useAuthored;
        ImGui.TextDisabled(useAuthored
            ? "  deltas should read 0.000 in MSUI mode at strength 1.0"
            : "  running on the hand-tuned constants; deltas show how far apart they are");

        if (_sky is not null)
        {
            bool skyOn = _sky.Enabled;
            if (ImGui.Checkbox("Draw sky gradient", ref skyOn)) _sky.Enabled = skyOn;

            // Only the COLOURS are authored. Where the bands sit is ours, so
            // these are sliders and are labelled as such.
            float m = _sky.StopMiddle, b1 = _sky.StopBand1, b2 = _sky.StopBand2;
            if (ImGui.SliderFloat("stop: middle", ref m, 0.05f, 0.99f)) _sky.StopMiddle = m;
            if (ImGui.SliderFloat("stop: band 1", ref b1, 0.02f, 0.9f)) _sky.StopBand1 = b1;
            if (ImGui.SliderFloat("stop: band 2", ref b2, 0.001f, 0.5f)) _sky.StopBand2 = b2;
            ImGui.TextDisabled("  band heights are NOT in the data - these are ours");

            // Clouds (PLAN_18). The palette + density are authored; the coverage
            // noise, the sun glow and the projection are the CloudField kernel's.
            ImGui.Separator();
            bool cloudsOn = _sky.CloudsEnabled;
            if (ImGui.Checkbox("Draw clouds", ref cloudsOn)) _sky.CloudsEnabled = cloudsOn;

            bool overrideDensity = _sky.CloudDensityOverride is not null;
            if (ImGui.Checkbox("override cloud density", ref overrideDensity))
                _sky.CloudDensityOverride = overrideDensity ? _cloudDensityOverride : null;
            if (overrideDensity)
            {
                if (ImGui.SliderFloat("cloud density C", ref _cloudDensityOverride, 0f, 1f))
                    _sky.CloudDensityOverride = _cloudDensityOverride;
            }
            ImGui.TextDisabled($"  authored C {_atmosphere.CloudDensity:F3}   " +
                               $"clouds ready: {(_atmosphere.AuthoredCloudsReady ? "yes" : "no")}");
        }

        // Skybox model (PLAN_18 Phase 2). Outdoor zones rarely author one - the force
        // dropdown lets a skybox be inspected anywhere.
        if (_skybox is not null)
        {
            ImGui.Separator();
            bool skyboxOn = _skybox.Enabled;
            if (ImGui.Checkbox("Draw skybox model", ref skyboxOn)) _skybox.Enabled = skyboxOn;
            if (ImGui.Combo("force skybox", ref _skyboxForceIndex, _skyboxForceLabels, _skyboxForceLabels.Length))
                _skybox.ForceModelPath = _skyboxForceIndex <= 0 ? null : _skyboxForcePaths[_skyboxForceIndex - 1];
            ImGui.TextDisabled($"  zone skybox id {_activeSkyboxId}   loaded: " +
                               $"{(_skybox.LoadedPath is { } sp ? Path.GetFileName(sp) : "none")}");
        }

        Row("ambient", sample.Ambient, _atmosphere.AmbientColor);
        Row("diffuse/sun", sample.Diffuse, _atmosphere.SunColor);
        Row("fog colour", sample.FogColor, _atmosphere.FogColor);
        Row("sky (top band)", sample.SkyTop, _atmosphere.SkyColor);

        ImGui.Text($"  fog start   data {sample.FogStart,8:F0} yd   " +
                   $"applied {_atmosphere.FogStart,8:F0} yd   " +
                   $"delta {_atmosphere.FogStart - sample.FogStart,8:F0}");
        ImGui.Text($"  fog end     data {sample.FogEnd,8:F0} yd   " +
                   $"applied {_atmosphere.FogEnd,8:F0} yd   " +
                   $"delta {_atmosphere.FogEnd - sample.FogEnd,8:F0}");

        // Fog end feeds VisibilityDistance, which feeds doodad draw distance,
        // which feeds the 727 yd residency radius. PLAN_09 D7: adopting the
        // authored value can move streaming cost, so the size of the change is
        // worth seeing before it is made, not after.
        float ratio = _atmosphere.FogEnd > 1f ? sample.FogEnd / _atmosphere.FogEnd : 0f;
        if (ratio > 1.05f)
            ImGui.TextDisabled($"  authored fog end is {ratio:F2}x ours - adopting it grows draw " +
                               "distance and streaming cost (PLAN_09 D7, SYSTEM_STREAMING §4)");

        ImGui.Separator();
        var lp = _exteriorLight.Params(dominantParams);
        if (lp is not null)
        {
            ImGui.Text($"LightParams {lp.Id}: skybox {lp.SkyboxId}  glow {lp.Glow:F2}  " +
                       $"highlightSky {(lp.HighlightSky ? "yes" : "no")}");
            ImGui.TextDisabled($"  water alpha {lp.WaterShallowAlpha:F2}/{lp.WaterDeepAlpha:F2}   " +
                               $"ocean {lp.OceanShallowAlpha:F2}/{lp.OceanDeepAlpha:F2}");
        }

        // PLAN_09 D9: these are the authored answers for colours SYSTEM_WATER.md
        // currently invents. Surfaced, deliberately not applied.
        ImGui.TextDisabled($"  ocean close {Fmt(sample.Colors[13])}  far {Fmt(sample.Colors[14])}");
        ImGui.TextDisabled($"  river close {Fmt(sample.Colors[15])}  far {Fmt(sample.Colors[16])}");

        if (ImGui.Button("Print probe to console")) PrintLightProbe();

        static string Fmt(Vector3 v) => $"({v.X:F2},{v.Y:F2},{v.Z:F2})";

        static void Row(string label, Vector3 data, Vector3 applied)
        {
            var d = data - applied;
            ImGui.ColorButton($"##d{label}", new Vector4(data.X, data.Y, data.Z, 1f),
                ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoPicker, new Vector2(16, 16));
            ImGui.SameLine();
            ImGui.ColorButton($"##a{label}", new Vector4(applied.X, applied.Y, applied.Z, 1f),
                ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoPicker, new Vector2(16, 16));
            ImGui.SameLine();
            ImGui.Text($"{label,-14} data {data.X:F3} {data.Y:F3} {data.Z:F3}   " +
                       $"applied {applied.X:F3} {applied.Y:F3} {applied.Z:F3}   " +
                       $"delta {d.X:+0.000;-0.000} {d.Y:+0.000;-0.000} {d.Z:+0.000;-0.000}");
        }
    }

    /// <summary>
    /// Dump the probe to the console, so a reading can be pasted into a plan or
    /// a commit message rather than screenshotted. Also lands in the hitch
    /// recorder's event ring via the console tee.
    /// </summary>
    private void PrintLightProbe()
    {
        var sample = _lightSample;
        if (sample is null || !sample.HasData) { Console.WriteLine("[light] no sample"); return; }

        var p = _controller?.Position ?? Vector3.Zero;
        float hours = _probePinTime ? _probeHours : _atmosphere.TimeOfDayHours;
        Console.WriteLine($"[light] probe at ({p.X:F0},{p.Y:F0},{p.Z:F0}) map {_config.Start.Map} " +
                          $"at {hours:F2} h");

        foreach (var c in sample.Contributors)
            Console.WriteLine($"[light]   light {c.LightId} params {c.ParamsId} " +
                              (c.IsDefault
                                  ? "MAP DEFAULT weight 1.00"
                                  : $"{c.DistanceYards:F0} yd falloff {c.FalloffStart:F0}..{c.FalloffEnd:F0} " +
                                    $"weight {c.Weight:F2}"));

        for (int b = 0; b < LightIntBandTable.BandsPerParams; b++)
        {
            var c = sample.Colors[b];
            Console.WriteLine($"[light]   {b,2} {LightIntBandTable.BandNames[b],-18} " +
                              $"{c.X:F3} {c.Y:F3} {c.Z:F3}");
        }
        for (int b = 0; b < LightFloatBandTable.BandsPerParams; b++)
            Console.WriteLine($"[light]   f{b} {LightFloatBandTable.BandNames[b],-24} {sample.Floats[b]:F3}");

        Console.WriteLine($"[light]   fog {sample.FogStart:F0}..{sample.FogEnd:F0} yd (data) vs " +
                          $"{_atmosphere.FogStart:F0}..{_atmosphere.FogEnd:F0} (applied)");

        // The raw keys of the two bands that matter most, so the interpolation
        // can be checked against the authored values rather than trusted. A
        // sampled colour that does not sit between two neighbouring keys is a
        // sampling bug; one that does is the data.
        uint dominant = sample.Contributors[^1].ParamsId;
        Console.WriteLine($"[light]   keys ambient : {_exteriorLight.DescribeColorBand(dominant, 1)}");
        Console.WriteLine($"[light]   keys sky top : {_exteriorLight.DescribeColorBand(dominant, 2)}");

        if (sample.Contributors.Count == 1 && sample.Contributors[0].IsDefault)
        {
            Console.WriteLine($"[light]   ONLY THE MAP DEFAULT APPLIES HERE");
            Console.WriteLine($"[light]   zone extent: {_exteriorLight.DescribeZoneExtent((uint)_config.Start.Map)}");
            foreach (var (zone, distance) in _exteriorLight.NearestZones((uint)_config.Start.Map, p, 5))
                Console.WriteLine($"[light]     nearest light {zone.Id} at " +
                                  $"({zone.Position.X:F0},{zone.Position.Y:F0},{zone.Position.Z:F0}) " +
                                  $"{distance:F0} yd, reach {zone.FalloffEnd:F0} yd");
        }
    }
}

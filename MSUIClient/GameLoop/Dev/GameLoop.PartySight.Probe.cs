using System.Numerics;
using MSUIClient.Engine;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// Party sight probe: a scripted offline proof of World/PartySight.cs in the REAL
// client. Activated by MSUI_PARTYSIGHT_PROBE=<map>,<x>,<y>,<z> (default: the
// Burning Blade Coven cave in the Valley of Trials, where the owner reported the
// hillside hiding the cave floor his character could see). The client boots the
// creator world at that spot, raises the Command View, parks the rig over the
// character, and dumps two screenshots: party sight ON, then OFF, so the
// difference is the feature. Console lines carry PASS/FAIL per claim plus the
// pass timings. No-ops unless the env var is set.
// ─────────────────────────────────────────────────────────────────────────────

public sealed partial class GameLoop
{
    private static readonly string? PartySightProbeSpec =
        Environment.GetEnvironmentVariable("MSUI_PARTYSIGHT_PROBE");

    private int _partySightProbeStage;
    private double _partySightProbeAt;
    private int _partySightProbeFailures;
    private Vector3 _partySightProbeSpot;
    private int _partySightProbeVantage;
    private bool _partySightProbeParkPending;

    /// <summary>Camera placements, all looking at the spot: the owner's own 31-degree side views
    /// (from the valley, then from the flank) and the top-down check. Yaw in WoW radians;
    /// the valley lies at yaw ~pi from the Burning Blade Coven, so the camera sits at
    /// spot - forward(yaw) * distance and looks along yaw.</summary>
    private static readonly (string Name, float PitchDeg, float Yaw, float Distance)[] PartySightProbeVantages =
    [
        ("valley31", 31f, -0.15f, 32f),
        ("flank31", 31f, 1.42f, 32f),
        ("top62", 62f, 0f, 15f),
    ];

    private void PartySightProbePark(int index)
    {
        if (_controller is null) return;
        var v = PartySightProbeVantages[index];
        // Park the rig by teleport, not CommanderFlyTo: the cave mouth is a terrain hole, so
        // a height sample there is null and the fly-to parks at 500 yd.
        float pitch = v.PitchDeg * MathF.PI / 180f;
        Vector3 rig = _partySightProbeSpot + new Vector3(
            -MathF.Cos(v.Yaw) * v.Distance, -MathF.Sin(v.Yaw) * v.Distance, v.Distance * MathF.Tan(pitch));
        Settings.Controls.CommandViewPitchDegrees = v.PitchDeg;
        _controller.Teleport(rig.X, rig.Y, rig.Z);
        _controller.Yaw = v.Yaw;
        _window.Camera.Yaw = v.Yaw;
        _window.Camera.OrbitYaw = 0f;
        _window.Camera.Target = _controller.Position;
        Console.WriteLine($"[partysight-probe] vantage {index} {v.Name}: rig=({rig.X:0.0},{rig.Y:0.0},{rig.Z:0.0}) " +
            $"pitch={v.PitchDeg} yaw={v.Yaw:0.00}");
    }

    private void PartySightProbeCheck(string name, bool ok, string detail = "")
    {
        Console.WriteLine($"[partysight-probe] {(ok ? "PASS" : "FAIL")}  {name}" +
                          (detail.Length > 0 ? $"  [{detail}]" : ""));
        if (!ok) _partySightProbeFailures++;
    }

    private void UpdatePartySightProbe()
    {
        if (PartySightProbeSpec is null || _partySightProbeStage >= 99) return;
        double now = NowSeconds();

        switch (_partySightProbeStage)
        {
            case 0:
            {
                if (_gl is null || _worldLoadStarted) return;
                if (_partySightProbeAt == 0) { _partySightProbeAt = now; return; }
                if (now - _partySightProbeAt < 1.0) return;
                int map = 1; float x = -171.3f, y = -4362.5f, z = 68.1f;
                string[] parts = PartySightProbeSpec.Split(',', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 4 &&
                    int.TryParse(parts[0], out int m) &&
                    float.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float px) &&
                    float.TryParse(parts[2], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float py) &&
                    float.TryParse(parts[3], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float pz))
                { map = m; x = px; y = py; z = pz; }
                _partySightProbeSpot = new Vector3(x, y, z);
                _config.DevTools = true;   // screenshots ride the gameplay-dump machinery
                Settings.Creator.LocMap = map;
                Settings.Creator.LocMapName = map switch { 0 => "Azeroth", 1 => "Kalimdor", _ => Settings.Creator.LocMapName };
                Settings.Creator.LocX = x; Settings.Creator.LocY = y; Settings.Creator.LocZ = z;
                Settings.Creator.LocYaw = 0f;
                // MSUI_PARTYSIGHT_PROBE_MODE=sight exercises the experimental party sight; the
                // default exercises the standard roof cut with its proximity fallback.
                bool sightMode = string.Equals(Environment.GetEnvironmentVariable("MSUI_PARTYSIGHT_PROBE_MODE"),
                    "sight", StringComparison.OrdinalIgnoreCase);
                Settings.Controls.CommandViewPartySightExperimental = sightMode;
                Settings.Controls.CommandViewCutPlane = true;
                // Noon, pinned: the creator world runs on the wall clock, and a midnight capture
                // proves nothing. A steep locked pitch so the rig looks DOWN at the cave.
                Settings.Lighting.TimeSource = TimeSource.Fixed;
                Settings.Lighting.TimeOfDay = 12f;
                Settings.Controls.CommandViewPitchDegrees = 62f;
                Console.WriteLine($"[partysight-probe] entering creator world at map {map} ({x:0.0}, {y:0.0}, {z:0.0})");
                EnterOfflineWorld();
                _partySightProbeStage = 1; _partySightProbeAt = now;
                return;
            }

            case 1:   // world streamed in: raise the Command View
                if (_worldLoading || now - _partySightProbeAt < 8.0) return;
                ToggleFreeView();
                PartySightProbeCheck("Command View raised offline", _freeView, $"_freeView={_freeView}");
                _partySightCursorOverride = _window.FramebufferSize * 0.5f;   // the pick probe's pixel
                _partySightProbeVantage = 0;
                PartySightProbePark(_partySightProbeVantage);
                _partySightProbeStage = 2; _partySightProbeAt = now;
                return;

            case 2:   // let the cube render and the streaming settle, then screenshot ON
                if (_partySightProbeParkPending)
                {
                    // The previous vantage's dump lands at the end of the frame it was armed
                    // in; move only once it has been written.
                    if (now - _partySightProbeAt < 0.5) return;
                    _partySightProbeParkPending = false;
                    PartySightProbePark(_partySightProbeVantage);
                    _partySightProbeAt = now - 2.0;   // 3 s settle, not 5
                    return;
                }
                if (now - _partySightProbeAt < 5.0) return;
                if (_partySightProbeVantage == 0)
                {
                    if (Settings.Controls.CommandViewPartySightExperimental)
                    {
                        PartySightProbeCheck("pass constructed", _partySight is not null);
                        PartySightProbeCheck("eye resolved", PartySightEye() is not null,
                            $"eye={PartySightEye()}");
                        PartySightProbeCheck("cube rendered", _partySight is { CubeRenders: > 0 },
                            $"renders={_partySight?.CubeRenders} cubeMs={_partySight?.CubeMilliseconds:0.00} " +
                            $"prePassMs={_partySight?.PrePassMilliseconds:0.00}");
                    }
                    else
                    {
                        PartySightProbeCheck("roof cut engaged", _wmo?.ActiveCut is not null,
                            _wmo?.ActiveCut is WorldCut c
                                ? $"footprint=({c.Min.X:0},{c.Min.Y:0})-({c.Max.X:0},{c.Max.Y:0}) cutZ={c.CutZ:0.#}"
                                : "ActiveCut=null");
                    }
                }
                _currentVantage = $"partysight-v{_partySightProbeVantage}-{PartySightProbeVantages[_partySightProbeVantage].Name}-on";
                ArmGameplayDump();
                if (_partySightProbeVantage + 1 < PartySightProbeVantages.Length)
                {
                    // Next vantage after the dump lands; the pick/march probe runs from the last one.
                    _partySightProbeVantage++;
                    _partySightProbeParkPending = true;
                    _partySightProbeAt = now;
                    return;
                }
                _partySightProbeStage = 3; _partySightProbeAt = now;
                return;

            case 3:   // pick through the middle of the screen: where does a click land?
            {
                if (now - _partySightProbeAt < 2.5) return;
                Vector2 centre = _window.FramebufferSize * 0.5f;
                bool ground = TryPickGround(centre, out Vector3 point, out bool onTerrain);
                Console.WriteLine($"[partysight-probe] centre pick ground={ground} onTerrain={onTerrain} " +
                    $"point=({point.X:0.0}, {point.Y:0.0}, {point.Z:0.0}) spot=({_partySightProbeSpot.X:0.0}, " +
                    $"{_partySightProbeSpot.Y:0.0}, {_partySightProbeSpot.Z:0.0})");
                // March a few pixels down the centre column and report every solid along each
                // ray with its source and the primary's verdict on it: which surface a cut is
                // "looking at" is otherwise invisible in a screenshot.
                if (_partySight is { Engaged: true } sight)
                    for (int py = 60; py <= 420; py += 90)
                    {
                        var ray = _window.Camera.ScreenPointToRay(new Vector2(centre.X, py), _window.FramebufferSize);
                        if (ray is null) continue;
                        (Vector3 origin, Vector3 direction) = ray.Value;
                        var trail = new System.Text.StringBuilder();
                        Vector3 from = origin; float remaining = 250f;
                        for (int step = 0; step < 6 && remaining > 0f; step++)
                        {
                            var chit = _collision?.Raycast(from, direction, remaining);
                            var tcross = PartySightTerrainCrossing(from, direction, chit?.Distance ?? remaining);
                            Vector3 hit; string what;
                            if (tcross is Vector3 tc) { hit = tc; what = "terrain"; }
                            else if (chit is { } ch) { hit = ch.Point; what = _collision!.SourceOf(ch.Triangle); }
                            else break;
                            bool seen = PartySightSees(sight, hit);
                            trail.Append($" -> ({hit.X:0.0},{hit.Y:0.0},{hit.Z:0.0}) {what} seen={seen}");
                            float advance = Vector3.Distance(from, hit) + 0.1f;
                            from = hit + direction * 0.1f; remaining -= advance;
                        }
                        Console.WriteLine($"[partysight-probe] ray y={py}: cut={PartySightCutAway(origin + direction * 0.5f)}{trail}");
                    }
                // The comparison capture: whichever cut this run exercised, off.
                Settings.Controls.CommandViewPartySightExperimental = false;
                Settings.Controls.CommandViewCutPlane = false;
                _partySightProbeStage = 4; _partySightProbeAt = now;
                return;
            }

            case 4:   // screenshot OFF for the side-by-side
                if (now - _partySightProbeAt < 2.0) return;
                _currentVantage = $"partysight-v{_partySightProbeVantage}-{PartySightProbeVantages[_partySightProbeVantage].Name}-off";
                ArmGameplayDump();
                _partySightProbeStage = 5; _partySightProbeAt = now;
                return;

            case 5:
                if (now - _partySightProbeAt < 2.5) return;
                Console.WriteLine(_partySightProbeFailures == 0
                    ? "[partysight-probe] VERDICT: ALL CHECKS PASSED"
                    : $"[partysight-probe] VERDICT: {_partySightProbeFailures} CHECK(S) FAILED");
                Console.Out.Flush();
                _quitRequested = true;
                _partySightProbeStage = 99;
                return;
        }
    }
}

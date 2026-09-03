using System.Globalization;
using System.Numerics;
using MSUIClient.Engine;
using MSUIClient.Player;
using MSUIClient.World.Collision;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// Swim probe: a scripted offline proof of swimming collision in the REAL world
// data. Activated by MSUI_SWIM_PROBE=<map>,<x>,<y>,<z> (default: the Durotar
// shipwrecks where the owner reported swimming through the ground and the
// hulls, 2026-09-03). The client boots the creator world at that spot, then
// swims a set of legs: the compass headings at the surface, and level / diving
// runs aimed at every WMO hull nearby. Every frame an INDEPENDENT detector
// checks the controller's actual displacement against the collision world and
// the height field — a segment ray through a triangle, or feet under terrain,
// is a penetration no matter what the controller believed — and vertical or
// horizontal jumps beyond a stroke are logged as snaps. Console lines carry
// the per-leg verdicts and a final summary. No-ops unless the env var is set.
// ─────────────────────────────────────────────────────────────────────────────

public sealed partial class GameLoop
{
    private static readonly string? SwimProbeSpec =
        Environment.GetEnvironmentVariable("MSUI_SWIM_PROBE");

    private sealed record SwimProbeLeg(
        string Name, Vector3? Start, float Yaw, float CameraPitch, string[] Keys, double Seconds);

    private sealed class SwimProbeStats
    {
        public int Frames;
        public float MaxTerrainPenetration;
        public int TerrainFrames;
        public int RoofedFrames;
        public int Tunnels;
        public readonly Dictionary<string, int> TunnelSources = new(StringComparer.OrdinalIgnoreCase);
        public int Snaps;
        public int Transitions;
        public float MaxFrameDz;
        public Vector3 Start, End;
        public int SwimFrames, GroundedFrames;
    }

    private int _swimProbeStage;
    private double _swimProbeAt;
    private Vector3 _swimProbeSpot;
    private readonly List<SwimProbeLeg> _swimProbeLegs = [];
    private int _swimProbeLegIndex = -1;
    private double _swimProbeLegStart;
    private Vector3 _swimProbePrev;
    private bool _swimProbeHavePrev;
    private SwimProbeStats? _swimProbeStats;
    private readonly List<string> _swimProbeSummary = [];
    private string _swimProbeLastState = "";
    private int _swimProbeLogBudget;
    private int _swimProbeTotalTunnels, _swimProbeTotalTerrain, _swimProbeTotalSnaps;

    private void UpdateSwimProbe()
    {
        if (SwimProbeSpec is null || _swimProbeStage >= 99) return;
        double now = NowSeconds();

        switch (_swimProbeStage)
        {
            case 0:
            {
                if (_gl is null || _worldLoadStarted) return;
                if (_swimProbeAt == 0) { _swimProbeAt = now; return; }
                if (now - _swimProbeAt < 1.0) return;
                int map = 1; float x = 28.3f, y = -5190.0f, z = -1.5f;
                string[] parts = SwimProbeSpec.Split(',', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 4 &&
                    int.TryParse(parts[0], out int m) &&
                    float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float px) &&
                    float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float py) &&
                    float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float pz))
                { map = m; x = px; y = py; z = pz; }
                _swimProbeSpot = new Vector3(x, y, z);
                _config.DevTools = true;
                Settings.Creator.LocMap = map;
                Settings.Creator.LocMapName = map switch { 0 => "Azeroth", 1 => "Kalimdor", _ => Settings.Creator.LocMapName };
                Settings.Creator.LocX = x; Settings.Creator.LocY = y; Settings.Creator.LocZ = z;
                Settings.Creator.LocYaw = 0f;
                Settings.Lighting.TimeSource = TimeSource.Fixed;
                Settings.Lighting.TimeOfDay = 12f;
                Console.WriteLine($"[swim-probe] entering creator world at map {map} ({x:0.0}, {y:0.0}, {z:0.0})");
                EnterOfflineWorld();
                _swimProbeStage = 1; _swimProbeAt = now;
                return;
            }

            case 1:   // world streamed in, collision accepted
            {
                if (_worldLoading || _controller is null || now - _swimProbeAt < 8.0) return;
                if (_controller.Collision is not { IsEmpty: false } && now - _swimProbeAt < 30.0) return;
                SwimProbeDescribeEnvironment();
                SwimProbeBuildLegs();
                _currentVantage = "swim-probe-spot";
                ArmGameplayDump();
                _swimProbeStage = 2; _swimProbeAt = now;
                return;
            }

            case 2:   // run the legs
            {
                if (_controller is null) return;
                if (_swimProbeLegIndex < 0 || now - _swimProbeLegStart >= _swimProbeLegs[_swimProbeLegIndex].Seconds)
                {
                    if (_swimProbeLegIndex >= 0) SwimProbeFinishLeg();
                    _swimProbeLegIndex++;
                    if (_swimProbeLegIndex >= _swimProbeLegs.Count)
                    {
                        _liveHeld.Clear();
                        SwimProbeReport();
                        _swimProbeStage = 3; _swimProbeAt = now;
                        return;
                    }
                    SwimProbeStartLeg(_swimProbeLegs[_swimProbeLegIndex], now);
                    return;
                }
                SwimProbeDrive(_swimProbeLegs[_swimProbeLegIndex]);
                SwimProbeObserve();
                return;
            }

            case 3:
                if (now - _swimProbeAt < 1.0) return;
                Console.WriteLine("[swim-probe] done, quitting");
                Console.Out.Flush();
                _quitRequested = true;
                _swimProbeStage = 99;
                return;
        }
    }

    private void SwimProbeDescribeEnvironment()
    {
        Vector3 spot = _swimProbeSpot;
        float? terrainZ = _terrain?.SampleHeight(spot.X, spot.Y);
        bool liquid = TryGetBodyLiquidSurface(spot, out float surfaceZ, out byte liquidType);
        Console.WriteLine($"[swim-probe] spot=({spot.X:0.00},{spot.Y:0.00},{spot.Z:0.00}) terrainZ={terrainZ?.ToString("0.00") ?? "none"} " +
                          $"liquid={(liquid ? $"surface {surfaceZ:0.00} type {liquidType}" : "NONE")} " +
                          $"collisionHeight={_controller!.CollisionHeight:0.00} radius={_config.Movement.Radius:0.00} height={_config.Movement.Height:0.00} " +
                          $"step={_config.Movement.StepHeight:0.00} maxSlope={_config.Movement.MaxSlopeDegrees:0}");
        Console.WriteLine($"[swim-probe] collision: {(_controller.Collision is { } c ? $"{c.TriangleCount:N0} triangles" : "NONE")} " +
                          $"wmoInstances={_wmo?.InstanceCount ?? 0} swimming={_controller.Swimming} grounded={_controller.Grounded} ground={_controller.GroundSource}");
        if (_wmo is null) return;
        foreach (var (path, min, max, origin) in _wmo.Placements
                     .Select(p => (p.Path, p.Min, p.Max, p.Origin))
                     .Where(p => Vector3.Distance(new Vector3((p.Min.X + p.Max.X) * 0.5f, (p.Min.Y + p.Max.Y) * 0.5f, spot.Z), new Vector3(spot.X, spot.Y, spot.Z)) < 150f)
                     .OrderBy(p => Vector3.Distance((p.Min + p.Max) * 0.5f, spot)))
        {
            Vector3 centre = (min + max) * 0.5f;
            Console.WriteLine($"[swim-probe] wmo {Path.GetFileName(path)} centre=({centre.X:0.0},{centre.Y:0.0},{centre.Z:0.0}) " +
                              $"bounds z {min.Z:0.0}..{max.Z:0.0} dist={Vector3.Distance(new Vector3(centre.X, centre.Y, spot.Z), spot):0.0}");
        }
    }

    private void SwimProbeBuildLegs()
    {
        _swimProbeLegs.Clear();
        Vector3 spot = _swimProbeSpot;

        // Compass at the surface: the "swim through the ground" report is about the sea floor
        // and the shore, wherever the owner happened to head.
        for (int i = 0; i < 8; i++)
        {
            float yaw = i * MathF.PI / 4f;
            _swimProbeLegs.Add(new($"surface-{i * 45:000}", spot, yaw, 0f, ["W"], 8.0));
        }
        // Diving compass: the camera pitched down ~40 degrees, the way a dive is done.
        for (int i = 0; i < 8; i += 2)
        {
            float yaw = i * MathF.PI / 4f;
            _swimProbeLegs.Add(new($"dive-{i * 45:000}", spot, yaw, 0.7f, ["W"], 8.0));
        }
        // Space after a dive: vertical ascent from depth, then a breach at the line.
        _swimProbeLegs.Add(new("dive-then-space:dive", spot, 0f, 1.2f, ["W"], 3.0));
        _swimProbeLegs.Add(new("dive-then-space:space", null, 0f, 0f, ["SPACE"], 4.0));

        // Every hull nearby: level run at it, a shallow dive at it, a steep dive at it, and a
        // run starting INSIDE its footprint (the owner was in among the wrecks).
        if (_wmo is not null)
        {
            var hulls = _wmo.Placements
                .Select(p => (p.Path, Centre: (p.Min + p.Max) * 0.5f, p.Min, p.Max))
                .Where(p => Vector2.Distance(new(p.Centre.X, p.Centre.Y), new(spot.X, spot.Y)) < 120f)
                .OrderBy(p => Vector2.Distance(new(p.Centre.X, p.Centre.Y), new(spot.X, spot.Y)))
                .Take(4)
                .ToArray();
            int index = 0;
            foreach (var hull in hulls)
            {
                string leaf = Path.GetFileNameWithoutExtension(hull.Path);
                Vector2 to = new(hull.Centre.X - spot.X, hull.Centre.Y - spot.Y);
                float distance = to.Length();
                float yaw = MathF.Atan2(to.Y, to.X);
                double seconds = Math.Clamp((distance + 25f) / SwimmingMovementLaw.DefaultForwardSpeed, 6.0, 30.0);
                _swimProbeLegs.Add(new($"hull{index}-{leaf}-level", spot, yaw, 0f, ["W"], seconds));
                _swimProbeLegs.Add(new($"hull{index}-{leaf}-dive35", spot, yaw, 0.6f, ["W"], seconds));
                _swimProbeLegs.Add(new($"hull{index}-{leaf}-dive70", spot, yaw, 1.2f, ["W"], seconds));
                // From the far side back through it, and across it sideways.
                Vector3 farSide = hull.Centre + new Vector3(MathF.Cos(yaw), MathF.Sin(yaw), 0f) * (distance * 0.5f + 15f);
                farSide.Z = spot.Z;
                _swimProbeLegs.Add(new($"hull{index}-{leaf}-return", farSide, yaw + MathF.PI, 0f, ["W"], seconds));
                Vector3 side = hull.Centre + new Vector3(MathF.Sin(yaw), -MathF.Cos(yaw), 0f) * 20f;
                side.Z = spot.Z;
                _swimProbeLegs.Add(new($"hull{index}-{leaf}-across", side, yaw + MathF.PI * 0.5f, 0.3f, ["W"], 14.0));
                // From INSIDE the hull, below the seabed the hull is sunk into: swim out through
                // it on every compass heading. This is the "I can swim through the ground" recipe -
                // a body that has proven it is under the outdoor shell must lose that proof the
                // moment nothing is over it any more.
                float? hullTerrain = _terrain?.SampleHeight(hull.Centre.X, hull.Centre.Y);
                float interiorZ = MathF.Min(spot.Z, (hullTerrain ?? spot.Z) - 5f);
                Vector3 interior = new(hull.Centre.X, hull.Centre.Y, interiorZ);
                for (int i = 0; i < 8; i += 2)
                    _swimProbeLegs.Add(new($"hull{index}-{leaf}-inside-{i * 45:000}", interior,
                        i * MathF.PI / 4f, 0f, ["W"], 12.0));
                _swimProbeLegs.Add(new($"hull{index}-{leaf}-inside-space", interior, 0f, 0f, ["SPACE"], 5.0));
                index++;
            }
        }
        Console.WriteLine($"[swim-probe] {_swimProbeLegs.Count} leg(s) planned");
    }

    private void SwimProbeStartLeg(SwimProbeLeg leg, double now)
    {
        if (leg.Start is { } start)
        {
            _controller!.Teleport(start.X, start.Y, start.Z);
            _window.Camera.Target = start;
        }
        _controller!.Yaw = leg.Yaw;
        _window.Camera.Yaw = leg.Yaw;
        _window.Camera.OrbitYaw = 0f;
        _window.Camera.Pitch = leg.CameraPitch;
        _liveHeld.Clear();
        foreach (string key in leg.Keys) _liveHeld.Add(key);
        _swimProbeLegStart = now;
        _swimProbeHavePrev = false;
        _swimProbeStats = new SwimProbeStats { Start = _controller.Position };
        _swimProbeLastState = "";
        _swimProbeLogBudget = 6;
        Console.WriteLine($"[swim-probe] leg {leg.Name}: start=({_controller.Position.X:0.0},{_controller.Position.Y:0.0},{_controller.Position.Z:0.00}) " +
                          $"yaw={leg.Yaw:0.00} camPitch={leg.CameraPitch:0.00} keys={string.Join("+", leg.Keys)} for {leg.Seconds:0}s");
    }

    private void SwimProbeDrive(SwimProbeLeg leg)
    {
        _window.Camera.Yaw = leg.Yaw;
        _window.Camera.OrbitYaw = 0f;
        _window.Camera.Pitch = leg.CameraPitch;
    }

    /// <summary>
    /// The independent detector. It never asks the controller whether it collided; it asks the
    /// world whether the displacement the controller just committed crossed anything.
    /// </summary>
    private void SwimProbeObserve()
    {
        if (_controller is null || _swimProbeStats is not { } stats) return;
        Vector3 cur = _controller.Position;
        stats.Frames++;
        if (_controller.Swimming) stats.SwimFrames++;
        if (_controller.Grounded) stats.GroundedFrames++;

        string state = $"{(_controller.Swimming ? "swim" : "walk")}/{(_controller.Grounded ? "grounded" : "free")}/{_controller.GroundSource}";
        if (state != _swimProbeLastState)
        {
            if (_swimProbeLastState.Length > 0)
            {
                stats.Transitions++;
                if (_swimProbeLogBudget > 0)
                    Console.WriteLine($"[swim-probe]   state {_swimProbeLastState} -> {state} at ({cur.X:0.0},{cur.Y:0.0},{cur.Z:0.00}) f{stats.Frames}");
            }
            _swimProbeLastState = state;
        }

        // Feet under the height field. Under a roof (a hull, a cave) that is an interior and
        // legitimate; under OPEN ground - nothing between the body and the height field - it is
        // the "swim through the ground" defect, whatever the controller's shell flag says.
        if (_terrain?.SampleHeight(cur.X, cur.Y) is float terrainZ)
        {
            float penetration = terrainZ - cur.Z;
            if (penetration > 0.1f)
            {
                bool roofed = _controller.Collision is { IsEmpty: false } roofWorld &&
                    roofWorld.Raycast(cur + new Vector3(0f, 0f, 1f), Vector3.UnitZ, MathF.Max(0.01f, penetration)) is not null;
                if (roofed) stats.RoofedFrames++;
                else
                {
                    stats.TerrainFrames++;
                    if (penetration > stats.MaxTerrainPenetration)
                    {
                        stats.MaxTerrainPenetration = penetration;
                        if (_swimProbeLogBudget > 0)
                        {
                            _swimProbeLogBudget--;
                            Console.WriteLine($"[swim-probe]   TERRAIN feet {penetration:0.00} under OPEN ground at ({cur.X:0.0},{cur.Y:0.0},{cur.Z:0.00}) " +
                                              $"terrainZ={terrainZ:0.00} shell={_controller.UnderTerrainShell} state={state} f{stats.Frames}");
                        }
                    }
                }
            }
        }

        if (_swimProbeHavePrev)
        {
            Vector3 delta = cur - _swimProbePrev;
            float length = delta.Length();
            float planar = new Vector2(delta.X, delta.Y).Length();
            stats.MaxFrameDz = MathF.Max(stats.MaxFrameDz, MathF.Abs(delta.Z));
            // A stroke is 4.72 yd/s; at the 50 ms dt clamp that is 0.24 yd. Anything beyond a
            // stroke and a half in one frame is a snap, not swimming.
            if (MathF.Abs(delta.Z) > 0.4f || planar > 0.4f)
            {
                stats.Snaps++;
                if (_swimProbeLogBudget > 0)
                {
                    _swimProbeLogBudget--;
                    Console.WriteLine($"[swim-probe]   SNAP d=({delta.X:0.00},{delta.Y:0.00},{delta.Z:0.00}) from ({_swimProbePrev.X:0.0},{_swimProbePrev.Y:0.0},{_swimProbePrev.Z:0.00}) " +
                                      $"state={state} ground={_controller.GroundZ?.ToString("0.00") ?? "-"} f{stats.Frames}");
                }
            }

            if (length > 1e-4f && _controller.Collision is { IsEmpty: false } world)
            {
                float body = MathF.Max(0.5f, _controller.CollisionHeight);
                Span<float> bands = [0.15f, body * 0.5f, body - 0.15f];
                foreach (float band in bands)
                {
                    Vector3 origin = _swimProbePrev + new Vector3(0f, 0f, band);
                    if (world.Raycast(origin, delta, length) is not { } hit) continue;
                    stats.Tunnels++;
                    string source = world.SourceOf(hit.Triangle);
                    stats.TunnelSources[source] = stats.TunnelSources.GetValueOrDefault(source) + 1;
                    if (_swimProbeLogBudget > 0)
                    {
                        _swimProbeLogBudget--;
                        Console.WriteLine($"[swim-probe]   TUNNEL band={band:0.00} through {source} n=({hit.Normal.X:0.00},{hit.Normal.Y:0.00},{hit.Normal.Z:0.00}) " +
                                          $"at {hit.Distance:0.00}/{length:0.00} from ({_swimProbePrev.X:0.0},{_swimProbePrev.Y:0.0},{_swimProbePrev.Z:0.00}) " +
                                          $"to ({cur.X:0.0},{cur.Y:0.0},{cur.Z:0.00}) state={state} f{stats.Frames}");
                    }
                    break;
                }
            }
        }
        _swimProbePrev = cur;
        _swimProbeHavePrev = true;
    }

    private void SwimProbeFinishLeg()
    {
        if (_swimProbeStats is not { } stats || _controller is null) return;
        stats.End = _controller.Position;
        SwimProbeLeg leg = _swimProbeLegs[_swimProbeLegIndex];
        float travelled = Vector2.Distance(new(stats.Start.X, stats.Start.Y), new(stats.End.X, stats.End.Y));
        string sources = stats.TunnelSources.Count == 0 ? "-" :
            string.Join(",", stats.TunnelSources.OrderByDescending(x => x.Value).Take(3).Select(x => $"{x.Key}x{x.Value}"));
        bool clean = stats.Tunnels == 0 && stats.MaxTerrainPenetration <= 0.1f && stats.Snaps == 0;
        string line = $"{(clean ? "PASS" : "FAIL")} {leg.Name,-34} frames={stats.Frames,4} travelled={travelled,6:0.0} " +
                      $"endZ={stats.End.Z,7:0.00} swim={stats.SwimFrames,4} grounded={stats.GroundedFrames,4} " +
                      $"terrainPen={stats.MaxTerrainPenetration,5:0.00}({stats.TerrainFrames}) roofed={stats.RoofedFrames,4} tunnels={stats.Tunnels,3} snaps={stats.Snaps,3} " +
                      $"maxDz={stats.MaxFrameDz:0.00} transitions={stats.Transitions} via {sources}";
        _swimProbeSummary.Add(line);
        _swimProbeTotalTunnels += stats.Tunnels;
        _swimProbeTotalTerrain += stats.TerrainFrames;
        _swimProbeTotalSnaps += stats.Snaps;
        Console.WriteLine($"[swim-probe] {line}");
        _swimProbeStats = null;
    }

    private void SwimProbeReport()
    {
        Console.WriteLine("[swim-probe] ── summary ──");
        foreach (string line in _swimProbeSummary) Console.WriteLine($"[swim-probe] {line}");
        int failed = _swimProbeSummary.Count(l => l.StartsWith("FAIL", StringComparison.Ordinal));
        Console.WriteLine($"[swim-probe] {(failed == 0 ? "PASS" : "FAIL")}: {failed}/{_swimProbeSummary.Count} leg(s) failed; " +
                          $"tunnels={_swimProbeTotalTunnels} terrainFrames={_swimProbeTotalTerrain} snaps={_swimProbeTotalSnaps}");
    }
}

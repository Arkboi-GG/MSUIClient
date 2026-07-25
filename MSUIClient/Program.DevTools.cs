using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using MSUIClient.Engine;
using MSUIClient.World.Wmo;

namespace MSUIClient;

// ============================================================================
// DevTools - the switchable developer overlay layer (FOUNDATION_PLAN.md sec 12).
//
// Everything in this file is developer TOOLING: it observes/presents core state
// and authors dev data (vantages now; the scene dump and override authoring
// next). Core never references it, and it runs only when config.DevTools is true,
// so a release build ships it dormant. Core decides; this layer observes. New
// dev tooling belongs HERE, not woven into Program.cs.
// ============================================================================
public sealed partial class GameLoop
{
    // ---- Vantages: reproducible viewpoints (FOUNDATION_PLAN / PLAN_01) ----

    /// <summary>
    /// Snapshot the current viewpoint - position, camera, time-of-day and every
    /// scene toggle - into a serializable Vantage. Reads from the same owners the
    /// HUD binds; the scene dump (step 2) reuses this as its reproducible half,
    /// and when TuningState lands (step 5) both move onto it.
    /// </summary>
    private Vantage CaptureVantage(string name)
    {
        var cam = _window.Camera;

        var v = new Vantage
        {
            Name = name,
            Map = _config.Start.Map,
            MapName = _config.Start.MapName,
            X = _controller?.Position.X ?? _config.Start.X,
            Y = _controller?.Position.Y ?? _config.Start.Y,
            Z = _controller?.Position.Z ?? _config.Start.Z,
            Flying = _controller?.Flying ?? false,

            Facing = cam.Yaw,
            OrbitYaw = cam.OrbitYaw,
            Pitch = cam.Pitch,
            Distance = cam.Distance,
            Fov = cam.FieldOfViewDegrees,
            FarPlane = cam.FarPlane,

            TimeOfDay = _atmosphere.TimeOfDayHours,
            DynamicLighting = _atmosphere.DynamicLighting,
            FogEnabled = _atmosphere.FogEnabled,
            CullAtFogEnd = _atmosphere.CullAtFogEnd,
            FogStart = _atmosphere.FogStart,
            FogEnd = _atmosphere.FogEnd,
            SunStrength = _atmosphere.SunStrength,
            AmbientStrength = _atmosphere.AmbientStrength,

            CycleTimeOfDay = _cycleTimeOfDay,
            CoupleFarPlaneToFog = _coupleFarPlaneToFog,
            GameHoursPerMinute = _gameHoursPerMinute,
        };

        if (_wmo is not null)
        {
            v.WmoEnabled = _wmo.Enabled;
            v.WmoFrustumCulling = _wmo.FrustumCulling;
            v.UseDistanceLodShells = _wmo.UseDistanceLodShells;
            v.WmoForceTwoSided = _wmo.ForceTwoSided;
            v.WmoOcclusionCulling = _wmo.OcclusionCulling;
            v.WmoVisTrace = _wmo.VisTrace;
            v.WmoDumpGroups = _wmo.DumpLargeWmoGroups;
            v.WmoInsideInstanceMargin = _wmo.InsideInstanceMargin;
            v.WmoInteriorCullDistance = _wmo.InteriorCullDistance;
            v.WmoShellNearGuard = _wmo.ShellNearGuard;
            v.WmoDrawDistance = _wmo.DrawDistance;
            v.WmoOcclusionMinDistance = _wmo.OcclusionMinDistance;
            v.WmoAlphaCutoff = _wmo.AlphaCutoff;
            v.WmoImpostorMaxVertices = _wmo.ImpostorMaxVertices;
        }

        if (_doodads is not null)
        {
            v.DoodadEnabled = _doodads.Enabled;
            v.DoodadFrustumCulling = _doodads.FrustumCulling;
            v.DoodadUseInstancing = _doodads.UseInstancing;
            v.DoodadDrawDistance = _doodads.DrawDistance;
            v.DoodadAlphaCutoff = _doodads.AlphaCutoff;
        }

        return v;
    }

    /// <summary>
    /// Restore a saved viewpoint. Position and camera snap immediately; if it is a
    /// far jump the moving residency ring streams the destination in over the next
    /// frames (UpdateWorldResidency does the re-home, so this stays simple). A
    /// cross-map vantage restores the viewpoint only and warns, because the ADT
    /// cache is built for one map (see PLAN_01_VANTAGES step 8).
    /// </summary>
    private void ApplyVantage(Vantage v)
    {
        if (!string.Equals(v.MapName, _config.Start.MapName, StringComparison.OrdinalIgnoreCase))
            Console.WriteLine(
                $"[vantage] WARNING '{v.Name}' is map '{v.MapName}' but the client is on " +
                $"'{_config.Start.MapName}' - restoring viewpoint only; terrain will not match.");

        if (_controller is not null)
        {
            _controller.Teleport(v.X, v.Y, v.Z);
            _controller.Flying = v.Flying;
            _controller.Yaw = v.Facing;
        }

        var cam = _window.Camera;
        cam.Target = _controller?.Position ?? new Vector3(v.X, v.Y, v.Z);
        cam.Yaw = v.Facing;
        cam.OrbitYaw = v.OrbitYaw;
        cam.Pitch = v.Pitch;
        cam.Distance = v.Distance;
        cam.EffectiveDistance = v.Distance;
        cam.FieldOfViewDegrees = v.Fov;
        cam.FarPlane = v.FarPlane;

        _atmosphere.TimeOfDayHours = v.TimeOfDay;
        _atmosphere.DynamicLighting = v.DynamicLighting;
        _atmosphere.FogEnabled = v.FogEnabled;
        _atmosphere.CullAtFogEnd = v.CullAtFogEnd;
        _atmosphere.FogStart = v.FogStart;
        _atmosphere.FogEnd = v.FogEnd;
        _atmosphere.SunStrength = v.SunStrength;
        _atmosphere.AmbientStrength = v.AmbientStrength;

        _cycleTimeOfDay = v.CycleTimeOfDay;
        _coupleFarPlaneToFog = v.CoupleFarPlaneToFog;
        _gameHoursPerMinute = v.GameHoursPerMinute;

        if (_wmo is not null)
        {
            _wmo.Enabled = v.WmoEnabled;
            _wmo.FrustumCulling = v.WmoFrustumCulling;
            _wmo.UseDistanceLodShells = v.UseDistanceLodShells;
            _wmo.ForceTwoSided = v.WmoForceTwoSided;
            _wmo.OcclusionCulling = v.WmoOcclusionCulling;
            _wmo.VisTrace = v.WmoVisTrace;
            _wmo.DumpLargeWmoGroups = v.WmoDumpGroups;
            _wmo.InsideInstanceMargin = v.WmoInsideInstanceMargin;
            _wmo.InteriorCullDistance = v.WmoInteriorCullDistance;
            _wmo.ShellNearGuard = v.WmoShellNearGuard;
            _wmo.DrawDistance = v.WmoDrawDistance;
            _wmo.OcclusionMinDistance = v.WmoOcclusionMinDistance;
            _wmo.AlphaCutoff = v.WmoAlphaCutoff;
            _wmo.ImpostorMaxVertices = v.WmoImpostorMaxVertices;
        }

        if (_doodads is not null)
        {
            _doodads.Enabled = v.DoodadEnabled;
            _doodads.FrustumCulling = v.DoodadFrustumCulling;
            _doodads.UseInstancing = v.DoodadUseInstancing;
            _doodads.DrawDistance = v.DoodadDrawDistance;
            _doodads.AlphaCutoff = v.DoodadAlphaCutoff;
        }

        _currentVantage = v.Name;
        // A teleport produces a legitimately enormous frame while the
        // destination streams in. That is not the bug the hitch recorder
        // hunts, so give it a grace window (PLAN_07).
        _hitch.SuppressFor(3.0);
        Console.WriteLine(VantageLine("loaded", v));
    }

    /// <summary>
    /// One-line [vantage] echo, identical in shape for save and load so the two can
    /// be diffed field-for-field - the step-1 done-test, and the bootstrap of the
    /// coherent data channel until the full scene dump (step 2) lands.
    /// </summary>
    private static string VantageLine(string verb, Vantage v)
        => $"[vantage] {verb} '{v.Name}' map={v.MapName} " +
           $"pos=({v.X:F1},{v.Y:F1},{v.Z:F1}) yaw={v.Facing:F3} pitch={v.Pitch:F3} " +
           $"dist={v.Distance:F1} fov={v.Fov:F0} fly={v.Flying} " +
           $"time={v.TimeOfDay:F2} cycle={v.CycleTimeOfDay} dyn={v.DynamicLighting} " +
           $"fog={v.FogEnabled}/{v.FogStart:F0}-{v.FogEnd:F0} coupleFar={v.CoupleFarPlaneToFog} " +
           $"wmo={v.WmoEnabled} lodSwap={v.UseDistanceLodShells} occl={v.WmoOcclusionCulling} " +
           $"wmoDist={v.WmoDrawDistance:F0} interiorCull={v.WmoInteriorCullDistance:F0} " +
           $"shellGuard={v.WmoShellNearGuard:F0} impostorVerts={v.WmoImpostorMaxVertices} " +
           $"doodad={v.DoodadEnabled} doodadDist={v.DoodadDrawDistance:F0}";

    // ---- Scene dump: coherent data output (FOUNDATION_PLAN / PLAN_02, step 2) ----

    /// <summary>
    /// Write a structured JSON scene dump (dumps/&lt;name&gt;.json) plus a one-line
    /// [dump] console summary. The reproducible half is a captured Vantage; the rest
    /// records what the client decided this view and why - the crosshair building's
    /// groups each carry the real WmoReasonCode. Paired with a screenshot this is the
    /// coherent data channel. Dev-only: reached from F9 and the HUD, both gated.
    /// </summary>
    private void DumpScene()
    {
        var cam = _window.Camera;
        string name = _currentVantage ?? DateTime.Now.ToString("yyyyMMdd-HHmmss");

        List<WmoRenderer.GroupReport>? crosshair = _wmo?.DescribeInstanceUnderRay(cam, cam.Position, cam.Forward);
        List<WmoRenderer.InstanceSummary> wmoInstances = _wmo?.SummarizeInstances(cam) ?? new List<WmoRenderer.InstanceSummary>();

        var dump = new
        {
            name,
            takenLocal = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            map = _config.Start.MapName,
            vantage = CaptureVantage(name),
            atmosphere = new
            {
                timeOfDay = _atmosphere.TimeOfDayHours,
                dynamicLighting = _atmosphere.DynamicLighting,
                sunDirection = V(_atmosphere.SunDirection),
                sunColor = V(_atmosphere.SunColor),
                sunIntensity = _atmosphere.SunIntensity,
                ambientColor = V(_atmosphere.AmbientColor),
                ambientIntensity = _atmosphere.AmbientIntensity,
                fogColor = V(_atmosphere.FogColor),
                fogStart = _atmosphere.ShaderFogStart,
                fogEnd = _atmosphere.ShaderFogEnd,
                skyColor = V(_atmosphere.SkyColor),
            },
            ground = _controller is null ? null : new
            {
                position = V(_controller.Position),
                grounded = _controller.Grounded,
                source = _controller.GroundSource,
                groundZ = _controller.GroundZ,
                terrainZ = _controller.TerrainGroundZ,
                collisionZ = _controller.CollisionGroundZ,
                groundTriangle = _controller.GroundTriangle,
                noGroundBelow = _controller.NoGroundBelow,
                flying = _controller.Flying,
            },
            terrain = new
            {
                residentTiles = _terrain?.TileCount ?? 0,
                pendingDiscovery = _backgroundDiscovery.Count,
            },
            doodads = _doodads is null ? null : new
            {
                placements = _doodads.InstanceCount,
                drawn = _doodads.DrawnLastFrame,
                drawCalls = _doodads.DrawCallsLastFrame,
            },
            wmo = _wmo is null ? null : new
            {
                drawnInstances = _wmo.DrawnLastFrame,
                visibleGroups = _wmo.VisibleGroupsLastFrame,
                drawCalls = _wmo.DrawCallsLastFrame,
                triangles = _wmo.TrianglesLastFrame,
                shellsDrawn = _wmo.ShellsDrawnLastFrame,
                shellsHidden = _wmo.ShellsHiddenLastFrame,
                occluded = _wmo.OccludedGroupsLastFrame,
                lodCulled = _wmo.LodGroupsCulledLastFrame,
                largest = _wmo.LargestWmoName,
                cameraInsideLargest = _wmo.LastInsideCity,
                crosshairInstanceGroups = crosshair,
                instances = wmoInstances,
            },
            perf = new
            {
                fps = _window.Fps,
                frameMs = _window.FrameMs,
                updateMs = _updateMilliseconds,
                movementMs = _movementMilliseconds,
                residencyMs = _residencyMilliseconds,
                preloadMs = _preloadMilliseconds,
                worldRenderMs = _worldRenderMilliseconds,
                characterRenderMs = _characterRenderMilliseconds,
                debugRenderMs = _debugRenderMilliseconds,
            },
        };

        try
        {
            string dir = Path.Combine(_config.RepoRoot, "dumps");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, name + ".json");
            File.WriteAllText(path, JsonSerializer.Serialize(dump, DumpJson));

            string cross = crosshair is null
                ? "none"
                : $"{crosshair.Count} group(s), " +
                  $"{crosshair.Count(g => g.Reason is WmoReasonCode.Drawn or WmoReasonCode.DrawnShellFar)} drawn";
            Console.WriteLine(
                $"[dump] wrote {path}: inside={_wmo?.LastInsideCity} crosshair=[{cross}] " +
                $"{wmoInstances.Count} resident WMO, {_wmo?.VisibleGroupsLastFrame ?? 0} groups drawn");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[dump] failed - {ex.Message}");
        }
    }

    private static readonly JsonSerializerOptions DumpJson = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    /// <summary>Serialize a Vector3 as a compact [x, y, z] array for the dump.</summary>
    private static float[] V(Vector3 v) => new[] { v.X, v.Y, v.Z };
}

using System.Numerics;
using ImGuiNET;
using MSUIClient.World.Collision;

namespace MSUIClient;

// ─────────────────────────────────────────────────────────────────────────────
// Creator-mode X-Ray: render the SERVER's collision geometry (vmaps) as an
// opaque mesh with the client's own world stripped away around it.
//
// WHY THIS EXISTS
//   "I fell through the floor" is a hole in the server's collision data, and
//   holes are invisible by construction — the missing triangle does not draw
//   in any view. Inverting the scene makes absence visible: terrain off, sky
//   off, background black, and the vmap mesh drawn opaque. Everywhere a floor
//   SHOULD be but is not, the black void shows through. Walk a bridge or a
//   city street in this view and a gap reads as a hole in a green surface,
//   not as a physics anecdote.
//
//   The mesh drawn here is built by the SAME VmapCollisionLoader the movement
//   system uses when collisionSource = vmaps, from the same .vmtile/.vmo files
//   mangosd raycasts against — so what you see is literally what the server
//   tests. Green is standable, red is wall, by the controller's real slope
//   limit (collision.frag). Note the boundary of responsibility: OUTDOOR
//   ground height is served by maps/*.map heightmaps, not vmaps, so open
//   terrain is EXPECTED to be void here. Holes worth chasing in this view are
//   missing floors in buildings, bridges, docks, caves and cities.
//
//   This view is deliberately independent of the movement collision world:
//   you keep walking on whatever collisionSource the config selected while
//   inspecting the server's mesh, so client-vs-server disagreements stay
//   observable instead of being defined away.
//
// LIFECYCLE
//   Nothing here persists. X-ray always boots OFF; toggling it on snapshots
//   the renderer enable flags and toggling it off restores them, so the mode
//   can never strand a session in a black, terrainless world.
// ─────────────────────────────────────────────────────────────────────────────
public sealed partial class GameLoop
{
    private const int XrayTerrainHidden = 0;
    private const int XrayTerrainWireframe = 1;
    private const int XrayTerrainTextured = 2;

    private bool _xrayActive;
    private int _xrayTerrainMode = XrayTerrainHidden;
    private bool _xrayShowBuildings;
    private bool _xrayShowDoodads;
    private bool _xrayShowFoliage;
    private bool _xrayShowWater;
    private bool _xrayShowSky;
    private bool _xraySolid = true;
    private bool _xrayIncludeM2 = true;
    private bool _xrayAutoRebuild = true;
    private float _xrayViewDistance = 800f;

    /// <summary>Renderer enable flags as they were when x-ray switched on.</summary>
    private (bool Wmo, bool Doodads, bool Sky, bool Foliage, bool Liquid)? _xraySaved;

    /// <summary>Built in Program.cs init alongside _collisionDebug: a second
    /// CollisionDebugRenderer so the vmap view never fights the Ctrl+C debug
    /// view of the MOVEMENT collision world over one GPU mesh.</summary>
    private CollisionDebugRenderer? _xrayDebug;

    private CollisionWorld? _xrayWorld;
    private Task<(int Generation, CollisionWorld World, string Summary)>? _xrayBuildTask;
    private int _xrayGeneration;
    private bool _xrayUploadPending;
    private string? _xrayStatus;

    /// <summary>The tile set the current build covers, for staleness detection
    /// as terrain streams while the player roams.</summary>
    private HashSet<(int col, int row)>? _xrayTilesBuilt;
    private int _xrayMapBuilt = -1;

    // ── navmesh (mmaps) overlay: the surface bots path on ────────────────────
    // Same machinery as the vmap mesh, third renderer instance, blue palette.
    // Lifted slightly so it does not z-fight the floors it was baked from.

    private bool _xrayShowVmapMesh = true;
    private bool _xrayShowNav;
    private const float XrayNavLift = 0.20f;

    private CollisionDebugRenderer? _xrayNavDebug;
    private CollisionWorld? _xrayNavWorld;
    private Task<(int Generation, CollisionWorld World, string Summary)>? _xrayNavBuildTask;
    private int _xrayNavGeneration;
    private bool _xrayNavUploadPending;
    private string? _xrayNavStatus;
    private HashSet<(int col, int row)>? _xrayNavTilesBuilt;
    private int _xrayNavMapBuilt = -1;

    // ── mode switch ──────────────────────────────────────────────────────────

    private void SetXrayActive(bool on)
    {
        if (on == _xrayActive) return;
        _xrayActive = on;

        if (on)
        {
            _xraySaved = (
                _wmo?.Enabled ?? true,
                _doodads?.Enabled ?? true,
                _sky?.Enabled ?? true,
                _foliage?.Enabled ?? true,
                _liquid?.Enabled ?? true);
            if (_xrayWorld is null && _xrayBuildTask is null) BeginXrayBuild();
        }
        else
        {
            if (_xraySaved is { } saved)
            {
                if (_wmo is not null) _wmo.Enabled = saved.Wmo;
                if (_doodads is not null) _doodads.Enabled = saved.Doodads;
                if (_sky is not null) _sky.Enabled = saved.Sky;
                if (_foliage is not null) _foliage.Enabled = saved.Foliage;
                if (_liquid is not null) _liquid.Enabled = saved.Liquid;
            }
            _xraySaved = null;
            if (_terrain is not null) _terrain.Enabled = true;
        }
    }

    /// <summary>
    /// Asserted every frame while x-ray is on (idempotent bool writes), so the
    /// settings modal or a vantage restore cannot silently re-light the world
    /// mid-inspection. Also the per-frame home of build acceptance: the GPU
    /// upload must happen on the render thread, and Render is that thread.
    /// </summary>
    private void ApplyXrayLayers()
    {
        if (_terrain is not null) _terrain.Enabled = _xrayTerrainMode != XrayTerrainHidden;
        if (_wmo is not null) _wmo.Enabled = _xrayShowBuildings;
        if (_doodads is not null) _doodads.Enabled = _xrayShowDoodads;
        if (_sky is not null) _sky.Enabled = _xrayShowSky;
        if (_foliage is not null) _foliage.Enabled = _xrayShowFoliage;
        if (_liquid is not null) _liquid.Enabled = _xrayShowWater;

        // The sky pass is off, so the window clear IS the background.
        if (!_xrayShowSky) _window.SkyColor = Vector3.Zero;

        AcceptXrayBuild();
        AcceptXrayNavBuild();

        if (_xrayAutoRebuild && _xrayBuildTask is null && XrayBuildIsStale())
            BeginXrayBuild();
        if (_xrayShowNav && _xrayAutoRebuild && _xrayNavBuildTask is null && XrayNavBuildIsStale())
            BeginXrayNavBuild();
    }

    /// <summary>Wireframe terrain bracket around the terrain draw. A textured
    /// terrain hides the mesh behind it; a wireframe one is a reference grid
    /// you can see the collision through.</summary>
    private void BeginXrayTerrainWireframe()
    {
        if (!_xrayActive || _xrayTerrainMode != XrayTerrainWireframe) return;
        _window.Gl.PolygonMode(
            Silk.NET.OpenGL.TriangleFace.FrontAndBack, Silk.NET.OpenGL.PolygonMode.Line);
    }

    private void EndXrayTerrainWireframe()
    {
        if (!_xrayActive || _xrayTerrainMode != XrayTerrainWireframe) return;
        _window.Gl.PolygonMode(
            Silk.NET.OpenGL.TriangleFace.FrontAndBack, Silk.NET.OpenGL.PolygonMode.Fill);
    }

    // ── build machinery ──────────────────────────────────────────────────────

    private bool XrayBuildIsStale()
        => XrayTilesStale(_xrayTilesBuilt, _xrayMapBuilt, _xrayWorld is null);

    private bool XrayNavBuildIsStale()
        => XrayTilesStale(_xrayNavTilesBuilt, _xrayNavMapBuilt, _xrayNavWorld is null);

    private bool XrayTilesStale(HashSet<(int col, int row)>? built, int mapBuilt, bool worldMissing)
    {
        if (_terrain is null) return false;
        if (built is null) return worldMissing;
        if (mapBuilt != _config.Start.Map) return true;
        int count = 0;
        foreach (var tile in _terrain.LoadedTiles)
        {
            if (!built.Contains(tile)) return true;
            count++;
        }
        return count != built.Count;
    }

    /// <summary>
    /// Kick an off-thread vmap load + BVH build for exactly the tiles terrain
    /// has loaded. The tile list is COPIED here on the render thread (handbook
    /// §5.4: no worker reads a live renderer collection); the loader parses
    /// files it opens itself. Superseded builds are discarded by generation.
    /// </summary>
    private void BeginXrayBuild()
    {
        if (_terrain is null) return;
        if (_config.VmapPath is not { } vmapPath || !_config.HasVmaps)
        {
            _xrayStatus = "no vmaps configured (client.json vmapPath)";
            return;
        }

        var tiles = _terrain.LoadedTiles.ToArray();
        if (tiles.Length == 0)
        {
            _xrayStatus = "no terrain tiles loaded yet";
            return;
        }

        int map = _config.Start.Map;
        bool includeM2 = _xrayIncludeM2;
        int generation = ++_xrayGeneration;
        _xrayTilesBuilt = tiles.ToHashSet();
        _xrayMapBuilt = map;
        _xrayStatus = $"building {tiles.Length} tile(s)...";

        _xrayBuildTask = Task.Run(() =>
        {
            var world = new CollisionWorld();
            var loader = new VmapCollisionLoader(vmapPath);
            foreach (var (col, row) in tiles)
                loader.LoadTile(world, map, col, row, includeM2);
            world.Build();
            return (generation, world, loader.Summary());
        });
    }

    /// <summary>Off-thread navmesh load for the loaded tiles, same shape as
    /// BeginXrayBuild. No BVH is queried, but CollisionWorld.Build also computes
    /// the debug-vertex array's backing store, so it stays.</summary>
    private void BeginXrayNavBuild()
    {
        if (_terrain is null) return;
        if (_config.MmapPath is not { } mmapPath || !_config.HasMmaps)
        {
            _xrayNavStatus = "no mmaps configured (client.json mmapPath)";
            return;
        }

        var tiles = _terrain.LoadedTiles.ToArray();
        if (tiles.Length == 0)
        {
            _xrayNavStatus = "no terrain tiles loaded yet";
            return;
        }

        int map = _config.Start.Map;
        int generation = ++_xrayNavGeneration;
        _xrayNavTilesBuilt = tiles.ToHashSet();
        _xrayNavMapBuilt = map;
        _xrayNavStatus = $"building {tiles.Length} tile(s)...";

        _xrayNavBuildTask = Task.Run(() =>
        {
            var world = new CollisionWorld();
            var loader = new MmapNavLoader(mmapPath);
            foreach (var (col, row) in tiles)
                loader.LoadTile(world, map, col, row);
            world.Build();
            return (generation, world, loader.Summary());
        });
    }

    private void AcceptXrayNavBuild()
    {
        if (_xrayNavBuildTask is not { IsCompleted: true } task) return;
        _xrayNavBuildTask = null;

        try
        {
            var ready = task.GetAwaiter().GetResult();
            if (ready.Generation != _xrayNavGeneration) return;

            _xrayNavWorld = ready.World.IsEmpty ? null : ready.World;
            _xrayNavStatus = ready.World.IsEmpty
                ? "no navmesh in the loaded tiles"
                : ready.Summary;
            _xrayNavUploadPending = _xrayNavWorld is not null;
            if (_xrayNavWorld is null) _xrayNavDebug?.Clear();
        }
        catch (Exception ex)
        {
            _xrayNavStatus = $"navmesh build FAILED - {ex.Message}";
            Console.WriteLine($"[xray] {_xrayNavStatus}");
        }
    }

    private void AcceptXrayBuild()
    {
        if (_xrayBuildTask is not { IsCompleted: true } task) return;
        _xrayBuildTask = null;

        try
        {
            var ready = task.GetAwaiter().GetResult();
            if (ready.Generation != _xrayGeneration) return;

            _xrayWorld = ready.World.IsEmpty ? null : ready.World;
            _xrayStatus = ready.World.IsEmpty
                ? "no vmap geometry in the loaded tiles"
                : ready.Summary;
            _xrayUploadPending = _xrayWorld is not null;
            if (_xrayWorld is null) _xrayDebug?.Clear();
        }
        catch (Exception ex)
        {
            _xrayStatus = $"vmap build FAILED - {ex.Message}";
            Console.WriteLine($"[xray] {_xrayStatus}");
        }
    }

    // ── render (called from the debug pass in Program.cs Render) ─────────────

    private void RenderXray()
    {
        if (!_xrayActive) return;

        if (_xrayDebug is not null)
        {
            if (_xrayUploadPending && _xrayWorld is not null)
            {
                _xrayUploadPending = false;
                _xrayDebug.Build(_xrayWorld);
            }

            if (_xrayShowVmapMesh && _xrayDebug.TriangleCount > 0)
            {
                _xrayDebug.Enabled = true;
                _xrayDebug.Solid = _xraySolid;
                _xrayDebug.FadeStart = _xrayViewDistance * 0.65f;
                _xrayDebug.FadeEnd = _xrayViewDistance;
                _xrayDebug.Render(
                    _window.Camera,
                    MathF.Cos(_config.Movement.MaxSlopeDegrees * MathF.PI / 180f),
                    Vector3.Zero);
            }
        }

        if (_xrayNavDebug is not null)
        {
            if (_xrayNavUploadPending && _xrayNavWorld is not null)
            {
                _xrayNavUploadPending = false;
                _xrayNavDebug.Build(_xrayNavWorld);
            }

            if (_xrayShowNav && _xrayNavDebug.TriangleCount > 0)
            {
                _xrayNavDebug.Enabled = true;
                _xrayNavDebug.Solid = _xraySolid;
                _xrayNavDebug.Palette = 1;
                _xrayNavDebug.FadeStart = _xrayViewDistance * 0.65f;
                _xrayNavDebug.FadeEnd = _xrayViewDistance;
                // Lifted a hand above the floors it was voxelised from, so blue
                // reads OVER green instead of z-fighting it.
                _xrayNavDebug.Render(_window.Camera, 0f, new Vector3(0f, 0f, XrayNavLift));
            }
        }

        // Always mark where the controller actually is: with the world dark
        // and the camera free, "where am I" stops being obvious.
        if (_xrayDebug is not null && _controller is not null)
            _xrayDebug.RenderPlayerMarker(
                _window.Camera,
                _controller.Position,
                _config.Movement.Radius,
                _config.Movement.Height,
                _controller.Yaw);
    }

    // ── creator panel ────────────────────────────────────────────────────────

    private partial void RegisterCreatorXraySections()
    {
        CreatorSection("XRay", "xray-mesh", "Server collision (vmaps)", true, DrawXrayMeshSection);
        CreatorSection("XRay", "xray-nav", "Bot navmesh (mmaps)", true, DrawXrayNavSection);
        CreatorSection("XRay", "xray-layers", "World layers", true, DrawXrayLayersSection);
    }

    private void DrawXrayNavSection()
    {
        if (!_config.HasMmaps)
        {
            ImGui.TextWrapped("No mmaps configured. Add \"mmapPath\": \"GameData\\\\mmaps\" to " +
                              "client.json and copy tiles from the server's run/data/mmaps " +
                              "(scp handles it; ~2 MB per tile).");
            return;
        }

        bool nav = _xrayShowNav;
        if (ImGui.Checkbox("Show navmesh", ref nav))
        {
            _xrayShowNav = nav;
            if (nav && !_xrayActive) SetXrayActive(true);
            if (nav && _xrayNavWorld is null && _xrayNavBuildTask is null) BeginXrayNavBuild();
        }
        ImGui.SameLine();
        ImGui.TextDisabled(_xrayNavWorld is { } w ? $"{w.TriangleCount:N0} tris" : "");

        if (_xrayNavStatus is { } status) ImGui.TextWrapped(status);
        if (_xrayNavBuildTask is not null) ImGui.TextDisabled("build running off-thread...");

        bool vmapMesh = _xrayShowVmapMesh;
        if (ImGui.Checkbox("Show vmap mesh alongside", ref vmapMesh)) _xrayShowVmapMesh = vmapMesh;

        if (CreatorButton("Rebuild navmesh")) BeginXrayNavBuild();

        ImGui.Spacing();
        ImGui.TextWrapped("BLUE = where bots can path (drawn a hand above the floor). " +
                          "Blue missing over walkable ground = bots detour or stall there. " +
                          "Blue present where green vmap floor is missing = the server will " +
                          "path a bot over a surface it cannot stand on - the fall-through recipe.");
    }

    private void DrawXrayMeshSection()
    {
        if (!_config.HasVmaps)
        {
            ImGui.TextWrapped("No vmaps configured. Point client.json vmapPath at the " +
                              "server's vmaps directory (setup-vmaps.ps1 copies them to " +
                              "GameData\\vmaps) and restart.");
            return;
        }

        bool active = _xrayActive;
        if (ImGui.Checkbox("X-Ray active", ref active)) SetXrayActive(active);
        ImGui.SameLine();
        ImGui.TextDisabled(_xrayWorld is { } w ? $"{w.TriangleCount:N0} tris" : "");

        if (_xrayStatus is { } status) ImGui.TextWrapped(status);
        if (_xrayBuildTask is not null) ImGui.TextDisabled("build running off-thread...");

        bool solid = _xraySolid;
        if (ImGui.Checkbox("Opaque (off = wireframe)", ref solid)) _xraySolid = solid;

        bool m2 = _xrayIncludeM2;
        if (ImGui.Checkbox("Include M2 collision (trees, fences)", ref m2))
        {
            _xrayIncludeM2 = m2;
            BeginXrayBuild();   // the loaded world baked the other choice
        }

        bool auto = _xrayAutoRebuild;
        if (ImGui.Checkbox("Rebuild as tiles stream in", ref auto)) _xrayAutoRebuild = auto;

        ImGui.SetNextItemWidth(CreatorControlWidth);
        ImGui.SliderFloat("View distance", ref _xrayViewDistance, 100f, 2000f, "%.0f yd");

        if (CreatorButton("Rebuild now")) BeginXrayBuild();

        ImGui.Spacing();
        ImGui.TextWrapped("Green = standable, red = wall (the controller's real slope " +
                          "limit). BLACK VOID where a floor should be = the server has " +
                          "no collision there - that is the hole. Open outdoor ground " +
                          "is SUPPOSED to be void: terrain height comes from maps/*.map, " +
                          "not vmaps. Chase missing floors in buildings, bridges, docks, " +
                          "caves and cities.");
    }

    // ── scripted probe ───────────────────────────────────────────────────────
    // MSUI_XRAY_PROBE=1: boot straight into the creator world, switch x-ray on,
    // wait for the vmap build to land, screenshot (dumps/gameplay-xray-probe-*),
    // quit. The repeatable "does the black-void view actually draw" check, in
    // the creator-probe pattern (Creator.Probe.cs).

    private static readonly bool XrayProbeArmed =
        Environment.GetEnvironmentVariable("MSUI_XRAY_PROBE") is not null;

    private int _xrayProbeStage;
    private double _xrayProbeStageAt;

    private void UpdateXrayProbe()
    {
        if (!XrayProbeArmed) return;
        double now = NowSeconds();

        switch (_xrayProbeStage)
        {
            case 0:   // boot: enter the creator world as soon as GL is up
                if (_gl is null || _worldLoadStarted) return;
                if (now - _xrayProbeStageAt < 1.0) { return; }
                Console.WriteLine("[xray-probe] entering creator world");
                EnterOfflineWorld();
                _xrayProbeStage = 1;
                _xrayProbeStageAt = now;
                return;

            case 1:   // world loaded: switch x-ray on
                if (_worldLoading || !_creatorWorldRequested) return;
                if (now - _xrayProbeStageAt < 2.0) return;
                // "stay": stop staging after world entry - an interactive
                // creator session for hand-driving input, no screenshot/quit.
                if (Environment.GetEnvironmentVariable("MSUI_XRAY_PROBE") == "stay")
                {
                    Console.WriteLine("[xray-probe] world entered, staying interactive");
                    _xrayProbeStage = 5;
                    return;
                }
                // "sound": world-soundscape verification - enter the world, let
                // the music transport and ambience bed run 20 s, dump the audio
                // journal, quit. Audio is invisible in a screenshot; the journal
                // IS the evidence.
                if (Environment.GetEnvironmentVariable("MSUI_XRAY_PROBE") == "sound")
                {
                    _xrayProbeStage = 8;
                    _xrayProbeStageAt = now;
                    return;
                }
                // "night": lighting A/B instead of x-ray - pin midnight, shoot
                // both lighting modes, quit. Compares against a real 1.12
                // night capture without hand-driving the clock.
                if (Environment.GetEnvironmentVariable("MSUI_XRAY_PROBE") == "night")
                {
                    Console.WriteLine("[night-probe] pinning midnight");
                    _atmosphere.TimeOfDayHours = 0f;
                    _devTimePin = true;
                    _atmosphere.Mode = Engine.LightingMode.Msui;
                    _xrayProbeStage = 6;
                    _xrayProbeStageAt = now;
                    return;
                }
                Console.WriteLine("[xray-probe] activating x-ray");
                SetXrayActive(true);
                if (_config.HasMmaps)
                {
                    _xrayShowNav = true;
                    BeginXrayNavBuild();
                }
                _xrayProbeStage = 2;
                _xrayProbeStageAt = now;
                return;

            case 2:   // build landed and uploaded: give it a beat, then screenshot
                if (now - _xrayProbeStageAt > 90.0)
                {
                    Console.WriteLine($"[xray-probe] FAIL: build never landed - {_xrayStatus}");
                    _quitRequested = true;
                    _xrayProbeStage = 4;
                    return;
                }
                if (_xrayWorld is null || _xrayUploadPending || _xrayBuildTask is not null) return;
                if (_xrayDebug is null || _xrayDebug.TriangleCount == 0) return;
                if (_config.HasMmaps &&
                    (_xrayNavUploadPending || _xrayNavBuildTask is not null ||
                     (_xrayNavWorld is not null && _xrayNavDebug?.TriangleCount == 0))) return;
                Console.WriteLine($"[xray-probe] mesh live: {_xrayStatus}");
                if (_config.HasMmaps) Console.WriteLine($"[xray-probe] navmesh: {_xrayNavStatus}");
                _currentVantage = "xray-probe";
                ArmGameplayDump();
                _xrayProbeStage = 3;
                _xrayProbeStageAt = now;
                return;

            case 3:   // linger long enough for the dump to flush, then quit
                if (now - _xrayProbeStageAt < 2.0) return;
                Console.WriteLine("[xray-probe] done, quitting");
                Console.Out.Flush();
                _quitRequested = true;
                _xrayProbeStage = 4;
                return;

            case 6:   // night A/B shot 1: MSUI mode at midnight, settled
                if (now - _xrayProbeStageAt < 8.0) return;
                Console.WriteLine("[night-probe] capturing MSUI mode");
                World.DayNightCycle.DumpRaw(_config.ClientDataPath);
                PrintLightProbe();
                Console.WriteLine($"[night-probe] applied sunDir {_atmosphere.SunDirection} " +
                                  $"sun {_atmosphere.SunColor} x{_atmosphere.SunIntensity:F2} " +
                                  $"ambient {_atmosphere.AmbientColor} x{_atmosphere.AmbientIntensity:F2} " +
                                  $"fog {_atmosphere.FogColor}");
                _currentVantage = "night-msui";
                ArmGameplayDump();
                _atmosphere.Mode = Engine.LightingMode.Parity112;
                _xrayProbeStage = 7;
                _xrayProbeStageAt = now;
                return;

            case 8:   // sound probe: let the soundscape run, then report.
                // 40 s: long enough to prove an mp3 track survives past the
                // ~10 s DirectShow stall the message pump exists to prevent.
                if (now - _xrayProbeStageAt < 40.0) return;
                Console.WriteLine($"[sound-probe] soundscape: {_soundscape?.Status ?? "NOT CREATED"}");
                Console.WriteLine($"[sound-probe] area={_soundscapeAreaId} " +
                                  $"interior=({_soundscapeInterior.Music},{_soundscapeInterior.Ambience},{_soundscapeInterior.Intro})");
                if (_spellSounds is not null)
                    foreach (var entry in _spellSounds.JournalSnapshot()
                                 .Where(j => j.Category is "music" or "ambience").TakeLast(10))
                        Console.WriteLine($"[sound-probe] {entry.Category}: kit {entry.SoundId} " +
                                          $"'{entry.ResolvedPath}' loop={entry.Looping}");
                _quitRequested = true;
                _xrayProbeStage = 4;
                return;

            case 7:   // night A/B shot 2: Parity112 at midnight
                if (now - _xrayProbeStageAt < 2.0) return;
                Console.WriteLine("[night-probe] capturing Parity112 mode");
                Console.WriteLine($"[night-probe] applied sunDir {_atmosphere.SunDirection} " +
                                  $"sun {_atmosphere.SunColor} x{_atmosphere.SunIntensity:F2} " +
                                  $"ambient {_atmosphere.AmbientColor} x{_atmosphere.AmbientIntensity:F2} " +
                                  $"fog {_atmosphere.FogColor}");
                _currentVantage = "night-parity";
                ArmGameplayDump();
                // RESTORE the persisted mode before quitting. The settings
                // capture mirrors _atmosphere.Mode back into settings.json on
                // exit, so leaving the probe's transient Parity112 in place
                // PERSISTED it - and the owner's next ordinary boot came up in
                // the dark-blue parity night they never chose (2026-08-14, the
                // "why did it go dark" incident). A probe must never be able
                // to change what the owner sees tomorrow.
                _atmosphere.Mode = Settings.Lighting.Mode;
                _xrayProbeStage = 3;   // reuse the flush-then-quit tail
                _xrayProbeStageAt = now;
                return;
        }
    }

    private void DrawXrayLayersSection()
    {
        string[] terrainModes = { "Hidden (black void)", "Wireframe reference", "Textured" };
        ImGui.SetNextItemWidth(CreatorControlWidth);
        ImGui.Combo("Terrain", ref _xrayTerrainMode, terrainModes, terrainModes.Length);

        bool b = _xrayShowBuildings;
        if (ImGui.Checkbox("Buildings (WMO)", ref b)) _xrayShowBuildings = b;
        bool d = _xrayShowDoodads;
        if (ImGui.Checkbox("Doodads (M2 - trees, props)", ref d)) _xrayShowDoodads = d;
        bool f = _xrayShowFoliage;
        if (ImGui.Checkbox("Foliage clutter", ref f)) _xrayShowFoliage = f;
        bool wtr = _xrayShowWater;
        if (ImGui.Checkbox("Water", ref wtr)) _xrayShowWater = wtr;
        bool sky = _xrayShowSky;
        if (ImGui.Checkbox("Sky (off = black background)", ref sky)) _xrayShowSky = sky;

        ImGui.TextDisabled("Layers apply only while X-Ray is active; switching it off\n" +
                           "restores the world exactly as it was.");
    }
}

using System.Numerics;
using System.Diagnostics;
using ImGuiNET;
using Silk.NET.Input;
using Silk.NET.OpenGL;
using MSUIClient.Engine;
using MSUIClient.Formats;
using MSUIClient.Player;
using MSUIClient.World;
using MSUIClient.World.Collision;
using MSUIClient.World.Doodads;
using MSUIClient.World.Units;
using MSUIClient.World.Wmo;

namespace MSUIClient;

/// <summary>
/// MSUI Client — native C# client for VMaNGOS 1.12.1 (client build 5875).
///
/// Phase 1: load Northshire straight out of the local MPQs and walk around it.
/// No asset server, no bake, no HTTP, no coordinate conversion.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        Console.WriteLine("MSUI Client — VMaNGOS 1.12.1 (build 5875)");
        Console.WriteLine();

        ClientConfig config;
        try
        {
            config = ClientConfig.Load(args.Length > 0 ? args[0] : null);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[config] {ex.Message}");
            return 1;
        }

        Console.WriteLine($"[start] map {config.Start.Map} ({config.Start.MapName}) " +
                          $"at ({config.Start.X:F1}, {config.Start.Y:F1}, {config.Start.Z:F1})");

        using var window = new ClientWindow(config);
        var game = new GameLoop(window, config);

        window.OnLoad += game.Load;
        window.OnUpdate += game.Update;
        window.OnRender += game.Render;
        window.OnGui += game.Gui;

        try
        {
            window.Run();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[fatal] {ex}");
            return 2;
        }

        game.Dispose();
        return 0;
    }
}

/// <summary>
/// Phase 1 loop: a character walking on real terrain, with vmap collision when
/// the vmaps are present.
///
/// The camera orbits the character rather than flying independently — its Target
/// is the character's feet and Camera.EyeHeight lifts the look-at point. Camera
/// yaw is the single source of facing: it feeds straight into the controller as
/// the character's orientation, which is also the value a movement packet wants
/// in Phase 2. Nothing is converted anywhere.
///
/// Free-fly survives as an F-key toggle. It is the fastest way to tell a
/// movement bug from a world bug: if a place looks wrong on foot, fly to it.
/// </summary>
public sealed class GameLoop : IDisposable
{
    private readonly ClientWindow _window;
    private readonly ClientConfig _config;

    private TerrainRenderer? _terrain;
    private CharacterController? _controller;
    private CollisionWorld? _collision;
    private VmapCollisionLoader? _vmaps;
    private MpqMount? _mpq;
    private GpuUploadWorker? _uploads;
    private AssetWorkerPool? _assetWorkers;
    private WmoRenderer? _wmo;
    private DoodadRenderer? _doodads;
    private AdtCache? _adts;
    private CollisionDebugRenderer? _collisionDebug;
    private CharacterRenderer? _character;

    /// <summary>Edge detection for the fly toggle — IsDown reports held, not pressed.</summary>
    private bool _flyKeyDown;
    private bool _collisionKeyDown;

    /// <summary>
    /// Draw the character capsule. OFF now that there is a model - the capsule
    /// is drawn solid and on top, so leaving it on hides the thing it was built
    /// to help verify. Tick "Show player capsule" in the HUD to bring it back;
    /// it is still the fastest way to confirm the model stands where the physics
    /// thinks it does.
    /// </summary>
    private bool _showPlayerMarker;

    /// <summary>Keyboard turn rate, radians per second. About 160 degrees a second.</summary>
    private float _turnSpeed = 2.8f;

    /// <summary>Whether the Tier 1 set is on. Toggling re-composites the atlas.</summary>
    private bool _dressed = true;


    /// <summary>Last frame's walk modifier, so the animator can pick Walk over Run.</summary>
    private bool _walking;

    private double _collisionBuildSeconds;
    private Task<(int Generation, CollisionWorld World, double Seconds)>? _collisionBuildTask;
    private int _collisionGeneration;
    private double _lastStreamSeconds;
    private (int col, int row)? _residentCentre;
    private bool _preloadWmoFirst;

    // A player can stand half a tile diagonal from its centre. Keeping objects
    // for that reach plus draw distance and a small large-model margin means a
    // tile transition never reveals an object that was not resident already.
    private float ObjectResidencyRadius
        => (_doodads?.DrawDistance ?? _config.Render.DoodadDistance)
         + TerrainRenderer.GridSize * 0.7071068f + 50f;

    private int WmoPreloadRadius
        => Math.Max(_config.Start.TileRadius + 1, _config.Start.WmoPreloadRadius);

    public GameLoop(ClientWindow window, ClientConfig config)
    {
        _window = window;
        _config = config;
    }

    public void Load(GL gl)
    {
        var startup = Stopwatch.StartNew();
        var phase = Stopwatch.StartNew();

        void PhaseComplete(string name)
        {
            Console.WriteLine($"[startup] {name,-25} {phase.Elapsed.TotalSeconds,6:F2}s " +
                              $"(total {startup.Elapsed.TotalSeconds,6:F2}s)");
            phase.Restart();
        }

        _window.Camera.Target = new Vector3(_config.Start.X, _config.Start.Y, _config.Start.Z);
        _window.Camera.Yaw = _config.Start.Orientation;

        // Mount the archives once and point AdtTerrainReader's extractor hook
        // at them. Without this every file read reopens up to fifteen MPQs,
        // which is where startup was going.
        _mpq = new MpqMount(_config.ClientDataPath);
        AdtTerrainReader.StormLibExtractor = _mpq.ReadFile;
        PhaseComplete("MPQ mount");

        _uploads = _window.CreateGpuUploadWorker();
        _assetWorkers = new AssetWorkerPool();
        Console.WriteLine("[stream] dedicated shared-context GPU uploader ready");

        _terrain = new TerrainRenderer(gl, _config, _uploads, _assetWorkers);

        // Shaders are copied next to the exe by the csproj; fall back to the
        // source tree so editing a .frag and hitting F5 picks it up.
        var shaderDir = Path.Combine(AppContext.BaseDirectory, "Shaders");
        if (!File.Exists(Path.Combine(shaderDir, "terrain.vert")))
            shaderDir = Path.Combine(_config.RepoRoot, "MSUIClient", "Shaders");
        _terrain.LoadShaders(shaderDir);

        // One parse per tile, shared by terrain, buildings and doodads.
        _adts = new AdtCache(_config.ClientDataPath, _config.Start.MapName);
        PhaseComplete("render setup");

        _terrain.LoadAround(_config.Start.X, _config.Start.Y, _config.Start.TileRadius, _adts);
        _residentCentre = TerrainRenderer.TileAt(_config.Start.X, _config.Start.Y);
        _terrain.QueuePreload(
            TerrainRenderer.TileRing(
                _residentCentre.Value.col, _residentCentre.Value.row,
                _config.Start.TileRadius + 1),
            _adts);

        // Self-check against the value the server independently agreed with.
        _terrain.VerifyAgainst(_config.Start.X, _config.Start.Y, _config.Start.Z);
        PhaseComplete("terrain");

        // Buildings BEFORE collision now: when collision comes from client
        // geometry, the buildings are its source.
        try
        {
            _wmo = new WmoRenderer(gl, _config, _uploads, _assetWorkers);
            _wmo.LoadShaders(shaderDir);
            _wmo.LoadForTiles(_terrain.LoadedTiles, _adts);

            // Pay the first outer-ring cost behind startup, not while walking.
            // Later rings are queued one model at a time while still at least
            // one full tile beyond the resident terrain block.
            var preloadRing = TerrainRenderer.TileRing(
                _residentCentre.Value.col, _residentCentre.Value.row, WmoPreloadRadius);
            _wmo.QueuePreloadForTiles(preloadRing, _adts);
            _wmo.DrainPreloads();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[wmo] FAILED - {ex.Message}");
            _wmo = null;
        }
        PhaseComplete("buildings");

        if (_config.Render.Doodads)
        {
            try
            {
                _doodads = new DoodadRenderer(gl, _config, _uploads, _assetWorkers)
                {
                    DrawDistance = _config.Render.DoodadDistance,
                    CollisionBasisIndex = _config.Render.DoodadCollisionBasis,
                };
                _doodads.LoadShaders(shaderDir);

                var preloadRing = TerrainRenderer.TileRing(
                    _residentCentre.Value.col, _residentCentre.Value.row, WmoPreloadRadius);
                _doodads.QueuePreloadForTiles(preloadRing, _adts);
                if (_wmo is not null)
                    _doodads.QueuePreloadModels(_wmo.TakeNewDoodadModelPaths());
                _doodads.DrainPreloads();

                PopulateDoodads(_residentCentre.Value, reportDiagnostics: true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[doodad] FAILED - {ex.Message}");
                _doodads = null;
            }
        }
        PhaseComplete("doodads (all)");

        // Keep the outer preload ADTs in RAM too. This is deliberate: the user
        // prefers a larger working set over boundary stalls.
        var initialRing = TerrainRenderer.TileRing(
            _residentCentre.Value.col, _residentCentre.Value.row, WmoPreloadRadius);
        _adts.Retain(initialRing);
        Console.WriteLine($"[adt] {_adts.Parses} parse(s), {_adts.Hits} reuse(s) - " +
                          $"retaining {_adts.HeldTiles} resident tile(s)");
        _mpq?.Report();

        LoadCollision();
        PhaseComplete("collision world");

        _controller = new CharacterController(_terrain, _config.Movement)
        {
            Collision = _collision,
            Yaw = _config.Start.Orientation,
        };

        // Spawn on the ground rather than at the config Z, which is the server's
        // spawn height and can differ from the sampled surface by a few cm.
        float? ground = _terrain.SampleHeight(_config.Start.X, _config.Start.Y);
        _controller.Teleport(_config.Start.X, _config.Start.Y, ground ?? _config.Start.Z);

        _window.Camera.Target = _controller.Position;
        PhaseComplete("controller + spawn");

        // The character model. After the controller, because it renders what
        // the controller decides; try/caught like the buildings, because a
        // missing model must not cost us a walkable world.
        try
        {
            _character = new CharacterRenderer(gl, _config);
            _character.LoadShaders(shaderDir);

            if (!_character.Load("Human", "Male"))
            {
                _character.Dispose();
                _character = null;
            }
            else
            {
                // Tier 1 warrior. The body-atlas pieces should appear; the helm,
                // pauldrons, sword and shield are separate M2 models and will
                // log as needing the attachment path, which is not built yet.
                _character.Equipment = CharacterEquipment.BattlegearOfMight();
                _character.ApplyEquipment();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[character] FAILED - {ex.Message}");
            _character = null;
        }
        PhaseComplete("character + equipment");

        if (_collision is not null)
        {
            try
            {
                _collisionDebug = new CollisionDebugRenderer(gl);
                _collisionDebug.LoadShaders(shaderDir);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[collision] debug renderer FAILED - {ex.Message}");
                _collisionDebug = null;
            }
        }
        PhaseComplete("debug setup (deferred)");

        CompareWmoToCollision();
        PhaseComplete("alignment checks");

        Console.WriteLine($"[game] ready in {startup.Elapsed.TotalSeconds:F2}s");
    }

    private void PopulateDoodads((int col, int row) centreTile, bool reportDiagnostics)
    {
        if (_doodads is null || _terrain is null || _adts is null) return;

        Vector2 centre = TerrainRenderer.TileCenter(centreTile.col, centreTile.row);
        float radius = ObjectResidencyRadius;

        _doodads.LoadForTiles(
            _terrain.LoadedTiles, _adts, centre, radius, reportDiagnostics);

        // Furniture. A huge WMO can touch the terrain ring while most of its
        // MODD placements are far outside doodad draw range. Resolve only the
        // furniture that could become visible before the next tile crossing.
        if (_wmo is null) return;

        var interiors = Stopwatch.StartNew();
        int requested = 0, placed = 0;
        foreach (var (path, transform) in _wmo.EnumerateDoodads(centre, radius))
        {
            requested++;
            if (_doodads.AddPlaced(path, transform)) placed++;
        }

        if (requested > 0)
            _doodads.ReportInterior(requested, placed, interiors.Elapsed.TotalSeconds);

        Console.WriteLine($"[stream] object residency [{centreTile.col},{centreTile.row}] " +
                          $"radius {radius:F0} yd");
    }

    private void UpdateWorldResidency()
    {
        if (_controller is null || _terrain is null || _adts is null) return;

        var next = TerrainRenderer.TileAt(_controller.Position.X, _controller.Position.Y);
        if (_residentCentre == next) return;

        var terrainLead = TerrainRenderer.TileRing(
            next.col, next.row, _config.Start.TileRadius + 1);
        _terrain.QueuePreload(terrainLead, _adts);
        var desiredTerrain = TerrainRenderer.TileRing(
            next.col, next.row, _config.Start.TileRadius);
        if (!_terrain.PreloadReady(desiredTerrain)) return;

        var timer = Stopwatch.StartNew();
        Console.WriteLine($"[stream] crossing to tile [{next.col},{next.row}]");

        try
        {
            _terrain.SetResidency(next.col, next.row, _config.Start.TileRadius, _adts);

            _wmo?.ResetPlacements();
            _wmo?.LoadForTiles(_terrain.LoadedTiles, _adts);

            var preloadRing = TerrainRenderer.TileRing(next.col, next.row, WmoPreloadRadius);
            _wmo?.QueuePreloadForTiles(preloadRing, _adts);
            _doodads?.QueuePreloadForTiles(preloadRing, _adts);
            if (_wmo is not null && _doodads is not null)
                _doodads.QueuePreloadModels(_wmo.TakeNewDoodadModelPaths());

            _doodads?.ResetPlacements();
            PopulateDoodads(next, reportDiagnostics: false);

            _adts.Retain(preloadRing);

            _residentCentre = next;
            BeginCollisionBuild();

            _lastStreamSeconds = timer.Elapsed.TotalSeconds;
            Console.WriteLine($"[stream] tile [{next.col},{next.row}] ready: " +
                              $"{_terrain.TileCount} terrain, {_wmo?.InstanceCount ?? 0} WMO, " +
                              $"{_doodads?.InstanceCount ?? 0} doodad placement(s), " +
                              $"{_lastStreamSeconds:F2}s");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[stream] FAILED entering [{next.col},{next.row}]: {ex.Message}");
        }
    }

    private void SetCollisionDebugEnabled(bool enabled)
    {
        if (_collisionDebug is null) return;

        if (enabled && _collisionDebug.TriangleCount == 0 && _collision is not null)
        {
            var timer = Stopwatch.StartNew();
            _collisionDebug.Build(_collision);
            Console.WriteLine($"[collision] debug upload deferred from startup: " +
                              $"{timer.Elapsed.TotalSeconds:F2}s");
        }

        _collisionDebug.Enabled = enabled;
    }

    /// <summary>
    /// Cross-check the rendered buildings against the collision meshes of the
    /// same buildings.
    ///
    /// These arrive by two completely independent routes — MODF placements read
    /// out of the ADT and transformed here, versus vmtile spawns extracted by
    /// the server and transformed by the collision loader. They describe the
    /// same objects, so any disagreement means one transform is wrong, and the
    /// SHAPE of the disagreement says which.
    ///
    /// A constant offset across every building points at a systematic error in
    /// one of the two placement chains — fixable in one line. Deltas that vary
    /// per building, especially with rotation, point at the rotation
    /// convention instead. Either way this turns "collision feels a few yards
    /// off" into a vector.
    /// </summary>
    /// <summary>
    /// Draw, in yellow and over everything, the exact triangles the character
    /// controller is standing on and blocked by — pulled by index from the same
    /// array the raycast intersected.
    ///
    /// The bulk wireframe answers "where is the collision world". This answers
    /// "where is the surface I am actually standing on", which is the only
    /// question that matters when movement disagrees with the picture.
    /// </summary>
    private void HighlightPhysicsTriangles()
    {
        if (_collisionDebug is null || _collision is null || _controller is null) return;
        if (!_collisionDebug.Enabled) return;

        var corners = new List<Vector3>(6);

        if (_collision.TryGetTriangle(_controller.GroundTriangle, out var a, out var b, out var c))
        {
            corners.Add(a); corners.Add(b); corners.Add(c);
        }

        if (_controller.HasBlock &&
            _collision.TryGetTriangle(_controller.LastBlockTriangle, out var d, out var e, out var f))
        {
            corners.Add(d); corners.Add(e); corners.Add(f);
        }

        if (corners.Count >= 3) _collisionDebug.RenderHighlight(_window.Camera, corners);
    }

    private void CompareWmoToCollision()
    {
        if (_wmo is null || _vmaps is null || _vmaps.WmoSpawnBounds.Count == 0) return;

        var deltas = new List<Vector3>();
        var originDeltas = new List<Vector3>();

        foreach (var (path, rMin, rMax, rOrigin) in _wmo.Placements)
        {
            string name = Path.GetFileName(path);
            var renderCentre = (rMin + rMax) * 0.5f;

            // Match by model name, then by proximity — a model placed several
            // times needs the nearest instance, not the first one.
            (string Name, Vector3 Min, Vector3 Max, Vector3 Origin)? best = null;
            float bestDistance = float.MaxValue;

            foreach (var spawn in _vmaps.WmoSpawnBounds)
            {
                if (!string.Equals(Path.GetFileName(spawn.Name), name, StringComparison.OrdinalIgnoreCase))
                    continue;

                float d = (((spawn.Min + spawn.Max) * 0.5f) - renderCentre).Length();
                if (d >= bestDistance) continue;

                bestDistance = d;
                best = spawn;
            }

            if (best is null) continue;

            var collisionCentre = (best.Value.Min + best.Value.Max) * 0.5f;
            var delta = collisionCentre - renderCentre;
            deltas.Add(delta);

            // ORIGIN vs ORIGIN. Bounding boxes depend on which triangles each
            // extractor kept, so they can never separate "the mesh is
            // different" from "the placement is different". The origins are
            // pure placement: the same building's MODF entry and its vmtile
            // spawn describe one position, so any difference here is a
            // transform bug and nothing else.
            var originDelta = best.Value.Origin - rOrigin;
            if (originDelta.Length() > 0.05f)
                Console.WriteLine(
                    $"[align] {name,-28} ORIGIN differs by ({originDelta.X,7:F2}," +
                    $"{originDelta.Y,7:F2},{originDelta.Z,7:F2}) = {originDelta.Length():F2} yd");
            originDeltas.Add(originDelta);

            if (delta.Length() > 1.5f)
            {
                // Size as well as centre. If the two footprints are the same
                // size but offset, the error is a translation. If the sizes
                // differ, the geometry is rotated differently and no amount of
                // shifting will line them up.
                var renderSize = rMax - rMin;
                var collisionSize = best.Value.Max - best.Value.Min;
                var sizeDelta = collisionSize - renderSize;

                Console.WriteLine(
                    $"[align] {name,-28} centre ({delta.X,7:F2},{delta.Y,7:F2},{delta.Z,7:F2}) " +
                    $"{delta.Length(),6:F2} yd   size ({sizeDelta.X,7:F2},{sizeDelta.Y,7:F2},{sizeDelta.Z,7:F2})");
            }
        }

        if (deltas.Count == 0)
        {
            Console.WriteLine("[align] no buildings matched between render and collision");
            return;
        }

        var mean = deltas.Aggregate(Vector3.Zero, (a, d) => a + d) / deltas.Count;
        double spread = deltas.Average(d => (double)(d - mean).Length());

        Console.WriteLine(
            $"[align] {deltas.Count} building(s) compared: mean offset " +
            $"({mean.X:F2}, {mean.Y:F2}, {mean.Z:F2}), magnitude {mean.Length():F2} yd, " +
            $"spread {spread:F2} yd");

        Console.WriteLine(spread < 1.0
            ? "[align] centre offset is consistent across buildings"
            : "[align] centre offset varies - expected, since the two meshes are not the same triangles");

        if (originDeltas.Count > 0)
        {
            var originMean = originDeltas.Aggregate(Vector3.Zero, (a, d) => a + d) / originDeltas.Count;
            float worst = originDeltas.Max(d => d.Length());

            Console.WriteLine(
                $"[align] ORIGINS: mean ({originMean.X:F3}, {originMean.Y:F3}, {originMean.Z:F3}), " +
                $"worst {worst:F3} yd");

            Console.WriteLine(worst < 0.05f
                ? "[align] origins AGREE - both chains place buildings at the same point, so any "
                  + "remaining misalignment is ROTATION or the mesh itself"
                : "[align] origins DISAGREE - one chain's translation is wrong, and that is the bug");
        }
    }

    /// <summary>
    /// Load vmap collision for exactly the tiles terrain loaded. Every failure
    /// here is non-fatal and printed: without vmaps you still walk on terrain,
    /// you just walk through buildings.
    /// </summary>
    private void LoadCollision()
    {
        if (_terrain is null) return;

        _collision = null;
        _vmaps = null;
        if (_controller is not null) _controller.Collision = null;
        _collisionDebug?.Clear();

        if (!_config.Movement.Collision)
        {
            Console.WriteLine("[collision] disabled in config (movement.collision = false)");
            return;
        }

        bool useClient = !string.Equals(_config.Movement.CollisionSource, "vmaps",
            StringComparison.OrdinalIgnoreCase);

        if (!useClient && !_config.HasVmaps)
        {
            Console.WriteLine("[collision] collisionSource is vmaps but none are configured — terrain only");
            return;
        }

        var started = DateTime.UtcNow;

        try
        {
            _collision = new CollisionWorld();

            if (useClient)
            {
                // The buildings the renderer already loaded. No second parse,
                // no second transform, no GameData\vmaps needed.
                if (_wmo is null)
                {
                    Console.WriteLine("[collision] no buildings loaded — terrain only");
                    _collision = null;
                    return;
                }

                _wmo.AppendCollision(_collision);
                _doodads?.AppendCollision(_collision);
            }
            else
            {
                _vmaps = new VmapCollisionLoader(_config.VmapPath!);

                foreach (var (col, row) in _terrain.LoadedTiles)
                    _vmaps.LoadTile(_collision, _config.Start.Map, col, row, _config.Movement.IncludeM2);
            }

            _collision.Build();
            _collisionBuildSeconds = (DateTime.UtcNow - started).TotalSeconds;

            if (_vmaps is not null) Console.WriteLine($"[collision] {_vmaps.Summary()}");
            Console.WriteLine(
                $"[collision] BVH {_collision.NodeCount:N0} nodes over " +
                $"{_collision.TriangleCount:N0} triangles, " +
                $"{_collision.DegenerateSkipped} degenerate skipped, " +
                $"{_collisionBuildSeconds:F1}s");

            // Bounds are the cheapest possible check that the spawn transform is
            // right: if this box does not straddle the loaded tiles, the geometry
            // is real but in the wrong place, and no amount of walking into
            // things will reveal that.
            var lo = _collision.BoundsMin;
            var hi = _collision.BoundsMax;
            Console.WriteLine(
                $"[collision] bounds X {lo.X:F0}..{hi.X:F0}  Y {lo.Y:F0}..{hi.Y:F0}  Z {lo.Z:F0}..{hi.Z:F0}");

            if (_collision.IsEmpty)
            {
                Console.WriteLine("[collision] WARNING no geometry loaded — check the unresolved names above");
                _collision = null;
            }

            if (_controller is not null) _controller.Collision = _collision;
            if (_collisionDebug is { Enabled: true } && _collision is not null)
                _collisionDebug.Build(_collision);
        }
        catch (Exception ex)
        {
            // Loudly. A silent failure here would present later as a physics bug.
            Console.WriteLine($"[collision] FAILED — {ex.Message}");
            Console.WriteLine("[collision] continuing with terrain collision only");
            _collision = null;
        }
    }

    /// <summary>
    /// Rebuild client-geometry collision without stopping movement. Triangle
    /// collection is a bounded snapshot on the render thread; the expensive
    /// BVH partition/sort runs on a worker while the previous world remains
    /// attached to the controller.
    /// </summary>
    private void BeginCollisionBuild()
    {
        bool useClient = !string.Equals(_config.Movement.CollisionSource, "vmaps",
            StringComparison.OrdinalIgnoreCase);
        if (!_config.Movement.Collision || !useClient || _wmo is null)
        {
            LoadCollision();
            return;
        }

        var next = new CollisionWorld();
        _wmo.AppendCollision(next);
        _doodads?.AppendCollision(next);
        _collisionDebug?.Clear();

        int generation = ++_collisionGeneration;
        _collisionBuildTask = Task.Run(() =>
        {
            var timer = Stopwatch.StartNew();
            next.Build();
            return (generation, next, timer.Elapsed.TotalSeconds);
        });
    }

    private void AcceptReadyCollision()
    {
        if (_collisionBuildTask is not { IsCompleted: true } task) return;
        _collisionBuildTask = null;

        try
        {
            var ready = task.GetAwaiter().GetResult();
            if (ready.Generation != _collisionGeneration) return;

            _collision = ready.World.IsEmpty ? null : ready.World;
            _collisionBuildSeconds = ready.Seconds;
            if (_controller is not null) _controller.Collision = _collision;

            Console.WriteLine(
                $"[collision-async] BVH {ready.World.NodeCount:N0} nodes over " +
                $"{ready.World.TriangleCount:N0} triangles, {ready.Seconds:F2}s off-thread");

            if (_collision is not null)
            {
                var lo = _collision.BoundsMin;
                var hi = _collision.BoundsMax;
                Console.WriteLine(
                    $"[collision-async] bounds X {lo.X:F0}..{hi.X:F0}  " +
                    $"Y {lo.Y:F0}..{hi.Y:F0}  Z {lo.Z:F0}..{hi.Z:F0}");
                if (_collisionDebug is { Enabled: true }) _collisionDebug.Build(_collision);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[collision-async] FAILED - {ex.Message}; keeping previous collision");
        }
    }

    public void Update(float dt)
    {
        if (_controller is null) return;
        _terrain?.PumpPreloads();
        AcceptReadyCollision();

        // F toggles free-fly. Edge-triggered so holding it doesn't strobe.
        bool flyKey = _window.IsDown(Key.F);
        if (flyKey && !_flyKeyDown)
        {
            _controller.Flying = !_controller.Flying;
            Console.WriteLine($"[move] {(_controller.Flying ? "flying" : "walking")}");
        }
        _flyKeyDown = flyKey;

        // C toggles the collision wireframe. Edge-triggered.
        bool collisionKey = _window.IsDown(Key.C);
        if (collisionKey && !_collisionKeyDown && _collisionDebug is not null)
        {
            SetCollisionDebugEnabled(!_collisionDebug.Enabled);
            Console.WriteLine($"[collision] wireframe {(_collisionDebug.Enabled ? "on" : "off")}");
        }
        _collisionKeyDown = collisionKey;

        bool shift = _window.IsDown(Key.ShiftLeft) || _window.IsDown(Key.ShiftRight);

        // A and D TURN, they do not strafe. That is vanilla's default bind and
        // it is what the hands expect; strafe lives on Q and E.
        //
        // Camera yaw IS the character's facing here - the controller takes
        // input.Yaw straight from it - so turning the camera turns the
        // character. There is only one heading in this client and this is it.
        //
        // Holding the RIGHT mouse button swaps the two, exactly as the real
        // client does: you are already steering with the mouse, so A and D are
        // free to become strafe and your hand does not have to move to Q and E
        // mid-fight.
        bool mouseSteering = _window.MouseRightDown;

        float turn = _window.Axis(Key.Left, Key.Right);
        if (!mouseSteering) turn += _window.Axis(Key.A, Key.D);
        turn = Math.Clamp(turn, -1f, 1f);

        if (turn != 0f) _window.Camera.Rotate(turn * _turnSpeed * dt, 0f);

        float strafe = _window.Axis(Key.E, Key.Q);
        if (mouseSteering) strafe += _window.Axis(Key.D, Key.A);
        strafe = Math.Clamp(strafe, -1f, 1f);

        // Look up and down without the mouse. Rotate clamps pitch either way.
        float tilt = _window.Axis(Key.PageUp, Key.PageDown);
        if (tilt != 0f) _window.Camera.Rotate(0f, tilt * _turnSpeed * 0.6f * dt);

        // Up and down arrows walk, like vanilla. Combined with W/S rather than
        // replacing it, and clamped so holding both does not double the speed.
        float forward = Math.Clamp(
            _window.Axis(Key.W, Key.S) + _window.Axis(Key.Up, Key.Down), -1f, 1f);

        var input = new MovementInput
        {
            Forward = forward,
            Strafe = strafe,
            Up = _window.Axis(Key.Space, Key.ControlLeft),
            Yaw = _window.Camera.Yaw,
            Jump = _window.IsDown(Key.Space),
            Walking = shift && !_controller.Flying,
            Boost = shift && _controller.Flying,
        };

        _controller.Update(dt, input);
        UpdateWorldResidency();

        // WoWee gives ready assets a small main-thread integration budget.
        // Alternate priority so neither queue starves, and never begin the
        // second GL upload/build after the first has consumed this frame.
        var preloadBudget = Stopwatch.StartNew();
        if (_preloadWmoFirst) _wmo?.WarmNextPreload();
        else _doodads?.WarmNextPreload();
        if (_wmo is not null && _doodads is not null)
            _doodads.QueuePreloadModels(_wmo.TakeNewDoodadModelPaths());
        if (preloadBudget.Elapsed.TotalMilliseconds < 6)
        {
            if (_preloadWmoFirst) _doodads?.WarmNextPreload();
            else _wmo?.WarmNextPreload();
        }
        _preloadWmoFirst = !_preloadWmoFirst;

        _walking = input.Walking;
        _character?.Update(dt, BuildUnitState());

        // Moving re-centres the camera behind the character, like the real
        // client. Holding the LEFT button overrides it: you are deliberately
        // looking at yourself, and having the view fight you would be worse
        // than not having the feature.
        // Turning counts as moving here. He said "if you hit wasd it snaps you
        // back behind the character", and A and D are part of WASD.
        bool moving = MathF.Abs(input.Forward) > 0.01f
                   || MathF.Abs(input.Strafe) > 0.01f
                   || turn != 0f;

        if (moving && !_window.MouseLeftDown) _window.Camera.EaseOrbitBehind(dt);

        // The camera orbits the character's feet; Camera.EyeHeight does the rest.
        _window.Camera.Target = _controller.Position;

        ResolveCameraCollision(dt);

        if (_window.IsDown(Key.Escape)) _window.Close();
    }

    /// <summary>
    /// What the unit renderer needs to know about the player, in the same shape
    /// it will need for every other unit once packets arrive.
    /// </summary>
    private CharacterRenderer.UnitState BuildUnitState() => new()
    {
        Position = _controller?.Position ?? Vector3.Zero,
        Yaw = _controller?.Yaw ?? 0f,
        Grounded = _controller?.Grounded ?? true,
        VerticalVelocity = _controller?.Velocity.Z ?? 0f,
        FallTimeMs = _controller?.FallTimeMs ?? 0f,
        Walking = _walking,
        Flying = _controller?.Flying ?? false,
    };

    /// <summary>
    /// Keep the camera out of the world.
    ///
    /// Two probes from the eye point outward along the orbit direction: a
    /// collision raycast for buildings and trees, and a march against the
    /// terrain height grid. Whichever is closer sets the distance, floored at
    /// MinDistance.
    ///
    /// The terrain part has to be a march rather than a single test at the
    /// camera position, because the camera can clear a ridge while the straight
    /// line between it and the character passes through it — you would see the
    /// character through the hill.
    ///
    /// MinDistance (1.5) times sin(PitchLimit) is about 1.49, comfortably under
    /// EyeHeight, so even fully pitched down the pulled-in camera stays above
    /// the character's feet. Steep terrain immediately behind you can still
    /// clip; so does the real client.
    /// </summary>
    private void ResolveCameraCollision(float dt)
    {
        var cam = _window.Camera;

        if (!_config.Camera.Collision)
        {
            cam.EffectiveDistance = cam.Distance;
            return;
        }

        var eye = cam.EyeTarget;
        var dir = cam.OrbitDirection;
        float clearance = _config.Camera.Clearance;
        float allowed = cam.Distance;

        if (_collision is { IsEmpty: false })
        {
            var hit = _collision.Raycast(eye, dir, cam.Distance + clearance);
            if (hit is not null) allowed = MathF.Min(allowed, hit.Value.Distance - clearance);
        }

        if (_terrain is not null)
        {
            const int steps = 10;
            float step = cam.Distance / steps;

            for (int i = 1; i <= steps; i++)
            {
                float d = step * i;
                if (d > allowed) break;

                var p = eye + dir * d;
                float? ground = _terrain.SampleHeight(p.X, p.Y);
                if (ground is null) continue;

                if (p.Z < ground.Value + clearance)
                {
                    allowed = d - step;
                    break;
                }
            }
        }

        allowed = Math.Clamp(allowed, cam.MinDistance, cam.Distance);

        // In immediately, out gradually.
        cam.EffectiveDistance = allowed < cam.EffectiveDistance
            ? allowed
            : MathF.Min(allowed, cam.EffectiveDistance + _config.Camera.RestoreSpeed * dt);
    }

    public void Render(float dt)
    {
        if (_terrain is not null) _terrain.Render(_window.Camera);
        _wmo?.Render(_window.Camera);
        _doodads?.Render(_window.Camera);

        if (_character is not null && _controller is not null)
            _character.Render(_window.Camera, BuildUnitState());

        // Last, so it draws over the world it describes.
        _collisionDebug?.Render(
            _window.Camera,
            MathF.Cos(_config.Movement.MaxSlopeDegrees * MathF.PI / 180f),
            _collision?.Offset ?? Vector3.Zero);

        HighlightPhysicsTriangles();

        if (_showPlayerMarker && _collisionDebug is not null && _controller is not null)
            _collisionDebug.RenderPlayerMarker(
                _window.Camera,
                _controller.Position,
                _config.Movement.Radius,
                _config.Movement.Height,
                _controller.Yaw);
    }

    public void Gui()
    {
        ImGui.SetNextWindowPos(new Vector2(12, 12), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(430, 0), ImGuiCond.FirstUseEver);

        if (ImGui.Begin("MSUI Client", ImGuiWindowFlags.NoCollapse))
        {
            ImGui.Text($"{_window.Fps:F0} fps   {_window.FrameMs:F2} ms");

            bool vsync = _window.VSync;
            if (ImGui.Checkbox("VSync (prevent tearing)", ref vsync))
                _window.VSync = vsync;

            if (_terrain is not null)
            {
                ImGui.Text($"tiles {_terrain.TileCount}   drawn {_terrain.DrawnLastFrame}");
                ImGui.Text($"triangles {_terrain.TotalTriangles:N0}");
                if (_residentCentre is { } resident)
                    ImGui.Text($"resident [{resident.col},{resident.row}]  " +
                               $"objects {ObjectResidencyRadius:F0} yd  " +
                               $"last {_lastStreamSeconds:F2}s");
                if (_wmo is not null)
                    ImGui.Text($"WMO preload {WmoPreloadRadius * 2 + 1}x{WmoPreloadRadius * 2 + 1}  " +
                               $"{_wmo.PendingPreloads} queued");
                if (_doodads is not null)
                    ImGui.Text($"M2 preload {_doodads.PendingPreloads} queued");
            }

            if (_controller is not null)
            {
                var p = _controller.Position;

                ImGui.Separator();
                ImGui.Text("Position (WoW space)");
                ImGui.Text($"  X {p.X,10:F2}   north");
                ImGui.Text($"  Y {p.Y,10:F2}   west");
                ImGui.Text($"  Z {p.Z,10:F2}   up");

                var (col, row) = TerrainRenderer.TileAt(p.X, p.Y);
                ImGui.Text($"  tile [{col}, {row}]");
                ImGui.Text($"  facing {_controller.Yaw * 180f / MathF.PI,5:F0} deg");

                ImGui.Separator();
                ImGui.Text("Movement");

                ImGui.Text(_controller.GroundZ is float g
                    ? $"  ground {g,10:F2}   (delta {p.Z - g,6:F2})"
                    : "  ground     (no data)");

                // WHICH of the two is holding you up. This is the line that
                // separates a misplaced collision mesh from terrain doing it.
                ImGui.Text(_controller.TerrainGroundZ is float tz
                    ? $"    terrain   {tz,9:F2}"
                    : "    terrain     (none)");
                ImGui.Text(_controller.CollisionGroundZ is float cz
                    ? $"    collision {cz,9:F2}"
                    : "    collision   (none)");
                ImGui.TextColored(new Vector4(0.6f, 0.9f, 1f, 1f),
                    $"    standing on {_controller.GroundSource}");
                if (_controller.GroundTriangle >= 0 && _collision is not null)
                    ImGui.Text($"    surface tri {_controller.GroundTriangle} " +
                               $"({_collision.SourceOf(_controller.GroundTriangle)})");

                if (_controller.GroundProbeOffset.LengthSquared() > 1e-6f)
                    ImGui.Text($"    support probe ({_controller.GroundProbeOffset.X,5:F2}," +
                               $" {_controller.GroundProbeOffset.Y,5:F2})");

                if (_controller.GroundProbesLastFrame > 1)
                    ImGui.Text($"    support fan {_controller.GroundProbesLastFrame} probes");

                if (_controller.GroundAdhesion)
                    ImGui.TextColored(new Vector4(0.6f, 0.9f, 1f, 1f),
                        "    ground adhesion");

                ImGui.Text($"  state  {(_controller.Flying ? "flying" : _controller.Grounded ? "grounded" : "airborne")}");
                ImGui.Text($"  vz     {_controller.Velocity.Z,10:F2}");

                if (_controller.FallTimeMs > 0)
                    ImGui.Text($"  fall   {_controller.FallTimeMs,10:F0} ms");

                float groundSnap = _config.Movement.GroundSnapDistance;
                if (ImGui.SliderFloat("Ground snap down", ref groundSnap, 0f, 1.5f, "%.2f yd"))
                    _config.Movement.GroundSnapDistance = groundSnap;

                float fallDelay = _config.Movement.FallAnimationDelayMs;
                if (ImGui.SliderFloat("Fall animation delay", ref fallDelay, 0f, 500f, "%.0f ms"))
                    _config.Movement.FallAnimationDelayMs = fallDelay;

                float runSpeed = _config.Movement.RunSpeed;
                if (ImGui.SliderFloat("Run speed", ref runSpeed, 1f, 12f, "%.2f yd/s"))
                    _config.Movement.RunSpeed = runSpeed;

                float walkSpeed = _config.Movement.WalkSpeed;
                if (ImGui.SliderFloat("Walk speed", ref walkSpeed, 0.5f, 6f, "%.2f yd/s"))
                    _config.Movement.WalkSpeed = walkSpeed;

                float backwardSpeed = _config.Movement.BackwardSpeed;
                if (ImGui.SliderFloat("Backward speed", ref backwardSpeed, 0.5f, 8f, "%.2f yd/s"))
                    _config.Movement.BackwardSpeed = backwardSpeed;

                // This one matters: it is the loud version of the failure that
                // once looked like a physics bug for 23 seconds of falling.
                if (_controller.NoGroundBelow)
                    ImGui.TextColored(new Vector4(1f, 0.45f, 0.35f, 1f),
                        "  NO GROUND — off tiles or missing MCVT");
            }

            ImGui.Separator();
            ImGui.Text("Buildings");
            if (_wmo is not null)
            {
                ImGui.Text($"  {_wmo.InstanceCount} placed, {_wmo.DrawnLastFrame} drawn");
                ImGui.Text($"  {_wmo.ModelCount} model(s), {_wmo.TextureCount} texture(s)");
                ImGui.Text($"  {_wmo.TotalTriangles:N0} triangles");

                bool showWmo = _wmo.Enabled;
                if (ImGui.Checkbox("Draw buildings", ref showWmo)) _wmo.Enabled = showWmo;

                bool wmoFrustum = _wmo.FrustumCulling;
                if (ImGui.Checkbox("Frustum culling##wmo", ref wmoFrustum))
                    _wmo.FrustumCulling = wmoFrustum;

                // If missing walls reappear when this is on, the geometry was
                // never lost - it was wound inward and culled.
                bool twoSided = _wmo.ForceTwoSided;
                if (ImGui.Checkbox("Force two-sided", ref twoSided)) _wmo.ForceTwoSided = twoSided;

                // Drag to zero to prove whether the alpha cut is eating walls.
                float cutoff = _wmo.AlphaCutoff;
                if (ImGui.SliderFloat("Alpha cutoff", ref cutoff, 0f, 1f))
                    _wmo.AlphaCutoff = cutoff;

                float wmoDistance = _wmo.DrawDistance;
                if (ImGui.SliderFloat("Building distance", ref wmoDistance, 300f, 1250f, "%.0f yd"))
                {
                    _wmo.DrawDistance = wmoDistance;
                    _wmo.FogEnd = wmoDistance;
                    _config.Render.WmoDistance = wmoDistance;
                }
            }
            else
            {
                ImGui.Text("  none loaded");
            }

            if (_doodads is not null)
            {
                ImGui.Separator();
                ImGui.Text("Doodads");
                ImGui.Text($"  {_doodads.InstanceCount:N0} placed, {_doodads.DrawnLastFrame:N0} drawn");
                ImGui.Text($"  {_doodads.ModelCount} model(s), {_doodads.CollisionModels} with collision");
                ImGui.Text($"  {_doodads.TotalTriangles:N0} triangles");

                bool showDoodads = _doodads.Enabled;
                if (ImGui.Checkbox("Draw doodads", ref showDoodads)) _doodads.Enabled = showDoodads;

                bool doodadFrustum = _doodads.FrustumCulling;
                if (ImGui.Checkbox("Frustum culling##doodads", ref doodadFrustum))
                    _doodads.FrustumCulling = doodadFrustum;

                float doodadCut = _doodads.AlphaCutoff;
                if (ImGui.SliderFloat("Doodad alpha cut", ref doodadCut, 0f, 1f))
                    _doodads.AlphaCutoff = doodadCut;

                float dist = _doodads.DrawDistance;
                if (ImGui.SliderFloat("Doodad distance", ref dist, 50f, 1200f))
                {
                    _doodads.DrawDistance = dist;
                    _config.Render.DoodadDistance = dist;
                    _residentCentre = null; // refresh object residency next update
                }

            }

            ImGui.Separator();
            ImGui.Text("Character");
            if (_character is not null)
            {
                ImGui.Text($"  {_character.Race} {_character.Gender}");
                ImGui.Text($"  {_character.BoneCount} bones, {_character.ClipCount} clip(s)");

                if (_character.BoneOverflow)
                    ImGui.TextColored(new Vector4(1f, 0.45f, 0.35f, 1f),
                        "  TOO MANY BONES - animation disabled, see the console");
                ImGui.Text($"  {_character.VisiblePieces}/{_character.PieceCount} geoset(s) drawn");

                if (_character.UnboundSlots > 0)
                    ImGui.TextColored(new Vector4(1f, 0.85f, 0.4f, 1f),
                        $"  {_character.UnboundSlots} texture slot(s) unbound");

                // If ClipTime sits pegged at ClipDuration and never wraps, the
                // clip is being treated as a one-shot and the character will
                // hold its last frame forever.
                ImGui.TextColored(new Vector4(0.6f, 0.9f, 1f, 1f),
                    $"  {_character.ClipName}  {_character.ClipTime:F2}/{_character.ClipDuration:F2}s " +
                    $"x{_character.ClipRate:F2} move {_character.ClipMoveSpeed:F2} " +
                    $"{(_character.ClipLooping ? "loop" : "ONCE")}");
                ImGui.Text($"  ground speed {_character.GroundSpeed,5:F2} yd/s");

                bool drawCharacter = _character.Enabled;
                if (ImGui.Checkbox("Draw character", ref drawCharacter)) _character.Enabled = drawCharacter;

                // FIRST THING TO TRY if the model looks folded or exploded: bind
                // pose makes every skin matrix an exact identity, so what you
                // see is the raw mesh at the placement transform. If bind pose
                // is right and animation is wrong, the bug is in M2Animator; if
                // bind pose is already wrong, it is the transform below.
                bool bind = _character.BindPose;
                if (ImGui.Checkbox("Bind pose (no animation)", ref bind)) _character.BindPose = bind;

                // The one thing arithmetic cannot settle - a bounding box is
                // invariant under a half turn, so only your eyes can say which
                // way the model faces. Line it up with the capsule's spike and
                // tell me the number.
                float heading = _character.HeadingOffsetDegrees;
                if (ImGui.SliderFloat("Heading offset", ref heading, -180f, 180f))
                    _character.HeadingOffsetDegrees = heading;
                ImGui.SameLine();
                if (ImGui.Button("90")) _character.HeadingOffsetDegrees = 90f;

                float modelScale = _character.ModelScale;
                if (ImGui.SliderFloat("Model scale", ref modelScale, 0.25f, 3f))
                    _character.ModelScale = modelScale;

                float zOffset = _character.ZOffset;
                if (ImGui.SliderFloat("Model Z offset", ref zOffset, -2f, 2f))
                    _character.ZOffset = zOffset;

                // Whole body turns to face travel / only the hips turn / a
                // separate sideways clip and nothing turns.
                int strafeStyle = (int)_character.Strafe;
                if (ImGui.Combo("Strafe style", ref strafeStyle,
                        "Split (legs + torso)\0Whole body\0Lower body only\0Sideways clips\0"))
                    _character.Strafe = (CharacterRenderer.StrafeStyle)strafeStyle;

                // How much of the strafe angle the TORSO keeps. The legs always
                // take all of it. 1.0 is the old whole-body mode, 0.0 the old
                // lower-body one, and the real client sits around two thirds.
                float torsoFollow = _character.TorsoFollow;
                if (ImGui.SliderFloat("Torso follow", ref torsoFollow, 0f, 1f))
                    _character.TorsoFollow = torsoFollow;

                ImGui.Text($"  legs {_character.MoveYawDegrees,4:F0} deg" +
                           $"   torso {_character.MoveYawDegrees * _character.TorsoFollow,4:F0} deg");

                int torsoBone = _character.TorsoBone;
                if (ImGui.DragInt("Torso bone (spine)", ref torsoBone, 0.25f, -1,
                        Math.Max(_character.BoneCount - 1, 0)))
                    _character.TorsoBone = torsoBone;

                ImGui.Text($"  strafe angle {_character.MoveYawDegrees,5:F0} deg" +
                           (_character.TwistBone < 0 ? "   HIP BONE NOT FOUND" : ""));

                // Isolates the mechanism from the trigger. Stand still and drag.
                float force = _character.ForceAngleDegrees;
                if (ImGui.SliderFloat("Force angle (deg)", ref force, -120f, 120f))
                    _character.ForceAngleDegrees = force;
                ImGui.SameLine();
                if (ImGui.Button("0")) _character.ForceAngleDegrees = 0f;

                // Ticked: the hip bone's subtree turns, which is the legs.
                // Unticked: everything else turns, which is the upper body.
                bool subtree = _character.TwistSubtree;
                if (ImGui.Checkbox("Twist subtree (legs, not torso)", ref subtree))
                    _character.TwistSubtree = subtree;

                // Where the twist stops. It comes from the key-bone table, which
                // is a convention rather than a guarantee - if the torso turns
                // with the legs, drag this until it does not.
                int hipBone = _character.TwistBone;
                if (ImGui.DragInt("Twist bone (hips)", ref hipBone, 0.25f, -1,
                        Math.Max(_character.BoneCount - 1, 0)))
                    _character.TwistBone = hipBone;

                float maxTwist = _character.MaxTwistDegrees;
                if (ImGui.SliderFloat("Max twist (deg)", ref maxTwist, 0f, 180f))
                    _character.MaxTwistDegrees = maxTwist;

                bool dressed = _dressed;
                if (ImGui.Checkbox("Wear Battlegear of Might", ref dressed))
                {
                    _dressed = dressed;
                    _character.Equipment = dressed
                        ? CharacterEquipment.BattlegearOfMight()
                        : new CharacterEquipment();
                    _character.ApplyEquipment();
                }

                if (_character.Attached is not null)
                {
                    ImGui.Text($"  attached {_character.Attached.DrawnLastFrame}/{_character.Attached.MountCount} drawn");

                    bool drawAttached = _character.Attached.Enabled;
                    if (ImGui.Checkbox("Draw attached items", ref drawAttached))
                        _character.Attached.Enabled = drawAttached;

                    // Attached items are SEPARATE M2 MODELS, not geosets, so the
                    // geoset checkboxes below have no effect on them. Two
                    // mechanisms, two switches.
                    if (ImGui.TreeNode("Attached items"))
                    {
                        foreach (var (label, visible) in _character.Attached.Mounts.ToList())
                        {
                            bool on = visible;
                            if (ImGui.Checkbox($"{label}##att", ref on))
                                _character.Attached.SetMountVisible(label, on);
                        }
                        ImGui.TreePop();
                    }
                }

                // Hair lives in category 0 alongside the base body, so the
                // category checkbox for it would take the whole character with
                // it. This is the switch for testing hair against a helm.
                // Appearance. These are the CharSections lookup keys - flipping
                // them proves the table is finding real rows, and the face and
                // hair should visibly change.
                if (ImGui.TreeNode("Appearance"))
                {
                    int skin = _character.SkinId, face = _character.FaceId;
                    int hairStyle = _character.HairStyleId, hairColour = _character.HairColorId;
                    int facial = _character.FacialHairId;
                    bool changed = false;

                    changed |= ImGui.SliderInt("Skin", ref skin, 0, 10);
                    changed |= ImGui.SliderInt("Face", ref face, 0, 10);
                    changed |= ImGui.SliderInt("Hair style", ref hairStyle, 0, 15);
                    changed |= ImGui.SliderInt("Hair colour", ref hairColour, 0, 10);
                    changed |= ImGui.SliderInt("Facial hair", ref facial, 0, 10);

                    if (changed)
                    {
                        _character.SkinId = skin;
                        _character.FaceId = face;
                        _character.HairStyleId = hairStyle;
                        _character.HairColorId = hairColour;
                        _character.FacialHairId = facial;
                        _character.Reload();
                    }

                    ImGui.TreePop();
                }

                bool hideHair = _character.HideHair;
                if (ImGui.Checkbox("Hide hair", ref hideHair))
                {
                    _character.HideHair = hideHair;
                    _character.ApplyEquipment();
                }

                // FLICKER HUNT. Z-fighting is two surfaces in the same place,
                // so the fastest way to name the pair is to switch one half off
                // and see whether it stops. One checkbox per category that is
                // actually being drawn, with its variant, so the list is short
                // and every entry means something.
                if (ImGui.TreeNode("Geosets drawn"))
                {
                    foreach (var (category, variant) in _character.ActiveGeosets)
                    {
                        bool on = !_character.HiddenCategories.Contains(category);
                        if (ImGui.Checkbox($"cat {category} (variant {variant})##geo{category}", ref on))
                        {
                            if (on) _character.HiddenCategories.Remove(category);
                            else _character.HiddenCategories.Add(category);
                            _character.ApplyEquipment();
                        }
                    }

                    // Soloing beats hiding for z-fighting: a fight needs both
                    // halves, so switching one off only proves a pair stopped.
                    // Stepping through one geoset at a time says which.
                    int solo = _character.SoloGeoset;
                    if (ImGui.SliderInt("Solo one geoset (-1 = all)", ref solo, -1,
                            Math.Max(_character.ActiveGeosets.Count - 1, 0)))
                    {
                        _character.SoloGeoset = solo;
                        _character.ApplyEquipment();
                    }

                    if (ImGui.Button("Show all categories"))
                    {
                        _character.HiddenCategories.Clear();
                        _character.SoloGeoset = -1;
                        _character.ApplyEquipment();
                    }

                    ImGui.TreePop();
                }

                bool allGeosets = _character.ShowAllGeosets;
                if (ImGui.Checkbox("All geosets", ref allGeosets)) _character.ShowAllGeosets = allGeosets;

                bool magenta = _character.MagentaUnbound;
                if (ImGui.Checkbox("Magenta unbound", ref magenta)) _character.MagentaUnbound = magenta;

                float charCut = _character.AlphaCutoff;
                if (ImGui.SliderFloat("Character alpha cut", ref charCut, 0f, 1f))
                    _character.AlphaCutoff = charCut;
            }
            else
            {
                ImGui.Text("  not loaded - see the console");
            }

            ImGui.Separator();
            ImGui.Text("Collision");
            if (_collision is not null)
            {
                ImGui.Text($"  {_collision.TriangleCount:N0} triangles, {_collision.NodeCount:N0} nodes");
                if (_vmaps is not null)
                    ImGui.Text($"  {_vmaps.SpawnsUsed}/{_vmaps.SpawnsSeen} spawns, " +
                               $"{_vmaps.DistinctUnresolved} model(s) with no .vmo");
                ImGui.Text($"  built in {_collisionBuildSeconds:F1}s");

                if (_collisionDebug is not null)
                {
                    bool show = _collisionDebug.Enabled;
                    if (ImGui.Checkbox("Show collision (C)", ref show))
                        SetCollisionDebugEnabled(show);

                    bool solid = _collisionDebug.Solid;
                    if (ImGui.Checkbox("Solid", ref solid)) _collisionDebug.Solid = solid;

                    // Isolate whatever last blocked you. One building's shell
                    // against that same building rendered is a single glance;
                    // a million triangles of wireframe is not.
                    bool isolate = _collisionDebug.SourceFilter >= 0;
                    if (ImGui.Checkbox("Isolate blocker", ref isolate))
                    {
                        _collisionDebug.SourceFilter = isolate && _controller is not null
                            ? _collision.SourceIdOf(_controller.LastBlockTriangle)
                            : -1;
                    }

                    // Live collision shift. Nudge until the wireframe sits on
                    // the geometry you can see, then bake the value into the
                    // loader and set this back to zero.
                    var offset = _collision.Offset;
                    var raw = new System.Numerics.Vector3(offset.X, offset.Y, offset.Z);
                    if (ImGui.DragFloat3("Collision offset", ref raw, 0.05f, -20f, 20f))
                        _collision.Offset = raw;

                    if (ImGui.Button("Nudge along facing") && _controller is not null)
                    {
                        var f = new Vector3(MathF.Cos(_controller.Yaw), MathF.Sin(_controller.Yaw), 0);
                        _collision.Offset += f * 0.25f;
                    }
                    ImGui.SameLine();
                    if (ImGui.Button("Reset offset")) _collision.Offset = Vector3.Zero;

                    if (_collisionDebug.SourceFilter >= 0)
                        ImGui.Text($"    showing only {_collision.SourceOf(_controller?.LastBlockTriangle ?? -1)}");
                }

                // Live probes. Stand next to something solid and face it: if
                // "ahead" stays empty, the geometry is not where it looks like
                // it is, and that is a data problem rather than a movement one.
                if (_controller is not null)
                {
                    var mid = _controller.Position + new Vector3(0, 0, _config.Movement.Height * 0.5f);
                    var facing = new Vector3(MathF.Cos(_controller.Yaw), MathF.Sin(_controller.Yaw), 0);

                    var ahead = _collision.Raycast(mid, facing, 60f);
                    if (ahead is not null)
                    {
                        var p = ahead.Value.Point;
                        ImGui.Text($"  ahead  {ahead.Value.Distance,6:F2} yd at ({p.X:F1}, {p.Y:F1}, {p.Z:F1})");
                        ImGui.Text($"         from {_collision.SourceOf(ahead.Value.Triangle)}");
                    }
                    else
                    {
                        ImGui.Text("  ahead    nothing within 60");
                    }

                    // What actually stopped you, as opposed to what happens to
                    // be in front of you.
                    if (_controller.HasBlock)
                    {
                        var b = _controller.LastBlockPoint;
                        var n = _controller.LastBlockNormal;
                        ImGui.TextColored(new Vector4(1f, 0.85f, 0.4f, 1f),
                            $"  BLOCKED at ({b.X:F2}, {b.Y:F2}, {b.Z:F2})");
                        ImGui.Text($"    from {_collision.SourceOf(_controller.LastBlockTriangle)}");
                        ImGui.Text($"    normal ({n.X:F2}, {n.Y:F2}, {n.Z:F2})");
                        ImGui.Text($"    you are at ({_controller.Position.X:F2}, " +
                                   $"{_controller.Position.Y:F2}, {_controller.Position.Z:F2})");
                        if (_controller.LastPushOut > 0.001f)
                            ImGui.Text($"    pushed out {_controller.LastPushOut:F2} yd this frame");
                    }
                }
            }
            else
            {
                ImGui.Text("  terrain only (no vmaps)");
            }

            var cam = _window.Camera;
            var cp = cam.Position;
            ImGui.Separator();
            ImGui.Text("Camera");
            ImGui.Text($"  {cp.X,9:F1} {cp.Y,9:F1} {cp.Z,9:F1}");
            ImGui.Text($"  yaw {cam.Yaw * 180f / MathF.PI,5:F0}  pitch {cam.Pitch * 180f / MathF.PI,4:F0}  " +
                       $"dist {cam.EffectiveDistance,4:F1}/{cam.Distance,4:F1}");

            // Non-zero orbit means the camera has been swung off the character's
            // back. It should return to 0 the moment you move.
            ImGui.Text($"  orbit {cam.OrbitYaw * 180f / MathF.PI,5:F0} deg   " +
                       $"view {cam.ViewYaw * 180f / MathF.PI,5:F0} deg");

            float turnSpeed = _turnSpeed * 180f / MathF.PI;
            if (ImGui.SliderFloat("Turn speed (deg/s)", ref turnSpeed, 45f, 360f))
                _turnSpeed = turnSpeed * MathF.PI / 180f;

            // Mouse-look diagnostics. Read these WHILE dragging - each line
            // eliminates one link in the chain, so "the mouse does nothing"
            // becomes a specific broken link instead of a theory:
            //   buttons never light  -> the press is not reaching us at all
            //   buttons light, captured no -> ImGui is eating the click
            //   captured yes, moves frozen -> no motion events in this mode
            //   moves climbing, applied frozen -> deltas rejected as oversized
            //   applied climbing, delta 0,0 -> the cursor mode reports no motion
            ImGui.Text($"  mouse  L{(_window.MouseLeftDown ? 1 : 0)} R{(_window.MouseRightDown ? 1 : 0)}   " +
                       $"captured {(_window.MouseCaptured ? "yes" : "no")}   cursor {_window.CursorModeName}");
            ImGui.Text($"  moves {_window.MouseMoveEvents}  applied {_window.MouseLookEvents}  " +
                       $"last delta ({_window.LastMouseDelta.X,6:F1},{_window.LastMouseDelta.Y,6:F1})");

            // If look is dead, this is the first thing to try. Raw is the mode a
            // game wants but the one most likely to be refused.
            bool rawCursor = _window.RawCursor;
            if (ImGui.Checkbox("Raw cursor (uncheck if look is dead)", ref rawCursor))
                _window.RawCursor = rawCursor;

            float mouseScale = _window.MouseSensitivity;
            if (ImGui.SliderFloat("Mouse sensitivity x", ref mouseScale, 0.1f, 10f))
                _window.MouseSensitivity = mouseScale;

            ImGui.Separator();

            if (_controller is not null)
            {
                bool flying = _controller.Flying;
                if (ImGui.Checkbox("Fly (F)", ref flying)) _controller.Flying = flying;
            }

            ImGui.Checkbox("Show player capsule", ref _showPlayerMarker);

            float eye = cam.EyeHeight;
            if (ImGui.SliderFloat("Eye height", ref eye, 0f, 10f)) cam.EyeHeight = eye;

            if (_terrain is not null)
            {
                int mode = _terrain.DebugMode;
                if (ImGui.Combo("Shading", ref mode,
                        "Textured\0Normals\0UVs\0Flat\0Splat mask\0Untextured\0"))
                    _terrain.DebugMode = mode;

                float scale = _terrain.TextureScale;
                if (ImGui.SliderFloat("Texture repeat", ref scale, 1f, 32f))
                    _terrain.TextureScale = scale;

                if (_terrain.TileCount > 0)
                    ImGui.Text($"tileset textures {_terrain.FirstTileTextureCount}");
            }

            ImGui.Separator();
            ImGui.TextWrapped("W/S walk, A/D turn, Q/E strafe (holding RIGHT mouse swaps A/D to " +
                              "strafe). Arrow keys turn and walk, PgUp/PgDn look up and down, " +
                              "Shift walk, Space jump, F toggle fly, C collision " +
                              "(Space/Ctrl for height while flying, Shift boosts). " +
                              "LEFT mouse swings the camera around your character without turning him; " +
                              "RIGHT mouse turns him and the camera together; moving re-centres the " +
                              "camera behind him. Wheel to zoom, Esc to quit.");
        }
        ImGui.End();
    }

    public void Dispose()
    {
        try { _collisionBuildTask?.GetAwaiter().GetResult(); }
        catch { /* Shutdown must continue after a failed background build. */ }
        _character?.Dispose();
        _collisionDebug?.Dispose();
        _doodads?.Dispose();
        _wmo?.Dispose();
        _terrain?.Dispose();
        _uploads?.Dispose();
        _assetWorkers?.Dispose();

        // Renderer disposal joins any asset-preparation workers before the
        // extractor is detached and its shared archive handles are closed.
        AdtTerrainReader.StormLibExtractor = null;
        _mpq?.Dispose();
    }
}

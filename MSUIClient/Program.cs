using System.Numerics;
using ImGuiNET;
using Silk.NET.Input;
using Silk.NET.OpenGL;
using MSUIClient.Engine;
using MSUIClient.Player;
using MSUIClient.World;
using MSUIClient.World.Collision;
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
    private WmoRenderer? _wmo;
    private CollisionDebugRenderer? _collisionDebug;

    /// <summary>Edge detection for the fly toggle — IsDown reports held, not pressed.</summary>
    private bool _flyKeyDown;
    private bool _collisionKeyDown;

    /// <summary>Draw the character capsule. On by default until there is a real model.</summary>
    private bool _showPlayerMarker = true;

    private double _collisionBuildSeconds;

    public GameLoop(ClientWindow window, ClientConfig config)
    {
        _window = window;
        _config = config;
    }

    public void Load(GL gl)
    {
        _window.Camera.Target = new Vector3(_config.Start.X, _config.Start.Y, _config.Start.Z);
        _window.Camera.Yaw = _config.Start.Orientation;

        _terrain = new TerrainRenderer(gl, _config);

        // Shaders are copied next to the exe by the csproj; fall back to the
        // source tree so editing a .frag and hitting F5 picks it up.
        var shaderDir = Path.Combine(AppContext.BaseDirectory, "Shaders");
        if (!File.Exists(Path.Combine(shaderDir, "terrain.vert")))
            shaderDir = Path.Combine(_config.RepoRoot, "MSUIClient", "Shaders");
        _terrain.LoadShaders(shaderDir);

        _terrain.LoadAround(_config.Start.X, _config.Start.Y, _config.Start.TileRadius);

        // Self-check against the value the server independently agreed with.
        _terrain.VerifyAgainst(_config.Start.X, _config.Start.Y, _config.Start.Z);

        LoadCollision();

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

        // Buildings. Independent of terrain and collision: if this throws, the
        // world is still walkable, just empty.
        try
        {
            _wmo = new WmoRenderer(gl, _config);
            _wmo.LoadShaders(shaderDir);
            _wmo.LoadForTiles(_terrain.LoadedTiles);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[wmo] FAILED - {ex.Message}");
            _wmo = null;
        }

        if (_collision is not null)
        {
            try
            {
                _collisionDebug = new CollisionDebugRenderer(gl);
                _collisionDebug.LoadShaders(shaderDir);
                _collisionDebug.Build(_collision);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[collision] debug renderer FAILED - {ex.Message}");
                _collisionDebug = null;
            }
        }

        CompareWmoToCollision();

        Console.WriteLine("[game] ready");
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

        if (!_config.Movement.Collision)
        {
            Console.WriteLine("[collision] disabled in config (movement.collision = false)");
            return;
        }

        if (!_config.HasVmaps)
        {
            Console.WriteLine("[collision] no vmaps — terrain only, you will walk through buildings");
            return;
        }

        var started = DateTime.UtcNow;

        try
        {
            _vmaps = new VmapCollisionLoader(_config.VmapPath!);
            _collision = new CollisionWorld();

            foreach (var (col, row) in _terrain.LoadedTiles)
                _vmaps.LoadTile(_collision, _config.Start.Map, col, row, _config.Movement.IncludeM2);

            _collision.Build();
            _vmaps.PublishSourceNames(_collision);
            _collisionBuildSeconds = (DateTime.UtcNow - started).TotalSeconds;

            Console.WriteLine($"[collision] {_vmaps.Summary()}");
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
        }
        catch (Exception ex)
        {
            // Loudly. A silent failure here would present later as a physics bug.
            Console.WriteLine($"[collision] FAILED — {ex.Message}");
            Console.WriteLine("[collision] continuing with terrain collision only");
            _collision = null;
        }
    }

    public void Update(float dt)
    {
        if (_controller is null) return;

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
            _collisionDebug.Enabled = !_collisionDebug.Enabled;
            Console.WriteLine($"[collision] wireframe {(_collisionDebug.Enabled ? "on" : "off")}");
        }
        _collisionKeyDown = collisionKey;

        bool shift = _window.IsDown(Key.ShiftLeft) || _window.IsDown(Key.ShiftRight);

        var input = new MovementInput
        {
            Forward = _window.Axis(Key.W, Key.S),
            Strafe = _window.Axis(Key.D, Key.A),
            Up = _window.Axis(Key.Space, Key.ControlLeft),
            Yaw = _window.Camera.Yaw,
            Jump = _window.IsDown(Key.Space),
            Walking = shift && !_controller.Flying,
            Boost = shift && _controller.Flying,
        };

        _controller.Update(dt, input);

        // The camera orbits the character's feet; Camera.EyeHeight does the rest.
        _window.Camera.Target = _controller.Position;

        ResolveCameraCollision(dt);

        if (_window.IsDown(Key.Escape)) _window.Close();
    }

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

            if (_terrain is not null)
            {
                ImGui.Text($"tiles {_terrain.TileCount}   drawn {_terrain.DrawnLastFrame}");
                ImGui.Text($"triangles {_terrain.TotalTriangles:N0}");
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

                ImGui.Text($"  state  {(_controller.Flying ? "flying" : _controller.Grounded ? "grounded" : "airborne")}");
                ImGui.Text($"  vz     {_controller.Velocity.Z,10:F2}");

                if (_controller.FallTimeMs > 0)
                    ImGui.Text($"  fall   {_controller.FallTimeMs,10:F0} ms");

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
            }
            else
            {
                ImGui.Text("  none loaded");
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
                    if (ImGui.Checkbox("Show collision (C)", ref show)) _collisionDebug.Enabled = show;

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
            ImGui.TextWrapped("WASD move, Shift walk, Space jump, F toggle fly, C collision " +
                              "(Space/Ctrl for height while flying, Shift boosts), " +
                              "hold mouse to look, wheel to zoom, Esc to quit.");
        }
        ImGui.End();
    }

    public void Dispose()
    {
        _collisionDebug?.Dispose();
        _wmo?.Dispose();
        _terrain?.Dispose();
    }
}

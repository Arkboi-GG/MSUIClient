using System.Numerics;
using System.Reflection;
using MSUIClient;
using MSUIClient.Engine;
using MSUIClient.Player;
using MSUIClient.World;
using MSUIClient.World.Collision;

TerrainRenderer terrain = CreateTerrain(height: 100f);
CollisionWorld collision = CreateFloor(height: 10f);

VerifyTeleportLanding(terrain, collision);
VerifyFlightExitLanding(terrain, collision);
VerifyInteriorJumpDoesNotSelectMountain(CreateTerrain(height: 100f));
VerifyOverheadTerrainExpandsSupportProbe(CreateTerrain(height: 100f));
VerifyContinuousInteriorEntryRetainsTerrainShell(CreateTerrain(height: 100f));
VerifyGlobalWmoFall(CreateEmptyTerrain(), collision);
VerifyWalkableTriangleGather();
VerifyCameraTerrainShellClassification();

Console.WriteLine("character-controller clinical checks passed");
return 0;

static void VerifyTeleportLanding(TerrainRenderer terrain, CollisionWorld collision)
{
    CharacterController controller = CreateController(terrain, collision);
    controller.Teleport(0f, 0f, 20f);

    controller.Update(1f / 60f, default);
    Require(controller.Position.Z < 20f,
        $"teleport snapped onto overhead terrain at Z={controller.Position.Z:F3}");
    Require(controller.GroundSource == "collision",
        $"teleport selected {controller.GroundSource}, expected collision");

    Land(controller);
    Require(MathF.Abs(controller.Position.Z - 10f) < 0.001f,
        $"teleport landed at Z={controller.Position.Z:F3}, expected 10");
}

static void VerifyFlightExitLanding(TerrainRenderer terrain, CollisionWorld collision)
{
    CharacterController controller = CreateController(terrain, collision);
    controller.Teleport(0f, 0f, 100f);
    controller.Update(1f / 60f, default);
    Require(controller.Grounded, "setup did not settle on outdoor terrain");

    controller.Flying = true;
    controller.Position = new Vector3(0f, 0f, 20f);
    controller.Flying = false;

    controller.Update(1f / 60f, default);
    Require(controller.Position.Z < 20f,
        $"flight exit snapped onto overhead terrain at Z={controller.Position.Z:F3}");
    Require(controller.GroundSource == "collision",
        $"flight exit selected {controller.GroundSource}, expected collision");

    Land(controller);
    Require(MathF.Abs(controller.Position.Z - 10f) < 0.001f,
        $"flight exit landed at Z={controller.Position.Z:F3}, expected 10");
}

static void VerifyInteriorJumpDoesNotSelectMountain(TerrainRenderer terrain)
{
    // Model the Ironforge failure: an upper WMO support ends beneath a prop,
    // the next WMO floor is more than the normal five-yard probe below it, and
    // the outdoor mountain ADT is far overhead.  Losing the upper support while
    // airborne must produce a fall to the lower WMO floor, never a 90-yard snap
    // to the outdoor height field.
    var collision = new CollisionWorld();
    AddFloor(collision, -0.2f, 10f, -10f, 10f, 10f);
    AddFloor(collision, -20f, 10f, -10f, 10f, 0f);
    collision.Build();

    CharacterController controller = CreateController(terrain, collision);
    controller.Teleport(0f, 0f, 10f);
    controller.Update(1f / 60f, default);
    Require(controller.Grounded && controller.GroundSource == "collision",
        "interior-jump setup did not settle on the upper WMO floor");

    var jump = new MovementInput { Forward = -1f, Yaw = 0f, Jump = true };
    controller.Update(1f / 60f, jump);

    float highest = controller.Position.Z;
    for (int i = 0; i < 600 && !controller.Grounded; i++)
    {
        // Keep moving only long enough to clear the upper support.  Remaining
        // still afterward makes the expected lower-floor landing deterministic.
        var input = i < 30
            ? new MovementInput { Forward = -1f, Yaw = 0f }
            : default;
        controller.Update(1f / 60f, input);
        highest = MathF.Max(highest, controller.Position.Z);
    }

    Require(highest < 20f,
        $"interior jump selected overhead terrain, reaching Z={highest:F3}");
    Require(controller.Grounded,
        "interior jump did not reach the deeper WMO floor within 10 seconds");
    Require(MathF.Abs(controller.Position.Z) < 0.001f,
        $"interior jump landed at Z={controller.Position.Z:F3}, expected lower WMO floor at 0");
    Require(controller.GroundSource == "collision",
        $"interior jump selected {controller.GroundSource}, expected collision");
}

static void VerifyOverheadTerrainExpandsSupportProbe(TerrainRenderer terrain)
{
    // The centre is just outside the floor but the left-hand capsule footprint
    // still overlaps it.  Overhead terrain must not count as "nearby" merely
    // because (feet - terrain) is a large negative number and suppress the
    // eight rescue probes.
    var collision = new CollisionWorld();
    AddFloor(collision, 0.15f, 10f, -10f, 10f, 10f);
    collision.Build();

    CharacterController controller = CreateController(terrain, collision);
    // First settle outdoors to clear Teleport's terrain-shell exception. The
    // next update must discover the interior only through the ordinary support
    // selection path, which directly exercises terrainNearby.
    controller.Teleport(0f, 0f, 100f);
    controller.Update(1f / 60f, default);
    Require(controller.Grounded && controller.GroundSource == "terrain",
        "footprint-probe setup did not clear teleport state outdoors");

    controller.Position = new Vector3(-0.1f, 0f, 10f);
    controller.Update(1f / 60f, default);

    Require(controller.TerrainGroundZ is float terrainZ && terrainZ > 90f,
        "overhead-terrain probe regression did not actually sample its terrain shell");
    Require(controller.Grounded,
        "overhead terrain suppressed footprint support probes");
    Require(controller.GroundSource == "collision",
        $"footprint support selected {controller.GroundSource}, expected collision");
    Require(controller.GroundProbesLastFrame == 9,
        $"footprint support used {controller.GroundProbesLastFrame} probes, expected expansion to 9");
    Require(controller.GroundProbeOffset.X > 0f,
        "footprint support did not come from the overlapping right probe");
}

static void VerifyContinuousInteriorEntryRetainsTerrainShell(TerrainRenderer terrain)
{
    // Simulate already being inside without Teleport's discontinuity marker.
    // Flight keeps the authored Z but clears no grounding state; exiting flight
    // normally sets the same landing marker, so instead establish an outdoor
    // landing first, then move the controller into the interior to exercise the
    // regular under-terrain closer-surface clause.
    var collision = new CollisionWorld();
    AddFloor(collision, -10f, 10f, -10f, 10f, 10f);
    collision.Build();

    CharacterController controller = CreateController(terrain, collision);
    controller.Teleport(0f, 0f, 100f);
    controller.Update(1f / 60f, default);
    Require(controller.Grounded && controller.GroundSource == "terrain",
        "continuous-entry setup did not clear teleport state on outdoor terrain");

    // Direct placement stands in for ordinary horizontal tunnel entry after
    // crossing the ADT shell. ResolveGround must establish WMO precedence from
    // the current heights alone, with no teleport exception remaining.
    controller.Position = new Vector3(0f, 0f, 10f);
    controller.Update(1f / 60f, default);
    Require(controller.Grounded && controller.GroundSource == "collision",
        $"continuous interior entry selected {controller.GroundSource}, expected collision");

    // Remove support for one frame. The already-proven terrain shell must stay
    // excluded instead of lifting the controller onto it.
    controller.Collision = new CollisionWorld();
    controller.Update(1f / 60f, default);
    Require(controller.Position.Z < 20f,
        $"continuous interior entry forgot its terrain shell and snapped to Z={controller.Position.Z:F3}");
    Require(!controller.Grounded && controller.Velocity.Z < 0f,
        "continuous interior entry froze instead of falling through a support gap");
}

static void VerifyGlobalWmoFall(TerrainRenderer terrain, CollisionWorld collision)
{
    CharacterController controller = CreateController(terrain, collision);
    controller.TerrainAbsentByDesign = true;
    controller.Teleport(0f, 0f, 10f);
    controller.Update(1f / 60f, default);
    Require(controller.Grounded, "global-WMO setup did not settle on its WMO floor");

    // Simulate walking off an upper ledge after the teleport recovery flag has
    // already cleared. Missing terrain must permit a real fall toward the next
    // WMO floor rather than freezing the controller in empty height data.
    controller.Position = new Vector3(0f, 0f, 20f);
    controller.Update(1f / 60f, default);
    Require(!controller.Grounded && controller.Velocity.Z < 0f,
        "global-WMO map froze instead of falling without terrain");

    Land(controller);
    Require(MathF.Abs(controller.Position.Z - 10f) < 0.001f,
        $"global-WMO fall landed at Z={controller.Position.Z:F3}, expected 10");
}

static void VerifyWalkableTriangleGather()
{
    var collision = new CollisionWorld { Offset = new Vector3(3f, -2f, 1f) };
    // A sloped floor, a vertical wall in the same XY box, and a floor outside
    // the requested Z slab. Only the nearby floor may receive a ground decal.
    collision.AddTriangle(new Vector3(-2f, -2f, 0f),
        new Vector3(2f, -2f, 0f), new Vector3(2f, 2f, 1f));
    collision.AddTriangle(new Vector3(-2f, -2f, 0f),
        new Vector3(2f, 2f, 1f), new Vector3(-2f, 2f, 1f));
    collision.AddTriangle(new Vector3(0f, 0f, 0f),
        new Vector3(0f, 2f, 0f), new Vector3(0f, 2f, 3f));
    collision.AddTriangle(new Vector3(-2f, -2f, 20f),
        new Vector3(2f, -2f, 20f), new Vector3(2f, 2f, 20f));
    collision.Build();

    var gathered = new List<(Vector3 A, Vector3 B, Vector3 C)>();
    collision.GatherWalkableTriangles(0f, -5f, 0f, 6f, 1f, 4f, gathered);
    Require(gathered.Count == 2,
        $"walkable gather returned {gathered.Count} triangles instead of the two floor faces");
    Require(gathered.SelectMany(t => new[] { t.A, t.B, t.C }).All(v => v.Z is >= 1f and <= 2f),
        "walkable gather lost collision Offset or admitted the high floor");
}

static void VerifyCameraTerrainShellClassification()
{
    // Values captured from load-azeroth-301: the server-authored BRM interior
    // position is Z=166.09 and the outdoor terrain at that XY is Z=274.77.
    // EyeTarget adds 2.2 yards to the character position.
    Require(Camera.TerrainIsOverhead(168.29f, 274.77f, 1f),
        "Blackrock Mountain's outdoor terrain shell was admitted as camera ground");

    Require(!Camera.TerrainIsOverhead(168.29f, 166.09f, 1f),
        "ordinary outdoor ground was mistaken for an overhead terrain shell");
    Require(!Camera.TerrainIsOverhead(168.29f, null, 1f),
        "missing terrain was mistaken for an overhead terrain shell");
}

static CharacterController CreateController(TerrainRenderer terrain, CollisionWorld collision)
    => new(terrain, new ClientConfig.MovementConfig()) { Collision = collision };

static TerrainRenderer CreateTerrain(float height)
{
    var terrain = new TerrainRenderer(null!, new ClientConfig(), null!, null!);
    FieldInfo field = typeof(TerrainRenderer).GetField("_heights",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("TerrainRenderer._heights not found");
    var heights = (Dictionary<(int col, int row), float[]>)field.GetValue(terrain)!;
    heights[(32, 32)] = Enumerable.Repeat(height,
        TerrainRenderer.HeightGridSide * TerrainRenderer.HeightGridSide).ToArray();
    return terrain;
}

static TerrainRenderer CreateEmptyTerrain()
    => new(null!, new ClientConfig(), null!, null!);

static CollisionWorld CreateFloor(float height)
{
    var collision = new CollisionWorld();
    collision.AddTriangle(new Vector3(-10f, -10f, height),
        new Vector3(10f, -10f, height), new Vector3(10f, 10f, height));
    collision.AddTriangle(new Vector3(-10f, -10f, height),
        new Vector3(10f, 10f, height), new Vector3(-10f, 10f, height));
    collision.Build();
    return collision;
}

static void AddFloor(CollisionWorld collision, float minX, float maxX,
    float minY, float maxY, float height)
{
    collision.AddTriangle(new Vector3(minX, minY, height),
        new Vector3(maxX, minY, height), new Vector3(maxX, maxY, height));
    collision.AddTriangle(new Vector3(minX, minY, height),
        new Vector3(maxX, maxY, height), new Vector3(minX, maxY, height));
}

static void Land(CharacterController controller)
{
    for (int i = 0; i < 600 && !controller.Grounded; i++)
        controller.Update(1f / 60f, default);
    Require(controller.Grounded, "controller did not land within 10 seconds");
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

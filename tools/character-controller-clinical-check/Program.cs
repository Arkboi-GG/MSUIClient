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
VerifyFloorEdgeKeepsFootprintSupport();
VerifySunkenSupportIsRecovered();
VerifyChestHighWallBlocksTheSweep();
VerifyRampStillWalkable();
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

static void VerifyFloorEdgeKeepsFootprintSupport()
{
    // Model the Stormwind auction house edge: an upper deck, and just past its
    // lip a plank a few tenths lower - inside GroundSnapDistance.
    //
    // Walk the capsule CENTRE a hand's breadth past the lip while most of the
    // footprint is still over the deck. The centre ray now reports the lower
    // plank, and the old gate read "something is nearby below, one ray is
    // enough" and suppressed the footprint fan at exactly the moment it was
    // load-bearing. Support collapsed to a single ray, adhesion pulled the
    // character down onto the plank, and repeating that a few frames walked it
    // below the deck entirely and out through the floor.
    var collision = new CollisionWorld();
    AddFloor(collision, -10f, 0f, -10f, 10f, 10f);      // deck
    AddFloor(collision, 0f, 10f, -10f, 10f, 9.7f);      // plank just past the lip
    collision.Build();

    CharacterController controller = CreateController(CreateTerrain(height: 5f), collision);
    controller.Teleport(-1f, 0f, 10f);
    controller.Update(1f / 60f, default);
    Require(controller.Grounded && MathF.Abs(controller.Position.Z - 10f) < 0.001f,
        $"floor-edge setup did not settle on the deck, Z={controller.Position.Z:F3}");

    // Centre 0.1 past the lip; the footprint reaches 0.34, so the deck still
    // carries the character.
    controller.Position = new Vector3(0.1f, 0f, 10f);
    controller.Update(1f / 60f, default);

    Require(controller.GroundProbesLastFrame == 9,
        $"floor edge used {controller.GroundProbesLastFrame} support probes, " +
        "expected the footprint fan to expand to 9");
    Require(controller.Grounded,
        "floor edge lost support entirely");
    Require(MathF.Abs(controller.Position.Z - 10f) < 0.001f,
        $"floor edge dropped to Z={controller.Position.Z:F3}; the deck under the " +
        "footprint should still hold the character at 10");
    Require(controller.GroundProbeOffset.X < 0f,
        "floor-edge support did not come from the probe still over the deck");
}

static void VerifySunkenSupportIsRecovered()
{
    // Whatever puts the feet below a floor - a step-up into a solid, a stair
    // lip miss, an adhesion frame that guessed low - must not be permanent.
    // A fixed StepHeight probe lift cannot see a surface 1.0 above the feet, so
    // the floor the character was standing on last frame becomes invisible and
    // it falls out of a floor that is still there.
    var collision = new CollisionWorld();
    AddFloor(collision, -10f, 10f, -10f, 10f, 10f);
    collision.Build();

    CharacterController controller = CreateController(CreateTerrain(height: 5f), collision);
    controller.Teleport(0f, 0f, 10f);
    controller.Update(1f / 60f, default);
    Require(controller.Grounded && controller.GroundSource == "collision",
        "sunken-support setup did not settle on the WMO floor");

    // Sink 1.0 - past StepHeight - while still horizontally over the floor.
    controller.Position = new Vector3(0f, 0f, 9f);
    controller.Update(1f / 60f, default);

    Require(controller.Grounded,
        $"sunken character fell instead of recovering, vz={controller.Velocity.Z:F3}");
    Require(controller.GroundSource == "collision",
        $"sunken recovery selected {controller.GroundSource}, expected collision");
    Require(MathF.Abs(controller.Position.Z - 10f) < 0.001f,
        $"sunken recovery left the character at Z={controller.Position.Z:F3}, expected 10");
}

static void VerifyChestHighWallBlocksTheSweep()
{
    // A rim 0.9 tall at the edge of a deck, with nothing beyond it. Taller than
    // StepHeight, so it is not climbable and must stop the character - but its
    // top is below the mid-body height the sweep used to be a single ray at, so
    // that ray passed clean over it and reported open air. The character walked
    // through a face the debug view draws in wall red and fell off the far side.
    var collision = new CollisionWorld();
    AddFloor(collision, -10f, 0f, -10f, 10f, 10f);
    AddWallAtX(collision, 0f, -10f, 10f, 10f, 10.9f);
    collision.Build();

    CharacterController controller = CreateController(CreateTerrain(height: 5f), collision);
    controller.Teleport(-2f, 0f, 10f);
    controller.Update(1f / 60f, default);
    Require(controller.Grounded, "chest-high wall setup did not settle on the deck");

    var forward = new MovementInput { Forward = 1f, Yaw = 0f };
    for (int i = 0; i < 120; i++) controller.Update(1f / 60f, forward);

    Require(controller.Position.X < 0f,
        $"sweep walked through the rim to X={controller.Position.X:F3}; it must stop before 0");
    Require(controller.Grounded,
        $"sweep left the deck entirely, Z={controller.Position.Z:F3}");
    Require(controller.HasBlock,
        "the rim never registered as a block, so the sweep never saw it");
}

static void VerifyRampStillWalkable()
{
    // The counterpart guard: the extra sample heights must not turn a walkable
    // slope into a wall. A 30 degree ramp is inside the 50 degree limit, so the
    // character has to climb it, not stall against it.
    var collision = new CollisionWorld();
    AddFloor(collision, -12f, -6f, -6f, 6f, 10f);
    // Ramp rising from Z=10 at X=-6 to Z=12.31 at X=-2 (about 30 degrees).
    collision.AddTriangle(new Vector3(-6f, -6f, 10f),
        new Vector3(-2f, -6f, 12.31f), new Vector3(-2f, 6f, 12.31f));
    collision.AddTriangle(new Vector3(-6f, -6f, 10f),
        new Vector3(-2f, 6f, 12.31f), new Vector3(-6f, 6f, 10f));
    AddFloor(collision, -2f, 6f, -6f, 6f, 12.31f);
    collision.Build();

    CharacterController controller = CreateController(CreateTerrain(height: 5f), collision);
    controller.Teleport(-9f, 0f, 10f);
    controller.Update(1f / 60f, default);
    Require(controller.Grounded, "ramp setup did not settle on the lower floor");

    var forward = new MovementInput { Forward = 1f, Yaw = 0f };
    for (int i = 0; i < 90; i++) controller.Update(1f / 60f, forward);

    Require(controller.Position.X > -2f,
        $"ramp stalled the sweep at X={controller.Position.X:F3}; expected to climb past -2");
    Require(controller.Position.Z > 12.2f,
        $"ramp climb ended at Z={controller.Position.Z:F3}, expected the upper floor at 12.31");
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

static void AddWallAtX(CollisionWorld collision, float x,
    float minY, float maxY, float minZ, float maxZ)
{
    collision.AddTriangle(new Vector3(x, minY, minZ),
        new Vector3(x, maxY, minZ), new Vector3(x, maxY, maxZ));
    collision.AddTriangle(new Vector3(x, minY, minZ),
        new Vector3(x, maxY, maxZ), new Vector3(x, minY, maxZ));
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

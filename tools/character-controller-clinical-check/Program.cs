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

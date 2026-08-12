using System.Numerics;
using MSUIClient;
using MSUIClient.Formats;
using MSUIClient.Net;
using MSUIClient.World.Collision;

ClientConfig config = ClientConfig.Load(Path.Combine("MSUIClient", "client-config.json"));
WdtFile wdt = WdtFile.Read(config.ClientDataPath, "BlackrockDepths")
    ?? throw new InvalidOperationException("BlackrockDepths.wdt was not found");
var placement = wdt.GlobalWmo
    ?? throw new InvalidOperationException("BlackrockDepths has no global MODF placement");
byte[] rootBytes = AdtTerrainReader.ReadFileFromMpqs(config.ClientDataPath, placement.ModelPath)
    ?? throw new InvalidOperationException($"{placement.ModelPath} was not found");
WmoRootData root = WmoReader.ParseRoot(rootBytes)
    ?? throw new InvalidOperationException("BRD WMO root failed to parse");

var rawMin = new Vector3(float.MaxValue);
var rawMax = new Vector3(float.MinValue);
var collision = new CollisionWorld();
string stem = placement.ModelPath[..^4];
int groups = 0;
for (int i = 0; i < root.NGroups; i++)
{
    byte[]? bytes = AdtTerrainReader.ReadFileFromMpqs(config.ClientDataPath, $"{stem}_{i:D3}.wmo");
    WmoGroupData? group = bytes is null ? null : WmoReader.ParseGroup(bytes, root.Flags);
    if (group is null) continue;
    groups++;
    foreach (var vertex in group.Vertices)
    {
        var point = new Vector3(vertex.x, vertex.y, vertex.z);
        rawMin = Vector3.Min(rawMin, point);
        rawMax = Vector3.Max(rawMax, point);
    }

    if (group.IsAntiportal) continue;
    int triangles = group.Indices.Count / 3;
    for (int t = 0; t < triangles; t++)
    {
        if (t < group.TriMaterials.Count &&
            (group.TriMaterials[t].flags & 0x04) != 0) continue;
        int i0 = group.Indices[t * 3];
        int i1 = group.Indices[t * 3 + 1];
        int i2 = group.Indices[t * 3 + 2];
        if (i0 >= group.Vertices.Count || i1 >= group.Vertices.Count ||
            i2 >= group.Vertices.Count) continue;
        collision.AddTriangle(ToGlobal(group.Vertices[i0]),
            ToGlobal(group.Vertices[i1]), ToGlobal(group.Vertices[i2]));
    }
}

Console.WriteLine($"path={placement.ModelPath}");
Console.WriteLine($"groups={groups}/{root.NGroups}");
Console.WriteLine($"vertices raw min={rawMin} max={rawMax}");
Console.WriteLine($"MODF min=<{placement.BbMinX}, {placement.BbMinY}, {placement.BbMinZ}> " +
                  $"max=<{placement.BbMaxX}, {placement.BbMaxY}, {placement.BbMaxZ}>");
Require(Near(rawMin.Y, placement.BbMinX) && Near(rawMin.Z, placement.BbMinY) &&
        Near(rawMin.X, placement.BbMinZ) && Near(rawMax.Y, placement.BbMaxX) &&
        Near(rawMax.Z, placement.BbMaxY) && Near(rawMax.X, placement.BbMaxZ),
    "BRD raw vertex bounds no longer match the WDT global-WMO axis law");

var entrance = new Vector3(456.929f, 34.0923f, -68.0896f);
byte[] triggerBytes = AdtTerrainReader.ReadFileFromMpqs(
    config.ClientDataPath, AreaTriggerTable.MpqPath)
    ?? throw new InvalidOperationException("AreaTrigger.dbc was not found");
AreaTriggerTable triggers = AreaTriggerTable.Parse(triggerBytes)
    ?? throw new InvalidOperationException("AreaTrigger.dbc failed to parse");
AreaTriggerRow? arrivalTrigger = triggers.Containing(230, entrance);
AreaTriggerRow? nearestTrigger = triggers.NearestOnMap(230, entrance);
var worldMin = new Vector3(-placement.BbMaxZ, -placement.BbMaxX, placement.BbMinY);
var worldMax = new Vector3(-placement.BbMinZ, -placement.BbMinX, placement.BbMaxY);
Require(entrance.X >= worldMin.X && entrance.X <= worldMax.X &&
        entrance.Y >= worldMin.Y && entrance.Y <= worldMax.Y &&
        entrance.Z >= worldMin.Z && entrance.Z <= worldMax.Z,
    $"server BRD entrance {entrance} is outside transformed WMO bounds {worldMin}..{worldMax}");
var poisonedOutdoorSpawn = new Vector3(-7178.1f, -924.9f, 166.8f);
Require(poisonedOutdoorSpawn.X < worldMin.X || poisonedOutdoorSpawn.X > worldMax.X ||
        poisonedOutdoorSpawn.Y < worldMin.Y || poisonedOutdoorSpawn.Y > worldMax.Y,
    "captured map-230/outdoor poisoned spawn unexpectedly lies inside BRD bounds");

collision.Build();
RayHit floor = collision.Raycast(entrance + Vector3.UnitZ * 2f,
    -Vector3.UnitZ, 10f)
    ?? throw new InvalidOperationException(
        "BRD entrance has no collision floor under it after the global transform");
Require(collision.Raycast(poisonedOutdoorSpawn + Vector3.UnitZ * 3f,
            -Vector3.UnitZ, 500f) is null,
    "captured poisoned outdoor spawn unexpectedly has a BRD collision floor");
Require(collision.TryFindNearestWalkablePoint(poisonedOutdoorSpawn, out Vector3 recoveryFloor),
    "BRD collision has no nearest walkable recovery point");
Require(recoveryFloor.X >= worldMin.X && recoveryFloor.X <= worldMax.X &&
        recoveryFloor.Y >= worldMin.Y && recoveryFloor.Y <= worldMax.Y,
    $"nearest recovery floor {recoveryFloor} is outside BRD world bounds");

AreaTriggerTeleportTable teleports = AreaTriggerTeleportTable.Load(config.RepoRoot);
AreaTriggerTeleport repairDestination = teleports.ById.Values
    .Where(t => t.TargetMap != 230)
    .OrderBy(t => Vector3.DistanceSquared(poisonedOutdoorSpawn, t.TargetPosition))
    .First();
Require(repairDestination.Id == 1472 && repairDestination.TargetMap == 0,
    $"poisoned BRD/outdoor spawn matched unexpected recovery destination {repairDestination}");
Require(Vector3.Distance(poisonedOutdoorSpawn, repairDestination.TargetPosition) < 40f,
    "poisoned BRD/outdoor spawn is no longer close enough for bounded portal recovery");

byte[] worldport = WorldSession.BuildWorldTeleportBody(
    0x11223344, (uint)repairDestination.TargetMap,
    repairDestination.TargetPosition, repairDestination.TargetOrientation);
var worldportReader = new PacketReader(worldport);
Require(worldport.Length == 24 && worldportReader.ReadU32() == 0x11223344 &&
        worldportReader.ReadU32() == 0 &&
        worldportReader.ReadVector3() == repairDestination.TargetPosition &&
        worldportReader.ReadF32() == repairDestination.TargetOrientation &&
        worldportReader.Remaining == 0,
    "administrator worldport recovery body is not the 1.12 time/map/position/orientation shape");
var decalFloors = new List<(Vector3 A, Vector3 B, Vector3 C)>();
collision.GatherWalkableTriangles(entrance.X - 20f, entrance.Y - 20f, entrance.Z - 40f,
    entrance.X + 20f, entrance.Y + 20f, entrance.Z + 40f, decalFloors);
Require(decalFloors.Count > 0,
    "BRD entrance has no walkable collision triangles for projected aggro discs");
Console.WriteLine($"world bounds min={worldMin} max={worldMax}");
Console.WriteLine($"entrance floor Z={floor.Point.Z:F3}, " +
                  $"{collision.TriangleCount:N0} collision triangles");
Console.WriteLine($"entrance arrival trigger={arrivalTrigger?.Id.ToString() ?? "none"}");
Console.WriteLine($"entrance nearest trigger={nearestTrigger?.ToString() ?? "none"}");
Console.WriteLine($"poisoned spawn recovery={repairDestination}");
Console.WriteLine($"nearest BRD fallback floor={recoveryFloor}");
Console.WriteLine($"entrance decal floors={decalFloors.Count:N0}");
Console.WriteLine("global-WMO clinical checks passed");
return 0;

static Vector3 ToGlobal((float x, float y, float z) vertex)
    => new(-vertex.x, -vertex.y, vertex.z);

static bool Near(float left, float right) => MathF.Abs(left - right) < 0.1f;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

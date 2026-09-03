using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using MSUIClient.Formats;

string dataPath = Path.GetFullPath(Arg("--data") ?? @"GameData\Data");
string vmapPath = Path.GetFullPath(Arg("--vmaps") ?? @"GameData\vmaps");
string mapName = Arg("--map") ?? "Kalimdor";
string? modelFilter = Arg("--model");

using var mpq = new MpqMount(dataPath);
AdtTerrainReader.StormLibExtractor = mpq.ReadFile;

Regex adtName = new(
    $@"^World[\\/]Maps[\\/]{Regex.Escape(mapName)}[\\/]{Regex.Escape(mapName)}_(\d+)_(\d+)\.adt$",
    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
string[] adts = mpq.ListedFiles()
    .Where(path => adtName.IsMatch(path))
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
    .ToArray();

var placements = new Dictionary<uint, Placement>();
var terrainChunks = new List<TerrainChunk>();
foreach (string adtPath in adts)
{
    byte[]? bytes = mpq.ReadFile(adtPath);
    if (bytes is null) continue;
    Match match = adtName.Match(adtPath);
    int col = int.Parse(match.Groups[1].Value);
    int row = int.Parse(match.Groups[2].Value);
    foreach (Placement placement in ReadPlacements(bytes, col, row))
        placements.TryAdd(placement.Id, placement);
    terrainChunks.AddRange(ReadTerrainChunks(bytes, col, row));
}

var vmoFiles = Directory.EnumerateFiles(vmapPath, "*.vmo", SearchOption.TopDirectoryOnly)
    .ToDictionary(path => Path.GetFileName(path)!, StringComparer.OrdinalIgnoreCase);
var placementsByModel = placements.Values
    .Where(p => modelFilter is null ||
        p.ModelPath.Contains(modelFilter, StringComparison.OrdinalIgnoreCase))
    .GroupBy(p => p.ModelPath, StringComparer.OrdinalIgnoreCase)
    .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
    .ToArray();

var suspects = new List<Suspect>();
var modelRoots = new Dictionary<string, WmoRootData>(StringComparer.OrdinalIgnoreCase);
var flagTotals = new Dictionary<byte, long>();
int rootsRead = 0, groupsRead = 0, groupsCompared = 0;
long clientTriangles = 0, serverTriangles = 0;

foreach (IGrouping<string, Placement> placedModel in placementsByModel)
{
    byte[]? rootBytes = mpq.ReadFile(placedModel.Key);
    WmoRootData? root = rootBytes is null ? null : WmoReader.ParseRoot(rootBytes);
    if (root is null) continue;
    modelRoots[placedModel.Key] = root;
    rootsRead++;

    string stem = placedModel.Key[..^4];
    string leaf = Path.GetFileName(placedModel.Key);
    vmoFiles.TryGetValue(leaf + ".vmo", out string? vmoFile);
    VmapWorldModel? vmo = null;
    if (vmoFile is not null)
        vmo = VmoReader.ReadTolerant(vmoFile, out _);

    for (int groupIndex = 0; groupIndex < root.NGroups; groupIndex++)
    {
        byte[]? groupBytes = mpq.ReadFile($"{stem}_{groupIndex:D3}.wmo");
        if (groupBytes is null) continue;
        WmoGroupData? parsed;
        try { parsed = WmoReader.ParseGroup(groupBytes, root.Flags); }
        catch { continue; }
        if (parsed is null) continue;
        WmoGroupData group = parsed;
        groupsRead++;

        var current = new List<Triangle>();
        int triangleCount = group.Indices.Count / 3;
        for (int triangleIndex = 0; triangleIndex < triangleCount; triangleIndex++)
        {
            (byte flags, byte material) = triangleIndex < group.TriMaterials.Count
                ? group.TriMaterials[triangleIndex]
                : ((byte)0xff, (byte)0xff);
            flagTotals[flags] = flagTotals.GetValueOrDefault(flags) + 1;
            if ((flags & 0x04) != 0) continue;
            if (!TryTriangle(group, triangleIndex, out Triangle triangle)) continue;
            current.Add(triangle with { Flags = flags, Material = material, Index = triangleIndex });
        }

        clientTriangles += current.Count;
        VmapGroupModel? serverGroup = MatchServerGroup(vmo, group, groupIndex);
        if (serverGroup is null) continue;
        groupsCompared++;
        serverTriangles += serverGroup.TriangleCount;

        List<Triangle> extra = FindClientOnly(current, serverGroup);
        Triangle[] blockers = extra.Where(IsCharacterBlockingSheet).ToArray();
        if (blockers.Length == 0) continue;

        suspects.Add(new Suspect(
            placedModel.Key,
            groupIndex,
            group.GroupName,
            group.GroupWmoId,
            current.Count,
            serverGroup.TriangleCount,
            extra.Count,
            blockers,
            placedModel.ToArray()));
    }
}

Console.WriteLine($"map={mapName} adts={adts.Length} placements={placements.Count} " +
    $"models={placementsByModel.Length} roots={rootsRead} groups={groupsRead} compared={groupsCompared}");
Console.WriteLine($"currentClientTriangles={clientTriangles:N0} serverVmapTriangles={serverTriangles:N0}");
Console.WriteLine("MOPY flags across scanned groups: " +
    string.Join(", ", flagTotals.OrderBy(x => x.Key).Select(x => $"0x{x.Key:X2}={x.Value:N0}")));
Console.WriteLine($"suspectGroups={suspects.Count} suspectPlacements={suspects.Sum(x => x.Placements.Length)}");
WorldMapAreaCatalog? mapAreas = WorldMapAreaCatalog.Load(mpq);
TerrainChunk[] holed = terrainChunks.Where(chunk => chunk.Holes != 0).ToArray();
int caveOpeningPlacements = 0;
var openingPlacements = new List<OpeningPlacement>();

foreach (IGrouping<string, Placement> placedModel in placementsByModel)
{
    if (!modelRoots.TryGetValue(placedModel.Key, out WmoRootData? root)) continue;
    int[] portalReferences = new int[root.Portals.Count];
    foreach (WmoPortalRef portalRef in root.PortalRefs)
        if (portalRef.PortalIndex < portalReferences.Length)
            portalReferences[portalRef.PortalIndex]++;

    Console.WriteLine($"CAVE_MODEL {placedModel.Key} placements={placedModel.Count()} " +
        $"bounds=({root.BbMinX:F2},{root.BbMinY:F2},{root.BbMinZ:F2}).." +
        $"({root.BbMaxX:F2},{root.BbMaxY:F2},{root.BbMaxZ:F2}) portals={root.Portals.Count}");
    foreach (Placement placement in placedModel.OrderBy(x => x.Id))
    {
        Matrix4x4 transform = BuildPlacement(placement);
        (Vector3 worldMin, Vector3 worldMax) = TransformBounds(root, transform);
        TerrainChunk[] overlappingHoles = holed.Where(chunk =>
            chunk.Center.X + AdtTerrainReader.CHUNK_SIZE * .5f >= worldMin.X &&
            chunk.Center.X - AdtTerrainReader.CHUNK_SIZE * .5f <= worldMax.X &&
            chunk.Center.Y + AdtTerrainReader.CHUNK_SIZE * .5f >= worldMin.Y &&
            chunk.Center.Y - AdtTerrainReader.CHUNK_SIZE * .5f <= worldMax.Y).ToArray();
        if (overlappingHoles.Length != 0)
        {
            caveOpeningPlacements++;
            openingPlacements.Add(new OpeningPlacement(
                placement, worldMin, worldMax, ZoneAt(placement.WorldOrigin, mapAreas)));
        }
        Console.WriteLine($"  CAVE_SPOT id={placement.Id} tile={placement.Col},{placement.Row} " +
            $"origin={F(placement.WorldOrigin)} zone={ZoneAt(placement.WorldOrigin, mapAreas)} " +
            $"holeChunks={overlappingHoles.Length}");
        for (int portalIndex = 0; portalIndex < root.Portals.Count; portalIndex++)
        {
            WmoPortal portal = root.Portals[portalIndex];
            if (portal.StartVertex + portal.VertexCount > root.PortalVertices.Count) continue;
            Vector3 center = Vector3.Zero;
            for (int i = 0; i < portal.VertexCount; i++)
                center += ToWmoVector(root.PortalVertices[portal.StartVertex + i]);
            if (portal.VertexCount != 0) center /= portal.VertexCount;
            Vector3 world = Vector3.Transform(center, transform);
            Console.WriteLine($"    PORTAL index={portalIndex} refs={portalReferences[portalIndex]} " +
                $"world={F(world)} local={F(center)}");
        }
    }
}
Console.WriteLine($"matchedPlacements={placementsByModel.Sum(group => group.Count())} " +
    $"authoredOpeningOverlaps={caveOpeningPlacements}");
List<OpeningPlacement[]> openingSites = ClusterOpeningPlacements(openingPlacements);
Console.WriteLine($"authoredOpeningSites={openingSites.Count}");
foreach (OpeningPlacement[] site in openingSites
    .OrderBy(group => group[0].Zone, StringComparer.OrdinalIgnoreCase)
    .ThenBy(group => group.Average(item => item.Placement.WorldOrigin.X))
    .ThenBy(group => group.Average(item => item.Placement.WorldOrigin.Y)))
{
    Vector3 center = site.Aggregate(Vector3.Zero,
        (sum, item) => sum + item.Placement.WorldOrigin) / site.Length;
    Console.WriteLine($"OPENING_SITE zone={site[0].Zone} center={F(center)} " +
        $"ids={string.Join(',', site.Select(item => item.Placement.Id).Order())}");
}

WorldMapAreaInfo durotar = mapAreas?.Areas.FirstOrDefault(area =>
    area.Directory.Equals("Durotar", StringComparison.OrdinalIgnoreCase)) ?? default;
if (durotar.Id != 0)
{
    // Published 1.12 guide location: cave mouth at zone-map 45,56.
    Vector2 cave = new(
        durotar.Top + .56f * (durotar.Bottom - durotar.Top),
        durotar.Left + .45f * (durotar.Right - durotar.Left));
    Console.WriteLine($"VALLEY_TARGET zoneMap=(45.00,56.00) world=({cave.X:F2},{cave.Y:F2})");
    foreach (Placement placement in placements.Values
        .Where(placement => Vector2.Distance(
            new Vector2(placement.WorldOrigin.X, placement.WorldOrigin.Y), cave) <= 300f)
        .OrderBy(placement => Vector2.Distance(
            new Vector2(placement.WorldOrigin.X, placement.WorldOrigin.Y), cave)))
        Console.WriteLine($"  NEAR_WMO id={placement.Id} tile={placement.Col},{placement.Row} " +
            $"origin={F(placement.WorldOrigin)} model={placement.ModelPath}");
    foreach (TerrainChunk chunk in terrainChunks
        .Where(chunk => Vector2.Distance(chunk.Center, cave) <= 100f)
        .OrderBy(chunk => Vector2.Distance(chunk.Center, cave)))
        Console.WriteLine($"  NEAR_CHUNK tile={chunk.Col},{chunk.Row} chunk={chunk.ChunkX},{chunk.ChunkY} " +
            $"center=({chunk.Center.X:F2},{chunk.Center.Y:F2}) impassable={chunk.Impassable} " +
            $"holes=0x{chunk.Holes:X4} area={chunk.AreaId} baseZ={chunk.BaseZ:F2}");
}

TerrainChunk[] caveBoundaryCandidates = terrainChunks
    .Where(chunk => chunk.Impassable && holed.Any(hole =>
        Vector2.Distance(chunk.Center, hole.Center) <= AdtTerrainReader.CHUNK_SIZE * 1.6f))
    .OrderBy(chunk => chunk.Center.X)
    .ThenBy(chunk => chunk.Center.Y)
    .ToArray();
Console.WriteLine($"terrainChunks={terrainChunks.Count} impassable={terrainChunks.Count(x => x.Impassable)} " +
    $"holed={holed.Length} impassableNearHole={caveBoundaryCandidates.Length}");
foreach (TerrainChunk chunk in caveBoundaryCandidates)
{
    string zone = mapAreas?.Areas.FirstOrDefault(area =>
        area.MapId == 1 &&
        chunk.Center.Y >= MathF.Min(area.Left, area.Right) &&
        chunk.Center.Y <= MathF.Max(area.Left, area.Right) &&
        chunk.Center.X >= MathF.Min(area.Top, area.Bottom) &&
        chunk.Center.X <= MathF.Max(area.Top, area.Bottom)).Directory ?? "";
    Console.WriteLine($"CAVEBOUNDARY zone={zone} tile={chunk.Col},{chunk.Row} " +
        $"chunk={chunk.ChunkX},{chunk.ChunkY} center=({chunk.Center.X:F2},{chunk.Center.Y:F2}) " +
        $"holes=0x{chunk.Holes:X4} baseZ={chunk.BaseZ:F2}");
}

foreach (Suspect suspect in suspects
    .OrderBy(x => x.ModelPath, StringComparer.OrdinalIgnoreCase)
    .ThenBy(x => x.GroupIndex))
{
    Console.WriteLine($"MODEL {suspect.ModelPath} group={suspect.GroupIndex} " +
        $"name='{suspect.GroupName}' id={suspect.GroupWmoId} " +
        $"client={suspect.ClientTriangles} vmap={suspect.ServerTriangles} " +
        $"extra={suspect.ExtraTriangles} blockingSheets={suspect.Blockers.Length}");
    foreach (Placement placement in suspect.Placements.OrderBy(x => x.Id))
    {
        Matrix4x4 transform = BuildPlacement(placement);
        Vector3 local = suspect.Blockers.Aggregate(Vector3.Zero, (sum, t) => sum + t.Center)
            / suspect.Blockers.Length;
        Vector3 world = Vector3.Transform(local, transform);
        Console.WriteLine($"  SPOT id={placement.Id} tile={placement.Col},{placement.Row} " +
            $"origin={F(placement.WorldOrigin)} blocker={F(world)}");
    }
    foreach (IGrouping<(byte Flags, byte Material), Triangle> kind in suspect.Blockers
        .GroupBy(t => (t.Flags, t.Material)).OrderBy(x => x.Key.Flags).ThenBy(x => x.Key.Material))
        Console.WriteLine($"  FACECLASS mopy=0x{kind.Key.Flags:X2} material={kind.Key.Material} " +
            $"triangles={kind.Count()} maxArea={kind.Max(t => t.Area):F2}");
}

if (suspects.Count != 0)
    Environment.ExitCode = 2;

return;

string? Arg(string name)
{
    int index = Array.FindIndex(args, value => value.Equals(name, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static VmapGroupModel? MatchServerGroup(VmapWorldModel? model, WmoGroupData group, int groupIndex)
{
    if (model is null) return null;
    VmapGroupModel[] byId = model.Groups.Where(g => g.GroupWmoId == group.GroupWmoId).ToArray();
    if (byId.Length == 1) return byId[0];
    return groupIndex < model.Groups.Length ? model.Groups[groupIndex] : null;
}

static List<Triangle> FindClientOnly(List<Triangle> client, VmapGroupModel server)
{
    var serverSignatures = new Dictionary<TriangleSignature, int>();
    for (int i = 0; i < server.Indices.Length / 3; i++)
    {
        int a = checked((int)server.Indices[i * 3]);
        int b = checked((int)server.Indices[i * 3 + 1]);
        int c = checked((int)server.Indices[i * 3 + 2]);
        if (a >= server.Vertices.Length || b >= server.Vertices.Length || c >= server.Vertices.Length)
            continue;
        var triangle = new Triangle(
            ToVmapVector(server.Vertices[a]), ToVmapVector(server.Vertices[b]),
            ToVmapVector(server.Vertices[c]), i, 0, 0);
        TriangleSignature signature = Signature(triangle);
        serverSignatures[signature] = serverSignatures.GetValueOrDefault(signature) + 1;
    }

    var extra = new List<Triangle>();
    foreach (Triangle triangle in client)
    {
        TriangleSignature signature = Signature(triangle);
        int available = serverSignatures.GetValueOrDefault(signature);
        if (available > 0) serverSignatures[signature] = available - 1;
        else extra.Add(triangle);
    }
    return extra;
}

static bool IsCharacterBlockingSheet(Triangle triangle)
{
    Vector3 normal = Vector3.Cross(triangle.B - triangle.A, triangle.C - triangle.A);
    float length = normal.Length();
    if (length < 1e-5f) return false;
    normal /= length;
    Vector3 min = Vector3.Min(triangle.A, Vector3.Min(triangle.B, triangle.C));
    Vector3 max = Vector3.Max(triangle.A, Vector3.Max(triangle.B, triangle.C));
    Vector3 size = max - min;
    float horizontalSpan = MathF.Max(size.X, size.Y);
    return MathF.Abs(normal.Z) < 0.35f && size.Z >= 1.5f &&
        horizontalSpan >= 0.8f && triangle.Area >= 0.5f;
}

static bool TryTriangle(WmoGroupData group, int triangleIndex, out Triangle triangle)
{
    triangle = default;
    int i0 = group.Indices[triangleIndex * 3];
    int i1 = group.Indices[triangleIndex * 3 + 1];
    int i2 = group.Indices[triangleIndex * 3 + 2];
    if (i0 >= group.Vertices.Count || i1 >= group.Vertices.Count || i2 >= group.Vertices.Count)
        return false;
    triangle = new Triangle(ToWmoVector(group.Vertices[i0]), ToWmoVector(group.Vertices[i1]),
        ToWmoVector(group.Vertices[i2]), triangleIndex, 0, 0);
    return true;
}

static TriangleSignature Signature(Triangle triangle)
{
    Span<float> lengths = stackalloc float[3]
    {
        Vector3.DistanceSquared(triangle.A, triangle.B),
        Vector3.DistanceSquared(triangle.B, triangle.C),
        Vector3.DistanceSquared(triangle.C, triangle.A),
    };
    lengths.Sort();
    return new TriangleSignature(Q(lengths[0]), Q(lengths[1]), Q(lengths[2]), Q(triangle.Area));
}

static int Q(float value) => (int)MathF.Round(value * 1000f);
static Vector3 ToVmapVector(Vec3 value) => new(value.X, value.Y, value.Z);
static Vector3 ToWmoVector((float x, float y, float z) value) => new(value.x, value.y, value.z);
static string F(Vector3 value) => $"({value.X:F2},{value.Y:F2},{value.Z:F2})";

static (Vector3 Min, Vector3 Max) TransformBounds(WmoRootData root, Matrix4x4 transform)
{
    Vector3 min = new(float.PositiveInfinity);
    Vector3 max = new(float.NegativeInfinity);
    foreach (float x in new[] { root.BbMinX, root.BbMaxX })
    foreach (float y in new[] { root.BbMinY, root.BbMaxY })
    foreach (float z in new[] { root.BbMinZ, root.BbMaxZ })
    {
        Vector3 world = Vector3.Transform(new Vector3(x, y, z), transform);
        min = Vector3.Min(min, world);
        max = Vector3.Max(max, world);
    }
    return (min, max);
}

static List<OpeningPlacement[]> ClusterOpeningPlacements(List<OpeningPlacement> placements)
{
    const float seamSlack = 8f;
    var result = new List<OpeningPlacement[]>();
    var remaining = new HashSet<int>(Enumerable.Range(0, placements.Count));
    while (remaining.Count != 0)
    {
        int seed = remaining.First();
        remaining.Remove(seed);
        var component = new List<int> { seed };
        var queue = new Queue<int>();
        queue.Enqueue(seed);
        while (queue.Count != 0)
        {
            OpeningPlacement current = placements[queue.Dequeue()];
            foreach (int candidateIndex in remaining.ToArray())
            {
                OpeningPlacement candidate = placements[candidateIndex];
                bool touches = current.Max.X + seamSlack >= candidate.Min.X &&
                    current.Min.X - seamSlack <= candidate.Max.X &&
                    current.Max.Y + seamSlack >= candidate.Min.Y &&
                    current.Min.Y - seamSlack <= candidate.Max.Y &&
                    current.Max.Z + seamSlack >= candidate.Min.Z &&
                    current.Min.Z - seamSlack <= candidate.Max.Z;
                if (!touches) continue;
                remaining.Remove(candidateIndex);
                component.Add(candidateIndex);
                queue.Enqueue(candidateIndex);
            }
        }
        result.Add(component.Select(index => placements[index]).ToArray());
    }
    return result;
}

static string ZoneAt(Vector3 world, WorldMapAreaCatalog? mapAreas) =>
    mapAreas?.Areas
        .Where(area => area.MapId == 1 &&
            world.Y >= MathF.Min(area.Left, area.Right) &&
            world.Y <= MathF.Max(area.Left, area.Right) &&
            world.X >= MathF.Min(area.Top, area.Bottom) &&
            world.X <= MathF.Max(area.Top, area.Bottom))
        .OrderBy(area => MathF.Abs(area.Right - area.Left) * MathF.Abs(area.Bottom - area.Top))
        .FirstOrDefault().Directory ?? "";

static IReadOnlyList<Placement> ReadPlacements(byte[] data, int col, int row)
{
    Dictionary<uint, (int Offset, int Size)> chunks = TopChunks(data);
    uint mwmo = ChunkId("MWMO"), mwid = ChunkId("MWID"), modf = ChunkId("MODF");
    if (!chunks.TryGetValue(mwmo, out var names) || !chunks.TryGetValue(modf, out var placements))
        return [];

    var byOffset = new Dictionary<uint, string>();
    int end = Math.Min(data.Length, names.Offset + names.Size);
    int start = names.Offset;
    for (int i = names.Offset; i <= end; i++)
    {
        if (i < end && data[i] != 0) continue;
        if (i > start)
            byOffset[(uint)(start - names.Offset)] = Encoding.ASCII.GetString(data, start, i - start);
        start = i + 1;
    }

    uint[] offsets = [];
    if (chunks.TryGetValue(mwid, out var ids))
    {
        offsets = new uint[ids.Size / 4];
        for (int i = 0; i < offsets.Length; i++)
            offsets[i] = BitConverter.ToUInt32(data, ids.Offset + i * 4);
    }

    var result = new List<Placement>();
    for (int at = placements.Offset; at + 64 <= placements.Offset + placements.Size; at += 64)
    {
        uint nameId = BitConverter.ToUInt32(data, at);
        uint nameOffset = nameId < offsets.Length ? offsets[nameId] : nameId;
        if (!byOffset.TryGetValue(nameOffset, out string? path)) continue;
        var placement = new Placement(
            BitConverter.ToUInt32(data, at + 4), path,
            BitConverter.ToSingle(data, at + 8), BitConverter.ToSingle(data, at + 12),
            BitConverter.ToSingle(data, at + 16), BitConverter.ToSingle(data, at + 20),
            BitConverter.ToSingle(data, at + 24), BitConverter.ToSingle(data, at + 28),
            col, row, Vector3.Zero);
        result.Add(placement with { WorldOrigin = Vector3.Transform(Vector3.Zero, BuildPlacement(placement)) });
    }
    return result;
}

static Dictionary<uint, (int Offset, int Size)> TopChunks(byte[] data)
{
    var result = new Dictionary<uint, (int, int)>();
    for (int at = 0; at + 8 <= data.Length;)
    {
        uint id = BitConverter.ToUInt32(data, at);
        uint rawSize = BitConverter.ToUInt32(data, at + 4);
        if (rawSize > int.MaxValue || at + 8L + rawSize > data.Length) break;
        int size = (int)rawSize;
        result.TryAdd(id, (at + 8, size));
        at += 8 + size;
    }
    return result;
}

static uint ChunkId(string fourcc)
{
    byte[] bytes = Encoding.ASCII.GetBytes(fourcc);
    Array.Reverse(bytes);
    return BitConverter.ToUInt32(bytes);
}

static IReadOnlyList<TerrainChunk> ReadTerrainChunks(byte[] data, int col, int row)
{
    const int headerSize = 128;
    uint mcnk = ChunkId("MCNK");
    var result = new List<TerrainChunk>(256);
    for (int at = 0; at + 8 <= data.Length;)
    {
        uint id = BitConverter.ToUInt32(data, at);
        uint rawSize = BitConverter.ToUInt32(data, at + 4);
        if (rawSize > int.MaxValue || at + 8L + rawSize > data.Length) break;
        int size = (int)rawSize;
        int payload = at + 8;
        if (id == mcnk && size >= headerSize)
        {
            uint flags = BitConverter.ToUInt32(data, payload);
            int chunkX = checked((int)BitConverter.ToUInt32(data, payload + 0x04));
            int chunkY = checked((int)BitConverter.ToUInt32(data, payload + 0x08));
            uint areaId = BitConverter.ToUInt32(data, payload + 0x34);
            ushort holes = BitConverter.ToUInt16(data, payload + 0x3c);
            float baseZ = BitConverter.ToSingle(data, payload + 0x70);
            float tileHighX = (32 - row) * AdtTerrainReader.GRID_SIZE;
            float tileHighY = (32 - col) * AdtTerrainReader.GRID_SIZE;
            Vector2 center = new(
                tileHighX - (chunkY + .5f) * AdtTerrainReader.CHUNK_SIZE,
                tileHighY - (chunkX + .5f) * AdtTerrainReader.CHUNK_SIZE);
            result.Add(new TerrainChunk(col, row, chunkX, chunkY, center,
                (flags & 0x2) != 0, holes, areaId, baseZ));
        }
        at += 8 + size;
    }
    return result;
}

static Matrix4x4 BuildPlacement(Placement p)
{
    const float degrees = MathF.PI / 180f;
    const float coordinateShift = 32f * 533.33333f;
    var basis = new Matrix4x4(
        1, 0, 0, 0, 0, 0, -1, 0, 0, 1, 0, 0, 0, 0, 0, 1);
    var placementToWorld = new Matrix4x4(
        0, -1, 0, 0, 0, 0, 1, 0, -1, 0, 0, 0,
        coordinateShift, coordinateShift, 0, 1);
    float heading = (p.RotY - 90f) * degrees;
    Matrix4x4 rotation = Matrix4x4.CreateRotationX(p.RotZ * degrees)
        * Matrix4x4.CreateRotationZ(-p.RotX * degrees)
        * Matrix4x4.CreateRotationY(heading);
    return basis * rotation * Matrix4x4.CreateTranslation(p.PosX, p.PosY, p.PosZ)
        * placementToWorld;
}

readonly record struct Placement(uint Id, string ModelPath,
    float PosX, float PosY, float PosZ, float RotX, float RotY, float RotZ,
    int Col, int Row, Vector3 WorldOrigin);
readonly record struct Triangle(Vector3 A, Vector3 B, Vector3 C, int Index, byte Flags, byte Material)
{
    public Vector3 Center => (A + B + C) / 3f;
    public float Area => Vector3.Cross(B - A, C - A).Length() * .5f;
}
readonly record struct TriangleSignature(int A, int B, int C, int Area);
sealed record Suspect(string ModelPath, int GroupIndex, string GroupName, uint GroupWmoId,
    int ClientTriangles, int ServerTriangles, int ExtraTriangles, Triangle[] Blockers,
    Placement[] Placements);
readonly record struct TerrainChunk(int Col, int Row, int ChunkX, int ChunkY, Vector2 Center,
    bool Impassable, ushort Holes, uint AreaId, float BaseZ);
readonly record struct OpeningPlacement(Placement Placement, Vector3 Min, Vector3 Max, string Zone);

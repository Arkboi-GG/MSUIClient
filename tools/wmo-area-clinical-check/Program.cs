using System.Text;
using System.Collections;
using System.Numerics;
using System.Reflection;
using MSUIClient;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.World.Wmo;

CheckSyntheticCatalogLaw();
CheckJoinMetadataParsers();
CheckArchivedVanillaAnchors();
CheckPortalWarmRadiusLaw();
CheckCameraVoidFallbackLaw();

Console.WriteLine("WMO area clinical checks passed.");

static void CheckCameraVoidFallbackLaw()
{
    Type renderer = typeof(WmoRenderer);
    Type groupType = renderer.GetNestedType("GroupMesh", BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("WmoRenderer.GroupMesh test anchor disappeared");
    Type modelType = renderer.GetNestedType("Model", BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("WmoRenderer.Model test anchor disappeared");
    MethodInfo find = renderer.GetMethod("FindCameraSeeds",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("WmoRenderer.FindCameraSeeds test anchor disappeared");
    MethodInfo gather = renderer.GetMethod("CollectCameraOnlyCollision",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "WmoRenderer camera-only MOPY gather test anchor disappeared");

    // Camera-only means the exact set difference: DETAIL set and
    // NOCAMCOLLIDE clear. Ordinary and DETAIL|NOCAMCOLLIDE faces stay out.
    var source = new WmoGroupData
    {
        Vertices =
        [
            (0f, 0f, 0f), (1f, 0f, 0f), (0f, 1f, 0f),
            (0f, 0f, 1f), (1f, 0f, 1f), (0f, 1f, 1f),
            (0f, 0f, 2f), (1f, 0f, 2f), (0f, 1f, 2f),
        ],
        Indices = [0, 1, 2, 3, 4, 5, 6, 7, 8],
        TriMaterials = [(0x04, 0), (0x06, 0), (0x00, 0)],
    };
    var gathered = new List<Vector3>();
    gather.Invoke(null, [source, gathered]);
    Require(gathered.Count == 3 && gathered.All(p => p.Z == 0f),
        "camera-only gather must keep DETAIL and reject NOCAMCOLLIDE/non-DETAIL faces");

    object pocket = NewGroup(0, flags: 0, collision: [], cameraOnly: Quad(0f));
    object model = NewModel(pocket);

    var (seeds, drop) = Find(model, new Vector3(0f, 0f, 2f), null);
    Require(seeds.SequenceEqual([0]) && MathF.Abs(drop - 2f) < 0.001f,
        "camera-void fallback did not name the DETAIL-floored room");

    (seeds, _) = Find(model, new Vector3(0f, 0f, 2f), 8f);
    Require(seeds.SequenceEqual([0]),
        "terrain above the eye is off the down segment and must not gate camera fallback");
    (seeds, _) = Find(model, new Vector3(0f, 0f, 2f), 1.9f);
    Require(seeds.Length == 0,
        "terrain at/below the eye must preserve the ordinary outside verdict");

    // Any walking-face answer pre-empts the fallback, even when a DETAIL face
    // in another room is nearer. The returned drop proves Leg A won.
    object walkingHall = NewGroup(1, flags: 0, collision: Quad(-5f), cameraOnly: []);
    (seeds, drop) = Find(NewModel(pocket, walkingHall), new Vector3(0f, 0f, 2f), null);
    Require(seeds.SequenceEqual([1]) && MathF.Abs(drop - 7f) < 0.001f,
        "camera fallback changed an existing walking-leg verdict");

    object exterior = NewGroup(2, flags: 0x08, collision: [], cameraOnly: Quad(0f));
    (seeds, _) = Find(NewModel(exterior), new Vector3(0f, 0f, 2f), null);
    Require(seeds.Length == 0,
        "camera fallback must reject an EXTERIOR group");

    Console.WriteLine("[wmo-minimap] camera-only DETAIL fallback gates passed");

    object NewGroup(int index, uint flags, Vector3[] collision, Vector3[] cameraOnly)
    {
        object group = Activator.CreateInstance(groupType, nonPublic: true)
            ?? throw new InvalidOperationException("could not construct WMO test group");
        Set(group, "GroupIndex", index);
        Set(group, "GroupFlags", flags);
        Set(group, "LocalMin", new Vector3(-10f, -10f, -10f));
        Set(group, "LocalMax", new Vector3(10f, 10f, 12f));
        Set(group, "CollisionTriangles", collision);
        Set(group, "CameraOnlyTriangles", cameraOnly);
        return group;
    }

    object NewModel(params object[] groups)
    {
        object value = Activator.CreateInstance(modelType, nonPublic: true)
            ?? throw new InvalidOperationException("could not construct WMO test model");
        IList list = (IList)(modelType.GetField("Groups")?.GetValue(value)
            ?? throw new InvalidOperationException("WMO test model has no group list"));
        foreach (object group in groups) list.Add(group);
        return value;
    }

    (int[] Seeds, float Drop) Find(object value, Vector3 eye, float? terrain)
    {
        object?[] args = [value, eye, terrain, 0f, 0];
        int[] found = (int[])(find.Invoke(null, args)
            ?? throw new InvalidOperationException("camera seed invocation returned null"));
        return (found, (float)args[3]!);
    }

    void Set(object target, string field, object value) =>
        (target.GetType().GetField(field)
            ?? throw new InvalidOperationException($"WMO test group field {field} disappeared"))
        .SetValue(target, value);

    static Vector3[] Quad(float z) =>
    [
        new(-10f, -10f, z), new(10f, -10f, z), new(10f, 10f, z),
        new(-10f, -10f, z), new(10f, 10f, z), new(-10f, 10f, z),
    ];
}

static void CheckPortalWarmRadiusLaw()
{
    float[] radii = WmoMinimapProjection.ZoomRadiusYards;
    Require(radii.Length == 6 && radii[0] == 150f,
        "portal minimap warm must use the widest 150-yard authored view");
    for (int i = 1; i < radii.Length; i++)
        Require(radii[i] > 0f && radii[i] <= radii[0],
            $"indoor zoom radius {radii[i]} is not covered by the portal warm radius");
}

static void CheckSyntheticCatalogLaw()
{
    var rows = new[]
    {
        Row(1, 59, 0, uint.MaxValue, 10, 11, 12, 13, 14, 15, "Abbey"),
        Row(2, 59, 0, 1934, 0, 21, 0, 0, 0, 219, "Main Hall"),
        Row(3, 59, 2, uint.MaxValue, 30, 31, 32, 33, 34, 35, "Alternate Abbey"),
        Row(4, 77, 0, 9, 40, 0, 0, 0, 0, 0, "Old duplicate"),
        Row(5, 77, 0, 9, 50, 0, 0, 0, 0, 0, "Last duplicate"),
    };

    byte[] bytes = BuildWmoAreaDbc(rows);
    WmoAreaCatalog catalog = WmoAreaCatalog.Parse(bytes)
        ?? throw new InvalidOperationException("valid 20-column WMOAreaTable fixture was rejected");

    Require(catalog.Count == 4, "duplicate exact keys must be last-row-wins");
    Require(catalog.GroupRow(77, 0, 9)?.Id == 5,
        "last duplicate WMOAreaTable row did not win");
    Require(catalog.GroupRow(59, 2, 1934) is null,
        "GroupRow must not silently fall back to name set zero");
    Require(catalog.DefaultRow(59, 9) is null,
        "DefaultRow must remain an exact name-set query");

    WmoAreaRow merged = catalog.Resolve(59, 0, 1934)
        ?? throw new InvalidOperationException("group/default overlay did not resolve");
    Require(merged.Id == 2 && merged.SoundProvider == 10 &&
            merged.SoundProviderUnderwater == 21 && merged.AmbienceId == 12 &&
            merged.ZoneMusicId == 13 && merged.IntroSoundId == 14 &&
            merged.AreaTableId == 219 && merged.Name == "Main Hall",
        "sparse group values did not overlay the whole-WMO default");

    WmoAreaRow selectedNameSet = catalog.Resolve(59, 2, 1934)
        ?? throw new InvalidOperationException("selected name-set default did not resolve");
    Require(selectedNameSet.Id == 3 && selectedNameSet.NameSetId == 2,
        "a populated selected name set must not mix with name set zero");

    WmoAreaRow retriedNameSet = catalog.Resolve(59, 9, 1934)
        ?? throw new InvalidOperationException("name-set-zero retry did not resolve");
    Require(retriedNameSet.Id == 2 && retriedNameSet.NameSetId == 0,
        "missing selected name set did not retry name set zero");
    Require(catalog.Resolve(999, 0, 1) is null,
        "unknown WMO identity must not fabricate an area row");

    byte[] wrongSchema = (byte[])bytes.Clone();
    BitConverter.GetBytes(12).CopyTo(wrongSchema, 8);
    Require(WmoAreaCatalog.Parse(wrongSchema) is null,
        "a non-vanilla WMOAreaTable schema must be rejected");
}

static void CheckJoinMetadataParsers()
{
    const uint rootWmoId = 0x1234ABCD;
    byte[] mohd = new byte[64];
    BitConverter.GetBytes(1u).CopyTo(mohd, 4); // nGroups keeps ParseRoot's validity law true.
    BitConverter.GetBytes(rootWmoId).CopyTo(mohd, 0x20);
    WmoRootData root = WmoReader.ParseRoot(Chunk("MOHD", mohd))
        ?? throw new InvalidOperationException("synthetic WMO root failed to parse");
    Require(root.WmoId == rootWmoId, "MOHD +0x20 WMOID was not retained");

    const uint groupWmoId = 0x89ABCDEF;
    byte[] groupHeader = new byte[68];
    BitConverter.GetBytes(groupWmoId).CopyTo(groupHeader, 0x38);
    byte[] vertices = new byte[3 * 12];
    BitConverter.GetBytes(1f).CopyTo(vertices, 12);
    BitConverter.GetBytes(1f).CopyTo(vertices, 28);
    byte[] indices = new byte[3 * 2];
    BitConverter.GetBytes((ushort)1).CopyTo(indices, 2);
    BitConverter.GetBytes((ushort)2).CopyTo(indices, 4);
    byte[] groupBody = Concat(groupHeader, Chunk("MOVT", vertices), Chunk("MOVI", indices));
    WmoGroupData group = WmoReader.ParseGroup(Chunk("MOGP", groupBody))
        ?? throw new InvalidOperationException("synthetic WMO group failed to parse");
    Require(group.GroupWmoId == groupWmoId,
        "MOGP +0x38 WMOGroupID was not retained");

    const ushort adtNameSet = 37;
    byte[] adt = BuildPlacementContainer(includeWdtHeader: false, nameSet: adtNameSet);
    AdtTerrainReader.WmoInstance adtPlacement = AdtTerrainReader.Parse(adt)?.Wmos?.Single()
        ?? throw new InvalidOperationException("synthetic ADT MODF failed to parse");
    Require(adtPlacement.NameSetId == adtNameSet,
        "ADT MODF +0x3C NameSetID was not retained");

    const ushort wdtNameSet = 73;
    byte[] wdt = BuildPlacementContainer(includeWdtHeader: true, nameSet: wdtNameSet);
    AdtTerrainReader.WmoInstance wdtPlacement = WdtFile.Parse(wdt)?.GlobalWmo
        ?? throw new InvalidOperationException("synthetic WDT MODF failed to parse");
    Require(wdtPlacement.NameSetId == wdtNameSet,
        "WDT MODF +0x3C NameSetID was not retained");
}

static void CheckArchivedVanillaAnchors()
{
    string dataRoot = Path.Combine(ClientConfig.FindRepoRoot(), "GameData", "Data");
    using var mount = new MpqMount(dataRoot);
    byte[] bytes = mount.ReadFile(WmoAreaCatalog.MpqPath)
        ?? throw new InvalidOperationException($"{WmoAreaCatalog.MpqPath} was not found");
    WmoAreaCatalog catalog = WmoAreaCatalog.Parse(bytes)
        ?? throw new InvalidOperationException("the live vanilla WMOAreaTable failed to parse");

    Require(catalog.Count == 21_105,
        $"expected 21,105 distinct vanilla rows, found {catalog.Count:N0}");

    WmoAreaRow northshire = catalog.Resolve(59, 0, 1934)
        ?? throw new InvalidOperationException("Northshire Main Hall anchor did not resolve");
    Require(northshire.AreaTableId == 219 && northshire.IntroSoundId == 221 &&
            northshire.ZoneMusicId == 204 && northshire.AmbienceId == 26 &&
            northshire.Name == "Main Hall",
        "Northshire group/default overlay no longer matches archived vanilla data");

    WmoAreaRow chapel = catalog.Resolve(656, 0, 478)
        ?? throw new InvalidOperationException("chapel interior anchor did not resolve");
    Require(chapel.SoundProvider == 75 &&
            chapel.SoundProviderUnderwater == 11 &&
            chapel.AmbienceId == 50 && chapel.ZoneMusicId == 0,
        "chapel WMOAreaTable identity no longer matches archived vanilla data");

    const string ironforgePath =
        @"World\wmo\KhazModan\Cities\Ironforge\Ironforge.wmo";
    byte[] ironforgeBytes = mount.ReadFile(ironforgePath)
        ?? throw new InvalidOperationException($"{ironforgePath} was not found");
    WmoRootData ironforgeRoot = WmoReader.ParseRoot(ironforgeBytes)
        ?? throw new InvalidOperationException("Ironforge root WMO failed to parse");
    DbcFile table = DbcFile.Parse(bytes)
        ?? throw new InvalidOperationException("WMOAreaTable failed its raw anchor scan");
    string ironforgeStem = ironforgePath[..^4];
    var ironforgeGroups = new Dictionary<uint, int>();
    for (int groupIndex = 0; groupIndex < ironforgeRoot.NGroups; groupIndex++)
    {
        byte[]? groupBytes = mount.ReadFile($"{ironforgeStem}_{groupIndex:D3}.wmo");
        if (groupBytes is null) continue;
        uint? groupWmoId = ReadMogpUniqueId(groupBytes);
        if (groupWmoId.HasValue) ironforgeGroups[groupWmoId.Value] = groupIndex;
    }
    int ironforgeRow = Enumerable.Range(0, table.RecordCount).FirstOrDefault(row =>
        table.GetUInt(row, 1) == ironforgeRoot.WmoId &&
        ironforgeGroups.ContainsKey(table.GetUInt(row, 3)) &&
        table.GetUInt(row, 10) == 1537);
    Require(table.GetUInt(ironforgeRow, 1) == ironforgeRoot.WmoId &&
            table.GetUInt(ironforgeRow, 10) == 1537 &&
            ironforgeGroups.ContainsKey(table.GetUInt(ironforgeRow, 3)),
        "Ironforge has no actual MOGP group joining to AreaTableID 1537");
    uint ironforgeNameSet = table.GetUInt(ironforgeRow, 2);
    uint ironforgeGroupId = table.GetUInt(ironforgeRow, 3);
    int ironforgeGroupIndex = ironforgeGroups[ironforgeGroupId];
    WmoAreaRow ironforge = catalog.Resolve(
        ironforgeRoot.WmoId, ironforgeNameSet, ironforgeGroupId)
        ?? throw new InvalidOperationException("Ironforge exact WMO-area join did not resolve");
    Require(ironforge.AreaTableId == 1537,
        "Ironforge exact root/name-set/group join did not resolve AreaTableID 1537");
    Console.WriteLine($"[wmo-area] Ironforge anchor: root={ironforgeRoot.WmoId} " +
        $"nameSet={ironforgeNameSet} group={ironforgeGroupId} " +
        $"file={ironforgeGroupIndex:D3} row={ironforge.Id}");

    CheckIronforgeMinimapAssets(mount, ironforgePath);
}

static void CheckIronforgeMinimapAssets(MpqMount mount, string ironforgePath)
{
    const int multiTileGroup = 66;
    string stem = WmoMinimapProjection.Stem(ironforgePath)
        ?? throw new InvalidOperationException("Ironforge WMO minimap stem was rejected");
    Require(stem == @"wmo\khazmodan\cities\ironforge\ironforge",
        "Ironforge WMO minimap stem is not archive-relative");

    string groupPath = ironforgePath[..^4] + $"_{multiTileGroup:D3}.wmo";
    WmoGroupData group = WmoReader.ParseGroup(mount.ReadFile(groupPath)
        ?? throw new InvalidOperationException($"{groupPath} was not found"))
        ?? throw new InvalidOperationException($"{groupPath} failed to parse");
    var xGrid = WmoMinimapProjection.AxisGrid(group.BbMaxX - group.BbMinX);
    var yGrid = WmoMinimapProjection.AxisGrid(group.BbMaxY - group.BbMinY);
    Require(xGrid == (2, 128f) && yGrid == (2, 128f),
        "Ironforge group 066 no longer resolves to its authored 2x2 minimap grid");

    byte[] translationBytes = mount.ReadFile(@"textures\Minimap\md5translate.trs")
        ?? throw new InvalidOperationException("minimap md5 translation table was not found");
    var translations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (string line in Encoding.UTF8.GetString(translationBytes).Split('\n'))
    {
        string[] parts = line.Trim().Split('\t', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2) translations[parts[0]] = parts[1];
    }

    for (int column = 0; column < xGrid.Count; column++)
    for (int row = 0; row < yGrid.Count; row++)
    {
        string logical = WmoMinimapProjection.LogicalTile(
            stem, multiTileGroup, column, row);
        Require(translations.TryGetValue(logical, out string? hashed),
            $"Ironforge authored minimap key is absent: {logical}");
        Require(mount.ReadFile(@"textures\Minimap\" + hashed) is not null,
            $"Ironforge authored minimap image is absent: {hashed}");
    }

    Console.WriteLine("[wmo-minimap] Ironforge group 066: 2x2 authored tile grid present");
}

static FixtureRow Row(uint id, uint wmo, uint nameSet, uint group,
    uint sound, uint underwater, uint ambience, uint music, uint intro,
    uint area, string name)
{
    var fields = new uint[WmoAreaCatalog.VanillaFieldCount];
    fields[0] = id;
    fields[1] = wmo;
    fields[2] = nameSet;
    fields[3] = group;
    fields[4] = sound;
    fields[5] = underwater;
    fields[6] = ambience;
    fields[7] = music;
    fields[8] = intro;
    fields[10] = area;
    // The fixture builder replaces field 11 with this row's string offset.
    return new FixtureRow(fields, name);
}

static byte[] BuildWmoAreaDbc(IReadOnlyList<FixtureRow> rows)
{
    using var strings = new MemoryStream();
    strings.WriteByte(0);
    foreach (FixtureRow row in rows)
    {
        row.Fields[11] = checked((uint)strings.Position);
        byte[] name = Encoding.UTF8.GetBytes(row.Name);
        strings.Write(name);
        strings.WriteByte(0);
    }

    using var result = new MemoryStream();
    using var writer = new BinaryWriter(result, Encoding.UTF8, leaveOpen: true);
    writer.Write(Encoding.ASCII.GetBytes("WDBC"));
    writer.Write(rows.Count);
    writer.Write(WmoAreaCatalog.VanillaFieldCount);
    writer.Write(WmoAreaCatalog.VanillaRecordSize);
    writer.Write(checked((int)strings.Length));
    foreach (FixtureRow row in rows)
        foreach (uint field in row.Fields)
            writer.Write(field);
    writer.Write(strings.ToArray());
    return result.ToArray();
}

static byte[] BuildPlacementContainer(bool includeWdtHeader, ushort nameSet)
{
    byte[] path = Encoding.ASCII.GetBytes("World\\Test\\Fixture.wmo\0");
    byte[] modf = new byte[64];
    BitConverter.GetBytes(nameSet).CopyTo(modf, 60);

    var chunks = new List<byte[]>();
    if (includeWdtHeader)
    {
        chunks.Add(Chunk("MVER", BitConverter.GetBytes(18u)));
        byte[] mphd = new byte[32];
        BitConverter.GetBytes(WdtFile.FlagGlobalWmo).CopyTo(mphd, 0);
        chunks.Add(Chunk("MPHD", mphd));
    }
    chunks.Add(Chunk("MWMO", path));
    if (!includeWdtHeader)
        chunks.Add(Chunk("MWID", BitConverter.GetBytes(0u)));
    chunks.Add(Chunk("MODF", modf));
    return Concat(chunks.ToArray());
}

static byte[] Chunk(string logicalId, byte[] body)
{
    byte[] id = Encoding.ASCII.GetBytes(logicalId);
    Array.Reverse(id);
    return Concat(id, BitConverter.GetBytes((uint)body.Length), body);
}

static uint? ReadMogpUniqueId(byte[] data)
{
    byte[] id = Encoding.ASCII.GetBytes("MOGP");
    Array.Reverse(id);
    uint mogp = BitConverter.ToUInt32(id);
    int offset = 0;
    while (offset + 8 <= data.Length)
    {
        uint magic = BitConverter.ToUInt32(data, offset);
        uint size = BitConverter.ToUInt32(data, offset + 4);
        int body = offset + 8;
        if (size > int.MaxValue || body + (long)size > data.Length) return null;
        if (magic == mogp && size >= 68)
            return BitConverter.ToUInt32(data, body + 0x38);
        offset = body + (int)size;
    }
    return null;
}

static byte[] Concat(params byte[][] parts)
{
    var result = new byte[parts.Sum(p => p.Length)];
    int offset = 0;
    foreach (byte[] part in parts)
    {
        part.CopyTo(result, offset);
        offset += part.Length;
    }
    return result;
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed record FixtureRow(uint[] Fields, string Name);

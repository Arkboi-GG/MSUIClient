using System.Numerics;
using MSUIClient.Formats;

namespace MSUIClient.World.Collision;

/// <summary>
/// Turns VMaNGOS's extracted vmaps into world-space collision triangles.
///
/// For each loaded ADT tile: read {map}_{col}_{row}.vmtile, resolve every spawn
/// to its .vmo, apply the spawn transform, and push the triangles into a
/// <see cref="CollisionWorld"/>. These are the server's OWN collision meshes, so
/// the client and mangosd agree on where the walls are by construction.
///
/// FILENAME RESOLUTION
///   A spawn's Name is something like "Elwynntreecanopy01.m2" and the geometry
///   is expected at "Elwynntreecanopy01.m2.vmo". Whether the extractor kept the
///   case, kept the path, or flattened it varies between cores and versions, so
///   rather than trusting one convention this builds a case-insensitive index of
///   the vmaps directory once and tries several candidate spellings per spawn.
///   Unresolved names are logged verbatim, because the actual convention is
///   worth knowing and a silent miss reads as "collision doesn't work".
///
/// M2 SPAWNS
///   Tree and fence collision comes from spawns flagged MOD_M2. Some cores use
///   M2 vmap geometry for line-of-sight only, not for movement, so
///   <paramref name="includeM2"/> exists: with it on you cannot walk through
///   tree trunks, but you may also collide with canopy geometry overhead. Start
///   with it on, and turn it off in config if Elwynn feels wrong.
/// </summary>
public sealed class VmapCollisionLoader
{
    private readonly string _root;

    /// <summary>Bare filename (case-insensitive) -> full path, built once.</summary>
    private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Parsed .vmo cache. Null means "looked for it, not there".</summary>
    private readonly Dictionary<string, VmapWorldModel?> _models = new(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> _unresolved = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _partial = [];

    /// <summary>
    /// World-space bounds of each WMO spawn actually baked in, kept so the
    /// rendered placement can be checked against the collision placement.
    ///
    /// These two arrive by completely different routes — MODF out of the ADT
    /// versus a vmtile spawn out of the server's own extraction — so where they
    /// disagree, one of the two transforms is wrong, and the disagreement is
    /// measurable rather than a matter of opinion. M2 spawns are skipped: there
    /// are tens of thousands and none of them are buildings.
    /// </summary>
    public List<(string Name, Vector3 Min, Vector3 Max, Vector3 Origin)> WmoSpawnBounds { get; } = [];

    /// <summary>
    /// Spawn identities already baked in. A model whose bounding box covers
    /// several tiles is listed in every one of them, and Stormwind.wmo is a
    /// single .vmo holding the entire city — so without this, six tiles bake
    /// roughly 900,000 triangles of Stormwind six times over.
    /// </summary>
    private readonly HashSet<(uint Id, string Name)> _seen = [];

    public int TilesLoaded { get; private set; }
    public int SpawnsSeen { get; private set; }
    public int SpawnsUsed { get; private set; }
    public int SpawnsSkippedM2 { get; private set; }
    public int SpawnsDuplicate { get; private set; }
    public int SpawnsUnresolved { get; private set; }
    public int TrianglesAdded { get; private set; }

    /// <summary>Distinct model names with no .vmo on disk.</summary>
    public int DistinctUnresolved => _unresolved.Count;

    /// <summary>Models that hit a parse error but yielded usable groups anyway.</summary>
    public IReadOnlyList<string> PartiallyParsed => _partial;

    public VmapCollisionLoader(string vmapDirectory)
    {
        _root = vmapDirectory;

        foreach (var path in Directory.EnumerateFiles(_root, "*", SearchOption.TopDirectoryOnly))
            _files[Path.GetFileName(path)] = path;

        Console.WriteLine($"[collision] indexed {_files.Count} file(s) in {_root}");
    }

    /// <summary>
    /// Load one tile's placements into <paramref name="world"/>. Call
    /// <see cref="CollisionWorld.Build"/> once after all tiles are in.
    /// Returns false when the tile has no vmtile, which is normal for ocean.
    /// </summary>
    public bool LoadTile(CollisionWorld world, int map, int col, int row, bool includeM2)
    {
        var index = new VmapTileIndex(map, col, row);

        if (!_files.TryGetValue(index.FileName, out var tilePath))
        {
            Console.WriteLine($"[collision] tile [{col},{row}]: no {index.FileName}");
            return false;
        }

        IReadOnlyList<VmapModelSpawn> spawns;
        try
        {
            spawns = VmtileReader.Read(tilePath);
        }
        catch (Exception ex)
        {
            // Loudly, per the rule that cost 5,300 units of falling to learn:
            // a parse failure must never present as a physics bug later.
            Console.WriteLine($"[collision] tile [{col},{row}]: {index.FileName} FAILED to parse - {ex.Message}");
            return false;
        }

        int used = 0, skippedM2 = 0, duplicate = 0, missing = 0, tris = 0;

        foreach (var spawn in spawns)
        {
            SpawnsSeen++;

            if (spawn.IsM2 && !includeM2) { skippedM2++; continue; }

            // Already baked from a neighbouring tile. Identity is the spawn id
            // plus the model name; the id alone should be unique per map, but
            // pairing it with the name costs nothing and cannot merge two
            // genuinely different objects.
            if (!_seen.Add((spawn.Id, spawn.Name))) { duplicate++; continue; }

            var model = ResolveModel(spawn);
            if (model is null) { missing++; continue; }

            // One source id per spawn instance, so a hit names not just the
            // model but which placement of it.
            int sourceId = world.RegisterSource($"{spawn.Name}#{spawn.Id}");

            var rot = spawn.BuildRotation();

            var spawnMin = new Vector3(float.MaxValue);
            var spawnMax = new Vector3(float.MinValue);

            foreach (var group in model.Groups)
            {
                var verts = group.Vertices;
                var idx = group.Indices;

                for (int i = 0; i + 2 < idx.Length; i += 3)
                {
                    uint i0 = idx[i], i1 = idx[i + 1], i2 = idx[i + 2];
                    if (i0 >= verts.Length || i1 >= verts.Length || i2 >= verts.Length) continue;

                    var a = ToWorld(spawn.TransformToWorld(verts[i0], rot));
                    var b = ToWorld(spawn.TransformToWorld(verts[i1], rot));
                    var c = ToWorld(spawn.TransformToWorld(verts[i2], rot));

                    world.AddTriangle(a, b, c, sourceId);

                    spawnMin = Vector3.Min(spawnMin, Vector3.Min(a, Vector3.Min(b, c)));
                    spawnMax = Vector3.Max(spawnMax, Vector3.Max(a, Vector3.Max(b, c)));

                    tris++;
                }
            }

            if (!spawn.IsM2 && spawnMin.X < float.MaxValue)
            {
                // The spawn ORIGIN, not the mesh bounds. Bounds depend on which
                // triangles the extractor kept; the origin is pure placement,
                // so comparing it against the MODF origin isolates the
                // transform from the geometry.
                WmoSpawnBounds.Add((spawn.Name, spawnMin, spawnMax, ToWorld(spawn.Position)));
            }

            used++;
        }

        TilesLoaded++;
        SpawnsUsed += used;
        SpawnsSkippedM2 += skippedM2;
        SpawnsDuplicate += duplicate;
        SpawnsUnresolved += missing;
        TrianglesAdded += tris;

        Console.WriteLine(
            $"[collision] tile [{col},{row}]: {spawns.Count} spawn(s), {used} used, " +
            $"{duplicate} already loaded, {skippedM2} m2 skipped, " +
            $"{missing} with no .vmo, {tris:N0} triangles");

        return true;
    }

    /// <summary>
    /// Find and parse the .vmo for a spawn. Tries the documented convention
    /// first, then the spellings other extractors produce.
    /// </summary>
    private VmapWorldModel? ResolveModel(VmapModelSpawn spawn)
    {
        string key = spawn.Name;

        if (_models.TryGetValue(key, out var cached)) return cached;

        string? path = null;
        foreach (var candidate in Candidates(spawn.Name))
        {
            if (_files.TryGetValue(candidate, out path)) break;
            path = null;
        }

        if (path is null)
        {
            _models[key] = null;

            // Log each distinct name once. Most of these are decoration —
            // torches, lanterns, signposts, weapon racks — which the extractor
            // never wrote a .vmo for because they have no collision geometry.
            // A dense city tile is mostly this, and shouting once per spawn
            // would bury the misses that actually matter.
            if (_unresolved.Add(spawn.Name) && _unresolved.Count <= 8)
                Console.WriteLine($"[collision] no .vmo for '{spawn.Name}'");

            return null;
        }

        var model = VmoReader.ReadTolerant(path, out string? error);

        if (error is not null)
        {
            string file = Path.GetFileName(path);
            _partial.Add(file);
            Console.WriteLine(
                $"[collision] {file} parsed {model.Groups.Length} group(s) then stopped - {error}");
        }

        _models[key] = model;
        return model;
    }

    /// <summary>
    /// Candidate .vmo filenames for a spawn name, most likely first. Cheap to
    /// try them all; the index lookup is a dictionary hit.
    /// </summary>
    private static IEnumerable<string> Candidates(string name)
    {
        // 0. The extractor's ".mdx" -> ".m2" rewrite happens in place, keeping
        //    the string length - so vmtile spawns arrive as "Torch.m2\0" (a
        //    padding NUL, printed as a blank) while the file on disk is
        //    "Torch.m2.vmo". Every candidate below works from the trimmed
        //    name; without this, torches, flagpoles and weapon racks with
        //    REAL .vmo geometry silently lose their collision.
        name = name.Trim().Trim('\0', ' ');

        // 1. The documented convention: "Elwynntreecanopy01.m2" -> "...m2.vmo"
        yield return name + ".vmo";

        // 2. Already carries the extension.
        if (name.EndsWith(".vmo", StringComparison.OrdinalIgnoreCase))
            yield return name;

        // 3. Stored with a path; the file on disk is the leaf.
        int slash = name.LastIndexOfAny(['\\', '/']);
        if (slash >= 0 && slash + 1 < name.Length)
        {
            string leaf = name[(slash + 1)..];
            yield return leaf + ".vmo";
            yield return leaf;
        }

        // 4. Stored with a path that the extractor flattened by deleting
        //    separators rather than taking the leaf.
        if (name.IndexOfAny(['\\', '/']) >= 0)
        {
            string flat = name.Replace("\\", "").Replace("/", "");
            yield return flat + ".vmo";
            yield return flat;
        }

        // 5. Extension swapped rather than appended.
        int dot = name.LastIndexOf('.');
        if (dot > 0) yield return name[..dot] + ".vmo";
    }

    /// <summary>
    /// Transformed vmap vertex to a world-space Vector3. The spawn transform
    /// happens entirely in vmap internal space — which is correct, because the
    /// spawn position and rotation are stored in that space — and the result is
    /// converted out here, at the last possible moment.
    /// </summary>
    private static Vector3 ToWorld(Vec3 v)
    {
        var w = VmapFormat.ToWorld(v);
        return new Vector3(w.X, w.Y, w.Z);
    }

    /// <summary>One-line summary for the console after all tiles are loaded.</summary>
    public string Summary()
        => $"{TilesLoaded} tile(s), {SpawnsUsed}/{SpawnsSeen} spawn(s) used, " +
           $"{SpawnsDuplicate} cross-tile duplicate(s), {SpawnsSkippedM2} m2 skipped, " +
           $"{SpawnsUnresolved} spawn(s) with no .vmo " +
           $"({DistinctUnresolved} distinct model(s)), " +
           $"{TrianglesAdded:N0} triangles, {_models.Count} model(s) looked up";
}

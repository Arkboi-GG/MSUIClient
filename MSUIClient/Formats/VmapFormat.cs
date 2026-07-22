using System.Buffers.Binary;
using System.Text;

namespace MSUIClient.Formats;

/// <summary>
/// Readers for VMaNGOS's extracted collision data (design doc DD-6).
///
/// These are the server's OWN collision meshes — the geometry mangosd uses for
/// line-of-sight and height checks. Reusing them means the browser client and
/// the server agree on where the walls are by construction, and we never have
/// to write a WMO/M2 collision parser.
///
/// Two file types, both under {DataDir}/vmaps:
///
///   {map:D3}_{col:D2}_{row:D2}.vmtile   placements: which models sit on this
///                                       ADT tile, and at what transform
///   {ModelName}.vmo                     geometry: the actual triangle soup
///
/// TILE INDEX ORDER (verified empirically, 2026-07-21):
///   col (FIRST number)  = floor(32 - worldY / 533.33333)
///   row (SECOND number) = floor(32 - worldX / 533.33333)
/// The axes are swapped relative to the obvious reading. Northshire's human
/// start (x=-8949.95, y=-132.49) lands on 000_32_48.vmtile, whose contents are
/// Elwynntreecanopy*.m2 — 000_48_32.vmtile is a different place entirely.
/// Use <see cref="VmapTileIndex.FromWorld"/>; never open-code the formula.
///
/// Formats confirmed against real bytes AND against the server source
/// (TileAssembler.cpp, WorldModel.cpp, BIH.cpp).
/// </summary>
public static class VmapFormat
{
    public static readonly byte[] Magic = "VMAP_7.0"u8.ToArray();

    /// <summary>ADT grid cell size in world units.</summary>
    public const double GridSize = 533.33333;

    /// <summary>
    /// Half the map in world units: 0.5 * 64 tiles * 533.33333 = 17066.67.
    ///
    /// VMAP FILES DO NOT STORE WORLD COORDINATES. Everything inside a .vmtile
    /// and .vmo — spawn positions, model vertices, bounds — is in VMAP's
    /// "internal representation", which VMapManager2::convertPositionToInternalRep
    /// produces from world coordinates as:
    ///
    ///     internal.x = mid - world.x
    ///     internal.y = mid - world.y
    ///     internal.z = world.z          (Z is NOT shifted)
    ///
    /// The server converts every query into this space and works there. We do
    /// the opposite and convert the geometry out, once, at the read site — the
    /// same treatment AdtTerrainReader gives the MCNK header's (Y, X, Z) field
    /// order, and for the same reason: everything downstream should mean what
    /// its name says.
    ///
    /// Two things make this safe rather than scary:
    ///
    /// 1. It is an involution. mid - (mid - x) = x, so <see cref="ToWorld"/>
    ///    converts in both directions and there is only one function to get
    ///    right.
    ///
    /// 2. Negating X and Y is NOT a mirror. The matrix diag(-1, -1, 1) has
    ///    determinant +1 — it is a 180-degree rotation about Z. Triangle
    ///    winding and surface normals survive untouched, which is why collision
    ///    geometry does not come out inside-out.
    ///
    /// It also explains the tile index formula, which looked backwards and was
    /// only settled empirically: floor(32 - worldY / 533.33) is exactly
    /// floor(internalY / 533.33). The filename numbers are internal-space grid
    /// indices, so <see cref="VmapTileIndex.FromWorld"/> was right all along and
    /// needs no change.
    /// </summary>
    public const double CoordShift = 32.0 * GridSize;

    /// <summary>
    /// vmap internal coordinates to WoW world space, and back — the map is its
    /// own inverse.
    /// </summary>
    public static Vec3 ToWorld(Vec3 v)
        => new((float)(CoordShift - v.X), (float)(CoordShift - v.Y), v.Z);

    // ModelSpawn flags. Values deduced from the data and cross-checked against
    // TileAssembler.cpp (MOD_WORLDSPAWN spawns are written to tile 65/65;
    // MOD_HAS_BOUND is set unconditionally by calculateTransformedBound).
    public const uint ModM2 = 1;
    public const uint ModWorldSpawn = 2;
    public const uint ModHasBound = 4;
}

public readonly record struct Vec3(float X, float Y, float Z);

public readonly record struct VmapTileIndex(int Map, int Col, int Row)
{
    /// <summary>
    /// World coords -> vmap/ADT tile index. Note the axis swap: the FIRST
    /// filename number comes from Y, the SECOND from X.
    /// </summary>
    public static VmapTileIndex FromWorld(int map, double worldX, double worldY) => new(
        map,
        (int)Math.Floor(32.0 - worldY / VmapFormat.GridSize),
        (int)Math.Floor(32.0 - worldX / VmapFormat.GridSize));

    public string FileName => $"{Map:D3}_{Col:D2}_{Row:D2}.vmtile";

    public override string ToString() => $"map{Map}[{Col},{Row}]";
}

/// <summary>One model placement on a tile. Mirrors VMAP::ModelSpawn.</summary>
public sealed class VmapModelSpawn
{
    public uint Flags { get; init; }
    public ushort AdtId { get; init; }
    public uint Id { get; init; }

    /// <summary>World position of the model origin.</summary>
    public Vec3 Position { get; init; }

    /// <summary>Euler angles in DEGREES. See <see cref="BuildRotation"/>.</summary>
    public Vec3 Rotation { get; init; }

    public float Scale { get; init; }

    /// <summary>Present only when <see cref="VmapFormat.ModHasBound"/> is set.</summary>
    public Vec3 BoundLo { get; init; }
    public Vec3 BoundHi { get; init; }
    public bool HasBound => (Flags & VmapFormat.ModHasBound) != 0;

    /// <summary>Model filename, e.g. "Elwynntreecanopy03.m2" — resolves to "{name}.vmo".</summary>
    public string Name { get; init; } = "";

    /// <summary>Trailing MapTree node index. We don't use it; kept for fidelity.</summary>
    public uint NodeIndex { get; init; }

    public bool IsM2 => (Flags & VmapFormat.ModM2) != 0;
    public bool IsWorldSpawn => (Flags & VmapFormat.ModWorldSpawn) != 0;

    /// <summary>The .vmo file this spawn refers to.</summary>
    public string VmoFileName => Name + ".vmo";

    /// <summary>
    /// Row-major 3x3 rotation, matching G3D::Matrix3::fromEulerAnglesZYX(
    ///   pi*iDir.y/180, pi*iDir.x/180, pi*iDir.z/180)
    /// as used by VMAP::ModelPosition::init(). Note the y/x/z argument order —
    /// it is NOT the obvious x/y/z, and getting it wrong rotates every doodad
    /// in the world by the wrong axis.
    ///
    /// A model-space vertex maps to world space as:
    ///     world = Position + Rotation * (vertex * Scale)
    /// (VMAP::ModelPosition::transform, then + spawn.iPos.)
    /// </summary>
    public double[] BuildRotation()
    {
        // fromEulerAnglesZYX(ez, ey, ex) = Rz(ez) * Ry(ey) * Rx(ex)
        double ez = Math.PI * Rotation.Y / 180.0;
        double ey = Math.PI * Rotation.X / 180.0;
        double ex = Math.PI * Rotation.Z / 180.0;

        double cz = Math.Cos(ez), sz = Math.Sin(ez);
        double cy = Math.Cos(ey), sy = Math.Sin(ey);
        double cx = Math.Cos(ex), sx = Math.Sin(ex);

        return new[]
        {
            cz * cy,  cz * sy * sx - sz * cx,  cz * sy * cx + sz * sx,
            sz * cy,  sz * sy * sx + cz * cx,  sz * sy * cx - cz * sx,
            -sy,      cy * sx,                 cy * cx,
        };
    }

    /// <summary>Model-space vertex -> world space.</summary>
    public Vec3 TransformToWorld(Vec3 v, double[] rot)
    {
        double sx = v.X * Scale, sy = v.Y * Scale, sz = v.Z * Scale;
        return new Vec3(
            (float)(rot[0] * sx + rot[1] * sy + rot[2] * sz + Position.X),
            (float)(rot[3] * sx + rot[4] * sy + rot[5] * sz + Position.Y),
            (float)(rot[6] * sx + rot[7] * sy + rot[8] * sz + Position.Z));
    }
}

/// <summary>One collision mesh group. Mirrors VMAP::GroupModel.</summary>
public sealed class VmapGroupModel
{
    public Vec3 BoundLo { get; init; }
    public Vec3 BoundHi { get; init; }

    /// <summary>0x8 = outdoor, 0x2000 = indoor (per WorldModel.h).</summary>
    public uint MogpFlags { get; init; }
    public uint GroupWmoId { get; init; }

    public Vec3[] Vertices { get; init; } = [];

    /// <summary>Triangle indices, flattened: [i0,i1,i2, i0,i1,i2, ...].</summary>
    public uint[] Indices { get; init; } = [];

    public uint LiquidType { get; init; }
    public bool HasLiquid { get; init; }

    public int TriangleCount => Indices.Length / 3;
    public bool IsIndoor => (MogpFlags & 0x2000) != 0;
}

/// <summary>A whole .vmo file. Mirrors VMAP::WorldModel.</summary>
public sealed class VmapWorldModel
{
    public uint RootWmoId { get; init; }
    public VmapGroupModel[] Groups { get; init; } = [];

    public int TotalVertices => Groups.Sum(g => g.Vertices.Length);
    public int TotalTriangles => Groups.Sum(g => g.TriangleCount);
}

/// <summary>
/// Little-endian cursor over a byte[]. Everything here is fixed-layout structs
/// written by fwrite on x86, so no alignment or endian surprises.
/// </summary>
internal sealed class VmapCursor(byte[] buffer, string what)
{
    private readonly byte[] _b = buffer;
    public int Offset { get; private set; }
    public int Remaining => _b.Length - Offset;

    private ReadOnlySpan<byte> Take(int n)
    {
        if (Offset + n > _b.Length)
            throw new InvalidDataException(
                $"{what}: wanted {n} bytes at offset {Offset}, only {Remaining} remain");
        var s = _b.AsSpan(Offset, n);
        Offset += n;
        return s;
    }

    public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
    public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    public float F32() => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(Take(4)));
    public Vec3 V3() => new(F32(), F32(), F32());

    public string Ascii(int n) => Encoding.ASCII.GetString(Take(n));

    public void Skip(int n) => Take(n);

    public int Length => _b.Length;

    /// <summary>
    /// Absolute reposition. Needed because one chunk in this format declares a
    /// size that is wrong — see VmoReader.ResolveLiquidLength.
    /// </summary>
    public void SeekTo(int offset)
    {
        if (offset < 0 || offset > _b.Length)
            throw new InvalidDataException(
                $"{what}: seek to {offset} is outside the file (length {_b.Length})");
        Offset = offset;
    }

    /// <summary>Read a u32 at an absolute offset without moving the cursor.</summary>
    public uint U32At(int offset)
        => offset >= 0 && offset + 4 <= _b.Length
            ? BinaryPrimitives.ReadUInt32LittleEndian(_b.AsSpan(offset, 4))
            : 0;

    /// <summary>Does this magic sit at that absolute offset? Cursor unmoved.</summary>
    public bool MatchesAt(int offset, ReadOnlySpan<byte> magic)
        => offset >= 0 && offset + magic.Length <= _b.Length
           && _b.AsSpan(offset, magic.Length).SequenceEqual(magic);

    public void Expect(ReadOnlySpan<byte> magic, string label)
    {
        int at = Offset;
        var got = Take(magic.Length);
        if (!got.SequenceEqual(magic))
            throw new InvalidDataException(
                $"{what}: expected '{Encoding.ASCII.GetString(magic)}' ({label}) at offset {at}, " +
                $"got '{Encoding.ASCII.GetString(got)}'");
    }

    public bool Peek(ReadOnlySpan<byte> magic)
        => Remaining >= magic.Length && _b.AsSpan(Offset, magic.Length).SequenceEqual(magic);

    public Vec3[] V3Array(int count)
    {
        var a = new Vec3[count];
        for (int i = 0; i < count; i++) a[i] = V3();
        return a;
    }

    public uint[] U32Array(int count)
    {
        var a = new uint[count];
        for (int i = 0; i < count; i++) a[i] = U32();
        return a;
    }
}

/// <summary>
/// Reads a .vmtile — the list of model placements for one ADT tile.
///
/// Layout (TileAssembler.cpp, convertWorld2):
///   "VMAP_7.0"                 8 bytes
///   u32 nSpawns
///   nSpawns x {
///     u32 flags
///     u16 adtId
///     u32 id
///     Vector3 pos
///     Vector3 rot            (Euler DEGREES, ZYX order — see BuildRotation)
///     f32 scale
///     [AABox lo, hi]         6 floats, ONLY if flags &amp; MOD_HAS_BOUND
///     u32 nameLen
///     char[nameLen]          NOT null-terminated
///     u32 nodeIndex          trailing MapTree node index, one per spawn
///   }
///
/// That trailing nodeIndex is easy to miss — without it every spawn after the
/// first is misaligned by 4 bytes and the parse silently produces garbage.
/// </summary>
public static class VmtileReader
{
    public static IReadOnlyList<VmapModelSpawn> Read(string path)
        => Parse(File.ReadAllBytes(path), Path.GetFileName(path));

    public static IReadOnlyList<VmapModelSpawn> Parse(byte[] bytes, string what)
    {
        var c = new VmapCursor(bytes, what);
        c.Expect(VmapFormat.Magic, "file magic");

        uint n = c.U32();
        if (n > 100_000)
            throw new InvalidDataException($"{what}: implausible spawn count {n}");

        var list = new List<VmapModelSpawn>((int)n);
        for (uint i = 0; i < n; i++)
        {
            uint flags = c.U32();
            ushort adtId = c.U16();
            uint id = c.U32();
            Vec3 pos = c.V3();
            Vec3 rot = c.V3();
            float scale = c.F32();

            Vec3 lo = default, hi = default;
            if ((flags & VmapFormat.ModHasBound) != 0)
            {
                lo = c.V3();
                hi = c.V3();
            }

            uint nameLen = c.U32();
            if (nameLen > 512)
                throw new InvalidDataException(
                    $"{what}: spawn {i} has implausible name length {nameLen} at offset {c.Offset} " +
                    $"(usually means a misaligned read earlier in the file)");
            string name = c.Ascii((int)nameLen);

            uint nodeIndex = c.U32();

            list.Add(new VmapModelSpawn
            {
                Flags = flags,
                AdtId = adtId,
                Id = id,
                Position = pos,
                Rotation = rot,
                Scale = scale,
                BoundLo = lo,
                BoundHi = hi,
                Name = name,
                NodeIndex = nodeIndex,
            });
        }

        return list;
    }
}

/// <summary>
/// Reads a .vmo — the collision geometry for one model.
///
/// Layout (WorldModel.cpp writeFile + GroupModel::writeToFile):
///   "VMAP_7.0"                        8 bytes
///   "WMOD" u32 chunkSize u32 RootWMOID
///   "GMOD" u32 groupCount             NOTE: no chunkSize — commented out in source
///     per group:
///       AABox bound                   6 floats (lo.xyz, hi.xyz)
///       u32 mogpFlags
///       u32 groupWMOID
///       "VERT" u32 chunkSize u32 count  Vector3[count]
///       "TRIM" u32 chunkSize u32 count  MeshTriangle[count]   (3 x u32)
///       "MBIH" &lt;BIH blob&gt;
///       "LIQU" u32 chunkSize [WmoLiquid if chunkSize &gt; 0]
///   "GBIH" &lt;BIH blob&gt;
///
/// chunkSize for VERT/TRIM includes the 4-byte count field
/// (verified: 699 verts -> 699*12 + 4 = 8392).
///
/// The BIH blobs are skipped, not parsed — three-mesh-bvh builds its own
/// acceleration structure in the browser. Skip size is exact arithmetic from
/// BIH::writeToFile.
/// </summary>
public static class VmoReader
{
    private static ReadOnlySpan<byte> Wmod => "WMOD"u8;
    private static ReadOnlySpan<byte> Gmod => "GMOD"u8;
    private static ReadOnlySpan<byte> Vert => "VERT"u8;
    private static ReadOnlySpan<byte> Trim => "TRIM"u8;
    private static ReadOnlySpan<byte> Mbih => "MBIH"u8;
    private static ReadOnlySpan<byte> Liqu => "LIQU"u8;
    private static ReadOnlySpan<byte> Gbih => "GBIH"u8;

    public static VmapWorldModel Read(string path)
        => Parse(File.ReadAllBytes(path), Path.GetFileName(path));

    /// <summary>
    /// Read, keeping whatever parsed successfully if a later group fails.
    ///
    /// Losing a whole model to one bad group is far worse than losing the group.
    /// Stormwind is a single .vmo: a failure at group 95 of 100 silently deletes
    /// an entire city's collision. The error is still reported — never swallowed
    /// — but the caller gets the geometry that did parse.
    /// </summary>
    public static VmapWorldModel ReadTolerant(string path, out string? error)
        => ParseTolerant(File.ReadAllBytes(path), Path.GetFileName(path), out error);

    public static VmapWorldModel ParseTolerant(byte[] bytes, string what, out string? error)
    {
        error = null;

        uint rootWmoId = 0;
        var groups = new List<VmapGroupModel>();

        try
        {
            var c = new VmapCursor(bytes, what);
            c.Expect(VmapFormat.Magic, "file magic");

            c.Expect(Wmod, "root chunk");
            _ = c.U32();
            rootWmoId = c.U32();

            if (c.Peek(Gmod))
            {
                c.Expect(Gmod, "group chunk");
                uint count = c.U32();
                if (count > 100_000)
                    throw new InvalidDataException($"{what}: implausible group count {count}");

                for (uint g = 0; g < count; g++)
                    groups.Add(ReadGroup(c, what, g, g == count - 1));
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        return new VmapWorldModel { RootWmoId = rootWmoId, Groups = groups.ToArray() };
    }

    public static VmapWorldModel Parse(byte[] bytes, string what)
    {
        var c = new VmapCursor(bytes, what);
        c.Expect(VmapFormat.Magic, "file magic");

        c.Expect(Wmod, "root chunk");
        _ = c.U32();                    // chunkSize — the server reads it and ignores it
        uint rootWmoId = c.U32();

        var groups = new List<VmapGroupModel>();

        // GMOD is absent for models with no geometry.
        if (c.Peek(Gmod))
        {
            c.Expect(Gmod, "group chunk");
            uint count = c.U32();
            if (count > 100_000)
                throw new InvalidDataException($"{what}: implausible group count {count}");

            for (uint g = 0; g < count; g++)
                groups.Add(ReadGroup(c, what, g, g == count - 1));

            if (c.Peek(Gbih))
            {
                c.Expect(Gbih, "group BIH");
                SkipBih(c, what, "GBIH");
            }
        }

        return new VmapWorldModel { RootWmoId = rootWmoId, Groups = groups.ToArray() };
    }

    private static VmapGroupModel ReadGroup(VmapCursor c, string what, uint index, bool isLast)
    {
        Vec3 lo = c.V3(), hi = c.V3();
        uint mogpFlags = c.U32();
        uint groupWmoId = c.U32();

        c.Expect(Vert, $"group {index} vertices");
        uint vChunk = c.U32();
        uint vCount = c.U32();
        if (vChunk != vCount * 12 + 4)
            throw new InvalidDataException(
                $"{what}: group {index} VERT chunkSize {vChunk} disagrees with count {vCount} " +
                $"(expected {vCount * 12 + 4})");
        var vertices = c.V3Array((int)vCount);

        c.Expect(Trim, $"group {index} triangles");
        uint tChunk = c.U32();
        uint tCount = c.U32();
        if (tChunk != tCount * 12 + 4)
            throw new InvalidDataException(
                $"{what}: group {index} TRIM chunkSize {tChunk} disagrees with count {tCount} " +
                $"(expected {tCount * 12 + 4})");
        var indices = c.U32Array((int)tCount * 3);

        c.Expect(Mbih, $"group {index} mesh BIH");
        SkipBih(c, what, $"group {index} MBIH");

        c.Expect(Liqu, $"group {index} liquid");
        uint liqSize = c.U32();
        int liqStart = c.Offset;
        bool hasLiquid = liqSize > 0;
        uint liquidType = 0;

        if (hasLiquid)
        {
            // WmoLiquid: u32 tilesX, u32 tilesY, Vector3 corner, u32 type,
            //            float[(tilesX+1)*(tilesY+1)], u8[tilesX*tilesY]
            liquidType = c.U32At(liqStart + 20);

            int length = ResolveLiquidLength(c, what, index, liqStart, (int)liqSize, isLast);
            c.SeekTo(liqStart + length);
        }

        return new VmapGroupModel
        {
            BoundLo = lo,
            BoundHi = hi,
            MogpFlags = mogpFlags,
            GroupWmoId = groupWmoId,
            Vertices = vertices,
            Indices = indices,
            LiquidType = liquidType,
            HasLiquid = hasLiquid,
        };
    }

    /// <summary>
    /// Work out how many bytes the LIQU payload actually occupies.
    ///
    /// THE DECLARED SIZE IS WRONG, AND IT IS WRONG IN THE CORE, NOT HERE.
    /// VMaNGOS writes the chunk size from WmoLiquid::GetFileSize(), which sums
    /// tilesX, tilesY, the corner Vector3, the height grid and the flag grid —
    /// but NOT the u32 liquid type that WmoLiquid::writeToFile then writes. So
    /// every liquid chunk on disk is exactly 4 bytes longer than the size field
    /// in front of it. Confirmed against three unrelated models with completely
    /// different tile counts: Stormwind 799 vs 795, Abbeygate01 116 vs 112,
    /// Blackrock 22438 vs 22434. A constant delta across those sizes can only be
    /// one missing fixed-width field.
    ///
    /// Rather than hardcode +4 and hope, this validates. The payload is followed
    /// either by the next group — AABox 24 + mogpFlags 4 + groupWMOID 4 = 32
    /// bytes, then "VERT" — or, for the last group, by "GBIH" or end of file.
    /// Whichever candidate length puts that token where it belongs is the right
    /// one, so the parse stays correct even if a future core fixes GetFileSize.
    /// </summary>
    private static int ResolveLiquidLength(
        VmapCursor c, string what, uint index, int start, int declared, bool isLast)
    {
        int[] candidates = [declared, declared + 4];

        foreach (int length in candidates)
        {
            int next = start + length;

            bool ok = isLast
                ? next == c.Length || c.MatchesAt(next, Gbih)
                : c.MatchesAt(next + 32, Vert);

            if (ok) return length;
        }

        throw new InvalidDataException(
            $"{what}: group {index} LIQU declares {declared} bytes at offset {start}, but neither " +
            $"{declared} nor {declared + 4} puts the following " +
            (isLast ? "\"GBIH\" or end of file" : "\"VERT\"") +
            " where it belongs — this file is malformed, or the group layout has changed");
    }

    /// <summary>
    /// Skip a BIH blob. From BIH::writeToFile:
    ///   float lo[3], float hi[3], u32 treeSize, u32 tree[treeSize],
    ///   u32 count, u32 objects[count]
    /// so the exact size is 24 + 4 + treeSize*4 + 4 + count*4.
    /// </summary>
    private static void SkipBih(VmapCursor c, string what, string label)
    {
        c.Skip(24);                     // bounds lo + hi
        uint treeSize = c.U32();
        if (treeSize > 50_000_000)
            throw new InvalidDataException($"{what}: {label} implausible treeSize {treeSize}");
        c.Skip(checked((int)(treeSize * 4)));
        uint count = c.U32();
        if (count > 50_000_000)
            throw new InvalidDataException($"{what}: {label} implausible object count {count}");
        c.Skip(checked((int)(count * 4)));
    }
}

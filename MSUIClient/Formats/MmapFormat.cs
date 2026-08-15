using System.Numerics;

namespace MSUIClient.Formats;

/// <summary>
/// Reader for VMaNGOS's movement maps (mmaps) — the Recast/Detour navmesh the
/// server's pathfinding (and therefore every bot) walks on. One .mmtile per
/// ADT tile: a 20-byte MmapTileHeader followed by a serialized dtMeshTile
/// blob, parsed here directly so the client needs no Detour dependency.
///
/// FILE NAMING (verified empirically against the live server, 2026-08-14):
///   {map:D3}{A:D2}{B:D2}.mmtile where (A, B) = (row, col) in the repo's
///   vmtile/terrain tile convention — Northshire's 000_32_48.vmtile pairs
///   with 0004832.mmtile. The server source's snprintf reads (mapId, y, x),
///   which cancels against its own grid-coordinate swap; do not re-derive,
///   MmapNavLoader validates the pick against the header bounds anyway.
///
/// BINARY LAYOUT (vmangos MoveMapSharedDefines.h + dep/recastnavigation,
/// DT_POLYREF64 defined, all little-endian, natural alignment — every array
/// below lands 4-byte aligned so dtAlign4 inserts no padding):
///   MmapTileHeader: mmapMagic 'MMAP', dtVersion 7, mmapVersion 6, size,
///                   usesLiquids                                   (20 bytes)
///   dtMeshHeader:   magic 'DNAV', version 7, x, y, layer, userId,
///                   polyCount, vertCount, maxLinkCount, detailMeshCount,
///                   detailVertCount, detailTriCount, bvNodeCount,
///                   offMeshConCount, offMeshBase, walkableHeight/Radius/
///                   Climb, bmin[3], bmax[3], bvQuantFactor        (100 bytes)
///   verts           float[3 * vertCount]
///   polys           dtPoly[polyCount]              (32 bytes each)
///   links           dtLink[maxLinkCount]           (16 bytes each: 64-bit ref)
///   detailMeshes    dtPolyDetail[detailMeshCount]  (12 bytes each)
///   detailVerts     float[3 * detailVertCount]
///   detailTris      byte[4 * detailTriCount]
///   (bvTree and off-mesh connections follow; not needed for drawing)
///
/// COORDINATES: Recast is Y-up. Mangos feeds it world coordinates rearranged
/// as (worldY, worldZ, worldX) — no CoordShift involution, unlike vmaps. So
/// world = (r[2], r[0], r[1]) and nothing is negated.
/// </summary>
public static class MmapFormat
{
    public const uint TileMagic = 0x4D4D4150;    // 'MMAP'
    public const uint TileVersion = 6;           // MMAP_VERSION
    public const int NavMagic = ('D' << 24) | ('N' << 16) | ('A' << 8) | 'V';
    public const int NavVersion = 7;             // DT_NAVMESH_VERSION

    /// <summary>Recast-space point to WoW world space.</summary>
    public static Vector3 ToWorld(float r0, float r1, float r2) => new(r2, r0, r1);
}

/// <summary>One parsed navmesh tile: world-space triangles plus bounds.</summary>
public sealed class MmapTileMesh
{
    /// <summary>Flat triangle list: 9 floats per triangle (a,b,c world xyz).</summary>
    public required float[] Triangles { get; init; }

    /// <summary>Per-triangle Detour area id (NavTerrain: 1 ground, 2 magma,
    /// 4 slime, 8 water...). Length = Triangles.Length / 9.</summary>
    public required byte[] Areas { get; init; }

    public required Vector3 BoundsMin { get; init; }
    public required Vector3 BoundsMax { get; init; }

    public int TriangleCount => Areas.Length;
}

public static class MmtileReader
{
    private const int TileHeaderSize = 20;
    private const int MeshHeaderSize = 100;
    private const int PolySize = 32;
    private const int LinkSize = 16;        // 8-byte dtPolyRef (DT_POLYREF64)
    private const int PolyDetailSize = 12;
    private const int VertsPerPolygon = 6;  // DT_VERTS_PER_POLYGON

    /// <summary>
    /// Parse one .mmtile into world-space triangles. Throws InvalidDataException
    /// on any header mismatch — loudly, per the rule that a parse failure must
    /// never present later as "the navmesh has a hole there".
    /// </summary>
    public static MmapTileMesh Read(string path)
    {
        byte[] b = File.ReadAllBytes(path);
        string name = Path.GetFileName(path);

        if (b.Length < TileHeaderSize + MeshHeaderSize)
            throw new InvalidDataException($"{name}: {b.Length} bytes is too small for any tile");

        uint tileMagic = BitConverter.ToUInt32(b, 0);
        uint dtVersion = BitConverter.ToUInt32(b, 4);
        uint mmapVersion = BitConverter.ToUInt32(b, 8);
        uint dataSize = BitConverter.ToUInt32(b, 12);
        if (tileMagic != MmapFormat.TileMagic)
            throw new InvalidDataException($"{name}: bad MMAP magic 0x{tileMagic:X8}");
        if (mmapVersion != MmapFormat.TileVersion || dtVersion != MmapFormat.NavVersion)
            throw new InvalidDataException(
                $"{name}: version mmap={mmapVersion} dt={dtVersion}, expected {MmapFormat.TileVersion}/{MmapFormat.NavVersion}");
        if (TileHeaderSize + dataSize > b.Length)
            throw new InvalidDataException($"{name}: header claims {dataSize} bytes, file has {b.Length - TileHeaderSize}");

        int h = TileHeaderSize;
        int navMagic = BitConverter.ToInt32(b, h);
        int navVersion = BitConverter.ToInt32(b, h + 4);
        if (navMagic != MmapFormat.NavMagic || navVersion != MmapFormat.NavVersion)
            throw new InvalidDataException($"{name}: bad DNAV header magic=0x{navMagic:X8} version={navVersion}");

        int polyCount = BitConverter.ToInt32(b, h + 24);
        int vertCount = BitConverter.ToInt32(b, h + 28);
        int maxLinkCount = BitConverter.ToInt32(b, h + 32);
        int detailMeshCount = BitConverter.ToInt32(b, h + 36);
        int detailVertCount = BitConverter.ToInt32(b, h + 40);
        int detailTriCount = BitConverter.ToInt32(b, h + 44);
        var bmin = new Vector3(
            BitConverter.ToSingle(b, h + 72), BitConverter.ToSingle(b, h + 76), BitConverter.ToSingle(b, h + 80));
        var bmax = new Vector3(
            BitConverter.ToSingle(b, h + 84), BitConverter.ToSingle(b, h + 88), BitConverter.ToSingle(b, h + 92));

        // Detour's addTile layout: header, verts, polys, links, detailMeshes,
        // detailVerts, detailTris. All sizes are multiples of 4 already.
        int vertsAt = h + MeshHeaderSize;
        int polysAt = vertsAt + 12 * vertCount;
        int detailMeshesAt = polysAt + PolySize * polyCount + LinkSize * maxLinkCount;
        int detailVertsAt = detailMeshesAt + PolyDetailSize * detailMeshCount;
        int detailTrisAt = detailVertsAt + 12 * detailVertCount;
        int end = detailTrisAt + 4 * detailTriCount;
        if (end > b.Length)
            throw new InvalidDataException(
                $"{name}: arrays overrun the file ({end} > {b.Length}) — layout assumption broken");

        var triangles = new List<float>(detailTriCount * 9);
        var areas = new List<byte>(detailTriCount);

        // Off-mesh connection polys are appended AFTER the base polys and own
        // no dtPolyDetail entry, so detailMeshCount = polyCount minus the
        // off-mesh count and indexes base polys 1:1. Iterating to
        // detailMeshCount both skips them and keeps the arrays in step; the
        // type check inside is belt on top of that.
        int basePolyCount = Math.Min(polyCount, detailMeshCount);
        for (int p = 0; p < basePolyCount; p++)
        {
            int poly = polysAt + p * PolySize;
            byte polyVertCount = b[poly + 30];
            byte areaAndType = b[poly + 31];
            if (areaAndType >> 6 == 1) continue;   // DT_POLYTYPE_OFFMESH_CONNECTION

            int pd = detailMeshesAt + p * PolyDetailSize;
            uint vertBase = BitConverter.ToUInt32(b, pd);
            uint triBase = BitConverter.ToUInt32(b, pd + 4);
            byte detailTrisInPoly = b[pd + 9];

            for (int t = 0; t < detailTrisInPoly; t++)
            {
                int tri = detailTrisAt + (int)(triBase + t) * 4;
                for (int k = 0; k < 3; k++)
                {
                    byte idx = b[tri + k];
                    int v = idx < polyVertCount
                        ? vertsAt + 12 * BitConverter.ToUInt16(b, poly + 4 + 2 * Math.Min((int)idx, VertsPerPolygon - 1))
                        : detailVertsAt + 12 * (int)(vertBase + idx - polyVertCount);
                    var world = MmapFormat.ToWorld(
                        BitConverter.ToSingle(b, v),
                        BitConverter.ToSingle(b, v + 4),
                        BitConverter.ToSingle(b, v + 8));
                    triangles.Add(world.X);
                    triangles.Add(world.Y);
                    triangles.Add(world.Z);
                }
                areas.Add((byte)(areaAndType & 0x3f));
            }
        }

        return new MmapTileMesh
        {
            Triangles = triangles.ToArray(),
            Areas = areas.ToArray(),
            BoundsMin = MmapFormat.ToWorld(bmin.X, bmin.Y, bmin.Z),
            BoundsMax = MmapFormat.ToWorld(bmax.X, bmax.Y, bmax.Z),
        };
    }
}

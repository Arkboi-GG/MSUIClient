using System.Numerics;
using MSUIClient.Formats;

namespace MSUIClient.World.Collision;

/// <summary>
/// Turns VMaNGOS's extracted mmaps into world-space navmesh triangles for the
/// creator X-ray. This is the surface bots actually path on: where it is
/// missing, bots detour, stall, or get server-walked into geometry the vmaps
/// disagree with.
///
/// The (row, col) filename order is the one verified against the live server,
/// but every loaded tile is still cross-checked: the tile header carries its
/// own world bounds, and a tile whose bounds do not straddle the ADT tile it
/// was named for is refused with the swapped-order file tried instead. A
/// naming convention that drifts between cores should present as a printed
/// warning, never as "the navmesh has a hole there".
/// </summary>
public sealed class MmapNavLoader
{
    private readonly string _root;

    public int TilesLoaded { get; private set; }
    public int TilesMissing { get; private set; }
    public int TrianglesAdded { get; private set; }

    public MmapNavLoader(string mmapDirectory) => _root = mmapDirectory;

    /// <summary>ADT tile (col,row) in the vmtile/terrain convention.</summary>
    public bool LoadTile(CollisionWorld world, int map, int col, int row)
    {
        // Expected world footprint of this ADT tile, from the same involution
        // the vmap tile index uses: col indexes internal Y, row internal X.
        double mid = VmapFormat.CoordShift;
        double gs = VmapFormat.GridSize;
        float yMin = (float)(mid - (col + 1) * gs), yMax = (float)(mid - col * gs);
        float xMin = (float)(mid - (row + 1) * gs), xMax = (float)(mid - row * gs);

        foreach (string candidate in new[]
                 {
                     $"{map:D3}{row:D2}{col:D2}.mmtile",   // verified order
                     $"{map:D3}{col:D2}{row:D2}.mmtile",   // drift fallback
                 })
        {
            string path = Path.Combine(_root, candidate);
            if (!File.Exists(path)) continue;

            MmapTileMesh mesh;
            try
            {
                mesh = MmtileReader.Read(path);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[navmesh] {candidate} FAILED to parse - {ex.Message}");
                continue;
            }

            // Overlap, not containment: the navmesh overhangs its tile edge by
            // the agent radius, so a loose straddle test is the honest one.
            const float slack = 30f;
            bool overlaps =
                mesh.BoundsMin.X < xMax + slack && mesh.BoundsMax.X > xMin - slack &&
                mesh.BoundsMin.Y < yMax + slack && mesh.BoundsMax.Y > yMin - slack;
            if (!overlaps)
            {
                Console.WriteLine(
                    $"[navmesh] {candidate} bounds X {mesh.BoundsMin.X:F0}..{mesh.BoundsMax.X:F0} " +
                    $"Y {mesh.BoundsMin.Y:F0}..{mesh.BoundsMax.Y:F0} do not overlap tile [{col},{row}] " +
                    $"(X {xMin:F0}..{xMax:F0} Y {yMin:F0}..{yMax:F0}) - wrong naming order, trying the other");
                continue;
            }

            int source = world.RegisterSource(candidate);
            var tris = mesh.Triangles;
            for (int i = 0; i + 8 < tris.Length; i += 9)
            {
                world.AddTriangle(
                    new Vector3(tris[i], tris[i + 1], tris[i + 2]),
                    new Vector3(tris[i + 3], tris[i + 4], tris[i + 5]),
                    new Vector3(tris[i + 6], tris[i + 7], tris[i + 8]),
                    source);
            }

            TilesLoaded++;
            TrianglesAdded += mesh.TriangleCount;
            Console.WriteLine($"[navmesh] tile [{col},{row}]: {candidate}, {mesh.TriangleCount:N0} triangles");
            return true;
        }

        TilesMissing++;
        Console.WriteLine($"[navmesh] tile [{col},{row}]: no usable mmtile (normal for ocean/unbaked tiles)");
        return false;
    }

    public string Summary()
        => $"{TilesLoaded} navmesh tile(s), {TilesMissing} missing, {TrianglesAdded:N0} triangles";
}

namespace MSUIClient.Formats;

/// <summary>
/// One ADT tile compiled for the Quest Tabletop's far layer (shared_docs/QUEST_TABLETOP.md §4): a coarse
/// 33×33 height grid (every 4th outer MCVT vertex, ~16.7 yd apart) and a per-chunk water level. Port of the
/// web Quest Atlas relief (MangosSuperUI Services/QuestAtlas/ReliefService.cs) - same samples, same water.
///
/// Grid law: sample (i, j) - i walks west → east (world Y falling), j walks north → south (world X falling);
/// <c>Heights[j * 33 + i]</c>. <c>Water[chunkRow * 16 + chunkCol]</c> (null = dry). The tile's north-west corner
/// is (NorthX, WestY) = ((32 - row) * 533.33, (32 - col) * 533.33). <c>MinSurface</c> = the lowest land-or-water surface
/// (each sample's terrain, raised to its chunk's water level where there is water): the slab sits under this, not under
/// the sea floor (owner, 2026-10-14).
/// </summary>
public sealed record ReliefTile(
    int Col, int Row,
    float NorthX, float WestY,
    float MinZ, float MaxZ,
    float MinSurface,
    float[] Heights,
    float?[] Water,
    int OriginsAgree, int OriginsChecked);

/// <summary>Compiles <see cref="ReliefTile"/>s from parsed ADTs. Pure decode: no GL, no game state.</summary>
public static class ReliefCompiler
{
    public const float TileYards = 533.33333f;
    public const int Samples = 33;
    public const float Step = TileYards / (Samples - 1);   // yards between relief samples
    private const float Cell = TileYards / 128f;            // outer-vertex spacing
    private const float ChunkYards = TileYards / 16f;

    /// <summary>
    /// Compile one tile. Chunks are placed by their indices (IndexX = column west → east, IndexY = row north →
    /// south), the law the terrain mesh is proven on; each chunk's MCNK origin (BaseX = 0x68 north, BaseY = 0x6C
    /// west) is checked against that placement and counted in <see cref="ReliefTile.OriginsAgree"/>, so a reader
    /// regression shows up as a count, not as tiles sliding off their squares.
    /// </summary>
    public static ReliefTile? Compile(AdtTerrainReader.AdtResult adt, int col, int row)
    {
        if (adt.Chunks is not { Length: > 0 } chunks) return null;
        float northX = (32 - row) * TileYards, westY = (32 - col) * TileYards;

        const int Full = 129;
        var full = new float[Full * Full];
        var have = new bool[Full * Full];
        var water = new float?[256];
        int agree = 0, checkedOrigins = 0;

        foreach (var ch in chunks)
        {
            if (ch.Heights is null) continue;
            int cx = ch.IndexX, cy = ch.IndexY;
            if ((uint)cx >= 16 || (uint)cy >= 16) continue;
            checkedOrigins++;
            if (MathF.Abs(ch.BaseX - (northX - cy * ChunkYards)) < 0.5f && MathF.Abs(ch.BaseY - (westY - cx * ChunkYards)) < 0.5f)
                agree++;

            for (int r = 0; r <= 8; r++)
            for (int c = 0; c <= 8; c++)
            {
                int fi = cx * 8 + c, fj = cy * 8 + r;
                full[fj * Full + fi] = ch.WorldHeightAt(c, r);
                have[fj * Full + fi] = true;
            }
            if (ch.Liquid is { Count: > 0 } liquid)
                water[cy * 16 + cx] = liquid[0].MaxHeight;
        }

        var heights = new float[Samples * Samples];
        float min = float.MaxValue, max = float.MinValue;
        for (int j = 0; j < Samples; j++)
        for (int i = 0; i < Samples; i++)
        {
            int k = j * 4 * Full + i * 4;
            float h = have[k] ? full[k] : float.NaN;
            heights[j * Samples + i] = h;
            if (!float.IsNaN(h)) { if (h < min) min = h; if (h > max) max = h; }
        }
        if (min == float.MaxValue) return null;
        // A chunk without MCVT becomes the tile minimum so the mesh stays closed (as the web relief does).
        for (int k = 0; k < heights.Length; k++)
            if (float.IsNaN(heights[k])) heights[k] = min;

        float minSurface = float.MaxValue;
        for (int j = 0; j < Samples; j++)
        for (int i = 0; i < Samples; i++)
        {
            float h = heights[j * Samples + i];
            if (water[Math.Min(j * 4 / 8, 15) * 16 + Math.Min(i * 4 / 8, 15)] is float w && w > h) h = w;
            minSurface = MathF.Min(minSurface, h);
        }

        return new ReliefTile(col, row, northX, westY, min, max, minSurface, heights, water, agree, checkedOrigins);
    }

    /// <summary>The chunk water level at a world position inside the tile, or null where it is dry.</summary>
    public static float? WaterIn(ReliefTile t, float x, float y)
    {
        int cc = Math.Clamp((int)((t.WestY - y) / (TileYards / 16f)), 0, 15);
        int cr = Math.Clamp((int)((t.NorthX - x) / (TileYards / 16f)), 0, 15);
        return t.Water[cr * 16 + cc];
    }

    /// <summary>Bilinear height inside one compiled tile (positions outside it clamp to its edge).</summary>
    public static float HeightIn(ReliefTile t, float x, float y)
    {
        float fi = Math.Clamp((t.WestY - y) / Step, 0f, Samples - 1);
        float fj = Math.Clamp((t.NorthX - x) / Step, 0f, Samples - 1);
        int i0 = Math.Min((int)fi, Samples - 2), j0 = Math.Min((int)fj, Samples - 2);
        float di = fi - i0, dj = fj - j0;
        var h = t.Heights;
        float a = h[j0 * Samples + i0], c = h[j0 * Samples + i0 + 1];
        float d = h[(j0 + 1) * Samples + i0], e = h[(j0 + 1) * Samples + i0 + 1];
        return (a * (1 - di) + c * di) * (1 - dj) + (d * (1 - di) + e * di) * dj;
    }
}

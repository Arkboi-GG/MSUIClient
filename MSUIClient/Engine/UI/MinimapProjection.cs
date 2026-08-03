using System.Numerics;
using MSUIClient.Formats;

namespace MSUIClient.Engine.UI;

/// <summary>
/// Vanilla continent/ADT/minimap coordinate law. A minimap tile covers one
/// 533.333-yard ADT; each ADT contains 16x16 MCNK area chunks.
/// </summary>
public readonly record struct MinimapProjection(
    int TileColumn, int TileRow, float TileU, float TileV, int ChunkX, int ChunkY)
{
    public const float TileWorldSize = 533.33333f;
    public const int ChunksPerTile = 16;

    public static MinimapProjection FromWorld(Vector3 position)
    {
        float tileX = 32f - position.Y / TileWorldSize;
        float tileY = 32f - position.X / TileWorldSize;
        int column = (int)MathF.Floor(tileX);
        int row = (int)MathF.Floor(tileY);
        float u = tileX - column;
        float v = tileY - row;
        int chunkX = Math.Clamp((int)(u * ChunksPerTile), 0, ChunksPerTile - 1);
        int chunkY = Math.Clamp((int)(v * ChunksPerTile), 0, ChunksPerTile - 1);
        return new(column, row, u, v, chunkX, chunkY);
    }

    public uint AreaId(AdtTerrainReader.AdtResult? adt)
    {
        if (adt?.Chunks is not { Length: > 0 } chunks) return 0;
        int index = ChunkY * ChunksPerTile + ChunkX;
        if ((uint)index < (uint)chunks.Length && chunks[index] is { } indexed &&
            indexed.IndexX == ChunkX && indexed.IndexY == ChunkY)
            return indexed.AreaId;
        int chunkX = ChunkX, chunkY = ChunkY;
        return chunks.FirstOrDefault(x => x is not null &&
            x.IndexX == chunkX && x.IndexY == chunkY)?.AreaId ?? 0;
    }
}

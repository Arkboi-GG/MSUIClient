using System.Numerics;

namespace MSUIClient.Engine.UI;

/// <summary>
/// Pure coordinate and asset-name law for vanilla WMO interior minimaps.
/// Blizzard bakes each group at 0.5 yard per texel. A tile edge is the next
/// power of two that covers the group on that axis, clamped to 32..256 px;
/// groups wider than a full 128-yard tile use multiple tiles.
/// </summary>
public static class WmoMinimapProjection
{
    public const float YardsPerTexel = 0.5f;
    public const float FullTileYards = 128f;

    public static readonly float[] ZoomRadiusYards = [150f, 120f, 90f, 60f, 40f, 25f];

    public static string? Stem(string instancePath)
    {
        if (string.IsNullOrWhiteSpace(instancePath)) return null;
        string normalized = instancePath.Replace('/', '\\').ToLowerInvariant();
        int world = normalized.IndexOf(@"world\", StringComparison.Ordinal);
        if (world < 0 || !normalized.EndsWith(".wmo", StringComparison.Ordinal)) return null;
        return normalized[(world + 6)..^4];
    }

    public static string LogicalTile(string stem, int groupIndex, int column, int row)
        => $@"{stem}_{groupIndex:000}_{column:00}_{row:00}.blp";

    public static (int Count, float SpanYards) AxisGrid(float extent)
    {
        if (!float.IsFinite(extent) || extent <= 0f) return (1, 16f);
        uint texels = (uint)MathF.Ceiling(MathF.Max(1f, extent / YardsPerTexel));
        uint pixels = NextPowerOfTwo(texels);
        pixels = Math.Clamp(pixels, 32u, 256u);
        int count = Math.Max(1, (int)MathF.Ceiling(extent / FullTileYards));
        return (count, pixels * YardsPerTexel);
    }

    /// <summary>North-up screen projection: world +X is up, world +Y is left.</summary>
    public static Vector2 ToScreen(Vector3 world, Vector3 playerWorld, Vector2 center, float pixelsPerYard)
        => center + new Vector2(
            -(world.Y - playerWorld.Y) * pixelsPerYard,
            -(world.X - playerWorld.X) * pixelsPerYard);

    private static uint NextPowerOfTwo(uint value)
    {
        if (value <= 1) return 1;
        value--;
        value |= value >> 1;
        value |= value >> 2;
        value |= value >> 4;
        value |= value >> 8;
        value |= value >> 16;
        return value + 1;
    }
}

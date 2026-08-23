using System.Numerics;

namespace MSUIClient.World.Wmo;

/// <summary>
/// CPU-side point sampling for one placed MLIQ grid. The grid remains an affine
/// lattice after a MODF placement, so world XY can be inverted directly to the
/// authored cell and its four heights can be bilinearly interpolated.
/// </summary>
public static class WmoLiquidPointLaw
{
    private const float GridEdgeTolerance = 1e-3f;

    public static bool TrySample(WmoLiquidSurface surface, float worldX, float worldY,
        out float height, out byte type)
    {
        height = 0f;
        type = 0;
        int columns = surface.XVerts;
        int rows = surface.YVerts;
        int cellsX = columns - 1;
        int cellsY = rows - 1;
        if (columns < 2 || rows < 2 || surface.Vertices.Length != columns * rows)
            return false;

        Vector3 origin = surface.Vertices[0];
        Vector3 farI = surface.Vertices[columns - 1];
        Vector3 farJ = surface.Vertices[(rows - 1) * columns];
        Vector2 u = new((farI.X - origin.X) / cellsX, (farI.Y - origin.Y) / cellsX);
        Vector2 v = new((farJ.X - origin.X) / cellsY, (farJ.Y - origin.Y) / cellsY);
        float determinant = u.X * v.Y - u.Y * v.X;
        if (!float.IsFinite(determinant) || MathF.Abs(determinant) <= 1e-9f)
            return false;

        float dx = worldX - origin.X;
        float dy = worldY - origin.Y;
        float a = (dx * v.Y - dy * v.X) / determinant;
        float b = (u.X * dy - u.Y * dx) / determinant;
        if (!TrySnap(a, cellsX, out int i, out float fx) ||
            !TrySnap(b, cellsY, out int j, out float fy) ||
            surface.IsHidden(i, j))
            return false;

        float z00 = surface.Vertices[j * columns + i].Z;
        float z10 = surface.Vertices[j * columns + i + 1].Z;
        float z01 = surface.Vertices[(j + 1) * columns + i].Z;
        float z11 = surface.Vertices[(j + 1) * columns + i + 1].Z;
        float top = z00 + (z10 - z00) * fx;
        float bottom = z01 + (z11 - z01) * fx;
        height = top + (bottom - top) * fy;
        type = surface.ShaderType(i, j);
        return float.IsFinite(height);
    }

    public static bool IsWater(byte shaderType) => shaderType is 1 or 4;

    /// <summary>MOGP groupLiquid low nibble to the renderer/query liquid code.</summary>
    public static bool TryMapGroupOverride(uint groupLiquid, out byte shaderType)
    {
        shaderType = (groupLiquid & 0x0fu) switch
        {
            0 or 4 or 8 => (byte)4,
            1 => (byte)1,
            2 or 6 => (byte)6,
            3 or 7 => (byte)3,
            _ => (byte)0,
        };
        return shaderType != 0;
    }

    private static bool TrySnap(float coordinate, int cells, out int index, out float fraction)
    {
        index = 0;
        fraction = 0f;
        if (!float.IsFinite(coordinate) || coordinate < -GridEdgeTolerance ||
            coordinate > cells + GridEdgeTolerance)
            return false;

        index = Math.Clamp((int)MathF.Floor(MathF.Max(coordinate, 0f)), 0, cells - 1);
        fraction = Math.Clamp(coordinate - index, 0f, 1f);
        return true;
    }
}

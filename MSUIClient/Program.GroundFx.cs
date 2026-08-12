using System.Numerics;

namespace MSUIClient;

public sealed partial class GameLoop
{
    /// <summary>One ground-decal seam for outdoor terrain and indoor collision floors.</summary>
    private void GatherGroundEffectTriangles(float minX, float minY, float minZ,
        float maxX, float maxY, float maxZ,
        List<(Vector3 A, Vector3 B, Vector3 C)> output)
    {
        _terrain?.GatherGroundTriangles(minX, minY, maxX, maxY, output);
        _collision?.GatherWalkableTriangles(minX, minY, minZ, maxX, maxY, maxZ, output);
    }
}

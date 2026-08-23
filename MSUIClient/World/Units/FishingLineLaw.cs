using System.Numerics;

namespace MSUIClient.World.Units;

/// <summary>
/// Current-Benilla fishing-line geometry and live eligibility law. The reference
/// draws one 64-segment line per visible fisher while the unit channels at a
/// streamed FISHINGNODE, with a fixed half-sine sag independent of span length.
/// </summary>
public static class FishingLineLaw
{
    public const uint FishingNodeType = 17;
    public const int Segments = 64;
    public const int VertexCount = Segments + 1;
    public const float Sag = .5f;

    public static bool Eligible(uint channelSpell, ulong? channelObject,
        bool targetIsGameObject, uint gameObjectType)
        => channelSpell != 0 && channelObject is > 0 && targetIsGameObject &&
           gameObjectType == FishingNodeType;

    public static Vector3[] Build(Vector3 near, Vector3 far)
    {
        var points = new Vector3[VertexCount];
        for (int i = 0; i <= Segments; i++)
        {
            float t = i / (float)Segments;
            points[i] = Vector3.Lerp(near, far, t);
            points[i].Z -= Sag * MathF.Sin(MathF.PI * t);
        }
        return points;
    }
}

public readonly record struct FishingPoleTipPlacement(ulong OwnerGuid, Vector3 WorldPosition);
public readonly record struct FishingLineSpan(Vector3 Near, Vector3 Far);

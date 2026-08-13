using System.Numerics;

namespace MSUIClient.World.Portals;

/// <summary>
/// Pure matching rules for consuming a prepared portal preview as the curtain
/// for an authoritative same-map teleport.  A recently used portal is only a
/// candidate: the server destination must still agree with its descriptor.
/// </summary>
public static class PortalHandoffLaw
{
    // SpellTargetPosition is the source of both the descriptor and the stock
    // teleport. Leave a little room for a core to adjust the body pose onto
    // nearby support, without letting an unrelated teleport consume the image.
    public const float DestinationTolerance = 8f;

    public static bool MatchesPreparedDestination(
        uint preparedMapId,
        in Vector3 preparedPosition,
        uint authoritativeMapId,
        in Vector3 authoritativePosition)
    {
        if (preparedMapId != authoritativeMapId ||
            !Finite(preparedPosition) || !Finite(authoritativePosition))
            return false;

        return Vector3.DistanceSquared(preparedPosition, authoritativePosition) <=
               DestinationTolerance * DestinationTolerance;
    }

    public static bool MatchesPreparedSameMap(
        int currentMapId,
        uint preparedMapId,
        in Vector3 preparedPosition,
        in Vector3 authoritativePosition)
    {
        return currentMapId >= 0 && MatchesPreparedDestination(
            preparedMapId, preparedPosition, (uint)currentMapId, authoritativePosition);
    }

    private static bool Finite(in Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}

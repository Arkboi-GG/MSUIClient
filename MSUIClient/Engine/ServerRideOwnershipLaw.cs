namespace MSUIClient.Engine;

/// <summary>
/// Decides whether a server-authored spline for the session character may drive the local
/// controller. The session character and the local controller are the same body only in the
/// ordinary own-character state. SUI pending, possession, and Free View route that controller
/// somewhere else while the session body continues to move in the entity stream.
/// </summary>
public static class ServerRideOwnershipLaw
{
    public static bool MayOwnController(bool freeView, bool ordinaryOwnCharacterState,
        bool controllingSessionCharacter) =>
        !freeView && ordinaryOwnCharacterState && controllingSessionCharacter;
}

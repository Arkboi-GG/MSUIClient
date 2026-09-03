namespace MSUIClient.Engine;

/// <summary>
/// Decides whether a server-authored spline for the session character may drive the local
/// controller. The session character and the local controller are the same body only in the
/// ordinary own-character state. SUI pending, possession, and Free View route that controller
/// somewhere else while the session body continues to move in the entity stream.
/// </summary>
public static class ServerRideOwnershipLaw
{
    /// <summary>
    /// The ordinary case: the own character, embodied, not in the Command View. Since
    /// 2026-09-03 a POSSESSED bot's ride also owns the controller (the human "follows the
    /// bot on taxi" and stays in control) — <paramref name="possessingEmbodiedBot"/> is the
    /// caller's Possessing state; the Command View still never rides.
    /// </summary>
    public static bool MayOwnController(bool freeView, bool ordinaryOwnCharacterState,
        bool controllingSessionCharacter, bool possessingEmbodiedBot = false) =>
        !freeView && (ordinaryOwnCharacterState && controllingSessionCharacter || possessingEmbodiedBot);
}

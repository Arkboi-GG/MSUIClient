namespace MSUIClient.Engine;

/// <summary>Classifies the two-dimensional SMSG_CLIENT_CONTROL_UPDATE statement.</summary>
public static class ClientControlUpdateLaw
{
    public enum Verdict : byte
    {
        SelfRevoked,
        SelfRestored,
        ForeignGranted,
        ForeignReleased,
    }

    public static Verdict Classify(ulong mover, bool allowMove, ulong selfGuid) =>
        (mover == selfGuid, allowMove) switch
        {
            (true, false) => Verdict.SelfRevoked,
            (true, true) => Verdict.SelfRestored,
            (false, true) => Verdict.ForeignGranted,
            _ => Verdict.ForeignReleased,
        };

    /// <summary>
    /// Stock control statements own movement only while the client is ordinarily embodied as
    /// its session character. SUI possession and Free View deliberately use the same server
    /// SetClientControl machinery while routing input to a bot or detached camera instead.
    /// </summary>
    public static bool SuiOwnsRouting(bool freeView, bool ordinaryOwnCharacterState) =>
        freeView || !ordinaryOwnCharacterState;

    public static bool LocksCurrentMover(bool selfControlLost, bool freeView,
        bool ordinaryOwnCharacterState, bool controllingSessionCharacter) =>
        selfControlLost && controllingSessionCharacter &&
        !SuiOwnsRouting(freeView, ordinaryOwnCharacterState);
    public static bool LocksAddressedBody(ulong lostGuid, ulong controlledGuid, bool freeView, bool possessing) =>
        lostGuid != 0 && lostGuid == controlledGuid && possessing && !freeView;

}

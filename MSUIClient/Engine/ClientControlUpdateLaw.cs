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
}

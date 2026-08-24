namespace MSUIClient.Engine.UI;

/// <summary>Build-5875 client-local loot-target handshake and kneel predicate.</summary>
public static class LootLatchLaw
{
    public enum TargetKind { Unresolved, Unit, GameObject, Item, Other }

    public readonly record struct ResponsePlan(bool Accept, bool SendRelease,
        ulong NextLatch);

    public static ResponsePlan AdmitResponse(ulong latch, ulong guid, byte lootType)
    {
        bool accepted = latch != 0
            ? latch == guid
            : lootType is 2 or 3 or 4;
        return accepted
            ? new(true, false, guid)
            : new(false, lootType != 0, 0);
    }

    // The active player's reference predicate reads the client-local loot target itself. Target
    // classification is relevant to remote descriptor-driven looting, not to this predicted latch.
    public static bool ShouldKneel(ulong latch, TargetKind kind, uint gameObjectType,
        uint unitHealth) => latch != 0;

    public static ulong ClearFor(ulong latch, ulong guid) => latch == guid ? 0 : latch;
}

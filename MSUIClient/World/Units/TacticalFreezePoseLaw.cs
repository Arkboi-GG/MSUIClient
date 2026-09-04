namespace MSUIClient.World.Units;

/// <summary>
/// Lower-layer aggregate of authoritative Tactical Freeze memberships. GameLoop supplies complete
/// per-lock snapshots; movement and render systems only ask whether a GUID is frozen. Keeping this
/// seam below GameLoop prevents the entity/renderer layers from depending on UI or session state.
///
/// Locks deliberately overlap. Releasing one removes only that lock's membership, and a unit stays
/// frozen until the last lock containing it is gone. Published sets are replace-only, so hot render
/// reads never observe a HashSet while it is being mutated.
/// </summary>
public static class TacticalFreezePoseLaw
{
    private static readonly object Gate = new();
    private static readonly Dictionary<ulong, HashSet<ulong>> MembersByLock = [];
    private static volatile HashSet<ulong> _frozen = [];
    private static uint _generation;

    public static uint Generation => _generation;
    public static bool IsFrozen(ulong guid) => guid != 0 && _frozen.Contains(guid);
    public static IReadOnlyCollection<ulong> FrozenGuids => _frozen;

    /// <summary>Replace one active lock snapshot, or remove precisely that lock on release.</summary>
    public static void ApplyLockSnapshot(ulong lockId, bool active,
        IEnumerable<ulong> frozenMembers)
    {
        if (lockId == 0) throw new ArgumentOutOfRangeException(nameof(lockId));
        ArgumentNullException.ThrowIfNull(frozenMembers);
        lock (Gate)
        {
            if (active)
            {
                var members = new HashSet<ulong>(frozenMembers.Where(guid => guid != 0));
                MembersByLock[lockId] = members;
            }
            else
            {
                MembersByLock.Remove(lockId);
            }
            PublishAggregate();
        }
    }

    /// <summary>Session/world teardown. An ordinary lock release must use ApplyLockSnapshot.</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            MembersByLock.Clear();
            PublishAggregate();
        }
    }

    private static void PublishAggregate()
    {
        var aggregate = new HashSet<ulong>();
        foreach (HashSet<ulong> members in MembersByLock.Values)
            aggregate.UnionWith(members);
        _frozen = aggregate;
        unchecked { _generation++; }
    }
}

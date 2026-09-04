using System.Numerics;

namespace MSUIClient.Net;

/// <summary>One authoritative member row in an SMSG_SUI_TACTICAL_FREEZE snapshot.</summary>
public readonly record struct TacticalFreezeMember(ulong Guid, byte Flags)
{
    public bool Frozen => (Flags & TacticalFreezeWire.MemberFrozen) != 0;
    public bool CommandableByRecipient =>
        (Flags & TacticalFreezeWire.MemberCommandableByRecipient) != 0;
    public bool RealHuman => (Flags & TacticalFreezeWire.MemberRealHuman) != 0;
    public bool AnchorBody => (Flags & TacticalFreezeWire.MemberAnchorBody) != 0;
}

/// <summary>One server-authoritative radius-lock snapshot. Each packet describes one lock.</summary>
public readonly record struct TacticalFreezeSnapshot(
    uint RequestId, byte Result, bool Active, uint Revision, ulong LockId, ulong OwnerGuid,
    Vector3 Center, float Radius, TacticalFreezeMember[] Members);

/// <summary>A fixed-width actor/action record sent in CMSG_SUI_TACTICAL_QUEUE.</summary>
public readonly record struct TacticalQueueRequestRecord(
    ulong ActorGuid, uint ActionId, byte Kind, ulong TargetGuid, Vector3 Position, uint SpellId);

/// <summary>One authoritative queued action returned by the server.</summary>
public readonly record struct TacticalQueuedAction(
    uint ActionId, byte Kind, ulong TargetGuid, Vector3 Position, uint SpellId);

/// <summary>One actor and its complete current queue.</summary>
public readonly record struct TacticalActorQueue(ulong ActorGuid, TacticalQueuedAction[] Actions);

/// <summary>
/// Authoritative queue snapshot/result. ResultActorGuid and ResultActionId attribute unsolicited
/// started/completed/skipped events without making the client infer which queue changed.
/// </summary>
public readonly record struct TacticalQueueSnapshot(
    ulong LockId, uint Revision, uint RequestId, byte Result,
    ulong ResultActorGuid, uint ResultActionId, TacticalActorQueue[] Actors);

/// <summary>
/// Tactical radius-lock/command-queue v1 wire law. Every body starts with version 1 and is parsed
/// to an exact length. The feature is never spoken until capability bit 12 has been advertised.
///
/// CMSG freeze (870): v:u8, request:u32, desiredActive:u8, lock:u64 — exactly 14 bytes.
/// SMSG freeze (871): v:u8, request:u32, result:u8, active:u8, revision:u32, lock:u64,
/// owner:u64, center:3*f32, radius:f32, count:u16, count*{guid:u64,flags:u8} — 45+9N.
/// CMSG queue (872): v:u8, lock:u64, request:u32, operation:u8, count:u8,
/// count*{actor:u64,action:u32,kind:u8,target:u64,position:3*f32,spell:u32} — 15+37N.
/// SMSG queue (873): v:u8, lock:u64, revision:u32, request:u32, result:u8,
/// resultActor:u64,resultAction:u32,actorCount:u8, then actor blocks. An actor block is
/// actor:u64,queueCount:u8 followed by queueCount fixed 29-byte action records.
/// </summary>
public static class TacticalFreezeWire
{
    public const byte Version = 1;

    public const int FreezeRequestBytes = 14;
    public const int FreezeSnapshotHeaderBytes = 45;
    public const int FreezeMemberBytes = 9;
    public const int QueueRequestHeaderBytes = 15;
    public const int QueueRequestRecordBytes = 37;
    public const int QueueSnapshotHeaderBytes = 31;
    public const int QueueActorHeaderBytes = 9;
    public const int QueueActionBytes = 29;

    // Membership follows the protocol's natural u16 ceiling. There is intentionally no smaller
    // gameplay cap: the server owns the radius set, and exact body length still bounds allocation.
    public const int MaxMembers = ushort.MaxValue;
    public const int MaxRequestRecords = 40;
    public const int MaxActors = 40;
    public const int MaxActionsPerActor = 5;

    public const byte MemberFrozen = 1 << 0;
    public const byte MemberCommandableByRecipient = 1 << 1;
    public const byte MemberRealHuman = 1 << 2;
    public const byte MemberAnchorBody = 1 << 3;
    public const byte KnownMemberFlags = MemberFrozen | MemberCommandableByRecipient |
        MemberRealHuman | MemberAnchorBody;

    public const byte QueueEnqueue = 0;
    public const byte QueueCancel = 1;
    public const byte QueueClear = 2;

    public const byte ActionMove = 1;
    public const byte ActionAttack = 2;
    public const byte ActionCast = 3;

    // Freeze verdict/release reasons.
    public const byte FreezeOk = 0;
    public const byte FreezeDeniedSession = 1;
    public const byte FreezeDeniedCommandView = 2;
    public const byte FreezeDeniedState = 3;
    public const byte FreezeAlreadyActive = 4;
    public const byte FreezeFrozenByOther = 5;
    public const byte FreezeNotOwner = 6;
    public const byte FreezeNotFound = 7;
    public const byte FreezeBadPacket = 8;
    public const byte FreezeReleasedView = 9;
    public const byte FreezeReleasedLogout = 10;
    public const byte FreezeReleasedMapChange = 11;
    public const byte FreezeReleasedDeath = 12;

    // Queue request verdicts and unsolicited execution notifications.
    public const byte QueueOk = 0;
    public const byte QueueBadPacket = 1;
    public const byte QueueLockNotFound = 2;
    public const byte QueueNotOwner = 3;
    public const byte QueueLockNotActive = 4;
    public const byte QueueActorNotMember = 5;
    public const byte QueueActorNotCommandable = 6;
    public const byte QueueActorUnavailable = 7;
    public const byte QueueFull = 8;
    public const byte QueueActionInvalid = 9;
    public const byte QueueActionNotFound = 10;
    public const byte QueueActionStarted = 11;
    public const byte QueueActionCompleted = 12;
    public const byte QueueActionSkippedInvalid = 13;
    public const byte QueueDrained = 14;

    public static byte[] BuildFreezeRequest(uint requestId, bool desiredActive, ulong lockId)
    {
        if (requestId == 0)
            throw new ArgumentOutOfRangeException(nameof(requestId), "a client freeze request needs an id");
        if (desiredActive && lockId != 0)
            throw new ArgumentOutOfRangeException(nameof(lockId), "activation does not name a prior lock");
        if (!desiredActive && lockId == 0)
            throw new ArgumentOutOfRangeException(nameof(lockId), "release must name the authoritative lock");

        var w = new PacketWriter(FreezeRequestBytes);
        w.WriteU8(Version);
        w.WriteU32(requestId);
        w.WriteU8(desiredActive ? (byte)1 : (byte)0);
        w.WriteU64(lockId);
        return w.ToArray();
    }

    public static bool TryParseFreezeSnapshot(byte[] body, out TacticalFreezeSnapshot snapshot)
    {
        snapshot = default;
        if (body.Length < FreezeSnapshotHeaderBytes || body[0] != Version) return false;
        try
        {
            var r = new PacketReader(body);
            r.Skip(1);
            uint requestId = r.ReadU32();
            byte result = r.ReadU8();
            if (result > FreezeReleasedDeath) return false;
            byte activeByte = r.ReadU8();
            if (activeByte > 1) return false;
            bool active = activeByte != 0;
            uint revision = r.ReadU32();
            ulong lockId = r.ReadU64();
            ulong ownerGuid = r.ReadU64();
            Vector3 center = r.ReadVector3();
            float radius = r.ReadF32();
            int count = r.ReadU16();
            if (count > MaxMembers || body.Length != FreezeSnapshotHeaderBytes +
                    FreezeMemberBytes * count)
                return false;
            if (!Finite(center) || !float.IsFinite(radius) || radius < 0f ||
                (!active && count != 0))
                return false;

            bool activeResult = result is FreezeOk or FreezeAlreadyActive or
                FreezeFrozenByOther or FreezeNotOwner;
            bool inactiveLockResult = result is FreezeOk or FreezeReleasedView or
                FreezeReleasedLogout or FreezeReleasedMapChange or FreezeReleasedDeath;
            if ((active && (!activeResult || radius != 100f)) ||
                (!active && (center != Vector3.Zero || radius != 0f)))
                return false;

            bool acquireDenialResult = result is FreezeDeniedSession or
                FreezeDeniedCommandView or FreezeDeniedState or FreezeBadPacket;
            bool noLockDenial = !active && count == 0 && lockId == 0 && ownerGuid == 0 &&
                revision == 0 && acquireDenialResult;
            // NOT_FOUND is a stateless retirement notice. It echoes the nonzero lock requested by
            // the client so a stale local entry can be removed even though there is no revision or
            // owner left on the server.
            bool notFound = !active && count == 0 && lockId != 0 && ownerGuid == 0 &&
                revision == 0 && result == FreezeNotFound;
            bool identifiedLock = lockId != 0 && ownerGuid != 0 && revision != 0 &&
                !acquireDenialResult && result != FreezeNotFound;
            if (!noLockDenial && !notFound && !identifiedLock) return false;
            if (active && (!identifiedLock || !activeResult)) return false;
            if (!active && identifiedLock && !inactiveLockResult) return false;

            var members = new TacticalFreezeMember[count];
            var unique = new HashSet<ulong>();
            int anchorBodies = 0;
            for (int i = 0; i < count; i++)
            {
                ulong guid = r.ReadU64();
                byte flags = r.ReadU8();
                if (guid == 0 || !unique.Add(guid) || (flags & ~KnownMemberFlags) != 0 ||
                    (flags & MemberFrozen) == 0)
                    return false;
                if ((flags & MemberAnchorBody) != 0)
                    anchorBodies++;
                members[i] = new TacticalFreezeMember(guid, flags);
            }
            if (active && (count == 0 || anchorBodies != 1 || radius <= 0f)) return false;
            snapshot = new TacticalFreezeSnapshot(requestId, result, active, revision,
                lockId, ownerGuid, center, radius, members);
            return true;
        }
        catch (EndOfStreamException)
        {
            return false;
        }
    }

    public static byte[] BuildQueueRequest(ulong lockId, uint requestId, byte operation,
        IReadOnlyList<TacticalQueueRequestRecord> records)
    {
        if (lockId == 0) throw new ArgumentOutOfRangeException(nameof(lockId));
        if (requestId == 0) throw new ArgumentOutOfRangeException(nameof(requestId));
        if (operation is not (QueueEnqueue or QueueCancel or QueueClear))
            throw new ArgumentOutOfRangeException(nameof(operation));
        if (records.Count is < 1 or > MaxRequestRecords)
            throw new ArgumentOutOfRangeException(nameof(records));

        var actors = new HashSet<ulong>();
        foreach (TacticalQueueRequestRecord record in records)
        {
            if (record.ActorGuid == 0 || !actors.Add(record.ActorGuid))
                throw new ArgumentOutOfRangeException(nameof(records),
                    "each queue request names every actor exactly once");
            if (!Finite(record.Position))
                throw new ArgumentOutOfRangeException(nameof(records), "positions must be finite");
            if (operation == QueueEnqueue)
            {
                if (record.ActionId != 0 || !KnownActionKind(record.Kind))
                    throw new ArgumentOutOfRangeException(nameof(records),
                        "enqueue carries action id 0 and a known action kind");
                if (record.Kind == ActionMove && (record.TargetGuid != 0 || record.SpellId != 0))
                    throw new ArgumentOutOfRangeException(nameof(records),
                        "move uses only its position");
                if (record.Kind == ActionAttack && (record.TargetGuid == 0 ||
                    record.Position != Vector3.Zero || record.SpellId != 0))
                    throw new ArgumentOutOfRangeException(nameof(records), "attack needs a target");
                if (record.Kind == ActionCast && (record.SpellId == 0 ||
                    (record.TargetGuid != 0 && record.Position != Vector3.Zero)))
                    throw new ArgumentOutOfRangeException(nameof(records),
                        "cast needs a spell and either a unit target or a ground position");
            }
            else if (operation == QueueCancel)
            {
                if (record.ActionId == 0 || record.Kind != 0 || record.TargetGuid != 0 ||
                    record.Position != Vector3.Zero || record.SpellId != 0)
                    throw new ArgumentOutOfRangeException(nameof(records),
                        "cancel carries only actor and authoritative action id");
            }
            else if (record.ActionId != 0 || record.Kind != 0 || record.TargetGuid != 0 ||
                     record.Position != Vector3.Zero || record.SpellId != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(records),
                    "clear carries only the actor");
            }
        }

        var w = new PacketWriter(QueueRequestHeaderBytes + QueueRequestRecordBytes * records.Count);
        w.WriteU8(Version);
        w.WriteU64(lockId);
        w.WriteU32(requestId);
        w.WriteU8(operation);
        w.WriteU8((byte)records.Count);
        foreach (TacticalQueueRequestRecord record in records)
        {
            w.WriteU64(record.ActorGuid);
            w.WriteU32(record.ActionId);
            w.WriteU8(record.Kind);
            w.WriteU64(record.TargetGuid);
            w.WriteVector3(record.Position);
            w.WriteU32(record.SpellId);
        }
        return w.ToArray();
    }

    public static bool TryParseQueueSnapshot(byte[] body, out TacticalQueueSnapshot snapshot)
    {
        snapshot = default;
        if (body.Length < QueueSnapshotHeaderBytes || body[0] != Version) return false;
        try
        {
            var r = new PacketReader(body);
            r.Skip(1);
            ulong lockId = r.ReadU64();
            uint revision = r.ReadU32();
            uint requestId = r.ReadU32();
            byte result = r.ReadU8();
            if (result > QueueDrained) return false;
            ulong resultActor = r.ReadU64();
            uint resultAction = r.ReadU32();
            int actorCount = r.ReadU8();
            if (actorCount > MaxActors ||
                r.Remaining < actorCount * QueueActorHeaderBytes)
                return false;

            bool statelessResult = result is QueueBadPacket or QueueLockNotFound;
            bool statelessFailure = revision == 0 && actorCount == 0 &&
                resultActor == 0 && resultAction == 0 &&
                statelessResult;
            bool stateful = revision != 0 && lockId != 0 && !statelessResult;
            if (!statelessFailure && !stateful) return false;

            var actors = new TacticalActorQueue[actorCount];
            var uniqueActors = new HashSet<ulong>();
            for (int actorIndex = 0; actorIndex < actorCount; actorIndex++)
            {
                if (r.Remaining < QueueActorHeaderBytes) return false;
                ulong actorGuid = r.ReadU64();
                int queueCount = r.ReadU8();
                if (actorGuid == 0 || !uniqueActors.Add(actorGuid) ||
                    queueCount > MaxActionsPerActor || r.Remaining < queueCount * QueueActionBytes)
                    return false;
                var actions = new TacticalQueuedAction[queueCount];
                var uniqueActions = new HashSet<uint>();
                for (int actionIndex = 0; actionIndex < queueCount; actionIndex++)
                {
                    uint actionId = r.ReadU32();
                    byte kind = r.ReadU8();
                    ulong targetGuid = r.ReadU64();
                    Vector3 position = r.ReadVector3();
                    uint spellId = r.ReadU32();
                    if (actionId == 0 || !uniqueActions.Add(actionId) || !KnownActionKind(kind) ||
                        !Finite(position) ||
                        (kind == ActionMove && (targetGuid != 0 || spellId != 0)) ||
                        (kind == ActionAttack && (targetGuid == 0 || position != Vector3.Zero ||
                            spellId != 0)) ||
                        (kind == ActionCast && (spellId == 0 ||
                            (targetGuid != 0 && position != Vector3.Zero))))
                        return false;
                    actions[actionIndex] = new TacticalQueuedAction(
                        actionId, kind, targetGuid, position, spellId);
                }
                actors[actorIndex] = new TacticalActorQueue(actorGuid, actions);
            }
            if (r.HasMore) return false;
            snapshot = new TacticalQueueSnapshot(lockId, revision, requestId, result,
                resultActor, resultAction, actors);
            return true;
        }
        catch (EndOfStreamException)
        {
            return false;
        }
    }

    public static bool KnownActionKind(byte kind) =>
        kind is ActionMove or ActionAttack or ActionCast;

    public static string FreezeResultText(byte result) => result switch
    {
        FreezeOk => "Freeze updated.",
        FreezeDeniedSession => "Tactical Freeze is unavailable for this session.",
        FreezeDeniedCommandView => "Tactical Freeze can only be changed from Command View.",
        FreezeDeniedState => "Your character cannot start Tactical Freeze right now.",
        FreezeAlreadyActive => "You already own a Tactical Freeze.",
        FreezeFrozenByOther => "Another player owns this Tactical Freeze.",
        FreezeNotOwner => "Only the player who started this Tactical Freeze can resume it.",
        FreezeNotFound => "That Tactical Freeze no longer exists.",
        FreezeBadPacket => "The Tactical Freeze request was rejected.",
        FreezeReleasedView => "Tactical Freeze ended when its owner left Command View.",
        FreezeReleasedLogout => "Tactical Freeze ended when its owner left the world.",
        FreezeReleasedMapChange => "Tactical Freeze ended at the map boundary.",
        FreezeReleasedDeath => "Tactical Freeze ended when its owner died.",
        _ => $"Tactical Freeze result {result}.",
    };

    public static string QueueResultText(byte result) => result switch
    {
        QueueOk => "Tactical order queued.",
        QueueBadPacket => "The tactical order was malformed.",
        QueueLockNotFound => "That Tactical Freeze no longer exists.",
        QueueNotOwner => "Only the freeze owner may author its queue.",
        QueueLockNotActive => "That Tactical Freeze is no longer accepting orders.",
        QueueActorNotMember => "That unit is outside the Tactical Freeze.",
        QueueActorNotCommandable => "That unit is frozen, but not yours to command.",
        QueueActorUnavailable => "That unit cannot accept a tactical order.",
        QueueFull => $"That unit already has {MaxActionsPerActor} queued orders.",
        QueueActionInvalid => "That tactical order is not valid.",
        QueueActionNotFound => "That queued order no longer exists.",
        QueueActionStarted => "Tactical order started.",
        QueueActionCompleted => "Tactical order completed.",
        QueueActionSkippedInvalid => "Tactical order skipped because its target became invalid.",
        QueueDrained => "Tactical queue complete.",
        _ => $"Tactical queue result {result}.",
    };

    private static bool Finite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}

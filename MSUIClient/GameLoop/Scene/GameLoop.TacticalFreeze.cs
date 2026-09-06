using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine;
using MSUIClient.Engine.UI;
using MSUIClient.Formats;
using MSUIClient.Net;
using MSUIClient.World.Units;
using Silk.NET.Input;

namespace MSUIClient;

/// <summary>
/// Client half of Tactical Freeze. The server owns locks, membership and every queue mutation;
/// request sends only create pending affordance state. Visual and input locks consume the latest
/// authoritative per-lock snapshots through TacticalFreezePoseLaw, including overlap refcounts.
/// </summary>
public sealed partial class GameLoop
{
    private sealed class TacticalLockView
    {
        public bool Active;
        public uint Revision;
        public ulong LockId;
        public ulong OwnerGuid;
        public Vector3 Center;
        public float Radius;
        public Dictionary<ulong, TacticalFreezeMember> Members = [];
    }

    private bool _tacticalFreezeAvailable;
    private uint _tacticalRequestSequence;
    private uint _tacticalFreezePendingRequest;
    private bool _tacticalFreezePendingDesiredActive;
    private ulong _tacticalFreezePendingLockId;
    private bool _tacticalCommandViewExitAuthorized;
    private ulong _tacticalCommandViewExitLockId;
    private readonly HashSet<uint> _tacticalQueuePendingRequests = [];
    private readonly Dictionary<ulong, TacticalLockView> _tacticalLocks = [];
    private readonly Dictionary<ulong, TacticalQueueSnapshot> _tacticalQueues = [];

    // Ground-targeted spells selected from the Command View need their ordinary rune click, but
    // commit into the tactical queue rather than CMSG_CAST_SPELL.
    private ulong _tacticalGroundLockId;
    private ulong _tacticalGroundActor;
    private uint _tacticalGroundSpellId;

    private TacticalLockView? OwnedActiveTacticalLock =>
        _tacticalLocks.Values.FirstOrDefault(view =>
            view.Active && view.OwnerGuid == LocalPlayerGuid);

    private TacticalLockView? LocalActiveTacticalLock =>
        _tacticalLocks.Values.FirstOrDefault(view => view.Active &&
            (view.OwnerGuid == LocalPlayerGuid || view.Members.ContainsKey(LocalPlayerGuid) ||
             view.Members.ContainsKey(ControlledGuid)));

    /// <summary>
    /// Resume releases the pose lock, but Core may still be executing its lock-keyed FIFO. Keep
    /// ordinary live authorship closed until the authoritative queue reaches DRAINED so a live
    /// order, handoff, or party mutation cannot race the plan that is already in flight.
    /// </summary>
    private TacticalLockView? OwnedDrainingTacticalLock =>
        _tacticalLocks.Values.FirstOrDefault(view =>
            !view.Active && view.OwnerGuid == LocalPlayerGuid &&
            _tacticalQueues.ContainsKey(view.LockId));

    private bool ControlledBodyTacticallyFrozen =>
        TacticalFreezePoseLaw.IsFrozen(ControlledGuid);

    /// <summary>
    /// A lock freezes live-world authorship, not the Command View UI. Include owned locks even if
    /// a malformed/late membership view has not yet named the driven body, and include local or
    /// controlled membership for a real human caught by somebody else's radius.
    /// </summary>
    private bool TacticalFreezeBlocksLiveCommands =>
        OwnedActiveTacticalLock is not null || OwnedDrainingTacticalLock is not null ||
        LocalActiveTacticalLock is not null || ControlledBodyTacticallyFrozen;

    private bool RefuseTacticalFreezeLiveCommand(string action)
    {
        if (!TacticalFreezeBlocksLiveCommands) return false;
        TacticalLockView? owned = OwnedActiveTacticalLock;
        TacticalLockView? draining = OwnedDrainingTacticalLock;
        TacticalLockView? local = LocalActiveTacticalLock;
        ShowUiError(draining is not null
            ? $"Wait for your tactical plan to finish before {action}."
            : owned is not null
                ? $"Resume your Tactical Freeze before {action}."
                : local is not null
                    ? $"Frozen by {ResolveTacticalOwner(local.OwnerGuid)} — {action} is read-only."
                    : $"This body is frozen — {action} is read-only.");
        return true;
    }

    /// <summary>
    /// Map-wide snapshots are delivered to observers outside the radius too. Their own session
    /// can remain live while a selected party/faction bot is a member of somebody else's lock,
    /// so actor-addressed sends need this target-local half of the authorship law as well.
    /// </summary>
    private bool IsTacticalActorFrozen(ulong guid) =>
        guid != 0 && TacticalFreezePoseLaw.IsFrozen(guid);

    private bool RefuseTacticalFrozenActor(ulong guid, string action)
    {
        if (!IsTacticalActorFrozen(guid)) return false;
        TacticalLockView? heldBy = _tacticalLocks.Values.FirstOrDefault(view =>
            view.Active && view.Members.TryGetValue(guid, out TacticalFreezeMember member) &&
            member.Frozen);
        string owner = heldBy is null ? "another player" : ResolveTacticalOwner(heldBy.OwnerGuid);
        ShowUiError($"{ResolveUnitName(guid)} is frozen by {owner} — cannot {action}.");
        return true;
    }

    private bool RefuseTacticalFrozenActors(IEnumerable<ulong> actors, string action)
    {
        ulong frozen = actors.FirstOrDefault(IsTacticalActorFrozen);
        return frozen != 0 && RefuseTacticalFrozenActor(frozen, action);
    }

    /// <summary>Vanilla social packets often carry only a name. Resolve a streamed/cached
    /// player when possible so the client can still enforce the target-local freeze boundary;
    /// an unresolved name remains server-authoritative.</summary>
    private ulong KnownPlayerGuid(string name) => string.IsNullOrWhiteSpace(name)
        ? 0
        : _playerNames.FirstOrDefault(pair =>
            pair.Value.Equals(name, StringComparison.OrdinalIgnoreCase)).Key;

    /// <summary>An empty legacy SUI_ORDER subject list means the whole real party.</summary>
    private IEnumerable<ulong> TacticalOrderActors(IReadOnlyList<ulong> subjects) =>
        subjects.Count == 0 ? CommandViewPartyGuids() : subjects;

    private bool TacticalOrderActorsFrozen(IReadOnlyList<ulong> subjects) =>
        TacticalOrderActors(subjects).Any(IsTacticalActorFrozen);

    /// <summary>Only seam for legacy SUI_ORDER sends from GameLoop UI.</summary>
    private bool TrySendLiveSuiOrder(byte orderType, IReadOnlyList<ulong> subjects,
        ulong targetGuid, float x, float y, float z, bool reportRefusal = true)
    {
        if (TacticalFreezeBlocksLiveCommands)
        {
            if (reportRefusal) RefuseTacticalFreezeLiveCommand("issuing live orders");
            return false;
        }
        ulong frozenActor = TacticalOrderActors(subjects).FirstOrDefault(IsTacticalActorFrozen);
        if (frozenActor != 0)
        {
            if (reportRefusal) RefuseTacticalFrozenActor(frozenActor, "issue live orders");
            return false;
        }
        if (targetGuid != 0 && IsTacticalActorFrozen(targetGuid))
        {
            if (reportRefusal) RefuseTacticalFrozenActor(targetGuid, "target it with a live order");
            return false;
        }
        return _net?.SuiOrder(orderType, subjects, targetGuid, x, y, z) == true;
    }

    private uint NextTacticalRequestId()
    {
        unchecked { _tacticalRequestSequence++; }
        if (_tacticalRequestSequence == 0) _tacticalRequestSequence = 1;
        return _tacticalRequestSequence;
    }

    private void ApplyTacticalFreezeCapability(uint capabilities)
    {
        bool available = (capabilities & SuiCapabilityWire.TacticalFreezeV1) != 0;
        if (_tacticalFreezeAvailable == available) return;
        _tacticalFreezeAvailable = available;
        if (!available) ResetTacticalFreezeState();
    }

    private void ResetTacticalFreezeState()
    {
        _tacticalFreezeAvailable = false;
        ResetTacticalFreezeWorldState();
    }

    /// <summary>
    /// Retire map-scoped locks without discarding the session-negotiated capability. Core may
    /// send the destination map's unsolicited active-lock snapshot immediately after NEW_WORLD;
    /// clearing bit 12 here would make the client drop that snapshot before any later roster ACK.
    /// </summary>
    private void ResetTacticalFreezeWorldState()
    {
        _tacticalFreezePendingRequest = 0;
        _tacticalFreezePendingDesiredActive = false;
        _tacticalFreezePendingLockId = 0;
        _tacticalCommandViewExitAuthorized = false;
        _tacticalCommandViewExitLockId = 0;
        _tacticalQueuePendingRequests.Clear();
        _tacticalLocks.Clear();
        _tacticalQueues.Clear();
        CancelTacticalGroundCast(silent: true);
        if (_rtsUnitCastTacticalLockId != 0)
            CancelRtsUnitCastTargeting(silent: true);
        TacticalFreezePoseLaw.Clear();
    }

    /// <summary>Freeze/Resume card act. Only an authoritative owned lock enables Resume.</summary>
    private void RequestTacticalFreezeToggle()
    {
        if (!_freeView || _net is not { IsInWorld: true } || !_tacticalFreezeAvailable)
        {
            ShowUiError("This server does not offer Tactical Freeze.");
            return;
        }
        if (_tacticalFreezePendingRequest != 0) return;

        TacticalLockView? owned = OwnedActiveTacticalLock;
        if (owned is null && (TacticalFreezePoseLaw.IsFrozen(LocalPlayerGuid) ||
            ControlledBodyTacticallyFrozen))
        {
            TacticalLockView? other = LocalActiveTacticalLock;
            string owner = other is null ? "another player" : ResolveTacticalOwner(other.OwnerGuid);
            ShowUiError($"Only {owner} can resume this Tactical Freeze.");
            return;
        }

        uint request = NextTacticalRequestId();
        bool desiredActive = owned is null;
        ulong lockId = owned?.LockId ?? 0;
        if (_net.SuiTacticalFreeze(request, desiredActive, lockId))
        {
            _tacticalFreezePendingRequest = request;
            _tacticalFreezePendingDesiredActive = desiredActive;
            _tacticalFreezePendingLockId = lockId;
            SetRtsControlGroupStatus(desiredActive
                ? "Requesting Tactical Freeze…" : "Requesting Resume…");
        }
    }

    /// <summary>Called before any live Command View exit request; state remains until the SMSG.</summary>
    private bool RequestOwnedTacticalThawForViewExit()
    {
        TacticalLockView? owned = OwnedActiveTacticalLock;
        if (owned is null || !_tacticalFreezeAvailable || _net is not { IsInWorld: true })
            return false;
        if (_tacticalFreezePendingRequest != 0)
            return !_tacticalFreezePendingDesiredActive &&
                _tacticalFreezePendingLockId == owned.LockId;
        uint request = NextTacticalRequestId();
        if (_net.SuiTacticalFreeze(request, desiredActive: false, owned.LockId))
        {
            _tacticalFreezePendingRequest = request;
            _tacticalFreezePendingDesiredActive = false;
            _tacticalFreezePendingLockId = owned.LockId;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Authorize the one control-release exception. A blocked Command View may only lower after
    /// this exact owner lock has a successfully sent desired=false request; somebody else's lock,
    /// an activation request, or an owned plan that is only draining cannot borrow the exception.
    /// </summary>
    private bool PrepareTacticalCommandViewExit()
    {
        _tacticalCommandViewExitAuthorized = false;
        _tacticalCommandViewExitLockId = 0;
        // An acquire has no lock identity yet. Never place a control-release behind it on the
        // wire; wait for the authoritative active/denial snapshot first.
        if (_tacticalFreezePendingRequest != 0 && _tacticalFreezePendingDesiredActive)
        {
            ShowUiError("Wait for the Tactical Freeze request to finish before leaving Command View.");
            return false;
        }
        if (!TacticalFreezeBlocksLiveCommands)
        {
            _tacticalCommandViewExitAuthorized = true;
            return true;
        }

        TacticalLockView? owned = OwnedActiveTacticalLock;
        bool anotherLocalLock = owned is not null && _tacticalLocks.Values.Any(view =>
            view.Active && view.LockId != owned.LockId &&
            (view.OwnerGuid == LocalPlayerGuid || view.Members.ContainsKey(LocalPlayerGuid) ||
             view.Members.ContainsKey(ControlledGuid)));
        if (owned is null || !RequestOwnedTacticalThawForViewExit())
        {
            RefuseTacticalFreezeLiveCommand("leaving Command View");
            return false;
        }

        bool queuedActions = _tacticalQueues.TryGetValue(owned.LockId,
            out TacticalQueueSnapshot queue) &&
            queue.Actors.Any(actor => actor.Actions.Length != 0);
        bool queueMutationPending = _tacticalQueuePendingRequests.Count != 0;
        bool anotherOwnedPlanDraining = _tacticalLocks.Values.Any(view => !view.Active &&
            view.OwnerGuid == LocalPlayerGuid && _tacticalQueues.ContainsKey(view.LockId));
        if (queuedActions || queueMutationPending || anotherOwnedPlanDraining)
        {
            string status = "Resume requested — the tactical plan must finish before " +
                "Command View can close. Press Ctrl+F again after DRAINED.";
            SetRtsControlGroupStatus(status);
            ShowUiError(status);
            return false;
        }
        if (anotherLocalLock)
        {
            ShowUiError("Resume requested, but another player's Tactical Freeze still holds " +
                "this body. Command View remains open.");
            return false;
        }

        if (_tacticalFreezePendingRequest != 0 && !_tacticalFreezePendingDesiredActive &&
            _tacticalFreezePendingLockId == owned.LockId)
        {
            _tacticalCommandViewExitAuthorized = true;
            _tacticalCommandViewExitLockId = owned.LockId;
            return true;
        }

        RefuseTacticalFreezeLiveCommand("leaving Command View");
        return false;
    }

    /// <summary>Revalidate the exit exception for every release retry.</summary>
    private bool TacticalCommandViewExitAuthorizationValid()
    {
        if (!_tacticalCommandViewExitAuthorized) return false;
        if (_tacticalCommandViewExitLockId == 0)
            return !TacticalFreezeBlocksLiveCommands &&
                !(_tacticalFreezePendingRequest != 0 && _tacticalFreezePendingDesiredActive);

        ulong authorized = _tacticalCommandViewExitLockId;
        bool conflictingActive = _tacticalLocks.Values.Any(view => view.Active &&
            view.LockId != authorized &&
            (view.OwnerGuid == LocalPlayerGuid || view.Members.ContainsKey(LocalPlayerGuid) ||
             view.Members.ContainsKey(ControlledGuid)));
        bool conflictingDrain = _tacticalLocks.Values.Any(view => !view.Active &&
            view.OwnerGuid == LocalPlayerGuid &&
            _tacticalQueues.ContainsKey(view.LockId));
        bool queueMutationPending = _tacticalQueuePendingRequests.Count != 0;
        bool authorizedQueueHasActions = _tacticalQueues.TryGetValue(authorized,
            out TacticalQueueSnapshot queue) &&
            queue.Actors.Any(actor => actor.Actions.Length != 0);
        return !conflictingActive && !conflictingDrain && !queueMutationPending &&
            !authorizedQueueHasActions;
    }

    private void ApplyTacticalFreezeSnapshot(byte[] body)
    {
        if (!_tacticalFreezeAvailable ||
            !TacticalFreezeWire.TryParseFreezeSnapshot(body, out TacticalFreezeSnapshot snapshot))
        {
            Console.WriteLine("[tactical-freeze] malformed or unadvertised snapshot dropped");
            return;
        }

        if (snapshot.RequestId != 0 && snapshot.RequestId == _tacticalFreezePendingRequest)
        {
            _tacticalFreezePendingRequest = 0;
            _tacticalFreezePendingDesiredActive = false;
            _tacticalFreezePendingLockId = 0;
        }

        // Acquire denials intentionally carry no lock identity and therefore cannot mutate pose.
        if (snapshot.LockId == 0)
        {
            if (snapshot.RequestId != 0)
                ShowUiError(TacticalFreezeWire.FreezeResultText(snapshot.Result));
            return;
        }

        // A NOT_FOUND reply has no owner/revision by definition. Its echoed lock id is still
        // authoritative retirement evidence and must beat any previously cached revision.
        if (snapshot.Result == TacticalFreezeWire.FreezeNotFound)
        {
            TacticalFreezePoseLaw.ApplyLockSnapshot(snapshot.LockId, active: false, []);
            _tacticalLocks.Remove(snapshot.LockId);
            _tacticalQueues.Remove(snapshot.LockId);
            if (_tacticalGroundLockId == snapshot.LockId)
                CancelTacticalGroundCast(silent: true);
            string text = TacticalFreezeWire.FreezeResultText(snapshot.Result);
            SetRtsControlGroupStatus(text);
            ShowUiError(text);
            return;
        }

        if (_tacticalLocks.TryGetValue(snapshot.LockId, out TacticalLockView? previous) &&
            previous.Revision > snapshot.Revision)
            return;

        var next = previous ?? new TacticalLockView { LockId = snapshot.LockId };
        next.Active = snapshot.Active;
        next.Revision = snapshot.Revision;
        next.OwnerGuid = snapshot.OwnerGuid;
        next.Center = snapshot.Center;
        next.Radius = snapshot.Radius;
        next.Members = snapshot.Members.ToDictionary(member => member.Guid);
        _tacticalLocks[snapshot.LockId] = next;

        TacticalFreezePoseLaw.ApplyLockSnapshot(snapshot.LockId, snapshot.Active,
            snapshot.Members.Where(member => member.Frozen).Select(member => member.Guid));

        bool affectsLocalControl = snapshot.OwnerGuid == LocalPlayerGuid ||
            next.Members.ContainsKey(LocalPlayerGuid) || next.Members.ContainsKey(ControlledGuid);
        bool SnapshotFreezes(ulong guid) => guid != 0 &&
            next.Members.TryGetValue(guid, out TacticalFreezeMember member) && member.Frozen;
        if (snapshot.Active && affectsLocalControl)
        {
            // Drafts and handoffs authored in live time cannot leak across the authoritative lock
            // edge. Tactical queues are separate server truth and remain untouched here.
            ClearRtsAttackQueue();
            ClearRtsWaypointChain();
            _hasPendingMoveOrder = false;
            _pendingMoveSubjects = null;
            CancelRtsPatrolAuthoring(silent: true);
            CancelPendingPrimaryItemUse();
            CancelPendingPrimaryCast();
            CancelRtsUnitCastTargeting(silent: true);
            _groundCastSpell = 0;
            _groundCursorPoint = null;
            CancelItemTargeting();
            _controlSwitchQueued = 0;
            ClearRtsForceTakeControl();
            _cvPendingInteractGuid = 0;
            _cvPendingInteractRoute = null;
            _cvPendingInteractWalkers.Clear();
            StopAutoFollow(showStatus: false);
        }

        if (snapshot.Active)
        {
            if (SnapshotFreezes(_autoFollowGuid)) StopAutoFollow(showStatus: false);
            // This recipient can stand outside another owner's radius while one of their selected
            // bots is inside it. Retire every delayed local authoring buffer that names a newly
            // frozen remote actor; none may wait out the lock and fire after DRAINED.
            if (SnapshotFreezes(_rtsForceTakeControlGuid)) ClearRtsForceTakeControl();
            if (SnapshotFreezes(_controlSwitchQueued)) _controlSwitchQueued = 0;
            if (SnapshotFreezes(_pendingCastPrimary) ||
                SnapshotFreezes(_pendingCastExplicitTarget))
                CancelPendingPrimaryCast();
            if (SnapshotFreezes(_pendingUsePrimary)) CancelPendingPrimaryItemUse();
            if (SnapshotFreezes(_rtsUnitCastPrimary))
                CancelRtsUnitCastTargeting(silent: true);
            if (_pendingBagsArmed && SnapshotFreezes(_pendingBagsSubject))
            {
                _pendingBagsArmed = false;
                _pendingBagsSubject = 0;
            }
            if (_hasPendingMoveOrder && _pendingMoveSubjects is not null &&
                TacticalOrderActorsFrozen(_pendingMoveSubjects))
            {
                _hasPendingMoveOrder = false;
                _pendingMoveSubjects = null;
            }
            if (_rtsAttackQueue.Count != 0 &&
                (TacticalOrderActorsFrozen(_rtsAttackSubjects) ||
                 _rtsAttackQueue.Any(IsTacticalActorFrozen)))
                ClearRtsAttackQueue();
            if (_rtsWaypointChain.Count != 0 && TacticalOrderActorsFrozen(_rtsWaypointSubjects))
                ClearRtsWaypointChain();
            if (_rtsPatrolAuthoring && TacticalOrderActorsFrozen(_rtsPatrolDraftSubjects))
                CancelRtsPatrolAuthoring(silent: true);
            if (SnapshotFreezes(_cvPendingInteractGuid) ||
                _cvPendingInteractWalkers.Any(IsTacticalActorFrozen))
            {
                _cvPendingInteractGuid = 0;
                _cvPendingInteractRoute = null;
                _cvPendingInteractWalkers.Clear();
            }
        }

        if (!snapshot.Active && _tacticalGroundLockId == snapshot.LockId)
            CancelTacticalGroundCast(silent: true);
        if (!snapshot.Active && _rtsUnitCastTacticalLockId == snapshot.LockId)
            CancelRtsUnitCastTargeting(silent: true);

        // A released lock without queued work has no future packet. Do not retain a tombstone
        // forever (especially on read-only observers); only an actually nonempty server queue
        // needs the owner/member context while it continues draining after thaw.
        bool queuedWorkRemains = _tacticalQueues.TryGetValue(snapshot.LockId,
            out TacticalQueueSnapshot queued) &&
            queued.Actors.Any(actor => actor.Actions.Length != 0);
        if (!snapshot.Active && !queuedWorkRemains)
        {
            _tacticalLocks.Remove(snapshot.LockId);
            _tacticalQueues.Remove(snapshot.LockId);
        }

        if (snapshot.RequestId != 0 || snapshot.Result != TacticalFreezeWire.FreezeOk)
        {
            string text = snapshot.Active && snapshot.Result == TacticalFreezeWire.FreezeOk
                ? snapshot.OwnerGuid == LocalPlayerGuid
                    ? $"Tactical Freeze active — {snapshot.Members.Length} units locked."
                    : $"Frozen by {ResolveTacticalOwner(snapshot.OwnerGuid)}."
                : TacticalFreezeWire.FreezeResultText(snapshot.Result);
            SetRtsControlGroupStatus(text);
            if (snapshot.Result is TacticalFreezeWire.FreezeDeniedSession or
                TacticalFreezeWire.FreezeDeniedCommandView or TacticalFreezeWire.FreezeDeniedState or
                TacticalFreezeWire.FreezeFrozenByOther or TacticalFreezeWire.FreezeNotOwner or
                TacticalFreezeWire.FreezeNotFound or TacticalFreezeWire.FreezeBadPacket)
                ShowUiError(text);
        }
    }

    private void ApplyTacticalQueueSnapshot(byte[] body)
    {
        if (!_tacticalFreezeAvailable ||
            !TacticalFreezeWire.TryParseQueueSnapshot(body, out TacticalQueueSnapshot snapshot))
        {
            Console.WriteLine("[tactical-freeze] malformed or unadvertised queue snapshot dropped");
            return;
        }

        _tacticalQueuePendingRequests.Remove(snapshot.RequestId);

        // Stateless queue failures carry revision zero and no actor snapshot. LOCK_NOT_FOUND
        // retires any stale client lock immediately; BAD_PACKET is feedback only. Never create a
        // dictionary key for a zero lock echoed from a packet too short for the server to decode.
        if (snapshot.Revision == 0)
        {
            if (snapshot.Result == TacticalFreezeWire.QueueLockNotFound && snapshot.LockId != 0)
            {
                TacticalFreezePoseLaw.ApplyLockSnapshot(snapshot.LockId, active: false, []);
                _tacticalLocks.Remove(snapshot.LockId);
                _tacticalQueues.Remove(snapshot.LockId);
                if (_tacticalGroundLockId == snapshot.LockId)
                    CancelTacticalGroundCast(silent: true);
            }
            string failure = TacticalFreezeWire.QueueResultText(snapshot.Result);
            SetRtsControlGroupStatus(failure);
            ShowUiError(failure);
            return;
        }

        if (_tacticalQueues.TryGetValue(snapshot.LockId, out TacticalQueueSnapshot previous) &&
            previous.Revision > snapshot.Revision)
            return;
        _tacticalQueues[snapshot.LockId] = snapshot;

        if (snapshot.Result == TacticalFreezeWire.QueueDrained && snapshot.Actors.Length == 0)
        {
            _tacticalQueues.Remove(snapshot.LockId);
            if (_tacticalLocks.TryGetValue(snapshot.LockId, out TacticalLockView? ended) &&
                !ended.Active)
                _tacticalLocks.Remove(snapshot.LockId);
        }

        string result = TacticalFreezeWire.QueueResultText(snapshot.Result);
        if (snapshot.Result != TacticalFreezeWire.QueueOk || snapshot.RequestId != 0)
            SetRtsControlGroupStatus(result);
        if (snapshot.Result is >= TacticalFreezeWire.QueueBadPacket and <=
            TacticalFreezeWire.QueueActionNotFound)
            ShowUiError(result);
    }

    private bool TryQueueTacticalMove(Vector3 destination)
    {
        if (!TryGetOwnedQueueLock(out TacticalLockView owned)) return FrozenInputConsumed();
        List<ulong> actors = TacticalCommandableSelection(owned);
        if (actors.Count == 0)
        {
            ShowUiError("Select at least one commandable frozen party member.");
            return true;
        }
        SendTacticalQueue(owned, TacticalFreezeWire.QueueEnqueue,
            actors.Select(actor => new TacticalQueueRequestRecord(actor, 0,
                TacticalFreezeWire.ActionMove, 0, destination, 0)).ToList());
        return true;
    }

    private bool TryQueueTacticalAttack(ulong targetGuid)
    {
        if (!TryGetOwnedQueueLock(out TacticalLockView owned)) return FrozenInputConsumed();
        if (targetGuid == 0 || !_entities.TryGet(targetGuid, out WorldEntity target) ||
            target.IsDead || !CanAttack(target))
        {
            ShowUiError("Choose a living hostile target for the tactical attack.");
            return true;
        }
        List<ulong> actors = TacticalCommandableSelection(owned);
        if (actors.Count == 0)
        {
            ShowUiError("Select at least one commandable frozen party member.");
            return true;
        }
        SendTacticalQueue(owned, TacticalFreezeWire.QueueEnqueue,
            actors.Select(actor => new TacticalQueueRequestRecord(actor, 0,
                TacticalFreezeWire.ActionAttack, targetGuid, Vector3.Zero, 0)).ToList());
        return true;
    }

    /// <summary>
    /// Queue a primary-card spell directly for its actor. This path never possesses the actor and
    /// intentionally skips present-time cooldown/resource/range checks; the server validates when
    /// the action executes after thaw.
    /// </summary>
    private bool TryQueueTacticalSpell(ulong actorGuid, uint spellId, ulong explicitTarget)
    {
        if (!TryGetOwnedQueueLock(out TacticalLockView owned)) return FrozenInputConsumed();
        if (!owned.Members.TryGetValue(actorGuid, out TacticalFreezeMember actor) ||
            !actor.Frozen || !actor.CommandableByRecipient)
        {
            ShowUiError("That frozen unit is read-only.");
            return true;
        }
        if (_spellCatalog?.TryGet(spellId, out SpellInfo spell) != true || spell.Passive ||
            !ActionsFor(actorGuid).KnownSpells.Contains(spellId))
        {
            ShowUiError("That unit does not know this active spell.");
            return true;
        }

        CastTargetCandidate? selected = null;
        ulong selectedGuid = explicitTarget != 0 ? explicitTarget : _selectionGuid;
        if (selectedGuid != 0 && _entities.TryGet(selectedGuid, out WorldEntity selectedEntity))
            selected = CastCandidate(selectedEntity, selectedGuid == actorGuid, actorGuid);
        CastTargetCandidate? self = _entities.TryGet(actorGuid, out WorldEntity actorEntity)
            ? CastCandidate(actorEntity, isSelf: true, casterGuid: actorGuid) : null;
        CastTargetVerdict verdict = CastTargetLaw.Resolve(
            spell, selected, self, autoSelfCast: explicitTarget == 0);

        if (verdict.Kind == CastTargetKind.Ground)
        {
            _tacticalGroundLockId = owned.LockId;
            _tacticalGroundActor = actorGuid;
            _tacticalGroundSpellId = spellId;
            _groundCastSpell = 0;
            _groundCursorPoint = null;
            SetRtsControlGroupStatus($"{spell.Name}: choose a ground point for the queue.");
            return true;
        }
        if (verdict.Kind is CastTargetKind.Refused or CastTargetKind.Item)
        {
            ShowUiError(verdict.Kind == CastTargetKind.Item
                ? "Item-target spells cannot be queued in Tactical Freeze."
                : "Choose a valid target for that queued spell.");
            return true;
        }

        // The Core distinguishes unit/self casts from ground casts by targetGuid. SelfImplicit
        // therefore names the actor explicitly; target=0 is reserved for the picked ground point.
        ulong queuedTarget = verdict.Kind == CastTargetKind.SelfImplicit
            ? actorGuid : verdict.Guid;
        SendTacticalQueue(owned, TacticalFreezeWire.QueueEnqueue,
            [new TacticalQueueRequestRecord(actorGuid, 0, TacticalFreezeWire.ActionCast,
                queuedTarget, Vector3.Zero, spellId)]);
        return true;
    }

    private bool TryHandleTacticalGroundCast(WorldMouseClick click, TargetPressPick pressPick)
    {
        if (_tacticalGroundSpellId == 0) return false;
        if (click.Button == MouseButton.Right)
        {
            CancelTacticalGroundCast(silent: false);
            return true;
        }
        if (click.Button != MouseButton.Left) return true;
        Vector3 point;
        if (pressPick.Armed && pressPick.GroundPoint is Vector3 latched) point = latched;
        else if (!TryPickGround(click.Position, out point)) return true;

        TacticalLockView? owned = OwnedActiveTacticalLock;
        if (owned is null || owned.LockId != _tacticalGroundLockId)
        {
            CancelTacticalGroundCast(silent: true);
            ShowUiError("That Tactical Freeze is no longer accepting orders.");
            return true;
        }
        ulong actor = _tacticalGroundActor;
        uint spell = _tacticalGroundSpellId;
        CancelTacticalGroundCast(silent: true);
        SendTacticalQueue(owned, TacticalFreezeWire.QueueEnqueue,
            [new TacticalQueueRequestRecord(actor, 0, TacticalFreezeWire.ActionCast,
                0, point, spell)]);
        return true;
    }

    private void CancelTacticalGroundCast(bool silent)
    {
        if (_tacticalGroundSpellId == 0) return;
        _tacticalGroundLockId = 0;
        _tacticalGroundActor = 0;
        _tacticalGroundSpellId = 0;
        _groundCursorPoint = null;
        if (!silent) SetRtsControlGroupStatus("Queued spell targeting cancelled.");
    }

    private void CancelTacticalAction(ulong lockId, ulong actorGuid, uint actionId)
    {
        if (!TryGetOwnedQueueLock(out TacticalLockView owned) || owned.LockId != lockId) return;
        SendTacticalQueue(owned, TacticalFreezeWire.QueueCancel,
            [new TacticalQueueRequestRecord(actorGuid, actionId, 0, 0, Vector3.Zero, 0)]);
    }

    private void ClearSelectedTacticalQueues(ulong lockId)
    {
        if (!TryGetOwnedQueueLock(out TacticalLockView owned) || owned.LockId != lockId) return;
        List<ulong> actors = TacticalCommandableSelection(owned);
        if (actors.Count == 0 && _tacticalQueues.TryGetValue(lockId, out TacticalQueueSnapshot queue))
            actors = queue.Actors.Select(actor => actor.ActorGuid)
                .Where(guid => owned.Members.TryGetValue(guid, out TacticalFreezeMember member) &&
                    member.CommandableByRecipient).Take(TacticalFreezeWire.MaxRequestRecords).ToList();
        if (actors.Count == 0) return;
        SendTacticalQueue(owned, TacticalFreezeWire.QueueClear,
            actors.Select(actor => new TacticalQueueRequestRecord(
                actor, 0, 0, 0, Vector3.Zero, 0)).ToList());
    }

    private void SendTacticalQueue(TacticalLockView owned, byte operation,
        IReadOnlyList<TacticalQueueRequestRecord> records)
    {
        if (!_tacticalFreezeAvailable || !owned.Active || owned.OwnerGuid != LocalPlayerGuid ||
            _net is not { IsInWorld: true } || records.Count == 0) return;
        uint request = NextTacticalRequestId();
        if (_net.SuiTacticalQueue(owned.LockId, request, operation, records))
        {
            _tacticalQueuePendingRequests.Add(request);
            SetRtsControlGroupStatus(operation switch
            {
                TacticalFreezeWire.QueueEnqueue => "Tactical order requested…",
                TacticalFreezeWire.QueueCancel => "Cancelling tactical order…",
                TacticalFreezeWire.QueueClear => "Clearing tactical queue…",
                _ => "Updating tactical queue…",
            });
        }
    }

    private bool TryGetOwnedQueueLock(out TacticalLockView owned)
    {
        TacticalLockView? candidate = OwnedActiveTacticalLock;
        if (!_tacticalFreezeAvailable || candidate is null ||
            candidate.OwnerGuid != LocalPlayerGuid)
        {
            owned = null!;
            return false;
        }
        owned = candidate;
        return true;
    }

    private bool FrozenInputConsumed()
    {
        if (!TacticalFreezeBlocksLiveCommands) return false;
        if (OwnedDrainingTacticalLock is not null)
        {
            ShowUiError("Wait for your tactical plan to finish before issuing live commands.");
            return true;
        }
        TacticalLockView? other = LocalActiveTacticalLock;
        ShowUiError(other is null
            ? "This body is frozen and read-only."
            : $"Frozen by {ResolveTacticalOwner(other.OwnerGuid)} — only the owner can resume.");
        return true;
    }

    private List<ulong> TacticalCommandableSelection(TacticalLockView owned) =>
        RtsControlGroupLaw.NormalizeMembers(_freecamSelection)
            .Where(guid => owned.Members.TryGetValue(guid, out TacticalFreezeMember member) &&
                member.Frozen && member.CommandableByRecipient)
            .Take(TacticalFreezeWire.MaxRequestRecords).ToList();

    private string ResolveTacticalOwner(ulong ownerGuid) => ownerGuid == LocalPlayerGuid
        ? "you" : ResolveUnitName(ownerGuid);

    private TacticalActorQueue? QueueFor(ulong lockId, ulong actorGuid)
    {
        if (!_tacticalQueues.TryGetValue(lockId, out TacticalQueueSnapshot snapshot)) return null;
        foreach (TacticalActorQueue actor in snapshot.Actors)
            if (actor.ActorGuid == actorGuid) return actor;
        return null;
    }

    /// <summary>Child strip above the command shelf; every displayed queue entry is server truth.</summary>
    private void DrawTacticalQueueStrip(HudFrameResult shelf, float scale)
    {
        TacticalLockView? active = OwnedActiveTacticalLock ?? LocalActiveTacticalLock;
        ulong lockId = active?.LockId ?? _tacticalQueues.Keys.FirstOrDefault(candidateLock =>
            _tacticalLocks.TryGetValue(candidateLock, out TacticalLockView? ended) &&
            ended.OwnerGuid == LocalPlayerGuid);
        if (lockId == 0) return;
        _tacticalLocks.TryGetValue(lockId, out TacticalLockView? view);
        bool owner = view?.OwnerGuid == LocalPlayerGuid;
        bool activeNow = view?.Active == true;

        const float height = 44f;
        HudFrameResult strip = HudFrame("tactical-queue-strip", "Tactical queue",
            new HudPlacement(HudAnchor.Top, HudAnchor.Bottom, 0f, -4f),
            new Vector2(ConsoleWidth, height), parent: "command-shelf", hideable: false);
        if (strip.Hidden) return;
        ImGui.SetNextWindowPos(strip.ScreenMin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(strip.ScreenSize, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoBringToFrontOnFocus;
        if (!ImGui.Begin("##tactical-queue-strip", flags)) { ImGui.End(); return; }
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        Vector2 origin = ImGui.GetWindowPos();
        DrawRtsConsoleBackdrop(dl, origin, origin + ImGui.GetWindowSize(), scale);

        string state = activeNow ? "FROZEN" : "EXECUTING";
        uint stateColor = activeNow ? 0xff8fe8ff : PainterlyFrameRule;
        GameText.Draw(dl, "GameFontNormalSmall", state,
            origin + new Vector2(9f, 7f) * scale, scale, stateColor);
        string ownerText = owner ? "You own this lock" :
            $"Read-only • {ResolveTacticalOwner(view?.OwnerGuid ?? 0)} owns Resume";
        GameText.Draw(dl, "GameFontHighlightSmall", ownerText,
            origin + new Vector2(9f, 23f) * scale, scale);

        List<ulong> actors = owner && view is not null
            ? TacticalCommandableSelection(view)
            : view?.Members.Values.Where(member => member.Guid == LocalPlayerGuid)
                .Select(member => member.Guid).ToList() ?? [];
        if (actors.Count == 0 && owner && _tacticalQueues.TryGetValue(lockId, out TacticalQueueSnapshot all))
            actors = all.Actors.Select(actor => actor.ActorGuid).Take(5).ToList();

        float x = 172f;
        const int shownActors = 5;
        foreach (ulong actorGuid in actors.Take(shownActors))
        {
            string name = ResolveUnitName(actorGuid);
            if (name.Length > 8) name = name[..8];
            GameText.Draw(dl, "GameFontHighlightSmall", name,
                origin + new Vector2(x, 4f) * scale, scale * .86f);
            TacticalActorQueue? actorQueue = QueueFor(lockId, actorGuid);
            TacticalQueuedAction[] actions = actorQueue?.Actions ?? [];
            for (int slot = 0; slot < TacticalFreezeWire.MaxActionsPerActor; slot++)
            {
                Vector2 min = origin + new Vector2(x + slot * 14f, 21f) * scale;
                Vector2 size = new Vector2(12f, 15f) * scale;
                Vector2 max = min + size;
                bool filled = slot < actions.Length;
                dl.AddRectFilled(min, max, filled ? 0xff273747 : 0xff10141c);
                dl.AddRect(min, max, filled ? PainterlyFrameRule : PainterlyFrameOuter,
                    0f, ImDrawFlags.None, MathF.Max(1f, scale));
                if (!filled) continue;
                TacticalQueuedAction action = actions[slot];
                string glyph = action.Kind switch
                {
                    TacticalFreezeWire.ActionMove => "M",
                    TacticalFreezeWire.ActionAttack => "A",
                    TacticalFreezeWire.ActionCast => "S",
                    _ => "?",
                };
                GameText.Draw(dl, "GameFontNormalSmall", glyph,
                    min + new Vector2(3f, 1f) * scale, scale * .82f);
                ImGui.SetCursorScreenPos(min);
                ImGui.InvisibleButton($"##tq-{lockId:X}-{actorGuid:X}-{action.ActionId}", size);
                if (ImGui.IsItemHovered()) HoverTip($"{TacticalActionText(action)}\nClick to cancel");
                if (owner && activeNow && ImGui.IsItemClicked())
                    CancelTacticalAction(lockId, actorGuid, action.ActionId);
            }
            x += 78f;
        }

        if (owner && activeNow)
        {
            Vector2 min = origin + new Vector2(ConsoleWidth - 53f, 12f) * scale;
            Vector2 size = new Vector2(43f, 22f) * scale;
            dl.AddRectFilled(min, min + size, 0xff241b1c);
            DrawBevel(dl, min, min + size, MathF.Max(1f, scale),
                PainterlyStoneTop, PainterlyFrameOuter);
            GameText.Draw(dl, "GameFontNormalSmall", "Clear",
                min + new Vector2(6f, 4f) * scale, scale * .85f);
            ImGui.SetCursorScreenPos(min);
            ImGui.InvisibleButton($"##tq-clear-{lockId:X}", size);
            if (ImGui.IsItemHovered()) HoverTip("Clear selected members' tactical queues");
            if (ImGui.IsItemClicked()) ClearSelectedTacticalQueues(lockId);
        }
        ImGui.End();
    }

    private string TacticalActionText(TacticalQueuedAction action) => action.Kind switch
    {
        TacticalFreezeWire.ActionMove =>
            $"Move to {action.Position.X:0.#}, {action.Position.Y:0.#}",
        TacticalFreezeWire.ActionAttack =>
            $"Attack {ResolveWorldUnitName(action.TargetGuid)}",
        TacticalFreezeWire.ActionCast => _spellCatalog?.TryGet(action.SpellId, out SpellInfo spell) == true
            ? $"Cast {spell.Name}" : $"Cast spell {action.SpellId}",
        _ => "Queued action",
    };

    /// <summary>Persistent read-only notice for a frozen real player outside Command View.</summary>
    private void DrawTacticalFreezeNotice()
    {
        TacticalLockView? view = LocalActiveTacticalLock;
        if (view is null || (!view.Members.ContainsKey(LocalPlayerGuid) &&
            !view.Members.ContainsKey(ControlledGuid)) ||
            (view.OwnerGuid == LocalPlayerGuid && _freeView)) return;
        const float width = 390f, height = 30f;
        HudFrameResult notice = HudFrame("tactical-freeze-notice", "Tactical Freeze status",
            HudPlacement.At(HudAnchor.Top, 0f, 54f), new Vector2(width, height), hideable: false);
        ImGui.SetNextWindowPos(notice.ScreenMin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(notice.ScreenSize, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoInputs |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoBringToFrontOnFocus;
        ImGui.Begin("##tactical-freeze-notice", flags);
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        Vector2 min = ImGui.GetWindowPos(), max = min + ImGui.GetWindowSize();
        dl.AddRectFilled(min, max, 0xe51a2029);
        DrawBevel(dl, min, max, MathF.Max(1f, GameplayUiScale()),
            PainterlyFrameRule, PainterlyFrameOuter);
        string owner = ResolveTacticalOwner(view.OwnerGuid);
        string text = view.OwnerGuid == LocalPlayerGuid
            ? "TACTICAL FREEZE • releasing on Command View exit…"
            : $"TACTICAL FREEZE • Frozen by {owner} • read-only • only {owner} can Resume";
        GameText.Draw(dl, "GameFontHighlightSmall", text,
            min + new Vector2(10f, 8f) * GameplayUiScale(), GameplayUiScale());
        ImGui.End();
    }
}

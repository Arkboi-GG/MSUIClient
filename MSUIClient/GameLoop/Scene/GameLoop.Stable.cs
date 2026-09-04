using MSUIClient.Engine.UI;
using MSUIClient.Net;

namespace MSUIClient;

/// <summary>
/// Pet stables (spec P3). The stablemaster window: the active pet, the stabled
/// pets, purchased stable slots, and the stable/unstable/swap/buy-slot actions.
///
/// The list is server-pushed (MSG_LIST_STABLED_PETS) — either because the player
/// picked the stablemaster's gossip option, or because /stable asked for it — and
/// every follow-up action addresses the same stablemaster GUID carried in that list.
/// A successful action re-pulls the list so the window matches the server.
///
/// Revive is deliberately absent: the core's CMSG_STABLE_REVIVE_PET handler is an
/// empty stub, so there is no server behavior to drive.
/// </summary>
public sealed partial class GameLoop
{
    private bool _stableOpen;

    /// <summary>The last stablemaster view, or null when never opened / closed.</summary>
    private StableList? _stableList;

    /// <summary>The pet number the panel has selected (for swap/unstable), 0 = none.</summary>
    private uint _stableSelected;

    private ulong StableNpcGuid => _stableList?.NpcGuid ?? 0;

    /// <summary>
    /// Open the stablemaster for the current target (used by /stable). The target must
    /// be a creature flagged as a stablemaster; the window opens when the list arrives.
    /// </summary>
    private void OpenStableForTarget()
    {
        if (RefuseTacticalFreezeLiveCommand("opening the stable")) return;
        ulong guid = _selectionGuid;
        if (RefuseTacticalFrozenActor(guid, "open its stable service")) return;
        if (guid == 0 || !_entities.TryGet(guid, out WorldEntity npc) || !npc.IsCreature)
        {
            ShowUiError("Target a stablemaster first.");
            return;
        }
        if ((npc.NpcFlags & WorldCursorUiLaw.StableMaster) == 0)
        {
            ShowUiError("That creature is not a stablemaster.");
            return;
        }
        _net?.RequestStabledPets(guid);
        EmitInterface("stable", "list", "REQUESTED", guid, "");
    }

    /// <summary>MSG_LIST_STABLED_PETS: the stablemaster view. Opens the window.</summary>
    private void ApplyStableList(byte[] body)
    {
        if (!StableWire.TryParseStableList(body, out StableList list))
        {
            EmitInterface("stable", "list", "MALFORMED", 0, $"bytes={body.Length}");
            return;
        }
        _stableList = list;
        _stableOpen = true;
        // Drop a selection that no longer exists after a refresh.
        if (_stableSelected != 0 && !list.Pets.Any(p => p.PetNumber == _stableSelected))
            _stableSelected = 0;
        Console.WriteLine($"[stable] {list.Pets.Length} pet(s), {list.StableSlots} slot(s)");
        EmitInterface("stable", "list", "APPLIED", list.NpcGuid,
            $"pets={list.Pets.Length};slots={list.StableSlots}");
    }

    /// <summary>SMSG_STABLE_RESULT: outcome of a stable action. On success, refresh.</summary>
    private void ApplyStableResult(byte[] body)
    {
        if (body.Length != 1)
        {
            EmitInterface("stable", "result", "MALFORMED", StableNpcGuid, $"bytes={body.Length}");
            return;
        }
        byte code = body[0];
        bool ok = StableWire.IsSuccess(code);
        if (ok) ShowUiInfo(StableWire.DescribeResult(code));
        else ShowUiError(StableWire.DescribeResult(code));
        EmitInterface("stable", "result", ok ? "OK" : "FAIL", StableNpcGuid, $"code=0x{code:X2}");

        // The server does not re-push the list after an action, so ask again to keep
        // the window truthful (the active pet and slots just changed).
        if (ok && StableNpcGuid != 0 && !TacticalFreezeBlocksLiveCommands &&
            !IsTacticalActorFrozen(StableNpcGuid))
            _net?.RequestStabledPets(StableNpcGuid);
    }

    // --- actions invoked by the panel; all address the stored stablemaster GUID ---

    private void StableActivePet()
    {
        if (RefuseTacticalFreezeLiveCommand("stabling a pet")) return;
        if (RefuseTacticalFrozenActor(StableNpcGuid, "stable a pet through it")) return;
        if (RefuseTacticalFrozenActor(_petGuid, "stable it")) return;
        if (StableNpcGuid != 0) _net?.StablePet(StableNpcGuid);
    }

    private void UnstableSelectedPet(uint petNumber)
    {
        if (RefuseTacticalFreezeLiveCommand("unstabling a pet")) return;
        if (RefuseTacticalFrozenActor(StableNpcGuid, "unstable a pet through it")) return;
        if (StableNpcGuid != 0 && petNumber != 0) _net?.UnstablePet(StableNpcGuid, petNumber);
    }

    private void SwapSelectedPet(uint petNumber)
    {
        if (RefuseTacticalFreezeLiveCommand("swapping stable pets")) return;
        if (RefuseTacticalFrozenActor(StableNpcGuid, "swap pets through it")) return;
        if (RefuseTacticalFrozenActor(_petGuid, "swap it out of the stable")) return;
        if (StableNpcGuid != 0 && petNumber != 0) _net?.SwapStablePet(StableNpcGuid, petNumber);
    }

    private void BuyStableSlot()
    {
        if (RefuseTacticalFreezeLiveCommand("buying a stable slot")) return;
        if (RefuseTacticalFrozenActor(StableNpcGuid, "buy a stable slot from it")) return;
        if (StableNpcGuid != 0) _net?.BuyStableSlot(StableNpcGuid);
    }

    /// <summary>Clear stable state on world-leave / character swap.</summary>
    private void ResetStable()
    {
        _stableOpen = false;
        _stableList = null;
        _stableSelected = 0;
    }
}

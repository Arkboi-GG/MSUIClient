using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private ulong _petInfoRequestOwner, _petInfoRequestGuid;

    private void ApplyActorPetPacket(Op opcode, byte[] body, ulong owner)
    {
        // PET_SPELLS includes an unaddressed zero-GUID teardown; pet feedback and
        // failures carry no owner. A direct main reply must never replace or clear
        // a companion bar just because both arrive on the commander's socket.
        if (owner == 0 || owner != ControlledGuid) return;
        switch (opcode)
        {
            case Op.SMSG_PET_SPELLS: ApplyPetSpells(body); break;
            case Op.SMSG_PET_MODE: ApplyPetMode(body); break;
            case Op.SMSG_PET_ACTION_FEEDBACK: ApplyPetActionFeedback(body); break;
            case Op.SMSG_PET_CAST_FAILED: ApplyPetCastFailed(body); break;
        }
    }

    private void ResetPetInfoRefresh()
    {
        _petInfoRequestOwner = 0;
        _petInfoRequestGuid = 0;
    }

    private void UpdatePetInfoRefresh()
    {
        // MiscHandler::HandleRequestPetInfoOpcode is still main-only. It can
        // refresh a missing main bar, but cannot retrieve a companion's pet.
        if (_net is not { IsInWorld: true } || ControlledGuid != LocalPlayerGuid ||
            !_entities.TryGet(ControlledGuid, out WorldEntity actor)) return;
        ulong pet = actor.Fields.Summon ?? actor.Fields.Charm ?? 0;
        if (pet == 0) { ResetPetInfoRefresh(); return; }
        if (_petGuid == pet || (_petInfoRequestOwner == ControlledGuid && _petInfoRequestGuid == pet)) return;
        if (!_net.RequestPetInfo()) return;
        // One request per observed owner/pet identity; no per-frame retry storm
        // when the server has no pet, it is disabled, or its answer is delayed.
        _petInfoRequestOwner = ControlledGuid;
        _petInfoRequestGuid = pet;
    }
}

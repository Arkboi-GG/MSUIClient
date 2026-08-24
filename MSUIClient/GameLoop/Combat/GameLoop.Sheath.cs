using System.Numerics;
using MSUIClient.Net;
using MSUIClient.Formats;
using MSUIClient.World.Sound;
using MSUIClient.World.Units;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private bool _sheathKeyWasDown;
    private byte _visualSheathState;
    private byte _lastServerSheathState = byte.MaxValue;
    private byte? _pendingCeremonialSheathState;
    private SheatheSoundCatalog? _sheatheSounds;
    private bool _sheatheSoundsLoaded;

    private void UpdateSheathInput(bool typing)
    {
        if (_net is null || !_net.IsInWorld || _character is null ||
            !_entities.TryGet(ControlledGuid, out WorldEntity player)) return;

        byte serverState = player.Fields.SheathState;
        if (serverState != _lastServerSheathState)
        {
            _lastServerSheathState = serverState;
            byte adopted = serverState <= 2 ? serverState : (byte)0;
            // Our volunteer echo may arrive before the hand reaches the weapon. Keep the old
            // placement pinned until the authored event; a different inbound state is external
            // authority and cancels the local ceremony immediately.
            if (_pendingCeremonialSheathState is not byte pending || adopted != pending)
            {
                _pendingCeremonialSheathState = null;
                _character.CancelSheathCeremony();
                _visualSheathState = adopted;
            }
        }

        if (_pendingCeremonialSheathState is byte ceremonialState &&
            _character.ConsumeSheathSwap())
        {
            _visualSheathState = ceremonialState;
            _pendingCeremonialSheathState = null;
            PlayCeremonialSheatheSounds(ceremonialState);
        }

        // Melee engagement is an instant draw in the reference client. Keep it
        // optimistic so the hand mount moves in the attack-start frame, then
        // volunteer the pose for other clients through UNIT_FIELD_BYTES_2.
        bool controllerOwnsBody = ControllerOwnsControlledBodyPose;
        if (player.Engaged && (_visualSheathState != 1 ||
            _pendingCeremonialSheathState is not null))
        {
            _pendingCeremonialSheathState = null;
            _character.CancelSheathCeremony();
            _visualSheathState = 1;
            if (controllerOwnsBody) _net.SetSheathed(1);
        }


        // BindingDown applies the global exact-chord-first, one-fallback dispatch law. Track the
        // base key separately so releasing a modifier before the base cannot manufacture an edge.
        bool physicalDown = BindingBaseDown(GameBinding.Sheath);
        bool acceptedDown = BindingDown(GameBinding.Sheath);
        if (controllerOwnsBody && acceptedDown && !_sheathKeyWasDown &&
            !typing && !player.Engaged && !_character.SheathCeremonyActive)
        {
            byte next = _visualSheathState == 0 ? (byte)1 : (byte)0;
            _net.SetSheathed(next);
            if (_character.BeginSheathCeremony())
            {
                // The attachment remains at its old location while the live gait continues
                // underneath the arm overlays. ConsumeSheathSwap moves it at $SHL/$SHR.
                _pendingCeremonialSheathState = next;
            }
            else
            {
                // No equipped hand, clip, or arm mask: the reference degrades silently to a snap.
                _visualSheathState = next;
            }
        }
        // Track the physical binding, not the accepted bare-key chord. Releasing Alt before Z
        // after TOGGLEUI must not manufacture a new bare-Z edge and unexpectedly sheath.
        _sheathKeyWasDown = physicalDown;
        _character.SheathState = _visualSheathState;
    }

    private void PlayCeremonialSheatheSounds(byte destinationState)
    {
        if (!AudioFeaturePolicy.ExpandedWorldAudioEnabled) return;
        if (_spellSounds is null || _character is null || _controller is null) return;
        if (!_sheatheSoundsLoaded)
        {
            _sheatheSoundsLoaded = true;
            if (_mpq is not null) _sheatheSounds = SheatheSoundCatalog.Load(_mpq);
        }
        if (_sheatheSounds is null) return;

        bool drawing = destinationState == 1;
        Vector3 listener = _controller.Position;
        // The volunteer Z ceremony moves the melee arms. Attack/reactive/ranged
        // SetVisualSheath paths are instant and deliberately never enter here.
        foreach (int equipmentSlot in new[] { 15, 16 })
        {
            CharacterEquipment.Piece? item = _character.Equipment.Pieces
                .FirstOrDefault(piece => piece.EquipmentSlot == equipmentSlot);
            if (item is null || !_sheatheSounds.TryGet(
                    item.ItemClass, item.ItemSubclass, item.Material, out SheatheSoundPair pair))
                continue;
            uint kit = drawing ? pair.Unsheathe : pair.Sheathe;
            _spellSounds.Play(kit, ControlledGuid, listener, listener,
                forceLoop: false, trackHold: false, category: "sfx");
        }
    }

    private void SetVisualSheath(byte state, bool volunteer = true)
    {
        if (state > 2) return;
        bool ceremonyInFlight = _pendingCeremonialSheathState is not null ||
            _character?.SheathCeremonyActive == true;
        _pendingCeremonialSheathState = null;
        _character?.CancelSheathCeremony();
        if (_visualSheathState == state && !ceremonyInFlight) return;
        _visualSheathState = state;
        if (volunteer && ControllerOwnsControlledBodyPose) _net?.SetSheathed(state);
        if (_character is not null) _character.SheathState = state;
    }
}

using Silk.NET.Input;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private bool _sheathKeyWasDown;
    private byte _visualSheathState;
    private byte _lastServerSheathState = byte.MaxValue;
    private byte? _pendingVisualSheathState;
    private double _sheathSwapAt;

    private void UpdateSheathInput(bool typing)
    {
        if (_net is null || !_net.IsInWorld || _character is null ||
            !_entities.TryGet(ControlledGuid, out WorldEntity player)) return;

        byte serverState = player.Fields.SheathState;
        if (serverState != _lastServerSheathState)
        {
            _lastServerSheathState = serverState;
            if (_pendingVisualSheathState is null)
                _visualSheathState = serverState <= 2 ? serverState : (byte)0;
        }

        // Melee engagement is an instant draw in the reference client. Keep it
        // optimistic so the hand mount moves in the attack-start frame, then
        // volunteer the pose for other clients through UNIT_FIELD_BYTES_2.
        if (player.Engaged && _visualSheathState != 1)
        {
            _pendingVisualSheathState = null;
            _visualSheathState = 1;
            _net.SetSheathed(1);
        }

        if (_pendingVisualSheathState is { } pending &&
            MovementInfo.ClientUptimeMs() / 1000.0 >= _sheathSwapAt)
        {
            _visualSheathState = pending;
            _pendingVisualSheathState = null;
        }

        bool down = BindingDown(GameBinding.Sheath);
        if (down && !_sheathKeyWasDown && !typing && !player.Engaged)
        {
            byte next = _visualSheathState == 0 ? (byte)1 : (byte)0;
            _net.SetSheathed(next);
            byte sheathType = _character.Equipment.Pieces
                .FirstOrDefault(p => p.EquipmentSlot == 15)?.Sheath ?? 0;
            int animation = sheathType is 3 or 7 ? 90 : 89;
            float duration = _character.TriggerOneShot(animation);
            _pendingVisualSheathState = next;
            _sheathSwapAt = MovementInfo.ClientUptimeMs() / 1000.0 + Math.Max(.12, duration * .48);
        }
        _sheathKeyWasDown = down;
        _character.SheathState = _visualSheathState;
    }

    private void SetVisualSheath(byte state, bool volunteer = true)
    {
        if (state > 2 || _visualSheathState == state) return;
        _visualSheathState = state;
        if (volunteer) _net?.SetSheathed(state);
        if (_character is not null) _character.SheathState = state;
    }
}

using MSUIClient.Engine;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private bool _vanillaSelfControlLost;
    private ulong _possessedControlLostGuid;
    private bool _possessedControlRestorePending;

    private void ResetPossessedClientControl()
    {
        _possessedControlLostGuid = 0;
        _possessedControlRestorePending = false;
    }

    private bool PossessedClientControlLocksMover => ClientControlUpdateLaw.LocksAddressedBody(
        _possessedControlLostGuid, ControlledGuid, _freeView, _controlState == ControlState.Possessing);

    private bool ApplyPossessedClientControlUpdate(NetworkClient net, ClientControlUpdatePacket update)
    {
        if (_controlState != ControlState.Possessing || update.Mover == 0 || update.Mover != ControlledGuid)
            return false;
        _vanillaSelfControlLost = false;
        if (!update.AllowMove)
        {
            bool newlyLost = _possessedControlLostGuid != update.Mover;
            _possessedControlLostGuid = update.Mover;
            _possessedControlRestorePending = false;
            if (!_freeView)
            {
                if (newlyLost && _controller is not null) _movementSender.ParkForRoot(net, _controller);
                _movementSender.Parked = true;
                _autorunToggled = false;
                StopAutoFollow(showStatus: false);
            }
        }
        else if (_possessedControlLostGuid == update.Mover)
        {
            _possessedControlRestorePending = true;
            TryRestorePossessedClientControl(net);
        }
        return true;
    }

    private void TryRestorePossessedClientControl(NetworkClient net)
    {
        if (!_possessedControlRestorePending) return;
        if (_controlState != ControlState.Possessing || _possessedControlLostGuid != ControlledGuid)
        { ResetPossessedClientControl(); return; }
        if (_freeView) { ResetPossessedClientControl(); return; }
        // Wait for the actual body/transport pose before resuming; stale controller coordinates
        // from before fear must never become a newly authored relocation.
        if (_controller is null || !_entities.TryGet(ControlledGuid, out WorldEntity body)) return;
        var transport = body.Transport;
        if (!TryApplyServerRidePose(transport?.Guid ?? 0, transport?.Pos ?? body.Position,
            transport?.Orientation ?? body.Orientation)) return;
        _movementSender.Reset(_controller.Yaw);
        net.SetActiveMover(ControlledGuid);
        _movementSender.Parked = false;
        ResetPossessedClientControl();
        AdoptControlledMovementModes();
        AdoptControlledSpeeds();
    }

    private bool SuiOwnsClientControl => ClientControlUpdateLaw.SuiOwnsRouting(
        _freeView, _controlState == ControlState.OwnChar);

    private bool VanillaSelfControlLocksMover => PossessedClientControlLocksMover || ClientControlUpdateLaw.LocksCurrentMover(
        _vanillaSelfControlLost,
        _freeView,
        _controlState == ControlState.OwnChar,
        ControlledGuid == LocalPlayerGuid);

    private void ApplyClientControlUpdate(NetworkClient net, byte[] body)
    {
        ClientControlUpdatePacket update = ClientControlUpdatePackets.Parse(body);
        // Unit::UpdateControl explicitly addresses SUI fear/confusion loss to the driven bot.
        // This is distinct from routing noise addressed to the parked session body.
        if (ApplyPossessedClientControlUpdate(net, update)) return;
        ClientControlUpdateLaw.Verdict verdict = ClientControlUpdateLaw.Classify(
            update.Mover, update.AllowMove, LocalPlayerGuid);

        // SUI possession/free-view uses SetClientControl to release the session BODY while input
        // belongs to a bot or detached camera. The stock packet is part of that hand-off, not a
        // fear/mind-control lock on the active rig. Consuming it here used to leave the latch set;
        // once FreeCam made ControlledGuid resolve to self, every movement axis was zeroed and the
        // camera appeared welded behind the character.
        if (SuiOwnsClientControl)
        {
            _vanillaSelfControlLost = false;
            return;
        }

        // Ordinary own-character control retains the stock fear/confusion/mind-control law.
        if (verdict == ClientControlUpdateLaw.Verdict.SelfRevoked)
        {
            if (!_vanillaSelfControlLost && _controller is not null)
                _movementSender.ParkForRoot(net, _controller);
            _vanillaSelfControlLost = true;
            _movementSender.Parked = true;
            _autorunToggled = false;
            StopAutoFollow(showStatus: false);
            return;
        }

        if (verdict != ClientControlUpdateLaw.Verdict.SelfRestored) return;

        _vanillaSelfControlLost = false;
        if (ControlledGuid == LocalPlayerGuid && !_freeView &&
            _controlState == ControlState.OwnChar)
        {
            net.SetActiveMover(update.Mover);
            _movementSender.Parked = false;
        }
    }

    private void ApplyVanillaControlLockout(ref float forward, ref float strafe, ref float turn,
        ref bool jump)
    {
        if (_net is { } net) TryRestorePossessedClientControl(net);
        if (!VanillaSelfControlLocksMover) return;
        forward = strafe = turn = 0f;
        jump = false;
        _movementSender.Parked = true;
        if (_controller is not null)
            _window.Camera.SetFacingKeepingView(_controller.Yaw);
    }

    private void ResetVanillaClientControl()
    {
        _vanillaSelfControlLost = false;
        ResetPossessedClientControl();
    }
}

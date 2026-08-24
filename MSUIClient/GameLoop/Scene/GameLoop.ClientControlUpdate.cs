using MSUIClient.Engine;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private bool _vanillaSelfControlLost;

    private bool SuiOwnsClientControl => ClientControlUpdateLaw.SuiOwnsRouting(
        _freeView, _controlState == ControlState.OwnChar);

    private bool VanillaSelfControlLocksMover => ClientControlUpdateLaw.LocksCurrentMover(
        _vanillaSelfControlLost,
        _freeView,
        _controlState == ControlState.OwnChar,
        ControlledGuid == LocalPlayerGuid);

    private void ApplyClientControlUpdate(NetworkClient net, byte[] body)
    {
        ClientControlUpdatePacket update = ClientControlUpdatePackets.Parse(body);
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
        if (!VanillaSelfControlLocksMover) return;
        forward = strafe = turn = 0f;
        jump = false;
        _movementSender.Parked = true;
        if (_controller is not null)
            _window.Camera.SetFacingKeepingView(_controller.Yaw);
    }

    private void ResetVanillaClientControl() => _vanillaSelfControlLost = false;
}

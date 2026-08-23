using MSUIClient.Engine;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private bool _vanillaSelfControlLost;

    private void ApplyClientControlUpdate(NetworkClient net, byte[] body)
    {
        ClientControlUpdatePacket update = ClientControlUpdatePackets.Parse(body);
        ClientControlUpdateLaw.Verdict verdict = ClientControlUpdateLaw.Classify(
            update.Mover, update.AllowMove, LocalPlayerGuid);

        // Stock control loss/restoration for our own body is independent of MSUI's protected SUI
        // bot-possession protocol. Foreign grant/release statements are therefore observed here
        // but must not mutate ControlState, its ACK workflow, proxy stores, or free-view camera.
        if (verdict == ClientControlUpdateLaw.Verdict.SelfRevoked)
        {
            if (!_vanillaSelfControlLost && ControlledGuid == LocalPlayerGuid &&
                _controller is not null)
                _movementSender.ParkForRoot(net, _controller);
            _vanillaSelfControlLost = true;
            _movementSender.Parked = ControlledGuid == LocalPlayerGuid;
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
        if (!_vanillaSelfControlLost || ControlledGuid != LocalPlayerGuid) return;
        forward = strafe = turn = 0f;
        jump = false;
        _movementSender.Parked = true;
        if (_controller is not null)
            _window.Camera.SetFacingKeepingView(_controller.Yaw);
    }

    private void ResetVanillaClientControl() => _vanillaSelfControlLost = false;
}

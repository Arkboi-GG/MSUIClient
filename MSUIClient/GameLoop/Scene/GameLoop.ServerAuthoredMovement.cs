using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    /// <summary>
    /// Apply a bare inbound MSG_MOVE_* for the currently driven mover. This is a server-authored
    /// correction/GM relocation, not the teleport handshake: hard snap, merge its authored flag
    /// subset, preserve the camera orbit, send no reply, and let the ordinary movement stream
    /// carry the adopted pose on its next natural edge.
    /// </summary>
    private void ApplyServerAuthoredSelfMove(MovementRelay relay)
    {
        if (_controller is null || _freeView || relay.Guid != ControlledGuid) return;
        MovementInfo movement = relay.Movement;
        uint localFlags = _movementSender.LastFlags |
            (uint)_controller.GrantedMovementFlags |
            (_movementRooted ? (uint)MovementFlags.Root : 0u);
        uint merged = ServerAuthoredMovementLaw.MergeFlags(localFlags, movement.Flags);

        _movementRooted = (merged & (uint)MovementFlags.Root) != 0;
        _controller.WaterWalking = (merged & (uint)MovementFlags.WaterWalking) != 0;
        _controller.FeatherFalling = (merged & (uint)MovementFlags.FeatherFalling) != 0;
        _controller.Hovering = (merged & (uint)MovementFlags.Hover) != 0;

        float oldFacing = _controller.Yaw;
        float delta = ServerAuthoredMovementLaw.FacingDelta(oldFacing, movement.Orientation);
        _controller.Teleport(movement.Position.X, movement.Position.Y, movement.Position.Z);
        _controller.Yaw = ServerAuthoredMovementLaw.WrapTau(oldFacing + delta);
        _window.Camera.Yaw = ServerAuthoredMovementLaw.WrapTau(_window.Camera.Yaw + delta);
        _window.Camera.Target = _controller.Position;
        _character?.SnapFacing(_controller.Yaw);
        _movementSender.Reset(_controller.Yaw);
        _entities.ApplyServerAuthoredMove(relay.Guid, movement, MovementInfo.ClientUptimeMs());

        _config.Start.X = movement.Position.X;
        _config.Start.Y = movement.Position.Y;
        _config.Start.Z = movement.Position.Z;
        _config.Start.Orientation = movement.Orientation;
    }
}

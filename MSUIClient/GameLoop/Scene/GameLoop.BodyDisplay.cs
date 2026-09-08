using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private readonly List<WorldEntity> _displayModelUnits = [];
    private bool ControlledUsesDisplayModel =>
        _entities.TryGet(ControlledGuid, out WorldEntity actor) && actor.Fields.HasDisplayTransform;

    private IReadOnlyList<WorldEntity> DisplayModelRenderUnits()
    {
        if (_freeView || !ControlledUsesDisplayModel || _controller is null) return _visibleWorldUnits;
        _displayModelUnits.Clear();
        foreach (WorldEntity unit in _visibleWorldUnits)
        {
            if (unit.Guid != ControlledGuid) { _displayModelUnits.Add(unit); continue; }
            if (_window.Camera.EffectiveDistance <= FirstPersonBodyHide) continue;
            _displayModelUnits.Add(unit.WithRenderPose(_controller.Position, _controller.Yaw,
                _controller.PlanarSpeed, _movementSender.LastFlags, _controller.Flying));
        }
        return _displayModelUnits;
    }
}

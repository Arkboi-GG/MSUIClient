using MSUIClient.Engine;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private ulong _farSightAnchor;
    private bool _farSightAwaitingFreeViewClear;

    /// <summary>
    /// Resolve PLAYER_FARSIGHT from the session character only. This changes the world camera's
    /// target, never the controlled mover, character-centred sound, zone, minimap, or free-view
    /// ownership. An unstreamed subject safely leaves the eye on the body while the field remains
    /// engaged.
    /// </summary>
    private void UpdateViewSubject()
    {
        var camera = _window.Camera;
        camera.AuthoredTarget = null;

        ulong anchor = 0;
        if (_net is { PlayerGuid: not 0 } net &&
            _entities.TryGet(net.PlayerGuid, out WorldEntity self))
            anchor = self.Fields.PlayerFarsight ?? 0;

        // SUI's detached camera is the stronger owner. Its server-side streaming eye updates
        // PLAYER_FARSIGHT; treating that transport field as stock Bind Sight retargets the
        // orbit and emits CMSG_FAR_SIGHT, pinning the RTS rig behind the character. The field
        // clear can trail the exit ACK, so retain ownership through that hand-off as well.
        ViewSubjectLaw.PlayerFarSightOwnership ownership =
            ViewSubjectLaw.ResolvePlayerFarSightOwnership(
                _freeView, _farSightAwaitingFreeViewClear, anchor);
        _farSightAwaitingFreeViewClear = ownership.AwaitClear;
        if (!ownership.MayOwnCamera) return;

        if (anchor != _farSightAnchor)
        {
            _net?.FarSight(anchor != 0);
            _farSightAnchor = anchor;
        }

        if (anchor == 0 || !_entities.TryGet(anchor, out WorldEntity remote)) return;

        float pivot = ViewSubjectLaw.PivotFallback;
        _creatures?.TryGetCameraPivotHeight(remote, out pivot);
        camera.AuthoredTarget = ViewSubjectLaw.EyeTarget(remote.Position, pivot);
    }

    private void ResetViewSubject()
    {
        _farSightAnchor = 0;
        _farSightAwaitingFreeViewClear = false;
        _window.Camera.AuthoredTarget = null;
    }
}

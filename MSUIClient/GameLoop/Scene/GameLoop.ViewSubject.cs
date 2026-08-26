using MSUIClient.Engine;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private ulong _farSightAnchor;
    private bool _farSightAwaitingFreeViewClear;
    private ulong _loggedViewAnchor;

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

        // Name the eye once per identity. The server decides what creature to spawn for it and
        // whether to flag it unselectable; when it forgets, the eye turns up in the world as an
        // ordinary unit and the only way to tell that from a real spawn is to see its guid named
        // here. One line per change, so a following eye cannot make this chatter.
        if (anchor != _loggedViewAnchor)
        {
            _loggedViewAnchor = anchor;
            if (anchor != 0)
                Console.WriteLine($"[view] farsight anchor {anchor:X} " +
                                  $"'{ResolveUnitName(anchor)}' " +
                                  $"(free view {(_freeView ? "up" : "down")}) - " +
                                  "suppressed from nameplates, picking and markers");
        }

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

    /// <summary>
    /// Is this unit the session character's PLAYER_FARSIGHT anchor — the server's own streaming
    /// eye rather than anything in the world?
    ///
    /// SUI's Free View asks the server to keep an eye near the camera so the world streams around
    /// it, and the server publishes that eye in PLAYER_FARSIGHT. It is spawned as an ordinary
    /// creature (a "World Trigger" on the current core), so unless it is flagged unselectable it
    /// arrives here as a perfectly normal level-60 neutral unit — and gets a nameplate, sitting
    /// right on top of whoever the camera is watching. That reads as "my character's name changed
    /// to World Trigger". Reported 2026-08-26.
    ///
    /// Read straight from the descriptor rather than from _farSightAnchor: that field tracks
    /// STOCK Bind Sight ownership and UpdateViewSubject deliberately stops maintaining it while
    /// the free view owns the camera, which is exactly when the eye exists.
    /// </summary>
    private bool IsViewAnchorUnit(ulong guid) =>
        guid != 0 && _net is { PlayerGuid: not 0 } net &&
        _entities.TryGet(net.PlayerGuid, out WorldEntity self) &&
        self.Fields.PlayerFarsight is ulong anchor && anchor == guid;

    private void ResetViewSubject()
    {
        _farSightAnchor = 0;
        _farSightAwaitingFreeViewClear = false;
        _loggedViewAnchor = 0;
        _window.Camera.AuthoredTarget = null;
    }
}

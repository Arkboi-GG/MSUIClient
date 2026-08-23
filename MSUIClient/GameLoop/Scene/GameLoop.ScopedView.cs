using MSUIClient.Engine;
using MSUIClient.Formats;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private bool _scopedViewActive;

    /// <summary>
    /// Apply aura 76 from the session character only. A possessed body or nearby player's public
    /// aura fields must not take ownership of this client-local camera override.
    /// </summary>
    private void UpdateScopedView()
    {
        float? zoom = null;
        if (_net is { PlayerGuid: not 0 } net && _spellCatalog is not null &&
            _entities.TryGet(net.PlayerGuid, out WorldEntity player))
        {
            foreach (var aura in player.Fields.Auras())
            {
                if (!_spellCatalog.TryGet(aura.SpellId, out SpellInfo spell)) continue;
                zoom = ScopedViewLaw.ZoomFraction(spell.AuraIds, spell.EffectMiscValues);
                if (zoom is not null) break;
            }
        }

        var camera = _window.Camera;
        _scopedViewActive = zoom is not null;
        camera.AuthoredVerticalFieldOfViewRadians = zoom is float fraction
            ? ScopedViewLaw.VerticalFieldOfViewRadians(camera.FieldOfViewDegrees, fraction)
            : null;

        // The reference parks both the realized camera and the wheel target every frame. Removing
        // the aura unlocks the view but deliberately leaves it in first person until the user
        // wheels back out.
        if (_scopedViewActive)
            camera.Distance = camera.EffectiveDistance = ScopedViewLaw.FirstPersonDistance;
    }

    private void ResetScopedView()
    {
        _scopedViewActive = false;
        _window.Camera.AuthoredVerticalFieldOfViewRadians = null;
    }
}

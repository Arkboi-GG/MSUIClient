using System.Numerics;
using ImGuiNET;
using MSUIClient.Engine.UI;
using MSUIClient.Net;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private bool _inspectHonorPage, _inspectHonorPending;
    private ulong _inspectHonorOwner;
    private double _inspectHonorDeadline;
    private HonorInspectPacket? _inspectHonor;
    private string _inspectHonorError = "";

    private void ResetInspectHonor()
    {
        _inspectHonorPage = _inspectHonorPending = false;
        _inspectHonorOwner = 0;
        _inspectHonor = null;
        _inspectHonorError = "";
    }

    private bool SubmitInspectQuery(ulong guid)
    {
        // Core CMSG_INSPECT sets _player selection before its range check. A driven
        // companion can display public gear without retargeting the parked main.
        return ControlledGuid != LocalPlayerGuid || _net?.Inspect(guid) == true;
    }

    private bool CanRequestInspectHonor() => _inspectOpen && ControlledGuid == LocalPlayerGuid &&
        _net is { IsInWorld: true } && TryGetInteractionBodyPose(out WorldBodyPose actor) &&
        _entities.TryGet(_inspectGuid, out WorldEntity target) &&
        InspectUiLaw.CanInspect(target.IsPlayer, target.Guid == ControlledGuid, CanAttack(target),
            Vector3.DistanceSquared(actor.Position, target.Position));

    private bool RequestInspectHonor()
    {
        ResetInspectHonor();
        _inspectHonorPage = true;
        // Honor is not public gear: this handler still checks the main's position
        // and hostility. Do not fetch or apply it on behalf of a companion.
        if (!CanRequestInspectHonor())
        { _inspectHonorError = "Honor information is unavailable."; return false; }
        if (_net?.InspectHonor(_inspectGuid) != true)
        { _inspectHonorError = "Unable to request honor information."; return false; }
        _inspectHonorOwner = ControlledGuid;
        _inspectHonorDeadline = NowSeconds() + 10;
        _inspectHonorPending = true;
        return true;
    }

    private void ApplyInspectHonor(byte[] body, ulong owner)
    {
        HonorInspectPacket packet = HonorInspectPacket.Parse(body);
        if (!_inspectHonorPage || !_inspectHonorPending || owner != _inspectHonorOwner ||
            owner != ControlledGuid || packet.Guid != _inspectGuid ||
            NowSeconds() > _inspectHonorDeadline || !CanRequestInspectHonor()) return;
        _inspectHonor = packet;
        _inspectHonorPending = false;
        _inspectHonorError = "";
    }

    private void DrawInspectHonorPage(ImDrawListPtr draw, Vector2 origin, float scale)
    {
        if (_inspectHonorOwner != 0 && _inspectHonorOwner != ControlledGuid)
        { ResetInspectHonor(); return; }
        if (_inspectHonorPending && NowSeconds() > _inspectHonorDeadline)
        { _inspectHonorPending = false; _inspectHonorError = "Honor information is unavailable."; }
        if (_inspectHonor is { } packet)
            DrawHonorStatisticsPage(draw, origin, scale, packet.Statistics);
        else
            GameText.DrawCentered(draw, "GameFontNormalSmall", _inspectHonorPending ? "Loading honor information..." : _inspectHonorError,
                origin + new Vector2(184, 170) * scale, scale);
        if (VanillaButton(draw, "##inspect-honor-refresh", "Refresh", origin + new Vector2(142,405) * scale,
            new Vector2(84,22), scale, enabled: !_inspectHonorPending)) RequestInspectHonor();
    }
}

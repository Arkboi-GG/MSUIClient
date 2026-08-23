using MSUIClient.Engine.UI;
using MSUIClient.Net;
using MSUIClient.World.Units;

namespace MSUIClient;

public sealed partial class GameLoop
{
    /// <summary>
    /// Compose quest-status and unknown-flight-master state into authored TalkToMe M2 draws.
    /// Quest status wins when both sources name the same NPC, matching Benilla's sync order.
    /// </summary>
    private IReadOnlyList<SpellMeshDraw> QuestMarkerMeshInstances(double now)
    {
        if (_questMarkerModels is null || _net is not { IsInWorld: true }) return [];

        var markers = new Dictionary<ulong, QuestMarkerStyle>();
        foreach ((ulong guid, uint status) in _questStatuses)
            if (QuestMarkerUiLaw.Style(status) is { } style) markers[guid] = style;
        foreach ((ulong guid, bool known) in _taxiNodeKnown)
            if (!known) markers.TryAdd(guid, QuestMarkerUiLaw.UnknownFlightMaster);

        var requests = new List<QuestMarkerModelRequest>(markers.Count);
        foreach ((ulong guid, QuestMarkerStyle style) in markers)
        {
            if (!_entities.TryGet(guid, out WorldEntity npc) || !npc.IsCreature || npc.IsDead ||
                (npc.NpcFlags & (NpcQuestGiver | NpcFlightMaster)) == 0) continue;

            bool mounted = npc.MountDisplayId > 0 &&
                _creatures?.TryGetMountSeat(guid, out _) == true;
            requests.Add(new QuestMarkerModelRequest(guid, style.ModelPath,
                QuestMarkerRaised(npc), mounted));
        }

        return _questMarkerModels.Build(now, requests, SpellEffectUnitPose);
    }

    private bool QuestMarkerRaised(WorldEntity unit)
    {
        if (SettingsModalOpen || unit.Guid == ControlledGuid || unit.IsDead ||
            (unit.Fields.UnitFlags & NotSelectable) != 0 ||
            !Settings.Controls.ShowNpcNames ||
            !_entities.TryGet(ControlledGuid, out WorldEntity player)) return false;

        if (!NameplateUiLaw.ModeAllows(ReactionTargetTowardPlayer(unit),
                _enemyNameplatesVisible, _friendlyNameplatesVisible)) return false;

        var self = _controller?.Position ?? player.Position;
        return System.Numerics.Vector3.DistanceSquared(self, UnitWorldPosition(unit)) <=
            NameplateRangeYards * NameplateRangeYards;
    }
}

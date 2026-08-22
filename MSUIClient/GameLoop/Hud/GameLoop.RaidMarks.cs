using System.Numerics;
using MSUIClient.Engine.UI;
using MSUIClient.Net;
using MSUIClient.World.Units;

namespace MSUIClient;

public sealed partial class GameLoop
{
    /// <summary>
    /// Current Benilla's non-V-plate path: a marked streamed unit carries a fixed one-world-unit,
    /// depth-tested camera-facing square at its bare overhead anchor. Nameplated units use
    /// DrawNameplate's exact plate child instead.
    /// </summary>
    private IReadOnlyList<WorldBillboardDraw> RaidMarkerBillboards()
    {
        var draws = new List<WorldBillboardDraw>();
        if (SettingsModalOpen || _net is not { IsInWorld: true } ||
            !_entities.TryGet(ControlledGuid, out WorldEntity player)) return draws;
        Vector3 selfPosition = _controller?.Position ?? player.Position;

        for (byte slot = 0; slot < _partyRaidTargets.Length; slot++)
        {
            ulong guid = _partyRaidTargets[slot];
            if (guid == 0 || !_entities.TryGet(guid, out WorldEntity unit)) continue;

            bool hasLiveVplate = _nameplatesVisible && unit.Guid != ControlledGuid && !unit.IsDead &&
                (unit.Fields.UnitFlags & NotSelectable) == 0 &&
                Vector3.DistanceSquared(selfPosition, UnitWorldPosition(unit)) <=
                    NameplateRangeYards * NameplateRangeYards;
            if (hasLiveVplate) continue;

            Vector3 anchor = UnitWorldPosition(unit) +
                new Vector3(0f, 0f, UnitOverheadHeight(unit));
            RaidMarkerUv uv = RaidMarkerUiLaw.AtlasUv(checked((byte)(slot + 1)));
            draws.Add(new WorldBillboardDraw(anchor, RaidMarkerUiLaw.WorldSize,
                RaidMarkerUiLaw.Texture, uv.Min, uv.Max, Vector3.One, 1f));
        }
        return draws;
    }
}

using System.Numerics;
using MSUIClient.Formats;

namespace MSUIClient;

public sealed partial class GameLoop
{
    private void RunEnvironmentAudit()
    {
        EnsureInstanceData();
        int portalRows = 0, portalErrors = 0, instanceRows = 0, instanceErrors = 0, waterSamples = 0;

        if (_maps is null || _mapWdts is null || _areaTriggers is null || _teleports is null)
        {
            EmitInterface("environment", "summary", "FAILED_DATA_LOAD", 0,
                $"maps={_maps?.Count ?? 0};wdts={_mapWdts?.Count ?? 0};triggers={_areaTriggers?.Count ?? 0};teleports={_teleports?.Count ?? 0}");
            return;
        }

        foreach ((int id, AreaTriggerTeleport teleport) in _teleports.ById.OrderBy(x => x.Key))
        {
            AreaTriggerRow? volume = _areaTriggers.All.FirstOrDefault(x => x.Id == id);
            MapRow? target = _maps.Get(teleport.TargetMap);
            bool finite = float.IsFinite(teleport.TargetPosition.X) && float.IsFinite(teleport.TargetPosition.Y) && float.IsFinite(teleport.TargetPosition.Z);
            string outcome = volume is null ? "MISSING_VOLUME" : target is null ? "MISSING_TARGET_MAP" : !finite ? "NONFINITE_TARGET" : "PASS";
            if (outcome != "PASS") portalErrors++;
            portalRows++;
            EmitInterface("environment", "portal", outcome, (ulong)(uint)id,
                $"sourceMap={volume?.MapId ?? -1};targetMap={teleport.TargetMap};target={teleport.TargetPosition.X:R}|{teleport.TargetPosition.Y:R}|{teleport.TargetPosition.Z:R};shape={(volume?.IsSphere == true ? "sphere" : "box")};name={SanitizeEvidence(teleport.Name)}");
        }

        foreach (MapRow map in _maps.All.Where(x => x.IsInstance).OrderBy(x => x.Id))
        {
            WdtFile? wdt = _mapWdts.GetValueOrDefault(map.Id);
            int entrances = EntrancesTo(map.Id).Count;
            string outcome;
            string detail;
            if (wdt is null) { outcome = "MISSING_WDT"; detail = $"entrances={entrances}"; instanceErrors++; }
            else if (wdt.UsesGlobalWmo) { outcome = "PASS_GLOBAL_WMO_CATALOGUED"; detail = $"entrances={entrances};globalWmo={wdt.GlobalWmoPath}"; }
            else if (wdt.TileCount == 0) { outcome = "PASS_ZERO_TILE_CATALOGUED"; detail = $"entrances={entrances};tiles=0"; }
            else
            {
                bool planned = TryPlanArrival(map, wdt, out Vector2 arrival, out string why);
                outcome = planned ? "PASS" : "ARRIVAL_REFUSED";
                detail = $"entrances={entrances};tiles={wdt.TileCount};arrival={arrival.X:R}|{arrival.Y:R};why={SanitizeEvidence(why)}";
                if (!planned) instanceErrors++;
            }
            instanceRows++;
            EmitInterface("environment", "instance-entry", outcome, (ulong)(uint)map.Id,
                $"map={map.Id};name={SanitizeEvidence(map.Name)};{detail}");
        }

        if (_liquid is not null && _controller is not null)
        {
            Vector3 p = _controller.Position;
            Vector2[] offsets = [Vector2.Zero, new(10,0), new(-10,0), new(0,10), new(0,-10)];
            foreach (Vector2 offset in offsets)
            {
                bool found = _liquid.TryGetSurface(p.X + offset.X, p.Y + offset.Y, out float height, out byte type);
                waterSamples++;
                EmitInterface("environment", "water", found ? "SURFACE" : "DRY", (ulong)(uint)waterSamples,
                    $"position={p.X+offset.X:R}|{p.Y+offset.Y:R};height={height:R};type={type};residentTiles={_liquid.TileCount};wakeTexture={_liquid.HasWakeTexture};authoredColors={_liquid.AuthoredColorsActive}");
            }
        }

        string summary = portalErrors == 0 && instanceErrors == 0 ? "PASS" : "FINDING";
        EmitInterface("environment", "summary", summary, 0,
            $"portals={portalRows};portalErrors={portalErrors};instances={instanceRows};instanceErrors={instanceErrors};waterSamples={waterSamples};currentMap={_config.Start.Map}");
    }
}
